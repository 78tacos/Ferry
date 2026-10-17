using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Ferry.App.Mvvm;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Reporting;
using Ferry.Core.Rollback;

namespace Ferry.App.ViewModels;

public enum SelectMode
{
    /// <summary>Old PC choosing what goes into a migration file.</summary>
    Capture,
    /// <summary>New PC choosing from the old PC's live inventory.</summary>
    Receive,
    /// <summary>New PC choosing from what's in a migration file.</summary>
    Restore,
}

public sealed class SelectViewModel : PageViewModel
{
    private readonly SelectMode mode;
    private readonly Inventory inventory;
    private string filter = "";
    private string summary = "";

    public SelectViewModel(MainViewModel main, SelectMode mode, Inventory inventory, Selection initial) : base(main)
    {
        this.mode = mode;
        this.inventory = inventory;
        Tree = new SelectionTree(inventory, initial);
        Tree.Root.Changed += UpdateSummary;
        Warnings = inventory.Warnings;
        DefaultsCommand = new RelayCommand(() => Tree.Apply(i => i.SelectedByDefault));
        AllCommand = new RelayCommand(() => Tree.Apply(_ => true));
        NoneCommand = new RelayCommand(() => Tree.Apply(_ => false));
        AddFolderCommand = new AsyncCommand(AddFolderAsync);
        SavePlanCommand = new RelayCommand(SavePlan);
        UpdateSummary();
    }

    public override string Title => mode switch
    {
        SelectMode.Capture => "Choose what to move",
        SelectMode.Receive => $"Choose what to bring over from {inventory.Machine.ComputerName}",
        _ => $"Choose what to restore from {inventory.Machine.ComputerName}",
    };

    public override string Subtitle => mode == SelectMode.Capture
        ? "Ferry has ticked the usual things. Untick anything you don't need on the new PC."
        : "Untick anything you don't want on this PC. Programs are reinstalled fresh unless you choose otherwise.";

    public override string Step => "Choose";
    public override bool CanGoNext => Tree.Root.SelectedCount > 0;

    public SelectionTree Tree { get; }
    public IEnumerable<SelectionNode> Categories => Tree.Root.Children;
    public IReadOnlyList<string> Warnings { get; }
    public bool CanAddFolders => mode == SelectMode.Capture;

    public ICommand DefaultsCommand { get; }
    public ICommand AllCommand { get; }
    public ICommand NoneCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand SavePlanCommand { get; }

    public string Filter
    {
        get => filter;
        set
        {
            if (Set(ref filter, value))
                Tree.Root.ApplyFilter(value.Trim());
        }
    }

    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

    private void UpdateSummary()
    {
        var selection = Tree.ToSelection();
        var size = inventory.Items.Where(i => selection.Contains(i.Id)).Sum(i => Preflight.EffectiveSize(i, selection));
        Summary = $"{Format.Count(selection.ItemIds.Count, "item")} selected · {Format.Bytes(size)}";
        Refresh();
    }

    private async Task AddFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a folder to move", Multiselect = true };
        if (dialog.ShowDialog() != true)
            return;
        IsBusy = true;
        try
        {
            // Rescan just the folders migrator for the new paths and merge the results in.
            var options = new ScanOptions { ExtraFolders = [.. dialog.FolderNames], QueryWinget = false };
            Flow.ScanOptions.ExtraFolders.AddRange(dialog.FolderNames);
            var scan = await Task.Run(() => new MigrationEngine([new FoldersMigrator { IncludeDriveRoots = false }])
                .ScanAsync(options, null, Flow.Cancellation.Token));
            var current = Tree.ToSelection();
            foreach (var item in scan.Items)
            {
                // A folder found at a drive root earlier is the same item; just tick it.
                if (inventory.Find(item.Id) is null)
                    inventory.Items.Add(item);
                current.ItemIds.Add(item.Id);
            }
            Main.Navigate(new SelectViewModel(Main, mode, inventory, current), remember: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SavePlan()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save these choices for silent migrations",
            Filter = "Ferry plan (*.json)|*.json",
            FileName = "ferry-plan.json",
        };
        if (dialog.ShowDialog() != true)
            return;
        MigrationPlan.FromSelection(inventory, Tree.ToSelection(), Flow.RestoreOptions).Save(dialog.FileName);
        MainViewModel.ShowInfo($"Saved. Use it on other PCs with:\n\nferry-cli capture --plan \"{dialog.FileName}\" --out <file.ferry>\nferry-cli receive --plan \"{dialog.FileName}\"");
    }

