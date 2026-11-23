using System;

namespace FH6Telemetry;

internal sealed class ShiftDynamics
{
    private const int ContactSettleMs = 350;
    private TelemetrySample? _previous;
    private long _lastAt;
    private long _contactUntil;
    private long _landingUntil;
    private long _loadPeakAt;
    private double _loadPeak;
    private bool _hasLoadedSuspension;
    private double _throttleAverage;
    private double _brakeAverage;
    private double _previousSteerAbs;

    public double SpeedRate { get; private set; }
    public double RpmRate { get; private set; }
    public double ThrottleRise { get; private set; }
    public double ThrottleFall { get; private set; }
    public double BrakeRise { get; private set; }
    public double BrakeFall { get; private set; }
    public double SteerRate { get; private set; }
    public double RoadGrade { get; private set; }
    public long PlateauMs { get; private set; }
    public long CoastingMs { get; private set; }
    public long ElapsedMs { get; private set; }
    public bool HasContact(long now) => now >= _contactUntil;
    public bool IsLandingHold(long now) => now < _landingUntil;

    public void ObserveFlight(TelemetrySample sample, long now)
    {
        var suspension = Math.Max(Math.Max(sample.FrontLeft.NormalizedSuspensionTravel, sample.FrontRight.NormalizedSuspensionTravel),
            Math.Max(sample.RearLeft.NormalizedSuspensionTravel, sample.RearRight.NormalizedSuspensionTravel));
        if (suspension > 0.10) _hasLoadedSuspension = true;
        if (sample.SpeedKmh > 15 && (sample.AccelerationY < -8 || (_hasLoadedSuspension && suspension <= 0.06)))
            _landingUntil = now + ContactSettleMs;
    }

    public void Observe(TelemetrySample sample, ShiftPedals pedals, long now, bool shifting, bool trackRpm = true)
    {
        var previous = _previous;
        var deltaMs = previous is null ? 0 : unchecked(sample.TimestampMs - previous.TimestampMs);
        ElapsedMs = previous is null ? 0 : Math.Clamp(now - _lastAt, 0, 100);
        _previous = sample;
        _lastAt = now;

        var impact = previous is not null && sample.SpeedKmh > 5 && previous.SpeedKmh - sample.SpeedKmh > 25;
        if (impact) _contactUntil = now + ContactSettleMs;

        var steerAbs = double.IsFinite(sample.Steer) ? Math.Abs(sample.Steer) : 0;
        if (previous is not null && deltaMs is >= 1 and <= 100 && HasContact(now))
        {
            var blend = 1 - Math.Exp(-deltaMs / 90.0);
            var speedRate = Math.Clamp((sample.SpeedKmh - previous.SpeedKmh) * 1000 / deltaMs, -65, 45);
            SpeedRate += blend * (speedRate - SpeedRate);
            var steerDeltaRate = Math.Clamp((steerAbs - _previousSteerAbs) * 1000 / deltaMs, -8, 8);
            SteerRate += blend * (steerDeltaRate - SteerRate);
            if (!shifting && trackRpm && sample.Gear == previous.Gear)
            {
                var rpmRate = Math.Clamp((sample.Rpm - previous.Rpm) * 1000 / deltaMs, -16000, 16000);
                RpmRate += blend * (rpmRate - RpmRate);
            }
            else RpmRate = 0;
        }
        else SpeedRate = RpmRate = SteerRate = 0;
        _previousSteerAbs = steerAbs;

        var gradeDecay = 1 - Math.Exp(-Math.Max(1, ElapsedMs) / 240.0);
        if (HasContact(now) && sample.SpeedKmh >= 20 && double.IsFinite(sample.VelocityY))
        {
            var speedMps = sample.SpeedKmh / 3.6;
            if (Math.Abs(sample.VelocityY) <= speedMps * 0.45)
            {
                var rawGrade = Math.Clamp(sample.VelocityY / speedMps, -0.35, 0.35);
                RoadGrade += gradeDecay * (rawGrade - RoadGrade);
            }
            else
                RoadGrade += gradeDecay * (0 - RoadGrade);
        }
        else
            RoadGrade += gradeDecay * (0 - RoadGrade);

        if (previous is null || shifting)
        {
            _throttleAverage = pedals.Throttle;
            _brakeAverage = pedals.Brake;
        }
        ThrottleRise = Math.Max(0, pedals.Throttle - _throttleAverage);
        ThrottleFall = Math.Max(0, _throttleAverage - pedals.Throttle);
        BrakeRise = Math.Max(0, pedals.Brake - _brakeAverage);
        BrakeFall = Math.Max(0, _brakeAverage - pedals.Brake);
        var pedalBlend = 1 - Math.Exp(-ElapsedMs / 140.0);
        _throttleAverage += pedalBlend * (pedals.Throttle - _throttleAverage);
        _brakeAverage += pedalBlend * (pedals.Brake - _brakeAverage);
        CoastingMs = !shifting && pedals.Throttle <= 0.15 && pedals.Brake <= 0.15 && HasContact(now)
            ? Math.Min(2000, CoastingMs + ElapsedMs) : 0;

        var peakBand = Math.Max(20, sample.EngineMaxRpm * 0.003);
        if (previous is null || sample.Gear != previous.Gear || shifting || !trackRpm || !HasContact(now) ||
            sample.Throttle < 0.85 || sample.Brake > 0.05 ||
            sample.Rpm > _loadPeak + peakBand || sample.Rpm < _loadPeak - 4 * peakBand)
        {
            _loadPeak = sample.Rpm;
            _loadPeakAt = now;
        }
        PlateauMs = now - _loadPeakAt;
    }

    public double EngagementSpeed(double speed, double milliseconds) =>
        Math.Max(0, speed + SpeedRate * Math.Clamp(milliseconds / 1000, 0, 0.35));

    public void Reset()
    {
        _previous = null;
        _contactUntil = 0;
        _previousSteerAbs = 0;
        SpeedRate = RpmRate = ThrottleRise = ThrottleFall = BrakeRise = BrakeFall = SteerRate = RoadGrade = 0;
        PlateauMs = CoastingMs = ElapsedMs = 0;
    }

    public void ResetFlight()
    {
        _landingUntil = 0;
        _hasLoadedSuspension = false;
    }
}
