using System.Text;
using System.Text.Json;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// Runs short Windows PowerShell 5.1 scripts for things with no good managed API
/// (printers, shadow copies, restore points). Always present on Windows 10/11.
/// </summary>
public static class PowerShellRunner
{
    private static string Exe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private const string Preamble =
        "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;";

    public static Task<ProcessResult> RunAsync(string script, CancellationToken ct, TimeSpan? timeout = null)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Preamble + script));
        return ProcessRunner.RunAsync(Exe,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            ct, timeout ?? TimeSpan.FromMinutes(5), Encoding.UTF8);
    }

    /// <summary>
    /// Runs a script whose pipeline output is converted to a JSON array and deserialized.
    /// The script should emit objects (e.g. Select-Object with the needed properties).
    /// </summary>
    public static async Task<List<T>> QueryAsync<T>(string script, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await RunAsync($"$r=@({script});ConvertTo-Json -InputObject $r -Depth 4 -Compress", ct, timeout).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException(FirstLine(result.StdErr) ?? $"PowerShell exited with code {result.ExitCode}.");
        var json = result.StdOut.Trim();
        if (json.Length == 0)
            return [];
        return JsonSerializer.Deserialize<List<T>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    /// <summary>Quotes a value as a PowerShell single-quoted string literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
