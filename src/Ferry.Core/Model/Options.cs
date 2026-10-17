using System.Text.Json.Serialization;

namespace Ferry.Core.Model;

/// <summary>What to do when a file being restored already exists on the new PC.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictPolicy>))]
public enum ConflictPolicy
{
    /// <summary>Keep the existing file and save the incoming one alongside it with "(from OLDPC)" in the name.</summary>
    KeepBoth,
    /// <summary>Replace the existing file only if the incoming one was modified more recently.</summary>
    OverwriteIfNewer,
    /// <summary>Always replace the existing file (it is backed up for undo first).</summary>
    Overwrite,
    /// <summary>Leave the existing file alone.</summary>
    Skip,
}

/// <summary>The items chosen for migration, with the handling chosen for items that offer one.</summary>
public sealed class Selection
{
    public HashSet<string> ItemIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Item id → chosen mode, for items with <see cref="InventoryItem.AvailableModes"/>.</summary>
    public Dictionary<string, string> Modes { get; set; } = new(StringComparer.Ordinal);

    public bool Contains(string itemId) => ItemIds.Contains(itemId);

    public string? ModeFor(InventoryItem item) =>
        Modes.TryGetValue(item.Id, out var mode) ? mode : item.DefaultMode;

    public static Selection Defaults(Inventory inventory)
    {
        var selection = new Selection();
        foreach (var item in inventory.Items.Where(i => i.SelectedByDefault))
            selection.ItemIds.Add(item.Id);
        return selection;
    }

    public static Selection All(Inventory inventory)
    {
        var selection = new Selection();
        foreach (var item in inventory.Items)
            selection.ItemIds.Add(item.Id);
        return selection;
    }
}

public sealed class ScanOptions
{
    /// <summary>Only scan these users (SIDs or account/user names). Empty means every real user profile.</summary>
    public List<string> Users { get; set; } = [];

    /// <summary>Folders outside user profiles to offer for migration, e.g. D:\Projects.</summary>
    public List<string> ExtraFolders { get; set; } = [];

    /// <summary>Extra exclusion patterns on top of the built-in ones (see <see cref="Infrastructure.ExclusionRules"/>).</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    /// <summary>Ask winget which installed programs it can reinstall. Slow-ish (a few seconds) but needed for app reinstall.</summary>
    public bool QueryWinget { get; set; } = true;
}

public sealed class CaptureOptions
{
    /// <summary>Read locked files (open Outlook data files, browser databases) from a Volume Shadow Copy snapshot.</summary>
    public bool UseShadowCopy { get; set; } = true;
    public List<string> ExcludePatterns { get; set; } = [];

    /// <summary>Skip individual files larger than this. Null means no limit.</summary>
    public long? MaxFileSizeBytes { get; set; }
}

public sealed class RestoreOptions
{
    /// <summary>Source user SID → target user SID. Items of unmapped users are skipped.</summary>
    public Dictionary<string, string> UserMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source folder → target folder, for folders outside user profiles (e.g. D:\Data → C:\Data).</summary>
    public Dictionary<string, string> FolderMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ConflictPolicy Conflicts { get; set; } = ConflictPolicy.KeepBoth;

    /// <summary>Walk the whole package and report what would happen without changing anything.</summary>
    public bool DryRun { get; set; }

    /// <summary>Create a System Restore point before changing anything (best effort; needs System Protection).</summary>
    public bool CreateRestorePoint { get; set; } = true;

    /// <summary>Root to redirect every write into, for testing restores safely. Null in real use.</summary>
    public string? SandboxRoot { get; set; }

    /// <summary>Close browsers, Outlook and Office on this PC when their data is about to be replaced.</summary>
    public bool CloseRunningApps { get; set; }
}
