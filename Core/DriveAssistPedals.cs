using System;
using static FH6Telemetry.DriveAssistEstimator;

namespace FH6Telemetry;

internal enum DriveAssistSurface { Unknown, Road, Loose, Wet, Curb }

internal readonly record struct DriveAssistPedalStatus(int SpinWheelMask, double? GripEstimate, double GripConfidence,
    double? StoppingDistanceMeters, DriveAssistSurface Surface, int WetWheelMask, int CurbWheelMask,
    int TireTemperatureMask, double? RoadGrade, double? FrontRearGripDelta, double? ThrottleGain, double? BrakeGain,
    double BrakeSlipThreshold, bool HillHolding, bool CalibrationRolledBack, bool GripChanged);

internal sealed class DriveAssistPedals
{
    private const double Gravity = TelemetryDerived.Gravity;
    private const double DefaultBrakeSlip = 0.90;
    private readonly PedalResponse _throttleResponse = new();
    private readonly PedalResponse _brakeResponse = new();
    private TelemetrySample? _previous;
    private ShiftDirection? _previousShift;
    private DriveAssistSurface _surface, _nextSurface;
    private double _surfaceTime, _gripChangeTime, _grip, _gripEvidence;
    private double _grade, _gradeEvidence, _gradeAge;
    private double _throttleRamp, _boostRise, _landingRecovery, _shiftCutTime, _driftClutchTime;
    private double _hillTime, _hillBrake, _rollbackTime;
    private double _brakeSlip = DefaultBrakeSlip;

    public DriveAssistPedalStatus Status { get; private set; }
    public bool BlocksShift { get; private set; }

    public void Reset(bool vehicleChanged)
    {
        Status = default;
        BlocksShift = false;
        _previous = null;
        _previousShift = null;
        _surface = _nextSurface = DriveAssistSurface.Unknown;
        _surfaceTime = _gripChangeTime = _grade = _gradeEvidence = _gradeAge = 0;
        _throttleRamp = _boostRise = _landingRecovery = _shiftCutTime = _driftClutchTime = 0;
        _hillTime = _hillBrake = _rollbackTime = 0;
        _grip = _gripEvidence = 0;
        if (vehicleChanged)
        {
            _throttleResponse.Clear();
            _brakeResponse.Clear();
            _brakeSlip = DefaultBrakeSlip;
        }
        else
        {
            _throttleResponse.ResetTransient();
            _brakeResponse.ResetTransient();
        }
    }

