import SwiftUI
import Network
import UIKit
import Darwin
import CoreImage.CIFilterBuiltins

@main struct CellRelayApp: App {
    @StateObject private var relay = Relay()
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            NavigationStack {
                Form {
                    Section("연결") {
                        Text(relay.status)
                        Text(relay.cellularStatus).font(.caption)
                        LabeledContent("실제 중계 포트", value: relay.listenPort == 0 ? "대기 중" : String(relay.listenPort))
                        LabeledContent("로컬 주소", value: relay.addresses)
                        ConnectionSetupView(relay: relay)
                        Text("Windows 프로그램에 이 연결 키를 입력하세요.")
                        Text(relay.token).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                        Button("연결 키 복사") { UIPasteboard.general.string = relay.token }
                        Button("새 연결 키 만들기") { relay.rotateKey() }.disabled(relay.running)
                        Button(relay.running ? "중지" : "중계 시작") { relay.running ? relay.stop() : relay.start() }
                        Text(relay.statistics).font(.caption.monospaced())
                        Text(relay.backgroundStatus).font(.caption)
                        Toggle("앱 복귀 시 자동 재개", isOn: $relay.autoResume)
                    }
                    Section("테스트 범위") {
                        Text("PC → 아이폰 앱 → 셀룰러. 백그라운드에서는 기존 연결을 iOS가 허용한 시간 동안만 마무리하며 새 연결은 받지 않습니다. 시간 만료 시 중지하고 앱 복귀 시 재개합니다. 무기한 백그라운드 중계는 지원하지 않습니다.")
                        Text("HTTP/HTTPS TCP 프록시 및 UDP 패킷 전달 테스트용. QUIC는 UDP 전달 경로를 이용합니다. 공유 데이터 미차감은 검증되지 않았습니다.")
                        Text("신뢰하는 USB/로컬 네트워크에서만 사용하세요. 연결 키와 PC-아이폰 구간은 암호화되지 않습니다. HTTPS 내용은 원래의 TLS로 보호됩니다.")
                    }
                    Section("기록") { Text(relay.events).font(.caption.monospaced()).textSelection(.enabled) }
                }.navigationTitle("CellRelay 0.7")
            }.onChange(of: phase) { value in
                switch value {
                case .inactive: relay.prepareForBackground()
                case .background: relay.enterBackground()
                case .active: relay.enterForeground()
                @unknown default: break
                }
            }
        }
    }
}

private struct ConnectionSetupView: View {
    @ObservedObject var relay: Relay
    @State private var showCode = false
    @State private var showQR = false
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button("주소 새로고침 · 연결을 끊지 않음") { relay.refreshAddresses() }
            TextField("PC에서 접속할 아이폰 주소 (예: 172.20.10.1)", text: $relay.selectedHost)
                .keyboardType(.numbersAndPunctuation).textInputAutocapitalization(.never).autocorrectionDisabled()
            if relay.localHosts.count > 1 {
                ForEach(relay.localHosts, id: \.self) { host in
                    Button("\(host) 선택") { relay.selectedHost = host }
                }
            }
            if let code = relay.connectionCode {
                Button("PC용 CR1 연결 코드 전체 복사") { relay.copyProfile(host: relay.selectedHost.trimmingCharacters(in: .whitespacesAndNewlines)) }
                DisclosureGroup("CR1 코드 직접 보기", isExpanded: $showCode) {
                    Text(code).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                }
                Button("PC 등록용 QR 보기") { showQR.toggle() }
                if showQR, let image = qrImage(code) {
                    Image(uiImage: image).interpolation(.none).resizable().scaledToFit().frame(maxWidth: 300).padding(16).background(Color.white)
                        .accessibilityLabel("PC 등록용 연결 QR 코드")
                    Text("이 화면을 캡처한 뒤 PC에서 QR 이미지로 등록하거나 클립보드 QR 등록을 누르세요.").font(.caption)
                }
                ShareLink(item: code) { Text("연결 코드 공유") }
                Text("연결 코드에는 인증 키가 포함됩니다. 본인 PC에만 전달하세요.").font(.caption)
            } else {
                Text(relay.listenPort == 0 ? "중계를 시작하면 CR1 코드를 만들 수 있습니다." : "주소를 찾지 못했거나 올바르지 않습니다. PC를 아이폰 핫스팟에 연결하고 주소를 새로고침하세요. 주소를 알고 있다면 위에 직접 입력할 수 있습니다.").font(.caption)
            }
        }
    }
}

