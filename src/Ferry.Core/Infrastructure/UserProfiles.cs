using System.Runtime.InteropServices;
using System.Security.Principal;
using Ferry.Core.Model;
using Microsoft.Win32;

namespace Ferry.Core.Infrastructure;

public static partial class UserProfiles
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>Profile folder names that belong to Windows rather than to a person.</summary>
    private static readonly HashSet<string> SystemProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "defaultuser0", "defaultuser1", "defaultuser100000", "WDAGUtilityAccount", "Public", "Default", "Default User", "All Users",
    };

    /// <summary>Real user profiles on this PC: local, domain and Entra ID accounts with a profile folder.</summary>
    public static List<UserProfileInfo> Enumerate()
    {
        var interactive = GetInteractiveAccountName();
        var result = new List<UserProfileInfo>();
        using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (list is null)
            return result;

        foreach (var sid in list.GetSubKeyNames())
        {
            // S-1-5-21: local and AD accounts. S-1-12-1: Microsoft Entra ID accounts. Skip .bak duplicates and service SIDs.
            if (!(sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) ||
                sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;

            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is not string rawPath)
                continue;
            var path = Environment.ExpandEnvironmentVariables(rawPath);
            var folderName = Path.GetFileName(path.TrimEnd('\\'));
            if (!Directory.Exists(path) || SystemProfileNames.Contains(folderName))
                continue;

            var account = TryTranslate(sid) ?? folderName;
            var userName = account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;
            var profile = new UserProfileInfo
            {
                Sid = sid,
                AccountName = account,
                UserName = userName,
                ProfilePath = path,
                Kind = sid.StartsWith("S-1-12-1-", StringComparison.Ordinal) ? AccountKind.EntraId
                    : IsLocalAccount(account) ? AccountKind.Local : AccountKind.Domain,
                IsLoaded = IsHiveLoaded(sid),
                LastUsedUtc = ReadLastUse(key),
            };
            profile.IsInteractiveUser = interactive is not null &&
                                        string.Equals(interactive, account, StringComparison.OrdinalIgnoreCase);
            result.Add(profile);
        }

        // Fall back to the account running Ferry when nobody is signed in at the console (e.g. RDP).
        if (!result.Any(p => p.IsInteractiveUser))
        {
            var current = result.FirstOrDefault(p => p.Sid == Elevation.CurrentUserSid);
            if (current is not null)
                current.IsInteractiveUser = true;
        }

        return result.OrderByDescending(p => p.IsInteractiveUser).ThenByDescending(p => p.LastUsedUtc).ToList();
    }

    public static bool IsHiveLoaded(string sid)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(sid);
            return key is not null;
        }
        catch (System.Security.SecurityException)
        {
            // Another signed-in user's hive: it exists, we just can't read it without admin rights.
            return true;
        }
    }

    /// <summary>Picks the best target user for a source user: same user name, else the signed-in user.</summary>
    public static UserProfileInfo? SuggestMapping(UserProfileInfo source, IReadOnlyList<UserProfileInfo> targets)
    {
        return targets.FirstOrDefault(t => string.Equals(t.Sid, source.Sid, StringComparison.OrdinalIgnoreCase))
               ?? targets.FirstOrDefault(t => string.Equals(t.AccountName, source.AccountName, StringComparison.OrdinalIgnoreCase))
               ?? targets.FirstOrDefault(t => string.Equals(t.UserName, source.UserName, StringComparison.OrdinalIgnoreCase))
               ?? targets.FirstOrDefault(t => NamesLookAlike(t.UserName, source.UserName))
               ?? targets.FirstOrDefault(t => t.IsInteractiveUser);
    }

    /// <summary>"jane.smith" vs "JaneSmith" (AD vs Entra ID naming of the same person).</summary>
    private static bool NamesLookAlike(string a, string b)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var na = Norm(a);
        return na.Length >= 3 && na == Norm(b);
    }

    private static string? TryTranslate(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            // Deleted account, or a domain account while the domain controller is unreachable.
            return null;
        }
    }

    private static bool IsLocalAccount(string account) =>
        account.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase) || !account.Contains('\\');

    private static DateTime? ReadLastUse(RegistryKey key)
    {
        if (key.GetValue("LocalProfileLoadTimeHigh") is int high && key.GetValue("LocalProfileLoadTimeLow") is int low)
        {
            var fileTime = ((long)(uint)high << 32) | (uint)low;
            if (fileTime > 0)
                return DateTime.FromFileTimeUtc(fileTime);
        }
        return null;
    }

    /// <summary>DOMAIN\user of whoever is signed in at the physical console, or null.</summary>
    public static string? GetInteractiveAccountName()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF)
                return null;
            var user = QuerySessionString(session, WtsUserName);
            if (string.IsNullOrEmpty(user))
                return null;
            var domain = QuerySessionString(session, WtsDomainName);
            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
        catch (Exception ex)
        {
            Log.Debug($"Console session lookup failed: {ex.Message}");
            return null;
        }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    private static string? QuerySessionString(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
