using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FileShare.Win;

class Peer
{
    public string Name = "", Platform = "", Ip = "";
    public int TcpPort;
    public DateTime LastSeen;
}

// UDP broadcast discovery. One socket both sends (every 2s) and receives; peers pruned after 6s.
sealed class Discovery : IDisposable
{
    readonly ConcurrentDictionary<string, Peer> _peers = new();
    readonly Socket _udp;
    readonly System.Threading.Timer _timer;
    readonly CancellationTokenSource _cts = new();
    public event Action? Changed;

    public Discovery()
    {
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Bind(new IPEndPoint(IPAddress.Any, Proto.UdpPort));
        _udp.EnableBroadcast = true;
        _ = ReceiveLoop();
            _timer = new System.Threading.Timer(_ => _ = Broadcast(), null, 0, 2000);
    }

    async Task Broadcast()
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Proto.BroadcastJson(Proto.DeviceName, "windows"));
            foreach (var target in BroadcastTargets())
                await _udp.SendToAsync(bytes, SocketFlags.None, new IPEndPoint(target, Proto.UdpPort));
        }
        catch { /* no network yet — next tick */ }
    }

    // Auto-detect every live IPv4 interface and announce on its own subnet broadcast,
    // plus the global 255.255.255.255. Adapter enumeration is the priciest part of the
    // tick, so results are cached briefly; adapter/hotspot changes are still picked up.
    static List<IPAddress> _bcCache = new();
    static DateTime _bcCacheAt = DateTime.MinValue;

    static List<IPAddress> BroadcastTargets()
    {
        if ((DateTime.UtcNow - _bcCacheAt).TotalSeconds < 20) return _bcCache;
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null) continue;
                    var ip = u.Address.GetAddressBytes();
                    var mask = u.IPv4Mask.GetAddressBytes();
                    if (ip.Length != 4 || mask.Length != 4) continue;
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var b = new IPAddress(bc);
                    if (!targets.Contains(b)) targets.Add(b);
                }
            }
        }
        catch { /* fall back to the global broadcast only */ }
        _bcCache = targets;
        _bcCacheAt = DateTime.UtcNow;
        return targets;
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[2048];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var res = await _udp.ReceiveFromAsync(buf.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                var ep = (IPEndPoint)res.RemoteEndPoint!;
                var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, res.ReceivedBytes)).RootElement;
                string? pname = doc.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (doc.TryGetProperty("app", out var app) && app.GetString() == Proto.AppTag &&
                    pname is not null && pname != Proto.DeviceName)
                {
                    _peers[ep.Address.ToString()] = new Peer
                    {
                        Name = pname,
                        Platform = doc.TryGetProperty("platform", out var pl) ? pl.GetString() ?? "" : "",
                        Ip = ep.Address.ToString(),
                        TcpPort = doc.TryGetProperty("tcp", out var tp) && tp.TryGetInt32(out var t) ? t : Proto.TcpPort,
                        LastSeen = DateTime.UtcNow,
                    };
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* bad packet — keep listening */ }
        }
    }

    public List<Peer> Snapshot()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-6);
        return _peers.Values.Where(p => p.LastSeen >= cutoff).OrderBy(p => p.Name).ToList();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
        _timer.Dispose();
    }
}
