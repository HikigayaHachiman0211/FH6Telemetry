using System;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal readonly record struct KeyboardShiftKeys(byte Up, byte Down, byte Clutch, byte Throttle)
{
    public static bool TryParse(AutomaticShiftSettings settings, out KeyboardShiftKeys keys)
    {
        var up = ParseKey(settings.KeyboardUpshift);
        var down = ParseKey(settings.KeyboardDownshift);
        var clutch = ParseKey(settings.KeyboardClutch);
        var throttle = ParseKey(settings.KeyboardThrottle);
        keys = new((byte)up, (byte)down, (byte)clutch, (byte)throttle);
        return up > 0 && down > 0 && up != down && clutch >= 0 && throttle >= 0 &&
            (clutch == 0 || (clutch != up && clutch != down && clutch != throttle)) &&
            (throttle == 0 || (throttle != up && throttle != down));
    }

    private static int ParseKey(string key)
    {
        var name = key.Trim().ToUpperInvariant();
        if (name.Length == 0) return 0;
        if (name is "左 SHIFT" or "左SHIFT" or "LSHIFT" or "LEFT SHIFT" or "SHIFT" or "왼쪽 SHIFT") return 0xA0;
        if (name.Length == 1 && name[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) return name[0];
        return -1;
    }
}

internal sealed class KeyboardShiftInput : IAutomaticShiftInput
{
    private const int ClutchLeadMs = 25;
    private const int ReleaseGapMs = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private readonly object _sync = new();
    private readonly Func<bool> _isForeground;
    private readonly Func<bool> _manualGamepad;
    private readonly Func<byte, bool> _isPhysicalKeyDown;
    private readonly bool _physicalStateIsReliable;
    private readonly Func<byte[], bool, int> _submit;
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private KeyboardShiftKeys _keys;
    private PulseStage _stage;
    // Keys we have successfully pressed, in release order.
    private byte[] _keyUps = [];
    private byte _gearKey;
    private bool _connected;
    private bool _disposed;
    private long _stageStartedAt;
    private TimeSpan _stageDuration;
    private TimeSpan _pressDuration;
    private string? _error;
    private Action<ShiftTraceEvent>? _traceSink;

    public KeyboardShiftInput(Func<bool> isForeground, Func<bool> manualGamepad,
        Func<byte, bool> isPhysicalKeyDown, Func<byte[], bool, int> submit,
        bool physicalStateIsReliable, TimeProvider? clock = null)
    {
        _isForeground = isForeground;
        _manualGamepad = manualGamepad;
        _isPhysicalKeyDown = isPhysicalKeyDown;
        _physicalStateIsReliable = physicalStateIsReliable;
        _submit = submit;
        _clock = clock ?? TimeProvider.System;
        _timer = _clock.CreateTimer(_ => CheckPulse(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsGameInForeground => _isForeground();
    public string? Error { get { lock (_sync) return _error; } }
    public bool ManualShiftHeld { get { lock (_sync) return IsManualHeld(); } }
    public bool IsShiftPulseActive { get { lock (_sync) return _stage != PulseStage.None || _keyUps.Length > 0; } }
    public void SetTraceSink(Action<ShiftTraceEvent>? sink) => Volatile.Write(ref _traceSink, sink);

    public Task<string?> ConnectAsync(CancellationToken cancellationToken)
        => ConnectAsync(new AutomaticShiftSettings(), cancellationToken);

    public Task<string?> ConnectAsync(AutomaticShiftSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_disposed) return Task.FromResult<string?>(TelemetryText.Get("KbErrDisposed"));
            if (!KeyboardShiftKeys.TryParse(settings, out var keys))
                return Task.FromResult<string?>(TelemetryText.Get("KbErrInvalidKeys"));
            ReleasePulse();
            if (_keyUps.Length > 0) return Task.FromResult(_error);
            _keys = keys;
            _connected = true;
            _error = null;
            return Task.FromResult<string?>(null);
        }
    }

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings) =>
        TryShift(direction, settings, null);

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger)
    {
        lock (_sync)
        {
            if (!_connected || _error is not null) return ShiftInputResult.Failed;
            if (!IsGameInForeground) return ShiftInputResult.NotInForeground;
            if (IsManualHeld()) return ShiftInputResult.Busy;
            if (_stage != PulseStage.None || _keyUps.Length > 0) return ShiftInputResult.PulseInProgress;

            _gearKey = direction == ShiftDirection.Up ? _keys.Up : _keys.Down;
            _pressDuration = TimeSpan.FromMilliseconds(settings.PressDurationMs);
            var hasClutch = settings.AutomaticClutch && _keys.Clutch != 0;
            var blip = hasClutch && direction == ShiftDirection.Down && downshiftTrigger != 0 ? _keys.Throttle : (byte)0;
            if (blip != 0 && _isPhysicalKeyDown(blip))
            {
                if (!_physicalStateIsReliable) return ShiftInputResult.Busy;
                blip = 0;
            }

            if (!IsGameInForeground) return ShiftInputResult.NotInForeground;
            byte[] initialKeys = !hasClutch ? [_gearKey] : blip == 0 ? [_keys.Clutch] : [_keys.Clutch, blip];
            if (!PressKeys(initialKeys)) return ShiftInputResult.Failed;
            if (hasClutch) Enter(PulseStage.ClutchLead, TimeSpan.FromMilliseconds(ClutchLeadMs));
            else Enter(PulseStage.GearHeld, _pressDuration);
            return ShiftInputResult.Sent;
        }
    }

    private bool IsManualHeld() => _manualGamepad() || (_connected &&
        (IsOriginalKeyHeld(_keys.Up) || IsOriginalKeyHeld(_keys.Down) || IsOriginalKeyHeld(_keys.Clutch)));

    private bool IsOriginalKeyHeld(byte key) => key != 0 && _isPhysicalKeyDown(key) &&
        (_physicalStateIsReliable || !Array.Exists(_keyUps, held => held == key));

    private void CheckPulse()
    {
        lock (_sync)
        {
            if (_disposed || (_stage == PulseStage.None && _keyUps.Length == 0)) return;
            if (_stage == PulseStage.None || !IsGameInForeground || IsManualHeld())
            {
                ReleasePulse();
                return;
            }
            var remaining = _stageDuration - _clock.GetElapsedTime(_stageStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                SchedulePoll(remaining);
                return;
            }

            switch (_stage)
            {
                case PulseStage.ClutchLead:
                    if (PressKeys([_gearKey])) Enter(PulseStage.GearHeld, _pressDuration);
                    break;
                case PulseStage.GearHeld:
                    if (_keyUps.Length > 1 && ReleaseKeys(1))
                    {
                        Enter(PulseStage.Trailing, TimeSpan.FromMilliseconds(ReleaseGapMs));
                        break;
                    }
                    ReleasePulse();
                    break;
                case PulseStage.Trailing:
                    ReleasePulse();
                    break;
            }
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            if (!_disposed) ReleasePulse();
        }
    }

    private bool PressKeys(byte[] keys)
    {
        var sent = _submit(keys, false);
        Volatile.Read(ref _traceSink)?.Invoke(new(Environment.TickCount64, "keyboard_down", OutputMode: GamepadShiftMode.Keyboard,
            VirtualKeysHex: Convert.ToHexString(keys), AcceptedKeys: sent));
        var pressed = keys[..sent];
        Array.Reverse(pressed);
        _keyUps = [.. pressed, .. _keyUps];
        if (sent == keys.Length) return true;
        _error = TelemetryText.Get("KbErrPrivilege");
        ReleasePulse();
        return false;
    }

    private bool ReleaseKeys(int count)
    {
        var keys = _keyUps[..count];
        if (_physicalStateIsReliable)
            keys = Array.FindAll(keys, key => !_isPhysicalKeyDown(key));
        var sent = keys.Length == 0 ? 0 : _submit(keys, true);
        if (keys.Length > 0)
            Volatile.Read(ref _traceSink)?.Invoke(new(Environment.TickCount64, "keyboard_up", OutputMode: GamepadShiftMode.Keyboard,
                VirtualKeysHex: Convert.ToHexString(keys), AcceptedKeys: sent));
        _keyUps = [.. keys.AsSpan(sent), .. _keyUps.AsSpan(count)];
        if (sent == keys.Length) return true;
        _error ??= TelemetryText.Get("KbErrReleaseRejected");
        return false;
    }

    private void ReleasePulse()
    {
        _stage = PulseStage.None;
        if (ReleaseKeys(_keyUps.Length))
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        SchedulePoll(PollInterval);
    }

    private void Enter(PulseStage stage, TimeSpan duration)
    {
        _stage = stage;
        _stageStartedAt = _clock.GetTimestamp();
        _stageDuration = duration;
        SchedulePoll(duration);
    }

    private void SchedulePoll(TimeSpan remaining) =>
        _timer.Change(remaining < PollInterval ? remaining : PollInterval, Timeout.InfiniteTimeSpan);

    public void Disconnect()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ReleasePulse();
            _connected = false;
        }
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

    private enum PulseStage { None, ClutchLead, GearHeld, Trailing }
}
