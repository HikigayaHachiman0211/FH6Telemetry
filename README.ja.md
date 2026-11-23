# FH6 テレメトリ

[简体中文](README.md) · [English](README.en.md) · **日本語** · [한국어](README.ko.md)

FH6 テレメトリは『Forza Horizon 6』向けの Windows x64 用コンパニオンアプリです。ゲーム公式の Data Out を受信し、車速、ギア、回転数、ペダル入力、車両の状態をダッシュボードとゲーム内 HUD に表示します。性能測定や走行の振り返りに加え、オフライン練習で自動シフトと試験的な運転支援を利用できます。 [Forza Data Out の公式資料](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

**[FH6Telemetry.exe をダウンロード](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest/download/FH6Telemetry.exe)** · **[プレイヤー向けガイド](docs/ja/user-guide.md)** · **[エージェント向け導入ガイド](docs/ja/agent-deployment.md)**

## クイックスタート

1. `FH6Telemetry.exe` をダウンロードして起動します。.NET ランタイムは同梱されています。
2. FH6 の「設定 → HUD とゲームプレイ」で Data Out を有効にします。同じ PC で使う場合は IP を `127.0.0.1`、ポートを `20440` に設定します。アプリの「モジュール設定」でも同じポートを指定します。
3. 「受信開始」を押し、ゲームの走行画面へ戻ります。車速、ギア、回転数が反映されれば、HUD、グラフ、記録を使用できます。

自動シフトを使う場合は、ゲーム側で「マニュアル」または「クラッチ付きマニュアル」を選び、出力方式に合わせてキーやボタンを設定します。 [入力方式と停車時の確認](docs/ja/user-guide.md#自動シフト)

## 主な機能

| 機能 | 内容 |
| --- | --- |
| リアルタイム表示と HUD | 車速、ギア、回転数、ペダル、操舵、出力、保護状態を表示し、位置や大きさを調整できます。 |
| 車両データと性能測定 | 車両の諸元を確認し、加速や制動を測定してチューニング結果を比較できます。 |
| 記録と再生 | テレメトリ、変速結果、運転支援の入出力を記録し、指定した区間を確認できます。 |
| 自動シフト | キーボードまたは仮想コントローラーで変速を指示し、実際のギアをテレメトリで確認します。 |
| 試験的な運転支援 | オフラインで制動、トラクション、カウンターステアなどを試せます。急カーブ用の上限速度は手動設定または学習で決められ、HUD 表示を切り替えられます。 |

画面、HUD、エクスポートは簡体字中国語、英語、日本語、韓国語に対応しています。

## ビルドとテスト

リポジトリのルートで、Windows x64 と .NET 10 SDK を使用します。単一ファイルの作成には MSVC x64 と Windows SDK も必要です。 [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [C++ ツールのインストール](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

```powershell
dotnet restore Tests/FH6Telemetry.Tests.csproj
dotnet build FH6Telemetry.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore
dotnet run --project Tests/FH6Telemetry.Tests.csproj --no-restore -- --ui
.\pack-exe.ps1 -NoRestore
```

出力先は `publish/current/FH6Telemetry.exe` です。テストはシミュレーションとローカル UDP を使います。 [導入手順、トラブル対応、ゲーム内確認](docs/ja/agent-deployment.md)

## ライセンス

ソースコードは [GPL-3.0](LICENSE) です。変速ロジックの出典とライセンスは [第三者ライセンス通知](ThirdPartyNotices.txt) を参照してください。本アプリはコミュニティによる独立プロジェクトです。
