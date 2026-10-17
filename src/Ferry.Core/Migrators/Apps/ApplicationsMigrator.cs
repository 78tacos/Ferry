using System.Threading.Channels;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Apps;

/// <summary>
/// Installed programs. By default each is reinstalled fresh on the new PC with winget (reliable, current version).
/// Optionally — and experimentally — its program folder, registry keys, shortcuts and services are copied as-is,
/// which is how PCmover works and which breaks for apps with drivers, licensing or complex installers.
/// </summary>
public sealed class ApplicationsMigrator : MigratorBase
{
    public override string Id => "apps";
    public override MigrationCategory Category => MigrationCategory.Applications;
    public override int Order => 20;

    private const string ListKey = "list";

    public override async Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        context.Status("Listing installed programs");
        var users = context.Users.Select(u => (u, context.UserRegistry(u))).ToList();
        var apps = AppCatalog.ReadInstalled(users);

        List<WingetPackage> listed = [];
        Dictionary<string, string> exportIds = new(StringComparer.OrdinalIgnoreCase);
        if (context.Options.QueryWinget)
        {
            if (Winget.IsAvailable)
            {
                context.Status("Asking winget which programs it can reinstall");
                var listTask = Winget.ListInstalledAsync(ct);
                var exportTask = Winget.ExportIdsAsync(ct);
                await Task.WhenAll(listTask, exportTask).ConfigureAwait(false);
                listed = listTask.Result;
                exportIds = exportTask.Result;
                AppCatalog.MatchWinget(apps, listed, exportIds);
                apps.AddRange(AppCatalog.StoreApps(listed, apps, exportIds));
            }
            else
            {
                context.Warn("winget (App Installer) isn't available on this PC, so only well-known programs could be matched for automatic reinstall.");
            }
        }
        AppCatalog.MatchKnown(apps);

        foreach (var app in apps)
        {
            var group = app.IsStoreApp ? "Microsoft Store apps" : app.IsSystemComponent ? "Drivers and system components" : "Programs";
            var owner = app.Root == "HKCU" ? context.Users.FirstOrDefault(u => u.Sid == app.UserSid) : null;
            var item = NewItem(app.Key, group, app.DisplayName, app.Root == "HKCU" ? app.UserSid : null);
            item.Description = string.Join(" · ", new[] { app.Summary, owner is null ? null : $"installed for {owner.UserName}" }
                .Where(s => !string.IsNullOrEmpty(s)));
            app.ToProperties(item);
            item.Properties["copySize"] = app.EstimatedSizeBytes.ToString();

            var canCopy = !app.IsStoreApp && app.InstallLocation is not null;
            if (app.WingetId is not null)
            {
                item.AvailableModes = canCopy ? [ItemModes.Winget, ItemModes.Copy] : [ItemModes.Winget];
                item.DefaultMode = ItemModes.Winget;
                item.SelectedByDefault = !app.IsSystemComponent;
            }
            else if (canCopy)
            {
                item.AvailableModes = [ItemModes.Copy];
                item.DefaultMode = ItemModes.Copy;
                item.SelectedByDefault = false;
                item.Warnings.Add("Not available from winget. Reinstall it yourself on the new PC, or tick it to copy its program files (experimental).");
            }
            else
            {
                item.SelectedByDefault = false;
                item.Warnings.Add("Can't be moved automatically. Reinstall it yourself on the new PC.");
            }
            if (app.IsSystemComponent)
                item.Warnings.Add("Drivers and system components usually come from Windows Update or the PC maker on the new PC.");
            context.Add(item);
        }

