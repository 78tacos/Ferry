using System.Diagnostics;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Engine;

public sealed class CaptureContext
{
    private readonly Action<string?, string?> status;
    private readonly Stopwatch statusClock = Stopwatch.StartNew();

    internal CaptureContext(PackageWriter writer, Inventory inventory, Selection selection, CaptureOptions options, HiveCache hives,
        ExclusionRules exclusions, ShadowCopyManager? shadow, ResultSet results, Action<string?, string?> status)
    {
        Writer = writer;
        Inventory = inventory;
        Selection = selection;
        Options = options;
        Hives = hives;
        Exclusions = exclusions;
        Shadow = shadow;
        Results = results;
        this.status = status;
    }

    public PackageWriter Writer { get; }
    public Inventory Inventory { get; }
    public Selection Selection { get; }
    public CaptureOptions Options { get; }
    public HiveCache Hives { get; }
    public ExclusionRules Exclusions { get; }
    public ShadowCopyManager? Shadow { get; }
    public ResultSet Results { get; }

    public UserProfileInfo? UserOf(InventoryItem item) => Inventory.FindUser(item.UserSid);

    public RegistryKey? UserRegistry(InventoryItem item)
    {
        var user = UserOf(item);
        if (user is null)
            return null;
        var hive = Hives.Get(user, out var error);
        if (hive is null)
            Results.Warn(item, $"Could not open {user.AccountName}'s registry: {error}");
        return hive?.Root;
    }

    public string? ModeOf(InventoryItem item) => Selection.ModeFor(item);

    /// <summary>Updates the progress line; throttled so tiny files don't flood the UI.</summary>
    public void Status(InventoryItem? item, string? detail, bool force = false)
    {
        if (!force && statusClock.ElapsedMilliseconds < 150)
            return;
        statusClock.Restart();
        status(item?.Name, detail);
    }

    /// <summary>
    /// Opens a file for reading even if another program has it open. Falls back to a shadow copy for files that
    /// are locked. Returns null (with the failure recorded) when the file can't be read at all.
    /// </summary>
    public async Task<Stream?> OpenSourceAsync(InventoryItem item, string path, CancellationToken ct, bool preferShadow = false)
    {
        if (preferShadow)
        {
            var shadowStream = await OpenFromShadowAsync(path, ct).ConfigureAwait(false);
            if (shadowStream is not null)
                return shadowStream;
        }
        try
        {
            return OpenShared(path);
        }
        catch (IOException ex) when (IsLockError(ex))
        {
            var shadowStream = await OpenFromShadowAsync(path, ct).ConfigureAwait(false);
            if (shadowStream is not null)
                return shadowStream;
            Results.FileFailed(item, path, "it is in use by another program. Close the program and try again.");
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted since the scan.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Results.FileFailed(item, path, "access was denied.");
            return null;
        }
        catch (IOException ex)
        {
            Results.FileFailed(item, path, ex.Message);
            return null;
        }
    }