    public void Suggest(TelemetrySample sample, DriveAssistDriver driver, DriveAssistState state, DriveAssistProfile profile,
        DriveAssistSettings settings, bool drifting, ShiftDirection? shifting, ref DriveAssistDemands demand)
    {
        var previous = _previous;
        _previous = sample;
        BlocksShift = false;
        var features = settings.EffectiveFeatures;
        var dt = state.Dt;
        if (state.Phase == DriveAssistPhase.Airborne)
        {
            _hillTime = _hillBrake = _shiftCutTime = _driftClutchTime = _boostRise = 0;
            _landingRecovery = 1;
            _previousShift = shifting;
            _throttleRamp = driver.Throttle;
            Status = Status with { HillHolding = false, SpinWheelMask = 0, StoppingDistanceMeters = null };
            return;
        }

        if (state.Phase == DriveAssistPhase.Landing)
            _landingRecovery = Math.Max(_landingRecovery, state.LandingStrength);
        else
            _landingRecovery = Math.Max(0, _landingRecovery - dt * 3.4);
        ObserveGrade(sample, previous, state);
        var roadGrade = _gradeEvidence >= 0.14 && _gradeAge < 3 ? _grade : (double?)null;
        var wheels = ObserveWheels(sample, state, settings.TireTempFahrenheit);
        UpdateGrip(state, wheels, driver);
        var learnVehicle = features.HasFlag(DriveAssistFeature.VehicleLearning);
        var learnThrottle = !settings.UseGameTractionControl && learnVehicle;
        var learnBrake = !settings.UseGameAbs && (learnVehicle || features.HasFlag(DriveAssistFeature.AdaptiveBraking));
        LearnResponses(sample, previous, state, roadGrade, driver, shifting, learnThrottle, learnBrake);

        var surfaceFactor = features.HasFlag(DriveAssistFeature.SurfaceProtection) ? _surface switch
        {
            DriveAssistSurface.Wet => 0.74,
            DriveAssistSurface.Loose => 0.83,
            DriveAssistSurface.Curb => 0.92,
            _ => 1,
        } : 1;
        var brakeThreshold = Math.Clamp(_brakeSlip * surfaceFactor - 0.11 * (1 - state.ThermalScale), 0.59, 1.10);
        if (!driver.Handbrake && !driver.Clutch)
        {
            var launching = demand.ThrottleCapOwner == DriveAssistFeature.Launch || demand.ClutchOwner == DriveAssistFeature.Launch;
            if (!launching && state.Phase != DriveAssistPhase.Landing)
            {
                if (features.HasFlag(DriveAssistFeature.PowerManagement) && shifting is null && !state.Rescuing)
                    SuggestPower(sample, previous, driver, state, learnThrottle, ref demand);
                else
                    _throttleRamp = driver.Throttle;
                if (!settings.UseGameTractionControl)
                {
                    var tractionOwner = features.HasFlag(DriveAssistFeature.AdaptiveTraction) ? DriveAssistFeature.AdaptiveTraction :
                        surfaceFactor < 1 ? DriveAssistFeature.SurfaceProtection : DriveAssistFeature.None;
                    if (tractionOwner != 0)
                        SuggestTraction(driver, state, profile, wheels, settings.TargetSlipPercent, surfaceFactor, roadGrade, drifting,
                            tractionOwner, ref demand);
                }
            }
            if (features.HasFlag(DriveAssistFeature.AdaptiveBraking))
                SuggestBrake(driver, state, wheels, brakeThreshold, roadGrade, settings.BrakeBiasPercent, ref demand);
            if (features.HasFlag(DriveAssistFeature.TransmissionAssist))
                CoordinateShift(sample, driver, state, drifting, shifting, ref demand);
            if (features.HasFlag(DriveAssistFeature.SpeedLimiter))
            {
                var projectedSpeed = sample.SpeedKmh + Math.Max(0, state.LongitudinalG) * Gravity * 0.22 * 3.6;
                var throttleLimit = Unit((settings.TargetSpeedKmh - projectedSpeed) / 4.5);
                if (throttleLimit < Math.Max(driver.Throttle, demand.Gas))
                    demand.LimitThrottle(throttleLimit, DriveAssistFeature.SpeedLimiter, cutRate: 18);
            }
        }
        else
        {
            _throttleRamp = driver.Throttle;
            _driftClutchTime = _shiftCutTime = _boostRise = 0;
        }

        _previousShift = shifting;
        var holding = features.HasFlag(DriveAssistFeature.HillStart) && SuggestHillHold(sample, driver, state, roadGrade, ref demand);
        var netDeceleration = -state.LongitudinalG * Gravity;
        var deceleration = driver.Brake >= 0.09 && netDeceleration > 0.8 ? netDeceleration :
            _brakeResponse.Gain is double brakeGain ? brakeGain * 0.70 * surfaceFactor + (roadGrade ?? 0) * Gravity : (double?)null;
        var stoppingDistance = state.Speed < 0.3 ? 0 :
            deceleration > 0.7 ? state.Speed * state.Speed / (2 * deceleration.Value) : (double?)null;
        Status = new(wheels.SpinMask, _gripEvidence > 0.06 ? _grip : null, Unit(_gripEvidence), stoppingDistance,
            _surface, wheels.WetMask, wheels.CurbMask, wheels.TemperatureMask, roadGrade,
            state.Speed > 4 ? wheels.AxleDifference : null, _throttleResponse.Gain, _brakeResponse.Gain,
            brakeThreshold, holding, _rollbackTime > 0, _gripChangeTime > 0);
    }

    private void ObserveGrade(TelemetrySample sample, TelemetrySample? previous, DriveAssistState state)
    {
        if (state.Speed >= 0.8 || previous is null ||
            Math.Abs(sample.PositionX - previous.PositionX) + Math.Abs(sample.PositionY - previous.PositionY) +
            Math.Abs(sample.PositionZ - previous.PositionZ) > 0.15)
            _gradeAge += state.Dt;
        if (state.Phase is DriveAssistPhase.Landing or DriveAssistPhase.Crest || state.Speed < 0.8 ||
            sample.VelocityZ < 0.6 || Math.Abs(state.Yaw) > 0.38) return;

        double? grade = null;
        if (previous is not null)
        {
            var dx = sample.PositionX - previous.PositionX;
            var dz = sample.PositionZ - previous.PositionZ;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            var dy = sample.PositionY - previous.PositionY;
            if (distance >= 0.012 && distance <= Math.Max(0.14, state.Speed * state.Dt * 2.2) &&
                Math.Abs(dy) <= distance * 0.36)
                grade = dy / distance;
        }
        if (!grade.HasValue && state.Speed >= 3 && Math.Abs(sample.VelocityY) <= state.Speed * 0.30)
            grade = sample.VelocityY / state.Speed;
        if (!grade.HasValue) return;
        _grade = _gradeEvidence == 0 ? grade.Value : _grade + (grade.Value - _grade) * Math.Min(1, state.Dt * 5);
        _gradeEvidence = Math.Min(1, _gradeEvidence + state.Dt);
        _gradeAge = 0;
    }

