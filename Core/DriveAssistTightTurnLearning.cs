using System;

namespace FH6Telemetry;

internal sealed class DriveAssistTightTurnLearning
{
    private (int Car, int PerformanceIndex, int Drivetrain) _vehicle;
    private int _turnDirection, _turnCount;
    private double _sampleSeconds, _gripTotal, _learnedGrip;

    public int? SpeedLimitKmh { get; private set; }

    public void UseVehicle(TelemetrySample sample)
    {
        var vehicle = (sample.CarOrdinal, sample.CarPerformanceIndex, sample.DrivetrainType);
        if (vehicle == _vehicle) return;
        _vehicle = vehicle;
        Reset();
    }

    public void Reset()
    {
        InterruptTurn();
        _turnCount = 0;
        _learnedGrip = 0;
        SpeedLimitKmh = null;
    }

    public void InterruptTurn()
    {
        _turnDirection = 0;
        _sampleSeconds = _gripTotal = 0;
    }

    public void Observe(DriveAssistDriver driver, DriveAssistState state, WheelSample outsideFront, double frontLimit, bool stable)
    {
        var steer = Math.Abs(driver.Steer);
        var direction = Math.Sign(driver.Steer);
        if (steer < 0.18 || direction != _turnDirection)
        {
            // Publish the new limit between turns so learning cannot revoke steering priority mid-corner.
            if (_sampleSeconds >= 0.20) CompleteTurn();
            InterruptTurn();
        }

        if (!stable || steer < 0.30 || !(state.Dt is > 0 and <= 0.12)) return;
        _turnDirection = direction;
        var speedKmh = state.Speed * 3.6;
        var tireUse = Math.Abs(outsideFront.SlipAngle) / frontLimit;
        var longG = Math.Abs(state.LongitudinalG);
        var yawG = state.Speed * state.Yaw * direction / TelemetryDerived.Gravity;
        if (speedKmh is < 18 or > 165 || driver.Brake > 0.68 || longG > 0.85 ||
            state.LateralG < 0.30 || yawG < state.LateralG * 0.52 || yawG > state.LateralG * 1.70 ||
            tireUse is < 0.42 or > 1.75 || Math.Abs(outsideFront.SlipRatio) > 0.65 ||
            outsideFront.NormalizedSuspensionTravel < 0.10 ||
            outsideFront.PuddleDepth > 0.14 || Math.Abs(outsideFront.SurfaceRumble) >= 0.52) return;

        // Combine lateral and longitudinal demand on the friction circle so trail-braked and apex-loaded corners calibrate cleanly.
        var combinedG = Math.Sqrt(state.LateralG * state.LateralG + 0.35 * longG * longG);
        var grip = Math.Max(combinedG, Math.Min(state.LateralG, yawG)) / Math.Clamp(tireUse, 0.58, 1.05);
        _sampleSeconds += state.Dt;
        _gripTotal += Math.Clamp(grip, 0.35, 3.6) * state.Dt;
    }

    private void CompleteTurn()
    {
        // Give each turn one vote even when the driver holds the same arc for a long time.
        _turnCount = Math.Min(_turnCount + 1, 6);
        _learnedGrip += (_gripTotal / _sampleSeconds - _learnedGrip) / _turnCount;
        if (_turnCount < 3) return;

        // At the same reference radius, speed grows with the square root of available lateral grip.
        var speedKmh = 56.0 * Math.Sqrt(_learnedGrip);
        SpeedLimitKmh = (int)Math.Clamp(Math.Round(speedKmh),
            DriveAssistSettings.MinimumTightTurnSpeedKmh, DriveAssistSettings.MaximumLearnedTightTurnSpeedKmh);
    }
}
