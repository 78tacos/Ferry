using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ferry.Core.Infrastructure;

namespace Ferry.Core.Transport;

public enum MessageType : byte
{
    Confirm = 1,
    Hello = 2,
    Status = 3,
    Inventory = 4,
    Selection = 5,
    Data = 6,
    DataEnd = 7,
    Result = 8,
    Error = 9,
    Cancel = 10,
}

public sealed class PairingFailedException(string message) : Exception(message);

/// <summary>The other PC sent an error or cancelled.</summary>
public sealed class RemoteException(string message) : IOException(message);

/// <summary>
/// Encrypted, authenticated message channel between the two PCs.
/// <para>
/// Handshake: both sides send an ephemeral P-256 public key and a nonce. Session keys come from HKDF over the ECDH
/// secret mixed with a key stretched from the pairing code shown on the new PC, salted with the handshake transcript.
/// Each side then proves it derived the same keys by sending an encrypted confirmation. A wrong code (or anyone in
/// the middle) fails that step; passive listeners learn nothing, even about the code.
/// </para>
/// Frames: uint32 length | AES-256-GCM(type byte + payload) | 16-byte tag, with a per-direction counter nonce.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    private static readonly byte[] Magic = "FERRYNET"u8.ToArray();
    private const byte ProtocolVersion = 1;
    private const int MaxFrame = 64 << 20;
    private const int TagSize = 16;

    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly AesGcm sendKey;
    private readonly AesGcm receiveKey;
    private readonly uint sendDirection;
    private readonly uint receiveDirection;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly byte[] lengthBuffer = new byte[4];
    private ulong sendCounter;
    private ulong receiveCounter;

    private SecureChannel(TcpClient client, byte[] sendKey, byte[] receiveKey, uint sendDirection, uint receiveDirection)
    {
        this.client = client;
        stream = client.GetStream();
        this.sendKey = new AesGcm(sendKey, TagSize);
        this.receiveKey = new AesGcm(receiveKey, TagSize);
        this.sendDirection = sendDirection;
        this.receiveDirection = receiveDirection;
        RemoteAddress = client.Client.RemoteEndPoint?.ToString() ?? "";
    }

    public string RemoteAddress { get; }

    // ───────────────────────────── Handshake ─────────────────────────────

    /// <summary>Old PC side: connects to the new PC and pairs using the code shown there.</summary>
    public static async Task<SecureChannel> ConnectAsync(string host, int port, string code, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"Could not reach {host}. Check that Ferry is waiting on the new PC and both PCs are on the same network.");
            }
            catch (SocketException ex)
            {
                throw new IOException($"Could not reach {host}: {ex.Message}");
            }
            Configure(client);
            // Something that accepts the connection but never answers mustn't leave the old PC waiting forever.
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                return await HandshakeAsync(client, code, isInitiator: true, handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"{host} accepted the connection but didn't answer like Ferry does.");
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>New PC side: runs the handshake on an accepted connection.</summary>
    public static async Task<SecureChannel> AcceptAsync(TcpClient client, string code, CancellationToken ct)
    {
        Configure(client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await HandshakeAsync(client, code, isInitiator: false, timeout.Token).ConfigureAwait(false);
    }

    private static void Configure(TcpClient client)
    {
        client.NoDelay = true;
        client.SendBufferSize = 1 << 20;
        client.ReceiveBufferSize = 1 << 20;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    private static async Task<SecureChannel> HandshakeAsync(TcpClient client, string code, bool isInitiator, CancellationToken ct)
    {
        var stream = client.GetStream();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ourHello = BuildHello(ecdh.PublicKey.ExportSubjectPublicKeyInfo(), RandomNumberGenerator.GetBytes(32));

        byte[] theirHello;
        if (isInitiator)
        {
            await stream.WriteAsync(ourHello, ct).ConfigureAwait(false);
            theirHello = await ReadHelloAsync(stream, ct).ConfigureAwait(false);
        }
        else
        {
            theirHello = await ReadHelloAsync(stream, ct).ConfigureAwait(false);
            await stream.WriteAsync(ourHello, ct).ConfigureAwait(false);
        }

        var (initiatorHello, responderHello) = isInitiator ? (ourHello, theirHello) : (theirHello, ourHello);
        var transcript = SHA256.HashData([.. initiatorHello, .. responderHello]);
        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(ParseHello(theirHello).PublicKey, out _);
        var shared = ecdh.DeriveRawSecretAgreement(peerKey.PublicKey);
        var psk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(PairingCode.Normalize(code)), "ferry-pairing-v1"u8.ToArray(), 100_000,
            HashAlgorithmName.SHA256, 32);
        byte[] ikm = [.. shared, .. psk];
        var initiatorKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, transcript, "ferry initiator v1"u8.ToArray());
        var responderKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, transcript, "ferry responder v1"u8.ToArray());
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(ikm);

        var channel = isInitiator
            ? new SecureChannel(client, initiatorKey, responderKey, 1, 2)
            : new SecureChannel(client, responderKey, initiatorKey, 2, 1);
        CryptographicOperations.ZeroMemory(initiatorKey);
        CryptographicOperations.ZeroMemory(responderKey);

        try
        {
            // Key confirmation: the initiator proves first so a wrong code is caught before the new PC reveals anything.
            if (isInitiator)
            {
                await channel.SendAsync(MessageType.Confirm, transcript, ct).ConfigureAwait(false);
                await channel.ExpectConfirmAsync(transcript, ct).ConfigureAwait(false);
            }
            else
            {
                await channel.ExpectConfirmAsync(transcript, ct).ConfigureAwait(false);
                await channel.SendAsync(MessageType.Confirm, transcript, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or IOException or InvalidDataException)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new PairingFailedException(isInitiator
                ? "The new PC didn't accept the code. Check the code shown on the new PC (it changes after a wrong attempt) and try again."
                : "Someone tried to connect with the wrong code.");
        }
        return channel;
    }

    private async Task ExpectConfirmAsync(byte[] transcript, CancellationToken ct)
    {
        // Before pairing is proven, only a tiny confirmation frame is acceptable.
        var (type, payload) = await ReceiveAsync(ct, maxFrame: 256).ConfigureAwait(false);
        if (type != MessageType.Confirm || !CryptographicOperations.FixedTimeEquals(payload, transcript))
            throw new InvalidDataException("Bad confirmation.");
    }

    private static byte[] BuildHello(byte[] publicKey, byte[] nonce)
    {
        var hello = new byte[Magic.Length + 1 + 2 + publicKey.Length + nonce.Length];
        Magic.CopyTo(hello, 0);
        hello[Magic.Length] = ProtocolVersion;
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(Magic.Length + 1), (ushort)publicKey.Length);
        publicKey.CopyTo(hello, Magic.Length + 3);
        nonce.CopyTo(hello, Magic.Length + 3 + publicKey.Length);
        return hello;
    }

    private static async Task<byte[]> ReadHelloAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[Magic.Length + 3];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (!head.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The other side isn't Ferry.");
        if (head[Magic.Length] != ProtocolVersion)
            throw new PairingFailedException("The two PCs are running incompatible versions of Ferry. Use the same version on both.");
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(Magic.Length + 1));
        if (keyLength is 0 or > 512)
            throw new InvalidDataException("Bad handshake.");
        var rest = new byte[keyLength + 32];
        await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
        return [.. head, .. rest];
    }

    private static (byte[] PublicKey, byte[] Nonce) ParseHello(byte[] hello)
    {
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(Magic.Length + 1));
        return (hello[(Magic.Length + 3)..(Magic.Length + 3 + keyLength)], hello[(Magic.Length + 3 + keyLength)..]);
    }

    // ───────────────────────────── Messages ─────────────────────────────

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length + 1 > MaxFrame)
            throw new ArgumentException("Message too large.", nameof(payload));
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plainLength = payload.Length + 1;
            var frame = new byte[4 + plainLength + TagSize];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(plainLength + TagSize));
            var plain = new byte[plainLength];
            plain[0] = (byte)type;
            payload.Span.CopyTo(plain.AsSpan(1));
            Span<byte> nonce = stackalloc byte[12];
            BuildNonce(nonce, sendDirection, sendCounter++);
            sendKey.Encrypt(nonce, plain, frame.AsSpan(4, plainLength), frame.AsSpan(4 + plainLength, TagSize));
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task<(MessageType Type, byte[] Payload)> ReceiveAsync(CancellationToken ct, int maxFrame = MaxFrame)
    {
        if (broken is not null)
            throw new InvalidDataException(broken);
        try
        {
            await stream.ReadExactlyAsync(lengthBuffer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new IOException("The other PC closed the connection.");
        }
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (length < TagSize + 1 || length > maxFrame + TagSize)
            throw Break("Bad frame from the other PC.");
        var frame = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new IOException("The other PC closed the connection.");
        }
        var plain = new byte[length - TagSize];
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce, receiveDirection, receiveCounter++);
        try
        {
            receiveKey.Decrypt(nonce, frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length, TagSize), plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            // Counters are now out of step and data may be missing: the connection can't be trusted any more.
            throw Break("The connection was damaged or tampered with.");
        }
        return ((MessageType)plain[0], plain[1..]);
    }

    private string? broken;

    private InvalidDataException Break(string reason)
    {
        broken = reason;
        Abort();
        return new InvalidDataException(reason);
    }

    /// <summary>Shuts the connection down at once, so the other PC's pending reads and writes fail instead of waiting.</summary>
    public void Abort()
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already closed.
        }
    }

    public Task SendJsonAsync<T>(MessageType type, T value, CancellationToken ct) => SendAsync(type, Json.ToBytes(value), ct);

    /// <summary>Waits for a message of the given type, passing Status messages to <paramref name="onStatus"/>.</summary>
    public async Task<T> ReceiveJsonAsync<T>(MessageType expected, CancellationToken ct, Action<string>? onStatus = null)
    {
        while (true)
        {
            var (type, payload) = await ReceiveAsync(ct).ConfigureAwait(false);
            if (type == expected)
                return Json.FromBytes<T>(payload);
            switch (type)
            {
                case MessageType.Status:
                    onStatus?.Invoke(Json.FromBytes<StatusMessage>(payload).Text);
                    continue;
                case MessageType.Error:
                    throw new RemoteException(Json.FromBytes<ErrorMessage>(payload).Message);
                case MessageType.Cancel:
                    throw new RemoteException("The migration was cancelled on the other PC.");
                default:
                    throw new InvalidDataException($"Unexpected {type} message from the other PC.");
            }
        }
    }

    private static void BuildNonce(Span<byte> nonce, uint direction, ulong counter)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce, direction);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    /// <summary>A write-only stream carrying package data as Data messages; dispose or <see cref="ChannelWriteStream.CompleteAsync"/> sends DataEnd.</summary>
    public ChannelWriteStream OpenDataWriter() => new(this);

    public ChannelReadStream OpenDataReader() => new(this);

    public async ValueTask DisposeAsync()
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Already closed.
        }
        await stream.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
        sendKey.Dispose();
        receiveKey.Dispose();
        sendLock.Dispose();
    }
}

