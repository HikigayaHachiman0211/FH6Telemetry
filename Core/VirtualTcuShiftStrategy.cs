using System;

namespace FH6Telemetry;

// Shift behavior reference: Forza-Love/fh6-virtual_tcu, virtual_tcu/logic/tcu.py.
internal sealed class VirtualTcuShiftStrategy
{
    private double _sportIndex;
    private bool _sporty;
    private long _strongBrakeMs;
    private long _steadySpeedMs;
    private long _brakeSpikeUntil;
    private long _throttleStompUntil;
    private long? _lastHardBrake;

    public void Observe(TelemetrySample sample, ShiftPedals pedals, ShiftDynamics motion, long now)
    {
        var effort = 0.50 * Ramp(pedals.Throttle, 0.65, 1) + 0.50 * Ramp(pedals.Brake, 0.35, 0.90) +
            0.60 * Ramp(Math.Abs(sample.AccelerationX) / TelemetryDerived.Gravity, 0.4, 1.2) +
            0.30 * Ramp(sample.Rpm / sample.EngineMaxRpm, 0.65, 1);
        var target = Math.Min(1, effort);
        var timeConstant = target > _sportIndex ? 300 : 6000;
        _sportIndex += (target - _sportIndex) * (1 - Math.Exp(-motion.ElapsedMs / (double)timeConstant));
        _sporty = _sportIndex >= (_sporty ? 0.27 : 0.33);
        _strongBrakeMs = pedals.Brake > 0.55 ? Math.Min(200, _strongBrakeMs + motion.ElapsedMs) : 0;
        _steadySpeedMs = Math.Abs(motion.SpeedRate) < 3 ? Math.Min(500, _steadySpeedMs + motion.ElapsedMs) : 0;
        if (motion.BrakeRise > 0.30) _brakeSpikeUntil = now + 180;
        if (motion.ThrottleRise > (_sporty ? 0.40 : 0.50)) _throttleStompUntil = now + 180;
        if (pedals.Brake > 0.50) _lastHardBrake = now;
    }

    public ShiftDecision Decide(TelemetrySample sample, ShiftPedals pedals, VirtualTcuMode mode, ReadOnlySpan<double> ratios,
        (double Torque, double Power) peaks, ShiftDynamics motion, double limit, double slip,
        double downDelay, bool canUpshift, bool fuelCut, long now, DriverIntentSnapshot intent = default)
    {
        var gear = sample.Gear;
        var fraction = sample.Rpm / sample.EngineMaxRpm;
        var projectedSpeed = motion.EngagementSpeed(sample.SpeedKmh, downDelay);
        var next = gear + 1;
        if (gear > 1 && sample.SpeedKmh < 10 && fraction < 0.40)
            return ratios[gear - 1] > 0
                ? Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.LowSpeed)
                : new(gear - 1, ShiftReason.LowSpeed, LandingRpm: sample.Rpm);
        if (sample.SpeedKmh < (mode == VirtualTcuMode.Drift ? 30 : 12)) return default;

