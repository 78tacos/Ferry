using System.Text;
using System.Text.Json.Nodes;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators.Email;

/// <summary>
/// Browser profiles: Chrome, Edge, Brave, Vivaldi, Opera (bookmarks, history, extensions, settings) and Firefox
/// (all of that plus saved passwords), and Thunderbird mail. Chromium browsers lock saved passwords and cookies to
/// the old PC, so those are left out rather than copied in a form the new PC can't read.
/// </summary>
public sealed class BrowsersMigrator : MigratorBase
{
    public override string Id => "browsers";
    public override MigrationCategory Category => MigrationCategory.BrowsersAndEmail;
    public override int Order => 40;

    private sealed record Chromium(string Key, string Name, KnownFolder Base, string UserData, string Process, bool SingleProfile = false);

    private static readonly Chromium[] ChromiumBrowsers =
    [
        new("chrome", "Google Chrome", KnownFolders.LocalAppData, @"Google\Chrome\User Data", "chrome"),
        new("edge", "Microsoft Edge", KnownFolders.LocalAppData, @"Microsoft\Edge\User Data", "msedge"),
        new("brave", "Brave", KnownFolders.LocalAppData, @"BraveSoftware\Brave-Browser\User Data", "brave"),
        new("vivaldi", "Vivaldi", KnownFolders.LocalAppData, @"Vivaldi\User Data", "vivaldi"),
        new("chromium", "Chromium", KnownFolders.LocalAppData, @"Chromium\User Data", "chrome"),
        new("opera", "Opera", KnownFolders.RoamingAppData, @"Opera Software\Opera Stable", "opera", SingleProfile: true),
    ];

    private const string LocalStateEntry = "_LocalState.json";
    private const string FirefoxKey = "firefox";
    private const string ThunderbirdKey = "thunderbird";

    /// <summary>Files Chromium encrypts with a key tied to the old PC's Windows account, plus caches and device state.</summary>
    private static readonly string[] ChromiumExclusions =
    [
        "Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal", "Cookies", "Cookies-journal",
        @"Network\Cookies", @"Network\Cookies-journal", "Safe Browsing*", "File System", "optimization_guide*", "Download Service",
        "GCM Store", "BudgetDatabase", "Local State", "Trust Tokens*", "DIPS*", "heavy_ad_intervention_opt_out*", "VideoDecodeStats",
        "shared_proto_db", "Site Characteristics Database", "*.tmp", @"**\Cache", @"**\Code Cache", @"**\GPUCache", @"**\DawnCache",
        @"**\DawnGraphiteCache", @"**\DawnWebGPUCache", @"**\GrShaderCache", @"**\ShaderCache", @"**\Service Worker\CacheStorage",
        @"**\Service Worker\ScriptCache", "Crashpad", "LOCK", "lockfile", "SingletonLock", "SingletonCookie", "SingletonSocket",
    ];

    private static readonly string[] FirefoxExclusions =
    [
        "cache2", "startupCache", "thumbnails", "crashes", "minidumps", "datareporting", "saved-telemetry-pings", "shader-cache",
        "parent.lock", "lock", ".parentlock", "*.tmp", @"storage\temporary", "safebrowsing",
    ];

