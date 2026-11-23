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
        var yawG = state.Speed * state.Yaw * direction / TelemetryDerived.Gravity;
        if (speedKmh is < 20 or > 110 || driver.Brake > 0.40 || Math.Abs(state.LongitudinalG) > 0.40 ||
            state.LateralG < 0.30 || yawG < state.LateralG * 0.55 || yawG > state.LateralG * 1.65 ||
            tireUse is < 0.50 or > 1.40 || Math.Abs(outsideFront.SlipRatio) > 0.40 ||
            outsideFront.OnRumbleStrip || outsideFront.PuddleDepth > 0.08 || Math.Abs(outsideFront.SurfaceRumble) >= 0.34) return;

        var grip = Math.Min(state.LateralG, yawG) / Math.Clamp(tireUse, 0.62, 1);
        _sampleSeconds += state.Dt;
        _gripTotal += Math.Clamp(grip, 0.35, 2.3) * state.Dt;
    }

    private void CompleteTurn()
    {
        // Give each turn one vote even when the driver holds the same arc for a long time.
        _turnCount = Math.Min(_turnCount + 1, 6);
        _learnedGrip += (_gripTotal / _sampleSeconds - _learnedGrip) / _turnCount;
        if (_turnCount < 3) return;

        // At the same reference radius, speed grows with the square root of available lateral grip.
        var speedKmh = DriveAssistSettings.DefaultTightTurnSpeedKmh * Math.Sqrt(_learnedGrip);
        SpeedLimitKmh = (int)Math.Clamp(Math.Round(speedKmh),
            DriveAssistSettings.MinimumTightTurnSpeedKmh, DriveAssistSettings.MaximumTightTurnSpeedKmh);
    }
}
