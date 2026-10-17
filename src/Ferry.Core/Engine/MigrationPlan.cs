using System.Text.RegularExpressions;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.Core.Engine;

/// <summary>
/// A saved set of choices for unattended (silent) migrations, as JSON. Selection rules are applied in order on top of
/// the defaults: "all", "defaults", "category:Files", "migrator:wifi", "item:&lt;id&gt;", "name:*Chrome*", "group:*joe*".
/// </summary>
public sealed class MigrationPlan
{
    /// <summary>Users to scan (account, user name or SID). Empty = everyone.</summary>
    public List<string> Users { get; set; } = [];
    public List<string> ExtraFolders { get; set; } = [];

    /// <summary>File patterns never to copy, e.g. "*.iso", "node_modules", "Downloads\*.exe".</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>Selection rules that add items. Empty means "defaults".</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Selection rules that remove items, applied after <see cref="Include"/>.</summary>
    public List<string> Skip { get; set; } = [];

    /// <summary>Exact handling per item id, as saved from the app's selection screen.</summary>
    public Dictionary<string, string> Modes { get; set; } = [];

    /// <summary>"winget", "copy" or "default": how to move programs that offer a choice.</summary>
    public string Apps { get; set; } = "default";

    public ConflictPolicy Conflicts { get; set; } = ConflictPolicy.KeepBoth;

    /// <summary>Old user → new user, by account name, user name or SID. Unlisted users map by name, else to the signed-in user.</summary>
    public Dictionary<string, string> UserMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FolderMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CreateRestorePoint { get; set; } = true;
    public bool CloseApps { get; set; }
    public bool UseShadowCopy { get; set; } = true;
    public long? MaxFileSizeBytes { get; set; }

    public static MigrationPlan Load(string path) => Json.ReadFile<MigrationPlan>(path);

    public void Save(string path) => Json.WriteFile(path, this);

    public ScanOptions ToScanOptions() => new()
    {
        Users = [.. Users],
        ExtraFolders = [.. ExtraFolders],
        ExcludePatterns = [.. Exclude],
    };

    public CaptureOptions ToCaptureOptions() => new()
    {
        UseShadowCopy = UseShadowCopy,
        ExcludePatterns = [.. Exclude],
        MaxFileSizeBytes = MaxFileSizeBytes,
    };

    /// <param name="everythingByDefault">
    /// True for a migration file or stream, where the old PC already chose: with no include rules, everything in it is restored.
    /// </param>
    public Selection BuildSelection(Inventory inventory, bool everythingByDefault = false)
    {
        var selection = Include.Count > 0 ? new Selection() : everythingByDefault ? Selection.All(inventory) : Selection.Defaults(inventory);
        foreach (var rule in Include)
            foreach (var item in Match(inventory, rule))
                selection.ItemIds.Add(item.Id);
        foreach (var rule in Skip)
            foreach (var item in Match(inventory, rule))
                selection.ItemIds.Remove(item.Id);

        foreach (var item in inventory.Items.Where(i => selection.Contains(i.Id) && i.AvailableModes.Count > 0))
        {
            var exact = Modes.FirstOrDefault(m => Glob(m.Key).IsMatch(item.Id)).Value;
            if (exact is not null && item.AvailableModes.Contains(exact))
                selection.Modes[item.Id] = exact;
            else if (Apps is ItemModes.Winget or ItemModes.Copy && item.AvailableModes.Contains(Apps))
                selection.Modes[item.Id] = Apps;
        }
        return selection;
    }

    private static IEnumerable<InventoryItem> Match(Inventory inventory, string rule)
    {
        var colon = rule.IndexOf(':');
        var kind = colon < 0 ? rule.Trim().ToLowerInvariant() : rule[..colon].Trim().ToLowerInvariant();
        var value = colon < 0 ? "" : rule[(colon + 1)..].Trim();
        return kind switch
        {
            "all" => inventory.Items,
            "defaults" => inventory.Items.Where(i => i.SelectedByDefault),
            "category" => inventory.Items.Where(i => string.Equals(i.Category.ToString(), value, StringComparison.OrdinalIgnoreCase)),
            "migrator" => inventory.Items.Where(i => string.Equals(i.MigratorId, value, StringComparison.OrdinalIgnoreCase)),
            "item" => inventory.Items.Where(i => Glob(value).IsMatch(i.Id)),
            "name" => inventory.Items.Where(i => Glob(value).IsMatch(i.Name)),
            "group" => inventory.Items.Where(i => Glob(value).IsMatch(i.Group)),
            _ => throw new InvalidDataException($"Unknown selection rule \"{rule}\". Use all, defaults, category:, migrator:, item:, name: or group:."),
        };
    }

    private static string Portable(string id) => Regex.Replace(id, @"S-1-[0-9-]+", "*");

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Restore options for this PC: the plan's user map resolved to SIDs, with every other source user mapped to the
    /// best match here (same name, else the signed-in user).
    /// </summary>
    public RestoreOptions ToRestoreOptions(Inventory source, IReadOnlyList<UserProfileInfo> targetUsers)
    {
        var options = new RestoreOptions
        {
            Conflicts = Conflicts,
            CreateRestorePoint = CreateRestorePoint,
            CloseRunningApps = CloseApps,
            FolderMap = new Dictionary<string, string>(FolderMap, StringComparer.OrdinalIgnoreCase),
        };
        foreach (var user in source.Users)
        {
            var mapped = UserMap.FirstOrDefault(m => Matches(user, m.Key));
            var target = mapped.Key is not null
                ? targetUsers.FirstOrDefault(t => Matches(t, mapped.Value))
                  ?? throw new InvalidDataException($"The plan maps {mapped.Key} to {mapped.Value}, but there is no profile for {mapped.Value} on this PC. Sign in as that user once first.")
                : UserProfiles.SuggestMapping(user, targetUsers);
            if (target is not null)
                options.UserMap[user.Sid] = target.Sid;
        }
        return options;
    }

    private static bool Matches(UserProfileInfo user, string name) =>
        string.Equals(user.Sid, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(user.AccountName, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(user.UserName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A plan that reproduces exactly what was chosen in the app, for repeating it silently on other PCs.</summary>
    public static MigrationPlan FromSelection(Inventory inventory, Selection selection, RestoreOptions? restore = null)
    {
        var plan = new MigrationPlan { Include = ["all"] };
        // Ids contain user SIDs, which differ on every PC. Where every item matching an id with the SID wildcarded
        // was left out, skip by that portable pattern; otherwise skip the exact id.
        foreach (var group in inventory.Items.Where(i => !selection.Contains(i.Id)).GroupBy(i => Portable(i.Id)))
        {
            var allSkipped = inventory.Items.Where(i => Portable(i.Id) == group.Key).All(i => !selection.Contains(i.Id));
            if (allSkipped)
                plan.Skip.Add($"item:{group.Key}");
            else
                plan.Skip.AddRange(group.Select(i => $"item:{i.Id}"));
        }
        foreach (var (id, mode) in selection.Modes)
            plan.Modes[Portable(id)] = mode;
        if (restore is not null)
        {
            plan.Conflicts = restore.Conflicts;
            plan.CreateRestorePoint = restore.CreateRestorePoint;
            plan.CloseApps = restore.CloseRunningApps;
            foreach (var (from, to) in restore.FolderMap)
                plan.FolderMap[from] = to;
        }
        return plan;
    }
}
