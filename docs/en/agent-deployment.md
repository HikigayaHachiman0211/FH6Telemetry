# Agent deployment guide

[简体中文](../fh6-telemetry-agent-setup.md) · **English** · [日本語](../ja/agent-deployment.md) · [한국어](../ko/agent-deployment.md)

This guide covers installation, builds, controller setup, validation, troubleshooting, upgrades, and releases on the Windows x64 PC that runs FH6. Commands start at the standalone repository root. [Player guide](user-guide.md)

## Choose the deployment path

| Scope | Prerequisites | Visible evidence |
| --- | --- | --- |
| Dashboard, HUD, benchmarks, or replay | Release EXE and FH6 Data Out | Speed, gear, or pedal input follows the game |
| Keyboard Auto-Shift | Working telemetry and matching keyboard bindings | Stationary check confirms first to second gear |
| Controller relay or active Drive Assist | Release EXE, XInput controller, ViGEmBus, and HidHide | FH6 sees the intended virtual pad and confirms forwarded and automatic inputs |
| Source build or distribution | Git, .NET 10 SDK, and MSVC x64 plus Windows SDK for the launcher | Both test suites pass and the packaged app reports the intended version |

Record the requested version, output mode, active control preset, UDP port, and settings path. Keep input submission and confirmation from game telemetry as separate observations.

## Obtain the executable or build from source

From a [GitHub release](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest), download `FH6Telemetry.exe` into a writable folder. Check the release's SHA-256 with `Get-FileHash -Algorithm SHA256 -LiteralPath '.\FH6Telemetry.exe'`. Open the launcher and check its version in **About**. The runtime is bundled.

