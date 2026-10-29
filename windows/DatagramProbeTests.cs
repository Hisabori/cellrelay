using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace CellRelay;
static class DatagramProbeTests
{
    static byte[] Response(byte[] query){var data=query.Concat(new byte[]{192,12,0,1,0,1,0,0,0,30,0,4,93,184,216,34}).ToArray();data[2]=129;data[3]=128;data[7]=1;return data;}
    public static async Task Run(){
        const string key="0123456789abcdef0123456789abcdef";var query=DatagramProbe.Query(1234);var response=Response(query);
        if(DatagramProbe.Validate(response,1234)!=1)throw new Exception("Valid compressed DNS answer rejected");
        var badId=response.ToArray();badId[0]^=1;
        var badQuestion=response.ToArray();badQuestion[13]=(byte)'z';
        var badCode=response.ToArray();badCode[3]|=3;
        var loop=response.ToArray();loop[query.Length+1]=(byte)query.Length;
        var noAnswer=response.ToArray();noAnswer[7]=0;
        foreach(var packet in new[]{badId,badQuestion,badCode,loop,noAnswer,response[..^1],new byte[3]}){
            bool rejected=false;try{DatagramProbe.Validate(packet,1234);}catch(IOException){rejected=true;}if(!rejected)throw new Exception("Malformed DNS probe response accepted");
        }
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));var ct=deadline.Token;
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try{
            var server=Task.Run(async()=>{
                for(int i=0;i<4;i++){
                    using var client=await listener.AcceptTcpClientAsync(ct);var stream=client.GetStream();using var hello=JsonDocument.Parse(await Proxy.ReadLine(stream,ct));
                    if(hello.RootElement.GetProperty("transport").GetString()!="udp"||hello.RootElement.GetProperty("host").GetString()!="1.1.1.1"||hello.RootElement.GetProperty("port").GetInt32()!=53)throw new Exception("DNS probe bypassed configured UDP relay target");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(i==2?"ERR "+key+"\n":"OK UDP1\n"),ct);if(i==2)continue;
                    var request=await DatagramRelay.ReadFrame(stream,ct);
                    if(i==3){byte[] end=new byte[1];await stream.ReadAsync(end,ct);continue;}
                    var reply=Response(request);if(i==1)reply[0]^=1;await DatagramRelay.WriteFrame(stream,reply,ct);
                }
            });
            var profile=new ConnectionProfile("127.0.0.1",key,((IPEndPoint)listener.LocalEndpoint).Port);
            for(int i=0;i<4;i++){
                bool failed=false;
                try{var result=await DatagramProbe.Run(profile,_=>{},ct,TimeSpan.FromMilliseconds(i==3?300:3000));if(i!=0||!result.Contains("성공"))throw new Exception("Unexpected UDP probe success");}
                catch(IOException e){failed=true;if(e.Message.Contains(key))throw new Exception("Probe leaked connection key");if(i==3&&!e.Message.Contains("시간 초과"))throw new Exception("UDP timeout not identified");}
                if(i==0&&failed||i>0&&!failed)throw new Exception("UDP probe result mismatch");
            }
            await server;
            using var cancel=new CancellationTokenSource();cancel.Cancel();bool canceled=false;
            try{await DatagramProbe.Run(profile,_=>{},cancel.Token);}catch(OperationCanceledException){canceled=true;}if(!canceled)throw new Exception("UDP probe ignored cancellation");
        }finally{listener.Stop();}
    }
}
