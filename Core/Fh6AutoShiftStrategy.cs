using System;
using System.Collections.Generic;

namespace FH6Telemetry;

// Shift behavior reference: ufsemmc/fh6-auto-shift, AdaptiveLimiterLearner and ShiftController.
internal sealed class Fh6AutoShiftStrategy
{
    private const int BinRpm = 100;
    private const int MinimumBinSamples = 4;
    private const int MaximumPullFrames = 192;
    private readonly PowerBin[,] _power = new PowerBin[AutomaticShiftSettings.MaximumForwardGears + 1, 1016];
    private readonly double[] _cutByGear = new double[AutomaticShiftSettings.MaximumForwardGears + 1];
    private readonly Queue<PoweredFrame> _pull = new(MaximumPullFrames);
    private double _carCut;
    private CutCandidate _cut;
    private int _pullGear;
    private int _softCutFrames;
    private long? _brakingAt;
    private int _kickdownHoldGear;
    private double _peakSpeedAfterUpshift;

    public int LearnedBins { get; private set; }

    public void Observe(TelemetrySample sample, ShiftPedals pedals, ShiftDynamics motion, long now, bool settled, bool pendingShift, double slip,
        double repeatedCutRpm)
    {
        if (!pendingShift && pedals.Braking && sample.Clutch < 0.10) _brakingAt = now;
        if (_kickdownHoldGear != 0)
        {
            if (sample.Gear != _kickdownHoldGear || pedals.Braking || pedals.Throttle < 0.72 ||
                sample.SpeedKmh < _peakSpeedAfterUpshift * 0.90)
                _kickdownHoldGear = 0;
            else
                _peakSpeedAfterUpshift = Math.Max(_peakSpeedAfterUpshift, sample.SpeedKmh);
        }
        if (pendingShift) return;
        if (!settled || !motion.HasContact(now) || sample.SpeedKmh < 10 || sample.Throttle < 0.95 || sample.Brake > 0.05 ||
            sample.Clutch > 0.05 || sample.HandBrake > 0.05 || !double.IsFinite(sample.PowerKw) ||
            !double.IsFinite(sample.TorqueNm) || !double.IsFinite(slip))
        {
            ResetPull();
            return;
        }
        if (_pullGear != sample.Gear || (_cut.Peak > 0 && now - _cut.At > 700))
        {
            ResetPull();
            _pullGear = sample.Gear;
        }

        var powered = sample.PowerKw > 5 && sample.TorqueNm > 5;
        var electric = sample.NumCylinders == 0 && sample.EngineIdleRpm == 0;
        if (_cut.Peak > 0)
        {
            if (!powered)
            {
                _cut = _cut with { Trough = Math.Min(_cut.Trough, sample.Rpm) };
                return;
            }
            if (_cut.Peak - _cut.Trough >= (electric ? 6 : 35)) RememberCut(_cut.Gear, _cut.Peak);
            ResetPull();
            _pullGear = sample.Gear;
        }
        if (powered)
        {
            while (_pull.TryPeek(out var frame) && (now - frame.At > 3000 || _pull.Count >= MaximumPullFrames))
                _pull.Dequeue();
            _pull.Enqueue(new(now, sample.Rpm, sample.PowerKw, sample.TorqueNm));
            if (slip <= 2.0 && sample.Rpm >= Math.Max(100, 1.20 * sample.EngineIdleRpm) && sample.Rpm <= 1.015 * sample.EngineMaxRpm)
                LearnPower(sample);
        }
        DetectCut(sample, now, electric);
        if (_cut.Peak == 0 && !powered && repeatedCutRpm > 0)
            _cut = new(sample.Gear, Math.Min(repeatedCutRpm, sample.EngineMaxRpm), sample.Rpm, now);
    }

    private void DetectCut(TelemetrySample sample, long now, bool electric)
    {
        var floor = double.PositiveInfinity;
        double peak = 0, power = 0, torque = 0;
        var recentCount = 0;
        foreach (var frame in _pull)
        {
            if (now - frame.At > 3000) continue;
            floor = Math.Min(floor, frame.Rpm);
            if (now - frame.At > 250) continue;
            recentCount++;
            peak = Math.Max(peak, frame.Rpm);
            power = Math.Max(power, frame.Power);
            torque = Math.Max(torque, frame.Torque);
        }
        if (recentCount < 2 || peak < sample.EngineMaxRpm * (electric ? 0.85 : 0.70) || peak - floor < 20 ||
            Math.Abs(sample.Rpm - peak) > sample.EngineMaxRpm * 0.08)
        {
            _softCutFrames = 0;
            return;
        }
        var droop = peak - sample.Rpm;
        var reversal = sample.PowerKw < -5 && sample.TorqueNm < -5;
        var collapse = droop >= (electric ? 6 : 35) && sample.PowerKw <= power * 0.08 && sample.TorqueNm <= torque * 0.08;
        var soft = droop >= (electric ? 10 : 60) && (sample.PowerKw <= power * 0.55 || sample.TorqueNm <= torque * 0.55);
        _softCutFrames = soft ? _softCutFrames + 1 : 0;
        if (reversal || collapse || _softCutFrames >= 2)
            _cut = new(sample.Gear, Math.Min(peak, sample.EngineMaxRpm), sample.Rpm, now);
    }

