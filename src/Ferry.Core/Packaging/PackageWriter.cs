using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Ferry.Core.Infrastructure;

namespace Ferry.Core.Packaging;

/// <summary>File-system metadata stored with a file entry.</summary>
public readonly record struct FileMeta(FileAttributes Attributes, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc, long Length)
{
    public static FileMeta From(WalkEntry e) => new(e.Attributes, e.CreationTimeUtc, e.LastWriteTimeUtc, e.Length);

    public static FileMeta From(FileInfo f) => new(f.Attributes, f.CreationTimeUtc, f.LastWriteTimeUtc, f.Length);
}

/// <summary>Reading a source file failed part-way; the entry was marked abandoned and the package is still valid.</summary>
public sealed class SourceReadException(Exception inner) : IOException(inner.Message, inner);

/// <summary>Writing to the destination (file, drive, network) failed. The package cannot be completed.</summary>
public sealed class DestinationException(Exception inner)
    : IOException($"Could not write the migration data: {inner.Message}", inner);

/// <summary>Writes a package sequentially to any stream: a file, a set of split files, or a network channel.</summary>
public sealed class PackageWriter : IAsyncDisposable
{
    private readonly Stream output;
    private readonly AeadWriteStream? aead;
    private readonly Stream body;
    private readonly bool compress;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly byte[] scratch = new byte[8];
    private long contentBytes;
    private int entryCount;
    private bool completed;

    private PackageWriter(Stream output, AeadWriteStream? aead, Stream body, PackageHeader header)
    {
        this.output = output;
        this.aead = aead;
        this.body = body;
        Header = header;
        compress = header.Compressed;
    }

    public PackageHeader Header { get; }

    /// <summary>Uncompressed content bytes written so far (what progress bars measure against).</summary>
    public long ContentBytes => Interlocked.Read(ref contentBytes);
    public int EntryCount => entryCount;

    /// <summary>Raised after each chunk with the running <see cref="ContentBytes"/> total.</summary>
    public event Action<long>? Progress;