    private static WheelSignals ObserveWheels(TelemetrySample sample, DriveAssistState state, bool fahrenheit)
    {
        var spin = 0; var wet = 0; var curb = 0; var temperature = 0;
        var rough = 0.0; var front = 0.0; var rear = 0.0; var frontLock = 0.0; var rearLock = 0.0;
        for (var i = 0; i < 4; i++)
        {
            var wheel = sample.Wheel(i);
            var bit = 1 << i;
            if (wheel.PuddleDepth > 0.08) wet |= bit;
            if (wheel.OnRumbleStrip) curb |= bit;
            if (double.IsFinite(wheel.SurfaceRumble)) rough += Unit(Math.Abs(wheel.SurfaceRumble)) * 0.25;
            var celsius = fahrenheit ? (wheel.TireTempC - 32) * (5.0 / 9) : wheel.TireTempC;
            if (wheel.TireTempC > 0 && celsius is >= -25 and <= 185 && celsius is < 52 or > 110) temperature |= bit;
            var supported = wheel.NormalizedSuspensionTravel > 0.08;
            var driven = sample.DrivetrainType switch { 0 => i < 2, 1 => i >= 2, 2 => true, _ => false };
            if (driven && supported && wheel.SlipRatio >= 1.04 &&
                (state.Speed > 2 || Math.Abs(wheel.RotationSpeedRadPerSec) > 13)) spin |= bit;
            if (i < 2)
            {
                front = Math.Max(front, Math.Abs(wheel.CombinedSlip));
                if (supported) frontLock = Math.Max(frontLock, -wheel.SlipRatio);
            }
            else
            {
                rear = Math.Max(rear, Math.Abs(wheel.CombinedSlip));
                if (supported) rearLock = Math.Max(rearLock, -wheel.SlipRatio);
            }
        }
        // shortcut: rumble identifies loose/rough terrain, until Data Out exposes the actual material.
        var surface = wet != 0 ? DriveAssistSurface.Wet : curb != 0 ? DriveAssistSurface.Curb :
            rough >= 0.34 ? DriveAssistSurface.Loose : DriveAssistSurface.Road;
        // Both axle masks are retained in the result, but axle grip remains a demand-sensitive proxy.
        var axleDifference = Math.Clamp((rear - front) * 0.4 + ((wet & 12) != 0 ? 0.22 : 0) - ((wet & 3) != 0 ? 0.22 : 0), -1, 1);
        return new(spin, wet, curb, temperature, surface, axleDifference, frontLock, rearLock);
    }

    private void UpdateGrip(DriveAssistState state, WheelSignals wheels, DriveAssistDriver driver)
    {
        var dt = state.Dt;
        _gripChangeTime = Math.Max(0, _gripChangeTime - dt);
        if (_nextSurface != wheels.Surface)
        {
            _surfaceTime = 0;
            _nextSurface = wheels.Surface;
        }
        _surfaceTime += dt;
        if (_surface != _nextSurface && _surfaceTime >= (_nextSurface == DriveAssistSurface.Wet ? 0.10 : 0.18))
        {
            if (_surface != DriveAssistSurface.Unknown) _gripChangeTime = 0.8;
            _surface = _nextSurface;
            _gripEvidence *= 0.25;
        }

        var g = Math.Sqrt(state.LongitudinalG * state.LongitudinalG + state.LateralG * state.LateralG);
        var slip = Math.Max(state.DrivenCombined, Math.Max(state.FrontCombined, Math.Max(wheels.FrontLock, wheels.RearLock)));
        if (state.Speed > 4 && state.Phase is not (DriveAssistPhase.Landing or DriveAssistPhase.Crest) &&
            !driver.Handbrake && !driver.Clutch && g is > 0.24 and < 2.8 && slip is > 0.46 and < 1.36)
        {
            var estimate = Math.Clamp(g / Math.Clamp(slip, 0.62, 1), 0.22, 2.6);
            if (_gripEvidence >= 0.32 && Math.Abs(estimate - _grip) >= 0.35)
                _gripChangeTime = Math.Max(_gripChangeTime, 0.12);
            _grip = _gripEvidence < 0.04 ? estimate : _grip + (estimate - _grip) * Math.Min(1, dt * 2.6);
            _gripEvidence = Math.Min(1, _gripEvidence + dt * 0.76);
        }
        else _gripEvidence = Math.Max(0, _gripEvidence - dt * 0.055);
    }

