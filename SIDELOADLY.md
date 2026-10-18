# Sideloadly 설치 대상

빌드 결과 파일명: `CellRelay-unsigned.ipa`.
이 파일은 미서명 iOS 기기용 패키지이며 Sideloadly에서 본인의 Apple ID로 재서명해야 합니다.
Apple ID와 비밀번호는 채팅으로 보내지 말고 Sideloadly 프로그램에 직접 입력하세요.

## IPA 생성

이 소스의 `.github/workflows/ios-ipa.yml`은 GitHub의 macOS 러너에서 앱을 빌드합니다.
저장소에 소스를 올리면 iOS 소스 변경 push 또는 Actions의 수동 실행으로 작동합니다.
성공한 실행의 `CellRelay-Sideloadly-unsigned` 아티팩트에는 IPA와 검사 결과 JSON이 포함됩니다.
IPA 무결성, arm64 기기 실행 파일, 앱 식별자, 로컬 네트워크 권한 문구를 검사합니다.
워크플로 준비만으로 iOS 빌드 성공이나 IPA 생성이 증명되는 것은 아닙니다.

Mac에서 직접 빌드하려면 `ios/Build-iOS.sh`를 실행합니다.

## 설치

1. 공식 사이트 https://sideloadly.io/ 에서 Windows용 Sideloadly를 설치합니다.
2. 아이폰을 USB로 연결하고 잠금 해제 후 '이 컴퓨터를 신뢰'를 승인합니다.
3. Sideloadly에서 아이폰을 선택하고 `CellRelay-unsigned.ipa`를 끌어 넣습니다.
4. 본인의 Apple ID를 입력하고 Apple ID Sideload 방식으로 시작합니다. 로그인·2단계 인증은 프로그램에서 직접 처리합니다.
5. 아이폰에서 요청되는 개발자 신뢰 및 개발자 모드 설정을 완료하고 앱을 실행합니다. 서명 기간과 갱신 조건은 사용 계정에 따라 다릅니다.
6. 앱의 로컬 네트워크 접근을 허용하고 화면에 켜 둔 상태로 PC 프로그램과 연결합니다. 세부 연결 방법은 README.md를 참고하세요.

설치 성공은 셀룰러 중계나 통신사 공유 데이터 미차감의 증거가 아닙니다. 두 항목을 실제 환경에서 별도 시험해야 합니다.

무선 설치는 별도 설정입니다. USB 신뢰/페어링과 Apple Devices의 Wi-Fi 연결 설정을 적용한 뒤, 케이블을 분리해도 Sideloadly에 기기가 표시되는지 확인해야 합니다.
