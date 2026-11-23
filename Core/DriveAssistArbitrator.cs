using System;
using static FH6Telemetry.DriveAssistEstimator;

namespace FH6Telemetry;

internal readonly record struct DriveAssistDriver(double Steer, double Throttle, double Brake, bool Handbrake, bool Clutch)
{
    public static DriveAssistDriver Read(ShiftGamepadReport p, ushort handbrake) => new(
        p.LeftX / (p.LeftX < 0 ? 32768.0 : 32767.0), p.RightTrigger / 255.0, p.LeftTrigger / 255.0,
        (p.Buttons & handbrake) != 0, (p.Buttons & (ushort)ShiftShortcutButton.LeftStick) != 0);
}

internal enum SteeringPriority
{
    Damper, SteeringTuning, Surface, Understeer, Skidpad, SplitGrip, Stability, CounterSteer, Drift, DriftTransition, Hairpin, Landing
}

internal struct DriveAssistDemands
{
    public DriveAssistFeature SteeringOwner, ThrottleOwner, BrakeOwner, ThrottleCapOwner, BrakeCapOwner, ClutchOwner;
    public double Steering, SteeringRate, Gas, Brake, ThrottleCap, BrakeCap;
    public double ThrottleCutRate, ThrottleRestoreRate, BrakeReleaseRate, BrakeRestoreRate;
    public bool ReleaseHandbrake;
    public bool PreserveDriverSteering { get; init; }
    private SteeringPriority _steeringPriority;
    private double _steeringConfidence;
    private int _gasPriority;

    public void Steer(double target, double confidence, SteeringPriority priority, DriveAssistFeature owner, double rate = 14)
    {
        if (PreserveDriverSteering && priority is (SteeringPriority.Damper or SteeringPriority.SteeringTuning or
            SteeringPriority.Understeer or SteeringPriority.Stability)) return;
        if (confidence <= 0 || SteeringOwner != 0 && (priority < _steeringPriority ||
            priority == _steeringPriority && confidence <= _steeringConfidence)) return;
        Steering = Math.Clamp(target, -1, 1);
        SteeringRate = rate;
        SteeringOwner = owner;
        _steeringPriority = priority;
        _steeringConfidence = confidence;
    }

    public void Throttle(double target, int priority, DriveAssistFeature owner)
    {
        if (ThrottleOwner != 0 && priority <= _gasPriority) return;
        Gas = Unit(target);
        ThrottleOwner = owner;
        _gasPriority = priority;
    }

    public void LimitThrottle(double ceiling, DriveAssistFeature owner, double cutRate = 0)
    {
        if (ThrottleCapOwner != 0 && ceiling >= ThrottleCap) return;
        ThrottleCap = Unit(ceiling);
        ThrottleCapOwner = owner;
        ThrottleCutRate = cutRate;
    }

    public void LimitBrake(double ceiling, DriveAssistFeature owner, double releaseRate = 0)
    {
        if (BrakeCapOwner != 0 && ceiling >= BrakeCap) return;
        BrakeCap = Unit(ceiling);
        BrakeCapOwner = owner;
        BrakeReleaseRate = releaseRate;
    }
}

internal sealed class DriveAssistArbitrator
{
    internal const DriveAssistFeature TractionLimits = DriveAssistFeature.Traction | DriveAssistFeature.CornerTraction |
        DriveAssistFeature.AdaptiveTraction;
    private DriveAssistDecision _last;
    private bool _hasLast;

    public void Reset() => _hasLast = false;

