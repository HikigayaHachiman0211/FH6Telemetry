using System;

namespace FH6Telemetry;

internal enum DriverIntentKind
{
    Idle, Cruising, Attacking, Kickdown, CornerEntry, Braking, HardBraking,
    TrailBraking, MidCorner, CornerExit, Coasting, Drifting, OffRoad, RearSliding,
}

internal readonly record struct DriverIntentSnapshot(
    DriverIntentKind Kind,
    double Aggression,
    double CornerLoad,
    double RoadGrade,
    double SurfaceRoughness,
    double BrakeIntensity,
    bool FastThrottleLift,
    bool ThrottleStomp,
    bool BrakeSpike,
    bool CornerExitWindow,
    bool RearSliding,
    bool DrivenWheelLockup,
    bool IsTurboSpooling);

/// <summary>Synthesizes real-time pedal dynamics, cornering, grade, slip, and boost telemetry into driver intent.</summary>
internal sealed class DriverIntentAnalyzer
{
    private double _aggression;
    private double _surfaceRoughness;
    private long _throttleStompUntil;
    private long _brakeSpikeUntil;
    private long _fastLiftUntil;
    private VehicleSideSlip _sideSlip;
    private long? _lastLoadedCornerAt;
    private bool _hasTurbo;

    public DriverIntentSnapshot Observe(TelemetrySample sample, ShiftPedals pedals, ShiftDynamics motion,
        bool driftMode, long now)
    {
        var latG = double.IsFinite(sample.AccelerationX) ? Math.Abs(sample.AccelerationX) / TelemetryDerived.Gravity : 0;
        var rpmFrac = sample.EngineMaxRpm > 0 ? Math.Clamp(sample.Rpm / sample.EngineMaxRpm, 0, 1.1) : 0;

        var rawAggression = Math.Min(1,
            0.45 * Ramp(pedals.Throttle, 0.60, 1.0) +
            0.50 * Ramp(pedals.Brake, 0.30, 0.85) +
            0.55 * Ramp(latG, 0.35, 1.15) +
            0.25 * Ramp(rpmFrac, 0.65, 1.0) +
            (motion.ThrottleRise > 0.30 ? 0.30 : 0) +
            (motion.BrakeRise > 0.25 ? 0.30 : 0));
        var aggrTau = rawAggression > _aggression ? 350.0 : 5500.0;
        _aggression += (rawAggression - _aggression) * (1 - Math.Exp(-motion.ElapsedMs / aggrTau));

        var steerAbs = double.IsFinite(sample.Steer) ? Math.Abs(sample.Steer) : 0;
        var yawAbs = double.IsFinite(sample.AngularVelocityY) ? Math.Abs(sample.AngularVelocityY) : 0;
        var cornerLoad = Math.Clamp(Math.Max(latG / 1.15,
            sample.SpeedKmh >= 25 ? Math.Max(yawAbs / 0.85, steerAbs * 0.75) : 0), 0, 1);

        var rawRough = (
            WheelRoughness(sample.FrontLeft) + WheelRoughness(sample.FrontRight) +
            WheelRoughness(sample.RearLeft) + WheelRoughness(sample.RearRight)) / 4.0;
        _surfaceRoughness += (rawRough - _surfaceRoughness) * (1 - Math.Exp(-Math.Max(1, motion.ElapsedMs) / 200.0));

        var brakeIntensity = Math.Clamp(Math.Max(pedals.Brake,
            Math.Max(0, -motion.SpeedRate) / (0.90 * TelemetryDerived.Gravity * 3.6)), 0, 1);

        if (motion.ThrottleRise >= 0.32 && pedals.Throttle >= 0.55)
            _throttleStompUntil = now + 250;
        if (motion.BrakeRise >= 0.26 && pedals.Brake >= 0.25)
            _brakeSpikeUntil = now + 220;

        if (pedals.Throttle >= 0.65)
            _fastLiftUntil = 0;
        else if (motion.ThrottleFall >= 0.28 && pedals.Throttle <= 0.45)
        {
            var cornering = cornerLoad >= 0.12 || steerAbs >= 0.12 || pedals.Braking;
            if (cornering && (_aggression >= 0.35 || rpmFrac >= 0.62))
                _fastLiftUntil = Math.Max(_fastLiftUntil, now + 780);
            else if (_aggression >= 0.45 && rpmFrac >= 0.62)
                _fastLiftUntil = Math.Max(_fastLiftUntil, now + 100);
        }

        if (cornerLoad >= 0.45)
            _lastLoadedCornerAt = now;

        var cornerExitWindow = _lastLoadedCornerAt is long cornerAt && now - cornerAt <= 1200 &&
            !pedals.Braking && pedals.Throttle >= 0.28 &&
            (motion.SteerRate <= -0.10 || (cornerLoad < 0.35 && steerAbs <= 0.12));
        var rearSliding = _sideSlip.Observe(sample, VehicleSideSlip.Angle(sample));

        if (double.IsFinite(sample.BoostPsi) && sample.BoostPsi > 2.5)
            _hasTurbo = true;

        var fastLift = now < _fastLiftUntil;
        var stomp = now < _throttleStompUntil;
        var brakeSpike = now < _brakeSpikeUntil;
        var lockup = pedals.Braking && MinDrivenSlip(sample) <= -0.45;

        var kind = ClassifyKind(sample, pedals, cornerLoad, steerAbs, brakeIntensity,
            fastLift, stomp, cornerExitWindow, rearSliding, driftMode, _surfaceRoughness);

        return new DriverIntentSnapshot(
            kind, _aggression, cornerLoad, motion.RoadGrade, _surfaceRoughness, brakeIntensity,
            fastLift, stomp, brakeSpike, cornerExitWindow, rearSliding, lockup, _hasTurbo);
    }

