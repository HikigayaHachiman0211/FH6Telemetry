# FH6 Telemetry

[简体中文](README.md) · **English** · [日本語](README.ja.md) · [한국어](README.ko.md)

FH6 Telemetry is a standalone Windows x64 companion for Forza Horizon 6. It receives the game's official Data Out feed and displays speed, gear, RPM, pedal inputs, and vehicle behavior in a desktop dashboard and an in-game HUD. You can benchmark a car, replay a drive, or try automatic shifting and experimental driving assists during offline practice. [Forza Data Out documentation](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

**[Download FH6Telemetry.exe](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest/download/FH6Telemetry.exe)** · **[Player guide](docs/en/user-guide.md)** · **[Agent deployment guide](docs/en/agent-deployment.md)**

## Quick start

1. Download and open `FH6Telemetry.exe`. The package includes the .NET runtime.
2. Enable Data Out under **Settings → HUD and Gameplay** in FH6. For a single PC, use IP `127.0.0.1` and port `20440`. Match the port in the app's **Settings** tab.
3. Click **Start Listening** and return to the driving screen. When speed, gear, and RPM follow the game, the HUD, charts, and recording are ready.

For automatic shifting, select **Manual** or **Manual with Clutch** in FH6 and match the bindings for your chosen output mode. [Input setup and stationary check](docs/en/user-guide.md#automatic-shifting)

## Features

| Feature | What you can do |
| --- | --- |
| Dashboard and HUD | Show speed, RPM, gear, pedals, steering, power, and protection status. Adjust placement, scale, opacity, and metrics. |
| Vehicle testing | Browse the vehicle catalog, measure acceleration and braking, save results, and compare tunes. |
| Recording and replay | Record telemetry, shift outcomes, and assist input/output. Review a moment or export diagnostics. |
| Automatic shifting | Pick a strategy and send shifts through a keyboard or virtual controller. Confirm the result from the game's reported gear. |
| Experimental driving assists | Try traction, braking, and countersteering assistance offline. The tight-turn feature supports a manual limit, per-car learning, and an optional HUD readout. |

The app, HUD, and exports support Simplified Chinese, English, Japanese, and Korean.

## Build and test

Use Windows x64 and the .NET 10 SDK from the repository root. The single-file package also needs MSVC x64 and the Windows SDK. [Download .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [Install the C++ tools](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

```powershell
dotnet restore Tests/FH6Telemetry.Tests.csproj
dotnet build FH6Telemetry.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore -- --ui
.\pack-exe.ps1 -NoRestore
```

The launcher is built at `publish/current/FH6Telemetry.exe`. The tests use simulated packets and local UDP. [Deployment, troubleshooting, and in-game validation](docs/en/agent-deployment.md)

## License

The source is licensed under [GPL-3.0](LICENSE). [Third-party notices](ThirdPartyNotices.txt) credit the shift strategies and their licenses. FH6 Telemetry is an independent community project.