    public static async Task<PackageWriter> CreateAsync(Stream output, PackageHeader header, string? password, CancellationToken ct)
    {
        byte[]? key = null;
        if (!string.IsNullOrEmpty(password))
        {
            (header.Encryption, key) = PackageCrypto.CreateForPassword(password);
        }
        else
        {
            header.Encryption = null;
        }

        var headerBytes = Json.ToBytes(header);
        var prefix = new byte[12];
        PackageFormat.Magic.CopyTo(prefix);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), headerBytes.Length);
        await output.WriteAsync(prefix, ct).ConfigureAwait(false);
        await output.WriteAsync(headerBytes, ct).ConfigureAwait(false);

        if (key is null)
            return new PackageWriter(output, null, new BufferedStream(output, 1 << 20), header);

        var aead = new AeadWriteStream(output, key, Convert.FromBase64String(header.Encryption!.NoncePrefix),
            header.Encryption.SegmentSize, SHA256.HashData(headerBytes), leaveOpen: true);
        CryptographicOperations.ZeroMemory(key);
        return new PackageWriter(output, aead, aead, header);
    }

    public async Task WriteDataAsync(string name, string itemId, ReadOnlyMemory<byte> data, Dictionary<string, string>? meta = null,
        CancellationToken ct = default)
    {
        await using var source = new MemoryStream(data.ToArray(), writable: false);
        await WriteEntryAsync(new EntryHeader
        {
            Name = name,
            ItemId = itemId,
            Kind = EntryKind.Data,
            Length = data.Length,
            Meta = meta,
        }, source, compressible: true, ct).ConfigureAwait(false);
    }

    public Task WriteJsonAsync<T>(string name, string itemId, T value, Dictionary<string, string>? meta = null, CancellationToken ct = default) =>
        WriteDataAsync(name, itemId, Json.ToBytes(value), meta, ct);

    /// <returns>The number of content bytes actually read from <paramref name="source"/>.</returns>
    public Task<long> WriteFileAsync(string name, string itemId, Stream source, FileMeta meta, Dictionary<string, string>? extra = null,
        CancellationToken ct = default)
    {
        var compressible = compress && !PackageFormat.IncompressibleExtensions.Contains(Path.GetExtension(name));
        return WriteEntryAsync(new EntryHeader
        {
            Name = name,
            ItemId = itemId,
            Kind = EntryKind.File,
            Length = meta.Length,
            Attributes = (int)meta.Attributes,
            CreatedTicks = meta.CreationTimeUtc.Ticks,
            ModifiedTicks = meta.LastWriteTimeUtc.Ticks,
            Meta = extra,
        }, source, compressible, ct);
    }

    public async Task WriteDirectoryAsync(string name, string itemId, FileMeta meta, CancellationToken ct = default)
    {
        await WriteEntryAsync(new EntryHeader
        {
            Name = name,
            ItemId = itemId,
            Kind = EntryKind.Directory,
            Attributes = (int)meta.Attributes,
            CreatedTicks = meta.CreationTimeUtc.Ticks,
            ModifiedTicks = meta.LastWriteTimeUtc.Ticks,
        }, null, false, ct).ConfigureAwait(false);
    }

    private async Task<long> WriteEntryAsync(EntryHeader header, Stream? source, bool compressible, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (completed)
                throw new InvalidOperationException("The package has already been completed.");
            var headerBytes = Json.ToBytes(header);
            await WriteByteAsync(PackageFormat.EntryRecord, ct).ConfigureAwait(false);
            await WriteInt32Async(headerBytes.Length, ct).ConfigureAwait(false);
            await body.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await body.WriteAsync(PackageFormat.HeaderCheck(headerBytes), ct).ConfigureAwait(false);
            entryCount++;
            return source is null ? 0 : await WriteContentAsync(source, compressible, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException and not SourceReadException and not DestinationException ||
                                   ex is ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            throw new DestinationException(ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<long> WriteContentAsync(Stream source, bool compressible, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PackageFormat.ChunkSize);
        var packed = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(PackageFormat.ChunkSize));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = await source.ReadAtLeastAsync(buffer.AsMemory(0, PackageFormat.ChunkSize), PackageFormat.ChunkSize,
                        throwOnEndOfStream: false, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Mark the entry as abandoned so the package stays readable; the reader discards it.
                    await WriteInt32Async(PackageFormat.AbortedChunk, ct).ConfigureAwait(false);
                    throw new SourceReadException(ex);
                }
                if (n == 0)
                    break;
                hash.AppendData(buffer, 0, n);

                var stored = false;
                if (compressible)
                {
                    // Quality 1 keeps up with gigabit networks and USB 3 drives; give up on data that doesn't shrink.
                    if (BrotliEncoder.TryCompress(buffer.AsSpan(0, n), packed, out var packedLength, quality: 1, window: WindowFor(n)) &&
                        packedLength < n - n / 10)
                    {
                        await WriteInt32Async(-packedLength, ct).ConfigureAwait(false);
                        await WriteInt32Async(n, ct).ConfigureAwait(false);
                        await body.WriteAsync(packed.AsMemory(0, packedLength), ct).ConfigureAwait(false);
                        stored = true;
                    }
                    else
                    {
                        compressible = false;
                    }
                }
                if (!stored)
                {
                    await WriteInt32Async(n, ct).ConfigureAwait(false);
                    await body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }

                total += n;
                Progress?.Invoke(Interlocked.Add(ref contentBytes, n));
                if (n < PackageFormat.ChunkSize)
                    break;
            }
            await WriteInt32Async(0, ct).ConfigureAwait(false);
            await body.WriteAsync(hash.GetHashAndReset(), ct).ConfigureAwait(false);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(packed);
        }
    }

    /// <summary>Writes the end record. A package without one is reported as incomplete when read.</summary>
    public async Task CompleteAsync(bool cancelled = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (completed)
                return;
            completed = true;
            var footer = Json.ToBytes(new PackageFooter { EntryCount = entryCount, ContentBytes = ContentBytes, Cancelled = cancelled });
            await WriteByteAsync(PackageFormat.EndRecord, ct).ConfigureAwait(false);
            await WriteInt32Async(footer.Length, ct).ConfigureAwait(false);
            await body.WriteAsync(footer, ct).ConfigureAwait(false);
            await body.WriteAsync(PackageFormat.HeaderCheck(footer), ct).ConfigureAwait(false);
            if (aead is not null)
                await aead.FinishAsync(ct).ConfigureAwait(false);
            else
                await body.FlushAsync(ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException and not DestinationException || ex is ObjectDisposedException)
        {
            throw new DestinationException(ex);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The smallest Brotli window that covers the chunk: a full 4 MB window costs a large allocation per call,
    /// which dominated the time for folders of small files.
    /// </summary>
    internal static int WindowFor(int length) =>
        Math.Clamp(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(length - 1, 1)) + 1, 10, 22);

    private ValueTask WriteByteAsync(byte value, CancellationToken ct)
    {
        scratch[0] = value;
        return body.WriteAsync(scratch.AsMemory(0, 1), ct);
    }

    private ValueTask WriteInt32Async(int value, CancellationToken ct)
    {
        BinaryPrimitives.WriteInt32LittleEndian(scratch, value);
        return body.WriteAsync(scratch.AsMemory(0, 4), ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (aead is null)
                await body.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // The destination went away (network drop, drive removed); the caller already has the real error.
        }
        if (aead is not null)
            await aead.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
