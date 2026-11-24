using System;

namespace FH6Telemetry;

[Flags]
internal enum DriveAssistFeature : ulong
{
    None = 0,
    CounterSteer = 1 << 0, Understeer = 1 << 1, Damper = 1 << 2, DriftHold = 1 << 3, Hairpin = 1 << 4,
    Traction = 1 << 5, CornerTraction = 1 << 6, Launch = 1 << 7, Abs = 1 << 8, TrailBrake = 1 << 9, Slipper = 1 << 10,
    Landing = 1 << 11, SplitGrip = 1 << 12, Crest = 1 << 13, BottomOut = 1 << 14,
    Profiles = 1 << 15, Thermal = 1 << 16, Cruise = 1 << 17, Feedback = 1 << 18,
    Stability = 1UL << 19, SteeringTuning = 1UL << 20, KeyboardSteering = 1UL << 21,
    PowerManagement = 1UL << 22, AdaptiveTraction = 1UL << 23, AdaptiveBraking = 1UL << 24,
    TransmissionAssist = 1UL << 25, SurfaceProtection = 1UL << 26, SuspensionProtection = 1UL << 27,
    DriftTransitions = 1UL << 28, VehicleLearning = 1UL << 29, SpeedLimiter = 1UL << 30,
    HillStart = 1UL << 31, VibrationWarning = 1UL << 32, TightTurnSteering = 1UL << 33,
}

public enum DriveAssistAuthority { Manual, Assisted, Automatic }

internal sealed record DriveAssistSettings
{
    public const int FeatureCount = 34;
    public const ulong FeatureMask = (1UL << FeatureCount) - 1;
    public const double MinimumTightTurnSpeedKmh = 20;
    public const double MaximumTightTurnSpeedKmh = 90;
    public const double MaximumLearnedTightTurnSpeedKmh = 105;
    public const int DefaultTightTurnSpeedKmh = 54;
    private const DriveAssistFeature GameAbsConflicts = DriveAssistFeature.Abs | DriveAssistFeature.AdaptiveBraking |
        DriveAssistFeature.TrailBrake | DriveAssistFeature.HillStart | DriveAssistFeature.Cruise | DriveAssistFeature.Slipper;
    private const DriveAssistFeature GameTractionControlConflicts = DriveAssistFeature.Traction | DriveAssistFeature.AdaptiveTraction |
        DriveAssistFeature.Launch | DriveAssistFeature.Cruise | DriveAssistFeature.DriftHold | DriveAssistFeature.Hairpin |
        DriveAssistFeature.DriftTransitions | DriveAssistFeature.Slipper;

    public DriveAssistFeature Features { get; init; }
    public bool UseGameAbs { get; init; }
    public bool UseGameTractionControl { get; init; }
    public int TargetSpeedKmh { get; init; } = 65;
    public int LaunchRpmPercent { get; init; } = 55;
    public bool Skidpad { get; init; }
    public bool TireTempFahrenheit { get; init; }
    public ShiftShortcutButton HandbrakeButton { get; init; } = ShiftShortcutButton.A;
    public DriveAssistAuthority Authority { get; init; } = DriveAssistAuthority.Assisted;
    public bool ShadowMode { get; init; }
    public bool FairPlay { get; init; }
    public int SteeringDeadzonePercent { get; init; } = 5;
    public int SteeringDebounceMs { get; init; } = 35;
    public int TightTurnSpeedLimitKmh { get; init; } = DefaultTightTurnSpeedKmh;
    public bool LearnTightTurnSpeed { get; init; }
    public int TargetSlipPercent { get; init; } = 100;
    public int BrakeBiasPercent { get; init; } = 62;

    public bool ReadOnly => ShadowMode || FairPlay || Authority == DriveAssistAuthority.Manual;
    internal DriveAssistFeature EffectiveFeatures => Features &
        ~(UseGameAbs ? GameAbsConflicts : DriveAssistFeature.None) &
        ~(UseGameTractionControl ? GameTractionControlConflicts : DriveAssistFeature.None);

