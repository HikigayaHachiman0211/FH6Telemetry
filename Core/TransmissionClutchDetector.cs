using System;

namespace FH6Telemetry;

internal enum TransmissionClutchVerdict
{
    Unknown,
    NeedsBaseline,
    ReadyToProbe,
    Probing,
    RaceClutchless,
    StandardRequiresClutch,
    GameAutoClutch,
}

internal readonly record struct TransmissionClutchStatus(
    TransmissionClutchVerdict Verdict,
    int ClutchedMs,
    int ProbeMs);

internal sealed class TransmissionClutchDetector
{
    private const long ProbePowerWindowMs = 450;
    private const double MinThrottleDuringObservation = 0.22;
    private const double MinThrottleToLaunchProbe = 0.25;
    private const double MaxSlipToLaunchProbe = 1.25;

    private double _clutchedLatencyMs;
    private double _clutchedRecoveryMs;
    private int _clutchedSamples;
    private int _lastProbeRecoveryMs;
    private int _probeAttempts;
    private bool _needsBaselineFallback;
    private bool _unconfirmedClutchless;
    private bool _sawPositivePower;
    private int _lastForwardGear;
    private bool _lastForwardLoadOk;
    private long _recentClutchUntil;
    private ActiveShift? _active;
    private SettlingShift? _settling;
    private PassiveShift? _passive;

    private TransmissionClutchVerdict Verdict { get; set; }
    private bool IsProbing => _active is { IsProbe: true } || _settling is { Shift.IsProbe: true };

    public TransmissionClutchStatus GetStatus(AutomaticShiftSettings settings)
    {
        if (!settings.AutoDetectRaceTransmission)
            return new(TransmissionClutchVerdict.Unknown, 0, 0);

        var verdict = IsProbing ? TransmissionClutchVerdict.Probing
            : Verdict != TransmissionClutchVerdict.Unknown ? Verdict
            : _unconfirmedClutchless || _needsBaselineFallback && _clutchedSamples == 0 ? TransmissionClutchVerdict.NeedsBaseline
            : TransmissionClutchVerdict.ReadyToProbe;
        var clutchedMs = _clutchedSamples > 0 ? (int)Math.Round(_sawPositivePower ? _clutchedRecoveryMs : _clutchedLatencyMs) : 0;
        return new(verdict, clutchedMs, _lastProbeRecoveryMs);
    }

    public bool EffectiveAutomaticClutch(AutomaticShiftSettings settings)
    {
        if (settings.AutoDetectRaceTransmission)
            return Verdict is not (TransmissionClutchVerdict.RaceClutchless or TransmissionClutchVerdict.GameAutoClutch);
        return settings.AutomaticClutch;
    }

    public bool SelectClutchForShift(AutomaticShiftSettings settings, ShiftDirection direction,
        TelemetrySample sample, ShiftPedals pedals, double drivenSlip, out bool isProbe)
    {
        isProbe = false;
        if (!settings.AutoDetectRaceTransmission)
            return settings.AutomaticClutch;
        if (Verdict is TransmissionClutchVerdict.RaceClutchless or TransmissionClutchVerdict.GameAutoClutch) return false;
        if (Verdict == TransmissionClutchVerdict.StandardRequiresClutch) return true;
        if (_unconfirmedClutchless || _needsBaselineFallback && _clutchedSamples == 0) return true;

        // Probe clutchless immediately on the very first qualifying automatic upshift (1-shift seamless detection).
        if (direction == ShiftDirection.Up && !pedals.Braking && pedals.Throttle >= MinThrottleToLaunchProbe &&
            sample.SpeedKmh >= 15 && sample.Gear >= 1 &&
            (!double.IsFinite(drivenSlip) || drivenSlip < MaxSlipToLaunchProbe))
        {
            isProbe = true;
            return false;
        }

        return true;
    }

