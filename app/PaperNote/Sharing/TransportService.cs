using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace PaperNote.Sharing;

// Peer-to-peer note transfer over TCP (length-prefixed JSON frames). No HttpListener (that needs
// a urlacl/admin reservation for LAN binds); TcpListener binds to any:0 with no special rights.
public sealed class TransportService : IDisposable
{
    private const int MaxFrame = 25_000_000;   // notes can carry base64 images
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    // Raised (on the UI thread) when a peer offers a note. Handler returns true to accept.
    public Func<ShareOffer, Task<bool>>? OfferReceived;

    // A subscribed sender pushed new content for shareId (title, markdown). UI thread.
    public Action<string, string, string>? UpdateReceived;

    // A receiver asked for the latest content of shareId. Return (found, title, markdown). UI thread.
    public Func<string, (bool Found, string Title, string Markdown)>? LatestRequested;

    public void Start()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose(); _cts = null;
        Port = 0;
    }

    public async Task<bool> SendOfferAsync(string ip, int port, ShareOffer offer)
    {
        // Wait generously for the receiver's accept/reject (a person clicking).
        var reply = await SendAsync(ip, port, new Wire { type = "offer", offer = offer }, replySeconds: 120);
        return reply?.accepted ?? false;
    }

    // Sender push: deliver new content to a subscriber. Returns true if the peer acked.
    public async Task<bool> SendUpdateAsync(string ip, int port, string shareId, string title, string markdown)
    {
        var reply = await SendAsync(ip, port,
            new Wire { type = "update", shareId = shareId, title = title, markdown = markdown }, replySeconds: 15);
        return reply?.accepted ?? false;
    }

    // Receiver pull: fetch the sender's latest content for a share.
    public async Task<(bool Found, string Title, string Markdown)> RequestLatestAsync(string ip, int port, string shareId)
    {
        var reply = await SendAsync(ip, port, new Wire { type = "latest-request", shareId = shareId }, replySeconds: 15);
        return reply is { found: true } ? (true, reply.title, reply.markdown) : (false, "", "");
    }

    private async Task<Wire?> SendAsync(string ip, int port, Wire message, int replySeconds)
    {
        if (port == 0) return null;
        try
        {
            using var client = new TcpClient();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(ip, port, connectCts.Token);

            using var ns = client.GetStream();
            await WriteFrame(ns, message, connectCts.Token);

            using var replyCts = new CancellationTokenSource(TimeSpan.FromSeconds(replySeconds));
            var data = await ReadFrame(ns, replyCts.Token);
            return data is null ? null : JsonSerializer.Deserialize<Wire>(data);
        }
        catch { return null; }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleClient(client, ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var ns = client.GetStream();
                var data = await ReadFrame(ns, ct);
                if (data is null) return;

                var msg = JsonSerializer.Deserialize<Wire>(data);
                if (msg is null) return;

                switch (msg.type)
                {
                    case "offer" when msg.offer is not null:
                        var accepted = OfferReceived is not null && await _dispatcher.Invoke(() => OfferReceived(msg.offer));
                        await WriteFrame(ns, new Wire { type = "offer-result", accepted = accepted }, ct);
                        break;

                    case "update":
                        _dispatcher.Invoke(() => UpdateReceived?.Invoke(msg.shareId, msg.title, msg.markdown));
                        await WriteFrame(ns, new Wire { type = "update-ack", accepted = true }, ct);
                        break;

                    case "latest-request":
                        var latest = LatestRequested is not null
                            ? _dispatcher.Invoke(() => LatestRequested(msg.shareId))
                            : (false, "", "");
                        await WriteFrame(ns, new Wire
                        {
                            type = "latest", found = latest.Item1, title = latest.Item2, markdown = latest.Item3
                        }, ct);
                        break;
                }
            }
            catch { }
        }
    }

    private static async Task WriteFrame(NetworkStream ns, Wire msg, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(msg);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        await ns.WriteAsync(len, ct);
        await ns.WriteAsync(payload, ct);
    }

    private static async Task<byte[]?> ReadFrame(NetworkStream ns, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExact(ns, lenBuf, ct)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len <= 0 || len > MaxFrame) return null;
        var buf = new byte[len];
        if (!await ReadExact(ns, buf, ct)) return null;
        return buf;
    }

    private static async Task<bool> ReadExact(NetworkStream ns, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await ns.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public void Dispose() => Stop();

    private sealed class Wire
    {
        public string type { get; set; } = "";
        public ShareOffer? offer { get; set; }
        public bool accepted { get; set; }
        public string shareId { get; set; } = "";
        public string title { get; set; } = "";
        public string markdown { get; set; } = "";
        public bool found { get; set; }
    }
}
