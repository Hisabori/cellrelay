using System.Net;
using System.Text;

namespace CellRelay;

sealed class RequestError(int status, string message) : IOException(message)
{
    public int Status { get; } = status;
}

sealed record RequestPlan(string Host, int Port, bool Tunnel, byte[]? Initial)
{
    public static RequestPlan Parse(string header)
    {
        var lines = header.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[2] is not ("HTTP/1.0" or "HTTP/1.1") || request[0].Length == 0 || request[0].Any(c => !char.IsAsciiLetter(c)))
            throw new RequestError(400, "잘못된 HTTP 요청");
        var fields = new List<(string Name, string Value)>();
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))) || line.Any(c => (c < 32 && c != '\t') || c == 127))
                throw new RequestError(400, "잘못된 HTTP 헤더");
            fields.Add((line[..colon], line[(colon + 1)..].Trim()));
        }
        string[] Values(string name) => fields.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Value).ToArray();
        var lengths = Values("Content-Length"); var encodings = Values("Transfer-Encoding");
        if (lengths.Length > 1 || (lengths.Length > 0 && (encodings.Length > 0 || !long.TryParse(lengths[0], out var length) || length < 0)) || encodings.Length > 1 || (encodings.Length == 1 && !encodings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            throw new RequestError(400, "모호한 HTTP 본문 길이");
        if (request[0] == "CONNECT")
        {
            var authority = request[1];
            int colon = authority.LastIndexOf(':');
            if (colon < 1 || !int.TryParse(authority[(colon + 1)..], out var port) || port is < 1 or > 65535)
                throw new RequestError(400, "CONNECT에는 목적지와 포트가 필요합니다");
            var host = authority[..colon];
            if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || authority.Contains('@') || authority.Contains('/') || authority.Contains('?') || authority.Contains('#'))
                throw new RequestError(400, "잘못된 CONNECT 주소");
            return new(host, port, true, null);
        }
        if (!Uri.TryCreate(request[1], UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Port is < 1 or > 65535 || Uri.CheckHostName(uri.Host.Trim('[', ']')) == UriHostNameType.Unknown)
            throw new RequestError(400, "올바른 HTTP 절대 주소가 필요합니다");
        if (Values("Upgrade").Length > 0) throw new RequestError(400, "프로토콜 업그레이드는 HTTPS CONNECT로 사용하세요");
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate", "Keep-Alive" };
        foreach (var field in Values("Connection").SelectMany(v => v.Split(',')))
        {
            var name = field.Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new RequestError(400, "본문 프레이밍 헤더를 연결 전용으로 지정할 수 없습니다");
            removed.Add(name);
        }
        var kept = fields.Where(f => !removed.Contains(f.Name) && !f.Name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase));
        var initial = Encoding.Latin1.GetBytes($"{request[0]} {uri.PathAndQuery} {request[2]}\r\nHost: {uri.Authority}\r\n" + string.Concat(kept.Select(f => f.Name + ": " + f.Value + "\r\n")) + "Connection: close\r\n\r\n");
        return new(uri.Host.Trim('[', ']'), uri.Port, false, initial);
    }
}
