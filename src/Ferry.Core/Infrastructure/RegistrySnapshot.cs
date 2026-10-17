using Ferry.Core.Rollback;
using Microsoft.Win32;

namespace Ferry.Core.Infrastructure;

public sealed class RegValueData
{
    public string Name { get; set; } = "";
    public RegistryValueKind Kind { get; set; }

    /// <summary>String and ExpandString data (unexpanded).</summary>
    public string? S { get; set; }
    public string[]? M { get; set; }
    public long? N { get; set; }

    /// <summary>Binary and None data, base64.</summary>
    public string? B { get; set; }

    public object ToRegistryObject(PathRemapper? remap) => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => remap?.Remap(S ?? "") ?? S ?? "",
        RegistryValueKind.MultiString => remap is null ? M ?? [] : remap.Remap(M ?? []),
        RegistryValueKind.DWord => unchecked((int)(N ?? 0)),
        RegistryValueKind.QWord => N ?? 0L,
        _ => Convert.FromBase64String(B ?? ""),
    };

    public static RegValueData? FromKey(RegistryKey key, string name)
    {
        RegistryValueKind kind;
        object? value;
        try
        {
            kind = key.GetValueKind(name);
            value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        if (value is null)
            return null;

        var data = new RegValueData { Name = name, Kind = kind };
        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                data.S = value as string ?? value.ToString();
                break;
            case RegistryValueKind.MultiString:
                data.M = value as string[] ?? [];
                break;
            case RegistryValueKind.DWord:
                data.N = Convert.ToInt32(value);
                break;
            case RegistryValueKind.QWord:
                data.N = Convert.ToInt64(value);
                break;
            default:
                // Binary, None, and exotic types .NET surfaces as Unknown; write those back as binary.
                data.Kind = kind == RegistryValueKind.None ? RegistryValueKind.None : RegistryValueKind.Binary;
                data.B = Convert.ToBase64String(value as byte[] ?? []);
                break;
        }
        return data;
    }
}

public sealed class RegKeyData
{
    /// <summary>Path relative to the exported key; empty for the exported key itself.</summary>
    public string Path { get; set; } = "";
    public List<RegValueData> Values { get; set; } = [];
}

/// <summary>A registry subtree captured on the old PC, replayable on the new one.</summary>
public sealed class RegistryExport
{
    /// <summary>"HKCU" or "HKLM".</summary>
    public string Root { get; set; } = "HKCU";
    public string Path { get; set; } = "";
    public List<RegKeyData> Keys { get; set; } = [];
    public bool Truncated { get; set; }

    public int ValueCount => Keys.Sum(k => k.Values.Count);
}

public sealed record RegistryApplyResult(int KeysCreated, int ValuesWritten, List<string> Errors);

public static class RegistrySnapshot
{
    /// <summary>Captures a key and its subkeys. Returns null when the key does not exist.</summary>
    /// <param name="valueFilter">Optional (relativeKeyPath, valueName) → include? filter.</param>
    /// <param name="skipSubkey">Optional relative subkey path → skip? filter.</param>
    public static RegistryExport? Capture(RegistryKey baseKey, string root, string path, int maxKeys = 20000,
        Func<string, bool>? skipSubkey = null, Func<string, string, bool>? valueFilter = null, bool recursive = true)
    {
        using var key = OpenRead(baseKey, path);
        if (key is null)
            return null;

        var export = new RegistryExport { Root = root, Path = path };
        var stack = new Stack<(RegistryKey Key, string Relative, bool Owned)>();
        stack.Push((key, "", false));
        while (stack.Count > 0)
        {
            var (current, relative, owned) = stack.Pop();
            try
            {
                var keyData = new RegKeyData { Path = relative };
                foreach (var name in SafeValueNames(current))
                {
                    if (valueFilter is not null && !valueFilter(relative, name))
                        continue;
                    var value = RegValueData.FromKey(current, name);
                    if (value is not null)
                        keyData.Values.Add(value);
                }
                export.Keys.Add(keyData);
                if (export.Keys.Count >= maxKeys)
                {
                    export.Truncated = true;
                    break;
                }

                if (!recursive)
                    continue;
                foreach (var sub in SafeSubKeyNames(current).Reverse())
                {
                    var subRelative = relative.Length == 0 ? sub : $@"{relative}\{sub}";
                    if (skipSubkey?.Invoke(subRelative) == true)
                        continue;
                    var child = OpenRead(current, sub);
                    if (child is not null)
                        stack.Push((child, subRelative, true));
                }
            }
            finally
            {
                if (owned)
                    current.Dispose();
            }
        }
        while (stack.Count > 0)
        {
            var (leftover, _, owned) = stack.Pop();
            if (owned)
                leftover.Dispose();
        }
        return export;
    }

