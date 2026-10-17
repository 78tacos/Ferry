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
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Reporting;
using Ferry.Core.Transport;

namespace Ferry.App.ViewModels;

/// <summary>Old PC, network: pick the waiting new PC (or type its name) and enter the code it shows.</summary>
public sealed class ConnectViewModel : PageViewModel
{
    private DiscoveryClient? discovery;
    private DiscoveredPeer? selectedPeer;
    private string address = "";
    private string code = "";

    public ConnectViewModel(MainViewModel main) : base(main)
    {
    }

    public override string Title => "Connect to your new PC";
    public override string Subtitle => "On the new PC, open Ferry and choose \"This is my new PC\", then \"Over the network\". It shows a pairing code.";
    public override string Step => "Connect";
    public override string NextText => IsBusy ? "Connecting…" : "Connect";
    public override bool CanGoNext => Address.Trim().Length > 0 && PairingCode.LooksValid(Code);

    public ObservableCollection<DiscoveredPeer> Peers { get; } = [];
    public bool NoPeers => Peers.Count == 0;

    public DiscoveredPeer? SelectedPeer
    {
        get => selectedPeer;
        set
        {
            if (Set(ref selectedPeer, value) && value is not null)
                Address = value.Address;
        }
    }

    public string Address
    {
        get => address;
        set
        {
            if (Set(ref address, value))
                Refresh();
        }
    }

    public string Code
    {
        get => code;
        set
        {
            if (Set(ref code, value))
                Refresh();
        }
    }

    public override void OnEntered()
    {
        try
        {
            discovery = new DiscoveryClient();
            discovery.PeersChanged += peers => OnUi(() =>
            {
                var current = selectedPeer?.Address;
                Peers.Clear();
                foreach (var peer in peers)
                    Peers.Add(peer);
                OnPropertyChanged(nameof(NoPeers));
                // One waiting PC is the common case: pick it.
                if (Peers.Count == 1 && string.IsNullOrEmpty(Address))
                    SelectedPeer = Peers[0];
                else if (current is not null)
                    selectedPeer = Peers.FirstOrDefault(p => p.Address == current);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Network discovery unavailable: {ex.Message}");
        }
    }

    public override void OnLeaving()
    {
        discovery?.Dispose();
        discovery = null;
    }

    protected override async Task NextAsync()
    {
        OnPropertyChanged(nameof(NextText));
        var (host, port) = ParseAddress(Address.Trim());
        if (selectedPeer is not null && selectedPeer.Address == host)
            port = selectedPeer.Port;
        try
        {
            var session = await SenderSession.ConnectAsync(host, port, Code, Flow.Cancellation.Token);
            Flow.SenderSession = session;
            Main.Navigate(new SenderProgressViewModel(Main, session), remember: false);
        }
        finally
        {
            OnPropertyChanged(nameof(NextText));
        }
    }

    private static (string Host, int Port) ParseAddress(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon > 0 && !text.Contains("::", StringComparison.Ordinal) && int.TryParse(text[(colon + 1)..], out var port))
            return (text[..colon], port);
        return (text, NetworkDefaults.TransferPort);
    }
}

/// <summary>Old PC, network: scans, waits while choices are made on the new PC, streams, then shows the result.</summary>
public sealed class SenderProgressViewModel : ProgressViewModelBase
{
    private readonly SenderSession session;
    private SenderStage stage = SenderStage.Scanning;

    public SenderProgressViewModel(MainViewModel main, SenderSession session) : base(main)
    {
        this.session = session;
        session.StageChanged += s => OnUi(() =>
        {
            stage = s;
            if (s != SenderStage.Sending)
                ResetProgress();
            OnPropertiesChanged(nameof(Title), nameof(Subtitle), nameof(Step), nameof(IsIndeterminate));
            Main.UpdateSteps();
        });
    }

    public override string Title => stage switch
    {
        SenderStage.Scanning => "Looking at this PC",
        SenderStage.WaitingForChoices => $"Choose what to move on {session.TargetMachine.ComputerName}",
        SenderStage.Sending => $"Moving your stuff to {session.TargetMachine.ComputerName}",
        SenderStage.WaitingForNewPc => "Finishing up on the new PC",
        _ => "Done",
    };