    public DriveAssistSettings Validated() => this with
    {
        Features = Features & (DriveAssistFeature)FeatureMask,
        TargetSpeedKmh = Math.Clamp(TargetSpeedKmh, 25, 160),
        LaunchRpmPercent = Math.Clamp(LaunchRpmPercent, 30, 80),
        HandbrakeButton = HandbrakeButton is ShiftShortcutButton.A or ShiftShortcutButton.Y or
            ShiftShortcutButton.LeftBumper or ShiftShortcutButton.RightBumper ? HandbrakeButton : ShiftShortcutButton.A,
        Authority = Enum.IsDefined(Authority) ? Authority : DriveAssistAuthority.Manual,
        SteeringDeadzonePercent = Math.Clamp(SteeringDeadzonePercent, 0, 30),
        SteeringDebounceMs = Math.Clamp(SteeringDebounceMs, 0, 200),
        TightTurnSpeedLimitKmh = (int)Math.Clamp(TightTurnSpeedLimitKmh, MinimumTightTurnSpeedKmh, MaximumTightTurnSpeedKmh),
        TargetSlipPercent = Math.Clamp(TargetSlipPercent, 50, 150),
        BrakeBiasPercent = Math.Clamp(BrakeBiasPercent, 45, 75),
    };
}

internal enum DriveAssistPhase { Stopped, Straight, Braking, Trail, Corner, CornerExit, Oversteer, Airborne, Landing, Crest }

internal readonly record struct DriveAssistProfile(double MassKg, double FrontWeight, double Wheelbase,
    double FrontLimit, double TractionLimit, double YawGain, double CenterDamper)
{
    public static DriveAssistProfile From(TelemetrySample s, bool adaptive)
    {
        var car = VehicleCatalog.Find(s.CarOrdinal);
        var mass = car?.CurbWeightKg is >= 550 and <= 4000 ? car.CurbWeightKg : 1450;
        var front = car?.WeightDistribution is >= 0.30 and <= 0.75 ? car.WeightDistribution : 0.50;
        var load = (front - 0.5) * 0.27 + Math.Clamp((mass - 1450) / 7000, -0.08, 0.12);
        // shortcut: use a stock-mass wheelbase estimate until measured wheelbase is available in the catalog.
        var wheelbase = Math.Clamp(2.62 + (mass - 1450) * 0.00038, 2.1, 3.3);
        if (!adaptive) return new(mass, front, wheelbase, 0.96, 0.91, 1, 1);
        return s.DrivetrainType switch
        {
            0 => new(mass, front, wheelbase, 0.91 - load, 0.93 - load, 0.94, 1.10),
            1 => new(mass, front, wheelbase, 0.99 - load, 0.86 + (0.5 - front) * 0.35, 1.18, 1),
            2 => new(mass, front, wheelbase, 0.93 - load, 0.94 - load * 0.45, 1.05, 1.16),
            _ => new(mass, front, wheelbase, 0.96 - load, 0.91, 1, 1),
        };
    }
}

internal readonly record struct DriveAssistState(double Dt, DriveAssistPhase Phase, double Speed, double Yaw,
    double YawAccel, double ExpectedYaw, double Beta, double LateralG, double LateralTrend, double LongitudinalG,
    double PitchRoll, double FrontAngle, double RearAngle, double FrontCombined, double DrivenCombined,
    double DrivenSlip, double BrakingSlip, double RearBrakingSlip, double RearBrakingSpike, double WheelDrop,
    double LeftRightSlip, double LeftRightSpeed, double SurfaceDifference, double Bump, double LandingStrength,
    double CrestStrength, double ThermalScale, double SteerRise, bool Downshift, bool Rescuing,
    bool DrivenWheelOverrun, double WheelLock);

