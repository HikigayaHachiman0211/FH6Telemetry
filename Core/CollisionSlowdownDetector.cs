using System;

namespace FH6Telemetry;

internal readonly record struct CollisionSlowdownState(bool Impact, bool PowerLimited, bool CheckingPower = false)
{
    public bool Holding => Impact || PowerLimited || CheckingPower;
}

internal sealed class CollisionSlowdownDetector
{
    private const int ContactSettleMs = 350;
    private const int ImpactWatchMs = 3600;
    private const int LimitEvidenceMs = 140;
    private const int PowerRecoveryMs = 120;
    private const int MaxLimitHoldMs = 7000;

    private TelemetrySample? _previous;
    private long _impactAt = long.MinValue, _contactUntil, _limitUntil;
    private int _lowPowerMs, _restoredPowerMs;

    public CollisionSlowdownState Observe(TelemetrySample sample, ShiftPedals pedals, long now, bool shifting)
    {
        if (!sample.IsRaceOn || sample.CarOrdinal <= 0)
        {
            Reset();
            return default;
        }

        var previous = _previous;
        var dt = previous is null ? 0 : unchecked(sample.TimestampMs - previous.TimestampMs);
        if (previous is not null && (dt > 120 || previous.CarOrdinal != sample.CarOrdinal ||
            previous.CarPerformanceIndex != sample.CarPerformanceIndex || previous.DrivetrainType != sample.DrivetrainType))
        {
            Reset();
            previous = null;
            dt = 0;
        }
        _previous = sample;

        var speedDrop = previous is null ? 0 : previous.SpeedKmh - sample.SpeedKmh;
        var lateral = Math.Abs(sample.AccelerationX);
        var decel = -sample.AccelerationZ;
        var longitudinal = Math.Abs(sample.AccelerationZ);
        var impactMagnitude = lateral * lateral + longitudinal * longitudinal;
        var newSmashable = (sample.SmashableVelDiff > 0.5 || sample.SmashableMass > 0) &&
            (previous is null || Math.Abs(sample.SmashableVelDiff - previous.SmashableVelDiff) > 0.10 ||
                sample.SmashableMass != previous.SmashableMass);
        // Walls often produce a sharp speed step even when both smashable fields stay zero.
        var hardContact = previous is not null && previous.SpeedKmh >= 12 && dt is > 0 and <= 100 &&
            (speedDrop > Math.Max(2.5, dt * 0.075) && impactMagnitude > 36 * 36 ||
             speedDrop >= Math.Max(8.5, dt * 0.16) &&
                SpeedAgreesWithVelocity(previous) && SpeedAgreesWithVelocity(sample) &&
                (Math.Abs(previous.VelocityZ - sample.VelocityZ) >= 1.7 || Math.Abs(previous.VelocityX - sample.VelocityX) >= 1.7) ||
             lateral > 50 && Math.Abs(previous.VelocityX - sample.VelocityX) >= 1.6);
        if (newSmashable || hardContact) _contactUntil = now + ContactSettleMs;
        if (hardContact || newSmashable && speedDrop > 1.5 && decel > 12) _impactAt = now;

        // Keep the gearbox quiet while the chassis is still rapidly decelerating or rotating after contact.
        var hasImpact = _impactAt != long.MinValue;
        var impactAge = hasImpact ? now - _impactAt : long.MaxValue;
        if (hasImpact && impactAge is >= 0 and < 1350 && sample.SpeedKmh >= 5 &&
            (decel > 23 || impactMagnitude > 37 * 37 || Math.Abs(sample.AngularVelocityY) > 0.65 &&
             Math.Abs(sample.VelocityX) > Math.Max(1.7, Math.Abs(sample.VelocityZ) * 0.16)))
            _contactUntil = Math.Max(_contactUntil, now + 170);

        var contact = now < _contactUntil;
        if (!hasImpact || impactAge is < 0 or >= MaxLimitHoldMs) return new(contact, false);

        // FH6 provides torque and pedal feedback, so unexpected output loss is inferred only under applied power.
        var usable = dt is > 0 and <= 120 && sample.Gear is >= 1 and <= AutomaticShiftSettings.MaximumForwardGears &&
            previous?.Gear == sample.Gear && !shifting && pedals.Throttle >= 0.55 && pedals.Brake < 0.14 &&
            sample.Throttle >= 0.38 && sample.Brake < 0.14 && sample.HandBrake < 0.08 && sample.Clutch < 0.08 &&
            sample.VelocityZ >= -1.2 && double.IsFinite(sample.PowerKw) && double.IsFinite(sample.TorqueNm);
        var limited = now < _limitUntil;
        // A collision can pin first gear at the reported idle RPM even with the pedal fully applied.
        var cappedAtIdle = sample.Gear == 1 && sample.SpeedKmh < 4 && sample.Throttle >= 0.55 &&
            sample.Rpm >= sample.EngineIdleRpm * 0.90;
        var lowOutput = (limited || impactAge < ImpactWatchMs) && usable &&
            (cappedAtIdle || sample.Rpm >= sample.EngineIdleRpm + 120) && sample.Rpm < 0.80 * sample.EngineMaxRpm &&
            sample.TorqueNm is >= -30 and <= 15 && sample.PowerKw <= 6 && sample.AccelerationZ < 1.4 && HasDrivenGrip(sample);
        _lowPowerMs = lowOutput ? (int)Math.Min(_lowPowerMs + dt, LimitEvidenceMs) : 0;
        if (_lowPowerMs >= LimitEvidenceMs) _limitUntil = now + 450;

        var torqueRestored = limited && usable && sample.TorqueNm >= 32 && sample.PowerKw >= 9;
        _restoredPowerMs = torqueRestored ? (int)Math.Min(_restoredPowerMs + dt, PowerRecoveryMs) : 0;
        if (_restoredPowerMs >= PowerRecoveryMs)
        {
            _limitUntil = 0;
            _impactAt = long.MinValue;
        }
        return new(contact, now < _limitUntil, lowOutput);
    }

    private static bool SpeedAgreesWithVelocity(TelemetrySample s)
    {
        var speed = Math.Sqrt(s.VelocityX * s.VelocityX + s.VelocityY * s.VelocityY + s.VelocityZ * s.VelocityZ) * 3.6;
        return Math.Abs(s.SpeedKmh - speed) <= Math.Max(5, s.SpeedKmh * 0.14);
    }

    private static bool HasDrivenGrip(TelemetrySample s)
    {
        var front = Math.Abs(s.FrontLeft.SlipRatio) <= 1 && Math.Abs(s.FrontRight.SlipRatio) <= 1;
        var rear = Math.Abs(s.RearLeft.SlipRatio) <= 1 && Math.Abs(s.RearRight.SlipRatio) <= 1;
        return s.DrivetrainType switch { 0 => front, 1 => rear, _ => front && rear };
    }

    public void Reset()
    {
        _previous = null;
        _impactAt = long.MinValue;
        _contactUntil = _limitUntil = 0;
        _lowPowerMs = _restoredPowerMs = 0;
    }
}
