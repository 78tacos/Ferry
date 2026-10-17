using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ferry.App.Mvvm;
using Ferry.App.ViewModels;
using Ferry.Core.Infrastructure;

namespace Ferry.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize("app");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AsyncCommand.Failed += ex =>
        {
            Log.Error("Command failed", ex);
            MainViewModel.ShowError(ex.Message);
        };

        // Development aid: "Ferry.exe --render-screens <folder>" saves a PNG of every page in light and dark.
        if (e.Args.Length == 2 && e.Args[0] == "--render-screens")
        {
            DesignPreview.RenderAll(e.Args[1]);
            Shutdown();
            return;
        }

        var window = new MainWindow(new MainViewModel());
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MainViewModel.ShowError($"Something went wrong: {e.Exception.Message}\n\nDetails are in the log: {Log.FilePath}");
        e.Handled = true;
    }
}
