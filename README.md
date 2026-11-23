# FH6 遥测助手

FH6 Telemetry 是面向《极限竞速：地平线 6》的 Windows x64 独立辅助工具。它接收游戏的官方 Data Out 遥测，在桌面和游戏 HUD 中显示车速、挡位、转速、踏板与车身状态。你可以用它实测车辆、回看行驶记录，或在离线练习中试用自动换挡和驾驶辅助。 [Forza Data Out 说明](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

**[下载最新版 FH6Telemetry.exe](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest/download/FH6Telemetry.exe)** · **[玩家运行指南](docs/user-guide.md)** · **[Agent 部署指南](docs/fh6-telemetry-agent-setup.md)**

## 使用

1. 下载并双击 `FH6Telemetry.exe`。发布包已包含 .NET 运行时。
2. 在 FH6 的「设置 → HUD 和游戏性」开启 Data Out。游戏和助手在同一台电脑时，填写 IP `127.0.0.1`、端口 `20440`。改用其他端口时，在助手「模块设置」中填同一个值。
3. 在助手点「开始接收」，回到游戏驾驶画面。车速、挡位和转速开始变化后，就可以使用记录、曲线和 HUD。

只看遥测、记录或 HUD，到这一步就可以。想用自动换挡，在游戏里选择「手动」或「手动（带离合器）」，再按所选输出方式核对键位。首次使用可以展开「自动换挡」页里的「新手使用引导」。 [输入模式和静止自检](docs/user-guide.md#自动换挡)

## 功能

| 功能 | 能做什么 |
| --- | --- |
| 实时仪表与 HUD | 查看速度、挡位、转速、踏板、转向、功率和防护状态。可调位置、大小、透明度和显示项目。 |
| 性能实测与车型档案 | 查看原厂资料，测加速、制动和其他性能项目，保存成绩并比较不同调校。 |
| 记录和回放 | 记录 UDP 样本、换挡结果和驾驶辅助输入输出，回看特定片段或导出诊断信息。 |
| 自动换挡 | 选择换挡策略与键盘或虚拟手柄输出。按实际挡位回报确认换挡。 |
| 实验性驾驶辅助 | 在离线练习中试用限滑、制动、反打等辅助。急弯转向保留支持手动速度上限、按车辆表现学习和可关闭的 HUD 提示。 |

界面、HUD 和导出支持简体中文、英语、日语和韩语。车辆档案按 Data Out 的车型编号查询，未收录的车型仍可使用实时遥测。 [遥测字段与实现说明](docs/performance-telemetry.md)

## 构建

在仓库根目录使用 Windows x64 和 .NET 10 SDK。生成单文件启动器还需要 Visual Studio 的「使用 C++ 的桌面开发」工具和 Windows SDK。 [下载 .NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [安装 C++ 工具](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

```powershell
dotnet restore FH6Telemetry.csproj
dotnet build FH6Telemetry.csproj --no-restore
.\pack-exe.ps1 -NoRestore
```

成品位于 `publish/current/FH6Telemetry.exe`。驱动配置、部署、验收、故障定位和发版步骤见 [Agent 部署指南](docs/fh6-telemetry-agent-setup.md)。

## 独立测试

```powershell
dotnet run --project Tests/FH6Telemetry.Tests.csproj
dotnet run --project Tests/FH6Telemetry.Tests.csproj -- --ui
```

测试使用模拟报文和本机 UDP 回环，可在游戏关闭时运行。 [真实设备验收](docs/fh6-telemetry-agent-setup.md#分层验收)

## 许可

源代码沿用 [GPL-3.0](LICENSE)。换挡策略的来源与第三方许可保留在 [ThirdPartyNotices.txt](ThirdPartyNotices.txt)。FH6 遥测助手是独立社区项目。