        var list = NewItem(ListKey, "Programs list", "List of installed programs");
        list.Description = $"Puts all {apps.Count} programs from this PC in the migration report, so anything not reinstalled can be found later.";
        context.Add(list);
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Id == $"{Id}:{ListKey}")
            {
                var all = context.Inventory.Items.Where(i => i.MigratorId == Id && i.Prop("app") is not null).Select(AppRecord.FromItem).ToList();
                await context.Writer.WriteJsonAsync(Entry(ListKey + ".json"), item.Id, all, ct: ct).ConfigureAwait(false);
                continue;
            }

            var app = AppRecord.FromItem(item);
            var mode = context.ModeOf(item);
            await context.Writer.WriteJsonAsync(Entry(app.Key, "app.json"), item.Id, app, new() { ["mode"] = mode ?? ItemModes.Skip }, ct)
                .ConfigureAwait(false);
            if (mode == ItemModes.Copy)
                await CaptureProgramAsync(context, item, app, ct).ConfigureAwait(false);
        }
    }

    // ─────────────────────────── Copy mode (experimental) ───────────────────────────

    private async Task CaptureProgramAsync(CaptureContext context, InventoryItem item, AppRecord app, CancellationToken ct)
    {
        if (app.InstallLocation is null || !Directory.Exists(app.InstallLocation))
        {
            context.Results.Error(item, "Its program folder couldn't be found, so it can't be copied. Reinstall it on the new PC.");
            return;
        }

        context.Status(item, "Copying program files", force: true);
        await context.CaptureFolderAsync(item, app.InstallLocation, Entry(app.Key, "files"), new ExclusionRules(includeBuiltIn: true), ct)
            .ConfigureAwait(false);

        // Registry: the uninstall entry, vendor keys, App Paths, file associations and COM classes pointing into the folder.
        var exports = 0;
        RegistryKey? userHive = app.Root == "HKCU" ? context.UserRegistry(item) : null;
        var baseKey = app.Root == "HKCU" ? userHive : Registry.LocalMachine;
        if (baseKey is not null && app.UninstallKeyPath is not null &&
            await context.CaptureRegistryAsync(item, baseKey, app.Root, app.UninstallKeyPath, Entry(app.Key, "reg", $"{exports}.json"), ct).ConfigureAwait(false))
            exports++;

        foreach (var (root, path) in AppRegistryFinder.Find(app, userHive))
        {
            ct.ThrowIfCancellationRequested();
            var source = root == "HKCU" ? userHive : Registry.LocalMachine;
            if (source is null)
                continue;
            if (await context.CaptureRegistryAsync(item, source, root, path, Entry(app.Key, "reg", $"{exports}.json"), ct).ConfigureAwait(false))
                exports++;
        }

        // Start menu and desktop shortcuts into the program folder.
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        foreach (var (root, tag) in new[] { (commonPrograms, "startmenu"), (publicDesktop, "desktop") })
        {
            foreach (var lnk in FindShortcutsInto(root, app.InstallLocation))
            {
                var relative = Path.GetRelativePath(root, lnk);
                await context.CaptureFileAsync(item, lnk, Entry(app.Key, "shortcuts", tag, relative), FileMeta.From(new FileInfo(lnk)), ct)
                    .ConfigureAwait(false);
            }
        }

        var services = AppRegistryFinder.FindServices(app.InstallLocation, out var drivers);
        if (services.Count > 0)
            await context.Writer.WriteJsonAsync(Entry(app.Key, "services.json"), item.Id, services, ct: ct).ConfigureAwait(false);
        if (drivers > 0)
            context.Results.Warn(item, $"It includes {Format.Count(drivers, "driver")}, which can't be copied. Reinstall it if it doesn't work.");
        context.Results.Warn(item, "Copied as-is (experimental). If it doesn't start on the new PC, reinstall it.");
    }

    private static IEnumerable<string> FindShortcutsInto(string root, string installDir)
    {
        if (!Directory.Exists(root))
            yield break;
        IEnumerable<string> links;
        try
        {
            links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var link in links)
        {
            var info = ShellLink.Read(link);
            if (info is not null && KnownFolders.IsUnder(Environment.ExpandEnvironmentVariables(info.Target), installDir))
                yield return link;
        }
    }

    // ─────────────────────────────────── Restore ───────────────────────────────────

    private sealed class RestoreState
    {
        public readonly object Gate = new();
        public List<AppRecord>? AllApps;
        public readonly Dictionary<string, (InventoryItem Item, AppRecord App, string Mode)> Apps = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<ServiceRecord>> Services = new(StringComparer.Ordinal);
        public Channel<(InventoryItem Item, AppRecord App)>? Installs;
        public Task? InstallWorker;
        public readonly HashSet<string> InstalledIds = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> HandledIds = new(StringComparer.OrdinalIgnoreCase);
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var state = context.State<RestoreState>(Id);
        if (entry.Name == Entry(ListKey + ".json"))
        {
            state.AllApps = await entry.ReadJsonAsync<List<AppRecord>>(ct).ConfigureAwait(false);
            return;
        }

        var app = AppRecord.FromItem(item);
        var rest = EntryNames.After(entry.Name, Entry(app.Key));
        if (rest is null)
            return;
        var (part, tail) = EntryNames.SplitFirst(rest);
        switch (part)
        {
            case "app.json":
            {
                // The new PC may switch a copied app to a fresh install, but can't copy files that weren't sent.
                var captured = entry.Header.GetMeta("mode") ?? item.DefaultMode ?? ItemModes.Skip;
                var chosen = context.Selection.ModeFor(item) ?? captured;
                var mode = chosen == ItemModes.Copy && captured != ItemModes.Copy ? captured : chosen;
                if (mode == ItemModes.Winget && app.WingetId is null && !item.AvailableModes.Contains(ItemModes.Winget))
                    mode = captured;
                lock (state.Gate)
                    state.Apps[app.Key] = (item, app, mode);
                if (mode == ItemModes.Winget)
                    QueueInstall(context, state, item, app, ct);
                else if (mode == ItemModes.Skip || item.AvailableModes.Count == 0)
                    context.Results.ManualStep($"Install {app.DisplayName}{(app.Summary.Length > 0 ? $" ({app.Summary})" : "")}.");
                break;
            }
            case "files":
            {
                var root = TargetInstallPath(context, app.InstallLocation!);
                var logical = Path.Combine(root, tail);
                context.Status(item, logical);
                if (entry.Kind == EntryKind.Directory)
                    context.Files.RestoreDirectory(entry, logical);
                else
                    await context.Files.RestoreAsync(entry, item, logical, ct, policy: ConflictPolicy.Overwrite, background: true).ConfigureAwait(false);
                break;
            }
            case "reg":
            {
                var export = await entry.ReadJsonAsync<RegistryExport>(ct).ConfigureAwait(false);
                RegistryRoot? target;
                if (export.Root == "HKCU")
                {
                    // Per-user settings only go to the person this user was mapped to, never to whoever is signed in.
                    var user = context.TargetUser(item);
                    if (user is null)
                    {
                        context.Results.Info(item, "Its per-user settings were skipped: no user on this PC was chosen for their owner.");
                        break;
                    }
                    target = context.UserRegistry(item, user);
                }
                else
                {
                    target = context.MachineRegistry();
                }
                if (target is null)
                    break;
                var remap = InstallRemap(context, app);
                var result = RegistrySnapshot.Apply(target, export, context.Journal, context.DryRun, remap);
                foreach (var error in result.Errors.Take(5))
                    context.Results.Warn(item, $"Registry: {error}");
                break;
            }
            case "shortcuts":
            {
                var (tag, relative) = EntryNames.SplitFirst(tail);
                var root = tag == "desktop"
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                    : Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
                await context.Files.RestoreAsync(entry, item, Path.Combine(root, relative), ct, InstallRemap(context, app), ConflictPolicy.Overwrite)
                    .ConfigureAwait(false);
                break;
            }
            case "services.json":
            {
                var services = await entry.ReadJsonAsync<List<ServiceRecord>>(ct).ConfigureAwait(false);
                lock (state.Gate)
                    state.Services[app.Key] = services;
                break;
            }
        }
    }

    public override async Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var state = context.State<RestoreState>(Id);
        state.Installs?.Writer.TryComplete();
        if (state.InstallWorker is not null)
            await state.InstallWorker.ConfigureAwait(false);

        foreach (var (key, services) in state.Services)
        {
            var (item, app, _) = state.Apps[key];
            foreach (var service in services)
                await CreateServiceAsync(context, item, app, service, ct).ConfigureAwait(false);
        }

        foreach (var (item, app, mode) in state.Apps.Values.Where(a => a.Mode == ItemModes.Copy))
            context.Results.Info(item, $"Program files copied to {TargetInstallPath(context, app.InstallLocation!)}.");

        // Everything else that was on the old PC goes in the report as a to-do.
        if (state.AllApps is { } all)
        {
            var handled = state.Apps.Values.Where(a => a.Mode != ItemModes.Skip).Select(a => a.App.Key).ToHashSet(StringComparer.Ordinal);
            var notMoved = all.Where(a => !handled.Contains(a.Key) && !a.IsSystemComponent).OrderBy(a => a.DisplayName).ToList();
            foreach (var app in notMoved)
                context.Results.ProgramNotMoved(app.Summary.Length > 0 ? $"{app.DisplayName} ({app.Summary})" : app.DisplayName);
        }
    }

    private void QueueInstall(RestoreContext context, RestoreState state, InventoryItem item, AppRecord app, CancellationToken ct)
    {
        lock (state.Gate)
        {
            if (state.Installs is null)
            {
                state.Installs = Channel.CreateUnbounded<(InventoryItem, AppRecord)>();
                // Installs run alongside the file copy instead of after it; they are mostly waiting on downloads.
                state.InstallWorker = Task.Run(() => InstallLoopAsync(context, state, ct), ct);
            }
        }
        state.Installs.Writer.TryWrite((item, app));
    }

    private async Task InstallLoopAsync(RestoreContext context, RestoreState state, CancellationToken ct)
    {
        if (!Winget.IsAvailable && !context.SimulateCommands)
        {
            await foreach (var (item, app) in state.Installs!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                context.Results.MarkFailed(item, "winget (App Installer) isn't available on this PC.");
                context.Results.ManualStep($"Install {app.DisplayName}.");
            }
            context.Results.ManualStep("Install \"App Installer\" from the Microsoft Store to get winget, then run the migration again to reinstall your programs automatically.");
            return;
        }

        if (!context.SimulateCommands)
        {
            try
            {
                foreach (var p in await Winget.ListInstalledAsync(ct).ConfigureAwait(false))
                    state.InstalledIds.Add(p.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Could not list programs on this PC: {ex.Message}");
            }
        }

        await foreach (var (item, app) in state.Installs!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var id = app.WingetId;
            if (id is null)
            {
                // The old PC had no winget; look the name up here.
                var found = context.SimulateCommands ? null : await Winget.FindByNameAsync(app.DisplayName, ct).ConfigureAwait(false);
                if (found is null)
                {
                    context.Results.MarkFailed(item, "No matching winget package was found.");
                    context.Results.ManualStep($"Install {app.DisplayName}.");
                    continue;
                }
                id = found.Id;
            }
            if (!state.HandledIds.Add(id))
            {
                context.Results.Info(item, "Installed together with another program from the list.");
                continue;
            }
            if (state.InstalledIds.Contains(id))
            {
                context.Results.Info(item, "Already installed on this PC.");
                continue;
            }

            context.Status(item, $"Installing {app.DisplayName} with winget", force: true);
            if (context.SimulateCommands)
            {
                Log.Info($"Would install {id} with winget");
                context.Results.Info(item, $"Would be installed with winget ({id}).");
                continue;
            }
            var result = await Winget.InstallAsync(id, app.WingetSource, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                if (result.AlreadyInstalled)
                {
                    context.Results.Info(item, "Already installed on this PC.");
                }
                else
                {
                    context.Results.Info(item, $"Installed with winget ({id}).");
                    context.Journal.CommandRun($"Install {app.DisplayName}", Winget.ExecutablePath!, Winget.UninstallArguments(id));
                }
            }
            else
            {
                context.Results.MarkFailed(item, $"winget couldn't install it: {result.Message}");
                context.Results.ManualStep($"Install {app.DisplayName} (winget id {id}).");
            }
        }
    }

    private static async Task CreateServiceAsync(RestoreContext context, InventoryItem item, AppRecord app, ServiceRecord service, CancellationToken ct)
    {
        using (var existing = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service.Name}"))
        {
            if (existing is not null)
                return;
        }
        var binPath = InstallRemap(context, app).Remap(service.ImagePath);
        var start = service.Start switch { 2 => "auto", 4 => "disabled", _ => "demand" };
        var type = service.Type == 0x20 ? "share" : "own";
        // sc.exe wants "option= value" with the space after '='.
        var args = $"create \"{service.Name}\" binPath= \"{binPath.Replace("\"", "\\\"")}\" start= {start} type= {type} DisplayName= \"{service.DisplayName}\"";
        var result = await context.RunAsync(item, $"Service {service.DisplayName}", ProcessRunner.System32("sc.exe"), args,
            ProcessRunner.System32("sc.exe"), $"delete \"{service.Name}\"", ct).ConfigureAwait(false);
        if (result.Succeeded)
            context.Results.Info(item, $"Created the Windows service \"{service.DisplayName}\".");
        else
            context.Results.Warn(item, $"Could not create the service \"{service.DisplayName}\": {result.CombinedOutput.Trim()}");
    }

    /// <summary>The same program folder, moved to the system drive if the old drive letter doesn't exist here.</summary>
    private static string TargetInstallPath(RestoreContext context, string installLocation)
    {
        var root = Path.GetPathRoot(installLocation);
        if (root is not null && context.Target.Drives.Any(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase)))
            return installLocation;
        return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, root is null ? installLocation : installLocation[root.Length..]);
    }

    private static PathRemapper InstallRemap(RestoreContext context, AppRecord app)
    {
        var remap = new PathRemapper();
        if (app.InstallLocation is not null)
            remap.Add(app.InstallLocation, TargetInstallPath(context, app.InstallLocation));
        return remap;
    }
}
