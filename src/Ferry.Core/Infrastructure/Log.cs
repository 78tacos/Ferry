using System.Text;
using Ferry.Core.Model;

namespace Ferry.Core.Infrastructure;

/// <summary>Minimal thread-safe file logger. Also raises <see cref="Message"/> so the UI and CLI can echo lines.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? writer;

    public static string? FilePath { get; private set; }

    /// <summary>Echo debug lines to <see cref="Message"/> subscribers too (always written to the file).</summary>
    public static bool Verbose { get; set; }

    public static event Action<MessageLevel, string>? Message;

    public static void Initialize(string component)
    {
        lock (Gate)
        {
            if (writer is not null)
                return;
            try
            {
                FilePath = Path.Combine(AppPaths.Logs, $"ferry-{component}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                writer.WriteLine($"Ferry {typeof(Log).Assembly.GetName().Version} ({component}) on {Environment.MachineName}, " +
                                 $"user {Environment.UserDomainName}\\{Environment.UserName}, admin={Elevation.IsAdministrator}");
            }
            catch
            {
                // Logging must never stop a migration.
                writer = null;
            }
        }
    }

    public static void Debug(string text) => Write("DBG", text, null, echo: Verbose);
    public static void Info(string text) => Write("INF", text, MessageLevel.Info);
    public static void Warn(string text) => Write("WRN", text, MessageLevel.Warning);

    /// <remarks>The exception's details go to the log file only; listeners get the one-line text.</remarks>
    public static void Error(string text, Exception? ex = null) =>
        Write("ERR", ex is null ? text : $"{text}{Environment.NewLine}{ex}", MessageLevel.Error, echoText: ex is null ? text : $"{text}: {ex.Message}");

    private static void Write(string tag, string text, MessageLevel? level, bool echo = true, string? echoText = null)
    {
        lock (Gate)
        {
            try
            {
                writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {tag} {text}");
            }
            catch
            {
                // Ignore: see Initialize.
            }
        }
        if (echo)
            Message?.Invoke(level ?? MessageLevel.Info, echoText ?? text);
    }
}
