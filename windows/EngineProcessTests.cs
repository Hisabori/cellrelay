using System.Diagnostics;
namespace CellRelay;
static class EngineProcessTests
{
    public static async Task Run(){
        static ProcessStartInfo Command(string code){var p=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};p.ArgumentList.Add("/d");p.ArgumentList.Add("/c");p.ArgumentList.Add("exit "+code);return p;}
        static ProcessStartInfo Slow(){var p=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};p.ArgumentList.Add("-NoProfile");p.ArgumentList.Add("-Command");p.ArgumentList.Add("Start-Sleep -Seconds 30");return p;}
        await EngineProcess.Check(Command("0"),CancellationToken.None);
        bool failed=false;try{await EngineProcess.Check(Command("7"),CancellationToken.None);}catch(IOException){failed=true;}if(!failed)throw new Exception("Failed engine check was accepted");
        var watch=Stopwatch.StartNew();failed=false;try{await EngineProcess.Check(Slow(),CancellationToken.None,TimeSpan.FromMilliseconds(250));}catch(IOException e){failed=e.Message.Contains("시간 초과");}if(!failed||watch.Elapsed>TimeSpan.FromSeconds(8))throw new Exception("Engine timeout cleanup failed");
        using var cancel=new CancellationTokenSource(250);watch.Restart();failed=false;try{await EngineProcess.Check(Slow(),cancel.Token);}catch(OperationCanceledException){failed=true;}if(!failed||watch.Elapsed>TimeSpan.FromSeconds(8))throw new Exception("Engine startup cancellation cleanup failed");
    }
}
