using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.Core.Packaging;

/// <summary>Reads a package strictly front to back. Each entry's content must be read (or is skipped) before the next.</summary>
public sealed class PackageReader : IAsyncDisposable
{
    private readonly Stream input;
    private readonly Stream body;
    private readonly bool ownsInput;
    private readonly byte[] scratch = new byte[8];
    private PackageEntry? current;
    private bool ended;

    private PackageReader(Stream input, Stream body, PackageHeader header, bool ownsInput)
    {
        this.input = input;
        this.body = body;
        this.ownsInput = ownsInput;
        Header = header;
    }

    public PackageHeader Header { get; }
    public PackageFooter? Footer { get; private set; }

    /// <summary>Uncompressed content bytes read so far.</summary>
    public long ContentBytes { get; internal set; }

    public event Action<long>? Progress;

    internal void ReportProgress(int bytes)
    {
        ContentBytes += bytes;
        Progress?.Invoke(ContentBytes);
    }

    /// <param name="password">Asked for only when the package is encrypted.</param>
    public static async Task<PackageReader> OpenAsync(Stream input, Func<PackageHeader, string?>? password, bool ownsInput, CancellationToken ct)
    {
        var prefix = new byte[12];
        try
        {
            await input.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("This is not a Ferry migration file (it is empty or too short).");
        }
        if (!prefix.AsSpan(0, 8).SequenceEqual(PackageFormat.Magic))
            throw new InvalidDataException("This is not a Ferry migration file.");

        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (headerLength is <= 0 or > PackageFormat.MaxHeaderLength)
            throw new InvalidDataException("The migration file header is damaged.");
        var headerBytes = new byte[headerLength];
        try
        {
            await input.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The migration file header is damaged.");
        }
        PackageHeader header;
        try
        {
            header = Json.FromBytes<PackageHeader>(headerBytes);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("The migration file header is damaged.");
        }
        if (header.FormatVersion > PackageFormat.CurrentVersion)
            throw new InvalidDataException($"This migration file was made by a newer version of Ferry (format {header.FormatVersion}). Update Ferry on this PC.");

        Stream body;
        if (header.Encryption is { } enc)
        {
            var pw = password?.Invoke(header);
            if (string.IsNullOrEmpty(pw))
                throw new PasswordRequiredException();
            var key = PackageCrypto.DeriveKey(pw, enc);
            body = new AeadReadStream(input, key, Convert.FromBase64String(enc.NoncePrefix), enc.SegmentSize, SHA256.HashData(headerBytes), leaveOpen: true);
            CryptographicOperations.ZeroMemory(key);
        }
        else
        {
            body = new BufferedStream(input, 1 << 20);
        }
        return new PackageReader(input, body, header, ownsInput);
    }