internal sealed class DriveAssistEstimator
{
    private readonly double[] _rollingRadius = new double[4];
    private TelemetrySample? _previous;
    private double _yaw, _steer, _lateral, _verticalBaseline, _travelBaseline, _airSeconds, _landingAge;
    private double _yawDirection = 1, _yawEvidence, _cornerExitTime;
    private double _slideEvidence, _slideHold, _peakLateralG;
    private bool _knownContact, _airborne, _landing;

    public double EffectiveLateralGripG { get; private set; } = 1.25;

    internal static bool IsValid(TelemetrySample s, bool shiftingThroughNeutral = false)
    {
        var validGear = s.Gear is >= 1 and <= AutomaticShiftSettings.MaximumForwardGears ||
            shiftingThroughNeutral && s.Gear is 11 or byte.MaxValue;
        if (s.CarOrdinal <= 0 || !s.IsRaceOn || !validGear ||
            !InRange(s.SpeedKmh, 740) || s.SpeedKmh < 0 || !InRange(s.VelocityX, 210) || !InRange(s.VelocityZ, 210) ||
            !InRange(s.AngularVelocityY, 45) || !InRange(s.AngularVelocityX, 45) || !InRange(s.AngularVelocityZ, 45) ||
            !InRange(s.AccelerationX, 250) || !InRange(s.AccelerationY, 250) || !InRange(s.AccelerationZ, 250) ||
            !(s.HandBrake is >= 0 and <= 1)) return false;

        for (var i = 0; i < 4; i++)
        {
            var wheel = s.Wheel(i);
            if (!InRange(wheel.SlipRatio, 250) || !InRange(wheel.SlipAngle, 250) || !InRange(wheel.CombinedSlip, 350) ||
                !InRange(wheel.RotationSpeedRadPerSec, 5000) || !InRange(wheel.NormalizedSuspensionTravel, 2)) return false;
        }
        return true;
    }

    public void Reset(bool vehicleChanged)
    {
        _previous = null;
        _yaw = _steer = _lateral = _verticalBaseline = _travelBaseline = _airSeconds = _landingAge = _cornerExitTime = 0;
        _slideEvidence = _slideHold = 0;
        _knownContact = _airborne = _landing = false;
        if (!vehicleChanged) return;
        Array.Clear(_rollingRadius);
        _yawDirection = 1;
        _yawEvidence = 0;
        _peakLateralG = 0;
        EffectiveLateralGripG = 1.25;
    }

