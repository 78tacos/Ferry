namespace Ferry.Core.Infrastructure;

public static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 ? $"{value:0} {Units[unit]}" : value >= 10 ? $"{value:0.#} {Units[unit]}" : $"{value:0.##} {Units[unit]}";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
            return $"{Math.Max(1, (int)span.TotalSeconds)} sec";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} min {span.Seconds} sec";
        return $"{(int)span.TotalHours} hr {span.Minutes} min";
    }

    public static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count:N0} {plural ?? singular + "s"}";

    /// <summary>Parses "4GB", "500 MB", "1.5g", "1000000" into bytes.</summary>
    public static long ParseSize(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        long multiplier = 1;
        foreach (var (suffix, mult) in new (string, long)[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10), ("B", 1) })
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = mult;
                t = t[..^suffix.Length];
                break;
            }
        }
        return (long)(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) * multiplier);
    }
}