    /// <summary>Returns the next entry, or null after the end record.</summary>
    public async Task<PackageEntry?> ReadNextAsync(CancellationToken ct)
    {
        if (pending is not null)
        {
            var p = pending;
            pending = null;
            return p;
        }
        if (ended)
            return null;
        if (current is not null)
        {
            await current.SkipAsync(ct).ConfigureAwait(false);
            current = null;
        }

        int recordType;
        try
        {
            await ReadExactAsync(scratch.AsMemory(0, 1), ct).ConfigureAwait(false);
            recordType = scratch[0];
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("The migration file is incomplete: it ends before its final record. It may have been cut short while being saved or copied.");
        }

        if (recordType is not (PackageFormat.EntryRecord or PackageFormat.EndRecord))
            throw new InvalidDataException($"The migration file is damaged (unknown record type {recordType}).");
        var length = await ReadInt32Async(ct).ConfigureAwait(false);
        if (length is < 0 or > PackageFormat.MaxHeaderLength)
            throw new InvalidDataException("The migration file is damaged (bad record length).");
        var bytes = new byte[length];
        await ReadExactAsync(bytes, ct).ConfigureAwait(false);
        var check = new byte[PackageFormat.HeaderCheckLength];
        await ReadExactAsync(check, ct).ConfigureAwait(false);
        if (!check.AsSpan().SequenceEqual(PackageFormat.HeaderCheck(bytes)))
            throw new InvalidDataException("The migration file is damaged (a record failed its integrity check).");

        try
        {
            if (recordType == PackageFormat.EndRecord)
            {
                Footer = Json.FromBytes<PackageFooter>(bytes);
                ended = true;
                if (Footer.EntryCount != entriesRead)
                    throw new InvalidDataException($"The migration file is damaged: it should hold {Footer.EntryCount} entries but {entriesRead} were found.");
                return null;
            }
            current = new PackageEntry(Json.FromBytes<EntryHeader>(bytes), this);
            entriesRead++;
            return current;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"The migration file is damaged (unreadable record: {ex.Message}).", ex);
        }
    }

    private int entriesRead;

    /// <remarks>
    /// Failures of the underlying stream (a network drop, the other PC cancelling, a removed drive) surface as
    /// <see cref="PackageReadException"/>, so restore code can tell them apart from problems writing one file.
    /// </remarks>
    internal async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            await body.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The migration file is incomplete: it ends unexpectedly.");
        }
        catch (IOException ex) when (ex is not PackageReadException)
        {
            throw new PackageReadException(ex);
        }
    }

    internal async Task<int> ReadInt32Async(CancellationToken ct)
    {
        await ReadExactAsync(scratch.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(scratch);
    }

    /// <summary>Opens a package file (or the first of its split parts) and reads just its inventory.</summary>
    public static async Task<(PackageHeader Header, Inventory Inventory, CaptureInfo? Capture)> ReadManifestAsync(string path, string? password,
        CancellationToken ct)
    {
        await using var reader = await OpenAsync(PackageFiles.OpenRead(path), _ => password, ownsInput: true, ct).ConfigureAwait(false);
        return await reader.ReadManifestAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the manifest entries that every package starts with.</summary>
    public async Task<(PackageHeader Header, Inventory Inventory, CaptureInfo? Capture)> ReadManifestAsync(CancellationToken ct)
    {
        var first = await ReadNextAsync(ct).ConfigureAwait(false);
        if (first is null || first.Header.Name != PackageFormat.ManifestEntry)
            throw new InvalidDataException("The migration file has no inventory; it may be damaged.");
        var inventory = await first.ReadJsonAsync<Inventory>(ct).ConfigureAwait(false);

        CaptureInfo? capture = null;
        var second = await ReadNextAsync(ct).ConfigureAwait(false);
        if (second?.Header.Name == PackageFormat.CaptureInfoEntry)
            capture = await second.ReadJsonAsync<CaptureInfo>(ct).ConfigureAwait(false);
        else
            pending = second;
        return (Header, inventory, capture);
    }

    /// <summary>An entry read ahead while looking for optional manifest entries; returned by the next ReadNextAsync.</summary>
    private PackageEntry? pending;

    public async ValueTask DisposeAsync()
    {
        await body.DisposeAsync().ConfigureAwait(false);
        if (ownsInput)
            await input.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Reading the package itself failed (network, drive or the other PC); the restore can't continue.</summary>
public sealed class PackageReadException(IOException inner) : IOException(inner.Message, inner);

/// <summary>The old PC could not finish reading this file while saving it; the entry carries no usable content.</summary>
public sealed class EntryAbortedException() : IOException("The old PC could not read this file completely while saving it.");

/// <summary>What the capture side recorded about the capture itself: which items it included and any problems.</summary>
public sealed class CaptureInfo
{
    public List<string> IncludedItemIds { get; set; } = [];
    public Dictionary<string, string> Modes { get; set; } = [];
    public long EstimatedBytes { get; set; }
}

public sealed class PackageEntry
{
    private readonly PackageReader reader;
    private ContentStream? content;
    private bool consumed;

    internal PackageEntry(EntryHeader header, PackageReader reader)
    {
        Header = header;
        this.reader = reader;
        consumed = !header.HasContent;
    }

    public EntryHeader Header { get; }
    public string Name => Header.Name;
    public string ItemId => Header.ItemId;
    public EntryKind Kind => Header.Kind;
    public DateTime CreationTimeUtc => new(Header.CreatedTicks, DateTimeKind.Utc);
    public DateTime LastWriteTimeUtc => new(Header.ModifiedTicks, DateTimeKind.Utc);
    public FileAttributes Attributes => (FileAttributes)Header.Attributes;

    /// <summary>Content stream. Reading it to the end verifies the SHA-256 recorded by the writer.</summary>
    public Stream OpenContent()
    {
        if (!Header.HasContent)
            return Stream.Null;
        if (content is not null || consumed)
            throw new InvalidOperationException($"The content of {Name} has already been read.");
        content = new ContentStream(reader, () => consumed = true);
        return content;
    }

    public async Task<byte[]> ReadAllBytesAsync(CancellationToken ct)
    {
        // The recorded length is only a hint (and could be damaged); don't let it size a huge buffer up front.
        using var ms = new MemoryStream((int)Math.Clamp(Header.Length, 0, 16 << 20));
        await using (var s = OpenContent())
            await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task<T> ReadJsonAsync<T>(CancellationToken ct) => Json.FromBytes<T>(await ReadAllBytesAsync(ct).ConfigureAwait(false));

    /// <summary>Discards whatever content has not been read.</summary>
    public async Task SkipAsync(CancellationToken ct)
    {
        if (consumed)
            return;
        content ??= new ContentStream(reader, () => consumed = true);
        await content.DrainAsync(ct).ConfigureAwait(false);
    }

    private sealed class ContentStream(PackageReader reader, Action onEnd) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private byte[] chunk = [];
        private int chunkLength;
        private int position;
        private bool done;
        private bool verify = true;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
                return 0;
            while (position == chunkLength)
            {
                if (done)
                    return 0;
                await NextChunkAsync(cancellationToken).ConfigureAwait(false);
            }
            var n = Math.Min(buffer.Length, chunkLength - position);
            chunk.AsMemory(position, n).CopyTo(buffer);
            position += n;
            return n;
        }

        public async Task DrainAsync(CancellationToken ct)
        {
            verify = false;
            while (!done)
                await NextChunkAsync(ct, decode: false).ConfigureAwait(false);
        }

        private async Task NextChunkAsync(CancellationToken ct, bool decode = true)
        {
            var n = await reader.ReadInt32Async(ct).ConfigureAwait(false);
            if (n == PackageFormat.AbortedChunk)
            {
                done = true;
                chunkLength = position = 0;
                ReturnBuffer();
                onEnd();
                if (decode && verify)
                    throw new EntryAbortedException();
                return;
            }
            if (n == 0)
            {
                var expected = new byte[32];
                await reader.ReadExactAsync(expected, ct).ConfigureAwait(false);
                if (verify && !hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
                {
                    done = true;
                    throw new InvalidDataException("A file in the migration package failed its integrity check; the package is damaged.");
                }
                done = true;
                chunkLength = position = 0;
                ReturnBuffer();
                onEnd();
                return;
            }

            int rawLength, storedLength;
            if (n > 0)
            {
                rawLength = storedLength = n;
            }
            else
            {
                storedLength = -n;
                rawLength = await reader.ReadInt32Async(ct).ConfigureAwait(false);
            }
            if (storedLength > PackageFormat.ChunkSize * 2 || rawLength is <= 0 or > PackageFormat.ChunkSize * 2)
                throw new InvalidDataException("The migration file is damaged (bad chunk length).");

            EnsureBuffer(rawLength);
            if (n > 0)
            {
                await reader.ReadExactAsync(chunk.AsMemory(0, storedLength), ct).ConfigureAwait(false);
            }
            else
            {
                var packed = ArrayPool<byte>.Shared.Rent(storedLength);
                try
                {
                    await reader.ReadExactAsync(packed.AsMemory(0, storedLength), ct).ConfigureAwait(false);
                    if (decode && (!BrotliDecoder.TryDecompress(packed.AsSpan(0, storedLength), chunk.AsSpan(0, rawLength), out var written) || written != rawLength))
                        throw new InvalidDataException("The migration file is damaged (a compressed block could not be decoded).");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packed);
                }
            }
            if (decode)
                hash.AppendData(chunk, 0, rawLength);
            chunkLength = decode ? rawLength : 0;
            position = 0;
            reader.ReportProgress(rawLength);
        }

        private void EnsureBuffer(int size)
        {
            if (chunk.Length >= size)
                return;
            ReturnBuffer();
            chunk = ArrayPool<byte>.Shared.Rent(Math.Max(size, PackageFormat.ChunkSize));
        }

        private void ReturnBuffer()
        {
            if (chunk.Length > 0)
                ArrayPool<byte>.Shared.Return(chunk);
            chunk = [];
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
