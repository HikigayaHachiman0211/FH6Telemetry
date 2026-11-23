using System;

namespace FH6Telemetry;

internal struct VehicleSideSlip
{
    public const double DefaultCombinedThreshold = 1.10;
    private uint? _previous;
    private uint _durationMs;

    public static double? Angle(TelemetrySample sample) =>
        sample.SpeedKmh >= 5 && sample.Gear != 0 && double.IsFinite(sample.VelocityX) &&
        double.IsFinite(sample.VelocityZ) && sample.VelocityZ >= 0 &&
        Math.Abs(sample.VelocityX) + sample.VelocityZ >= 1.5
            ? Math.Atan2(sample.VelocityX, sample.VelocityZ) : null;

    public bool Observe(TelemetrySample sample, double? bodyAngle, double combinedThreshold = DefaultCombinedThreshold)
    {
        var rearAngle = (Math.Abs(sample.RearLeft.SlipAngle) + Math.Abs(sample.RearRight.SlipAngle)) / 2;
        var rearCombined = (Math.Abs(sample.RearLeft.CombinedSlip) + Math.Abs(sample.RearRight.CombinedSlip)) / 2;
        if (!sample.IsRaceOn || sample.SpeedKmh < 25 || bodyAngle is not double angle || Math.Abs(angle) < 0.16 ||
            !(Math.Abs(sample.AngularVelocityY) >= 0.12 || Math.Abs(angle) >= 0.28) ||
            !(double.IsFinite(rearAngle) && double.IsFinite(rearCombined) && rearAngle >= 0.50 &&
              (rearAngle >= 1 || rearCombined >= combinedThreshold)))
        {
            this = default;
            return false;
        }

        // A single kerb strike can spike the normalized tire readings without a sustained slide.
        var elapsed = _previous is uint previous ? unchecked(sample.TimestampMs - previous) : 0u;
        _previous = sample.TimestampMs;
        _durationMs = elapsed <= 150 ? Math.Min(90, _durationMs + elapsed) : 0;
        return _durationMs >= 90;
    }
}
