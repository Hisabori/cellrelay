using System.Net.Sockets;

namespace CellRelay;

static class ProxyStarter
{
    // The actual listener holds the selected port; no check-then-bind gap.
    public static Proxy Start(string phone, string token, int preferred, Action<string> log, int phonePort = 49152)
    {
        var candidates = new[] { preferred }.Concat(Enumerable.Range(1, 20).Select(n => preferred + n).Where(p => p <= 65535)).Append(0);
        foreach (int port in candidates)
        {
            var proxy = new Proxy(phone, phonePort, token, port, log);
            try
            {
                proxy.Start();
                if (proxy.Port != preferred) log($"PC 포트 {preferred}은 사용 중이거나 예약되어 있어 {proxy.Port}로 시작했습니다. 브라우저 프록시도 127.0.0.1:{proxy.Port}로 변경하세요.");
                return proxy;
            }
            catch (SocketException e) when (port != 0 && e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { proxy.Dispose(); }
            catch { proxy.Dispose(); throw; }
        }
        throw new IOException("사용 가능한 PC 프록시 포트를 열지 못했습니다.");
    }
}
