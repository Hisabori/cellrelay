using System.Diagnostics;
using System.Security.Principal;

namespace CellRelay;
sealed class GameModePanel : UserControl
{
    readonly Button start=new(){Text="게임 모드 시작 (전체 TCP·UDP)",AutoSize=true};
    readonly Button stop=new(){Text="게임 모드 중지",AutoSize=true,Enabled=false};
    readonly Label status=new(){Text="중지됨",AutoSize=true,MaximumSize=new Size(720,0)};
    readonly Label readiness=new(){AutoSize=true,MaximumSize=new Size(720,0)};
    readonly CancellationTokenSource closing=new();
    CancellationTokenSource? starting;
    SocksRelay? socks;
    Process? engine;
    readonly Action<string> log;
    readonly Func<ConnectionProfile> profile;
    string EnginePath=>Path.Combine(AppContext.BaseDirectory,"Engine","sing-box.exe");
    public bool Active=>socks!=null || starting!=null || engine!=null;
    public GameModePanel(Func<ConnectionProfile> profile,Action<string> log,Action register)
    {
        this.log=log;this.profile=profile;Dock=DockStyle.Fill;
        var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="아이폰 0.7 이상에서 중계를 켜고 QR/CR1 연결 정보를 등록하세요. 게임 모드는 PC 전체 TCP·UDP와 DNS를 아이폰 중계로 전달합니다. 관리자 권한이 필요하며 다른 VPN은 종료한 상태로 시작하세요."});
        panel.Controls.Add(readiness);
        var ready=new Button {Text="준비 상태 새로고침",AutoSize=true};ready.Click+=(_,_)=>RefreshReadiness();panel.Controls.Add(ready);
        var pairing=new Button {Text="QR · CR1 연결 정보 등록",AutoSize=true};pairing.Click+=(_,_)=>register();panel.Controls.Add(pairing);
        panel.Controls.Add(start);panel.Controls.Add(stop);panel.Controls.Add(status);
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="UDP는 PC–아이폰 사이 TCP 터널로 전달되어 지연이 증가할 수 있습니다. ICMP·외부에서 먼저 시작하는 연결은 지원하지 않습니다. 아이폰 화면을 켜 두세요. 엔진 실행 표시는 게임 접속 성공을 뜻하지 않습니다. 실제 TUN·게임·통신사 집계는 미검증입니다."});
        Controls.Add(panel);RefreshReadiness();VisibleChanged+=(_,_)=>{if(Visible)RefreshReadiness();};
        start.Click+=async(_,_)=>await Start();
        stop.Click+=(_,_)=>{
            if(starting!=null){starting.Cancel();stop.Enabled=false;status.Text="시작 취소 중 · 연결과 검사 프로세스를 정리합니다.";}
            else {Cleanup();SetIdle("중지됨 · 터널 엔진 종료");}
        };
        var timer=new System.Windows.Forms.Timer {Interval=1000};
        timer.Tick+=(_,_)=>{if(starting==null && engine is {HasExited:true}){Cleanup();SetIdle("터널 엔진 종료 · 연결 기록을 확인하세요.");}};timer.Start();
        Disposed+=(_,_)=>{closing.Cancel();starting?.Cancel();timer.Stop();timer.Dispose();Cleanup();};
    }
    static bool Administrator(){using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
    void RefreshReadiness(){
        string connection;
        try{var p=profile();TunnelConfig.Create(p.Host,1080);connection=$"등록됨 · {p.Host}:{p.Port}";}
        catch(FormatException e){connection=e.Message;}
        bool files=File.Exists(EnginePath)&&File.Exists(Path.Combine(Path.GetDirectoryName(EnginePath)!,"libcronet.dll"));
        readiness.Text=$"연결 정보: {connection}\r\n터널 엔진 파일: {(files?"준비됨 · 시작 시 설정 검사":"준비 필요 · GAME-MODE.txt 확인")}\r\n관리자 권한: {(Administrator()?"확인됨":"필요 · 실행 파일을 관리자 권한으로 실행")}";
    }
    async Task Start(){
        if(Active)return;
        RefreshReadiness();
        if(!Administrator()){MessageBox.Show("앱을 종료하고 Desktop/CellRelay.exe를 마우스 오른쪽 버튼으로 눌러 관리자 권한으로 실행한 뒤 연결 정보를 다시 등록하세요.");return;}
        if(!File.Exists(EnginePath)||!File.Exists(Path.Combine(Path.GetDirectoryName(EnginePath)!,"libcronet.dll"))){MessageBox.Show("GAME-MODE.txt에 따라 공식 엔진을 준비하여 Desktop/Engine 폴더에 넣으세요.");return;}
        using var attempt=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);starting=attempt;
        start.Enabled=false;stop.Enabled=true;stop.Text="시작 취소";
        bool running=false;string outcome="중지됨";
        try{
            var selected=profile();TunnelConfig.Create(selected.Host,1080);
            status.Text="1/3 · 아이폰 HTTPS 경로 확인";
            await RelayProbe.Run(selected.Host,selected.Port,selected.Token,s=>{if(!IsDisposed)status.Text="1/3 · "+s;},attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            socks=new SocksRelay(selected,log);socks.Start();
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CellRelay");Directory.CreateDirectory(folder);
            string config=Path.Combine(folder,"game-tunnel.json");File.WriteAllText(config,TunnelConfig.Create(selected.Host,socks.Port));
            status.Text="2/3 · 터널 엔진 설정 검사 (최대 10초)";
            await EngineProcess.Check(EngineProcess.Info(EnginePath,"check",config),attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();status.Text="3/3 · 터널 엔진 시작";
            var launched=new Process {StartInfo=EngineProcess.Info(EnginePath,"run",config)};engine=launched;
            launched.ErrorDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))log("터널: "+e.Data);};launched.OutputDataReceived+=(_,_)=>{};
            launched.Start();launched.BeginErrorReadLine();launched.BeginOutputReadLine();
            await Task.Delay(1200,attempt.Token);
            if(launched.HasExited)throw new IOException("터널 엔진이 시작 직후 종료됐습니다. 연결 기록에서 오류를 확인하세요.");
            running=true;outcome="터널 엔진 실행 중 · 지포스 나우에서 네트워크 검사와 게임 접속을 확인하세요.";log("게임 터널 시작 · PC 전체 TCP/UDP · SOCKS 127.0.0.1:"+socks.Port);
        }catch(OperationCanceledException){outcome="시작 취소됨 · 연결과 검사 프로세스 정리 완료";log("게임 모드 시작 취소");}
        catch(Exception e)when(e is IOException or FormatException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException){outcome="시작 실패: "+e.Message;log(outcome);}
        finally{
            starting=null;if(!running)Cleanup();
            if(!IsDisposed){status.Text=outcome;start.Enabled=!running;stop.Enabled=running;stop.Text="게임 모드 중지";RefreshReadiness();}
        }
    }
    public void RequestStart(){if(Parent is TabPage page&&page.Parent is TabControl tabs)tabs.SelectedTab=page;start.PerformClick();}
    void SetIdle(string message){if(!IsDisposed){start.Enabled=true;stop.Enabled=false;stop.Text="게임 모드 중지";status.Text=message;RefreshReadiness();}}
    void Cleanup(){
        if(engine!=null){try{if(!engine.HasExited){engine.Kill(entireProcessTree:true);engine.WaitForExit(5000);}}catch(InvalidOperationException){}finally{engine.Dispose();engine=null;}}
        socks?.Dispose();socks=null;
    }
}
