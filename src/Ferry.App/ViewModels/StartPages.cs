using System.Linq;
using System.Windows.Input;
using Ferry.App.Mvvm;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Rollback;

namespace Ferry.App.ViewModels;

public sealed class HomeViewModel : PageViewModel
{
    public HomeViewModel(MainViewModel main) : base(main)
    {
        var machine = ThisPc.Machine();
        MachineText = $"{machine.ComputerName} · {machine.OsSummary} · {machine.JoinType}";
        OldPcCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.None);
            Main.Navigate(new SendMethodViewModel(Main));
        });
        NewPcCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.None);
            Main.Navigate(new ReceiveMethodViewModel(Main));
        });
        UndoCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.Undo);
            Main.Navigate(new UndoViewModel(Main));
        });
        ReportsCommand = new RelayCommand(() => Shell.Open(AppPaths.Reports));
        HasUndo = UndoEngine.List().Any(u => u.Status is not UndoStatus.Undone);
    }

    public override string Title => "Move to a new PC";
    public override string Subtitle => "Ferry moves your files, settings and programs from your old PC to your new one. Run it on both PCs.";
    public override bool ShowNext => false;
    public override bool ShowBack => false;

    public string MachineText { get; }
    public bool HasUndo { get; }
    public ICommand OldPcCommand { get; }
    public ICommand NewPcCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ReportsCommand { get; }
}

public sealed class SendMethodViewModel : PageViewModel
{
    public SendMethodViewModel(MainViewModel main) : base(main)
    {
        NetworkCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.SendNetwork);
            Main.Navigate(new ConnectViewModel(Main));
        });
        FileCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.SendFile);
            Main.Navigate(new ScanViewModel(Main));
        });
    }

    public override string Title => "How do you want to move your stuff?";
    public override string Subtitle => "This is your old PC. Everything stays here; Ferry only copies.";
    public override bool ShowNext => false;

    public ICommand NetworkCommand { get; }
    public ICommand FileCommand { get; }
}

public sealed class ReceiveMethodViewModel : PageViewModel
{
    public ReceiveMethodViewModel(MainViewModel main) : base(main)
    {
        NetworkCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.ReceiveNetwork);
            Main.Navigate(new WaitViewModel(Main));
        });
        FileCommand = new RelayCommand(() =>
        {
            Main.StartFlow(FlowKind.ReceiveFile);
            Main.Navigate(new OpenFileViewModel(Main));
        });
    }

    public override string Title => "How is your stuff coming over?";
    public override string Subtitle => "This is your new PC. Sign in as the person who'll use it before you start.";
    public override bool ShowNext => false;

    public ICommand NetworkCommand { get; }
    public ICommand FileCommand { get; }
}