        var brake = pedals.Brake;
        var braking = brake > 0.05 && !pedals.WantsPower;
        if (mode == VirtualTcuMode.Drift)
        {
            if (fraction < 0.65)
                return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.VirtualTcuDrift);
            return canUpshift && !braking && (fuelCut || sample.Rpm >= Math.Min(0.92 * sample.EngineMaxRpm, limit))
                ? new(next, ShiftReason.VirtualTcuDrift) : default;
        }

        var comfort = mode == VirtualTcuMode.Comfort;
        var race = mode == VirtualTcuMode.Race;
        var brakeThreshold = comfort ? (_sporty ? 0.315 : 0.35) : race ? 0.21 : 0.35;
        var brakingIntent = now < _brakeSpikeUntil || (comfort ? _strongBrakeMs >= 90 :
            brake > 0.45 || (brake > 0.30 && motion.SpeedRate < -4));
        if (braking)
        {
            if (gear <= 1 || brake < brakeThreshold || !brakingIntent ||
                sample.SpeedKmh <= (comfort ? (_sporty ? 30 : 35) : 25)) return default;
            var target = comfort ? 0 : PowerBandRpm(sample, peaks, 0.60, 0.70);
            if (target > 0 && intent.Kind == DriverIntentKind.TrailBraking) target *= 0.94;
            var brakingSpeed = Math.Min(projectedSpeed, sample.SpeedKmh * (1 - 0.20 * Math.Min(1, brake / 0.80)));
            return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.Braking, target, brakingSpeed);
        }

        var throttle = pedals.Throttle;
        if (gear > 1 && sample.Rpm < (mode == VirtualTcuMode.OffRoad ? 1320 : 1100) && throttle < 0.10 &&
            sample.SpeedKmh < (mode == VirtualTcuMode.OffRoad ? 25 : 20))
            return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.Lugging);

        var uphill = intent.RoadGrade > 0.045;
        var downhill = intent.RoadGrade < -0.045;
        var holdCornerOrLift = mode != VirtualTcuMode.Drift && (intent.FastThrottleLift || intent.CornerLoad >= 0.42);
        var rearSliding = mode != VirtualTcuMode.Drift && intent.RearSliding;

        if (rearSliding && fraction < 0.50 && gear > 1)
            return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.Kickdown);

        if (comfort)
        {
            var kickPedal = sample.DrivetrainType switch { 0 => 0.86, 2 => 0.73, _ => 0.78 };
            if (uphill) kickPedal -= 0.08;
            if (intent.CornerExitWindow) kickPedal -= 0.10;
            var stomp = (now < _throttleStompUntil || intent.ThrottleStomp) && throttle > (_sporty ? 0.70 : 0.80) &&
                fraction < (_sporty ? 0.70 : 0.65) && gear > (_sporty ? 1 : 2) &&
                sample.SpeedKmh > (_sporty ? 30 : 40);
            if (stomp || (throttle >= kickPedal && fraction < (uphill ? 0.56 : 0.50) && gear > 2))
                return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.Kickdown);
        }
        else if (race)
        {
            var exitingCorner = intent.CornerExitWindow || (_lastHardBrake is long hardBrake && now - hardBrake < 2000);
            var minimumThrottle = exitingCorner ? 0.45 : uphill ? 0.56 : 0.68;
            var demandFloor = peaks.Torque > 0 ? Math.Max(0.58, peaks.Torque - 0.05) : 0.60;
            if (gear > 2 && sample.SpeedKmh > 25 && throttle >= minimumThrottle && fraction < demandFloor && slip <= 0.8)
                return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.Kickdown,
                    PowerBandRpm(sample, peaks, 0.45, 0.72), projectedSpeed);
        }
        else if (gear > 1 && throttle >= 0.40 && fraction < 0.55)
        {
            return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.Kickdown, huntingCeiling: 0.82 * sample.EngineMaxRpm);
        }

        var coastingFloor = downhill ? 0.48 : comfort ? 0.28 : race ? 0.30 : 0.32;
        if (gear > 1 && throttle < (downhill ? 0.08 : 0.05) && fraction < coastingFloor &&
            (downhill || !comfort || (!_sporty && sample.SpeedKmh > 50 && Math.Abs(sample.AngularVelocityY) < 0.20)))
            return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.EngineBraking);
        if (gear > 2 && sample.SpeedKmh >= 40 && motion.SpeedRate > 2 && fraction < 0.55 &&
            motion.CoastingMs >= (downhill ? 150 : comfort ? 250 : 800))
            return Downshift(sample, ratios, projectedSpeed, limit, ShiftReason.EngineBraking);

        if (!canUpshift) return default;
        var tractionSlipLimit = intent.SurfaceRoughness >= 0.45 ? 1.55 : 1.2;
        if (slip > tractionSlipLimit && throttle >= 0.80 && sample.SpeedKmh > 15 && sample.Rpm >= limit * 0.89 &&
            (!race || sample.SpeedKmh * ratios[next] >= sample.EngineMaxRpm * 0.60))
            return new(next, ShiftReason.VirtualTcuTraction);
        if (comfort)
        {
            if (rearSliding)
                return fuelCut || sample.Rpm >= Math.Min(0.95 * sample.EngineMaxRpm, limit)
                    ? new(next, ShiftReason.VirtualTcuComfort) : default;
            var steadyCruise = !_sporty && !uphill && !holdCornerOrLift && gear >= 3 && throttle is > 0.05 and < 0.55 &&
                fraction > 0.30 && _steadySpeedMs >= 250 && Math.Abs(sample.AngularVelocityY) < 0.15;
            if (holdCornerOrLift && !fuelCut) return default;
            var light = _sporty ? 0.42 : 0.40;
            var shiftAt = throttle <= 0.50 ? light + (0.58 - light) * throttle * 2 : 0.58 + 0.24 * (throttle - 0.50) * 2;
            if (uphill) shiftAt = Math.Min(0.90, shiftAt + Math.Min(0.14, intent.RoadGrade * 1.2));
            return fuelCut || steadyCruise || (throttle > 0.20 && sample.Rpm >= Math.Min(shiftAt * sample.EngineMaxRpm, limit))
                ? new(next, ShiftReason.VirtualTcuComfort) : default;
        }

        var mid = race ? 0.80 : 0.72;
        var fallback = Math.Min(race ? 0.94 : 0.90, limit / sample.EngineMaxRpm);
        var threshold = peaks.Power > 0
            ? Math.Min(fallback, Math.Max(Math.Max(mid, fallback - 0.06), peaks.Power + (race ? (_sporty ? 0.03 : 0) : 0.07)))
            : fallback;
        if (rearSliding) threshold = Math.Max(threshold, Math.Min(0.95, limit / sample.EngineMaxRpm));
        else if (uphill) threshold = Math.Min(limit / sample.EngineMaxRpm, threshold + Math.Min(0.05, intent.RoadGrade * 0.5));
        if (holdCornerOrLift && !fuelCut) return default;
        if (motion.PlateauMs >= 700 && fraction >= Math.Max(mid, peaks.Power) && Math.Abs(motion.SpeedRate) < 3)
            threshold = Math.Min(threshold, fraction);
        return (fuelCut || (throttle >= (race ? 0.05 : 0.20) && fraction >= threshold))
            ? new(next, race ? ShiftReason.VirtualTcuPowerBand : ShiftReason.VirtualTcuOffRoad) : default;
    }

    private static ShiftDecision Downshift(TelemetrySample sample, ReadOnlySpan<double> ratios, double projectedSpeed,
        double limit, ShiftReason reason, double targetRpm = 0, double targetSpeed = 0, double huntingCeiling = double.PositiveInfinity)
    {
        var lower = sample.Gear - 1;
        if (lower < 1) return default;
        var currentRatio = ratios[sample.Gear];
        var lowerRatio = ratios[lower];
        if (lowerRatio <= 0) return new(0, reason, NeedsRatio: true);
        if (currentRatio > 0 && lowerRatio <= currentRatio * 1.02) return default;

        var ceiling = Math.Min(limit * 0.98, huntingCeiling);
        var currentSpeedRpm = Math.Max(sample.SpeedKmh, projectedSpeed) * lowerRatio;
        if (currentSpeedRpm >= ceiling) return default;

        if (targetRpm > 0)
        {
            var currentRpm = currentRatio > 0 ? targetSpeed * currentRatio : sample.Rpm;
            var distance = Math.Abs(currentRpm - targetRpm);
            var betterGear = false;
            var previousRatio = currentRatio;
            for (var gear = lower; gear >= 1; gear--)
            {
                var ratio = ratios[gear];
                if (ratio <= previousRatio * 1.02 || ratio * sample.SpeedKmh >= ceiling) break;
                previousRatio = ratio;
                var error = Math.Abs(targetSpeed * ratio - targetRpm);
                if (error >= distance) continue;
                betterGear = true;
                distance = error;
            }
            if (!betterGear) return default;
        }
        return new(lower, reason, LandingRpm: projectedSpeed * lowerRatio);
    }

    private static double PowerBandRpm(TelemetrySample sample, (double Torque, double Power) peaks, double weight, double fallback) =>
        sample.EngineMaxRpm * (peaks.Power > 0 ? peaks.Torque + (Math.Max(peaks.Power, peaks.Torque) - peaks.Torque) * weight : fallback);

    private static double Ramp(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp((value - minimum) / (maximum - minimum), 0, 1) : 0;

    public void Reset()
    {
        _sportIndex = 0;
        _sporty = false;
        _strongBrakeMs = _steadySpeedMs = _brakeSpikeUntil = _throttleStompUntil = 0;
        _lastHardBrake = null;
    }
}
