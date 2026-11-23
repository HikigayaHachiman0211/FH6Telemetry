using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FH6Telemetry;

internal static class StandaloneTelemetryTests
{
    public static void Register(Action<string, Action> run)
    {
        run("关于页：只检查配置的仓库，并在启用后校验和下载新版", () => WithDirectories((home, _) =>
        {
            using var handler = new AboutReleaseHandler();
            using var http = new HttpClient(handler);
            var preferences = new TelemetrySettingsFile(home);
            var settings = new TelemetrySettings { Port = 20441 };
            using var blank = new AboutViewModel(preferences, settings, new GitHubUpdates(null, http));
            Check(!blank.HasRepository && !blank.ShowStarPrompt && !blank.CheckForUpdatesCommand.CanExecute(null),
                "空仓库应关闭更新和点星入口");

            using var about = new AboutViewModel(preferences, settings, new GitHubUpdates("example-owner/auto-shift-tests", http));
            Check(about.ShowStarPrompt && about.LatestReleasePage?.AbsoluteUri == "https://github.com/example-owner/auto-shift-tests/releases/latest",
                "链接应使用配置的仓库");
            about.CheckAtStartup();
            Check(handler.Requests.Count == 0 && !about.AutoDownloadUpdates, "自动下载应默认关闭");
            about.CheckForUpdatesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(about.UpdateStatus == TelemetryText.Format("AboutUpdateAvailable", handler.Tag) && handler.Requests.Count == 1 &&
                  handler.Requests[0].AbsoluteUri == "https://api.github.com/repos/example-owner/auto-shift-tests/releases/latest",
                "手动检查应查询该仓库并显示新版本");

            about.AutoDownloadUpdates = true;
            about.DismissStarPromptCommand.Execute(null);
            var saved = preferences.Load();
            Check(saved.AutoDownloadUpdates && saved.AboutStarPromptDismissed && saved.Port == settings.Port && !about.ShowStarPrompt,
                "关于页偏好应和其他设置一起保存");
            blank.CheckAtStartup();
            Check(handler.Requests.Count == 1, "空仓库的自动检查不得发出请求");

            about.CheckAtStartup();
            about.CheckForUpdatesCommand.ExecutionTask!.GetAwaiter().GetResult();
            var file = Path.Combine(preferences.UpdatesDirectory, "FH6Telemetry-99.1.0.exe");
            Check(about.ShowUpdatesFolder && File.ReadAllBytes(file).SequenceEqual(handler.Package) &&
                  handler.Requests[^1].AbsoluteUri == "https://github.com/example-owner/auto-shift-tests/releases/download/v99.1.0/FH6Telemetry.exe",
                "自动下载应保存并校验自己的发布附件");

            handler.Tag = "v99.2.0";
            handler.CorruptDownload = true;
            about.CheckForUpdatesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(Directory.GetFiles(preferences.UpdatesDirectory).SequenceEqual([file]) &&
                  about.UpdateStatus == TelemetryText.Get("AboutUpdateFailed"), "校验失败的下载应清理临时文件并报告错误");
        }));

        run("快捷键：鼠标侧键可保存，其他冲突不会禁用 F9", () => WithDirectories((home, _) =>
        {
            var file = new TelemetrySettingsFile(home);
            var settings = new TelemetrySettings();
            settings.AutomaticShift = settings.AutomaticShift with
            {
                HudToggleKeyboardKey = "Mouse4", ClutchToggleKeyboardKey = "F9",
                DriveAssistToggleGamepadButton = ShiftShortcutButton.RightBumper,
            };
            Check(ShiftShortcut.ParseKey("Mouse4") == 0x05 && ShiftShortcut.ParseKey("Mouse5") == 0x06,
                "鼠标两颗侧键应保留各自的 Windows 键值");
            Check(file.Save(settings) is null, "应能保存快捷键配置");
            var saved = file.Load().AutomaticShift;
            Check(saved.EnableShiftShortcut && saved.ToggleKeyboardKey == "F9" && saved.ClutchToggleKeyboardKey == "" &&
                  saved.HudToggleKeyboardKey == "Mouse4" && saved.DriveAssistToggleGamepadButton == ShiftShortcutButton.RightBumper,
                "只应清除重复的次要绑定，保留自动换挡和有效的鼠标、手柄绑定");
        }));

        run("独立遥测：普通启动使用独立配置，即使存在旧修改器遥测数据", () => WithDirectories((home, legacy) =>
        {
            var oldFile = new TelemetrySettingsFile(legacy);
            Check(oldFile.Save(new TelemetrySettings { Port = 20520, AutoStart = true }) is null, "应保存旧配置以验证隔离");
            var file = StandaloneTelemetryStorage.Open(home, null, out var error);
            var settings = file.Load();
            Check(error is null && settings.Port == 20440 && !settings.AutoStart, "首次启动应使用独立版默认配置");
            Check(file.GetRecordDirectory(settings) == Path.Combine(home, "telemetry"), "记录目录应位于独立版的根目录");
            Check(!File.Exists(file.FilePath) && oldFile.Load().Port == 20520, "普通启动不会复制或改写旧配置");
        }));

        run("独立遥测：首次复制旧版设置、成绩与采样，后续使用自己的副本", () => WithDirectories((home, legacy) =>
        {
            var customRecords = Path.Combine(legacy, "custom-records");
            var oldFile = new TelemetrySettingsFile(legacy);
            var oldSettings = new TelemetrySettings
            {
                Port = 20520,
                DataDirectory = customRecords,
                AutomaticShift = new AutomaticShiftSettings
                {
                    GamepadMode = GamepadShiftMode.Combination,
                    PressDurationMs = 180,
                    UpshiftGripProtection = true,
                    Algorithm = ShiftAlgorithm.PowerDrop,
                    MaximumGear = 7,
                },
            };
            Check(oldFile.Save(oldSettings) is null, "应保存迁移用的旧配置");
            var record = new TestResult { Title = "0–100 km/h", DurationSeconds = 4.2, CarOrdinal = 2740 };
            var originalStore = new TelemetryStore(customRecords);
            originalStore.Save(record, [new TelemetrySample { TimestampMs = 20, SpeedKmh = 20 }]);

            var standalone = StandaloneTelemetryStorage.Open(home, legacy, out var error);
            var settings = standalone.Load();
            Check(error is null && settings.Port == 20520 && settings.AutomaticShift == oldSettings.AutomaticShift,
                "应保留之前的监听端口和手柄设置");
            var history = new TelemetryStore(standalone.GetRecordDirectory(settings));
            Check(history.RootPath == Path.Combine(home, "telemetry") && history.Records.Single().Id == record.Id,
                "旧成绩应复制到独立版的目录");
            Check(File.ReadAllText(history.SamplePath(record.Id)!) == File.ReadAllText(originalStore.SamplePath(record.Id)!),
                "应为旧成绩保留原始采样");

            history.Delete(record.Id);
            settings.Port = 20521;
            Check(standalone.Save(settings) is null, "独立版设置应能保存");
            var reopened = StandaloneTelemetryStorage.Open(home, legacy, out error);
            Check(error is null && reopened.Load().Port == 20521 &&
                  new TelemetryStore(reopened.GetRecordDirectory(reopened.Load())).Records.Count == 0,
                "重开独立版应保留新的设置和历史修改");
            Check(oldFile.Load().Port == 20520 && new TelemetryStore(customRecords).Records.Single().Id == record.Id,
                "操作独立副本不可影响旧版数据");
        }));

        run("独立遥测：读取旧版 JSON 时保留设置并使用缺失字段的默认值", () => WithDirectories((home, legacy) =>
        {
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "telemetry-settings.json"), """
                { "Port": 20523, "SpeedUnit": "mph", "AutomaticShift": {
                    "GamepadMode": 1, "UpshiftPercent": 91, "DownshiftPercent": 32, "PressDurationMs": 150, "ToggleKeyboardKey": "F10" } }
                """);
            var standalone = StandaloneTelemetryStorage.Open(home, legacy, out var error);
            var settings = standalone.Load();
            Check(error is null && settings.Port == 20523 && settings.SpeedUnit == "mph", "应读取旧版设置");
            Check(settings.Hud == new TelemetryHudOptions(), "旧版没有 HUD 设置时应保持原来的底部居中外观");
            Check(settings.AutomaticShift is { GamepadMode: GamepadShiftMode.Keyboard, PressDurationMs: 150, ToggleKeyboardKey: "F10",
                    HudToggleKeyboardKey: "", StartRecordingKeyboardKey: "", StopRecordingKeyboardKey: "" },
                "旧 Steam 模式应改用键盘，并保留其他设置。新增快捷键默认留空");
            var migrated = File.ReadAllText(standalone.FilePath);
            Check(!migrated.Contains("UpshiftPercent") && !migrated.Contains("DownshiftPercent"), "升级后的配置应移除手动阈值");
        }));

        run("HUD 设置：保存布局、外观和显示项目，并修正无效的配置值", () => WithDirectories((home, _) =>
        {
            var file = new TelemetrySettingsFile(home);
            var hud = new TelemetryHudOptions { DisplayId = "DISPLAY2", HorizontalPercent = 40, VerticalPercent = 15,
                ScalePercent = 135, OpacityPercent = 68, ShowSpeed = false, ShowShiftHint = false,
                ShowPedals = false, ShowTelemetryDetails = false, ShowProtectionStatus = false, EnableTextOutline = false };
            Check(file.Save(new TelemetrySettings { Port = 20526, Hud = hud }) is null, "应保存 HUD 设置");
            var settings = file.Load();
            Check(settings.Hud == hud && settings.Port == 20526, "重开后应恢复 HUD 设置和接收端口");

            File.WriteAllText(file.FilePath, """
                { "Hud": { "HorizontalPercent": -12, "VerticalPercent": 500, "ScalePercent": 900,
                  "OpacityPercent": -10, "ShowSpeed": false, "ShowGear": false, "ShowRpm": false } }
                """);
            var repaired = file.Load().Hud;
            Check(repaired.HorizontalPercent == 0 && repaired.VerticalPercent == 100 &&
                  repaired.ScalePercent == TelemetryHudOptions.MaximumScale &&
                  repaired.OpacityPercent == TelemetryHudOptions.MinimumOpacity &&
                  repaired.ShowGear && repaired.ShowPedals && repaired.ShowTelemetryDetails &&
                  repaired.ShowProtectionStatus && repaired.EnableTextOutline,
                "越界的 HUD 应回到可见范围，至少保留挡位且新扩展细节、防护状态与高对比度描边默认开启");

            var transparentHud = hud with { OpacityPercent = 0 };
            var saveError = file.Save(new TelemetrySettings { Port = 20526, Hud = transparentHud });
            Check(saveError is null, saveError ?? "应保存透明 HUD 设置");
            var savedOpacity = file.Load().Hud.OpacityPercent;
            Check(savedOpacity == 0, $"HUD 背景不透明度应恢复 0%，实际为 {savedOpacity}%");
        }));

        run("独立遥测：自定义缓存保留、定时清缓存和历史清理配置可保存恢复", () => WithDirectories((home, _) =>
        {
            var file = new TelemetrySettingsFile(home);
            Check(file.Load().Retention is { Preset: TelemetryRetentionPreset.Balanced, ClearCache: false, CleanHistory: false },
                "新配置应默认平衡档，两种定时清理均由用户启用");
            var retention = new TelemetryRetention { Preset = TelemetryRetentionPreset.Custom, Seconds = 21, Frames = 345,
                CleanupIntervalSeconds = 8, ClearCache = true, CleanHistory = true, HistoryDays = 7 };
            Check(file.Save(new TelemetrySettings { Retention = retention }) is null && file.Load().Retention == retention,
                "自定义秒数、帧数、间隔及成绩保留天数应原样恢复");
        }));

        run("换挡策略配置：两个新策略和驾驶模式可恢复，旧配置仍可读取", () => WithDirectories((home, _) =>
        {
            Directory.CreateDirectory(home);
            var file = new TelemetrySettingsFile(home);
            File.WriteAllText(file.FilePath, """{ "AutomaticShift": { "UpshiftAlgorithm": 1 } }""");
            Check(file.Load().AutomaticShift.Algorithm == ShiftAlgorithm.PowerDrop, "旧动力衰减设置应保留");
            File.WriteAllText(file.FilePath, """{ "AutomaticShift": { "UpshiftAlgorithm": 4, "ShiftLatencyMs": 9999 } }""");
            Check(file.Load().AutomaticShift is { Algorithm: ShiftAlgorithm.Mode5, ShiftLatencyMs: null },
                "旧的第五个策略应继续有效，无效延迟应单独恢复为自动估计");
            foreach (var strategy in new[] { ShiftAlgorithm.VirtualTcu, ShiftAlgorithm.Fh6AutoShift })
            {
                var expected = new AutomaticShiftSettings { Algorithm = strategy, VirtualTcuMode = VirtualTcuMode.Race,
                    GamepadMode = GamepadShiftMode.Combination, MaximumGear = 10, ShiftLatencyMs = 230 };
                Check(file.Save(new TelemetrySettings { AutomaticShift = expected }) is null &&
                      file.Load().AutomaticShift == expected, "策略、驾驶模式与手柄配置应一起保留");
            }
            File.WriteAllText(file.FilePath, """{ "AutomaticShift": { "UpshiftAlgorithm": 99, "VirtualTcuMode": -1 } }""");
            var fallback = file.Load().AutomaticShift;
            Check(fallback.Algorithm == ShiftAlgorithm.LazyForza && fallback.VirtualTcuMode == VirtualTcuMode.Comfort,
                "无效配置应回到已有默认值");
        }));

        run("独立遥测：个人资料目录不可写时改用便携目录", () => WithDirectories((home, legacy) =>
        {
            File.WriteAllText(home, "Folder unavailable");
            var portable = Path.Combine(Path.GetDirectoryName(home)!, "portable", "Data");
            var oldFile = new TelemetrySettingsFile(legacy);
            Check(oldFile.Save(new TelemetrySettings { Port = 20525 }) is null, "应写入旧版配置");
            var opened = StandaloneTelemetryStorage.Open(home, legacy, out var notice, portable);
            Check(notice?.Contains("程序目录") == true && opened.Load().Port == 20525,
                "应提示便携目录，且能在其中迁移旧版配置");
            Check(opened.GetRecordDirectory(opened.Load()) == Path.Combine(portable, "telemetry"),
                "便携记录应随程序目录保存");
        }));

        run("严格车速匹配：新配置与未记录开关的旧配置默认开启，可保存关闭", () => WithDirectories((home, _) =>
        {
            Directory.CreateDirectory(home);
            var file = new TelemetrySettingsFile(home);
            Check(file.Load().AutomaticShift is { StrictSpeedGearMatching: true, AutoDetectRaceTransmission: true,
                ProtectLaunchWheelspin: true, EnableShiftShortcut: true, ToggleKeyboardKey: "F9" },
                "新安装应默认开启赛用变速箱识别、起步防滑及 F9 快捷键");
            File.WriteAllText(file.FilePath, """
                { "Port": 20526, "Hud": { "ShowAutoShiftStatus": true, "ShowSpeed": false },
                  "AutomaticShift": { "UpshiftAlgorithm": 1, "ToggleKeyboardKey": "F10", "ToggleGamepadButton": 128 } }
                """);
            var settings = file.Load();
            Check(!settings.ShowGearChart && settings.Hud is { ShowAutoShiftStatus: true, ShowSpeed: false, ShowLearnedTightTurnSpeed: true } &&
                  settings.AutomaticShift is { AutomaticClutch: true, AutoDetectRaceTransmission: true, AdaptiveGearMatching: false, StrictSpeedGearMatching: true,
                      ProtectLaunchWheelspin: true, ProtectAirborne: true, ProtectShiftReengagement: false, EnableShiftShortcut: true, DriftMode: false,
                      ToggleKeyboardKey: "F10", ToggleGamepadButton: ShiftShortcutButton.RightStick,
                      ClutchToggleKeyboardKey: "", ClutchToggleGamepadButton: ShiftShortcutButton.None,
                      Algorithm: ShiftAlgorithm.PowerDrop } &&
                  settings.Port == 20526, "旧配置缺少开关时应打开严格匹配并保留已有设置");
            settings.ShowGearChart = true;
            settings.Hud = settings.Hud with
                { ShowAutoShiftStatus = false, ShowVehicleIdentity = false, ShowDriveAssistStatus = false, ShowLearnedTightTurnSpeed = false };
            settings.AutomaticShift = settings.AutomaticShift with
            {
                Algorithm = ShiftAlgorithm.Mode5, AutomaticClutch = false, AutoDetectRaceTransmission = false,
                ClutchToggleKeyboardKey = "C", ClutchToggleGamepadButton = ShiftShortcutButton.DpadDown,
                HudToggleKeyboardKey = "F12", HudToggleGamepadButton = ShiftShortcutButton.LeftStick,
                StartRecordingKeyboardKey = "F6", StartRecordingGamepadButton = ShiftShortcutButton.DpadLeft,
                StopRecordingKeyboardKey = "F7", StopRecordingGamepadButton = ShiftShortcutButton.DpadRight,
                AdaptiveGearMatching = true, StrictSpeedGearMatching = false, ProtectShiftReengagement = true,
                DriveAssistToggleKeyboardKey = "F8", DriveAssistToggleGamepadButton = ShiftShortcutButton.LeftBumper,
                ProtectLaunchWheelspin = false, ProtectAirborne = false, EnableShiftShortcut = false, DriftMode = true,
            };
            Check(file.Save(settings) is null, "用户启用功能后应保存");
            var reopened = file.Load();
            Check(reopened.ShowGearChart && reopened.Hud == settings.Hud && reopened.AutomaticShift == settings.AutomaticShift,
                "重启后应保留用户选择的开关");

            Check(ShiftShortcut.Validate(settings.AutomaticShift with { ClutchToggleKeyboardKey = "F10" }) is not null,
                "自动换挡与自动离合不能绑定相同键盘快捷键");
            Check(ShiftShortcut.Validate(settings.AutomaticShift with { ClutchToggleGamepadButton = ShiftShortcutButton.RightStick }) is not null,
                "自动换挡与自动离合不能绑定相同手柄快捷键");
            var kbConflict = settings.AutomaticShift with { GamepadMode = GamepadShiftMode.Keyboard, EnableShiftShortcut = true,
                ClutchToggleKeyboardKey = "E" };
            Check(!kbConflict.TryValidate(out _), "自动离合快捷键不能与键盘升/降挡、离合或补油键冲突");
            Check(ShiftShortcut.Validate(settings.AutomaticShift with { StopRecordingKeyboardKey = "f6" }) is not null &&
                  ShiftShortcut.Validate(settings.AutomaticShift with { StartRecordingGamepadButton = ShiftShortcutButton.LeftStick }) is not null &&
                  ShiftShortcut.Validate(settings.AutomaticShift with { HudToggleKeyboardKey = "E" }) is not null,
                "新增快捷键也应校验重复绑定与换挡键冲突");
            Check(ShiftShortcut.Validate(settings.AutomaticShift with { DriveAssistToggleKeyboardKey = "f10" }) is not null &&
                  ShiftShortcut.Validate(settings.AutomaticShift with { DriveAssistToggleGamepadButton = ShiftShortcutButton.RightStick }) is not null &&
                  ShiftShortcut.Validate(settings.AutomaticShift with { DriveAssistToggleKeyboardKey = "W" }) is not null,
                "驾驶辅助应拒绝重复绑定，以及与合成的键盘油门冲突的快捷键");
        }));

        run("四语本地化：字典键与占位符完全对齐，英文与韩文无中文残留", () =>
        {
            var zh = TelemetryText.DictionaryFor(TelemetryLanguage.ZhCn);
            var languages = new[] { TelemetryLanguage.En, TelemetryLanguage.Ja, TelemetryLanguage.Ko };
            var zhKeys = zh.Keys.ToHashSet(StringComparer.Ordinal);
            static HashSet<int> Placeholders(string text)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)(?:[^}]*)?\}");
                return matches.Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            }

            foreach (var (key, value) in zh)
                Check(!string.IsNullOrWhiteSpace(value), $"简体中文字典键 {key} 不应为空");

            foreach (var lang in languages)
            {
                var dict = TelemetryText.DictionaryFor(lang);
                var missing = zhKeys.Except(dict.Keys, StringComparer.Ordinal).ToArray();
                var extra = dict.Keys.Except(zhKeys, StringComparer.Ordinal).ToArray();
                Check(missing.Length == 0 && extra.Length == 0 && dict.Count == zh.Count,
                    $"{lang} 字典键不对齐：missing=[{string.Join(',', missing)}] extra=[{string.Join(',', extra)}]");

                foreach (var (key, zhValue) in zh)
                {
                    var translated = dict[key];
                    Check(!string.IsNullOrWhiteSpace(translated), $"{lang} 键 {key} 译文不应为空");
                    Check(Placeholders(zhValue).SetEquals(Placeholders(translated)),
                        $"{lang} 键 {key} 占位符与中文不一致：zh=\"{zhValue}\" target=\"{translated}\"");
                    if (lang is TelemetryLanguage.En or TelemetryLanguage.Ko)
                    {
                        var textToCheck = key == "TipLanguage"
                            ? translated.Replace("简体中文", "").Replace("日本語", "")
                            : translated;
                        var hasHan = textToCheck.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
                        Check(!hasHan, $"{lang} 键 {key} 不应残留汉字：{translated}");
                    }
                }
            }
        });

        run("四语本地化：界面语言配置持久化、Core 数据文本与 HTML 报告切换", () => WithDirectories((home, _) =>
        {
            try
            {
                var defaults = new TelemetrySettings();
                Check(defaults.Language == "zh-CN", "默认界面语言应为 zh-CN");
                Check(TelemetryText.ParseCode("en-US") == TelemetryLanguage.En &&
                      TelemetryText.ParseCode("ja-JP") == TelemetryLanguage.Ja &&
                      TelemetryText.ParseCode("ko-KR") == TelemetryLanguage.Ko &&
                      TelemetryText.ParseCode("fr-FR") == TelemetryLanguage.ZhCn,
                    "语言代码解析应兼容区域后缀并对未知代码回退 zh-CN");

                var file = new TelemetrySettingsFile(home);
                foreach (var (lang, code) in new[]
                {
                    (TelemetryLanguage.En, "en"),
                    (TelemetryLanguage.Ja, "ja"),
                    (TelemetryLanguage.Ko, "ko"),
                    (TelemetryLanguage.ZhCn, "zh-CN"),
                })
                {
                    Check(file.Save(new TelemetrySettings { Language = code }) is null, $"应保存语言 {code}");
                    var loaded = file.Load();
                    Check(loaded.Language == code && TelemetryText.ParseCode(loaded.Language) == lang,
                        $"重启读取后应保持语言 {code}");
                }

                TelemetryText.SetLanguage(TelemetryLanguage.En);
                Check(DrivetrainNames.Name(2) == "AWD" &&
                      VehicleCatalog.DriveName(3) == "AWD" &&
                      VehicleCatalog.AspirationName(1) == "Naturally Aspirated" &&
                      TestCatalog.LocalizedTitle("a0_100", 0, 100, "0–100 加速") == "0–100 Accel",
                    "切换英文后 Core 驱动形式/目录/测试名称应随语言更新");

                var store = new TelemetryStore(Path.Combine(home, "store-en"));
                var sampleRecord = new TestResult
                {
                    Key = "a0_100",
                    Title = "0–100 加速",
                    Kind = PerformanceTestKind.Acceleration,
                    StartValue = 0,
                    EndValue = 100,
                    DurationSeconds = 3.21,
                    CarOrdinal = 1155,
                    CarName = "1965 Shelby Cobra Daytona",
                    Valid = true,
                };
                var reportPath = store.ExportHtmlReport([sampleRecord]);
                var html = File.ReadAllText(reportPath);
                Check(html.Contains("<html lang=\"en\">", StringComparison.Ordinal) &&
                      html.Contains("0–100 Accel", StringComparison.Ordinal) &&
                      !html.Contains("生成时间", StringComparison.Ordinal),
                    "英文下导出 HTML 报告应使用 lang=en 与英文表头及测试名称");

                TelemetryText.SetLanguage(TelemetryLanguage.Ja);
                Check(DrivetrainNames.Name(1) == "FR (RWD)" && VehicleCatalog.AspirationName(2) == "ターボ",
                    "切换日文后 Core 文本应更新为日语");
                TelemetryText.SetLanguage(TelemetryLanguage.Ko);
                Check(DrivetrainNames.Name(0) == "전륜 (FWD)" && VehicleCatalog.AspirationName(2) == "터보차저",
                    "切换韩文后 Core 文本应更新为韩语");
            }
            finally
            {
                TelemetryText.SetLanguage(TelemetryLanguage.ZhCn);
            }
        }));
    }

    private sealed class AboutReleaseHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public byte[] Package { get; } = [1, 2, 3];
        public string Tag { get; set; } = "v99.1.0";
        public bool CorruptDownload { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            HttpContent content = uri.Host == "api.github.com"
                ? JsonContent.Create(new
                {
                    tag_name = Tag,
                    assets = new[] { new { name = "FH6Telemetry.exe", size = Package.Length, digest = $"sha256:{Convert.ToHexString(SHA256.HashData(Package))}" } },
                })
                : new ByteArrayContent(CorruptDownload ? [1, 2, 4] : Package);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static void WithDirectories(Action<string, string> test)
    {
        var root = Directory.CreateTempSubdirectory("fh6-standalone-test-");
        try { test(Path.Combine(root.FullName, "standalone"), Path.Combine(root.FullName, "legacy")); }
        finally { root.Delete(recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
