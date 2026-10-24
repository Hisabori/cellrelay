using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CellRelay;
static class SocksTests
{
    public static async Task Run()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));var ct=timeout.Token;
        var phone=new TcpListener(IPAddress.Loopback,0);phone.Start();
        try {
            var tasks=new List<Task>();
            var accept=Task.Run(async()=> {
                for(int i=0;i<3;i++) {
                    var c=await phone.AcceptTcpClientAsync(ct);
                    tasks.Add(Task.Run(async()=> {using(c) {
                        var stream=c.GetStream();using var doc=JsonDocument.Parse(await Proxy.ReadLine(stream,ct));
                        bool udp=doc.RootElement.GetProperty("transport").GetString()=="udp";
                        if(doc.RootElement.GetProperty("token").GetString()!="0123456789abcdef0123456789abcdef")throw new Exception("SOCKS authentication missing");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(udp ? "OK UDP1\n":"OK\n"),ct);
                        if(udp){var payload=await DatagramRelay.ReadFrame(stream,ct);await DatagramRelay.WriteFrame(stream,payload,ct);byte[] end=new byte[1];await stream.ReadAsync(end,ct);}
                        else {byte[] b=new byte[4];await stream.ReadExactlyAsync(b,ct);await stream.WriteAsync(b,ct);c.Client.Shutdown(SocketShutdown.Send);}
                    }}));
                }
            });
            using var socks=new SocksRelay(new("127.0.0.1","0123456789abcdef0123456789abcdef",((IPEndPoint)phone.LocalEndpoint).Port),_=>{});socks.Start();
            async Task<TcpClient> Open() {
                var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,socks.Port,ct);var s=client.GetStream();
                await s.WriteAsync(new byte[]{5,1,0},ct);byte[] result=new byte[2];await s.ReadExactlyAsync(result,ct);if(!result.SequenceEqual(new byte[]{5,0}))throw new Exception("SOCKS greeting failed");return client;
            }
            using(var tcp=await Open()) {
                var s=tcp.GetStream();await s.WriteAsync(new byte[]{5,1,0,1,1,1,1,1,1,187},ct);byte[] response=new byte[10];await s.ReadExactlyAsync(response,ct);if(response[1]!=0)throw new Exception("CONNECT failed");
                await s.WriteAsync("TEST"u8.ToArray(),ct);tcp.Client.Shutdown(SocketShutdown.Send);byte[] echo=new byte[4];await s.ReadExactlyAsync(echo,ct);if(Encoding.ASCII.GetString(echo)!="TEST")throw new Exception("SOCKS TCP corruption");
            }
            using(var assoc=await Open()) using(var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0))) {
                var s=assoc.GetStream();await s.WriteAsync(new byte[]{5,3,0,1,0,0,0,0,0,0},ct);byte[] response=new byte[10];await s.ReadExactlyAsync(response,ct);var endpoint=new IPEndPoint(IPAddress.Loopback,response[8]*256+response[9]);
                // Fragmentation is rejected; it must not allocate an upstream flow.
                await udp.SendAsync(new byte[]{0,0,1,1,1,1,1,1,0,53,42},endpoint,ct);
                foreach(byte last in new byte[]{1,2}) {
                    byte[] packet={0,0,0,1,1,1,1,last,0,53,0,1,2,3};await udp.SendAsync(packet,endpoint,ct);var echo=await udp.ReceiveAsync(ct);
                    if(!packet.SequenceEqual(echo.Buffer))throw new Exception("SOCKS UDP destination/header/payload lost");
                }
            }
            await accept;await Task.WhenAll(tasks);
            var config=TunnelConfig.Create("172.20.10.1",socks.Port);using var parsed=JsonDocument.Parse(config);
            if(config.Contains("0123456789abcdef") || parsed.RootElement.GetProperty("route").GetProperty("final").GetString()!="phone")throw new Exception("Unsafe tunnel config");
            File.WriteAllText("work/game-tunnel-check.json",config);
        }finally{phone.Stop();}
    }
}