    protected override Task NextAsync()
    {
        Flow.Selection = Tree.ToSelection();
        Flow.Inventory = inventory;
        if (mode == SelectMode.Capture)
            Main.Navigate(new SaveFileViewModel(Main));
        else
            Main.Navigate(new RestoreOptionsViewModel(Main));
        return Task.CompletedTask;
    }

    protected override void Back()
    {
        if (mode == SelectMode.Receive && !MainViewModel.Confirm("Going back disconnects from the old PC. Continue?"))
            return;
        if (mode == SelectMode.Receive)
        {
            Main.GoHome();
            return;
        }
        base.Back();
    }
}

/// <summary>Shows a long-running operation's progress, then moves on to the result page.</summary>
public abstract class ProgressViewModelBase : PageViewModel
{
    private readonly CancellationTokenSource cts;
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer;
    private bool started;
    private bool running;
    private MigrationProgress? last;
    private long rateBytes;
    private TimeSpan rateTime;
    private double bytesPerSecond;

    protected ProgressViewModelBase(MainViewModel main) : base(main)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(Flow.Cancellation.Token);
        CancelCommand = new RelayCommand(Cancel, () => running && !cts.IsCancellationRequested);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => OnPropertiesChanged(nameof(ElapsedText));
    }

    public override bool ShowNext => false;
    public override bool ShowBack => false;
    public override bool ShowCancel => true;
    public override bool IsWorking => running;
    public override ICommand CancelCommand { get; }

    public string Phase => last?.Phase ?? "Getting ready";
    public string? CurrentItem => last?.CurrentItem;
    public string? CurrentDetail => last?.CurrentDetail;
    public double Percent => (last?.Fraction ?? 0) * 100;
    public virtual bool IsIndeterminate => last?.Fraction is null;
    public string? BytesText => last is { BytesTotal: > 0 } p ? $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}" : null;
    public string? SpeedText => bytesPerSecond > 1024 ? $"{Format.Bytes((long)bytesPerSecond)}/s" : null;

    public string? EtaText
    {
        get
        {
            if (last is not { BytesTotal: > 0 } p || bytesPerSecond < 1024 || clock.Elapsed < TimeSpan.FromSeconds(5))
                return null;
            var seconds = (p.BytesTotal - p.BytesDone) / bytesPerSecond;
            return seconds is > 0 and < 360000 ? $"About {Format.Duration(TimeSpan.FromSeconds(seconds))} left" : null;
        }
    }

    public string ElapsedText => $"{Format.Duration(clock.Elapsed)} so far";

    protected abstract Task<(MigrationReport Report, MigrationReport? Remote)> RunAsync(IProgress<MigrationProgress> progress, CancellationToken ct);

    protected virtual string? ReportCopyFolder => null;

    internal void Preview(MigrationProgress progress)
    {
        bytesPerSecond = 41_000_000;
        clock.Start();
        Update(progress);
    }

    protected void ResetProgress()
    {
        last = null;
        bytesPerSecond = 0;
        OnPropertiesChanged(nameof(Phase), nameof(CurrentItem), nameof(CurrentDetail), nameof(Percent), nameof(IsIndeterminate), nameof(BytesText),
            nameof(SpeedText), nameof(EtaText));
    }

    public override async void OnEntered()
    {
        if (started)
            return;
        started = true;
        running = true;
        clock.Start();
        timer.Start();
        Refresh();
        var progress = new Progress<MigrationProgress>(Update);
        MigrationReport report;
        MigrationReport? remote = null;
        try
        {
            (report, remote) = await Task.Run(() => RunAsync(progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            report = new MigrationReport { Cancelled = true, FinishedUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            Log.Error("Migration step failed", ex);
            report = new MigrationReport { FatalError = ex.Message, FinishedUtc = DateTime.UtcNow, LogPath = Log.FilePath };
        }
        finally
        {
            running = false;
            timer.Stop();
            clock.Stop();
            Refresh();
        }

        string? path = null, remotePath = null;
        try
        {
            path = HtmlReport.Save(report, ReportCopyFolder);
            if (remote is not null)
                remotePath = HtmlReport.Save(remote);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the report: {ex.Message}");
        }
        Main.Navigate(new DoneViewModel(Main, report, path, remote, remotePath), remember: false);
    }

    private void Update(MigrationProgress p)
    {
        var now = clock.Elapsed;
        if (now - rateTime > TimeSpan.FromSeconds(1))
        {
            var instant = (p.BytesDone - rateBytes) / (now - rateTime).TotalSeconds;
            if (rateTime > TimeSpan.Zero && instant >= 0)
                bytesPerSecond = bytesPerSecond == 0 ? instant : bytesPerSecond * 0.75 + instant * 0.25;
            rateBytes = p.BytesDone;
            rateTime = now;
        }
        last = p;
        OnPropertiesChanged(nameof(Phase), nameof(CurrentItem), nameof(CurrentDetail), nameof(Percent), nameof(IsIndeterminate), nameof(BytesText),
            nameof(SpeedText), nameof(EtaText));
    }

    private void Cancel()
    {
        if (MainViewModel.Confirm("Stop now? Whatever was already moved stays, and on the new PC it can be undone later.", "Stop the migration?"))
        {
            cts.Cancel();
            Refresh();
        }
    }
}

/// <summary>Progress for an operation given as a delegate (save to file, restore, undo).</summary>
public sealed class RunViewModel(
    MainViewModel main,
    string title,
    string subtitle,
    string step,
    Func<IProgress<MigrationProgress>, CancellationToken, Task<(MigrationReport, MigrationReport?)>> run,
    string? reportCopyFolder = null) : ProgressViewModelBase(main)
{
    public override string Title => title;
    public override string? Subtitle => subtitle;
    public override string Step => step;
    protected override string? ReportCopyFolder => reportCopyFolder;

    protected override Task<(MigrationReport Report, MigrationReport? Remote)> RunAsync(IProgress<MigrationProgress> progress, CancellationToken ct) =>
        run(progress, ct);
}

public sealed record ProblemRow(string Name, string Group, string Message);

public sealed class DoneViewModel : PageViewModel
{
    private readonly MigrationReport report;
    private readonly string? reportPath;
    private readonly MigrationReport? remote;
    private readonly string? remotePath;

    public DoneViewModel(MainViewModel main, MigrationReport report, string? reportPath, MigrationReport? remote, string? remotePath) : base(main)
    {
        this.report = report;
        this.reportPath = reportPath;
        this.remote = remote;
        this.remotePath = remotePath;
        var shown = remote ?? report;
        ManualSteps = shown.ManualSteps;
        ProgramsNotMoved = shown.ProgramsNotMoved;
        Notes = [.. report.Notes, .. remote?.Notes ?? []];
        Problems = shown.Items.Where(i => i.Status == ItemStatus.Failed)
            .Select(i => new ProblemRow(i.Name, i.Group, i.Messages.LastOrDefault(m => m.Level == MessageLevel.Error)?.Text ?? "Failed"))
            .ToList();
        OpenReportCommand = new RelayCommand(() => Shell.Open(reportPath), () => reportPath is not null);
        OpenRemoteReportCommand = new RelayCommand(() => Shell.Open(remotePath), () => remotePath is not null);
        ShowFileCommand = new RelayCommand(() => Shell.ShowInExplorer(report.PackagePath!), () => report.PackagePath is not null && File.Exists(report.PackagePath));
        UndoCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.Undo);
            Main.Navigate(new UndoViewModel(Main, report.UndoId));
        }, () => report.UndoId is not null && report.Kind == OperationKind.Restore);
        FinishCommand = new RelayCommand(Main.GoHome);
    }

    public override string Title => report.Cancelled ? "Stopped"
        : report.FatalError is not null ? "The migration stopped"
        : (remote ?? report).HasFailures ? "Done, but some things couldn't be moved"
        : report.Kind == OperationKind.Undo ? "Undone"
        : report.DryRun ? "Dry run finished"
        : "All done";

    public override string? Subtitle
    {
        get
        {
            if (report.Cancelled)
                return report.Kind == OperationKind.Restore ? "What was moved so far stays, and can be undone." : "Nothing more was copied.";
            if (report.FatalError is not null)
                return report.FatalError;
            var shown = remote ?? report;
            if (report.Kind == OperationKind.Restore && shown.TotalFiles == 0 && shown.TotalSkipped > 0)
                return report.DryRun
                    ? $"Nothing was changed. Everything selected ({Format.Count(shown.TotalSkipped, "file")}) is already on this PC."
                    : $"Everything selected ({Format.Count(shown.TotalSkipped, "file")}) was already on this PC, so nothing needed copying.";
            var what = report.Kind == OperationKind.Undo
                ? $"{Format.Count(shown.TotalFiles, "change")} reversed"
                : $"{Format.Count(shown.TotalFiles, "file")} and settings, {Format.Bytes(shown.TotalBytes)}";
            return report.Kind switch
            {
                OperationKind.Capture when remote is null && report.PackagePath is not null =>
                    $"{what} saved to {Path.GetFileName(report.PackagePath)}. Take it to the new PC, open Ferry there and choose \"This is my new PC\" → \"From a migration file\".",
                OperationKind.Capture => $"{what} moved to {report.Target?.ComputerName}.",
                OperationKind.Restore when report.DryRun => $"Nothing was changed. {what} would be moved.",
                OperationKind.Restore => $"{what} moved from {report.Source?.ComputerName}. Sign out and back in to see everything.",
                _ => what,
            };
        }
    }

    public override string Step => "Done";
    public override bool ShowNext => false;
    public override bool ShowBack => false;

    public bool IsGood => !report.Cancelled && report.FatalError is null && !(remote ?? report).HasFailures;
    public IReadOnlyList<string> ManualSteps { get; }
    public IReadOnlyList<string> ProgramsNotMoved { get; }
    public string ProgramsNotMovedTitle => $"Programs that weren't reinstalled ({ProgramsNotMoved.Count})";
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyList<ProblemRow> Problems { get; }
    public bool HasRemoteReport => remotePath is not null;
    public bool CanUndo => report.UndoId is not null && report.Kind == OperationKind.Restore;
    public bool HasFile => report.PackagePath is not null && File.Exists(report.PackagePath);

    public ICommand OpenReportCommand { get; }
    public ICommand OpenRemoteReportCommand { get; }
    public ICommand ShowFileCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand FinishCommand { get; }
}

