using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FH6Telemetry;
using FH6Telemetry.Testing;
using FH6Telemetry.ViewModels;
using FH6Telemetry.Views;
using FH6Telemetry.Views.Controls;

internal static class StandaloneTelemetryWindowTests
{
    public static void Run()
    {
        AppBuilder.Configure<TelemetryApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();

        var root = Directory.CreateTempSubdirectory("fh6-standalone-window-");
        try
        {
            int port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            var preferences = new TelemetrySettingsFile(root.FullName);
            Check(preferences.Save(new TelemetrySettings { Port = port, TimeoutMs = 600 }) is null, "应保存独立测试配置");
            var pad = new HeadlessShiftGamepad();
            var input = new ShortcutInput(new GamepadShiftInput(() => pad, () => true, () => false));
            var window = new TelemetryWindow(preferences, input);
            var model = (PerformanceTelemetryViewModel)window.DataContext!;
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Check(window.Title == "FH6 遥测助手" && window.Content is PerformanceTelemetryView,
                    "独立窗口应直接显示遥测页面");
                Check(window.Icon is not null, "独立窗口应加载 FH6 遥测应用图标");
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
                Check(tabs.ItemCount == 15 && tabs.Items[0] is TabItem { Header: "自动换挡" } && tabs.SelectedIndex == 0,
                    "独立窗口应保留完整标签页并默认打开首位的自动换挡");
                CheckAboutTab(window, model, tabs);
                Check(!model.IsListening && !model.IsAutoShiftEnabled, "首次运行应等待手动启用");
                Check(model.SettingsStorePath == Path.Combine(root.FullName, "telemetry"), "窗口应使用独立历史目录");
                var view = (PerformanceTelemetryView)window.Content!;
                model.SelectedTab = (int)TelemetryTab.DriveAssist;
                Dispatcher.UIThread.RunJobs();
                var ecu = view.FindControl<ScrollViewer>("DriveAssistPage")!;
                var assistChecks = ecu.GetVisualDescendants().OfType<CheckBox>().Where(check => check.DataContext is DriveAssistOption).ToArray();
                Check(tabs.SelectedItem is TabItem { Header: "驾驶辅助" } && ecu.IsEffectivelyVisible && assistChecks.Length > 0 &&
                      assistChecks.All(check => check.IsChecked == false) && !model.IsDriveAssistArmed && !model.CanConnectDriveAssist,
                    "驾驶辅助应有独立标签页，全部功能默认为关");
                Check(ecu.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible &&
                      text.Text is { } notice && notice.Contains("实验性功能") && notice.Contains("仍在开发") &&
                      notice.Contains("稳定性和圈速") && notice.Contains("游戏自带")),
                    "接通前应能看到实验状态和与游戏自带辅助的差距提示");
                Check(ecu.GetVisualDescendants().OfType<TextBlock>().Count(text => text.DataContext is DriveAssistOption option &&
                       text.Text == option.Description && text.Text.Length > 10 && text.Text != "EcuDesc" + option.Feature) == assistChecks.Length,
                    "每项辅助应显示一段直接可读的解释");
                var presetPicker = view.FindControl<ComboBox>("DriveAssistPresetPicker")!;
                var applyPreset = view.FindControl<Button>("ApplyDriveAssistPresetButton")!;
                var presetStatus = view.FindControl<TextBlock>("DriveAssistPresetStatusText")!;
                Check(presetPicker.IsEffectivelyVisible && presetPicker.ItemCount == 13 && !applyPreset.IsEnabled &&
                      presetStatus.Text?.Contains("全部关闭") == true, "驾驶辅助页应显示所有预设和默认全关组合");
                Check(view.FindControl<ComboBox>("AssistAuthority")?.ItemCount == 3 &&
                      view.FindControl<Expander>("AssistLiveAnalysis") is not null &&
                      view.FindControl<Expander>("AssistReplayPanel") is not null,
                    "驾驶辅助同页应有三档控制、实时诊断与离线回放");
                presetPicker.SelectedItem = model.DriveAssistPresets.Single(preset => preset.Label == "赛道抓地");
                Dispatcher.UIThread.RunJobs();
                Check(applyPreset.IsEnabled && assistChecks.All(check => check.IsChecked == false), "预览赛道预设不能立即切换勾选");
                Capture(window, "standalone-drive-assist.png");
                assistChecks[0].IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Check(!model.CanConnectDriveAssist, "主动输出前应确认离线练习");
                model.AssistOfflineConfirmed = true;
                Check(model.CanConnectDriveAssist && !model.IsDriveAssistArmed && !model.IsAutoShiftEnabled &&
                      preferences.Load().DriveAssist.Features.HasFlag(DriveAssistFeature.CounterSteer), "单项选择应保存，连接与换挡仍需单独开启");
                assistChecks[0].IsChecked = false;
                var gameAbs = view.FindControl<CheckBox>("UseGameAbs")!;
                var gameTcs = view.FindControl<CheckBox>("UseGameTractionControl")!;
                Check(gameAbs.IsEffectivelyVisible && gameAbs.IsChecked == false &&
                      gameTcs.IsEffectivelyVisible && gameTcs.IsChecked == false, "游戏辅助配合选项默认关闭");
                var abs = assistChecks.Single(check => check.DataContext is DriveAssistOption { Feature: DriveAssistFeature.Abs });
                var traction = assistChecks.Single(check => check.DataContext is DriveAssistOption { Feature: DriveAssistFeature.Traction });
                var corner = assistChecks.Single(check => check.DataContext is DriveAssistOption { Feature: DriveAssistFeature.CornerTraction });
                abs.IsChecked = traction.IsChecked = corner.IsChecked = true;
                gameAbs.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Check(!abs.IsEffectivelyEnabled && abs.IsChecked == true && traction.IsEffectivelyEnabled && corner.IsEffectivelyEnabled &&
                      preferences.Load().DriveAssist is { UseGameAbs: true, UseGameTractionControl: false },
                    "启用游戏 ABS 时保留自选制动项，但停用重叠控制，不影响牵引功能");
                gameTcs.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Check(!traction.IsEffectivelyEnabled && traction.IsChecked == true && corner.IsEffectivelyEnabled && corner.IsChecked == true &&
                      preferences.Load().DriveAssist is { UseGameAbs: true, UseGameTractionControl: true },
                    "启用游戏 TCS 时保留自选限滑项，但停用重叠控制，弯中限油仍可使用");
                gameAbs.IsChecked = gameTcs.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Check(abs.IsEffectivelyEnabled && abs.IsChecked == true && traction.IsEffectivelyEnabled && traction.IsChecked == true,
                    "退出游戏辅助配合后应恢复原有选项");
                abs.IsChecked = traction.IsChecked = corner.IsChecked = false;
                model.SelectedTab = (int)TelemetryTab.Experimental;
                Dispatcher.UIThread.RunJobs();
                var experimentChecks = view.GetVisualDescendants().OfType<CheckBox>()
                    .Where(check => check.IsEffectivelyVisible && check.DataContext is DriveAssistOption).ToArray();
                var tightTurn = experimentChecks.SingleOrDefault(check =>
                    check.DataContext is DriveAssistOption { Feature: DriveAssistFeature.TightTurnSteering, Label: "低速急弯保留转向" });
                Check(tabs.SelectedItem is TabItem { Header: "测试功能" } && tightTurn is { IsChecked: false } &&
                      assistChecks.Length + experimentChecks.Length == DriveAssistSettings.FeatureCount,
                    "低速急弯选项应出现在测试功能页，默认关闭");
                tightTurn!.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Check(preferences.Load().DriveAssist.Features == DriveAssistFeature.TightTurnSteering && model.CanConnectDriveAssist && !model.IsDriveAssistArmed,
                    "实验开关应持久化，并沿用驾驶辅助的手动接通流程");
                var tightTurnSpeed = view.FindControl<Slider>("TightTurnSpeedLimitSlider");
                var tightTurnAuto = view.FindControl<CheckBox>("TightTurnAutoSpeed");
                var tightTurnSpeedStatus = view.FindControl<TextBlock>("TightTurnSpeedStatus");
                Check(tightTurnSpeed is { IsEffectivelyVisible: true, Minimum: 20, Maximum: 90, Value: 54 } &&
                      tightTurnAuto is { IsEffectivelyVisible: true, IsChecked: false }, "实验页应显示默认 54 km/h 和手动/自动选择");
                tightTurnSpeed!.Value = 68;
                tightTurnAuto!.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Check(tightTurnSpeedStatus is { IsEffectivelyVisible: true, Text: { } speedStatus } &&
                      speedStatus.Contains("68 km/h"), "自动模式应展示未学习时的备用速度");
                Check(preferences.Load().DriveAssist is { TightTurnSpeedLimitKmh: 68, LearnTightTurnSpeed: true },
                    "手动速度与自动模式应分别保存");
                tightTurnAuto.IsChecked = false;
                tightTurn.IsChecked = false;
                model.SelectedTab = (int)TelemetryTab.AutomaticShift;
                Dispatcher.UIThread.RunJobs();
                var guide = view.FindControl<Expander>("BeginnerShiftGuide")!;
                guide.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                Check(guide.IsEffectivelyVisible && guide.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                    text.IsEffectivelyVisible && text.Text?.Contains("127.0.0.1") == true), "应能展开页内引导并查看 Data Out 设置");
                Check(guide.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible &&
                    text.Text is { } guideText && guideText.Contains("手动（带离合器）") && guideText.Contains("左摇杆按下（L3）")),
                    "新手引导应明确带离合器的手动模式和手柄 L3 离合绑定");
                Capture(window, "standalone-beginner-guide.png");
                var setupLink = view.FindControl<Button>("OpenVirtualControllerSetup")!;
                setupLink.Command!.Execute(setupLink.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                var routingPath = view.FindControl<TextBox>("VirtualControllerRoutingPath")!;
                Check(tabs.SelectedItem is TabItem { Header: "虚拟手柄配置" } &&
                      routingPath.IsEffectivelyVisible && routingPath.Text == input.RoutingPath && routingPath.IsReadOnly,
                    "应能从引导打开独立配置页并复制实际运行路径");
                Check(window.GetVisualDescendants().OfType<HyperlinkButton>().Count(link =>
                    link.IsEffectivelyVisible && link.NavigateUri?.Scheme == "https") == 4, "配置页应提供驱动发布与排查文档入口");
                Capture(window, "standalone-virtual-controller.png");
                var back = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "返回自动换挡"));
                back.Command!.Execute(back.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Check(tabs.SelectedIndex == (int)TelemetryTab.AutomaticShift, "应能从手柄配置页返回自动换挡");
                guide.IsExpanded = false;
                view.FindControl<TextBox>("ShiftLatencyInput")!.Text = "abc";
                model.EnableAutoShiftCommand.Execute(null);
                Check(!model.IsAutoShiftEnabled && model.AutoShiftSettingsError.Contains("整数毫秒"), "非整数换挡延迟应显示错误");
                view.FindControl<TextBox>("ShiftLatencyInput")!.Text = "260";
                var toggle = view.FindControl<Button>("HudToggleButton")!;
                Check(view.Hud is { IsVisible: true } && Equals(toggle.Content, "关闭 HUD"), "独立助手启动时应显示 HUD");
                var hud = view.Hud!;
                Check(hud.IsVisible && hud.Topmost && !hud.ShowInTaskbar && !hud.ShowActivated &&
                      (hud.Background as ISolidColorBrush)?.Color == Colors.Transparent &&
                      hud.TransparencyLevelHint.Contains(WindowTransparencyLevel.Transparent) &&
                      !model.Hud.Live.HasData && model.Hud.Live.Status == "等待遥测", "HUD 应透明置顶并等待驾驶数据");
                CheckOutlineContrastHelper();

                var panel = hud.GetVisualDescendants().OfType<TelemetryHudPanel>().Single();
                var autoStatus = panel.FindControl<TextBlock>("HudAutoShiftStatus")!;
                var autoShortcut = panel.FindControl<TextBlock>("HudAutoShiftShortcut")!;
                var assistStatus = panel.FindControl<TextBlock>("HudDriveAssistStatus")!;
                var learnedSpeed = panel.FindControl<TextBlock>("HudLearnedTightTurnSpeed");
                Check(learnedSpeed is { IsEffectivelyVisible: false }, "未学习时，HUD 应隐藏急弯上限提示");
                var heightWithoutLearnedSpeed = hud.Height;
                model.Hud.Live = model.Hud.Live with { LearnedTightTurnSpeedText = "急弯上限 · 66 km/h" };
                Dispatcher.UIThread.RunJobs();
                Check(learnedSpeed is { IsEffectivelyVisible: true, Text: "急弯上限 · 66 km/h" } && hud.Height > heightWithoutLearnedSpeed,
                    "学到急弯上限后，实况 HUD 应显示数值并给提示留出空间");
                model.Hud.ShowLearnedTightTurnSpeed = false;
                Dispatcher.UIThread.RunJobs();
                Check(!learnedSpeed!.IsEffectivelyVisible && assistStatus.IsEffectivelyVisible && hud.Height == heightWithoutLearnedSpeed,
                    "关闭急弯上限提醒只应收起提示行");
                model.Hud.ShowLearnedTightTurnSpeed = true;
                model.Hud.Live = model.Hud.Live with { LearnedTightTurnSpeedText = "" };
                var transmissionStatus = panel.FindControl<TextBlock>("HudTransmissionStatus")!;
                var hudProtectionRow = panel.FindControl<Border>("HudProtectionRow")!;
                var dynamicBadge = panel.FindControl<Border>("HudDynamicProtectionBadge")!;
                var dynamicText = panel.FindControl<TextBlock>("HudDynamicProtectionText")!;
                Check(hudProtectionRow.IsEffectivelyVisible && model.Hud.ShowProtectionStatus &&
                      model.Hud.Live.Protections is { Airborne: HudProtectionLevel.Armed, Launch: HudProtectionLevel.Armed } &&
                      !dynamicBadge.IsEffectivelyVisible, "HUD 应默认显示已开启的起步防滑和滞空防护");
                Check(autoStatus.IsEffectivelyVisible && autoStatus.Text == "自动换挡启动失败" && autoShortcut.IsEffectivelyVisible &&
                      autoShortcut.Text == "快捷键 F9" && assistStatus.IsEffectivelyVisible && assistStatus.Text == "驾驶辅助 关",
                      "HUD 应默认显示换挡状态、默认快捷键和驾驶辅助状态，参数填错时应能看出启动失败");
                Check(transmissionStatus.IsEffectivelyVisible && transmissionStatus.Text == "变速箱待校准 · 自动离合 开",
                      "智能识别默认开启，接收遥测前就应显示待校准");
                var initialHudHeight = hud.Height;
                model.AutoShiftAutoDetectRaceTransmission = false;
                model.Hud.ShowAutoShiftStatus = false;
                Dispatcher.UIThread.RunJobs();
                Check(!transmissionStatus.IsEffectivelyVisible && !autoStatus.IsEffectivelyVisible && !autoShortcut.IsEffectivelyVisible &&
                      hud.Height < initialHudHeight, "关闭 HUD 换挡状态时应同时收起对应快捷键");
                model.Hud.ShowAutoShiftStatus = true;
                Dispatcher.UIThread.RunJobs();
                Check(model.AutoShiftProtectAirborne && model.AutoShiftStrictSpeedMatching &&
                      model.AutoShiftProtectLaunch && !model.AutoShiftProtectReengagement &&
                      !model.AutoShiftAdaptiveMatching && !model.AutoShiftDriftMode && model.AutoShiftShortcutEnabled,
                    "起步防滑、滞空保护、严格匹配和快捷键应默认开启");
                model.AutoShiftToggleKeyboard = "F10";
                model.AutoShiftToggleGamepad = model.ShiftShortcutChoices.Single(choice => choice.Button == ShiftShortcutButton.RightStick);
                model.AutoShiftClutchToggleKeyboard = "F11";
                model.DriftExitKeyboard = "F13";
                model.AutoShiftClutchToggleGamepad = model.ShiftShortcutChoices.Single(choice => choice.Button == ShiftShortcutButton.DpadDown);
                model.SelectedTab = (int)TelemetryTab.Shortcuts;
                Dispatcher.UIThread.RunJobs();
                Check(tabs.SelectedItem is TabItem { Header: "快捷键" } && view.FindControl<Grid>("ShortcutBindings")!.IsEffectivelyVisible &&
                      view.FindControl<ComboBox>("DriveAssistShortcutButton")!.IsEffectivelyVisible, "快捷键页应单独显示所有动作和手柄绑定");
                model.AutoShiftShortcutEnabled = false;
                view.FindControl<TextBox>("HudShortcutKey")!.Text = "f12";
                view.FindControl<TextBox>("StartRecordingShortcutKey")!.Text = "F6";
                view.FindControl<TextBox>("StopRecordingShortcutKey")!.Text = "F7";
                view.FindControl<TextBox>("DriveAssistShortcutKey")!.Text = "f8";
                model.DriveAssistToggleGamepad = model.ShiftShortcutChoices.Single(choice => choice.Button == ShiftShortcutButton.LeftBumper);
                model.HudToggleGamepad = model.ShiftShortcutChoices.Single(choice => choice.Button == ShiftShortcutButton.LeftStick);
                model.StartRecordingGamepad = model.ShiftShortcutChoices.Single(choice => choice.Button == ShiftShortcutButton.DpadLeft);
                Dispatcher.UIThread.RunJobs();
                model.SaveAutoShiftShortcutCommand.Execute(null);
                Check(preferences.Load().AutomaticShift is
                      {
                          ToggleKeyboardKey: "F10",
                          ToggleGamepadButton: ShiftShortcutButton.RightStick,
                          DriveAssistToggleKeyboardKey: "F8",
                          DriveAssistToggleGamepadButton: ShiftShortcutButton.LeftBumper,
                          ClutchToggleKeyboardKey: "F11",
                          DriftExitKeyboardKey: "F13",
                          ClutchToggleGamepadButton: ShiftShortcutButton.DpadDown,
                          HudToggleKeyboardKey: "F12",
                          HudToggleGamepadButton: ShiftShortcutButton.LeftStick,
                          StartRecordingKeyboardKey: "F6",
                          StartRecordingGamepadButton: ShiftShortcutButton.DpadLeft,
                          StopRecordingKeyboardKey: "F7",
                          EnableShiftShortcut: true,
                      },
                    "各项键盘和手柄快捷键应能保存并立即启用");
                Dispatcher.UIThread.RunJobs();
                Check(autoShortcut.Text == "快捷键 F10 / R3", "HUD 应使用刚保存的实际绑定");
                Capture(window, "standalone-shortcuts.png");
                input.Key = 0x78;
                model.PollShiftShortcut();
                Check(!model.IsAutoShiftEnabled, "更改快捷键后原 F9 不再有效");
                model.AutoShiftShortcutEnabled = false;
                var readsBeforeDisable = input.ShortcutReads;
                input.Key = 0x79;
                model.PollShiftShortcut();
                Check(input.ShortcutReads == readsBeforeDisable && !model.IsAutoShiftEnabled, "未开启快捷键时不应读取键盘或手柄绑定");
                model.AutoShiftShortcutEnabled = true;
                input.Key = 0;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();
                input.Key = 0x79;
                input.IsGameInForeground = false;
                model.PollShiftShortcut();
                Check(!model.IsAutoShiftEnabled, "游戏失焦时不响应快捷键");
                input.IsGameInForeground = true;
                model.PollShiftShortcut();
                Check(!model.IsAutoShiftEnabled, "切回游戏时按住的旧输入不能触发");
                input.Key = 0;
                model.PollShiftShortcut();
                input.Key = 0x79;
                WaitUntil(() => model.IsListening && model.IsAutoShiftEnabled && !model.IsConnectingAutoShift,
                    "F10 应启动接收器和自动换挡", model.PollShiftShortcut);
                Check(preferences.Load().AutomaticShift.ShiftLatencyMs == 260 && model.AutoShiftDetailText.Contains("手动换挡延迟 260 ms"),
                    "填写的延迟应在启用时保存并用于实时状态");
                model.PollShiftShortcut();
                Dispatcher.UIThread.RunJobs();
                Check(autoStatus.Text == "自动换挡 开", "持续按住按键应只切换一次");
                window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = (int)TelemetryTab.AutomaticShift;
                Dispatcher.UIThread.RunJobs();
                var shiftStrategy = view.FindControl<ComboBox>("ShiftStrategyPicker")!;
                var expectedModes = new[]
                {
                    "换挡模式1 · 轮上扭矩对比",
                    "换挡模式2 · 实测动力衰减",
                    "换挡模式3 · 踏板与驾驶模式自适应",
                    "换挡模式4 · 断油与功率交点预测",
                    "换挡模式5 · 动力区间与高转保挡",
                };
                var forbiddenNames = new[] { "LazyForza", "Virtual TCU", "FH6 Auto Shift" };
                Check(shiftStrategy.ItemCount == expectedModes.Length, "换挡策略列表应包含 5 个换挡模式");
                var strategyHelp = view.FindControl<TextBlock>("ShiftStrategyHelp")!;
                var descriptions = new HashSet<string>();
                foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
                {
                    var index = (int)algorithm;
                    var itemText = (shiftStrategy.Items[index] as ComboBoxItem)?.Content?.ToString();
                    Check(itemText == expectedModes[index] && forbiddenNames.All(name => !itemText.Contains(name, StringComparison.OrdinalIgnoreCase)),
                        $"第 {index + 1} 个策略应显示「{expectedModes[index]}」且不含项目名");
                    shiftStrategy.SelectedIndex = index;
                    Dispatcher.UIThread.RunJobs();
                    Check(strategyHelp.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(strategyHelp.Text) &&
                          descriptions.Add(strategyHelp.Text) &&
                          forbiddenNames.All(name => !strategyHelp.Text.Contains(name, StringComparison.OrdinalIgnoreCase)),
                        $"{algorithm} 应显示自己的简短说明且不含项目名");
                }
                Check(guide.GetVisualDescendants().OfType<TextBlock>().All(text =>
                    string.IsNullOrEmpty(text.Text) || forbiddenNames.All(name => !text.Text.Contains(name, StringComparison.OrdinalIgnoreCase))),
                    "新手引导正文不应包含项目名");
                var clutchToggle = view.FindControl<CheckBox>("AutomaticClutchToggle")!;
                var raceDetectToggle = view.FindControl<CheckBox>("AutoDetectRaceTransmissionToggle")!;
                var airborneToggle = view.FindControl<CheckBox>("AirborneProtectionToggle")!;
                Check(airborneToggle.IsChecked == true && airborneToggle.Parent is WrapPanel protectionRow &&
                      protectionRow.Children.IndexOf(airborneToggle) > 0 &&
                      protectionRow.Children[protectionRow.Children.IndexOf(airborneToggle) - 1] is CheckBox { Content: "起步打滑保护" },
                    "滞空保护应默认开启，紧挨起步打滑保护");
                var strictToggle = view.FindControl<CheckBox>("StrictSpeedMatchingToggle")!;
                Check(strictToggle.IsChecked == true && strictToggle.Parent is WrapPanel strictRow &&
                      strictRow.Children.IndexOf(strictToggle) > 0 && strictRow.Children[strictRow.Children.IndexOf(strictToggle) - 1] == airborneToggle,
                    "严格车速匹配应默认开启，紧挨滞空保护");
                strictToggle.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Check(model.IsAutoShiftEnabled && !preferences.Load().AutomaticShift.StrictSpeedGearMatching, "运行中关闭严格匹配应立即保存");
                airborneToggle.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Check(model.IsAutoShiftEnabled && !preferences.Load().AutomaticShift.ProtectAirborne, "运行中关闭滞空保护应立即保存");
                airborneToggle.IsChecked = true;
                Check(clutchToggle.IsChecked == true && raceDetectToggle.IsChecked == false, "测试中手动关闭赛用识别后可编辑离合");
                shiftStrategy.SelectedIndex = (int)ShiftAlgorithm.Mode5;
                clutchToggle.IsChecked = false;
                raceDetectToggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Check(model.IsAutoShiftEnabled && preferences.Load().AutomaticShift is
                      { Algorithm: ShiftAlgorithm.Mode5, AutomaticClutch: false, AutoDetectRaceTransmission: true },
                    "可以在自动换挡开启时选择换挡模式5，且能独立保存离合与变速箱识别设置");
                Check(transmissionStatus.Text == "变速箱待校准 · 自动离合 开",
                    "HUD 应显示识别功能实际接管的自动离合状态");
                shiftStrategy.SelectedIndex = (int)ShiftAlgorithm.LazyForza;
                clutchToggle.IsChecked = true;
                raceDetectToggle.IsChecked = false;
                model.AutoShiftProtectLaunch = true;
                model.AutoShiftProtectReengagement = true;
                model.AutoShiftAdaptiveMatching = true;
                model.AutoShiftUpshiftGripProtection = true;
                Dispatcher.UIThread.RunJobs();
                Check(model.IsAutoShiftEnabled && preferences.Load().AutomaticShift is
                      { ProtectLaunchWheelspin: true, ProtectShiftReengagement: true, AdaptiveGearMatching: true, UpshiftGripProtection: true },
                    "驾驶时可分别打开并保存各项保护和自适应");
                Check(model.Hud.Live.Protections is
                      { Launch: HudProtectionLevel.Armed, Airborne: HudProtectionLevel.Armed,
                        UpshiftGrip: HudProtectionLevel.Armed, Reengagement: HudProtectionLevel.Armed },
                    "开启多项防护后 HUD 防护徽章应立即变为武装状态");
                model.AutoShiftProtectLaunch = model.AutoShiftProtectReengagement = model.AutoShiftAdaptiveMatching =
                    model.AutoShiftUpshiftGripProtection = false;
                model.AutoShiftDriftMode = true;
                Dispatcher.UIThread.RunJobs();
                Check(autoStatus.Text == "自动换挡 开 · 漂移" && preferences.Load().AutomaticShift.DriftMode, "HUD 应实时显示独立漂移模式");
                model.AutoShiftDriftMode = false;
                input.Key = 0;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();
                input.Button = ShiftShortcutButton.RightStick;
                WaitUntil(() => !model.IsAutoShiftEnabled, "手柄 R3 应关闭自动换挡", model.PollShiftShortcut);
                Dispatcher.UIThread.RunJobs();
                Check(autoStatus.Text == "自动换挡 关", "手柄关闭后 HUD 应立即更新");
                input.Button = ShiftShortcutButton.None;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();
                input.Button = ShiftShortcutButton.RightStick;
                WaitUntil(() => model.IsAutoShiftEnabled && !model.IsConnectingAutoShift,
                    "再次按 R3 应开启自动换挡", model.PollShiftShortcut);
                input.Button = ShiftShortcutButton.None;

                // 自动离合快捷键：键盘 F11 / 手柄方向键 ↓，并验证焦点保护和退出智能变速箱识别
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();
                input.Key = 0x7A; // F11
                model.PollShiftShortcut();
                model.PollShiftShortcut();
                Dispatcher.UIThread.RunJobs();
                Check(!model.AutoShiftAutomaticClutch && !preferences.Load().AutomaticShift.AutomaticClutch &&
                      input.LastClutchSetting == false && autoStatus.Text == "自动换挡 开 · 无离合",
                    "按 F11 应立即关闭自动离合并同步到底层和 HUD，长按不重复切换");
                input.Key = 0;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();

                input.IsGameInForeground = false;
                input.Button = ShiftShortcutButton.DpadDown;
                model.PollShiftShortcut();
                Check(!model.AutoShiftAutomaticClutch, "游戏失焦时不响应自动离合快捷键");
                input.IsGameInForeground = true;
                model.PollShiftShortcut();
                Check(!model.AutoShiftAutomaticClutch, "切回游戏时持续按住的离合快捷键不应误触发");
                input.Button = ShiftShortcutButton.None;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();

                input.Button = ShiftShortcutButton.DpadDown;
                model.PollShiftShortcut();
                Dispatcher.UIThread.RunJobs();
                Check(model.AutoShiftAutomaticClutch && preferences.Load().AutomaticShift.AutomaticClutch &&
                      input.LastClutchSetting == true && autoStatus.Text == "自动换挡 开",
                    "手柄方向键 ↓ 应重新开启自动离合");
                input.Button = ShiftShortcutButton.None;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();

                raceDetectToggle.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                input.Key = 0x7A;
                model.PollShiftShortcut();
                Dispatcher.UIThread.RunJobs();
                Check(!model.AutoShiftAutoDetectRaceTransmission && !model.AutoShiftAutomaticClutch &&
                      autoStatus.Text == "自动换挡 开 · 无离合",
                    "开启智能识别时按离合快捷键应退出智能识别并切换到手动关闭离合");
                input.Key = 0;
                for (var i = 0; i < 3; i++) model.PollShiftShortcut();
                model.AutoShiftAutomaticClutch = true;
                Dispatcher.UIThread.RunJobs();

                using var sender = new UdpClient();
                var packet = new byte[Fh6Telemetry.PacketLength];
                BinaryPrimitives.WriteInt32LittleEndian(packet, 1);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), 8000);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), 800);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16), 4500);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(212), 2740);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(220), 685);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(256), 20);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(260), 185000);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(264), 340);
                packet[315] = 204; // 80% throttle
                packet[316] = 102; // 40% brake
                packet[319] = 10;
                packet[320] = 64; // steering right ~50%
                uint timestamp = 0;
                void SendPacket()
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), timestamp += 20);
                    sender.Send(packet, new IPEndPoint(IPAddress.Loopback, port));
                }
                WaitUntil(() => model.HasData, "独立窗口应能显示 UDP 遥测", SendPacket);
                Check(model.SpeedValue == 72 && model.CarText.Contains("阿巴斯"), "实测页应显示速度和车型");
                Check(model.Hud.Live.HasData && panel.FindControl<TextBlock>("HudSpeed")?.Text == model.SpeedText &&
                      panel.FindControl<TextBlock>("HudGear")?.Text == "10" &&
                      panel.FindControl<TextBlock>("HudRpm")?.Text == "4500 rpm", "HUD 应显示实时车速、十挡和转速");
                Check(panel.FindControl<TextBlock>("HudVehicleName") is { IsEffectivelyVisible: true, Text: "阿巴斯 124 Spider" } &&
                      panel.FindControl<TextBlock>("HudVehiclePi") is { IsEffectivelyVisible: true, Text: "PI 685" },
                      "HUD 应并列显示厂商、车型和实测评分");
                var pedalsGrid = panel.FindControl<Grid>("HudPedals")!;
                var throttleBar = panel.FindControl<Border>("HudThrottleBar")!;
                var brakeBar = panel.FindControl<Border>("HudBrakeBar")!;
                Check(pedalsGrid.IsEffectivelyVisible &&
                      panel.FindControl<TextBlock>("HudThrottlePercent")?.Text == "80%" &&
                      panel.FindControl<TextBlock>("HudBrakePercent")?.Text == "40%" &&
                      throttleBar.Height > brakeBar.Height && brakeBar.Height > 0 &&
                      panel.FindControl<Border>("HudDetailRow")?.IsEffectivelyVisible == true,
                    "HUD 应同时展示双竖条油门/刹车电量样式百分比及细节条");
                var powertrain = panel.FindControl<TextBlock>("HudPowertrainText")!;
                for (var drivetrain = 0; drivetrain < 3; drivetrain++)
                {
                    var driveCode = new[] { "FWD", "RWD", "AWD" }[drivetrain];
                    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(224), drivetrain);
                    WaitUntil(() => powertrain.IsEffectivelyVisible &&
                        powertrain.Text == $"功率 185.0 kW / 251.5 PS · 扭矩 340.0 N·m · {driveCode}",
                        $"HUD 应并列显示两种功率单位、扭矩和实时驱动形式 {driveCode}", SendPacket);
                }
                Capture(hud, "standalone-driving-hud.png");
                CheckHudOpacityAndOutlines(panel, model.Hud, 0.94);
                var originalOpacity = model.Hud.OpacityPercent;
                model.Hud.OpacityPercent = 30;
                Dispatcher.UIThread.RunJobs();
                CheckHudOpacityAndOutlines(panel, model.Hud, 0.30);
                Capture(hud, "standalone-driving-hud-low-opacity.png");
                model.Hud.OpacityPercent = 0;
                Dispatcher.UIThread.RunJobs();
                CheckHudOpacityAndOutlines(panel, model.Hud, 0.0);
                Capture(hud, "standalone-driving-hud-zero-opacity.png");
                model.Hud.OpacityPercent = originalOpacity;
                model.Hud.EnableTextOutline = false;
                Dispatcher.UIThread.RunJobs();
                CheckHudOpacityAndOutlines(panel, model.Hud, originalOpacity / 100.0);
                Capture(hud, "standalone-driving-hud-no-outline.png");
                model.Hud.EnableTextOutline = true;
                Dispatcher.UIThread.RunJobs();
                packet[318] = 200; // handbrake held -> 离合/手刹
                WaitUntil(() => dynamicBadge.IsEffectivelyVisible && dynamicText.Text == "离合/手刹",
                    "拉手刹时 HUD 防护栏应亮起离合/手刹提示", SendPacket);
                packet[318] = 0;
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24), -11f); // airborne
                WaitUntil(() => model.Hud.Live.Protections.Airborne == HudProtectionLevel.Active && !dynamicBadge.IsEffectivelyVisible,
                    "飞坡滞空时 HUD 滞空防护徽章应高亮激活", SendPacket);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24), 0f);
                WaitUntil(() => model.Hud.Live.Protections.Airborne == HudProtectionLevel.Armed,
                    "恢复平地行驶后滞空防护应回到武装待命", SendPacket);
                packet[319] = 0;
                WaitUntil(() => model.GearText == "R", "HUD 应识别倒挡", SendPacket);
                model.SelectedTab = (int)TelemetryTab.Charts;
                Dispatcher.UIThread.RunJobs();
                Check(model.GearSeries.Count == 0 && view.FindControl<TelemetryChart>("GearChart")?.IsEffectivelyVisible == false,
                    "挡位曲线默认隐藏且不生成绘图数据");
                model.ShowGearChart = true;
                WaitUntil(() => model.GearSeries.SingleOrDefault() is { GearSteps: true } gears &&
                    gears.Values.Contains(10) && gears.Values.Contains(0), "挡位曲线应使用实际遥测的 10 挡和倒挡", SendPacket);
                Check(view.FindControl<TelemetryChart>("GearChart")?.IsEffectivelyVisible == true, "实时挡位曲线应可见");
                model.ShowGearChart = false;
                Dispatcher.UIThread.RunJobs();
                Check(model.GearSeries.Count == 0 && !preferences.Load().ShowGearChart, "关闭挡位曲线后应清除绘图数据并保存");

                model.DisableAutoShiftCommand.Execute(null);
                model.Hud.ShowAutoShiftStatus = false;
                model.AutoShiftAutoDetectRaceTransmission = true;
                packet[316] = 0;
                packet[319] = 1;
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(264), 340);
                WaitUntil(() => model.GearText == "1", "应先接收赛用车的一挡样本", SendPacket);
                packet[319] = 2;
                WaitUntil(() => transmissionStatus.Text == "赛用变速箱 · 自动离合 关", "赛用判定应显示在 HUD 上", SendPacket);
                Check(transmissionStatus.IsEffectivelyVisible && input.LastClutchSetting == false && model.AutoShiftAutomaticClutch,
                    "HUD 的离合开关应跟随实际判定，独立于手动离合选项和自动换挡状态行");
                Capture(hud, "standalone-transmission-hud.png");

                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(212), 2512);
                packet[319] = 1;
                WaitUntil(() => model.GearText == "1" && transmissionStatus.Text == "变速箱待校准 · 自动离合 开",
                    "换车后 HUD 应立即回到等待校准和自动离合开", SendPacket);
                packet[319] = 2;
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(260), 0);
                var torqueCut = System.Diagnostics.Stopwatch.StartNew();
                WaitUntil(() => model.GearText == "2" && torqueCut.ElapsedMilliseconds >= 340, "应收到常规车升挡的断动力样本", SendPacket);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(260), 185000);
                WaitUntil(() => transmissionStatus.Text == "常规变速箱 · 自动离合 开" && input.LastClutchSetting == true,
                    "识别常规变速箱后 HUD 应显示结果与开启的自动离合", SendPacket);
                model.AutoShiftAutoDetectRaceTransmission = false;

                input.Button = ShiftShortcutButton.DpadLeft;
                model.PollShiftShortcut();
                Check(model.IsDriveRecording && !model.IsAutoShiftEnabled, "手柄快捷键应能独立开始记录");
                var driveFolder = model.DriveRecordingFolder;
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(256), 25);
                WaitUntil(() => model.SpeedValue == 90, "记录期间应接收新的遥测", SendPacket);
                model.SelectedTab = (int)TelemetryTab.AllParameters;
                WaitUntil(() => model.ParameterGroups.Count > 0, "应能打开刷新中的参数页", SendPacket);
                var lateralSlipRow = model.ParameterGroups.SelectMany(group => group.Rows).Single(row => row.Name == TelemetryText.Get("PrmTireSlipAngle"));
                Check(lateralSlipRow.Unit.Length == 0 && lateralSlipRow.Source == TelemetryText.Get("SrcDirect"),
                    "Data Out 的轮胎侧向滑移是归一化读数，应原样显示");
                var speedRow = model.ParameterGroups.SelectMany(group => group.Rows).Single(row => row.Name == TelemetryText.Get("PrmSpeedKmh"));
                var oldSpeed = speedRow.Value;
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(256), 26);
                WaitUntil(() => speedRow.Value != oldSpeed, "应原位更新参数行，不重新创建表格", SendPacket);

                model.SelectedTab = (int)TelemetryTab.Settings;
                Dispatcher.UIThread.RunJobs();
                var clearCache = view.FindControl<Button>("ClearLocalCacheButton")!;
                var autoClear = view.FindControl<CheckBox>("AutoClearLocalCache")!;
                Check(clearCache.IsEffectivelyVisible && autoClear is { IsEffectivelyVisible: true, IsChecked: false }, "模块设置应提供手动清缓存和默认关闭的定时开关");
                clearCache.Command!.Execute(clearCache.CommandParameter);
                Check(model.SpeedSeries.Count == 0 && model.IsDriveRecording && Directory.Exists(driveFolder) &&
                      model.RetentionStatus.Contains("已清空本地缓存"), "手动清理应清空曲线但保持当前行驶记录");
                autoClear.IsChecked = true;
                model.SettingsCleanupInterval = "5";
                model.SaveSettingsCommand.Execute(null);
                Check(preferences.Load().Retention is { ClearCache: true, CleanupIntervalSeconds: 5, CleanHistory: false }, "应独立保存定时清缓存设置和间隔");
                model.SelectedTab = (int)TelemetryTab.Charts;
                WaitUntil(() => model.SpeedSeries.Any(series => series.Values.Length > 0), "清理后仍应继续绘制曲线", SendPacket);
                model.SelectedTab = (int)TelemetryTab.Settings;
                model.MaintainRetention(Environment.TickCount64 + 6000);
                Check(model.SpeedSeries.Count == 0 && model.IsDriveRecording && Directory.Exists(driveFolder), "定时清理也应保留当前行驶记录");
                model.SettingsClearCache = false;
                model.SaveSettingsCommand.Execute(null);
                input.Button = ShiftShortcutButton.None;
                input.Key = 0x76; // F7
                model.PollShiftShortcut();
                WaitUntil(() => !model.IsDriveRecording && !model.IsSavingDriveRecording, "结束记录快捷键应等待保存完成");
                Check(model.DriveRecordingStatus.StartsWith("已保存") && File.Exists(Path.Combine(driveFolder, "telemetry.jsonl.gz")),
                    "结束快捷键应保存行驶记录文件");
                input.Key = 0;
                WaitUntil(() => !model.Hud.Live.HasData && model.Hud.Live.Status == "遥测超时", "HUD 应停止显示过期遥测");
                CheckHudSettings(window, model, hud, preferences, port);
                input.Key = 0x7B; // F12
                model.PollShiftShortcut();
                model.PollShiftShortcut();
                Check(!hud.IsVisible && view.Hud is null && Equals(toggle.Content, "显示 HUD"), "长按 HUD 快捷键应只切换一次");
                input.Key = 0;
                for (var poll = 0; poll < 3; poll++) model.PollShiftShortcut();
                model.SelectedTab = (int)TelemetryTab.Overview;
                Capture(window, "standalone-overview.png");
                model.SelectedTab = (int)TelemetryTab.AutomaticShift;
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Capture(window, "standalone-shift-minimum.png");
                input.Button = ShiftShortcutButton.LeftStick;
                model.PollShiftShortcut();
                Check(view.Hud is { IsVisible: true } && Equals(toggle.Content, "关闭 HUD"), "手柄快捷键应能重新显示 HUD");
                window.Width = 1080;
                window.Height = 740;
                CheckLiveLocalization(window, view, model, preferences);
                window.Close();
                Check(view.Hud is null, "关闭独立应用应同时关闭 HUD");
            }
            finally { window.Close(); }

            Check(!model.IsListening && pad.Disposed, "关窗后应停止监听并断开虚拟手柄");
            using var reopened = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            CheckDriveAssistShortcut(Path.Combine(root.FullName, "assist-shortcut"));
            CheckRecordedShortcuts(Path.Combine(root.FullName, "recorded-shortcuts"));
            CheckDriveAssistPresets(Path.Combine(root.FullName, "assist-presets"));
            CheckAutoShiftStaysArmed(Path.Combine(root.FullName, "auto-shift-resume"));
            Console.WriteLine("PASS 独立窗口、HUD 换挡状态、键盘与手柄开关、挡位曲线和资源释放");
        }
        finally { root.Delete(recursive: true); }
    }

    private static void CheckAutoShiftStaysArmed(string folder)
    {
        int port;
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var preferences = new TelemetrySettingsFile(folder);
        var settings = new TelemetrySettings { Port = port };
        settings.AutomaticShift = settings.AutomaticShift with
        {
            GamepadMode = GamepadShiftMode.Combination, MaximumGear = 6, StrictSpeedGearMatching = false,
            AutoDetectRaceTransmission = false,
        };
        Check(preferences.Save(settings) is null, "应能保存测试换挡输出设置");
        var hardwareReady = 0;
        var attempts = 0;
        var pad = new ForwardingPad();
        ShortcutInput NewInput() => new(new GamepadShiftInput(() =>
        {
            Interlocked.Increment(ref attempts);
            if (Volatile.Read(ref hardwareReady) == 0) throw new InvalidOperationException("测试手柄暂未连接");
            return pad;
        }, () => true, () => false)) { BufferedDesktop = true };

        using var sender = new UdpClient();
        var packet = new byte[Fh6Telemetry.PacketLength];
        BinaryPrimitives.WriteInt32LittleEndian(packet, 1);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), 8000);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), 850);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16), 7985);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(212), 2740);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(256), 25);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(260), 240000);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(264), 287);
        packet[315] = 255;
        packet[319] = 2;
        uint stamp = 0;
        void Send()
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), stamp += 25);
            sender.Send(packet, new IPEndPoint(IPAddress.Loopback, port));
        }

        using (var model = new PerformanceTelemetryViewModel(preferences, NewInput()))
        {
            model.EnableAutoShiftCommand.Execute(null);
            WaitUntil(() => Volatile.Read(ref attempts) == 1 && !model.IsConnectingAutoShift, "应收到首次手柄连接失败");
            Check(model.IsAutoShiftEnabled && model.Hud.Live.AutomaticShiftStatus == "自动换挡 开 · 等待" && pad.UpshiftCount == 0,
                "连接失败时应显示开启并等待");
            Volatile.Write(ref hardwareReady, 1);
            WaitUntil(() => model.IsGamepadForwarding && pad.UpshiftCount == 1, "手柄恢复后应自动连接并在有效驾驶中换挡", Send);
            WaitUntil(() => model.Hud.Live.Status == "遥测超时", "应模拟赛事名单页上的遥测超时");
            Check(model.IsAutoShiftEnabled && model.Hud.Live.AutomaticShiftStatus == "自动换挡 开" && pad.UpshiftCount == 1,
                "赛事加载断流时 HUD 应保留开启并且不发额外指令");
            model.StopCommand.Execute(null);
            Check(model.IsAutoShiftEnabled && !model.IsListening && !model.IsGamepadForwarding, "停止接收须释放输出并保留开关");
        }

        var restoredInput = NewInput();
        using (var model = new PerformanceTelemetryViewModel(preferences, restoredInput))
        {
            Check(model.IsAutoShiftEnabled && !model.IsGamepadForwarding, "重启应记住开启，并等有效数据再连接手柄");
            WaitUntil(() => model.IsGamepadForwarding && pad.UpshiftCount == 2, "重启后应从有效遥测自动恢复换挡", Send);
            model.DisconnectGamepadRelayCommand.Execute(null);
            Check(model.IsAutoShiftEnabled && model.IsAutoShiftRelayPaused && !model.IsGamepadForwarding,
                "手动断开转发应保留开启选择并暂停输出");
            model.EnableAutoShiftCommand.Execute(null);
            WaitUntil(() => model.IsGamepadForwarding && pad.UpshiftCount == 3, "可从手动断开中恢复输出", Send);
            restoredInput.PressDesktop(0x78);
            WaitUntil(() => !model.IsAutoShiftEnabled, "F9 应能主动关闭自动换挡");
        }
        using var closed = new PerformanceTelemetryViewModel(preferences, NewInput());
        Check(!closed.IsAutoShiftEnabled && pad.UpshiftCount == 3, "重启应记住手动关闭");
    }

    private static void CheckDriveAssistPresets(string folder)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        var preferences = new TelemetrySettingsFile(folder);
        var initial = new DriveAssistSettings { TargetSpeedKmh = 87, LaunchRpmPercent = 61, HandbrakeButton = ShiftShortcutButton.Y };
        Check(preferences.Save(new TelemetrySettings { Port = port, DriveAssist = initial }) is null, "应保存初始辅助设置");
        var input = new ShortcutInput(new GamepadShiftInput(() => new ForwardingPad(), () => true, () => false));
        using var model = new PerformanceTelemetryViewModel(preferences, input);
        model.AssistOfflineConfirmed = true;

        var track = model.DriveAssistPresets.Single(preset => preset.Label == "赛道抓地");
        var drift = model.DriveAssistPresets.Single(preset => preset.Label == "漂移练习");
        var skidpad = model.DriveAssistPresets.Single(preset => preset.Label == "稳态定圆");
        var cruise = model.DriveAssistPresets.Single(preset => preset.Label == "定速巡航");
        var off = model.DriveAssistPresets.Single(preset => preset.Label == "全部关闭");
        Check(model.SelectedDriveAssistPreset == off && !model.HasDriveAssistSelections, "默认仍应全部关闭");
        model.SelectedDriveAssistPreset = track;
        Check(!model.HasDriveAssistSelections && model.CanApplyDriveAssistPreset && preferences.Load().DriveAssist == initial,
            "切换下拉选项只预览，不能改变已经保存的功能");
        model.ApplyDriveAssistPresetCommand.Execute(null);
        var trackFeatures = preferences.Load().DriveAssist.Features;
        var enabled = model.SteeringAssists.Concat(model.PedalAssists).Concat(model.SurfaceAssists).Concat(model.VehicleAssists);
        Check(!model.IsDriveAssistArmed && !model.IsListening && trackFeatures.HasFlag(DriveAssistFeature.TrailBrake) &&
              trackFeatures.HasFlag(DriveAssistFeature.CornerTraction) && !trackFeatures.HasFlag(DriveAssistFeature.DriftHold) &&
              enabled.Where(option => option.IsEnabled).Aggregate(DriveAssistFeature.None, (flags, option) => flags | option.Feature) == trackFeatures,
            "应用赛道预设应持久化并同步勾选，但不接通或开始监听");

        model.ArmDriveAssistCommand.Execute(null);
        WaitUntil(() => model.IsDriveAssistArmed, "应能接通已应用的赛道预设");
        var configureCalls = input.DriveAssistConfigureCalls;
        model.SelectedDriveAssistPreset = drift;
        model.ApplyDriveAssistPresetCommand.Execute(null);
        var driftFeatures = preferences.Load().DriveAssist.Features;
        Check(input.DriveAssistConfigureCalls == configureCalls + 1 && model.IsDriveAssistArmed &&
              driftFeatures == (DriveAssistFeature.DriftHold | DriveAssistFeature.Hairpin | DriveAssistFeature.Traction |
                  DriveAssistFeature.DriftTransitions | DriveAssistFeature.TransmissionAssist | DriveAssistFeature.Feedback),
            "切换整套预设应一次性替换已接通的功能，清掉会干扰漂移的抓地限制");

        model.SelectedDriveAssistPreset = skidpad;
        model.ApplyDriveAssistPresetCommand.Execute(null);
        Check(model.IsCruiseSelected && model.DriveAssistSkidpad && !model.IsCruiseBusy && model.DriveAssistTargetSpeed == 87,
            "定圆预设应选对模式，保留测试车速，不自动开始试验");
        model.SelectedDriveAssistPreset = cruise;
        model.ApplyDriveAssistPresetCommand.Execute(null);
        Check(model.IsCruiseSelected && !model.DriveAssistSkidpad && !model.IsCruiseBusy,
            "定速巡航应清掉定圆模式");
        model.VehicleAssists.Single(option => option.Feature == DriveAssistFeature.Thermal).IsEnabled = true;
        Check(model.DriveAssistPresetStatus.Contains("自定义"), "手动改动预设后应显示自定义组合");
        model.SelectedDriveAssistPreset = off;
        model.ApplyDriveAssistPresetCommand.Execute(null);
        Check(!model.IsDriveAssistArmed && !model.HasDriveAssistSelections &&
              preferences.Load().DriveAssist == initial with { TargetSlipPercent = 112 },
            "全部关闭应交还控制，保留油门转速、车速与手刹等参数");

        model.AssistOfflineConfirmed = false;
        model.SelectedDriveAssistPreset = model.DriveAssistPresets.Single(preset => preset.Label == "公平模式");
        model.ApplyDriveAssistPresetCommand.Execute(null);
        model.ArmDriveAssistCommand.Execute(null);
        Check(model.IsDriveAssistArmed && preferences.Load().DriveAssist.ReadOnly &&
              input.TryShift(ShiftDirection.Up, new AutomaticShiftSettings()) != ShiftInputResult.Sent,
            "公平模式可启动只读采集，不能发出自动换挡");
    }

    private static void CheckRecordedShortcuts(string folder)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        socket.Close();
        var preferences = new TelemetrySettingsFile(folder);
        Check(preferences.Save(new TelemetrySettings { Port = port }) is null, "应保存默认 F9");
        var input = new ShortcutInput(new GamepadShiftInput(() => new ForwardingPad(), () => true, () => false))
        {
            BufferedDesktop = true,
            IsGameInForeground = false,
        };
        using var model = new PerformanceTelemetryViewModel(preferences, input);
        input.PressDesktop(0x78);
        input.IsGameInForeground = true;
        Dispatcher.UIThread.RunJobs();
        Check(!model.IsAutoShiftEnabled && !model.IsConnectingAutoShift, "其他程序中按的 F9 切回游戏时不能触发");
        input.PressDesktop(0x78);
        WaitUntil(() => model.IsAutoShiftEnabled, "即便轮询从未看到按住，游戏内 F9 按下事件仍应开启自动换挡");
        input.IsGameInForeground = false;
        input.IsShortcutEditorInForeground = true;
        model.SelectedTab = (int)TelemetryTab.Shortcuts;
        input.PressDesktop(0x78);
        WaitUntil(() => !model.IsAutoShiftEnabled, "快捷键页也应能试按自动换挡的功能键");

        model.AutoShiftShortcutEnabled = false;
        model.RecordShortcutCommand.Execute(ShortcutAction.AutoShift);
        input.PressDesktop(0x61);
        WaitUntil(() => !model.IsRecordingShortcut, "键盘原始事件应完成录制");
        Check(model.AutoShiftToggleKeyboard == "NumPad1" && !model.IsAutoShiftEnabled,
            "总开关停用时仍可录到小键盘 1，录制期间不能触发换挡");
        model.RecordShortcutCommand.Execute(ShortcutAction.Hud);
        input.PressDesktop(0x05);
        Dispatcher.UIThread.RunJobs();
        Check(model.IsRecordingShortcut, "必须在录制区域内点击鼠标");
        model.SetShortcutCapturePointer(true);
        input.PressDesktop(0x05);
        WaitUntil(() => !model.IsRecordingShortcut, "鼠标侧键应完成录制");
        Check(model.HudToggleKeyboard == "Mouse4", "第一颗鼠标侧键应录到 Mouse4");

        input.Button = ShiftShortcutButton.LeftBumper;
        model.RecordShortcutCommand.Execute(ShortcutAction.AutoShift);
        model.PollShiftShortcut();
        Check(model.IsRecordingShortcut, "录制开始前已按住的手柄键应忽略");
        input.Button = ShiftShortcutButton.LeftBumper | ShiftShortcutButton.DpadRight | ShiftShortcutButton.DpadUp;
        model.PollShiftShortcut();
        Check(model.IsRecordingShortcut, "同帧两个新手柄键应忽略，避免把斜向方向键录错");
        input.Button = ShiftShortcutButton.RightStick;
        model.PollShiftShortcut();
        Check(!model.IsRecordingShortcut && model.AutoShiftToggleGamepad?.Button == ShiftShortcutButton.RightStick,
            "应识别到 R3，并写入手柄列");
        model.RecordShortcutCommand.Execute(ShortcutAction.DriveAssist);
        input.PressDesktop(0x1B);
        WaitUntil(() => !model.IsRecordingShortcut, "Escape 应取消录制");
        Check(model.DriveAssistToggleKeyboard == "", "取消后不可修改绑定");
        model.SaveAutoShiftShortcutCommand.Execute(null);
        Check(preferences.Load().AutomaticShift is { ToggleKeyboardKey: "NumPad1", HudToggleKeyboardKey: "Mouse4",
            ToggleGamepadButton: ShiftShortcutButton.RightStick }, "录制的键鼠和手柄绑定应保存并可重启恢复");
        var hudTriggers = 0;
        model.HudToggleRequested += () => hudTriggers++;
        input.IsGameInForeground = true;
        input.PressDesktop(0x05);
        WaitUntil(() => hudTriggers == 1, "保存后的鼠标绑定应走真实快捷键触发路径");
    }

    private static void CheckDriveAssistShortcut(string folder)
    {
        int port;
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var settings = new TelemetrySettings { Port = port, DriveAssist = new() { Features = DriveAssistFeature.Damper } };
        settings.AutomaticShift = settings.AutomaticShift with
        {
            DriveAssistToggleKeyboardKey = "F8", DriveAssistToggleGamepadButton = ShiftShortcutButton.LeftBumper,
        };
        var preferences = new TelemetrySettingsFile(folder);
        Check(preferences.Save(settings) is null, "应能写入驾驶辅助快捷键配置");
        var pad = new ForwardingPad();
        var input = new ShortcutInput(new GamepadShiftInput(() => pad, () => true, () => false));
        using var model = new PerformanceTelemetryViewModel(preferences, input);
        model.AssistOfflineConfirmed = true;
        model.PollShiftShortcut();
        input.IsGameInForeground = false;
        input.Key = 0x77;
        model.PollShiftShortcut();
        input.IsGameInForeground = true;
        model.PollShiftShortcut();
        Check(!model.IsDriveAssistArmed && !model.IsConnectingDriveAssist, "切回游戏时持续按住 F8 不应接通驾驶辅助");
        input.Key = 0;
        model.PollShiftShortcut();
        input.Key = 0x77;
        model.PollShiftShortcut();
        WaitUntil(() => model.IsDriveAssistArmed, "松开一次再按 F8 应独立接通驾驶辅助");
        model.PollShiftShortcut();
        Check(model.IsDriveAssistArmed && !model.IsAutoShiftEnabled && model.Hud.Live.DriveAssistStatus == "驾驶辅助 · 等待",
            "长按只能触发一次，HUD 应随辅助接通更新");
        input.Key = 0;
        model.PollShiftShortcut();
        input.Button = ShiftShortcutButton.LeftBumper;
        model.PollShiftShortcut();
        Check(!model.IsDriveAssistArmed && input.IsForwarding && model.Hud.Live.DriveAssistStatus == "驾驶辅助 关",
            "手柄快捷键关闭驾驶辅助时应保留虚拟手柄转发");

        input.Button = ShiftShortcutButton.None;
        model.AutoShiftModeIndex = 0;
        using var sender = new UdpClient();
        var packet = new byte[Fh6Telemetry.PacketLength];
        BinaryPrimitives.WriteInt32LittleEndian(packet, 1);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), 8000);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), 800);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16), 800);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(212), 2740);
        uint ms = 0;
        void SendFrame(byte gear, byte brake)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), ms += 25);
            packet[319] = gear;
            packet[316] = brake;
            sender.Send(packet, new IPEndPoint(IPAddress.Loopback, port));
        }
        model.CheckShiftOutputCommand.Execute(null);
        WaitUntil(() => model.HasData && model.IsCheckingShiftOutput, "自检应等待满足条件的实时报文", () => SendFrame(1, 0));
        Check(pad.UpshiftCount == 0, "未踩刹车时不能发出自检查挡");
        WaitUntil(() => pad.UpshiftCount > 0, "一挡静止且踩住刹车时应试发一次升挡", () => SendFrame(1, 140));
        WaitUntil(() => !model.IsCheckingShiftOutput, "自检应等待二挡的实际遥测确认", () => SendFrame(2, 140));
        Check(pad.UpshiftCount == 1 && model.ShiftOutputCheckStatus.Contains("一挡升到二挡") && !model.IsAutoShiftEnabled,
            "自检成功应仅发一次，不开启连续自动换挡");

        model.ArmDriveAssistCommand.Execute(null);
        WaitUntil(() => model.IsDriveAssistArmed, "应重新接通驾驶辅助");
        input.SimulatedLearnedTightTurnSpeedKmh = 66;
        WaitUntil(() => model.Hud.Live.HasData && model.Hud.Live.LearnedTightTurnSpeedText == "急弯上限 · 66 km/h",
            "HUD 应在实况遥测刷新后保留已学到的上限", () => SendFrame(2, 0));
        input.SimulatedLearnedTightTurnSpeedKmh = 72;
        WaitUntil(() => model.Hud.Live.LearnedTightTurnSpeedText == "急弯上限 · 72 km/h",
            "HUD 应跟随学到的新上限更新", () => SendFrame(2, 0));
        input.SimulatedLearnedTightTurnSpeedKmh = null;
        WaitUntil(() => !model.Hud.Live.HasLearnedTightTurnSpeed,
            "清除学习值后应隐藏 HUD 提示", () => SendFrame(2, 0));
        model.AutoShiftShortcutEnabled = false;
        model.PollShiftShortcut();
        input.Key = 0x13;
        model.PollShiftShortcut();
        Check(!model.IsDriveAssistArmed && !model.IsAutoShiftEnabled && model.IsListening && input.IsForwarding &&
              model.DriveAssistError.Contains("已停止辅助输出"), "关闭常规快捷键后仍可急停，遥测与实体转发继续工作");
    }

    private static void CheckHudSettings(Window window, PerformanceTelemetryViewModel model, Window hud,
        TelemetrySettingsFile preferences, int port)
    {
        model.Hud.ShowAutoShiftStatus = true;
        model.SelectedTab = (int)TelemetryTab.HudSettings;
        Capture(window, "standalone-hud-settings.png");
        var page = window.GetVisualDescendants().OfType<TelemetryHudSettingsView>().Single();
        var example = page.FindControl<TelemetryHudPanel>("ExamplePanel")!;
        var outlineToggle = page.FindControl<CheckBox>("HudTextOutlineToggle")!;
        Check(outlineToggle.IsChecked == true && model.Hud.EnableTextOutline, "HUD 文字高对比度描边开关应默认勾选");
        var learnedSpeedToggle = page.FindControl<CheckBox>("HudLearnedTightTurnSpeedToggle");
        var exampleLearnedSpeed = example.FindControl<TextBlock>("HudLearnedTightTurnSpeed");
        Check(learnedSpeedToggle is { IsChecked: true } && example.Height > model.Hud.PanelHeight &&
              exampleLearnedSpeed is { IsEffectivelyVisible: true, Text: "急弯上限 · 66 km/h" },
            "急弯上限提示应默认开启，并在设置页预览");
        CheckHudOpacityAndOutlines(example, model.Hud, model.Hud.OpacityFactor);
        var savedOpacity = model.Hud.OpacityPercent;
        model.Hud.OpacityPercent = 30;
        Dispatcher.UIThread.RunJobs();
        CheckHudOpacityAndOutlines(example, model.Hud, 0.30);
        Capture(window, "standalone-hud-settings-low-opacity.png");
        model.Hud.OpacityPercent = savedOpacity;
        Dispatcher.UIThread.RunJobs();
        var map = page.FindControl<Canvas>("ScreenMap")!;
        var marker = page.FindControl<Border>("MapMarker")!;
        Check(example.FindControl<TextBlock>("HudShiftHint")?.Text == "↑ 5 挡" &&
              model.Hud.SelectedDisplay is not null &&
              Equals(page.FindControl<ComboBox>("DisplayPicker")?.SelectedItem, model.Hud.SelectedDisplay),
            "设置页应提供独立示例和显示器选择");

        var initialPosition = hud.Position;
        var start = marker.TranslatePoint(new Point(marker.Bounds.Width / 2, marker.Bounds.Height / 2), window)!.Value;
        var end = map.TranslatePoint(new Point(map.Bounds.Width * 0.22, map.Bounds.Height * 0.26), window)!.Value;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Check(model.Hud.HorizontalPercent is < 35 and > 0 && model.Hud.VerticalPercent is < 40 and > 0 &&
              hud.Position != initialPosition, "拖动设置预览应立即移动 HUD");

        model.Hud.ScalePercent = 125;
        model.Hud.OpacityPercent = 62;
        model.Hud.ShowRpm = false;
        model.Hud.ShowPedals = false;
        model.Hud.ShowTelemetryDetails = false;
        model.Hud.ShowProtectionStatus = false;
        model.Hud.ShowVehicleIdentity = false;
        model.Hud.ShowDriveAssistStatus = false;
        Dispatcher.UIThread.RunJobs();
        Check(exampleLearnedSpeed!.IsEffectivelyVisible, "急弯上限提示应独立于驾驶辅助状态");
        learnedSpeedToggle!.IsChecked = false;
        outlineToggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Check(!model.Hud.EnableTextOutline, "取消勾选 HUD 设置中的描边开关应同步到视图模型");
        CheckHudOpacityAndOutlines(example, model.Hud, 0.62);
        var connected = model.Hud.Displays.Where(display => display.Screen is not null).ToArray();
        var display = connected.First();
        model.Hud.SelectedDisplay = display;
        model.Hud.UpdateDisplays([]);
        Check(model.Hud.Options.DisplayId == display.Id && model.Hud.ActiveScreen == model.Hud.MainScreen,
            "选中的显示器断开后应暂用主程序屏幕并保留原选择");
        model.Hud.UpdateDisplays(connected);
        Check(model.Hud.SelectedDisplay == display && model.Hud.ActiveScreen == display.Screen,
            "显示器重连后应恢复原来的显示器");
        Dispatcher.UIThread.RunJobs();
        var settings = model.Hud.Options;
        Check(Math.Abs(hud.Width - model.Hud.PreviewWidth) < 1 && Math.Abs(hud.Height - model.Hud.PreviewHeight) < 1,
            $"自定义尺寸应立即更新，实际 {hud.Width} × {hud.Height}，期望 {model.Hud.PreviewWidth} × {model.Hud.PreviewHeight}");
        Check(example.FindControl<TextBlock>("HudRpm")?.IsEffectivelyVisible == false &&
              example.FindControl<Grid>("HudPedals")?.IsEffectivelyVisible == false &&
              example.FindControl<Border>("HudDetailRow")?.IsEffectivelyVisible == false &&
              example.FindControl<Border>("HudProtectionRow")?.IsEffectivelyVisible == false &&
              example.FindControl<TextBlock>("HudVehicleName")?.IsEffectivelyVisible == false &&
              example.FindControl<TextBlock>("HudDriveAssistStatus")?.IsEffectivelyVisible == false &&
              !exampleLearnedSpeed.IsEffectivelyVisible,
            "关闭转速、踏板竖条、细节栏与防护状态栏应从预览中移除对应模块");
        model.Hud.SaveCommand.Execute(null);
        Check(preferences.Load().Hud == settings && preferences.Load().Port == port && !model.Hud.HasChanges,
            "保存 HUD 应保留位置、外观和原有遥测配置");

        model.Hud.ShowSpeed = model.Hud.ShowGear = false;
        model.Hud.SaveCommand.Execute(null);
        Check(preferences.Load().Hud == settings && model.Hud.SaveStatus.Contains("至少"),
            "全部隐藏时应提示保留至少一项");
        model.Hud.RestoreDefaultsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check(model.Hud.Options == new TelemetryHudOptions() && outlineToggle.IsChecked == true && hud.Position == initialPosition,
            "恢复默认应回到原 HUD 位置与外观");
        model.Hud.SaveCommand.Execute(null);
    }

    private static void CheckLiveLocalization(TelemetryWindow window, PerformanceTelemetryView view,
        PerformanceTelemetryViewModel model, TelemetrySettingsFile preferences)
    {
        var languagePicker = view.FindControl<ComboBox>("LanguagePicker")!;
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        var hud = view.Hud!;
        var panel = hud.GetVisualDescendants().OfType<TelemetryHudPanel>().Single();
        var toggle = view.FindControl<Button>("HudToggleButton")!;
        var shiftStrategy = view.FindControl<ComboBox>("ShiftStrategyPicker")!;
        var previousShowAutoShift = model.Hud.ShowAutoShiftStatus;
        var previousShowProtection = model.Hud.ShowProtectionStatus;
        model.Hud.ShowAutoShiftStatus = true;
        model.Hud.ShowProtectionStatus = true;
        Dispatcher.UIThread.RunJobs();

        try
        {
            var cases = new[]
            {
                (Index: 1, Code: "en", Title: "FH6 Telemetry Companion", FirstTab: "Auto-Shift",
                    CloseHud: "Close HUD", SpeedLbl: "Speed",
                    Mode1: "Shift Mode 1 · Wheel Torque Comparison", SaveHud: "Save HUD Settings", CheckNoHan: true),
                (Index: 2, Code: "ja", Title: "FH6 テレメトリ・アシスタント", FirstTab: "自動シフト",
                    CloseHud: "HUD 閉じる", SpeedLbl: "車速",
                    Mode1: "シフトモード1 · 駆動輪トルク比較", SaveHud: "HUD 設定を保存", CheckNoHan: false),
                (Index: 3, Code: "ko", Title: "FH6 텔레메트리 어시스턴트", FirstTab: "자동 변속",
                    CloseHud: "HUD 끄기", SpeedLbl: "차속",
                    Mode1: "변속 모드 1 · 휠 토크 비교", SaveHud: "HUD 설정 저장", CheckNoHan: true),
            };

            foreach (var c in cases)
            {
                tabs.SelectedIndex = (int)TelemetryTab.AutomaticShift;
                languagePicker.SelectedIndex = c.Index;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                hud.UpdateLayout();

                Check(window.Title == c.Title, $"切换到 {c.Code} 后窗口标题应为 {c.Title}，实际={window.Title}");
                Check(tabs.Items[0] is TabItem { Header: string h0 } && h0 == c.FirstTab,
                    $"切换到 {c.Code} 后首个标签页应为 {c.FirstTab}，实际={(tabs.Items[0] as TabItem)?.Header}");
                AssertTabHeadersNotClipped(tabs, c.Code);
                Check(Equals(toggle.Content, c.CloseHud),
                    $"切换到 {c.Code} 后 HUD 开关按钮应为 {c.CloseHud}，实际={toggle.Content}");
                Check(shiftStrategy.Items[0] is ComboBoxItem { Content: string item0 } && item0 == c.Mode1,
                    $"切换到 {c.Code} 后策略1 应为 {c.Mode1}，实际={(shiftStrategy.Items[0] as ComboBoxItem)?.Content}");
                var selectedBox = shiftStrategy.SelectionBoxItem?.ToString();
                Check(selectedBox == c.Mode1 || (shiftStrategy.SelectionBoxItem as ComboBoxItem)?.Content?.ToString() == c.Mode1,
                    $"切换到 {c.Code} 后下拉框当前选中文本应刷新为 {c.Mode1}，实际={selectedBox}");
                Check(preferences.Load().Language == c.Code, $"切换到 {c.Code} 后设置应持久化语言代码");
                Check(panel.GetVisualDescendants().OfType<OutlinedTextBlock>().Any(t => t.Text == c.SpeedLbl),
                    $"切换到 {c.Code} 后 HUD 车速标签应为 {c.SpeedLbl}");

                if (c.CheckNoHan) AssertNoHanLeak(window, hud, model.SettingsStorePath, c.Code);
                Capture(window, $"standalone-shift-{c.Code}.png");

                tabs.SelectedIndex = (int)TelemetryTab.DriveAssist;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Check(tabs.SelectedItem is TabItem { Header: string assistHeader } && assistHeader == TelemetryText.Get("TabDriveAssist") &&
                      window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == TelemetryText.Get("EcuCounterSteer")),
                    $"切换到 {c.Code} 后标签页和驾驶辅助开关应立即刷新语言");
                if (c.CheckNoHan) AssertNoHanLeak(window, hud, model.SettingsStorePath, c.Code);

                tabs.SelectedIndex = (int)TelemetryTab.HudSettings;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, c.SaveHud)),
                    $"切换到 {c.Code} 后 HUD 设置保存按钮应为 {c.SaveHud}");
                if (c.CheckNoHan) AssertNoHanLeak(window, hud, model.SettingsStorePath, c.Code);
                Capture(window, $"standalone-hud-{c.Code}.png");
            }

            tabs.SelectedIndex = (int)TelemetryTab.AutomaticShift;
            languagePicker.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertTabHeadersNotClipped(tabs, "zh-CN");
            Check(window.Title == "FH6 遥测助手" &&
                  tabs.Items[0] is TabItem { Header: string zhHeader } && zhHeader == "自动换挡" &&
                  Equals(toggle.Content, "关闭 HUD") &&
                  preferences.Load().Language == "zh-CN",
                "切回简体中文后界面、HUD 与配置应完整还原");
        }
        finally
        {
            model.Hud.ShowAutoShiftStatus = previousShowAutoShift;
            model.Hud.ShowProtectionStatus = previousShowProtection;
            TelemetryText.SetLanguage(TelemetryLanguage.ZhCn);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertTabHeadersNotClipped(TabControl tabs, string code)
    {
        var firstTabY = ((TabItem)tabs.Items[0]!).Bounds.Y;
        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            Check(Math.Abs(tab.Bounds.Y - firstTabY) < 2,
                $"切换到 {code} 后顶部标签栏应单行排布不换行（{tab.Header} Y={tab.Bounds.Y} vs {firstTabY}）");
            var header = tab.Header?.ToString() ?? "";
            var label = tab.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(tb => tb.Text == header);
            Check(label is not null && label.Bounds.Width > 0,
                $"切换到 {code} 后未找到标签文本控件：{header}");
            var formatted = new FormattedText(
                header,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(label!.FontFamily, label.FontStyle, label.FontWeight),
                label.FontSize,
                Brushes.White);
            Check(label.Bounds.Width + 1.0 >= formatted.Width,
                $"切换到 {code} 后标签文本被裁剪：{header} actualWidth={label.Bounds.Width:0.1} required={formatted.Width:0.1}");
        }
    }

    private static void AssertNoHanLeak(Window window, Window hud, string storePath, string code)
    {
        static bool ContainsHan(string? text) => !string.IsNullOrEmpty(text) && text.Any(ch => ch is >= '\u4e00' and <= '\u9fff');

        foreach (var root in new Visual[] { window, hud })
        {
            foreach (var tab in root.GetVisualDescendants().OfType<TabItem>())
            {
                var header = tab.Header?.ToString();
                Check(!ContainsHan(header), $"{code} 模式下标签头不应残留汉字：{header}");
            }
            foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                if (text.Text is "简体中文" or "日本語") continue;
                if (text.Text == storePath) continue;
                Check(!ContainsHan(text.Text), $"{code} 模式下可见文本不应残留汉字：{text.Text}");
            }
        }
    }

    private static void CheckOutlineContrastHelper()
    {
        foreach (var hex in new[] { "#F5F8FF", "#B4C7DE", "#FFB86B", "#FF9AA6", "#7AF2B0", "#8CE6FF", "#95A6BA", "#9ED8FF", "#FFCB6B" })
        {
            var fg = Color.Parse(hex);
            var stroke = OutlinedTextBlock.ComputeHighContrastStrokeColor(fg);
            Check(stroke.R < 32 && OutlinedTextBlock.ContrastRatio(fg, stroke) >= 7.0,
                $"亮色 HUD 文本 {hex} 应选用深色高对比描边");
        }

        foreach (var hex in new[] { "#000000", "#121B2A", "#1E2B3D", "#243447" })
        {
            var fg = Color.Parse(hex);
            var stroke = OutlinedTextBlock.ComputeHighContrastStrokeColor(fg);
            Check(stroke.R > 220 && OutlinedTextBlock.ContrastRatio(fg, stroke) >= 10.0,
                $"深色前景 {hex} 应选用浅色高对比描边");
        }

        var darkAnchor = Color.FromRgb(5, 8, 15);
        var lightAnchor = Color.FromRgb(248, 251, 255);
        foreach (var hex in new[] { "#CC2020", "#1E6B3A", "#2B5CE0" })
        {
            var fg = Color.Parse(hex);
            var stroke = OutlinedTextBlock.ComputeHighContrastStrokeColor(fg);
            var expectedMax = Math.Max(OutlinedTextBlock.ContrastRatio(fg, darkAnchor), OutlinedTextBlock.ContrastRatio(fg, lightAnchor));
            Check(OutlinedTextBlock.ContrastRatio(fg, stroke) >= expectedMax - 0.05,
                $"中间色 {hex} 应选择 WCAG 对比度更高的描边色");
        }
    }

    private static void CheckHudOpacityAndOutlines(TelemetryHudPanel panel, TelemetryHudViewModel hudModel, double expectedBackdropOpacity)
    {
        var backdrop = panel.FindControl<Border>("HudPanelBackdrop")!;
        Check(Math.Abs(backdrop.Opacity - expectedBackdropOpacity) < 1e-6 &&
              Math.Abs(hudModel.OpacityFactor - expectedBackdropOpacity) < 1e-6,
            $"HUD 背景透明度应为 {expectedBackdropOpacity:0.00}，实际 {backdrop.Opacity:0.00}");

        var texts = panel.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => !text.GetVisualAncestors().OfType<ProgressBar>().Any())
            .ToArray();
        Check(texts.Length > 0 && texts.All(text => text is OutlinedTextBlock),
            "HUD 面板内所有文本控件都应启用高对比度描边");

        foreach (var outlined in texts.Cast<OutlinedTextBlock>())
        {
            Check(outlined.IsOutlineEnabled == hudModel.EnableTextOutline,
                $"HUD 文本 {outlined.Name ?? outlined.Text} 描边开关应与 HUD 设置保持一致");
            Check(outlined.Opacity == 1.0, $"HUD 文本 {outlined.Name ?? outlined.Text} 自身不透明度必须保持 1.0");
            for (Visual? current = outlined; current is not null; current = current.GetVisualParent())
                Check(current.Opacity == 1.0,
                    $"HUD 文本 {outlined.Name ?? outlined.Text} 不得被半透明祖先 {current.GetType().Name} 淡化");

            var fg = outlined.Foreground is ISolidColorBrush solid ? solid.Color : Colors.White;
            var stroke = outlined.ResolveStrokeColor();
            Check(OutlinedTextBlock.ContrastRatio(fg, stroke) >= 7.0,
                $"HUD 文本 {outlined.Name ?? outlined.Text} 描边对比度应 >= 7.0");
        }
    }

    private static void WaitUntil(Func<bool> done, string message, Action? tick = null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && deadline.ElapsedMilliseconds < 5000)
        {
            tick?.Invoke();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Check(done(), message);
    }

    private static void CheckAboutTab(Window window, PerformanceTelemetryViewModel model, TabControl tabs)
    {
        var tab = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "关于"));
        tabs.SelectedItem = tab;
        Dispatcher.UIThread.RunJobs();
        var page = (AboutView)tab.Content!;
        var about = (AboutViewModel)page.DataContext!;
        Check(page.IsEffectivelyVisible && about.VersionText.StartsWith('v'), "关于页应显示独立程序版本");
        var guideLink = page.FindControl<HyperlinkButton>("AboutGuideLink");
        if (!about.HasRepository)
        {
            Check(!about.CheckForUpdatesCommand.CanExecute(null) && page.GetVisualDescendants().OfType<HyperlinkButton>()
                .All(link => link.NavigateUri is null && !link.IsEffectivelyEnabled || !link.IsEffectivelyVisible),
                "仓库留空时，检查更新和项目链接应停用");
        }
        else
        {
            Check(about.CheckForUpdatesCommand.CanExecute(null) &&
                  guideLink is { IsEffectivelyEnabled: true, NavigateUri: { } guide } &&
                  guide == new Uri($"{about.ProjectPage}/blob/HEAD/docs/user-guide.md"),
                "配置仓库后，关于页应开放检查更新并链接中文使用指南");
        }

        Capture(window, "standalone-about.png");
        model.SelectedLanguageIndex = (int)TelemetryLanguage.En;
        Dispatcher.UIThread.RunJobs();
        Check(Equals(tab.Header, "About"), "关于标签应随界面切换语言");
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Capture(window, "standalone-about-en-minimum.png");
        if (about.HasRepository)
        {
            foreach (var (language, locale) in new[] { (TelemetryLanguage.En, "en"), (TelemetryLanguage.Ja, "ja"), (TelemetryLanguage.Ko, "ko") })
            {
                model.SelectedLanguageIndex = (int)language;
                Check(model.Hud.Live.Status == TelemetryText.Get("HudWaitingTelemetry"),
                    $"切换语言后，应立即翻译等待遥测的 HUD 状态。当前状态 {model.Hud.Live.Status}");
                Dispatcher.UIThread.RunJobs();
                Check(guideLink!.NavigateUri == new Uri($"{about.ProjectPage}/blob/HEAD/docs/{locale}/user-guide.md"),
                    "关于页的使用指南链接应随应用语言切换");
            }
        }
        window.Width = 1080;
        window.Height = 740;
        model.SelectedLanguageIndex = (int)TelemetryLanguage.ZhCn;
        tabs.SelectedIndex = (int)TelemetryTab.AutomaticShift;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string filename)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var output = Path.Combine(AppContext.BaseDirectory, "ui-preview");
        Directory.CreateDirectory(output);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(output, filename), new PngBitmapEncoderOptions());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ShortcutInput(GamepadShiftInput output) : IAutomaticShiftInput
    {
        public bool IsGameInForeground { get; set; } = true;
        public byte Key { get; set; }
        public ShiftShortcutButton Button { get; set; }
        public string RoutingPath => @"C:\Telemetry Test\Runtime\FH6Telemetry.exe";
        public int ShortcutReads { get; private set; }
        public bool? LastClutchSetting { get; private set; }
        public void SetAutomaticClutch(bool enabled)
        {
            LastClutchSetting = enabled;
            output.SetAutomaticClutch(enabled);
        }
        public bool BufferedDesktop { get; set; }
        public bool IsShortcutEditorInForeground { get; set; }
        public event Action<byte>? ShortcutKeyPressed;
        public void PressDesktop(byte key) => ShortcutKeyPressed?.Invoke(key);
        public bool BuffersShortcutKey(byte key) => BufferedDesktop;
        public bool IsShortcutKeyDown(byte key)
        {
            ShortcutReads++;
            return Key != 0 && Key == key;
        }
        public ushort ReadShortcutButtons() => (ushort)Button;
        public string? Error => output.Error;
        public bool IsForwarding => output.IsForwarding;
        public int? SimulatedLearnedTightTurnSpeedKmh { get; set; }
        public int DriveAssistConfigureCalls { get; private set; }
        public bool ConfigureDriveAssist(DriveAssistSettings? settings)
        {
            DriveAssistConfigureCalls++;
            return output.ConfigureDriveAssist(settings);
        }
        public DriveAssistStatus GetDriveAssistStatus(bool includeReports)
        {
            var status = output.GetDriveAssistStatus(includeReports);
            return SimulatedLearnedTightTurnSpeedKmh is int speed ? status with { LearnedTightTurnSpeedKmh = speed } : status;
        }
        public void SetDriveAssistReadOnly(bool readOnly) => output.SetDriveAssistReadOnly(readOnly);
        public ShiftGamepadReport? PhysicalDriveReport => output.PhysicalDriveReport;
        public void ExitDriveAssistDrift() => output.ExitDriveAssistDrift();
        public Task<string?> ConnectAsync(CancellationToken cancellationToken) => output.ConnectAsync(cancellationToken);
        public Task<string?> ConnectAsync(AutomaticShiftSettings settings, CancellationToken cancellationToken) =>
            output.ConnectAsync(settings, cancellationToken);
        public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings) => output.TryShift(direction, settings);
        public void Release() => output.Release();
        public void Disconnect() => output.Disconnect();
        public void Dispose() => output.Dispose();
    }

    private sealed class ForwardingPad : IShiftGamepad
    {
        private int _upshiftCount;
        public int UpshiftCount => Volatile.Read(ref _upshiftCount);
        public bool ForwardsPhysicalInput => true;
        public void Submit(ShiftGamepadReport report)
        {
            if ((report.Buttons & (ushort)ShiftShortcutButton.B) != 0) Interlocked.Increment(ref _upshiftCount);
        }
        public void Dispose() { }
    }
}
