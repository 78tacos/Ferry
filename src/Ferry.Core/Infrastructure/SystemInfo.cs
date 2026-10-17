using System.Runtime.InteropServices;
using Ferry.Core.Model;
using Microsoft.Win32;

namespace Ferry.Core.Infrastructure;

public static partial class SystemInfo
{
    public static string AppVersion => typeof(SystemInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static MachineInfo Collect()
    {
        var info = new MachineInfo
        {
            ComputerName = Environment.MachineName,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            AppVersion = AppVersion,
            MemoryBytes = (long)GetTotalMemory(),
        };

        using (var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            var product = cv?.GetValue("ProductName") as string ?? "Windows";
            var build = int.TryParse(cv?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
            // ProductName still says "Windows 10" on Windows 11.
            if (build >= 22000)
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            info.OsName = product;
            info.OsDisplayVersion = cv?.GetValue("DisplayVersion") as string ?? cv?.GetValue("ReleaseId") as string ?? "";
            var ubr = cv?.GetValue("UBR") is int u ? u : 0;
            info.OsVersion = $"10.0.{build}.{ubr}";
        }

        using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
        {
            info.Manufacturer = (bios?.GetValue("SystemManufacturer") as string ?? "").Trim();
            info.Model = (bios?.GetValue("SystemProductName") as string ?? "").Trim();
        }

        var (domain, adJoined) = GetDomainJoin();
        var entra = IsEntraJoined(out var tenant);
        info.DomainName = adJoined ? domain : entra ? tenant : null;
        info.JoinType = (adJoined, entra) switch
        {
            (true, true) => "Hybrid (Active Directory + Microsoft Entra ID)",
            (true, false) => "Active Directory",
            (false, true) => "Microsoft Entra ID",
            _ => "Workgroup",
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                info.Drives.Add(new DriveSummary
                {
                    Name = drive.Name,
                    Label = drive.VolumeLabel,
                    Format = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsRemovable = drive.DriveType == DriveType.Removable,
                });
            }
            catch (IOException)
            {
                // Card readers and the like report not ready mid-enumeration.
            }
        }
        return info;
    }

    public static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    private static (string? Domain, bool Joined) GetDomainJoin()
    {
        try
        {
            if (NetGetJoinInformation(null, out var buffer, out var status) == 0)
            {
                try
                {
                    var name = Marshal.PtrToStringUni(buffer);
                    return (name, status == NetJoinStatus.Domain);
                }
                finally
                {
                    NetApiBufferFree(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"NetGetJoinInformation failed: {ex.Message}");
        }
        return (null, false);
    }

    private static bool IsEntraJoined(out string? tenant)
    {
        tenant = null;
        using var joinInfo = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
        if (joinInfo is null || joinInfo.SubKeyCount == 0)
            return false;
        using var tenantInfo = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\TenantInfo");
        var tenantId = tenantInfo?.GetSubKeyNames().FirstOrDefault();
        if (tenantId is not null)
        {
            using var t = tenantInfo!.OpenSubKey(tenantId);
            tenant = t?.GetValue("DisplayName") as string ?? tenantId;
        }
        return true;
    }

    private static ulong GetTotalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    }

    private enum NetJoinStatus { Unknown, Unjoined, Workgroup, Domain }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out NetJoinStatus status);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
