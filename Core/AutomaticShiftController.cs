using System;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

public sealed record AutomaticShiftSettings
{
    public GamepadShiftMode GamepadMode { get; init; } = GamepadShiftMode.Keyboard;
    public int PressDurationMs { get; init; } = 120;
    public int? ShiftLatencyMs { get; init; }
    public bool AutomaticClutch { get; init; } = true;
    public bool AutoDetectRaceTransmission { get; init; }
    public bool UpshiftGripProtection { get; init; }
    public bool ProtectLaunchWheelspin { get; init; }
    public bool ProtectAirborne { get; init; } = true;
    public bool ProtectShiftReengagement { get; init; }
    public bool AdaptiveGearMatching { get; init; }
    public bool StrictSpeedGearMatching { get; init; }
    public bool DriftMode { get; init; }
    public bool EnableShiftShortcut { get; init; }
    public ShiftAlgorithm Algorithm { get; init; } = ShiftAlgorithm.LazyForza;
    public VirtualTcuMode VirtualTcuMode { get; init; } = VirtualTcuMode.Comfort;
    public int? MaximumGear { get; init; }
    public string KeyboardUpshift { get; init; } = "E";
    public string KeyboardDownshift { get; init; } = "Q";
    public string KeyboardClutch { get; init; } = "左 Shift";
    public string KeyboardThrottle { get; init; } = "W";
    public string ToggleKeyboardKey { get; init; } = "F9";
    public ShiftShortcutButton ToggleGamepadButton { get; init; }
    public string DriveAssistToggleKeyboardKey { get; init; } = "";
    public ShiftShortcutButton DriveAssistToggleGamepadButton { get; init; }
    public string EmergencyKeyboardKey { get; init; } = "Pause";
    public ShiftShortcutButton EmergencyGamepadButton { get; init; }
    public string DriftExitKeyboardKey { get; init; } = "F11";
    public ShiftShortcutButton DriftExitGamepadButton { get; init; }
    public string ClutchToggleKeyboardKey { get; init; } = "";
    public ShiftShortcutButton ClutchToggleGamepadButton { get; init; }
    public string HudToggleKeyboardKey { get; init; } = "";
    public ShiftShortcutButton HudToggleGamepadButton { get; init; }
    public string StartRecordingKeyboardKey { get; init; } = "";
    public ShiftShortcutButton StartRecordingGamepadButton { get; init; }
    public string StopRecordingKeyboardKey { get; init; } = "";
    public ShiftShortcutButton StopRecordingGamepadButton { get; init; }

    internal const int MaximumForwardGears = 10;
    internal const int MaximumShiftLatencyMs = 900;
    internal bool UsesGearProfiles => AdaptiveGearMatching || StrictSpeedGearMatching;
    internal bool DriftPredictionEnabled => DriftMode ||
        (!StrictSpeedGearMatching && Algorithm == ShiftAlgorithm.VirtualTcu && VirtualTcuMode == VirtualTcuMode.Drift);

    internal bool TryValidate(out string error)
    {
        error = !Enum.IsDefined(GamepadMode) ? TelemetryText.Get("ValChooseOutput")
            : !Enum.IsDefined(Algorithm) ? TelemetryText.Get("ValChooseStrategy")
            : !Enum.IsDefined(VirtualTcuMode) ? TelemetryText.Get("ValChooseMode3Drive")
            : PressDurationMs is < 50 or > 250 ? TelemetryText.Get("ValPressDurationRange")
            : ShiftLatencyMs is < 0 or > MaximumShiftLatencyMs ? TelemetryText.Format("ValLatencyRange", MaximumShiftLatencyMs)
            : MaximumGear is < 1 or > MaximumForwardGears ? TelemetryText.Get("ValMaxGearRange")
            : GamepadMode == GamepadShiftMode.Keyboard && !KeyboardShiftKeys.TryParse(this, out _)
                ? TelemetryText.Get("ValKbKeysInvalid")
                : EnableShiftShortcut ? ShiftShortcut.Validate(this) ?? "" : "";
        return error.Length == 0;
    }
}

// Preserve the IDs used by saved preferences and recordings.
public enum GamepadShiftMode { Combination = 0, Keyboard = 2 }
public enum ShiftAlgorithm { LazyForza, PowerDrop, VirtualTcu, Fh6AutoShift, Mode5 = 4 }
public enum VirtualTcuMode { Comfort, Race, Drift, OffRoad }
internal enum ShiftDirection { Up, Down }

internal readonly record struct ShiftPedals(double Throttle, double Brake)
{
    public bool Braking => Brake >= BrakeShiftStrategy.MinimumBrake && (Brake >= 0.50 || Brake >= Throttle * 0.55);
    public bool WantsPower => Throttle >= 0.38 && Brake <= 0.22 && Throttle >= Brake * 4;
    public bool Kickdown => WantsPower && Throttle >= 0.82;
    public bool Coasting => Throttle <= 0.12 && Brake < 0.08;
}

internal interface IAutomaticShiftInput : IDisposable
{
    bool IsGameInForeground { get; }
    bool ManualShiftHeld => false;
    bool IsShiftPulseActive => false;
    bool DriveAssistBlocksShift => false;
    ShiftPedals? DriverPedals => null;
    event Action<byte>? ShortcutKeyPressed { add { } remove { } }
    bool BuffersShortcutKey(byte key) => false;
    bool IsShortcutKeyDown(byte key) => false;
    bool IsShortcutEditorInForeground => IsGameInForeground;
    ushort ReadShortcutButtons() => 0;
    void SetAutomaticClutch(bool enabled) { }
    void SetTraceSink(Action<ShiftTraceEvent>? sink) { }
    bool IsForwarding => false;
    bool ConfigureDriveAssist(DriveAssistSettings? settings) => false;
    void SetDriveAssistReadOnly(bool readOnly) { }
    ShiftGamepadReport? PhysicalDriveReport => null;
    void ExitDriveAssistDrift() { }
    void ResetDriveAssistCalibration() { }
    DriveAssistFrame? FeedDriveAssist(TelemetrySample sample) => null;
    DriveAssistStatus GetDriveAssistStatus(bool includeReports) => default;
    void SetDriveAssistCruise(bool enable) { }
    void ResetDriveAssistLog() { }
    bool HasPairedGamepad => false;
    string? RoutingPath => null;
    string? Error { get; }
    Task<string?> ConnectAsync(CancellationToken cancellationToken);
    Task<string?> ConnectAsync(AutomaticShiftSettings settings, CancellationToken cancellationToken) => ConnectAsync(cancellationToken);
    ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings);
    ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger) =>
        TryShift(direction, settings);
    void Release();
    void Disconnect();
}

internal enum ShiftInputResult { Sent, NotInForeground, Busy, PulseInProgress, Failed }

internal enum AutomaticShiftState
{
    Disabled, WaitingForTelemetry, WaitingForGame, WaitingForDriving, Holding, Protected, UpshiftGripHold, BrakeHold, BrakingGripHold,
    WaitingForRatio, ReverseHold, NeutralHold, ManualHold, Shifting, ProbingGear, ConfirmationTimedOut, UnexpectedGear, InputFailed,
    WheelContactHold, DrivetrainSettling, LaunchGripHold, ReleasingShiftInput, WaitingForOutput,
    CollisionPowerHold, DriveAssistHold,
}

internal enum GearLimitSource { Unconfirmed, Probe, Manual, DataOut }
internal readonly record struct DownshiftCue(int Gear, int LandingRpm);

[Flags]
internal enum ShiftProtectionFlags
{
    None = 0,
    LaunchArmed = 1 << 0,
    LaunchActive = 1 << 1,
    AirborneArmed = 1 << 2,
    AirborneActive = 1 << 3,
    UpshiftGripArmed = 1 << 4,
    UpshiftGripActive = 1 << 5,
    ReengagementArmed = 1 << 6,
    ReengagementActive = 1 << 7,
    ImpactHold = 1 << 8,
    BrakingGripHold = 1 << 9,
    ClutchOrHandbrakeHold = 1 << 10,
    ManualHold = 1 << 11,
}

internal readonly record struct AutomaticShiftSnapshot(AutomaticShiftState State, bool Enabled,
    int? Gear, int MaximumGear, int LearnedGears, int TargetGear, int LastShiftFrom, int LastShiftTo,
    int LearnedOutputBins, ShiftReason Reason, bool IsForwarding, double ShiftLatencyMs,
    GearLimitSource GearLimit, int AdvisedPairs, DownshiftCue? DownshiftCue, int LastLandingRpm, int SuggestedGear, int LearnedCutRpm,
    SpeedGearMatch? SpeedMatch, bool DriftMode,
    TransmissionClutchVerdict TransmissionClutch = TransmissionClutchVerdict.Unknown,
    int ClutchedShiftMs = 0, int ProbeShiftMs = 0, bool EffectiveAutomaticClutch = true,
    DriverIntentKind Intent = DriverIntentKind.Idle, bool StrictSpeedGearMatching = false,
    ShiftProtectionFlags Protections = ShiftProtectionFlags.None, bool OutputReady = false);

/// <summary>Processes live Data Out samples and uses monotonic receive time for input timing.</summary>
internal sealed class AutomaticShiftController(IAutomaticShiftInput input, string? profilesPath = null) : IDisposable
{
    private const int SignalGapMs = 260;
    private const int GearStableMs = 100;
    private const int BrakeGearStableMs = 45;
    private const double StoppedSpeedKmh = 2.0;
    private const double CrawlingSpeedKmh = 5.0;
    private const double LowSpeedRecoveryKmh = 25.0;
    private const int CoastIntentMs = 65;
    private const int DirectionStableMs = 240;
    private const int DecisionStableMs = 60;
    private const int RatioStableMs = 140;
    private const int ConfirmationMs = 950;
    private const int ShiftCooldownMs = 350;
    private const int ManualHoldMs = 1100;
    private const double RatioChangeThreshold = 0.09;

    private readonly object _sync = new();
    private ShiftProfileStore? _profiles;
    private ShiftProfileStore.Profile? _adaptiveProfile;
    private readonly double[] _rpmPerKmh = new double[AutomaticShiftSettings.MaximumForwardGears + 1];
    private readonly AdaptiveShiftStrategy _strategy = new();
    private readonly BrakeShiftStrategy _braking = new();
    private readonly WheelGearRatioLearning _wheelRatios = new();
    private readonly LazyForzaShiftLearning _lazyForza = new();
    private readonly RevLimiterEvidence _revLimiter = new();
    private readonly ShiftDynamics _motion = new();
    private readonly CollisionSlowdownDetector _collision = new();
    private readonly DriverIntentAnalyzer _intent = new();
    private readonly VirtualTcuShiftStrategy _virtualTcu = new();
    private readonly Fh6AutoShiftStrategy _fh6AutoShift = new();
    private readonly TransmissionClutchDetector _transmission = new();
    private DriverIntentSnapshot _lastIntent;
    private AutomaticShiftSettings _settings = new();
    private bool _effectiveAutomaticClutch = true;
    private double _blipGain = 1.0;
    private BlipCalibration? _blipWindow;
    private bool _enabled;
    private bool _outputReady;
    private bool _disposed;
    private AutomaticShiftState _state;
    private Action<ShiftTraceEvent>? _traceSink;
    private long? _lastFrameAt;
    private uint _lastTimestamp;
    private ShiftVehicleConfiguration _vehicle;
    private int _observedMaximumGear;
    private int? _probedMaximumGear;
    private int _failedProbeGear;
    private int _probeFailures;
    private long _probeRetryAt;
    private int _gear;
    private int? _reportedGear;
    private int _manualSuggestion;
    private bool _directionChanged;
    private long _gearSince;
    private long? _stoppedSince;
    private double _ratioCandidate;
    private long _ratioSince;
    private double _ratioStartSpeed;
    private int _rememberedCurveRevision;
    private long _nextProfileSave;
    private long? _engagedSince;
    private bool _awaitingEngagement;
    private double _engagementRatio;
    private SpeedGearMatch? _speedMatch;
    private bool _launching;
    private long _launchGripUntil;
    private ShiftDecision _candidate;
    private long _targetSince;
    private long _cooldownUntil;
    private readonly double[,] _shiftLatencies = new double[3, 2];
    private double _observedLimitRpm;
    private long _zeroOutputPlateauMs;
    private long _manualUntil;
    private long _retryHoldUntil;
    private long _eventTransitionUntil;
    private int _unexplainedGearChanges;
    private int _consecutiveConfirmationTimeouts;
    private TelemetrySample? _lastSessionSample;
    private ShiftRequest? _pending;
    private ShiftRequest? _lastShift;
    private long _lastShiftConfirmedAt;

