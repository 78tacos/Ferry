using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Windows;

/// <summary>
/// Which app opens web links, PDFs, email and media on the old PC. Windows protects these choices from being set
/// by other programs, so Ferry records them and lists them in the report for the user to set in Settings.
/// </summary>
public sealed class DefaultAppsMigrator : MigratorBase
{
    public override string Id => "defaultapps";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 15;

    private static readonly (string Kind, string Name, string Label)[] Associations =
    [
        ("url", "http", "Web browser"),
        ("url", "mailto", "Email"),
        ("ext", ".pdf", "PDF files"),
        ("ext", ".htm", "Web pages (.htm)"),
        ("ext", ".txt", "Text files"),
        ("ext", ".jpg", "Photos (.jpg)"),
        ("ext", ".png", "Pictures (.png)"),
        ("ext", ".mp4", "Videos (.mp4)"),
        ("ext", ".mp3", "Music (.mp3)"),
        ("ext", ".docx", "Word documents"),
        ("ext", ".xlsx", "Excel workbooks"),
        ("ext", ".zip", "Zip files"),
        ("url", "callto", "Calls"),
    ];

    public sealed record DefaultChoice(string Label, string Name, string ProgId, string AppName);

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        foreach (var user in context.Users)
        {
            var hive = context.UserRegistry(user);
            if (hive is null)
                continue;
            var choices = Read(hive);
            if (choices.Count == 0)
                continue;
            var item = NewItem(user.Sid, user.AccountName, "Default apps", user.Sid);
            item.Description = string.Join(", ", choices.Take(3).Select(c => $"{c.Label}: {c.AppName}")) + (choices.Count > 3 ? ", …" : "");
            item.Properties["choices"] = Json.ToText(choices, indented: false);
            item.Warnings.Add("Windows doesn't let other programs set default apps, so the report lists them for you to set in Settings > Apps > Default apps.");
            context.Add(item);
        }
        return Task.CompletedTask;
    }

    private static List<DefaultChoice> Read(RegistryKey hive)
    {
        var result = new List<DefaultChoice>();
        foreach (var (kind, name, label) in Associations)
        {
            var path = kind == "url"
                ? $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{name}\UserChoice"
                : $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{name}\UserChoice";
            using var key = RegistrySnapshot.OpenRead(hive, path);
            if (key?.GetValue("ProgId") is not string progId || progId.Length == 0)
                continue;
            result.Add(new DefaultChoice(label, name, progId, FriendlyName(hive, progId)));
        }
        return result;
    }

    /// <summary>"ChromeHTML" → "Google Chrome", via the ProgID's application name when it has one.</summary>
    private static string FriendlyName(RegistryKey hive, string progId)
    {
        foreach (var (root, path) in new[] { (hive, $@"Software\Classes\{progId}"), (Registry.LocalMachine, $@"SOFTWARE\Classes\{progId}") })
        {
            using var app = RegistrySnapshot.OpenRead(root, $@"{path}\Application");
            if (app?.GetValue("ApplicationName") is string name && !name.StartsWith('@'))
                return name;
            using var key = RegistrySnapshot.OpenRead(root, path);
            if (key?.GetValue("") is string description && description.Length > 0 && !description.StartsWith('@'))
                return description;
        }
        return progId switch
        {
            var p when p.StartsWith("ChromeHTML", StringComparison.OrdinalIgnoreCase) => "Google Chrome",
            var p when p.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) => "Microsoft Edge",
            var p when p.StartsWith("FirefoxURL", StringComparison.OrdinalIgnoreCase) || p.StartsWith("FirefoxHTML", StringComparison.OrdinalIgnoreCase) => "Firefox",
            var p when p.StartsWith("Acrobat", StringComparison.OrdinalIgnoreCase) => "Adobe Acrobat",
            _ => progId,
        };
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            await context.Writer.WriteDataAsync(Entry(item.UserSid! + ".json"), item.Id, System.Text.Encoding.UTF8.GetBytes(item.Prop("choices") ?? "[]"),
                ct: ct).ConfigureAwait(false);
            context.Results.FileDone(item, 0);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var choices = await entry.ReadJsonAsync<List<DefaultChoice>>(ct).ConfigureAwait(false);
        var user = context.TargetUser(item)?.UserName;
        foreach (var choice in choices)
            context.Results.ManualStep($"Default app for {choice.Label}{(user is null ? "" : $" ({user})")}: {choice.AppName} — set it in Settings > Apps > Default apps.");
        context.Results.Info(item, "Listed in the report's to-do list.");
        context.Results.FileDone(item, 0);
    }
}