public sealed record StatusMessage(string Text);

public sealed record ErrorMessage(string Message);

public sealed class ChannelWriteStream(SecureChannel channel) : Stream
{
    private const int BufferSize = 512 * 1024;
    private readonly byte[] buffer = new byte[BufferSize];
    private int length;
    private bool completed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, BufferSize - length);
            data[..n].CopyTo(buffer.AsMemory(length));
            length += n;
            data = data[n..];
            if (length == BufferSize)
                await SendBufferAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => FlushAsync().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => length > 0 ? SendBufferAsync(cancellationToken) : Task.CompletedTask;

    private async Task SendBufferAsync(CancellationToken ct)
    {
        await channel.SendAsync(MessageType.Data, buffer.AsMemory(0, length), ct).ConfigureAwait(false);
        length = 0;
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        if (completed)
            return;
        completed = true;
        await FlushAsync(ct).ConfigureAwait(false);
        await channel.SendAsync(MessageType.DataEnd, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class ChannelReadStream(SecureChannel channel) : Stream
{
    private byte[] current = [];
    private int position;
    private bool ended;
    private Exception? failure;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Total package bytes received so far (compressed, as sent).</summary>
    public long BytesReceived { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (position == current.Length)
        {
            if (ended)
                return 0;
            // Once the stream has failed (cancelled, error, dropped), every later read fails the same way
            // rather than waiting on a connection that will never deliver more data.
            if (failure is not null)
                throw failure;
            try
            {
                var (type, payload) = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                switch (type)
                {
                    case MessageType.Data:
                        current = payload;
                        position = 0;
                        BytesReceived += payload.Length;
                        break;
                    case MessageType.DataEnd:
                        ended = true;
                        return 0;
                    case MessageType.Error:
                        throw new RemoteException(Json.FromBytes<ErrorMessage>(payload).Message);
                    case MessageType.Cancel:
                        throw new RemoteException("The migration was cancelled on the old PC.");
                    case MessageType.Status:
                        continue;
                    default:
                        throw new InvalidDataException($"Unexpected {type} message during transfer.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                failure = ex;
                throw;
            }
        }
        var n = Math.Min(buffer.Length, current.Length - position);
        current.AsMemory(position, n).CopyTo(buffer);
        position += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