    public ShiftDecision Decide(TelemetrySample sample, ShiftPedals pedals, ReadOnlySpan<double> ratios, ShiftDynamics motion,
        bool canUpshift, double upDelay, double downDelay, double lowerWheelRpm, long now, DriverIntentSnapshot intent = default)
    {
        if (canUpshift && pedals.Throttle >= 0.35 && pedals.Brake <= 0.10)
        {
            var upshift = DecideUpshift(sample, pedals, ratios, motion, upDelay, now, intent);
            if (upshift.Target != 0) return upshift;
        }
        return DecideDownshift(sample, pedals, ratios, motion, downDelay, lowerWheelRpm, now, intent);
    }

    private ShiftDecision DecideUpshift(TelemetrySample sample, ShiftPedals pedals, ReadOnlySpan<double> ratios, ShiftDynamics motion,
        double delay, long now, DriverIntentSnapshot intent)
    {
        var throttle = pedals.Throttle;
        var next = sample.Gear + 1;
        var currentRatio = ratios[sample.Gear];
        var step = currentRatio > 0 ? ratios[next] / currentRatio : 0;
        if (step >= 0.995 || (currentRatio > 0 && sample.SpeedKmh * currentRatio < sample.Rpm * 0.75))
            return default;
        if (_cut.Gear == sample.Gear && _cut.Peak > 0 && now - _cut.At <= 140)
            return new(next, ShiftReason.Fh6FuelCut);

        var nominal = sample.EngineMaxRpm;
        var provenCut = LearnedCutFor(sample.Gear);
        var ceiling = provenCut > 0 ? Math.Min(nominal, provenCut) : nominal * 0.98;
        var predicted = sample.Rpm + Math.Max(0, motion.RpmRate) * Math.Min(400, delay) / 1000;
        var suppressEarlyCrossover = intent.FastThrottleLift || intent.RearSliding ||
            (!pedals.WantsPower && intent.CornerLoad >= 0.45);
        var crossover = !suppressEarlyCrossover && step is >= 0.42 and < 0.995 ? Crossover(sample, step, ceiling) : 0;
        if (crossover > 0 && sample.Rpm >= crossover * 0.70 && predicted >= crossover - 100)
            return new(next, ShiftReason.Fh6PowerCrossover);
        if (provenCut > 0 && sample.Rpm >= ceiling * 0.70 && predicted >= ceiling - 100)
            return new(next, ShiftReason.Fh6PredictedLimiter);

        if (throttle < 0.95 || Math.Abs(motion.SpeedRate) > 3) return default;
        // An unreachable limiter must not hold a loaded gear indefinitely on a speed plateau.
        if (!intent.RearSliding && motion.PlateauMs >= 2000 && sample.Rpm >= ceiling * 0.80)
        {
            var here = PowerAt(sample.Gear, sample.Rpm);
            var there = step > 0 ? PowerAt(next, sample.Rpm * step) : 0;
            if ((here > 0 && there > here * 1.002) || (step == 0 && sample.Rpm >= nominal * 0.90))
                return new(next, ShiftReason.Fh6LoadPlateau);
        }
        return motion.PlateauMs >= 400 && sample.Rpm >= nominal * 0.997
            ? new(next, ShiftReason.Fh6PredictedLimiter) : default;
    }

    private ShiftDecision DecideDownshift(TelemetrySample sample, ShiftPedals pedals, ReadOnlySpan<double> ratios, ShiftDynamics motion,
        double delay, double lowerWheelRpm, long now, DriverIntentSnapshot intent)
    {
        var lower = sample.Gear - 1;
        if (lower < 1) return default;
        var braking = pedals.Braking;
        var lowerLimit = LandingLimit(lower, sample.EngineMaxRpm);
        if (lowerWheelRpm * 0.75 >= lowerLimit) return default;
        if (sample.SpeedKmh <= 5 && sample.Rpm < lowerLimit * 0.5 && (braking || pedals.Throttle <= 0.05) &&
            sample.SpeedKmh * ratios[lower] < lowerLimit)
            return new(lower, ShiftReason.LowSpeed, LandingRpm: Math.Max(sample.Rpm, sample.SpeedKmh * ratios[lower]));
        if (sample.SpeedKmh < 10 || (!braking && pedals.Brake > 0.05 && !pedals.WantsPower)) return default;

        var exiting = pedals.Throttle >= 0.25 &&
            ((_brakingAt is long brake && now - brake <= 1500) || intent.CornerExitWindow) &&
            sample.Rpm < 0.67 * (_carCut > 0 ? _carCut : sample.EngineMaxRpm);
        var kickThreshold = intent.ThrottleStomp ? 0.58 : intent.RoadGrade > 0.05 ? 0.52 : 0.72;
        var reason = braking ? ShiftReason.Braking : pedals.Throttle < 0.25 ? ShiftReason.Fh6Coast
            : exiting ? ShiftReason.Fh6CornerExit
            : pedals.Throttle >= kickThreshold && pedals.WantsPower && _kickdownHoldGear != sample.Gear ? ShiftReason.Kickdown : ShiftReason.None;
        if (reason == ShiftReason.None) return default;
        var ratio = ratios[lower];
        var currentRatio = ratios[sample.Gear];
        if (ratio <= 0) return new(0, reason, NeedsRatio: true);
        if (currentRatio > 0 && ratio <= currentRatio * 1.02) return default;

        var atCurrentSpeed = sample.SpeedKmh * ratio;
        var atEngagement = motion.EngagementSpeed(sample.SpeedKmh, delay) * ratio;
        var safetyMargin = braking && intent.CornerLoad >= 0.60 ? 350 : 200;
        var effectiveCeiling = reason == ShiftReason.Fh6Coast && intent.RoadGrade >= -0.045 && intent.Aggression < 0.35
            ? Math.Min(lowerLimit - 200, lowerLimit * 0.80)
            : lowerLimit - safetyMargin;
        if (atCurrentSpeed >= lowerLimit || atEngagement > effectiveCeiling || atEngagement < sample.Rpm * 1.02)
            return default;
        return new(lower, reason, LandingRpm: atEngagement);
    }

