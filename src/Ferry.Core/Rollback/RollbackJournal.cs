using System.Text;
using System.Text.Json.Serialization;
using Ferry.Core.Infrastructure;
using Microsoft.Win32;

namespace Ferry.Core.Rollback;

[JsonConverter(typeof(JsonStringEnumConverter<UndoStatus>))]
public enum UndoStatus { InProgress, Completed, Failed, Cancelled, Undone, PartiallyUndone }

/// <summary>Summary of one restore's undo data, stored as info.json next to the journal.</summary>
public sealed class UndoInfo
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UndoneUtc { get; set; }
    public string SourceComputer { get; set; } = "";
    public string TargetComputer { get; set; } = Environment.MachineName;
    public List<string> TargetUsers { get; set; } = [];
    public UndoStatus Status { get; set; } = UndoStatus.InProgress;
    public int ChangeCount { get; set; }
    public long BackupBytes { get; set; }

    [JsonIgnore] public string Directory { get; set; } = "";

    public string Title => $"From {SourceComputer} on {CreatedUtc.ToLocalTime():g}";
}

/// <summary>One line of journal.jsonl.</summary>
public sealed class JournalRecord
{
    public string Op { get; set; } = "";
    public string? Path { get; set; }
    public string? Backup { get; set; }
    public RegLocation? Reg { get; set; }
    public string? Name { get; set; }
    public RegValueData? Previous { get; set; }
    public string? Description { get; set; }
    public string? File { get; set; }
    public string? Args { get; set; }

    public const string FileCreatedOp = "fileCreated";
    public const string FileCreateAbandonedOp = "fileCreateAbandoned";
    public const string FileReplacedOp = "fileReplaced";
    public const string DirCreatedOp = "dirCreated";
    public const string DirReplacedOp = "dirReplaced";
    public const string RegKeyCreatedOp = "regKeyCreated";
    public const string RegValueOp = "regValue";
    public const string CommandOp = "command";
}

