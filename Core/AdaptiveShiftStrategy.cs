using System;

namespace FH6Telemetry;

internal enum ShiftReason
{
    None, LazyForzaUpshift, LazyForzaDownshift, HigherGearPull, HighRpmOutputLoss, Limiter, LightLoad,
    Kickdown, Braking, EngineBraking, LowSpeed, Lugging, LearningLimit,
    VirtualTcuComfort, VirtualTcuPowerBand, VirtualTcuDrift, VirtualTcuOffRoad, VirtualTcuTraction,
    Fh6FuelCut, Fh6PowerCrossover, Fh6PredictedLimiter, Fh6LoadPlateau, Fh6Coast, Fh6CornerExit,
    SpeedMatchedPower, SpeedMatchedCoast, SpeedMatchedBraking, CoastDownshift, SpeedMatchedLearning, Mode5PowerBand, Mode5Limiter,
    StrictSpeedMatch,
}

internal readonly record struct ShiftDecision(int Target, ShiftReason Reason, bool NeedsRatio = false, double LandingRpm = 0);

internal sealed class AdaptiveShiftStrategy
{
    internal const int BinCount = 40;
    internal const double LowRpmFraction = 0.34;
    private const double FallbackUpshiftFraction = 0.95;
    private const double PredictiveLoad = 0.75;
    private const double HighLoad = 0.84;
    private readonly OutputBin[] _curve = new OutputBin[BinCount];
    private long? _lastLoadedAt;
    private long _peakAt;
    private double _peakPower;
    private double _peakTorque;
    private double _previousLoadedPower;
    private double _previousThrottle;
    private double _previousRpm;
    private uint _previousRpmTimestamp;
    private double _rpmRate;
    private double _weakOutputMs;
    private double _nearLimitMs;
    private int _pendingBinIndex = -1;
    private double _pendingBinPower;

    public int LearnedBins { get; private set; }
    public int CurveRevision { get; private set; }
    public bool HasOutputLoss => _weakOutputMs >= 48;

    public OutputBin[] CopyCurve() => (OutputBin[])_curve.Clone();

    public (double Torque, double Power) PeakFractions(OutputBin[]? savedCurve = null)
    {
        var lowest = 1.0;
        var highest = 0.0;
        OutputBin torquePeak = default, powerPeak = default;
        var bestTorque = 0.0;
        var count = 0;
        for (var index = 0; index < _curve.Length; index++)
        {
            var bin = _curve[index].Power > 0 ? _curve[index] : savedCurve?[index] ?? default;
            if (bin.Power <= 0) continue;
            count++;
            lowest = Math.Min(lowest, bin.Fraction);
            highest = Math.Max(highest, bin.Fraction);
            if (bin.Power > powerPeak.Power) powerPeak = bin;
            var torque = bin.Power / bin.Fraction;
            if (torque <= bestTorque) continue;
            bestTorque = torque;
            torquePeak = bin;
        }
        return count >= 8 && highest >= 0.80 && highest - lowest >= 0.23
            ? (torquePeak.Fraction, powerPeak.Fraction) : default;
    }

    public void Observe(TelemetrySample sample, long now, bool learnCurve, bool measureOutput, double rpmLimit)
    {
        if (!measureOutput || sample.Throttle < PredictiveLoad)
        {
            ResetTransient();
            return;
        }

        if (_previousRpm > 0 && sample.Throttle < _previousThrottle - 0.06)
            ResetTransient();
        UpdateRpmRate(sample);
        _previousThrottle = sample.Throttle;
        if (sample.Throttle < HighLoad)
        {
            ResetLoadedOutput();
            return;
        }

        var elapsed = _lastLoadedAt is long last ? Math.Clamp(now - last, 0, 45) : 0;
        _lastLoadedAt = now;

        var fraction = sample.Rpm / sample.EngineMaxRpm;
        var limitFraction = sample.Rpm / rpmLimit;
        var power = UsablePower(sample);
        var torque = sample.TorqueNm;
        if (limitFraction < 0.78 || now - _peakAt > 1200)
        {
            _peakPower = _peakTorque = 0;
            _weakOutputMs = 0;
        }

        var weak = limitFraction >= 0.85 && _peakPower > 0 && _peakTorque > 0 &&
            double.IsFinite(power) && double.IsFinite(torque) &&
            power <= _peakPower * 0.80 && torque <= _peakTorque * 0.80;
        _weakOutputMs = weak ? Math.Min(260, _weakOutputMs + elapsed) : Math.Max(0, _weakOutputMs - 0.42 * elapsed);
        if (limitFraction >= 0.78 && power >= Math.Max(1, _peakPower * 0.97) && torque > 0 && double.IsFinite(torque) &&
            IsRepeatablePower(_peakPower, power, _previousLoadedPower))
        {
            _peakPower = Math.Max(_peakPower, power);
            _peakTorque = Math.Max(_peakTorque, torque);
            _peakAt = now;
        }
        _previousLoadedPower = power;
        _nearLimitMs = limitFraction >= 0.997 ? _nearLimitMs + elapsed : 0;

        if (learnCurve && sample.Throttle >= 0.92 && !weak && power > 1 && fraction is >= 0.22 and <= 1.005)
            LearnOutput(fraction, power);
        else
            _pendingBinIndex = -1;
    }

