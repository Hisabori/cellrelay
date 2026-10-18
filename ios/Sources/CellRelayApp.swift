import SwiftUI
import Network
import UIKit
import Darwin

@main struct CellRelayApp: App {
    @StateObject private var relay = Relay()
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            NavigationStack {
                Form {
                    Section("연결") {
                        Text(relay.status)
                        LabeledContent("포트", value: "49152")
                        LabeledContent("로컬 주소", value: relay.addresses)
                        ForEach(relay.localHosts, id: \.self) { host in
                            Button("\(host) 연결 정보 복사") { relay.copyProfile(host: host) }
                        }
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
                        Text("HTTP/HTTPS TCP 프록시 테스트용. 공유 데이터 미차감은 검증되지 않았습니다.")
                        Text("신뢰하는 USB/로컬 네트워크에서만 사용하세요. 연결 키와 PC-아이폰 구간은 암호화되지 않습니다. HTTPS 내용은 원래의 TLS로 보호됩니다.")
                    }
                    Section("기록") { Text(relay.events).font(.caption.monospaced()).textSelection(.enabled) }
                }.navigationTitle("CellRelay 테스트")
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

final class Relay: ObservableObject {
    @Published var status = "중지됨"
    @Published var running = false
    @Published var events = ""
    @Published var addresses = "시작 시 확인"
    @Published var token = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    @Published var statistics = "연결 0 · 업로드 0 B · 다운로드 0 B"
    @Published var backgroundStatus = "화면 활성 상태"
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
    private var statsTimer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "cellrelay.network")
    private var listener: NWListener?
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
    func copyProfile(host: String) {
        guard IPv4Address(host) != nil,
              let data = try? JSONSerialization.data(withJSONObject: ["host": host, "token": token, "port": 49152], options: [.sortedKeys]) else { return }
        UIPasteboard.general.string = "CR1:" + data.base64EncodedString()
        note("연결 정보를 복사했습니다. Windows에 직접 전달하세요. 인증 키가 포함되어 있습니다.")
    }
    func rotateKey() { guard !running else { return }; token = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }
    func note(_ message: String) { DispatchQueue.main.async { self.events = String((self.events + "\n" + message).suffix(6000)) } }
    func start() {
        guard !running, !isBackground else { return }
        resumeOnForeground = false
        do {
            let currentRun = UUID(); let connectionToken = token
            let listener = try NWListener(using: .tcp, on: 49152)
            self.listener = listener; self.runID = currentRun
            queue.async {
                self.queueRunID = currentRun; self.queueIsBackground = false; self.uploaded = 0; self.downloaded = 0
                self.statsTimer?.cancel()
                let timer = DispatchSource.makeTimerSource(queue: self.queue)
                timer.schedule(deadline: .now(), repeating: 1)
                timer.setEventHandler { [weak self] in
                    guard let self, self.queueRunID == currentRun else { return }
                    let summary = "연결 \(self.sessions.count)/32 · 업로드 \(self.uploaded) B · 다운로드 \(self.downloaded) B"
                    let drained = self.queueIsBackground && self.sessions.isEmpty
                    DispatchQueue.main.async { [weak self] in
                        guard let self, self.runID == currentRun else { return }
                        self.statistics = summary
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
            listener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                DispatchQueue.main.async {
                    guard self.runID == currentRun else { return }
                    switch state {
                    case .ready: self.status = "연결 대기 · 셀룰러 출구"; self.running = true; UIApplication.shared.isIdleTimerDisabled = !self.isBackground
                    case .failed(let error): self.note(error.localizedDescription); self.stop()
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
            addresses = Self.localAddresses(); running = true; status = "시작 중"; listener.start(queue: queue)
        } catch { note(error.localizedDescription) }
    }
    func stop(resumeWhenActive: Bool = false) {
        resumeOnForeground = resumeWhenActive
        let old = listener; listener = nil; runID = nil; old?.cancel()
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

private struct Hello: Decodable { let token: String; let host: String; let port: UInt16 }
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
                self.header.removeAll(); self.connect(hello, pending: pending)
            } else if self.header.count >= 8192 { self.reject("header") }
            else if complete || error != nil { self.close() } else { self.readHello() }
        }
    }
    func connect(_ hello: Hello, pending: Data) {
        let parameters = NWParameters.tcp
        parameters.requiredInterfaceType = .cellular
        let remote = NWConnection(host: NWEndpoint.Host(hello.host), port: NWEndpoint.Port(rawValue: hello.port)!, using: parameters)
        upstream = remote
        remote.stateUpdateHandler = { [weak self] state in
            guard let self, !self.finished else { return }
            switch state {
            case .ready:
                guard !self.ready, !self.rejecting else { return }; self.ready = true; self.lastActivity = ProcessInfo.processInfo.systemUptime
                self.note("셀룰러 연결: \(hello.host):\(hello.port)")
                self.client.send(content: Data("OK\n".utf8), completion: .contentProcessed { [weak self] error in
                    guard let self, !self.finished else { return }
                    if error != nil { self.close(); return }
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