private func qrImage(_ text: String) -> UIImage? {
    let filter = CIFilter.qrCodeGenerator()
    filter.message = Data(text.utf8); filter.correctionLevel = "M"
    guard let output = filter.outputImage, let image = CIContext().createCGImage(output.transformed(by: CGAffineTransform(scaleX: 8, y: 8)), from: output.extent.applying(CGAffineTransform(scaleX: 8, y: 8))) else { return nil }
    return UIImage(cgImage: image)
}

final class Relay: ObservableObject {
    @Published var status = "중지됨"
    @Published var running = false
    @Published var events = ""
    @Published var addresses = "시작 시 확인"
    @Published var token = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    @Published var statistics = "연결 0 · 업로드 0 B · 다운로드 0 B"
    @Published var backgroundStatus = "화면 활성 상태"
    @Published var listenPort: UInt16 = 0
    @Published var selectedHost = ""
    @Published var cellularStatus = "셀룰러 경로 확인 중"
    private let cellularMonitor = NWPathMonitor(requiredInterfaceType: .cellular)
    init() {
        cellularMonitor.pathUpdateHandler = { [weak self] path in
            let text = path.status == .satisfied ? "셀룰러 경로 사용 가능 · 실제 인터넷은 PC에서 검사" : "셀룰러 경로 사용 불가 · 모바일 데이터 설정 확인"
            DispatchQueue.main.async { self?.cellularStatus = text }
        }
        cellularMonitor.start(queue: queue)
    }
    deinit { cellularMonitor.cancel() }
    @Published var autoResume = true
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid
    private var backgroundGeneration: UUID?
    private var resumeOnForeground = false
    private var isBackground = false
    private var queueIsBackground = false
    private var runID: UUID?
    // Listener lifecycle belongs to the main queue; sessions and counters belong to queue.
    private var queueRunID: UUID?
    private var uploaded: UInt64 = 0
    private var downloaded: UInt64 = 0
    private var refreshTicks = 0
    private var statsTimer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "cellrelay.network")
    private var listener: NWListener?
    private var preferredPort: UInt16 = 49152
    private var sessions: [UUID: Session] = [:]
    func prepareForBackground() {
        guard running, backgroundTask == .invalid else { return }
        let expectedRun = runID
        let generation = UUID(); backgroundGeneration = generation
        backgroundTask = UIApplication.shared.beginBackgroundTask(withName: "CellRelayFinishConnections") { [weak self] in
            guard let self, self.runID == expectedRun, self.backgroundGeneration == generation else { return }
            guard self.isBackground else { self.endBackgroundTime(); return }
            self.note("백그라운드 허용 시간 만료 · 기존 연결 정리")
            self.stop(resumeWhenActive: self.autoResume)
            self.backgroundStatus = "시간 만료 · 앱을 열면 재개"
        }
    }
    func enterBackground() {
        isBackground = true; UIApplication.shared.isIdleTimerDisabled = false
        guard running else { return }
        prepareForBackground()
        guard backgroundTask != .invalid else {
            stop(resumeWhenActive: autoResume); backgroundStatus = "백그라운드 실행 시간이 허용되지 않음"; return
        }
        backgroundStatus = "백그라운드 · 기존 연결만 마무리 중"
        queue.async { self.queueIsBackground = true }
    }
    func enterForeground() {
        isBackground = false
        refreshAddresses()
        queue.sync { queueIsBackground = false }
        endBackgroundTime()
        backgroundStatus = "화면 활성 상태"
        let resume = resumeOnForeground && autoResume; resumeOnForeground = false
        if resume { start(); note("앱 복귀 · 새 연결 수신 재개 (이전 요청은 재전송하지 않음)") }
        UIApplication.shared.isIdleTimerDisabled = running
    }
    private func endBackgroundTime() {
        let task = backgroundTask; backgroundTask = .invalid; backgroundGeneration = nil
        if task != .invalid { UIApplication.shared.endBackgroundTask(task) }
    }
    var localHosts: [String] { addresses.components(separatedBy: ", ").compactMap { entry in
        let host = entry.components(separatedBy: ": ").last ?? ""
        return IPv4Address(host) == nil ? nil : host
    } }
    func refreshAddresses() {
        let previous = localHosts
        addresses = Self.localAddresses()
        let current = localHosts
        if selectedHost.isEmpty || (previous.contains(selectedHost) && !current.contains(selectedHost)) {
            selectedHost = current.first ?? ""
        }
    }
    var connectionCode: String? { profileCode(host: selectedHost.trimmingCharacters(in: .whitespacesAndNewlines)) }
    private func profileCode(host: String) -> String? {
        guard IPv4Address(host) != nil, listenPort > 0,
              let data = try? JSONSerialization.data(withJSONObject: ["host": host, "token": token, "port": Int(listenPort)], options: [.sortedKeys]) else { return nil }
        return "CR1:" + data.base64EncodedString()
    }
    func copyProfile(host: String) {
        guard let code = profileCode(host: host) else { note("중계 준비와 주소를 확인한 뒤 연결 정보를 복사하세요."); return }
        UIPasteboard.general.string = code
        note("연결 정보를 복사했습니다. Windows에 직접 전달하세요. 인증 키가 포함되어 있습니다.")
    }
    func rotateKey() { guard !running else { return }; token = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }
    func note(_ message: String) { DispatchQueue.main.async { self.events = String((self.events + "\n" + message).suffix(6000)) } }
    func start(dynamicPort: Bool = false) {
        guard !running, !isBackground else { return }
        resumeOnForeground = false
        do {
            let currentRun = UUID(); let connectionToken = token
            let listener = try NWListener(using: .tcp, on: dynamicPort ? .any : NWEndpoint.Port(rawValue: preferredPort)!)
            self.listener = listener; self.runID = currentRun
            queue.async {
                self.queueRunID = currentRun; self.queueIsBackground = false; self.uploaded = 0; self.downloaded = 0; self.refreshTicks = 0
                self.statsTimer?.cancel()
                let timer = DispatchSource.makeTimerSource(queue: self.queue)
                timer.schedule(deadline: .now(), repeating: 1)
                timer.setEventHandler { [weak self] in
                    guard let self, self.queueRunID == currentRun else { return }
                    let summary = "연결 \(self.sessions.count)/32 · 업로드 \(self.uploaded) B · 다운로드 \(self.downloaded) B"
                    let drained = self.queueIsBackground && self.sessions.isEmpty
                    self.refreshTicks += 1
                    let refreshAddress = self.refreshTicks % 5 == 0
                    DispatchQueue.main.async { [weak self] in
                        guard let self, self.runID == currentRun else { return }
                        self.statistics = summary
                        if refreshAddress && !self.isBackground { self.refreshAddresses() }
                        if drained && self.isBackground { self.stop(resumeWhenActive: self.autoResume); self.backgroundStatus = "기존 연결 완료 · 앱을 열면 재개" }
                        else if self.isBackground {
                            let remaining = UIApplication.shared.backgroundTimeRemaining
                            self.backgroundStatus = remaining.isFinite && remaining < 86400 ? "백그라운드 · 남은 시간 약 \(Int(max(0, remaining)))초" : "백그라운드 · iOS 허용 시간 내 연결 마무리"
                        }
                    }
                }
                self.statsTimer = timer; timer.resume()
            }
            listener.service = NWListener.Service(name: "CellRelay", type: "_cellrelay._tcp")
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                guard let self else { return }
                DispatchQueue.main.async {
                    guard self.runID == currentRun else { return }
                    switch state {
                    case .ready:
                        self.listenPort = listener?.port?.rawValue ?? 0
                        if self.listenPort > 0 { self.preferredPort = self.listenPort }
                        self.status = "연결 대기 · 포트 \(self.listenPort)"; self.running = true; UIApplication.shared.isIdleTimerDisabled = !self.isBackground
                        if dynamicPort { self.note("기본 포트 사용 중 · 빈 포트 \(self.listenPort)로 전환. 연결 정보를 다시 복사해 PC에 적용하세요.") }
                    case .failed(let error):
                        self.stop()
                        if !dynamicPort, case .posix(.EADDRINUSE) = error { self.note("이전 중계 포트가 사용 중입니다. 빈 포트로 다시 시작합니다."); self.start(dynamicPort: true) }
                        else { self.status = "중계 시작 실패"; self.note("\(error). 로컬 네트워크 권한과 다른 중계 앱 실행 여부를 확인하세요.") }
                    case .waiting(let error): self.status = "네트워크 대기: \(error.localizedDescription)"
                    default: break
                    }
                }
            }
            listener.newConnectionHandler = { [weak self] client in
                guard let self else { client.cancel(); return }
                guard self.queueRunID == currentRun, !self.queueIsBackground else { client.cancel(); return }
                guard self.sessions.count < 32 else { client.cancel(); return }
                let id = UUID()
                let session = Session(client: client, token: connectionToken, queue: self.queue, note: self.note, activity: { [weak self] count, upload in
                    guard let self else { return }
                    if upload { self.uploaded += UInt64(count) } else { self.downloaded += UInt64(count) }
                }) { [weak self] in self?.sessions.removeValue(forKey: id) }
                self.sessions[id] = session; session.start()
            }
            refreshAddresses(); running = true; status = "시작 중"; listener.start(queue: queue)
        } catch {
            if !dynamicPort, let networkError = error as? NWError, case .posix(.EADDRINUSE) = networkError { note("기본 포트 사용 중 · 빈 포트 시도"); start(dynamicPort: true) }
            else { status = "중계 시작 실패"; note("\(error). 설정에서 로컬 네트워크 접근을 확인하세요.") }
        }
    }
    func stop(resumeWhenActive: Bool = false) {
        resumeOnForeground = resumeWhenActive
        let old = listener; listener = nil; runID = nil; listenPort = 0; old?.cancel()
        queue.sync {
            self.queueRunID = nil; self.statsTimer?.cancel(); self.statsTimer = nil
            let oldSessions = Array(self.sessions.values); oldSessions.forEach { $0.close() }
        }
        endBackgroundTime()
        running = false; status = resumeWhenActive ? "중지됨 · 화면 복귀 대기" : "중지됨"; statistics = "활성 연결 0"; UIApplication.shared.isIdleTimerDisabled = false
    }
    static func localAddresses() -> String {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0 else { return "네트워크 설정에서 확인" }
        defer { freeifaddrs(head) }
        var result: [String] = []; var cursor = head
        while let entry = cursor {
            defer { cursor = entry.pointee.ifa_next }
            guard let addr = entry.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }
            let name = String(cString: entry.pointee.ifa_name)
            guard name != "lo0", !name.hasPrefix("pdp_ip") else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 { result.append("\(name): \(String(cString: host))") }
        }
        return result.isEmpty ? "PC와 로컬 연결 후 다시 시작" : result.joined(separator: ", ")
    }
}

