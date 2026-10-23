using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace CellRelay;

static class RelayProbe
{
    public static async Task<string> Run(string host, int port, string token, Action<string> progress, CancellationToken ct)
    {
        string stage = "아이폰 접속";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try {
            progress(stage);
            using var phone = new TcpClient();
            await phone.ConnectAsync(host, port, deadline.Token);
            stage = "키 인증 및 셀룰러 목적지 연결"; progress(stage);
            var stream = phone.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token, host = "example.com", port = 443 }) + "\n"), deadline.Token);
            var reply = await Proxy.ReadLine(stream, deadline.Token);
            // Never echo an untrusted relay reply: it could contain credentials.
            if (reply != "OK") throw new IOException("아이폰이 요청을 거절했습니다. 현재 연결 키와 셀룰러 상태를 확인하세요.");
            stage = "HTTPS 인증서 확인"; progress(stage);
            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "example.com" }, deadline.Token);
            stage = "실제 HTTPS 응답 확인"; progress(stage);
            await tls.WriteAsync("HEAD / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"u8.ToArray(), deadline.Token);
            var header = await Proxy.ReadHeader(tls, deadline.Token);
            var first = header.Split("\r\n", 2)[0].Split(' ');
            if (first.Length < 2 || first[0] is not ("HTTP/1.1" or "HTTP/1.0") || !int.TryParse(first[1], out int status) || status < 100 || status > 599)
                throw new IOException("올바른 HTTP 응답이 아닙니다.");
            if (status < 200 || status >= 400) throw new IOException($"검사 사이트 응답 HTTP {status}. 인터넷 사용 가능 여부를 확인하세요.");
            return $"HTTPS 성공 · HTTP {status} · {watch.ElapsedMilliseconds:N0} ms";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new IOException(stage + ": 전체 검사 제한 25초 초과"); }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException) {
            string reason = e is AuthenticationException ? "서버 인증서 또는 TLS 연결을 검증하지 못했습니다." : e is SocketException ? "아이폰 주소·현재 포트·같은 핫스팟 연결을 확인하세요." : e.Message;
            throw new IOException(stage + ": " + reason, e);
        }
    }
}
