using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CellRelay;

static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { try { SelfTest.Run().GetAwaiter().GetResult(); Environment.Exit(0); } catch (Exception e) { File.WriteAllText("self-test-result.txt", e.ToString()); Environment.Exit(1); } return; }
        using var singleInstance = new Mutex(true, @"Local\CellRelay-PC-UI", out bool created);
        if (!created) { MessageBox.Show("CellRelay PC UI가 이미 실행 중입니다. 작업 표시줄에서 기존 창을 열어 주세요."); return; }
        ApplicationConfiguration.Initialize(); Application.Run(new MainForm());
    }
}

sealed class MainForm : Form
{
    readonly TextBox host = new() { Text = "172.20.10.1", Width = 210 };
    readonly TextBox token = new() { Width = 350, UseSystemPasswordChar = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "중계 시작", AutoSize = true };
    readonly Button stop = new() { Text = "중지", Enabled = false, AutoSize = true };
    readonly Button diagnose = new() { Text = "아이폰 연결 검사", AutoSize = true };
    readonly NumericUpDown proxyPort = new() { Minimum = 1024, Maximum = 65535, Value = 8877, Width = 100 };
    readonly NumericUpDown phonePort = new() { Minimum = 1024, Maximum = 65535, Value = 49152, Width = 100 };
    readonly TextBox profileInput = new() { Width = 420, PlaceholderText = "CR1:로 시작하는 연결 코드 전체", UseSystemPasswordChar = true };
    readonly Button import = new() { Text = "연결 정보 적용", AutoSize = true };
    readonly Label proxyHint = new() { AutoSize = true };
    readonly Label phoneStatus = new() { AutoSize = true, Text = "아이폰 연결: 아직 검사하지 않음" };
    readonly CancellationTokenSource closing = new();
    long lastUp, lastDown;
    readonly Label statistics = new() { AutoSize = true, MaximumSize = new Size(710, 0), Text = "대기 중" };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    Proxy? proxy;
    DatagramRelay? datagrams;
    public MainForm()
    {
        Text = "CellRelay 0.7 · PC 연결 관리자"; Width = 900; Height = 780; MinimumSize = new Size(800, 650);
        Font = new Font("Malgun Gothic", 9F); BackColor = Color.White;
        var saved = AppSettings.Load(AppSettings.DefaultPath); host.Text = saved.Host; proxyPort.Value = saved.ProxyPort; phonePort.Value = saved.PhonePort;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 440, AutoScroll = true, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label { Text = "CellRelay · 아이폰 셀룰러 연결", Font = new Font(Font.FontFamily, 15F, FontStyle.Bold), AutoSize = true });
        panel.Controls.Add(new Label { Text = "① 아이폰 중계 시작 → ② 연결 정보 적용 → ③ PC 중계 시작 → ④ 브라우저 프록시 설정", AutoSize = true, Margin = new Padding(3, 8, 3, 12) });
        panel.Controls.Add(new Label { Text = "CR1 코드로 입력: 아이폰의 'PC용 CR1 연결 코드 전체 복사'를 사용하세요", AutoSize = true });
        var profileRow = new FlowLayoutPanel { AutoSize = true }; profileRow.Controls.AddRange([profileInput, import]); panel.Controls.Add(profileRow);
        var easy = new Button { Text = "코드 적용 · 검사 · 간편 연결", AutoSize = true };
        var qr = new Button { Text = "QR 이미지로 등록", AutoSize = true };
        var qrClipboard = new Button { Text = "클립보드 QR 등록", AutoSize = true };
        var quickRow = new FlowLayoutPanel { AutoSize = true }; quickRow.Controls.AddRange([easy, qr, qrClipboard]); panel.Controls.Add(quickRow);
        void Apply(ConnectionProfile profile) {
            if (proxy != null || datagrams != null) throw new FormatException("실행 중인 중계를 먼저 중지하세요.");
            host.Text = profile.Host; token.Text = profile.Token; phonePort.Value = profile.Port; profileInput.Clear();
            Log("연결 정보를 적용했습니다. 간편 연결 또는 중계 시작을 누르세요.");
        }
        qr.Click += (_, _) => {
            using var picker = new OpenFileDialog { Filter = "QR 이미지|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
            if (picker.ShowDialog() != DialogResult.OK) return;
            try { if (new FileInfo(picker.FileName).Length > 20 * 1024 * 1024) throw new FormatException("이미지는 20MB 이하로 준비하세요."); using var image = new Bitmap(picker.FileName); Apply(QrProfile.Read(image)); }
            catch (Exception e) when (e is FormatException or ArgumentException or IOException or OutOfMemoryException or UnauthorizedAccessException) { MessageBox.Show("QR 등록 실패: " + e.Message); }
        };
        qrClipboard.Click += (_, _) => {
            try { using var source = Clipboard.GetImage(); if (source is null) throw new FormatException("클립보드에 QR 이미지가 없습니다. 아이폰 QR 화면 이미지를 복사하세요."); using var image = new Bitmap(source); Apply(QrProfile.Read(image)); }
            catch (Exception e) when (e is FormatException or ArgumentException or System.Runtime.InteropServices.ExternalException) { MessageBox.Show("QR 등록 실패: " + e.Message); }
        };
        easy.Click += async (_, _) => {
            if (proxy != null || datagrams != null) { MessageBox.Show("실행 중인 중계를 먼저 중지하세요."); return; }
            try {
                if (!string.IsNullOrWhiteSpace(profileInput.Text)) Apply(ConnectionProfile.Parse(profileInput.Text));
                if (Uri.CheckHostName(host.Text.Trim()) == UriHostNameType.Unknown || token.Text.Trim().Length != 32 || !token.Text.Trim().All(Uri.IsHexDigit)) throw new FormatException("CR1 코드 또는 QR을 먼저 등록하세요.");
                panel.Enabled = false;
                var result = await RelayProbe.Run(host.Text.Trim(), (int)phonePort.Value, token.Text.Trim(), stage => phoneStatus.Text = "간편 연결: " + stage, closing.Token);
                phoneStatus.Text = result; Log(result);
                panel.Enabled = true; start.PerformClick();
            } catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or FormatException) { phoneStatus.Text = "간편 연결 실패: " + e.Message; Log(phoneStatus.Text); }
            finally { if (!IsDisposed) panel.Enabled = true; }
        };
        var reveal = new CheckBox { Text = "연결 코드·키 보기 (화면 공유 시 주의)", AutoSize = true }; reveal.CheckedChanged += (_, _) => { profileInput.UseSystemPasswordChar = token.UseSystemPasswordChar = !reveal.Checked; }; panel.Controls.Add(reveal);
        panel.Controls.Add(new Label { Text = "직접 입력: CR1 코드가 없다면 아래 주소·실제 포트·연결 키만 입력하세요", AutoSize = true, Margin = new Padding(3, 12, 3, 6) });
        panel.Controls.Add(new Label { Text = "아이폰 주소", AutoSize = true }); panel.Controls.Add(host);
        var phonePortRow = new FlowLayoutPanel { AutoSize = true }; phonePortRow.Controls.AddRange([new Label { Text = "아이폰 실제 중계 포트", AutoSize = true }, phonePort]); panel.Controls.Add(phonePortRow);
        panel.Controls.Add(new Label { Text = "아이폰 앱에 표시된 연결 키", AutoSize = true }); panel.Controls.Add(token);
        var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.AddRange([start, stop, diagnose]); panel.Controls.Add(buttons);
        var portRow = new FlowLayoutPanel { AutoSize = true }; portRow.Controls.AddRange([new Label { Text = "PC 프록시 포트", AutoSize = true }, proxyPort]); panel.Controls.Add(portRow);
        panel.Controls.Add(proxyHint);
        panel.Controls.Add(statistics);
        panel.Controls.Add(phoneStatus);
        var tools = new FlowLayoutPanel { AutoSize = true };
        var copyProxy = new Button { Text = "프록시 주소 복사", AutoSize = true }; var export = new Button { Text = "진단 기록 저장", AutoSize = true }; var clear = new Button { Text = "기록 지우기", AutoSize = true };
        tools.Controls.AddRange([copyProxy, export, clear]); panel.Controls.Add(tools);
        void Hint() => proxyHint.Text = $"브라우저 HTTP/HTTPS 프록시: 127.0.0.1:{proxyPort.Value} · 시스템 설정은 직접 변경";
        Hint(); proxyPort.ValueChanged += (_, _) => Hint();
        copyProxy.Click += (_, _) => { try { Clipboard.SetText($"127.0.0.1:{proxyPort.Value}"); } catch (System.Runtime.InteropServices.ExternalException) { Log("다른 프로그램이 클립보드를 사용 중입니다."); } };
        clear.Click += (_, _) => log.Clear();
        import.Click += (_, _) => { try { Apply(ConnectionProfile.Parse(profileInput.Text)); } catch (FormatException e) { MessageBox.Show(e.Message); } };
        export.Click += (_, _) => { using var picker = new SaveFileDialog { Filter = "진단 기록 (*.txt)|*.txt", FileName = "CellRelay-diagnostics.txt" }; if (picker.ShowDialog() != DialogResult.OK) return; try { File.WriteAllText(picker.FileName, $"CellRelay 0.7 · {DateTimeOffset.Now:O}\r\n{statistics.Text}\r\n현재 연결 키와 연결 코드를 가립니다. 목적지 호스트가 기록에 포함될 수 있습니다.\r\n\r\n" + Diagnostics.Redact(log.Text, token.Text.Trim())); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("저장 실패: " + e.Message); } };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var logTab = new TabPage("연결 기록"); logTab.Controls.Add(log);
        var guideTab = new TabPage("사용 안내 · 백그라운드");
        guideTab.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = "연결 방법\r\n\r\n1. 아이폰 앱에서 중계를 시작합니다.\r\n2. PC와 아이폰을 신뢰하는 USB/로컬 네트워크로 연결합니다.\r\n3. 아이폰의 CR1 연결 정보를 위에 붙여 넣고 적용합니다. 주소·키 직접 입력도 가능합니다.\r\n4. 아이폰 연결 검사 후 PC 중계를 시작합니다.\r\n5. 브라우저의 HTTP/HTTPS 프록시에 화면에 표시된 127.0.0.1 주소와 포트를 입력합니다.\r\n\r\n아이폰 0.4 백그라운드\r\n\r\niOS 허용 시간 동안 기존 연결만 마무리하며 새 연결은 받지 않습니다. 시간 만료 시 중지합니다. 앱을 다시 열면 자동 재개 설정에 따라 새로운 연결을 받습니다. 끊긴 업로드를 자동 재전송하지 않습니다. 무기한 백그라운드 동작은 지원하지 않습니다.\r\n\r\nPC 중계 시작은 로컬 프록시 수신 시작입니다. 아이폰 연결 성공 여부는 연결 검사를 따로 실행하세요. 검사 결과는 해당 시각의 결과입니다. 전송량은 통신사 데이터 집계값이 아닙니다.\r\n\r\n사용 후 브라우저 프록시를 복구하세요. 실제 기기 동작과 공유 데이터 미차감은 미검증입니다." });
        var connectionTab = new TabPage("연결 설정"); panel.Dock = DockStyle.Fill; connectionTab.Controls.Add(panel);
        var udpTab = new TabPage("UDP · QUIC 전달");
        var udpPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var destination = new TextBox { Width = 320, PlaceholderText = "목적지 서버 호스트 또는 IP" };
        var destinationPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 443, Width = 100 };
        var localUdp = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 10800, Width = 100 };
        var udpStart = new Button { Text = "UDP 전달 시작", AutoSize = true };
        var udpStop = new Button { Text = "UDP 전달 중지", AutoSize = true, Enabled = false };
        var udpStatus = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Text = "중지됨" };
        udpPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(720, 0), Text = "목적지 고정 UDP 전달입니다. QUIC는 이 경로로 UDP 패킷을 전달합니다. 앱을 아래 로컬 포트로 보내도록 설정해야 합니다. QUIC의 원래 서버 이름·인증서 설정은 유지하세요. TCP 터널을 사용하므로 지연이 증가할 수 있습니다. 브라우저 HTTP 프록시는 QUIC를 자동 전달하지 않습니다." });
        udpPanel.Controls.Add(new Label { Text = "목적지 서버", AutoSize = true }); udpPanel.Controls.Add(destination);
        udpPanel.Controls.Add(new Label { Text = "목적지 UDP 포트", AutoSize = true }); udpPanel.Controls.Add(destinationPort);
        udpPanel.Controls.Add(new Label { Text = "PC 로컬 UDP 포트", AutoSize = true }); udpPanel.Controls.Add(localUdp);
        udpPanel.Controls.Add(udpStart); udpPanel.Controls.Add(udpStop); udpPanel.Controls.Add(udpStatus); udpTab.Controls.Add(udpPanel);
        udpStart.Click += (_, _) => {
            if (Uri.CheckHostName(destination.Text.Trim()) == UriHostNameType.Unknown || Uri.CheckHostName(host.Text.Trim()) == UriHostNameType.Unknown || token.Text.Trim().Length != 32 || !token.Text.Trim().All(Uri.IsHexDigit)) { MessageBox.Show("연결 정보와 목적지 서버를 먼저 입력하세요."); return; }
            try {
                datagrams = new DatagramRelay(host.Text.Trim(), (int)phonePort.Value, token.Text.Trim(), destination.Text.Trim(), (int)destinationPort.Value, (int)localUdp.Value, Log); datagrams.Start();
                udpStatus.Text = $"수신 중: 127.0.0.1:{datagrams.Port} → {destination.Text.Trim()}:{destinationPort.Value} · 아이폰 연결은 첫 패킷 시 시작";
                udpStart.Enabled = destination.Enabled = destinationPort.Enabled = localUdp.Enabled = false; udpStop.Enabled = true;
            } catch (Exception e) when (e is SocketException or IOException) { datagrams?.Dispose(); datagrams = null; udpStatus.Text = "시작 실패: " + e.Message; }
        };
        udpStop.Click += (_, _) => { datagrams?.Dispose(); datagrams = null; udpStart.Enabled = destination.Enabled = destinationPort.Enabled = localUdp.Enabled = true; udpStop.Enabled = false; udpStatus.Text = "중지됨"; };
        tabs.TabPages.AddRange([connectionTab, udpTab, logTab, guideTab]); Controls.Add(tabs);
        start.Click += (_, _) => {
            if (token.Text.Trim().Length != 32 || !token.Text.Trim().All(Uri.IsHexDigit) || Uri.CheckHostName(host.Text.Trim()) == UriHostNameType.Unknown) { MessageBox.Show("아이폰 주소와 앱에 표시된 32자리 연결 키를 입력하세요. 주소에는 http:// 또는 포트를 넣지 마세요."); return; }
            try { int requestedPort = (int)proxyPort.Value; proxy = ProxyStarter.Start(host.Text.Trim(), token.Text.Trim(), requestedPort, Log, (int)phonePort.Value); proxyPort.Value = proxy.Port; if (proxy.Port != requestedPort) { proxyHint.ForeColor = Color.Firebrick; proxyHint.Font = new Font(Font, FontStyle.Bold); } else { proxyHint.ForeColor = SystemColors.ControlText; proxyHint.Font = Font; } lastUp = lastDown = 0; start.Enabled = false; stop.Enabled = true; host.Enabled = token.Enabled = phonePort.Enabled = proxyPort.Enabled = import.Enabled = profileInput.Enabled = false; try { new AppSettings(host.Text.Trim(), (int)proxyPort.Value, (int)phonePort.Value).Save(AppSettings.DefaultPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("설정 저장 실패: " + e.Message); } Log("PC 수신 시작: 127.0.0.1:" + proxy.Port + " · 브라우저에도 같은 포트를 설정하세요. 공유 데이터 미차감은 미검증입니다."); }
            catch (Exception e) { proxy?.Dispose(); proxy = null; Log(e.Message); }
        };
        stop.Click += (_, _) => Stop(); FormClosing += (_, _) => { closing.Cancel(); datagrams?.Dispose(); refresh.Stop(); refresh.Dispose(); proxy?.Dispose(); };
        diagnose.Click += async (_, _) => {
            if (Uri.CheckHostName(host.Text.Trim()) == UriHostNameType.Unknown || token.Text.Trim().Length != 32 || !token.Text.Trim().All(Uri.IsHexDigit)) { MessageBox.Show("아이폰 주소와 앱의 32자리 연결 키를 먼저 입력하세요."); return; }
            var probeHost = host.Text.Trim(); var probePort = (int)phonePort.Value; var probeKey = token.Text.Trim();
            diagnose.Enabled = false;
            try {
                var result = await RelayProbe.Run(probeHost, probePort, probeKey, stage => { if (!IsDisposed) phoneStatus.Text = "검사 중: " + stage; }, closing.Token);
                if (!IsDisposed) phoneStatus.Text = $"마지막 검사 {DateTime.Now:HH:mm:ss}: {result}";
                Log(result + " · 아이폰 중계 경로 검사. 브라우저 설정·통신사 집계는 별도 확인하세요.");
            } catch (OperationCanceledException) { }
            catch (IOException e) { if (!IsDisposed) phoneStatus.Text = "검사 실패: " + e.Message; Log("검사 실패: " + e.Message); }
            finally { if (!IsDisposed) diagnose.Enabled = true; }
        };
        refresh.Tick += (_, _) => { if (proxy is { } current) { var up = current.Uploaded; var down = current.Downloaded; statistics.Text = current.Summary + $" · ↑{Math.Max(0, up-lastUp):N0} ↓{Math.Max(0, down-lastDown):N0} B/초(약)"; lastUp = up; lastDown = down; } }; refresh.Start();
    }
    void Stop() { var final = proxy?.Summary; proxy?.Dispose(); proxy = null; start.Enabled = true; stop.Enabled = false; host.Enabled = token.Enabled = phonePort.Enabled = proxyPort.Enabled = import.Enabled = profileInput.Enabled = true; statistics.Text = "중지됨"; Log("중지됨 · " + final); }
    void Log(string s) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(new Action(() => { if (IsDisposed) return; if (log.TextLength > 32000) log.Text = log.Text[^16000..]; log.AppendText($"{DateTime.Now:HH:mm:ss} {s}\r\n"); })); } catch (InvalidOperationException) { } }
}

