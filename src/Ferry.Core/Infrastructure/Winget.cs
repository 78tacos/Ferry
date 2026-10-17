using System.Text;
using System.Text.Json;

namespace Ferry.Core.Infrastructure;

public sealed record WingetPackage(string Name, string Id, string Version, string Source);

public sealed record WingetInstallResult(bool Succeeded, bool AlreadyInstalled, int ExitCode, string Message);

/// <summary>Thin wrapper over the winget command line (App Installer).</summary>
public static class Winget
{
    private static readonly Lazy<string?> PathLazy = new(Locate);

    public static string? ExecutablePath => PathLazy.Value;
    public static bool IsAvailable => ExecutablePath is not null;

    private const string CommonArgs = "--accept-source-agreements --disable-interactivity";

    private static string? Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias))
            return alias;
        // Elevated as a different admin, or as SYSTEM: the execution alias isn't there, but the package folder is.
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var candidate = Directory.EnumerateDirectories(root, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "winget.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null)
                return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // WindowsApps is not listable for most accounts.
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(path))
                    return path;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Everything winget sees as installed, with ids for the ones it can reinstall.</summary>
    public static async Task<List<WingetPackage>> ListInstalledAsync(CancellationToken ct)
    {
        if (ExecutablePath is not { } exe)
            return [];
        var result = await ProcessRunner.RunRawAsync(exe, $"list {CommonArgs}", ct, TimeSpan.FromMinutes(3), Encoding.UTF8).ConfigureAwait(false);
        if (result.StdOut.Length == 0)
        {
            Log.Warn($"winget list returned nothing (exit {result.ExitCode}).");
            return [];
        }
        return ParseTable(result.StdOut);
    }

    /// <summary>Exact package ids (winget export never truncates them like the table view can).</summary>
    public static async Task<Dictionary<string, string>> ExportIdsAsync(CancellationToken ct)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ExecutablePath is not { } exe)
            return ids;
        var file = Path.Combine(AppPaths.Temp, $"winget-export-{Guid.NewGuid():N}.json");
        try
        {
            await ProcessRunner.RunRawAsync(exe, $"export -o \"{file}\" --include-versions {CommonArgs}", ct, TimeSpan.FromMinutes(3), Encoding.UTF8)
                .ConfigureAwait(false);
            if (!File.Exists(file))
                return ids;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
            {
                var sourceName = source.TryGetProperty("SourceDetails", out var details) && details.TryGetProperty("Name", out var n)
                    ? n.GetString() ?? "winget"
                    : "winget";
                foreach (var package in source.GetProperty("Packages").EnumerateArray())
                {
                    var id = package.GetProperty("PackageIdentifier").GetString();
                    if (!string.IsNullOrEmpty(id))
                        ids[id] = sourceName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"Could not read winget export: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Temp file; harmless.
            }
        }
        return ids;
    }

    public static async Task<WingetInstallResult> InstallAsync(string id, string? source, CancellationToken ct)
    {
        if (ExecutablePath is not { } exe)
            return new WingetInstallResult(false, false, -1, "winget is not available on this PC.");

        var sourceArg = string.IsNullOrEmpty(source) ? "" : $" --source {source}";
        // When elevated, prefer a machine-wide install so the app is there for every user; fall back to the
        // package's default scope for packages that only install per user.
        var scopeArg = Elevation.IsAdministrator ? " --scope machine" : "";
        var result = await ProcessRunner.RunRawAsync(exe,
            $"install --id \"{id}\" --exact{sourceArg} --silent{scopeArg} --accept-package-agreements {CommonArgs}",
            ct, TimeSpan.FromMinutes(45), Encoding.UTF8).ConfigureAwait(false);
        if (!result.Succeeded && !LooksAlreadyInstalled(result) && scopeArg.Length > 0)
        {
            result = await ProcessRunner.RunRawAsync(exe,
                $"install --id \"{id}\" --exact{sourceArg} --silent --accept-package-agreements {CommonArgs}",
                ct, TimeSpan.FromMinutes(45), Encoding.UTF8).ConfigureAwait(false);
        }
        if (LooksAlreadyInstalled(result))
            return new WingetInstallResult(true, true, result.ExitCode, "Already installed.");
        return result.Succeeded
            ? new WingetInstallResult(true, false, 0, "Installed.")
            : new WingetInstallResult(false, false, result.ExitCode, LastMeaningfulLine(result.CombinedOutput) ?? $"winget exited with code {result.ExitCode}.");
    }

    /// <summary>Finds a winget package whose name matches exactly, for apps the old PC couldn't match itself.</summary>
    public static async Task<WingetPackage?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (ExecutablePath is not { } exe)
            return null;
        var result = await ProcessRunner.RunRawAsync(exe, $"search --name \"{name.Replace("\"", "")}\" --exact --source winget {CommonArgs}",
            ct, TimeSpan.FromMinutes(2), Encoding.UTF8).ConfigureAwait(false);
        if (!result.Succeeded)
            return null;
        var rows = ParseTable(result.StdOut);
        return rows.Count == 1 ? rows[0] : null;
    }

    public static string UninstallArguments(string id) => $"uninstall --id \"{id}\" --exact --silent {CommonArgs}";

    private static bool LooksAlreadyInstalled(ProcessResult result) =>
        result.CombinedOutput.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
        result.CombinedOutput.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase) ||
        result.CombinedOutput.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase);

    private static string? LastMeaningfulLine(string text) =>
        text.Split('\n').Select(l => CleanLine(l).Trim()).Where(l => l.Length > 3 && !l.All(c => c is '█' or '▒' or ' ' or '-' or '\\' or '|' or '/'))
            .LastOrDefault();

    /// <summary>Drops the line ending, then any progress-spinner frames winget overwrote with carriage returns.</summary>
    internal static string CleanLine(string line)
    {
        line = line.TrimEnd('\r');
        var i = line.LastIndexOf('\r');
        return i >= 0 ? line[(i + 1)..] : line;
    }

    /// <summary>
    /// Parses winget's table output. Column positions come from the header line above the dashed rule, so
    /// localized headers work; names truncated with "…" are kept as-is for prefix matching.
    /// </summary>
    internal static List<WingetPackage> ParseTable(string output)
    {
        var lines = output.Split('\n').Select(l => CleanLine(l).TrimEnd()).ToList();
        var rule = lines.FindIndex(l => l.Length > 10 && l.All(c => c == '-'));
        if (rule <= 0)
            return [];
        var header = lines[rule - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
                starts.Add(i);
        }
        if (starts.Count < 3)
            return [];

        var rows = new List<string[]>();
        foreach (var line in lines.Skip(rule + 1))
        {
            if (line.Length < starts[1] || line.All(c => c == ' '))
                continue;
            // A footer like "3 upgrades available." has no Id column.
            var cells = new string[starts.Count];
            for (var c = 0; c < starts.Count; c++)
            {
                var from = starts[c];
                var to = c + 1 < starts.Count ? starts[c + 1] : line.Length;
                cells[c] = from >= line.Length ? "" : line[from..Math.Min(to, line.Length)].Trim();
            }
            if (cells[1].Length == 0)
                continue;
            rows.Add(cells);
        }

        // Columns: Name, Id, Version, [Available], [Source]. With four columns, the fourth is a source if it holds names.
        var sourceColumn = -1;
        if (starts.Count >= 5)
            sourceColumn = 4;
        else if (starts.Count == 4)
        {
            var values = rows.Select(r => r[3]).Where(v => v.Length > 0).ToList();
            if (values.Count > 0 && values.Count(v => char.IsLetter(v[0])) > values.Count / 2)
                sourceColumn = 3;
        }

        return rows.Select(r => new WingetPackage(
                r[0],
                r[1],
                r[2].TrimStart('>', '<', ' '),
                sourceColumn >= 0 ? r[sourceColumn] : ""))
            .ToList();
    }
}
