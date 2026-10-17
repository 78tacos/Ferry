using System.Diagnostics;

namespace Ferry.Core.Infrastructure;

/// <summary>Programs whose files Ferry needs closed: browsers, Outlook and Office, Sticky Notes.</summary>
public static class RunningApps
{
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi",
        ["opera"] = "Opera",
        ["firefox"] = "Firefox",
        ["thunderbird"] = "Thunderbird",
        ["outlook"] = "Outlook",
        ["olk"] = "Outlook (new)",
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["onenote"] = "OneNote",
        ["Microsoft.Notes"] = "Sticky Notes",
    };

    public static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var running = processes.Length > 0;
        foreach (var p in processes)
            p.Dispose();
        return running;
    }

    /// <summary>Friendly names of the given (or all known) programs that are running now.</summary>
    public static List<string> Running(IEnumerable<string>? processNames = null) =>
        (processNames ?? Known.Keys).Where(IsRunning).Select(n => Known.GetValueOrDefault(n, n)).Distinct().ToList();

    /// <summary>Asks the program to close, then ends it if it hasn't after a few seconds.</summary>
    public static async Task CloseAsync(string processName, CancellationToken ct)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
        for (var i = 0; i < 20 && IsRunning(processName); i++)
            await Task.Delay(250, ct).ConfigureAwait(false);

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Log.Debug($"Could not end {processName}: {ex.Message}");
                }
            }
        }
        Log.Info($"Closed {Known.GetValueOrDefault(processName, processName)}");
    }

    /// <summary>
    /// Makes sure a program is closed before its files are replaced: closes it when allowed, else returns
    /// a message for the user. Returns null when it's fine to go ahead.
    /// </summary>
    public static async Task<string?> EnsureClosedAsync(string processName, bool allowClose, CancellationToken ct)
    {
        if (!IsRunning(processName))
            return null;
        var name = Known.GetValueOrDefault(processName, processName);
        if (!allowClose)
            return $"{name} is open on this PC. Close it (including from the system tray) and run the migration again.";
        await CloseAsync(processName, ct).ConfigureAwait(false);
        return IsRunning(processName) ? $"{name} could not be closed." : null;
    }
}
