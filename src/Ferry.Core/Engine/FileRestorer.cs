using System.Text;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Engine;

public enum FileOutcome { Written, Replaced, KeptBoth, SkippedIdentical, SkippedExisting, Failed }

/// <summary>Thrown when the new PC's disk fills up; the restore stops instead of failing every remaining file.</summary>
public sealed class DiskFullException(string path, Exception inner)
    : IOException($"The disk is full while writing {path}. Free up space on the new PC and run the migration again.", inner);

/// <summary>
/// Writes restored files with the chosen conflict policy, journaling every change for undo.
/// <para>
/// Bulk files (documents, app data) can be written in the background: their content is read from the package in
/// order, then up to <see cref="Writers"/> files are written to disk at once. Creating a file costs far more than its
/// bytes on most PCs (antivirus scans each one as it's closed), so overlapping them matters for folders full of small
/// files. Large files always stream straight through.
/// </para>
/// </summary>
public sealed class FileRestorer(RestoreContext context)
{
    private const FileAttributes KeptAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    /// <summary>Files up to this size may be written in the background (bounding memory to Writers × this, roughly).</summary>
    private const long BackgroundLimit = 2 << 20;
    private const int Writers = 12;

    private readonly SemaphoreSlim slots = new(Writers);
    private readonly List<Task> pending = [];
    private Exception? fatal;

