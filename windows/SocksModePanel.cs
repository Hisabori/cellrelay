namespace CellRelay;
sealed class SocksModePanel : UserControl
{
    readonly Button start=new(){Text="TCP·UDP SOCKS5 시작",AutoSize=true};
    readonly Button stop=new(){Text="중지",AutoSize=true,Enabled=false};
    readonly Label status=new(){Text="중지됨",AutoSize=true,MaximumSize=new Size(720,0)};
    SocksRelay? relay;
    readonly CancellationTokenSource closing=new();
    CancellationTokenSource? probe;
    public bool Active=>relay!=null;
    public SocksModePanel(Func<ConnectionProfile> profile,Action<string> log,Action register){
        Dock=DockStyle.Fill;var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="TCP CONNECT와 UDP ASSOCIATE를 지원하는 로컬 SOCKS5 연결입니다. 관리자 권한과 터널 엔진 없이 사용할 수 있습니다. 대상 앱이 SOCKS5와 UDP 전달을 지원해야 합니다. HTTP 프록시만 지원하는 앱은 TCP 모드를 사용하세요."});
        var pairing=new Button {Text="QR · CR1 등록",AutoSize=true};pairing.Click+=(_,_)=>register();panel.Controls.Add(pairing);
        panel.Controls.Add(start);panel.Controls.Add(stop);panel.Controls.Add(status);
        var copy=new Button {Text="SOCKS5 주소 복사",AutoSize=true};panel.Controls.Add(copy);
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="시작 후 앱의 SOCKS5 서버에 화면의 127.0.0.1 주소와 포트를 입력하세요. 로컬 SOCKS 인증은 사용하지 않습니다. 아이폰 구간은 등록한 연결 키로 인증합니다. UDP 사용에는 아이폰 0.7 이상이 필요합니다. PC 전체 트래픽 전환은 VPN 모드에서 지원합니다."});
        var test=new Button {Text="아이폰 UDP·DNS 검사",AutoSize=true};var cancelTest=new Button {Text="검사 취소",AutoSize=true,Enabled=false};var result=new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="UDP 검사: 아직 실행하지 않음"};
        panel.Controls.Add(test);panel.Controls.Add(cancelTest);panel.Controls.Add(result);
        panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(720,0),Text="아이폰을 통해 1.1.1.1:53에 example.com DNS 질의를 보냅니다. 검사 성공은 해당 경로의 UDP 응답 확인이며 모든 앱의 동작을 보장하지 않습니다."});
        test.Click+=async(_,_)=>{
            if(probe!=null)return;using var attempt=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);probe=attempt;test.Enabled=false;cancelTest.Enabled=true;
            try{var selected=profile();var message=await DatagramProbe.Run(selected,s=>{if(!IsDisposed)result.Text="검사 중: "+s;},attempt.Token);if(!IsDisposed)result.Text=message;log(message);}
            catch(OperationCanceledException){if(!IsDisposed)result.Text="UDP 검사 취소됨";}
            catch(Exception e)when(e is IOException or FormatException){if(!IsDisposed)result.Text="UDP 검사 실패: "+e.Message;log("UDP 검사 실패: "+e.Message);}
            finally{probe=null;if(!IsDisposed){test.Enabled=true;cancelTest.Enabled=false;}}
        };
        cancelTest.Click+=(_,_)=>probe?.Cancel();
        Controls.Add(panel);
        start.Click+=(_,_)=>{
            if(relay!=null)return;
            try{var selected=profile();if(Uri.CheckHostName(selected.Host)==UriHostNameType.Unknown)throw new FormatException("아이폰 주소를 확인하세요.");relay=new SocksRelay(selected,log);relay.Start();status.Text=$"수신 중 · SOCKS5 127.0.0.1:{relay.Port} · TCP + UDP · 앱에서 직접 설정하세요. 아이폰 연결은 앱 요청 시 시작합니다.";start.Enabled=false;stop.Enabled=true;log("SOCKS5 수신 시작 · TCP/UDP · 127.0.0.1:"+relay.Port);}
            catch(Exception e)when(e is FormatException or System.Net.Sockets.SocketException or IOException){relay?.Dispose();relay=null;status.Text="시작 실패: "+e.Message;log(status.Text);}
        };
        stop.Click+=(_,_)=>RequestStop();
        copy.Click+=(_,_)=>{if(relay==null){status.Text="먼저 SOCKS5를 시작하세요.";return;}try{Clipboard.SetText("127.0.0.1:"+relay.Port);}catch(System.Runtime.InteropServices.ExternalException){status.Text="클립보드 사용 중 · 잠시 후 다시 시도하세요.";}};
        Disposed+=(_,_)=>{closing.Cancel();probe?.Cancel();relay?.Dispose();relay=null;};
    }
    public void RequestStart(){if(Parent is TabPage page&&page.Parent is TabControl tabs)tabs.SelectedTab=page;start.PerformClick();}
    public void RequestStop(){probe?.Cancel();relay?.Dispose();relay=null;if(!IsDisposed){start.Enabled=true;stop.Enabled=false;status.Text="중지됨";}}
}

