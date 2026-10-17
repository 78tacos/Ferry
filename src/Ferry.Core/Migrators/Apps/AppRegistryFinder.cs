using Ferry.Core.Infrastructure;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Apps;

public sealed class ServiceRecord
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public int Start { get; set; } = 3;
    public int Type { get; set; } = 0x10;
}

/// <summary>Finds the registry keys and services that belong to a program, for copy-mode migration.</summary>
internal static class AppRegistryFinder
{
    /// <summary>(root, path) pairs to capture: vendor keys, App Paths, file associations and COM classes.</summary>
    public static List<(string Root, string Path)> Find(AppRecord app, RegistryKey? userHive)
    {
        var result = new List<(string, string)>();
        var install = app.InstallLocation!;

        foreach (var softwarePath in new[] { "SOFTWARE", @"SOFTWARE\WOW6432Node" })
        {
            using var software = RegistrySnapshot.OpenRead(Registry.LocalMachine, softwarePath);
            if (software is null)
                continue;
            foreach (var key in AppCatalog.FindVendorKeys(software, app))
                result.Add(("HKLM", $@"{softwarePath}\{key}"));
        }
        if (userHive is not null)
        {
            using var software = RegistrySnapshot.OpenRead(userHive, "Software");
            if (software is not null)
            {
                foreach (var key in AppCatalog.FindVendorKeys(software, app))
                    result.Add(("HKCU", $@"Software\{key}"));
            }
        }

        // App Paths: lets "Run" and the shell find the program's exe by name.
        using (var appPaths = RegistrySnapshot.OpenRead(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
        {
            foreach (var exe in SafeSubKeys(appPaths))
            {
                using var key = RegistrySnapshot.OpenRead(appPaths!, exe);
                if (key?.GetValue("") is string target && PointsInto(target, install))
                    result.Add(("HKLM", $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}"));
            }
        }

        // File associations: ProgIDs whose open command runs the program, and extensions that point at them.
        using (var classes = RegistrySnapshot.OpenRead(Registry.LocalMachine, @"SOFTWARE\Classes"))
        {
            var names = SafeSubKeys(classes);
            var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names.Where(n => !n.StartsWith('.') && n is not ("CLSID" or "Interface" or "TypeLib" or "AppID" or "WOW6432Node")))
            {
                using var command = RegistrySnapshot.OpenRead(classes!, $@"{name}\shell\open\command");
                if (command?.GetValue("") is string cmd && PointsInto(cmd, install))
                {
                    progIds.Add(name);
                    result.Add(("HKLM", $@"SOFTWARE\Classes\{name}"));
                }
            }
            foreach (var ext in names.Where(n => n.StartsWith('.')))
            {
                using var key = RegistrySnapshot.OpenRead(classes!, ext);
                if (key?.GetValue("") is string progId && progIds.Contains(progId))
                    result.Add(("HKLM", $@"SOFTWARE\Classes\{ext}"));
            }
        }

        // COM servers (shell extensions, automation) implemented by files in the program folder.
        foreach (var clsidRoot in new[] { @"SOFTWARE\Classes\CLSID", @"SOFTWARE\Classes\WOW6432Node\CLSID" })
        {
            using var clsids = RegistrySnapshot.OpenRead(Registry.LocalMachine, clsidRoot);
            foreach (var clsid in SafeSubKeys(clsids))
            {
                foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var key = RegistrySnapshot.OpenRead(clsids!, $@"{clsid}\{server}");
                    if (key?.GetValue("") is string path && PointsInto(path, install))
                    {
                        result.Add(("HKLM", $@"{clsidRoot}\{clsid}"));
                        break;
                    }
                }
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>Win32 services whose executable lives in the program folder. Kernel drivers are counted, not returned.</summary>
    public static List<ServiceRecord> FindServices(string installDir, out int drivers)
    {
        drivers = 0;
        var services = new List<ServiceRecord>();
        using var root = RegistrySnapshot.OpenRead(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services");
        foreach (var name in SafeSubKeys(root))
        {
            using var key = RegistrySnapshot.OpenRead(root!, name);
            if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image)
                continue;
            var expanded = Environment.ExpandEnvironmentVariables(image);
            if (!PointsInto(expanded, installDir))
                continue;
            var type = key.GetValue("Type") is int t ? t : 0x10;
            if ((type & 0x3) != 0)
            {
                drivers++;
                continue;
            }
            services.Add(new ServiceRecord
            {
                Name = name,
                DisplayName = key.GetValue("DisplayName") as string ?? name,
                ImagePath = image,
                Start = key.GetValue("Start") is int s ? s : 3,
                Type = (type & 0x20) != 0 ? 0x20 : 0x10,
            });
        }
        return services;
    }

    /// <summary>True when a command line or path refers to a file inside <paramref name="dir"/>.</summary>
    private static bool PointsInto(string commandOrPath, string dir) =>
        commandOrPath.Replace("\"", "").Contains(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string[] SafeSubKeys(RegistryKey? key)
    {
        if (key is null)
            return [];
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}
