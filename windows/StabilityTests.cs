using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CellRelay;

static class StabilityTests
{
    public static async Task Run()
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        foreach (var bad in new[] {
            "CONNECT example.com HTTP/1.1\r\n\r\n",
            "CONNECT user@example.com:443 HTTP/1.1\r\n\r\n",
            "GET http://example.com/ HTTP/1.9\r\n\r\n",
            "GET http://example.com/ HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n",
            "POST http://example.com/ HTTP/1.1\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n",
            "GET http://example.com/ HTTP/1.1\r\n Folded: value\r\n\r\n",
            "GET http://example.com/ HTTP/1.1\r\nConnection: Content-Length\r\n\r\n"
        }) { bool rejected = false; try { RequestPlan.Parse(bad); } catch (RequestError) { rejected = true; } Require(rejected, "Malformed request accepted"); }
        var ipv6 = RequestPlan.Parse("CONNECT [::1]:443 HTTP/1.1\r\n\r\n"); Require(ipv6.Host == "::1" && ipv6.Port == 443, "IPv6 CONNECT parsing");
        var plan = RequestPlan.Parse("POST http://example.com/upload HTTP/1.1\r\nContent-Length: 4\r\nConnection: X-Private\r\nX-Private: remove\r\n\r\n");
        var transformed = Encoding.Latin1.GetString(plan.Initial!); Require(!transformed.Contains("X-Private") && transformed.Contains("Content-Length: 4"), "Connection header sanitization");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        using (var invalidProxy = new Proxy("127.0.0.1", 9, "unused", 0, _ => {}))
        {
            invalidProxy.Start();
            foreach (var pair in new[] { ("CONNECT missing-port HTTP/1.1\r\n\r\n", 400), (new string('x', 32768), 431) })
            {
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, invalidProxy.Port, ct);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(pair.Item1), ct);
                Require((await Proxy.ReadHeader(client.GetStream(), ct)).StartsWith($"HTTP/1.1 {pair.Item2}"), "Invalid request status");
            }
        }
        foreach (bool stop in new[] { false, true })
        {
            var fake = new TcpListener(IPAddress.Loopback, 0); fake.Start();
            try
            {
                var peer = Task.Run(async () => {
                    using var accepted = await fake.AcceptTcpClientAsync(ct); var stream = accepted.GetStream();
                    await Proxy.ReadLine(stream, ct); await stream.WriteAsync("OK\n"u8.ToArray(), ct);
                    byte[] one = new byte[1]; Require(await stream.ReadAsync(one, ct) == 0, "Remote session not closed");
                }, ct);
                using var proxy = new Proxy("127.0.0.1", ((IPEndPoint)fake.LocalEndpoint).Port, "unused", 0, _ => {}, TimeSpan.FromMilliseconds(200)); proxy.Start();
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Port, ct);
                await client.GetStream().WriteAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n"u8.ToArray(), ct);
                Require((await Proxy.ReadHeader(client.GetStream(), ct)).Contains("200"), "Session setup");
                if (stop) { proxy.Dispose(); proxy.Dispose(); }
                byte[] one = new byte[1]; Require(await client.GetStream().ReadAsync(one, ct) == 0, "Idle/stop did not close client");
                await peer;
                while (proxy.Active != 0) await Task.Delay(10, ct);
            }
            finally { fake.Stop(); }
        }
        // HTTP uploads must survive when headers and the body arrive in one socket write.
        var uploadPeer = new TcpListener(IPAddress.Loopback, 0); uploadPeer.Start();
        try
        {
            var peer = Task.Run(async () => {
                using var accepted = await uploadPeer.AcceptTcpClientAsync(ct); var stream = accepted.GetStream();
                await Proxy.ReadLine(stream, ct); await stream.WriteAsync("OK\n"u8.ToArray(), ct);
                var header = await Proxy.ReadHeader(stream, ct); Require(header.Contains("Content-Length: 4"), "Upload framing lost");
                byte[] body = new byte[4]; await stream.ReadExactlyAsync(body, ct); Require(Encoding.ASCII.GetString(body) == "BODY", "Upload corrupted");
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct);
            }, ct);
            using var proxy = new Proxy("127.0.0.1", ((IPEndPoint)uploadPeer.LocalEndpoint).Port, "unused", 0, _ => {}); proxy.Start();
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxy.Port, ct);
            var uploadStream = client.GetStream();
            await uploadStream.WriteAsync("POST http://example.com/upload HTTP/1.1\r\nContent-Length: 4\r\n\r\nBODY"u8.ToArray(), ct);
            client.Client.Shutdown(SocketShutdown.Send);
            Require((await Proxy.ReadHeader(uploadStream, ct)).Contains("200"), "Upload half-close lost response"); await peer;
        }
        finally { uploadPeer.Stop(); }
        var clients = new List<TcpClient>();
        using var bounded = new Proxy("127.0.0.1", 9, "unused", 0, _ => {}); bounded.Start(); int port = bounded.Port;
        try
        {
            for (int i = 0; i < 32; i++) { var c = new TcpClient(); clients.Add(c); await c.ConnectAsync(IPAddress.Loopback, port, ct); }
            while (bounded.Active != 32) await Task.Delay(10, ct);
            using var overflow = new TcpClient(); await overflow.ConnectAsync(IPAddress.Loopback, port, ct);
            byte[] one = new byte[1]; Require(await overflow.GetStream().ReadAsync(one, ct) == 0, "Connection limit exceeded");
            bounded.Dispose();
            while (bounded.Active != 0) await Task.Delay(10, ct);
            using var restarted = new Proxy("127.0.0.1", 9, "unused", port, _ => {}); restarted.Start();
            using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port, ct);
            await probe.GetStream().WriteAsync("CONNECT missing-port HTTP/1.1\r\n\r\n"u8.ToArray(), ct);
            Require((await Proxy.ReadHeader(probe.GetStream(), ct)).StartsWith("HTTP/1.1 400"), "Restart on same port failed");
        }
        finally { foreach (var client in clients) client.Dispose(); }
        var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        try
        {
            int occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            using var fallback = ProxyStarter.Start("127.0.0.1", "unused", occupiedPort, _ => {});
            Require(fallback.Port != occupiedPort, "Port conflict did not select a new port");
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, fallback.Port, ct);
            var stream = client.GetStream(); await stream.WriteAsync("CONNECT missing-port HTTP/1.1\r\n\r\n"u8.ToArray(), ct);
            Require((await Proxy.ReadHeader(stream, ct)).StartsWith("HTTP/1.1 400"), "Fallback listener not working");
            using var originalClient = new TcpClient(); await originalClient.ConnectAsync(IPAddress.Loopback, occupiedPort, ct);
            using var originalAccepted = await occupied.AcceptTcpClientAsync(ct);
        }
        finally { occupied.Stop(); }
    }
}
