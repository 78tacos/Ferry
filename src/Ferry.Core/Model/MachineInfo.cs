namespace Ferry.Core.Model;

public sealed class MachineInfo
{
    public string ComputerName { get; set; } = "";
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsDisplayVersion { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public long MemoryBytes { get; set; }

    /// <summary>"Workgroup", "Active Directory", "Microsoft Entra ID" or "Hybrid (AD + Entra ID)".</summary>
    public string JoinType { get; set; } = "Workgroup";
    public string? DomainName { get; set; }
    public string AppVersion { get; set; } = "";
    public List<DriveSummary> Drives { get; set; } = [];

    public string OsSummary => string.IsNullOrEmpty(OsDisplayVersion) ? OsName : $"{OsName} {OsDisplayVersion}";

    public DriveSummary? FindDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is null ? null : Drives.FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DriveSummary
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Format { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsRemovable { get; set; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AccountKind>))]
public enum AccountKind { Local, Domain, EntraId }

public sealed class UserProfileInfo
{
    public string Sid { get; set; } = "";

    /// <summary>DOMAIN\user, AzureAD\user or PC\user. Falls back to the profile folder name if the SID can't be resolved.</summary>
    public string AccountName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public AccountKind Kind { get; set; }

    /// <summary>True when the hive is currently loaded (the user is signed in or a process is running as them).</summary>
    public bool IsLoaded { get; set; }

    /// <summary>True for the user signed in at the console, which is usually who the migration is for.</summary>
    public bool IsInteractiveUser { get; set; }
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>Known folder key (see KnownFolders) → resolved path on this PC. Filled in by the scan.</summary>
    public Dictionary<string, string> FolderPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OneDrive root folders for this user, so synced folders can be recognised.</summary>
    public List<string> OneDriveRoots { get; set; } = [];

    public override string ToString() => AccountName;
}