    public override string? Subtitle => stage switch
    {
        SenderStage.Scanning => "Finding your files, settings and programs. This can take a few minutes.",
        SenderStage.WaitingForChoices => "Your stuff is listed on the new PC. Pick what to bring over there; this PC waits.",
        SenderStage.Sending => "Keep both PCs on and connected. You can keep using this one.",
        SenderStage.WaitingForNewPc => "Everything has been sent. The new PC is reinstalling programs and finishing settings.",
        _ => null,
    };

    public override string Step => stage switch
    {
        SenderStage.Scanning => "Scan",
        SenderStage.WaitingForChoices => "Choose on new PC",
        SenderStage.Sending or SenderStage.WaitingForNewPc => "Move",
        _ => "Done",
    };

    public override bool IsIndeterminate => stage != SenderStage.Sending || base.IsIndeterminate;

    protected override async Task<(MigrationReport Report, MigrationReport? Remote)> RunAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
    {
        var (capture, restore) = await session.RunAsync(Flow.Engine, Flow.ScanOptions, progress, ct);
        return (capture, restore);
    }
}

/// <summary>Old PC, file: scans this PC, then moves on to choosing.</summary>
public sealed class ScanViewModel : PageViewModel
{
    private CancellationTokenSource? cts;
    private string status = "Starting…";

    public ScanViewModel(MainViewModel main) : base(main)
    {
    }

    public override string Title => "Looking at this PC";
    public override string Subtitle => "Finding your files, settings and programs. This can take a few minutes on a full PC.";
    public override string Step => "Scan";
    public override bool ShowNext => false;

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public override async void OnEntered()
    {
        if (cts is not null)
            return;
        cts = CancellationTokenSource.CreateLinkedTokenSource(Flow.Cancellation.Token);
        var progress = new Progress<MigrationProgress>(p => Status = p.CurrentDetail ?? p.Phase);
        try
        {
            var inventory = await Task.Run(() => Flow.Engine.ScanAsync(Flow.ScanOptions, progress, cts.Token));
            Flow.Inventory = inventory;
            Main.Navigate(new SelectViewModel(Main, SelectMode.Capture, inventory, Selection.Defaults(inventory)), remember: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
            Error = $"The scan failed: {ex.Message}";
        }
    }

    protected override void Back()
    {
        cts?.Cancel();
        base.Back();
    }
}

/// <summary>Old PC, file: where to save, and the password that protects it.</summary>
public sealed class SaveFileViewModel : PageViewModel
{
    private string path;
    private bool usePassword = true;
    private string password = "";
    private string confirm = "";
    private bool useShadowCopy = ThisPc.IsAdministrator();
    private IReadOnlyList<PreflightIssue> issues = [];

    /// <summary>Design preview: a sample destination, without checking this PC's drives or open programs.</summary>
    internal void Preview(string samplePath)
    {
        path = samplePath;
        OnPropertyChanged(nameof(Path));
        Issues = [];
        Refresh();
    }

    public SaveFileViewModel(MainViewModel main) : base(main)
    {
        path = DefaultPath(Flow.Inventory!.Machine.ComputerName);
        BrowseCommand = new RelayCommand(Browse);
        UpdateIssues();
    }

    public override string Title => "Where should the migration file go?";
    public override string Subtitle => "Pick a USB drive, external disk or network share you can take to the new PC.";
    public override string Step => "Save";
    public override string NextText => "Save";
    public override bool CanGoNext => Path.Trim().Length > 3 && (!UsePassword || (Password.Length >= 6 && Password == Confirm)) &&
                                      !Issues.Any(i => i.Blocking);

    public ICommand BrowseCommand { get; }

    public string Path
    {
        get => path;
        set
        {
            if (Set(ref path, value))
                UpdateIssues();
        }
    }

    public bool UsePassword
    {
        get => usePassword;
        set
        {
            if (Set(ref usePassword, value))
                UpdateIssues();
        }
    }

