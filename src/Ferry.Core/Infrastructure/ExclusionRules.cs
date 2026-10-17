using System.Text.RegularExpressions;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// File and folder exclusion patterns. A pattern without a backslash matches a file or folder name anywhere
/// ("*.tmp", "node_modules"); a pattern with one matches the path relative to the folder being copied
/// ("AppData\Local\Temp", "**\Cache\*"). '*' matches within one path segment, '**' across segments.
/// </summary>
public sealed class ExclusionRules
{
    /// <summary>Junk that is never worth moving, wherever it is.</summary>
    public static readonly string[] BuiltIn =
    [
        "Thumbs.db", "ehthumbs.db", "~$*", "*.tmp", "*.tmp.*", "*.temp", "$RECYCLE.BIN", "System Volume Information",
        "*.ferrytmp", "*.ost", "*.nst", "hiberfil.sys", "pagefile.sys", "swapfile.sys", "~WRL*.tmp",
    ];

    private readonly List<Regex> namePatterns = [];
    private readonly List<Regex> pathPatterns = [];

    public ExclusionRules(IEnumerable<string>? patterns = null, bool includeBuiltIn = true)
    {
        if (includeBuiltIn)
            AddRange(BuiltIn);
        if (patterns is not null)
            AddRange(patterns);
    }

    public IReadOnlyList<string> Patterns => patternText;
    private readonly List<string> patternText = [];

    public ExclusionRules AddRange(IEnumerable<string> patterns)
    {
        foreach (var p in patterns)
            Add(p);
        return this;
    }

    /// <remarks>A leading backslash anchors a single name to the root: "\desktop.ini" excludes only the top-level one.</remarks>
    public ExclusionRules Add(string pattern)
    {
        var raw = pattern.Trim().Replace('/', '\\');
        var anchored = raw.StartsWith('\\');
        var p = raw.Trim('\\');
        if (p.Length == 0)
            return this;
        patternText.Add(anchored ? "\\" + p : p);
        var regex = new Regex("^" + GlobToRegex(p) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        (anchored || p.Contains('\\') ? pathPatterns : namePatterns).Add(regex);
        return this;
    }

    /// <summary>Returns a copy with extra patterns, leaving this instance untouched.</summary>
    public ExclusionRules With(IEnumerable<string> extra)
    {
        var copy = new ExclusionRules(includeBuiltIn: false);
        copy.AddRange(patternText).AddRange(extra);
        return copy;
    }

    /// <param name="relativePath">Path relative to the root being copied, backslash-separated.</param>
    public bool IsExcluded(string relativePath)
    {
        var name = relativePath.Length == 0 ? "" : relativePath[(relativePath.LastIndexOf('\\') + 1)..];
        foreach (var r in namePatterns)
            if (r.IsMatch(name))
                return true;
        foreach (var r in pathPatterns)
            if (r.IsMatch(relativePath))
                return true;
        return false;
    }

    internal static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    // "**\" matches zero or more whole segments; a trailing "**" matches anything.
                    if (i + 2 < glob.Length && glob[i + 2] == '\\')
                    {
                        sb.Append(@"(?:.*\\)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else
                {
                    sb.Append(@"[^\\]*");
                }
            }
            else if (c == '?')
            {
                sb.Append(@"[^\\]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return sb.ToString();
    }
}
