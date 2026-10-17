using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ferry.Core.Packaging;

public sealed class PasswordRequiredException() : Exception("This migration file is password protected. Enter the password it was saved with.");

public sealed class WrongPasswordException() : Exception("The password is not correct for this migration file.");

/// <summary>Password-based encryption for migration files: PBKDF2-SHA256 → AES-256-GCM in authenticated segments.</summary>
public static class PackageCrypto
{
    public const int Iterations = 600_000;
    public const int SegmentSize = 256 * 1024;
    private const int NoncePrefixLength = 7;
    private static readonly byte[] KeyCheckLabel = "ferry-key-check-v1"u8.ToArray();

    public static (EncryptionInfo Info, byte[] Key) CreateForPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var prefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        var key = Derive(password, salt, Iterations);
        var info = new EncryptionInfo
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            NoncePrefix = Convert.ToBase64String(prefix),
            KeyCheck = Convert.ToBase64String(KeyCheck(key)),
            SegmentSize = SegmentSize,
        };
        return (info, key);
    }

    public static byte[] DeriveKey(string password, EncryptionInfo info)
    {
        if (info.Algorithm != "AES-256-GCM-STREAM" || info.Kdf != "PBKDF2-SHA256")
            throw new InvalidDataException($"Unsupported encryption {info.Algorithm}/{info.Kdf}.");
        // The header isn't authenticated until the key is known, so refuse values that would stall or exhaust memory.
        if (info.Iterations is < 100_000 or > 10_000_000 || info.SegmentSize is < 4096 or > 16 << 20)
            throw new InvalidDataException("The migration file header is damaged (implausible encryption settings).");
        var key = Derive(password, Convert.FromBase64String(info.Salt), info.Iterations);
        if (!CryptographicOperations.FixedTimeEquals(KeyCheck(key), Convert.FromBase64String(info.KeyCheck)))
            throw new WrongPasswordException();
        return key;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] KeyCheck(byte[] key) => HMACSHA256.HashData(key, KeyCheckLabel)[..16];

    internal static void BuildNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint counter, bool final)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixLength..], counter);
        nonce[11] = final ? (byte)1 : (byte)0;
    }
}

/// <summary>
/// Encrypts a stream as a sequence of AES-GCM segments (the "STREAM" construction): each segment's nonce
/// carries its index and a last-segment flag, so reordering, truncation and tampering are all detected.
/// The last segment is always shorter than a full one, which is how the reader recognises it.
/// </summary>
internal sealed class AeadWriteStream : Stream
{
    private const int TagSize = 16;
    private readonly Stream inner;
    private readonly AesGcm aes;
    private readonly byte[] prefix;
    private readonly byte[] aad;
    private readonly byte[] plain;
    private readonly byte[] output;
    private readonly bool leaveOpen;
    private int plainLength;
    private uint counter;
    private bool finished;

    public AeadWriteStream(Stream inner, byte[] key, byte[] prefix, int segmentSize, byte[] aad, bool leaveOpen)
    {
        this.inner = inner;
        this.prefix = prefix;
        this.aad = aad;
        this.leaveOpen = leaveOpen;
        aes = new AesGcm(key, TagSize);
        plain = new byte[segmentSize];
        output = new byte[segmentSize + TagSize];
    }

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
            if (plainLength == plain.Length)
                inner.Write(output, 0, Seal(final: false));
            var n = Math.Min(buffer.Length, plain.Length - plainLength);
            buffer[..n].CopyTo(plain.AsSpan(plainLength));
            plainLength += n;
            buffer = buffer[n..];
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0)
        {
            if (plainLength == plain.Length)
                await inner.WriteAsync(output.AsMemory(0, Seal(final: false)), cancellationToken).ConfigureAwait(false);
            var n = Math.Min(buffer.Length, plain.Length - plainLength);
            buffer.Span[..n].CopyTo(plain.AsSpan(plainLength));
            plainLength += n;
            buffer = buffer[n..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Writes the final segment. Nothing may be written afterwards.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        if (finished)
            return;
        finished = true;
        if (plainLength == plain.Length)
            await inner.WriteAsync(output.AsMemory(0, Seal(final: false)), ct).ConfigureAwait(false);
        await inner.WriteAsync(output.AsMemory(0, Seal(final: true)), ct).ConfigureAwait(false);
        await inner.FlushAsync(ct).ConfigureAwait(false);
    }

    private int Seal(bool final)
    {
        Span<byte> nonce = stackalloc byte[12];
        PackageCrypto.BuildNonce(nonce, prefix, counter++, final);
        aes.Encrypt(nonce, plain.AsSpan(0, plainLength), output.AsSpan(0, plainLength), output.AsSpan(plainLength, TagSize), aad);
        var total = plainLength + TagSize;
        plainLength = 0;
        return total;
    }

    public override void Flush()
    {
        // Segments are only written when full; flushing a partial one would end the stream.
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            if (!leaveOpen)
                inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AeadReadStream : Stream
{
    private const int TagSize = 16;
    private readonly Stream inner;
    private readonly AesGcm aes;
    private readonly byte[] prefix;
    private readonly byte[] aad;
    private readonly byte[] input;
    private readonly byte[] plain;
    private readonly bool leaveOpen;
    private int plainLength;
    private int plainPosition;
    private uint counter;
    private bool finalSeen;

    public AeadReadStream(Stream inner, byte[] key, byte[] prefix, int segmentSize, byte[] aad, bool leaveOpen)
    {
        this.inner = inner;
        this.prefix = prefix;
        this.aad = aad;
        this.leaveOpen = leaveOpen;
        aes = new AesGcm(key, TagSize);
        input = new byte[segmentSize + TagSize];
        plain = new byte[segmentSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return 0;
        while (plainPosition == plainLength)
        {
            if (finalSeen)
                return 0;
            await OpenNextSegmentAsync(cancellationToken).ConfigureAwait(false);
        }
        var n = Math.Min(buffer.Length, plainLength - plainPosition);
        plain.AsMemory(plainPosition, n).CopyTo(buffer);
        plainPosition += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async Task OpenNextSegmentAsync(CancellationToken ct)
    {
        var got = await inner.ReadAtLeastAsync(input, input.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got < TagSize)
            throw new InvalidDataException("The migration file is incomplete: it ends before its last encrypted block.");

        var final = got < input.Length;
        var length = got - TagSize;
        Span<byte> nonce = stackalloc byte[12];
        PackageCrypto.BuildNonce(nonce, prefix, counter++, final);
        try
        {
            aes.Decrypt(nonce, input.AsSpan(0, length), input.AsSpan(length, TagSize), plain.AsSpan(0, length), aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("The migration file is damaged or has been modified (an encrypted block failed verification).");
        }
        plainLength = length;
        plainPosition = 0;
        finalSeen = final;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            if (!leaveOpen)
                inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
