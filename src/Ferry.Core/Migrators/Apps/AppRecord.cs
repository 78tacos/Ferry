using System.Text.RegularExpressions;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Apps;

/// <summary>An installed program as found in the Uninstall registry keys (or the Store via winget).</summary>
public sealed class AppRecord
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallLocation { get; set; }

    /// <summary>"HKLM" or "HKCU".</summary>
    public string Root { get; set; } = "HKLM";

    /// <summary>Path of the Uninstall subkey under <see cref="Root"/>.</summary>
    public string? UninstallKeyPath { get; set; }
    public string? UserSid { get; set; }
    public string? WingetId { get; set; }
    public string? WingetSource { get; set; }
    public string? DisplayIcon { get; set; }
    public long EstimatedSizeBytes { get; set; }
    public bool IsSystemComponent { get; set; }
    public bool IsStoreApp { get; set; }

    public string Summary => string.Join(", ", new[] { Version is null ? null : $"version {Version}", Publisher }.Where(s => !string.IsNullOrEmpty(s)));

    public void ToProperties(InventoryItem item) => item.Properties["app"] = Json.ToText(this, indented: false);

    public static AppRecord FromItem(InventoryItem item) => Json.FromText<AppRecord>(item.Prop("app") ?? "{}");
}

internal static partial class AppCatalog
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UserUninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    // Lookarounds instead of \b so tokens ending in punctuation ("Intel(R)", "Visual C++") still match.
    [GeneratedRegex(@"(?<![a-z0-9])(driver|drivers|redistributable|runtime|chipset|firmware|bios|hotfix|security update|update for|sdk|\.net (framework|runtime|desktop)|visual c\+\+|directx|physx|realtek|intel\(r\)|intel®|nvidia|amd software|synaptics|dolby|maxxaudio|thunderbolt|bonjour|webview2|windows software development kit|microsoft update health|vs_|icecap|vcpp|msvc|windows app runtime|windowsappruntime|ms-resource|telemetry|bootstrapper|language pack|proofing tools)(?![a-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex SystemComponentPattern();

    [GeneratedRegex(@"\s*[\(\[]?\b(x64|x86|64-bit|32-bit|amd64|arm64|64 bit|32 bit|x64 en-us|en-us)\b[\)\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitecturePattern();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)+.*$")]
    private static partial Regex TrailingVersionPattern();

    public static List<AppRecord> ReadInstalled(IEnumerable<(UserProfileInfo User, RegistryKey? Hive)> users)
    {
        var apps = new List<AppRecord>();
        ReadUninstallKey(Registry.LocalMachine, "HKLM", UninstallPath, null, apps);
        ReadUninstallKey(Registry.LocalMachine, "HKLM", UninstallPath32, null, apps);
        foreach (var (user, hive) in users)
        {
            if (hive is not null)
                ReadUninstallKey(hive, "HKCU", UserUninstallPath, user.Sid, apps);
        }

        // The same program often registers in both views or for several users.
        return apps
            .GroupBy(a => (Normalize(a.DisplayName), a.Version ?? "", a.Root == "HKCU" ? a.UserSid : null))
            .Select(g => g.OrderByDescending(a => a.InstallLocation is not null).First())
            .ToList();
    }

    private static void ReadUninstallKey(RegistryKey baseKey, string root, string path, string? sid, List<AppRecord> apps)
    {
        using var uninstall = RegistrySnapshot.OpenRead(baseKey, path);
        if (uninstall is null)
            return;
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = RegistrySnapshot.OpenRead(uninstall, name);
            if (key is null || key.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
                continue;
            if (key.GetValue("SystemComponent") is 1 || key.GetValue("ParentKeyName") is string || key.GetValue("ReleaseType") is string)
                continue;
            if (displayName.StartsWith("KB", StringComparison.Ordinal) && displayName.Length < 12)
                continue;

            var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"').TrimEnd('\\');
            if (string.IsNullOrEmpty(location) || !Directory.Exists(location))
                location = InferLocation(key.GetValue("DisplayIcon") as string) ?? InferLocation(key.GetValue("UninstallString") as string);

            apps.Add(new AppRecord
            {
                Key = KeyFor(root, sid, name),
                DisplayName = displayName.Trim(),
                Version = (key.GetValue("DisplayVersion") as string)?.Trim(),
                Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                InstallLocation = location,
                Root = root,
                UninstallKeyPath = $@"{path}\{name}",
                UserSid = sid,
                DisplayIcon = key.GetValue("DisplayIcon") as string,
                EstimatedSizeBytes = key.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
                IsSystemComponent = SystemComponentPattern().IsMatch(displayName),
            });
        }
    }

    private static string KeyFor(string root, string? sid, string name)
    {
        var raw = $"{root}|{sid}|{name}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw.ToUpperInvariant())))[..12]
            .ToLowerInvariant();
    }

    /// <summary>Program folder from a "C:\Program Files\App\app.exe,0" style value, if it is a real app folder.</summary>
    private static string? InferLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        string path;
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            path = end > 1 ? v[1..end] : v.Trim('"');
        }
        else
        {
            var exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? v[..(exe + 4)] : v.Split(',')[0];
        }
        try
        {
            var dir = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(path));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;
            // Uninstallers living in shared places say nothing about where the app is.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (KnownFolders.IsUnder(dir, windows) || KnownFolders.IsUnder(dir, Path.Combine(common, "Package Cache")) ||
                dir.Length <= 3 || string.Equals(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase))
                return null;
            return dir.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Attaches winget ids by matching names against "winget list" (exact, then truncated-prefix).</summary>
    public static void MatchWinget(List<AppRecord> apps, List<WingetPackage> listed, Dictionary<string, string> exportIds)
    {
        var available = listed.Where(p => p.Source.Length > 0 && !p.Id.StartsWith("ARP\\", StringComparison.Ordinal) &&
                                          !p.Id.StartsWith("MSIX\\", StringComparison.Ordinal)).ToList();
        foreach (var app in apps)
        {
            var match = available.FirstOrDefault(p => string.Equals(p.Name, app.DisplayName, StringComparison.OrdinalIgnoreCase))
                        ?? available.FirstOrDefault(p => p.Name.EndsWith('…') &&
                                                         app.DisplayName.StartsWith(p.Name[..^1], StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;
            var id = ResolveId(match.Id, exportIds);
            if (id is null)
                continue;
            app.WingetId = id;
            app.WingetSource = match.Source;
        }
        MatchKnown(apps);
    }

    /// <summary>Fills in well-known ids for programs winget didn't match itself (or when winget isn't on the old PC).</summary>
    public static void MatchKnown(List<AppRecord> apps)
    {
        foreach (var app in apps.Where(a => a.WingetId is null))
        {
            if (KnownWingetIds.Lookup(app.DisplayName) is { } known)
            {
                app.WingetId = known.Id;
                app.WingetSource = known.Source;
            }
        }
    }

    /// <summary>Store apps winget can reinstall that have no Uninstall registry entry.</summary>
    public static IEnumerable<AppRecord> StoreApps(List<WingetPackage> listed, List<AppRecord> known, Dictionary<string, string> exportIds)
    {
        foreach (var p in listed.Where(p => string.Equals(p.Source, "msstore", StringComparison.OrdinalIgnoreCase)))
        {
            if (known.Any(a => string.Equals(a.WingetId, p.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            var id = ResolveId(p.Id, exportIds);
            if (id is null)
                continue;
            yield return new AppRecord
            {
                Key = KeyFor("STORE", null, id),
                DisplayName = p.Name.TrimEnd('…'),
                Version = p.Version,
                WingetId = id,
                WingetSource = "msstore",
                IsStoreApp = true,
                Root = "HKCU",
            };
        }
    }

    private static string? ResolveId(string listedId, Dictionary<string, string> exportIds)
    {
        if (!listedId.EndsWith('…'))
            return listedId;
        var prefix = listedId[..^1];
        var candidates = exportIds.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public static string Normalize(string? s) =>
        s is null ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>"Notepad++ (64-bit x64)" → "notepad"; "7-Zip 23.01 (x64)" → "7zip".</summary>
    public static string CoreName(string displayName) =>
        Normalize(TrailingVersionPattern().Replace(ArchitecturePattern().Replace(displayName, ""), ""));

    private static readonly HashSet<string> SharedVendorKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "WOW6432Node", "Wow6432Node", "Clients", "RegisteredApplications", "Intel", "ODBC",
        "Windows", "Partner", "DefaultUserEnvironment", "Khronos", "Realtek", "NVIDIA Corporation", "AMD", "ATI Technologies",
        "Dolby", "Waves Audio", "Synaptics", "CBSTEST", "Setup", "Description", "Hardware", "OEM", "Wbem",
    };

    private static readonly string[] PublisherSuffixes =
        ["incorporated", "inc", "llc", "ltd", "limited", "corporation", "corp", "gmbh", "software", "technologies", "co", "company", "ag", "sa", "bv", "srl", "plc"];

    /// <summary>Registry keys under SOFTWARE that most likely belong to this app (vendor\product or product).</summary>
    public static List<string> FindVendorKeys(RegistryKey software, AppRecord app)
    {
        var found = new List<string>();
        var core = CoreName(app.DisplayName);
        var full = Normalize(app.DisplayName);
        var publisher = Normalize(app.Publisher);
        foreach (var suffix in PublisherSuffixes)
        {
            if (publisher.EndsWith(suffix, StringComparison.Ordinal) && publisher.Length > suffix.Length + 2)
                publisher = publisher[..^suffix.Length];
        }
        if (core.Length < 3)
            return found;

        string[] names;
        try
        {
            names = software.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return found;
        }
        foreach (var name in names)
        {
            if (SharedVendorKeys.Contains(name))
                continue;
            var n = Normalize(name);
            if (n.Length < 3)
                continue;
            if (n == core || n == full)
            {
                found.Add(name);
                continue;
            }
            var isVendor = publisher.Length >= 3 && (n == publisher || (n.Length >= 4 && publisher.StartsWith(n, StringComparison.Ordinal)));
            if (!isVendor)
                continue;
            using var vendor = RegistrySnapshot.OpenRead(software, name);
            if (vendor is null)
                continue;
            var products = vendor.GetSubKeyNames()
                .Where(p => Normalize(p) is var np && np.Length >= 3 && (core.StartsWith(np, StringComparison.Ordinal) || np.StartsWith(core, StringComparison.Ordinal)))
                .ToList();
            if (products.Count > 0)
                found.AddRange(products.Select(p => $@"{name}\{p}"));
            else if (vendor.SubKeyCount <= 3)
                found.Add(name);
        }
        return found;
    }
}