    private void LearnResponses(TelemetrySample sample, TelemetrySample? previous, DriveAssistState state, double? grade,
        DriveAssistDriver driver, ShiftDirection? shifting, bool learnThrottle, bool learnBrake)
    {
        _rollbackTime = Math.Max(0, _rollbackTime - state.Dt);
        if ((!learnThrottle && !learnBrake) || previous is null || sample.Gear != previous.Gear || shifting is not null ||
            state.Speed < 5 || state.LateralG >= 0.38 || _landingRecovery > 0.05 ||
            state.Phase == DriveAssistPhase.Crest || driver.Handbrake || driver.Clutch || sample.Clutch > 0.12) return;
        var acceleration = (state.LongitudinalG + (grade ?? 0)) * Gravity;
        if (learnThrottle && sample.Throttle is > 0.12 and <= 1 && sample.Brake < 0.06 &&
            state.DrivenSlip < 0.56 && acceleration > 0.4 &&
            _throttleResponse.Observe(sample.Throttle, acceleration, state.Dt))
            _rollbackTime = 2;
        if (!learnBrake || sample.Brake is <= 0.12 or > 1 || sample.Throttle > 0.12 || -acceleration < 0.5) return;
        if (_brakeResponse.Observe(sample.Brake, -acceleration, state.Dt))
        {
            _rollbackTime = 2;
            _brakeSlip = DefaultBrakeSlip;
        }
        else if (state.BrakingSlip is > 0.40 and < 1.35)
        {
            var evidence = state.WheelLock > 0.05 || state.WheelDrop > 2.7 ? state.BrakingSlip * 0.83 :
                Math.Max(state.BrakingSlip, 0.65) * 1.10;
            _brakeSlip += (Math.Clamp(evidence, 0.62, 1.08) - _brakeSlip) * Math.Min(1, state.Dt * 0.85);
        }
    }

    private void SuggestPower(TelemetrySample sample, TelemetrySample? previous, DriveAssistDriver driver,
        DriveAssistState state, bool learnThrottle, ref DriveAssistDemands demand)
    {
        var speed = state.Speed;
        var rate = sample.Gear <= 2 ? 2.35 : speed > 40 ? 3.4 : 4.3;
        _throttleRamp = previous is null ? driver.Throttle : Math.Min(driver.Throttle, _throttleRamp + state.Dt * rate);
        _boostRise = Math.Max(0, _boostRise - state.Dt * 0.85);
        if (previous is not null && driver.Throttle > 0.35 && sample.BoostPsi is > 2 and < 80 &&
            previous.BoostPsi is >= 0 and < 80)
            _boostRise = Math.Max(_boostRise, 0.34 * Unit((sample.BoostPsi - previous.BoostPsi - 24 * state.Dt) / 6));
        var lowerGearCap = sample.Gear switch { 1 when speed > 1.8 => 0.83, 2 when speed > 4 => 0.93, _ => 1 };
        var cap = Math.Min(lowerGearCap, _throttleRamp) * (1 - _boostRise);
        if (learnThrottle)
            cap = Math.Min(cap, _throttleResponse.LinearizedInput(driver.Throttle));
        if (cap < driver.Throttle - 0.004)
            demand.LimitThrottle(cap, DriveAssistFeature.PowerManagement);
    }

