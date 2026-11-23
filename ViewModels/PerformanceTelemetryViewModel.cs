using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FH6Telemetry.Views.Controls;
using Avalonia.Media;

namespace FH6Telemetry.ViewModels;

public enum TelemetryTab { AutomaticShift, DriveAssist, Shortcuts, Overview, AllParameters, Tests, Charts, Compare, History, Diagnostics, Settings, HudSettings, Experimental, VirtualControllerSetup }

/// <summary>全部参数页的一行：值、单位与数据来源标注（直接采集 / 计算 / 估算 / 暂不支持）。</summary>
public sealed class ParameterRow(string name, string value, string unit, string source, string? detail) : ObservableObject
{
    private string _value = value, _unit = unit, _source = source;
    private string? _detail = detail;

    public string Name { get; } = name;
    public string Value => _value;
    public string Unit => _unit;
    public string Source => _source;
    public string? Detail => _detail;

    internal void Update(ParameterRow row)
    {
        SetProperty(ref _value, row.Value, nameof(Value));
        SetProperty(ref _unit, row.Unit, nameof(Unit));
        SetProperty(ref _source, row.Source, nameof(Source));
        SetProperty(ref _detail, row.Detail, nameof(Detail));
    }
}

public sealed record ParameterGroup(string Title, IReadOnlyList<ParameterRow> Rows);

/// <summary>某个测试项目的历史成绩汇总。</summary>
public sealed record TestSummary(string Key, string Title, string Best, string Last, string Average, int Count);

public sealed partial class ShiftShortcutChoice : ObservableObject
{
    public ShiftShortcutButton Button { get; }
    private readonly string? _fixedLabel;
    private readonly string? _key;

    public ShiftShortcutChoice(ShiftShortcutButton button, string label, bool isKey = false)
    {
        Button = button;
        if (isKey) _key = label;
        else _fixedLabel = label;
    }

    public string Label => _key is not null ? TelemetryText.Get(_key) : _fixedLabel!;

    internal void NotifyLabelChanged() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// 车辆性能实测模块：遥测接收、实时仪表、测试引擎、曲线、调校对比、历史记录、诊断与设置。
/// 所有展示值来自官方 Data Out 数据或明确公式；不支持的数据显示“暂不支持”，绝不填充假值。
/// </summary>
public partial class PerformanceTelemetryViewModel : ObservableObject, IDisposable
{
    private readonly object _sync = new();
    private readonly TelemetrySettingsFile _preferences;
    private readonly TelemetrySettings _settings;
    private readonly TelemetryReceiver _receiver;
    private readonly AutomaticShiftController _autoShift;
    private readonly IAutomaticShiftInput _shiftInput;
    private readonly TelemetryStore _store;
    private readonly PerformanceTestEngine _engine;
    private readonly TelemetryDerived _derived = new();
    private readonly TelemetryRingBuffer _ringBuffer = new(TelemetryRetention.MaximumFrames);
    private readonly TelemetrySampleBuffer _sessionSamples = new();
    private readonly Timer _slowTimer;
    private readonly DispatcherTimer _shiftShortcutTimer;
    private DriveRecorder? _driveRecorder;
    private Task? _driveFlush;

    private TelemetrySample? _latest;
    private TelemetryDerived? _latestDerived;
    private long _lastSampleTicks;
    private long _lastArrivalAt;
    private long _lastPostedRefreshAt;
    private int _sampleRefreshQueued;
    private int _slowRefreshQueued;
    private int _disposed;
    private long _lastChartRefreshAt;
    private long _lastParameterRefreshAt;
    private long _nextCleanupAt;
    private int _chartRevision;
    private int _drawnChartRevision = -1;
    private int _drawnChartWindow;
    private Task<int>? _historyCleanup;
    private Task? _recordWrite;
    private bool _refreshingHistory;
    private bool _hasStoppedSession;
    private long _nextAutoShiftReconnectAt;
    private bool _hasSnapshotSaved;
    private bool _hasComparisonGenerated;
    private bool _timedOutNotified;
    private (int Ordinal, int Pi, int Drive, int Cylinders, TelemetryLanguage Language)? _carHeaderKey;
    private string _compactVehicleName = "";
    private string _allVehiclesLabel = TelemetryText.Get("AllVehicles");
    private double _sessionSeconds;
    private int _lastCar = -1;
    private Dictionary<string, double> _bestByKey = new();
    private Dictionary<string, (double Last, double Sum, int Count)> _historyByKey = new();
    private readonly List<TestResult> _pendingRecords = new();
    private AutomaticShiftSnapshot? _previousShiftSnapshot;

    public string PageTitle => TelemetryText.Get("PageTitle");
    public string PageSubtitle => TelemetryText.Get("PageSubtitle");
    public IReadOnlyList<string> LanguageOptions { get; } = ["简体中文", "English", "日本語", "한국어"];
    internal bool SuppressIndexSideEffects { get; set; }

    public IReadOnlyList<TestDefinition> AvailableTests { get; }
    public ObservableCollection<TestDefinition> EnabledTests { get; } = new();
    public ObservableCollection<TestSummary> TestSummaries { get; } = new();
    public ObservableCollection<ParameterGroup> ParameterGroups { get; } = new();
    public ObservableCollection<TestResult> HistoryRecords { get; } = new();
    public ObservableCollection<TestResult> CompareOptions { get; } = new();
    public ObservableCollection<string> CompareRows { get; } = new();
    public ObservableCollection<string> HistoryVehicles { get; } = new();

    [RelayCommand]
    private void NavigateToTab(TelemetryTab tab) => SelectedTab = (int)tab;

    // ===== 状态与诊断 =====
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private int _selectedLanguageIndex;
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _connectionStateText = "";
    [ObservableProperty] private string _carText = "";
    [ObservableProperty] private string _carDetailText = "—";
    [ObservableProperty] private string _carSourceText = "";
    public TelemetryHudViewModel Hud { get; }

    // ===== 实时总览 =====
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _speedValueText = "—";
    [ObservableProperty] private string _rpmValueText = "—";
    [ObservableProperty] private string _peakSpeedText = "—";
    [ObservableProperty] private string _rpmText = "—";
    [ObservableProperty] private string _gearText = "—";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _torqueText = "—";
    [ObservableProperty] private string _peakPowerText = "—";
    [ObservableProperty] private string _peakTorqueText = "—";
    [ObservableProperty] private string _longitudinalGText = "—";
    [ObservableProperty] private string _lateralGText = "—";
    [ObservableProperty] private string _combinedGText = "—";
    [ObservableProperty] private string _throttleText = "—";
    [ObservableProperty] private string _brakeText = "—";
    [ObservableProperty] private string _steerText = "—";
    [ObservableProperty] private string _zeroToHundredText = "";
    [ObservableProperty] private string _drivingStateText = "—";
    [ObservableProperty] private string _inputStateText = "";

    // 仪表原始值
    [ObservableProperty] private double _speedValue;
    [ObservableProperty] private double _speedMaximum = 320;
    [ObservableProperty] private double _rpmValue;
    [ObservableProperty] private double _rpmMaximum = 8000;
    [ObservableProperty] private double _rpmRedline;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private double _throttleValue;
    [ObservableProperty] private double _brakeValue;

