using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Ferry.Core.Infrastructure;

namespace Ferry.Core.Transport;

public sealed record PeerAnnouncement(string App, int Version, string Name, int Port, string Os, string? User);

public sealed record DiscoveredPeer(string Name, string Address, int Port, string Os, string? User, DateTime LastSeenUtc)
{
    public string Display => string.IsNullOrEmpty(User) ? $"{Name} ({Address})" : $"{Name} — {User} ({Address})";
}

public static class NetworkDefaults
{
    public const int DiscoveryPort = 47420;
    public const int TransferPort = 47421;
    internal static readonly byte[] Query = "FERRY?v1"u8.ToArray();
}

/// <summary>
/// Runs on the new PC: answers "is anyone waiting?" broadcasts from old PCs on the local network.
/// The new PC only answers, so the old PC needs no inbound firewall opening.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly UdpClient? udp;
    private readonly CancellationTokenSource cts = new();

    public DiscoveryResponder(int transferPort, string? user)
    {
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkDefaults.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            Log.Warn($"Network discovery is unavailable ({ex.Message}); the old PC can still connect by name or address.");
            udp?.Dispose();
            udp = null;
            return;
        }
        var announcement = Json.ToBytes(new PeerAnnouncement("ferry", 1, Environment.MachineName, transferPort, SystemInfo.Collect().OsSummary, user));
        _ = Task.Run(() => RespondLoopAsync(announcement, cts.Token));
    }

    private async Task RespondLoopAsync(byte[] announcement, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && udp is not null)
        {
            try
            {
                var request = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (request.Buffer.AsSpan().SequenceEqual(NetworkDefaults.Query))
                    await udp.SendAsync(announcement, request.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested)
                    return;
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        udp?.Dispose();
        cts.Dispose();
    }
}

/// <summary>Runs on the old PC: broadcasts every couple of seconds and collects the new PCs that answer.</summary>
public sealed class DiscoveryClient : IDisposable
{
    private readonly UdpClient udp;
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<string, DiscoveredPeer> peers = new(StringComparer.OrdinalIgnoreCase);

    public DiscoveryClient()
    {
        udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        _ = Task.Run(() => ListenLoopAsync(cts.Token));
        _ = Task.Run(() => BroadcastLoopAsync(cts.Token));
    }

    /// <summary>Raised (on a background thread) whenever the list of waiting PCs changes.</summary>
    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersChanged;

    public IReadOnlyList<DiscoveredPeer> Peers =>
        peers.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc < TimeSpan.FromSeconds(10)).OrderBy(p => p.Name).ToList();

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var address in BroadcastAddresses())
            {
                try
                {
                    await udp.SendAsync(NetworkDefaults.Query, new IPEndPoint(address, NetworkDefaults.DiscoveryPort), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // An interface without a route; try the others.
                }
            }
            // Drop PCs that stopped answering.
            var stale = peers.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc > TimeSpan.FromSeconds(10)).ToList();
            foreach (var peer in stale)
                peers.TryRemove(peer.Address, out _);
            if (stale.Count > 0)
                PeersChanged?.Invoke(Peers);
            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var announcement = Json.FromBytes<PeerAnnouncement>(response.Buffer);
                if (announcement.App != "ferry")
                    continue;
                var address = response.RemoteEndPoint.Address.ToString();
                var isNew = !peers.ContainsKey(address);
                peers[address] = new DiscoveredPeer(announcement.Name, address, announcement.Port, announcement.Os, announcement.User, DateTime.UtcNow);
                if (isNew)
                    PeersChanged?.Invoke(Peers);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or System.Text.Json.JsonException or InvalidDataException)
            {
                if (ct.IsCancellationRequested)
                    return;
            }
        }
    }

    /// <summary>The limited broadcast address plus each IPv4 interface's subnet broadcast address.</summary>
    internal static List<IPAddress> BroadcastAddresses()
    {
        var addresses = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                var address = new IPAddress(broadcast);
                if (!addresses.Contains(address))
                    addresses.Add(address);
            }
        }
        return addresses;
    }

    public void Dispose()
    {
        cts.Cancel();
        udp.Dispose();
        cts.Dispose();
    }
}

/// <summary>The new PC's own addresses, to show next to the pairing code for typing in by hand.</summary>
public static class LocalAddresses
{
    public static List<string> IPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();
}

/// <summary>Opens Windows Firewall for Ferry on the new PC while it waits, and closes it again afterwards.</summary>
public sealed class TemporaryFirewallRule : IDisposable
{
    private const string RuleName = "Ferry migration (temporary)";
    private readonly bool created;

    private TemporaryFirewallRule(bool created) => this.created = created;

    public static TemporaryFirewallRule Open()
    {
        if (!Elevation.IsAdministrator || Environment.ProcessPath is not { } exe)
            return new TemporaryFirewallRule(false);
        var netsh = ProcessRunner.System32("netsh.exe");
        // Remove a leftover from a run that crashed, then add a program rule (covers both TCP and UDP).
        ProcessRunner.RunRawAsync(netsh, $"advfirewall firewall delete rule name=\"{RuleName}\"", CancellationToken.None).GetAwaiter().GetResult();
        var result = ProcessRunner.RunRawAsync(netsh,
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any",
            CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded)
            Log.Warn($"Could not open the firewall: {result.CombinedOutput.Trim()}");
        else
            Log.Info("Opened Windows Firewall for Ferry while waiting for the old PC");
        return new TemporaryFirewallRule(result.Succeeded);
    }

    public void Dispose()
    {
        if (!created)
            return;
        ProcessRunner.RunRawAsync(ProcessRunner.System32("netsh.exe"), $"advfirewall firewall delete rule name=\"{RuleName}\"", CancellationToken.None)
            .GetAwaiter().GetResult();
        Log.Info("Closed the temporary firewall opening");
    }
}
