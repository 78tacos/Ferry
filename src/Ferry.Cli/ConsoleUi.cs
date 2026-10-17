using System.Diagnostics;
using System.Text;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.Cli;

/// <summary>Console output: a single updating progress line on a terminal, periodic lines when redirected.</summary>
internal sealed class ConsoleUi : IProgress<MigrationProgress>
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Stopwatch rate = Stopwatch.StartNew();
    private readonly bool interactive = !Console.IsOutputRedirected;
    private int lastLength;
    private long rateBytes;
    private double bytesPerSecond;

    public bool Quiet { get; init; }

    public void Report(MigrationProgress value)
    {
        lock (gate)
        {
            if (Quiet || clock.ElapsedMilliseconds < (interactive ? 200 : 5000))
                return;
            clock.Restart();
            if (rate.ElapsedMilliseconds > 1000)
            {
                var instant = (value.BytesDone - rateBytes) / rate.Elapsed.TotalSeconds;
                bytesPerSecond = bytesPerSecond == 0 ? instant : bytesPerSecond * 0.7 + instant * 0.3;
                rateBytes = value.BytesDone;
                rate.Restart();
            }

            var sb = new StringBuilder();
            if (value.Fraction is { } f)
            {
                const int width = 24;
                var filled = (int)(f * width);
                sb.Append('[').Append('█', filled).Append('░', width - filled).Append("] ");
                sb.Append($"{f * 100,3:0}%  {Format.Bytes(value.BytesDone)} of {Format.Bytes(value.BytesTotal)}");
                if (bytesPerSecond > 0)
                {
                    sb.Append($"  {Format.Bytes((long)bytesPerSecond)}/s");
                    var remaining = (value.BytesTotal - value.BytesDone) / bytesPerSecond;
                    if (remaining is > 0 and < 360000)
                        sb.Append($"  ~{Format.Duration(TimeSpan.FromSeconds(remaining))} left");
                }
                sb.Append("  ");
            }
            else
            {
                sb.Append(value.Phase).Append(": ");
            }
            sb.Append(value.CurrentItem ?? "");
            if (!string.IsNullOrEmpty(value.CurrentDetail) && value.Fraction is null)
                sb.Append(value.CurrentDetail);
            WriteStatus(sb.ToString());
        }
    }

    private void WriteStatus(string text)
    {
        if (!interactive)
        {
            Console.WriteLine(text);
            return;
        }
        var width = Math.Max(20, SafeWidth() - 1);
        if (text.Length > width)
            text = text[..(width - 1)] + "…";
        Console.Write("\r" + text.PadRight(Math.Max(lastLength, text.Length)));
        lastLength = text.Length;
    }

    /// <summary>Ends the progress line so normal output starts on a fresh line.</summary>
    public void EndStatus()
    {
        lock (gate)
        {
            if (interactive && lastLength > 0)
            {
                Console.Write("\r" + new string(' ', lastLength) + "\r");
                lastLength = 0;
            }
        }
    }

    public void Line(string text = "")
    {
        EndStatus();
        Console.WriteLine(text);
    }

    public void Warn(string text)
    {
        EndStatus();
        WriteColored($"! {text}", ConsoleColor.Yellow);
    }

    public void Error(string text)
    {
        EndStatus();
        WriteColored($"✖ {text}", ConsoleColor.Red, error: true);
    }

    public void Good(string text)
    {
        EndStatus();
        WriteColored($"✔ {text}", ConsoleColor.Green);
    }

    private static void WriteColored(string text, ConsoleColor color, bool error = false)
    {
        var writer = error ? Console.Error : Console.Out;
        var old = Console.ForegroundColor;
        if (!Console.IsOutputRedirected)
            Console.ForegroundColor = color;
        writer.WriteLine(text);
        if (!Console.IsOutputRedirected)
            Console.ForegroundColor = old;
    }

    private static int SafeWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 120;
        }
    }

    /// <summary>Reads a password without echoing it. Falls back to a plain line when input is redirected.</summary>
    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                    sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