sealed class Proxy(string phone, int phonePort, string token, int port, Action<string> log, TimeSpan? idleTimeout = null) : IDisposable
{
    readonly CancellationTokenSource shutdown = new();
    readonly TcpListener listener = new(IPAddress.Loopback, port);
    readonly SemaphoreSlim capacity = new(32);
    long uploaded, downloaded, failures;
    int active, disposed;
    public int Active => Volatile.Read(ref active);
    public long Uploaded => Interlocked.Read(ref uploaded);
    public long Downloaded => Interlocked.Read(ref downloaded);
    public string Summary => $"연결 {Active}/32 · 업로드 {Interlocked.Read(ref uploaded):N0} B · 다운로드 {Interlocked.Read(ref downloaded):N0} B · 오류 {Interlocked.Read(ref failures)}";
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start() { listener.Start(); _ = Accept(); }
    async Task Accept()
    {
        try { while (!shutdown.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(shutdown.Token); if (!capacity.Wait(0)) { client.Dispose(); continue; } _ = Handle(client); } }
        catch (OperationCanceledException) { } catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
        catch (SocketException e) { if (!shutdown.IsCancellationRequested) log("수신 오류: " + e.Message); }
    }
    async Task Handle(TcpClient client)
    {
        Interlocked.Increment(ref active);
        using (client)
        using (var remote = new TcpClient())
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        {
            bool committed = false;
            try
            {
                session.CancelAfter(TimeSpan.FromSeconds(20)); var ct = session.Token;
                var local = client.GetStream(); var header = await ReadHeader(local, ct);
                var plan = RequestPlan.Parse(header); var target = plan.Host; var targetPort = plan.Port;
                client.NoDelay = remote.NoDelay = true;
                await remote.ConnectAsync(phone, phonePort, ct); var bridge = remote.GetStream();
                await bridge.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token, host = target, port = targetPort }) + "\n"), ct);
                var response = await ReadLine(bridge, ct);
                if (response != "OK") throw new IOException("아이폰 중계 거절: " + response);
                if (plan.Tunnel) { await local.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct); committed = true; }
                else { await bridge.WriteAsync(plan.Initial!, ct); Interlocked.Add(ref uploaded, plan.Initial!.Length); committed = true; }
                log($"연결: {target}:{targetPort}"); session.CancelAfter(idleTimeout ?? TimeSpan.FromMinutes(2));
                async Task Transfer(Stream from, Stream to, Socket destination, bool upload) {
                    try { await Pump(from, to, destination, ct, count => { if (upload) Interlocked.Add(ref uploaded, count); else Interlocked.Add(ref downloaded, count); session.CancelAfter(idleTimeout ?? TimeSpan.FromMinutes(2)); }); }
                    catch { session.Cancel(); throw; }
                }
                var up = Transfer(local, bridge, remote.Client, true); var down = Transfer(bridge, local, client.Client, false);
                await Task.WhenAll(up, down);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!shutdown.IsCancellationRequested) { Interlocked.Increment(ref failures); log("연결 종료: " + (e is OperationCanceledException ? "응답 대기 또는 유휴 시간 초과" : e.Message)); }
                int status = e is RequestError bad ? bad.Status : e is OperationCanceledException ? 504 : 502;
                if (!committed && !shutdown.IsCancellationRequested) { try { using var timeout = new CancellationTokenSource(1000); await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Error\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), timeout.Token); } catch { } }
            }
            finally { session.Cancel(); Interlocked.Decrement(ref active); capacity.Release(); }
        }
    }
    static async Task Pump(Stream from, Stream to, Socket socket, CancellationToken ct, Action<int> activity)
    {
        try { byte[] buffer = new byte[65536]; int count; while ((count = await from.ReadAsync(buffer, ct)) > 0) { activity(count); await to.WriteAsync(buffer.AsMemory(0, count), ct); activity(0); } try { socket.Shutdown(SocketShutdown.Send); } catch (SocketException) { } }
        catch { try { socket.Dispose(); } catch { } throw; }
    }
    internal static async Task<string> ReadLine(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(); byte[] one = new byte[1];
        while (bytes.Count < 8192) { if (await stream.ReadAsync(one, ct) == 0) throw new IOException("중계 응답 없음"); if (one[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r'); bytes.Add(one[0]); }
        throw new IOException("응답 헤더가 너무 큽니다");
    }
    internal static async Task<string> ReadHeader(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(); byte[] one = new byte[1];
        while (bytes.Count < 32768) { if (await stream.ReadAsync(one, ct) == 0) throw new IOException("요청 중단"); bytes.Add(one[0]); int n = bytes.Count; if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) return Encoding.Latin1.GetString(bytes.ToArray()); }
        throw new RequestError(431, "요청 헤더가 너무 큽니다");
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; shutdown.Cancel(); listener.Stop(); }
}

static class SelfTest
{
    public static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = deadline.Token;
        var fake = new TcpListener(IPAddress.Loopback, 0); fake.Start();
        try
        {
            var server = Task.Run(async () => {
                for (int i = 0; i < 4; i++) {
                    using var c = await fake.AcceptTcpClientAsync(ct); var s = c.GetStream(); var line = await Proxy.ReadLine(s, ct);
                    using var doc = JsonDocument.Parse(line); if (doc.RootElement.GetProperty("token").GetString() != "0123456789abcdef") throw new Exception("Token mismatch");
                    if (doc.RootElement.GetProperty("host").GetString() != "example.com") throw new Exception("Host mismatch");
                    if (i == 2) { await s.WriteAsync("ERR authentication\n"u8.ToArray(), ct); continue; }
                    await s.WriteAsync("OK\n"u8.ToArray(), ct);
                    if (i is 0 or 3) { byte[] b = new byte[4]; await s.ReadExactlyAsync(b, ct); await s.WriteAsync(b, ct); }
                    else { var h = await Proxy.ReadHeader(s, ct); if (!h.StartsWith("GET /probe?q=1 HTTP/1.1\r\n") || h.Contains("Proxy-Authorization")) throw new Exception("HTTP rewrite failed"); await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nTEST"u8.ToArray(), ct); }
                }
            }, ct);
            using var p = new Proxy("127.0.0.1", ((IPEndPoint)fake.LocalEndpoint).Port, "0123456789abcdef", 0, _ => {}); p.Start();
            for (int i = 0; i < 4; i++) {
                using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, p.Port, ct); var s = c.GetStream();
                string req = i == 1 ? "GET http://example.com/probe?q=1 HTTP/1.1\r\nProxy-Authorization: secret\r\n\r\n" : "CONNECT example.com:443 HTTP/1.1\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(req), ct); var h = await Proxy.ReadHeader(s, ct);
                if (i == 2) { if (!h.StartsWith("HTTP/1.1 502")) throw new Exception("Authentication failure not propagated"); }
                else { if (!h.StartsWith("HTTP/1.1 200")) throw new Exception("Proxy response failed"); if (i is 0 or 3) await s.WriteAsync("TEST"u8.ToArray(), ct); byte[] b = new byte[4]; await s.ReadExactlyAsync(b, ct); if (Encoding.ASCII.GetString(b) != "TEST") throw new Exception("Relay corruption"); }
            }
            await server;
            await StabilityTests.Run();
            FeatureTests.Run();
            await ProbeTests.Run();
            await DatagramTests.Run();
            File.WriteAllText("self-test-result.txt", "PASS: relay; HTTP framing; authentication failure recovery; idle/stop cleanup; upload half-close; 32-session limit and restart; iOS profile import and invalid profile rejection; settings roundtrip and corrupt-file fallback; HTTPS probe rejection, TLS failure stage, cancellation and reply redaction; UDP frame boundaries/empty packet/echo; QR profile roundtrip and blank rejection. iOS/device/carrier behavior NOT tested.");
        }
        finally { fake.Stop(); }
    }
}