    public void BeginShift(ShiftDirection direction, int fromGear, int toGear, bool usedClutch, bool isProbe,
        TelemetrySample sample, long now)
    {
        if (IsPositivePower(sample)) _sawPositivePower = true;
        _settling = null;
        _passive = null;
        _active = new ActiveShift(direction, fromGear, toGear, usedClutch, isProbe, _sawPositivePower, now);
    }

    public void CancelActive()
    {
        _active = null;
        _settling = null;
        _passive = null;
    }

    public void RequireClutchConfirmation() => _unconfirmedClutchless = true;

    public void ObserveFrame(TelemetrySample sample, ShiftPedals pedals, long now, long elapsedMs, bool passiveEnabled = true)
    {
        if (IsPositivePower(sample)) _sawPositivePower = true;
        if (sample.Clutch >= 0.15) _recentClutchUntil = now + 100;

        if (_active is { } active)
        {
            if (pedals.Braking || pedals.Throttle < MinThrottleDuringObservation)
                active.ValidLoad = false;
            if (sample.Gear is 11 or byte.MaxValue)
                active.BetweenGearsMs += Math.Clamp(elapsedMs, 0, 100);
            if (!active.UsedClutch && sample.Clutch >= 0.15)
                active.SawGameClutch = true;

            if (sample.Gear == active.To)
            {
                if (_unconfirmedClutchless && active.UsedClutch)
                {
                    Verdict = TransmissionClutchVerdict.StandardRequiresClutch;
                    _unconfirmedClutchless = false;
                }
                var confirmMs = Math.Max(0, now - active.RequestedAt);
                _active = null;
                if (active.Direction == ShiftDirection.Up && (active.IsProbe || active.UsedClutch))
                {
                    if (!active.ExpectsPower || IsEngagedPositivePower(sample))
                        CompleteObservation(active, confirmMs, confirmMs);
                    else
                        _settling = new SettlingShift(active, confirmMs, now);
                }
            }
            UpdateForwardGearTracker(sample, pedals);
            return;
        }

        if (_settling is { } settling)
        {
            if (pedals.Braking || pedals.Throttle < MinThrottleDuringObservation)
                settling.Shift.ValidLoad = false;
            if (!settling.Shift.UsedClutch && sample.Clutch >= 0.15)
                settling.Shift.SawGameClutch = true;

            if (IsEngagedPositivePower(sample) || now - settling.ConfirmedAt >= ProbePowerWindowMs)
            {
                var recoveryMs = Math.Max(settling.ConfirmMs, now - settling.Shift.RequestedAt);
                _settling = null;
                CompleteObservation(settling.Shift, settling.ConfirmMs, recoveryMs);
            }
            UpdateForwardGearTracker(sample, pedals);
            return;
        }

        if (passiveEnabled && Verdict == TransmissionClutchVerdict.Unknown)
            ObservePassiveUpshift(sample, pedals, now, elapsedMs);

        UpdateForwardGearTracker(sample, pedals);
    }

    private void UpdateForwardGearTracker(TelemetrySample sample, ShiftPedals pedals)
    {
        if (sample.Gear is >= 1 and <= AutomaticShiftSettings.MaximumForwardGears)
        {
            _lastForwardGear = sample.Gear;
            _lastForwardLoadOk = !pedals.Braking && pedals.Throttle >= MinThrottleDuringObservation && sample.SpeedKmh >= 15;
        }
        else if (sample.Gear == 0)
        {
            _lastForwardGear = 0;
            _lastForwardLoadOk = false;
        }
    }

