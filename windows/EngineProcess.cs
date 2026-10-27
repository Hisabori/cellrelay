using System.Diagnostics;

namespace CellRelay;
static class EngineProcess
{
    internal static ProcessStartInfo Info(string path,string command,string config) {
        var info=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=Path.GetDirectoryName(path)!};
        info.ArgumentList.Add(command);info.ArgumentList.Add("-c");info.ArgumentList.Add(config);return info;
    }
    internal static async Task Check(ProcessStartInfo info,CancellationToken ct,TimeSpan? limit=null) {
        using var child=new Process {StartInfo=info};
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(limit??TimeSpan.FromSeconds(10));
        ct.ThrowIfCancellationRequested();child.Start();
        var error=Tail(child.StandardError);var output=Tail(child.StandardOutput);
        try {
            await child.WaitForExitAsync(deadline.Token);
            var detail=await error;await output;
            if(child.ExitCode!=0)throw new IOException("엔진 설정 검사 실패: "+detail.Trim());
        } catch(OperationCanceledException) {
            if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}
            await Task.WhenAll(error,output);
            if(ct.IsCancellationRequested)throw;
            throw new IOException("엔진 설정 검사 시간 초과 · 검사 프로세스를 정리했습니다.");
        }
    }
    static async Task<string> Tail(StreamReader reader) {
        char[] buffer=new char[1024];string tail="";int count;
        while((count=await reader.ReadAsync(buffer))>0){tail+=new string(buffer,0,count);if(tail.Length>4096)tail=tail[^4096..];}
        return tail;
    }
}
