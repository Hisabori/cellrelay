using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace CellRelay;
static class DatagramProbe
{
    internal static byte[] Query(ushort id){
        var data=new List<byte>{(byte)(id>>8),(byte)id,1,0,0,1,0,0,0,0,0,0};
        foreach(var label in "example.com".Split('.')){data.Add((byte)label.Length);data.AddRange(Encoding.ASCII.GetBytes(label));}
        data.AddRange(new byte[]{0,0,1,0,1});return data.ToArray();
    }
    public static async Task<string> Run(ConnectionProfile profile,Action<string> progress,CancellationToken ct,TimeSpan? limit=null){
        if(Uri.CheckHostName(profile.Host)==UriHostNameType.Unknown||profile.Port is <1 or >65535||profile.Token.Length!=32||!profile.Token.All(Uri.IsHexDigit))throw new FormatException("아이폰 주소·현재 포트·32자리 연결 키를 확인하세요.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(limit??TimeSpan.FromSeconds(8));
        var watch=System.Diagnostics.Stopwatch.StartNew();string stage="아이폰 UDP 중계 연결";
        try{
            progress(stage);using var phone=new TcpClient();await phone.ConnectAsync(profile.Host,profile.Port,deadline.Token);var stream=phone.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{token=profile.Token,host="1.1.1.1",port=53,transport="udp"})+"\n"),deadline.Token);
            if(await Proxy.ReadLine(stream,deadline.Token)!="OK UDP1")throw new IOException("UDP 중계가 거절됐습니다. 아이폰 0.7 이상과 현재 연결 키를 확인하세요.");
            stage="UDP DNS 응답 대기";progress(stage);ushort id=(ushort)RandomNumberGenerator.GetInt32(65536);
            await DatagramRelay.WriteFrame(stream,Query(id),deadline.Token);var response=await DatagramRelay.ReadFrame(stream,deadline.Token);
            stage="DNS 응답 검증";progress(stage);int count=Validate(response,id);
            return $"UDP·DNS 성공 · 주소 응답 {count}개 · {watch.ElapsedMilliseconds:N0} ms";
        }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(OperationCanceledException){throw new IOException(stage+": 검사 시간 초과 · 아이폰 셀룰러와 UDP/53 경로를 확인하세요.");}
        catch(Exception e)when(e is IOException or SocketException){throw new IOException(stage+": "+(e is SocketException?"아이폰 주소와 현재 포트에 접속하지 못했습니다.":e.Message),e);}
    }
    static ushort U16(byte[] data,int offset){if(offset<0||offset+2>data.Length)throw new IOException("잘린 DNS 응답");return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset,2));}
    internal static int Validate(byte[] data,ushort id){
        if(data.Length<12||U16(data,0)!=id)throw new IOException("DNS 응답 번호가 일치하지 않습니다.");
        ushort flags=U16(data,2);if((flags&0xFA0F)!=0x8000)throw new IOException("DNS 오류·잘림 또는 지원하지 않는 응답입니다.");
        int count=U16(data,6);if(U16(data,4)!=1||count==0||count>256)throw new IOException("DNS 주소 응답이 없습니다.");
        int position=12;if(!ReadName(data,ref position).Equals("example.com",StringComparison.OrdinalIgnoreCase)||U16(data,position)!=1||U16(data,position+2)!=1)throw new IOException("DNS 질문이 일치하지 않습니다.");position+=4;
        var addresses=new List<string>();var aliases=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<count;i++){
            string owner=ReadName(data,ref position);int type=U16(data,position),cls=U16(data,position+2),length=U16(data,position+8);position+=10;
            if(position>data.Length-length)throw new IOException("잘린 DNS 레코드");
            if(type==1&&cls==1&&length==4)addresses.Add(owner);
            if(type==5&&cls==1){int rdata=position;var name=ReadName(data,ref rdata);if(rdata!=position+length)throw new IOException("잘못된 DNS 별칭");aliases[owner]=name;}
            position+=length;
        }
        string expected="example.com";for(int i=0;i<16;i++){int found=addresses.Count(x=>x.Equals(expected,StringComparison.OrdinalIgnoreCase));if(found>0)return found;if(!aliases.TryGetValue(expected,out var next))break;expected=next;}
        throw new IOException("검사 도메인의 IPv4 응답이 없습니다.");
    }
    static string ReadName(byte[] data,ref int position){
        int cursor=position,total=0;bool jumped=false;var labels=new List<string>();
        for(int hops=0;hops<128;hops++){
            if(cursor>=data.Length)throw new IOException("잘린 DNS 이름");int length=data[cursor++];
            if(length==0){if(!jumped)position=cursor;return string.Join('.',labels);}
            if((length&0xC0)==0xC0){if(cursor>=data.Length)throw new IOException("잘린 DNS 포인터");int target=((length&63)<<8)|data[cursor++];if(target>=data.Length)throw new IOException("잘못된 DNS 포인터");if(!jumped)position=cursor;jumped=true;cursor=target;continue;}
            if((length&0xC0)!=0||cursor>data.Length-length||(total+=length+1)>254)throw new IOException("잘못된 DNS 이름");
            for(int i=0;i<length;i++)if(data[cursor+i]<33||data[cursor+i]>126||data[cursor+i]==46)throw new IOException("잘못된 DNS 라벨");
            labels.Add(Encoding.ASCII.GetString(data,cursor,length));cursor+=length;
        }
        throw new IOException("DNS 압축 포인터 순환");
    }
}

