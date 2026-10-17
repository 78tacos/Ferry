using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferry.Core.Infrastructure;

public static class Json
{
    /// <summary>Compact options for wire and package data.</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Indented options for files people may read or edit (plans, journals' info files).</summary>
    public static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    public static byte[] ToBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Compact);

    public static T FromBytes<T>(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Compact) ?? throw new InvalidDataException($"Empty {typeof(T).Name} data.");

    public static string ToText<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Indented : Compact);

    public static T FromText<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Compact) ?? throw new InvalidDataException($"Empty {typeof(T).Name} data.");

    /// <summary>Reads without blocking a writer that is replacing the file at the same time.</summary>
    public static T ReadFile<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return FromText<T>(reader.ReadToEnd());
    }

    /// <summary>
    /// Writes to a temporary file and swaps it into place, so readers never see half a file and a reader holding
    /// the old one open doesn't make the write fail.
    /// </summary>
    public static void WriteFile<T>(string path, T value)
    {
        var temp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(temp, ToText(value));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