    private void SuggestTraction(DriveAssistDriver driver, DriveAssistState state, DriveAssistProfile profile,
        WheelSignals wheels, int targetPercent, double surfaceFactor, double? grade, bool drifting, DriveAssistFeature owner,
        ref DriveAssistDemands demand)
    {
        if (driver.Brake > 0.13 || driver.Throttle <= 0.08 || (state.Speed < 1.4 && wheels.SpinMask == 0)) return;
        var slopePenalty = state.Speed < 8 && grade > 0.05 ? 0.87 : 1;
        var usableGrip = _gripEvidence > 0.50 ? Math.Clamp(_grip, 0.69, 1.05) : 1;
        var target = profile.TractionLimit * Math.Clamp(targetPercent, 50, 150) / 100 *
            state.ThermalScale * surfaceFactor * slopePenalty * usableGrip;
        if (drifting) target = Math.Max(1.40, target);
        var excess = state.DrivenSlip - target;
        if (wheels.SpinMask != 0 && state.Speed < 1.4) excess = Math.Max(excess, 0.27);
        if (excess > 0.015)
            demand.LimitThrottle(driver.Throttle * (1 - Math.Min(0.74, excess * 0.68)), owner,
                cutRate: wheels.SpinMask != 0 && excess > 0.7 ? 17 : 0);
    }

    private void SuggestBrake(DriveAssistDriver driver, DriveAssistState state, WheelSignals wheels, double threshold,
        double? grade, int brakeBias, ref DriveAssistDemands demand)
    {
        if (state.Speed < 2.1 || driver.Brake < 0.07) return;
        var frontBias = (Math.Clamp(brakeBias, 40, 80) - 60) / 100.0;
        var rearRisk = Unit((wheels.RearLock - wheels.FrontLock - 0.13 + frontBias) / 0.9) * Unit((wheels.RearLock - 0.73) / 0.45);
        var frontRisk = Unit((wheels.FrontLock - wheels.RearLock - 0.25 - frontBias) / 1.1) * Unit((wheels.FrontLock - 0.76) / 0.46);
        var yawRisk = Unit((Math.Abs(state.Yaw - state.ExpectedYaw) - 0.21) / 0.92);
        var loadRisk = Unit((driver.Brake - 0.30) / 0.40);
        var lockRisk = Unit((Math.Max(wheels.FrontLock, wheels.RearLock) - threshold) / 0.85);
        var cut = Math.Max(lockRisk * 0.74, loadRisk * (rearRisk * (0.31 + 0.26 * yawRisk) + frontRisk * 0.20));
        if (cut > 0.01)
            demand.LimitBrake(driver.Brake * (1 - cut), DriveAssistFeature.AdaptiveBraking, releaseRate: 14 + 11 * yawRisk);
        else if (demand.BrakeOwner == 0 && !state.Rescuing && _landingRecovery < 0.1 && driver.Brake is > 0.13 and < 0.86)
        {
            var compensation = Math.Clamp(-(grade ?? 0) * 0.34, -0.09, 0.09);
            var target = Unit(_brakeResponse.LinearizedInput(driver.Brake) + compensation);
            if (target < driver.Brake - 0.006) demand.LimitBrake(target, DriveAssistFeature.AdaptiveBraking);
            else if (target > driver.Brake + 0.006 && driver.Throttle < 0.08)
            {
                demand.Brake = target;
                demand.BrakeOwner = DriveAssistFeature.AdaptiveBraking;
            }
        }
        if (_landingRecovery > 0 && demand.BrakeCapOwner != 0 && demand.BrakeCap < driver.Brake)
            demand.BrakeCap += (driver.Brake - demand.BrakeCap) * _landingRecovery * 0.86;
    }

    private void CoordinateShift(TelemetrySample sample, DriveAssistDriver driver, DriveAssistState state, bool drifting,
        ShiftDirection? shifting, ref DriveAssistDemands demand)
    {
        var usableRpm = sample.EngineMaxRpm is > 1000 and <= 30000 &&
            sample.Rpm > Math.Max(sample.EngineIdleRpm * 1.6, sample.EngineMaxRpm * 0.25) &&
            sample.Rpm < sample.EngineMaxRpm * 0.92;
        var loose = _surface == DriveAssistSurface.Loose && (state.DrivenSlip > 0.38 || state.Bump > 0.30);
        BlocksShift = shifting is null && usableRpm && state.Speed > 3.5 && driver.Brake < 0.28 &&
            (drifting || loose && state.Speed < 26);
        if (shifting == ShiftDirection.Up && _previousShift != ShiftDirection.Up) _shiftCutTime = 0.14;
        if (shifting == ShiftDirection.Down) _shiftCutTime = 0;
        if (_shiftCutTime > 0 && driver.Throttle > 0.15)
            demand.LimitThrottle(driver.Throttle * (1 - 0.53 * Unit(_shiftCutTime / 0.09)),
                DriveAssistFeature.TransmissionAssist, cutRate: 24);
        _shiftCutTime = Math.Max(0, _shiftCutTime - state.Dt);

        if (!drifting || shifting is not null || state.LandingStrength > 0.1) _driftClutchTime = 0;
        else if (state.Downshift && state.RearBrakingSpike > 0.26 && state.RearBrakingSlip > 0.61)
            _driftClutchTime = 0.08;
        if (_driftClutchTime > 0 && demand.ClutchOwner == 0 && driver.Brake < 0.14)
        {
            demand.ClutchOwner = DriveAssistFeature.TransmissionAssist;
            demand.Throttle(driver.Throttle + 0.13 * Unit(_driftClutchTime / 0.08), 3, DriveAssistFeature.TransmissionAssist);
        }
        _driftClutchTime = Math.Max(0, _driftClutchTime - state.Dt);
    }

