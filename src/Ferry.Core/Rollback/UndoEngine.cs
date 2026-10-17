using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Microsoft.Win32;

namespace Ferry.Core.Rollback;

/// <summary>Lists, reverses and discards the undo data recorded by restores on this PC.</summary>
public static class UndoEngine
{
    public static List<UndoInfo> List()
    {
        var result = new List<UndoInfo>();
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.Undo))
        {
            var infoPath = Path.Combine(dir, "info.json");
            if (!File.Exists(infoPath))
                continue;
            try
            {
                var info = Json.ReadFile<UndoInfo>(infoPath);
                info.Directory = dir;
                result.Add(info);
            }
            catch (Exception ex)
            {
                Log.Warn($"Ignoring unreadable undo data in {dir}: {ex.Message}");
            }
        }
        return result.OrderByDescending(i => i.CreatedUtc).ToList();
    }

    public static UndoInfo? Find(string id) => List().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deletes undo data (and the backups in it) to free space. The migration can no longer be undone.</summary>
    public static void Discard(string id)
    {
        var info = Find(id) ?? throw new InvalidOperationException($"No undo data with id {id}.");
        Directory.Delete(info.Directory, recursive: true);
        Log.Info($"Discarded undo data {id}");
    }

    /// <summary>Reverses every recorded change, newest first. Keeps going past individual failures.</summary>
    public static async Task<MigrationReport> UndoAsync(string id, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        var info = Find(id) ?? throw new InvalidOperationException($"No undo data with id {id}.");
        if (info.Status == UndoStatus.Undone)
            throw new InvalidOperationException("This migration has already been undone.");

        using var awake = KeepAwake.Start("Ferry is undoing a migration");
        var report = new MigrationReport { Kind = OperationKind.Undo, Target = SystemInfo.Collect(), UndoId = id };
        var files = new ItemResult { ItemId = "undo:files", Name = "Files and folders", Category = MigrationCategory.Files };
        var registry = new ItemResult { ItemId = "undo:registry", Name = "Registry settings", Category = MigrationCategory.WindowsSettings };
        var commands = new ItemResult { ItemId = "undo:commands", Name = "Wi-Fi, printers, apps and other changes", Category = MigrationCategory.WindowsSettings };
        report.Items.AddRange([files, registry, commands]);

        var records = (await ReadLinesSharedAsync(Path.Combine(info.Directory, "journal.jsonl"), ct).ConfigureAwait(false))
            .Where(l => l.Length > 0)
            .Select(l =>
            {
                try
                {
                    return Json.FromText<JournalRecord>(l);
                }
                catch
                {
                    // A torn last line from a crash.
                    return null;
                }
            })
            .OfType<JournalRecord>()
            .ToList();

        var backup = Path.Combine(info.Directory, "backup");
        var hives = new Dictionary<string, IDisposable?>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, RegistryKey>(StringComparer.OrdinalIgnoreCase);

        // Undo can be run again after a partial or cancelled attempt: records already reversed are listed in
        // undone.txt and skipped, so a second pass never touches something the first one put back.
        var progressPath = Path.Combine(info.Directory, "undone.txt");
        var undone = File.Exists(progressPath)
            ? (await ReadLinesSharedAsync(progressPath, ct).ConfigureAwait(false)).Select(l => int.TryParse(l, out var n) ? n : -1).ToHashSet()
            : [];
        await using var progressLog = new StreamWriter(new FileStream(progressPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        try
        {
            for (var i = records.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                if (undone.Contains(i))
                    continue;
                var r = records[i];
                var done = records.Count - i;
                if (done % 50 == 0)
                    progress?.Report(new MigrationProgress { Phase = "Undoing changes", ItemsDone = done, ItemsTotal = records.Count, CurrentDetail = r.Path ?? r.Reg?.Path ?? r.Description });
                try
                {
                    switch (r.Op)
                    {
                        case JournalRecord.FileCreatedOp:
                            DeleteFile(r.Path!);
                            files.FilesDone++;
                            break;
                        case JournalRecord.FileReplacedOp:
                        {
                            // The record is written before the original is moved aside. No backup means the move never
                            // happened (or was already reversed), so whatever is at the path is the original: leave it.
                            var saved = Path.Combine(backup, r.Backup!);
                            if (File.Exists(saved))
                            {
                                DeleteFile(r.Path!);
                                Directory.CreateDirectory(Path.GetDirectoryName(r.Path!)!);
                                File.Move(saved, r.Path!, overwrite: true);
                            }
                            files.FilesDone++;
                            break;
                        }
                        case JournalRecord.DirCreatedOp:
                            if (Directory.Exists(r.Path) && !Directory.EnumerateFileSystemEntries(r.Path!).Any())
                                Directory.Delete(r.Path!);
                            break;
                        case JournalRecord.DirReplacedOp:
                        {
                            var saved = Path.Combine(backup, r.Backup!);
                            if (Directory.Exists(saved))
                            {
                                if (Directory.Exists(r.Path))
                                    Directory.Delete(r.Path!, recursive: true);
                                RollbackJournal.MoveDirectory(saved, r.Path!);
                            }
                            files.FilesDone++;
                            break;
                        }
                        case JournalRecord.RegKeyCreatedOp:
                            OpenBase(r.Reg!, hives, bases).DeleteSubKeyTree(r.Reg!.Path, throwOnMissingSubKey: false);
                            registry.FilesDone++;
                            break;
                        case JournalRecord.RegValueOp:
                            RestoreValue(OpenBase(r.Reg!, hives, bases), r);
                            registry.FilesDone++;
                            break;
                        case JournalRecord.CommandOp:
                            var result = await ProcessRunner.RunRawAsync(r.File!, r.Args ?? "", ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
                            if (result.Succeeded)
                            {
                                commands.FilesDone++;
                                commands.Messages.Add(new ItemMessage(MessageLevel.Info, $"Reversed: {r.Description}"));
                            }
                            else
                            {
                                commands.FilesFailed++;
                                commands.Messages.Add(new ItemMessage(MessageLevel.Warning, $"Could not reverse \"{r.Description}\" (exit code {result.ExitCode})."));
                                // Not marked as undone, so running undo again retries it.
                                continue;
                            }
                            break;
                    }
                    await progressLog.WriteLineAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var target = r.Op.StartsWith("reg", StringComparison.Ordinal) ? registry : r.Op == JournalRecord.CommandOp ? commands : files;
                    target.FilesFailed++;
                    if (target.Messages.Count < 200)
                        target.Messages.Add(new ItemMessage(MessageLevel.Warning, $"{r.Path ?? r.Reg?.ToString() ?? r.Description}: {ex.Message}"));
                    Log.Warn($"Undo {r.Op} {r.Path ?? r.Reg?.ToString()}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var hive in hives.Values)
                hive?.Dispose();
        }

        foreach (var item in report.Items)
            item.Finish();
        var failed = report.Items.Sum(i => i.FilesFailed);
        info.Status = failed == 0 ? UndoStatus.Undone : UndoStatus.PartiallyUndone;
        info.UndoneUtc = DateTime.UtcNow;
        Json.WriteFile(Path.Combine(info.Directory, "info.json"), info);
        if (failed == 0)
            report.Notes.Add("Every recorded change was reversed. You can now discard this undo data to free up space.");
        report.Notes.Add("Sign out and back in (or restart) so Windows and apps pick up the restored settings.");
        report.FinishedUtc = DateTime.UtcNow;
        report.LogPath = Log.FilePath;
        return report;
    }

    /// <summary>Reads a file another process (an unfinished restore) may still have open for writing.</summary>
    private static async Task<string[]> ReadLinesSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return text.Split('\n', StringSplitOptions.TrimEntries);
    }

    private static RegistryKey OpenBase(RegLocation location, Dictionary<string, IDisposable?> hives, Dictionary<string, RegistryKey> bases)
    {
        var key = location.Root == "USER" ? $"USER:{location.Sid}" : location.Root;
        if (bases.TryGetValue(key, out var existing))
            return existing;
        var opened = RegistryRoot.OpenBase(location, out var hive);
        hives[key] = hive;
        bases[key] = opened;
        return opened;
    }

    private static void RestoreValue(RegistryKey baseKey, JournalRecord r)
    {
        if (r.Previous is null)
        {
            using var key = baseKey.OpenSubKey(r.Reg!.Path, writable: true);
            key?.DeleteValue(r.Name ?? "", throwOnMissingValue: false);
            return;
        }
        using var target = baseKey.CreateSubKey(r.Reg!.Path, writable: true);
        target.SetValue(r.Name ?? "", r.Previous.ToRegistryObject(null), r.Previous.Kind);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }
}
