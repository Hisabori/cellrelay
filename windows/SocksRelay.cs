using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CellRelay;

// RFC 1928 loopback SOCKS5. Each UDP association is pinned to one local source.
sealed class SocksRelay(ConnectionProfile profile, Action<string> log) : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sessions = new(32);
    readonly SemaphoreSlim flows = new(24);
    readonly object lifecycle=new();
    int state,active;
    internal int ActiveSessions=>Volatile.Read(ref active);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start() { lock(lifecycle){if(state!=0)throw new InvalidOperationException("SOCKS listener already started or disposed");listener.Start();state=1;_ = Accept();} }
    async Task Accept() {
        try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); if (!sessions.Wait(0)) { c.Dispose(); continue; } Interlocked.Increment(ref active); _ = Handle(c); } }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException) { if (!stop.IsCancellationRequested) log("SOCKS 수신 종료"); }
    }
    async Task Handle(TcpClient client) {
        using (client) using (var life = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
            life.CancelAfter(TimeSpan.FromSeconds(20));
            try {
                var stream = client.GetStream(); byte[] greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, life.Token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                byte[] methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, life.Token);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] {5,255}, life.Token); return; }
                await stream.WriteAsync(new byte[] {5,0}, life.Token);
                byte[] request = new byte[4]; await stream.ReadExactlyAsync(request, life.Token);
                if (request[0] != 5 || request[2] != 0) return;
                var target = await ReadAddress(stream, request[3], life.Token);
                if (request[1] == 1) {
                    using var phone = await Connect(target.Host, target.Port, false, life.Token);
                    await Reply(stream, 0, 0, life.Token);
                    life.CancelAfter(TimeSpan.FromSeconds(120));
                    async Task Pump(Stream from, Stream to, Socket end) {
                        try {
                            byte[] bytes = new byte[65536]; int n;
                            while ((n = await from.ReadAsync(bytes, life.Token)) > 0) { await to.WriteAsync(bytes.AsMemory(0,n), life.Token); life.CancelAfter(TimeSpan.FromSeconds(120)); }
                            try { end.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                        } catch { life.Cancel(); throw; }
                    }
                    var upstream = phone.GetStream();
                    var up = Pump(stream, upstream, phone.Client); var down = Pump(upstream, stream, client.Client);
                    try { await Task.WhenAll(up, down); } finally { life.Cancel(); }
                } else if (request[1] == 3) {
                    if (target.Host != "0.0.0.0" && target.Host != "::" && !IPAddress.IsLoopback(IPAddress.TryParse(target.Host, out var ip) ? ip : IPAddress.Any)) { await Reply(stream,2,0,life.Token); return; }
                    await Associate(stream, target.Port, life);
                } else await Reply(stream, 7, 0, life.Token);
            } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or FormatException) {
                if (!stop.IsCancellationRequested && e is not OperationCanceledException) log("SOCKS 연결 종료: " + (e is IOException ? "중계 연결 또는 프로토콜 오류" : e.GetType().Name));
            } finally { life.Cancel(); Interlocked.Decrement(ref active); sessions.Release(); }
        }
    }
    async Task<TcpClient> Connect(string host, int port, bool udp, CancellationToken ct) {
        var phone = new TcpClient();
        try {
            if (port == 0) throw new IOException("Invalid destination");
            await phone.ConnectAsync(profile.Host, profile.Port, ct);
            var s = phone.GetStream();
            await s.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token=profile.Token, host, port, transport=udp ? "udp" : "tcp" })+"\n"),ct);
            if (await Proxy.ReadLine(s,ct) != (udp ? "OK UDP1" : "OK")) throw new IOException("Relay rejected");
            return phone;
        } catch { phone.Dispose(); throw; }
    }
    async Task Associate(NetworkStream control, int requestedPort, CancellationTokenSource life) {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var routes = new Dictionary<string,UdpFlow>();
        IPEndPoint? source = requestedPort == 0 ? null : new(IPAddress.Loopback,requestedPort);
        await Reply(control,0,((IPEndPoint)udp.Client.LocalEndPoint!).Port,life.Token);
        life.CancelAfter(TimeSpan.FromSeconds(120));
        var monitor = Task.Run(async () => { try { byte[] b=new byte[1]; await control.ReadAsync(b,life.Token); } finally { life.Cancel(); } });
        try {
            while (!life.IsCancellationRequested) {
                var p = await udp.ReceiveAsync(life.Token);
                if (!IPAddress.IsLoopback(p.RemoteEndPoint.Address)) continue;
                if (source != null && !source.Equals(p.RemoteEndPoint)) continue;
                if (p.Buffer.Length < 4 || p.Buffer[0]!=0 || p.Buffer[1]!=0 || p.Buffer[2]!=0) continue; // no fragmentation
                (string Host,int Port) target; int headerLength;
                try { using var data = new MemoryStream(p.Buffer,3,p.Buffer.Length-3,false); int type=data.ReadByte(); target=await ReadAddress(data,(byte)type,life.Token); headerLength=3+(int)data.Position; }
                catch (Exception e) when (e is IOException or FormatException) { continue; }
                if (target.Port==0 || p.Buffer.Length-headerLength > 65507) continue;
                source ??= p.RemoteEndPoint; life.CancelAfter(TimeSpan.FromSeconds(120));
                string key=target.Host+":"+target.Port;
                routes.TryGetValue(key,out var flow);
                if(flow!=null && flow.Completion.IsCompleted){flow.Dispose();routes.Remove(key);flow=null;}
                if (flow==null) {
                    foreach (var dead in routes.Where(x=>x.Value.Completion.IsCompleted).ToArray()) { dead.Value.Dispose(); routes.Remove(dead.Key); }
                    if (routes.Count>=8 || !flows.Wait(0)) continue;
                    var responseHeader = p.Buffer[..headerLength];
                    flow=new UdpFlow(this,target.Host,target.Port,udp,source,responseHeader,life.Token,()=>life.CancelAfter(TimeSpan.FromSeconds(120)));
                    routes.Add(key,flow);
                }
                flow.Enqueue(p.Buffer[headerLength..]);
            }
        } finally {
            life.Cancel(); foreach(var flow in routes.Values) flow.Dispose();
            try { await Task.WhenAll(routes.Values.Select(x=>x.Completion).Append(monitor)); } catch (Exception e) when(e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }
    internal static async Task<(string Host,int Port)> ReadAddress(Stream stream, byte type, CancellationToken ct) {
        int length=type switch {1=>4,4=>16,3=>1,_=>throw new IOException("Address type")};
        byte[] bytes=new byte[length]; await stream.ReadExactlyAsync(bytes,ct);
        if(type==3) { if(bytes[0]==0) throw new IOException("Empty host"); bytes=new byte[bytes[0]]; await stream.ReadExactlyAsync(bytes,ct); }
        string host=type==3 ? Encoding.ASCII.GetString(bytes) : new IPAddress(bytes).ToString();
        if(Uri.CheckHostName(host)==UriHostNameType.Unknown) throw new IOException("Invalid host");
        byte[] port=new byte[2]; await stream.ReadExactlyAsync(port,ct); return(host,port[0]*256+port[1]);
    }
    static Task Reply(Stream stream, byte code, int port, CancellationToken ct) => stream.WriteAsync(new byte[] {5,code,0,1,127,0,0,1,(byte)(port>>8),(byte)port},ct).AsTask();
    public void Dispose() { lock(lifecycle){if(state==2)return;state=2;stop.Cancel();listener.Stop();} }
    sealed class UdpFlow : IDisposable {
        readonly CancellationTokenSource life;
        readonly System.Threading.Channels.Channel<byte[]> queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(64) { FullMode=System.Threading.Channels.BoundedChannelFullMode.DropWrite,SingleReader=true,SingleWriter=true });
        public Task Completion {get;}
        public UdpFlow(SocksRelay owner,string host,int port,UdpClient socket,IPEndPoint source,byte[] header,CancellationToken ct,Action activity) {
            life=CancellationTokenSource.CreateLinkedTokenSource(ct); life.CancelAfter(TimeSpan.FromSeconds(120));
            Completion=Task.Run(async()=> {
                try {
                    using var handshake=CancellationTokenSource.CreateLinkedTokenSource(life.Token); handshake.CancelAfter(TimeSpan.FromSeconds(20));
                    using var phone=await owner.Connect(host,port,true,handshake.Token); var s=phone.GetStream();
                    var up=Task.Run(async()=> { await foreach(var bytes in queue.Reader.ReadAllAsync(life.Token)) { await DatagramRelay.WriteFrame(s,bytes,life.Token); life.CancelAfter(TimeSpan.FromSeconds(120)); activity(); } });
                    var down=Task.Run(async()=> { while(!life.IsCancellationRequested) { var bytes=await DatagramRelay.ReadFrame(s,life.Token); if(header.Length+bytes.Length<=65507) { byte[] response=new byte[header.Length+bytes.Length]; header.CopyTo(response,0); bytes.CopyTo(response,header.Length); await socket.SendAsync(response,source,life.Token); } life.CancelAfter(TimeSpan.FromSeconds(120)); activity(); } });
                    await Task.WhenAny(up,down); life.Cancel(); try { await Task.WhenAll(up,down); } catch(Exception e) when(e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
                } catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                finally { life.Cancel(); queue.Writer.TryComplete(); owner.flows.Release(); }
            });
        }
        public void Enqueue(byte[] bytes)=>queue.Writer.TryWrite(bytes);
        public void Dispose()=>life.Cancel();
    }
}




