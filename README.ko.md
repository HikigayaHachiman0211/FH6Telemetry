# FH6 텔레메트리

[简体中文](README.md) · [English](README.en.md) · [日本語](README.ja.md) · **한국어**

FH6 텔레메트리는 Forza Horizon 6용 Windows x64 독립 실행 앱입니다. 게임의 공식 Data Out을 받아 속도, 기어, RPM, 페달 입력과 차량 상태를 대시보드와 게임 화면의 HUD에 표시합니다. 차량 성능을 측정하거나 주행 기록을 다시 보고, 오프라인 연습에서 자동 변속과 실험용 주행 보조를 사용할 수 있습니다. [Forza Data Out 공식 문서](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

**[FH6Telemetry.exe 다운로드](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest/download/FH6Telemetry.exe)** · **[플레이어 가이드](docs/ko/user-guide.md)** · **[에이전트용 배포 가이드](docs/ko/agent-deployment.md)**

## 빠른 시작

1. `FH6Telemetry.exe`를 다운로드해 실행합니다. .NET 런타임이 포함되어 있습니다.
2. FH6의 **설정 → HUD 및 게임 플레이**에서 Data Out을 켭니다. 같은 PC라면 IP는 `127.0.0.1`, 포트는 `20440`으로 설정하고 앱의 **모듈 설정**에서도 같은 포트를 사용합니다.
3. **수신 시작**을 누르고 게임의 주행 화면으로 돌아갑니다. 속도, 기어, RPM이 바뀌면 HUD, 그래프, 기록을 사용할 수 있습니다.

자동 변속을 쓰려면 게임에서 **수동** 또는 **클러치 수동**을 선택하고 앱의 출력 방식에 맞게 게임 키를 확인합니다. [입력 설정과 정차 점검](docs/ko/user-guide.md#자동-변속)

## 주요 기능

| 기능 | 설명 |
| --- | --- |
| 대시보드와 HUD | 속도, 기어, RPM, 페달, 조향, 출력과 보호 상태를 표시합니다. 위치, 크기, 투명도와 표시 항목을 조절할 수 있습니다. |
| 차량 정보와 성능 측정 | 차량 정보를 보고 가속과 제동을 측정해 튜닝 결과를 비교할 수 있습니다. |
| 주행 기록과 리플레이 | 텔레메트리, 변속 결과, 보조 입력과 출력을 기록하고 원하는 구간을 확인합니다. |
| 자동 변속 | 변속 전략과 키보드 또는 가상 패드 출력을 선택합니다. 게임에서 보고한 기어로 변속을 확인합니다. |
| 실험용 주행 보조 | 오프라인에서 트랙션, 제동, 조향 보조를 시험합니다. 급커브 작동 상한은 직접 설정하거나 차량의 주행 데이터에서 학습하고 HUD에 표시할 수 있습니다. |

앱, HUD와 내보내기는 중국어 간체, 영어, 일본어, 한국어를 지원합니다.

## 빌드와 테스트

저장소 루트에서 Windows x64와 .NET 10 SDK를 사용합니다. 단일 실행 파일을 만들려면 MSVC x64와 Windows SDK도 필요합니다. [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [C++ 도구 설치](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

```powershell
dotnet restore Tests/FH6Telemetry.Tests.csproj
dotnet build FH6Telemetry.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore -- --ui
.\pack-exe.ps1 -NoRestore
```

실행 파일은 `publish/current/FH6Telemetry.exe`에 생성됩니다. 테스트에는 모의 데이터와 로컬 UDP를 사용합니다. [배포, 문제 해결 및 게임 검증](docs/ko/agent-deployment.md)

## 라이선스

소스는 [GPL-3.0](LICENSE)을 따릅니다. 변속 전략의 출처와 라이선스는 [타사 고지](ThirdPartyNotices.txt)를 참고하세요. FH6 텔레메트리는 독립 커뮤니티 프로젝트입니다.
