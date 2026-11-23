# FH6 遥测助手 Agent 部署指南

**简体中文** · [English](en/agent-deployment.md) · [日本語](ja/agent-deployment.md) · [한국어](ko/agent-deployment.md)

适用环境是运行 FH6 的 Windows x64 桌面。本指南从取得运行包开始，覆盖源码构建、配置、输入链路验证、采样排错、升级和发布。以下命令默认在独立仓库根目录运行。 [给玩家的运行指南](user-guide.md)

## 选择交付范围

| 目标 | 需要什么 | 完成证据 |
| --- | --- | --- |
| 实时遥测、HUD、性能测试和回放 | 发布版 EXE 与 FH6 Data Out | 车速、挡位或踏板随游戏更新 |
| 键盘自动换挡 | 可用遥测与当前游戏键盘绑定 | 静止自检在遥测中确认一挡升二挡 |
| 单手柄转发、主动驾驶辅助 | 发布版、XInput 实体手柄、ViGEmBus 和 HidHide | 启用后只向游戏提供预期的虚拟手柄，游戏确认实体转发与自动输出 |
| 开发或发布源码 | Git、.NET 10 SDK，单文件包另需 MSVC x64 和 Windows SDK | 核心、无界面测试通过，打包版启动并报告对应版本 |

开始前记下用户要使用的版本、输出模式、活跃的控制预设、监听端口和配置路径。用验收表逐层判定，输入提交成功与游戏确认新挡是两个独立结果。

## 获取发布版或从源码构建

### 使用发布版

1. 从 Releases 下载单个 `FH6Telemetry.exe`，放到可写入的目录。需要固定版本时，从该标签的 Release 下载。
2. 用 `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FH6Telemetry.exe'` 核对发布页所列哈希。
3. 双击启动器。首次启动会准备运行缓存，「关于」页可查看实际版本。可执行文件包含 .NET 运行时。 [发布页](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases)

### 从源码构建