For a source build, verify that `dotnet --list-sdks` shows a .NET 10 SDK. Install the **Desktop development with C++** workload with x64 MSVC and a Windows SDK to build the single-file launcher. The packaging script finds the toolchain through `vswhere.exe`. Check out the matching Git tag when reproducing a release. [Download .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [Install the C++ tools](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

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

Core and headless UI tests use simulated input and UDP loopback. Run them where local UDP is permitted. UI screenshots are written to `ui-preview` beneath the test output. `dotnet run --project FH6Telemetry.csproj` is suitable for a window or receive-only check. Use the packaged launcher to validate the controller relay.

## Settings and upgrades

| Location | Purpose |
| --- | --- |
| `%APPDATA%\FH6Telemetry\telemetry-settings.json` | Standard settings |
| `Data\telemetry-settings.json` beside the launcher | Portable settings when the standard file has not been created. The app can fall back to the other location on a write failure. |
| `drives\` in the telemetry folder | Drive recordings. **History** opens the current records location. |
| `speed-gear-profiles.json` in the active settings directory | Learned gear and speed information |
| `Data\fh6-hidhide.json` beside the launcher | Optional, machine-specific controller pairing |
| `%LOCALAPPDATA%\FH6Telemetry\Runtime` | Versioned runtime cache. The launcher may fall back to a temporary or launcher-local directory. **Virtual Pad** shows the actual path. |

Exit the old app and back up active settings and records before an upgrade. Try the new EXE in a separate directory when diagnosing a compatibility issue. On the same PC, migrate a verified pairing JSON beside the new launcher. The launcher manages its `.lease`. Keep the old EXE for rollback. To import the older combined tool's telemetry into a fresh installation, start the launcher with `--import-trainer-telemetry`. Existing standalone settings take precedence.

## Validate Data Out

Enable Data Out under FH6 **Settings → HUD and Gameplay**. For the same PC, use `127.0.0.1:20440` on both ends. Start listening, return to free roam, and confirm live speed, gear, and pedal readings. **Diagnostics** shows packet counts, parse failures, and sender information. The current FH6 parser expects 324-byte packets. Menu and pause screens may stop the feed. [Forza Data Out documentation](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

```powershell
Get-NetUDPEndpoint -LocalPort 20440 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
netstat.exe -ano -p UDP | Select-String '127\.0\.0\.1:20440\s+'
```

If there is a port conflict, identify the owner and stop the other listener normally, or change the configured port on both ends.

## Single-controller relay

1. Verify an XInput physical controller. Install the x64 ViGEmBus and HidHide drivers when this output mode is needed, then follow the installers' restart instructions. [ViGEmBus releases](https://github.com/nefarius/ViGEmBus/releases/latest) · [HidHide releases](https://github.com/nefarius/HidHide/releases/latest)
2. Open **Virtual Pad** in the packaged app. Allow the displayed runtime path under HidHide **Applications**. Identify the physical device by reconnecting it and select the right controller under **Devices**, using normal hiding mode. A wired Xbox controller can have both USB and HID entries. [HidHide setup](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/)
3. Start **Single-Controller Relay · Built-in Clutch & Blip**. Verify that the app reads the physical controller and FH6 sees the virtual pad. If FH6 started earlier, leave the relay active and restart the game normally if necessary. Check other mappers or Steam Input one at a time when diagnosing duplicate input.

For a portable pairing, `fh6-hidhide.json` holds an array of the selected `HID\` or `USB\` device-instance identifiers. Confirm it matches the hidden device set on this PC before using it. The app temporarily allows its runtime to read the device, and normal disconnection restores visibility. After an interruption, start the paired launcher to let it recover its session. Inspect the following read-only HidHide state locally, with the configuration client closed. [HidHide CLI](https://docs.nefarius.at/projects/HidHide/CLI-Usage/)

```powershell
$hidHideRoot = Join-Path $env:ProgramFiles 'Nefarius Software Solutions/HidHide'
$hidHideCli = Join-Path $hidHideRoot 'HidHideCLI.exe'
if (-not (Test-Path -LiteralPath $hidHideCli)) {
    $hidHideCli = Join-Path $hidHideRoot 'x64/HidHideCLI.exe'
}
& $hidHideCli --cloak-state --inv-state --dev-list --app-list
```

Run the launcher probe in the user's current Windows desktop session. After the relay connects, one visible virtual pad is the expected configuration. Confirm its identity by forwarding a real input.

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

## Keyboard output and validation

Keyboard defaults are `E` up, `Q` down, left `Shift` clutch, and `W` throttle. Virtual controller defaults are `B`, `X`, `L3`, and `RT`, with `LT` for braking and left stick for steering. Check the current FH6 control preset and use **Manual with Clutch** when testing automatic clutch. Match the chosen drive-assist handbrake button to the in-game binding. Check focus, app and game startup mode, and duplicate bindings if inputs are ignored. [Controller troubleshooting](https://support.forza.net/hc/en-us/articles/4408251153427-Controller-Troubleshooting)

1. Validate live telemetry first. While parked, press the brake to check that input data changes.
2. Verify manual upshift and downshift using the chosen input path. Then park in first gear with Auto-Shift and Drive Assist off.
3. Open **Virtual Pad → Check stationary upshift**, return to the game within 15 seconds, and hold only the brake. Confirm exactly one requested input and second gear in the game and telemetry.
4. Return to first and test one automatic shift during offline free roam on a straight. Disable Auto-Shift and check that it releases its inputs. Disconnect the relay separately to stop controller forwarding.
5. In offline practice, arm Drive Assist and collect stable turns with the tight-turn experiment and automatic learning on. Verify the learned limit in **Experimental** and the HUD reminder, then check that the reminder setting persists. The default `Pause` key stops assist output.
6. Treat replay comparisons and in-game confirmation as separate results.

If the app waits for output, check the selected mode, drivers, and relay pairing. If input is submitted but no gear changes, try the same bindings manually and check clutch mode, foreground focus, and duplicate input sources. If learning stays at the manual value, look for enough stable turns for the selected car.

## Record and report

Capture a short, repeatable segment from **Diagnostics**. `session.json` describes the session. `telemetry.jsonl.gz` carries the raw packets and parsed samples. `shift-actions.jsonl` carries requests, actual output, and assist events. Align the files with `elapsedMs` and `telemetryTimestampMs`. In `assist_frame`, `physicalReport` is driver input, `report` is forwarded output, and `appliedAssist` records the intervention. Keep local paths, device identifiers, and complete recordings on the user's machine unless needed for the reported issue.

## Publish

Set `Version` in `FH6Telemetry.csproj` and point `GitHubRepository` at the repository that serves its updates. Build, test, and package from the commit you plan to tag. Push `v<Version>` and attach the built `FH6Telemetry.exe` to the corresponding Release. Include changes, tested scope, and the SHA-256, then verify the public download. The **About** update check uses the latest stable release. [GitHub releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

Report the commit, tag, package URL, SHA-256, and test results. Identify the last verified layer when you cannot validate real game input, and keep a copy of the previous EXE for rollback.