    private void UpdateRpmRate(TelemetrySample sample)
    {
        var deltaMs = unchecked(sample.TimestampMs - _previousRpmTimestamp);
        if (_previousRpm > 0 && deltaMs is >= 1 and <= 45)
        {
            var rate = (sample.Rpm - _previousRpm) * 1000 / deltaMs;
            _rpmRate += (rate - _rpmRate) * 0.25;
        }
        else
        {
            _rpmRate = 0;
        }
        _previousRpm = sample.Rpm;
        _previousRpmTimestamp = sample.TimestampMs;
    }

    private void LearnOutput(double fraction, double power)
    {
        var index = Math.Min(BinCount - 1, (int)(fraction * BinCount));
        ref var bin = ref _curve[index];
        var previous = _pendingBinIndex == index ? _pendingBinPower : 0;
        _pendingBinIndex = -1;
        if (power <= bin.Power) return;
        // Keep a full-load envelope, but require two similar samples for a substantial jump.
        if (!IsRepeatablePower(bin.Power, power, previous))
        {
            _pendingBinIndex = index;
            _pendingBinPower = power;
            return;
        }
        if (bin.Power == 0) LearnedBins++;
        bin = new OutputBin(fraction, power);
        CurveRevision++;
    }

    private static bool IsRepeatablePower(double reference, double current, double previous) =>
        reference == 0 || current <= reference * 1.02 || (previous > 0 && Math.Abs(current / previous - 1) <= 0.02);

    public bool IsAtLimiter(TelemetrySample sample, double limit, bool useLazyForza) =>
        sample.Rpm >= limit * (useLazyForza ? 0.997 : 1.003) || _nearLimitMs >= 120;

    public ShiftDecision DecideUpshift(TelemetrySample sample, double throttle, double[] ratios,
        int gear, double lookaheadMs, bool allowPredictive, DriverIntentSnapshot intent = default)
    {
        var fraction = sample.Rpm / sample.EngineMaxRpm;
        var lead = allowPredictive ? Math.Min(160, Math.Max(0, _rpmRate) * lookaheadMs / 1000) : 0;
        var predictedRpm = Math.Min(sample.Rpm + lead, sample.EngineMaxRpm * 1.02);
        var predictedFraction = predictedRpm / sample.EngineMaxRpm;

        var currentRatio = ratios[gear];
        var nextRatio = ratios[gear + 1];
        if (nextRatio == 0 && gear >= 2 && currentRatio > 0 && ratios[gear - 1] > currentRatio)
            nextRatio = currentRatio * currentRatio / ratios[gear - 1];
        var nextRpm = sample.SpeedKmh * nextRatio;
        if (nextRpm > 0 && (nextRpm < Math.Max(sample.EngineIdleRpm * 1.35, sample.EngineMaxRpm * 0.27) ||
            nextRpm >= sample.Rpm * 0.98))
            return default;

        if (throttle >= PredictiveLoad)
        {
            // At equal road speed, power divided by speed is proportional to wheel force.
            var predictedNextRpm = currentRatio > 0 ? predictedRpm * nextRatio / currentRatio : 0;
            var current = Estimate(lead > 0 ? predictedFraction : fraction);
            var next = Estimate((lead > 0 ? predictedNextRpm : nextRpm) / sample.EngineMaxRpm);
            if (current > 0 && next > 0)
            {
                var live = UsablePower(sample);
                if (lead == 0 && double.IsFinite(live) && sample.Throttle >= 0.92) current = Math.Max(current * 0.97, live);
                return allowPredictive && next > current * 1.045 ? new(gear + 1, ShiftReason.HigherGearPull) : default;
            }

            // Use the RPM limit when engine output is unavailable.
            if (UsablePower(sample) <= 0 && _peakPower == 0 && next == 0 &&
                fraction >= FallbackUpshiftFraction)
                return new(gear + 1, ShiftReason.LearningLimit);
            return default;
        }

        if (!allowPredictive) return default;
        if (intent.FastThrottleLift || intent.CornerLoad >= 0.45 || intent.RearSliding) return default;
        if (intent.IsTurboSpooling && nextRpm > 0 && nextRpm < sample.EngineMaxRpm * 0.34) return default;
        var gradeLift = intent.RoadGrade > 0.04 ? Math.Clamp(intent.RoadGrade * 0.90, 0, 0.12) : 0;
        var aggressionLift = intent.Aggression >= 0.35 ? (intent.Aggression - 0.35) * 0.16 : 0;
        var lightLoadRpm = Math.Min(0.90, 0.57 + 0.28 * throttle + gradeLift + aggressionLift);
        if (LearnedBins > 0 && UsablePower(sample) <= 0) return default;
        return fraction >= lightLoadRpm ? new(gear + 1, ShiftReason.LightLoad) : default;
    }

