using System.Text;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Reporting;
using Ferry.Core.Rollback;
using Ferry.Core.Transport;

namespace Ferry.Cli;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitItemsFailed = 2;
    private const int ExitStopped = 3;
    private const int ExitCancelled = 4;
    private const int ExitPassword = 5;
    private const int ExitUsage = 64;

    private static ConsoleUi ui = new();

    private static readonly string[] CommonOptions = ["quiet", "verbose", "help", "h", "?"];
    private static readonly string[] PlanOptions =
        ["plan", "user", "folder", "exclude", "include", "skip", "apps", "no-winget", "conflicts", "close-apps", "no-restore-point", "map", "map-folder"];
    private static readonly string[] PasswordOptions = ["password", "password-env", "password-file"];

    /// <summary>
    /// The options each command reads. Anything else is refused: an ignored typo in a silent deployment
    /// ("--skp Downloads") would quietly move something the admin meant to leave behind.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownOptions = new()
    {
        ["scan"] = [.. PlanOptions, "all", "details", "json"],
        ["capture"] = [.. PlanOptions, .. PasswordOptions, "out", "split", "no-shadow"],
        ["restore"] = [.. PlanOptions, .. PasswordOptions, "in", "dry-run", "sandbox"],
        ["send"] = [.. PlanOptions, "to", "code"],
        ["receive"] = [.. PlanOptions, "code", "timeout", "listen", "port", "dry-run", "sandbox"],
        ["undo"] = ["list", "discard", "yes"],
        ["info"] = [.. PasswordOptions, "in", "details"],
        ["plan"] = ["out", "from-scan", "no-winget"],
    };

    private static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var args = CliArgs.Parse(argv);
        if (args.Command is null or "help" || args.Has("help") || args.Has("h") || args.Has("?"))
        {
            PrintHelp(args.Positionals.FirstOrDefault() ?? (args.Command is "help" or null ? null : args.Command));
            return args.Command is null ? ExitUsage : ExitOk;
        }
        if (args.Command == "version")
        {
            Console.WriteLine($"Ferry {SystemInfo.AppVersion}");
            return ExitOk;
        }
        var command = args.Command switch { "save" => "capture", "apply" => "restore", var c => c };
        if (KnownOptions.TryGetValue(command, out var known) && args.Unknown([.. known, .. CommonOptions]).FirstOrDefault() is { } unknown)
        {
            ui.Error($"\"{command}\" has no option --{unknown}. Run \"ferry-cli help {command}\" to see its options.");
            return ExitUsage;
        }

        ui = new ConsoleUi { Quiet = args.Has("quiet") };
        Log.Initialize("cli");
        Log.Verbose = args.Has("verbose");
        // Problems end up in the report and summary; the console only echoes the log with --verbose.
        Log.Message += (level, text) =>
        {
            if (!Log.Verbose)
                return;
            if (level == MessageLevel.Error)
                ui.Error(text);
            else if (level == MessageLevel.Warning)
                ui.Warn(text);
            else
                ui.Line(text);
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (!cts.IsCancellationRequested)
            {
                ui.Warn("Cancelling… (changes made so far can be undone with \"ferry-cli undo\")");
                cts.Cancel();
            }
        };

        try
        {
            return args.Command switch
            {
                "scan" => await ScanAsync(args, cts.Token),
                "capture" or "save" => await CaptureAsync(args, cts.Token),
                "restore" or "apply" => await RestoreAsync(args, cts.Token),
                "send" => await SendAsync(args, cts.Token),
                "receive" => await ReceiveAsync(args, cts.Token),
                "undo" => await UndoAsync(args, cts.Token),
                "info" => await InfoAsync(args, cts.Token),
                "plan" => await PlanAsync(args, cts.Token),
                _ => throw new UsageException($"Unknown command \"{args.Command}\". Run \"ferry-cli help\" for the list."),
            };
        }
        catch (UsageException ex)
        {
            ui.Error(ex.Message);
            return ExitUsage;
        }
        catch (Exception ex) when (ex is WrongPasswordException or PasswordRequiredException)
        {
            ui.Error(ex.Message);
            return ExitPassword;
        }
        catch (OperationCanceledException)
        {
            ui.Warn("Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PairingFailedException or InvalidOperationException or UnauthorizedAccessException)
        {
            ui.Error(ex.Message);
            Log.Error("Command failed", ex);
            return ExitStopped;
        }
        catch (Exception ex)
        {
            ui.Error($"Unexpected error: {ex.Message} (details in {Log.FilePath})");
            Log.Error("Unexpected error", ex);
            return ExitStopped;
        }
    }

    // ─────────────────────────────── scan ───────────────────────────────

    private static async Task<int> ScanAsync(CliArgs args, CancellationToken ct)
    {
        var plan = LoadPlan(args);
        var engine = new MigrationEngine();
        var options = plan.ToScanOptions();
        options.QueryWinget = !args.Has("no-winget");
        var inventory = await engine.ScanAsync(options, ui, ct);
        ui.EndStatus();
        var selection = plan.BuildSelection(inventory);
        PrintInventory(inventory, selection, args.Has("all") || args.Has("details"), args.Has("details"));
        if (args.Get("json") is { } json && json != "true")
        {
            Json.WriteFile(json, inventory);
            ui.Line($"Inventory written to {json}");
        }
        return ExitOk;
    }

    // ────────────────────────────── capture ─────────────────────────────

    private static async Task<int> CaptureAsync(CliArgs args, CancellationToken ct)
    {
        var output = Path.GetFullPath(args.Require("out", "file.ferry"));
        if (!output.EndsWith(PackageFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            output += PackageFormat.FileExtension;
        var password = ResolvePassword(args, confirm: true);
        var plan = LoadPlan(args);
        WarnIfNotAdmin();

        var engine = new MigrationEngine();
        var scan = plan.ToScanOptions();
        scan.QueryWinget = !args.Has("no-winget");
        var inventory = await engine.ScanAsync(scan, ui, ct);
        ui.EndStatus();
        var selection = plan.BuildSelection(inventory);
        var capture = plan.ToCaptureOptions();
        if (args.Has("no-shadow"))
            capture.UseShadowCopy = false;

        PrintSelectionSummary(inventory, selection);
        foreach (var issue in Preflight.ForCapture(inventory, selection, output, password is not null))
        {
            if (issue.Blocking)
                throw new InvalidOperationException(issue.Text);
            ui.Warn(issue.Text);
        }

        long? partSize = args.Get("split") switch
        {
            null or "auto" => PackageFiles.AutoPartSize(output),
            "none" or "off" => null,
            var size => Format.ParseSize(size),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        MigrationReport report;
        await using (var stream = PackageFiles.OpenWrite(output, partSize))
        {
            await using var writer = await PackageWriter.CreateAsync(stream, new PackageHeader
            {
                SourceComputer = inventory.Machine.ComputerName,
                AppVersion = SystemInfo.AppVersion,
                SplitPartSize = partSize,
            }, password, ct);
            report = await engine.CaptureAsync(inventory, selection, capture, writer, ui, ct);
        }
        report.PackagePath = output;
        if (report.Cancelled || report.FatalError is not null)
        {
            foreach (var part in PackageFiles.ExistingParts(output))
                File.Delete(part);
            ui.Warn("The incomplete migration file was deleted.");
        }
        return Finish(report, Path.GetDirectoryName(output));
    }

    // ────────────────────────────── restore ─────────────────────────────

    private static async Task<int> RestoreAsync(CliArgs args, CancellationToken ct)
    {
        var input = Path.GetFullPath(args.Require("in", "file.ferry"));
        if (!File.Exists(input))
            throw new UsageException($"{input} does not exist.");
        var password = ResolvePassword(args, confirm: false);
        var plan = LoadPlan(args);
        WarnIfNotAdmin();

        PackageHeader header;
        Inventory inventory;
        try
        {
            (header, inventory, _) = await PackageReader.ReadManifestAsync(input, password, ct);
        }
        catch (PasswordRequiredException) when (!Console.IsInputRedirected && password is null)
        {
            password = ConsoleUi.ReadPassword("Password for the migration file: ");
            (header, inventory, _) = await PackageReader.ReadManifestAsync(input, password, ct);
        }
        ui.Line($"Migration file from {header.SourceComputer}, saved {header.CreatedUtc.ToLocalTime():g}{(header.Encryption is null ? "" : " (encrypted)")}");

        var targetUsers = UserProfiles.Enumerate();
        var selection = plan.BuildSelection(inventory, everythingByDefault: true);
        var options = BuildRestoreOptions(args, plan, inventory, targetUsers);
        PrintSelectionSummary(inventory, selection);
        PrintUserMap(inventory, options, targetUsers);
        if (!CheckPreflight(Preflight.ForRestore(inventory, selection, options, targetUsers)))
            return ExitStopped;

        var engine = new MigrationEngine();
        await using var reader = await PackageReader.OpenAsync(PackageFiles.OpenRead(input), _ => password, ownsInput: true, ct);
        await reader.ReadManifestAsync(ct);
        var report = await engine.RestoreAsync(reader, inventory, selection, options, ui, ct);
        report.PackagePath = input;
        return Finish(report, null);
    }

    // ─────────────────────────────── send ───────────────────────────────

    private static async Task<int> SendAsync(CliArgs args, CancellationToken ct)
    {
        var target = args.Require("to", "new PC name or address");
        var port = NetworkDefaults.TransferPort;
        var colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], out var p))
        {
            port = p;
            target = target[..colon];
        }
        var code = args.Get("code") is { Length: > 0 } c && c != "true" ? c : ConsoleUi.ReadPassword("Pairing code shown on the new PC: ");
        var plan = LoadPlan(args);
        WarnIfNotAdmin();

        ui.Line($"Connecting to {target}…");
        await using var session = await SenderSession.ConnectAsync(target, port, code, ct);
        ui.Good($"Connected to {session.TargetMachine.ComputerName} ({session.TargetMachine.OsSummary}).");
        session.StageChanged += stage =>
        {
            var text = stage switch
            {
                SenderStage.Scanning => "Scanning this PC…",
                SenderStage.WaitingForChoices => "Waiting for choices on the new PC…",
                SenderStage.Sending => "Sending…",
                SenderStage.WaitingForNewPc => "Sent. Waiting for the new PC to finish (installing programs can take a while)…",
                _ => null,
            };
            if (text is not null)
                ui.Line(text);
        };
        var engine = new MigrationEngine();
        var scan = plan.ToScanOptions();
        scan.QueryWinget = !args.Has("no-winget");
        var (capture, restore) = await session.RunAsync(engine, scan, ui, ct);
        if (restore is not null)
        {
            var restorePath = HtmlReport.Save(restore);
            ui.Line($"The new PC's report: {restorePath}");
            return Finish(capture, null, exitFrom: restore);
        }
        return Finish(capture, null);
    }

    // ────────────────────────────── receive ─────────────────────────────

    private static async Task<int> ReceiveAsync(CliArgs args, CancellationToken ct)
    {
        var plan = LoadPlan(args);
        WarnIfNotAdmin();
        var fixedCode = args.Get("code") is { Length: > 0 } c && c != "true" ? c : null;
        if (fixedCode is not null && !PairingCode.LooksValid(fixedCode))
            throw new UsageException($"--code needs at least {PairingCode.Length} letters or digits (or leave it out to get a random one-time code).");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (args.Get("timeout") is { } minutes && double.TryParse(minutes, out var m))
            timeout.CancelAfter(TimeSpan.FromMinutes(m));

        var listen = args.Get("listen") is { } l && l != "true" ? System.Net.IPAddress.Parse(l) : null;
        var port = args.Get("port") is { } portText && int.TryParse(portText, out var parsedPort) ? parsedPort : NetworkDefaults.TransferPort;
        await using var receiver = NetworkReceiver.Start(fixedCode, port, UserProfiles.GetInteractiveAccountName(), listen);
        ui.Line($"Waiting for the old PC. On the old PC run:");
        ui.Line($"    ferry-cli send --to {Environment.MachineName} --code {receiver.Code}");
        ui.Line($"(or use the Ferry app). This PC: {Environment.MachineName} · {string.Join(", ", receiver.Addresses)} · port {receiver.Port}");
        receiver.CodeChanged += code => ui.Warn($"Someone tried a wrong code. The new pairing code is {code}");

        await using var session = await receiver.WaitForSenderAsync(timeout.Token);
        ui.Good($"Connected to {session.SourceMachine.ComputerName}. Waiting for it to scan…");
        var inventory = await session.ReceiveInventoryAsync(status => ui.Report(new MigrationProgress { Phase = "Old PC", CurrentDetail = status }), ct);
        ui.EndStatus();

        var targetUsers = UserProfiles.Enumerate();
        var selection = plan.BuildSelection(inventory);
        var options = BuildRestoreOptions(args, plan, inventory, targetUsers);
        PrintSelectionSummary(inventory, selection);
        PrintUserMap(inventory, options, targetUsers);
        if (!CheckPreflight(Preflight.ForRestore(inventory, selection, options, targetUsers)))
        {
            await session.CancelAsync();
            return ExitStopped;
        }
        var capture = plan.ToCaptureOptions();
        var report = await session.RestoreAsync(new MigrationEngine(), selection, capture, options, ui, ct);
        return Finish(report, null);
    }

    // ─────────────────────────────── undo ───────────────────────────────

    private static async Task<int> UndoAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Has("list") || (args.Positionals.Count == 0 && !args.Has("discard")))
        {
            var all = UndoEngine.List();
            if (all.Count == 0)
            {
                ui.Line("There are no migrations to undo on this PC.");
                return ExitOk;
            }
            ui.Line($"{"Id",-22} {"When",-18} {"From",-16} {"Changes",8}  Status");
            foreach (var info in all)
                ui.Line($"{info.Id,-22} {info.CreatedUtc.ToLocalTime(),-18:g} {info.SourceComputer,-16} {info.ChangeCount,8:N0}  {info.Status}");
            return ExitOk;
        }
        if (args.Get("discard") is { } discard && discard != "true")
        {
            UndoEngine.Discard(discard);
            ui.Good($"Discarded the undo data for {discard}.");
            return ExitOk;
        }

        var id = args.Positionals[0];
        var found = UndoEngine.Find(id) ?? throw new UsageException($"No undo data with id {id}. Run \"ferry-cli undo --list\".");
        if (!args.Has("yes"))
        {
            ui.Line($"This reverses {found.ChangeCount:N0} changes made by the migration from {found.SourceComputer} on {found.CreatedUtc.ToLocalTime():g}.");
            ui.Line("Files it added are deleted (including any edits made to them since). Replaced files and settings are put back.");
            Console.Write("Type YES to continue: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal))
                return ExitCancelled;
        }
        var report = await UndoEngine.UndoAsync(id, ui, ct);
        return Finish(report, null);
    }

    // ─────────────────────────────── info ───────────────────────────────

    private static async Task<int> InfoAsync(CliArgs args, CancellationToken ct)
    {
        var input = args.Positionals.FirstOrDefault() ?? args.Require("in", "file.ferry");
        var password = ResolvePassword(args, confirm: false);
        PackageHeader header;
        Inventory inventory;
        try
        {
            (header, inventory, _) = await PackageReader.ReadManifestAsync(input, password, ct);
        }
        catch (PasswordRequiredException) when (!Console.IsInputRedirected && password is null)
        {
            password = ConsoleUi.ReadPassword("Password for the migration file: ");
            (header, inventory, _) = await PackageReader.ReadManifestAsync(input, password, ct);
        }
        ui.Line($"File:      {Path.GetFullPath(input)} ({Format.Bytes(PackageFiles.TotalSize(input))}, {Format.Count(PackageFiles.ExistingParts(input).Count, "part")})");
        ui.Line($"From:      {inventory.Machine.ComputerName} · {inventory.Machine.OsSummary} · {inventory.Machine.JoinType}");
        ui.Line($"Saved:     {header.CreatedUtc.ToLocalTime():F} with Ferry {header.AppVersion}");
        ui.Line($"Protected: {(header.Encryption is null ? "no password" : "password (AES-256)")}");
        ui.Line($"Users:     {string.Join(", ", inventory.Users.Select(u => u.AccountName))}");
        ui.Line();
        PrintInventory(inventory, Selection.All(inventory), showAll: true, details: args.Has("details"));
        return ExitOk;
    }

    // ─────────────────────────────── plan ───────────────────────────────

    private static async Task<int> PlanAsync(CliArgs args, CancellationToken ct)
    {
        var output = args.Require("out", "plan.json");
        MigrationPlan plan;
        if (args.Has("from-scan"))
        {
            var engine = new MigrationEngine();
            var inventory = await engine.ScanAsync(new ScanOptions { QueryWinget = !args.Has("no-winget") }, ui, ct);
            ui.EndStatus();
            plan = MigrationPlan.FromSelection(inventory, Selection.Defaults(inventory));
        }
        else
        {
            plan = new MigrationPlan
            {
                Exclude = ["*.iso", "*.vhdx"],
                Skip = ["migrator:folders", "category:AppSettings"],
                Apps = ItemModes.Winget,
                UserMap = new(StringComparer.OrdinalIgnoreCase) { ["OLDPC\\someone"] = "AzureAD\\SomeoneElse" },
            };
        }
        plan.Save(output);
        ui.Good($"Plan written to {output}. Edit it, then pass it with --plan to capture, restore, send or receive.");
        return ExitOk;
    }

    // ────────────────────────────── helpers ─────────────────────────────

    private static MigrationPlan LoadPlan(CliArgs args)
    {
        var plan = args.Get("plan") is { } path && path != "true" ? MigrationPlan.Load(path) : new MigrationPlan();
        plan.Users.AddRange(args.GetAll("user"));
        plan.ExtraFolders.AddRange(args.GetAll("folder"));
        plan.Exclude.AddRange(args.GetAll("exclude"));
        plan.Include.AddRange(args.GetAll("include"));
        plan.Skip.AddRange(args.GetAll("skip"));
        if (args.Get("apps") is { } apps && apps != "true")
            plan.Apps = apps.ToLowerInvariant();
        if (args.Get("conflicts") is { } conflicts && conflicts != "true")
        {
            plan.Conflicts = conflicts.ToLowerInvariant() switch
            {
                "keepboth" or "keep-both" or "both" => ConflictPolicy.KeepBoth,
                "newer" or "overwrite-if-newer" or "overwriteifnewer" => ConflictPolicy.OverwriteIfNewer,
                "overwrite" or "replace" => ConflictPolicy.Overwrite,
                "skip" => ConflictPolicy.Skip,
                _ => throw new UsageException("--conflicts must be keepboth, newer, overwrite or skip."),
            };
        }
        if (args.Has("close-apps"))
            plan.CloseApps = true;
        if (args.Has("no-restore-point"))
            plan.CreateRestorePoint = false;
        foreach (var map in args.GetAll("map"))
        {
            var eq = map.IndexOf('=');
            if (eq <= 0)
                throw new UsageException("--map takes OLDUSER=NEWUSER, e.g. --map \"OLDPC\\joe=AzureAD\\JoeSmith\".");
            plan.UserMap[map[..eq]] = map[(eq + 1)..];
        }
        foreach (var map in args.GetAll("map-folder"))
        {
            var eq = map.IndexOf('=');
            if (eq <= 0)
                throw new UsageException("--map-folder takes OLD=NEW, e.g. --map-folder \"D:\\Data=C:\\Data\".");
            plan.FolderMap[map[..eq]] = map[(eq + 1)..];
        }
        return plan;
    }

    private static RestoreOptions BuildRestoreOptions(CliArgs args, MigrationPlan plan, Inventory inventory, IReadOnlyList<UserProfileInfo> targetUsers)
    {
        var options = plan.ToRestoreOptions(inventory, targetUsers);
        options.DryRun = args.Has("dry-run");
        if (args.Get("sandbox") is { } sandbox && sandbox != "true")
        {
            options.SandboxRoot = Path.GetFullPath(sandbox);
            options.CreateRestorePoint = false;
            Directory.CreateDirectory(options.SandboxRoot);
            ui.Warn($"Sandbox: files go under {options.SandboxRoot}, registry changes under HKCU\\Software\\FerrySandbox, and installs and system changes are only logged.");
        }
        if (options.DryRun)
            ui.Warn("Dry run: nothing will be changed.");
        return options;
    }

    private static string? ResolvePassword(CliArgs args, bool confirm)
    {
        if (args.Get("password-env") is { } env && env != "true")
            return Environment.GetEnvironmentVariable(env) ?? throw new UsageException($"The environment variable {env} is not set.");
        if (args.Get("password-file") is { } file && file != "true")
            return File.ReadAllText(file).TrimEnd('\r', '\n');
        if (args.Get("password") is { } password)
        {
            if (password != "true")
                return password;
            // --password with no value: ask.
            var first = ConsoleUi.ReadPassword("Password: ");
            if (confirm && ConsoleUi.ReadPassword("Again: ") != first)
                throw new UsageException("The passwords don't match.");
            return first;
        }
        return null;
    }

    private static void WarnIfNotAdmin()
    {
        if (!Elevation.IsAdministrator)
            ui.Warn("Not running as administrator: only your own account is included and machine settings will be skipped. Use an elevated prompt for a full migration.");
    }

    private static bool CheckPreflight(List<PreflightIssue> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.Blocking)
                ui.Error(issue.Text);
            else
                ui.Warn(issue.Text);
        }
        return !issues.Any(i => i.Blocking);
    }

    private static void PrintSelectionSummary(Inventory inventory, Selection selection)
    {
        var selected = inventory.Items.Where(i => selection.Contains(i.Id)).ToList();
        ui.Line($"{Format.Count(selected.Count, "item")} selected, about {Format.Bytes(selected.Sum(i => Preflight.EffectiveSize(i, selection)))}:");
        foreach (var group in selected.GroupBy(i => i.Category).OrderBy(g => g.Key))
            ui.Line($"  {Categories.DisplayName(group.Key),-28} {Format.Count(group.Count(), "item"),10}  {Format.Bytes(group.Sum(i => Preflight.EffectiveSize(i, selection))),10}");
        foreach (var warning in inventory.Warnings)
            ui.Warn(warning);
    }

    private static void PrintUserMap(Inventory inventory, RestoreOptions options, IReadOnlyList<UserProfileInfo> targetUsers)
    {
        foreach (var user in inventory.Users)
        {
            var target = options.UserMap.TryGetValue(user.Sid, out var sid) ? targetUsers.FirstOrDefault(t => t.Sid == sid)?.AccountName : null;
            ui.Line($"  {user.AccountName} → {target ?? "(not moved; use --map to choose a user)"}");
        }
    }

    private static void PrintInventory(Inventory inventory, Selection selection, bool showAll, bool details)
    {
        ui.Line($"{inventory.Machine.ComputerName} · {inventory.Machine.OsSummary} · {inventory.Machine.JoinType}");
        foreach (var warning in inventory.Warnings)
            ui.Warn(warning);
        foreach (var category in inventory.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            ui.Line();
            ui.Line(Categories.DisplayName(category.Key).ToUpperInvariant());
            foreach (var group in category.GroupBy(i => i.Group))
            {
                var items = group.Where(i => showAll || selection.Contains(i.Id)).ToList();
                if (items.Count == 0)
                    continue;
                ui.Line($"  {group.Key}");
                foreach (var item in items)
                {
                    var mark = selection.Contains(item.Id) ? "[x]" : "[ ]";
                    var mode = selection.ModeFor(item) is { } m && item.AvailableModes.Count > 0 ? $"  ({ItemModes.DisplayName(m)})" : "";
                    var size = item.SizeBytes > 0 ? Format.Bytes(item.SizeBytes) : "";
                    ui.Line($"    {mark} {Truncate(item.Name, 60),-60} {size,10}{mode}");
                    if (details)
                    {
                        ui.Line($"        id: {item.Id}");
                        foreach (var warning in item.Warnings)
                            ui.Line($"        ! {warning}");
                    }
                }
            }
        }
        var selectedItems = inventory.Items.Where(i => selection.Contains(i.Id)).ToList();
        ui.Line();
        ui.Line($"{Format.Count(selectedItems.Count, "item")} selected of {inventory.Items.Count}, about {Format.Bytes(selectedItems.Sum(i => Preflight.EffectiveSize(i, selection)))}.");
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static int Finish(MigrationReport report, string? copyReportTo, MigrationReport? exitFrom = null)
    {
        ui.EndStatus();
        var path = HtmlReport.Save(report, copyReportTo);
        var source = exitFrom ?? report;
        var failed = report.Items.Where(i => i.Status == ItemStatus.Failed).ToList();
        foreach (var item in failed.Take(15))
            ui.Error($"{item.Name}: {item.Messages.LastOrDefault(m => m.Level == MessageLevel.Error)?.Text ?? "failed"}");
        if (failed.Count > 15)
            ui.Error($"…and {failed.Count - 15} more (see the report).");

        if (report.ManualSteps.Count > 0)
        {
            ui.Line();
            ui.Line("Still to do:");
            foreach (var step in report.ManualSteps.Take(25))
                ui.Line($"  • {step}");
            if (report.ManualSteps.Count > 25)
                ui.Line($"  …and {report.ManualSteps.Count - 25} more in the report.");
        }
        if (report.ProgramsNotMoved.Count > 0)
            ui.Line($"{Format.Count(report.ProgramsNotMoved.Count, "program")} from the old PC weren't reinstalled; they're listed in the report.");
        foreach (var note in report.Notes)
            ui.Line($"Note: {note}");

        ui.Line();
        var elapsed = Format.Duration(report.FinishedUtc - report.StartedUtc);
        var summary = report.Kind == OperationKind.Undo
            ? $"{HtmlReport.Outcome(report)}: {Format.Count(report.TotalFiles, "change")} reversed in {elapsed}."
            : $"{HtmlReport.Outcome(report)}: {Format.Count(report.TotalFiles, "file")}, {Format.Bytes(report.TotalBytes)} in {elapsed}.";
        if (report.Cancelled || report.FatalError is not null || failed.Count > 0)
            ui.Warn(summary);
        else
            ui.Good(summary);
        if (report.FatalError is not null)
            ui.Error(report.FatalError);
        if (report.UndoId is not null && report.Kind == OperationKind.Restore)
            ui.Line($"To undo: ferry-cli undo {report.UndoId}");
        ui.Line($"Report: {path}");

        return source.Cancelled ? ExitCancelled
            : source.FatalError is not null ? ExitStopped
            : source.Items.Any(i => i.Status == ItemStatus.Failed) ? ExitItemsFailed
            : ExitOk;
    }

    private static void PrintHelp(string? command)
    {
        var help = command switch
        {
            "capture" or "save" => """
                ferry-cli capture --out <file.ferry> [options]
                  Scans this (old) PC and saves what's selected to a migration file.
                  --password [<pw>]     Encrypt the file (asks if no value). Also --password-env VAR, --password-file F.
                  --split auto|none|4GB Split into parts (auto splits on FAT32 drives).
                  --no-shadow           Don't use Volume Shadow Copy for files that are in use.
                """,
            "restore" or "apply" => """
                ferry-cli restore --in <file.ferry> [options]
                  Applies a migration file to this (new) PC.
                  --map OLD=NEW         Map an old-PC user to a user here (repeatable). Others map by name, else to you.
                  --map-folder OLD=NEW  Restore a folder from outside the user profiles somewhere else.
                  --conflicts keepboth|newer|overwrite|skip   When a file already exists (default keepboth).
                  --apps winget         Reinstall copied programs with winget instead.
                  --close-apps          Close browsers and Office when their data is replaced.
                  --dry-run             Show what would happen; change nothing.
                  --sandbox <dir>       Test restore: files under <dir>, registry under HKCU\Software\FerrySandbox.
                  --no-restore-point    Skip the System Restore point.
                """,
            "send" => """
                ferry-cli send --to <new-pc>[:port] --code <pairing code> [selection options]
                  Connects to a new PC waiting with "ferry-cli receive" (or the app) and sends this PC's data.
                """,
            "receive" => """
                ferry-cli receive [--code <code>] [--timeout <minutes>] [selection and restore options]
                  Waits for an old PC on the network. Without --code a one-time code is shown and changes after a wrong try.
                """,
            "undo" => """
                ferry-cli undo --list | <id> [--yes] | --discard <id>
                  Reverses a restore on this PC, or deletes its undo data to free space.
                """,
            _ => """
                Ferry — move files, settings and programs from one Windows PC to another.

                  ferry-cli scan        List what can be moved from this PC (--all, --details, --json <file>)
                  ferry-cli capture     Save this PC's data to a migration file
                  ferry-cli restore     Apply a migration file to this PC
                  ferry-cli send        Send this PC's data to a new PC over the network
                  ferry-cli receive     Receive from an old PC over the network
                  ferry-cli undo        Undo a migration on this PC
                  ferry-cli info <file> Show what's in a migration file
                  ferry-cli plan --out <plan.json> [--from-scan]   Write a plan file for silent runs

                Selection options (scan, capture, send, receive, restore):
                  --plan <plan.json>    Choices saved from the app or written by "ferry-cli plan".
                  --include <rule>      Add items: all, defaults, category:Files, migrator:wifi, name:*Chrome*, group:*joe*, item:<id>
                  --skip <rule>         Remove items (same rules).
                  --user <name>         Only include these users (repeatable).
                  --folder <path>       Also offer a folder outside user profiles (repeatable).
                  --exclude <pattern>   Never copy matching files, e.g. *.iso or Downloads\*.exe (repeatable).
                  --apps winget|copy    How to move programs (default: reinstall with winget).
                  --verbose, --quiet

                Exit codes: 0 done, 2 some items failed, 3 stopped, 4 cancelled, 5 password problem, 64 bad arguments.
                Run "ferry-cli help <command>" for more.
                """,
        };
        Console.WriteLine(help);
    }
}