    public string Password
    {
        get => password;
        set
        {
            if (Set(ref password, value))
                OnPropertyChanged(nameof(PasswordHint));
            Refresh();
        }
    }

    public string Confirm
    {
        get => confirm;
        set
        {
            if (Set(ref confirm, value))
                OnPropertyChanged(nameof(PasswordHint));
            Refresh();
        }
    }

    public string? PasswordHint => !UsePassword ? null
        : Password.Length is > 0 and < 6 ? "Use at least 6 characters."
        : Confirm.Length > 0 && Confirm != Password ? "The passwords don't match."
        : null;

    public bool UseShadowCopy
    {
        get => useShadowCopy;
        set => Set(ref useShadowCopy, value);
    }

    public bool CanUseShadowCopy => ThisPc.IsAdministrator();

    public IReadOnlyList<PreflightIssue> Issues
    {
        get => issues;
        private set => Set(ref issues, value);
    }

    public string SizeText => $"About {Format.Bytes(Flow.Inventory!.Items.Where(i => Flow.Selection.Contains(i.Id)).Sum(i => Preflight.EffectiveSize(i, Flow.Selection)))} before compression.";

    private void UpdateIssues()
    {
        var full = SafeFullPath(Path);
        Issues = full is null ? [new PreflightIssue(true, "That isn't a valid file path.")] : Preflight.ForCapture(Flow.Inventory!, Flow.Selection, full, UsePassword);
        Refresh();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the migration file",
            Filter = "Ferry migration file (*.ferry)|*.ferry",
            FileName = System.IO.Path.GetFileName(Path),
            InitialDirectory = System.IO.Path.GetDirectoryName(Path) is { } dir && Directory.Exists(dir) ? dir : null,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() == true)
            Path = dialog.FileName;
    }

    protected override Task NextAsync()
    {
        var full = SafeFullPath(Path) ?? throw new InvalidOperationException("That isn't a valid file path.");
        if (!full.EndsWith(PackageFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            full += PackageFormat.FileExtension;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        Flow.PackagePath = full;
        Flow.Password = UsePassword ? Password : null;
        Flow.PartSize = PackageFiles.AutoPartSize(full);
        Flow.CaptureOptions.UseShadowCopy = UseShadowCopy;

        var inventory = Flow.Inventory!;
        var selection = Flow.Selection;
        var options = Flow.CaptureOptions;
        var password = Flow.Password;
        var partSize = Flow.PartSize;
        var engine = Flow.Engine;
        Main.Navigate(new RunViewModel(Main, "Saving your stuff", "Copying from this PC into the migration file.", "Move", async (progress, ct) =>
        {
            MigrationReport report;
            await using (var stream = PackageFiles.OpenWrite(full, partSize))
            {
                await using var writer = await PackageWriter.CreateAsync(stream, new PackageHeader
                {
                    SourceComputer = inventory.Machine.ComputerName,
                    AppVersion = SystemInfo.AppVersion,
                    SplitPartSize = partSize,
                }, password, ct);
                report = await engine.CaptureAsync(inventory, selection, options, writer, progress, ct);
            }
            report.PackagePath = full;
            if (report.Cancelled || report.FatalError is not null)
            {
                foreach (var part in PackageFiles.ExistingParts(full))
                    File.Delete(part);
            }
            return (report, null);
        }, reportCopyFolder: System.IO.Path.GetDirectoryName(full)), remember: false);
        return Task.CompletedTask;
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return path.Trim().Length == 0 ? null : System.IO.Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A removable drive if one is plugged in, otherwise Documents.</summary>
    private static string DefaultPath(string computer)
    {
        var name = $"{computer} {DateTime.Now:yyyy-MM-dd}{PackageFormat.FileExtension}";
        var removable = DriveInfo.GetDrives().FirstOrDefault(d =>
        {
            try
            {
                return d.IsReady && d.DriveType == DriveType.Removable && d.AvailableFreeSpace > 1L << 30;
            }
            catch (IOException)
            {
                return false;
            }
        });
        var folder = removable is not null
            ? System.IO.Path.Combine(removable.RootDirectory.FullName, "Ferry")
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ferry");
        return System.IO.Path.Combine(folder, name);
    }
}
