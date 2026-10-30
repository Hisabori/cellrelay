# 0.5.0 — 2026-10-18

아이폰: CR1 코드 직접 보기·전체 복사·공유, 주소 수동 입력/새로고침/주기적 갱신, 앱 복귀 시 주소 갱신, 같은 실행 중 이전 포트 우선 사용.
PC: 연결 설정/기록/안내 탭, 연결 코드와 직접 입력 구분, 코드·키 표시 옵션, 소문자 cr1 접두어 허용, 키 단독 입력에 대한 구체적 오류, 아이폰 도달 실패 단계 안내.
Windows CI 회귀 시험을 추가했습니다. 실제 기기의 연결·클립보드 전달·백그라운드 및 데이터 집계는 별도 확인이 필요합니다.

# 0.4.2 — 2026-10-18

아이폰 중계 시작 시 기본 포트 EADDRINUSE 오류이면 동적 포트로 한 번 재시도합니다. 실제 포트를 화면/연결 코드에 표시합니다. 다른 오류는 원문과 로컬 네트워크 권한 안내를 남깁니다. PC는 가변 아이폰 포트 가져오기·직접 입력·저장을 지원하며 구 설정 파일은 49152 기본값으로 읽습니다. 실제 아이폰에서 보고된 오류의 정확한 원인과 수정 후 동작은 별도 기기 검증 대상입니다.

# PC 0.4.1 — 2026-10-18

PC 프록시의 포트 사용 중/접근 거부 오류 시 자동으로 다른 포트를 바인딩합니다. 실제 선택 포트를 브라우저 설정 안내·복사·설정 저장에 반영합니다. 새 PC UI 중복 실행을 방지합니다. 기존 프로세스 종료는 하지 않습니다. 모의 점유 포트를 유지한 채 대체 프록시가 동작하는 회귀 시험을 추가했습니다. iOS는 0.4 IPA를 그대로 사용합니다.

# 0.4.0 — 2026-10-18

아이폰: 화면 전환 시 기존 연결을 유한한 백그라운드 실행 시간 내에서 마무리, 만료 정리, 앱 복귀 시 선택적 자동 재개. 백그라운드 새 연결과 무기한 수신은 지원하지 않습니다. macOS CI 컴파일과 IPA 구조 검사는 통과했으며 기기 런타임은 미검증입니다.

Windows: 연결 순서 배너, 마지막 아이폰 검사 결과·시각 표시, 연결 기록/사용 안내 탭. 기존 중계 회귀 시험 통과. 화면 조작 검증은 별도입니다.

# 0.3.0 — 2026-10-18

아이폰 연결 코드 복사와 Windows 가져오기, PC 프록시 포트 선택·주소 복사, 주소/포트 설정 기억, 대략적인 전송 속도, 연결 검사 소요 시간, 진단 기록 저장·인증 키 가림·기록 지우기.

설정 파일에 인증 키를 저장하지 않습니다. 연결 코드는 암호화하지 않은 인증 정보이므로 직접 전달해야 합니다.
새 기능 검증은 Windows 자체 시험에 포함합니다. 아이폰 변경은 소스만 준비되었으며 macOS 빌드와 실제 기기 시험은 남아 있습니다.

# 0.2.0 — 2026-10-18

Windows: 연결 검사, 활동·전송량 표시, 입력 및 HTTP 프레이밍 검증, 유휴 연결 회수, 중지·재시작 정리, 크기가 제한된 로그.

iOS 소스: 키 복사/재발급, 활동·전송량 표시, 리스너 재시작 경합 방지, 연결·유휴 감시, 열린 터널에 오류 메시지가 섞이는 문제 수정.

Windows 회귀 시험은 VALIDATION.txt와 self-test-result.txt를 참고하세요.
iOS 컴파일·IPA 생성·실제 기기 동작·공유 데이터 미차감은 별도 검증 대상입니다.

## 0.6.0
- PC connection diagnostics now verify relay acceptance, TLS certificate validation, and an actual HTTPS HEAD response from example.com, with a 25-second total deadline and failure-stage display.
- Untrusted relay rejection text is never copied into diagnostic logs.
- iPhone displays cellular path availability; this is a path indication, not proof of Internet access or carrier accounting.
- Regression coverage includes rejected relay replies, premature TLS closure, credential-safe errors, and cancellation.

## 0.7.0
- Quick connection applies a CR1 profile, checks actual HTTPS through the iPhone, then starts the PC TCP proxy.
- iPhone shows a QR code containing the current CR1 profile. PC imports a QR image file or clipboard image; no camera scanning is included.
- Adds loopback UDP forwarding to a fixed remote host/port through authenticated, length-framed TCP to the iPhone cellular UDP socket. Supports up to 16 PC source endpoints, bounded queues and idle cleanup.
- QUIC UDP payloads can use this forwarding path when the client can direct traffic to the local port while retaining the original server identity. This is not a native QUIC transport, SOCKS UDP proxy, or automatic browser HTTP/3 integration. TCP tunneling can increase latency.
- UDP and QR need the matching 0.7 iPhone build. Existing TCP clients remain compatible.