private struct Hello: Decodable { let token: String; let host: String; let port: UInt16; let transport: String? }
private final class Session {
    let client: NWConnection
    let token: String
    let queue: DispatchQueue
    let note: (String) -> Void
    let ended: () -> Void
    let activity: (Int, Bool) -> Void
    var upstream: NWConnection?
    var header = Data()
    var finished = false
    var ready = false
    var eofCount = 0
    var watchdog: DispatchSourceTimer?
    var lastActivity = ProcessInfo.processInfo.systemUptime
    var rejecting = false
    init(client: NWConnection, token: String, queue: DispatchQueue, note: @escaping (String) -> Void, activity: @escaping (Int, Bool) -> Void, ended: @escaping () -> Void) {
        self.client = client; self.token = token; self.queue = queue; self.note = note; self.activity = activity; self.ended = ended
    }
    func start() {
        let watchdog = DispatchSource.makeTimerSource(queue: queue)
        watchdog.schedule(deadline: .now() + 5, repeating: 5)
        watchdog.setEventHandler { [weak self] in
            guard let self, !self.finished else { return }
            if self.rejecting || ProcessInfo.processInfo.systemUptime - self.lastActivity >= (self.ready ? 120 : 20) { self.note("연결 시간 초과 · 정리됨"); self.close() }
        }
        self.watchdog = watchdog; watchdog.resume()
        client.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            if case .ready = state { self.readHello() }
            if case .failed = state { self.close() }
        }
        client.start(queue: queue)
    }
    func readHello() {
        client.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, complete, error in
            guard let self, !self.finished else { return }
            if let data { self.header.append(data) }
            if let newline = self.header.firstIndex(of: 10) {
                guard newline < 8192, let hello = try? JSONDecoder().decode(Hello.self, from: self.header.prefix(upTo: newline)), hello.token == self.token, hello.port > 0, !hello.host.isEmpty, hello.host.utf8.count <= 253, !hello.host.unicodeScalars.contains(where: { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.controlCharacters.contains($0) }) else { self.reject("authentication or destination"); return }
                let pending = Data(self.header.suffix(from: self.header.index(after: newline)))
                self.header.removeAll(); guard hello.transport == nil || hello.transport == "tcp" || hello.transport == "udp" else { self.reject("transport"); return }; self.connect(hello, pending: pending)
            } else if self.header.count >= 8192 { self.reject("header") }
            else if complete || error != nil { self.close() } else { self.readHello() }
        }
    }
    func connect(_ hello: Hello, pending: Data) {
        let parameters = hello.transport == "udp" ? NWParameters.udp : NWParameters.tcp
        parameters.requiredInterfaceType = .cellular
        let remote = NWConnection(host: NWEndpoint.Host(hello.host), port: NWEndpoint.Port(rawValue: hello.port)!, using: parameters)
        upstream = remote
        remote.stateUpdateHandler = { [weak self] state in
            guard let self, !self.finished else { return }
            switch state {
            case .ready:
                guard !self.ready, !self.rejecting else { return }; self.ready = true; self.lastActivity = ProcessInfo.processInfo.systemUptime
                self.note("셀룰러 연결: \(hello.host):\(hello.port)")
                self.client.send(content: Data((hello.transport == "udp" ? "OK UDP1\n" : "OK\n").utf8), completion: .contentProcessed { [weak self] error in
                    guard let self, !self.finished else { return }
                    if error != nil { self.close(); return }
                    if hello.transport == "udp" { self.datagramBuffer = pending; self.readDatagram(remote); self.receiveDatagram(remote); return }
                    if pending.isEmpty { self.pump(self.client, remote) }
                    else { self.activity(pending.count, true); remote.send(content: pending, completion: .contentProcessed { [weak self] error in guard let self, !self.finished else { return }; if error != nil { self.close() } else { self.pump(self.client, remote) } }) }
                    self.pump(remote, self.client)
                })
            case .failed(let error): self.note(error.localizedDescription); if self.ready { self.close() } else { self.reject("cellular connection") }
            default: break
            }
        }
        remote.start(queue: queue)
    }
    var datagramBuffer = Data()
    func readDatagram(_ remote: NWConnection) {
        guard !finished else { return }
        if datagramBuffer.count >= 2 {
            let size = Int(datagramBuffer[datagramBuffer.startIndex]) * 256 + Int(datagramBuffer[datagramBuffer.index(after: datagramBuffer.startIndex)])
            guard size <= 65507 else { reject("datagram size"); return }
            if datagramBuffer.count >= size + 2 {
                let packet = Data(datagramBuffer.dropFirst(2).prefix(size))
                datagramBuffer = Data(datagramBuffer.dropFirst(size + 2))
                remote.send(content: packet, completion: .contentProcessed { [weak self] error in
                    guard let self, !self.finished else { return }
                    if error != nil { self.close(); return }
                    self.lastActivity = ProcessInfo.processInfo.systemUptime; self.activity(packet.count, true); self.readDatagram(remote)
                })
                return
            }
        }
        client.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self, !self.finished else { return }
            if let data { self.datagramBuffer.append(data) }
            if error != nil || complete { self.close(); return }
            self.readDatagram(remote)
        }
    }
    func receiveDatagram(_ remote: NWConnection) {
        guard !finished else { return }
        remote.receiveMessage { [weak self] data, _, _, error in
            guard let self, !self.finished else { return }
            if error != nil { self.close(); return }
            guard let data, data.count <= 65507 else { self.close(); return }
            var frame = Data([UInt8(data.count >> 8), UInt8(data.count & 255)]); frame.append(data)
            self.client.send(content: frame, completion: .contentProcessed { [weak self] error in
                guard let self, !self.finished else { return }
                if error != nil { self.close(); return }
                self.lastActivity = ProcessInfo.processInfo.systemUptime; self.activity(data.count, false); self.receiveDatagram(remote)
            })
        }
    }
    func pump(_ from: NWConnection, _ to: NWConnection) {
        guard !finished else { return }
        from.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self, !self.finished else { return }
            if error != nil { self.close(); return }
            if let data, !data.isEmpty { self.lastActivity = ProcessInfo.processInfo.systemUptime; self.activity(data.count, from === self.client) }
            to.send(content: data, contentContext: complete ? .finalMessage : .defaultMessage, isComplete: complete, completion: .contentProcessed { [weak self] error in
                guard let self, !self.finished else { return }
                if error != nil { self.close() }
                else if let data, !data.isEmpty, !complete { self.lastActivity = ProcessInfo.processInfo.systemUptime; self.pump(from, to) }
                else if complete { self.eofCount += 1; if self.eofCount == 2 { self.close() } }
                else { self.pump(from, to) }
            })
        }
    }
    func reject(_ reason: String) {
        guard !finished, !rejecting else { return }; rejecting = true
        upstream?.stateUpdateHandler = nil; upstream?.cancel()
        client.send(content: Data("ERR \(reason)\n".utf8), completion: .contentProcessed { [weak self] _ in self?.close() })
    }
    func close() {
        guard !finished else { return }; finished = true; watchdog?.cancel(); watchdog = nil
        client.stateUpdateHandler = nil; upstream?.stateUpdateHandler = nil
        client.cancel(); upstream?.cancel(); ended()
    }
}