public sealed class UndoRow(UndoInfo info)
{
    public UndoInfo Info { get; } = info;
    public string Title => Info.Title;
    public string Details => $"{Format.Count(Info.ChangeCount, "change")} · for {string.Join(", ", Info.TargetUsers)} · {StatusText}";
    public bool CanUndo => Info.Status is not UndoStatus.Undone;

    private string StatusText => Info.Status switch
    {
        UndoStatus.Completed => "finished",
        UndoStatus.Cancelled => "stopped part-way",
        UndoStatus.Failed => "stopped by an error",
        UndoStatus.InProgress => "didn't finish",
        UndoStatus.Undone => $"undone {Info.UndoneUtc?.ToLocalTime():g}",
        UndoStatus.PartiallyUndone => "partly undone",
        _ => Info.Status.ToString(),
    };
}

public sealed class UndoViewModel : PageViewModel
{
    private UndoRow? selected;

    public UndoViewModel(MainViewModel main, string? preselect = null) : base(main)
    {
        DiscardCommand = new RelayCommand(Discard, () => selected is not null);
        Load(preselect);
    }

    public override string Title => "Undo a migration";
    public override string Subtitle => "Puts this PC back the way it was before: files Ferry added are removed, and replaced files and settings come back.";
    public override string Step => "Choose";
    public override string NextText => "Undo";
    public override bool CanGoNext => selected?.CanUndo == true;

