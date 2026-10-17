using Microsoft.Win32;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// Where a registry change was made, in a form undo can reopen later even if the user's hive is
/// mounted somewhere else by then. Root is "HKLM", "HKCU" (the account running Ferry) or "USER" (by SID).
/// </summary>
public sealed record RegLocation(string Root, string Path, string? Sid = null, string? ProfilePath = null)
{
    public override string ToString() => Root == "USER" ? $"HKU\\{Sid}\\{Path}" : $"{Root}\\{Path}";
}

/// <summary>A base key that registry changes are applied under, plus how to describe paths under it.</summary>
public sealed class RegistryRoot
{
    private readonly string root;
    private readonly string prefix;
    private readonly string? sid;
    private readonly string? profilePath;

    private RegistryRoot(RegistryKey key, string root, string prefix, string? sid, string? profilePath)
    {
        Key = key;
        this.root = root;
        this.prefix = prefix;
        this.sid = sid;
        this.profilePath = profilePath;
    }

    public RegistryKey Key { get; }

    /// <summary>"HKLM" or "HKCU", for paths captured from the old PC.</summary>
    public string LogicalRoot { get; private init; } = "";

    public static RegistryRoot Machine() => new(Registry.LocalMachine, "HKLM", "", null, null) { LogicalRoot = "HKLM" };

    public static RegistryRoot User(UserHive hive, string profilePath) =>
        new(hive.Root, "USER", "", hive.Sid, profilePath) { LogicalRoot = "HKCU" };

    /// <summary>A stand-in root under HKCU\Software\FerrySandbox for test restores that must not touch the real registry.</summary>
    public static RegistryRoot Sandbox(string name, string logicalRoot)
    {
        var path = $@"Software\FerrySandbox\{name}";
        return new RegistryRoot(Registry.CurrentUser.CreateSubKey(path, writable: true), "HKCU", path, null, null) { LogicalRoot = logicalRoot };
    }

    public RegLocation Locate(string relativePath) =>
        new(root, prefix.Length == 0 ? relativePath : relativePath.Length == 0 ? prefix : $@"{prefix}\{relativePath}", sid, profilePath);

    /// <summary>Opens the base key a <see cref="RegLocation"/> refers to. The returned hive (if any) must be disposed.</summary>
    public static RegistryKey OpenBase(RegLocation location, out IDisposable? hive)
    {
        hive = null;
        switch (location.Root)
        {
            case "HKLM":
                return Registry.LocalMachine;
            case "HKCU":
                return Registry.CurrentUser;
            case "USER":
                var userHive = UserHive.Open(location.Sid!, location.ProfilePath ?? "", writable: true);
                hive = userHive;
                return userHive.Root;
            default:
                throw new InvalidDataException($"Unknown registry root {location.Root}.");
        }
    }
}
