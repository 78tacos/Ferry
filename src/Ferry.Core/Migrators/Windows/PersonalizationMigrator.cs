using System.Runtime.InteropServices;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Windows;

/// <summary>Per-user Windows look and feel, as groups of registry keys plus the files they refer to.</summary>
public sealed partial class PersonalizationMigrator : MigratorBase
{
    public override string Id => "personalization";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 13;

    private sealed record RegSpec(string Path, bool Recursive = false, string[]? OnlyValues = null);

    private sealed record FileSpec(KnownFolder Base, string RelativeDir, string Pattern = "*", bool Recursive = false, int MinBuild = 0);

    private sealed record SettingsGroup(string Key, string Name, string Description, RegSpec[] Registry, FileSpec[] Files, string? Warning = null);

    private const string WallpaperKey = "wallpaper";
    private const int Windows11Build = 22000;

    private static readonly SettingsGroup[] Groups =
    [
        new(WallpaperKey, "Desktop background", "Your wallpaper picture and how it's fitted to the screen.",
            [
                new(@"Control Panel\Desktop", OnlyValues: ["WallPaper", "WallpaperStyle", "TileWallpaper"]),
                new(@"Control Panel\Colors", OnlyValues: ["Background"]),
                new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers", OnlyValues: ["BackgroundType"]),
            ], []),
        new("theme", "Colors and theme", "Light or dark mode, accent color and transparency.",
            [
                new(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"),
                new(@"Software\Microsoft\Windows\DWM"),
                new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"),
            ], []),
        new("input", "Mouse, keyboard and touchpad", "Pointer speed, button swap, cursor scheme, key repeat and touchpad gestures.",
            [
                new(@"Control Panel\Mouse"),
                new(@"Control Panel\Keyboard"),
                new(@"Control Panel\Cursors", Recursive: true),
                new(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad", Recursive: true),
            ], []),
        new("regional", "Region, formats and keyboard layouts", "Date, time and number formats, display languages and input methods.",
            [
                new(@"Control Panel\International", Recursive: true),
                new(@"Keyboard Layout", Recursive: true),
            ], []),
        new("explorer", "File Explorer and taskbar options", "Show file extensions and hidden files, taskbar alignment and buttons, search box style.",
            [
                new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"),
                new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState"),
                new(@"Software\Microsoft\Windows\CurrentVersion\Search", OnlyValues: ["SearchboxTaskbarMode", "SearchboxTaskbarModeCache"]),
                new(@"Software\Microsoft\Windows\CurrentVersion\Explorer", OnlyValues: ["ShowRecent", "ShowFrequent", "ShowCloudFilesInQuickAccess", "EnableAutoTray"]),
            ], []),
        new("pins", "Taskbar and Start pins", "Programs pinned to the taskbar, and to Start on Windows 11.",
            [
                new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"),
            ],
            [
                new(KnownFolders.RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar", "*.lnk"),
                new(KnownFolders.LocalAppData, @"Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState", "start*.bin", MinBuild: Windows11Build),
            ],
            "Best effort: pins only show up for programs that are installed on the new PC, after signing in again."),
        new("accessibility", "Accessibility", "Text size, sticky keys, high contrast, cursor size and other ease-of-access settings.",
            [
                new(@"Control Panel\Accessibility", Recursive: true),
                new(@"Software\Microsoft\Accessibility", Recursive: true),
            ], []),
    ];

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        foreach (var user in context.Users)
        {
            var hive = context.UserRegistry(user);
            if (hive is null)
                continue;
            foreach (var group in Groups)
            {
                if (!group.Registry.Any(r => RegistrySnapshot.OpenRead(hive, r.Path) is { } k && Dispose(k)))
                    continue;
                var item = NewItem($"{user.Sid}:{group.Key}", user.AccountName, group.Name, user.Sid);
                item.Description = group.Description;
                item.Properties["group"] = group.Key;
                item.SizeBytes = 4096;
                if (group.Warning is not null)
                    item.Warnings.Add(group.Warning);
                if (group.Key == WallpaperKey && WallpaperFile(hive, user) is { } wallpaper)
                {
                    item.Description = $"{group.Description} ({Path.GetFileName(wallpaper)})";
                    item.SizeBytes = new FileInfo(wallpaper).Length;
                }
                context.Add(item);
            }
        }
        return Task.CompletedTask;
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }

    /// <summary>The wallpaper image worth copying: the user's own picture, not a Windows stock one.</summary>
    private static string? WallpaperFile(RegistryKey hive, UserProfileInfo user)
    {
        using var desktop = RegistrySnapshot.OpenRead(hive, @"Control Panel\Desktop");
        var path = desktop?.GetValue("WallPaper") as string;
        if (!string.IsNullOrEmpty(path))
        {
            path = KnownFolders.ExpandForUser(path, user.ProfilePath);
            if (File.Exists(path) && !KnownFolders.IsUnder(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web")))
                return path;
            if (File.Exists(path))
                return null;
        }
        // Spotlight, slideshows and deleted pictures leave only Windows' own processed copy.
        var transcoded = Path.Combine(user.FolderPaths.GetValueOrDefault(KnownFolders.RoamingAppData.Key) ?? "", @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var sourceBuild = BuildOf(context.Inventory.Machine);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var group = Groups.First(g => g.Key == item.Prop("group"));
            var hive = context.UserRegistry(item);
            var user = context.UserOf(item);
            if (hive is null || user is null)
                continue;
            context.Status(item, null, force: true);

            if (group.Key == WallpaperKey && WallpaperFile(hive, user) is { } wallpaper)
            {
                var name = Path.GetFileName(wallpaper);
                if (name == "TranscodedWallpaper")
                    name = "Wallpaper.jpg";
                await context.CaptureFileAsync(item, wallpaper, Entry(item.UserSid!, group.Key, "wallpaper", name), FileMeta.From(new FileInfo(wallpaper)), ct)
                    .ConfigureAwait(false);
            }

            for (var i = 0; i < group.Files.Length; i++)
            {
                var spec = group.Files[i];
                if (sourceBuild < spec.MinBuild)
                    continue;
                var dir = Path.Combine(user.FolderPaths.GetValueOrDefault(spec.Base.Key) ?? Path.Combine(user.ProfilePath, spec.Base.DefaultRelativePath), spec.RelativeDir);
                if (!Directory.Exists(dir))
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir, spec.Pattern, spec.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    var relative = Path.GetRelativePath(dir, file);
                    await context.CaptureFileAsync(item, file, Entry(item.UserSid!, group.Key, "files", i.ToString(), relative), FileMeta.From(new FileInfo(file)), ct)
                        .ConfigureAwait(false);
                }
            }

            for (var i = 0; i < group.Registry.Length; i++)
            {
                var spec = group.Registry[i];
                var only = spec.OnlyValues is null ? null : new HashSet<string>(spec.OnlyValues, StringComparer.OrdinalIgnoreCase);
                await context.CaptureRegistryAsync(item, hive, "HKCU", spec.Path, Entry(item.UserSid!, group.Key, "reg", $"{i}.json"), ct,
                    valueFilter: only is null ? null : (_, name) => only.Contains(name), recursive: spec.Recursive).ConfigureAwait(false);
            }
            context.Results.FileDone(item, 0);
        }
    }

    private sealed class WallpaperState
    {
        public readonly Dictionary<string, string> Restored = new(StringComparer.Ordinal);
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var group = Groups.FirstOrDefault(g => g.Key == item.Prop("group"));
        if (group is null)
            return;
        var rest = EntryNames.After(entry.Name, Entry(item.UserSid!, group.Key))!;
        var (part, tail) = EntryNames.SplitFirst(rest);
        var remap = context.RemapperFor(item);
        var wallpapers = context.State<WallpaperState>($"{Id}:wallpaper");

        switch (part)
        {
            case "wallpaper":
            {
                var folder = Path.Combine(context.TargetFolder(target, KnownFolders.Pictures), $"Wallpapers from {context.SourceComputer}");
                var path = Path.Combine(folder, tail);
                await context.Files.RestoreAsync(entry, item, path, ct, policy: ConflictPolicy.Overwrite).ConfigureAwait(false);
                lock (wallpapers)
                    wallpapers.Restored[item.Id] = path;
                break;
            }
            case "files":
            {
                var (index, relative) = EntryNames.SplitFirst(tail);
                var spec = group.Files[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)];
                if (BuildOf(context.Target) < spec.MinBuild)
                {
                    await entry.SkipAsync(ct).ConfigureAwait(false);
                    break;
                }
                var path = Path.Combine(context.TargetFolder(target, spec.Base), spec.RelativeDir, relative);
                await context.Files.RestoreAsync(entry, item, path, ct, remap, ConflictPolicy.Overwrite).ConfigureAwait(false);
                break;
            }
            case "reg":
            {
                var export = await entry.ReadJsonAsync<RegistryExport>(ct).ConfigureAwait(false);
                var root = context.UserRegistry(item, target);
                if (root is null)
                    break;
                string? wallpaperPath;
                lock (wallpapers)
                    wallpapers.Restored.TryGetValue(item.Id, out wallpaperPath);
                if (group.Key == WallpaperKey && wallpaperPath is not null)
                {
                    foreach (var value in export.Keys.SelectMany(k => k.Values).Where(v => v.Name.Equals("WallPaper", StringComparison.OrdinalIgnoreCase)))
                        value.S = wallpaperPath;
                }
                var result = RegistrySnapshot.Apply(root, export, context.Journal, context.DryRun, remap);
                foreach (var error in result.Errors.Take(5))
                    context.Results.Warn(item, error);
                if (group.Key == WallpaperKey && wallpaperPath is not null)
                    ApplyWallpaperNow(context, target, wallpaperPath);
                break;
            }
        }
    }

    private static void ApplyWallpaperNow(RestoreContext context, UserProfileInfo target, string path)
    {
        // SystemParametersInfo only affects the calling user's session.
        if (context.SimulateCommands || target.Sid != Elevation.CurrentUserSid)
            return;
        SystemParametersInfoW(0x0014 /* SPI_SETDESKWALLPAPER */, 0, path, 0x01 | 0x02);
    }

    internal static int BuildOf(MachineInfo machine)
    {
        var parts = machine.OsVersion.Split('.');
        return parts.Length >= 3 && int.TryParse(parts[2], out var build) ? build : 0;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, string value, uint flags);
}
