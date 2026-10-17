using Ferry.Core.Infrastructure;
using Microsoft.Win32;

namespace Ferry.Core.Rollback;

/// <summary>
/// Records every change a restore makes on the new PC so <see cref="UndoEngine"/> can reverse it.
/// Callers record a change before (or immediately after) making it.
/// </summary>
public interface IChangeJournal
{
    void FileCreated(string path);

    /// <summary>Moves an existing file into undo storage. The path is free to be written afterwards.</summary>
    void BackupFile(string path);

    void DirectoryCreated(string path);

    /// <summary>Moves an existing directory into undo storage (used when a whole browser profile is replaced).</summary>
    void BackupDirectory(string path);

    void RegistryKeyCreated(RegLocation location);

    /// <summary>A value is about to be set. Pass a null kind when the value did not exist before.</summary>
    void RegistryValueChanged(RegLocation location, string name, RegistryValueKind? previousKind, object? previousValue);

    /// <summary>Something was done with an external tool; undo runs the given command to reverse it.</summary>
    void CommandRun(string description, string undoFileName, string undoArguments);
}

/// <summary>Used for dry runs: nothing is changed, so nothing needs recording.</summary>
public sealed class NullJournal : IChangeJournal
{
    public static readonly NullJournal Instance = new();

    public void FileCreated(string path) { }
    public void BackupFile(string path) { }
    public void DirectoryCreated(string path) { }
    public void BackupDirectory(string path) { }
    public void RegistryKeyCreated(RegLocation location) { }
    public void RegistryValueChanged(RegLocation location, string name, RegistryValueKind? previousKind, object? previousValue) { }
    public void CommandRun(string description, string undoFileName, string undoArguments) { }
}
