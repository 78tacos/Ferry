using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ferry.App.ViewModels;
using Ferry.Core.Engine;
using Ferry.Core.Model;
using Ferry.Core.Rollback;
using Ferry.Core.Transport;

namespace Ferry.App;

/// <summary>
/// Renders every page with sample data to PNG files, without touching the network or the system,
/// so layouts can be reviewed (and screenshots made for documentation) in light and dark themes.
/// </summary>
internal static class DesignPreview
{
    private const string OldSid = "S-1-5-21-1000";
    private const string NewSid = "S-1-12-1-2000";

    public static void RenderAll(string folder)
    {
        Directory.CreateDirectory(folder);
        // Render as a sample new PC, run as administrator the way a full migration should be.
        ThisPc.IsAdministrator = () => true;
        ThisPc.Name = () => "NEW-LAPTOP";
        ThisPc.Machine = () => new MachineInfo
        {
            ComputerName = "NEW-LAPTOP",
            OsName = "Windows 11 Pro",
            OsDisplayVersion = "25H2",
            JoinType = "Microsoft Entra ID",
            Drives = [new DriveSummary { Name = @"C:\", Format = "NTFS", TotalBytes = 1_000_000_000_000, FreeBytes = 820_000_000_000 }],
        };
        ThisPc.Users = () =>
        [
            new UserProfileInfo
            {
                Sid = NewSid,
                AccountName = @"AzureAD\AlexMorgan",
                UserName = "AlexMorgan",
                ProfilePath = @"C:\Users\AlexMorgan",
                Kind = AccountKind.EntraId,
                IsInteractiveUser = true,
            },
        ];
        var main = new MainViewModel();
        var window = new MainWindow(main)
        {
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();

        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            Application.Current.ThemeMode = theme;
            foreach (var (name, flow, page) in Pages(main))
            {
                main.Flow.Kind = flow;
                main.Preview(page());
                Render(window, Path.Combine(folder, $"{name}-{theme.Value.ToLowerInvariant()}.png"));
            }
        }
        window.Close();
    }

    private static IEnumerable<(string Name, FlowKind Flow, Func<PageViewModel> Page)> Pages(MainViewModel main)
    {
        var inventory = SampleInventory();
        main.Flow.Inventory = inventory;
        main.Flow.Selection = Selection.Defaults(inventory);

        yield return ("01-home", FlowKind.None, () => new HomeViewModel(main));
        yield return ("02-send-method", FlowKind.None, () => new SendMethodViewModel(main));
        yield return ("03-connect", FlowKind.SendNetwork, () =>
        {
            var connect = new ConnectViewModel(main) { Code = "K7QMX-2XRP9" };
            connect.Peers.Add(new DiscoveredPeer("NEW-LAPTOP", "192.168.1.42", NetworkDefaults.TransferPort, "Windows 11 Pro 25H2", @"AzureAD\AlexMorgan", DateTime.UtcNow));
            connect.SelectedPeer = connect.Peers[0];
            return connect;
        });
        yield return ("04-wait", FlowKind.ReceiveNetwork, () =>
        {
            var wait = new WaitViewModel(main);
            wait.Preview("K7QMX-2XRP9", "192.168.1.42");
            return wait;
        });
        yield return ("05-select", FlowKind.ReceiveNetwork, () => new SelectViewModel(main, SelectMode.Receive, inventory, Selection.Defaults(inventory)));
        yield return ("05b-select-search", FlowKind.SendFile, () =>
            new SelectViewModel(main, SelectMode.Capture, inventory, Selection.Defaults(inventory)) { Filter = "Zo" });
        yield return ("06-save-file", FlowKind.SendFile, () =>
        {
            var save = new SaveFileViewModel(main);
            save.Preview(@"E:\OLD-LAPTOP 2026-10-17.ferry");
            return save;
        });
        yield return ("06b-open-file", FlowKind.ReceiveFile, () =>
        {
            var open = new OpenFileViewModel(main);
            open.Preview(@"E:\OLD-LAPTOP 2026-10-17.ferry", "From OLD-LAPTOP, saved Friday, October 17, 2026 9:14 AM · 38.2 GB · password protected");
            return open;
        });
        yield return ("07-options", FlowKind.ReceiveNetwork, () => new RestoreOptionsViewModel(main));
        yield return ("08-summary", FlowKind.ReceiveNetwork, () =>
        {
            main.Flow.RestoreOptions = new RestoreOptions { UserMap = { [OldSid] = NewSid }, CreateRestorePoint = true };
            var summary = new SummaryViewModel(main);
            summary.Preview([new PreflightIssue(false, "Close Google Chrome before starting, or its data can't be replaced.")], ["chrome"]);
            return summary;
        });
        yield return ("09-progress", FlowKind.ReceiveNetwork, () =>
        {
            var run = new RunViewModel(main, "Moving your stuff", "Keep this PC on. You can use it meanwhile.", "Move", (_, _) => throw new InvalidOperationException());
            run.Preview(new MigrationProgress
            {
                Phase = "Moving your stuff",
                CurrentItem = "Documents",
                CurrentDetail = @"C:\Users\alex\Documents\Projects\Budget 2026.xlsx",
                BytesDone = 18_400_000_000,
                BytesTotal = 41_000_000_000,
            });
            return run;
        });
        yield return ("10-done", FlowKind.ReceiveNetwork, () => new DoneViewModel(main, SampleReport(), "report.html", null, null));
        yield return ("11-undo", FlowKind.Undo, () =>
        {
            var undo = new UndoViewModel(main);
            undo.Preview(
            [
                new UndoInfo
                {
                    Id = "20261017-101500-a1b2", SourceComputer = "OLD-LAPTOP", TargetComputer = "NEW-LAPTOP", CreatedUtc = DateTime.UtcNow.AddHours(-2),
                    TargetUsers = [@"AzureAD\AlexMorgan"], Status = UndoStatus.Completed, ChangeCount = 11_342, BackupBytes = 48_000_000,
                },
            ]);
            return undo;
        });
    }

    private static void Render(Window window, string path)
    {
        Pump();
        window.UpdateLayout();
        Pump();
        var root = (FrameworkElement)window.Content;
        var width = (int)root.ActualWidth;
        var height = (int)root.ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Mica doesn't render off-screen; paint the theme's base background first, then the page over it.
            var background = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static Inventory SampleInventory()
    {
        const string sid = "S-1-5-21-1000";
        var inventory = new Inventory
        {
            Machine = new MachineInfo { ComputerName = "OLD-LAPTOP", OsName = "Windows 10 Pro", OsDisplayVersion = "22H2", JoinType = "Workgroup" },
            Users = [new UserProfileInfo { Sid = sid, AccountName = @"OLD-LAPTOP\alex", UserName = "alex", ProfilePath = @"C:\Users\alex" }],
        };
        void Add(string migrator, MigrationCategory category, string group, string name, long size, string? description = null, bool selected = true,
            string? user = sid, string[]? modes = null, string? warning = null)
        {
            var item = new InventoryItem
            {
                Id = $"{migrator}:{group}:{name}",
                MigratorId = migrator,
                Category = category,
                Group = group,
                Name = name,
                Description = description,
                SizeBytes = size,
                SelectedByDefault = selected,
                UserSid = user,
            };
            if (modes is not null)
            {
                item.AvailableModes = [.. modes];
                item.DefaultMode = modes[0];
            }
            if (warning is not null)
                item.Warnings.Add(warning);
            inventory.Items.Add(item);
        }

        Add("wifi", MigrationCategory.WindowsSettings, "Wi-Fi networks", "Home-5G", 1400, "WPA2-Personal", user: null);
        Add("wifi", MigrationCategory.WindowsSettings, "Wi-Fi networks", "Office Guest", 1400, "WPA3-Personal", user: null);
        Add("printers", MigrationCategory.WindowsSettings, "Printers", "HP LaserJet M404 (Office)", 0, "Network printer at 192.168.1.20", user: null);
        Add("personalization", MigrationCategory.WindowsSettings, @"OLD-LAPTOP\alex", "Desktop background", 2_100_000, "Your wallpaper picture");
        Add("personalization", MigrationCategory.WindowsSettings, @"OLD-LAPTOP\alex", "Colors and theme", 4096, "Light or dark mode, accent color");
        Add("apps", MigrationCategory.Applications, "Programs", "Google Chrome", 0, "version 141.0 · Google LLC", modes: ["winget"]);
        Add("apps", MigrationCategory.Applications, "Programs", "Zoom Workplace", 0, "version 6.6 · Zoom Communications", modes: ["winget", "copy"]);
        Add("apps", MigrationCategory.Applications, "Programs", "Old Accounting 2014", 0, "version 14.2 · Contoso", selected: false, modes: ["copy"],
            warning: "Not available from winget. Reinstall it yourself, or tick it to copy its program files (experimental).");
        Add("appdata", MigrationCategory.AppSettings, @"OLD-LAPTOP\alex", "Zoom (app data)", 12_000_000);
        Add("browsers", MigrationCategory.BrowsersAndEmail, @"OLD-LAPTOP\alex", "Google Chrome — Alex (alex@example.com)", 340_000_000,
            "Bookmarks, history, extensions and settings", warning: "Saved passwords and cookies can't be moved: Chrome locks them to this PC.");
        Add("office", MigrationCategory.BrowsersAndEmail, @"OLD-LAPTOP\alex", "Outlook signatures", 80_000);
        Add("files", MigrationCategory.Files, @"OLD-LAPTOP\alex", "Documents", 23_400_000_000, @"C:\Users\alex\Documents");
        Add("files", MigrationCategory.Files, @"OLD-LAPTOP\alex", "Pictures", 15_800_000_000, @"C:\Users\alex\Pictures");
        Add("files", MigrationCategory.Files, @"OLD-LAPTOP\alex", "Desktop", 1_200_000_000, @"C:\Users\alex\Desktop");
        Add("files", MigrationCategory.Files, @"OLD-LAPTOP\alex", "Downloads", 9_100_000_000, @"C:\Users\alex\Downloads");
        inventory.Items.ForEach(i => i.FileCount = (int)Math.Min(int.MaxValue, i.SizeBytes / 2_000_000));
        return inventory;
    }

    private static MigrationReport SampleReport()
    {
        var report = new MigrationReport
        {
            Kind = OperationKind.Restore,
            Source = new MachineInfo { ComputerName = "OLD-LAPTOP" },
            Target = new MachineInfo { ComputerName = "NEW-LAPTOP" },
            StartedUtc = DateTime.UtcNow.AddMinutes(-23),
            FinishedUtc = DateTime.UtcNow,
            UndoId = "20261016-101500-a1b2",
        };
        report.Items.Add(new ItemResult { Name = "Documents", Status = ItemStatus.Succeeded, FilesDone = 11_204, BytesDone = 23_400_000_000 });
        report.Items.Add(new ItemResult { Name = "HP LaserJet M404 (Office)", Group = "Printers", Status = ItemStatus.Failed,
            Messages = [new ItemMessage(MessageLevel.Error, "The driver \"HP Universal Printing PCL 6\" isn't available on this PC.")] });
        report.ManualSteps.Add("Install the printer HP LaserJet M404 (Office) at 192.168.1.20 (get the driver from the maker's website).");
        report.ProgramsNotMoved.AddRange(["Old Accounting 2014 (version 14.2, Contoso)", "Label Printer Utility (version 3.1, Brother)",
            "Scanner Tools (version 2.0, Canon)"]);
        report.ManualSteps.Add("Default app for PDF files: Adobe Acrobat — set it in Settings > Apps > Default apps.");
        report.Notes.Add("A System Restore point named \"Before Ferry migration\" was created.");
        report.Notes.Add("Sign out and back in (or restart) so Windows and your apps pick up the moved settings.");
        return report;
    }
}
