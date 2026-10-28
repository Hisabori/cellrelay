using System.Net;
using System.Net.Sockets;
using System.Text;
namespace CellRelay;
static class ShutdownTests
{
    public static async Task Run(){
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=deadline.Token;
        var profile=new ConnectionProfile("127.0.0.1","0123456789abcdef0123456789abcdef",49152);
        static void Rejected(Action action){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("Listener restarted after start/dispose");}
        using(var relay=new SocksRelay(profile,_=>{})){
            relay.Start();Rejected(relay.Start);
            var clients=new List<TcpClient>();
            try{
                for(int i=0;i<8;i++){var c=new TcpClient();clients.Add(c);await c.ConnectAsync(IPAddress.Loopback,relay.Port,ct);await c.GetStream().WriteAsync(new byte[]{5},ct);}
                while(relay.ActiveSessions<8)await Task.Delay(10,ct);
                await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(relay.Dispose)));
                foreach(var c in clients){using var read=new CancellationTokenSource(2000);byte[] b=new byte[1];try{if(await c.GetStream().ReadAsync(b,read.Token)!=0)throw new Exception("Disposed SOCKS listener still active");}catch(IOException){}}
                while(relay.ActiveSessions!=0)await Task.Delay(10,ct);
                Rejected(relay.Start);
            }finally{foreach(var c in clients)c.Dispose();}
        }
        using(var relay=new SocksRelay(profile,_=>{})){relay.Dispose();Rejected(relay.Start);relay.Dispose();}
        using(var udp=new DatagramRelay("127.0.0.1",49152,"unused","example.com",443,0,_=>{})){udp.Dispose();Rejected(udp.Start);udp.Dispose();}
        using(var udp=new DatagramRelay("127.0.0.1",49152,"unused","example.com",443,0,_=>{})){udp.Start();int port=udp.Port;Rejected(udp.Start);udp.Dispose();udp.Dispose();using var rebound=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));}
        var fake=new TcpListener(IPAddress.Loopback,0);fake.Start();
        var reset=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try{
            var server=Task.Run(async()=>{using var phone=await fake.AcceptSocketAsync(ct);using var remote=new NetworkStream(phone,ownsSocket:false);await Proxy.ReadLine(remote,ct);await remote.WriteAsync("OK\n"u8.ToArray(),ct);await reset.Task.WaitAsync(ct);phone.LingerState=new LingerOption(true,0);phone.Close(0);});
            using var relay=new SocksRelay(profile with {Port=((IPEndPoint)fake.LocalEndpoint).Port},_=>{});relay.Start();
            using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,relay.Port,ct);var stream=client.GetStream();
            await stream.WriteAsync(new byte[]{5,1,0},ct);byte[] method=new byte[2];await stream.ReadExactlyAsync(method,ct);
            await stream.WriteAsync(new byte[]{5,1,0,1,1,1,1,1,1,187},ct);byte[] reply=new byte[10];await stream.ReadExactlyAsync(reply,ct);
            if(reply[1]!=0)throw new Exception("Reset test CONNECT failed");reset.TrySetResult();await server;
            // Keep upload open: a failed download must cancel the blocked upload immediately.
            using var bound=new CancellationTokenSource(2000);byte[] one=new byte[1];try{if(await stream.ReadAsync(one,bound.Token)!=0)throw new Exception("Unexpected reset payload");}catch(IOException){}
            while(relay.ActiveSessions!=0)await Task.Delay(10,bound.Token);
        }finally{reset.TrySetResult();fake.Stop();}
    }
}