    public ObservableCollection<UndoRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public ICommand DiscardCommand { get; }

    public UndoRow? Selected
    {
        get => selected;
        set
        {
            Set(ref selected, value);
            Refresh();
        }
    }

    /// <summary>Design preview: sample migrations instead of this PC's undo data.</summary>
    internal void Preview(IEnumerable<UndoInfo> sample)
    {
        Rows.Clear();
        foreach (var info in sample)
            Rows.Add(new UndoRow(info));
        Selected = Rows.FirstOrDefault(r => r.CanUndo);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Load(string? preselect)
    {
        Rows.Clear();
        foreach (var info in UndoEngine.List())
            Rows.Add(new UndoRow(info));
        Selected = Rows.FirstOrDefault(r => r.Info.Id == preselect) ?? Rows.FirstOrDefault(r => r.CanUndo);
        OnPropertyChanged(nameof(IsEmpty));
    }

    protected override Task NextAsync()
    {
        var row = selected!;
        if (!MainViewModel.Confirm($"Undo the migration {row.Title.ToLowerInvariant()}?\n\nFiles it added are deleted, including any changes you've made to them since. " +
                                   "Programs it installed are uninstalled.", "Undo the migration?"))
            return Task.CompletedTask;
        Main.Navigate(new RunViewModel(Main, "Undoing the migration", "Reversing each change, newest first.", "Undo",
            async (progress, ct) => (await UndoEngine.UndoAsync(row.Info.Id, progress, ct), null)), remember: false);
        return Task.CompletedTask;
    }

    private void Discard()
    {
        var row = selected!;
        if (!MainViewModel.Confirm($"Delete the undo data for the migration {row.Title.ToLowerInvariant()}? It frees up space, but that migration can no longer be undone."))
            return;
        UndoEngine.Discard(row.Info.Id);
        Load(null);
    }
}
