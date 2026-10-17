using Ferry.Core.Model;
using Microsoft.Win32;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// A user's registry hive (their HKEY_CURRENT_USER). Uses HKU\&lt;SID&gt; when the user is signed in, otherwise
/// loads their NTUSER.DAT under HKU\Ferry_&lt;SID&gt; and unloads it on dispose. Loading needs admin rights.
/// </summary>
public sealed class UserHive : IDisposable
{
    private readonly bool loadedByUs;

    private UserHive(string sid, string mountName, RegistryKey root, bool loadedByUs)
    {
        Sid = sid;
        MountName = mountName;
        Root = root;
        this.loadedByUs = loadedByUs;
    }

    public string Sid { get; }

    /// <summary>The subkey of HKEY_USERS the hive is mounted at.</summary>
    public string MountName { get; }
    public RegistryKey Root { get; }

    public static UserHive Open(string sid, string profilePath, bool writable)
    {
        var existing = TryOpen(sid, writable);
        if (existing is not null)
            return new UserHive(sid, sid, existing, loadedByUs: false);

        var mount = $"Ferry_{sid}";
        // A previous run may have crashed with the hive still mounted; reuse and unload it.
        var stale = TryOpen(mount, writable);
        if (stale is not null)
            return new UserHive(sid, mount, stale, loadedByUs: true);

        if (!Elevation.IsAdministrator)
            throw new InvalidOperationException("Administrator rights are needed to read or change the settings of a user who is not signed in.");

        var hiveFile = Path.Combine(profilePath, "NTUSER.DAT");
        if (!File.Exists(hiveFile))
            throw new InvalidOperationException($"The registry hive {hiveFile} was not found.");

        var result = ProcessRunner.RunAsync(ProcessRunner.System32("reg.exe"), ["load", $@"HKU\{mount}", hiveFile], CancellationToken.None,
            TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not load the registry hive for {profilePath}: {result.CombinedOutput.Trim()}");

        var root = TryOpen(mount, writable) ?? throw new InvalidOperationException($"The registry hive for {profilePath} loaded but could not be opened.");
        Log.Debug($"Loaded hive {hiveFile} at HKU\\{mount}");
        return new UserHive(sid, mount, root, loadedByUs: true);
    }

    public static UserHive Open(UserProfileInfo user, bool writable) => Open(user.Sid, user.ProfilePath, writable);

    private static RegistryKey? TryOpen(string name, bool writable)
    {
        try
        {
            return Registry.Users.OpenSubKey(name, writable);
        }
        catch (System.Security.SecurityException) when (writable)
        {
            try
            {
                return Registry.Users.OpenSubKey(name, writable: false);
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }
        catch (System.Security.SecurityException)
        {
            // Another user's hive without admin rights; Open() reports that properly.
            return null;
        }
    }

    public void Dispose()
    {
        Root.Dispose();
        if (!loadedByUs)
            return;

        // Any handle left open inside the hive blocks the unload; finalize stray RegistryKey objects first.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var result = ProcessRunner.RunAsync(ProcessRunner.System32("reg.exe"), ["unload", $@"HKU\{MountName}"], CancellationToken.None,
                TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            if (result.Succeeded)
            {
                Log.Debug($"Unloaded hive HKU\\{MountName}");
                return;
            }
            Thread.Sleep(500);
        }
        Log.Warn($"Could not unload registry hive HKU\\{MountName}. It will be released at the next restart.");
    }
}

/// <summary>Keeps user hives open for the length of an operation so each is loaded at most once.</summary>
public sealed class HiveCache : IDisposable
{
    private readonly Dictionary<string, UserHive> hives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool writable;
    private readonly object gate = new();

    public HiveCache(bool writable) => this.writable = writable;

    /// <summary>Returns the user's hive, or null (with <paramref name="error"/> set) when it can't be opened.</summary>
    public UserHive? Get(UserProfileInfo user, out string? error)
    {
        lock (gate)
        {
            error = null;
            if (hives.TryGetValue(user.Sid, out var hive))
                return hive;
            if (failures.TryGetValue(user.Sid, out error))
                return null;
            try
            {
                hive = UserHive.Open(user, writable);
                hives[user.Sid] = hive;
                return hive;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                failures[user.Sid] = error;
                Log.Warn($"Registry for {user.AccountName}: {error}");
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var hive in hives.Values)
                hive.Dispose();
            hives.Clear();
        }
    }
}