    private static readonly string[] ThunderbirdExclusions = [.. FirefoxExclusions, "Crash Reports", "Pending Pings"];

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (var user in context.Users)
        {
            context.Status($"Looking for {user.UserName}'s browsers");
            foreach (var browser in ChromiumBrowsers)
            {
                var userData = Path.Combine(SourceFolder(user, browser.Base), browser.UserData);
                if (!Directory.Exists(userData))
                    continue;
                foreach (var (dir, name) in ChromiumProfiles(userData, browser))
                {
                    var item = NewItem($"{user.Sid}:{browser.Key}:{dir}", user.AccountName, $"{browser.Name} — {name}", user.Sid);
                    item.Description = "Bookmarks, history, extensions and settings";
                    item.Properties["browser"] = browser.Key;
                    item.Properties["profile"] = dir;
                    item.Properties["path"] = browser.SingleProfile ? userData : Path.Combine(userData, dir);
                    item.Warnings.Add($"Saved passwords and cookies can't be moved: {browser.Name} locks them to this PC. " +
                                      "Turn on sync (or export your passwords) before switching PCs, and sign in again on the new one.");
                    if (RunningApps.IsRunning(browser.Process))
                        item.Warnings.Add($"{browser.Name} is open. Close it before moving so nothing is half-written.");
                    context.Add(item);
                    tasks.Add(MeasureAsync(item, item.Prop("path")!, context.Exclusions.With(ChromiumExclusions), ct));
                }
            }

            var firefoxRoot = Path.Combine(SourceFolder(user, KnownFolders.RoamingAppData), @"Mozilla\Firefox");
            foreach (var (folder, path, name) in FirefoxProfiles(firefoxRoot))
            {
                var item = NewItem($"{user.Sid}:{FirefoxKey}:{folder}", user.AccountName, $"Firefox — {name}", user.Sid);
                item.Description = "Bookmarks, history, saved passwords, extensions and settings";
                item.Properties["browser"] = FirefoxKey;
                item.Properties["profile"] = folder;
                item.Properties["path"] = path;
                item.Properties["name"] = name;
                if (RunningApps.IsRunning("firefox"))
                    item.Warnings.Add("Firefox is open. Close it before moving so nothing is half-written.");
                context.Add(item);
                tasks.Add(MeasureAsync(item, path, context.Exclusions.With(FirefoxExclusions), ct));
            }

            var thunderbird = Path.Combine(SourceFolder(user, KnownFolders.RoamingAppData), "Thunderbird");
            if (File.Exists(Path.Combine(thunderbird, "profiles.ini")))
            {
                var item = NewItem($"{user.Sid}:{ThunderbirdKey}", user.AccountName, "Thunderbird email", user.Sid);
                item.Description = "Mail, accounts, address books and settings";
                item.Properties["browser"] = ThunderbirdKey;
                item.Properties["path"] = thunderbird;
                context.Add(item);
                tasks.Add(MeasureAsync(item, thunderbird, context.Exclusions.With(ThunderbirdExclusions), ct));
            }
        }
        return Task.WhenAll(tasks);
    }

    private static string SourceFolder(UserProfileInfo user, KnownFolder folder) =>
        user.FolderPaths.GetValueOrDefault(folder.Key) ?? Path.Combine(user.ProfilePath, folder.DefaultRelativePath);

    private static Task MeasureAsync(InventoryItem item, string path, ExclusionRules rules, CancellationToken ct) => Task.Run(() =>
    {
        var stats = FileWalker.Measure(path, rules, ct);
        item.SizeBytes = stats.Bytes;
        item.FileCount = stats.Files;
    }, ct);

    private static IEnumerable<(string Dir, string Name)> ChromiumProfiles(string userData, Chromium browser)
    {
        if (browser.SingleProfile)
        {
            yield return ("Default", "Default profile");
            yield break;
        }
        var localState = ReadJson(Path.Combine(userData, "Local State"));
        var cache = localState?["profile"]?["info_cache"] as JsonObject;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cache is not null)
        {
            foreach (var (dir, info) in cache)
            {
                if (!Directory.Exists(Path.Combine(userData, dir)))
                    continue;
                seen.Add(dir);
                var name = info?["name"]?.GetValue<string>() ?? dir;
                var email = info?["user_name"]?.GetValue<string>();
                yield return (dir, string.IsNullOrEmpty(email) ? name : $"{name} ({email})");
            }
        }
        if (!seen.Contains("Default") && Directory.Exists(Path.Combine(userData, "Default")))
            yield return ("Default", "Default profile");
    }

    private static IEnumerable<(string Folder, string Path, string Name)> FirefoxProfiles(string root)
    {
        var ini = IniFile.Load(Path.Combine(root, "profiles.ini"));
        foreach (var (section, values) in ini.Sections.Where(s => s.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)))
        {
            var path = values.FirstOrDefault(v => v.Key == "Path").Value;
            if (string.IsNullOrEmpty(path))
                continue;
            var relative = values.FirstOrDefault(v => v.Key == "IsRelative").Value != "0";
            var full = relative ? Path.Combine(root, path.Replace('/', '\\')) : path;
            if (!Directory.Exists(full))
                continue;
            yield return (Path.GetFileName(full.TrimEnd('\\')), full, values.FirstOrDefault(v => v.Key == "Name").Value ?? section);
        }
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ─────────────────────────────────── Capture ───────────────────────────────────

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var key = item.Prop("browser")!;
            var path = item.Prop("path")!;
            var user = context.UserOf(item)!;
            context.Status(item, null, force: true);
            switch (key)
            {
                case FirefoxKey:
                {
                    var root = Path.Combine(SourceFolder(user, KnownFolders.RoamingAppData), @"Mozilla\Firefox");
                    var ini = File.Exists(Path.Combine(root, "profiles.ini")) ? await File.ReadAllTextAsync(Path.Combine(root, "profiles.ini"), ct).ConfigureAwait(false) : "";
                    var installs = File.Exists(Path.Combine(root, "installs.ini")) ? await File.ReadAllTextAsync(Path.Combine(root, "installs.ini"), ct).ConfigureAwait(false) : "";
                    await context.Writer.WriteJsonAsync(Entry(item.UserSid!, FirefoxKey, "_profiles.json"), item.Id,
                        new Dictionary<string, string> { ["profiles"] = ini, ["installs"] = installs, ["folder"] = item.Prop("profile")!, ["name"] = item.Prop("name") ?? "" },
                        ct: ct).ConfigureAwait(false);
                    await context.CaptureFolderAsync(item, path, Entry(item.UserSid!, FirefoxKey, item.Prop("profile")!), context.Exclusions.With(FirefoxExclusions), ct)
                        .ConfigureAwait(false);
                    break;
                }
                case ThunderbirdKey:
                    await context.CaptureFolderAsync(item, path, Entry(item.UserSid!, ThunderbirdKey), context.Exclusions.With(ThunderbirdExclusions), ct)
                        .ConfigureAwait(false);
                    break;
                default:
                {
                    var browser = ChromiumBrowsers.First(b => b.Key == key);
                    var userData = Path.Combine(SourceFolder(user, browser.Base), browser.UserData);
                    var localState = ReadJson(Path.Combine(userData, "Local State")) as JsonObject;
                    if (localState is not null)
                    {
                        // The encryption key is tied to this PC's Windows account; the new PC must make its own.
                        localState.Remove("os_crypt");
                        await context.Writer.WriteDataAsync(Entry(item.UserSid!, key, item.Prop("profile")!, LocalStateEntry), item.Id,
                            Encoding.UTF8.GetBytes(localState.ToJsonString()), ct: ct).ConfigureAwait(false);
                    }
                    await context.CaptureFolderAsync(item, path, Entry(item.UserSid!, key, item.Prop("profile")!), context.Exclusions.With(ChromiumExclusions), ct)
                        .ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    // ─────────────────────────────────── Restore ───────────────────────────────────

    private sealed class RestoreState
    {
        /// <summary>Item id → profile folder name chosen on this PC (may differ when the original name is taken).</summary>
        public readonly Dictionary<string, string> TargetDirs = new(StringComparer.Ordinal);
        public readonly HashSet<string> Blocked = new(StringComparer.Ordinal);

        /// <summary>Profile folders (as written) already used by an item in this restore.</summary>
        public readonly HashSet<string> Assigned = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(InventoryItem Item, Dictionary<string, string> Data)> FirefoxIni = [];
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var state = context.State<RestoreState>(Id);
        var key = item.Prop("browser")!;

        if (!state.TargetDirs.ContainsKey(item.Id) && !state.Blocked.Contains(item.Id))
        {
            var process = key switch
            {
                FirefoxKey => "firefox",
                ThunderbirdKey => "thunderbird",
                _ => ChromiumBrowsers.First(b => b.Key == key).Process,
            };
            var problem = context.SimulateCommands ? null : await RunningApps.EnsureClosedAsync(process, context.Options.CloseRunningApps, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                state.Blocked.Add(item.Id);
                context.Results.MarkFailed(item, problem);
                return;
            }
            var chosen = PrepareTarget(context, state, item, target, key);
            if (chosen is null)
            {
                state.Blocked.Add(item.Id);
                context.Results.MarkSkipped(item, $"Another {item.Name.Split(' ')[0]} profile was already moved to {target.UserName} in this migration; " +
                                                  "only one can go to the same user.");
                return;
            }
            state.TargetDirs[item.Id] = chosen;
        }
        if (state.Blocked.Contains(item.Id))
            return;
        var targetDir = state.TargetDirs[item.Id];
        var remap = context.RemapperFor(item);

        switch (key)
        {
            case FirefoxKey:
            {
                var rest = EntryNames.After(entry.Name, Entry(item.UserSid!, FirefoxKey))!;
                if (rest == "_profiles.json")
                {
                    var data = await entry.ReadJsonAsync<Dictionary<string, string>>(ct).ConfigureAwait(false);
                    lock (state.FirefoxIni)
                        state.FirefoxIni.Add((item, data));
                    return;
                }
                var (_, relative) = EntryNames.SplitFirst(rest);
                var path = Path.Combine(FirefoxRoot(context, target), "Profiles", targetDir, relative);
                await RestorePathAsync(context, item, entry, path, remap, ct).ConfigureAwait(false);
                break;
            }
            case ThunderbirdKey:
            {
                var relative = EntryNames.After(entry.Name, Entry(item.UserSid!, ThunderbirdKey))!;
                var path = Path.Combine(context.TargetFolder(target, KnownFolders.RoamingAppData), "Thunderbird", relative);
                await RestorePathAsync(context, item, entry, path, remap, ct).ConfigureAwait(false);
                break;
            }
            default:
            {
                var browser = ChromiumBrowsers.First(b => b.Key == key);
                var userData = Path.Combine(context.TargetFolder(target, browser.Base), browser.UserData);
                var relative = EntryNames.After(entry.Name, Entry(item.UserSid!, key, item.Prop("profile")!))!;
                if (relative == LocalStateEntry)
                {
                    MergeLocalState(context, item, entry, await entry.ReadAllBytesAsync(ct).ConfigureAwait(false), userData, browser, targetDir);
                    return;
                }
                var path = browser.SingleProfile ? Path.Combine(userData, relative) : Path.Combine(userData, targetDir, relative);
                await RestorePathAsync(context, item, entry, path, remap, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    private static Task RestorePathAsync(RestoreContext context, InventoryItem item, PackageEntry entry, string path, PathRemapper remap, CancellationToken ct)
    {
        context.Status(item, path);
        if (entry.Kind == EntryKind.Directory)
        {
            context.Files.RestoreDirectory(entry, path);
            return Task.CompletedTask;
        }
        return context.Files.RestoreAsync(entry, item, path, ct, remap, ConflictPolicy.Overwrite, remapText: true, background: true);
    }

    private static string FirefoxRoot(RestoreContext context, UserProfileInfo target) =>
        Path.Combine(context.TargetFolder(target, KnownFolders.RoamingAppData), @"Mozilla\Firefox");

    /// <summary>
    /// Picks where a profile goes. A profile folder that is new or barely used on this PC is replaced (its old
    /// contents go to undo storage); one that holds real data is kept, and the incoming profile gets a new folder.
    /// </summary>
    /// <remarks>
    /// Folders already filled earlier in this restore are never picked again or replaced, so two old profiles (or
    /// two old users mapped to one new user) can't overwrite each other. Returns null when there's nowhere to put it.
    /// </remarks>
    private static string? PrepareTarget(RestoreContext context, RestoreState state, InventoryItem item, UserProfileInfo target, string key)
    {
        var profile = item.Prop("profile") ?? "";
        bool Taken(string logical) => state.Assigned.Contains(context.Redirect(logical));
        bool Claim(string logical) => state.Assigned.Add(context.Redirect(logical));
        switch (key)
        {
            case FirefoxKey:
            {
                var profiles = Path.Combine(FirefoxRoot(context, target), "Profiles");
                var dir = profile;
                for (var n = 2; Directory.Exists(context.Redirect(Path.Combine(profiles, dir))) || Taken(Path.Combine(profiles, dir)); n++)
                    dir = $"{profile}-{n}";
                Claim(Path.Combine(profiles, dir));
                return dir;
            }
            case ThunderbirdKey:
            {
                var root = Path.Combine(context.TargetFolder(target, KnownFolders.RoamingAppData), "Thunderbird");
                if (!Claim(root))
                    return null;
                if (Directory.Exists(context.Redirect(root)))
                    context.Files.ReplaceDirectory(root);
                return "";
            }
            default:
            {
                var browser = ChromiumBrowsers.First(b => b.Key == key);
                var userData = Path.Combine(context.TargetFolder(target, browser.Base), browser.UserData);
                if (browser.SingleProfile)
                {
                    if (!Claim(userData))
                        return null;
                    if (Directory.Exists(context.Redirect(userData)))
                        context.Files.ReplaceDirectory(userData);
                    return "";
                }
                var existing = context.Redirect(Path.Combine(userData, profile));
                if (!Taken(Path.Combine(userData, profile)))
                {
                    if (!Directory.Exists(existing))
                    {
                        Claim(Path.Combine(userData, profile));
                        return profile;
                    }
                    if (IsFreshChromiumProfile(existing))
                    {
                        context.Files.ReplaceDirectory(Path.Combine(userData, profile));
                        Claim(Path.Combine(userData, profile));
                        return profile;
                    }
                }
                var n = 1;
                while (Directory.Exists(context.Redirect(Path.Combine(userData, $"Profile {n}"))) || Taken(Path.Combine(userData, $"Profile {n}")))
                    n++;
                Claim(Path.Combine(userData, $"Profile {n}"));
                context.Results.Info(item, $"This PC already has a {browser.Name} profile in use, so this one was added as a separate profile " +
                                           "(pick it from the profile menu in the browser's toolbar).");
                return $"Profile {n}";
            }
        }
    }

    private static bool IsFreshChromiumProfile(string dir)
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        return Size(Path.Combine(dir, "History")) < 2 << 20 && Size(Path.Combine(dir, "Bookmarks")) < 8 << 10;
    }

    /// <summary>Adds the profile to the browser's profile list on this PC, keeping this PC's own encryption key.</summary>
    private static void MergeLocalState(RestoreContext context, InventoryItem item, PackageEntry entry, byte[] incomingBytes, string userData,
        Chromium browser, string targetDir)
    {
        if (browser.SingleProfile)
        {
            var path = Path.Combine(userData, "Local State");
            if (!File.Exists(context.Redirect(path)))
                context.Files.WriteBytes(item, path, incomingBytes, replace: false);
            return;
        }
        var incoming = JsonNode.Parse(incomingBytes) as JsonObject;
        var sourceDir = item.Prop("profile")!;
        var info = incoming?["profile"]?["info_cache"]?[sourceDir]?.DeepClone();
        var logical = Path.Combine(userData, "Local State");
        var existing = ReadJson(context.Redirect(logical)) as JsonObject;

        JsonObject result;
        if (existing is null)
        {
            // Nothing here yet: take the old PC's settings, listing just this profile.
            result = incoming ?? new JsonObject();
            var profile = result["profile"] as JsonObject ?? new JsonObject();
            result["profile"] = profile;
            profile["info_cache"] = new JsonObject();
            profile["last_used"] = targetDir;
            profile["last_active_profiles"] = new JsonArray(targetDir);
        }
        else
        {
            result = existing;
        }
        var profileNode = result["profile"] as JsonObject ?? new JsonObject();
        result["profile"] = profileNode;
        var cache = profileNode["info_cache"] as JsonObject ?? new JsonObject();
        profileNode["info_cache"] = cache;
        if (info is JsonObject infoObject)
        {
            if (!string.Equals(targetDir, sourceDir, StringComparison.OrdinalIgnoreCase))
                infoObject["name"] = $"{infoObject["name"]?.GetValue<string>() ?? "Profile"} (from {context.SourceComputer})";
            cache[targetDir] = infoObject;
        }
        else if (!cache.ContainsKey(targetDir))
        {
            cache[targetDir] = new JsonObject { ["name"] = $"From {context.SourceComputer}" };
        }
        context.Files.WriteBytes(item, logical, Encoding.UTF8.GetBytes(result.ToJsonString()), replace: true);
    }

    public override Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var state = context.State<RestoreState>(Id);
        foreach (var (item, data) in state.FirefoxIni)
        {
            if (state.Blocked.Contains(item.Id) || !context.TryGetTargetUser(item, out var target))
                continue;
            UpdateFirefoxProfiles(context, item, target, data, state.TargetDirs.GetValueOrDefault(item.Id) ?? data["folder"]);
        }
        foreach (var item in items.Where(i => i.Prop("browser") is { } b && b != FirefoxKey && b != ThunderbirdKey && !state.Blocked.Contains(i.Id)))
            context.Results.Info(item, "Sign in to the browser on this PC to bring back saved passwords through sync.");
        return Task.CompletedTask;
    }

    /// <summary>Registers the moved profile in profiles.ini. On a PC without Firefox profiles it becomes the default.</summary>
    private static void UpdateFirefoxProfiles(RestoreContext context, InventoryItem item, UserProfileInfo target, Dictionary<string, string> data, string folder)
    {
        var logical = Path.Combine(FirefoxRoot(context, target), "profiles.ini");
        var existingPath = context.Redirect(logical);
        var hadProfiles = File.Exists(existingPath);
        var ini = hadProfiles ? IniFile.Load(existingPath) : new IniFile();
        var relativePath = $"Profiles/{folder}";

        if (ini.Sections.Any(s => s.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) &&
                                  s.Values.Any(v => v.Key == "Path" && string.Equals(v.Value, relativePath, StringComparison.OrdinalIgnoreCase))))
            return;

        if (!hadProfiles)
        {
            ini.Set("General", "StartWithLastProfile", "1");
            ini.Set("General", "Version", "2");
        }
        var index = 0;
        while (ini.Sections.Any(s => s.Name.Equals($"Profile{index}", StringComparison.OrdinalIgnoreCase)))
            index++;
        var name = data.GetValueOrDefault("name") is { Length: > 0 } n ? n : folder;
        if (hadProfiles)
            name = $"{name} (from {context.SourceComputer})";
        var section = $"Profile{index}";
        ini.Set(section, "Name", name);
        ini.Set(section, "IsRelative", "1");
        ini.Set(section, "Path", relativePath);
        if (!hadProfiles)
        {
            ini.Set(section, "Default", "1");
            // Firefox pins each installation to a profile; carry the old PC's pins over for this profile.
            var source = IniFile.Parse(data.GetValueOrDefault("profiles") ?? "");
            foreach (var (installName, values) in source.Sections.Where(s => s.Name.StartsWith("Install", StringComparison.OrdinalIgnoreCase)))
            {
                if (values.Any(v => v.Key == "Default" && v.Value.EndsWith("/" + data["folder"], StringComparison.OrdinalIgnoreCase)))
                {
                    ini.Set(installName, "Default", relativePath);
                    ini.Set(installName, "Locked", "1");
                }
            }
        }
        context.Files.WriteBytes(item, logical, Encoding.UTF8.GetBytes(ini.ToString()), replace: true);
        if (hadProfiles)
            context.Results.ManualStep($"In Firefox, open about:profiles and choose \"{name}\" to use the profile from {context.SourceComputer}.");
    }
}