## 0.8.0 (PC)
- Experimental whole-PC game mode using an external sing-box TUN engine and local SOCKS5 TCP/UDP relay. Requires administrator rights and manual official engine preparation; see GAME-MODE.txt.
- Dynamic IPv4/IPv6/domain destinations, TCP half-close, loopback-pinned UDP associations, bounded flows/queues, idle cleanup and UDP fragment rejection.
- Validates the iPhone HTTPS path and engine configuration before starting. Phone and loopback routes are excluded to avoid recursive tunneling; DNS uses the cellular SOCKS path.
- iPhone 0.7 remains compatible. Real TUN activation/removal, device cellular routing, GeForce NOW gameplay and carrier accounting remain unverified. UDP travels over TCP between PC and iPhone and may add latency.

## 0.8.1 (PC)
- Opens the game-mode tab by default. The quick-connect button now starts the game-mode flow, including its existing HTTPS preflight and engine configuration check.
- Adds a direct QR/CR1 registration button on the default screen and corrects manual engine/administrator instructions.
- No tunnel starts merely by opening the app; the user starts it with the connection button. Existing TCP proxy and fixed UDP forwarding remain available.

## 0.8.2 (PC)
- Displays connection profile status, required engine files and Administrator status on the default game screen, with a refresh button.
- Adds three visible startup stages and a cancel button while the HTTPS probe, engine configuration check or engine launch is pending.
- Limits engine configuration checks to 10 seconds, drains bounded output and terminates the owned check process on timeout/cancellation.
- Prevents the exit monitor from disposing an engine while the startup code still checks its result.
- Adds real subprocess regression checks for failed exit codes, timeout cleanup and user cancellation. TUN, gameplay and carrier verification still require a device.

## 0.9.0 (PC)
- Adds an explicit VPN/TCP/SOCKS5/UDP connection-mode selector to quick connect.
- Exposes the existing dynamic SOCKS5 TCP CONNECT and UDP ASSOCIATE relay as a standalone loopback listener, without Administrator or a TUN engine. Supports destination applications that implement SOCKS5 UDP forwarding.
- Adds a global listener/startup status bar and a stop-all button that stops HTTP, fixed UDP, SOCKS5 and the VPN tunnel, or cancels pending tunnel startup.
- VPN means the existing sing-box TUN integration forwarding whole-PC TCP/UDP/DNS through the iPhone. External OpenVPN/WireGuard profiles, ICMP, unsolicited inbound connections and an encrypted PC-iPhone VPN transport are not implemented.
- iPhone 0.7 remains compatible. Real device routing, VPN restoration, gameplay and carrier accounting remain unverified.

## 0.9.1 (PC stabilization)
- Cancels the opposite SOCKS5 TCP pump when one direction fails, while preserving normal TCP half-close behavior.
- Serializes listener start/disposal, rejects duplicate or post-dispose starts, and makes repeated SOCKS5/UDP disposal safe.
- Retains ownership of the VPN engine and SOCKS listener when engine termination fails, permitting retry rather than reporting a false stop.
- Window close cancels and awaits pending VPN startup before shutting down owned services. Failed cleanup keeps the window available. Final close is queued to avoid reentrant FormClosing.
- Adds regression coverage for parallel disposal, pending handshakes, UDP port release, upstream TCP reset, repeated owned-process termination and a hidden WinForms close smoke check.
- Physical iPhone/VPN restoration/gameplay/carrier accounting remain unverified.

## 0.10.0 (PC)
- Adds an actual UDP DNS probe through the iPhone relay to 1.1.1.1:53 (example.com A query), with an 8-second deadline, explicit stages and cancellation.
- Validates DNS response ID, question, flags, compressed names, CNAME chain and IPv4 answer records. Rejects malformed, truncated and pointer-loop responses; never echoes raw relay errors into logs.
- VPN startup now checks HTTPS, optional UDP/DNS, engine configuration and engine startup in four stages. UDP/DNS checking is enabled by default and may be disabled when the configured test server is blocked.
- Standalone SOCKS5 screen adds UDP/DNS test and cancel controls; stop-all also cancels this probe. A successful test proves only the sample relay path at that time, not every UDP application, VPN interface routing or carrier accounting.
- iPhone 0.7 remains compatible. No iPhone code changes.

## 0.11.0 (PC) / 0.8.0 (iPhone)
- PC close-to-tray background mode is enabled by default, with a persistent preference and an explicit background button. Tray menu supports restore, stop-all and full exit. Full exit keeps the owned-engine cleanup sequence.
- Stop-all uses direct stop actions even when their tabs or the main window are hidden.
- Adds hidden WinForms smoke tests for close-to-tray, restore and explicit exit, plus tray-preference migration/persistence checks. Tests do not overwrite the user's settings.
- iPhone persists the foreground-auto-resume preference and uses accurate manual/automatic restart hints after background completion/expiration. The existing iOS background time limit remains unchanged: existing connections only, no indefinite background listener, no automatic restart after force-quit or OS termination.
- PC sleep/shutdown and physical iPhone/VPN/gameplay/carrier behavior remain outside the verified boundary.
