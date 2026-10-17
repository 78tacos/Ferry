using System.Diagnostics;
using System.Text;

namespace Ferry.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
    public string CombinedOutput => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}{Environment.NewLine}{StdErr}";
}

public static class ProcessRunner
{
    /// <summary>Runs a program with each argument quoted for it.</summary>
    public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken ct,
        TimeSpan? timeout = null, Encoding? outputEncoding = null)
    {
        var psi = CreateStartInfo(fileName, outputEncoding);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        return RunAsync(psi, ct, timeout);
    }

    /// <summary>Runs a program with a pre-built command line, for tools like netsh that parse their own quoting.</summary>
    public static Task<ProcessResult> RunRawAsync(string fileName, string arguments, CancellationToken ct,
        TimeSpan? timeout = null, Encoding? outputEncoding = null)
    {
        var psi = CreateStartInfo(fileName, outputEncoding);
        psi.Arguments = arguments;
        return RunAsync(psi, ct, timeout);
    }

    /// <summary>Full path of a tool in System32, so we never pick up a same-named program from the current directory.</summary>
    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static ProcessStartInfo CreateStartInfo(string fileName, Encoding? outputEncoding)
    {
        // Console tools such as netsh and reg write in the OEM code page when redirected.
        var encoding = outputEncoding ?? OemEncoding.Value;
        return new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
    }

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, CancellationToken ct, TimeSpan? timeout)
    {
        Log.Debug($"Running: {psi.FileName} {(psi.ArgumentList.Count > 0 ? string.Join(' ', psi.ArgumentList.Select(Quote)) : psi.Arguments)}");
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"Could not start {psi.FileName}: {ex.Message}");
        }
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
            timeoutCts.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
            if (ct.IsCancellationRequested)
                throw;
            return new ProcessResult(-2, await SafeAwait(stdout), $"{psi.FileName} timed out after {timeout}.");
        }

        var result = new ProcessResult(process.ExitCode, await SafeAwait(stdout), await SafeAwait(stderr));
        if (!result.Succeeded)
            Log.Debug($"{Path.GetFileName(psi.FileName)} exited {result.ExitCode}: {Truncate(result.CombinedOutput, 2000)}");
        return result;
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static readonly Lazy<Encoding> OemEncoding = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    });
}
