using System.Text;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// Rewrites old-PC paths to new-PC paths inside strings: registry values, shortcuts, JSON and INI settings.
/// "C:\Users\olduser\Documents\x" → "C:\Users\newuser\OneDrive\Documents\x". Longer (more specific) prefixes win,
/// matches are case-insensitive and only at a path boundary, so C:\Users\joe never rewrites C:\Users\joey.
/// </summary>
public sealed class PathRemapper
{
    private readonly List<(string From, string To)> pairs = [];
    private bool sorted;

    public bool IsEmpty => pairs.Count == 0;

    public PathRemapper Add(string from, string to)
    {
        from = from.TrimEnd('\\', '/');
        to = to.TrimEnd('\\', '/');
        if (from.Length < 3 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return this;
        if (pairs.Any(p => string.Equals(p.From, from, StringComparison.OrdinalIgnoreCase)))
            return this;

        pairs.Add((from, to));
        // Forward-slash form (C:/Users/x) and JSON-escaped form (C:\\Users\\x) used by many config files.
        pairs.Add((from.Replace('\\', '/'), to.Replace('\\', '/')));
        pairs.Add((from.Replace("\\", "\\\\"), to.Replace("\\", "\\\\")));
        sorted = false;
        return this;
    }

    public string Remap(string value)
    {
        if (pairs.Count == 0 || string.IsNullOrEmpty(value))
            return value;
        EnsureSorted();

        StringBuilder? sb = null;
        var i = 0;
        var copiedUpTo = 0;
        while (i < value.Length)
        {
            var matched = false;
            foreach (var (from, to) in pairs)
            {
                if (i + from.Length > value.Length || string.Compare(value, i, from, 0, from.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                if (!IsBoundaryBefore(value, i) || !IsBoundaryAfter(value, i + from.Length))
                    continue;

                sb ??= new StringBuilder(value.Length + 32);
                sb.Append(value, copiedUpTo, i - copiedUpTo).Append(to);
                i += from.Length;
                copiedUpTo = i;
                matched = true;
                break;
            }
            if (!matched)
                i++;
        }
        if (sb is null)
            return value;
        sb.Append(value, copiedUpTo, value.Length - copiedUpTo);
        return sb.ToString();
    }

    public string[] Remap(string[] values) => values.Select(Remap).ToArray();

    /// <summary>Rewrites a path, but only if it starts with one of the mapped folders. Returns null if none apply.</summary>
    public string? RemapPrefix(string path)
    {
        EnsureSorted();
        foreach (var (from, to) in pairs)
        {
            if (path.StartsWith(from, StringComparison.OrdinalIgnoreCase) && IsBoundaryAfter(path, from.Length))
                return to + path[from.Length..];
        }
        return null;
    }

    private void EnsureSorted()
    {
        if (sorted)
            return;
        pairs.Sort((a, b) => b.From.Length.CompareTo(a.From.Length));
        sorted = true;
    }

    // A drive-letter path must not be preceded by a letter/digit ("XC:\Users" is not "C:\Users").
    private static bool IsBoundaryBefore(string s, int index) => index == 0 || !char.IsLetterOrDigit(s[index - 1]);

    // The match must end the string or be followed by a separator or a delimiter, never by more name characters.
    private static bool IsBoundaryAfter(string s, int index) =>
        index >= s.Length || s[index] is '\\' or '/' or '"' or '\'' or ';' or ',' or ' ' or '\0' or '>' or '<' or ')' or ']' or '|' or '\r' or '\n' or '\t';
}
