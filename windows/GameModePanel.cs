using System.Diagnostics;
using System.Security.Principal;

namespace CellRelay;
sealed class GameModePanel : UserControl
{
    readonly Button start=new(){Text="게임 모드 시작 (전체 TCP·UDP)",AutoSize=true};
    readonly Button stop=new(){Text="게임 모드 중지",AutoSize=true,Enabled=false};
    readonly Label status=new(){Text="중지됨",AutoSize=true,MaximumSize=new Size(720,0)};
    readonly CancellationTokenSource closing=new();
    SocksRelay? socks;
    Process? engine;
    readonly Action<string> log;
    public bool Active=>socks!=null || !start.Enabled;
    public GameModePanel(Func<ConnectionProfile> profile,Action<string> log,Action register)
    {
        this.log=log; Dock=DockStyle.Fill;
        var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="지포스 나우용 실험 모드입니다. 관리자 권한으로 실행해야 합니다. 아이폰 0.7 이상에서 중계를 켜고 QR/CR1 연결 정보를 먼저 등록하세요. 시작하면 PC 전체의 TCP·UDP와 DNS 경로를 아이폰 중계로 바꿉니다. 다른 VPN은 먼저 종료하세요. 게임 실행과 품질은 실제 기기 확인이 필요합니다."});
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="UDP는 PC–아이폰 사이 TCP 터널로 전달되어 지연과 재전송 대기가 증가할 수 있습니다. ICMP·외부에서 먼저 시작하는 연결은 지원하지 않습니다. 아이폰 화면을 켜 둔 상태로 테스트하세요. 중지 버튼으로 이 앱이 시작한 터널을 종료합니다."});
        var pairing=new Button {Text="QR · CR1 연결 정보 등록",AutoSize=true}; pairing.Click+=(_,_)=>register(); panel.Controls.Add(pairing);
        panel.Controls.Add(start); panel.Controls.Add(stop); panel.Controls.Add(status); Controls.Add(panel);
        start.Click+=async(_,_)=> {
            using var identity=WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {MessageBox.Show("앱을 종료하고 Desktop/CellRelay.exe를 마우스 오른쪽 버튼으로 눌러 관리자 권한으로 실행한 뒤 연결 정보를 다시 등록하세요.");return;}
            string path=Path.Combine(AppContext.BaseDirectory,"Engine","sing-box.exe");
            if(!File.Exists(path)){MessageBox.Show("GAME-MODE.txt에 따라 공식 엔진을 준비하여 Desktop/Engine 폴더에 넣으세요.");return;}
            start.Enabled=false;
            try {
                var selected=profile(); TunnelConfig.Create(selected.Host,1080);
                status.Text="아이폰 HTTPS 경로 확인 중…";
                await RelayProbe.Run(selected.Host,selected.Port,selected.Token,s=>{if(!IsDisposed)status.Text=s;},closing.Token);
                socks=new SocksRelay(selected,log); socks.Start();
                string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CellRelay");Directory.CreateDirectory(folder);
                string config=Path.Combine(folder,"game-tunnel.json"); File.WriteAllText(config,TunnelConfig.Create(selected.Host,socks.Port));
                using(var check=new Process {StartInfo=Info(path,"check",config)}) {
                    check.Start(); var error=check.StandardError.ReadToEndAsync(); await check.WaitForExitAsync(closing.Token);
                    if(check.ExitCode!=0) throw new IOException("터널 설정 검사 실패: "+await error);
                }
                engine=new Process {StartInfo=Info(path,"run",config)};
                engine.ErrorDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))log("터널: "+e.Data);};
                engine.OutputDataReceived+=(_,_)=>{};
                engine.Start();engine.BeginErrorReadLine();engine.BeginOutputReadLine();
                await Task.Delay(1200,closing.Token);
                if(engine.HasExited)throw new IOException("터널 엔진이 종료됐습니다. 연결 기록에서 오류를 확인하세요.");
                status.Text="터널 엔진 실행 중 · 지포스 나우를 실행해 네트워크 검사를 진행하세요. 실제 게임 연결 성공을 뜻하지 않습니다.";stop.Enabled=true;
                log("게임 터널 시작 · PC 전체 TCP/UDP · SOCKS 127.0.0.1:"+socks.Port);
            } catch(Exception e) when(e is IOException or FormatException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) {Stop();if(!IsDisposed)status.Text="시작 실패: "+e.Message;log("게임 모드 시작 실패");}
        };
        stop.Click+=(_,_)=>Stop();
        var timer=new System.Windows.Forms.Timer {Interval=1000}; timer.Tick+=(_,_)=>{if(engine is {HasExited:true}){Stop();status.Text="터널 엔진 종료 · 연결 기록을 확인하세요.";}};timer.Start();
        Disposed+=(_,_)=>{closing.Cancel();timer.Stop();timer.Dispose();Stop();};
    }
    public void RequestStart() {
        if(Parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab=page;
        start.PerformClick();
    }
    static ProcessStartInfo Info(string path,string command,string config) {
        var info=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=Path.GetDirectoryName(path)!};
        info.ArgumentList.Add(command);info.ArgumentList.Add("-c");info.ArgumentList.Add(config);return info;
    }
    void Stop() {
        if(engine!=null) {try {if(!engine.HasExited){engine.Kill(entireProcessTree:true);engine.WaitForExit(5000);}}catch(InvalidOperationException){} finally{engine.Dispose();engine=null;}}
        socks?.Dispose();socks=null;
        if(!IsDisposed){start.Enabled=true;stop.Enabled=false;status.Text="중지됨 · 터널 엔진 종료";}
    }
}

