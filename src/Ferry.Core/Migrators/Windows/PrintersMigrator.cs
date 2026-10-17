using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Windows;

/// <summary>
/// Shared network printers (\\server\printer, per user), network (TCP/IP) printers installed on the PC, and each
/// user's default printer. USB and WSD printers are found automatically on the new PC, so they are not moved.
/// </summary>
public sealed class PrintersMigrator : MigratorBase
{
    public override string Id => "printers";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 11;

    private const string ConnectionKind = "connection";
    private const string TcpKind = "tcp";
    private const string DefaultKind = "default";

    private static readonly string[] VirtualPrinterHints =
        ["Microsoft Print to PDF", "Microsoft XPS", "OneNote", "Fax", "Adobe PDF", "PDF", "Snagit", "Send To", "Webex", "Foxit"];

    public sealed class TcpPrinter
    {
        public string Name { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string PortName { get; set; } = "";
        public string? PrinterHostAddress { get; set; }
        public int PortNumber { get; set; } = 9100;
        public string? Location { get; set; }
        public string? Comment { get; set; }
    }

    public override async Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        context.Status("Looking for printers");
        foreach (var user in context.Users)
        {
            var hive = context.UserRegistry(user);
            if (hive is null)
                continue;
            foreach (var connection in ReadConnections(hive))
            {
                var item = NewItem($"{user.Sid}:{ConnectionKind}:{connection}", user.AccountName, connection, user.Sid);
                item.Description = "Shared printer on a print server";
                item.Group = "Printers";
                item.Properties["kind"] = ConnectionKind;
                item.Properties["connection"] = connection;
                context.Add(item);
            }
            var defaultPrinter = ReadDefault(hive);
            if (defaultPrinter is not null && !VirtualPrinterHints.Any(h => defaultPrinter.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                var item = NewItem($"{user.Sid}:{DefaultKind}", "Printers", $"Default printer for {user.UserName}: {defaultPrinter}", user.Sid);
                item.Properties["kind"] = DefaultKind;
                item.Properties["printer"] = defaultPrinter;
                context.Add(item);
            }
        }

        foreach (var printer in await ReadTcpPrintersAsync(ct).ConfigureAwait(false))
        {
            var item = NewItem($"{TcpKind}:{printer.Name}", "Printers", printer.Name);
            item.Description = $"Network printer at {printer.PrinterHostAddress} · driver {printer.DriverName}";
            item.Properties["kind"] = TcpKind;
            item.Properties["printer"] = Json.ToText(printer, indented: false);
            item.Warnings.Add("The new PC needs the same printer driver; Windows Update provides most of them.");
            context.Add(item);
        }
    }

    private static IEnumerable<string> ReadConnections(RegistryKey hive)
    {
        using var key = RegistrySnapshot.OpenRead(hive, @"Printers\Connections");
        if (key is null)
            return [];
        // Stored as ",,server,printer".
        return key.GetSubKeyNames()
            .Select(n => n.Split(','))
            .Where(p => p.Length >= 4 && p[2].Length > 0)
            .Select(p => $@"\\{p[2]}\{string.Join(',', p[3..])}")
            .ToList();
    }

    private static string? ReadDefault(RegistryKey hive)
    {
        using var key = RegistrySnapshot.OpenRead(hive, @"Software\Microsoft\Windows NT\CurrentVersion\Windows");
        var device = key?.GetValue("Device") as string;
        return string.IsNullOrEmpty(device) ? null : device.Split(',')[0];
    }

