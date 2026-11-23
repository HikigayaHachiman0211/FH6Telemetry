# エージェント向け導入ガイド

[简体中文](../fh6-telemetry-agent-setup.md) · [English](../en/agent-deployment.md) · **日本語** · [한국어](../ko/agent-deployment.md)

FH6 が動く Windows x64 PC を対象に、導入、ビルド、入力設定、動作確認、トラブル対応、更新と公開の手順を示します。コマンドは独立リポジトリのルートから実行します。 [プレイヤー向けガイド](user-guide.md)

## 導入範囲を決める

| 目的 | 前提 | 完了の確認 |
| --- | --- | --- |
| テレメトリ、HUD、測定、再生 | 配布 EXE と Data Out | 車速、ギア、ペダルがゲームに追従 |
| キーボード自動シフト | テレメトリと一致したキーバインド | 停車時の確認で 1 速から 2 速に変化 |
| パッド中継、能動的な運転支援 | 配布 EXE、XInput パッド、ViGEmBus、HidHide | ゲームが意図した仮想パッドを認識し、転送・自動入力が反映 |
| ソースビルドと配布 | Git、.NET 10 SDK、単体起動ファイル用の MSVC x64 と Windows SDK | 両方のテストとパッケージが成功し、意図したバージョンを表示 |

対象のバージョン、出力方式、ゲームの操作プリセット、UDP ポート、設定の場所を控えます。入力を送信できたことと、ゲームのギアが変化したことを個別に記録します。

## 配布版またはソースから用意する

