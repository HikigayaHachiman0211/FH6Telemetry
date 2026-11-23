using System;
using System.Collections.Generic;

namespace FH6Telemetry;

internal sealed class BrakeShiftStrategy
{
    public const double MinimumBrake = 0.10;
    private const int TorqueBins = 40;
    private const int MinimumTorqueSamples = 4;
    private readonly BrakeTorqueBin[] _curve = new BrakeTorqueBin[TorqueBins];
    private TelemetrySample? _previous;
    private double _speedRateKmhPerSecond;

    public static bool CanShift(TelemetrySample sample) =>
        Math.Abs(sample.AccelerationX) < 1.65 * TelemetryDerived.Gravity &&
        sample.SmashableMass <= 0 && sample.SmashableVelDiff <= 0.5;

    public void Observe(TelemetrySample sample, bool canLearnTorque)
    {
        var interval = _previous is { } previous ? unchecked(sample.TimestampMs - previous.TimestampMs) : 0;
        if (_previous is { } last && interval is > 0 and <= 100)
        {
            var speedRate = (sample.SpeedKmh - last.SpeedKmh) * 1000 / interval;
            var maxRate = 1.65 * TelemetryDerived.Gravity * 3.6;
            var weight = interval / (interval + 60.0);
            _speedRateKmhPerSecond += (Math.Clamp(speedRate, -maxRate, maxRate) - _speedRateKmhPerSecond) * weight;
        }
        else _speedRateKmhPerSecond = 0;
        _previous = sample;

        if (!canLearnTorque || sample.SpeedKmh < 14 || sample.Throttle > 0.04 || sample.Clutch >= 0.08 ||
            sample.HandBrake >= 0.08 || !CanShift(sample) || sample.Rpm < sample.EngineIdleRpm * 1.2 ||
            sample.PowerKw >= 0 || !double.IsFinite(sample.TorqueNm) || sample.TorqueNm >= -1)
            return;

        var index = (int)Math.Round(Math.Clamp(sample.Rpm / sample.EngineMaxRpm, 0, 1) * (TorqueBins - 1));
        ref var bin = ref _curve[index];
        bin.Count = Math.Min(bin.Count + 1, 16);
        bin.Torque += (-sample.TorqueNm - bin.Torque) / bin.Count;
    }

