# FH6 Telemetry Agent 指南

本仓库根目录就是独立的 FH6 遥测助手工程。首次接手、打包、配置手柄或排查真实游戏时，从 [完整部署指南](docs/fh6-telemetry-agent-setup.md) 开始。玩家入口见 [运行指南](docs/user-guide.md)。

部署指南 / Deployment guide · [简体中文](docs/fh6-telemetry-agent-setup.md) · [English](docs/en/agent-deployment.md) · [日本語](docs/ja/agent-deployment.md) · [한국어](docs/ko/agent-deployment.md)

- `Core/` 处理 Data Out、换挡、驾驶辅助、记录与回放。`Input/` 处理 Windows 输入和虚拟手柄转发。
- `ViewModels/`、`Views/` 和 `Core/TelemetryStrings.*.cs` 负责界面、HUD 和四种语言。
- `Tests/` 包含核心与无界面测试。`Packaging/Launcher.cpp` 和 `pack-exe.ps1` 负责 Windows x64 单文件包。
- 在仓库根目录运行 `dotnet run --project Tests/FH6Telemetry.Tests.csproj`，再以 `-- --ui` 运行独立窗口测试。
- 单手柄转发使用打包后的 EXE 验收。报告中分别记录核心测试、UI 测试、实时收包和游戏内档位确认。
- 版本号取自 `FH6Telemetry.csproj`。将 EXE 放到同一源码提交对应的发布标签下，保留 `LICENSE` 和 `ThirdPartyNotices.txt`。