    public static string BadgeText(DriverIntentKind kind) => kind switch
    {
        DriverIntentKind.Cruising => TelemetryText.Get("IntentCruising"),
        DriverIntentKind.Attacking => TelemetryText.Get("IntentAttacking"),
        DriverIntentKind.Kickdown => TelemetryText.Get("IntentKickdown"),
        DriverIntentKind.CornerEntry => TelemetryText.Get("IntentCornerEntry"),
        DriverIntentKind.Braking => TelemetryText.Get("IntentBraking"),
        DriverIntentKind.HardBraking => TelemetryText.Get("IntentHardBraking"),
        DriverIntentKind.TrailBraking => TelemetryText.Get("IntentTrailBraking"),
        DriverIntentKind.MidCorner => TelemetryText.Get("IntentMidCorner"),
        DriverIntentKind.CornerExit => TelemetryText.Get("IntentCornerExit"),
        DriverIntentKind.Coasting => TelemetryText.Get("IntentCoasting"),
        DriverIntentKind.Drifting => TelemetryText.Get("IntentDrifting"),
        DriverIntentKind.RearSliding => TelemetryText.Get("DsRearSlide"),
        DriverIntentKind.OffRoad => TelemetryText.Get("IntentOffRoad"),
        _ => "",
    };

    public void Reset()
    {
        _aggression = _surfaceRoughness = 0;
        _throttleStompUntil = _brakeSpikeUntil = _fastLiftUntil = 0;
        _sideSlip = default;
        _lastLoadedCornerAt = null;
        _hasTurbo = false;
    }

    private static DriverIntentKind ClassifyKind(TelemetrySample sample, ShiftPedals pedals, double cornerLoad,
        double steerAbs, double brakeIntensity, bool fastLift, bool stomp, bool cornerExitWindow,
        bool rearSliding, bool driftMode, double surfaceRoughness)
    {
        if (sample.SpeedKmh < 3) return DriverIntentKind.Idle;
        if (rearSliding) return driftMode ? DriverIntentKind.Drifting : DriverIntentKind.RearSliding;
        if (pedals.Braking)
        {
            if (brakeIntensity >= 0.65 && cornerLoad < 0.50) return DriverIntentKind.HardBraking;
            if (cornerLoad >= 0.45) return DriverIntentKind.TrailBraking;
            return DriverIntentKind.Braking;
        }
        if (fastLift && (cornerLoad >= 0.15 || steerAbs >= 0.15)) return DriverIntentKind.CornerEntry;
        if (cornerExitWindow && pedals.WantsPower) return DriverIntentKind.CornerExit;
        if (cornerLoad >= 0.52) return DriverIntentKind.MidCorner;
        if (pedals.Kickdown || (stomp && pedals.WantsPower)) return DriverIntentKind.Kickdown;
        if (surfaceRoughness >= 0.45) return DriverIntentKind.OffRoad;
        if (pedals.Throttle >= 0.72 && pedals.WantsPower) return DriverIntentKind.Attacking;
        if (pedals.Coasting) return DriverIntentKind.Coasting;
        return DriverIntentKind.Cruising;
    }

    private static double WheelRoughness(WheelSample wheel) =>
        (double.IsFinite(wheel.SurfaceRumble) ? Math.Clamp(wheel.SurfaceRumble, 0, 1) : 0) +
        (double.IsFinite(wheel.PuddleDepth) ? Math.Clamp(wheel.PuddleDepth * 1.2, 0, 1) : 0);

    private static double MinDrivenSlip(TelemetrySample sample)
    {
        var front = Math.Min(SlipOrZero(sample.FrontLeft.SlipRatio), SlipOrZero(sample.FrontRight.SlipRatio));
        var rear = Math.Min(SlipOrZero(sample.RearLeft.SlipRatio), SlipOrZero(sample.RearRight.SlipRatio));
        return sample.DrivetrainType switch { 0 => front, 1 => rear, _ => Math.Min(front, rear) };
    }

    private static double SlipOrZero(double v) => double.IsFinite(v) ? v : 0;
    private static double Ramp(double v, double min, double max) =>
        double.IsFinite(v) ? Math.Clamp((v - min) / (max - min), 0, 1) : 0;
}
