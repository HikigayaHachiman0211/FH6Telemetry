# 에이전트용 배포 가이드

[简体中文](../fh6-telemetry-agent-setup.md) · [English](../en/agent-deployment.md) · [日本語](../ja/agent-deployment.md) · **한국어**

FH6를 실행하는 Windows x64 PC에서 설치, 소스 빌드, 입력 구성, 검증, 문제 해결, 업그레이드와 릴리스 절차를 다룹니다. 명령은 독립 저장소의 루트 기준입니다. [플레이어 가이드](user-guide.md)

## 배포 범위 선택

| 목적 | 준비물 | 확인할 결과 |
| --- | --- | --- |
| 실시간 텔레메트리, HUD, 성능 측정, 리플레이 | 릴리스 EXE와 FH6 Data Out | 속도, 기어 또는 페달 값이 게임에 맞춰 변함 |
| 키보드 자동 변속 | 정상 텔레메트리와 게임 키 설정 | 정차 점검에서 1단에서 2단으로 변함 |
| 패드 릴레이 또는 능동 주행 보조 | 릴리스 EXE, XInput 패드, ViGEmBus, HidHide | 게임에 원하는 가상 패드가 보이고 실제 입력과 자동 입력이 반영됨 |
| 소스 빌드와 배포 | Git, .NET 10 SDK, 단일 파일용 MSVC x64 및 Windows SDK | 두 테스트가 통과하고 패키지에서 해당 버전을 확인 |

목표 버전, 출력 방식, 활성 게임 프리셋, UDP 포트와 설정 경로를 먼저 기록합니다. 입력 전송 성공과 게임 텔레메트리에서의 기어 확인을 따로 기록합니다.

## 릴리스 다운로드 또는 소스 빌드

[GitHub 릴리스](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest)에서 `FH6Telemetry.exe`를 쓰기 가능한 폴더에 내려받습니다. `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FH6Telemetry.exe'`로 릴리스에 표시된 해시와 비교합니다. 실행 후 **정보**에서 버전을 확인합니다. .NET 런타임은 실행 파일에 포함되어 있습니다.

