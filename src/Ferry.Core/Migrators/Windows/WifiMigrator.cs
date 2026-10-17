using System.Text;
using System.Xml.Linq;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators.Windows;

/// <summary>Saved Wi-Fi networks, with their passwords when running as administrator.</summary>
public sealed class WifiMigrator : MigratorBase
{
    public override string Id => "wifi";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 10;

    private static string Netsh => ProcessRunner.System32("netsh.exe");

    private sealed record WifiProfile(string Name, string Xml, string Authentication, bool HasKey);

    public override async Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        context.Status("Looking for saved Wi-Fi networks");
        var profiles = await ExportAsync(ct).ConfigureAwait(false);
        foreach (var profile in profiles)
        {
            var item = NewItem(profile.Name, "Wi-Fi networks", profile.Name);
            item.Description = profile.Authentication;
            item.SizeBytes = profile.Xml.Length;
            if (!profile.HasKey && profile.Authentication.Contains("Personal", StringComparison.OrdinalIgnoreCase))
                item.Warnings.Add("The password can only be included when Ferry runs as administrator; you'll be asked for it on the new PC.");
            context.Add(item);
        }
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var profiles = (await ExportAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!profiles.TryGetValue(item.Name, out var profile))
            {
                context.Results.Warn(item, "This network is no longer saved on this PC.");
                continue;
            }
            context.Status(item, null);
            await context.Writer.WriteDataAsync(Entry(Safe(profile.Name) + ".xml"), item.Id, Encoding.UTF8.GetBytes(profile.Xml),
                new() { ["name"] = profile.Name }, ct).ConfigureAwait(false);
            context.Results.FileDone(item, profile.Xml.Length);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var name = entry.Header.GetMeta("name") ?? item.Name;
        var xml = await entry.ReadAllBytesAsync(ct).ConfigureAwait(false);

        var exists = await ProcessRunner.RunRawAsync(Netsh, $"wlan show profile name=\"{name}\"", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (exists.Succeeded)
        {
            context.Results.Info(item, "Already saved on this PC.");
            return;
        }
        if (context.SimulateCommands)
        {
            context.Results.Info(item, "Would be added.");
            return;
        }

        var dir = AppPaths.NewTempDirectory("wifi");
        try
        {
            var file = Path.Combine(dir, "profile.xml");
            await File.WriteAllBytesAsync(file, xml, ct).ConfigureAwait(false);
            var result = await context.RunAsync(item, $"Wi-Fi network {name}", Netsh, $"wlan add profile filename=\"{file}\" user=all",
                Netsh, $"wlan delete profile name=\"{name}\"", ct).ConfigureAwait(false);
            if (result.Succeeded)
                context.Results.FileDone(item, xml.Length);
            else if (result.CombinedOutput.Contains("wlansvc", StringComparison.OrdinalIgnoreCase))
                context.Results.MarkSkipped(item, "This PC has no Wi-Fi (the WLAN AutoConfig service isn't running).");
            else
                context.Results.Error(item, $"netsh couldn't add it: {result.CombinedOutput.Trim()}");
        }
        finally
        {
            AppPaths.TryDeleteDirectory(dir);
        }
    }

    /// <summary>Exports every profile to XML via netsh (key=clear needs admin) and reads them back.</summary>
    private static async Task<List<WifiProfile>> ExportAsync(CancellationToken ct)
    {
        var dir = AppPaths.NewTempDirectory("wifi");
        try
        {
            var key = Elevation.IsAdministrator ? " key=clear" : "";
            var result = await ProcessRunner.RunRawAsync(Netsh, $"wlan export profile folder=\"{dir}\"{key}", ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                Log.Debug($"No Wi-Fi profiles exported: {result.CombinedOutput.Trim()}");
                return [];
            }
            var profiles = new List<WifiProfile>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
            {
                try
                {
                    var xml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var doc = XDocument.Parse(xml);
                    XNamespace ns = doc.Root!.Name.Namespace;
                    var name = doc.Root.Element(ns + "name")?.Value ?? Path.GetFileNameWithoutExtension(file);
                    var auth = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "authentication")?.Value ?? "open";
                    var hasKey = doc.Descendants().Any(e => e.Name.LocalName == "keyMaterial");
                    profiles.Add(new WifiProfile(name, xml, Describe(auth), hasKey));
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
                {
                    Log.Warn($"Skipping unreadable Wi-Fi profile {file}: {ex.Message}");
                }
            }
            return profiles;
        }
        finally
        {
            AppPaths.TryDeleteDirectory(dir);
        }
    }

    private static string Describe(string auth) => auth switch
    {
        "WPA2PSK" => "WPA2-Personal",
        "WPA3SAE" => "WPA3-Personal",
        "WPAPSK" => "WPA-Personal",
        "WPA2" => "WPA2-Enterprise",
        "WPA3ENT" or "WPA3ENT192" => "WPA3-Enterprise",
        "WPA" => "WPA-Enterprise",
        "open" => "Open network",
        _ => auth,
    };

    private static string Safe(string name) =>
        new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c).ToArray());
}
