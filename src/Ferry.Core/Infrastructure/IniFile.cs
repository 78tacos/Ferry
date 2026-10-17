using System.Text;

namespace Ferry.Core.Infrastructure;

/// <summary>Minimal INI reader/writer that keeps section and key order (for Firefox's profiles.ini).</summary>
public sealed class IniFile
{
    public List<(string Name, List<KeyValuePair<string, string>> Values)> Sections { get; } = [];

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        List<KeyValuePair<string, string>>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = [];
                ini.Sections.Add((line[1..^1], current));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && current is not null)
                current.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return ini;
    }

    public static IniFile Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : new IniFile();

    public string? Get(string section, string key) =>
        Sections.FirstOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase))
            .Values?.FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public List<KeyValuePair<string, string>> Section(string name)
    {
        var existing = Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing.Values is not null)
            return existing.Values;
        var values = new List<KeyValuePair<string, string>>();
        Sections.Add((name, values));
        return values;
    }

    public void Set(string section, string key, string value)
    {
        var values = Section(section);
        var index = values.FindIndex(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            values[index] = new(key, value);
        else
            values.Add(new(key, value));
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (name, values) in Sections)
        {
            sb.Append('[').Append(name).Append("]\r\n");
            foreach (var (key, value) in values)
                sb.Append(key).Append('=').Append(value).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