[GitHub リリース](https://github.com/HikigayaHachiman0211/FH6Telemetry/releases/latest)から `FH6Telemetry.exe` を書き込み可能なフォルダーにダウンロードします。`Get-FileHash -Algorithm SHA256 -LiteralPath '.\FH6Telemetry.exe'` で公開された値と照合し、起動後に「アプリ情報」でバージョンを確認します。.NET ランタイムは同梱です。

ソースから作る場合は、`dotnet --list-sdks` で .NET 10 SDK を確認します。単一ファイルにまとめる場合は、Visual Studio Build Tools の「C++ によるデスクトップ開発」から MSVC x64 と Windows SDK を導入します。スクリプトは `vswhere.exe` でツールを検出します。既存の配布版を再現する場合は一致するタグをチェックアウトします。 [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [C++ ツール](https://learn.microsoft.com/en-us/cpp/build/vscpp-step-0-installation)

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

テストには模擬入力と UDP ループバックを使用します。ローカル UDP が利用できる環境で実行し、UI テストの出力先の `ui-preview` にある画像を参照します。`dotnet run --project FH6Telemetry.csproj` でウィンドウと受信を確認できます。パッド中継は、実行パスとデバイス確認を扱う配布用の起動ファイルで検証します。

## 設定とアップグレード

| 場所 | 用途 |
| --- | --- |
| `%APPDATA%\FH6Telemetry\telemetry-settings.json` | 標準設定 |
| 起動ファイルの隣の `Data\telemetry-settings.json` | 既存の標準設定がない場合に使用できるポータブル設定。書き込みできない場合は別の場所を試します。 |
| テレメトリフォルダー内の `drives\` | 走行記録。履歴画面から現在の保存先を開けます。 |
| 設定フォルダー内の `speed-gear-profiles.json` | 学習済みのギアと車速の情報 |
| 起動ファイルの隣の `Data\fh6-hidhide.json` | 任意の本機用パッド設定 |
| `%LOCALAPPDATA%\FH6Telemetry\Runtime` | バージョンごとの実行キャッシュ。実行パスは「仮想パッド設定」に表示されます。 |

旧版を終了し、使用中の設定と記録をバックアップします。必要なら新版を別フォルダーで試します。同じ PC のパッド設定は、デバイスを確認したうえで新版の起動ファイルの隣へ移せます。`.lease` はアプリが管理します。問題時に戻せるよう、旧 EXE を保管します。旧統合ツールのデータを初めて移す場合は `--import-trainer-telemetry` を指定します。既存の独立版設定が優先されます。

## Data Out の確認

FH6 の「設定 → HUD とゲームプレイ」で Data Out を有効にし、同一 PC では両方を `127.0.0.1:20440` に合わせます。「受信開始」で受信し、フリーロームに戻って車速、ギア、ペダルを確認します。「診断ログ」で受信数、解析エラー、送信元を確認できます。現在の FH6 パーサーは 324 バイトのパケットに対応します。ゲームの一時停止中やメニュー表示中は配信が止まることがあります。 [Data Out 公式資料](https://support.forza.net/hc/en-us/articles/51744149102611-Forza-Horizon-6-Data-Out-Documentation)

```powershell
Get-NetUDPEndpoint -LocalPort 20440 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
netstat.exe -ano -p UDP | Select-String '127\.0\.0\.1:20440\s+'
```

競合時は使用中のプロセスを確認して古い受信を通常停止するか、ゲームとアプリの両方でポートを変更します。

## コントローラー中継

1. XInput 対応の実機パッドを確認し、中継を使う場合は x64 対応の ViGEmBus と HidHide をインストールします。インストーラーの再起動指示に従います。 [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest) · [HidHide](https://github.com/nefarius/HidHide/releases/latest)
2. 「仮想パッド設定」に表示される実行ファイルを HidHide の Applications で許可します。抜き差しして Devices の対象を特定し、通常の非表示モードでそのパッドを選びます。有線 Xbox パッドには USB と HID の項目が両方ある場合があります。 [HidHide セットアップ](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/)
3. 「単体コントローラー中継 · クラッチ＆ブリッピング内蔵」で接続し、実機入力と仮想パッドを確認します。仮想パッド出現後にゲームが認識しない場合は、中継を維持して通常の手順で FH6 を再起動します。入力が重複する場合は他のマッパーや Steam Input を一つずつ確認します。

ポータブルの `fh6-hidhide.json` は、非表示にする `HID\` または `USB\` のデバイスインスタンス文字列配列です。同じ PC の HidHide の対象一覧と照合します。中継中だけアプリの実行ファイルが実機パッドを読み取れるようにし、通常切断時に元の状態を戻します。中断後は、ペアリング済みの起動ファイルを開いて復元させます。設定クライアントを閉じ、次の読み取り専用確認を行います。 [HidHide CLI](https://docs.nefarius.at/projects/HidHide/CLI-Usage/)

```powershell
$hidHideRoot = Join-Path $env:ProgramFiles 'Nefarius Software Solutions/HidHide'
$hidHideCli = Join-Path $hidHideRoot 'HidHideCLI.exe'
if (-not (Test-Path -LiteralPath $hidHideCli)) {
    $hidHideCli = Join-Path $hidHideRoot 'x64/HidHideCLI.exe'
}
& $hidHideCli --cloak-state --inv-state --dev-list --app-list
```

ユーザーの Windows デスクトップセッションで、起動ファイルから見えるパッドを数えます。中継中の期待値は仮想パッド 1 台です。実機入力を送って転送先を確認します。

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

## キーボード出力と検証

キーボードの既定は `E`／`Q`、左 `Shift`、`W` です。仮想パッドは `B`／`X`、`L3`、`RT`、`LT` と左スティックを使用します。FH6 の有効な操作プリセットと運転支援のハンドブレーキを合わせます。クラッチを検証する場合は「クラッチ付きマニュアル」を選びます。 [コントローラーのトラブル対応](https://support.forza.net/hc/en-us/articles/4408251153427-Controller-Troubleshooting)

1. テレメトリが変化することを確認します。停車中はブレーキ入力を確認します。
2. 選んだ入力で手動変速を確認し、自動シフトと運転支援を停止して 1 速で停車します。
3. 「仮想パッド設定 → 停車中のシフトアップを確認」を開始し、15 秒以内にゲームに戻ってブレーキだけを踏みます。1 回の出力後にゲームとテレメトリの両方で 2 速を確認します。
4. 1 速へ戻り、オフラインの直線で自動変速を 1 回試します。停止後は自動入力の解除を確認します。中継を止めるときは別に切断します。
5. オフラインで運転支援を有効にし、急カーブの試験機能と自動学習を選んで安定した旋回を行います。「試験機能」の学習値と HUD の表示・保存を確認します。既定の `Pause` で支援出力を停止できます。
6. 記録の再計算結果とゲーム内で確認した結果を分けて記録します。

接続待ちの場合は出力方式、ドライバー、ペアリングを確認します。出力が送られても変速しない場合は、同じキーを手動で試し、クラッチ設定、前面ウィンドウ、重複入力を確認します。学習が進まない場合は、選択中の車両で安定した旋回が収集できているかを確認します。

## 記録・報告・リリース

「診断ログ」で再現区間を短く記録します。`session.json` はセッション情報、`telemetry.jsonl.gz` は生パケットと解析結果、`shift-actions.jsonl` は出力と支援イベントを含みます。`elapsedMs` と `telemetryTimestampMs` を使って整列します。`assist_frame` の `physicalReport` は実機入力、`report` は中継出力、`appliedAssist` は介入項目です。個別のデバイス ID、ローカルパス、全走行ログは必要な範囲に絞って扱います。

`FH6Telemetry.csproj` の `Version` と `GitHubRepository` を確認し、タグを付けるコミットからビルド、テスト、パッケージを行います。`v<Version>` の Release に `FH6Telemetry.exe` を添付し、変更点、検証範囲、SHA-256 を記載します。公開後にダウンロードを確認します。「アプリ情報」は最新の正式リリースを参照します。 [GitHub リリースの資料](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

引き継ぎ時にはコミット、タグ、ダウンロード URL、SHA-256、テスト結果を記録し、ゲーム環境でどの段階まで確認したかを明示します。
