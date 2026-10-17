namespace Ferry.Core.Packaging;

/// <summary>
/// Migration files can be split into parts (name.ferry, name.ferry.002, name.ferry.003…) so they fit on
/// FAT32 drives, which cannot hold files over 4 GB. Readers concatenate the parts transparently.
/// </summary>
public static class PackageFiles
{
    /// <summary>Just under FAT32's 4 GiB file limit.</summary>
    public const long Fat32PartSize = 4L * 1024 * 1024 * 1024 - 64 * 1024 * 1024;

    public static string PartPath(string basePath, int partNumber) => partNumber <= 1 ? basePath : $"{basePath}.{partNumber:000}";

    public static List<string> ExistingParts(string basePath)
    {
        var parts = new List<string>();
        for (var n = 1; File.Exists(PartPath(basePath, n)); n++)
            parts.Add(PartPath(basePath, n));
        return parts;
    }

    public static long TotalSize(string basePath) => ExistingParts(basePath).Sum(p => new FileInfo(p).Length);

    /// <summary>The part size to use for a destination, or null when no splitting is needed.</summary>
    public static long? AutoPartSize(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var format = new DriveInfo(root).DriveFormat;
            return format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || format.Equals("FAT", StringComparison.OrdinalIgnoreCase)
                ? Fat32PartSize
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static Stream OpenWrite(string basePath, long? partSize)
    {
        // Leftover parts from an earlier, longer file with the same name would be read back as garbage.
        for (var n = 2; File.Exists(PartPath(basePath, n)); n++)
            File.Delete(PartPath(basePath, n));
        return partSize is { } size ? new SplitFileWriteStream(basePath, size) : CreatePart(basePath);
    }

    /// <summary>
    /// Opens a package for reading, joining its parts only if its header says it was written in parts, so stray
    /// .002 files left next to a single-file package from an older migration are never read as part of it.
    /// </summary>
    public static Stream OpenRead(string basePath) =>
        IsSplit(basePath) ? new SplitFileReadStream(basePath) : OpenPart(basePath);

    private static bool IsSplit(string basePath)
    {
        if (!File.Exists(PartPath(basePath, 2)))
            return false;
        try
        {
            using var stream = OpenPart(basePath);
            Span<byte> prefix = stackalloc byte[12];
            stream.ReadExactly(prefix);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]);
            if (!prefix[..8].SequenceEqual(PackageFormat.Magic) || length is <= 0 or > PackageFormat.MaxHeaderLength)
                return false;
            var header = new byte[length];
            stream.ReadExactly(header);
            return Infrastructure.Json.FromBytes<PackageHeader>(header).SplitPartSize is > 0;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            // Let the package reader report the real problem.
            return false;
        }
    }

    internal static FileStream CreatePart(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static FileStream OpenPart(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
}

internal sealed class SplitFileWriteStream(string basePath, long partSize) : Stream
{
    private FileStream current = PackageFiles.CreatePart(basePath);
    private int partNumber = 1;
    private long written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            RollIfFull();
            var n = (int)Math.Min(buffer.Length, partSize - written);
            current.Write(buffer[..n]);
            written += n;
            buffer = buffer[n..];
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0)
        {
            RollIfFull();
            var n = (int)Math.Min(buffer.Length, partSize - written);
            await current.WriteAsync(buffer[..n], cancellationToken).ConfigureAwait(false);
            written += n;
            buffer = buffer[n..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void RollIfFull()
    {
        if (written < partSize)
            return;
        current.Dispose();
        partNumber++;
        current = PackageFiles.CreatePart(PackageFiles.PartPath(basePath, partNumber));
        written = 0;
    }

    public override void Flush() => current.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => current.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            current.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class SplitFileReadStream(string basePath) : Stream
{
    private FileStream? current = PackageFiles.OpenPart(basePath);
    private int partNumber = 1;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (current is not null)
        {
            var n = await current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0 || buffer.Length == 0)
                return n;
            await current.DisposeAsync().ConfigureAwait(false);
            partNumber++;
            var next = PackageFiles.PartPath(basePath, partNumber);
            current = File.Exists(next) ? PackageFiles.OpenPart(next) : null;
        }
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            current?.Dispose();
        base.Dispose(disposing);
    }
}
