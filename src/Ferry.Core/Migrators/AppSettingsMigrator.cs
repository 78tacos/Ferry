using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators;

/// <summary>
/// Per-user program settings: each vendor folder in AppData\Roaming (ticked) and AppData\Local (offered), and each
/// vendor key in HKCU\Software. Paths inside text settings and registry values are rewritten for the new user.
/// </summary>
public sealed class AppSettingsMigrator : MigratorBase
{
    public override string Id => "appdata";
    public override MigrationCategory Category => MigrationCategory.AppSettings;
    public override int Order => 30;

    private const string RoamingKind = "roaming";
    private const string LocalKind = "local";
    private const string RegistryKind = "reg";

    /// <summary>Roaming folders that belong to Windows or are moved by a more specific migrator.</summary>
    private static readonly HashSet<string> SkippedRoaming = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Mozilla", "Thunderbird", "Opera Software", "NVIDIA", "Intel", "AMD", "Macromedia", "Sun", "Media Center Programs",
        "Identities", "IDM", "Microsoft Corporation", "Windows Defender", "com.adobe.dunamis",
    };

    /// <summary>Local folders that are caches, Windows plumbing, installed programs, or moved elsewhere.</summary>
    private static readonly HashSet<string> SkippedLocal = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Google", "BraveSoftware", "Vivaldi", "Chromium", "Mozilla", "CrashDumps", "D3DSCache",
        "NVIDIA", "NVIDIA Corporation", "Intel", "AMD", "pip", "npm-cache", "Yarn", "SquirrelTemp", "Comms", "ConnectedDevicesPlatform",
        "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers", "Programs", "Microsoft Help", "VirtualStore", "History",
        "Temporary Internet Files", "Application Data", "ElevatedDiagnostics", "IsolatedStorage", "assembly", "cache", "fontconfig",
        "NuGet", "Diagnostics", "Deployment", "GroupPolicy", "Downloaded Installations", "Package Cache", "PackageManagement",
        "UnrealEngine", "CEF", "gtk-3.0", "Steam", "EpicGamesLauncher", "Docker", "ms-playwright", "JetBrains", "go-build",
    };

    /// <summary>HKCU\Software keys that are Windows' own or not tied to one app.</summary>
    private static readonly HashSet<string> SkippedRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "AppDataLow", "RegisteredApplications", "Clients", "Intel",
        "NVIDIA Corporation", "Realtek", "Wine", "Chromium", "Google", "Mozilla", "MozillaPlugins", "BraveSoftware", "Vivaldi",
        "Microsoft Corporation", "Khronos", "SyncEngines", "Netscape", "IM Providers", "ODBC", "Synaptics", "AMD", "ATI",
        "Waves Audio", "Dolby", "Conexant", "Partner", "CBSTEST", "Ferry", "FerrySandbox",
    };

    /// <summary>Caches inside app folders: rebuilt by the app, often large, never useful on another PC.</summary>
    public static readonly string[] CacheExclusions =
    [
        @"**\Cache", @"**\Cache2", @"**\Code Cache", @"**\GPUCache", @"**\CachedData", @"**\CachedExtensions", @"**\CachedExtensionVSIXs",
        @"**\Crashpad", @"**\Crash Reports", @"**\CrashDumps", @"**\DawnCache", @"**\DawnGraphiteCache", @"**\DawnWebGPUCache", @"**\GrShaderCache",
        @"**\ShaderCache", @"**\Service Worker\CacheStorage", @"**\Service Worker\ScriptCache", @"**\logs", @"**\Logs", @"**\temp", @"**\Temp",
        @"**\tmp", "*.log", "*.dmp", "*.lock", "LOCK", "lockfile", "SingletonLock", "SingletonCookie", "SingletonSocket",
    ];

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        var rules = context.Exclusions.With(CacheExclusions);
        var tasks = new List<Task>();
        foreach (var user in context.Users)
        {
            ct.ThrowIfCancellationRequested();
            context.Status($"Looking at {user.UserName}'s program settings");
            var roaming = user.FolderPaths.GetValueOrDefault(KnownFolders.RoamingAppData.Key) ?? Path.Combine(user.ProfilePath, @"AppData\Roaming");
            var local = user.FolderPaths.GetValueOrDefault(KnownFolders.LocalAppData.Key) ?? Path.Combine(user.ProfilePath, @"AppData\Local");

            foreach (var dir in SafeDirectories(roaming))
            {
                var name = Path.GetFileName(dir);
                if (SkippedRoaming.Contains(name) || name.StartsWith('.') && name.Length < 3)
                    continue;
                var item = NewItem($"{user.Sid}:{RoamingKind}:{name}", user.AccountName, $"{name} (app data)", user.Sid);
                item.Description = dir;
                item.Properties["path"] = dir;
                item.Properties["kind"] = RoamingKind;
                item.Properties["name"] = name;
                context.Add(item);
                tasks.Add(MeasureAsync(item, dir, rules, ct));
            }

            foreach (var dir in SafeDirectories(local))
            {
                var name = Path.GetFileName(dir);
                if (SkippedLocal.Contains(name))
                    continue;
                var item = NewItem($"{user.Sid}:{LocalKind}:{name}", user.AccountName, $"{name} (local app data)", user.Sid);
                item.Description = $"{dir} — local data is mostly caches; tick it if this program keeps settings here.";
                item.Properties["path"] = dir;
                item.Properties["kind"] = LocalKind;
                item.Properties["name"] = name;
                item.SelectedByDefault = false;
                context.Add(item);
                tasks.Add(MeasureAsync(item, dir, rules, ct));
            }

            var hive = context.UserRegistry(user);
            using var software = hive is null ? null : RegistrySnapshot.OpenRead(hive, "Software");
            if (software is null)
                continue;
            foreach (var name in software.GetSubKeyNames())
            {
                if (SkippedRegistry.Contains(name))
                    continue;
                using var key = RegistrySnapshot.OpenRead(software, name);
                if (key is null)
                    continue;
                var item = NewItem($"{user.Sid}:{RegistryKind}:{name}", user.AccountName, $"{name} (registry settings)", user.Sid);
                item.Description = $@"HKEY_CURRENT_USER\Software\{name}";
                item.Properties["kind"] = RegistryKind;
                item.Properties["name"] = name;
                item.SizeBytes = EstimateRegistrySize(key);
                context.Add(item);
            }
        }
        return Task.WhenAll(tasks);
    }

    private static Task MeasureAsync(InventoryItem item, string dir, ExclusionRules rules, CancellationToken ct) => Task.Run(() =>
    {
        var stats = FileWalker.Measure(dir, rules, ct);
        item.SizeBytes = stats.Bytes;
        item.FileCount = stats.Files;
        if (stats.Files == 0)
            item.SelectedByDefault = false;
        else if (stats.Bytes > 2L << 30 && item.SelectedByDefault)
            item.Warnings.Add("This is large for program settings; it may contain downloaded content the program can fetch again.");
    }, ct);

    private static long EstimateRegistrySize(Microsoft.Win32.RegistryKey key)
    {
        try
        {
            return 200L * (key.ValueCount + key.SubKeyCount * 4);
        }
        catch
        {
            return 0;
        }
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var rules = context.Exclusions.With(CacheExclusions);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var kind = item.Prop("kind")!;
            var name = item.Prop("name")!;
            if (kind == RegistryKind)
            {
                var hive = context.UserRegistry(item);
                if (hive is not null)
                    await context.CaptureRegistryAsync(item, hive, "HKCU", $@"Software\{name}", Entry(item.UserSid!, RegistryKind, name + ".json"), ct)
                        .ConfigureAwait(false);
                continue;
            }
            await context.CaptureFolderAsync(item, item.Prop("path")!, Entry(item.UserSid!, kind, name), rules, ct).ConfigureAwait(false);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var rest = EntryNames.After(entry.Name, Entry(item.UserSid!))!;
        var (kind, tail) = EntryNames.SplitFirst(rest);
        var remap = context.RemapperFor(item);

        if (kind == RegistryKind)
        {
            var export = await entry.ReadJsonAsync<RegistryExport>(ct).ConfigureAwait(false);
            var root = context.UserRegistry(item, target);
            if (root is null)
                return;
            context.Status(item, export.Path, force: true);
            var result = RegistrySnapshot.Apply(root, export, context.Journal, context.DryRun, remap);
            foreach (var error in result.Errors.Take(10))
                context.Results.Warn(item, error);
            context.Results.FileDone(item, 0);
            return;
        }

        var baseFolder = kind == LocalKind ? KnownFolders.LocalAppData : KnownFolders.RoamingAppData;
        var logicalPath = Path.Combine(context.TargetFolder(target, baseFolder), tail);
        context.Status(item, logicalPath);
        if (entry.Kind == EntryKind.Directory)
            context.Files.RestoreDirectory(entry, logicalPath);
        else
            // Settings from the old PC should win over defaults an app may already have written here.
            await context.Files.RestoreAsync(entry, item, logicalPath, ct, remap, ConflictPolicy.Overwrite, remapText: true, background: true).ConfigureAwait(false);
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Where(d => !IsLink(d)).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsLink(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }
}