    public DriveAssistDecision Resolve(DriveAssistDriver driver, DriveAssistDemands demand, double dt, bool holdTraction = false,
        bool restoreThrottle = false, bool startFromDriverThrottle = false)
    {
        var previous = _hasLast ? _last : new DriveAssistDecision(driver, 0, 0, 0, 0, 0, 0, false);
        if (startFromDriverThrottle && previous.ThrottleOwner == 0 && demand.ThrottleCapOwner != 0)
            previous = previous with { Throttle = driver.Throttle };
        if (holdTraction && (previous.ThrottleOwner & TractionLimits) != 0 && previous.Throttle < driver.Throttle)
            demand.LimitThrottle(previous.Throttle, previous.ThrottleOwner);
        var steerOwner = demand.SteeringOwner;
        if (steerOwner == 0 && !demand.PreserveDriverSteering) steerOwner = previous.SteerOwner;
        var steering = steerOwner == 0 ? driver.Steer : Move(previous.Steering,
            demand.SteeringOwner != 0 ? demand.Steering : driver.Steer,
            dt * (demand.SteeringOwner != 0 ? demand.SteeringRate : 7));
        const DriveAssistFeature activeSteering = DriveAssistFeature.CounterSteer | DriveAssistFeature.DriftHold |
            DriveAssistFeature.Hairpin | DriveAssistFeature.SplitGrip | DriveAssistFeature.Cruise |
            DriveAssistFeature.Stability | DriveAssistFeature.SteeringTuning | DriveAssistFeature.DriftTransitions;
        if ((steerOwner & activeSteering) == 0 && steering * driver.Steer <= 0) steering = 0;
        if (Math.Abs(steering - driver.Steer) < 0.0001) steerOwner = 0;

        var gas = demand.ThrottleOwner != 0 ? demand.Gas : driver.Throttle;
        var gasOwner = demand.ThrottleOwner;
        if (demand.ThrottleCapOwner != 0 && gas > demand.ThrottleCap)
        {
            gas = demand.ThrottleCap;
            gasOwner = demand.ThrottleCapOwner;
        }
        if (gasOwner == 0 && !restoreThrottle && previous.ThrottleOwner != 0 && previous.Throttle < previous.Driver.Throttle && gas > previous.Throttle)
            gasOwner = previous.ThrottleOwner;
        if (gasOwner != 0 && driver.Throttle > 0.045 && driver.Throttle < previous.Driver.Throttle - 0.08 && gas < driver.Throttle)
            gas = Math.Min(gas, previous.Throttle);
        if (gasOwner != 0)
        {
            var falling = gas < previous.Throttle;
            var speed = falling ? 9.0 : 2.8;
            if ((gasOwner & DriveAssistFeature.Launch) != 0) speed = falling ? 14 : 8;
            if ((gasOwner & DriveAssistFeature.Landing) != 0) speed = falling ? 25 : 3.1;
            if (falling && gasOwner == demand.ThrottleCapOwner) speed = Math.Max(speed, demand.ThrottleCutRate);
            if (!falling && (gasOwner & TractionLimits) != 0) speed = Math.Max(speed, demand.ThrottleRestoreRate);
            gas = Move(previous.Throttle, gas, speed * dt);
            if (demand.ThrottleOwner == 0 && driver.Throttle < gas) gas = driver.Throttle;
            if (driver.Throttle <= 0.045 && demand.ThrottleOwner == 0) gas = driver.Throttle;
        }
        if (Math.Abs(gas - driver.Throttle) < 0.001) gasOwner = 0;

        var brake = demand.BrakeOwner != 0 ? demand.Brake : driver.Brake;
        var brakeOwner = demand.BrakeOwner;
        if (demand.BrakeCapOwner != 0 && brake > demand.BrakeCap)
        {
            brake = demand.BrakeCap;
            brakeOwner = demand.BrakeCapOwner;
        }
        if (brakeOwner == 0 && previous.BrakeOwner != 0 && previous.Brake < previous.Driver.Brake && brake > previous.Brake)
            brakeOwner = previous.BrakeOwner;
        if (brakeOwner != 0)
        {
            var rate = brake < previous.Brake ? Math.Max(11, demand.BrakeReleaseRate) : Math.Max(3.9, demand.BrakeRestoreRate);
            brake = Move(previous.Brake, brake, dt * rate);
            if (demand.BrakeOwner == 0 && driver.Brake < brake) brake = driver.Brake;
        }
        if (Math.Abs(brake - driver.Brake) < 0.001) brakeOwner = 0;

        _hasLast = true;
        return _last = new(driver, steering, gas, brake, steerOwner, gasOwner, brakeOwner,
            demand.ReleaseHandbrake, demand.ClutchOwner);
    }