    private bool SuggestHillHold(TelemetrySample sample, DriveAssistDriver driver, DriveAssistState state, double? grade,
        ref DriveAssistDemands demand)
    {
        if (state.Speed > 0.85 || grade is not (> 0.052) || sample.Gear != 1 ||
            driver.Throttle > 0.16 || driver.Handbrake || driver.Clutch || state.Phase != DriveAssistPhase.Stopped)
        {
            _hillTime = _hillBrake = 0;
            return false;
        }
        if (driver.Brake >= 0.40)
        {
            _hillBrake = Math.Clamp(driver.Brake, 0.4, 0.68);
            _hillTime = 1.55;
            return false;
        }
        _hillTime = Math.Max(0, _hillTime - state.Dt);
        var brake = _hillBrake * Unit(_hillTime / 0.28) * Unit((0.16 - driver.Throttle) / 0.085);
        if (brake <= driver.Brake || demand.BrakeOwner != 0) return false;
        demand.Brake = brake;
        demand.BrakeOwner = DriveAssistFeature.HillStart;
        return true;
    }

    private readonly record struct WheelSignals(int SpinMask, int WetMask, int CurbMask, int TemperatureMask,
        DriveAssistSurface Surface, double AxleDifference, double FrontLock, double RearLock);

    private sealed class PedalResponse
    {
        private readonly double[] _gain = new double[3];
        private readonly double[] _evidence = new double[3];
        private readonly double[] _trusted = new double[3];
        private readonly double[] _contradiction = new double[3];
        private double _cooldown;

        public double? Gain
        {
            get
            {
                for (var band = _gain.Length - 1; band >= 0; band--)
                    if (_evidence[band] >= 0.20) return _gain[band];
                return null;
            }
        }

        public void ResetTransient()
        {
            Array.Clear(_contradiction);
            _cooldown = 0;
        }

        public void Clear()
        {
            ResetTransient();
            Array.Clear(_gain);
            Array.Clear(_evidence);
            Array.Clear(_trusted);
        }

        public bool Observe(double pedal, double acceleration, double dt)
        {
            _cooldown = Math.Max(0, _cooldown - dt);
            if (_cooldown > 0) return false;
            var band = Band(pedal);
            var observed = acceleration / pedal;
            if (observed is < 0.65 or > 24 || !double.IsFinite(observed)) return false;
            if (_trusted[band] > 0 && Math.Abs(observed / _trusted[band] - 1) > 0.57)
            {
                _contradiction[band] += dt;
                if (_contradiction[band] < 0.26) return false;
                _gain[band] = _trusted[band];
                _evidence[band] = 0.20;
                _trusted[band] = 0;
                _contradiction[band] = 0;
                _cooldown = 0.72;
                return true;
            }
            _contradiction[band] = Math.Max(0, _contradiction[band] - dt * 2);
            _gain[band] = _evidence[band] == 0 ? observed :
                _gain[band] + (observed - _gain[band]) * Math.Min(1, dt * 2.1);
            _evidence[band] = Math.Min(2, _evidence[band] + dt);
            if (_evidence[band] > 0.64) _trusted[band] = _gain[band];
            return false;
        }

        public double LinearizedInput(double pedal)
        {
            var band = Band(pedal);
            if (band == 2 || _cooldown > 0 || _evidence[band] < 0.32 || _evidence[2] < 0.32) return pedal;
            var correction = Math.Clamp((_gain[2] / _gain[band] - 1) * pedal, -0.11, 0.11);
            return Unit(pedal + correction * (1 - pedal));
        }

        private static int Band(double pedal) => pedal < 0.35 ? 0 : pedal < 0.68 ? 1 : 2;
    }
}