    // ===== 曲线 =====
    [ObservableProperty] private int _chartWindowSeconds = 30;
    [ObservableProperty] private bool _chartPaused;
    [ObservableProperty] private bool _showGearChart;
    public IReadOnlyList<ChartSeries> SpeedSeries { get; private set; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<ChartSeries> GearSeries { get; private set; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<ChartSeries> PowerTorqueSeries { get; private set; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<ChartSeries> AccelerationSeries { get; private set; } = Array.Empty<ChartSeries>();
    public IReadOnlyList<ChartSeries> InputSeries { get; private set; } = Array.Empty<ChartSeries>();
    public event Action? ChartsChanged;
    internal event Action? HudToggleRequested;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartDriveRecording))]
    private bool _isDriveRecording;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartDriveRecording))]
    private bool _isSavingDriveRecording;
    [ObservableProperty] private string _driveRecordingStatus = "";
    [ObservableProperty] private string _driveRecordingFolder = "";

    public bool CanStartDriveRecording => !IsDriveRecording && !IsSavingDriveRecording;
    private string DriveRecordingRoot => Path.Combine(_store.RootPath, "drives");

    // ===== 测试 =====
    [ObservableProperty] private string _testStateText = "";
    [ObservableProperty] private string _activeTestText = "";
    [ObservableProperty] private TestResult? _selectedResult;
    [ObservableProperty] private string _customAccelerationRange = "0-200";
    [ObservableProperty] private string _customBrakingSpeed = "100";
    [ObservableProperty] private string _customDistanceMeters = "500";

    // ===== 历史与对比 =====
    [ObservableProperty] private string _historyFilter = "";
    [ObservableProperty] private string _historyVehicleFilter = "";
    [ObservableProperty] private string _historyInfoText = "";
    [ObservableProperty] private TestResult? _compareBase;
    [ObservableProperty] private TestResult? _compareTarget;
    [ObservableProperty] private string _compareSummary = "";
    [ObservableProperty] private string _snapshotInfoText = "";

    /// <summary>实时诊断快照（供测试与故障排查，直接读取接收器计数器，不依赖 UI 刷新）。</summary>
    public string DiagnosticSnapshot =>
        $"pkts={_receiver.PacketsReceived} acc={_receiver.PacketsAccepted} fail={_receiver.ParseFailures} " +
        $"run={_receiver.IsRunning} len={_receiver.LastPacketLength} err={_receiver.LastError} ep={_receiver.ListenEndpoint}";

    // ===== 诊断 =====
    [ObservableProperty] private string _diagnosticsText = "";
    [ObservableProperty] private string _diagnosticsDetail = "";

    // ===== 设置 =====
    [ObservableProperty] private string _settingsAddress = "127.0.0.1";
    [ObservableProperty] private string _settingsPort = "20440";
    [ObservableProperty] private string _settingsSpeedUnit = "km/h";
    [ObservableProperty] private string _settingsPowerUnit = "kW";
    [ObservableProperty] private string _settingsTorqueUnit = "N·m";
    [ObservableProperty] private string _settingsRefreshHz = "30";
    [ObservableProperty] private string _settingsSmoothing = "25";
    [ObservableProperty] private string _settingsMaxTestSeconds = "180";
    [ObservableProperty] private string _settingsStorePath = "";
    [ObservableProperty] private bool _settingsAutoSave = true;
    [ObservableProperty] private string _settingsStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustomRetention))] private int _settingsRetentionPresetIndex = (int)TelemetryRetentionPreset.Balanced;
    [ObservableProperty] private string _settingsRetentionSeconds = "";
    [ObservableProperty] private string _settingsRetentionFrames = "";
    [ObservableProperty] private string _settingsCleanupInterval = "";
    [ObservableProperty] private bool _settingsClearCache;
    [ObservableProperty] private bool _settingsCleanHistory;
    [ObservableProperty] private string _settingsHistoryDays = "";
    [ObservableProperty] private string _retentionStatus = "";

    public bool IsCustomRetention => SettingsRetentionPresetIndex == (int)TelemetryRetentionPreset.Custom;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditAutoShift)), NotifyPropertyChangedFor(nameof(CanCheckShiftOutput))] private bool _isAutoShiftEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditAutoShift)), NotifyPropertyChangedFor(nameof(CanConnectDriveAssist)),
        NotifyPropertyChangedFor(nameof(CanCheckShiftOutput))] private bool _isConnectingAutoShift;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutoShiftModeHelp)), NotifyPropertyChangedFor(nameof(IsKeyboardShiftMode)),
        NotifyPropertyChangedFor(nameof(ShowAutoShiftRoutingPath))] private int _autoShiftModeIndex;
    [ObservableProperty] private bool _isGamepadForwarding;
    [ObservableProperty] private bool _isAutoShiftRelayPaused;
    [ObservableProperty] private string _autoShiftPressDuration = "";
    [ObservableProperty] private string _autoShiftLatency = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutoShiftModeHelp))]
    private bool _autoShiftAutomaticClutch = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutoShiftModeHelp))]
    private bool _autoShiftAutoDetectRaceTransmission;
    [ObservableProperty] private bool _autoShiftUpshiftGripProtection;
    [ObservableProperty] private bool _autoShiftProtectLaunch;
    [ObservableProperty] private bool _autoShiftProtectAirborne = true;
    [ObservableProperty] private bool _autoShiftProtectReengagement;
    [ObservableProperty] private bool _autoShiftAdaptiveMatching;
    [ObservableProperty] private bool _autoShiftStrictSpeedMatching;
    [ObservableProperty] private bool _autoShiftDriftMode;
    [ObservableProperty] private bool _autoShiftShortcutEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsVirtualTcuStrategy)),
        NotifyPropertyChangedFor(nameof(AutoShiftStrategyHelp))] private int _autoShiftAlgorithmIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VirtualTcuModeHelp))] private int _autoShiftVirtualTcuModeIndex;
    [ObservableProperty] private string _autoShiftMaximumGear = "";
    [ObservableProperty] private string _autoShiftKeyboardUp = "";
    [ObservableProperty] private string _autoShiftKeyboardDown = "";
    [ObservableProperty] private string _autoShiftKeyboardClutch = "";
    [ObservableProperty] private string _autoShiftKeyboardThrottle = "";
    [ObservableProperty] private string _autoShiftToggleKeyboard = "F9";
    [ObservableProperty] private ShiftShortcutChoice? _autoShiftToggleGamepad;
    [ObservableProperty] private string _driveAssistToggleKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _driveAssistToggleGamepad;
    [ObservableProperty] private string _autoShiftClutchToggleKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _autoShiftClutchToggleGamepad;
    [ObservableProperty] private string _hudToggleKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _hudToggleGamepad;
    [ObservableProperty] private string _startRecordingKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _startRecordingGamepad;
    [ObservableProperty] private string _stopRecordingKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _stopRecordingGamepad;
    [ObservableProperty] private string _autoShiftShortcutStatus = "";
    [ObservableProperty] private string _autoShiftSettingsError = "";
    [ObservableProperty] private string _autoShiftStatusText = "";
    [ObservableProperty] private string _autoShiftDetailText = "";

    public bool CanEditAutoShift => !IsAutoShiftEnabled && !IsConnectingAutoShift && !IsConnectingDriveAssist && !IsCheckingShiftOutput;
    public bool IsVirtualTcuStrategy => AutoShiftAlgorithmIndex == (int)ShiftAlgorithm.VirtualTcu;
    public IReadOnlyList<ShiftShortcutChoice> ShiftShortcutChoices { get; } =
    [
        new(ShiftShortcutButton.None, "PadNone", isKey: true),
        new(ShiftShortcutButton.View, "PadView", isKey: true),
        new(ShiftShortcutButton.Menu, "PadMenu", isKey: true),
        new(ShiftShortcutButton.DpadUp, "PadUp", isKey: true),
        new(ShiftShortcutButton.DpadDown, "PadDown", isKey: true),
        new(ShiftShortcutButton.DpadLeft, "PadLeft", isKey: true),
        new(ShiftShortcutButton.DpadRight, "PadRight", isKey: true),
        new(ShiftShortcutButton.LeftStick, "L3"), new(ShiftShortcutButton.RightStick, "R3"),
        new(ShiftShortcutButton.LeftBumper, "LB"), new(ShiftShortcutButton.RightBumper, "RB"),
        new(ShiftShortcutButton.A, "A"), new(ShiftShortcutButton.B, "B"), new(ShiftShortcutButton.X, "X"),
        new(ShiftShortcutButton.Y, "Y"),
    ];

    public string AutoShiftStrategyHelp => (ShiftAlgorithm)AutoShiftAlgorithmIndex switch
    {
        ShiftAlgorithm.LazyForza => TelemetryText.Get("StrategyHelp1"),
        ShiftAlgorithm.PowerDrop => TelemetryText.Get("StrategyHelp2"),
        ShiftAlgorithm.VirtualTcu => TelemetryText.Get("StrategyHelp3"),
        ShiftAlgorithm.Fh6AutoShift => TelemetryText.Get("StrategyHelp4"),
        ShiftAlgorithm.Mode5 => TelemetryText.Get("StrategyHelp5"),
        _ => "",
    };

    public string VirtualTcuModeHelp => (VirtualTcuMode)AutoShiftVirtualTcuModeIndex switch
    {
        VirtualTcuMode.Comfort => TelemetryText.Get("DriveModeHelpComfort"),
        VirtualTcuMode.Race => TelemetryText.Get("DriveModeHelpRace"),
        VirtualTcuMode.Drift => TelemetryText.Get("DriveModeHelpDrift"),
        VirtualTcuMode.OffRoad => TelemetryText.Get("DriveModeHelpOffRoad"),
        _ => "",
    };

    public bool IsKeyboardShiftMode => AutoShiftModeIndex == 1;
    public string AutoShiftRoutingPath => _shiftInput.RoutingPath ?? "";
    public bool ShowAutoShiftRoutingPath => !IsKeyboardShiftMode && !_shiftInput.HasPairedGamepad && AutoShiftRoutingPath.Length > 0;

    public string AutoShiftModeHelp => IsKeyboardShiftMode
        ? (AutoShiftAutoDetectRaceTransmission
            ? TelemetryText.Get("ModeHelpTransDetect")
            : AutoShiftAutomaticClutch
                ? TelemetryText.Get("ModeHelpKbClutch")
                : TelemetryText.Get("ModeHelpKbNoClutch"))
        : IsolationHelp + (AutoShiftAutoDetectRaceTransmission
            ? TelemetryText.Get("ModeHelpTransDetect")
            : AutoShiftAutomaticClutch
                ? TelemetryText.Get("ModeHelpPadClutch")
                : TelemetryText.Get("ModeHelpPadNoClutch"));

    private string IsolationHelp => _shiftInput.HasPairedGamepad
        ? TelemetryText.Get("IsoHelpPaired")
        : TelemetryText.Get("IsoHelpUnpaired");

    private readonly Dictionary<string, string> _vehicleParameterSnapshot = new();

    internal PerformanceTelemetryViewModel(TelemetrySettingsFile preferences, IAutomaticShiftInput shiftInput, TelemetrySettings? settings = null)
    {
        _preferences = preferences;
        _settings = settings ?? preferences.Load();
        var initialLanguage = TelemetryText.ParseCode(_settings.Language);
        TelemetryText.SetLanguage(initialLanguage);
        _selectedLanguageIndex = (int)initialLanguage;
        _allVehiclesLabel = TelemetryText.Get("AllVehicles");
        _historyVehicleFilter = _allVehiclesLabel;
        _statusText = TelemetryText.Get("StatusInitial");
        _connectionStateText = TelemetryText.Get("ConnDisconnected");
        _inputStateText = TelemetryText.Get("ConnDisconnected");
        _carText = TelemetryText.Get("CarNoData");
        _zeroToHundredText = TelemetryText.Get("OvWaitZero");
        _driveRecordingStatus = TelemetryText.Get("RecIdleStatus");
        _testStateText = TelemetryText.Get("TestStateWaitStart");
        _activeTestText = TelemetryText.Get("NoneText");
        _compareSummary = TelemetryText.Get("CmpInitial");
        _snapshotInfoText = TelemetryText.Get("SnapshotNone");
        _autoShiftStatusText = TelemetryText.Get("AutoShiftDisabled");
        Hud = new TelemetryHudViewModel(preferences, _settings);
        _receiver = new TelemetryReceiver(_settings);
        _shiftInput = shiftInput;
        _autoShift = new AutomaticShiftController(shiftInput, preferences.GearProfilesPath);
        _store = new TelemetryStore(preferences.GetRecordDirectory(_settings));
        _engine = new PerformanceTestEngine(_settings);
        AvailableTests = TestCatalog.All;
        foreach (var test in AvailableTests.Where(t => t.Enabled && t.Kind is PerformanceTestKind.Acceleration or PerformanceTestKind.Braking))
            EnabledTests.Add(test);
        _engine.SetEnabled(EnabledTests);

        _slowTimer = new Timer(_ => QueueSlowRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        _shiftShortcutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _shiftShortcutTimer.Tick += (_, _) => PollShiftShortcut();
        _shiftInput.ShortcutKeyPressed += OnDesktopShortcutPressed;
        _receiver.SampleReceived += OnSample;

        SettingsAddress = _settings.ListenAddress;
        SettingsPort = _settings.Port.ToString(CultureInfo.InvariantCulture);
        SettingsSpeedUnit = _settings.SpeedUnit;
        SettingsPowerUnit = _settings.PowerUnit;
        SettingsTorqueUnit = _settings.TorqueUnit;
        SettingsRefreshHz = _settings.UiRefreshHz.ToString(CultureInfo.InvariantCulture);
        SettingsSmoothing = _settings.SmoothingAlpha.ToString(CultureInfo.InvariantCulture);
        SettingsMaxTestSeconds = _settings.MaxTestSeconds.ToString(CultureInfo.InvariantCulture);
        SettingsAutoSave = _settings.AutoSaveRecords;
        SettingsStorePath = _store.RootPath;
        LoadRetentionFields(_settings.Retention);
        _nextCleanupAt = Environment.TickCount64 + _settings.Retention.CleanupIntervalSeconds * 1000L;
        AutoShiftModeIndex = _settings.AutomaticShift.GamepadMode == GamepadShiftMode.Keyboard ? 1 : 0;
        AutoShiftPressDuration = _settings.AutomaticShift.PressDurationMs.ToString(CultureInfo.InvariantCulture);
        AutoShiftLatency = _settings.AutomaticShift.ShiftLatencyMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        AutoShiftAutomaticClutch = _settings.AutomaticShift.AutomaticClutch;
        _autoShift.SetAutomaticClutch(AutoShiftAutomaticClutch);
        AutoShiftAutoDetectRaceTransmission = _settings.AutomaticShift.AutoDetectRaceTransmission;
        _autoShift.SetAutoDetectRaceTransmission(AutoShiftAutoDetectRaceTransmission);
        AutoShiftUpshiftGripProtection = _settings.AutomaticShift.UpshiftGripProtection;
        AutoShiftProtectLaunch = _settings.AutomaticShift.ProtectLaunchWheelspin;
        AutoShiftProtectAirborne = _settings.AutomaticShift.ProtectAirborne;
        AutoShiftProtectReengagement = _settings.AutomaticShift.ProtectShiftReengagement;
        AutoShiftAdaptiveMatching = _settings.AutomaticShift.AdaptiveGearMatching;
        AutoShiftStrictSpeedMatching = _settings.AutomaticShift.StrictSpeedGearMatching;
        _autoShift.SetAssists(AutoShiftProtectLaunch, AutoShiftProtectReengagement, AutoShiftAdaptiveMatching,
            AutoShiftProtectAirborne, AutoShiftStrictSpeedMatching);
        AutoShiftAlgorithmIndex = (int)_settings.AutomaticShift.Algorithm;
        AutoShiftVirtualTcuModeIndex = (int)_settings.AutomaticShift.VirtualTcuMode;
        _autoShift.SetStrategy(_settings.AutomaticShift.Algorithm, _settings.AutomaticShift.VirtualTcuMode);
        AutoShiftDriftMode = _settings.AutomaticShift.DriftMode;
        _autoShift.SetDriftMode(AutoShiftDriftMode);
        AutoShiftMaximumGear = _settings.AutomaticShift.MaximumGear?.ToString(CultureInfo.InvariantCulture) ?? "";
        AutoShiftKeyboardUp = _settings.AutomaticShift.KeyboardUpshift;
        AutoShiftKeyboardDown = _settings.AutomaticShift.KeyboardDownshift;
        AutoShiftKeyboardClutch = _settings.AutomaticShift.KeyboardClutch;
        AutoShiftKeyboardThrottle = _settings.AutomaticShift.KeyboardThrottle;
        AutoShiftToggleKeyboard = _settings.AutomaticShift.ToggleKeyboardKey;
        AutoShiftToggleGamepad = ShiftShortcutChoices.First(choice => choice.Button == _settings.AutomaticShift.ToggleGamepadButton);
        DriveAssistToggleKeyboard = _settings.AutomaticShift.DriveAssistToggleKeyboardKey;
        DriveAssistToggleGamepad = ShiftShortcutChoices.First(choice => choice.Button == _settings.AutomaticShift.DriveAssistToggleGamepadButton);
        EmergencyKeyboard = _settings.AutomaticShift.EmergencyKeyboardKey;
        EmergencyGamepad = PadChoice(_settings.AutomaticShift.EmergencyGamepadButton);
        DriftExitKeyboard = _settings.AutomaticShift.DriftExitKeyboardKey;
        DriftExitGamepad = PadChoice(_settings.AutomaticShift.DriftExitGamepadButton);
        AutoShiftClutchToggleKeyboard = _settings.AutomaticShift.ClutchToggleKeyboardKey;
        AutoShiftClutchToggleGamepad = ShiftShortcutChoices.First(choice => choice.Button == _settings.AutomaticShift.ClutchToggleGamepadButton);
        HudToggleKeyboard = _settings.AutomaticShift.HudToggleKeyboardKey;
        HudToggleGamepad = ShiftShortcutChoices.First(choice => choice.Button == _settings.AutomaticShift.HudToggleGamepadButton);
        StartRecordingKeyboard = _settings.AutomaticShift.StartRecordingKeyboardKey;
        StartRecordingGamepad = ShiftShortcutChoices.First(choice => choice.Button == _settings.AutomaticShift.StartRecordingGamepadButton);
        StopRecordingKeyboard = _settings.AutomaticShift.StopRecordingKeyboardKey;
        StopRecordingGamepad = ShiftShortcutChoices.First(choice => choice.Button == _settings.AutomaticShift.StopRecordingGamepadButton);
        AutoShiftShortcutEnabled = _settings.AutomaticShift.EnableShiftShortcut;
        ShowGearChart = _settings.ShowGearChart;
        LoadDriveAssist();
        TelemetryText.LanguageChanged += OnLanguageChanged;
        RefreshHistory();
        RefreshDiagnostics();
        _slowTimer.Change(1000, 1000);
        ApplyShortcutSettings();
        if (_settings.AutomaticShiftEnabled)
            _autoShift.TryEnable(_settings.AutomaticShift, out _, outputReady: false);
        if (_settings.AutoStart || _settings.AutomaticShiftEnabled) Start();
        RefreshAutoShift();
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (SuppressIndexSideEffects || value < 0 || value >= LanguageOptions.Count) return;
        var language = (TelemetryLanguage)value;
        var code = TelemetryText.ToCode(language);
        if (!string.Equals(_settings.Language, code, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Language = code;
            if (_preferences.Save(_settings) is { } saveError) SettingsStatus = saveError;
        }
        TelemetryText.SetLanguage(language);
    }

    private void OnLanguageChanged(TelemetryLanguage language)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var uiThread = Dispatcher.UIThread;
        if (!uiThread.CheckAccess())
        {
            uiThread.Post(() => OnLanguageChanged(language));
            return;
        }
        if (Volatile.Read(ref _disposed) != 0) return;
        if (SelectedLanguageIndex != (int)language)
        {
            SuppressIndexSideEffects = true;
            try { SelectedLanguageIndex = (int)language; }
            finally { SuppressIndexSideEffects = false; }
        }
        RefreshLocalization();
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(AutoShiftStrategyHelp));
        OnPropertyChanged(nameof(VirtualTcuModeHelp));
        OnPropertyChanged(nameof(AutoShiftModeHelp));
        OnPropertyChanged(nameof(PadSetupProfileText));
        foreach (var choice in ShiftShortcutChoices) choice.NotifyLabelChanged();
        RefreshDriveAssistLanguage();
        RefreshHudShortcut();
        AutoShiftShortcutStatus = "";
        AutoShiftSettingsError = "";
        DriveAssistError = "";
        SettingsStatus = "";
        RetentionStatus = "";

        var previousAllVehicles = _allVehiclesLabel;
        _allVehiclesLabel = TelemetryText.Get("AllVehicles");
        if (string.Equals(HistoryVehicleFilter, previousAllVehicles, StringComparison.Ordinal))
            HistoryVehicleFilter = _allVehiclesLabel;

        Hud.Live = Hud.Live with { IntentBadge = "" };
        _previousShiftSnapshot = null;
        RefreshAutoShift();

        TelemetrySample? latest;
        TelemetryDerived? derived;
        VehicleInfo? carInfo;
        lock (_sync)
        {
            latest = _latest;
            derived = _latestDerived;
            carInfo = _carInfo;
        }

        if (latest is not null && derived is not null)
        {
            if (IsListening) RefreshFromLatest();
            else
            {
                Hud.Live = Hud.Live with { VehicleName = UpdateCarHeader(latest, carInfo) };
                DrivingStateText = DescribeState(derived.State);
                TestStateText = DescribeTestState(_engine.CurrentState);
                var titles = _engine.ActiveTestTitles;
                ActiveTestText = titles.Length > 0 ? titles : TelemetryText.Get("NoneText");
                if (_bestByKey.TryGetValue("a0_100", out var best)) ZeroToHundredText = TelemetryText.Format("OvSecFmt", best);
                else ZeroToHundredText = TelemetryText.Get("OvWaitZero");
            }
            RefreshParameterGroups(latest, derived);
        }

        if (!HasData)
        {
            CarText = TelemetryText.Get("CarNoData");
            ZeroToHundredText = TelemetryText.Get("OvWaitZero");
            TestStateText = DescribeTestState(_engine.CurrentState);
            ActiveTestText = TelemetryText.Get("NoneText");
        }

        if (!IsListening)
        {
            StatusText = _hasStoppedSession ? TelemetryText.Get("StatusStopped") : TelemetryText.Get("StatusInitial");
            ConnectionStateText = _hasStoppedSession ? TelemetryText.Get("ConnStopped") : TelemetryText.Get("ConnDisconnected");
            if (!HasData) InputStateText = TelemetryText.Get("ConnDisconnected");
        }
        else if (!HasData)
        {
            var timedOut = _receiver.IsTimedOut;
            ConnectionStateText = TelemetryText.Get(timedOut ? "ConnTimeout" : "ConnWaitingData");
            InputStateText = ConnectionStateText;
            StatusText = timedOut
                ? TelemetryText.Get("StatusTimeout")
                : TelemetryText.Format("StatusListening", _receiver.ListenEndpoint);
        }

        if (_driveRecorder is { } recorder)
        {
            DriveRecordingStatus = TelemetryText.Format("RecLiveFmt",
                recorder.DurationMs / 1000.0, recorder.Frames, recorder.Actions);
        }
        else if (!IsDriveRecording && !IsSavingDriveRecording)
        {
            DriveRecordingStatus = TelemetryText.Get("RecIdleStatus");
        }

        for (var i = 0; i < EnabledTests.Count; i++) EnabledTests[i] = EnabledTests[i];
        RefreshTestSummaries();
        RefreshHistory(force: true);
        if (_hasComparisonGenerated && CompareBase is not null && CompareTarget is not null) GenerateComparison();
        else CompareSummary = TelemetryText.Get("CmpInitial");
        if (!_hasSnapshotSaved) SnapshotInfoText = TelemetryText.Get("SnapshotNone");
        RefreshDiagnostics();
        _drawnChartWindow = 0;
        RefreshCharts();
        Hud.RefreshLocalization();
    }

    private static string DescribeTestState(TestRunState state) => state switch
    {
        TestRunState.Running => TelemetryText.Get("TestStateRunning"),
        TestRunState.Armed => TelemetryText.Get("TestStateArmed"),
        _ => TelemetryText.Get("TestStateWaitStart"),
    };

    // ===== 接收 =====
    [RelayCommand]
    private void Start()
    {
        if (IsListening && _receiver.IsRunning) return;
        _hasStoppedSession = false;
        ApplySettings();
        if (!_receiver.Start(out var error))
        {
            IsListening = false;
            StatusText = error;
            ConnectionStateText = TelemetryText.Get("ConnFailed");
            return;
        }
        IsListening = true;
        _timedOutNotified = false;
        StatusText = TelemetryText.Format("StatusListening", _receiver.ListenEndpoint);
        ConnectionStateText = TelemetryText.Get("ConnWaitingData");
        InputStateText = TelemetryText.Get("ConnWaitingData");
        _sessionSeconds = 0;
    }

    [RelayCommand]
    private void Stop()
    {
        EnableAutoShiftCommand.Cancel();
        CheckShiftOutputCommand.Cancel();
        _shiftInput.SetDriveAssistReadOnly(true);
        DisarmDriveAssist();
        _autoShift.ResetSession();
        _receiver.Stop();
        _ = StopDriveRecordingAsync();
        IsListening = false;
        _hasStoppedSession = true;
        _timedOutNotified = false;
        StatusText = TelemetryText.Get("StatusStopped");
        ConnectionStateText = TelemetryText.Get("ConnStopped");
        Hud.Live = Hud.Live with { Status = TelemetryText.Get("HudWaitingTelemetry"), ShiftHint = "" };
        RefreshAutoShift();
    }

    [RelayCommand]
    private void StartDriveRecording()
    {
        if (!CanStartDriveRecording || Volatile.Read(ref _disposed) != 0) return;
        Start();
        if (!_receiver.IsRunning)
        {
            DriveRecordingStatus = StatusText;
            return;
        }
        try
        {
            var recorder = new DriveRecorder(DriveRecordingRoot, _settings.AutomaticShift, _settings.DriveAssist);
            _driveRecorder = recorder;
            DriveRecordingFolder = recorder.Folder;
            _receiver.DatagramReceived += recorder.RecordTelemetry;
            _autoShift.SetTraceSink(recorder.RecordShift);
            IsDriveRecording = true;
            DriveRecordingStatus = TelemetryText.Get("RecActiveStatus");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DriveRecordingStatus = TelemetryText.Format("RecStartFail", ex.Message);
        }
    }

    private DriveRecorder? DetachDriveRecorder()
    {
        if (_driveRecorder is not { } recorder) return null;
        _receiver.DatagramReceived -= recorder.RecordTelemetry;
        _autoShift.SetTraceSink(null);
        _driveRecorder = null;
        return recorder;
    }

    [RelayCommand]
    private async Task StopDriveRecordingAsync()
    {
        if (DetachDriveRecorder() is not { } recorder) return;
        IsSavingDriveRecording = true;
        IsDriveRecording = false;
        DriveRecordingStatus = TelemetryText.Get("RecSaving");
        _driveFlush = recorder.StopAsync();
        await _driveFlush;
        _driveFlush = null;
        if (Volatile.Read(ref _disposed) != 0) return;
        IsSavingDriveRecording = false;
        DriveRecordingStatus = recorder.Error ?? TelemetryText.Format("RecSavedFmt", recorder.Frames, recorder.Actions);
    }

    [RelayCommand]
    private void OpenDriveRecordingFolder()
    {
        try
        {
            var path = Directory.Exists(DriveRecordingFolder) ? DriveRecordingFolder : DriveRecordingRoot;
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { DriveRecordingStatus = TelemetryText.Format("RecOpenDirFail", ex.Message); }
    }

    [RelayCommand]
    private async Task EnableAutoShiftAsync(CancellationToken cancellationToken)
    {
        if (IsConnectingDriveAssist || IsCheckingShiftOutput || Volatile.Read(ref _disposed) != 0) return;
        if (_settings.DriveAssist.ReadOnly) { AutoShiftSettingsError = TelemetryText.Get("EcuReadOnly"); return; }
        _shiftInput.SetDriveAssistReadOnly(false);
        AutoShiftSettingsError = "";
        if (IsKeyboardShiftMode && IsDriveAssistArmed)
        {
            AutoShiftSettingsError = TelemetryText.Get("EcuDisableForKeyboard");
            return;
        }
        if (!_settings.AutomaticShiftEnabled)
        {
            if (ReadShiftOptions(out var error) is not { } options ||
                !_autoShift.TryEnable(options, out error, outputReady: false))
            {
                AutoShiftSettingsError = error;
                return;
            }

            _settings.AutomaticShift = options;
            _settings.AutomaticShiftEnabled = true;
            AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
            RefreshAutoShift();
        }

        IsAutoShiftRelayPaused = false;
        IsConnectingAutoShift = true;
        AutoShiftStatusText = IsKeyboardShiftMode ? TelemetryText.Get("ConnectingKb") : TelemetryText.Get("ConnectingRelay");
        try
        {
            Start();
            if (!IsListening)
            {
                AutoShiftSettingsError = StatusText;
                return;
            }

            var connectError = await _shiftInput.ConnectAsync(_settings.AutomaticShift, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (connectError is not null)
            {
                AutoShiftSettingsError = connectError;
                return;
            }

            _autoShift.ResumeOutput();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _nextAutoShiftReconnectAt = Environment.TickCount64 + 2000;
            IsConnectingAutoShift = false;
            _previousShiftSnapshot = null;
            RefreshAutoShift();
        }
    }

    [RelayCommand]
    private void DisableAutoShift()
    {
        EnableAutoShiftCommand.Cancel();
        _autoShift.Disable();
        IsAutoShiftRelayPaused = false;
        if (_settings.AutomaticShiftEnabled)
        {
            _settings.AutomaticShiftEnabled = false;
            AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        }
        RefreshAutoShift();
    }

    private void ToggleAutomaticClutchByShortcut()
    {
        var wasAutoDetect = AutoShiftAutoDetectRaceTransmission;
        var currentEffective = _autoShift.GetSnapshot(Environment.TickCount64).EffectiveAutomaticClutch;
        var target = wasAutoDetect ? !currentEffective : !AutoShiftAutomaticClutch;
        if (wasAutoDetect) AutoShiftAutoDetectRaceTransmission = false;
        AutoShiftAutomaticClutch = target;
        var stateWord = TelemetryText.Get(target ? "OnWord" : "OffWord");
        AutoShiftShortcutStatus = wasAutoDetect
            ? TelemetryText.Format("ScStatusClutchToggleExitAuto", stateWord)
            : TelemetryText.Format("ScStatusClutchToggle", stateWord);
    }

    [RelayCommand]
    private void SaveAutoShiftShortcut()
    {
        static string SavedKey(string name)
        {
            var key = ShiftShortcut.ParseKey(name);
            return key > 0 ? ShiftShortcut.Name((byte)key)! : name.Trim();
        }

        var options = _settings.AutomaticShift with
        {
            EnableShiftShortcut = true,
            ToggleKeyboardKey = SavedKey(AutoShiftToggleKeyboard),
            ToggleGamepadButton = AutoShiftToggleGamepad?.Button ?? ShiftShortcutButton.None,
            DriveAssistToggleKeyboardKey = SavedKey(DriveAssistToggleKeyboard),
            DriveAssistToggleGamepadButton = DriveAssistToggleGamepad?.Button ?? ShiftShortcutButton.None,
            EmergencyKeyboardKey = SavedKey(EmergencyKeyboard),
            EmergencyGamepadButton = EmergencyGamepad?.Button ?? ShiftShortcutButton.None,
            DriftExitKeyboardKey = SavedKey(DriftExitKeyboard),
            DriftExitGamepadButton = DriftExitGamepad?.Button ?? ShiftShortcutButton.None,
            ClutchToggleKeyboardKey = SavedKey(AutoShiftClutchToggleKeyboard),
            ClutchToggleGamepadButton = AutoShiftClutchToggleGamepad?.Button ?? ShiftShortcutButton.None,
            HudToggleKeyboardKey = SavedKey(HudToggleKeyboard),
            HudToggleGamepadButton = HudToggleGamepad?.Button ?? ShiftShortcutButton.None,
            StartRecordingKeyboardKey = SavedKey(StartRecordingKeyboard),
            StartRecordingGamepadButton = StartRecordingGamepad?.Button ?? ShiftShortcutButton.None,
            StopRecordingKeyboardKey = SavedKey(StopRecordingKeyboard),
            StopRecordingGamepadButton = StopRecordingGamepad?.Button ?? ShiftShortcutButton.None,
        };
        if (ShiftShortcut.Validate(options) is { } error)
        {
            AutoShiftShortcutStatus = error;
            return;
        }
        var previous = _settings.AutomaticShift;
        _settings.AutomaticShift = options;
        if (_preferences.Save(_settings) is { } errorSaving)
        {
            _settings.AutomaticShift = previous;
            AutoShiftShortcutStatus = errorSaving;
            return;
        }
        AutoShiftShortcutEnabled = true;
        ApplyShortcutSettings();
        AutoShiftShortcutStatus = TelemetryText.Get("ScSaved");
    }

    partial void OnAutoShiftShortcutEnabledChanged(bool value)
    {
        if (_settings.AutomaticShift.EnableShiftShortcut == value) return;
        _settings.AutomaticShift = _settings.AutomaticShift with { EnableShiftShortcut = value };
        ApplyShortcutSettings();
        AutoShiftShortcutStatus = _preferences.Save(_settings) ?? "";
    }

    private void RefreshHudShortcut()
    {
        var settings = _settings.AutomaticShift;
        var keyboard = ShiftShortcut.Name((byte)Math.Max(0, ShiftShortcut.ParseKey(settings.ToggleKeyboardKey))) ?? "";
        var gamepad = settings.ToggleGamepadButton == ShiftShortcutButton.None ? "" :
            ShiftShortcutChoices.First(choice => choice.Button == settings.ToggleGamepadButton).Label;
        var binding = keyboard.Length == 0 ? gamepad : gamepad.Length == 0 ? keyboard : $"{keyboard} / {gamepad}";
        Hud.AutoShiftShortcutText = binding.Length == 0 ? TelemetryText.Get("HudShortcutUnset") :
            TelemetryText.Format(settings.EnableShiftShortcut ? "HudShortcutFmt" : "HudShortcutDisabledFmt", binding);
    }


    partial void OnAutoShiftProtectLaunchChanged(bool value) =>
        UpdateAssists(_settings.AutomaticShift with { ProtectLaunchWheelspin = value });

    partial void OnAutoShiftProtectAirborneChanged(bool value) =>
        UpdateAssists(_settings.AutomaticShift with { ProtectAirborne = value });

    partial void OnAutoShiftProtectReengagementChanged(bool value) =>
        UpdateAssists(_settings.AutomaticShift with { ProtectShiftReengagement = value });

    partial void OnAutoShiftAdaptiveMatchingChanged(bool value) =>
        UpdateAssists(_settings.AutomaticShift with { AdaptiveGearMatching = value });

    partial void OnAutoShiftStrictSpeedMatchingChanged(bool value) =>
        UpdateAssists(_settings.AutomaticShift with { StrictSpeedGearMatching = value });

    private void UpdateAssists(AutomaticShiftSettings options)
    {
        if (_settings.AutomaticShift == options) return;
        _settings.AutomaticShift = options;
        _autoShift.SetAssists(options.ProtectLaunchWheelspin, options.ProtectShiftReengagement, options.AdaptiveGearMatching,
            options.ProtectAirborne, options.StrictSpeedGearMatching);
        AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        RefreshAutoShift();
    }

    partial void OnAutoShiftDriftModeChanged(bool value)
    {
        if (_settings.AutomaticShift.DriftMode == value) return;
        _settings.AutomaticShift = _settings.AutomaticShift with { DriftMode = value };
        _autoShift.SetDriftMode(value);
        AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        RefreshAutoShift();
    }

    partial void OnAutoShiftUpshiftGripProtectionChanged(bool value)
    {
        if (_settings.AutomaticShift.UpshiftGripProtection == value) return;
        _settings.AutomaticShift = _settings.AutomaticShift with { UpshiftGripProtection = value };
        _autoShift.SetUpshiftGripProtection(value);
        AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        RefreshAutoShift();
    }

    partial void OnAutoShiftAutomaticClutchChanged(bool value)
    {
        if (_settings.AutomaticShift.AutomaticClutch == value) return;
        _settings.AutomaticShift = _settings.AutomaticShift with { AutomaticClutch = value };
        _autoShift.SetAutomaticClutch(value);
        AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        _previousShiftSnapshot = null;
        RefreshAutoShift();
    }

    partial void OnAutoShiftAutoDetectRaceTransmissionChanged(bool value)
    {
        if (_settings.AutomaticShift.AutoDetectRaceTransmission == value) return;
        _settings.AutomaticShift = _settings.AutomaticShift with { AutoDetectRaceTransmission = value };
        _autoShift.SetAutoDetectRaceTransmission(value);
        AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        _previousShiftSnapshot = null;
        RefreshAutoShift();
    }

    partial void OnAutoShiftAlgorithmIndexChanged(int value)
    {
        if (SuppressIndexSideEffects || value < 0) return;
        UpdateShiftStrategy((ShiftAlgorithm)value, _settings.AutomaticShift.VirtualTcuMode);
    }

    partial void OnAutoShiftVirtualTcuModeIndexChanged(int value)
    {
        if (SuppressIndexSideEffects || value < 0) return;
        UpdateShiftStrategy(_settings.AutomaticShift.Algorithm, (VirtualTcuMode)value);
    }

    private void UpdateShiftStrategy(ShiftAlgorithm algorithm, VirtualTcuMode mode)
    {
        if (!Enum.IsDefined(algorithm) || !Enum.IsDefined(mode)) return;
        var options = _settings.AutomaticShift with { Algorithm = algorithm, VirtualTcuMode = mode };
        if (options == _settings.AutomaticShift) return;
        _settings.AutomaticShift = options;
        _autoShift.SetStrategy(algorithm, mode);
        AutoShiftSettingsError = _preferences.Save(_settings) ?? "";
        _previousShiftSnapshot = null;
        RefreshAutoShift();
    }

    [RelayCommand]
    private void DisconnectGamepadRelay()
    {
        EnableAutoShiftCommand.Cancel();
        CheckShiftOutputCommand.Cancel();
        DisarmDriveAssist();
        IsAutoShiftRelayPaused = IsAutoShiftEnabled;
        _autoShift.ResetSession();
        _previousShiftSnapshot = null;
        RefreshAutoShift();
    }

    private void RefreshAutoShift()
    {
        if (IsConnectingAutoShift) return;
        var snapshot = _autoShift.GetSnapshot(Environment.TickCount64);
        if (snapshot == _previousShiftSnapshot) return;
        _previousShiftSnapshot = snapshot;
        var hint = snapshot.Gear is int current && current > 0 && snapshot.SuggestedGear > 0 && snapshot.SuggestedGear != current
            ? TelemetryText.Format("HudHintGearFmt", snapshot.SuggestedGear > current ? '↑' : '↓', snapshot.SuggestedGear)
            : "";
        var automaticStatus = !snapshot.Enabled
            ? TelemetryText.Get(AutoShiftSettingsError.Length > 0 || snapshot.State == AutomaticShiftState.InputFailed ? "HudAutoStartFailed" : "HudAutoOff")
            : snapshot.DriftMode
                ? TelemetryText.Get("HudAutoOn") + TelemetryText.Get("HudAutoDriftSuffix")
                : TelemetryText.Get("HudAutoOn");
        var outputPaused = snapshot.Enabled && (!snapshot.OutputReady || snapshot.State == AutomaticShiftState.ConfirmationTimedOut);
        if (outputPaused) automaticStatus = TelemetryText.Get("HudAutoOn") + TelemetryText.Get("HudAutoPausedSuffix");
        var transmissionStatus = "";
        if (AutoShiftAutoDetectRaceTransmission)
        {
            var result = snapshot.TransmissionClutch switch
            {
                TransmissionClutchVerdict.ReadyToProbe => TelemetryText.Get("HudTransPending"),
                TransmissionClutchVerdict.NeedsBaseline => TelemetryText.Get("HudTransBaseline"),
                TransmissionClutchVerdict.Probing => TelemetryText.Get("HudTransProbing"),
                TransmissionClutchVerdict.RaceClutchless => TelemetryText.Get("HudTransRace"),
                TransmissionClutchVerdict.StandardRequiresClutch => TelemetryText.Get("HudTransStd"),
                TransmissionClutchVerdict.GameAutoClutch => TelemetryText.Get("HudTransGame"),
                _ => TelemetryText.Get("HudTransUnavailable"),
            };
            transmissionStatus = TelemetryText.Format("HudTransStatusFmt", result,
                TelemetryText.Get(snapshot.EffectiveAutomaticClutch ? "OnWord" : "OffWord"));
        }
        else if (!snapshot.EffectiveAutomaticClutch && !outputPaused)
        {
            automaticStatus += TelemetryText.Get("HudAutoNoClutchSuffix");
        }
        Hud.TransmissionStatus = transmissionStatus;
        var intentBadge = DriverIntentAnalyzer.BadgeText(snapshot.Intent);
        var protections = BuildHudProtections(snapshot.Protections);
        Hud.Live = Hud.Live with { ShiftHint = hint, AutomaticShiftStatus = automaticStatus, IntentBadge = intentBadge, Protections = protections };
        IsAutoShiftEnabled = snapshot.Enabled;
        IsGamepadForwarding = snapshot.IsForwarding;
        AutoShiftStatusText = IsAutoShiftRelayPaused ? TelemetryText.Get("StateRelayPaused") : snapshot.State switch
        {
            AutomaticShiftState.WaitingForTelemetry => TelemetryText.Get("StateWaitingTelemetry"),
            AutomaticShiftState.WaitingForGame => TelemetryText.Get("StateWaitingGame"),
            AutomaticShiftState.WaitingForDriving => TelemetryText.Get("StateWaitingDriving"),
            AutomaticShiftState.WaitingForOutput => TelemetryText.Get("StateWaitingOutput"),
            AutomaticShiftState.ReverseHold => TelemetryText.Get("StateReverseHold"),
            AutomaticShiftState.NeutralHold => TelemetryText.Get("StateNeutralHold"),
            AutomaticShiftState.Holding => TelemetryText.Get("StateHolding"),
            AutomaticShiftState.Protected => TelemetryText.Get("StateProtected"),
            AutomaticShiftState.UpshiftGripHold => TelemetryText.Get("StateUpshiftGripHold"),
            AutomaticShiftState.BrakeHold => TelemetryText.Get("StateBrakeHold"),
            AutomaticShiftState.BrakingGripHold => TelemetryText.Get("StateBrakingGripHold"),
            AutomaticShiftState.WheelContactHold => TelemetryText.Get("StateWheelContactHold"),
            AutomaticShiftState.CollisionPowerHold => TelemetryText.Get("StateCollisionPowerHold"),
            AutomaticShiftState.DriveAssistHold => TelemetryText.Get("StateDriveAssistHold"),
            AutomaticShiftState.DrivetrainSettling => TelemetryText.Get("StateDrivetrainSettling"),
            AutomaticShiftState.LaunchGripHold => TelemetryText.Get("StateLaunchGripHold"),
            AutomaticShiftState.ReleasingShiftInput => TelemetryText.Get("StateReleasingInput"),
            AutomaticShiftState.WaitingForRatio => TelemetryText.Get("StateWaitingRatio"),
            AutomaticShiftState.ManualHold => TelemetryText.Get("StateManualHold"),
            AutomaticShiftState.Shifting => TelemetryText.Format("StateShifting", snapshot.TargetGear),
            AutomaticShiftState.ProbingGear => TelemetryText.Format("StateProbingGear", snapshot.TargetGear),
            AutomaticShiftState.ConfirmationTimedOut => TelemetryText.Get("StateConfirmationTimedOut"),
            AutomaticShiftState.UnexpectedGear => TelemetryText.Get("StateUnexpectedGear"),
            AutomaticShiftState.InputFailed => _shiftInput.Error ?? TelemetryText.Get("StateInputFailed"),
            _ => TelemetryText.Get("AutoShiftDisabled"),
        };
        var details = new List<string>();
        if (snapshot.StrictSpeedGearMatching) details.Add(TelemetryText.Get("DetailStrictMatch"));
        if (snapshot.Gear is int gear) details.Add(gear == 0 ? TelemetryText.Get("DetailCurrentReverse") : TelemetryText.Format("DetailCurrentGear", gear));
        if (snapshot.DownshiftCue is { } cue)
        {
            details.Add(TelemetryText.Format("DetailSuggestDown", cue.Gear));
            if (cue.LandingRpm > 0) details.Add(TelemetryText.Format("DetailExpectedLanding", cue.LandingRpm));
        }
        if (snapshot.SpeedMatch is { } match)
            details.Add(TelemetryText.Format("DetailSpeedMatch", match.SpeedBandStart, match.SpeedBandStart + SpeedGearMatcher.BandKmh, match.Gear, match.Rpm));
        if (snapshot.MaximumGear > 0) details.Add(snapshot.GearLimit switch
        {
            GearLimitSource.Manual => TelemetryText.Format("DetailMaxManual", snapshot.MaximumGear),
            GearLimitSource.DataOut => TelemetryText.Format("DetailMaxDataOut", snapshot.MaximumGear),
            GearLimitSource.Probe => TelemetryText.Format("DetailMaxProbe", snapshot.MaximumGear + 1, snapshot.MaximumGear),
            _ => TelemetryText.Format("DetailMaxObserved", snapshot.MaximumGear),
        });
        if (snapshot.Gear.HasValue || snapshot.LearnedGears > 0)
            details.Add(TelemetryText.Format("DetailLearnedProgress", snapshot.LearnedGears, snapshot.LearnedOutputBins));
        if (!snapshot.StrictSpeedGearMatching && snapshot.AdvisedPairs > 0 &&
            (ShiftAlgorithm)AutoShiftAlgorithmIndex is ShiftAlgorithm.LazyForza or ShiftAlgorithm.PowerDrop)
            details.Add(TelemetryText.Format("DetailAdvisedPairs", snapshot.AdvisedPairs));
        if (snapshot.LearnedCutRpm > 0) details.Add(TelemetryText.Format("DetailLearnedCut", snapshot.LearnedCutRpm));
        if (snapshot.TransmissionClutch != TransmissionClutchVerdict.Unknown)
        {
            var timing = snapshot.ClutchedShiftMs > 0 && snapshot.ProbeShiftMs > 0
                ? TelemetryText.Format("TransTimingBoth", snapshot.ClutchedShiftMs, snapshot.ProbeShiftMs)
                : snapshot.ProbeShiftMs > 0
                    ? TelemetryText.Format("TransTimingNoClutch", snapshot.ProbeShiftMs)
                    : "";
            var verdictText = snapshot.TransmissionClutch switch
            {
                TransmissionClutchVerdict.NeedsBaseline => TelemetryText.Get("TransNeedsBaseline"),
                TransmissionClutchVerdict.ReadyToProbe => TelemetryText.Get("TransReadyToProbe"),
                TransmissionClutchVerdict.Probing => TelemetryText.Get("TransProbing"),
                TransmissionClutchVerdict.RaceClutchless => TelemetryText.Format("TransVerdictRace", timing),
                TransmissionClutchVerdict.StandardRequiresClutch => TelemetryText.Format("TransVerdictStd", timing),
                TransmissionClutchVerdict.GameAutoClutch => TelemetryText.Get("TransVerdictGame"),
                _ => "",
            };
            if (verdictText.Length > 0) details.Add(verdictText);
        }
        if (!snapshot.EffectiveAutomaticClutch && snapshot.TransmissionClutch == TransmissionClutchVerdict.Unknown)
            details.Add(TelemetryText.Get("DetailClutchOff"));
        if (snapshot.IsForwarding) details.Add(TelemetryText.Get("DetailIsForwarding"));
        if (snapshot.Enabled || snapshot.LastShiftTo > 0)
        {
            details.Add(TelemetryText.Format(
                _settings.AutomaticShift.ShiftLatencyMs.HasValue ? "DetailManualLatency" : "DetailEstLatency",
                snapshot.ShiftLatencyMs));
        }
        if (snapshot.LastShiftTo > 0)
        {
            var recent = TelemetryText.Format("DetailRecentShift", snapshot.LastShiftFrom, snapshot.LastShiftTo);
            if (snapshot.LastLandingRpm > 0) recent += TelemetryText.Format("DetailRecentLanding", snapshot.LastLandingRpm);
            details.Add(recent);
        }
        if (snapshot.Enabled && snapshot.Intent != DriverIntentKind.Idle && intentBadge.Length > 0)
            details.Add(TelemetryText.Format("DetailIntentPrefix", intentBadge));
        if (snapshot.Reason != ShiftReason.None) details.Add(ShiftReasonText(snapshot.Reason));
        AutoShiftDetailText = string.Join(" · ", details);
    }

    partial void OnAutoShiftSettingsErrorChanged(string value)
    {
        if (IsConnectingAutoShift || IsAutoShiftEnabled) return;
        _previousShiftSnapshot = null;
        RefreshAutoShift();
    }

    private static TelemetryHudProtections BuildHudProtections(ShiftProtectionFlags flags)
    {
        static HudProtectionLevel Level(ShiftProtectionFlags f, ShiftProtectionFlags active, ShiftProtectionFlags armed) =>
            (f & active) != 0 ? HudProtectionLevel.Active : (f & armed) != 0 ? HudProtectionLevel.Armed : HudProtectionLevel.Off;

        var alert = (flags & ShiftProtectionFlags.ImpactHold) != 0 ? TelemetryText.Get("ProtAlertImpact")
            : (flags & ShiftProtectionFlags.BrakingGripHold) != 0 ? TelemetryText.Get("ProtAlertBrakeGrip")
            : (flags & ShiftProtectionFlags.ClutchOrHandbrakeHold) != 0 ? TelemetryText.Get("ProtAlertClutchHandbrake")
            : (flags & ShiftProtectionFlags.ManualHold) != 0 ? TelemetryText.Get("ProtAlertManual") : "";
        return new(
            Level(flags, ShiftProtectionFlags.LaunchActive, ShiftProtectionFlags.LaunchArmed),
            Level(flags, ShiftProtectionFlags.AirborneActive, ShiftProtectionFlags.AirborneArmed),
            Level(flags, ShiftProtectionFlags.UpshiftGripActive, ShiftProtectionFlags.UpshiftGripArmed),
            Level(flags, ShiftProtectionFlags.ReengagementActive, ShiftProtectionFlags.ReengagementArmed),
            alert);
    }

    private static string ShiftReasonText(ShiftReason reason) => reason switch
    {
        ShiftReason.LazyForzaUpshift => TelemetryText.Get("ReasonMode1Up"),
        ShiftReason.LazyForzaDownshift => TelemetryText.Get("ReasonMode1Down"),
        ShiftReason.HigherGearPull => TelemetryText.Get("ReasonHigherPull"),
        ShiftReason.HighRpmOutputLoss => TelemetryText.Get("ReasonOutputLoss"),
        ShiftReason.Limiter => TelemetryText.Get("ReasonLimiter"),
        ShiftReason.LightLoad => TelemetryText.Get("ReasonLightLoad"),
        ShiftReason.Kickdown => TelemetryText.Get("ReasonKickdown"),
        ShiftReason.Braking => TelemetryText.Get("ReasonBraking"),
        ShiftReason.EngineBraking => TelemetryText.Get("ReasonEngineBrake"),
        ShiftReason.LowSpeed => TelemetryText.Get("ReasonLowSpeed"),
        ShiftReason.Lugging => TelemetryText.Get("ReasonLugging"),
        ShiftReason.LearningLimit => TelemetryText.Get("ReasonLearningLimit"),
        ShiftReason.VirtualTcuComfort => TelemetryText.Get("ReasonMode3Comfort"),
        ShiftReason.VirtualTcuPowerBand => TelemetryText.Get("ReasonMode3Power"),
        ShiftReason.VirtualTcuDrift => TelemetryText.Get("ReasonMode3Drift"),
        ShiftReason.VirtualTcuOffRoad => TelemetryText.Get("ReasonMode3OffRoad"),
        ShiftReason.VirtualTcuTraction => TelemetryText.Get("ReasonMode3Traction"),
        ShiftReason.Fh6FuelCut => TelemetryText.Get("ReasonMode4Cut"),
        ShiftReason.Fh6PowerCrossover => TelemetryText.Get("ReasonMode4Cross"),
        ShiftReason.Fh6PredictedLimiter => TelemetryText.Get("ReasonMode4PredLim"),
        ShiftReason.Fh6LoadPlateau => TelemetryText.Get("ReasonMode4Plateau"),
        ShiftReason.Fh6Coast => TelemetryText.Get("ReasonMode4Coast"),
        ShiftReason.Fh6CornerExit => TelemetryText.Get("ReasonMode4Exit"),
        ShiftReason.SpeedMatchedPower => TelemetryText.Get("ReasonSpeedMatchPower"),
        ShiftReason.SpeedMatchedCoast => TelemetryText.Get("ReasonSpeedMatchCoast"),
        ShiftReason.SpeedMatchedBraking => TelemetryText.Get("ReasonSpeedMatchBrake"),
        ShiftReason.SpeedMatchedLearning => TelemetryText.Get("ReasonSpeedMatchLearn"),
        ShiftReason.StrictSpeedMatch => TelemetryText.Get("ReasonStrictMatch"),
        ShiftReason.Mode5PowerBand => TelemetryText.Get("ReasonMode5Power"),
        ShiftReason.Mode5Limiter => TelemetryText.Get("ReasonMode5Lim"),
        ShiftReason.CoastDownshift => TelemetryText.Get("ReasonCoastDown"),
        _ => "",
    };

    [RelayCommand]
    private void Reset()
    {
        lock (_sync)
        {
            _derived.Reset();
            _ringBuffer.Clear();
            _chartRevision++;
            _sessionSamples.Clear();
            _bestByKey.Clear();
            _sessionSeconds = 0;
            _lastCar = -1;
        }
        _engine.Reset();
        _peakSpeedKmh = 0; _peakPowerKw = 0; _peakTorqueNm = 0;
        SpeedValue = 0; RpmValue = 0; ThrottleValue = 0; BrakeValue = 0;
        SpeedText = RpmText = SpeedValueText = RpmValueText = GearText = PowerText = TorqueText = "—";
        PeakSpeedText = PeakPowerText = PeakTorqueText = "—";
        LongitudinalGText = LateralGText = CombinedGText = "—";
        ThrottleText = BrakeText = SteerText = "—";
        ZeroToHundredText = TelemetryText.Get("OvWaitZero");
        DrivingStateText = "—";
        RefreshTestSummaries();
        StatusText = TelemetryText.Get("StatusReset");
    }

    private void OnSample(TelemetrySample sample)
    {
        if (!_receiver.IsRunning) return;
        var arrival = Environment.TickCount64;
        var assist = _shiftInput.FeedDriveAssist(sample);
        RecordAssistAnalysis(sample, assist, arrival);
        _autoShift.Feed(sample, arrival);
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_lastSampleTicks != 0 && now.Ticks - _lastSampleTicks > TimeSpan.TicksPerSecond * 2)
            {
                // 数据中断：重置差分与曲线时间基准，避免把暂停时间算进曲线。
                _derived.InvalidateSession();
                _sessionSeconds = 0;
                _ringBuffer.Clear();
                _sessionSamples.Clear();
            }
            _lastSampleTicks = now.Ticks;
            _lastArrivalAt = arrival;
            if (_lastCar >= 0 && _lastCar != sample.CarOrdinal)
            {
                _ringBuffer.Clear();
                _sessionSamples.Clear();
                _sessionSeconds = 0;
                _bestByKey.Clear();
            }
            _lastCar = sample.CarOrdinal;
            if (_carInfoOrdinal != sample.CarOrdinal)
            {
                // 车型编号（CarOrdinal）对应游戏数据库的 Data_Car.Id；查离线档案补齐车厂/型号/年份等资料。
                _carInfoOrdinal = sample.CarOrdinal;
                _carInfo = VehicleCatalog.Find(sample.CarOrdinal);
                _carName = _carInfo?.Name ?? "";
                _engine.SetCarName(_carName);
            }

            if (_latest is not null)
            {
                var deltaMs = unchecked(sample.TimestampMs - _latest.TimestampMs);
                if (deltaMs is > 0 and < 2000) _sessionSeconds += deltaMs / 1000.0;
            }
            _derived.Update(sample, _settings);
            _latest = sample;
            _latestDerived = _derived;
            _ringBuffer.Add(_sessionSeconds, sample, _derived);
            var retention = _settings.Retention;
            _ringBuffer.Trim(_sessionSeconds - retention.Seconds, retention.Frames);
            _sessionSamples.Add(sample, arrival, retention);
            _chartRevision++;
            _peakSpeedKmh = Math.Max(_peakSpeedKmh, sample.SpeedKmh);
            _peakPowerKw = Math.Max(_peakPowerKw, sample.PowerKw);
            _peakTorqueNm = Math.Max(_peakTorqueNm, Math.Abs(sample.TorqueNm));
            var completed = _engine.Feed(sample, _derived,
                _settings.DriveAssist.Features.HasFlag(DriveAssistFeature.Feedback) ? assist : null);
            for (var i = 0; i < completed.Count; i++)
            {
                var result = completed[i];
                _pendingRecords.Add(result);
                if (!_bestByKey.TryGetValue(result.Key, out var best) || result.DurationSeconds < best)
                    _bestByKey[result.Key] = result.DurationSeconds;
                var previous = _historyByKey.TryGetValue(result.Key, out var value) ? value : (0, 0, 0);
                _historyByKey[result.Key] = (result.DurationSeconds, previous.Sum + result.DurationSeconds, previous.Count + 1);
            }
        }
        QueueSampleRefresh(arrival);
    }

    private void QueueSampleRefresh(long now)
    {
        var interval = Math.Max(16, 1000 / Math.Max(5, _settings.UiRefreshHz));
        if (Volatile.Read(ref _disposed) != 0 || now - _lastPostedRefreshAt < interval ||
            Interlocked.CompareExchange(ref _sampleRefreshQueued, 1, 0) != 0) return;
        _lastPostedRefreshAt = now;
        Dispatcher.UIThread.Post(() =>
        {
            try { if (Volatile.Read(ref _disposed) == 0) RefreshFromLatest(); }
            finally { Volatile.Write(ref _sampleRefreshQueued, 0); }
        });
    }

    private void QueueSlowRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _slowRefreshQueued, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { if (Volatile.Read(ref _disposed) == 0) RefreshSlow(); }
            finally { Volatile.Write(ref _slowRefreshQueued, 0); }
        });
    }

    private void RefreshFromLatest()
    {
        if (IsListening && !_receiver.IsRunning)
        {
            EnableAutoShiftCommand.Cancel();
            DisarmDriveAssist();
            _autoShift.ResetSession();
            _ = StopDriveRecordingAsync();
            IsListening = false;
            ConnectionStateText = TelemetryText.Get("ConnInterrupted");
            StatusText = _receiver.LastError;
        }
        RefreshAutoShift();
        if (IsDriveAssistArmed || SelectedTab == (int)TelemetryTab.DriveAssist) RefreshDriveAssist(includeReports: false);
        if (!IsListening)
        {
            Hud.Live = Hud.Live with { Status = TelemetryText.Get("HudWaitingTelemetry"), ShiftHint = "" };
            return;
        }

        TelemetrySample? sample;
        TelemetryDerived? derived;
        VehicleInfo? carInfo;
        double sessionSeconds;
        lock (_sync)
        {
            sample = _latest;
            derived = _latestDerived;
            carInfo = _carInfo;
            sessionSeconds = _sessionSeconds;
        }
        var timedOut = _receiver.IsTimedOut;
        var hudStatus = !_receiver.HasEverReceived ? TelemetryText.Get("HudWaitingTelemetry") : timedOut ? TelemetryText.Get("HudTelemetryTimeout")
            : sample?.IsRaceOn != true ? TelemetryText.Get("HudWaitingDriving") : "";
        if (sample is null || derived is null)
        {
            Hud.Live = Hud.Live with { Status = hudStatus };
            ConnectionStateText = TelemetryText.Get(timedOut ? "ConnTimeout" : "ConnWaitingData");
            return;
        }

        HasData = true;
        var speed = TelemetryUnits.Speed(sample.SpeedKmh, _settings.SpeedUnit);
        var speedSymbol = TelemetryUnits.SpeedSymbol(_settings.SpeedUnit);
        var speedDecimals = "0." + new string('0', Math.Max(0, _settings.Decimals));
        SpeedText = $"{speed.ToString(speedDecimals, CultureInfo.CurrentCulture)} {speedSymbol}";
        SpeedValueText = speed.ToString(speedDecimals, CultureInfo.CurrentCulture);
        PeakSpeedText = $"{TelemetryUnits.Speed(_peakSpeedKmh, _settings.SpeedUnit).ToString(speedDecimals, CultureInfo.CurrentCulture)} {speedSymbol}";
        SpeedValue = sample.SpeedKmh;
        SpeedMaximum = Math.Max(SpeedMaximum, Math.Ceiling(sample.SpeedKmh / 40.0) * 40.0);
        if (SpeedMaximum < 200) SpeedMaximum = 200;

        var rpmMax = double.IsFinite(sample.EngineMaxRpm) && sample.EngineMaxRpm > 0 ? sample.EngineMaxRpm : 8000;
        RpmMaximum = Math.Max(RpmMaximum, rpmMax);
        RpmRedline = rpmMax;
        RpmValue = sample.Rpm;
        RpmText = $"{sample.Rpm:0} rpm";
        RpmValueText = $"{sample.Rpm:0}";
        GearText = sample.Gear switch
        {
            0 => "R",
            >= 1 and <= AutomaticShiftSettings.MaximumForwardGears => sample.Gear.ToString(CultureInfo.InvariantCulture),
            _ => "—",
        };

        PowerText = $"{TelemetryUnits.Power(sample.PowerKw, _settings.PowerUnit):0.0} {TelemetryUnits.PowerSymbol(_settings.PowerUnit)}";
        TorqueText = $"{TelemetryUnits.Torque(sample.TorqueNm, _settings.TorqueUnit):0.0} {TelemetryUnits.TorqueSymbol(_settings.TorqueUnit)}";
        PeakPowerText = $"{TelemetryUnits.Power(_peakPowerKw, _settings.PowerUnit):0.0} {TelemetryUnits.PowerSymbol(_settings.PowerUnit)}";
        PeakTorqueText = $"{TelemetryUnits.Torque(Math.Max(Math.Abs(sample.TorqueNm), _peakTorqueNm), _settings.TorqueUnit):0.0} {TelemetryUnits.TorqueSymbol(_settings.TorqueUnit)}";

        LongitudinalGText = $"{derived.SmoothedLongitudinalG:+0.00;-0.00;0.00} G";
        LateralGText = $"{derived.SmoothedLateralG:+0.00;-0.00;0.00} G";
        CombinedGText = $"{derived.SmoothedCombinedG:0.00} G";
        ThrottleText = $"{sample.Throttle * 100:0}%";
        BrakeText = $"{sample.Brake * 100:0}%";
        SteerText = $"{sample.Steer * 100:+0;-0;0}%";
        ThrottleValue = sample.Throttle * 100;
        BrakeValue = sample.Brake * 100;
        DrivingStateText = DescribeState(derived.State);

        var throttlePct = Math.Clamp(sample.Throttle * 100.0, 0, 100);
        var brakePct = Math.Clamp(sample.Brake * 100.0, 0, 100);
        var hudDetail = TelemetryText.Format("HudDetailLongLatFmt", derived.SmoothedLongitudinalG, Math.Abs(derived.SmoothedLateralG));
        if (double.IsFinite(sample.BoostPsi) && sample.BoostPsi >= 1.0)
            hudDetail += $" · +{sample.BoostPsi:0} psi";
        var drivenSlip = sample.DrivetrainType switch
        {
            0 => Math.Max(Math.Abs(sample.FrontLeft.SlipRatio), Math.Abs(sample.FrontRight.SlipRatio)),
            1 => Math.Max(Math.Abs(sample.RearLeft.SlipRatio), Math.Abs(sample.RearRight.SlipRatio)),
            _ => Math.Max(Math.Max(Math.Abs(sample.FrontLeft.SlipRatio), Math.Abs(sample.FrontRight.SlipRatio)),
                          Math.Max(Math.Abs(sample.RearLeft.SlipRatio), Math.Abs(sample.RearRight.SlipRatio))),
        };
        if (sample.HandBrake >= 0.08) hudDetail += " · " + TelemetryText.Get("HudDetailHandbrake");
        else if (sample.Clutch >= 0.08) hudDetail += " · " + TelemetryText.Format("HudDetailClutchFmt", sample.Clutch * 100);
        else if (double.IsFinite(drivenSlip) && drivenSlip >= 0.42) hudDetail += " · " + TelemetryText.Get("HudDetailSlip");
        var hudIntentBadge = !string.IsNullOrEmpty(Hud.Live.IntentBadge) ? Hud.Live.IntentBadge : derived.State switch
        {
            DrivingState.HardAcceleration => TelemetryText.Get("IntentKickdown"),
            DrivingState.HardBraking => TelemetryText.Get("IntentHardBraking"),
            DrivingState.Braking => TelemetryText.Get("IntentBraking"),
            DrivingState.DriftCandidate when _settings.AutomaticShift.DriftPredictionEnabled => TelemetryText.Get("IntentDrifting"),
            DrivingState.RearSliding or DrivingState.DriftCandidate => TelemetryText.Get("DsRearSlide"),
            DrivingState.TrailBraking => TelemetryText.Get("IntentTrailBraking"),
            DrivingState.TurningLeft or DrivingState.TurningRight => TelemetryText.Get("IntentMidCorner"),
            DrivingState.Accelerating => TelemetryText.Get("DsAccel"),
            DrivingState.Cruising => TelemetryText.Get("IntentCruising"),
            _ => "",
        };
        Hud.Live = new(hudStatus, SpeedText, GearText, RpmText, RpmValue, rpmMax,
            Hud.Live.ShiftHint, Hud.Live.AutomaticShiftStatus,
            throttlePct, brakePct, sample.Steer, hudDetail, hudIntentBadge,
            sample.PowerKw, sample.TorqueNm, sample.DrivetrainType, Hud.Live.Protections,
            UpdateCarHeader(sample, carInfo), sample.CarPerformanceIndex, Hud.Live.DriveAssistStatus, Hud.Live.LearnedTightTurnSpeedText);
        InputStateText = TelemetryText.Get("ConnOk");

        ConnectionStateText = TelemetryText.Get(timedOut ? "ConnTimeout" : "ConnOk");
        StatusText = TelemetryText.Format("StatusRecvRate", _receiver.PacketsAccepted, _receiver.AveragePacketsPerSecond, sessionSeconds);
        TestStateText = DescribeTestState(_engine.CurrentState);
        var titles = _engine.ActiveTestTitles;
        ActiveTestText = titles.Length > 0 ? titles : TelemetryText.Get("NoneText");
        if (_latestDerived is not null && _bestByKey.TryGetValue("a0_100", out var best))
            ZeroToHundredText = TelemetryText.Format("OvSecFmt", best);

        if (SelectedTab == (int)TelemetryTab.AllParameters && Environment.TickCount64 - _lastParameterRefreshAt >= 125)
            RefreshParameterGroups(sample, derived);
        if (!ChartPaused && SelectedTab == (int)TelemetryTab.Charts) RefreshCharts();
    }

    private static string CompactVehicleName(TelemetrySample sample, VehicleInfo? carInfo) => carInfo is not null
        ? $"{carInfo.MakeName} {carInfo.Model}".Trim()
        : TelemetryText.Format("CarUnknownTitle", sample.CarOrdinal);

    private string UpdateCarHeader(TelemetrySample sample, VehicleInfo? carInfo)
    {
        var key = (sample.CarOrdinal, sample.CarPerformanceIndex, sample.DrivetrainType, sample.NumCylinders, TelemetryText.CurrentLanguage);
        if (_carHeaderKey == key) return _compactVehicleName;
        _carHeaderKey = key;
        if (carInfo is not null)
        {
            CarText = carInfo.Name;
            CarDetailText = DescribeCarDetail(carInfo, sample);
            CarSourceText = TelemetryText.Format("CarSourceCatalog", VehicleCatalog.Count);
        }
        else
        {
            CarText = TelemetryText.Format("CarUnknownTitle", sample.CarOrdinal);
            CarDetailText = TelemetryText.Format("CarUnknownDetail", sample.CarOrdinal, sample.CarPerformanceIndex,
                DrivetrainNames.Name(sample.DrivetrainType), TelemetryText.Format("CatCylindersFmt", sample.NumCylinders));
            CarSourceText = TelemetryText.Get("CarSourceUnknown");
        }
        return _compactVehicleName = CompactVehicleName(sample, carInfo);
    }

    private double _peakSpeedKmh;
    private double _peakPowerKw;
    private double _peakTorqueNm;
    private string _carName = "";
    private VehicleInfo? _carInfo;
    private int _carInfoOrdinal = int.MinValue;

    private void RefreshSlow()
    {
        RefreshFromLatest();
        RetryAutoShiftOutput();
        if (_driveRecorder is { } recorder)
        {
            if (recorder.Error is not null) _ = StopDriveRecordingAsync();
            else DriveRecordingStatus = TelemetryText.Format("RecLiveFmt", recorder.DurationMs / 1000.0, recorder.Frames, recorder.Actions);
        }
        _autoShift.SaveProfiles();
        MaintainRetention(Environment.TickCount64);
        RefreshDiagnostics();
        PumpRecordWrites();
        if (IsListening && _receiver.IsTimedOut && !_timedOutNotified)
        {
            _timedOutNotified = true;
            ConnectionStateText = TelemetryText.Get("ConnTimeout");
            StatusText = TelemetryText.Get("StatusTimeout");
        }
        else if (IsListening && !_receiver.IsTimedOut)
        {
            _timedOutNotified = false;
        }
    }

    private void RetryAutoShiftOutput()
    {
        var now = Environment.TickCount64;
        if (_settings.DriveAssist.ReadOnly || !_settings.AutomaticShiftEnabled || _hasStoppedSession || IsAutoShiftRelayPaused ||
            IsConnectingAutoShift || IsConnectingDriveAssist || IsCheckingShiftOutput ||
            now < _nextAutoShiftReconnectAt || (IsKeyboardShiftMode && IsDriveAssistArmed)) return;

        if (!IsListening)
        {
            _nextAutoShiftReconnectAt = now + 2000;
            Start();
        }
        else if (_previousShiftSnapshot is { OutputReady: false, Gear: > 0 } && _shiftInput.IsGameInForeground)
        {
            EnableAutoShiftCommand.Execute(null);
        }
    }

    private void PumpRecordWrites()
    {
        if (_recordWrite is { IsCompleted: false }) return;
        if (_recordWrite is not null)
        {
            _recordWrite.GetAwaiter().GetResult();
            _recordWrite = null;
            RefreshHistory();
            RefreshTestSummaries();
        }

        var records = TakePendingRecords();
        if (records.Length == 0) return;
        var latest = records[^1];
        StatusText = TelemetryText.Format("StatusTestCompleted", latest.LocalizedTitle, FormatResult(latest));
        if (!_settings.AutoSaveRecords) { RefreshTestSummaries(); return; }

        var samples = TakeRecentSamples();
        var max = _settings.MaxStoredRecords;
        _recordWrite = Task.Run(() => SaveRecords(records, samples, max));
    }

    private TestResult[] TakePendingRecords()
    {
        lock (_sync)
        {
            var records = _pendingRecords.ToArray();
            _pendingRecords.Clear();
            return records;
        }
    }

    private void SaveRecords(TestResult[] records, TelemetrySample[] samples, int max)
    {
        var settings = new TelemetrySettings { MaxStoredRecords = max };
        foreach (var record in records) _store.Save(record, samples, settings);
    }

    private TelemetrySample[] TakeRecentSamples()
    {
        lock (_sync)
        {
            _sessionSamples.Trim(Environment.TickCount64, _settings.Retention);
            return _sessionSamples.Snapshot();
        }
    }

    internal void MaintainRetention(long now)
    {
        if (_historyCleanup is { IsCompleted: true } pending)
        {
            _historyCleanup = null;
            var removed = pending.GetAwaiter().GetResult();
            if (removed > 0) RefreshHistory();
            RetentionStatus = _store.LastError ?? TelemetryText.Format("MsgCacheCleanedFmt", removed);
        }

        if (now < _nextCleanupAt) return;
        var retention = _settings.Retention;
        _nextCleanupAt = now + retention.CleanupIntervalSeconds * 1000L;
        if (retention.ClearCache) ClearLocalCache();
        else RetentionStatus = TelemetryText.Format("MsgChartFramesFmt", TrimCaches(now, retention));
        if (retention.CleanHistory && _historyCleanup is null)
        {
            var cutoff = DateTime.Now.AddDays(-retention.HistoryDays);
            var max = _settings.MaxStoredRecords;
            _historyCleanup = Task.Run(() => _store.PruneHistory(cutoff, max));
        }
        if (SelectedTab == (int)TelemetryTab.Charts && !ChartPaused) RefreshCharts();
    }

    [RelayCommand]
    private void ClearLocalCache()
    {
        int removedFrames;
        lock (_sync)
        {
            removedFrames = _ringBuffer.Count;
            _ringBuffer.Clear();
            _chartRevision++;
            if (_engine.CurrentState != TestRunState.Running && _pendingRecords.Count == 0) _sessionSamples.Clear();
        }
        ClearChartSnapshots();
        RetentionStatus = TelemetryText.Format("MsgLocalCacheClearedFmt", removedFrames);
    }

    private int TrimCaches(long now, TelemetryRetention retention)
    {
        lock (_sync)
        {
            var before = _ringBuffer.Count;
            var elapsed = _lastArrivalAt > 0 ? Math.Max(0, now - _lastArrivalAt) / 1000.0 : 0;
            _ringBuffer.Trim(_sessionSeconds + elapsed - retention.Seconds, retention.Frames);
            _sessionSamples.Trim(now, retention);
            if (_ringBuffer.Count != before) _chartRevision++;
            return _ringBuffer.Count;
        }
    }

    // ===== 全部参数 =====
    private void RefreshParameterGroups(TelemetrySample s, TelemetryDerived d)
    {
        _lastParameterRefreshAt = Environment.TickCount64;
        var srcDirect = TelemetryText.Get("SrcDirect");
        var srcComputed = TelemetryText.Get("SrcComputed");
        var srcEstimated = TelemetryText.Get("SrcEstimated");
        var srcUnsupported = TelemetryText.Get("SrcUnsupported");
        var srcCatalog = TelemetryText.Get("SrcCatalog");

        var groupIndex = 0;
        void UpdateGroup(string title, IReadOnlyList<ParameterRow> rows)
        {
            var index = groupIndex++;
            if (index == ParameterGroups.Count)
            {
                ParameterGroups.Add(new(title, rows));
                return;
            }

            var group = ParameterGroups[index];
            var sameLayout = group.Title == title && group.Rows.Count == rows.Count;
            for (var i = 0; sameLayout && i < rows.Count; i++) sameLayout = group.Rows[i].Name == rows[i].Name;
            if (!sameLayout) ParameterGroups[index] = new(title, rows);
            else for (var i = 0; i < rows.Count; i++) group.Rows[i].Update(rows[i]);
        }

        UpdateGroup(TelemetryText.Get("GrpVehicle"), new[]
        {
            Row(TelemetryText.Get("PrmCarOrd"), s.CarOrdinal.ToString(CultureInfo.InvariantCulture), "", srcDirect, TelemetryText.Get("PrmCarOrdNote")),
            Row(TelemetryText.Get("PrmCarClass"), s.CarClass.ToString(CultureInfo.InvariantCulture), "", srcDirect, TelemetryText.Get("PrmCarClassNote")),
            Row(TelemetryText.Get("PrmPi"), s.CarPerformanceIndex.ToString(CultureInfo.InvariantCulture), "", srcDirect, TelemetryText.Get("PrmPiNote")),
            Row(TelemetryText.Get("PrmDrive"), DrivetrainNames.Name(s.DrivetrainType), "", srcDirect, TelemetryText.Get("PrmDriveNote")),
            Row(TelemetryText.Get("PrmCyl"), s.NumCylinders.ToString(CultureInfo.InvariantCulture), "", srcDirect, ""),
            Row(TelemetryText.Get("PrmCarGroup"), s.CarGroup.ToString(CultureInfo.InvariantCulture), "", srcDirect, s.CarGroup <= 0 ? TelemetryText.Get("FrameNotProvided") : ""),
            Row(TelemetryText.Get("PrmFullName"), _carInfo?.Name ?? TelemetryText.Get("UnlistedShort"), "", srcCatalog, _carInfo is null ? TelemetryText.Get("UnlistedOrdinalNote") : TelemetryText.Get("LookupOrdinalNote")),
        });
        UpdateGroup(TelemetryText.Get("GrpCatalog"), BuildCatalogRows(_carInfo, s.CarOrdinal));
        UpdateGroup(TelemetryText.Get("GrpMotion"), new[]
        {
            Row(TelemetryText.Get("PrmSpeedKmh"), s.SpeedKmh.ToString("0.00"), "km/h", srcDirect, TelemetryText.Get("PrmSpeedKmhNote")),
            Row(TelemetryText.Get("PrmSpeedMph"), (s.SpeedKmh / 1.609344).ToString("0.00"), "mph", srcComputed, ""),
            Row(TelemetryText.Get("PrmSpeedMs"), (s.SpeedKmh / 3.6).ToString("0.00"), "m/s", srcDirect, TelemetryText.Get("PrmSpeedMsNote")),
            Row(TelemetryText.Get("PrmLongG"), d.SmoothedLongitudinalG.ToString("+0.000;-0.000;0.000"), "G", srcComputed, TelemetryText.Get("PrmLongGNote")),
            Row(TelemetryText.Get("PrmLatG"), d.SmoothedLateralG.ToString("+0.000;-0.000;0.000"), "G", srcComputed, TelemetryText.Get("PrmLatGNote")),
            Row(TelemetryText.Get("PrmVertG"), d.VerticalG.ToString("+0.000;-0.000;0.000"), "G", srcComputed, TelemetryText.Get("PrmVertGNote")),
            Row(TelemetryText.Get("PrmCombG"), d.SmoothedCombinedG.ToString("0.000"), "G", srcComputed, TelemetryText.Get("PrmCombGNote")),
            Row(TelemetryText.Get("PrmSpeedDiffAccel"), d.SpeedDerivedAccelerationMps2?.ToString("0.00") ?? srcUnsupported, "m/s²", srcComputed, TelemetryText.Get("PrmSpeedDiffAccelNote")),
            Row(TelemetryText.Get("PrmDistance"), s.DistanceTraveledM.ToString("0.0"), "m", srcDirect, TelemetryText.Get("PrmDistanceNote")),
            Row(TelemetryText.Get("PrmYawRate"), d.YawRateDegPerSec.ToString("0.0"), "°/s", srcComputed, TelemetryText.Get("PrmYawNote")),
            Row(TelemetryText.Get("PrmPitchRate"), d.PitchRateDegPerSec.ToString("0.0"), "°/s", srcComputed, TelemetryText.Get("PrmPitchNote")),
            Row(TelemetryText.Get("PrmRollRate"), d.RollRateDegPerSec.ToString("0.0"), "°/s", srcComputed, TelemetryText.Get("PrmRollNote")),
            Row(TelemetryText.Get("PrmBodySlip"), d.SlipAngleDeg?.ToString("0.0") ?? srcUnsupported, "°", srcEstimated, TelemetryText.Get("PrmBodySlipNote")),
            Row(TelemetryText.Get("PrmSteerAngle"), d.SteerAngleDeg?.ToString("0.0") ?? srcUnsupported, "°", srcEstimated, TelemetryText.Get("PrmSteerAngleNote")),
            Row(TelemetryText.Get("PrmDrivState"), DescribeState(d.State), "", srcComputed, TelemetryText.Get("PrmDrivStateNote")),
        });
        UpdateGroup(TelemetryText.Get("GrpEngine"), new[]
        {
            Row(TelemetryText.Get("PrmRpm"), s.Rpm.ToString("0"), "rpm", srcDirect, ""),
            Row(TelemetryText.Get("PrmRpmPct"), (s.EngineMaxRpm > 0 ? s.Rpm / s.EngineMaxRpm * 100 : 0).ToString("0.0"), "%", srcComputed, ""),
            Row(TelemetryText.Get("PrmIdleRpm"), s.EngineIdleRpm.ToString("0"), "rpm", srcDirect, ""),
            Row(TelemetryText.Get("PrmMaxRpm"), s.EngineMaxRpm.ToString("0"), "rpm", srcDirect, TelemetryText.Get("PrmMaxRpmNote")),
            Row(TelemetryText.Get("PrmPower"), s.PowerKw.ToString("0.0"), "kW", srcDirect, TelemetryText.Get("PrmPowerNote")),
            Row(TelemetryText.Get("PrmPowerHp"), (s.PowerKw / 0.745699872).ToString("0.0"), "hp", srcComputed, TelemetryText.Get("PrmPowerHpNote")),
            Row(TelemetryText.Get("PrmPowerPs"), (s.PowerKw / 0.73549875).ToString("0.0"), "PS", srcComputed, TelemetryText.Get("PrmPowerPsNote")),
            Row(TelemetryText.Get("PrmTorque"), s.TorqueNm.ToString("0.0"), "N·m", srcDirect, ""),
            Row(TelemetryText.Get("PrmTorqueLbFt"), (s.TorqueNm / 1.3558179483).ToString("0.0"), "lb-ft", srcComputed, ""),
            Row(TelemetryText.Get("PrmBoost"), s.BoostPsi.ToString("0.00"), "psi", srcDirect, TelemetryText.Get("PrmBoostNote")),
            Row(TelemetryText.Get("PrmFuel"), (s.FuelRatio * 100).ToString("0.0"), "%", srcDirect, ""),
            Row(TelemetryText.Get("PrmEngTemp"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmEngTempNote")),
            Row(TelemetryText.Get("PrmEngLoad"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmEngLoadNote")),
        });
        UpdateGroup(TelemetryText.Get("GrpDrivetrain"), new[]
        {
            Row(TelemetryText.Get("PrmCurGear"), s.Gear == 0 ? "R" : s.Gear.ToString(CultureInfo.InvariantCulture), "", srcDirect, ""),
            Row(TelemetryText.Get("PrmThrottle"), $"{s.Throttle * 100:0}%", "", srcDirect, TelemetryText.Get("PrmThrottleNote")),
            Row(TelemetryText.Get("PrmBrake"), $"{s.Brake * 100:0}%", "", srcDirect, ""),
            Row(TelemetryText.Get("PrmClutch"), $"{s.Clutch * 100:0}%", "", srcDirect, ""),
            Row(TelemetryText.Get("PrmHandbrake"), $"{s.HandBrake * 100:0}%", "", srcDirect, ""),
            Row(TelemetryText.Get("PrmSteerInput"), $"{s.Steer * 100:+0;-0;0}%", "", srcDirect, TelemetryText.Get("PrmSteerInputNote")),
            Row(TelemetryText.Get("PrmGearRatio"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmGearRatioNote")),
            Row(TelemetryText.Get("PrmTorqueSplit"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmTorqueSplitNote")),
        });
        UpdateGroup(TelemetryText.Get("GrpTires"), new[]
        {
            Row(TelemetryText.Get("PrmTireSlipRatio"), $"{s.FrontLeft.SlipRatio:0.00} / {s.FrontRight.SlipRatio:0.00} / {s.RearLeft.SlipRatio:0.00} / {s.RearRight.SlipRatio:0.00}", "", srcDirect, ""),
            Row(TelemetryText.Get("PrmTireSlipAngle"), $"{s.FrontLeft.SlipAngle:0.00} / {s.FrontRight.SlipAngle:0.00} / {s.RearLeft.SlipAngle:0.00} / {s.RearRight.SlipAngle:0.00}", "", srcDirect, TelemetryText.Get("PrmTireSlipAngleNote")),
            Row(TelemetryText.Get("PrmTireCombSlip"), $"{s.FrontLeft.CombinedSlip:0.00} / {s.FrontRight.CombinedSlip:0.00} / {s.RearLeft.CombinedSlip:0.00} / {s.RearRight.CombinedSlip:0.00}", "", srcDirect, ""),
            Row(TelemetryText.Get("PrmWheelSpeed"), $"{s.FrontLeft.RotationSpeedRadPerSec:0.0} / {s.FrontRight.RotationSpeedRadPerSec:0.0} / {s.RearLeft.RotationSpeedRadPerSec:0.0} / {s.RearRight.RotationSpeedRadPerSec:0.0}", "rad/s", srcDirect, ""),
            Row(TelemetryText.Get("PrmTireTemp"), $"{T(s.FrontLeft.TireTempC)} / {T(s.FrontRight.TireTempC)} / {T(s.RearLeft.TireTempC)} / {T(s.RearRight.TireTempC)}", _settings.TemperatureUnit, srcDirect, TelemetryText.Get("PrmTireTempNote")),
            Row(TelemetryText.Get("PrmTireSurface"), $"{s.FrontLeft.OnRumbleStrip} / {s.FrontLeft.PuddleDepth:0.00} / {s.FrontLeft.SurfaceRumble:0.00}", "", srcDirect, TelemetryText.Get("PrmTireSurfaceNote")),
            Row(TelemetryText.Get("PrmTirePressure"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmTirePressureNote")),
            Row(TelemetryText.Get("PrmTireTempZones"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmTireTempZonesNote")),
        });
        UpdateGroup(TelemetryText.Get("GrpSuspension"), new[]
        {
            Row(TelemetryText.Get("PrmSuspTravel"), $"{M(s.FrontLeft.SuspensionTravelMeters)} / {M(s.FrontRight.SuspensionTravelMeters)} / {M(s.RearLeft.SuspensionTravelMeters)} / {M(s.RearRight.SuspensionTravelMeters)}", "m", srcDirect, ""),
            Row(TelemetryText.Get("PrmNormSusp"), $"{s.FrontLeft.NormalizedSuspensionTravel:0.000} / {s.FrontRight.NormalizedSuspensionTravel:0.000} / {s.RearLeft.NormalizedSuspensionTravel:0.000} / {s.RearRight.NormalizedSuspensionTravel:0.000}", "", srcDirect, ""),
            Row(TelemetryText.Get("PrmAttitude"), $"{s.Yaw:0.00} / {s.Pitch:0.00} / {s.Roll:0.00}", "rad", srcDirect, ""),
            Row(TelemetryText.Get("PrmWheelLoads"), srcUnsupported, "", srcUnsupported, TelemetryText.Get("PrmWheelLoadsNote")),
        });
        UpdateGroup(TelemetryText.Get("GrpRace"), new[]
        {
            Row(TelemetryText.Get("PrmCurLap"), s.LapNumber.ToString(CultureInfo.InvariantCulture), "", srcDirect, ""),
            Row(TelemetryText.Get("PrmRacePos"), s.RacePosition.ToString(CultureInfo.InvariantCulture), "", srcDirect, ""),
            Row(TelemetryText.Get("PrmCurLapTime"), s.CurrentLapSeconds.ToString("0.000"), "s", srcDirect, ""),
            Row(TelemetryText.Get("PrmLastLapTime"), s.LastLapSeconds.ToString("0.000"), "s", srcDirect, ""),
            Row(TelemetryText.Get("PrmBestLapTime"), s.BestLapSeconds.ToString("0.000"), "s", srcDirect, ""),
            Row(TelemetryText.Get("PrmRaceTime"), s.CurrentRaceTimeSeconds.ToString("0.000"), "s", srcDirect, ""),
        });
    }

    private string T(double celsius) => TelemetryUnits.Temperature(celsius, _settings.TemperatureUnit).ToString("0.0");
    private string M(double meters) => meters.ToString("0.000");
    private static ParameterRow Row(string name, string value, string unit, string source, string detail)
        => new(name, value, unit, source, detail);

    /// <summary>标题栏车辆详情：车型档案里的静态资料拼成一行，未知字段自动省略。</summary>
    private static string DescribeCarDetail(VehicleInfo info, TelemetrySample sample)
    {
        var parts = new List<string>
        {
            TelemetryText.Format("CarDetailOrd", sample.CarOrdinal),
            TelemetryText.Format("CarDetailMake", info.MakeName),
            TelemetryText.Format("CarDetailModel", info.Model),
        };
        if (info.Year > 0) parts.Add(TelemetryText.Format("CarDetailYear", info.Year));
        if (info.CountryName.Length > 0) parts.Add(TelemetryText.Format("CarDetailOrigin", info.CountryName));
        var engine = new List<string>();
        if (info.EngineConfigName != "—") engine.Add(info.EngineConfigName);
        if (info.CylinderText != "—") engine.Add(info.CylinderText);
        if (info.DisplacementCc > 0) engine.Add((info.DisplacementCc / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " L");
        if (info.AspirationName != "—") engine.Add(info.AspirationName);
        if (engine.Count > 0) parts.Add(TelemetryText.Format("CarDetailEngine", string.Join(' ', engine)));
        if (info.NumGears > 0) parts.Add(TelemetryText.Format("CarDetailGears", info.NumGears));
        if (info.CurbWeightKg > 0) parts.Add(TelemetryText.Format("CarDetailWeight", info.CurbWeightKg));
        parts.Add(TelemetryText.Format("CarDetailPi", sample.CarPerformanceIndex));
        return string.Join(" · ", parts);
    }

    /// <summary>“全部参数”页的车型档案分组：全部来自本地离线车型数据库，不依赖游戏连接。</summary>
    private static IReadOnlyList<ParameterRow> BuildCatalogRows(VehicleInfo? info, int carOrdinal)
    {
        var srcUnsupported = TelemetryText.Get("SrcUnsupported");
        var srcCatalog = TelemetryText.Get("SrcCatalog");
        if (info is null)
            return
            [
                Row(TelemetryText.Get("PrmCatRecord"), TelemetryText.Get("UnlistedShort"), "", srcUnsupported,
                    TelemetryText.Format("UnlistedCatalogDetail", carOrdinal)),
            ];
        var rows = new List<ParameterRow>
        {
            Row(TelemetryText.Get("PrmFullName"), info.Name, "", srcCatalog, "Data_Car.Id = " + info.CarId.ToString(CultureInfo.InvariantCulture)),
            Row(TelemetryText.Get("PrmCatEnName"), info.NameEn, "", srcCatalog, TelemetryText.Get("PrmCatEnNameNote")),
            Row(TelemetryText.Get("PrmMake"), info.MakeName, "", srcCatalog, "List_CarMake：" + info.MakeEn),
            Row(TelemetryText.Get("PrmModel"), info.Model, "", srcCatalog, TelemetryText.Get("PrmModelNote")),
        };
        if (info.Year > 0) rows.Add(Row(TelemetryText.Get("PrmYear"), info.Year.ToString(CultureInfo.InvariantCulture), "", srcCatalog, "Data_Car.Year"));
        if (info.CountryName.Length > 0) rows.Add(Row(TelemetryText.Get("PrmOrigin"), info.CountryName, "", srcCatalog, "List_Country：" + info.CountryEn));
        rows.Add(Row(TelemetryText.Get("PrmDrive"), info.DriveName, "", srcCatalog, TelemetryText.Get("PrmCatDriveNote")));
        rows.Add(Row(TelemetryText.Get("PrmEngLayout"), info.EngineConfigName, "", srcCatalog, "List_EngineConfig"));
        if (!info.IsElectric) rows.Add(Row(TelemetryText.Get("PrmAspiration"), info.AspirationName, "", srcCatalog, "List_Aspiration"));
        rows.Add(Row(TelemetryText.Get("PrmCyl"), info.CylinderText, "", srcCatalog, "List_Cylinders"));
        if (info.DisplacementCc > 0)
            rows.Add(Row(TelemetryText.Get("PrmDisplacement"), info.DisplacementCc.ToString("0", CultureInfo.InvariantCulture), "cc",
                srcCatalog, TelemetryText.Format("PrmDispNoteFmt", info.DisplacementCc / 1000)));
        if (info.NumGears > 0) rows.Add(Row(TelemetryText.Get("PrmStockGears"), info.NumGears.ToString(CultureInfo.InvariantCulture), TelemetryText.Get("PrmGearUnit"), srcCatalog, "Data_Car.NumGears"));
        if (info.CurbWeightKg > 0) rows.Add(Row(TelemetryText.Get("PrmCurbWeight"), info.CurbWeightKg.ToString("0.#", CultureInfo.InvariantCulture), "kg", srcCatalog, TelemetryText.Get("PrmWeightNote")));
        if (info.WeightDistributionText != "—") rows.Add(Row(TelemetryText.Get("PrmWeightDist"), info.WeightDistributionText, TelemetryText.Get("PrmWeightDistUnit"), srcCatalog, "Data_Car.WeightDistribution"));
        if (info.TireText != "—") rows.Add(Row(TelemetryText.Get("PrmStockTires"), info.TireText, "", srcCatalog, TelemetryText.Get("PrmStockTiresNote")));
        if (info.PeakPowerKw > 0)
            rows.Add(Row(TelemetryText.Get("PrmNomPower"), info.PeakPowerKw.ToString("0.#", CultureInfo.InvariantCulture), "kW", srcCatalog,
                TelemetryText.Format("PrmNomPowerNoteFmt", info.PeakPowerKw / 0.73549875)));
        if (info.PeakTorqueNm > 0)
            rows.Add(Row(TelemetryText.Get("PrmNomTorque"), info.PeakTorqueNm.ToString("0.#", CultureInfo.InvariantCulture), "N·m", srcCatalog,
                TelemetryText.Get("PrmNomTorqueNote")));
        if (info.TopSpeedKmh > 0)
            rows.Add(Row(TelemetryText.Get("PrmNomTopSpeed"), info.TopSpeedKmh.ToString("0.#", CultureInfo.InvariantCulture), "km/h", srcCatalog,
                TelemetryText.Get("PrmNomTopSpeedNote")));
        if (info.RedlineRpm > 0)
            rows.Add(Row(TelemetryText.Get("PrmStockRedline"), info.RedlineRpm.ToString("0", CultureInfo.InvariantCulture), "rpm", srcCatalog,
                TelemetryText.Get("PrmStockRedlineNote")));
        return rows;
    }

    // ===== 曲线 =====
    private void RefreshCharts()
    {
        var window = Math.Clamp(ChartWindowSeconds, 5, 600);
        var now = Environment.TickCount64;
        lock (_sync)
        {
            if (window == _drawnChartWindow && (_chartRevision == _drawnChartRevision || now - _lastChartRefreshAt < 100)) return;
            _drawnChartRevision = _chartRevision;
            _drawnChartWindow = window;
            _lastChartRefreshAt = now;
        }
        var limit = _settings.Retention.ChartPointLimit;
        (double[] Times, double[] Values) Read(TelemetryRingBuffer.Channel channel)
        {
            lock (_sync) return _ringBuffer.Window(channel, window, limit);
        }

        var (speedTimes, speedValues) = Read(TelemetryRingBuffer.Channel.Speed);
        SpeedSeries = [new(TelemetryText.Get("SeriesSpeed"), speedTimes, speedValues, Color.Parse("#FF9F43"), "km/h")];

        if (ShowGearChart)
        {
            (double[] Times, double[] Values) gears;
            lock (_sync) gears = _ringBuffer.GearWindow(window, limit);
            GearSeries = [new(TelemetryText.Get("SeriesGear"), gears.Times, gears.Values, Color.Parse("#80D8ED"), "", GearSteps: true)];
        }

        var (powerTimes, powerValues) = Read(TelemetryRingBuffer.Channel.Power);
        var (torqueTimes, torqueValues) = Read(TelemetryRingBuffer.Channel.Torque);
        PowerTorqueSeries =
        [
            new(TelemetryText.Get("SeriesPower"), powerTimes, powerValues, Color.Parse("#4DA3FF"), "kW"),
            new(TelemetryText.Get("SeriesTorque"), torqueTimes, torqueValues, Color.Parse("#7BD88F"), "N·m"),
        ];

        var (longTimes, longValues) = Read(TelemetryRingBuffer.Channel.LongitudinalG);
        var (latTimes, latValues) = Read(TelemetryRingBuffer.Channel.LateralG);
        AccelerationSeries =
        [
            new(TelemetryText.Get("SeriesLongG"), longTimes, longValues, Color.Parse("#FF9F43"), "G"),
            new(TelemetryText.Get("SeriesLatG"), latTimes, latValues, Color.Parse("#E5534B"), "G"),
        ];

        var (throttleTimes, throttleValues) = Read(TelemetryRingBuffer.Channel.Throttle);
        var (brakeTimes, brakeValues) = Read(TelemetryRingBuffer.Channel.Brake);
        var (steerTimes, steerValues) = Read(TelemetryRingBuffer.Channel.Steer);
        InputSeries =
        [
            new(TelemetryText.Get("SeriesThrottle"), throttleTimes, throttleValues, Color.Parse("#7BD88F"), ""),
            new(TelemetryText.Get("SeriesBrake"), brakeTimes, brakeValues, Color.Parse("#E5534B"), ""),
            new(TelemetryText.Get("SeriesSteer"), steerTimes, steerValues, Color.Parse("#4DA3FF"), ""),
        ];
        ChartsChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleChartPause() => ChartPaused = !ChartPaused;

    partial void OnShowGearChartChanged(bool value)
    {
        if (_settings.ShowGearChart == value) return;
        _settings.ShowGearChart = value;
        _drawnChartWindow = 0;
        if (!value) GearSeries = [];
        if (SelectedTab == (int)TelemetryTab.Charts && !ChartPaused) RefreshCharts();
        else ChartsChanged?.Invoke();
        SettingsStatus = _preferences.Save(_settings) ?? "";
    }

    [RelayCommand]
    private void ClearCharts()
    {
        lock (_sync) { _ringBuffer.Clear(); _chartRevision++; }
        ClearChartSnapshots();
    }

    private void ClearChartSnapshots()
    {
        _drawnChartWindow = 0;
        SpeedSeries = GearSeries = PowerTorqueSeries = AccelerationSeries = InputSeries = [];
        ChartsChanged?.Invoke();
    }

    // ===== 测试 =====
    [RelayCommand]
    private void AddCustomAcceleration()
    {
        if (!TryParseRange(CustomAccelerationRange, out var from, out var to) || to <= from || from < 0 || to > 600)
        {
            StatusText = TelemetryText.Get("MsgBadAccelRange");
            return;
        }
        AddTest(TestCatalog.CustomAcceleration(from, to));
    }

    [RelayCommand]
    private void AddCustomBraking()
    {
        if (!double.TryParse(CustomBrakingSpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out var from) || from is < 5 or > 600)
        {
            StatusText = TelemetryText.Get("MsgBadBrakeSpeed");
            return;
        }
        AddTest(TestCatalog.CustomBraking(from));
    }

    [RelayCommand]
    private void AddCustomDistance()
    {
        if (!double.TryParse(CustomDistanceMeters, NumberStyles.Float, CultureInfo.InvariantCulture, out var meters) || meters is < 10 or > 10000)
        {
            StatusText = TelemetryText.Get("MsgBadDistMeters");
            return;
        }
        AddTest(TestCatalog.CustomDistance(meters));
    }

    private void AddTest(TestDefinition definition)
    {
        if (EnabledTests.Any(t => t.Key == definition.Key)) { StatusText = TelemetryText.Get("MsgTestExists"); return; }
        EnabledTests.Add(definition);
        _engine.SetEnabled(EnabledTests);
        _engine.Reset();
        StatusText = TelemetryText.Format("MsgTestAdded", definition.RangeText);
    }

    [RelayCommand]
    private void RemoveTest(TestDefinition? definition)
    {
        if (definition is null) return;
        EnabledTests.Remove(definition);
        _engine.SetEnabled(EnabledTests);
        StatusText = TelemetryText.Format("MsgTestRemoved", definition.LocalizedTitle);
    }

    [RelayCommand]
    private void FinishTopSpeed()
    {
        TelemetrySample? last;
        lock (_sync) last = _latest;
        var result = _engine.FinishTopSpeed(last);
        if (result is null) { StatusText = TelemetryText.Get("MsgTopSpeedNotStarted"); return; }
        lock (_sync) _pendingRecords.Add(result);
        PumpRecordWrites();
        StatusText = TelemetryText.Format("MsgTopSpeedDone", FormatResult(result));
    }

    private static bool TryParseRange(string text, out double from, out double to)
    {
        from = to = 0;
        var parts = text.Replace('–', '-').Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out from)
                               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out to);
    }

    private void RefreshTestSummaries()
    {
        var saved = _store.Records.Where(record => record.Valid).GroupBy(record => record.Key)
            .ToDictionary(group => group.Key, group => group.Count());
        var summaries = new List<TestSummary>();
        lock (_sync)
        {
            foreach (var definition in EnabledTests)
            {
                var best = _bestByKey.TryGetValue(definition.Key, out var localBest) ? localBest : double.MaxValue;
                var average = _historyByKey.TryGetValue(definition.Key, out var history) && history.Count > 0
                    ? history.Sum / history.Count : (double?)null;
                var last = history.Count > 0 ? history.Last : (double?)null;
                summaries.Add(new TestSummary(
                    definition.Key, definition.RangeText,
                    best == double.MaxValue ? "—" : $"{best:0.000} s",
                    last is null ? "—" : $"{last:0.000} s",
                    average is null ? "—" : $"{average:0.000} s",
                    saved.GetValueOrDefault(definition.Key) + history.Count));
            }
        }
        TestSummaries.Clear();
        foreach (var summary in summaries) TestSummaries.Add(summary);
    }

    private static string FormatResult(TestResult record) => record.Kind switch
    {
        PerformanceTestKind.DriveAssist when record.DriveAssist is { } run =>
            TelemetryText.Format("EcuRunStats", run.Seconds, run.MeanSpeedKmh, run.MeanLateralG, run.PeakLateralG),
        PerformanceTestKind.Braking => TelemetryText.Format("FmtBrakingResult", record.DurationSeconds, record.DistanceMeters),
        PerformanceTestKind.TopSpeed => TelemetryText.Format("FmtTopSpeedResult", record.PeakSpeedKmh),
        PerformanceTestKind.Distance => TelemetryText.Format("FmtDistResult", record.DurationSeconds, record.EndSpeedKmh),
        _ => TelemetryText.Format("FmtDefaultResult", record.DurationSeconds),
    };

    // ===== 历史 =====
    partial void OnHistoryFilterChanged(string value) => RefreshHistory();
    partial void OnHistoryVehicleFilterChanged(string value) => RefreshHistory();

    private void RefreshHistory(bool force = false)
    {
        if (_refreshingHistory) return;
        _refreshingHistory = true;
        try
        {
            var available = _store.Records.Select(WithResolvedCarName).ToArray();
            var vehicles = available.Select(record => record.CarName).Distinct().OrderBy(vehicle => vehicle).Prepend(_allVehiclesLabel).ToArray();
            var selected = vehicles.Contains(HistoryVehicleFilter) ? HistoryVehicleFilter : _allVehiclesLabel;
            if (!HistoryVehicles.SequenceEqual(vehicles))
            {
                HistoryVehicles.Clear();
                foreach (var vehicle in vehicles) HistoryVehicles.Add(vehicle);
            }
            HistoryVehicleFilter = selected;

            var records = available.Where(r => selected == _allVehiclesLabel || r.CarName == selected)
                .Where(r => string.IsNullOrWhiteSpace(HistoryFilter) ||
                            r.Title.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase) ||
                            r.LocalizedTitle.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase) ||
                            r.CarName.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase) ||
                            r.Note.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.RecordedAtLocal).Take(300).ToArray();
            if (force || !HistoryRecords.SequenceEqual(records))
            {
                var baseId = CompareBase?.Id;
                var targetId = CompareTarget?.Id;
                HistoryRecords.Clear();
                CompareOptions.Clear();
                foreach (var record in records)
                {
                    HistoryRecords.Add(record);
                    CompareOptions.Add(record);
                }
                CompareBase = baseId is null ? null : CompareOptions.FirstOrDefault(r => r.Id == baseId);
                CompareTarget = targetId is null ? null : CompareOptions.FirstOrDefault(r => r.Id == targetId);
            }
            HistoryInfoText = TelemetryText.Format("HistInfoFmt", records.Length, _store.DescribeIntegrity()) +
                              (string.IsNullOrEmpty(_store.LastError) ? "" : " · " + _store.LastError);
        }
        finally { _refreshingHistory = false; }
    }

    /// <summary>旧版本记录的 CarName 为空：按车型编号查档案补齐；仍未知时回退为“车型 N”。</summary>
    private static TestResult WithResolvedCarName(TestResult record)
    {
        if (record.CarName.Length > 0) return record;
        var name = VehicleCatalog.DisplayName(record.CarOrdinal);
        return record with { CarName = name.Length > 0 ? name : TelemetryText.Format("CarUnknownTitle", record.CarOrdinal) };
    }

    [RelayCommand]
    private void DeleteRecord(TestResult? record)
    {
        if (record is null) return;
        _store.Delete(record.Id);
        RefreshHistory();
        StatusText = TelemetryText.Format("MsgDeletedOne", record.LocalizedTitle, record.RecordedAtLocal);
    }

    [RelayCommand]
    private void DeleteAllRecords()
    {
        _recordWrite?.GetAwaiter().GetResult();
        _recordWrite = null;
        lock (_sync) _pendingRecords.Clear();
        _store.DeleteAll();
        RefreshHistory();
        StatusText = TelemetryText.Get("MsgDeletedAll");
    }

    [RelayCommand]
    private void EditNote(TestResult? record)
    {
        if (record is null) { StatusText = TelemetryText.Get("MsgSelectRecord"); return; }
        var updated = record with { Note = record.Note.Length >= 40 ? "" : record.Note + " [" + TelemetryText.Get("NoteTagPrefix") + " " + DateTime.Now.ToString("HH:mm") + "]" };
        _store.Update(updated);
        RefreshHistory();
        StatusText = TelemetryText.Get("MsgNoteUpdated");
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var path = _store.ExportCsv(HistoryRecords);
        StatusText = TelemetryText.Format("MsgCsvExported", path);
    }

    [RelayCommand]
    private void ExportJson()
    {
        var path = _store.ExportJson(HistoryRecords);
        StatusText = TelemetryText.Format("MsgJsonExported", path);
    }

    [RelayCommand]
    private void ExportHtmlReport()
    {
        var parameters = _vehicleParameterSnapshot.Count > 0 ? _vehicleParameterSnapshot : null;
        var path = _store.ExportHtmlReport(HistoryRecords, parameters);
        StatusText = TelemetryText.Format("MsgHtmlExported", path);
    }

    [RelayCommand]
    private void BackupStore()
    {
        var path = _store.Backup();
        StatusText = TelemetryText.Format("MsgBackedUp", path);
    }

    [RelayCommand]
    private void OpenStoreFolder()
    {
        try
        {
            var directory = _store.RootPath;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText = TelemetryText.Format("MsgOpenDirFailed", ex.Message); }
    }

    // ===== 调校对比 =====
    /// <summary>保存当前车辆参数快照（供报告使用）；快照来自车辆参数模块的只读读取。</summary>
    [RelayCommand]
    private void SaveVehicleSnapshot()
    {
        _vehicleParameterSnapshot.Clear();
        lock (_sync)
        {
            if (_latest is null) { StatusText = TelemetryText.Get("SnapshotNoData"); return; }
            var info = _carInfo;
            _vehicleParameterSnapshot[TelemetryText.Get("PrmCarOrd")] = _latest.CarOrdinal.ToString(CultureInfo.InvariantCulture);
            if (info is not null)
            {
                _vehicleParameterSnapshot[TelemetryText.Get("PrmFullName")] = info.Name;
                _vehicleParameterSnapshot[TelemetryText.Get("PrmMake")] = info.MakeName;
                _vehicleParameterSnapshot[TelemetryText.Get("PrmModel")] = info.Model;
                if (info.Year > 0) _vehicleParameterSnapshot[TelemetryText.Get("PrmYear")] = info.Year.ToString(CultureInfo.InvariantCulture);
                if (info.CountryName.Length > 0) _vehicleParameterSnapshot[TelemetryText.Get("PrmOrigin")] = info.CountryName;
            }
            else _vehicleParameterSnapshot[TelemetryText.Get("PrmFullName")] = TelemetryText.Format("CarUnknownTitle", _latest.CarOrdinal);
            _vehicleParameterSnapshot["PI"] = _latest.CarPerformanceIndex.ToString(CultureInfo.InvariantCulture);
            _vehicleParameterSnapshot[TelemetryText.Get("PrmDrive")] = DrivetrainNames.Name(_latest.DrivetrainType);
            _vehicleParameterSnapshot[TelemetryText.Get("PrmMaxRpm")] = _latest.EngineMaxRpm.ToString("0");
        }
        _hasSnapshotSaved = true;
        SnapshotInfoText = TelemetryText.Format("SnapshotSavedInfo", string.Join(" · ", _vehicleParameterSnapshot.Select(kv => $"{kv.Key}={kv.Value}")));
        StatusText = TelemetryText.Get("SnapshotSavedToast");
    }

    [RelayCommand]
    private void GenerateComparison()
    {
        CompareRows.Clear();
        if (CompareBase is null || CompareTarget is null)
        {
            _hasComparisonGenerated = false;
            CompareSummary = TelemetryText.Get("CmpNeedBoth");
            return;
        }
        _hasComparisonGenerated = true;
        var same = CompareBase.Key == CompareTarget.Key;
        CompareRows.Add(TelemetryText.Format("CmpRowBase", CompareBase.LocalizedTitle, CompareBase.RecordedAtLocal, FormatResult(CompareBase)));
        CompareRows.Add(TelemetryText.Format("CmpRowTarget", CompareTarget.LocalizedTitle, CompareTarget.RecordedAtLocal, FormatResult(CompareTarget)));
        var delta = CompareTarget.DurationSeconds - CompareBase.DurationSeconds;
        var percent = CompareBase.DurationSeconds > 0 ? delta / CompareBase.DurationSeconds * 100 : 0;
        if (!same) CompareRows.Add(TelemetryText.Get("CmpRowDifferentKind"));
        if (CompareBase.Kind != PerformanceTestKind.DriveAssist && CompareTarget.Kind != PerformanceTestKind.DriveAssist)
        {
            if (same) CompareRows.Add(TelemetryText.Format("CmpRowDelta", delta, percent));
            CompareRows.Add(TelemetryText.Format("CmpRowPower", CompareBase.PeakPowerKw, CompareTarget.PeakPowerKw, CompareTarget.PeakPowerKw - CompareBase.PeakPowerKw));
            CompareRows.Add(TelemetryText.Format("CmpRowTorque", CompareBase.PeakTorqueNm, CompareTarget.PeakTorqueNm));
            CompareRows.Add(TelemetryText.Format("CmpRowSpeed", CompareBase.PeakSpeedKmh, CompareTarget.PeakSpeedKmh));
            CompareRows.Add(TelemetryText.Format("CmpRowMaxG", CompareBase.MaxAccelerationG, CompareTarget.MaxAccelerationG));
        }
        if (CompareBase.DriveAssist is { } baseRun) CompareRows.Add(TelemetryText.Get("EcuCompareBase") + Environment.NewLine + FormatDriveAssistRun(baseRun));
        if (CompareTarget.DriveAssist is { } targetRun) CompareRows.Add(TelemetryText.Get("EcuCompareTarget") + Environment.NewLine + FormatDriveAssistRun(targetRun));
        if (same && CompareBase.DriveAssist is { } baseline && CompareTarget.DriveAssist is { } comparison)
            CompareRows.Add(TelemetryText.Format("EcuCompareDelta", comparison.MeanLateralG - baseline.MeanLateralG, comparison.PeakLateralG - baseline.PeakLateralG));
        CompareRows.Add(TelemetryText.Format("CmpRowCars", CompareBase.CarName, CompareBase.CarPerformanceIndex, CompareTarget.CarName, CompareTarget.CarPerformanceIndex));
        if (CompareBase.CarOrdinal != CompareTarget.CarOrdinal)
            CompareSummary = TelemetryText.Get("CmpDiffCars");
        else if (CompareBase.CarPerformanceIndex != CompareTarget.CarPerformanceIndex)
            CompareSummary = TelemetryText.Get("CmpDiffPi");
        else
            CompareSummary = TelemetryText.Get("CmpSameCarPi");
    }


    // ===== 诊断 =====
    private void RefreshDiagnostics()
    {
        var receiver = _receiver;
        DiagnosticsText = TelemetryText.Format("DiagReportBody",
            receiver.ListenEndpoint ?? "—", _settings.ListenAddress, _settings.Port,
            TelemetryText.Get(receiver.IsRunning ? "DiagRunning" : "DiagStopped"), ConnectionStateText,
            receiver.PacketsReceived, receiver.PacketsAccepted, receiver.ParseFailures, receiver.WrongSource,
            receiver.AveragePacketsPerSecond, receiver.LastPacketLength,
            receiver.LastPacketUtc == DateTime.MinValue ? TelemetryText.Get("DiagNever") : receiver.LastPacketUtc.ToLocalTime().ToString("HH:mm:ss"),
            ImplementedFieldCount, VehicleCatalog.Count, _carName.Length > 0 ? _carName : TelemetryText.Get("DiagCarUnknown"),
            TestStateText, EnabledTests.Count, _settings.UiRefreshHz);
        DiagnosticsDetail = TelemetryText.Get("DiagTroubleshootBody");
        var assist = RefreshDriveAssist(includeReports: true);
        if (assist.Segment is not null)
            DiagnosticsDetail += Environment.NewLine + Environment.NewLine + TelemetryText.Get("EcuFeedback") + Environment.NewLine + DriveAssistReport;
        if (assist.PreviousLap is not null)
            DiagnosticsDetail += Environment.NewLine + Environment.NewLine + TelemetryText.Get("ecu_lap") + Environment.NewLine + DriveAssistLapReport;
    }

    private const int ImplementedFieldCount = 60;

    [RelayCommand]
    private void ClearDiagnostics()
    {
        _receiver.ResetCounters();
        RefreshDiagnostics();
        StatusText = TelemetryText.Get("MsgDiagCleared");
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var path = Path.Combine(_store.RootPath, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, DiagnosticsText + Environment.NewLine + Environment.NewLine + DiagnosticsDetail);
            StatusText = TelemetryText.Format("MsgDiagExported", path);
        }
        catch (Exception ex) { StatusText = TelemetryText.Format("MsgDiagExportFailed", ex.Message); }
    }

    // ===== 设置 =====
    private void LoadRetentionFields(TelemetryRetention retention)
    {
        SettingsRetentionPresetIndex = (int)retention.Preset;
        SettingsRetentionSeconds = retention.Seconds.ToString(CultureInfo.InvariantCulture);
        SettingsRetentionFrames = retention.Frames.ToString(CultureInfo.InvariantCulture);
        SettingsCleanupInterval = retention.CleanupIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        SettingsClearCache = retention.ClearCache;
        SettingsCleanHistory = retention.CleanHistory;
        SettingsHistoryDays = retention.HistoryDays.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnSettingsRetentionPresetIndexChanged(int value)
    {
        if (SuppressIndexSideEffects || value < 0) return;
        var preset = (TelemetryRetentionPreset)value;
        if (preset == TelemetryRetentionPreset.Custom || !Enum.IsDefined(preset)) return;
        var (seconds, frames) = TelemetryRetention.Limits(preset);
        SettingsRetentionSeconds = seconds.ToString(CultureInfo.InvariantCulture);
        SettingsRetentionFrames = frames.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryReadRetention(out TelemetryRetention retention)
    {
        retention = _settings.Retention;
        var preset = (TelemetryRetentionPreset)SettingsRetentionPresetIndex;
        if (!Enum.IsDefined(preset) || !int.TryParse(SettingsCleanupInterval, out var interval) || interval is < 5 or > 3600 ||
            !int.TryParse(SettingsHistoryDays, out var historyDays) || historyDays is < 1 or > 3650) return false;
        var (seconds, frames) = TelemetryRetention.Limits(preset);
        if (preset == TelemetryRetentionPreset.Custom &&
            (!int.TryParse(SettingsRetentionSeconds, out seconds) || seconds is < 1 or > TelemetryRetention.MaximumSeconds ||
             !int.TryParse(SettingsRetentionFrames, out frames) || frames is < 10 or > TelemetryRetention.MaximumFrames)) return false;

        retention = new TelemetryRetention { Preset = preset, Seconds = seconds, Frames = frames,
            CleanupIntervalSeconds = interval, ClearCache = SettingsClearCache, CleanHistory = SettingsCleanHistory, HistoryDays = historyDays };
        return true;
    }

    private string? ApplySettings()
    {
        if (!TryReadRetention(out var retention)) return TelemetryText.Get("MsgRetRangeErr");
        _settings.ListenAddress = SettingsAddress.Trim();
        if (int.TryParse(SettingsPort, out var port) && port is >= 1024 and <= 65535) _settings.Port = port;
        if (SettingsSpeedUnit is "km/h" or "mph" or "m/s") _settings.SpeedUnit = SettingsSpeedUnit;
        if (SettingsPowerUnit is "kW" or "hp" or "PS") _settings.PowerUnit = SettingsPowerUnit;
        if (SettingsTorqueUnit is "N·m" or "lb-ft") _settings.TorqueUnit = SettingsTorqueUnit;
        if (int.TryParse(SettingsRefreshHz, out var hz) && hz is >= 5 and <= 120) _settings.UiRefreshHz = hz;
        if (int.TryParse(SettingsSmoothing, out var alpha) && alpha is >= 1 and <= 100) _settings.SmoothingAlpha = alpha;
        if (double.TryParse(SettingsMaxTestSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && max is >= 5 and <= 3600)
            _settings.MaxTestSeconds = max;
        _settings.AutoSaveRecords = SettingsAutoSave;
        if (_settings.Retention != retention)
        {
            var now = Environment.TickCount64;
            _settings.Retention = retention;
            _nextCleanupAt = now + retention.CleanupIntervalSeconds * 1000L;
            TrimCaches(now, retention);
            _drawnChartWindow = 0;
        }
        return _preferences.Save(_settings);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SettingsStatus = ApplySettings() ?? TelemetryText.Get("MsgSettingsSaved");
        RefreshDiagnostics();
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var defaults = new TelemetrySettings();
        SettingsAddress = defaults.ListenAddress;
        SettingsPort = defaults.Port.ToString(CultureInfo.InvariantCulture);
        SettingsSpeedUnit = defaults.SpeedUnit;
        SettingsPowerUnit = defaults.PowerUnit;
        SettingsTorqueUnit = defaults.TorqueUnit;
        SettingsRefreshHz = defaults.UiRefreshHz.ToString(CultureInfo.InvariantCulture);
        SettingsSmoothing = defaults.SmoothingAlpha.ToString(CultureInfo.InvariantCulture);
        SettingsMaxTestSeconds = defaults.MaxTestSeconds.ToString(CultureInfo.InvariantCulture);
        SettingsAutoSave = defaults.AutoSaveRecords;
        LoadRetentionFields(defaults.Retention);
        SettingsStatus = ApplySettings() ?? TelemetryText.Get("MsgSettingsReset");
    }

    private string DescribeState(DrivingState state) => state switch
    {
        DrivingState.Idle => TelemetryText.Get("DsIdle"),
        DrivingState.Accelerating => TelemetryText.Get("DsAccel"),
        DrivingState.HardAcceleration => TelemetryText.Get("DsHardAccel"),
        DrivingState.Cruising => TelemetryText.Get("DsCruise"),
        DrivingState.Braking => TelemetryText.Get("DsBrake"),
        DrivingState.HardBraking => TelemetryText.Get("DsHardBrake"),
        DrivingState.TurningLeft => TelemetryText.Get("DsLeft"),
        DrivingState.TurningRight => TelemetryText.Get("DsRight"),
        DrivingState.DriftCandidate when _settings.AutomaticShift.DriftPredictionEnabled => TelemetryText.Get("DsDrift"),
        DrivingState.RearSliding or DrivingState.DriftCandidate => TelemetryText.Get("DsRearSlide"),
        DrivingState.TrailBraking => TelemetryText.Get("IntentTrailBraking"),
        _ => "—",
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        TelemetryText.LanguageChanged -= OnLanguageChanged;
        EnableAutoShiftCommand.Cancel();
        CheckShiftOutputCommand.Cancel();
        ArmDriveAssistCommand.Cancel();
        DisposeAssistAnalysis();
        _shiftInput.ConfigureDriveAssist(null);
        IsListening = false;
        _shiftInput.ShortcutKeyPressed -= OnDesktopShortcutPressed;
        _shiftShortcutTimer.Stop();
        _autoShift.Dispose();
        _slowTimer.Dispose();
        _receiver.SampleReceived -= OnSample;
        _receiver.Dispose();
        DetachDriveRecorder()?.StopAsync().GetAwaiter().GetResult();
        _driveFlush?.GetAwaiter().GetResult();
        _recordWrite?.GetAwaiter().GetResult();
        _historyCleanup?.GetAwaiter().GetResult();
        var records = TakePendingRecords();
        if (records.Length > 0 && _settings.AutoSaveRecords) SaveRecords(records, TakeRecentSamples(), _settings.MaxStoredRecords);
    }
}