/// <summary>
/// Durable, append-only record of a restore's changes, under &lt;data&gt;\Undo\&lt;id&gt;\. Files that get replaced
/// are moved into backup\ first; everything else is reversible from the record alone.
/// </summary>
public sealed class RollbackJournal : IChangeJournal, IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly string backupDirectory;
    private readonly UndoInfo info;
    private int backupCounter;
    private bool crossVolumeCopy;
    private Action<string, string>? beforeCrossVolumeCopy;

    private RollbackJournal(UndoInfo info)
    {
        this.info = info;
        backupDirectory = Path.Combine(info.Directory, "backup");
        System.IO.Directory.CreateDirectory(backupDirectory);
        writer = new StreamWriter(new FileStream(Path.Combine(info.Directory, "journal.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
        SaveInfo();
    }

    public string Id => info.Id;
    public string Directory => info.Directory;
    public int ChangeCount => info.ChangeCount;

    public static RollbackJournal Create(string sourceComputer, IEnumerable<string> targetUsers)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x4}";
        var dir = Path.Combine(AppPaths.Undo, id);
        System.IO.Directory.CreateDirectory(dir);
        return new RollbackJournal(new UndoInfo
        {
            Id = id,
            Directory = dir,
            SourceComputer = sourceComputer,
            TargetUsers = targetUsers.ToList(),
        });
    }

    public void FileCreated(string path) => Append(new JournalRecord { Op = JournalRecord.FileCreatedOp, Path = path });

    /// <summary>
    /// The matching <see cref="FileCreated"/> did not keep a file. Undo then leaves a later file at
    /// <paramref name="path"/> alone instead of deleting it with that create.
    /// </summary>
    public void FileCreateAbandoned(string path) => Append(new JournalRecord { Op = JournalRecord.FileCreateAbandonedOp, Path = path });

    /// <remarks>
    /// The record goes in first: if the move then fails or the process dies, undo finds no backup and leaves
    /// whatever is at the path (the original) alone. A file on another volume is copied, and the original is
    /// deleted only after that copy has been published under its backup name.
    /// </remarks>
    public void BackupFile(string path)
    {
        lock (gate)
        {
            var name = $"f{++backupCounter:D7}";
            var backup = Path.Combine(backupDirectory, name);
            var length = new FileInfo(path).Length;
            Append(new JournalRecord { Op = JournalRecord.FileReplacedOp, Path = path, Backup = name });
            MoveFile(path, backup, crossVolumeCopy, beforeCrossVolumeCopy);
            info.BackupBytes += length;
        }
    }

    public void DirectoryCreated(string path) => Append(new JournalRecord { Op = JournalRecord.DirCreatedOp, Path = path });

    public void BackupDirectory(string path)
    {
        lock (gate)
        {
            var name = $"d{++backupCounter:D7}";
            var backup = Path.Combine(backupDirectory, name);
            Append(new JournalRecord { Op = JournalRecord.DirReplacedOp, Path = path, Backup = name });
            MoveDirectory(path, backup, crossVolumeCopy, beforeCrossVolumeCopy, markIncompleteBackup: true);
        }
    }

    /// <summary>
    /// Test seam. Backups copy and then delete, as they do when the file and the undo folder are on different
    /// volumes, even when both paths share a drive. <paramref name="beforeCopy"/> runs before each file is copied
    /// and may throw to fail that copy partway.
    /// </summary>
    internal void UseCrossVolumeCopy(Action<string, string>? beforeCopy = null)
    {
        crossVolumeCopy = true;
        beforeCrossVolumeCopy = beforeCopy;
    }

    public void RegistryKeyCreated(RegLocation location) =>
        Append(new JournalRecord { Op = JournalRecord.RegKeyCreatedOp, Reg = location });

    public void RegistryValueChanged(RegLocation location, string name, RegistryValueKind? previousKind, object? previousValue)
    {
        RegValueData? previous = null;
        if (previousKind is { } kind && previousValue is not null)
        {
            previous = new RegValueData { Name = name, Kind = kind };
            switch (previousValue)
            {
                case string s: previous.S = s; break;
                case string[] m: previous.M = m; break;
                case int i: previous.N = i; break;
                case long l: previous.N = l; break;
                case byte[] b: previous.B = Convert.ToBase64String(b); break;
            }
        }
        Append(new JournalRecord { Op = JournalRecord.RegValueOp, Reg = location, Name = name, Previous = previous });
    }

    public void CommandRun(string description, string undoFileName, string undoArguments) =>
        Append(new JournalRecord { Op = JournalRecord.CommandOp, Description = description, File = undoFileName, Args = undoArguments });

    public void Complete(UndoStatus status)
    {
        lock (gate)
        {
            info.Status = status;
            SaveInfo();
        }
    }

    private void Append(JournalRecord record)
    {
        lock (gate)
        {
            writer.WriteLine(Json.ToText(record, indented: false));
            info.ChangeCount++;
            // Keep the summary roughly current in case the process dies mid-restore.
            if (info.ChangeCount % 500 == 0)
                SaveInfo();
        }
    }

    /// <summary>The summary is a convenience (the journal itself is what undo needs), so failing to save it never stops a restore.</summary>
    private void SaveInfo()
    {
        try
        {
            Json.WriteFile(Path.Combine(info.Directory, "info.json"), info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not update the undo summary: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>. Across volumes that is a copy, and the
    /// source is deleted only after the copy has fully succeeded.
    /// </summary>
    internal static void MoveFile(string source, string destination, bool forceCrossVolume = false, Action<string, string>? beforeCopy = null)
    {
        try
        {
            if (forceCrossVolume)
                throw new IOException("Cannot move a file to a different volume.");
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException) when (IsDifferentVolume(source, destination, forceCrossVolume))
        {
            // Publish under the backup name only once the copy has finished, so a partial file is not a backup.
            var partial = PartialFilePath(destination);
            try
            {
                beforeCopy?.Invoke(source, destination);
                File.Copy(source, partial, overwrite: true);
                File.Move(partial, destination, overwrite: true);
            }
            catch
            {
                TryDeleteFile(partial);
                throw;
            }
            DeleteCopiedSource(source);
        }
    }

    internal static void MoveDirectory(string source, string destination) =>
        MoveDirectory(source, destination, forceCrossVolume: false, beforeCopy: null, markIncompleteBackup: false);

    private static void MoveDirectory(string source, string destination, bool forceCrossVolume, Action<string, string>? beforeCopy, bool markIncompleteBackup)
    {
        try
        {
            if (forceCrossVolume)
                throw new IOException("Cannot move a directory to a different volume.");
            System.IO.Directory.Move(source, destination);
        }
        catch (IOException) when (IsDifferentVolume(source, destination, forceCrossVolume))
        {
            // Directory.Move cannot cross volumes: copy, then delete the source.
            // The marker goes down first, so a copy that dies partway is not a finished backup.
            var marker = markIncompleteBackup ? IncompleteBackupMarker(destination) : null;
            var copyFinished = false;
            try
            {
                if (marker is not null)
                    File.WriteAllText(marker, "");
                CopyDirectory(source, destination, beforeCopy);
                copyFinished = true;
                if (marker is not null)
                    File.Delete(marker);
            }
            catch
            {
                if (!copyFinished)
                    RemovePartialBackup(destination, marker);
                throw;
            }
            System.IO.Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>A directory backup undo may restore. Missing, or still marked unfinished, does not count.</summary>
    internal static bool IsCompleteDirectoryBackup(string backupPath) =>
        System.IO.Directory.Exists(backupPath) && !File.Exists(IncompleteBackupMarker(backupPath));

    internal static string IncompleteBackupMarker(string backupPath) => backupPath + ".incomplete";

    private static bool IsDifferentVolume(string source, string destination, bool force) =>
        force || !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);

    private static string PartialFilePath(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        return Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
    }

    private static void CopyDirectory(string source, string destination, Action<string, string>? beforeCopy)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            beforeCopy?.Invoke(file, target);
            File.Copy(file, target, overwrite: true);
        }
        foreach (var dir in System.IO.Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), beforeCopy);
    }

    /// <summary>Drops a cross-volume copy that did not finish. The marker stays if the partial tree cannot be removed.</summary>
    private static void RemovePartialBackup(string destination, string? marker)
    {
        try
        {
            if (System.IO.Directory.Exists(destination))
                System.IO.Directory.Delete(destination, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (marker is null)
            return;
        try
        {
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tree is already gone, so the marker alone is not a backup undo would restore.
        }
    }

    private static void DeleteCopiedSource(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the partial name is not the backup undo looks for.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            SaveInfo();
            writer.Dispose();
        }
    }
}
