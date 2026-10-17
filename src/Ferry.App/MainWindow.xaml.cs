using System.ComponentModel;
using System.Windows;
using Ferry.App.ViewModels;

namespace Ferry.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.ConfirmClose())
            e.Cancel = true;
        base.OnClosing(e);
    }
}