    private void ObservePassiveUpshift(TelemetrySample sample, ShiftPedals pedals, long now, long elapsedMs)
    {
        if (_passive is null)
        {
            if (_lastForwardGear is < 1 or >= AutomaticShiftSettings.MaximumForwardGears || !_lastForwardLoadOk)
                return;

            if (sample.Gear is 11 or byte.MaxValue)
            {
                _passive = new PassiveShift(_lastForwardGear, _lastForwardGear + 1, now,
                    BetweenGearsMs: Math.Clamp(elapsedMs, 0, 100),
                    SawClutch: sample.Clutch >= 0.15 || now <= _recentClutchUntil,
                    ExpectsPower: _sawPositivePower);
            }
            else if (sample.Gear == _lastForwardGear + 1 && _sawPositivePower)
            {
                var confirmMs = Math.Clamp(elapsedMs, 1, 100);
                var passive = new PassiveShift(_lastForwardGear, sample.Gear, now - confirmMs,
                    BetweenGearsMs: 0,
                    SawClutch: sample.Clutch >= 0.15 || now <= _recentClutchUntil,
                    ExpectsPower: true)
                {
                    Confirmed = true,
                    ConfirmMs = confirmMs,
                    ConfirmedAt = now,
                };
                if (IsEngagedPositivePower(sample))
                    CompletePassive(passive, confirmMs, confirmMs);
                else
                    _passive = passive;
            }
            return;
        }

        var current = _passive;
        if (pedals.Braking || pedals.Throttle < MinThrottleDuringObservation)
            current.ValidLoad = false;
        if (sample.Clutch >= 0.15)
            current.SawClutch = true;

        if (!current.Confirmed)
        {
            if (sample.Gear is 11 or byte.MaxValue)
            {
                current.BetweenGearsMs += Math.Clamp(elapsedMs, 0, 100);
                if (now - current.StartedAt > 900) _passive = null;
            }
            else if (sample.Gear == current.To)
            {
                var confirmMs = Math.Max(1, now - current.StartedAt);
                if (!current.ExpectsPower || IsEngagedPositivePower(sample))
                {
                    CompletePassive(current, confirmMs, confirmMs);
                }
                else
                {
                    current.Confirmed = true;
                    current.ConfirmMs = confirmMs;
                    current.ConfirmedAt = now;
                }
            }
            else
            {
                _passive = null;
            }
            return;
        }

        if (sample.Gear != current.To)
        {
            _passive = null;
            return;
        }

        if (IsEngagedPositivePower(sample) || now - current.ConfirmedAt >= ProbePowerWindowMs)
            CompletePassive(current, current.ConfirmMs, Math.Max(current.ConfirmMs, now - current.StartedAt));
    }

    private void CompletePassive(PassiveShift passive, long confirmMs, long recoveryMs)
    {
        _passive = null;
        if (!passive.ValidLoad || !passive.ExpectsPower) return;

        if (passive.SawClutch)
        {
            RecordClutchedBaseline(confirmMs, recoveryMs);
            return;
        }

        _lastProbeRecoveryMs = (int)recoveryMs;
        if (passive.BetweenGearsMs <= 145 && recoveryMs <= 245 && confirmMs <= 215)
            Verdict = TransmissionClutchVerdict.RaceClutchless;
        else if (passive.BetweenGearsMs >= 165 || recoveryMs >= 285 || confirmMs >= 245)
            Verdict = TransmissionClutchVerdict.StandardRequiresClutch;
    }

