namespace Ferry.Cli;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>"ferry &lt;command&gt; [positional…] [--option value] [--flag]" parsing. Options may repeat.</summary>
internal sealed class CliArgs
{
    /// <summary>Options that never take a value, so "--dry-run something" doesn't swallow "something".</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "dry-run", "close-apps", "no-restore-point", "no-shadow", "verbose", "quiet", "yes", "list", "help", "all", "details",
        "from-scan", "no-winget", "h", "?",
    };

    private readonly Dictionary<string, List<string>> options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }
    public List<string> Positionals { get; } = [];

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.StartsWith('-') && arg.Length == 2) || arg == "/?")
            {
                var name = arg.TrimStart('-', '/');
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (!Flags.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                if (!result.options.TryGetValue(name, out var list))
                    result.options[name] = list = [];
                list.Add(value ?? "true");
            }
            else if (result.Command is null)
            {
                result.Command = arg.ToLowerInvariant();
            }
            else
            {
                result.Positionals.Add(arg);
            }
        }
        return result;
    }

    public bool Has(string name) => options.ContainsKey(name);

    public string? Get(string name) => options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => options.TryGetValue(name, out var values) ? values : [];

    public string Require(string name, string description) =>
        Get(name) is { Length: > 0 } value && value != "true" ? value : throw new UsageException($"Missing --{name} <{description}>.");

    public IEnumerable<string> Unknown(IEnumerable<string> known) => options.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase));
}
