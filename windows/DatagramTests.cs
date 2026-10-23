using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CellRelay;
static class DatagramTests
{
    public static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = deadline.Token;
        using var memory = new MemoryStream();
        await DatagramRelay.WriteFrame(memory, [], ct); await DatagramRelay.WriteFrame(memory, new byte[65507], ct); memory.Position = 0;
        if ((await DatagramRelay.ReadFrame(memory, ct)).Length != 0 || (await DatagramRelay.ReadFrame(memory, ct)).Length != 65507) throw new Exception("UDP boundary sizes failed");
        using var truncated = new MemoryStream(new byte[] { 0, 3, 1 });
        bool rejected = false; try { await DatagramRelay.ReadFrame(truncated, ct); } catch (EndOfStreamException) { rejected = true; }
        if (!rejected) throw new Exception("Truncated UDP frame accepted");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try {
            var server = Task.Run(async () => {
                using var phone = await listener.AcceptTcpClientAsync(ct); var stream = phone.GetStream();
                using var doc = JsonDocument.Parse(await Proxy.ReadLine(stream, ct));
                if (doc.RootElement.GetProperty("transport").GetString() != "udp" || doc.RootElement.GetProperty("port").GetInt32() != 443 || doc.RootElement.GetProperty("token").GetString() != "test-token") throw new Exception("UDP handshake failed");
                await stream.WriteAsync("OK UDP1\n"u8.ToArray(), ct);
                for (int i=0; i<3; i++) { var packet = await DatagramRelay.ReadFrame(stream, ct); await DatagramRelay.WriteFrame(stream, packet, ct); }
            });
            using var relay = new DatagramRelay("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "test-token", "example.com", 443, 0, _=>{}); relay.Start();
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            foreach (var packet in new[] { Array.Empty<byte>(), Encoding.ASCII.GetBytes("packet-two"), Enumerable.Range(0, 4096).Select(x => (byte)x).ToArray() }) {
                await client.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, relay.Port), ct);
                var reply = await client.ReceiveAsync(ct);
                if (!packet.SequenceEqual(reply.Buffer)) throw new Exception("UDP packet boundary/content lost");
            }
            await server;
        } finally { listener.Stop(); }
        var writer = new ZXing.Windows.Compatibility.BarcodeWriter { Format = ZXing.BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 400, Height = 400, Margin = 4 } };
        string code = "CR1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { host="172.20.10.1", port=60532, token="0123456789abcdef0123456789abcdef" })));
        using var qr = writer.Write(code);
        if (QrProfile.Read(qr).Port != 60532) throw new Exception("QR profile roundtrip failed");
        using var blank = new Bitmap(200, 200); using (var g = Graphics.FromImage(blank)) g.Clear(Color.White);
        rejected=false; try { QrProfile.Read(blank); } catch (FormatException) { rejected=true; }
        if (!rejected) throw new Exception("Blank image accepted as QR");
    }
}