    private ShiftAlgorithm Algorithm => _settings.DriftMode && !_settings.StrictSpeedGearMatching ? ShiftAlgorithm.VirtualTcu : _settings.Algorithm;
    private VirtualTcuMode TcuMode => _settings.DriftMode ? VirtualTcuMode.Drift : _settings.VirtualTcuMode;
    private bool InDriftMode => UsesAlternateStrategy && Algorithm == ShiftAlgorithm.VirtualTcu && TcuMode == VirtualTcuMode.Drift;
    private DriverIntentKind CurrentIntent => _lastIntent.Kind == DriverIntentKind.Drifting && !_settings.DriftPredictionEnabled
        ? DriverIntentKind.RearSliding : _lastIntent.Kind;
    private bool UsesAlternateStrategy => !_settings.StrictSpeedGearMatching &&
        Algorithm is ShiftAlgorithm.VirtualTcu or ShiftAlgorithm.Fh6AutoShift;
    private int UpshiftCeiling(long now) => _settings.MaximumGear ??
        (UsesAlternateStrategy && Algorithm == ShiftAlgorithm.VirtualTcu && now >= _probeRetryAt ? null : _probedMaximumGear) ??
        AutomaticShiftSettings.MaximumForwardGears;

    public bool TryEnable(AutomaticShiftSettings settings, out string error, bool outputReady = true)
    {
        if (!settings.TryValidate(out error)) return false;
        lock (_sync)
        {
            if (_disposed)
            {
                error = TelemetryText.Get("RecvClosed");
                return false;
            }

            input.Release();
            UpdateGearProfiles(settings.UsesGearProfiles);
            _settings = settings;
            _transmission.CancelActive();
            _blipWindow = null;
            SyncEffectiveAutomaticClutch(force: true);
            _pending = _lastShift = null;
            _awaitingEngagement = false;
            _engagedSince = null;
            _engagementRatio = 0;
            _speedMatch = null;
            _candidate = default;
            _cooldownUntil = _manualUntil = _retryHoldUntil = _eventTransitionUntil = 0;
            ResetGearProbe();
            _unexplainedGearChanges = 0;
            _consecutiveConfirmationTimeouts = 0;
            _strategy.ResetTransient();
            _revLimiter.Reset();
            _zeroOutputPlateauMs = 0;
            ResetStrategyHistory();
            _enabled = true;
            _outputReady = outputReady;
            SetState(outputReady ? AutomaticShiftState.WaitingForTelemetry : AutomaticShiftState.WaitingForOutput);
            TraceSettings();
            return true;
        }
    }

