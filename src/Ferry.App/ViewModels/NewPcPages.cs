using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Ferry.App.Mvvm;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Transport;

namespace Ferry.App.ViewModels;

/// <summary>New PC, network: shows the pairing code and waits for the old PC to connect and scan.</summary>
public sealed class WaitViewModel : PageViewModel
{
    private CancellationTokenSource? cts;
    private string code = "";
    private string status = "Waiting for your old PC to connect…";
    private bool connected;

    public WaitViewModel(MainViewModel main) : base(main)
    {
    }

    public override string Title => connected ? $"Connected to {Flow.ReceiverSession?.SourceMachine.ComputerName}" : "Waiting for your old PC";
    public override string Subtitle => connected
        ? "The old PC is looking for your files, settings and programs. This can take a few minutes."
        : "Leave this screen open and go to your old PC.";
    public override string Step => "Connect";
    public override bool ShowNext => false;

    public string Code
    {
        get => code;
        private set => Set(ref code, value);
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool IsConnected
    {
        get => connected;
        private set
        {
            if (Set(ref connected, value))
                OnPropertiesChanged(nameof(Title), nameof(Subtitle));
        }
    }

    public string ComputerName => ThisPc.Name();
    public string AddressText { get; private set; } = "";

    internal void Preview(string previewCode, string address)
    {
        Code = previewCode;
        AddressText = address;
    }

    public override async void OnEntered()
    {
        if (cts is not null)
            return;
        cts = CancellationTokenSource.CreateLinkedTokenSource(Flow.Cancellation.Token);
        try
        {
            Flow.Receiver = NetworkReceiver.Start(userLabel: UserProfiles.GetInteractiveAccountName());
            Code = Flow.Receiver.Code;
            AddressText = string.Join(" or ", Flow.Receiver.Addresses.Take(2));
            OnPropertyChanged(nameof(AddressText));
            Flow.Receiver.CodeChanged += newCode => OnUi(() =>
            {
                Code = newCode;
                Status = "Someone typed a wrong code, so it has changed. Use the new code below.";
            });

            var session = await Task.Run(() => Flow.Receiver.WaitForSenderAsync(cts.Token));
            Flow.ReceiverSession = session;
            IsConnected = true;
            Status = "Waiting for the old PC to finish scanning…";
            var inventory = await Task.Run(() => session.ReceiveInventoryAsync(s => OnUi(() => Status = s), cts.Token));
            Flow.Inventory = inventory;
            // No more connections needed: stop listening and close the firewall opening.
            await Flow.Receiver.DisposeAsync();
            Flow.Receiver = null;
            Main.Navigate(new SelectViewModel(Main, SelectMode.Receive, inventory, Selection.Defaults(inventory)), remember: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Waiting for the old PC failed", ex);
            Error = ex.Message;
        }
    }

    protected override void Back()
    {
        cts?.Cancel();
        Main.GoHome();
    }
}

/// <summary>New PC, file: pick the migration file and unlock it.</summary>
public sealed class OpenFileViewModel : PageViewModel
{
    private string path;
    private string password = "";
    private bool needsPassword;
    private string? info;

    public OpenFileViewModel(MainViewModel main) : base(main)
    {
        path = FindLikelyFile() ?? "";
        BrowseCommand = new RelayCommand(Browse);
        if (path.Length > 0)
            _ = PeekAsync();
    }

    public override string Title => "Open the migration file";
    public override string Subtitle => "Plug in the drive with the file from your old PC (keep all its parts together if it was split).";
    public override string Step => "Open file";
    public override string NextText => "Open";
    public override bool CanGoNext => File.Exists(Path) && (!NeedsPassword || Password.Length > 0);

    public ICommand BrowseCommand { get; }

    public string Path
    {
        get => path;
        set
        {
            if (Set(ref path, value))
            {
                NeedsPassword = false;
                Info = null;
                Refresh();
                _ = PeekAsync();
            }
        }
    }

    public string Password
    {
        get => password;
        set
        {
            Set(ref password, value);
            Refresh();
        }
    }

    public bool NeedsPassword
    {
        get => needsPassword;
        private set => Set(ref needsPassword, value);
    }

    public string? Info
    {
        get => info;
        private set => Set(ref info, value);
    }

    /// <summary>Reads the plaintext header to say whose file it is and whether it needs a password.</summary>
    private async Task PeekAsync()
    {
        var file = Path;
        if (!File.Exists(file))
            return;
        try
        {
            await using var reader = await PackageReader.OpenAsync(PackageFiles.OpenRead(file), _ => null, ownsInput: true, default);
            SetInfo(reader.Header, file);
        }
        catch (PasswordRequiredException)
        {
            NeedsPassword = true;
            try
            {
                // The header itself is readable without the password.
                await using var stream = PackageFiles.OpenRead(file);
                var header = await ReadHeaderOnlyAsync(stream);
                if (header is not null)
                    SetInfo(header, file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Info = ex.Message;
        }
        Refresh();
    }

    /// <summary>Design preview: a sample file, without looking for or reading real ones.</summary>
    internal void Preview(string samplePath, string sampleInfo)
    {
        path = samplePath;
        Info = sampleInfo;
        NeedsPassword = true;
        OnPropertyChanged(nameof(Path));
    }

    private void SetInfo(PackageHeader header, string file) =>
        Info = $"From {header.SourceComputer}, saved {header.CreatedUtc.ToLocalTime():f} · {Format.Bytes(PackageFiles.TotalSize(file))}" +
               (header.Encryption is null ? " · not password protected" : " · password protected");

    private static async Task<PackageHeader?> ReadHeaderOnlyAsync(Stream stream)
    {
        var prefix = new byte[12];
        await stream.ReadExactlyAsync(prefix);
        var length = BitConverter.ToInt32(prefix, 8);
        if (length is <= 0 or > PackageFormat.MaxHeaderLength)
            return null;
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes);
        return Json.FromBytes<PackageHeader>(bytes);
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a migration file",
            Filter = "Ferry migration file (*.ferry)|*.ferry",
            InitialDirectory = System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null,
        };
        if (dialog.ShowDialog() == true)
            Path = dialog.FileName;
    }

    protected override async Task NextAsync()
    {
        try
        {
            var (_, inventory, _) = await Task.Run(() => PackageReader.ReadManifestAsync(Path, NeedsPassword ? Password : null, Flow.Cancellation.Token));
            Flow.Inventory = inventory;
            Flow.PackagePath = Path;
            Flow.Password = NeedsPassword ? Password : null;
            Main.Navigate(new SelectViewModel(Main, SelectMode.Restore, inventory, Selection.All(inventory)));
        }
        catch (PasswordRequiredException)
        {
            NeedsPassword = true;
            Error = "This file is password protected. Enter the password it was saved with.";
        }
        catch (WrongPasswordException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>The newest .ferry file in a "Ferry" folder (or the root) of any removable or fixed data drive.</summary>
    private static string? FindLikelyFile()
    {
        var candidates = new List<FileInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Removable or DriveType.Fixed))
                    continue;
                foreach (var dir in new[] { drive.RootDirectory.FullName, System.IO.Path.Combine(drive.RootDirectory.FullName, "Ferry") })
                {
                    if (Directory.Exists(dir))
                        candidates.AddRange(new DirectoryInfo(dir).EnumerateFiles("*" + PackageFormat.FileExtension));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        var documents = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ferry");
        if (Directory.Exists(documents))
            candidates.AddRange(new DirectoryInfo(documents).EnumerateFiles("*" + PackageFormat.FileExtension));
        return candidates.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }
}

public sealed class TargetOption(UserProfileInfo? user)
{
    public UserProfileInfo? User { get; } = user;
    public string Display => User is null ? "Don't move this person's things" : User.IsInteractiveUser ? $"{User.AccountName} (signed in now)" : User.AccountName;
}

public sealed class UserMappingRow : Observable
{
    private TargetOption selected;

    public UserMappingRow(UserProfileInfo source, int itemCount, IReadOnlyList<TargetOption> options, TargetOption selected)
    {
        Source = source;
        ItemCount = itemCount;
        Options = options;
        this.selected = selected;
    }

    public UserProfileInfo Source { get; }
    public int ItemCount { get; }
    public string SourceText => $"{Source.AccountName} on the old PC";
    public string CountText => Format.Count(ItemCount, "item");
    public IReadOnlyList<TargetOption> Options { get; }

    public TargetOption Selected
    {
        get => selected;
        set => Set(ref selected, value);
    }
}

public sealed class ConflictOption(ConflictPolicy policy, string title, string description, bool selected = false) : Observable
{
    private bool isSelected = selected;

    public ConflictPolicy Policy { get; } = policy;
    public string Title { get; } = title;
    public string Description { get; } = description;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }
}

public sealed class FolderMappingRow(string source, string target) : Observable
{
    private string target = target;

    public string Source { get; } = source;

    public string Target
    {
        get => target;
        set => Set(ref target, value);
    }
}

/// <summary>New PC: who gets whose data, what to do with existing files, and safety options.</summary>
public sealed class RestoreOptionsViewModel : PageViewModel
{
    private bool createRestorePoint = ThisPc.IsAdministrator();
    private bool closeApps;
    private bool dryRun;

    public RestoreOptionsViewModel(MainViewModel main) : base(main)
    {
        var inventory = Flow.Inventory!;
        var selection = Flow.Selection;
        Flow.TargetUsers = ThisPc.Users();
        var options = new List<TargetOption> { new(null) };
        options.AddRange(Flow.TargetUsers.Select(u => new TargetOption(u)));

        foreach (var user in inventory.Users)
        {
            var count = inventory.Items.Count(i => i.UserSid == user.Sid && selection.Contains(i.Id));
            if (count == 0)
                continue;
            var suggested = UserProfiles.SuggestMapping(user, Flow.TargetUsers);
            var option = options.FirstOrDefault(o => o.User is not null && o.User.Sid == suggested?.Sid) ?? options[0];
            Users.Add(new UserMappingRow(user, count, options, option));
        }

        Conflicts =
        [
            new(ConflictPolicy.KeepBoth, "Keep both", $"Keep the file on this PC and save the incoming one next to it as \"name (from {inventory.Machine.ComputerName})\". Nothing is lost.", selected: true),
            new(ConflictPolicy.OverwriteIfNewer, "Keep the newer one", "Replace a file here only if the old PC's copy was changed more recently."),
            new(ConflictPolicy.Overwrite, "Replace with the old PC's", "Always use the old PC's file. The one here is kept for undo."),
            new(ConflictPolicy.Skip, "Keep this PC's", "Leave files that already exist here alone."),
        ];

        var drives = ThisPc.Machine().Drives;
        foreach (var item in inventory.Items.Where(i => i.MigratorId == "folders" && selection.Contains(i.Id)))
        {
            var source = item.Prop("path")!;
            Folders.Add(new FolderMappingRow(source, FoldersMigrator.DefaultTarget(source, drives, inventory.Machine.ComputerName)));
        }
    }

    public override string Title => "How should things be set up here?";
    public override string Subtitle => "Choose who gets each person's files and settings, and what happens when a file is already on this PC.";
    public override string Step => "Options";

    public ObservableCollection<UserMappingRow> Users { get; } = [];
    public IReadOnlyList<ConflictOption> Conflicts { get; }
    public ObservableCollection<FolderMappingRow> Folders { get; } = [];
    public bool HasUsers => Users.Count > 0;
    public bool HasFolders => Folders.Count > 0;
    public bool IsAdmin => ThisPc.IsAdministrator();

    public ConflictOption Conflict => Conflicts.FirstOrDefault(c => c.IsSelected) ?? Conflicts[0];

    public bool CreateRestorePoint
    {
        get => createRestorePoint;
        set => Set(ref createRestorePoint, value);
    }

    public bool CloseApps
    {
        get => closeApps;
        set => Set(ref closeApps, value);
    }

    public bool DryRun
    {
        get => dryRun;
        set => Set(ref dryRun, value);
    }

    protected override Task NextAsync()
    {
        var options = new RestoreOptions
        {
            Conflicts = Conflict.Policy,
            CreateRestorePoint = CreateRestorePoint,
            CloseRunningApps = CloseApps,
            DryRun = DryRun,
        };
        foreach (var row in Users.Where(r => r.Selected.User is not null))
            options.UserMap[row.Source.Sid] = row.Selected.User!.Sid;
        foreach (var row in Folders.Where(r => !string.Equals(r.Source, r.Target, StringComparison.OrdinalIgnoreCase)))
            options.FolderMap[row.Source] = row.Target.Trim();
        Flow.RestoreOptions = options;
        Main.Navigate(new SummaryViewModel(Main));
        return Task.CompletedTask;
    }
}

public sealed record SummaryLine(string Name, string Count, string Size);

/// <summary>New PC: last look before starting, with anything that would get in the way.</summary>
public sealed class SummaryViewModel : PageViewModel
{
    private IReadOnlyList<PreflightIssue> issues = [];
    private IReadOnlyList<string> runningApps = [];

    public SummaryViewModel(MainViewModel main) : base(main)
    {
        var inventory = Flow.Inventory!;
        var selection = Flow.Selection;
        var selected = inventory.Items.Where(i => selection.Contains(i.Id)).ToList();
        Lines = selected.GroupBy(i => i.Category).OrderBy(g => g.Key)
            .Select(g => new SummaryLine(Categories.DisplayName(g.Key), Format.Count(g.Count(), "item"), Format.Bytes(g.Sum(i => Preflight.EffectiveSize(i, selection)))))
            .ToList();
        Total = $"{Format.Count(selected.Count, "item")} · {Format.Bytes(selected.Sum(i => Preflight.EffectiveSize(i, selection)))}";
        Mappings = inventory.Users
            .Where(u => Flow.RestoreOptions.UserMap.ContainsKey(u.Sid))
            .Select(u => $"{u.AccountName} → {Flow.TargetUsers.FirstOrDefault(t => t.Sid == Flow.RestoreOptions.UserMap[u.Sid])?.AccountName}")
            .ToList();
        Apps = selected.Count(i => i.MigratorId == "apps" && selection.ModeFor(i) == ItemModes.Winget);
        CloseAppsCommand = new AsyncCommand(CloseAppsAsync, () => RunningApps.Count > 0);
        Check();
    }

    public override string Title => Flow.RestoreOptions.DryRun ? "Ready for a dry run" : "Ready to move";
    public override string Subtitle => Flow.RestoreOptions.DryRun
        ? "Nothing will be changed: Ferry goes through everything and reports what it would do."
        : $"From {Flow.SourceName} to this PC. Every change is recorded so it can be undone.";
    public override string Step => "Review";
    public override string NextText => Flow.RestoreOptions.DryRun ? "Start dry run" : "Start moving";
    public override bool CanGoNext => !Issues.Any(i => i.Blocking);

    public IReadOnlyList<SummaryLine> Lines { get; }
    public string Total { get; }
    public IReadOnlyList<string> Mappings { get; }
    public int Apps { get; }
    public string? AppsText => Apps > 0 ? $"{Format.Count(Apps, "program")} will be downloaded and installed with winget while your files copy." : null;
    public ICommand CloseAppsCommand { get; }

    public IReadOnlyList<PreflightIssue> Issues
    {
        get => issues;
        private set => Set(ref issues, value);
    }

    public IReadOnlyList<string> RunningApps
    {
        get => runningApps;
        private set => Set(ref runningApps, value);
    }

    /// <summary>Design preview: sample checks instead of this PC's disk space, admin rights and open programs.</summary>
    internal void Preview(IReadOnlyList<PreflightIssue> sampleIssues, IReadOnlyList<string> sampleRunningApps)
    {
        Issues = sampleIssues;
        RunningApps = sampleRunningApps;
        Refresh();
    }

    private void Check()
    {
        var selected = Flow.Inventory!.Items.Where(i => Flow.Selection.Contains(i.Id));
        Issues = Preflight.ForRestore(Flow.Inventory!, Flow.Selection, Flow.RestoreOptions, Flow.TargetUsers);
        RunningApps = Preflight.BlockingProcesses(selected);
        Refresh();
    }

    private async Task CloseAppsAsync()
    {
        var names = string.Join(", ", RunningApps.Select(p => Ferry.Core.Infrastructure.RunningApps.Known.GetValueOrDefault(p, p)));
        if (!MainViewModel.Confirm($"Close {names} now? Save anything you're working on in them first."))
            return;
        foreach (var process in RunningApps)
            await Ferry.Core.Infrastructure.RunningApps.CloseAsync(process, default);
        Check();
    }

    protected override Task NextAsync()
    {
        var engine = Flow.Engine;
        var inventory = Flow.Inventory!;
        var selection = Flow.Selection;
        var options = Flow.RestoreOptions;
        var title = options.DryRun ? "Checking what would move" : "Moving your stuff";
        var subtitle = options.DryRun ? "Nothing is being changed." : "Keep this PC on. You can use it meanwhile, but leave the apps being moved closed.";

        if (Flow.ReceiverSession is { } session)
        {
            var capture = Flow.CaptureOptions;
            Main.Navigate(new RunViewModel(Main, title, subtitle, "Move",
                async (progress, ct) => (await session.RestoreAsync(engine, selection, capture, options, progress, ct), null)), remember: false);
        }
        else
        {
            var path = Flow.PackagePath!;
            var password = Flow.Password;
            Main.Navigate(new RunViewModel(Main, title, subtitle, "Move", async (progress, ct) =>
            {
                await using var reader = await PackageReader.OpenAsync(PackageFiles.OpenRead(path), _ => password, ownsInput: true, ct);
                await reader.ReadManifestAsync(ct);
                var report = await engine.RestoreAsync(reader, inventory, selection, options, progress, ct);
                report.PackagePath = path;
                return (report, null);
            }), remember: false);
        }
        return Task.CompletedTask;
    }
}
