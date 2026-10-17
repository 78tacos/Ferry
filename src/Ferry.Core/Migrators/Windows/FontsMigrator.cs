using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Windows;

/// <summary>
/// Fonts that were added to the PC (for all users) or by a user (for just them). Windows' own fonts are recognised
/// because they all share the timestamp of the Windows install; anything installed at another time is offered.
/// Fonts the new PC already has are left alone.
/// </summary>
public sealed class FontsMigrator : MigratorBase
{
    public override string Id => "fonts";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 14;

    private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const string UserFontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    public sealed record FontFile(string RegistryName, string Path, long Length);

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        var (added, bundled) = FindAddedSystemFonts();
        if (added.Count > 0)
        {
            var item = NewItem("system", "Fonts", $"Fonts added to this PC ({added.Count})");
            item.Description = Describe(added);
            item.SizeBytes = added.Sum(f => f.Length);
            item.FileCount = added.Count;
            item.Properties["fonts"] = Json.ToText(added, indented: false);
            context.Add(item);
        }
        if (bundled.Count > 0)
        {
            var item = NewItem("bundled", "Fonts", $"Fonts installed with programs ({bundled.Count})");
            item.Description = Describe(bundled);
            item.SizeBytes = bundled.Sum(f => f.Length);
            item.FileCount = bundled.Count;
            item.SelectedByDefault = false;
            item.Warnings.Add("These came with a program such as Microsoft Office and come back when it is reinstalled.");
            item.Properties["fonts"] = Json.ToText(bundled, indented: false);
            context.Add(item);
        }

        foreach (var user in context.Users)
        {
            var hive = context.UserRegistry(user);
            var fonts = hive is null ? [] : ReadUserFonts(hive);
            if (fonts.Count == 0)
                continue;
            var item = NewItem(user.Sid, "Fonts", $"Fonts installed by {user.UserName} ({fonts.Count})", user.Sid);
            item.Description = Describe(fonts);
            item.SizeBytes = fonts.Sum(f => f.Length);
            item.FileCount = fonts.Count;
            item.Properties["fonts"] = Json.ToText(fonts, indented: false);
            context.Add(item);
        }
        return Task.CompletedTask;
    }

    private static string Describe(List<FontFile> fonts) =>
        string.Join(", ", fonts.Select(f => f.RegistryName.Replace(" (TrueType)", "").Replace(" (OpenType)", "")).Distinct().Take(6)) +
        (fonts.Count > 6 ? ", …" : "");

    /// <summary>Programs like Office install their fonts in one go; a day with this many new fonts is a bundle, not the user.</summary>
    private const int BundleSize = 25;

    /// <summary>
    /// Fonts Windows didn't ship. Windows' own fonts are hard links into the component store (WinSxS), so a font file
    /// with a single link was added later — by the user (a few at a time) or by a program (a big batch on one day).
    /// </summary>
    private static (List<FontFile> Added, List<FontFile> Bundled) FindAddedSystemFonts()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        using var key = Registry.LocalMachine.OpenSubKey(FontsKey);
        if (key is null)
            return ([], []);
        var fonts = new List<(FontFile Font, DateTime Created)>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string file || file.Length == 0)
                continue;
            var path = Path.IsPathRooted(file) ? file : Path.Combine(fontsDir, file);
            if (!File.Exists(path) || FileLinks.Count(path) != 1)
                continue;
            var info = new FileInfo(path);
            fonts.Add((new FontFile(name, path, info.Length), info.CreationTimeUtc.Date));
        }
        var bundleDays = fonts.GroupBy(f => f.Created).Where(g => g.Count() >= BundleSize).Select(g => g.Key).ToHashSet();
        return (fonts.Where(f => !bundleDays.Contains(f.Created)).Select(f => f.Font).ToList(),
                fonts.Where(f => bundleDays.Contains(f.Created)).Select(f => f.Font).ToList());
    }

    private static List<FontFile> ReadUserFonts(RegistryKey hive)
    {
        using var key = RegistrySnapshot.OpenRead(hive, UserFontsKey);
        if (key is null)
            return [];
        var fonts = new List<FontFile>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string path && File.Exists(path))
                fonts.Add(new FontFile(name, path, new FileInfo(path).Length));
        }
        return fonts;
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var fonts = Json.FromText<List<FontFile>>(item.Prop("fonts") ?? "[]");
            foreach (var font in fonts)
            {
                ct.ThrowIfCancellationRequested();
                context.Status(item, font.RegistryName);
                await context.CaptureFileAsync(item, font.Path, Entry(item.UserSid ?? "system", Path.GetFileName(font.Path)),
                    FileMeta.From(new FileInfo(font.Path)), ct, new() { ["font"] = font.RegistryName }).ConfigureAwait(false);
            }
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var fileName = Path.GetFileName(entry.Name.Replace('/', '\\'));
        var registryName = entry.Header.GetMeta("font") ?? Path.GetFileNameWithoutExtension(fileName);

        if (item.UserSid is null)
        {
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
            using (var fonts = Registry.LocalMachine.OpenSubKey(FontsKey))
            {
                if (File.Exists(target) || fonts?.GetValue(registryName) is not null)
                {
                    await entry.SkipAsync(ct).ConfigureAwait(false);
                    context.Results.FileSkipped(item);
                    return;
                }
            }
            var outcome = await context.Files.RestoreAsync(entry, item, target, ct, policy: ConflictPolicy.Skip).ConfigureAwait(false);
            if (outcome is FileOutcome.Written && !context.DryRun)
                RegistrySnapshot.SetValue(context.MachineRegistry(), FontsKey, registryName, fileName, RegistryValueKind.String, context.Journal);
            return;
        }

        if (!context.TryGetTargetUser(item, out var user))
            return;
        var folder = Path.Combine(context.TargetFolder(user, KnownFolders.LocalAppData), @"Microsoft\Windows\Fonts");
        var path = Path.Combine(folder, fileName);
        var written = await context.Files.RestoreAsync(entry, item, path, ct, policy: ConflictPolicy.Skip).ConfigureAwait(false);
        if (written is FileOutcome.Written && !context.DryRun && context.UserRegistry(item, user) is { } root)
            RegistrySnapshot.SetValue(root, UserFontsKey, registryName, path, RegistryValueKind.String, context.Journal);
    }
}
