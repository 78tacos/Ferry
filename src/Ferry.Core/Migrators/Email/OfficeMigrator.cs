using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators.Email;

/// <summary>
/// Outlook signatures and data files, Office templates, dictionaries, startup add-ins and ribbon customizations,
/// and Sticky Notes. Each is a set of files under the user's AppData plus, for signatures, a registry key.
/// </summary>
public sealed class OfficeMigrator : MigratorBase
{
    public override string Id => "office";
    public override MigrationCategory Category => MigrationCategory.BrowsersAndEmail;
    public override int Order => 45;

    private sealed record FileSet(KnownFolder Base, string RelativeDir, string Pattern = "*", bool Recursive = true);

    private sealed record OfficeGroup(string Key, string Name, string Description, FileSet[] Files, string[] RegistryKeys, string? Process = null,
        string? Warning = null, ConflictPolicy Policy = ConflictPolicy.Overwrite);

    private static readonly OfficeGroup[] Groups =
    [
        new("signatures", "Outlook signatures", "Your email signatures, and which ones Outlook uses for new messages and replies.",
            [new(KnownFolders.RoamingAppData, @"Microsoft\Signatures")],
            [@"Software\Microsoft\Office\16.0\Common\MailSettings"]),
        new("pst", "Outlook data files (.pst)", "Archives and personal folders kept in Outlook's own folder.",
            [
                new(KnownFolders.LocalAppData, @"Microsoft\Outlook", "*.pst", Recursive: false),
                new(KnownFolders.RoamingAppData, @"Microsoft\Outlook", "*.pst", Recursive: false),
            ], [], "outlook",
            "Outlook doesn't open moved data files by itself: the report says where they went so you can add them (File > Open & Export > Open Outlook Data File).",
            ConflictPolicy.KeepBoth),
        new("templates", "Office templates", "Normal.dotm (Word's default formatting and macros), email templates and your own templates.",
            [
                new(KnownFolders.RoamingAppData, @"Microsoft\Templates"),
                new(KnownFolders.RoamingAppData, @"Microsoft\Document Building Blocks"),
            ], [], "winword"),
        new("proofing", "Custom dictionaries and AutoCorrect", "Words you've added to the spelling dictionary and AutoCorrect entries.",
            [
                new(KnownFolders.RoamingAppData, @"Microsoft\UProof"),
                new(KnownFolders.RoamingAppData, @"Microsoft\Office", "*.acl", Recursive: false),
            ], []),
        new("startup", "Excel and Word startup files", "Personal macro workbook (PERSONAL.XLSB), startup templates and add-ins.",
            [
                new(KnownFolders.RoamingAppData, @"Microsoft\Excel\XLSTART"),
                new(KnownFolders.RoamingAppData, @"Microsoft\Word\STARTUP"),
                new(KnownFolders.RoamingAppData, @"Microsoft\AddIns"),
            ], [], "excel"),
        new("ribbon", "Office ribbon and Quick Access Toolbar", "Customized ribbons and toolbars in Word, Excel, Outlook and PowerPoint.",
            [new(KnownFolders.LocalAppData, @"Microsoft\Office", "*.officeUI", Recursive: false)], []),
        new("stickynotes", "Sticky Notes", "Notes kept on this PC. Notes synced with a Microsoft account come back by signing in instead.",
            [new(KnownFolders.LocalAppData, @"Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState", "plum.sqlite*", Recursive: false)],
            [], "Microsoft.Notes"),
    ];

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        foreach (var user in context.Users)
        {
            foreach (var group in Groups)
            {
                var files = Files(user.FolderPaths, user.ProfilePath, group).ToList();
                if (files.Count == 0)
                    continue;
                var item = NewItem($"{user.Sid}:{group.Key}", user.AccountName, group.Name, user.Sid);
                item.Description = group.Description;
                item.Properties["group"] = group.Key;
                item.SizeBytes = files.Sum(f => f.Length);
                item.FileCount = files.Count;
                if (group.Warning is not null)
                    item.Warnings.Add(group.Warning);
                if (group.Key == "pst")
                    item.Description += " " + string.Join(", ", files.Select(f => $"{f.Name} ({Format.Bytes(f.Length)})"));
                context.Add(item);
            }

            // Data files in Documents travel with Documents; just point them out.
            var documents = user.FolderPaths.GetValueOrDefault(KnownFolders.Documents.Key);
            var outlookFiles = documents is null ? null : Path.Combine(documents, "Outlook Files");
            if (outlookFiles is not null && Directory.Exists(outlookFiles) && Directory.EnumerateFiles(outlookFiles, "*.pst").Any())
                context.Warn($"{user.UserName} has Outlook data files in {outlookFiles}; they move with Documents.");
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<FileInfo> Files(IReadOnlyDictionary<string, string> folders, string profilePath, OfficeGroup group)
    {
        foreach (var set in group.Files)
        {
            var dir = SetRoot(folders, profilePath, set);
            if (!Directory.Exists(dir))
                continue;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(dir).EnumerateFiles(set.Pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = set.Recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (!file.Name.StartsWith("~$", StringComparison.Ordinal) && !FileWalker.IsCloudOnly(file.Attributes))
                    yield return file;
            }
        }
    }

    private static string SetRoot(IReadOnlyDictionary<string, string> folders, string profilePath, FileSet set) =>
        Path.Combine(folders.GetValueOrDefault(set.Base.Key) ?? Path.Combine(profilePath, set.Base.DefaultRelativePath), set.RelativeDir);

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var group = Groups.First(g => g.Key == item.Prop("group"));
            var user = context.UserOf(item);
            if (user is null)
                continue;
            for (var i = 0; i < group.Files.Length; i++)
            {
                var set = group.Files[i];
                var root = SetRoot(user.FolderPaths, user.ProfilePath, set);
                foreach (var file in Files(user.FolderPaths, user.ProfilePath, group with { Files = [set] }))
                {
                    ct.ThrowIfCancellationRequested();
                    context.Status(item, file.Name);
                    var relative = Path.GetRelativePath(root, file.FullName);
                    await context.CaptureFileAsync(item, file.FullName, Entry(item.UserSid!, group.Key, i.ToString(), relative), FileMeta.From(file), ct)
                        .ConfigureAwait(false);
                }
            }
            var hive = group.RegistryKeys.Length > 0 ? context.UserRegistry(item) : null;
            for (var i = 0; hive is not null && i < group.RegistryKeys.Length; i++)
                await context.CaptureRegistryAsync(item, hive, "HKCU", group.RegistryKeys[i], Entry(item.UserSid!, group.Key, "reg", $"{i}.json"), ct)
                    .ConfigureAwait(false);
        }
    }

    private sealed class RestoreState
    {
        public readonly HashSet<string> Checked = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> RestoredPsts = new(StringComparer.Ordinal);
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var group = Groups.FirstOrDefault(g => g.Key == item.Prop("group"));
        if (group is null)
            return;
        var state = context.State<RestoreState>(Id);
        if (state.Checked.Add(item.Id) && group.Process is not null && !context.SimulateCommands)
        {
            var problem = await RunningApps.EnsureClosedAsync(group.Process, context.Options.CloseRunningApps, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                context.Results.MarkFailed(item, problem);
                return;
            }
        }

        var rest = EntryNames.After(entry.Name, Entry(item.UserSid!, group.Key))!;
        var (part, relative) = EntryNames.SplitFirst(rest);
        if (part == "reg")
        {
            var export = await entry.ReadJsonAsync<RegistryExport>(ct).ConfigureAwait(false);
            var root = context.UserRegistry(item, target);
            if (root is not null)
                RegistrySnapshot.Apply(root, export, context.Journal, context.DryRun, context.RemapperFor(item));
            return;
        }

        var set = group.Files[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)];
        var path = Path.Combine(context.TargetFolder(target, set.Base), set.RelativeDir, relative);
        context.Status(item, path);
        var outcome = await context.Files.RestoreAsync(entry, item, path, ct, context.RemapperFor(item), group.Policy).ConfigureAwait(false);
        if (group.Key == "pst" && outcome is FileOutcome.Written or FileOutcome.KeptBoth or FileOutcome.Replaced or FileOutcome.SkippedIdentical)
        {
            lock (state.RestoredPsts)
            {
                if (!state.RestoredPsts.TryGetValue(item.Id, out var list))
                    state.RestoredPsts[item.Id] = list = [];
                list.Add(path);
            }
        }
    }

    public override Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var state = context.State<RestoreState>(Id);
        foreach (var (_, paths) in state.RestoredPsts)
        {
            foreach (var path in paths)
                context.Results.ManualStep($"In Outlook, open the data file {path} (File > Open & Export > Open Outlook Data File).");
        }
        return Task.CompletedTask;
    }
}
