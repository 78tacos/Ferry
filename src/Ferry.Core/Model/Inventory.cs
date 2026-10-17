using System.Text.Json.Serialization;

namespace Ferry.Core.Model;

/// <summary>The top-level groups items are shown under, in the order they are migrated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationCategory>))]
public enum MigrationCategory
{
    WindowsSettings,
    Applications,
    AppSettings,
    BrowsersAndEmail,
    Files,
}

public static class Categories
{
    public static string DisplayName(MigrationCategory category) => category switch
    {
        MigrationCategory.WindowsSettings => "Windows settings",
        MigrationCategory.Applications => "Applications",
        MigrationCategory.AppSettings => "Application settings",
        MigrationCategory.BrowsersAndEmail => "Browsers, Office & email",
        MigrationCategory.Files => "Files & folders",
        _ => category.ToString(),
    };

    public static string Description(MigrationCategory category) => category switch
    {
        MigrationCategory.WindowsSettings => "Wi-Fi networks, printers, mapped drives, wallpaper, theme, taskbar, fonts and more.",
        MigrationCategory.Applications => "Programs installed on the old PC. Reinstalled fresh with winget, or copied as-is (experimental).",
        MigrationCategory.AppSettings => "Per-user program settings stored in AppData and the registry.",
        MigrationCategory.BrowsersAndEmail => "Browser profiles and bookmarks, Outlook signatures and data files, Office customizations.",
        MigrationCategory.Files => "Documents, Desktop, Pictures and other folders.",
        _ => "",
    };
}

/// <summary>The kinds of handling a user can pick for an item. Only applications offer a choice today.</summary>
public static class ItemModes
{
    public const string Winget = "winget";
    public const string Copy = "copy";
    public const string Skip = "skip";

    public static string DisplayName(string mode) => mode switch
    {
        Winget => "Reinstall with winget",
        Copy => "Copy program files (experimental)",
        Skip => "Don't install",
        _ => mode,
    };
}

/// <summary>One selectable thing that can be migrated: a folder, an app, a Wi-Fi network, a settings group.</summary>
public sealed class InventoryItem
{
    /// <summary>Stable id, unique within an inventory: "&lt;migrator&gt;:&lt;key&gt;".</summary>
    public string Id { get; set; } = "";
    public string MigratorId { get; set; } = "";
    public MigrationCategory Category { get; set; }

    /// <summary>Sub-heading inside the category, e.g. a user name or "Wi-Fi networks".</summary>
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>The source user this item belongs to, if any. Restore maps it to a target user.</summary>
    public string? UserSid { get; set; }

    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool SelectedByDefault { get; set; } = true;
    public List<string> Warnings { get; set; } = [];

    /// <summary>Migrator-specific data needed to capture or restore the item (paths, ids, keys).</summary>
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Handling choices offered for this item (see <see cref="ItemModes"/>); empty when there is no choice.</summary>
    public List<string> AvailableModes { get; set; } = [];
    public string? DefaultMode { get; set; }

    public string? Prop(string key) => Properties.TryGetValue(key, out var value) ? value : null;

    public override string ToString() => $"{Id} ({Name})";
}

/// <summary>Everything found on a source PC that could be migrated.</summary>
public sealed class Inventory
{
    public int FormatVersion { get; set; } = 1;
    public Guid ScanId { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public MachineInfo Machine { get; set; } = new();
    public List<UserProfileInfo> Users { get; set; } = [];
    public List<InventoryItem> Items { get; set; } = [];

    /// <summary>Problems that affect the whole scan, e.g. not running as administrator.</summary>
    public List<string> Warnings { get; set; } = [];

    public InventoryItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public UserProfileInfo? FindUser(string? sid) =>
        sid is null ? null : Users.FirstOrDefault(u => string.Equals(u.Sid, sid, StringComparison.OrdinalIgnoreCase));
}