1. 安装 Git 和 .NET 10 SDK，用 `dotnet --list-sdks` 确认可用。单文件启动器需要 Visual Studio Build Tools 的「使用 C++ 的桌面开发」工作负载，包含 MSVC x64 和 Windows SDK。脚本通过 `vswhere.exe` 查找工具，通常可从普通 PowerShell 运行。 [下载 .NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [安装 MSVC 与 Windows SDK](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)
2. 克隆仓库。复现已发布版本时检出对应标签。更新分支前先核对工作区状态。

```powershell
git clone https://github.com/HikigayaHachiman0211/FH6Telemetry.git
Set-Location -LiteralPath '.\FH6Telemetry'
dotnet restore FH6Telemetry.csproj
dotnet restore Tests/FH6Telemetry.Tests.csproj
dotnet build FH6Telemetry.csproj --no-restore -v:q
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore -- --ui
.\pack-exe.ps1 -NoRestore
Get-FileHash -LiteralPath 'publish/current/FH6Telemetry.exe' -Algorithm SHA256
```

所有命令应返回成功。测试使用模拟报文、UDP 回环和无界面窗口，运行时无需启动 FH6。若测试能创建窗口但收不到回环报文，请在允许本机 UDP 的环境重跑。窗口截图在测试输出目录的 `ui-preview`。

`dotnet run --project FH6Telemetry.csproj` 可验证窗口和只读收包。验收单手柄转发时运行 `publish/current/FH6Telemetry.exe`，以便启动器提供实际运行路径和手柄探测。

## 部署目录和升级

| 位置 | 内容 |
| --- | --- |
| `%APPDATA%\FH6Telemetry\telemetry-settings.json` | 标准设置，包含监听、快捷键、驾驶辅助和 HUD |
| 启动器旁的 `Data\telemetry-settings.json` | 便携设置。标准设置尚未建立且便携文件存在时优先使用便携设置。首选目录不可写时会尝试另一处。 |
| 遥测数据目录内的 `drives\` | 行驶录制。默认位于设置目录下的 `telemetry\drives`。自定义目录以「历史记录」打开的位置为准。 |
| 实际设置目录中的 `speed-gear-profiles.json` | 学到的换挡和挡比资料 |
| 启动器旁的 `Data\fh6-hidhide.json` | 可选的本机实体手柄配对，与设置目录选择无关 |
| `%LOCALAPPDATA%\FH6Telemetry\Runtime` | 启动器按版本准备的运行缓存。可写性不足时会尝试临时目录或启动器旁的缓存。实际程序路径见「虚拟手柄配置」。 |

升级时退出旧助手，备份实际配置及历史目录。可以先把新版放到单独目录核对启动与收包。若旧版用了便携配对，只在同一台机器核对设备后迁移配对 JSON。`.lease` 由程序管理。保留旧 EXE，出现兼容问题时可退出新版并启动旧版。

首次从旧版整合工具导入本机遥测时，可用 `.\FH6Telemetry.exe --import-trainer-telemetry`。已有的独立设置优先。常规升级直接沿用现用的独立设置。

## 验证 Data Out

1. 在游戏「设置 → HUD 和游戏性」开启 Data Out。同机设 `127.0.0.1` 和 `20440`，助手「模块设置」使用同一地址和端口。已有实例占用时，正常停止旧实例，或让两端一起改用其他端口。
2. 点「开始接收」。回到自由漫游，让车速或踏板变化。在「数据诊断」看累计接收、解析失败、来源和包长。当前解析器使用 FH6 的 324 字节包。暂停或菜单期间可能停发，输出频率随游戏帧率变化。
3. 若端口冲突，可查询端口的拥有进程，再在对应助手正常停止接收。 [Forza Data Out 文档](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

```powershell
Get-NetUDPEndpoint -LocalPort 20440 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
netstat.exe -ano -p UDP | Select-String '127\.0\.0\.1:20440\s+'
```

## 单手柄转发

### 全新设置

1. 确认可用的 XInput 手柄。需要转发时安装 ViGEmBus 与 HidHide x64，按安装程序要求重启。只使用键盘兼容换挡时直接去下一节。 [ViGEmBus 发布](https://github.com/nefarius/ViGEmBus/releases/latest) · [HidHide 发布](https://github.com/nefarius/HidHide/releases/latest)
2. 打开打包版的「虚拟手柄配置」，复制显示的实际运行程序路径。在 HidHide 的 Applications 放行该文件。按断开与重连确认 Devices 中的目标实体手柄。Xbox 有线设备可能有 USB 和 HID 两个条目。保持普通隐藏模式，启用目标设备的隐藏并重连。
3. 启用助手中的「单手柄转发 · 内置离合与补油」。确认助手能读取实体手柄，游戏接入虚拟 Xbox 手柄。如果出现双输入，逐一排查其他映射器和 Steam Input 的单游戏配置。若虚拟手柄出现得比游戏晚且游戏未接入，保持转发并正常重启 FH6。 [HidHide 设置](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/) · [Xbox 可见性排错](https://docs.nefarius.at/projects/HidHide/CLI-Usage/#hiding-xbox-xinput-controllers)

### 已有便携配对

`fh6-hidhide.json` 是本机选定的 `HID\` 或 `USB\` 设备实例字符串数组。它应与 HidHide 当前要隐藏的实体设备列表一致。「虚拟手柄配置」显示找到便携配对后，助手在转发时验证配对并临时放行自己的运行文件。

用下面的只读命令核对驱动状态。关闭占用 HidHide 的配置客户端后再运行。比较设备实例时留在本机，验收报告记录匹配结果和数量即可。 [HidHide CLI 文档](https://docs.nefarius.at/projects/HidHide/CLI-Usage/)

```powershell
$hidHideRoot = Join-Path $env:ProgramFiles 'Nefarius Software Solutions/HidHide'
$hidHideCli = Join-Path $hidHideRoot 'HidHideCLI.exe'
if (-not (Test-Path -LiteralPath $hidHideCli)) {
    $hidHideCli = Join-Path $hidHideRoot 'x64/HidHideCLI.exe'
}
& $hidHideCli --cloak-state --inv-state --dev-list --app-list
```

把已核对的旧配对文件复制到新版 EXE 旁的 `Data`。核对普通隐藏模式和设备列表一致。续用临时隔离时，游戏和 Steam 采用普通设备可见性，助手运行程序获准读取实体手柄。正常断开转发应恢复连接前的可见性。上次进程异常退出时，先正常启动配对的助手，让它恢复原会话，再继续排查。

### 核对游戏视角的手柄数

在玩家当前的 Windows 桌面会话执行。启用转发后，期待由启动器视角看到一只虚拟手柄。计数本身只能说明可见数量，结合转发状态和实际手柄操作确认设备身份。

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

## 键盘兼容与游戏键位

| 动作 | 键盘默认 | 虚拟 Xbox 默认 |
| --- | --- | --- |
| 升挡 | `E` | `B` |
| 降挡 | `Q` | `X` |
| 离合 | 左 `Shift` | `L3` |
| 油门 | `W` | `RT` |
| 刹车与转向 | 随原游戏布局 | `LT` 与左摇杆横轴 |

在 FH6 选择当前生效的控制器或键盘预设。使用自动离合时核对「手动（带离合器）」与离合键。主动驾驶辅助的手刹键可以在助手单独选择，要与游戏布局一致。停车后先手动试当前输出方式的升降挡。键盘提交依赖游戏前台，助手与游戏使用相同的常规启动方式。 [Forza 控制器排错](https://support.forza.net/hc/en-us/articles/4408251153427-Controller-Troubleshooting) · [Windows 键盘输入文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

## 分层验收

1. **收包。** 在驾驶画面观察速度和挡位变化。停车时用刹车百分比验证报文仍在更新。
2. **实体操作。** 在游戏停车，确认所用键盘或转发手柄可正常升降挡。
3. **静止自检。** 停车挂一挡，关闭自动换挡和驾驶辅助。在「虚拟手柄配置」选输出方式并点「静止自检升挡」。15 秒内返回前台游戏，踩住刹车，松开油门、离合和手刹。日志只应出现一次自检指令，游戏和遥测都应确认二挡。
4. **自动换挡。** 回一挡后在离线自由漫游的平直路段启用自动换挡，观察一次发出指令并确认新挡。停用后确认自动输入释放。需要停止虚拟手柄时单独断开转发。
5. **驾驶辅助。** 完成转发后，在离线练习勾选确认，再接通所需功能。启用急弯实验后勾选自动学习，做几次稳定转弯。观察「测试功能」显示学到的上限，然后验证「HUD 设置 → 急弯上限提醒」的显示、关闭与保存。默认 `Pause` 用于停止辅助输出。
6. **回放。** 有记录时载入相同片段，核对实录轴值与重算输出。把回放和真实游戏验收分别记录。

| 现象 | 定位方法 |
| --- | --- |
| 连续等待遥测 | 对齐地址与端口，确认前台驾驶画面，查端口拥有者 |
| 一直等待连接输出 | 查选中的输出模式、驱动、便携配对或手动放行路径 |
| 助手收到快捷键但不发出换挡 | 查是否已连接、游戏焦点、最新遥测、手动接管或保护状态 |
| 输出已提交但挡位没变 | 用相同布局手动验键，检查离合模式、重复输入与游戏前台 |
| 手柄重复或不可用 | 对照启动器探测、实体录入和虚拟手柄可见性。逐项复核当前设备，断开转发可恢复 |
| 急弯上限仍用手动值 | 核对驾驶辅助已接通、实验和自动学习均启用，以及本车是否收集到有效稳定样本 |

## 记录、回放与诊断交付

在「数据诊断 → 行驶记录」开始录制。按需要依次做一次手动换挡、自检或正常自动换挡，注明约略时间、车型、输出模式和预期挡位。需要排查辅助转向时尽量覆盖入弯前、方向改变及出弯，记录启用的功能。

- `session.json` 保存概要、帧数和会话设置。
- `telemetry.jsonl.gz` 保存原始报文与解析样本。
- `shift-actions.jsonl` 保存输入、换挡状态和驾驶辅助介入。用 `elapsedMs` 同步时序，用 `telemetryTimestampMs` 对齐 `assist_frame` 与游戏样本。`physicalReport` 是实际输入，`report` 是转发输出，`appliedAssist` 是介入项。
- 对外汇报优先给必要时间段、结果和统计。实际设备实例、用户路径、完整行驶记录与配对文件保存在本机。 [数据结构与测试说明](performance-telemetry.md)

## 发布新版本

1. 更新 `FH6Telemetry.csproj` 的 `Version`，确保 `GitHubRepository` 指向发布目标。
2. 从将要标记的源码提交恢复依赖、构建、运行两组测试并打包。记录 EXE 的 SHA-256。
3. 将 `v<Version>` 标签推到远端。在对应的 GitHub Release 上传名为 `FH6Telemetry.exe` 的成品，发布说明列出变更、校验值和验证过的范围。源码下载随标签提供。 [GitHub 发版说明](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)
4. 发布前若使用草稿，先检查资产和说明。发布后验证 Release 页、二进制下载地址和 README 跳转。应用的「关于」页从配置的仓库检查最新正式 Release，开启自动下载后将新版保存到本地 `updates`。

交付时给出源码提交、发布标签和下载链接，注明包的哈希与测试结果。若没有在真实 FH6 环境验收，记录实际验证到了构建、收包、输入链路、静止验键或试跑中的哪一层。