    public DriveAssistState Update(TelemetrySample sample, DriveAssistDriver driver, DriveAssistProfile profile, double dt,
        bool thermal, bool tireTempFahrenheit, bool allowLearning = true)
    {
        var old = _previous;
        _previous = sample;
        var speed = sample.SpeedKmh / 3.6;
        var beta = Math.Atan2(sample.VelocityX, Math.Max(1.5, sample.VelocityZ));
        var lateral = Math.Abs(sample.AccelerationX / TelemetryDerived.Gravity);
        var longitudinal = sample.AccelerationZ / TelemetryDerived.Gravity;
        var vertical = sample.AccelerationY / TelemetryDerived.Gravity;
        var pitchRoll = Math.Abs(sample.AngularVelocityX) + Math.Abs(sample.AngularVelocityZ);
        var frontAngle = Math.Max(Math.Abs(sample.FrontLeft.SlipAngle), Math.Abs(sample.FrontRight.SlipAngle));
        var rearAngle = Math.Max(Math.Abs(sample.RearLeft.SlipAngle), Math.Abs(sample.RearRight.SlipAngle));
        var insideRearAngle = Math.Min(Math.Abs(sample.RearLeft.SlipAngle), Math.Abs(sample.RearRight.SlipAngle));
        var frontCombined = Math.Max(Math.Abs(sample.FrontLeft.CombinedSlip), Math.Abs(sample.FrontRight.CombinedSlip));
        var rawYaw = sample.AngularVelocityY;
        var measuredSteer = double.IsFinite(sample.Steer) && Math.Abs(sample.Steer) > 0.18 ? sample.Steer : driver.Steer;
        if (allowLearning && speed > 7 && Math.Abs(measuredSteer) > 0.18 && Math.Abs(rawYaw) is > 0.13 and < 1.1 &&
            Math.Abs(beta) < 0.12 && frontAngle < 0.85 && rearAngle < 0.75 && lateral > 0.24 && driver.Brake < 0.15)
        {
            _yawEvidence = Math.Clamp(_yawEvidence + Math.Sign(measuredSteer * rawYaw) * dt, -0.4, 0.4);
            if (Math.Abs(_yawEvidence) >= 0.25) _yawDirection = Math.Sign(_yawEvidence);
        }

        var previousYaw = _yaw;
        var previousLateral = _lateral;
        var blend = 1 - Math.Exp(-dt / 0.055);
        _yaw = old is null ? rawYaw : _yaw + blend * (rawYaw - _yaw);
        _lateral = old is null ? lateral : _lateral + blend * (lateral - _lateral);
        var yaw = _yaw * _yawDirection;
        var yawAccel = old is null ? 0 : Math.Clamp((_yaw - previousYaw) * _yawDirection / dt, -24, 24);
        var lateralTrend = old is null ? 0 : (_lateral - previousLateral) / dt;
        var steerRise = old is null ? 0 : (Math.Abs(driver.Steer) - _steer) / dt;
        _steer = Math.Abs(driver.Steer);

        var maxTravel = 0.0;
        var meanTravel = 0.0;
        var maxBump = 0.0;
        var leftSurface = 0.0; var rightSurface = 0.0;
        var leftWheels = 0.0; var rightWheels = 0.0;
        var slowestWheel = double.PositiveInfinity; var fastestWheel = 0.0;
        var leftSlip = 0.0; var rightSlip = 0.0;
        var drivenSlip = 0.0; var drivenCombined = 0.0;
        var brakingSlip = 0.0; var rearBrake = 0.0; var rearSpike = 0.0; var wheelDrop = 0.0;
        var drivenWheelOverrun = false;
        var wheelLock = 0.0;
        var thermalScale = 1.0;
        for (var i = 0; i < 4; i++)
        {
            var w = sample.Wheel(i);
            var combined = Math.Abs(w.CombinedSlip);
            var wheelSpeed = Math.Abs(w.RotationSpeedRadPerSec);
            var left = i % 2 == 0;
            var rumble = double.IsFinite(w.SurfaceRumble) ? Unit(Math.Abs(w.SurfaceRumble)) : 0;
            var water = double.IsFinite(w.PuddleDepth) ? Unit(w.PuddleDepth) : 0;
            var surface = Math.Max(water, w.OnRumbleStrip ? 0.75 : rumble);
            if (left) { leftSurface = Math.Max(leftSurface, surface); leftWheels += wheelSpeed; leftSlip += w.SlipRatio; }
            else { rightSurface = Math.Max(rightSurface, surface); rightWheels += wheelSpeed; rightSlip += w.SlipRatio; }
            maxBump = Math.Max(maxBump, rumble);
            maxTravel = Math.Max(maxTravel, w.NormalizedSuspensionTravel);
            meanTravel += w.NormalizedSuspensionTravel * 0.25;

            // FH6 reports normalized tire slip. Learn a rolling radius before estimating a physical slip fraction.
            if (allowLearning && speed > 10 && wheelSpeed > 10 && Math.Abs(w.SlipRatio) < 0.20 && combined < 0.45 &&
                w.NormalizedSuspensionTravel > 0.09 && Math.Abs(yaw) < 0.12 && driver.Brake < 0.08 && driver.Throttle < 0.40)
            {
                var radius = speed / wheelSpeed;
                if (radius is > 0.10 and < 0.67)
                    _rollingRadius[i] = _rollingRadius[i] == 0 ? radius : _rollingRadius[i] + dt * (radius - _rollingRadius[i]);
            }
            var calibrated = _rollingRadius[i] > 0;
            var treadSpeed = wheelSpeed * (calibrated ? _rollingRadius[i] : 0.33);
            slowestWheel = Math.Min(slowestWheel, treadSpeed);
            fastestWheel = Math.Max(fastestWheel, treadSpeed);
            var longitudinalSlip = w.SlipRatio;
            var groundSpeed = Math.Max(3, sample.VelocityZ + (left ? 0.8 : -0.8) * yaw);
            if (calibrated && speed > 6 && Math.Abs(longitudinalSlip) > 0.20 && sample.VelocityZ > 0)
            {
                var physicalSlip = (treadSpeed - groundSpeed) / groundSpeed;
                if (Math.Sign(physicalSlip) == Math.Sign(longitudinalSlip))
                    longitudinalSlip = Math.Max(Math.Abs(longitudinalSlip), Math.Min(2, Math.Abs(physicalSlip) * (0.91 / 0.12))) * Math.Sign(longitudinalSlip);
            }
            if (sample.DrivetrainType switch { 0 => i < 2, 1 => i >= 2, _ => true })
            {
                drivenSlip = Math.Max(drivenSlip, longitudinalSlip);
                drivenCombined = Math.Max(drivenCombined, combined);
                // A speed difference still makes sense near a standstill, where a slip percentage would not.
                var conservativeTreadSpeed = calibrated ? treadSpeed : wheelSpeed * 0.17;
                drivenWheelOverrun |= w.SlipRatio > 1.05 && conservativeTreadSpeed - speed > Math.Max(1.2, speed * 0.16);
            }
            brakingSlip = Math.Max(brakingSlip, -longitudinalSlip);
            if (speed > 2.1 && sample.VelocityZ > 1.5 && w.SlipRatio < -0.95 && w.NormalizedSuspensionTravel > 0.08)
            {
                var rotationLoss = calibrated ? Unit((0.72 - treadSpeed / groundSpeed) / 0.62) : Unit((6 - wheelSpeed) / 6);
                wheelLock = Math.Max(wheelLock, rotationLoss * Unit((-w.SlipRatio - 0.95) / 0.65));
            }
            if (i >= 2) rearBrake = Math.Max(rearBrake, -w.SlipRatio);
            if (old is not null)
            {
                var previousWheel = old.Wheel(i);
                var previousRotation = Math.Abs(previousWheel.RotationSpeedRadPerSec);
                if (i >= 2) rearSpike = Math.Max(rearSpike, previousWheel.SlipRatio - w.SlipRatio);
                if (w.SlipRatio < -0.35 && previousRotation > 8)
                    wheelDrop = Math.Max(wheelDrop, (previousRotation - wheelSpeed) / (dt * previousRotation) -
                        Math.Max(0, (old.SpeedKmh - sample.SpeedKmh) / (dt * Math.Max(12, old.SpeedKmh))));
            }
            if (thermal && w.TireTempC > 0)
            {
                var celsius = tireTempFahrenheit ? (w.TireTempC - 32) * (5.0 / 9) : w.TireTempC;
                if (celsius is >= -25 and <= 185)
                {
                    var outsideBand = Math.Max(0, Math.Max(66 - celsius, celsius - 102));
                    thermalScale = Math.Min(thermalScale, 1 - 0.30 * Unit(outsideBand / 30));
                }
            }
        }

        var wheelImbalance = Math.Abs(leftWheels - rightWheels) / Math.Max(10, Math.Max(leftWheels, rightWheels));
        if (maxTravel > 0.12) _knownContact = true;
        if (speed > 5 && _knownContact && maxTravel <= 0.065)
        {
            _airSeconds += dt;
            if (_airSeconds >= 0.032 || vertical < _verticalBaseline - 0.70) _airborne = true;
        }
        else if (maxTravel > 0.11 || speed <= 5)
        {
            if (_airborne && speed > 5) { _landing = true; _landingAge = 0; }
            _airborne = false;
            _airSeconds = 0;
        }
        var landingStrength = _airborne ? 1.0 : 0.0;
        if (_landing && !_airborne)
        {
            var wheelSpeedSpread = (fastestWheel - slowestWheel) / Math.Max(4, fastestWheel);
            var unsettled = vertical > _verticalBaseline + 0.65 || pitchRoll > 0.7 || wheelSpeedSpread > 0.23 || brakingSlip > 1.3;
            if (_landingAge >= 0.40) _landing = false;
            else landingStrength = _landingAge < 0.20 || unsettled ? 1 : Unit((0.40 - _landingAge) / 0.20);
            _landingAge += dt;
        }

        if (old is null) { _verticalBaseline = vertical; _travelBaseline = meanTravel; }
        var unloading = _knownContact && !_airborne && !_landing && speed > 23 && meanTravel < _travelBaseline - 0.07
            ? Unit((_verticalBaseline - vertical - 0.26) / 0.45) : 0;
        if (!_airborne && !_landing && unloading == 0 && maxTravel > 0.12)
        {
            _verticalBaseline += Math.Min(1, dt * 2) * (vertical - _verticalBaseline);
            _travelBaseline += Math.Min(1, dt * 2) * (meanTravel - _travelBaseline);
        }

        var piGrip = sample.CarPerformanceIndex > 0
            ? Math.Clamp(0.95 + Math.Max(0, sample.CarPerformanceIndex - 500) * 0.0011 + Math.Max(0, sample.CarPerformanceIndex - 750) * 0.0022, 0.95, 2.05)
            : 1.18;
        if (allowLearning && !_airborne && !_landing && speed > 10 && maxTravel > 0.12 &&
            lateral is > 0.45 and < 2.65 && Math.Abs(beta) < 0.24 && rearAngle < 1.05 && Math.Abs(yaw) > 0.12)
        {
            _peakLateralG = _peakLateralG <= 0
                ? lateral
                : lateral > _peakLateralG
                    ? _peakLateralG + Math.Min(1, dt * 1.4) * (lateral - _peakLateralG)
                    : _peakLateralG + dt * 0.02 * (lateral - _peakLateralG);
        }
        EffectiveLateralGripG = Math.Clamp(
            Math.Max(piGrip, _peakLateralG > 0 ? Math.Max(_peakLateralG * 0.96, piGrip) : piGrip) * thermalScale,
            0.90, 2.50);

        var gripScale = Math.Sqrt(Math.Clamp(EffectiveLateralGripG / 1.25, 0.72, 2.0));
        var steerAngle = driver.Steer * 0.54;
        var understeerSpeed = 13.0 * gripScale - (profile.FrontWeight - 0.5) * 3;
        var expected = speed * Math.Tan(steerAngle) / (profile.Wheelbase * (1 + Math.Pow(speed / understeerSpeed, 2)));
        var maximumYaw = Math.Max(1.25, EffectiveLateralGripG * 1.12) * TelemetryDerived.Gravity / Math.Max(5, speed);
        expected = Math.Clamp(expected, -maximumYaw, maximumYaw);

        var bump = Math.Max(Unit((maxBump - 0.58) / 0.35), Unit((maxTravel - 0.92) / 0.10));
        var rescuing = !_airborne && !_landing && speed > 4.8 && sample.VelocityZ > 0.4 &&
            ObserveBodySlide(beta, old, yaw, expected, insideRearAngle, rearAngle, frontAngle, driver.Steer, drivenWheelOverrun, dt);
        if (_airborne || _landing || speed <= 4.8 || sample.VelocityZ <= 0.4) _slideEvidence = _slideHold = 0;
        if (lateral > 0.42 || driver.Brake > 0.14) _cornerExitTime = 0.6;
        else _cornerExitTime = Math.Max(0, _cornerExitTime - dt);
        var phase = _airborne ? DriveAssistPhase.Airborne : _landing ? DriveAssistPhase.Landing : unloading > 0.12 ? DriveAssistPhase.Crest :
            speed < 1.4 ? DriveAssistPhase.Stopped : rescuing ? DriveAssistPhase.Oversteer :
            driver.Brake > 0.10 ? (Math.Abs(driver.Steer) > 0.15 ? DriveAssistPhase.Trail : DriveAssistPhase.Braking) :
            driver.Throttle > 0.25 && _cornerExitTime > 0 && (lateralTrend < -0.18 || Math.Abs(driver.Steer) < 0.35)
                ? DriveAssistPhase.CornerExit : lateral > 0.35 || Math.Abs(driver.Steer) > 0.25 ? DriveAssistPhase.Corner : DriveAssistPhase.Straight;
        return new(dt, phase, speed, yaw, yawAccel, expected, beta, _lateral, lateralTrend, longitudinal, pitchRoll,
            frontAngle, rearAngle, frontCombined, drivenCombined, drivenSlip, brakingSlip, rearBrake, rearSpike, wheelDrop,
            Math.Abs(leftSlip - rightSlip) * 0.5, wheelImbalance, Math.Abs(leftSurface - rightSurface), bump,
            landingStrength, unloading, thermalScale, steerRise,
            old is not null && old.Gear <= AutomaticShiftSettings.MaximumForwardGears && old.Gear > sample.Gear,
            rescuing, drivenWheelOverrun, wheelLock);
    }