    private static double Move(double from, double to, double limit) => from + Math.Clamp(to - from, -limit, limit);
}

internal readonly record struct DriveAssistDecision(DriveAssistDriver Driver, double Steering, double Throttle, double Brake,
    DriveAssistFeature SteerOwner, DriveAssistFeature ThrottleOwner, DriveAssistFeature BrakeOwner, bool ReleaseHandbrake,
    DriveAssistFeature ClutchOwner = DriveAssistFeature.None)
{
    public ShiftGamepadReport Apply(ShiftGamepadReport physical, ShiftGamepadReport output, ushort handbrake,
        bool shifting, out DriveAssistFeature applied, byte shiftBlip = 0)
    {
        applied = 0;
        var current = DriveAssistDriver.Read(physical, handbrake);
        if (current.Handbrake && !Driver.Handbrake) return output;

        if (SteerOwner != 0 && Math.Abs(current.Steer - Driver.Steer) < 0.30)
        {
            var axis = (short)Math.Round(Steering * (Steering < 0 ? 32768 : 32767));
            if (axis != output.LeftX) applied |= SteerOwner;
            output = output with { LeftX = axis };
        }
        var holdOverridden = BrakeOwner.HasFlag(DriveAssistFeature.HillStart) &&
            (current.Throttle > 0.16 || current.Handbrake || current.Clutch);
        var extraBrakeOverridden = BrakeOwner.HasFlag(DriveAssistFeature.AdaptiveBraking) &&
            Brake > Driver.Brake && (current.Brake < 0.07 || current.Throttle >= 0.08);
        if (BrakeOwner != 0 && !holdOverridden && !extraBrakeOverridden)
        {
            var brake = (byte)Math.Round(Brake * 255);
            var axis = Brake <= Driver.Brake ? Math.Min(output.LeftTrigger, brake) : Math.Max(output.LeftTrigger, brake);
            if (axis != output.LeftTrigger) applied |= BrakeOwner;
            output = output with { LeftTrigger = axis };
        }
        var suppressBlip = (ThrottleOwner & (DriveAssistFeature.Landing | DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability)) != 0;
        var cutsThrottle = Throttle <= Driver.Throttle;
        const DriveAssistFeature throughShift = DriveAssistArbitrator.TractionLimits |
            DriveAssistFeature.TransmissionAssist | DriveAssistFeature.Landing | DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability;
        if (ThrottleOwner != 0 && (!shifting || cutsThrottle && (ThrottleOwner & throughShift) != 0))
        {
            var throttle = (byte)Math.Round(Throttle * 255);
            var axis = cutsThrottle ? Math.Min(output.RightTrigger, throttle) :
                current.Brake < 0.03 && output.LeftTrigger == 0 && !current.Clutch && Math.Abs(current.Throttle - Driver.Throttle) < 0.09
                    ? Math.Max(output.RightTrigger, throttle) : output.RightTrigger;
            if (shifting && !suppressBlip) axis = Math.Max(axis, shiftBlip);
            if (axis != output.RightTrigger) applied |= ThrottleOwner;
            output = output with { RightTrigger = axis };
        }
        if (ReleaseHandbrake && (output.Buttons & handbrake) != 0)
        {
            output = output with { Buttons = (ushort)(output.Buttons & ~handbrake) };
            applied |= DriveAssistFeature.Hairpin;
        }
        if (ClutchOwner != 0 && !shifting && (output.Buttons & (ushort)ShiftShortcutButton.LeftStick) == 0)
        {
            output = output with { Buttons = (ushort)(output.Buttons | (ushort)ShiftShortcutButton.LeftStick) };
            applied |= ClutchOwner;
        }
        return output;
    }
}
