using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace PaperNote.Sharing;

// LAN presence over UDP multicast. While running, broadcasts a small heartbeat every few seconds
// and listens for others, keeping a peer list that drops peers gone silent. Same-subnet only.
public sealed class PresenceService(ShareConfig config) : IDisposable
{
    private const string GroupAddress = "239.255.13.37";
    private const int GroupPort = 50777;
    private const int BeatMs = 3000;
    private const int PeerTtlSeconds = 10;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Timer? _beat;
    private Timer? _prune;

    // The transport (HTTP) port advertised to peers; set by the transport layer (B2). 0 until then.
    public int TransportPort { get; set; }

    public ObservableCollection<PeerInfo> Peers { get; } = [];
    public bool IsRunning => _udp is not null;

    public void Start()
    {
        if (_udp is not null) return;

        var udp = new UdpClient { ExclusiveAddressUse = false };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, GroupPort));
        udp.JoinMulticastGroup(IPAddress.Parse(GroupAddress));
        _udp = udp;

        _cts = new CancellationTokenSource();
        _ = ReceiveLoop(_cts.Token);
        _beat = new Timer(_ => Broadcast(), null, 0, BeatMs);
        _prune = new Timer(_ => Prune(), null, 1000, 2000);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _beat?.Dispose(); _beat = null;
        _prune?.Dispose(); _prune = null;
        try { _udp?.DropMulticastGroup(IPAddress.Parse(GroupAddress)); } catch { }
        _udp?.Dispose(); _udp = null;
        _cts?.Dispose(); _cts = null;
        _dispatcher.Invoke(Peers.Clear);
    }

    private void Broadcast()
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new Beat
            {
                p = config.PeerId, n = config.DisplayName, port = TransportPort
            });
            _udp?.Send(payload, payload.Length, new IPEndPoint(IPAddress.Parse(GroupAddress), GroupPort));
        }
        catch { }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(ct); }
            catch { break; }

            Beat? beat;
            try { beat = JsonSerializer.Deserialize<Beat>(result.Buffer); }
            catch { continue; }

            if (beat is null || string.IsNullOrEmpty(beat.p) || beat.p == config.PeerId) continue;

            var ip = result.RemoteEndPoint.Address.ToString();
            _dispatcher.Invoke(() => Upsert(beat, ip));
        }
    }

    private void Upsert(Beat beat, string ip)
    {
        var now = DateTime.UtcNow.Ticks;
        var existing = Peers.FirstOrDefault(p => p.PeerId == beat.p);
        if (existing is not null)
        {
            existing.LastSeenTicks = now;
            existing.Ip = ip;
            existing.Port = beat.port;
        }
        else
        {
            Peers.Add(new PeerInfo
            {
                PeerId = beat.p, Name = string.IsNullOrWhiteSpace(beat.n) ? "PaperNote user" : beat.n,
                Ip = ip, Port = beat.port, LastSeenTicks = now
            });
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-PeerTtlSeconds).Ticks;
        _dispatcher.Invoke(() =>
        {
            for (var i = Peers.Count - 1; i >= 0; i--)
                if (Peers[i].LastSeenTicks < cutoff) Peers.RemoveAt(i);
        });
    }

    public void Dispose() => Stop();

    private sealed class Beat
    {
        public string p { get; set; } = "";   // peerId
        public string n { get; set; } = "";   // display name
        public int port { get; set; }          // transport port
    }
}