    private bool ObserveBodySlide(double beta, TelemetrySample? previous, double yaw, double expected,
        double insideRearAngle, double rearAngle, double frontAngle, double steer, bool drivenOverrun, double dt)
    {
        var angle = Math.Abs(beta);
        var previousAngle = previous is null ? angle : Math.Abs(Math.Atan2(previous.VelocityX, Math.Max(1.5, previous.VelocityZ)));
        var growing = angle - previousAngle > 0.35 * dt;
        var rotationAway = yaw * beta < 0 && Math.Abs(yaw) >= 0.20;
        var rearMean = (rearAngle + insideRearAngle) * 0.5;
        var committedTurnIn = Math.Abs(steer) > 0.30 && Math.Abs(yaw) >= 0.08 && Math.Sign(yaw) == Math.Sign(steer);
        var frontPushing = committedTurnIn && frontAngle >= rearMean * 0.88 && angle < 0.30 && !drivenOverrun;

        // Body motion stays observable when the driver countersteers or the front tires saturate too.
        var clearSlide = !frontPushing && angle >= 0.22 && rearMean > 0.73 && insideRearAngle > 0.38 &&
            (rotationAway || growing || angle >= 0.29);
        var developing = !frontPushing && angle > 0.105 && insideRearAngle > 0.47 && rearMean > 0.71 &&
            (rearMean > frontAngle * 0.77 || angle >= 0.15) &&
            (growing && rotationAway || rotationAway && Math.Abs(yaw - expected) > 0.24);
        _slideEvidence = developing ? Math.Min(0.15, _slideEvidence + dt) : Math.Max(0, _slideEvidence - (frontPushing ? 5 * dt : 2 * dt));
        var remainsSliding = !frontPushing && _slideHold > 0 && angle > 0.13 && rearMean > 0.50;
        _slideHold = clearSlide || _slideEvidence >= 0.045 || remainsSliding ? 0.15 : Math.Max(0, _slideHold - (frontPushing ? 5 * dt : dt));
        return !frontPushing && _slideHold > 0;
    }

    internal static double Unit(double value) => Math.Clamp(value, 0, 1);
    private static bool InRange(double value, double limit) => Math.Abs(value) <= limit;
}
