using System;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal readonly record struct ShiftGamepadReport(ushort Buttons, byte RightTrigger, byte LeftTrigger = 0,
    short LeftX = 0, short LeftY = 0, short RightX = 0, short RightY = 0)
{
    private const ushort LeftStickClick = 0x0040;
    private const ushort B = 0x2000;
    private const ushort X = 0x4000;
    internal const ushort ClutchButton = LeftStickClick;
    internal const ushort ManualShiftButtons = LeftStickClick | B | X;

    public static ShiftGamepadReport For(ShiftDirection direction, bool automaticClutch = true,
        byte downshiftTrigger = byte.MaxValue)
    {
        var button = direction == ShiftDirection.Up ? B : X;
        var report = new ShiftGamepadReport(button, 0);
        return automaticClutch ? report.WithManualShiftBindings(downshiftTrigger) : report;
    }

    public ShiftGamepadReport WithShift(ShiftGamepadReport shift) => this with
    {
        Buttons = (ushort)(Buttons | shift.Buttons),
        RightTrigger = Math.Max(RightTrigger, shift.RightTrigger),
    };

    public ShiftGamepadReport WithManualShiftBindings(byte downshiftTrigger = byte.MaxValue) => (Buttons & (B | X)) == 0 ? this : this with
    {
        Buttons = (ushort)(Buttons | LeftStickClick),
        RightTrigger = (Buttons & X) != 0 ? Math.Max(RightTrigger, downshiftTrigger) : RightTrigger,
    };
}

internal interface IShiftGamepad : IDisposable
{
    bool ForwardsPhysicalInput => false;
    ShiftGamepadReport ReadPhysical() => default;
    void Submit(ShiftGamepadReport report);
    void SetAssistVibration(byte strength) { }
}