    private static readonly HashSet<string> TextSettingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".json", ".xml", ".config", ".cfg", ".conf", ".js", ".txt", ".props", ".yaml", ".yml", ".toml",
    };

    /// <summary>Extension-less settings files known to be text (Chromium's Preferences).</summary>
    private static readonly HashSet<string> TextSettingNames = new(StringComparer.OrdinalIgnoreCase) { "Preferences", ".gitconfig", ".npmrc" };

    private static bool IsTextSetting(string path) =>
        TextSettingExtensions.Contains(Path.GetExtension(path)) || TextSettingNames.Contains(Path.GetFileName(path));

    /// <summary>Restores a file entry to <paramref name="logicalPath"/> (a path on the new PC; sandboxing is applied here).</summary>
    /// <param name="remap">Rewrites paths inside shortcuts (and text settings when <paramref name="remapText"/> is set).</param>
    /// <param name="policy">Overrides the restore's conflict policy, e.g. settings files that must replace what's there.</param>
    /// <param name="background">
    /// Let a small file be written in the background. The returned outcome is then only provisional, so callers that
    /// act on it (e.g. registering a font) must not pass true. Call <see cref="DrainAsync"/> before relying on the results.
    /// </param>
    public async Task<FileOutcome> RestoreAsync(PackageEntry entry, InventoryItem item, string logicalPath, CancellationToken ct,
        PathRemapper? remap = null, ConflictPolicy? policy = null, bool remapText = false, bool background = false)
    {
        ThrowIfFailed();
        var header = entry.Header;
        if (!background || context.DryRun || header.Length > BackgroundLimit)
            return await WriteAsync(header, entry, null, item, logicalPath, remap, policy, remapText, ct).ConfigureAwait(false);

        byte[] content;
        try
        {
            content = await entry.ReadAllBytesAsync(ct).ConfigureAwait(false);
        }
        catch (EntryAbortedException)
        {
            return FileOutcome.Failed;
        }

        await slots.WaitAsync(ct).ConfigureAwait(false);
        var job = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(header, null, content, item, logicalPath, remap, policy, remapText, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The restore was cancelled; nothing to report for this file.
            }
            catch (DiskFullException ex)
            {
                // Stops the restore at the next file (or when the engine drains).
                lock (pending)
                    fatal ??= ex;
            }
            catch (Exception ex)
            {
                Log.Error($"Writing {logicalPath} failed", ex);
                context.Results.FileFailed(item, logicalPath, ex.Message);
            }
            finally
            {
                slots.Release();
            }
        }, CancellationToken.None);
        lock (pending)
        {
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(job);
        }
        return FileOutcome.Written;
    }

    /// <summary>Waits for background writes to finish, then reports a disk-full (or other fatal) failure from them.</summary>
    public async Task DrainAsync(bool throwOnFailure = true)
    {
        Task[] jobs;
        lock (pending)
        {
            jobs = [.. pending];
            pending.Clear();
        }
        await Task.WhenAll(jobs).ConfigureAwait(false);
        if (throwOnFailure)
            ThrowIfFailed();
    }

    private void ThrowIfFailed()
    {
        Exception? failure;
        lock (pending)
            failure = fatal;
        if (failure is not null)
            throw failure is IOException io ? io : new IOException(failure.Message, failure);
    }

    /// <summary>Decides, writes (from the entry's stream or from bytes already read), journals and finishes one file.</summary>
    private async Task<FileOutcome> WriteAsync(EntryHeader header, PackageEntry? entry, byte[]? content, InventoryItem item, string logicalPath,
        PathRemapper? remap, ConflictPolicy? policy, bool remapText, CancellationToken ct)
    {
        var target = context.Redirect(logicalPath);
        var results = context.Results;
        string? temp = null;
        try
        {
            var outcome = Decide(header, ref target, policy ?? context.Options.Conflicts);
            if (outcome is FileOutcome.SkippedIdentical or FileOutcome.SkippedExisting)
            {
                if (entry is not null)
                    await entry.SkipAsync(ct).ConfigureAwait(false);
                results.FileSkipped(item);
                return outcome;
            }
            if (context.DryRun)
            {
                if (entry is not null)
                    await entry.SkipAsync(ct).ConfigureAwait(false);
                results.FileDone(item, header.Length);
                return outcome;
            }

            EnsureDirectory(Path.GetDirectoryName(target)!);
            // A file that replaces another is written aside first so the original stays intact until the new one is
            // complete. A new file goes straight to its name: it's journaled first, so undo removes it even if
            // Ferry dies part-way, and a failed write deletes it below.
            var replacing = outcome == FileOutcome.Replaced;
            if (!replacing)
                context.Journal.FileCreated(target);
            temp = replacing ? target + ".ferrytmp" : target;
            var isShortcut = remap is { IsEmpty: false } && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            long written;
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await using (var input = content is not null ? new MemoryStream(content, writable: false) : entry!.OpenContent())
                {
                    if (remapText && remap is { IsEmpty: false } && IsTextSetting(target) && header.Length < 16 << 20)
                    {
                        using var buffer = new MemoryStream();
                        await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
                        var bytes = RemapText(buffer.ToArray(), remap);
                        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                        written = buffer.Length;
                    }
                    else
                    {
                        await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
                        written = output.Length;
                    }
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                // Setting times on the open handle saves reopening the file (each open is another antivirus scan).
                if (!isShortcut)
                    TrySetTimes(output.SafeFileHandle, header);
            }

            if (replacing)
            {
                // Renaming works on read-only files, so the original keeps its attributes in undo storage.
                context.Journal.BackupFile(target);
                File.Move(temp, target, overwrite: true);
            }
            temp = null;

            if (isShortcut)
            {
                ShellLink.Retarget(target, remap!);
                TrySetTimes(target, header, directory: false);
            }
            ApplyAttributes(target, header);
            results.FileDone(item, written);
            return outcome;
        }
        catch (EntryAbortedException)
        {
            // The old PC gave up on this file and already reported it; a retry entry may follow.
            return FileOutcome.Failed;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new DiskFullException(target, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not PackageReadException)
        {
            // A problem writing this one file. (Problems reading the package itself are fatal and propagate.)
            if (entry is not null)
                await SafeSkip(entry, ct).ConfigureAwait(false);
            results.FileFailed(item, logicalPath, ex.Message);
            return FileOutcome.Failed;
        }
        finally
        {
            // Cancelled, damaged package, or a failed write: never leave a half-written file behind.
            if (temp is not null)
                TryDelete(temp);
        }
    }

    /// <summary>Writes in-memory data as a file (used for settings rebuilt on the new PC).</summary>
    public void WriteBytes(InventoryItem item, string logicalPath, byte[] data, bool replace)
    {
        var target = context.Redirect(logicalPath);
        if (context.DryRun)
            return;
        EnsureDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (!replace)
                return;
            context.Journal.BackupFile(target);
        }
        else
        {
            context.Journal.FileCreated(target);
        }
        File.WriteAllBytes(target, data);
        context.Results.FileDone(item, data.Length);
    }

    public void RestoreDirectory(PackageEntry entry, string logicalPath)
    {
        if (context.DryRun)
            return;
        var target = context.Redirect(logicalPath);
        EnsureDirectory(target);
        TrySetTimes(target, entry.Header, directory: true);
    }

    /// <summary>Folders already known to exist, so thousands of files in one folder don't each check it again.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> knownFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a directory (and missing parents), journaling each one created.</summary>
    public void EnsureDirectory(string path)
    {
        if (context.DryRun || knownFolders.ContainsKey(path))
            return;
        if (Directory.Exists(path))
        {
            knownFolders.TryAdd(path, 0);
            return;
        }
        var missing = new Stack<string>();
        for (var dir = path; !string.IsNullOrEmpty(dir) && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
            missing.Push(dir);
        foreach (var dir in missing)
        {
            // Background writers can race to create the same folder; only the one that creates it records it.
            if (Directory.Exists(dir))
                continue;
            Directory.CreateDirectory(dir);
            context.Journal.DirectoryCreated(dir);
        }
        knownFolders.TryAdd(path, 0);
    }

    /// <summary>Moves an existing directory aside (into undo storage) so a fresh copy can take its place.</summary>
    public void ReplaceDirectory(string logicalPath)
    {
        var target = context.Redirect(logicalPath);
        if (context.DryRun || !Directory.Exists(target))
            return;
        context.Journal.BackupDirectory(target);
        foreach (var known in knownFolders.Keys.Where(k => KnownFolders.IsUnder(k, target)))
            knownFolders.TryRemove(known, out _);
    }

    private FileOutcome Decide(EntryHeader header, ref string target, ConflictPolicy policy)
    {
        if (!File.Exists(target))
            return FileOutcome.Written;
        var existing = new FileInfo(target);
        var incomingTime = ModifiedUtc(header);
        if (IsSame(existing, header))
            return FileOutcome.SkippedIdentical;

        switch (policy)
        {
            case ConflictPolicy.Skip:
                return FileOutcome.SkippedExisting;
            case ConflictPolicy.Overwrite:
                return FileOutcome.Replaced;
            case ConflictPolicy.OverwriteIfNewer:
                return incomingTime > existing.LastWriteTimeUtc.AddSeconds(2) ? FileOutcome.Replaced : FileOutcome.SkippedExisting;
            default:
                // Re-running a migration must not pile up copies: an identical "(from OLDPC)" file counts as done.
                for (var n = 1; n < 100; n++)
                {
                    var candidate = AlternateName(target, context.SourceComputer, n);
                    if (!File.Exists(candidate))
                    {
                        target = candidate;
                        return FileOutcome.KeptBoth;
                    }
                    if (IsSame(new FileInfo(candidate), header))
                        return FileOutcome.SkippedIdentical;
                }
                return FileOutcome.SkippedExisting;
        }
    }

    private static DateTime ModifiedUtc(EntryHeader header) => new(header.ModifiedTicks, DateTimeKind.Utc);

    private static bool IsSame(FileInfo existing, EntryHeader header) =>
        existing.Length == header.Length && Math.Abs((existing.LastWriteTimeUtc - ModifiedUtc(header)).TotalSeconds) <= 2;

    /// <summary>"Report.docx" → "Report (from OLDPC).docx", then "Report (from OLDPC 2).docx" and so on.</summary>
    internal static string AlternateName(string path, string sourceComputer, int n = 1)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var suffix = n <= 1 ? $"from {sourceComputer}" : $"from {sourceComputer} {n}";
        return Path.Combine(dir, $"{name} ({suffix}){ext}");
    }

    private static byte[] RemapText(byte[] content, PathRemapper remap)
    {
        // Settings files are overwhelmingly UTF-8 or UTF-16 with a BOM; anything else is left untouched.
        Encoding encoding;
        var offset = 0;
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            offset = 2;
        }
        else if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            offset = 3;
        }
        else
        {
            encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
        }
        string text;
        try
        {
            text = encoding.GetString(content, offset, content.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return content;
        }
        var remapped = remap.Remap(text);
        if (ReferenceEquals(remapped, text))
            return content;
        var body = encoding.GetBytes(remapped);
        return offset == 0 ? body : [.. content.AsSpan(0, offset), .. body];
    }

    /// <summary>Applies read-only, hidden and system. A new file already has Archive, so plain files need no call at all.</summary>
    private static void ApplyAttributes(string path, EntryHeader header)
    {
        var attributes = (FileAttributes)header.Attributes & KeptAttributes;
        if ((attributes & ~FileAttributes.Archive) == 0)
            return;
        try
        {
            File.SetAttributes(path, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Could not set attributes on {path}: {ex.Message}");
        }
    }

    private static void TrySetTimes(Microsoft.Win32.SafeHandles.SafeFileHandle handle, EntryHeader header)
    {
        try
        {
            if (header.CreatedTicks > 0)
                File.SetCreationTimeUtc(handle, new DateTime(header.CreatedTicks, DateTimeKind.Utc));
            if (header.ModifiedTicks > 0)
                File.SetLastWriteTimeUtc(handle, ModifiedUtc(header));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug($"Could not set file times: {ex.Message}");
        }
    }

    private static void TrySetTimes(string path, EntryHeader header, bool directory)
    {
        try
        {
            if (header.CreatedTicks > 0)
            {
                var created = new DateTime(header.CreatedTicks, DateTimeKind.Utc);
                if (directory)
                    Directory.SetCreationTimeUtc(path, created);
                else
                    File.SetCreationTimeUtc(path, created);
            }
            if (header.ModifiedTicks > 0)
            {
                if (directory)
                    Directory.SetLastWriteTimeUtc(path, ModifiedUtc(header));
                else
                    File.SetLastWriteTimeUtc(path, ModifiedUtc(header));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug($"Could not set times on {path}: {ex.Message}");
        }
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }

    private static async Task SafeSkip(PackageEntry entry, CancellationToken ct)
    {
        try
        {
            await entry.SkipAsync(ct).ConfigureAwait(false);
        }
        catch (EntryAbortedException)
        {
        }
    }
}