    private void CompleteObservation(ActiveShift shift, long confirmMs, long recoveryMs)
    {
        if (shift.UsedClutch)
        {
            if (!shift.ValidLoad) return;
            RecordClutchedBaseline(confirmMs, recoveryMs);
            _needsBaselineFallback = false;
            return;
        }

        if (!shift.IsProbe) return;
        if (shift.SawGameClutch)
        {
            _lastProbeRecoveryMs = (int)recoveryMs;
            Verdict = TransmissionClutchVerdict.GameAutoClutch;
            return;
        }
        if (!shift.ValidLoad) return;

        _lastProbeRecoveryMs = (int)recoveryMs;
        var baselineConfirm = _clutchedSamples > 0 ? _clutchedLatencyMs : 105.0;
        var baselineRecovery = _clutchedSamples > 0 ? _clutchedRecoveryMs : 185.0;

        // Race transmissions shift cleanly without clutch (~<= 215 ms confirm, <= 145 ms neutral, <= 255 ms recovery),
        // while non-race transmissions pay the long torque cut / shift penalty (~>= 245 ms / >= 165 ms / >= 285 ms).
        if (confirmMs <= Math.Max(215.0, baselineConfirm + 110.0) &&
            shift.BetweenGearsMs <= 145 &&
            recoveryMs <= Math.Max(255.0, baselineRecovery + 55.0))
        {
            Verdict = TransmissionClutchVerdict.RaceClutchless;
        }
        else if (confirmMs >= Math.Max(245.0, baselineConfirm + 135.0) ||
                 shift.BetweenGearsMs >= 165 ||
                 recoveryMs >= Math.Max(285.0, baselineRecovery + 85.0))
        {
            Verdict = TransmissionClutchVerdict.StandardRequiresClutch;
        }
        else if (++_probeAttempts >= 2)
        {
            Verdict = TransmissionClutchVerdict.StandardRequiresClutch;
        }
        else if (_clutchedSamples == 0)
        {
            // Rare ambiguous grey-zone probe: grab one clutched baseline next shift, then decide on the following shift.
            _needsBaselineFallback = true;
        }
    }

    private void RecordClutchedBaseline(long confirmMs, long recoveryMs)
    {
        _clutchedLatencyMs = _clutchedSamples == 0 ? confirmMs : _clutchedLatencyMs + (confirmMs - _clutchedLatencyMs) * 0.35;
        _clutchedRecoveryMs = _clutchedSamples == 0 ? recoveryMs : _clutchedRecoveryMs + (recoveryMs - _clutchedRecoveryMs) * 0.35;
        _clutchedSamples++;
    }

    private static bool IsPositivePower(TelemetrySample sample) =>
        double.IsFinite(sample.PowerKw) && double.IsFinite(sample.TorqueNm) && sample.PowerKw > 3 && sample.TorqueNm > 3;

    private static bool IsEngagedPositivePower(TelemetrySample sample) =>
        sample.Clutch < 0.15 && IsPositivePower(sample);

    public void Reset()
    {
        Verdict = TransmissionClutchVerdict.Unknown;
        _clutchedLatencyMs = 0;
        _clutchedRecoveryMs = 0;
        _clutchedSamples = 0;
        _lastProbeRecoveryMs = 0;
        _probeAttempts = 0;
        _needsBaselineFallback = false;
        _unconfirmedClutchless = false;
        _sawPositivePower = false;
        _lastForwardGear = 0;
        _lastForwardLoadOk = false;
        _recentClutchUntil = 0;
        _active = null;
        _settling = null;
        _passive = null;
    }

    private sealed class ActiveShift(ShiftDirection direction, int from, int to, bool usedClutch, bool isProbe,
        bool expectsPower, long requestedAt)
    {
        public ShiftDirection Direction { get; } = direction;
        public int From { get; } = from;
        public int To { get; } = to;
        public bool UsedClutch { get; } = usedClutch;
        public bool IsProbe { get; } = isProbe;
        public bool ExpectsPower { get; } = expectsPower;
        public long RequestedAt { get; } = requestedAt;
        public long BetweenGearsMs { get; set; }
        public bool SawGameClutch { get; set; }
        public bool ValidLoad { get; set; } = true;
    }

    private sealed class PassiveShift(int from, int to, long startedAt, long BetweenGearsMs, bool SawClutch, bool ExpectsPower)
    {
        public int From { get; } = from;
        public int To { get; } = to;
        public long StartedAt { get; } = startedAt;
        public long BetweenGearsMs { get; set; } = BetweenGearsMs;
        public bool SawClutch { get; set; } = SawClutch;
        public bool ExpectsPower { get; } = ExpectsPower;
        public bool ValidLoad { get; set; } = true;
        public bool Confirmed { get; set; }
        public long ConfirmMs { get; set; }
        public long ConfirmedAt { get; set; }
    }

    private sealed record SettlingShift(ActiveShift Shift, long ConfirmMs, long ConfirmedAt);
}