    public void SetUpshiftGripProtection(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed || _settings.UpshiftGripProtection == enabled) return;
            _settings = _settings with { UpshiftGripProtection = enabled };
            if (_candidate.Target > _gear) _candidate = default;
            _revLimiter.Reset();
            TraceSettings();
        }
    }

    public void SetAutomaticClutch(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed || _settings.AutomaticClutch == enabled) return;
            _settings = _settings with { AutomaticClutch = enabled };
            SyncEffectiveAutomaticClutch();
            TraceSettings();
        }
    }

    public void SetAutoDetectRaceTransmission(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed || _settings.AutoDetectRaceTransmission == enabled) return;
            _settings = _settings with { AutoDetectRaceTransmission = enabled };
            if (!enabled) _transmission.Reset();
            SyncEffectiveAutomaticClutch();
            TraceSettings();
        }
    }

    private void SyncEffectiveAutomaticClutch(bool force = false)
    {
        var effective = _transmission.EffectiveAutomaticClutch(_settings);
        if (!force && effective == _effectiveAutomaticClutch) return;
        _effectiveAutomaticClutch = effective;
        input.SetAutomaticClutch(effective);
    }

    public void SetAssists(bool launch, bool reengagement, bool adaptive, bool airborne, bool strict)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_settings.ProtectShiftReengagement != reengagement)
            {
                _awaitingEngagement = false;
                _engagedSince = null;
                _engagementRatio = 0;
            }
            if (!launch)
            {
                _launching = false;
                _launchGripUntil = 0;
            }
            var options = _settings with
            {
                ProtectLaunchWheelspin = launch,
                ProtectAirborne = airborne,
                ProtectShiftReengagement = reengagement,
                AdaptiveGearMatching = adaptive,
                StrictSpeedGearMatching = strict,
            };
            UpdateGearProfiles(options.UsesGearProfiles);
            _settings = options;
            _candidate = default;
            _speedMatch = null;
            TraceSettings();
        }
    }

    private void UpdateGearProfiles(bool enabled)
    {
        if (_settings.UsesGearProfiles == enabled) return;
        if (enabled)
        {
            _profiles ??= new ShiftProfileStore(profilesPath);
            _adaptiveProfile = _profiles.Find(_vehicle);
        }
        else
        {
            RememberProfile();
            _profiles?.Save();
            _adaptiveProfile = null;
        }
    }

    public void SetStrategy(ShiftAlgorithm algorithm, VirtualTcuMode mode = VirtualTcuMode.Comfort)
    {
        lock (_sync)
        {
            if (_disposed || !Enum.IsDefined(algorithm) || !Enum.IsDefined(mode) ||
                (_settings.Algorithm == algorithm && _settings.VirtualTcuMode == mode)) return;
            _settings = _settings with { Algorithm = algorithm, VirtualTcuMode = mode };
            _candidate = default;
            _manualSuggestion = 0;
            ResetStrategyHistory();
            TraceSettings();
        }
    }

    public void SetDriftMode(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed || _settings.DriftMode == enabled) return;
            _settings = _settings with { DriftMode = enabled };
            _candidate = default;
            _manualSuggestion = 0;
            _strategy.ResetTransient();
            _revLimiter.Reset();
            _zeroOutputPlateauMs = 0;
            ResetStrategyHistory();
            TraceSettings();
        }
    }

    public void SetTraceSink(Action<ShiftTraceEvent>? sink)
    {
        lock (_sync)
        {
            _traceSink = sink;
            input.SetTraceSink(sink);
            _traceSink?.Invoke(new(Environment.TickCount64, "recording_started", Gear: _reportedGear,
                State: _state, Algorithm: Algorithm, DrivingMode: TcuMode, OutputMode: _settings.GamepadMode));
        }
    }

    private void TraceSettings() => _traceSink?.Invoke(new(Environment.TickCount64, "shift_settings",
        Gear: _reportedGear, Algorithm: Algorithm, DrivingMode: TcuMode, OutputMode: _settings.GamepadMode, Settings: _settings));

    private void SetState(AutomaticShiftState state)
    {
        if (_state == state) return;
        _state = state;
        _traceSink?.Invoke(new(Environment.TickCount64, "shift_state", Gear: _reportedGear, State: state,
            TargetGear: _pending?.To, Algorithm: Algorithm, DrivingMode: TcuMode, OutputMode: _settings.GamepadMode));
    }

    public void Disable()
    {
        lock (_sync)
        {
            if (!_disposed) DisableOutput();
        }
    }

    private void DisableOutput()
    {
        if (input.IsForwarding) input.Release();
        else input.Disconnect();
        _enabled = false;
        _outputReady = false;
        _pending = null;
        _candidate = default;
        _consecutiveConfirmationTimeouts = 0;
        _retryHoldUntil = _eventTransitionUntil = 0;
        SetState(AutomaticShiftState.Disabled);
    }

    public void ResetSession()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _outputReady = false;
            input.Disconnect();
            _lastFrameAt = null;
            ResetVehicle(default);
            SetState(_enabled ? AutomaticShiftState.WaitingForTelemetry : AutomaticShiftState.Disabled);
        }
    }

    public void ResumeOutput()
    {
        lock (_sync)
        {
            if (_disposed || !_enabled || _outputReady) return;
            _outputReady = true;
            SyncEffectiveAutomaticClutch(force: true);
            Pause(AutomaticShiftState.WaitingForTelemetry, cancelPending: true);
        }
    }

    public AutomaticShiftSnapshot GetSnapshot(long now)
    {
        lock (_sync)
        {
            var fresh = _lastFrameAt is long last && now - last <= SignalGapMs;
            CheckInputFailure();
            if (_enabled && !fresh) Pause(AutomaticShiftState.WaitingForTelemetry, cancelPending: true);
            if (_enabled && !input.IsGameInForeground && (_pending.HasValue || _candidate.Target != 0 ||
                _state is AutomaticShiftState.Holding or AutomaticShiftState.BrakeHold))
                Pause(AutomaticShiftState.WaitingForGame, cancelPending: true);

            var learned = 0;
            for (var gear = 1; gear <= AutomaticShiftSettings.MaximumForwardGears; gear++)
                if (LearnedRoadRatio(gear) > 0 || _adaptiveProfile?.Ratios[gear] > 0) learned++;

            var gearLimit = _settings.MaximumGear.HasValue ? GearLimitSource.Manual
                : _probedMaximumGear.HasValue ? GearLimitSource.Probe
                : _observedMaximumGear == AutomaticShiftSettings.MaximumForwardGears ? GearLimitSource.DataOut
                : GearLimitSource.Unconfirmed;
            var target = _pending?.To ?? _candidate.Target;
            var landingRpm = _pending?.LandingRpm ?? _candidate.LandingRpm;
            DownshiftCue? cue = target > 0 && target < (_pending?.From ?? _gear)
                ? new(target, DisplayRpm(landingRpm)) : null;
            var suggestedGear = fresh && _reportedGear is > 0 ? (_enabled ? target : _manualSuggestion) : 0;
            var learnedBins = Algorithm switch
            {
                ShiftAlgorithm.Fh6AutoShift => Math.Max(_fh6AutoShift.LearnedBins, _strategy.LearnedBins),
                ShiftAlgorithm.VirtualTcu => _strategy.LearnedBins,
                _ => Math.Max(_strategy.LearnedBins, _lazyForza.ReadyBins),
            };
            var learnedCut = Algorithm == ShiftAlgorithm.Fh6AutoShift ? _fh6AutoShift.LearnedCutFor(_gear) : 0;
            if (_adaptiveProfile is { } profile)
            {
                var storedBins = 0;
                foreach (var bin in profile.Curve)
                    if (bin.Power > 0) storedBins++;
                learnedBins = Math.Max(learnedBins, storedBins);
            }
            var transmission = _transmission.GetStatus(_settings);
            var protections = ShiftProtectionFlags.None;
            if (_settings.ProtectLaunchWheelspin) protections |= ShiftProtectionFlags.LaunchArmed;
            if (_settings.ProtectAirborne) protections |= ShiftProtectionFlags.AirborneArmed;
            if (_settings.UpshiftGripProtection) protections |= ShiftProtectionFlags.UpshiftGripArmed;
            if (_settings.ProtectShiftReengagement) protections |= ShiftProtectionFlags.ReengagementArmed;
            if (fresh && _enabled && _outputReady)
            {
                var airborneNow = _settings.ProtectAirborne && _state == AutomaticShiftState.WheelContactHold && _motion.IsLandingHold(now);
                if (airborneNow) protections |= ShiftProtectionFlags.AirborneActive;
                else if (_state is AutomaticShiftState.WheelContactHold or AutomaticShiftState.CollisionPowerHold)
                    protections |= ShiftProtectionFlags.ImpactHold;
                var activeDrivingState = _state is not (AutomaticShiftState.WaitingForTelemetry or AutomaticShiftState.WaitingForGame or
                    AutomaticShiftState.WaitingForDriving or AutomaticShiftState.ReverseHold or AutomaticShiftState.NeutralHold or
                    AutomaticShiftState.Protected or AutomaticShiftState.ManualHold);
                if (_settings.ProtectLaunchWheelspin && (_state == AutomaticShiftState.LaunchGripHold || (activeDrivingState && now < _launchGripUntil)))
                    protections |= ShiftProtectionFlags.LaunchActive;
                if (_state == AutomaticShiftState.UpshiftGripHold) protections |= ShiftProtectionFlags.UpshiftGripActive;
                if (_settings.ProtectShiftReengagement && _state == AutomaticShiftState.DrivetrainSettling)
                    protections |= ShiftProtectionFlags.ReengagementActive;
                if (_state == AutomaticShiftState.BrakingGripHold) protections |= ShiftProtectionFlags.BrakingGripHold;
                if (_state == AutomaticShiftState.Protected) protections |= ShiftProtectionFlags.ClutchOrHandbrakeHold;
                if (_state is AutomaticShiftState.ManualHold or AutomaticShiftState.UnexpectedGear) protections |= ShiftProtectionFlags.ManualHold;
            }
            return new AutomaticShiftSnapshot(_state, _enabled, fresh ? _reportedGear : null,
                _settings.MaximumGear ?? _probedMaximumGear ?? _observedMaximumGear,
                learned, _pending?.To ?? 0, _lastShift?.From ?? 0, _lastShift?.To ?? 0,
                learnedBins, _pending?.Reason ?? _lastShift?.Reason ?? ShiftReason.None,
                input.IsForwarding, ShiftLatency((_pending ?? _lastShift) is { } shift && shift.To < shift.From
                    ? ShiftDirection.Down : ShiftDirection.Up), gearLimit, _lazyForza.Targets.Count, cue,
                DisplayRpm(_lastShift?.LandingRpm ?? 0), suggestedGear, DisplayRpm(learnedCut),
                fresh ? _speedMatch : null, InDriftMode,
                transmission.Verdict, transmission.ClutchedMs, transmission.ProbeMs,
                _transmission.EffectiveAutomaticClutch(_settings),
                fresh ? CurrentIntent : DriverIntentKind.Idle, _settings.StrictSpeedGearMatching, protections, _outputReady);
        }
    }

    private static int DisplayRpm(double rpm) => (int)(Math.Round(rpm / 50) * 50);

    public void Feed(TelemetrySample sample, long now)
    {
        lock (_sync)
        {
            if (_disposed) return;
            CheckInputFailure();
            var previousFrameAt = _lastFrameAt;
            if (!AcceptFrame(sample, now)) return;
            var frameElapsedMs = previousFrameAt is long prevAt ? Math.Clamp(now - prevAt, 0, 100) : 0;
            _manualSuggestion = 0;
            if (!_pending.HasValue) _speedMatch = null;

            if (!IsDrivingFrame(sample))
            {
                _collision.Reset();
                _unexplainedGearChanges = 0;
                _consecutiveConfirmationTimeouts = 0;
                _retryHoldUntil = 0;
                ClearGear();
                if (_enabled) Pause(AutomaticShiftState.WaitingForDriving, cancelPending: true);
                return;
            }
            if (sample.IsPreRaceGrid || now < _eventTransitionUntil)
            {
                _collision.Reset();
                _unexplainedGearChanges = 0;
                _consecutiveConfirmationTimeouts = 0;
                _retryHoldUntil = 0;
                ClearGear();
                if (_enabled) Pause(AutomaticShiftState.WaitingForDriving, cancelPending: true);
                return;
            }

            var nearStop = sample.SpeedKmh <= StoppedSpeedKmh && Math.Abs(sample.AccelerationY) < 8 &&
                Math.Max(Math.Abs(sample.AccelerationX), Math.Abs(sample.AccelerationZ)) < 0.50 * TelemetryDerived.Gravity;
            _stoppedSince = nearStop ? _stoppedSince ?? now : null;
            var stopped = _stoppedSince is long stopAt && now - stopAt >= GearStableMs;

            var pedals = input.DriverPedals ?? new ShiftPedals(sample.Throttle, sample.Brake);
            var pulseActive = input.IsShiftPulseActive;
            var manualShiftHeld = _enabled && input.ManualShiftHeld;
            var shifting = _pending.HasValue || pulseActive || manualShiftHeld || sample.Clutch >= 0.08 || sample.HandBrake >= 0.08;
            var collision = _collision.Observe(sample, pedals, now, shifting);
            _motion.ObserveFlight(sample, now);
            if (_settings.ProtectAirborne && !stopped && _motion.IsLandingHold(now))
            {
                if (_gear != 0) ClearGear();
                int? reported = sample.Gear is >= 0 and <= AutomaticShiftSettings.MaximumForwardGears ? sample.Gear : null;
                if (_reportedGear != reported) _gearSince = now;
                _reportedGear = reported;
                if (_enabled) Pause(AutomaticShiftState.WheelContactHold, cancelPending: true);
                return;
            }

            var drivenSlip = DrivenSlipMagnitude(sample);
            if (!collision.Holding)
            {
                _transmission.ObserveFrame(sample, pedals, now, frameElapsedMs, passiveEnabled: _settings.AutoDetectRaceTransmission);
                SyncEffectiveAutomaticClutch();
                UpdateBlipCalibration(sample, drivenSlip, now);
            }

            var reversing = sample.Gear == 0 || (sample.Gear == 1 && sample.VelocityZ < -1.5);
            if (reversing || sample.Gear is < 1 or > AutomaticShiftSettings.MaximumForwardGears)
            {
                _lazyForza.BreakSequence();
                var betweenGears = sample.Gear is 11 or byte.MaxValue;
                if (betweenGears && !collision.Holding && _pending is ShiftRequest pending &&
                    now - pending.RequestedAt < ConfirmationMs && input.IsGameInForeground && !input.ManualShiftHeld)
                {
                    _reportedGear = null;
                    SetState(pending.IsGearProbe ? AutomaticShiftState.ProbingGear : AutomaticShiftState.Shifting);
                    return;
                }

                var failedInNeutral = betweenGears && !collision.Holding && CheckConfirmationTimeout(now);
                ClearGear();
                _reportedGear = sample.Gear == 0 ? 0 : null;
                _directionChanged = true;
                if (_enabled && !failedInNeutral)
                    Pause(reversing ? AutomaticShiftState.ReverseHold : AutomaticShiftState.NeutralHold, cancelPending: true);
                return;
            }

            var changedGear = _gear != 0 && _gear != sample.Gear;
            if (_gear != sample.Gear || _reportedGear != sample.Gear)
            {
                if (_reportedGear != sample.Gear) _gearSince = now;
                _gear = sample.Gear;
                _ratioCandidate = 0;
                _engagedSince = null;
                _engagementRatio = 0;
                _candidate = default;
                _strategy.ResetTransient();
                _revLimiter.Reset();
                _zeroOutputPlateauMs = 0;
            }
            _reportedGear = _gear;
            if (collision.Holding && (!stopped || _gear == 1 || collision.PowerLimited || collision.CheckingPower))
            {
                // An interrupted shift supplies neither a valid latency sample nor a failed top-gear/gear-ratio probe.
                _unexplainedGearChanges = _consecutiveConfirmationTimeouts = 0;
                _retryHoldUntil = _launchGripUntil = 0;
                _launching = _awaitingEngagement = false;
                _ratioCandidate = _engagementRatio = 0;
                _engagedSince = null;
                _braking.BreakSequence();
                _wheelRatios.BreakSequence();
                _lazyForza.BreakSequence();
                if (_enabled && _outputReady)
                {
                    var collisionState = collision.PowerLimited ? AutomaticShiftState.CollisionPowerHold : AutomaticShiftState.WheelContactHold;
                    if (manualShiftHeld)
                    {
                        _manualUntil = now + ManualHoldMs;
                        collisionState = AutomaticShiftState.ManualHold;
                    }
                    else if (sample.HandBrake >= 0.08 || !pulseActive && sample.Clutch >= 0.08)
                        collisionState = AutomaticShiftState.Protected;
                    Pause(collisionState, cancelPending: true);
                }
                else
                {
                    _strategy.ResetTransient();
                    _revLimiter.Reset();
                    _zeroOutputPlateauMs = 0;
                    ResetStrategyHistory();
                }
                return;
            }
            if (!collision.Holding && now - _gearSince >= GearStableMs) ObserveGear(_gear);
            var launchGripHold = !collision.Holding && _settings.ProtectLaunchWheelspin && ProtectLaunch(sample, drivenSlip, now);
            var checkReengagement = _settings.ProtectShiftReengagement;
            var strictMatching = _settings.StrictSpeedGearMatching;
            var leftPreviousRatio = !checkReengagement || MatchesNewGear(sample);
            var rpmPlausible = !checkReengagement || RpmPlausibleForUpshift(sample);
            _motion.Observe(sample, pedals, now, shifting, trackRpm: leftPreviousRatio && rpmPlausible && !launchGripHold);
            _lastIntent = _intent.Observe(sample, pedals, _motion, _settings.DriftPredictionEnabled, now);
            var contact = !collision.Holding && ((!strictMatching && !UsesAlternateStrategy && !checkReengagement) || _motion.HasContact(now));
            var physicalReady = contact && !shifting;
            if (physicalReady && !launchGripHold)
            {
                if (LearnRatio(sample, pedals, now, drivenSlip, requireTravel: !leftPreviousRatio))
                {
                    // RPM tracked changing road speed, so an old departure or gear ratio can no longer hold this gear.
                    _awaitingEngagement = false;
                    leftPreviousRatio = rpmPlausible = true;
                }
            }
            else
                _ratioCandidate = 0;

            if (physicalReady && leftPreviousRatio && rpmPlausible && !launchGripHold) _engagedSince ??= now;
            else _engagedSince = null;
            if (_awaitingEngagement && _engagedSince is long engaged && now - engaged >= 50)
            {
                _awaitingEngagement = false;
                if (sample.SpeedKmh >= 12 && sample.Rpm >= Math.Max(sample.EngineIdleRpm * 1.25, sample.EngineMaxRpm * 0.23))
                    _engagementRatio = sample.Rpm / sample.SpeedKmh;
            }
            var settled = _engagedSince.HasValue && !_awaitingEngagement && !launchGripHold;
            if (settled) _wheelRatios.Observe(sample, drivenSlip);
            else _wheelRatios.BreakSequence();
            if (!settled) _lazyForza.BreakSequence();
            if (settled && !UsesAlternateStrategy && _lazyForza.Observe(sample, _vehicle.RoundedMaxRpm, now))
            {
                _vehicle = ShiftVehicleConfiguration.From(sample);
                _gearSince = now;
                _ratioCandidate = 0;
                _observedMaximumGear = 0;
                _observedLimitRpm = 0;
                _lastShift = null;
                ResetGearProbe();
                ClearLearnedRatios();
                _revLimiter.Reset();
                ResetStrategyHistory();
                if (_enabled) Pause(AutomaticShiftState.WaitingForRatio, cancelPending: true);
                return;
            }
            var cleanTraction = drivenSlip < 0.38;
            var tractionGrounded = cleanTraction && !_wheelRatios.DrivenWheelsOutrunRoad(sample);
            var ratioMatches = RatioMatches(sample);
            var stableSlope = ratioMatches && _lazyForza.MatchesGearSlope(sample);
            var gearIsStable = now - _gearSince >= GearStableMs && settled;
            var canLearnOutput = contact && cleanTraction && Math.Abs(sample.AccelerationX) < 0.45 * TelemetryDerived.Gravity &&
                sample.SpeedKmh >= 7 && sample.Brake < 0.08 && sample.Clutch < 0.09 && sample.HandBrake < 0.08 &&
                gearIsStable && ratioMatches;
            var roadRatio = LearnedRoadRatio(_gear);
            var coupledForBraking = roadRatio > 0 && sample.SpeedKmh >= 14 &&
                Math.Abs(sample.Rpm / (sample.SpeedKmh * roadRatio) - 1) <= 0.10;
            _braking.Observe(sample, contact && gearIsStable && coupledForBraking);

            var accelerating = sample.SpeedKmh >= 6 && sample.Throttle >= 0.12 && sample.Brake < 0.08;
            var upshiftAllowed = _gear < UpshiftCeiling(now) &&
                (_failedProbeGear != _gear || now >= _probeRetryAt) &&
                (strictMatching
                    ? sample.SpeedKmh >= 6
                    : (!pedals.Braking && pedals.Throttle >= 0.05 &&
                        (UsesAlternateStrategy ? sample.SpeedKmh >= 10 && sample.Throttle >= 0.05 && sample.Brake <= 0.10 : accelerating)));
            var canUpshift = upshiftAllowed && settled;
            var straight = double.IsFinite(drivenSlip) && Math.Abs(sample.AccelerationX) < 0.25 * TelemetryDerived.Gravity &&
                Math.Abs(sample.Steer) < 0.15;
            var underLoad = settled && accelerating && sample.Throttle >= 0.90 &&
                sample.SmashableMass <= 0 && sample.SmashableVelDiff <= 0.5;
            var allowShallowCut = strictMatching && underLoad && straight && sample.SpeedKmh >= 20 && !_motion.IsLandingHold(now) &&
                (tractionGrounded || (!_settings.UpshiftGripProtection && drivenSlip <= 0.65)) &&
                _wheelRatios.HasRollingSpeedAgreement(sample);
            var measuredLimiter = _revLimiter.Observe(sample, now, underLoad && (straight || !_settings.UpshiftGripProtection),
                allowShallowCut, out var shallowCutRpm);
            var shallowLimiter = shallowCutRpm > 0 &&
                (sample.NumCylinders != 0 || _observedMaximumGear > 1 || _settings.MaximumGear > 1 ||
                    LearnedRoadRatio(2) > 0 || _adaptiveProfile?.Ratios[2] > 0 || VehicleCatalog.Find(sample.CarOrdinal)?.NumGears != 1);
            var verifiedLimiter = shallowLimiter || (measuredLimiter && tractionGrounded);
            if (verifiedLimiter)
            {
                var limit = Math.Min(sample.EngineMaxRpm, measuredLimiter && tractionGrounded ? _revLimiter.LastCutRpm : shallowCutRpm);
                _observedLimitRpm = _observedLimitRpm == 0 ? limit : _observedLimitRpm + (limit - _observedLimitRpm) * 0.20;
            }
            if (underLoad && tractionGrounded &&
                sample.Rpm >= Math.Max(sample.EngineIdleRpm * 1.8, sample.EngineMaxRpm * 0.62) &&
                double.IsFinite(sample.PowerKw) && double.IsFinite(sample.TorqueNm) &&
                sample.PowerKw <= 1 && sample.TorqueNm <= 1)
                _zeroOutputPlateauMs = Math.Min(2000, _zeroOutputPlateauMs + frameElapsedMs);
            else
                _zeroOutputPlateauMs = 0;
            var canMeasureOutput = contact && sample.Clutch < 0.09 && sample.HandBrake < 0.08 && sample.Brake < 0.08 &&
                sample.SpeedKmh >= 6 && gearIsStable && sample.SmashableMass <= 0 && sample.SmashableVelDiff <= 0.5 &&
                (cleanTraction || sample.Rpm >= 0.985 * EffectiveLimit(sample)) &&
                (!_settings.UpshiftGripProtection || straight || canLearnOutput);
            _strategy.Observe(sample, now, canLearnOutput, canMeasureOutput, EffectiveLimit(sample));
            if (!strictMatching && Algorithm == ShiftAlgorithm.VirtualTcu && settled)
                _virtualTcu.Observe(sample, pedals, _motion, now);
            if (!strictMatching && Algorithm == ShiftAlgorithm.Fh6AutoShift)
                _fh6AutoShift.Observe(sample, pedals, _motion, now, gearIsStable, shifting, drivenSlip,
                    measuredLimiter ? _revLimiter.LastCutRpm : 0);

            var braking = _gear > 1 && pedals.Braking;
            if (!_enabled)
            {
                if (gearIsStable && contact && sample.Clutch < 0.09 && sample.HandBrake < 0.08)
                {
                    if (strictMatching)
                        _manualSuggestion = DecideStrictSpeedShift(sample, canUpshift, tractionGrounded, verifiedLimiter, now).Target;
                    else if (UsesAlternateStrategy)
                    {
                        if (!braking || BrakeShiftStrategy.CanShift(sample))
                            _manualSuggestion = DecideAlternate(sample, pedals, canUpshift, measuredLimiter, drivenSlip, now).Target;
                    }
                    else if (Algorithm == ShiftAlgorithm.Mode5)
                    {
                        if (!braking || BrakeShiftStrategy.CanShift(sample))
                            _manualSuggestion = DecideMode5Shift(sample, pedals, canUpshift, cleanTraction, measuredLimiter).Target;
                    }
                    else
                    {
                        var advice = LazyForzaShiftAdvisor.Recommend(_lazyForza.Targets, _gear, sample.Rpm, sample.EngineMaxRpm);
                        if (advice.Gear != _gear) _manualSuggestion = advice.Gear;
                    }
                }
                return;
            }
            if (!_outputReady)
            {
                if (_state != AutomaticShiftState.InputFailed) SetState(AutomaticShiftState.WaitingForOutput);
                return;
            }
            if (!input.IsGameInForeground) { Pause(AutomaticShiftState.WaitingForGame, cancelPending: true); return; }
            if (manualShiftHeld)
            {
                _unexplainedGearChanges = 0;
                _consecutiveConfirmationTimeouts = 0;
                _manualUntil = now + ManualHoldMs;
                Pause(AutomaticShiftState.ManualHold, cancelPending: true);
                return;
            }
            if (_pending is ShiftRequest request)
            {
                ConfirmShift(request, sample, pedals, now);
                return;
            }
            if (changedGear)
            {
                _consecutiveConfirmationTimeouts = 0;
                _retryHoldUntil = 0;
                _awaitingEngagement = false;
                _manualUntil = now + ManualHoldMs;
            }

            if (pulseActive) { Pause(AutomaticShiftState.ReleasingShiftInput); return; }
            if (sample.Clutch >= 0.08 || sample.HandBrake >= 0.08) { Pause(AutomaticShiftState.Protected); return; }
            if (now < _manualUntil)
            {
                if (_state != AutomaticShiftState.UnexpectedGear) Pause(AutomaticShiftState.ManualHold);
                return;
            }
            if (now < _retryHoldUntil) return;
            if (input.DriveAssistBlocksShift) { Pause(AutomaticShiftState.DriveAssistHold); return; }
            var recovery = !strictMatching || stopped ? DecideLowSpeedRecovery(sample, pedals, stopped) : default;
            // Impact telemetry can linger while the car is already stationary.
            var stoppedRecovery = stopped && recovery.Target > 0;
            if (!contact && !stoppedRecovery) { Pause(AutomaticShiftState.WheelContactHold); return; }

            var holdState = braking ? AutomaticShiftState.BrakeHold
                : upshiftAllowed && launchGripHold ? AutomaticShiftState.LaunchGripHold
                : upshiftAllowed && !settled ? AutomaticShiftState.DrivetrainSettling : AutomaticShiftState.Holding;
            var shortStep = (!strictMatching && braking) || recovery.Target > 0;
            var stableFor = _directionChanged ? DirectionStableMs : shortStep && _lastShift is { } previous && previous.To == _gear
                ? BrakeGearStableMs : GearStableMs;
            if (!strictMatching && Algorithm == ShiftAlgorithm.Fh6AutoShift && !shortStep) stableFor = Math.Max(stableFor, 180);
            if (now - _gearSince < stableFor) { SetState(holdState); return; }
            _directionChanged = false;
            if (!strictMatching && braking && !stoppedRecovery && !BrakeShiftStrategy.CanShift(sample)) { Pause(AutomaticShiftState.BrakingGripHold); return; }

            if (recovery.Target > 0)
            {
                SetState(holdState);
                _candidate = recovery;
                RequestShift(recovery, sample, pedals, now);
                return;
            }

            var straightSlipAtLimit = !cleanTraction && straight && underLoad &&
                (measuredLimiter || sample.Rpm >= 0.997 * EffectiveLimit(sample));
            var gripHold = canUpshift && _settings.UpshiftGripProtection && !straightSlipAtLimit &&
                (!cleanTraction || !stableSlope || !(Math.Abs(sample.AccelerationX) < 0.75 * TelemetryDerived.Gravity));

            var decision = strictMatching ? DecideStrictSpeedShift(sample, canUpshift, tractionGrounded, verifiedLimiter, now) : Algorithm switch
            {
                ShiftAlgorithm.VirtualTcu or ShiftAlgorithm.Fh6AutoShift => DecideAlternate(sample, pedals, canUpshift, measuredLimiter, drivenSlip, now),
                ShiftAlgorithm.Mode5 => DecideMode5Shift(sample, pedals, canUpshift, cleanTraction, measuredLimiter),
                _ => braking ? DecideBrakingShift(sample, pedals.Brake) :
                    DecideDrivingShift(sample, pedals, canUpshift, cleanTraction, stableSlope, measuredLimiter, straightSlipAtLimit),
            };
            var yawRate = Math.Abs(sample.AngularVelocityY);
            var lateralSpeedKmh = Math.Abs(sample.VelocityX) * 3.6;
            var slideKickdown = !strictMatching && Algorithm == ShiftAlgorithm.LazyForza && pedals.Kickdown &&
                pedals.Brake < BrakeShiftStrategy.MinimumBrake && double.IsFinite(drivenSlip) &&
                (_lastIntent.RearSliding || (!cleanTraction &&
                    ((double.IsFinite(yawRate) && yawRate >= 0.65) ||
                     (double.IsFinite(lateralSpeedKmh) && lateralSpeedKmh >= sample.SpeedKmh * 0.35))));
            if (slideKickdown)
            {
                if (decision.Target <= _gear)
                    decision = leftPreviousRatio ? DecideSlideKickdown(sample, now) : default;
            }
            else if (!strictMatching && (_settings.AdaptiveGearMatching ||
                (Algorithm == ShiftAlgorithm.LazyForza && decision.Target == 0 && !launchGripHold &&
                    CanTryLazyForzaKickdown(sample, pedals, drivenSlip, stableSlope, now))))
            {
                var matched = MatchDownshift(sample, pedals, decision, braking, drivenSlip, now);
                if (matched.Target > 0 && (pedals.Kickdown || decision.Target == 0 || decision.Target > _gear))
                    decision = matched;
                else if (pedals.Kickdown && !InDriftMode && sample.SpeedKmh >= 12 && decision.Target > 0 && decision.Target < _gear)
                    decision = matched;
            }
            if (!strictMatching && decision.Target == 0 && !braking)
            {
                var coast = DecideCoastDownshift(sample, pedals, drivenSlip);
                if (coast.Target > 0) decision = coast;
            }
            if (_lastIntent.RearSliding && !InDriftMode && decision.Target > _gear &&
                decision.Reason is ShiftReason.LightLoad or ShiftReason.HigherGearPull or ShiftReason.VirtualTcuComfort or
                    ShiftReason.Mode5PowerBand or ShiftReason.Fh6PowerCrossover)
                decision = default;
            if (gripHold && decision.Target > _gear) { Pause(AutomaticShiftState.UpshiftGripHold); return; }
            if (UsesAlternateStrategy && decision.Target != 0 && AlternateShiftOnCooldown(decision, braking, now))
                decision = default;
            if (!UsesAlternateStrategy && (strictMatching || !braking) && decision.Target != 0 && now < _cooldownUntil && _lastShift is { } last &&
                (decision.Target > _gear) != (last.To > last.From))
                decision = default;
            if (decision.NeedsRatio) holdState = AutomaticShiftState.WaitingForRatio;
            else if (decision.Target == 0 && gripHold) holdState = AutomaticShiftState.UpshiftGripHold;
            SetState(holdState);
            if (decision.Target != _candidate.Target) _targetSince = now;
            _candidate = decision;
            if (strictMatching && decision.Target > 0 && decision.LandingRpm > 0)
                _speedMatch = new(decision.Target, (int)(sample.SpeedKmh / SpeedGearMatcher.BandKmh) * SpeedGearMatcher.BandKmh, DisplayRpm(decision.LandingRpm));
            var immediate = decision.Reason is ShiftReason.HighRpmOutputLoss or ShiftReason.Limiter ||
                (!strictMatching && (UsesAlternateStrategy || braking || decision.Reason is ShiftReason.LazyForzaUpshift or ShiftReason.LazyForzaDownshift or
                    ShiftReason.SpeedMatchedCoast or ShiftReason.CoastDownshift or ShiftReason.Mode5Limiter));
            if (decision.Target != 0 && (immediate || now - _targetSince >= DecisionStableMs)) RequestShift(decision, sample, pedals, now);
        }
    }

    private ShiftDecision DecideStrictSpeedShift(TelemetrySample sample, bool canUpshift, bool tractionGrounded, bool verifiedLimiter, long now)
    {
        var profile = _adaptiveProfile;
        var currentRatio = LearnedRoadRatio(_gear);
        if (currentRatio > 0 && profile?.Ratios[_gear] is > 0 and var saved && Math.Abs(currentRatio / saved - 1) > RatioChangeThreshold)
            profile = null;
        if (currentRatio <= 0) currentRatio = profile?.Ratios[_gear] ?? 0;

        double Ratio(int gear)
        {
            var live = LearnedRoadRatio(gear);
            return live > 0 ? live : profile?.Ratios[gear] ?? 0;
        }

        var effectiveLimit = EffectiveLimit(sample);
        // Data Out EngineMaxRpm is often the tach ceiling rather than the actual fuel cut;
        // once a lower real limiter is observed, scale both speed boundaries while keeping the 20% hysteresis gap.
        var upshiftRpm = _observedLimitRpm > 0
            ? Math.Min(0.90 * sample.EngineMaxRpm, 0.95 * effectiveLimit)
            : 0.90 * sample.EngineMaxRpm;
        var downshiftRpm = 0.80 * upshiftRpm;

        if (canUpshift)
        {
            var higherRatio = Ratio(_gear + 1);
            // Rescue grounded full-load upshifts immediately when hitting the real limiter,
            // whether the nominal speed threshold is unreachable, ratios are stale, or ratio learning hasn't finished yet.
            var liveRatio = sample.SpeedKmh >= 12 ? sample.Rpm / sample.SpeedKmh : 0;
            var referenceRatio = currentRatio > 0 ? Math.Max(currentRatio, liveRatio) : liveRatio;
            var canStepUp = higherRatio <= 0 || referenceRatio <= 0 || higherRatio < referenceRatio;
            var sustainedLimiter = sample.SpeedKmh >= 6 && (_zeroOutputPlateauMs >= 200 ||
                (sample.Throttle >= 0.90 && _motion.PlateauMs >= 250 && sample.Rpm >= effectiveLimit * 0.995));
            if (canStepUp && (verifiedLimiter || (tractionGrounded && sustainedLimiter)))
                return new(_gear + 1, ShiftReason.HighRpmOutputLoss,
                    LandingRpm: higherRatio > 0 ? sample.SpeedKmh * higherRatio : 0);

            if (currentRatio > 0 && sample.SpeedKmh * currentRatio >= upshiftRpm && higherRatio < currentRatio)
                return new(_gear + 1, ShiftReason.StrictSpeedMatch, LandingRpm: sample.SpeedKmh * higherRatio);
        }

        if (currentRatio <= 0) return new(0, ShiftReason.None, NeedsRatio: true);

        if (_gear <= 1) return default;
        var lowerRatio = Ratio(_gear - 1);
        if (lowerRatio <= currentRatio) return new(0, ShiftReason.None, NeedsRatio: true);
        if (sample.SpeedKmh * lowerRatio > downshiftRpm) return default;

        var safetySpeed = Math.Max(sample.SpeedKmh, _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down)));
        var landingRpm = ConservativeDownshiftRpm(sample, safetySpeed, currentRatio, lowerRatio);
        return landingRpm < SafeDownshiftLimit(sample)
            ? new(_gear - 1, ShiftReason.StrictSpeedMatch, LandingRpm: landingRpm) : default;
    }

    private ShiftDecision DecideAlternate(TelemetrySample sample, ShiftPedals pedals, bool canUpshift, bool fuelCut, double slip, long now)
    {
        Span<double> ratios = stackalloc double[AutomaticShiftSettings.MaximumForwardGears + 1];
        for (var gear = 1; gear <= Math.Min(_gear + 1, AutomaticShiftSettings.MaximumForwardGears); gear++)
            ratios[gear] = LearnedRoadRatio(gear);
        return Algorithm == ShiftAlgorithm.VirtualTcu
            ? _virtualTcu.Decide(sample, pedals, TcuMode, ratios, _strategy.PeakFractions(), _motion,
                EffectiveLimit(sample), slip, Math.Max(ShiftLatency(ShiftDirection.Down), _settings.PressDurationMs + 16), canUpshift, fuelCut, now, _lastIntent)
            : _fh6AutoShift.Decide(sample, pedals, ratios, _motion, canUpshift, ShiftLatency(ShiftDirection.Up), ShiftLatency(ShiftDirection.Down),
                _gear > 1 ? _wheelRatios.RpmFromDrivenWheels(sample, _gear - 1) : 0, now, _lastIntent);
    }

    private ShiftDecision DecideMode5Shift(TelemetrySample sample, ShiftPedals pedals, bool canUpshift, bool cleanTraction, bool fuelCut)
    {
        if (pedals.Braking) return DecideBrakingShift(sample, pedals.Brake);
        var limit = EffectiveLimit(sample);
        var nominal = sample.EngineMaxRpm;
        var ratio = LearnedRoadRatio(_gear);
        if (canUpshift && !pedals.Braking && pedals.Throttle >= 0.12 && RpmPlausibleForUpshift(sample) &&
            (cleanTraction || sample.Rpm >= limit * 0.997 && !_wheelRatios.DrivenWheelsOutrunRoad(sample)))
        {
            var lead = Math.Clamp(_motion.RpmRate * Math.Min(100, ShiftLatency(ShiftDirection.Up)) / 1000, 0, limit * 0.035);
            var nextRatio = LearnedRoadRatio(_gear + 1);
            var predictedRpm = Math.Min(limit, sample.Rpm + lead);
            if (!_lastIntent.RearSliding && pedals.WantsPower && ratio > nextRatio && nextRatio > 0 && cleanTraction)
            {
                var nextRpm = predictedRpm * nextRatio / ratio;
                var here = _strategy.Estimate(predictedRpm / nominal, _adaptiveProfile?.Curve);
                var there = _strategy.Estimate(nextRpm / nominal, _adaptiveProfile?.Curve);
                if (here > 0 && there >= Math.Max(here * 1.035, here + 3))
                    return new(_gear + 1, ShiftReason.Mode5PowerBand, LandingRpm: nextRpm);
            }
            if (fuelCut || sample.Rpm + lead >= limit * 0.997)
                return new(_gear + 1, ShiftReason.Mode5Limiter);
        }

        var canCornerExit = _lastIntent.CornerExitWindow && _motion.SteerRate <= -0.10;
        var maxLatG = (canCornerExit ? 0.62 : 0.50) * TelemetryDerived.Gravity;
        if (_gear <= 1 || !pedals.WantsPower || !cleanTraction || sample.SpeedKmh < 12 ||
            Math.Abs(sample.AccelerationX) >= maxLatG) return default;
        var lowerRatio = LearnedRoadRatio(_gear - 1);
        if (lowerRatio <= 0) return new(0, ShiftReason.None, NeedsRatio: sample.Rpm < limit * 0.58);
        var speed = _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down));
        var currentRatio = ratio > 0 ? ratio : sample.Rpm / sample.SpeedKmh;
        var landing = speed * lowerRatio;
        if (lowerRatio <= currentRatio * 1.025 || Math.Max(speed, sample.SpeedKmh) * lowerRatio >= SafeDownshiftLimit(sample))
            return default;

        // Use the RPM the driver would have after an upshift from the lower gear as the bottom of the band.
        var hereRpm = speed * currentRatio;
        var currentPower = _strategy.Estimate(hereRpm / nominal, _adaptiveProfile?.Curve);
        var lowerPower = _strategy.Estimate(landing / nominal, _adaptiveProfile?.Curve);
        var gainThreshold = canCornerExit ? 1.05 : 1.08;
        var uphillOrTurboLug = _lastIntent.RoadGrade > 0.05 && pedals.Throttle >= 0.45 &&
            sample.Rpm < limit * 0.42 && landing < SafeDownshiftLimit(sample) * 0.90;
        var worthShifting = currentPower > 0 && lowerPower > 0
            ? lowerPower >= Math.Max(currentPower * gainThreshold, currentPower + 4) || uphillOrTurboLug
            : hereRpm < limit * 0.95 * currentRatio / lowerRatio && landing > hereRpm + nominal * 0.025;
        return worthShifting ? new(_gear - 1, ShiftReason.Mode5PowerBand, LandingRpm: landing) : default;
    }

    private bool AlternateShiftOnCooldown(ShiftDecision decision, bool braking, long now)
    {
        if (_lastShift is not { } last || last.To != _gear) return false;
        var elapsed = now - _lastShiftConfirmedAt;
        var up = decision.Target > _gear;
        var lastWasUp = last.To > last.From;
        if (decision.Reason is ShiftReason.SpeedMatchedPower or ShiftReason.SpeedMatchedCoast or ShiftReason.SpeedMatchedBraking or
            ShiftReason.CoastDownshift or ShiftReason.SpeedMatchedLearning)
            return elapsed < (lastWasUp ? 350 : 110);
        if (Algorithm == ShiftAlgorithm.Fh6AutoShift)
        {
            if (elapsed < 100) return true;
            var atLimiter = decision.Reason is ShiftReason.Fh6FuelCut or ShiftReason.Fh6PredictedLimiter;
            return !braking && up != lastWasUp && !atLimiter && elapsed < (up ? 400 : 650);
        }
        var delay = braking ? 200 : up == lastWasUp ? 300 : !up && TcuMode == VirtualTcuMode.Comfort ? 1000 : 500;
        return elapsed < delay;
    }

    private ShiftDecision DecideBrakingShift(TelemetrySample sample, double brake)
    {
        Span<double> ratios = stackalloc double[AutomaticShiftSettings.MaximumForwardGears + 1];
        for (var gear = 1; gear <= _gear; gear++) ratios[gear] = LearnedRoadRatio(gear);
        var engagementDelay = Math.Max(ShiftLatency(ShiftDirection.Down), _settings.PressDurationMs + 16);
        return _braking.Decide(sample, brake, ratios, _lazyForza.Targets, EffectiveLimit(sample), engagementDelay,
            sample.EngineMaxRpm * AdaptiveShiftStrategy.LowRpmFraction, _strategy.PeakFractions(_adaptiveProfile?.Curve), _lastIntent);
    }

    private ShiftDecision DecideLowSpeedRecovery(TelemetrySample sample, ShiftPedals pedals, bool stopped)
    {
        if (_gear <= 1 || (!stopped && (!_settings.AdaptiveGearMatching || InDriftMode || sample.SpeedKmh > LowSpeedRecoveryKmh)))
            return default;

        var crawling = sample.SpeedKmh <= CrawlingSpeedKmh;
        if (!stopped)
        {
            var luggingRpm = Math.Max(sample.EngineIdleRpm * 1.6, EffectiveLimit(sample) * (AdaptiveShiftStrategy.LowRpmFraction - 0.04));
            if (sample.Rpm > luggingRpm && (!crawling || pedals.Throttle >= 0.20)) return default;
            if (!crawling && !pedals.Braking && !(Math.Abs(sample.AccelerationX) < 0.50 * TelemetryDerived.Gravity)) return default;
        }

        var lowerGear = _gear - 1;
        var bound = 0.0;
        for (var gear = lowerGear; gear >= 1 && bound <= 0; gear--)
        {
            var live = LearnedRoadRatio(gear);
            bound = live > 0 ? live : _adaptiveProfile?.Ratios[gear] ?? 0;
        }

        var speed = Math.Max(sample.SpeedKmh, _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down)));
        // An observed shorter gear bounds the RPM of any unlearned gear between it and the current gear.
        if (bound == 0 && speed > StoppedSpeedKmh) return default;
        if (!crawling && bound <= LearnedRoadRatio(_gear)) return default;
        var landingRpm = Math.Max(sample.EngineIdleRpm, speed * bound);
        if (_settings.StrictSpeedGearMatching && _wheelRatios.DrivenWheelsOutrunRoad(sample))
        {
            var wheelRpm = _wheelRatios.RpmFromDrivenWheels(sample, lowerGear);
            if (wheelRpm <= 0) return default;
            landingRpm = Math.Max(landingRpm, wheelRpm);
        }
        return landingRpm < SafeDownshiftLimit(sample) ? new(lowerGear, ShiftReason.LowSpeed, LandingRpm: landingRpm) : default;
    }

    private bool CanTryLazyForzaKickdown(TelemetrySample sample, ShiftPedals pedals, double slip, bool stableSlope, long now)
    {
        if (!pedals.Kickdown || !stableSlope || pedals.Brake >= BrakeShiftStrategy.MinimumBrake || _gear <= 1 || sample.SpeedKmh < 12 ||
            !_motion.HasContact(now) || _wheelRatios.DrivenWheelsOutrunRoad(sample))
            return false;

        // A low, road-consistent RPM can still call for kickdown when a driven tire reports some slip.
        var roadRpm = sample.SpeedKmh * LearnedRoadRatio(_gear);
        return slip < 0.38 || (slip < 1.2 && Math.Abs(sample.Rpm - roadRpm) <= roadRpm * 0.13);
    }

    private ShiftDecision DecideSlideKickdown(TelemetrySample sample, long now)
    {
        if (_gear <= 1 || sample.SpeedKmh <= StoppedSpeedKmh || !_motion.HasContact(now)) return default;

        var lowerGear = _gear - 1;
        var currentRatio = LearnedRoadRatio(_gear);
        var lowerRatio = LearnedRoadRatio(lowerGear);
        if (currentRatio <= 0 || lowerRatio <= currentRatio) return default;

        var limit = EffectiveLimit(sample);
        var speed = Math.Max(sample.SpeedKmh, _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down)));
        if (Math.Max(sample.Rpm, speed * currentRatio) > limit * 0.45) return default;

        var landingRpm = ConservativeDownshiftRpm(sample, speed, currentRatio, lowerRatio);
        return landingRpm < limit * 0.89
            ? new(lowerGear, ShiftReason.Kickdown, LandingRpm: landingRpm)
            : default;
    }

    private double ConservativeDownshiftRpm(TelemetrySample sample, double speed, double currentRatio, double lowerRatio)
    {
        var wheelRpm = _wheelRatios.RpmFromDrivenWheels(sample, _gear - 1);
        if (wheelRpm <= 0 && _wheelRatios.DrivenWheelsOutrunRoad(sample)) return double.PositiveInfinity;

        // Road speed can underestimate landing RPM while the driven wheels spin.
        var landingRpm = Math.Max(wheelRpm, Math.Max(speed * lowerRatio, sample.Rpm * lowerRatio / currentRatio));
        return Math.Max(sample.EngineIdleRpm, landingRpm);
    }

    private ShiftDecision MatchDownshift(TelemetrySample sample, ShiftPedals pedals, ShiftDecision decision, bool braking, double slip, long now)
    {
        if (InDriftMode || _gear <= 1 || sample.SpeedKmh < 12 || !double.IsFinite(slip)) return default;
        var kickdown = pedals.Kickdown;
        if (!kickdown && slip >= 0.38) return default;
        var coasting = IsCoasting(pedals);
        var maxLatG = (_lastIntent.CornerExitWindow && _motion.SteerRate <= -0.10 ? 0.62 : 0.50) * TelemetryDerived.Gravity;
        if (braking)
        {
            var slowingWithGap = LearnedRoadRatio(_gear - 1) == 0 && _motion.SpeedRate < -2;
            if (decision.Target > 0 || (!decision.NeedsRatio && !slowingWithGap)) return default;
        }
        else if ((!coasting && !pedals.WantsPower) || Math.Abs(sample.AccelerationX) >= maxLatG)
            return default;

        var profile = _adaptiveProfile;
        if (profile?.Ratios[_gear] is > 0 and var savedRatio)
        {
            var liveRatio = LearnedRoadRatio(_gear);
            if (liveRatio == 0) liveRatio = sample.Rpm / sample.SpeedKmh;
            if (Math.Abs(liveRatio / savedRatio - 1) > 0.12) profile = null;
        }
        Span<double> ratios = stackalloc double[AutomaticShiftSettings.MaximumForwardGears + 1];
        for (var gear = 1; gear <= Math.Min(_gear + 2, AutomaticShiftSettings.MaximumForwardGears); gear++)
        {
            var live = LearnedRoadRatio(gear);
            ratios[gear] = live > 0 ? live : profile?.Ratios[gear] ?? 0;
        }
        var severelyLugging = _gear >= 4 && sample.Rpm <= sample.EngineMaxRpm * 0.37;
        if (slip >= 0.38 && !severelyLugging && (ratios[_gear] == 0 ||
            Math.Abs(sample.Rpm / (sample.SpeedKmh * ratios[_gear]) - 1) > 0.13)) return default;
        var safety = SafeDownshiftLimit(sample);
        if (safety <= 0) return default;
        var engagementSpeed = _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down));
        var match = SpeedGearMatcher.Downshift(sample, ratios, _strategy, engagementSpeed, safety, braking, coasting,
            out var destination, out var rpm, profile?.Curve, kickdown, _lastIntent);
        var severelyLuggingAtThrottle = pedals.WantsPower &&
            sample.Rpm <= Math.Max(sample.EngineIdleRpm * 2.0, sample.EngineMaxRpm * 0.28);
        var brakingLowRpmGap = braking && LearnedRoadRatio(_gear - 1) == 0 &&
            sample.Rpm <= sample.EngineMaxRpm * 0.42 && _motion.SpeedRate < -2;
        if (match.Target == 0 && (kickdown || severelyLuggingAtThrottle || brakingLowRpmGap) && _motion.HasContact(now) &&
            (_settings.AdaptiveGearMatching || ratios[_gear] > 0))
        {
            var ratioCanBeSampledFrom = Math.Max(_engagedSince ?? now, _gearSince + GearStableMs);
            var ratioSettling = _rpmPerKmh[_gear] == 0 && _ratioCandidate > 0 &&
                now - _ratioSince < RatioStableMs + 20 && now - ratioCanBeSampledFrom < 260;
            var verifyingLastGear = _lastShift is { Reason: ShiftReason.SpeedMatchedLearning } recent && recent.To == _gear &&
                (now - _lastShiftConfirmedAt < 220 || now - ratioCanBeSampledFrom < RatioStableMs + 20 || ratioSettling);
            if (!verifyingLastGear)
            {
                var maxProbeFraction = !_settings.AdaptiveGearMatching ? 0.28 : brakingLowRpmGap ? 0.42 : 0.58;
                match = SpeedGearMatcher.LearnLowerGear(sample, ratios, engagementSpeed, safety, maxProbeFraction);
                destination = match.Target;
                rpm = match.LandingRpm;
            }
        }
        if (destination > 0)
            _speedMatch = new(destination, (int)(sample.SpeedKmh / SpeedGearMatcher.BandKmh) * SpeedGearMatcher.BandKmh, DisplayRpm(rpm));
        return match;
    }

    private bool IsCoasting(ShiftPedals pedals) => pedals.Coasting && _motion.CoastingMs >= CoastIntentMs &&
        _motion.SpeedRate <= (_lastIntent.RoadGrade < -0.045 ? 8 : 2);

    private ShiftDecision DecideCoastDownshift(TelemetrySample sample, ShiftPedals pedals, double slip)
    {
        if (_gear <= 1 || InDriftMode || sample.SpeedKmh < 12 || !IsCoasting(pedals) || !(slip < 0.38) ||
            Math.Abs(sample.AccelerationX) >= 0.50 * TelemetryDerived.Gravity) return default;

        var lowerRatio = LearnedRoadRatio(_gear - 1);
        var currentRatio = LearnedRoadRatio(_gear);
        if (lowerRatio == 0 || lowerRatio <= currentRatio * 1.025) return default;
        var safety = SafeDownshiftLimit(sample);
        var speed = _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down));
        var limit = EffectiveLimit(sample);
        var currentRpm = currentRatio > 0 ? speed * currentRatio : sample.Rpm;
        var landingRpm = speed * lowerRatio;
        var (torque, _) = _strategy.PeakFractions(_adaptiveProfile?.Curve);
        var floorFactor = _lastIntent.RoadGrade < -0.045
            ? Math.Clamp(Math.Max(torque * 0.95, 0.72), 0.68, 0.74)
            : Math.Clamp(torque * 0.95, 0.60, 0.69);
        var floor = Math.Max(limit * floorFactor, sample.EngineIdleRpm * 1.6);
        return currentRpm < floor && landingRpm - currentRpm >= limit * 0.025 &&
            landingRpm < 0.89 * limit && Math.Max(speed, sample.SpeedKmh) * lowerRatio < safety
            ? new(_gear - 1, ShiftReason.CoastDownshift, LandingRpm: landingRpm) : default;
    }

    private double SafeDownshiftLimit(TelemetrySample sample)
    {
        var limit = EffectiveLimit(sample);
        var safety = 0.982 * limit;
        if (UsesAlternateStrategy && Algorithm == ShiftAlgorithm.Fh6AutoShift)
        {
            var cut = _fh6AutoShift.LearnedCutFor(_gear - 1);
            safety = Math.Min(safety, (cut > 0 ? cut : sample.EngineMaxRpm * 0.90) - 200);
            if (_wheelRatios.RpmFromDrivenWheels(sample, _gear - 1) * 0.75 >= safety) return 0;
        }
        return safety;
    }

    private double LearnedRoadRatio(int gear)
    {
        var grounded = _rpmPerKmh[gear];
        if (grounded > 0) return grounded;
        var precise = _lazyForza.RpmPerKmh(gear);
        if (!UsesAlternateStrategy && precise > 0) return precise;
        var estimate = _wheelRatios.RpmPerKmh(gear);
        return estimate > 0 ? estimate : precise;
    }

    private ShiftDecision DecideDrivingShift(TelemetrySample sample, ShiftPedals pedals, bool canUpshift, bool cleanTraction,
        bool stableSlope, bool measuredLimiter, bool straightSlipAtLimit)
    {
        var advice = LazyForzaShiftAdvisor.Recommend(_lazyForza.Targets, _gear, sample.Rpm, sample.EngineMaxRpm);
        // With very narrow RPM coverage, the upstream fallback can land outside the usable engine range.
        var rpmLimit = EffectiveLimit(sample);
        if (canUpshift && (measuredLimiter || _strategy.HasOutputLoss)) return new(_gear + 1, ShiftReason.HighRpmOutputLoss);
        var usesLazyForza = Algorithm == ShiftAlgorithm.LazyForza;
        var protectsFullLoad = pedals.Throttle >= 0.90;
        var learnedUp = usesLazyForza && advice.Upshift is { } up && up.TargetRpm <= sample.EngineMaxRpm * 1.05 ? up : null;
        if (canUpshift && learnedUp is { } target)
        {
            // LazyForza's advanced cue gives a person time to react. A virtual button has no such reaction delay.
            var shiftRpm = protectsFullLoad ? Math.Max(target.TargetRpm, rpmLimit * 0.995) : target.TargetRpm;
            if (sample.Rpm >= shiftRpm) return new(target.ToGear, ShiftReason.LazyForzaUpshift);
        }

        if (canUpshift && ((straightSlipAtLimit && usesLazyForza) || _strategy.IsAtLimiter(sample, rpmLimit, usesLazyForza)))
            return new(_gear + 1, ShiftReason.Limiter);

        var previousUpshift = advice.Downshift;
        if (previousUpshift is { } down && (down.AfterShiftRpm <= 0 || down.TargetRpm > sample.EngineMaxRpm * 1.05))
            previousUpshift = null;
        var canCornerExit = _lastIntent.CornerExitWindow && _motion.SteerRate <= -0.10;
        var canDownshift = _gear > 1 && pedals.Brake < BrakeShiftStrategy.MinimumBrake && cleanTraction &&
            Math.Abs(sample.AccelerationX) < (canCornerExit ? 0.62 : 0.50) * TelemetryDerived.Gravity;
        var speedAtEngagement = _braking.SpeedAtEngagement(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down), braking: false);
        if (canDownshift && stableSlope && previousUpshift is not null && advice.Gear < _gear)
        {
            var landingRpm = LazyForzaLandingRpm(sample, previousUpshift, speedAtEngagement);
            if (landingRpm < rpmLimit * 0.975)
                return new(advice.Gear, ShiftReason.LazyForzaDownshift, LandingRpm: landingRpm);
        }

        if (canUpshift && learnedUp is null)
        {
            var upshift = _strategy.DecideUpshift(sample, pedals.Throttle, _rpmPerKmh, _gear, ShiftLatency(ShiftDirection.Up),
                allowPredictive: usesLazyForza && !protectsFullLoad, intent: _lastIntent);
            if (upshift.Target != 0) return upshift;
        }
        if (!canDownshift) return default;
        if (!stableSlope) return new(0, ShiftReason.None, NeedsRatio: true);
        return previousUpshift is null ? _strategy.DecideDownshift(sample, pedals, LearnedRoadRatio(_gear - 1), _gear, speedAtEngagement, rpmLimit, _lastIntent) : default;
    }

    private double LazyForzaLandingRpm(TelemetrySample sample, LazyForzaShiftTarget previousUpshift, double speedAtEngagement)
    {
        var projectedRpm = sample.Rpm * previousUpshift.TargetRpm / previousUpshift.AfterShiftRpm;
        if (sample.SpeedKmh >= 12)
        {
            var slope = Math.Max(projectedRpm / sample.SpeedKmh, LearnedRoadRatio(previousUpshift.FromGear));
            projectedRpm = speedAtEngagement * slope;
        }
        return projectedRpm;
    }

    private double EffectiveLimit(TelemetrySample sample) =>
        _observedLimitRpm > 0 ? Math.Min(_observedLimitRpm, sample.EngineMaxRpm) : sample.EngineMaxRpm;

    private double ShiftLatency(ShiftDirection direction) => _settings.ShiftLatencyMs ?? EstimatedShiftLatency(direction);

    private double EstimatedShiftLatency(ShiftDirection direction)
    {
        var learned = _shiftLatencies[(int)_settings.GamepadMode, (int)direction];
        return learned > 0 ? learned : UsesAlternateStrategy && Algorithm == ShiftAlgorithm.Fh6AutoShift ? 65 : 180;
    }

    private bool AcceptFrame(TelemetrySample sample, long now)
    {
        var elapsed = now - (_lastFrameAt ?? now);
        var delta = _lastFrameAt.HasValue ? unchecked((int)(sample.TimestampMs - _lastTimestamp)) : 1;
        if (delta == 0 || (delta < 0 && elapsed <= SignalGapMs)) return false;

        if (sample.IsRaceOn && sample.CarOrdinal > 0 && IsValidRpmLimit(sample.EngineMaxRpm))
        {
            var vehicle = ShiftVehicleConfiguration.From(sample);
            if (!_vehicle.Matches(vehicle)) ResetVehicle(vehicle);
        }

        var signalGap = delta < 0 || delta > SignalGapMs || elapsed > SignalGapMs;
        var driving = sample.IsRaceOn && sample.CarOrdinal > 0;
        var eventTransition = !signalGap && driving && _lastSessionSample is { } previous &&
            sample.StartsNewDrivingSegment(previous, elapsed);

        if (signalGap || eventTransition || !driving) _collision.Reset();
        if (signalGap)
        {
            _unexplainedGearChanges = 0;
            _consecutiveConfirmationTimeouts = 0;
            _retryHoldUntil = 0;
            ClearGear();
            if (_enabled) Pause(AutomaticShiftState.WaitingForTelemetry, cancelPending: true);
        }
        else if (eventTransition)
        {
            _eventTransitionUntil = now + 500;
            _unexplainedGearChanges = 0;
            _consecutiveConfirmationTimeouts = 0;
            _retryHoldUntil = 0;
            ClearGear();
            if (_enabled) Pause(AutomaticShiftState.WaitingForDriving, cancelPending: true);
        }
        // Data Out has no garage-car ID, so a break in driving can hide a swap between identical configurations.
        if (signalGap || eventTransition || !sample.IsRaceOn || sample.CarOrdinal <= 0) ResetTransmissionCalibration();
        _lastSessionSample = driving ? sample : null;
        _lastFrameAt = now;
        _lastTimestamp = sample.TimestampMs;
        return true;
    }

    private void ResetTransmissionCalibration()
    {
        _transmission.Reset();
        SyncEffectiveAutomaticClutch();
    }

    private void ResetVehicle(ShiftVehicleConfiguration vehicle)
    {
        input.Release();
        RememberProfile();
        _vehicle = vehicle;
        _observedMaximumGear = 0;
        _observedLimitRpm = 0;
        _zeroOutputPlateauMs = 0;
        _collision.Reset();
        ResetGearProbe();
        _motion.ResetFlight();
        ClearGear();
        _directionChanged = false;
        _pending = _lastShift = null;
        _cooldownUntil = _manualUntil = _retryHoldUntil = _eventTransitionUntil = 0;
        _unexplainedGearChanges = 0;
        _consecutiveConfirmationTimeouts = 0;
        _lastSessionSample = null;
        Array.Clear(_rpmPerKmh);
        _strategy.Clear();
        _adaptiveProfile = _settings.UsesGearProfiles ? _profiles?.Find(vehicle) : null;
        _rememberedCurveRevision = _strategy.CurveRevision;
        _braking.Clear();
        _wheelRatios.Clear();
        _lazyForza.Clear();
        _fh6AutoShift.Clear();
        ResetTransmissionCalibration();
        _blipGain = 1.0;
        _blipWindow = null;
    }

    private void ClearGear()
    {
        _gear = 0;
        _stoppedSince = null;
        _reportedGear = null;
        _manualSuggestion = 0;
        _speedMatch = null;
        _engagedSince = null;
        _awaitingEngagement = false;
        _engagementRatio = 0;
        _launching = false;
        _launchGripUntil = 0;
        _ratioCandidate = 0;
        _candidate = default;
        _transmission.CancelActive();
        _blipWindow = null;
        _strategy.ResetTransient();
        _braking.BreakSequence();
        _wheelRatios.BreakSequence();
        _revLimiter.Reset();
        _zeroOutputPlateauMs = 0;
        _lazyForza.BreakSequence();
        ResetStrategyHistory();
    }

    private void ResetStrategyHistory()
    {
        _motion.Reset();
        _intent.Reset();
        _lastIntent = default;
        _virtualTcu.Reset();
        _fh6AutoShift.BreakSequence();
    }

    private bool LearnRatio(TelemetrySample sample, ShiftPedals pedals, long now, double drivenSlip, bool requireTravel)
    {
        // RPM/speed estimates the effective ratio for this car, including the final drive and tires.
        if (now - _gearSince < GearStableMs || sample.SpeedKmh < 12 || sample.Rpm < sample.EngineIdleRpm * 1.25 ||
            sample.Rpm / sample.EngineMaxRpm is < 0.22 or > 1.0 || sample.Clutch >= 0.08 || sample.HandBrake >= 0.08 ||
            (sample.Throttle >= 0.90 && sample.PowerKw < -1 && sample.TorqueNm < -1) ||
            (sample.Brake >= BrakeShiftStrategy.MinimumBrake && !(pedals.Kickdown && sample.Brake <= 0.22)) || !double.IsFinite(sample.AccelerationX) ||
            Math.Abs(sample.AccelerationX) > 0.45 * TelemetryDerived.Gravity || !(drivenSlip < 0.35))
        {
            _ratioCandidate = 0;
            return false;
        }

        var ratio = sample.Rpm / sample.SpeedKmh;
        if (_ratioCandidate == 0 || Math.Abs(ratio / _ratioCandidate - 1) > 0.045)
        {
            _ratioCandidate = ratio;
            _ratioSince = now;
            _ratioStartSpeed = sample.SpeedKmh;
            return false;
        }
        _ratioCandidate += (ratio - _ratioCandidate) * 0.2;
        if (now - _ratioSince < RatioStableMs) return false;

        var previous = _rpmPerKmh[_gear];
        var reference = previous > 0 ? previous : _engagementRatio;
        var different = reference > 0 && Math.Abs(_ratioCandidate / reference - 1) > RatioChangeThreshold;
        if (requireTravel || different)
        {
            // A new tune must explain RPM at multiple road speeds before replacing this car's profile.
            if (now - _ratioSince < 280 || Math.Abs(sample.SpeedKmh - _ratioStartSpeed) < Math.Max(5, _ratioStartSpeed * 0.04))
                return false;
            if (different) ClearLearnedRatios();
            _engagementRatio = 0;
            _rpmPerKmh[_gear] = _ratioCandidate;
            return true;
        }
        if (previous == 0 || Math.Abs(_ratioCandidate / previous - 1) <= 0.045)
            _rpmPerKmh[_gear] = previous == 0 ? _ratioCandidate : previous + (_ratioCandidate - previous) * 0.05;
        return false;
    }

    private void ClearLearnedRatios()
    {
        if (_settings.UsesGearProfiles) _profiles?.Forget(_vehicle);
        _adaptiveProfile = null;
        Array.Clear(_rpmPerKmh);
        _strategy.Clear();
        _braking.Clear();
        _lazyForza.Clear();
        _wheelRatios.Clear();
        _fh6AutoShift.Clear();
        ResetTransmissionCalibration();
        _blipGain = 1.0;
        _blipWindow = null;
    }

    private bool MatchesNewGear(TelemetrySample sample)
    {
        if (!_awaitingEngagement || sample.SpeedKmh < 12 || _lastShift is not { FromRatio: > 0 } last || last.To != _gear)
            return true;
        var live = sample.Rpm / sample.SpeedKmh;
        if (last.To > last.From) return live < last.FromRatio * 0.995;
        if (live <= last.FromRatio * 1.005) return false;
        for (var gear = last.To; gear >= 1; gear--)
        {
            var ratio = LearnedRoadRatio(gear);
            if (ratio > last.FromRatio) return live <= ratio * 1.12;
        }
        return true;
    }

    private bool RpmPlausibleForUpshift(TelemetrySample sample)
    {
        if (sample.SpeedKmh < 12) return true;
        var ratio = LearnedRoadRatio(_gear);
        var roadRpm = sample.SpeedKmh * (ratio > 0 ? ratio : _engagementRatio);
        if (roadRpm == 0 || sample.Rpm <= roadRpm + Math.Max(240, roadRpm * 0.12)) return true;
        var wheelRpm = _wheelRatios.RpmFromDrivenWheels(sample, _gear);
        return wheelRpm > 0 && Math.Abs(sample.Rpm - wheelRpm) <= wheelRpm * 0.08 &&
            DrivenSlipMagnitude(sample) < 0.35 && !_wheelRatios.DrivenWheelsOutrunRoad(sample);
    }

    private bool RatioMatches(TelemetrySample sample)
    {
        var learnedRatio = _rpmPerKmh[_gear];
        return sample.SpeedKmh < 12 || learnedRatio == 0 ||
            Math.Abs(sample.Rpm / (sample.SpeedKmh * learnedRatio) - 1) <= RatioChangeThreshold;
    }

    private static bool IsValidRpmLimit(double rpm) => rpm is >= 500 and <= 100_000;

    private static bool IsDrivingFrame(TelemetrySample sample) => sample.IsRaceOn && sample.CarOrdinal > 0 &&
        IsValidRpmLimit(sample.EngineMaxRpm) &&
        sample.EngineIdleRpm >= 0 && sample.EngineIdleRpm < sample.EngineMaxRpm && sample.Rpm > 0 &&
        sample.Rpm <= sample.EngineMaxRpm * 1.15 && sample.SpeedKmh is >= 0 and <= 720 &&
        sample.Throttle is >= 0 and <= 1 && sample.Brake is >= 0 and <= 1 &&
        sample.Clutch is >= 0 and <= 1 && sample.HandBrake is >= 0 and <= 1;

    private static double DrivenSlipMagnitude(TelemetrySample sample)
    {
        var front = Math.Max(Math.Abs(sample.FrontLeft.SlipRatio), Math.Abs(sample.FrontRight.SlipRatio));
        var rear = Math.Max(Math.Abs(sample.RearLeft.SlipRatio), Math.Abs(sample.RearRight.SlipRatio));
        return sample.DrivetrainType switch { 0 => front, 1 => rear, _ => Math.Max(front, rear) };
    }

    private bool ProtectLaunch(TelemetrySample sample, double slip, long now)
    {
        if (_gear > 2 || sample.SpeedKmh >= 55)
        {
            _launching = false;
            _launchGripUntil = 0;
            return false;
        }
        if (sample.SpeedKmh <= 12) _launching = true;
        if ((_launching || sample.SpeedKmh <= 45) && sample.Throttle >= 0.15 &&
            (slip >= 0.45 || _wheelRatios.DrivenWheelsOutrunRoad(sample)))
            _launchGripUntil = now + 120;
        if (sample.SpeedKmh >= 45 && now >= _launchGripUntil) _launching = false;
        return now < _launchGripUntil;
    }

    private void ObserveGear(int gear)
    {
        if (gear <= _observedMaximumGear) return;
        _observedMaximumGear = gear;
        if (gear > _failedProbeGear) ResetGearProbe();
    }

    private void ResetGearProbe()
    {
        _failedProbeGear = 0;
        _probeFailures = 0;
        _probeRetryAt = 0;
        _probedMaximumGear = null;
    }

    private void RequestShift(ShiftDecision decision, TelemetrySample sample, ShiftPedals pedals, long now)
    {
        var direction = decision.Target > _gear ? ShiftDirection.Up : ShiftDirection.Down;
        var drivenSlip = DrivenSlipMagnitude(sample);
        var useClutch = _transmission.SelectClutchForShift(_settings, direction, sample, pedals, drivenSlip, out var isClutchProbe);
        _traceSink?.Invoke(new(Environment.TickCount64, "shift_request", Gear: _gear, TargetGear: decision.Target,
            Reason: decision.Reason, Algorithm: Algorithm, DrivingMode: TcuMode, OutputMode: _settings.GamepadMode,
            LandingRpm: decision.LandingRpm, DriverThrottle: pedals.Throttle, DriverBrake: pedals.Brake,
            UsedClutch: useClutch));
        var shiftSettings = useClutch == _settings.AutomaticClutch ? _settings : _settings with { AutomaticClutch = useClutch };
        byte? blip = null;
        var expectedLandingRpm = decision.LandingRpm;
        if (direction == ShiftDirection.Down)
            blip = ComputeDownshiftBlip(decision, sample, pedals, useClutch, out expectedLandingRpm);
        var result = input.TryShift(direction, shiftSettings, blip);
        _traceSink?.Invoke(new(Environment.TickCount64, "shift_result", Gear: _gear, TargetGear: decision.Target,
            OutputMode: _settings.GamepadMode, Detail: result.ToString()));
        if (result == ShiftInputResult.PulseInProgress) return;
        _candidate = default;
        switch (result)
        {
            case ShiftInputResult.Sent:
                var isProbe = !_settings.MaximumGear.HasValue && direction == ShiftDirection.Up &&
                    decision.Target > _observedMaximumGear;
                var fromRatio = LearnableDepartureRatio(sample);
                _pending = new ShiftRequest(_gear, decision.Target, now, decision.Reason, isProbe, decision.LandingRpm, fromRatio, isClutchProbe);
                _transmission.BeginShift(direction, _gear, decision.Target, useClutch, isClutchProbe, sample, now);
                if (direction == ShiftDirection.Down && useClutch && blip is > 0 && expectedLandingRpm > 0 && sample.SpeedKmh >= 12)
                    _blipWindow = new BlipCalibration(decision.Target, expectedLandingRpm, sample.Rpm, double.PositiveInfinity, 0, now + ConfirmationMs, 0, 0);
                else
                    _blipWindow = null;
                SetState(isProbe ? AutomaticShiftState.ProbingGear : AutomaticShiftState.Shifting);
                break;
            case ShiftInputResult.NotInForeground:
                Pause(AutomaticShiftState.WaitingForGame);
                break;
            case ShiftInputResult.Busy when input.DriveAssistBlocksShift:
                Pause(AutomaticShiftState.DriveAssistHold);
                break;
            case ShiftInputResult.Busy:
                _manualUntil = now + ShiftCooldownMs;
                Pause(AutomaticShiftState.ManualHold);
                break;
            default:
                HoldOutputFailure();
                break;
        }
    }

    private byte? ComputeDownshiftBlip(ShiftDecision decision, TelemetrySample sample, ShiftPedals pedals, bool useClutch, out double expectedLandingRpm)
    {
        expectedLandingRpm = decision.LandingRpm;
        if (!useClutch) return null;
        if (expectedLandingRpm <= 0 && _gear > 1 && sample.SpeedKmh >= 12)
        {
            var lowerRatio = LearnedRoadRatio(_gear - 1);
            if (lowerRatio > 0)
                expectedLandingRpm = _motion.EngagementSpeed(sample.SpeedKmh, ShiftLatency(ShiftDirection.Down)) * lowerRatio;
        }
        if (sample.SpeedKmh <= 8) return 0;
        if (expectedLandingRpm <= 0)
            return _settings.GamepadMode == GamepadShiftMode.Combination ? (byte)160 : null;
        var gap = (expectedLandingRpm - sample.Rpm) / sample.EngineMaxRpm;
        if (gap < 0.015) return 0;
        var durationScale = Math.Clamp(120.0 / Math.Max(50, _settings.PressDurationMs), 0.78, 1.25);
        var intentScale = _lastIntent.DrivenWheelLockup || (_lastIntent.Kind == DriverIntentKind.TrailBraking && _lastIntent.CornerLoad >= 0.55)
            ? 1.08
            : pedals.Throttle >= 0.65 && !pedals.Braking ? 0.85 : 1.0;
        var throttle = Math.Clamp((0.28 + gap * 1.15) * durationScale * _blipGain * intentScale, 0.22, 0.85);
        return (byte)Math.Round(255 * throttle);
    }

    private void UpdateBlipCalibration(TelemetrySample sample, double drivenSlip, long now)
    {
        if (_blipWindow is not { } cal) return;
        if (cal.ConfirmedAt == 0)
        {
            if (now < cal.ObserveUntil)
                _blipWindow = cal with { MaxRpmSeen = Math.Max(cal.MaxRpmSeen, sample.Rpm) };
            else
                _blipWindow = null;
            return;
        }
        if (input.IsShiftPulseActive || sample.Clutch >= 0.08)
        {
            _blipWindow = cal with { MaxRpmSeen = Math.Max(cal.MaxRpmSeen, sample.Rpm) };
            return;
        }
        if (sample.Gear == cal.TargetGear && now < cal.ObserveUntil)
        {
            _blipWindow = cal with
            {
                MaxRpmSeen = Math.Max(cal.MaxRpmSeen, sample.Rpm),
                MinRpmSeen = Math.Min(cal.MinRpmSeen, sample.Rpm),
                MaxSlipSeen = Math.Max(cal.MaxSlipSeen, double.IsFinite(drivenSlip) ? drivenSlip : 0),
                EngagedSamples = cal.EngagedSamples + 1,
            };
            return;
        }
        if (cal.EngagedSamples >= 2)
        {
            var band = sample.EngineMaxRpm * 0.07;
            if (cal.MaxRpmSeen > cal.ExpectedLandingRpm + band)
                _blipGain = Math.Clamp(_blipGain * 0.90, 0.65, 1.40);
            else if (cal.MinRpmSeen < cal.ExpectedLandingRpm - band && cal.MaxSlipSeen >= 0.25)
                _blipGain = Math.Clamp(_blipGain * 1.10, 0.65, 1.40);
        }
        _blipWindow = null;
    }

    private double LearnableDepartureRatio(TelemetrySample sample)
    {
        if (!_settings.ProtectShiftReengagement || sample.SpeedKmh < 12) return 0;
        var learned = LearnedRoadRatio(_gear);
        var observed = sample.Rpm / sample.SpeedKmh;
        return learned > 0 && Math.Abs(observed / learned - 1) <= RatioChangeThreshold ? learned : observed;
    }

    private void ConfirmShift(ShiftRequest pending, TelemetrySample sample, ShiftPedals pedals, long now)
    {
        if (_gear == pending.To)
        {
            _traceSink?.Invoke(new(Environment.TickCount64, "shift_confirmed", Gear: pending.From, TargetGear: _gear,
                Reason: pending.Reason, OutputMode: _settings.GamepadMode, LandingRpm: sample.Rpm));
            ObserveGear(_gear);
            var latency = now - pending.RequestedAt;
            var direction = pending.To > pending.From ? ShiftDirection.Up : ShiftDirection.Down;
            if (!pending.IsClutchProbe)
            {
                ref var estimate = ref _shiftLatencies[(int)_settings.GamepadMode, (int)direction];
                var previousLatency = EstimatedShiftLatency(direction);
                estimate = Math.Clamp(previousLatency + (latency - previousLatency) * 0.30, 40, 600);
            }
            if (_blipWindow is { } cal && cal.TargetGear == _gear)
                _blipWindow = cal with { MaxRpmSeen = Math.Max(cal.MaxRpmSeen, sample.Rpm), ConfirmedAt = now, ObserveUntil = now + 220 };
            if (direction == ShiftDirection.Up &&
                ((UsesAlternateStrategy && Algorithm == ShiftAlgorithm.Fh6AutoShift) || pending.Reason == ShiftReason.Fh6FuelCut))
                _fh6AutoShift.ConfirmUpshift(pending.From, sample, pedals.Throttle, pending.Reason, now);
            _lastShift = pending;
            _awaitingEngagement = _settings.ProtectShiftReengagement;
            _engagedSince = null;
            _lastShiftConfirmedAt = now;
            _pending = null;
            _unexplainedGearChanges = 0;
            _consecutiveConfirmationTimeouts = 0;
            _retryHoldUntil = 0;
            _cooldownUntil = now + ShiftCooldownMs;
            SetState(AutomaticShiftState.Holding);
        }
        else if (_gear != pending.From && now - _gearSince >= GearStableMs)
        {
            var unexpectedGear = ++_unexplainedGearChanges >= 2;
            _manualUntil = now + ManualHoldMs;
            Pause(unexpectedGear ? AutomaticShiftState.UnexpectedGear : AutomaticShiftState.ManualHold, cancelPending: true);
        }
        else if (pending.IsClutchProbe && now - pending.RequestedAt >= ConfirmationMs)
        {
            CheckConfirmationTimeout(now);
        }
        else if (pending.IsGearProbe && _gear == pending.From && now - pending.RequestedAt >= ConfirmationMs)
        {
            if (UsesAlternateStrategy && Algorithm == ShiftAlgorithm.VirtualTcu)
            {
                _probeFailures = _failedProbeGear == pending.From ? Math.Min(5, _probeFailures + 1) : 1;
                _probedMaximumGear = pending.From;
                _probeRetryAt = now + Math.Min(8000, 800 << (_probeFailures - 1));
            }
            else
            {
                if (_failedProbeGear == pending.From) _probedMaximumGear = pending.From;
                _probeRetryAt = now + ShiftCooldownMs;
            }
            _failedProbeGear = pending.From;
            _pending = null;
            _candidate = default;
            _traceSink?.Invoke(new(Environment.TickCount64, "gear_probe_unconfirmed", Gear: _gear, TargetGear: pending.To));
            SetState(AutomaticShiftState.Holding);
        }
        else if (_gear == pending.From && _stoppedSince is long stopAt && pending.RequestedAt < stopAt &&
                 now - pending.RequestedAt >= ConfirmationMs)
        {
            // A request sent at speed should not stop the controller from recovering after a crash.
            Pause(AutomaticShiftState.Holding, cancelPending: true);
        }
        else if (!CheckConfirmationTimeout(now))
        {
            SetState(pending.IsGearProbe ? AutomaticShiftState.ProbingGear : AutomaticShiftState.Shifting);
        }
    }

    private bool CheckConfirmationTimeout(long now)
    {
        if (_pending is not ShiftRequest pending || now - pending.RequestedAt < ConfirmationMs) return false;
        _traceSink?.Invoke(new(Environment.TickCount64, pending.IsClutchProbe ? "clutch_probe_unconfirmed" : "shift_unconfirmed",
            Gear: pending.From, TargetGear: pending.To, OutputMode: _settings.GamepadMode, Detail: $"ObservedGear={_reportedGear}"));
        if (pending.IsClutchProbe)
        {
            _transmission.RequireClutchConfirmation();
            _retryHoldUntil = now + ShiftCooldownMs;
            Pause(AutomaticShiftState.Holding, cancelPending: true);
            return true;
        }
        _consecutiveConfirmationTimeouts = Math.Min(4, _consecutiveConfirmationTimeouts + 1);
        _retryHoldUntil = now + (900 << (_consecutiveConfirmationTimeouts - 1));
        Pause(AutomaticShiftState.ConfirmationTimedOut, cancelPending: true);
        return true;
    }

    private void CheckInputFailure()
    {
        if (_enabled && _outputReady && input.Error is not null) HoldOutputFailure();
    }

    private void HoldOutputFailure()
    {
        _outputReady = false;
        Pause(AutomaticShiftState.InputFailed, cancelPending: true);
    }

    private void Pause(AutomaticShiftState state, bool cancelPending = false)
    {
        if (cancelPending)
        {
            if (_state != state || _pending.HasValue)
            {
                input.Release();
                ResetStrategyHistory();
            }
            _pending = null;
            _transmission.CancelActive();
            _blipWindow = null;
            _speedMatch = null;
            _strategy.ResetTransient();
            _revLimiter.Reset();
            _zeroOutputPlateauMs = 0;
        }
        _candidate = default;
        SetState(state);
    }

    private void RememberProfile()
    {
        if (!_settings.UsesGearProfiles || _vehicle.Ordinal <= 0 || _profiles is not { } profiles)
            return;

        var previous = _adaptiveProfile ?? profiles.Find(_vehicle);
        var changed = _rememberedCurveRevision != _strategy.CurveRevision && _strategy.LearnedBins > 0;
        var compatible = true;
        for (var gear = 1; gear < _rpmPerKmh.Length; gear++)
        {
            var ratio = _rpmPerKmh[gear];
            var savedRatio = previous?.Ratios[gear] ?? 0;
            if (ratio > 0 && Math.Abs(ratio - savedRatio) > ratio * 0.01) changed = true;
            if (ratio > 0 && savedRatio > 0 && Math.Abs(ratio / savedRatio - 1) > RatioChangeThreshold) compatible = false;
        }
        if (changed)
        {
            var ratios = (double[])_rpmPerKmh.Clone();
            var curve = _strategy.CopyCurve();
            if (compatible && previous is not null)
            {
                for (var gear = 1; gear < ratios.Length; gear++)
                    if (ratios[gear] == 0) ratios[gear] = previous.Ratios[gear];
                for (var index = 0; index < curve.Length; index++)
                    if (curve[index].Power == 0) curve[index] = previous.Curve[index];
            }
            _adaptiveProfile = profiles.Remember(_vehicle, ratios, curve);
        }
        _rememberedCurveRevision = _strategy.CurveRevision;
    }

    public void SaveProfiles()
    {
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (_disposed || !_settings.UsesGearProfiles || now < _nextProfileSave) return;
            _nextProfileSave = now + 15_000;
            RememberProfile();
            _profiles?.Save();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            DisableOutput();
            input.Dispose();
            RememberProfile();
            _disposed = true;
        }
        _profiles?.Save(flush: true);
    }

    private readonly record struct ShiftRequest(int From, int To, long RequestedAt, ShiftReason Reason, bool IsGearProbe,
        double LandingRpm, double FromRatio, bool IsClutchProbe = false);

    private readonly record struct BlipCalibration(int TargetGear, double ExpectedLandingRpm, double MaxRpmSeen,
        double MinRpmSeen, long ConfirmedAt, long ObserveUntil, double MaxSlipSeen, int EngagedSamples);
}
