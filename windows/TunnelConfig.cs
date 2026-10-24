using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CellRelay;
static class TunnelConfig
{
    public static string Create(string phoneHost,int socksPort)
    {
        if(!IPAddress.TryParse(phoneHost,out var phone) || IPAddress.IsLoopback(phone) || phone.Equals(IPAddress.Any) || phone.Equals(IPAddress.IPv6Any)) throw new FormatException("게임 모드에는 아이폰의 실제 IP 주소를 입력하세요.");
        return JsonSerializer.Serialize(new {
            log=new { level="warn",timestamp=true },
            dns=new { servers=new[] {new {type="udp",tag="cellular-dns",server="1.1.1.1",server_port=53,detour="phone"}}, final="cellular-dns" },
            inbounds=new[] {new {type="tun",tag="game-tun",interface_name="CellRelayGame",address=new[]{"172.31.255.1/30","fdfe:ce11:abcd::1/126"},mtu=1400,auto_route=true,strict_route=true,dns_mode="hijack",stack="mixed",route_exclude_address=new[]{phone+ (phone.AddressFamily==AddressFamily.InterNetwork ? "/32":"/128"),"127.0.0.0/8","::1/128"}}},
            outbounds=new[] {new {type="socks",tag="phone",server="127.0.0.1",server_port=socksPort,version="5"}},
            route=new {auto_detect_interface=true,final="phone",rules=new[]{new {port=53,action="hijack-dns"}}}
        },new JsonSerializerOptions {WriteIndented=true});
    }
}