    private double LandingLimit(int gear, double nominal)
    {
        if (_carCut <= 0) return nominal * 0.90;
        var limit = Math.Min(_carCut, nominal);
        return _cutByGear[gear] > 0 ? Math.Min(limit, _cutByGear[gear]) : limit;
    }

    public double LearnedCutFor(int gear) => _cutByGear[gear] > 0 ? _cutByGear[gear] : _carCut;

    private double Crossover(TelemetrySample sample, double step, double ceiling)
    {
        var floor = Math.Max(ceiling * 0.70, sample.EngineIdleRpm * 2);
        var result = 0.0;
        for (var rpm = Math.Floor(ceiling / BinRpm) * BinRpm; rpm >= floor; rpm -= BinRpm)
        {
            var here = PowerAt(sample.Gear, rpm);
            var there = PowerAt(sample.Gear + 1, rpm * step);
            if (here <= 0 || there <= 0)
            {
                if (result == 0) continue;
                break;
            }
            if (there < 1.005 * here) break;
            result = rpm;
        }
        return result;
    }

    private void LearnPower(TelemetrySample sample)
    {
        var index = (int)Math.Round(sample.Rpm / BinRpm);
        ref var bin = ref _power[sample.Gear, index];
        bin.Count = Math.Min(24, bin.Count + 1);
        if (bin.Count == MinimumBinSamples) LearnedBins++;
        bin.Power += (sample.PowerKw - bin.Power) / bin.Count;
    }

    private double PowerAt(int gear, double rpm)
    {
        var point = rpm / BinRpm;
        var low = (int)Math.Floor(point);
        var high = (int)Math.Ceiling(point);
        var count = _power.GetLength(1);
        while (low >= 0 && low < count && point - low <= 2.5 && _power[gear, low].Count < MinimumBinSamples) low--;
        while (high < count && high - point <= 2.5 && _power[gear, high].Count < MinimumBinSamples) high++;
        if (low < 0 || low >= count || high >= count || point - low > 2.5 || high - point > 2.5) return 0;
        var power = _power[gear, low].Power;
        return low == high ? power : power + (_power[gear, high].Power - power) * (point - low) / (high - low);
    }

    public void ConfirmUpshift(int from, TelemetrySample sample, double throttle, ShiftReason reason, long now)
    {
        if (reason == ShiftReason.Fh6FuelCut && _cut.Gear == from && _cut.Peak > 0 && now - _cut.At <= 950)
            RememberCut(from, _cut.Peak);
        ResetPull();
        if (throttle >= 0.72)
        {
            _kickdownHoldGear = sample.Gear;
            _peakSpeedAfterUpshift = sample.SpeedKmh;
        }
    }

    private void RememberCut(int gear, double rpm)
    {
        ref var gearCut = ref _cutByGear[gear];
        gearCut = gearCut > 0 ? gearCut + (rpm - gearCut) * 0.25 : rpm;
        _carCut = _carCut > 0 ? _carCut + (rpm - _carCut) * 0.25 : rpm;
    }

    private void ResetPull()
    {
        _pull.Clear();
        _cut = default;
        _softCutFrames = 0;
    }

    public void BreakSequence()
    {
        ResetPull();
        _brakingAt = null;
        _kickdownHoldGear = 0;
    }

    public void Clear()
    {
        BreakSequence();
        Array.Clear(_power);
        Array.Clear(_cutByGear);
        _carCut = 0;
        LearnedBins = 0;
    }

    private readonly record struct PoweredFrame(long At, double Rpm, double Power, double Torque);
    private readonly record struct CutCandidate(int Gear, double Peak, double Trough, long At);
    private struct PowerBin { public int Count; public double Power; }
}