    private static async Task<List<TcpPrinter>> ReadTcpPrintersAsync(CancellationToken ct)
    {
        try
        {
            var printers = await PowerShellRunner.QueryAsync<TcpPrinter>(
                "$ports=@{};Get-PrinterPort | Where-Object PrinterHostAddress | ForEach-Object { $ports[$_.Name]=$_ };" +
                "Get-Printer | Where-Object { $_.Type -eq 'Local' -and $ports.ContainsKey($_.PortName) } | ForEach-Object {" +
                " $p=$ports[$_.PortName]; [pscustomobject]@{Name=$_.Name;DriverName=$_.DriverName;PortName=$_.PortName;" +
                " PrinterHostAddress=$p.PrinterHostAddress;PortNumber=[int]$p.PortNumber;Location=$_.Location;Comment=$_.Comment} }",
                ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            return printers.Where(p => !VirtualPrinterHints.Any(h => p.Name.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not list printers: {ex.Message}");
            return [];
        }
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            await context.Writer.WriteJsonAsync(Entry(item.Id[(Id.Length + 1)..].Replace(':', '_').Replace('\\', '_') + ".json"), item.Id,
                item.Properties, ct: ct).ConfigureAwait(false);
            context.Results.FileDone(item, 0);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var props = await entry.ReadJsonAsync<Dictionary<string, string>>(ct).ConfigureAwait(false);
        switch (props.GetValueOrDefault("kind"))
        {
            case ConnectionKind:
                await RestoreConnectionAsync(context, item, props["connection"], ct).ConfigureAwait(false);
                break;
            case DefaultKind:
                RestoreDefault(context, item, props["printer"]);
                break;
            case TcpKind:
                await RestoreTcpAsync(context, item, Json.FromText<TcpPrinter>(props["printer"]), ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task RestoreConnectionAsync(RestoreContext context, InventoryItem item, string connection, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var printUi = $"{ProcessRunner.System32("rundll32.exe")}";
        string args, undo;
        if (target.Sid == Elevation.CurrentUserSid)
        {
            // The account running Ferry: connect right away.
            args = $"printui.dll,PrintUIEntry /in /q /n\"{connection}\"";
            undo = $"printui.dll,PrintUIEntry /dn /q /n\"{connection}\"";
        }
        else
        {
            // Another user: a per-machine connection appears for everyone at their next sign-in.
            args = $"printui.dll,PrintUIEntry /ga /q /n\"{connection}\"";
            undo = $"printui.dll,PrintUIEntry /gd /q /n\"{connection}\"";
        }
        var result = await context.RunAsync(item, $"Printer {connection}", printUi, args, printUi, undo, ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        if (result.Succeeded)
        {
            context.Results.Info(item, target.Sid == Elevation.CurrentUserSid
                ? "Connected."
                : $"Added; it appears for {target.UserName} at their next sign-in.");
        }
        else
        {
            context.Results.Error(item, $"Could not connect to {connection}. Check that this PC can reach the print server.");
            context.Results.ManualStep($"Add the printer {connection} (Settings > Bluetooth & devices > Printers & scanners).");
        }
    }

    private static void RestoreDefault(RestoreContext context, InventoryItem item, string printer)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var root = context.UserRegistry(item, target);
        if (root is null || context.DryRun)
            return;
        const string path = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";
        // Stop Windows from picking the default itself, then set ours; applies at the next sign-in.
        RegistrySnapshot.SetValue(root, path, "LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord, context.Journal);
        RegistrySnapshot.SetValue(root, path, "Device", $"{printer},winspool,Ne00:", RegistryValueKind.String, context.Journal);
        context.Results.Info(item, $"{printer} becomes the default printer at the next sign-in.");
    }

    private static async Task RestoreTcpAsync(RestoreContext context, InventoryItem item, TcpPrinter printer, CancellationToken ct)
    {
        var q = PowerShellRunner.Quote;
        var exists = await PowerShellRunner.RunAsync($"if(Get-Printer -Name {q(printer.Name)} -ErrorAction SilentlyContinue){{exit 0}}else{{exit 1}}", ct)
            .ConfigureAwait(false);
        if (exists.Succeeded)
        {
            context.Results.Info(item, "Already installed on this PC.");
            return;
        }

        var script =
            $"if(-not (Get-PrinterPort -Name {q(printer.PortName)} -ErrorAction SilentlyContinue)){{" +
            $"Add-PrinterPort -Name {q(printer.PortName)} -PrinterHostAddress {q(printer.PrinterHostAddress ?? "")} -PortNumber {printer.PortNumber}}};" +
            $"if(-not (Get-PrinterDriver -Name {q(printer.DriverName)} -ErrorAction SilentlyContinue)){{" +
            $"Add-PrinterDriver -Name {q(printer.DriverName)}}};" +
            $"Add-Printer -Name {q(printer.Name)} -DriverName {q(printer.DriverName)} -PortName {q(printer.PortName)}" +
            (printer.Location is { Length: > 0 } l ? $" -Location {q(l)}" : "") +
            (printer.Comment is { Length: > 0 } c ? $" -Comment {q(c)}" : "");
        var undo = $"Remove-Printer -Name {q(printer.Name)} -ErrorAction SilentlyContinue";
        var result = await context.RunPowerShellAsync(item, $"Printer {printer.Name}", script, undo, ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        if (result.Succeeded)
        {
            context.Results.Info(item, "Installed.");
        }
        else
        {
            var driverMissing = result.StdErr.Contains("driver", StringComparison.OrdinalIgnoreCase);
            context.Results.Error(item, driverMissing
                ? $"The driver \"{printer.DriverName}\" isn't available on this PC."
                : $"Could not add it: {result.StdErr.Trim()}");
            context.Results.ManualStep($"Install the printer {printer.Name} at {printer.PrinterHostAddress}" +
                                       (driverMissing ? $" (get the \"{printer.DriverName}\" driver from the maker's website)." : "."));
        }
    }
}
