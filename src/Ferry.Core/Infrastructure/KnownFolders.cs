using Microsoft.Win32;

namespace Ferry.Core.Infrastructure;

/// <summary>A per-user shell folder (Documents, Desktop…), as named in the User Shell Folders registry key.</summary>
public sealed record KnownFolder(string Key, string DisplayName, string RegistryValue, string DefaultRelativePath, bool IsUserContent);

public static class KnownFolders
{
    public static readonly KnownFolder Desktop = new("Desktop", "Desktop", "Desktop", "Desktop", true);
    public static readonly KnownFolder Documents = new("Documents", "Documents", "Personal", "Documents", true);
    public static readonly KnownFolder Downloads = new("Downloads", "Downloads", "{374DE290-123F-4565-9164-39C4925E467B}", "Downloads", true);
    public static readonly KnownFolder Pictures = new("Pictures", "Pictures", "My Pictures", "Pictures", true);
    public static readonly KnownFolder Music = new("Music", "Music", "My Music", "Music", true);
    public static readonly KnownFolder Videos = new("Videos", "Videos", "My Video", "Videos", true);
    public static readonly KnownFolder Favorites = new("Favorites", "Favorites", "Favorites", "Favorites", true);
    public static readonly KnownFolder Links = new("Links", "Links", "{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}", "Links", true);
    public static readonly KnownFolder Contacts = new("Contacts", "Contacts", "{56784854-C6CB-462B-8169-88E350ACB882}", "Contacts", true);
    public static readonly KnownFolder SavedGames = new("SavedGames", "Saved Games", "{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}", "Saved Games", true);
    public static readonly KnownFolder Searches = new("Searches", "Searches", "{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}", "Searches", true);
    public static readonly KnownFolder RoamingAppData = new("RoamingAppData", "AppData (Roaming)", "AppData", @"AppData\Roaming", false);
    public static readonly KnownFolder LocalAppData = new("LocalAppData", "AppData (Local)", "Local AppData", @"AppData\Local", false);
    public static readonly KnownFolder StartMenu = new("StartMenu", "Start Menu", "Start Menu", @"AppData\Roaming\Microsoft\Windows\Start Menu", false);

    public static readonly IReadOnlyList<KnownFolder> UserContent =
        [Desktop, Documents, Downloads, Pictures, Music, Videos, Favorites, Links, Contacts, SavedGames, Searches];

    public static readonly IReadOnlyList<KnownFolder> All = [.. UserContent, RoamingAppData, LocalAppData, StartMenu];

    public static KnownFolder? ByKey(string key) => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    private const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>
    /// Resolves a user's folder from their own hive, expanding %USERPROFILE% to *their* profile (not ours).
    /// Falls back to the default location under the profile when the hive is unavailable.
    /// </summary>
    public static string Resolve(KnownFolder folder, string profilePath, RegistryKey? userHive)
    {
        if (userHive is not null)
        {
            using var key = RegistrySnapshot.OpenRead(userHive, ShellFoldersKey);
            if (key?.GetValue(folder.RegistryValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw &&
                !string.IsNullOrWhiteSpace(raw))
            {
                return ExpandForUser(raw, profilePath);
            }
        }
        return Path.Combine(profilePath, folder.DefaultRelativePath);
    }

    public static string ExpandForUser(string raw, string profilePath)
    {
        var expanded = raw.Replace("%USERPROFILE%", profilePath, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", Path.Combine(profilePath, @"AppData\Roaming"), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Path.Combine(profilePath, @"AppData\Local"), StringComparison.OrdinalIgnoreCase)
            .Replace("%HOMEPATH%", profilePath.Length > 2 ? profilePath[2..] : profilePath, StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(expanded).TrimEnd('\\');
    }

    /// <summary>Root folders of every OneDrive account the user has set up (personal and work/school).</summary>
    public static List<string> OneDriveRoots(RegistryKey? userHive, string profilePath)
    {
        var roots = new List<string>();
        if (userHive is not null)
        {
            using var accounts = RegistrySnapshot.OpenRead(userHive, @"Software\Microsoft\OneDrive\Accounts");
            if (accounts is not null)
            {
                foreach (var name in accounts.GetSubKeyNames())
                {
                    using var account = accounts.OpenSubKey(name);
                    if (account?.GetValue("UserFolder") is string folder && !string.IsNullOrWhiteSpace(folder))
                        roots.Add(folder.TrimEnd('\\'));
                }
            }
        }
        if (roots.Count == 0)
        {
            // No hive access: fall back to the conventional folder names.
            try
            {
                roots.AddRange(Directory.EnumerateDirectories(profilePath, "OneDrive*"));
            }
            catch
            {
                // Profile not readable.
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
