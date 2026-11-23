using System;
using static FH6Telemetry.DriveAssistEstimator;

namespace FH6Telemetry;

internal sealed class DriveAssistCruise
{
    private long? _pendingUntil;
    private DriveAssistAccumulator? _run;
    private double _integral, _target, _radius, _centerX, _centerZ, _direction, _turnIn, _previousRadius;
    private double _probeTime, _probeG;
    private bool _foundGripLimit, _steady;
    private TelemetrySample? _previous;

    public bool IsActive => _run is not null;
    public bool IsPending(long now) => _pendingUntil is long until && now < until;
    public DriveAssistRun? Result => _run?.Snapshot() ?? LastResult;
    private DriveAssistRun? LastResult { get; set; }

    public void Request(long now)
    {
        Cancel();
        _pendingUntil = now + 15000;
    }

    public void Cancel(bool clearPending = true)
    {
        if (clearPending) _pendingUntil = null;
        if (_run?.Snapshot() is { Seconds: >= 1 } result) LastResult = result;
        _run = null;
        _integral = _target = _radius = _probeTime = _probeG = 0;
        _foundGripLimit = _steady = false;
        _previous = null;
    }

    public void Suggest(TelemetrySample s, DriveAssistState state, DriveAssistDriver driver, DriveAssistSettings settings,
        long now, ref DriveAssistDemands demand)
    {
        var old = _previous;
        _previous = s;
        var overridePedals = driver.Brake > 0.055 || driver.Throttle > 0.12 || driver.Clutch || driver.Handbrake || s.HandBrake > 0.2;
        var unsafeMotion = state.Phase is DriveAssistPhase.Airborne or DriveAssistPhase.Landing or DriveAssistPhase.Oversteer ||
            state.Speed < 4.5 || s.VelocityZ < 0;
        if (IsActive && (overridePedals || unsafeMotion || settings.Skidpad && Math.Abs(driver.Steer) > 0.73))
        {
            Cancel();
            return;
        }

        if (!IsActive)
        {
            if (!IsPending(now) || overridePedals || unsafeMotion || s.RacePosition > 0 && s.CurrentRaceTimeSeconds <= 0) return;
            if (settings.Skidpad)
            {
                if (old is null || Math.Abs(driver.Steer) is < 0.18 or > 0.70 || Math.Abs(state.Yaw) < 0.14 || state.LateralG < 0.30) return;
                var dx = s.PositionX - old.PositionX;
                var dz = s.PositionZ - old.PositionZ;
                var distance = Math.Sqrt(dx * dx + dz * dz);
                var radius = state.Speed / Math.Abs(state.Yaw);
                if (!(distance is >= 0.035 and <= 12 && radius is >= 8 and <= 160)) return;
                _direction = Math.Sign(state.Yaw);
                _radius = _previousRadius = radius;
                _centerX = s.PositionX + _direction * radius * dz / distance;
                _centerZ = s.PositionZ - _direction * radius * dx / distance;
                _turnIn = driver.Steer;
                _target = Math.Min(settings.TargetSpeedKmh, s.SpeedKmh);
                _probeG = state.LateralG;
            }
            else _target = settings.TargetSpeedKmh;

            _pendingUntil = null;
            _run = new(settings.Skidpad ? DriveAssistRunMode.Skidpad : DriveAssistRunMode.Cruise);
        }

        if (settings.Skidpad)
        {
            var measuredRadius = Math.Sqrt(Math.Pow(s.PositionX - _centerX, 2) + Math.Pow(s.PositionZ - _centerZ, 2));
            if (!(measuredRadius >= _radius * 0.55 && measuredRadius <= _radius * 1.45)) { Cancel(); return; }
            var radialSpeed = (measuredRadius - _previousRadius) / state.Dt;
            _previousRadius = measuredRadius;
            var correction = Math.Clamp((measuredRadius / _radius - 1) * 1.8 + radialSpeed / state.Speed * 0.8, -0.55, 0.55);
            var desiredYaw = _direction * state.Speed / _radius * (1 + correction);
            var steering = _turnIn + (desiredYaw - state.Yaw) * 0.85 - state.YawAccel * 0.025;
            demand.Steer(steering, 1, SteeringPriority.Skidpad, DriveAssistFeature.Cruise, 4.5);

            _probeTime += state.Dt;
            var rearCombined = Math.Max(Math.Abs(s.RearLeft.CombinedSlip), Math.Abs(s.RearRight.CombinedSlip));
            var saturated = Math.Max(state.FrontCombined, rearCombined) >= 0.94 || Math.Abs(state.Beta) > 0.15;
            if (saturated) _target = Math.Max(18, Math.Min(_target, s.SpeedKmh) - 3.5 * state.Dt);
            else if (!_foundGripLimit && Math.Abs(measuredRadius / _radius - 1) < 0.15)
                _target = Math.Min(settings.TargetSpeedKmh, _target + 1.5 * state.Dt);
            _steady = Math.Abs(measuredRadius / _radius - 1) < 0.12 && Math.Abs(s.SpeedKmh - _target) < 4 &&
                Math.Abs(state.YawAccel) < 0.70 && Math.Abs(state.Beta) < 0.18;

            if (_probeTime >= 1.6)
            {
                _foundGripLimit |= state.LateralG <= _probeG + 0.018 && Math.Abs(s.SpeedKmh - _target) < 3;
                _probeTime = 0;
                _probeG = state.LateralG;
            }
        }

        var error = (_target - s.SpeedKmh) / 3.6;
        _integral = Math.Clamp(_integral + error * state.Dt * 0.085, -0.27, 0.45);
        var effort = 0.14 * error + _integral - 0.15 * state.LongitudinalG;
        demand.Throttle(Math.Min(0.70, Unit(effort)), 1, DriveAssistFeature.Cruise);
        demand.Brake = Math.Min(0.42, Unit(-effort));
        demand.BrakeOwner = DriveAssistFeature.Cruise;
    }

    public void Record(TelemetrySample sample, DriveAssistFrame frame, bool feedback) => _run?.Add(sample, frame, feedback, _target, _radius, _steady);
}
