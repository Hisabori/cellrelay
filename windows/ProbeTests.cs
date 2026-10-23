using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CellRelay;
static class ProbeTests
{
    public static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () => {
                for (int i = 0; i < 2; i++) {
                    using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                    var stream = client.GetStream(); await Proxy.ReadLine(stream, deadline.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(i == 0 ? "ERR secret-should-not-leak\n" : "OK\n"), deadline.Token);
                    // Second connection closes before TLS: TCP acceptance must not count as HTTPS success.
                }
            });
            for (int i = 0; i < 2; i++) {
                bool rejected = false;
                try { await RelayProbe.Run("127.0.0.1", port, "test-token", _ => {}, deadline.Token); }
                catch (IOException e) {
                    rejected = e.Message.Contains(i == 0 ? "키 인증" : "HTTPS 인증서");
                    if (e.Message.Contains("secret-should-not-leak")) throw new Exception("Relay reply leaked");
                }
                if (!rejected) throw new Exception("Probe accepted incomplete relay or lost failing stage");
            }
            await server;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool stopped = false;
            try { await RelayProbe.Run("127.0.0.1", port, "test-token", _ => {}, canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            if (!stopped) throw new Exception("Probe ignored cancellation");
        } finally { listener.Stop(); }
    }
}