소스 빌드는 `dotnet --list-sdks`로 .NET 10 SDK를 확인합니다. 단일 파일을 만들려면 Visual Studio Build Tools의 **C++를 사용한 데스크톱 개발** 워크로드에서 MSVC x64와 Windows SDK를 설치합니다. 패키징 스크립트는 `vswhere.exe`로 도구를 찾습니다. 이전 릴리스를 재현하려면 해당 태그를 체크아웃합니다. [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [C++ 도구](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

```powershell
git clone https://github.com/HikigayaHachiman0211/FH6Telemetry.git
Set-Location -LiteralPath '.\FH6Telemetry'
dotnet restore Tests/FH6Telemetry.Tests.csproj
dotnet build FH6Telemetry.csproj --no-restore -v:q
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore -- --ui
.\pack-exe.ps1 -NoRestore
Get-FileHash -LiteralPath 'publish/current/FH6Telemetry.exe' -Algorithm SHA256
```

테스트는 모의 입력과 UDP 루프백을 사용합니다. 로컬 UDP가 허용되는 환경에서 실행하고, UI 캡처는 테스트 출력 폴더의 `ui-preview`에서 확인합니다. `dotnet run --project FH6Telemetry.csproj`로 창과 수신을 확인할 수 있습니다. 패드 릴레이 검증에는 실제 실행 경로와 장치 탐지를 제공하는 패키지 실행 파일을 사용합니다.

## 설정 경로와 업그레이드

| 경로 | 용도 |
| --- | --- |
| `%APPDATA%\FH6Telemetry\telemetry-settings.json` | 기본 설정 |
| 실행 파일 옆 `Data\telemetry-settings.json` | 기본 파일이 없을 때 사용할 수 있는 휴대용 설정. 쓰기 문제가 있으면 다른 위치를 시도합니다. |
| 텔레메트리 폴더 안의 `drives\` | 주행 기록. 기록 화면에서 현재 위치를 열 수 있습니다. |
| 설정 폴더의 `speed-gear-profiles.json` | 학습한 기어와 속도 자료 |
| 실행 파일 옆 `Data\fh6-hidhide.json` | 선택한 실제 패드의 PC별 페어링 자료 |
| `%LOCALAPPDATA%\FH6Telemetry\Runtime` | 버전별 실행 캐시. 실제 경로는 **가상 패드 설정**에서 확인합니다. |

기존 앱을 종료하고 사용 중인 설정과 기록을 백업합니다. 호환성 확인이 필요하면 별도 폴더에서 새 EXE를 시험합니다. 같은 PC에서 장치를 확인한 페어링 JSON은 새 실행 파일 옆으로 옮길 수 있습니다. `.lease`는 앱이 관리합니다. 문제가 생기면 이전 EXE로 돌아갈 수 있게 복사본을 보관합니다. 이전 통합 도구의 텔레메트리를 처음 가져오는 경우 `--import-trainer-telemetry`로 실행합니다. 기존 독립 설정이 우선합니다.

## Data Out 검증

FH6의 **설정 → HUD 및 게임 플레이**에서 Data Out을 켭니다. 같은 PC에서는 게임과 앱을 `127.0.0.1:20440`에 맞춥니다. **수신 시작** 후 자유 주행에서 속도, 기어, 브레이크를 확인합니다. **진단**에서 수신 수, 파싱 실패 수, 송신 위치를 볼 수 있습니다. 현재 FH6 파서는 324바이트 패킷을 받습니다. 일시 정지나 메뉴에서는 수신이 멈출 수 있습니다. [Forza 공식 자료](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

```powershell
Get-NetUDPEndpoint -LocalPort 20440 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
netstat.exe -ano -p UDP | Select-String '127\.0\.0\.1:20440\s+'
```

충돌 시 포트를 쓰는 프로세스를 찾아 정상적으로 수신을 멈추거나 게임과 앱의 포트를 함께 바꿉니다.

## 단일 패드 릴레이

1. XInput 실제 패드를 확인합니다. 릴레이를 쓸 경우 x64 ViGEmBus와 HidHide를 설치하고 설치 프로그램의 재시작 안내를 따릅니다. [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest) · [HidHide](https://github.com/nefarius/HidHide/releases/latest)
2. 패키지의 **가상 패드 설정**에 표시된 실행 파일을 HidHide **Applications**에서 허용합니다. 패드를 다시 연결해 **Devices**에서 대상을 확인하고 일반 숨김 모드로 선택합니다. 유선 Xbox 패드는 USB와 HID 항목이 모두 있을 수 있습니다. [HidHide 설정](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/)
3. **단일 패드 릴레이 · 내장 클러치 및 블립**을 연결하고 앱의 실제 입력과 게임의 가상 패드 인식을 확인합니다. 게임이 먼저 시작돼 가상 패드를 못 잡으면 릴레이를 유지한 채 필요한 경우 FH6를 정상 재시작합니다. 중복 입력이 있으면 다른 매퍼와 Steam Input을 하나씩 확인합니다.

휴대용 `fh6-hidhide.json`은 대상 `HID\` 또는 `USB\` 장치 인스턴스 문자열의 배열입니다. 같은 PC의 HidHide 숨김 대상과 대조합니다. 앱은 중계 중에 실행 파일을 임시 허용하고 정상 연결 해제 시 이전 표시 상태를 복구합니다. 비정상 종료 후에는 페어링된 실행 파일을 정상 실행해 복구할 기회를 줍니다. HidHide 구성 클라이언트를 닫은 뒤 다음 명령으로 로컬 상태를 확인합니다. [HidHide CLI](https://docs.nefarius.at/projects/HidHide/CLI-Usage/)

```powershell
$hidHideRoot = Join-Path $env:ProgramFiles 'Nefarius Software Solutions/HidHide'
$hidHideCli = Join-Path $hidHideRoot 'HidHideCLI.exe'
if (-not (Test-Path -LiteralPath $hidHideCli)) {
    $hidHideCli = Join-Path $hidHideRoot 'x64/HidHideCLI.exe'
}
& $hidHideCli --cloak-state --inv-state --dev-list --app-list
```

사용자의 Windows 데스크톱 세션에서 다음 검사로 XInput 표시 개수를 확인합니다. 릴레이 연결 후에는 의도한 가상 패드 하나가 보여야 합니다. 실제 버튼을 눌러 전달 경로를 확인합니다.

```powershell
$exe = (Resolve-Path -LiteralPath 'publish/current/FH6Telemetry.exe').Path
$probeInfo = [Diagnostics.ProcessStartInfo]::new($exe, '--probe-visible-gamepads')
$probeInfo.UseShellExecute = $false
$probeInfo.CreateNoWindow = $true
$probe = [Diagnostics.Process]::Start($probeInfo)
if (-not $probe.WaitForExit(5000) -or ($probe.ExitCode -band 0xFFFFFFF0) -ne 0x100) {
    throw 'XInput probe failed'
}
$mask = $probe.ExitCode -band 0xF
@(0..3 | Where-Object { $mask -band (1 -shl $_) }).Count
```

## 키보드 출력과 단계별 검증

키보드 기본값은 `E`, `Q`, 왼쪽 `Shift`, `W`입니다. 가상 패드는 `B`, `X`, `L3`, `RT`를 사용하고 `LT`로 제동하며 왼쪽 스틱으로 조향합니다. 게임에서 현재 활성 프리셋과 주행 보조의 핸드브레이크 버튼을 맞춥니다. 자동 클러치 확인에는 **클러치 수동** 모드를 사용합니다. [패드 문제 해결](https://support.forza.net/hc/en-us/articles/4408251153427-Controller-Troubleshooting)

1. 텔레메트리가 변하는지 확인합니다. 정차 중에는 브레이크 값으로 확인합니다.
2. 선택한 장치로 수동 변속을 시험하고, 자동 변속과 주행 보조를 끈 상태로 1단에 정차합니다.
3. **가상 패드 설정 → 정차 업시프트 점검**을 시작합니다. 15초 안에 게임으로 돌아가 브레이크만 유지합니다. 한 번의 입력으로 게임과 텔레메트리에서 모두 2단이 되는지 확인합니다.
4. 1단으로 돌아가 오프라인 직선에서 자동 변속을 한 번 확인합니다. 자동 변속을 끈 뒤 입력 해제를 확인합니다. 패드 전달을 멈출 때는 릴레이를 따로 끊습니다.
5. 오프라인 연습에서 주행 보조를 활성화하고 급커브 실험과 자동 학습을 켠 뒤 안정적으로 코너를 돕니다. 학습값과 HUD 알림 표시·숨김·저장을 확인합니다. 기본 `Pause`는 보조 출력을 중지합니다.
6. 리플레이 비교와 실제 게임에서의 확인을 별도로 기록합니다.

출력 연결을 기다리는 경우 모드, 드라이버와 페어링을 확인합니다. 입력을 전송했지만 변속이 되지 않으면 같은 키를 수동으로 시험하고 클러치 모드, 포커스와 중복 입력원을 확인합니다. 자동 상한이 아직 수동값이면 현재 차량의 안정적인 코너 샘플이 충분한지 확인합니다.

## 기록, 인계 및 릴리스

**진단**에서 재현 구간을 짧게 기록합니다. `session.json`은 세션 개요, `telemetry.jsonl.gz`는 원본 패킷과 분석 결과, `shift-actions.jsonl`은 출력과 보조 이벤트를 저장합니다. `elapsedMs`와 `telemetryTimestampMs`로 맞춰 봅니다. `assist_frame`의 `physicalReport`는 실제 입력, `report`는 전달 출력, `appliedAssist`는 개입한 기능입니다. 장치 ID, 로컬 경로와 전체 기록은 필요한 범위에 맞춰 다룹니다.

`FH6Telemetry.csproj`의 `Version`과 `GitHubRepository`를 확인하고 출시 대상 커밋으로 빌드, 테스트, 패키징을 수행합니다. `v<Version>`의 Release에 `FH6Telemetry.exe`를 첨부하고 변경점, 검증 범위와 SHA-256을 적습니다. 게시 후 공개 다운로드를 확인합니다. **정보**의 업데이트 확인은 최신 정식 릴리스를 사용합니다. [GitHub 릴리스 문서](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

인계에는 커밋, 태그, 다운로드 링크, SHA-256, 테스트 결과와 게임 환경에서 실제 확인한 범위를 기록합니다.
