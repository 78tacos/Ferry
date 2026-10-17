using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.Core.Engine;

public sealed record PreflightIssue(bool Blocking, string Text);

/// <summary>Checks worth making before a capture or restore starts: space, rights, open programs, unmapped users.</summary>
public static class Preflight
{
    /// <summary>The program that must be closed for an item, keyed by the item's "browser" or "group" property.</summary>
    private static readonly Dictionary<string, string> ProcessByItemKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "chrome",
        ["chromium"] = "chrome",
        ["edge"] = "msedge",
        ["brave"] = "brave",
        ["vivaldi"] = "vivaldi",
        ["opera"] = "opera",
        ["firefox"] = "firefox",
        ["thunderbird"] = "thunderbird",
        ["pst"] = "outlook",
        ["templates"] = "winword",
        ["startup"] = "excel",
        ["stickynotes"] = "Microsoft.Notes",
    };

    public static List<PreflightIssue> ForCapture(Inventory inventory, Selection selection, string? destination, bool encrypted)
    {
        var issues = new List<PreflightIssue>();
        var selected = inventory.Items.Where(i => selection.Contains(i.Id)).ToList();
        if (selected.Count == 0)
            issues.Add(new(true, "Nothing is selected."));

        var bytes = selected.Sum(i => EffectiveSize(i, selection));
        if (destination is not null)
        {
            var free = SystemInfo.FreeSpace(destination);
            // Compression usually saves something, but photos and videos don't shrink, so plan for the full size.
            if (free > 0 && free < bytes)
                issues.Add(new(false, $"The destination has {Format.Bytes(free)} free but up to {Format.Bytes(bytes)} may be needed."));
            if (Packaging.PackageFiles.AutoPartSize(destination) is not null && bytes > Packaging.PackageFiles.Fat32PartSize)
                issues.Add(new(false, "The destination drive is FAT32, so the migration file will be split into 4 GB parts. Keep all the parts together."));
        }
        if (!encrypted && selected.Any(i => i.MigratorId is "wifi" or "browsers"))
            issues.Add(new(false, "The migration file will contain Wi-Fi passwords and browser data. Protect it with a password."));
        AddRunningApps(issues, selected, "Close {0} first so its data is copied in a consistent state.");
        return issues;
    }

    public static List<PreflightIssue> ForRestore(Inventory source, Selection selection, RestoreOptions options, IReadOnlyList<UserProfileInfo> targetUsers)
    {
        var issues = new List<PreflightIssue>();
        var selected = source.Items.Where(i => selection.Contains(i.Id)).ToList();
        if (selected.Count == 0)
            issues.Add(new(true, "Nothing is selected."));

        // Users whose data has nowhere to go.
        foreach (var user in source.Users)
        {
            var count = selected.Count(i => i.UserSid == user.Sid);
            if (count > 0 && !options.UserMap.ContainsKey(user.Sid))
                issues.Add(new(false, $"{user.AccountName}'s {Format.Count(count, "item")} will be skipped: no user on this PC was chosen for them."));
        }
        foreach (var sid in options.UserMap.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!targetUsers.Any(t => string.Equals(t.Sid, sid, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new(true, "One of the chosen users has no profile on this PC yet. Sign in as them once, then try again."));
        }

        if (!Elevation.IsAdministrator && options.SandboxRoot is null)
        {
            var needsAdmin = selected.Any(i => i.UserSid is null) ||
                             options.UserMap.Values.Any(sid => !string.Equals(sid, Elevation.CurrentUserSid, StringComparison.OrdinalIgnoreCase));
            if (needsAdmin)
                issues.Add(new(false, "Ferry is not running as administrator, so programs, Wi-Fi, printers and other users' data will fail. Restart it as administrator."));
        }

        // Disk space, per drive the data lands on.
        var needs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        foreach (var item in selected)
        {
            var size = EffectiveSize(item, selection);
            if (size == 0)
                continue;
            string drive;
            if (item.UserSid is not null && options.UserMap.TryGetValue(item.UserSid, out var sid) &&
                targetUsers.FirstOrDefault(t => string.Equals(t.Sid, sid, StringComparison.OrdinalIgnoreCase)) is { } target)
                drive = Path.GetPathRoot(target.ProfilePath) ?? systemDrive;
            else if (item.MigratorId == "folders" && item.Prop("path") is { } path)
                drive = Path.GetPathRoot(options.FolderMap.GetValueOrDefault(path) ?? path) is { } root && Directory.Exists(root) ? root : systemDrive;
            else
                drive = systemDrive;
            needs[drive] = needs.GetValueOrDefault(drive) + size;
        }
        foreach (var (drive, bytes) in needs)
        {
            var free = SystemInfo.FreeSpace(drive);
            if (free > 0 && free < bytes + (1L << 30))
                issues.Add(new(true, $"Drive {drive} has {Format.Bytes(free)} free, but {Format.Bytes(bytes)} is needed (plus room to spare). Free up space or choose less."));
        }

        if (selected.Any(i => i.MigratorId == "apps" && selection.ModeFor(i) == ItemModes.Winget) && !Winget.IsAvailable && options.SandboxRoot is null && !options.DryRun)
            issues.Add(new(false, "winget (App Installer) isn't available on this PC, so programs can't be reinstalled. Install \"App Installer\" from the Microsoft Store first."));

        AddRunningApps(issues, selected, options.CloseRunningApps
            ? "{0} is open and will be closed when its data is moved."
            : "Close {0} before starting, or its data can't be replaced.");
        return issues;
    }

    /// <summary>Bytes an item will move, given how it's handled (a copied program moves its files; a reinstalled one doesn't).</summary>
    public static long EffectiveSize(InventoryItem item, Selection selection)
    {
        if (item.MigratorId == "apps")
            return selection.ModeFor(item) == ItemModes.Copy && long.TryParse(item.Prop("copySize"), out var copy) ? copy : 0;
        return item.SizeBytes;
    }

    private static void AddRunningApps(List<PreflightIssue> issues, List<InventoryItem> selected, string format)
    {
        var processes = selected
            .Where(i => i.MigratorId is "browsers" or "office")
            .Select(i => ProcessByItemKind.GetValueOrDefault(i.Prop("browser") ?? i.Prop("group") ?? ""))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RunningApps.Running(processes))
            issues.Add(new(false, string.Format(System.Globalization.CultureInfo.CurrentCulture, format, name)));
    }

    /// <summary>Running programs that hold data for the selected items, as process names (for offering to close them).</summary>
    public static List<string> BlockingProcesses(IEnumerable<InventoryItem> selected) =>
        selected
            .Where(i => i.MigratorId is "browsers" or "office")
            .Select(i => ProcessByItemKind.GetValueOrDefault(i.Prop("browser") ?? i.Prop("group") ?? ""))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(RunningApps.IsRunning)
            .ToList();
}
