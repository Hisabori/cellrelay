using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CellRelay;

// Datagram boundaries are preserved over the authenticated TCP control connection.
// This transports QUIC UDP payloads; it does not terminate QUIC or configure applications.
sealed class DatagramRelay(string phoneHost, int phonePort, string token, string targetHost, int targetPort, int localPort, Action<string> log) : IDisposable
{
    readonly CancellationTokenSource shutdown = new();
    readonly SemaphoreSlim peers = new(16);
    readonly System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, Channel> channels = new();
    UdpClient? socket;
    int disposed;
    readonly object lifecycle=new();
    public int Port => ((IPEndPoint)socket!.Client.LocalEndPoint!).Port;
    public void Start() {
        lock(lifecycle) {
        if(disposed!=0||socket!=null)throw new InvalidOperationException("UDP relay already started or disposed");
        socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, localPort));
        _ = Receive();
        }
    }
    async Task Receive() {
        try {
            while (!shutdown.IsCancellationRequested) {
                var packet = await socket!.ReceiveAsync(shutdown.Token);
                if (packet.Buffer.Length > 65507) continue;
                if (!channels.TryGetValue(packet.RemoteEndPoint, out var channel)) {
                    if (!peers.Wait(0)) continue;
                    channel = new Channel();
                    if (!channels.TryAdd(packet.RemoteEndPoint, channel)) { peers.Release(); channel = channels[packet.RemoteEndPoint]; }
                    else _ = RunPeer(packet.RemoteEndPoint, channel);
                }
                // A bounded queue drops excess traffic instead of allocating indefinitely.
                channel.Queue.Writer.TryWrite(packet.Buffer);
            }
        } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) {
            if (!shutdown.IsCancellationRequested) log("UDP 수신 종료: " + e.Message);
        }
    }
    async Task RunPeer(IPEndPoint peer, Channel channel) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(120));
        try {
            using var phone = new TcpClient();
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                handshake.CancelAfter(TimeSpan.FromSeconds(20));
                await phone.ConnectAsync(phoneHost, phonePort, handshake.Token);
                await phone.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token, host = targetHost, port = targetPort, transport = "udp" }) + "\n"), handshake.Token);
                if (await Proxy.ReadLine(phone.GetStream(), handshake.Token) != "OK UDP1") throw new IOException("아이폰이 UDP 연결을 거절했습니다. 새 IPA와 키를 확인하세요.");
            }
            var stream = phone.GetStream();
            var upload = Task.Run(async () => {
                await foreach (var bytes in channel.Queue.Reader.ReadAllAsync(lifetime.Token)) {
                    await WriteFrame(stream, bytes, lifetime.Token);
                    lifetime.CancelAfter(TimeSpan.FromSeconds(120));
                }
            });
            var download = Task.Run(async () => {
                while (!lifetime.IsCancellationRequested) {
                    var bytes = await ReadFrame(stream, lifetime.Token);
                    await socket!.SendAsync(bytes, peer, lifetime.Token);
                    lifetime.CancelAfter(TimeSpan.FromSeconds(120));
                }
            });
            await Task.WhenAny(upload, download); lifetime.Cancel();
            try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) {
            if (!shutdown.IsCancellationRequested && e is not OperationCanceledException) log("UDP 연결 종료: " + e.Message);
        } finally { lifetime.Cancel(); channel.Queue.Writer.TryComplete(); channels.TryRemove(peer, out _); peers.Release(); }
    }
    internal static async Task WriteFrame(Stream stream, byte[] bytes, CancellationToken ct) {
        if (bytes.Length > 65507) throw new IOException("UDP 패킷 크기 초과");
        byte[] size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)bytes.Length);
        await stream.WriteAsync(size, ct); await stream.WriteAsync(bytes, ct);
    }
    internal static async Task<byte[]> ReadFrame(Stream stream, CancellationToken ct) {
        byte[] size = new byte[2]; await stream.ReadExactlyAsync(size, ct);
        int count = BinaryPrimitives.ReadUInt16BigEndian(size);
        if (count > 65507) throw new IOException("UDP 패킷 크기 초과");
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
    }
    public void Dispose() { lock(lifecycle){if(disposed!=0)return;disposed=1;shutdown.Cancel();socket?.Dispose();} }
    sealed class Channel {
        public readonly System.Threading.Channels.Channel<byte[]> Queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(32) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true });
    }
}