internal sealed class IsolatedGamepadRelay(IShiftGamepad output, Func<int, ShiftGamepadReport?> read,
    int sourceSlot, int outputSlot) : IShiftGamepad
{
    private long _checkTopologyAt;

    public void SetAssistVibration(byte strength) => output.SetAssistVibration(strength);

    internal static IsolatedGamepadRelay Connect(Func<int, ShiftGamepadReport?> read,
        Func<int> visibleSlots, Func<int, IShiftGamepad> attach)
    {
        var physical = SlotMask(read);
        if (physical == 0 || (physical & (physical - 1)) != 0)
            throw new InvalidOperationException(TelemetryText.Get("PadErrNeedOnePhysical"));
        if (visibleSlots() != 0)
            throw new InvalidOperationException(TelemetryText.Get("PadErrStillVisible"));

        var sourceSlot = System.Numerics.BitOperations.TrailingZeroCount((uint)physical);
        var output = attach(sourceSlot);
        try
        {
            var addedSlotMask = 0;
            if (!SpinWait.SpinUntil(() =>
                {
                    var local = SlotMask(read);
                    addedSlotMask = local & ~physical;
                    return (local & physical) != 0 && addedSlotMask != 0 && (addedSlotMask & (addedSlotMask - 1)) == 0;
                }, TimeSpan.FromSeconds(1)) || visibleSlots() != addedSlotMask)
                throw new InvalidOperationException(TelemetryText.Get("PadErrCannotVerifyUnique"));

            var virtualSlot = System.Numerics.BitOperations.TrailingZeroCount((uint)addedSlotMask);
            return new IsolatedGamepadRelay(output, read, sourceSlot, virtualSlot);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public bool ForwardsPhysicalInput => true;

    public ShiftGamepadReport ReadPhysical()
    {
        if (!read(outputSlot).HasValue || read(sourceSlot) is not ShiftGamepadReport physical)
            throw new InvalidOperationException(TelemetryText.Get("PadErrDisconnected"));
        var now = Environment.TickCount64;
        if (now >= _checkTopologyAt)
        {
            _checkTopologyAt = now + 200;
            if (SlotMask(read) != ((1 << sourceSlot) | (1 << outputSlot)))
                throw new InvalidOperationException(TelemetryText.Get("PadErrExtraDevice"));
        }
        return physical;
    }

    public void Submit(ShiftGamepadReport report) => output.Submit(report);

    public void Dispose() => output.Dispose();

    internal static int SlotMask(Func<int, ShiftGamepadReport?> read)
    {
        var mask = 0;
        for (var slot = 0; slot < 4; slot++)
            if (read(slot).HasValue) mask |= 1 << slot;
        return mask;
    }
}

internal sealed class GamepadShiftInput : IAutomaticShiftInput
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan MinimumReleaseInterval = TimeSpan.FromMilliseconds(20);
    private readonly object _sync = new();
    private readonly Func<IShiftGamepad> _connect;
    private readonly Func<bool> _gameInForeground;
    private readonly Func<bool> _manualShiftHeld;
    private readonly Func<int>? _keyboardSteer;
    private readonly TimeProvider _clock;
    private readonly long _clockOrigin;
    private readonly ITimer _timer;
    private readonly DriveAssistController _driveAssist = new(new());
    private volatile bool _driveAssistArmed;
    private bool _readOnly;
    private IShiftGamepad? _gamepad;
    private GamepadShiftMode _mode;
    private bool _automaticClutch = true;
    private ShiftGamepadReport _physical;
    private ShiftGamepadReport _shift;
    private ShiftDirection? _shiftDirection;
    private ShiftTorquePlan? _activeShiftTorque;
    private long? _upshiftRestoreStart;
    private byte _upshiftRestoreFloor;
    private int _upshiftRestoreDurationMs;
    private ShiftGamepadReport? _lastReport;
    private uint? _lastAssistRefresh;
    private ShiftGamepadReport _lastInjection;
    private string? _error;
    private Action<ShiftTraceEvent>? _traceSink;
    private long? _pressedAt;
    private long? _releasedAt;
    private long _keyboardAt, _vibrationUntil, _nextVibrationAt;
    private double _keyboardAxis;
    private byte _vibration;
    private TimeSpan _pressDuration;
    private int _connectionGeneration;
    private bool _disposed;

    public GamepadShiftInput(Func<IShiftGamepad> connect, Func<bool> gameInForeground,
        Func<bool> manualShiftHeld, TimeProvider? clock = null, Func<int>? keyboardSteer = null)
    {
        _connect = connect;
        _gameInForeground = gameInForeground;
        _manualShiftHeld = manualShiftHeld;
        _keyboardSteer = keyboardSteer;
        _clock = clock ?? TimeProvider.System;
        _clockOrigin = _clock.GetTimestamp();
        _timer = _clock.CreateTimer(_ => CheckPulse(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsGameInForeground => _gameInForeground();

    public string? Error { get { lock (_sync) return _error; } }

    public bool ManualShiftHeld { get { lock (_sync) return IsManualHeld(); } }
    public bool IsShiftPulseActive { get { lock (_sync) return _pressedAt.HasValue; } }
    public bool DriveAssistBlocksShift { get { lock (_sync) return HoldsAutomaticShift; } }
    private bool HoldsAutomaticShift => _driveAssistArmed && _driveAssist.BlocksShift && _driveAssist.HasFreshData(DriveAssistNow);
    public ShiftPedals? DriverPedals
    {
        get
        {
            lock (_sync)
                return _gamepad?.ForwardsPhysicalInput == true ? new(_physical.RightTrigger / 255.0, _physical.LeftTrigger / 255.0) : null;
        }
    }
    public void SetTraceSink(Action<ShiftTraceEvent>? sink) => Volatile.Write(ref _traceSink, sink);
    public void SetAutomaticClutch(bool enabled) => Volatile.Write(ref _automaticClutch, enabled);

    public bool TryGetForwardedButtons(out ushort buttons)
    {
        lock (_sync)
        {
            buttons = _physical.Buttons;
            return _gamepad?.ForwardsPhysicalInput == true;
        }
    }

    public bool IsForwarding { get { lock (_sync) return _gamepad?.ForwardsPhysicalInput == true; } }
    public ShiftGamepadReport? PhysicalDriveReport
    {
        get { lock (_sync) return _gamepad?.ForwardsPhysicalInput == true ? _physical : null; }
    }

    private long DriveAssistNow => (long)_clock.GetElapsedTime(_clockOrigin).TotalMilliseconds;

    public bool ConfigureDriveAssist(DriveAssistSettings? settings)
    {
        lock (_sync)
        {
            if (settings?.ReadOnly == true) EnterReadOnly();
            _driveAssistArmed = !_readOnly && settings is not null && _gamepad?.ForwardsPhysicalInput == true;
            _driveAssist.Configure(_driveAssistArmed ? settings! : new());
            try { UpdateReport(); }
            catch (Exception error) { FailOutput(error); }
            return _driveAssistArmed;
        }
    }

    public void SetDriveAssistReadOnly(bool readOnly)
    {
        lock (_sync)
        {
            if (_readOnly == readOnly) return;
            if (readOnly) EnterReadOnly();
            else _readOnly = false;
            try { UpdateReport(); }
            catch (Exception error) { FailOutput(error); }
        }
    }

    private void EnterReadOnly()
    {
        _readOnly = true;
        _driveAssistArmed = false;
        _driveAssist.Configure(new());
        ClearPulse();
    }

    public void ExitDriveAssistDrift()
    {
        lock (_sync)
        {
            _driveAssist.RequestDriftExit();
            try { UpdateReport(); }
            catch (Exception error) { FailOutput(error); }
        }
    }

    public void ResetDriveAssistCalibration()
    {
        lock (_sync)
        {
            _driveAssist.ResetCalibration();
            try { UpdateReport(); }
            catch (Exception error) { FailOutput(error); }
        }
    }

    public DriveAssistFrame? FeedDriveAssist(TelemetrySample sample)
    {
        if (!_driveAssistArmed) return null;
        lock (_sync)
        {
            if (!_driveAssistArmed || !_driveAssist.Enabled || _gamepad?.ForwardsPhysicalInput != true) return null;
            try
            {
                UpdateReport(sample);
                return _driveAssist.LastFrame is { } frame
                    ? frame with { Physical = _physical, Output = _lastReport } : null;
            }
            catch (Exception error)
            {
                FailOutput(error);
                return null;
            }
        }
    }

    public DriveAssistStatus GetDriveAssistStatus(bool includeReports)
    {
        lock (_sync)
        {
            return _driveAssist.Snapshot(DriveAssistNow, _driveAssistArmed, includeReports);
        }
    }

    public void SetDriveAssistCruise(bool enable)
    {
        lock (_sync)
        {
            if (enable && _driveAssistArmed) _driveAssist.RequestCruise(DriveAssistNow);
            else _driveAssist.CancelCruise();
            try { UpdateReport(); }
            catch (Exception error) { FailOutput(error); }
        }
    }

    public void ResetDriveAssistLog() { lock (_sync) _driveAssist.ResetLog(); }

    public Task<string?> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectAsync(new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination }, cancellationToken);

    public async Task<string?> ConnectAsync(AutomaticShiftSettings settings, CancellationToken cancellationToken)
    {
        int generation;
        lock (_sync)
        {
            if (_disposed) return TelemetryText.Get("PadErrDisposed");
            if (_gamepad is not null)
            {
                _mode = settings.GamepadMode;
                SetAutomaticClutch(settings.AutomaticClutch);
                try
                {
                    UpdateReport();
                    return null;
                }
                catch (Exception error)
                {
                    FailOutput(error);
                    return _error;
                }
            }
            generation = _connectionGeneration;
        }

        IShiftGamepad gamepad;
        try { gamepad = await Task.Run(_connect, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = ex is TypeInitializationException ? ex.GetBaseException().Message : ex.Message;
            return TelemetryText.Format("PadErrConnectFailed", detail);
        }

        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested || generation != _connectionGeneration || _gamepad is not null)
            {
                gamepad.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                return _gamepad is not null ? null : TelemetryText.Get("PadErrConnectCanceled");
            }

            _gamepad = gamepad;
            _mode = settings.GamepadMode;
            SetAutomaticClutch(settings.AutomaticClutch);
            try
            {
                if (gamepad.ForwardsPhysicalInput)
                {
                    UpdateReport();
                    _timer.Change(PollInterval, PollInterval);
                }
                _error = null;
                return null;
            }
            catch (Exception ex)
            {
                FailOutput(ex);
                return _error;
            }
        }
    }

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings) =>
        TryShift(direction, settings, null, null);

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger) =>
        TryShift(direction, settings, downshiftTrigger, null);

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger,
        ShiftTorquePlan? torquePlan)
    {
        lock (_sync)
        {
            if (_gamepad is null) return ShiftInputResult.Failed;
            if (_readOnly || !IsGameInForeground) return ShiftInputResult.NotInForeground;
            try
            {
                _physical = _gamepad.ReadPhysical();
                if (IsManualHeld()) return ShiftInputResult.Busy;
                if (HoldsAutomaticShift) return ShiftInputResult.Busy;
                if (_pressedAt.HasValue || (_releasedAt is long released && _clock.GetElapsedTime(released) < MinimumReleaseInterval))
                    return ShiftInputResult.PulseInProgress;
                if (!IsGameInForeground) return ShiftInputResult.NotInForeground;

                _shift = ShiftGamepadReport.For(direction, settings.AutomaticClutch, downshiftTrigger ?? byte.MaxValue);
                _shiftDirection = direction;
                _pressedAt = _clock.GetTimestamp();
                _pressDuration = TimeSpan.FromMilliseconds(settings.PressDurationMs);
                if (_gamepad.ForwardsPhysicalInput && !_readOnly && direction == ShiftDirection.Up && torquePlan is { } plan)
                {
                    _activeShiftTorque = plan;
                    _upshiftRestoreStart = null;
                    _upshiftRestoreFloor = 0;
                    _upshiftRestoreDurationMs = 0;
                }
                else
                {
                    ClearShiftTorque();
                }
                Submit();
                if (!_gamepad.ForwardsPhysicalInput) _timer.Change(PollInterval, PollInterval);
                return ShiftInputResult.Sent;
            }
            catch (Exception ex)
            {
                FailOutput(ex);
                return ShiftInputResult.Failed;
            }
        }
    }

    private void CheckPulse()
    {
        lock (_sync)
        {
            try { UpdateReport(); }
            catch (Exception ex) { FailOutput(ex); }
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            try { ReleasePulse(); }
            catch (Exception ex) { FailOutput(ex); }
        }
    }

    private void ReleasePulse()
    {
        if (!_pressedAt.HasValue || _gamepad is null) return;
        _physical = _gamepad.ReadPhysical();
        ClearPulse();
        Submit();
    }

    private void ClearShiftTorque()
    {
        _activeShiftTorque = null;
        _upshiftRestoreStart = null;
        _upshiftRestoreFloor = 0;
        _upshiftRestoreDurationMs = 0;
    }

    private void ClearPulse(bool abort = true)
    {
        if (_pressedAt.HasValue) _releasedAt = _clock.GetTimestamp();
        _pressedAt = null;
        _shift = default;
        _shiftDirection = null;
        if (abort) ClearShiftTorque();
        if (_gamepad?.ForwardsPhysicalInput != true) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private bool IsManualHeld() => (_physical.Buttons & ShiftGamepadReport.ManualShiftButtons) != 0 || _manualShiftHeld();

    private void UpdateReport(TelemetrySample? sample = null)
    {
        if (_gamepad is null || (!_gamepad.ForwardsPhysicalInput && !_pressedAt.HasValue)) return;
        _physical = _gamepad.ReadPhysical();
        if (_pressedAt is long start)
        {
            if (!IsGameInForeground || IsManualHeld())
                ClearPulse();
            else if (_clock.GetElapsedTime(start) >= _pressDuration)
            {
                if (_gamepad.ForwardsPhysicalInput && !_readOnly && _shiftDirection == ShiftDirection.Up &&
                    _activeShiftTorque is { UpshiftRestoreMs: > 0 } plan)
                {
                    _upshiftRestoreStart = _clock.GetTimestamp();
                    _upshiftRestoreFloor = Math.Max((byte)115, plan.UpshiftEngageFloor);
                    _upshiftRestoreDurationMs = plan.UpshiftRestoreMs;
                    _activeShiftTorque = null;
                }
                else
                {
                    ClearShiftTorque();
                }
                ClearPulse(abort: false);
            }
        }
        var now = DriveAssistNow;
        var focused = IsGameInForeground;
        var driver = WithKeyboardSteering(now, focused);
        if (sample is not null && focused)
        {
            var rawShift = (_physical.Buttons & (ushort)ShiftShortcutButton.B) != 0 ? ShiftDirection.Up :
                (_physical.Buttons & (ushort)ShiftShortcutButton.X) != 0 ? ShiftDirection.Down : (ShiftDirection?)null;
            var shift = _shiftDirection ?? rawShift;
            if (shift == ShiftDirection.Up &&
                ((_shift.Buttons | _physical.Buttons) & ShiftGamepadReport.ClutchButton) == 0 &&
                sample.Clutch < 0.08)
                shift = null;
            _driveAssist.Feed(sample, driver, now, _pressedAt.HasValue, shift);
        }
        Submit(sample, driver);
    }

    private ShiftGamepadReport WithKeyboardSteering(long now, bool focused)
    {
        var dt = Math.Clamp(now - _keyboardAt, 0, 40) / 1000.0;
        _keyboardAt = now;
        if (_keyboardSteer is null || !focused || !_driveAssistArmed || !_driveAssist.KeyboardSteering ||
            !_driveAssist.HasFreshData(now) || Math.Abs((int)_physical.LeftX) > 2300)
        {
            _keyboardAxis = 0;
            return _physical;
        }

        var target = Math.Clamp(_keyboardSteer(), -1, 1);
        var step = dt * (target == 0 ? 5.5 : 3.3);
        _keyboardAxis += Math.Clamp(target - _keyboardAxis, -step, step);
        return _keyboardAxis == 0 ? _physical : _physical with { LeftX = (short)Math.Round(32767 * _keyboardAxis) };
    }

    private void Submit(TelemetrySample? sample = null, ShiftGamepadReport? driverReport = null)
    {
        var driver = driverReport ?? WithKeyboardSteering(DriveAssistNow, IsGameInForeground);
        var physical = !_readOnly && _mode == GamepadShiftMode.Combination && Volatile.Read(ref _automaticClutch) && _gamepad!.ForwardsPhysicalInput
            ? driver.WithManualShiftBindings() : driver;
        var report = physical.WithShift(_shift);
        var injectedThrottle = Math.Max(_shift.RightTrigger, physical.RightTrigger > _physical.RightTrigger ? physical.RightTrigger : (byte)0);
        if (_driveAssistArmed && _driveAssist.Enabled)
        {
            var now = DriveAssistNow;
            if (IsGameInForeground)
                report = _driveAssist.Render(driver, report, now, _shift.Buttons != 0, injectedThrottle);
            else if (_driveAssist.CruiseActive || _driveAssist.HasFreshData(now) || _driveAssist.LastFrame.HasValue)
                _driveAssist.Suspend();
        }

        if ((_activeShiftTorque.HasValue || _upshiftRestoreStart.HasValue) &&
            (_readOnly || !_gamepad!.ForwardsPhysicalInput || !IsGameInForeground || IsManualHeld() ||
             _physical.LeftTrigger >= 25 || _physical.RightTrigger < 90))
            ClearShiftTorque();

        if (_gamepad!.ForwardsPhysicalInput && !_readOnly)
        {
            if (_pressedAt.HasValue && _shiftDirection == ShiftDirection.Up &&
                _activeShiftTorque is { UpshiftPulseCeiling: byte ceiling })
            {
                report = report with { RightTrigger = Math.Min(report.RightTrigger, ceiling) };
            }
            else if (_upshiftRestoreStart is long restoreStart)
            {
                var elapsedMs = _clock.GetElapsedTime(restoreStart).TotalMilliseconds;
                if (elapsedMs >= _upshiftRestoreDurationMs || _upshiftRestoreDurationMs <= 0)
                {
                    ClearShiftTorque();
                }
                else if (report.RightTrigger > _upshiftRestoreFloor)
                {
                    var progress = Math.Clamp(elapsedMs / _upshiftRestoreDurationMs, 0, 1);
                    // Keep restored throttle >= UpshiftEngageFloor (~45%) while Buttons == 0 to avoid FH6's virtual pad idle deadzone.
                    var ramp = (byte)Math.Round(_upshiftRestoreFloor + (report.RightTrigger - _upshiftRestoreFloor) * progress);
                    report = report with { RightTrigger = Math.Min(report.RightTrigger, ramp) };
                }
            }
        }

        // Hold critical reductions explicitly while FH6 still reports fresh driving frames.
        const DriveAssistFeature pedalLimits = DriveAssistFeature.Abs | DriveAssistArbitrator.TractionLimits |
            DriveAssistFeature.TrailBrake | DriveAssistFeature.Launch | DriveAssistFeature.AdaptiveBraking | DriveAssistFeature.SpeedLimiter |
            DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability;
        var changed = _lastReport != report;
        var refresh = sample is not null && _lastAssistRefresh != sample.TimestampMs && (_driveAssist.Active & pedalLimits) != 0 &&
            (report.LeftTrigger < _physical.LeftTrigger || report.RightTrigger < _physical.RightTrigger);
        if (refresh) _lastAssistRefresh = sample!.TimestampMs;
        if (changed || refresh)
        {
            _gamepad!.Submit(report);
            _lastReport = report;
        }
        UpdateAssistVibration();

        var sink = Volatile.Read(ref _traceSink);
        if (_driveAssistArmed && sample is not null && sink is not null)
            sink(new(Environment.TickCount64, "assist_frame", Gear: sample.Gear, OutputMode: _mode, Report: report,
                Detail: changed ? "submitted" : refresh ? "refreshed" : "held", TelemetryTimestampMs: sample.TimestampMs,
                PhysicalReport: _physical, AppliedAssist: _driveAssist.Active, AssistFresh: _driveAssist.HasFreshData(DriveAssistNow)));

        var injected = new ShiftGamepadReport((ushort)(_shift.Buttons | (physical.Buttons & ~_physical.Buttons)),
            injectedThrottle);
        if (injected == _lastInjection) return;
        _lastInjection = injected;
        sink?.Invoke(new(Environment.TickCount64, injected == default ? "gamepad_release" : "gamepad_apply",
            OutputMode: _mode, InjectedButtons: injected.Buttons, InjectedRightTrigger: injected.RightTrigger, Report: report));
    }

    private void UpdateAssistVibration()
    {
        var now = DriveAssistNow;
        var eligible = _driveAssistArmed && _driveAssist.WarnWithVibration && IsGameInForeground && _driveAssist.HasFreshData(now);
        if (!eligible) _vibrationUntil = 0;
        var lateral = _driveAssist.LateralStatus;
        if (eligible && now >= _nextVibrationAt && (lateral.Risk > 0.72 || lateral.RollRisk > 0.68 || lateral.BottomedOut ||
            _driveAssist.PedalStatus.TireTemperatureMask != 0))
        {
            _vibrationUntil = now + 145;
            _nextVibrationAt = now + 950;
        }
        var strength = eligible && now < _vibrationUntil ? (byte)100 : (byte)0;
        if (_vibration == strength) return;
        _gamepad!.SetAssistVibration(strength);
        _vibration = strength;
    }

    private void FailOutput(Exception error)
    {
        _error = TelemetryText.Format("PadErrOutputFailed", error.Message);
        Volatile.Read(ref _traceSink)?.Invoke(new(Environment.TickCount64, "gamepad_error", OutputMode: _mode, Detail: error.Message));
        CloseGamepad();
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            Release();
            CloseGamepad();
        }
    }

    private void CloseGamepad()
    {
        _connectionGeneration++;
        var gamepad = _gamepad;
        _gamepad = null;
        _driveAssistArmed = false;
        _driveAssist.Configure(new());
        _physical = default;
        _lastReport = null;
        _lastAssistRefresh = null;
        _lastInjection = default;
        _keyboardAxis = 0;
        _vibration = 0;
        _vibrationUntil = _nextVibrationAt = 0;
        ClearPulse();
        _releasedAt = null;
        gamepad?.Dispose();
        if (gamepad is not null)
            Volatile.Read(ref _traceSink)?.Invoke(new(Environment.TickCount64, "gamepad_disconnect", OutputMode: _mode));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            Disconnect();
            _timer.Dispose();
            _disposed = true;
        }
    }
}