    public ShiftDecision Decide(TelemetrySample sample, double brake, ReadOnlySpan<double> ratios,
        IReadOnlyList<LazyForzaShiftTarget> targets, double rpmLimit, double shiftLatencyMs, double lowSpeedRpm,
        (double Torque, double Power) peaks = default, DriverIntentSnapshot intent = default)
    {
        if (sample.Gear <= 1) return default;
        var nextGear = sample.Gear - 1;
        var hardLimit = rpmLimit * 0.990;

        // At crawling speed bring the transmission progressively back to first.
        if (sample.SpeedKmh <= 5 && sample.Rpm <= lowSpeedRpm)
        {
            var landing = sample.SpeedKmh * ratios[nextGear];
            return landing < hardLimit ? new(nextGear, ShiftReason.LowSpeed, LandingRpm: landing) : default;
        }
        if (ratios[nextGear] <= 0) return new(0, ShiftReason.None, NeedsRatio: true);

        var longitudinalDeceleration = Math.Max(0, -_speedRateKmhPerSecond);
        var engagementSpeed = SpeedAtEngagement(sample.SpeedKmh, shiftLatencyMs, braking: true);
        var lateralLoad = Math.Min(1, Math.Abs(sample.AccelerationX) / (1.25 * TelemetryDerived.Gravity));
        var peakCeiling = peaks.Power > 0 ? Math.Min(0.980, Math.Max(0.92, peaks.Power + 0.12)) : 0.980;
        var lateralPenalty = intent.Kind == DriverIntentKind.TrailBraking ? 0.10 : 0.08;
        var brakeCeiling = rpmLimit * (peakCeiling - lateralPenalty * lateralLoad);
        var brakeDemand = Math.Clamp(Math.Max((brake - MinimumBrake) / 0.55,
            longitudinalDeceleration / (0.85 * TelemetryDerived.Gravity * 3.6)), 0, 1);
        if (intent.RoadGrade < -0.04)
            brakeDemand = Math.Clamp(brakeDemand + Math.Min(0.25, -intent.RoadGrade * 2.5), 0, 1);
        if (intent.BrakeSpike && lateralLoad < 0.45)
            brakeDemand = Math.Max(brakeDemand, 0.80);

        var currentSlope = ratios[sample.Gear] > 0 ? ratios[sample.Gear] : sample.Rpm / Math.Max(1, sample.SpeedKmh);
        var currentRpm = engagementSpeed * currentSlope;
        var currentForce = TorqueAt(currentRpm, sample.EngineMaxRpm) * currentSlope;
        var minimumRpm = Math.Max(sample.EngineIdleRpm * 1.2, currentRpm * 1.02);
        var fallbackReference = peaks.Power > 0
            ? sample.EngineMaxRpm * Math.Clamp((peaks.Torque + peaks.Power) * 0.5, 0.62, 0.80)
            : rpmLimit * 0.78;
        var previousSlope = currentSlope;
        double nextRpm = 0, nextForce = 0, strongestLowerForce = 0;

        for (var gear = nextGear; gear >= 1; gear--)
        {
            var slope = ratios[gear];
            var rpm = engagementSpeed * slope;
            var referenceRpm = Math.Min(brakeCeiling, ReferenceLandingRpm(gear, targets, sample.EngineMaxRpm) ?? fallbackReference);
            var desiredRpm = referenceRpm + (brakeCeiling - referenceRpm) * brakeDemand;
            // Engine RPM can drop when the driven wheels slide. For over-rev protection, use the larger estimate at the current speed.
            var rpmWithoutDeceleration = Math.Max(sample.SpeedKmh * slope, sample.Rpm * slope / currentSlope);
            if (slope <= previousSlope || rpmWithoutDeceleration >= hardLimit || rpm > desiredRpm) break;
            previousSlope = slope;
            var force = TorqueAt(rpm, sample.EngineMaxRpm) * slope;
            if (gear == nextGear)
            {
                nextRpm = rpm;
                nextForce = force;
            }
            if (rpm >= minimumRpm) strongestLowerForce = Math.Max(strongestLowerForce, force);
        }

        if (nextRpm <= 0) return default;
        if (currentForce > 0 && strongestLowerForce > currentForce * 1.04)
            return new(nextGear, ShiftReason.EngineBraking, LandingRpm: nextRpm);
        var belowUsableBand = currentRpm < Math.Max(sample.EngineIdleRpm * 1.8,
            rpmLimit * (peaks.Torque > 0 ? Math.Clamp(peaks.Torque * 0.72, 0.38, 0.50) : 0.42));
        return nextRpm >= minimumRpm && (currentForce <= 0 || nextForce <= 0 || belowUsableBand)
            ? new(nextGear, ShiftReason.Braking, LandingRpm: nextRpm)
            : default;
    }

    private static double? ReferenceLandingRpm(int lowerGear, IReadOnlyList<LazyForzaShiftTarget> targets, double engineMaxRpm)
    {
        foreach (var pair in targets)
            if (pair.FromGear == lowerGear && pair.TargetRpm <= engineMaxRpm * 1.05)
                return pair.TargetRpm * 0.90;
        return null;
    }

    private double TorqueAt(double rpm, double engineMaxRpm)
    {
        var point = rpm / engineMaxRpm * (TorqueBins - 1);
        var lower = Math.Min(TorqueBins - 1, (int)Math.Floor(point));
        while (lower >= 0 && point - lower <= 3 && _curve[lower].Count < MinimumTorqueSamples) lower--;
        var upper = Math.Max(0, (int)Math.Ceiling(point));
        while (upper < TorqueBins && upper - point <= 3 && _curve[upper].Count < MinimumTorqueSamples) upper++;
        if (lower >= 0 && point - lower <= 3 && upper < TorqueBins && upper - point <= 3)
            return lower == upper ? _curve[lower].Torque : _curve[lower].Torque +
                (_curve[upper].Torque - _curve[lower].Torque) * (point - lower) / (upper - lower);
        if (lower >= 0 && point - lower <= 1.5) return _curve[lower].Torque;
        return upper < TorqueBins && upper - point <= 1.5 ? _curve[upper].Torque : 0;
    }

    public void BreakSequence()
    {
        _previous = null;
        _speedRateKmhPerSecond = 0;
    }

    public double SpeedAtEngagement(double speedKmh, double delayMs, bool braking)
    {
        var speedRate = braking ? Math.Min(0, _speedRateKmhPerSecond) : Math.Max(0, _speedRateKmhPerSecond);
        return Math.Max(0, speedKmh + speedRate * Math.Clamp(delayMs / 1000.0, 0, 0.35));
    }

    public void Clear()
    {
        BreakSequence();
        Array.Clear(_curve);
    }

    private struct BrakeTorqueBin
    {
        public int Count;
        public double Torque;
    }
}
