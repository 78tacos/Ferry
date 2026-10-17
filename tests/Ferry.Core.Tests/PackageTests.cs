using System.Text;
using Ferry.Core.Packaging;

namespace Ferry.Core.Tests;

public class PackageTests
{
    private static readonly DateTime Created = new(2024, 3, 1, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2025, 7, 9, 17, 5, 0, DateTimeKind.Utc);

    private static byte[] Compressible(int size) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Ferry moves your stuff. ", size / 24 + 1)))[..size];

    private static byte[] Random(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> WritePackage(string? password, Func<PackageWriter, Task> write, bool compressed = true)
    {
        using var ms = new MemoryStream();
        await using (var writer = await PackageWriter.CreateAsync(ms, new PackageHeader { SourceComputer = "OLDPC", Compressed = compressed }, password, default))
        {
            await write(writer);
            await writer.CompleteAsync();
        }
        return ms.ToArray();
    }

    private static Task<PackageReader> Open(byte[] package, string? password) =>
        PackageReader.OpenAsync(new MemoryStream(package), _ => password, ownsInput: true, default);

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("s3cret pässword", true)]
    public async Task RoundTripsEveryKindOfEntry(string? password, bool compressed)
    {
        var text = Compressible(3 * PackageFormat.ChunkSize + 1234);
        var noise = Random(PackageFormat.ChunkSize + 77);
        var package = await WritePackage(password, async w =>
        {
            await w.WriteJsonAsync("test/data.json", "item:1", new Dictionary<string, string> { ["hello"] = "world" });
            await w.WriteFileAsync("test/files/big.txt", "item:2", new MemoryStream(text),
                new FileMeta(FileAttributes.ReadOnly | FileAttributes.Archive, Created, Modified, text.Length), new() { ["src"] = @"C:\x\big.txt" });
            await w.WriteFileAsync("test/files/photo.jpg", "item:2", new MemoryStream(noise), new FileMeta(FileAttributes.Normal, Created, Modified, noise.Length));
            await w.WriteFileAsync("test/files/empty.txt", "item:2", new MemoryStream(), new FileMeta(FileAttributes.Normal, Created, Modified, 0));
            await w.WriteDirectoryAsync("test/files/emptydir", "item:2", new FileMeta(FileAttributes.Directory, Created, Modified, 0));
        }, compressed);

        await using var reader = await Open(package, password);
        Assert.Equal("OLDPC", reader.Header.SourceComputer);
        Assert.Equal(password is not null, reader.Header.Encryption is not null);

        var data = await reader.ReadNextAsync(default);
        Assert.Equal(EntryKind.Data, data!.Kind);
        Assert.Equal("world", (await data.ReadJsonAsync<Dictionary<string, string>>(default))["hello"]);

        var big = await reader.ReadNextAsync(default);
        Assert.Equal("test/files/big.txt", big!.Name);
        Assert.Equal("item:2", big.ItemId);
        Assert.Equal(Modified, big.LastWriteTimeUtc);
        Assert.Equal(Created, big.CreationTimeUtc);
        Assert.True(big.Attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(@"C:\x\big.txt", big.Header.GetMeta("src"));
        Assert.Equal(text, await big.ReadAllBytesAsync(default));

        var photo = await reader.ReadNextAsync(default);
        Assert.Equal(noise, await photo!.ReadAllBytesAsync(default));

        var empty = await reader.ReadNextAsync(default);
        Assert.Empty(await empty!.ReadAllBytesAsync(default));

        var dir = await reader.ReadNextAsync(default);
        Assert.Equal(EntryKind.Directory, dir!.Kind);

        Assert.Null(await reader.ReadNextAsync(default));
        Assert.Equal(5, reader.Footer!.EntryCount);
        Assert.False(reader.Footer.Cancelled);
    }

    [Fact]
    public async Task CompressesTextButStoresNoiseAsIs()
    {
        var text = Compressible(4 * PackageFormat.ChunkSize);
        var noise = Random(4 * PackageFormat.ChunkSize);
        var textPackage = await WritePackage(null, w => w.WriteFileAsync("t/a.txt", "i", new MemoryStream(text), new FileMeta(0, Created, Modified, text.Length)));
        var noisePackage = await WritePackage(null, w => w.WriteFileAsync("t/a.bin", "i", new MemoryStream(noise), new FileMeta(0, Created, Modified, noise.Length)));
        Assert.True(textPackage.Length < text.Length / 10);
        Assert.InRange(noisePackage.Length, noise.Length, noise.Length + 4096);
    }

    [Fact]
    public async Task SkippingContentLeavesTheReaderOnTheNextEntry()
    {
        var package = await WritePackage(null, async w =>
        {
            await w.WriteFileAsync("t/skip.txt", "i", new MemoryStream(Compressible(2 * PackageFormat.ChunkSize)), new FileMeta(0, Created, Modified, 0));
            await w.WriteDataAsync("t/keep.txt", "i", Encoding.UTF8.GetBytes("kept"));
        });
        await using var reader = await Open(package, null);
        await reader.ReadNextAsync(default); // never read its content
        var keep = await reader.ReadNextAsync(default);
        Assert.Equal("kept", Encoding.UTF8.GetString(await keep!.ReadAllBytesAsync(default)));
    }

    [Fact]
    public async Task WrongOrMissingPasswordIsReportedClearly()
    {
        var package = await WritePackage("right", w => w.WriteDataAsync("t/x", "i", new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<WrongPasswordException>(() => Open(package, "wrong"));
        await Assert.ThrowsAsync<PasswordRequiredException>(() => Open(package, null));
    }

    [Fact]
    public async Task TamperedEncryptedPackageIsRejected()
    {
        var package = await WritePackage("pw", w => w.WriteDataAsync("t/x", "i", Compressible(100_000)));
        package[^40] ^= 0x55;
        await using var reader = await Open(package, "pw");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            while (await reader.ReadNextAsync(default) is { } entry)
                await entry.ReadAllBytesAsync(default);
        });
    }

    [Fact]
    public async Task TamperedPlainPackageFailsItsChecksum()
    {
        var content = Random(50_000);
        var package = await WritePackage(null, w => w.WriteFileAsync("t/x.bin", "i", new MemoryStream(content), new FileMeta(0, Created, Modified, content.Length)),
            compressed: false);
        package[package.Length / 2] ^= 0xFF;
        await using var reader = await Open(package, null);
        var entry = await reader.ReadNextAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => entry!.ReadAllBytesAsync(default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pw")]
    public async Task TruncatedPackageIsReportedAsIncomplete(string? password)
    {
        var package = await WritePackage(password, async w =>
        {
            for (var i = 0; i < 5; i++)
                await w.WriteDataAsync($"t/{i}", "i", Random(200_000));
        });
        var truncated = package[..(package.Length * 2 / 3)];
        await using var reader = await Open(truncated, password);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            while (await reader.ReadNextAsync(default) is { } entry)
                await entry.ReadAllBytesAsync(default);
        });
    }

    [Fact]
    public async Task FileThatFailsMidReadIsAbandonedWithoutBreakingThePackage()
    {
        var package = await WritePackage(null, async w =>
        {
            await Assert.ThrowsAsync<SourceReadException>(() =>
                w.WriteFileAsync("t/locked.db", "i", new FailingStream(PackageFormat.ChunkSize + 10), new FileMeta(0, Created, Modified, 0)));
            await w.WriteDataAsync("t/after", "i", Encoding.UTF8.GetBytes("still here"));
        });
        await using var reader = await Open(package, null);
        var locked = await reader.ReadNextAsync(default);
        await Assert.ThrowsAsync<EntryAbortedException>(() => locked!.ReadAllBytesAsync(default));
        var after = await reader.ReadNextAsync(default);
        Assert.Equal("still here", Encoding.UTF8.GetString(await after!.ReadAllBytesAsync(default)));
    }

    [Fact]
    public async Task SplitFilesReadBackAsOne()
    {
        var dir = TestEnvironment.NewFolder("split");
        var path = Path.Combine(dir, "m.ferry");
        var content = Random(900_000);
        await using (var stream = PackageFiles.OpenWrite(path, partSize: 200_000))
        await using (var writer = await PackageWriter.CreateAsync(stream, new PackageHeader { SplitPartSize = 200_000 }, "pw", default))
        {
            await writer.WriteFileAsync("t/a.bin", "i", new MemoryStream(content), new FileMeta(0, Created, Modified, content.Length));
            await writer.CompleteAsync();
        }
        Assert.True(PackageFiles.ExistingParts(path).Count >= 5);

        await using var reader = await PackageReader.OpenAsync(PackageFiles.OpenRead(path), _ => "pw", ownsInput: true, default);
        var entry = await reader.ReadNextAsync(default);
        Assert.Equal(content, await entry!.ReadAllBytesAsync(default));
    }

    [Fact]
    public async Task StaleExtraPartsAreRemovedWhenOverwriting()
    {
        var dir = TestEnvironment.NewFolder("split-stale");
        var path = Path.Combine(dir, "m.ferry");
        await File.WriteAllTextAsync(PackageFiles.PartPath(path, 2), "old");
        await File.WriteAllTextAsync(PackageFiles.PartPath(path, 3), "old");
        await using (var stream = PackageFiles.OpenWrite(path, partSize: null))
        await using (var writer = await PackageWriter.CreateAsync(stream, new PackageHeader(), null, default))
            await writer.CompleteAsync();
        Assert.Single(PackageFiles.ExistingParts(path));
    }

    [Fact]
    public async Task StrayPartsNextToASingleFilePackageAreIgnored()
    {
        var dir = TestEnvironment.NewFolder("split-stray");
        var path = Path.Combine(dir, "m.ferry");
        await using (var stream = PackageFiles.OpenWrite(path, partSize: null))
        await using (var writer = await PackageWriter.CreateAsync(stream, new PackageHeader(), "pw", default))
        {
            await writer.WriteDataAsync("t/x", "i", Random(10_000));
            await writer.CompleteAsync();
        }
        // An old split migration's leftovers, copied next to it later.
        await File.WriteAllBytesAsync(PackageFiles.PartPath(path, 2), Random(5000));

        await using var reader = await PackageReader.OpenAsync(PackageFiles.OpenRead(path), _ => "pw", ownsInput: true, default);
        var entry = await reader.ReadNextAsync(default);
        Assert.Equal(10_000, (await entry!.ReadAllBytesAsync(default)).Length);
        Assert.Null(await reader.ReadNextAsync(default));
    }

    [Fact]
    public async Task DamagedRecordHeadersAreCaughtInPlainPackages()
    {
        var package = await WritePackage(null, w => w.WriteDataAsync("files/S-1-5-21-1/kf/Documents/a.txt", "files:a", Random(100)), compressed: false);
        var at = Encoding.UTF8.GetString(package).IndexOf("Documents", StringComparison.Ordinal);
        package[at + 5] ^= 0x01;
        await using var reader = await Open(package, null);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadNextAsync(default));
    }

    /// <summary>Returns some data, then fails like a file locked part-way through.</summary>
    private sealed class FailingStream(int failAfter) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= failAfter)
                throw new IOException("The process cannot access the file because another process has locked a portion of the file.");
            var n = Math.Min(count, failAfter - position);
            position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