    /// <summary>
    /// Writes an export under <paramref name="target"/> at <paramref name="targetPath"/> (defaults to the
    /// exported path), merging with what is there. Every change is journaled for undo.
    /// </summary>
    public static RegistryApplyResult Apply(RegistryRoot target, RegistryExport export, IChangeJournal journal, bool dryRun,
        PathRemapper? remap = null, string? targetPath = null)
    {
        var basePath = targetPath ?? export.Path;
        var errors = new List<string>();
        int keysCreated = 0, valuesWritten = 0;

        foreach (var keyData in export.Keys)
        {
            var fullPath = keyData.Path.Length == 0 ? basePath : $@"{basePath}\{keyData.Path}";
            try
            {
                if (dryRun)
                {
                    valuesWritten += keyData.Values.Count;
                    continue;
                }

                var createdTop = FindFirstMissingAncestor(target.Key, fullPath);
                using var key = target.Key.CreateSubKey(fullPath, writable: true);
                if (createdTop is not null)
                {
                    journal.RegistryKeyCreated(target.Locate(createdTop));
                    keysCreated++;
                }
                var isNewKey = createdTop is not null;

                foreach (var value in keyData.Values)
                {
                    try
                    {
                        var newValue = value.ToRegistryObject(remap);
                        if (!isNewKey)
                        {
                            var previous = RegValueData.FromKey(key, value.Name);
                            if (previous is not null && SameValue(previous, value.Kind, newValue))
                                continue;
                            journal.RegistryValueChanged(target.Locate(fullPath), value.Name, previous?.Kind,
                                previous?.ToRegistryObject(null));
                        }
                        key.SetValue(value.Name, newValue, value.Kind);
                        valuesWritten++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                    {
                        errors.Add($@"{fullPath}\{value.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors.Add($"{fullPath}: {ex.Message}");
            }
        }
        return new RegistryApplyResult(keysCreated, valuesWritten, errors);
    }

    /// <summary>Sets a single value with journaling. Returns false when it was already set to this.</summary>
    public static bool SetValue(RegistryRoot target, string path, string name, object value, RegistryValueKind kind, IChangeJournal journal)
    {
        var createdTop = FindFirstMissingAncestor(target.Key, path);
        using var key = target.Key.CreateSubKey(path, writable: true);
        if (createdTop is not null)
        {
            journal.RegistryKeyCreated(target.Locate(createdTop));
        }
        else
        {
            var previous = RegValueData.FromKey(key, name);
            if (previous is not null && SameValue(previous, kind, value))
                return false;
            journal.RegistryValueChanged(target.Locate(path), name, previous?.Kind, previous?.ToRegistryObject(null));
        }
        key.SetValue(name, value, kind);
        return true;
    }

    /// <summary>The shortest prefix of <paramref name="path"/> that does not exist yet, or null if it all exists.</summary>
    internal static string? FindFirstMissingAncestor(RegistryKey baseKey, string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current = current.Length == 0 ? part : $@"{current}\{part}";
            using var key = OpenRead(baseKey, current);
            if (key is null)
                return current;
        }
        return null;
    }

    private static bool SameValue(RegValueData previous, RegistryValueKind kind, object newValue)
    {
        if (previous.Kind != kind)
            return false;
        var old = previous.ToRegistryObject(null);
        return (old, newValue) switch
        {
            (string a, string b) => a == b,
            (string[] a, string[] b) => a.SequenceEqual(b),
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            _ => Equals(old, newValue),
        };
    }

    public static RegistryKey? OpenRead(RegistryKey baseKey, string path)
    {
        try
        {
            return path.Length == 0 ? baseKey.OpenSubKey("", writable: false) : baseKey.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string[] SafeValueNames(RegistryKey key)
    {
        try
        {
            return key.GetValueNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}
