using System.Net;
using System.Net.Sockets;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Transport;

public sealed class HelloMessage
{
    public string Role { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public MachineInfo Machine { get; set; } = new();
    public List<UserProfileInfo> Users { get; set; } = [];
}

public sealed class SelectionMessage
{
    public Selection Selection { get; set; } = new();
    public CaptureOptions Options { get; set; } = new();
}

/// <summary>
/// The new PC's side of a network migration: listens (with a temporary firewall opening and LAN discovery),
/// shows a pairing code, and hands back a <see cref="ReceiverSession"/> once the old PC pairs successfully.
/// </summary>
public sealed class NetworkReceiver : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly DiscoveryResponder? discovery;
    private readonly TemporaryFirewallRule? firewall;
    private readonly string? fixedCode;
    private readonly KeepAwake awake = KeepAwake.Start("Ferry is waiting for the old PC to connect");

    private NetworkReceiver(TcpListener listener, DiscoveryResponder? discovery, TemporaryFirewallRule? firewall, string? fixedCode)
    {
        this.listener = listener;
        this.discovery = discovery;
        this.firewall = firewall;
        this.fixedCode = fixedCode;
        Code = fixedCode is null ? PairingCode.Generate() : PairingCode.Display(fixedCode);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public string Code { get; private set; }
    public int Port { get; }
    public IReadOnlyList<string> Addresses => LocalAddresses.IPv4();

    /// <summary>The code was replaced after a wrong attempt.</summary>
    public event Action<string>? CodeChanged;

    /// <param name="fixedCode">A code agreed in advance (silent mode); otherwise a fresh one is generated and rotated.</param>
    /// <param name="bindAddress">Address to listen on. Loopback (for testing on one PC) skips discovery and the firewall.</param>
    public static NetworkReceiver Start(string? fixedCode = null, int port = NetworkDefaults.TransferPort, string? userLabel = null,
        IPAddress? bindAddress = null)
    {
        var address = bindAddress ?? IPAddress.Any;
        var loopbackOnly = IPAddress.IsLoopback(address);
        var firewall = loopbackOnly ? null : TemporaryFirewallRule.Open();
        TcpListener listener;
        try
        {
            listener = new TcpListener(address, port);
            listener.Start();
        }
        catch (SocketException)
        {
            // The usual port is taken; any port works because discovery tells the old PC which one.
            listener = new TcpListener(address, 0);
            listener.Start();
        }
        var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Log.Info($"Waiting for the old PC on {address}:{actualPort}");
        return new NetworkReceiver(listener, loopbackOnly ? null : new DiscoveryResponder(actualPort, userLabel), firewall, fixedCode);
    }

    public async Task<ReceiverSession> WaitForSenderAsync(CancellationToken ct)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            var remote = client.Client.RemoteEndPoint?.ToString();
            try
            {
                var channel = await SecureChannel.AcceptAsync(client, Code, ct).ConfigureAwait(false);
                Log.Info($"Paired with {remote}");
                return await ReceiverSession.StartAsync(channel, ct).ConfigureAwait(false);
            }
            catch (PairingFailedException ex)
            {
                Log.Warn($"Pairing attempt from {remote} failed: {ex.Message}");
                client.Dispose();
                if (fixedCode is null)
                {
                    // One guess per code: an attacker gets nothing from trying.
                    Code = PairingCode.Generate();
                    CodeChanged?.Invoke(Code);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Debug($"Connection from {remote} dropped during pairing: {ex.Message}");
                client.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        listener.Stop();
        discovery?.Dispose();
        firewall?.Dispose();
        awake.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A paired connection on the new PC: receives the old PC's inventory, sends back choices, restores the stream.</summary>
public sealed class ReceiverSession : IAsyncDisposable
{
    private readonly SecureChannel channel;

    /// <summary>Sleeping would drop the connection while someone is still choosing what to move.</summary>
    private readonly KeepAwake awake = KeepAwake.Start("Ferry is connected to the old PC");

    private ReceiverSession(SecureChannel channel, HelloMessage peer)
    {
        this.channel = channel;
        Peer = peer;
    }

    public HelloMessage Peer { get; }
    public MachineInfo SourceMachine => Peer.Machine;
    public string RemoteAddress => channel.RemoteAddress;

    internal static async Task<ReceiverSession> StartAsync(SecureChannel channel, CancellationToken ct)
    {
        var peer = await channel.ReceiveJsonAsync<HelloMessage>(MessageType.Hello, ct).ConfigureAwait(false);
        await channel.SendJsonAsync(MessageType.Hello, new HelloMessage
        {
            Role = "receiver",
            AppVersion = SystemInfo.AppVersion,
            Machine = SystemInfo.Collect(),
            Users = UserProfiles.Enumerate(),
        }, ct).ConfigureAwait(false);
        return new ReceiverSession(channel, peer);
    }

    /// <summary>Waits while the old PC scans (its progress arrives through <paramref name="onStatus"/>).</summary>
    public Task<Inventory> ReceiveInventoryAsync(Action<string>? onStatus, CancellationToken ct) =>
        channel.ReceiveJsonAsync<Inventory>(MessageType.Inventory, ct, onStatus);

    /// <summary>Sends the choices, then restores what the old PC streams back, as it arrives.</summary>
    public async Task<MigrationReport> RestoreAsync(MigrationEngine engine, Selection selection, CaptureOptions captureOptions, RestoreOptions options,
        IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        await channel.SendJsonAsync(MessageType.Selection, new SelectionMessage { Selection = selection, Options = captureOptions }, ct).ConfigureAwait(false);
        var data = channel.OpenDataReader();
        MigrationReport report;
        try
        {
            await using var reader = await PackageReader.OpenAsync(data, null, ownsInput: false, ct).ConfigureAwait(false);
            var (_, manifest, _) = await reader.ReadManifestAsync(ct).ConfigureAwait(false);
            // The old PC sent exactly what was chosen, with each item's chosen handling recorded in the manifest.
            report = await engine.RestoreAsync(reader, manifest, Selection.All(manifest), options, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TrySendAsync(MessageType.Cancel, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            channel.Abort();
            throw;
        }

        if (!report.Cancelled && report.FatalError is null)
        {
            try
            {
                var buffer = new byte[64 * 1024];
                while (await data.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Log.Debug($"Stream ended early after restore: {ex.Message}");
            }
            await TrySendAsync(MessageType.Result, Json.ToBytes(report)).ConfigureAwait(false);
            return report;
        }

        // Stopped here. The old PC may be blocked sending data nobody reads: tell it why, then hang up so its
        // writes fail at once (it reads the reason from what arrived before the connection closed).
        if (report.Cancelled)
            await TrySendAsync(MessageType.Cancel, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        else
            await TrySendAsync(MessageType.Error, Json.ToBytes(new ErrorMessage($"The new PC stopped: {report.FatalError}"))).ConfigureAwait(false);
        channel.Abort();
        return report;
    }

    public Task CancelAsync() => TrySendAsync(MessageType.Cancel, ReadOnlyMemory<byte>.Empty);

    private ValueTask CloseAsync()
    {
        awake.Dispose();
        return channel.DisposeAsync();
    }

    private async Task TrySendAsync(MessageType type, ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await channel.SendAsync(type, payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            Log.Debug($"Could not tell the old PC ({type}): {ex.Message}");
        }
    }

    public ValueTask DisposeAsync() => CloseAsync();
}

public enum SenderStage { Scanning, WaitingForChoices, Sending, WaitingForNewPc, Done }

/// <summary>The old PC's side: connects with the pairing code, scans, sends the inventory, streams what the new PC picks.</summary>
public sealed class SenderSession : IAsyncDisposable
{
    private readonly SecureChannel channel;
    private readonly KeepAwake awake = KeepAwake.Start("Ferry is connected to the new PC");

    private SenderSession(SecureChannel channel, HelloMessage peer)
    {
        this.channel = channel;
        Peer = peer;
    }

    public HelloMessage Peer { get; }
    public MachineInfo TargetMachine => Peer.Machine;

    public event Action<SenderStage>? StageChanged;

    public static async Task<SenderSession> ConnectAsync(string host, int port, string code, CancellationToken ct)
    {
        var channel = await SecureChannel.ConnectAsync(host, port, code, ct).ConfigureAwait(false);
        try
        {
            await channel.SendJsonAsync(MessageType.Hello, new HelloMessage
            {
                Role = "sender",
                AppVersion = SystemInfo.AppVersion,
                Machine = SystemInfo.Collect(),
            }, ct).ConfigureAwait(false);
            var peer = await channel.ReceiveJsonAsync<HelloMessage>(MessageType.Hello, ct).ConfigureAwait(false);
            Log.Info($"Connected to {peer.Machine.ComputerName}");
            return new SenderSession(channel, peer);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <returns>This PC's capture report, and the new PC's restore report when it sent one back.</returns>
    public async Task<(MigrationReport Capture, MigrationReport? Restore)> RunAsync(MigrationEngine engine, ScanOptions scanOptions,
        IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        StageChanged?.Invoke(SenderStage.Scanning);
        string? latestStatus = null;
        var scanProgress = new InlineProgress<MigrationProgress>(p =>
        {
            latestStatus = p.CurrentDetail ?? p.Phase;
            progress?.Report(p);
        });
        using var scanDone = new CancellationTokenSource();
        var statusPump = Task.Run(async () =>
        {
            string? sent = null;
            while (!scanDone.IsCancellationRequested)
            {
                if (latestStatus is { } s && s != sent)
                {
                    sent = s;
                    await channel.SendJsonAsync(MessageType.Status, new StatusMessage(s), ct).ConfigureAwait(false);
                }
                try
                {
                    await Task.Delay(500, scanDone.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);

        Inventory inventory;
        try
        {
            inventory = await engine.ScanAsync(scanOptions, scanProgress, ct).ConfigureAwait(false);
        }
        finally
        {
            await scanDone.CancelAsync().ConfigureAwait(false);
            try
            {
                await statusPump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Reported by the next send.
            }
        }

        await channel.SendJsonAsync(MessageType.Inventory, inventory, ct).ConfigureAwait(false);
        StageChanged?.Invoke(SenderStage.WaitingForChoices);
        var choice = await channel.ReceiveJsonAsync<SelectionMessage>(MessageType.Selection, ct).ConfigureAwait(false);

        StageChanged?.Invoke(SenderStage.Sending);
        var data = channel.OpenDataWriter();
        MigrationReport capture;
        await using (var writer = await PackageWriter.CreateAsync(data, new PackageHeader
                     {
                         SourceComputer = inventory.Machine.ComputerName,
                         AppVersion = SystemInfo.AppVersion,
                         Compressed = true,
                     }, password: null, ct).ConfigureAwait(false))
        {
            capture = await engine.CaptureAsync(inventory, choice.Selection, choice.Options, writer, progress, ct).ConfigureAwait(false);
        }
        capture.Target = TargetMachine;

        if (capture.FatalError is not null && await RemoteReasonAsync().ConfigureAwait(false) is { } reason)
        {
            // Writing failed because the new PC stopped; say why instead of "could not write".
            capture.FatalError = reason.Cancelled ? null : reason.Message;
            capture.Cancelled = reason.Cancelled;
            if (reason.Cancelled)
                capture.Notes.Add("The migration was cancelled on the new PC.");
            return (capture, null);
        }
        if (capture.Cancelled || capture.FatalError is not null)
        {
            try
            {
                await channel.SendAsync(MessageType.Cancel, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The new PC is already gone.
            }
            channel.Abort();
            return (capture, null);
        }

        await data.CompleteAsync(ct).ConfigureAwait(false);
        StageChanged?.Invoke(SenderStage.WaitingForNewPc);
        MigrationReport? restore = null;
        try
        {
            // Installs and finishing steps can take a while on the new PC.
            restore = await channel.ReceiveJsonAsync<MigrationReport>(MessageType.Result, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Log.Warn($"The new PC didn't send its results: {ex.Message}");
        }
        StageChanged?.Invoke(SenderStage.Done);
        return (capture, restore);
    }

    /// <summary>After a failed send, looks for a Cancel or Error the new PC sent before hanging up.</summary>
    private async Task<(bool Cancelled, string Message)?> RemoteReasonAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var (type, payload) = await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                switch (type)
                {
                    case MessageType.Cancel:
                        return (true, "The migration was cancelled on the new PC.");
                    case MessageType.Error:
                        return (false, Json.FromBytes<ErrorMessage>(payload).Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        awake.Dispose();
        return channel.DisposeAsync();
    }
}

/// <summary>An IProgress that runs its callback synchronously on the reporting thread (Progress&lt;T&gt; posts to a sync context).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
