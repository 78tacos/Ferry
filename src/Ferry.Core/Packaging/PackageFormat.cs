using System.Text.Json.Serialization;

namespace Ferry.Core.Packaging;

/// <summary>
/// Layout of a Ferry package (a ".ferry" migration file, or the stream sent over the network):
/// <code>
///   "FERRYPK1" | int32 headerLength | PackageHeader JSON (plaintext)
///   body: [AES-256-GCM segments, if encrypted] carrying a sequence of records:
///     0x01 Entry: int32 len | EntryHeader JSON | 8-byte header check | (content chunks…)?
///          chunk: int32 n &gt; 0 → n raw bytes; n &lt; 0 → int32 rawLength + (-n) Brotli bytes; n == 0 → end + 32-byte SHA-256
///     0x02 End:   int32 len | PackageFooter JSON | 8-byte header check
/// </code>
/// The header check (the first 8 bytes of the JSON's SHA-256) catches damaged names and ids even in unencrypted files.
/// Everything is written strictly sequentially so the same format streams over a socket and restores on the fly.
/// </summary>
public static class PackageFormat
{
    public static ReadOnlySpan<byte> Magic => "FERRYPK1"u8;
    public const int CurrentVersion = 1;
    public const int ChunkSize = 1 << 20;
    public const byte EntryRecord = 0x01;
    public const byte EndRecord = 0x02;
    public const int MaxHeaderLength = 16 << 20;

    /// <summary>Chunk length that marks an entry abandoned by the writer (its source could not be read to the end).</summary>
    public const int AbortedChunk = int.MinValue;

    public const int HeaderCheckLength = 8;

    internal static byte[] HeaderCheck(ReadOnlySpan<byte> header) => System.Security.Cryptography.SHA256.HashData(header)[..HeaderCheckLength];
    public const string FileExtension = ".ferry";

    /// <summary>Name of the first entry in every package: the inventory of what it contains.</summary>
    public const string ManifestEntry = "manifest/inventory.json";
    public const string CaptureInfoEntry = "manifest/capture.json";

    /// <summary>Extensions whose content is already compressed; Brotli is not attempted on them.</summary>
    public static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma",
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab",
        ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".msi", ".msix",
        ".appx", ".nupkg", ".iso", ".vhd", ".vhdx", ".jfif", ".pdf",
    };
}

public sealed class PackageHeader
{
    public int FormatVersion { get; set; } = PackageFormat.CurrentVersion;
    public Guid PackageId { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SourceComputer { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public EncryptionInfo? Encryption { get; set; }
    public bool Compressed { get; set; } = true;

    /// <summary>Set when the file was written in parts (name.ferry.002, …); readers only join parts when it is.</summary>
    public long? SplitPartSize { get; set; }
}

public sealed class EncryptionInfo
{
    public string Algorithm { get; set; } = "AES-256-GCM-STREAM";
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    public string NoncePrefix { get; set; } = "";

    /// <summary>HMAC of a constant under the key, so a wrong password is reported clearly instead of as corruption.</summary>
    public string KeyCheck { get; set; } = "";
    public int SegmentSize { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<EntryKind>))]
public enum EntryKind
{
    /// <summary>A file with content and file-system metadata.</summary>
    File,
    /// <summary>A directory (no content), so empty folders survive.</summary>
    Directory,
    /// <summary>Migrator data (JSON, exported settings) with content.</summary>
    Data,
}

public sealed class EntryHeader
{
    /// <summary>"&lt;migratorId&gt;/…" path inside the package.</summary>
    [JsonPropertyName("n")] public string Name { get; set; } = "";

    /// <summary>The inventory item this entry belongs to, so restore can honour per-item selection.</summary>
    [JsonPropertyName("i")] public string ItemId { get; set; } = "";
    [JsonPropertyName("k")] public EntryKind Kind { get; set; }
    [JsonPropertyName("l")] public long Length { get; set; }
    [JsonPropertyName("a")] public int Attributes { get; set; }
    [JsonPropertyName("c")] public long CreatedTicks { get; set; }
    [JsonPropertyName("w")] public long ModifiedTicks { get; set; }

    /// <summary>Extra migrator metadata, e.g. the original path on the old PC.</summary>
    [JsonPropertyName("p")] public Dictionary<string, string>? Meta { get; set; }

    [JsonIgnore] public bool HasContent => Kind != EntryKind.Directory;

    public string? GetMeta(string key) => Meta is not null && Meta.TryGetValue(key, out var v) ? v : null;
}

public sealed class PackageFooter
{
    public int EntryCount { get; set; }
    public long ContentBytes { get; set; }
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;
    public bool Cancelled { get; set; }
}
