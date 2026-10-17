using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators;

/// <summary>
/// Folders outside user profiles: ones the user adds (D:\Projects), plus non-system folders found at drive roots
/// (C:\Scans, E:\Photos), which are offered but not ticked. Restored to the same path, or a mapped one.
/// </summary>
public sealed class FoldersMigrator : MigratorBase
{
    public override string Id => "folders";
    public override MigrationCategory Category => MigrationCategory.Files;
    public override int Order => 95;

    /// <summary>Also offer non-system folders found at drive roots. Off when only measuring folders the user just added.</summary>
    public bool IncludeDriveRoots { get; init; } = true;

    private static readonly HashSet<string> SystemRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "Users", "PerfLogs", "Recovery",
        "System Volume Information", "$Recycle.Bin", "$RECYCLE.BIN", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset",
        "$GetCurrent", "$WINDOWS.~Q", "Config.Msi", "Documents and Settings", "MSOCache", "Intel", "AMD", "NVIDIA", "Drivers",
        "OneDriveTemp", "inetpub", "XboxGames", "ESD", "SWSetup", "Dell", "HP", "Lenovo", "Temp", "tmp", "WindowsApps",
        "WpSystem", "Program Files (Arm)", "Boot", "EFI", "Apps", "OEM", "PNPDrivers", "Logs", "dotnet", "msys64", "cygwin64", "Python",
    };

    public override async Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        var candidates = new List<(string Path, bool Explicit)>();
        foreach (var folder in context.Options.ExtraFolders)
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder)).TrimEnd('\\');
            if (Directory.Exists(full))
                candidates.Add((full, true));
            else
                context.Warn($"The folder {folder} does not exist and was not included.");
        }

        foreach (var drive in context.Machine.Drives.Where(d => IncludeDriveRoots && !d.IsRemovable))
        {
            foreach (var dir in SafeDirectories(drive.Name))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('$') || name.StartsWith('.') || SystemRootFolders.Contains(name))
                    continue;
                if (candidates.Any(c => string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    if ((File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                candidates.Add((dir, false));
            }
        }

        var tasks = new List<Task>();
        foreach (var (path, isExplicit) in candidates)
        {
            var item = NewItem(KeyFor(path), "Other folders", path);
            item.Description = isExplicit ? "Added by you" : $"Found on drive {Path.GetPathRoot(path)}";
            item.Properties["path"] = path;
            item.SelectedByDefault = isExplicit;
            context.Add(item);
            tasks.Add(Task.Run(() =>
            {
                var stats = FileWalker.Measure(path, context.Exclusions, ct);
                item.SizeBytes = stats.Bytes;
                item.FileCount = stats.Files;
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>A short, stable key for a folder path (paths themselves can be long and contain '/'-unsafe text).</summary>
    internal static string KeyFor(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..12].ToLowerInvariant();

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await context.CaptureFolderAsync(item, item.Prop("path")!, Entry(KeyOf(item)), context.Exclusions, ct).ConfigureAwait(false);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var relative = EntryNames.After(entry.Name, Entry(KeyOf(item)));
        if (relative is null)
            return;
        var root = TargetRoot(context, item);
        var logicalPath = Path.Combine(root, relative);
        context.Status(item, logicalPath);
        if (entry.Kind == EntryKind.Directory)
            context.Files.RestoreDirectory(entry, logicalPath);
        else
            await context.Files.RestoreAsync(entry, item, logicalPath, ct, context.RemapperFor(item), background: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Where a folder goes on the new PC: the mapping chosen by the user; else the same path if that drive exists;
    /// else under C:\From OLDPC\D\… so nothing lands on a drive letter that isn't there.
    /// </summary>
    public static string TargetRoot(RestoreContext context, InventoryItem item)
    {
        var source = item.Prop("path")!;
        if (context.Options.FolderMap.TryGetValue(source, out var mapped))
            return mapped;
        return DefaultTarget(source, context.Target.Drives, context.SourceComputer);
    }

    /// <summary>
    /// The same path when its drive exists here; otherwise C:\From OLDPC\D\… (or …\server_share\… for a network
    /// folder, which must never be written back to the share itself).
    /// </summary>
    public static string DefaultTarget(string source, IEnumerable<DriveSummary> drives, string sourceComputer)
    {
        var root = Path.GetPathRoot(source);
        var isUnc = root?.StartsWith(@"\\", StringComparison.Ordinal) == true;
        if (root is not null && !isUnc && drives.Any(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase)))
            return source;
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var relative = root is null ? source.TrimStart('\\') : source[root.Length..].TrimStart('\\');
        var rootName = root is null ? "" : isUnc ? root.Trim('\\').Replace('\\', '_') : root.TrimEnd(':', '\\');
        return Path.Combine(systemDrive, $"From {sourceComputer}", rootName, relative);
    }

    private static string KeyOf(InventoryItem item) => item.Id[(item.Id.IndexOf(':') + 1)..];

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