    private async Task<Stream?> OpenFromShadowAsync(string path, CancellationToken ct)
    {
        if (Shadow is null || !Options.UseShadowCopy)
            return null;
        var shadowPath = await Shadow.TranslateAsync(path, ct).ConfigureAwait(false);
        if (shadowPath is null)
            return null;
        try
        {
            return OpenShared(shadowPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Shadow copy read of {path} failed: {ex.Message}");
            return null;
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static bool IsLockError(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Writes one file as an entry, retrying from a shadow copy if it turns out to be locked part-way.</summary>
    public async Task<bool> CaptureFileAsync(InventoryItem item, string path, string entryName, FileMeta meta, CancellationToken ct,
        Dictionary<string, string>? extra = null)
    {
        var stream = await OpenSourceAsync(item, path, ct).ConfigureAwait(false);
        if (stream is null)
            return false;
        try
        {
            await using (stream)
            {
                var bytes = await Writer.WriteFileAsync(entryName, item.Id, stream, meta, extra, ct).ConfigureAwait(false);
                Results.FileDone(item, bytes);
                return true;
            }
        }
        catch (SourceReadException ex)
        {
            var retry = await OpenFromShadowAsync(path, ct).ConfigureAwait(false);
            if (retry is null)
            {
                Results.FileFailed(item, path, ex.InnerException is IOException io && IsLockError(io) ? "it is in use by another program." : ex.Message);
                return false;
            }
            try
            {
                await using (retry)
                {
                    var bytes = await Writer.WriteFileAsync(entryName, item.Id, retry, meta, extra, ct).ConfigureAwait(false);
                    Results.FileDone(item, bytes);
                    return true;
                }
            }
            catch (SourceReadException again)
            {
                Results.FileFailed(item, path, again.Message);
                return false;
            }
        }
    }

    /// <summary>Small files are read this many at a time ahead of the writer.</summary>
    private const int ReadAhead = 16;
    private const long ReadAheadLimit = 2 << 20;

    /// <summary>
    /// Writes a whole folder tree as entries "&lt;entryPrefix&gt;/&lt;relative path&gt;".
    /// Opening a file costs far more than reading a small one (antivirus scans it on open), so up to
    /// <see cref="ReadAhead"/> small files are opened and read in parallel while the package is written in order.
    /// </summary>
    public async Task CaptureFolderAsync(InventoryItem item, string root, string entryPrefix, ExclusionRules? rules, CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            Results.Warn(item, $"{root} no longer exists.");
            return;
        }
        var stats = new WalkStats();
        var ahead = new Queue<(WalkEntry Entry, string Name, Task<byte[]?> Content)>();
        foreach (var entry in FileWalker.Walk(root, rules ?? Exclusions, stats, ct, Options.MaxFileSizeBytes))
        {
            var name = $"{entryPrefix}/{entry.RelativePath}";
            if (entry.IsDirectory)
            {
                await Writer.WriteDirectoryAsync(name, item.Id, FileMeta.From(entry), ct).ConfigureAwait(false);
                continue;
            }
            if (entry.Length <= ReadAheadLimit)
            {
                var path = entry.FullPath;
                ahead.Enqueue((entry, name, Task.Run(() => TryReadSmall(path), ct)));
                while (ahead.Count >= ReadAhead)
                    await WriteReadAheadAsync(item, ahead.Dequeue(), ct).ConfigureAwait(false);
                continue;
            }
            Status(item, entry.RelativePath);
            await CaptureFileAsync(item, entry.FullPath, name, FileMeta.From(entry), ct).ConfigureAwait(false);
        }
        while (ahead.Count > 0)
            await WriteReadAheadAsync(item, ahead.Dequeue(), ct).ConfigureAwait(false);
        if (stats.CloudOnlySkipped > 0)
            Results.Info(item, $"{Format.Count(stats.CloudOnlySkipped, "cloud-only file")} skipped (they stay in OneDrive or other cloud storage).");
    }

    private async Task WriteReadAheadAsync(InventoryItem item, (WalkEntry Entry, string Name, Task<byte[]?> Content) next, CancellationToken ct)
    {
        Status(item, next.Entry.RelativePath);
        var content = await next.Content.ConfigureAwait(false);
        if (content is null)
        {
            // Locked, denied or gone: the normal path retries from a shadow copy and reports the outcome.
            await CaptureFileAsync(item, next.Entry.FullPath, next.Name, FileMeta.From(next.Entry), ct).ConfigureAwait(false);
            return;
        }
        var meta = FileMeta.From(next.Entry) with { Length = content.Length };
        await using var stream = new MemoryStream(content, writable: false);
        var bytes = await Writer.WriteFileAsync(next.Name, item.Id, stream, meta, ct: ct).ConfigureAwait(false);
        Results.FileDone(item, bytes);
    }

    private static byte[]? TryReadSmall(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var length = stream.Length;
            if (length > ReadAheadLimit * 2)
                return null;
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            // A file still being written may have grown; take whatever is there now.
            return stream.Read(new byte[1]) == 0 ? buffer : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Captures a registry subtree as a JSON data entry. Returns false when the key does not exist.</summary>
    public async Task<bool> CaptureRegistryAsync(InventoryItem item, RegistryKey baseKey, string root, string path, string entryName,
        CancellationToken ct, Func<string, bool>? skipSubkey = null, Func<string, string, bool>? valueFilter = null, bool recursive = true)
    {
        var export = RegistrySnapshot.Capture(baseKey, root, path, skipSubkey: skipSubkey, valueFilter: valueFilter, recursive: recursive);
        if (export is null)
            return false;
        if (export.Truncated)
            Results.Warn(item, $@"{root}\{path} is very large; only part of it was saved.");
        await Writer.WriteJsonAsync(entryName, item.Id, export, ct: ct).ConfigureAwait(false);
        return true;
    }
}