    public ShiftDecision DecideDownshift(TelemetrySample sample, ShiftPedals pedals, double lowerRatio,
        int gear, double speedAtEngagement, double rpmLimit, DriverIntentSnapshot intent = default)
    {
        var fraction = sample.Rpm / sample.EngineMaxRpm;
        var floor = LowRpmFraction;
        if (sample.SpeedKmh <= 5 && pedals.Throttle < 0.20 && fraction <= floor)
            return new(gear - 1, ShiftReason.LowSpeed);

        var kickThreshold = intent.CornerExitWindow ? 0.56
            : intent.ThrottleStomp ? 0.60
            : intent.RoadGrade > 0.05 ? 0.58 : 0.74;
        var kickdown = pedals.Throttle >= kickThreshold && pedals.WantsPower;
        var uphillLug = intent.RoadGrade > 0.05 && pedals.Throttle >= 0.35 && fraction < floor + 0.08;
        var lugging = pedals.Brake < 0.08 &&
            (uphillLug || fraction < Math.Max(floor - 0.04, sample.EngineIdleRpm * 1.55 / sample.EngineMaxRpm));
        if (!kickdown && !lugging) return default;

        if (lowerRatio == 0) return new(0, ShiftReason.None, NeedsRatio: true);
        var newRpm = sample.SpeedKmh * lowerRatio;
        var landingRpm = speedAtEngagement * lowerRatio;
        if (newRpm <= sample.Rpm * 1.04 || landingRpm >= rpmLimit * 0.89)
            return default;

        var landing = newRpm / sample.EngineMaxRpm;
        if (kickdown)
        {
            var current = Estimate(fraction);
            var lower = Estimate(landing);
            var live = UsablePower(sample);
            if (sample.Throttle >= 0.92 && double.IsFinite(live)) current = Math.Max(current, live);
            var minGain = intent.CornerExitWindow || intent.ThrottleStomp ? 1.08 : 1.13;
            if (current > 0 && lower > 0)
                return lower >= current * minGain ? new(gear - 1, ShiftReason.Kickdown, LandingRpm: landingRpm) : default;

            if (fraction <= floor + 0.16 && landing >= fraction + 0.09)
                return new(gear - 1, ShiftReason.Kickdown, LandingRpm: landingRpm);
        }
        return lugging && landing >= fraction + 0.03 ? new(gear - 1, ShiftReason.Lugging, LandingRpm: landingRpm) : default;
    }

    private static double UsablePower(TelemetrySample sample)
    {
        var measured = sample.PowerKw;
        var fromTorque = sample.TorqueNm * sample.Rpm * (2 * Math.PI / 60_000);
        var hasPower = double.IsFinite(measured) && measured > 0;
        var hasTorque = double.IsFinite(fromTorque) && fromTorque > 0;
        if (hasPower && hasTorque && Math.Abs(measured / fromTorque - 1) <= 0.24)
            return (measured + fromTorque) / 2;
        if (hasPower) return measured;
        if (hasTorque) return fromTorque;
        return double.IsFinite(measured) || double.IsFinite(fromTorque) ? 0 : double.NaN;
    }

    internal double Estimate(double fraction, OutputBin[]? savedCurve = null)
    {
        const double maximumDistance = 0.12;
        const double nearSample = 0.014;
        OutputBin below = default, above = default;
        for (var index = 0; index < _curve.Length; index++)
        {
            var bin = _curve[index].Power > 0 ? _curve[index] : savedCurve?[index] ?? default;
            if (bin.Power == 0) continue;
            if (bin.Fraction <= fraction) below = bin;
            if (bin.Fraction >= fraction) { above = bin; break; }
        }
        if (below.Power > 0 && fraction - below.Fraction <= nearSample) return below.Power;
        if (above.Power > 0 && above.Fraction - fraction <= nearSample) return above.Power;
        if (below.Power == 0 || above.Power == 0 || above.Fraction - below.Fraction > maximumDistance) return 0;
        return below.Power + (above.Power - below.Power) * (fraction - below.Fraction) / (above.Fraction - below.Fraction);
    }

    public void ResetTransient()
    {
        _previousRpm = _rpmRate = 0;
        ResetLoadedOutput();
    }

    private void ResetLoadedOutput()
    {
        _lastLoadedAt = null;
        _peakPower = _peakTorque = _weakOutputMs = _nearLimitMs = _previousLoadedPower = 0;
        _pendingBinIndex = -1;
    }

    public void Clear()
    {
        Array.Clear(_curve);
        LearnedBins = 0;
        CurveRevision++;
        ResetTransient();
    }

    internal readonly record struct OutputBin(double Fraction, double Power);
}
