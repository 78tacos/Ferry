using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Ferry.App.Mvvm;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Transport;

namespace Ferry.App.ViewModels;

public enum FlowKind { None, SendNetwork, SendFile, ReceiveNetwork, ReceiveFile, Undo }

/// <summary>Everything one run through the wizard collects, from the first choice to the report.</summary>
public sealed class Flow : IAsyncDisposable
{
    public FlowKind Kind { get; set; }
    public MigrationEngine Engine { get; } = new();
    public Inventory? Inventory { get; set; }
    public Selection Selection { get; set; } = new();
    public ScanOptions ScanOptions { get; } = new();
    public CaptureOptions CaptureOptions { get; } = new();
    public RestoreOptions RestoreOptions { get; set; } = new();
    public List<UserProfileInfo> TargetUsers { get; set; } = [];
    public string? PackagePath { get; set; }
    public string? Password { get; set; }
    public long? PartSize { get; set; }
    public NetworkReceiver? Receiver { get; set; }
    public ReceiverSession? ReceiverSession { get; set; }
    public SenderSession? SenderSession { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public string SourceName => Inventory?.Machine.ComputerName ?? "your old PC";

    public static string[] StepsFor(FlowKind kind) => kind switch
    {
        FlowKind.SendNetwork => ["Start", "Connect", "Scan", "Choose on new PC", "Move", "Done"],
        FlowKind.SendFile => ["Start", "Scan", "Choose", "Save", "Move", "Done"],
        FlowKind.ReceiveNetwork => ["Start", "Connect", "Choose", "Options", "Review", "Move", "Done"],
        FlowKind.ReceiveFile => ["Start", "Open file", "Choose", "Options", "Review", "Move", "Done"],
        FlowKind.Undo => ["Start", "Choose", "Undo", "Done"],
        _ => ["Start"],
    };

    public async ValueTask DisposeAsync()
    {
        await Cancellation.CancelAsync();
        if (SenderSession is not null)
            await SenderSession.DisposeAsync();
        if (ReceiverSession is not null)
            await ReceiverSession.DisposeAsync();
        if (Receiver is not null)
            await Receiver.DisposeAsync();
        Cancellation.Dispose();
    }
}

public enum StepState { Done, Current, Upcoming }

public sealed class StepViewModel(string name, StepState state)
{
    public string Name { get; } = name;
    public StepState State { get; } = state;
    public bool IsDone => State == StepState.Done;
    public bool IsCurrent => State == StepState.Current;
}

public sealed class MainViewModel : Observable
{
    private readonly Stack<PageViewModel> history = new();
    private PageViewModel current;

    public MainViewModel()
    {
        Flow = new Flow();
        current = new HomeViewModel(this);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        OpenLogCommand = new RelayCommand(() => Shell.Open(Log.FilePath));
        current.OnEntered();
        UpdateSteps();
    }

    public Flow Flow { get; private set; }

    public PageViewModel Current
    {
        get => current;
        private set => Set(ref current, value);
    }

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    public bool IsAdmin => ThisPc.IsAdministrator();
    public string ComputerName => ThisPc.Name();
    public string Version => $"Version {SystemInfo.AppVersion}";
    public ICommand RestartAsAdminCommand { get; }
    public ICommand OpenLogCommand { get; }

    public void Navigate(PageViewModel page, bool remember = true)
    {
        current.OnLeaving();
        if (remember)
            history.Push(current);
        Current = page;
        page.OnEntered();
        UpdateSteps();
    }

    public void GoBack()
    {
        if (history.Count == 0)
            return;
        current.OnLeaving();
        Current = history.Pop();
        Current.OnEntered();
        UpdateSteps();
    }

    public async void GoHome()
    {
        current.OnLeaving();
        history.Clear();
        var old = Flow;
        Flow = new Flow();
        Current = new HomeViewModel(this);
        Current.OnEntered();
        UpdateSteps();
        try
        {
            await old.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug($"Closing the previous migration: {ex.Message}");
        }
    }

    /// <summary>Shows a page without running its OnEntered work (network, scans); used by <see cref="DesignPreview"/>.</summary>
    internal void Preview(PageViewModel page)
    {
        Current = page;
        UpdateSteps();
    }

    public void StartFlow(FlowKind kind)
    {
        Flow.Kind = kind;
        UpdateSteps();
    }

    public void UpdateSteps()
    {
        Steps.Clear();
        var names = Flow.StepsFor(Flow.Kind);
        var index = Array.IndexOf(names, Current.Step);
        for (var i = 0; i < names.Length; i++)
            Steps.Add(new StepViewModel(names[i], i < index ? StepState.Done : i == index ? StepState.Current : StepState.Upcoming));
    }

    public static void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "Ferry", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void ShowInfo(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "Ferry", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message, string title = "Ferry") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Asks before closing the window in the middle of a migration.</summary>
    public bool ConfirmClose() => !Current.IsWorking || Confirm("A migration is in progress. Stop it and close Ferry?\n\nChanges made so far can be undone later.", "Stop the migration?");

    private void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt was declined; stay as we are.
        }
    }
}

/// <summary>Base for wizard pages: title, which sidebar step it is, and the Back / Next buttons.</summary>
public abstract class PageViewModel : Observable
{
    private bool isBusy;
    private string? error;

    protected PageViewModel(MainViewModel main)
    {
        Main = main;
        NextCommand = new AsyncCommand(RunNextAsync, () => CanGoNext && !IsBusy);
        BackCommand = new RelayCommand(Back, () => CanGoBack && !IsBusy);
    }

    protected MainViewModel Main { get; }
    protected Flow Flow => Main.Flow;

    public abstract string Title { get; }
    public virtual string? Subtitle => null;
    public virtual string Step => "Start";
    public virtual string NextText => "Next";
    public virtual bool ShowNext => true;
    public virtual bool ShowBack => true;
    public virtual bool ShowCancel => false;
    public virtual bool CanGoNext => true;
    public virtual bool CanGoBack => true;

    /// <summary>True while a migration is actually running (closing the window needs confirming).</summary>
    public virtual bool IsWorking => false;

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public virtual ICommand? CancelCommand => null;

    public bool IsBusy
    {
        get => isBusy;
        protected set
        {
            if (Set(ref isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? Error
    {
        get => error;
        protected set => Set(ref error, value);
    }

    private async Task RunNextAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await NextAsync();
        }
        catch (OperationCanceledException)
        {
            // The user backed out.
        }
        catch (Exception ex)
        {
            Log.Error("Page action failed", ex);
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected virtual Task NextAsync() => Task.CompletedTask;

    protected virtual void Back() => Main.GoBack();

    public virtual void OnEntered() { }

    public virtual void OnLeaving() { }

    protected static void Refresh() => CommandManager.InvalidateRequerySuggested();

    protected static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}

internal static class Shell
{
    public static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainViewModel.ShowError($"Could not open {path}: {ex.Message}");
        }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainViewModel.ShowError($"Could not open the folder: {ex.Message}");
        }
    }
}
