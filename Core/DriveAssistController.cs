using System;
using static FH6Telemetry.DriveAssistEstimator;

namespace FH6Telemetry;

internal sealed class DriveAssistController(DriveAssistSettings initialSettings)
{
    internal const int FreshnessMs = 100;
    private const int HandbrakeOverrideMs = 520;
    private const int NeutralTransitionMs = 250;
    private const int ClutchLoadSettleMs = 45;
    private const int SessionSettleMs = 500;
    private const DriveAssistFeature BenignCalibrationFlags =
        DriveAssistFeature.SteeringTuning | DriveAssistFeature.Damper | DriveAssistFeature.Profiles |
        DriveAssistFeature.Thermal | DriveAssistFeature.Feedback | DriveAssistFeature.TightTurnSteering;
    private readonly DriveAssistEstimator _estimator = new();
    private readonly DriveAssistArbitrator _arbitrator = new();
    private readonly DriveAssistCruise _cruise = new();
    private readonly DriveAssistLateral _lateral = new();
    private readonly DriveAssistPedals _pedals = new();
    private readonly DriveAssistTightTurnLearning _tightTurnLearning = new();
    private readonly CollisionSlowdownDetector _collision = new();
    private DriveAssistSettings _settings = initialSettings.Validated();
    private DriveAssistProfile _profile;
    private DriveAssistDecision _decision;
    private DriveAssistState _state;
    private CollisionSlowdownState _collisionState;
    private TelemetrySample? _pending;
    private TelemetrySample? _lastDrivingSample;
    private DriveAssistAccumulator? _segment;
    private long? _handbrakeAt;
    private long _arrivedAt;
    private long? _lastForwardGearAt;
    private long _tractionRecoveryAt;
    private long _sessionSettleUntil;
    private uint? _timestamp;
    private int _car, _pi, _drivetrain;
    private double _previousThrottle, _previousSteer, _frontPressure;
    private double _tightTurnSteerTime;
    private double _tractionIntegral, _tractionError, _tractionDerivative, _tractionCut;
    private double _brakeCut, _splitTime, _splitSteer, _splitCooldown;
    private double _launchHold, _launchAge = -1, _launchBoost, _launchIntegral;
    private double _hairpinAngle, _hairpinExit, _downshiftWindow, _slipperTime;
    private bool _hairpinReleased, _hairpinVerified, _hasDecision, _hasDriver, _launchStaged, _driftSuppressed;

    public bool Enabled => _settings.EffectiveFeatures != 0;
    public bool KeyboardSteering => Has(DriveAssistFeature.KeyboardSteering);
    public bool WarnWithVibration => Has(DriveAssistFeature.VibrationWarning);
    public DriveAssistLateralStatus LateralStatus => _lateral.Status;
    public DriveAssistPedalStatus PedalStatus => _pedals.Status;
    public bool BlocksShift { get; private set; }
    public DriveAssistPhase Phase => _state.Phase;
    public DriveAssistFeature Active { get; private set; }
    public DriveAssistFrame? LastFrame { get; private set; }
    public DriveAssistRun? Segment => _segment?.Snapshot();
    public DriveAssistRun? PreviousLap { get; private set; }
    public DriveAssistRun? CruiseResult => _cruise.Result;
    public bool CruiseActive => _cruise.IsActive;
    public bool CruisePending(long now) => _cruise.IsPending(now);
    public bool HasFreshData(long now) => _timestamp.HasValue && now - _arrivedAt < FreshnessMs;

    private bool Has(DriveAssistFeature feature) => (_settings.EffectiveFeatures & feature) != 0;
    private int? LearnedTightTurnSpeedKmh => _settings.LearnTightTurnSpeed && Has(DriveAssistFeature.TightTurnSteering)
        ? _tightTurnLearning.SpeedLimitKmh : null;

    public void Configure(DriveAssistSettings settings)
    {
        settings = settings.Validated();
        if (_settings == settings) return;
        _settings = settings;
        _car = 0;
        _driftSuppressed = false;
        _sessionSettleUntil = 0;
        Suspend(cancelCruiseRequest: true);
    }

    public void ResetLog() { _segment = null; PreviousLap = null; }

    public DriveAssistStatus Snapshot(long now, bool armed, bool includeReports) =>
        new(armed, HasFreshData(now), Phase, Active, CruisePending(now), CruiseActive,
            includeReports ? Segment : null, includeReports ? PreviousLap : null, includeReports ? CruiseResult : null,
            LateralStatus, PedalStatus, _collisionState, LearnedTightTurnSpeedKmh);

    public void RequestDriftExit()
    {
        _driftSuppressed = true;
        _lateral.RequestDriftExit();
        ClearDecision();
    }

    public void ResetCalibration()
    {
        Suspend(cancelCruiseRequest: true);
        _estimator.Reset(vehicleChanged: true);
        _lateral.Reset(vehicleChanged: true);
        _pedals.Reset(vehicleChanged: true);
        _tightTurnLearning.Reset();
    }

    public void RequestCruise(long now)
    {
        if (Has(DriveAssistFeature.Cruise)) _cruise.Request(now);
    }

    public void CancelCruise()
    {
        _cruise.Cancel();
        ClearDecision();
    }

    public void Suspend(bool cancelCruiseRequest = false, bool preserveCollision = false)
    {
        ClearDecision();
        _timestamp = null;
        _lastDrivingSample = null;
        _lastForwardGearAt = null;
        _tractionRecoveryAt = 0;
        _state = default;
        if (!preserveCollision)
        {
            _collision.Reset();
            _collisionState = default;
        }
        _estimator.Reset(vehicleChanged: false);
        _lateral.Reset(vehicleChanged: false);
        _pedals.Reset(vehicleChanged: false);
        _cruise.Cancel(cancelCruiseRequest);
        _handbrakeAt = null;
        _frontPressure = _tractionIntegral = _tractionError = _tractionDerivative = _tractionCut = _brakeCut = 0;
        _tightTurnSteerTime = 0;
        _tightTurnLearning.InterruptTurn();
        _splitTime = _splitSteer = _splitCooldown = _launchHold = _launchBoost = _launchIntegral = 0;
        _hairpinAngle = _hairpinExit = _downshiftWindow = _slipperTime = 0;
        _launchAge = -1;
        _hairpinReleased = _hairpinVerified = _hasDriver = _launchStaged = false;
    }

    private void ClearDecision()
    {
        _arbitrator.Reset();
        _hasDecision = BlocksShift = false;
        Active = 0;
        LastFrame = null;
        _pending = null;
    }

    public void Feed(TelemetrySample sample, ShiftGamepadReport physical, long now, bool shifting = false,
        ShiftDirection? shiftDirection = null)
    {
        LastFrame = null;
        if (!Enabled) return;
        var sameVehicle = _car == sample.CarOrdinal && _pi == sample.CarPerformanceIndex && _drivetrain == sample.DrivetrainType;
        var shiftingThroughNeutral = sample.Gear is 11 or byte.MaxValue && sample.SpeedKmh > 5 && sample.VelocityZ > 1 &&
            _lastForwardGearAt is long forwardAt && now - forwardAt <= NeutralTransitionMs && sameVehicle;
        if (!IsValid(sample, shiftingThroughNeutral))
        {
            var gearTransition = sameVehicle && sample.IsRaceOn && !sample.IsPreRaceGrid && sample.Gear is 0 or 11 or byte.MaxValue;
            if (gearTransition)
                _collisionState = _collision.Observe(sample, new ShiftPedals(physical.RightTrigger / 255.0, physical.LeftTrigger / 255.0), now, shifting: true);
            Suspend(cancelCruiseRequest: sample.IsRaceOn && sample.Gear == 0, preserveCollision: gearTransition);
            return;
        }

        if (!sameVehicle)
        {
            Suspend(cancelCruiseRequest: _car != 0);
            _estimator.Reset(vehicleChanged: true);
            _lateral.Reset(vehicleChanged: true);
            _pedals.Reset(vehicleChanged: true);
            _tightTurnLearning.UseVehicle(sample);
            _car = sample.CarOrdinal;
            _pi = sample.CarPerformanceIndex;
            _drivetrain = sample.DrivetrainType;
            _profile = DriveAssistProfile.From(sample, Has(DriveAssistFeature.Profiles));
        }

        var elapsed = _timestamp is uint last ? unchecked((int)(sample.TimestampMs - last)) : 16;
        if (_timestamp.HasValue && elapsed is <= 0 and > -1000) return;
        if (_lastDrivingSample is { } previous && sample.StartsNewDrivingSegment(previous, now - _arrivedAt))
        {
            _sessionSettleUntil = now + SessionSettleMs;
            Suspend(cancelCruiseRequest: true);
            return;
        }
        if (sample.IsPreRaceGrid || now < _sessionSettleUntil)
        {
            if (_timestamp.HasValue || _cruise.IsPending(now)) Suspend(cancelCruiseRequest: true);
            return;
        }
        if (elapsed is < 0 or > FreshnessMs || _timestamp.HasValue && !HasFreshData(now))
        {
            Suspend();
            if (shiftingThroughNeutral) return;
            elapsed = 16;
        }
        if (!shiftingThroughNeutral) _lastForwardGearAt = now;
        var contiguous = _timestamp.HasValue;
        _timestamp = sample.TimestampMs;
        _arrivedAt = now;
        _lastDrivingSample = sample;
        var driver = DriveAssistDriver.Read(physical, (ushort)_settings.HandbrakeButton);
        var wasHolding = _collisionState.Holding;
        _collisionState = _collision.Observe(sample, new ShiftPedals(driver.Throttle, driver.Brake), now,
            shifting || shiftingThroughNeutral);
        var collisionHold = _collisionState.Holding;
        if (collisionHold)
        {
            _cruise.Cancel();
            ResetTraction();
            _launchHold = 0;
            _launchAge = -1;
            _launchStaged = false;
            _slipperTime = _downshiftWindow = 0;
            if (!wasHolding) _pedals.Reset(vehicleChanged: false);
        }
        // A free-spinning driveline can look like recovered grip for several frames before torque reaches the wheels.
        if (shifting || shiftingThroughNeutral || sample.Clutch >= 0.08) _tractionRecoveryAt = now + ClutchLoadSettleMs;
        var previousPhase = _state.Phase;
        _state = _estimator.Update(sample, driver, _profile, elapsed / 1000.0, Has(DriveAssistFeature.Thermal),
            _settings.TireTempFahrenheit, allowLearning: !collisionHold);
        var airborne = _state.Phase == DriveAssistPhase.Airborne;
        if (airborne)
        {
            ResetTraction();
            _brakeCut = _slipperTime = _downshiftWindow = 0;
            if (previousPhase != DriveAssistPhase.Airborne) _arbitrator.Reset();
        }
        if (!_hasDriver)
        {
            _previousThrottle = driver.Throttle;
            _previousSteer = driver.Steer;
            _hasDriver = true;
        }
        var hasHandbrake = driver.Handbrake || sample.HandBrake > 0.25;
        _handbrakeAt = hasHandbrake ? _handbrakeAt ?? now : null;
        var manualHandbrake = _handbrakeAt is long held && now - held >= HandbrakeOverrideMs;
        var demand = new DriveAssistDemands
        {
            PreserveDriverSteering = Has(DriveAssistFeature.TightTurnSteering) &&
                ShouldPreserveTightTurnSteering(sample, driver,
                    controlsAvailable: !collisionHold && !hasHandbrake && !shiftingThroughNeutral,
                    shifting: shifting || shiftDirection is not null),
        };
        var recovering = false;
        if (manualHandbrake || hasHandbrake && !Has(DriveAssistFeature.Hairpin) ||
            hasHandbrake && !driver.Handbrake && !_hairpinReleased)
        {
            CancelCruise();
            _launchHold = 0;
            _launchAge = -1;
            _launchStaged = false;
            _hairpinExit = 0;
        }
        else
        {
            if (!collisionHold && !shiftingThroughNeutral && Has(DriveAssistFeature.Cruise)) _cruise.Suggest(sample, _state, driver, _settings, now, ref demand);
            var hairpin = !collisionHold && !airborne && !shiftingThroughNeutral && !_lateral.DriftExiting && SuggestHairpin(sample, driver, ref demand);
            var drift = !collisionHold && !airborne && !shiftingThroughNeutral && !hairpin && !_lateral.DriftExiting &&
                !_driftSuppressed && SuggestDrift(driver, ref demand);
            if (!hasHandbrake || hairpin)
            {
                var launching = false;
                if (!shiftingThroughNeutral)
                {
                    SuggestSteering(sample, driver, collisionHold, ref demand);
                    SuggestSurface(driver, ref demand);
                    var manualShift = (physical.Buttons & ShiftGamepadReport.ManualShiftButtons) != 0;
                    launching = !collisionHold && !hasHandbrake && !shifting && !manualShift && SuggestLaunch(sample, driver, ref demand);
                }
                if (!airborne)
                {
                    if (!hasHandbrake && !hairpin && !drift && !_lateral.DriftExiting)
                        recovering = SuggestRotationRecovery(driver, collisionHold, ref demand);
                    if (!collisionHold && !launching)
                    {
                        if (!_settings.UseGameTractionControl) SuggestTraction(sample, driver, drift, ref demand);
                        else if (!hasHandbrake && !shifting && !shiftingThroughNeutral && shiftDirection is null && sample.Clutch < 0.08)
                            SuggestCornerThrottle(sample, driver, ref demand);
                    }
                    if (!_settings.UseGameAbs) SuggestBraking(driver, ref demand);
                    if (!collisionHold && !shiftingThroughNeutral && !launching && !shifting && !hasHandbrake) SuggestSlipper(sample, driver, ref demand);
                }
                var advancedSettings = (_driftSuppressed || collisionHold) && !_lateral.DriftExiting
                    ? _settings with { Features = _settings.Features & ~DriveAssistFeature.DriftTransitions } : _settings;
                if (!shiftingThroughNeutral)
                    _lateral.Suggest(sample, driver, _state, _profile, advancedSettings, drift, (Active & ~BenignCalibrationFlags) == 0 && !collisionHold,
                        ref demand, limitDrive: !collisionHold);
                if (!collisionHold && !launching && !hasHandbrake)
                    _pedals.Suggest(sample, driver, _state, _profile, _settings, drift, shiftDirection, ref demand);
                // Resolve only demands the app is allowed to issue, so canceled assists cannot leave a recovery ramp.
                if (_settings.UseGameAbs) demand.BrakeOwner = demand.BrakeCapOwner = 0;
                if (_settings.UseGameTractionControl) demand.ThrottleOwner = 0;
                _decision = _arbitrator.Resolve(driver, demand, _state.Dt,
                    holdTraction: !_settings.UseGameTractionControl && !collisionHold && now < _tractionRecoveryAt,
                    restoreThrottle: collisionHold, startFromDriverThrottle: _settings.UseGameTractionControl);
                _hasDecision = true;
            }
            else ClearDecision();
        }

        BlocksShift = _hasDecision && (recovering || _launchStaged || _launchAge is >= 0 and < 0.28 || _slipperTime > 0 ||
            !airborne && _pedals.BlocksShift ||
            Has(DriveAssistFeature.Landing) && _state.LandingStrength > 0.65);
        _previousThrottle = driver.Throttle;
        _previousSteer = driver.Steer;
        if (contiguous) _pending = sample;
    }

    public ShiftGamepadReport Render(ShiftGamepadReport physical, ShiftGamepadReport combined, long now, bool shifting = false, byte shiftBlip = 0)
    {
        if (_settings.UseGameAbs) combined = combined with { LeftTrigger = physical.LeftTrigger };
        if (!HasFreshData(now))
        {
            if (_timestamp.HasValue) Suspend();
            return combined;
        }
        var driver = DriveAssistDriver.Read(physical, (ushort)_settings.HandbrakeButton);
        if (_cruise.IsActive && (driver.Brake > 0.055 || driver.Throttle > 0.12 || driver.Clutch || driver.Handbrake ||
            _settings.Skidpad && Math.Abs(driver.Steer) > 0.73)) CancelCruise();
        var handbrakeOverride = driver.Handbrake && _handbrakeAt is long held && now - held >= HandbrakeOverrideMs;
        if (handbrakeOverride) BlocksShift = false;
        var applied = DriveAssistFeature.None;
        var decision = _decision;
        if (_settings.UseGameTractionControl && decision.ThrottleOwner == DriveAssistFeature.CornerTraction &&
            (shifting || (physical.Buttons & ShiftGamepadReport.ManualShiftButtons) != 0))
            decision = decision with { ThrottleOwner = 0 };
        var report = _hasDecision && !handbrakeOverride ?
            decision.Apply(physical, combined, (ushort)_settings.HandbrakeButton, shifting, out applied, shiftBlip) : combined;
        if (_settings.UseGameTractionControl)
            report = report with { RightTrigger = Math.Min(report.RightTrigger, combined.RightTrigger) };
        Active = applied;
        if (_pending is { } sample)
        {
            _pending = null;
            var frame = new DriveAssistFrame(_state.Dt, _state.Phase, applied, _state.LateralG, physical, report);
            LastFrame = frame;
            var feedback = Has(DriveAssistFeature.Feedback);
            if (feedback)
            {
                if (_segment is null || _segment.CarOrdinal != _car || _segment.PerformanceIndex != _pi)
                {
                    PreviousLap = null;
                    _segment = new();
                }
                else if (sample.RacePosition > 0 && _segment.LapNumber >= 0 && sample.LapNumber == _segment.LapNumber + 1)
                {
                    PreviousLap = _segment.Snapshot(DriveAssistRunMode.Lap);
                    _segment = new();
                }
                else if (sample.RacePosition <= 0 && _segment.LapNumber >= 0 ||
                    sample.RacePosition > 0 && sample.LapNumber != _segment.LapNumber) _segment = new();
                _segment.Add(sample, frame);
            }
            _cruise.Record(sample, frame, feedback);
        }
        return report;
    }

    private bool SuggestRotationRecovery(DriveAssistDriver driver, bool collisionHold, ref DriveAssistDemands demand)
    {
        var owner = Has(DriveAssistFeature.CounterSteer) ? DriveAssistFeature.CounterSteer : _settings.EffectiveFeatures & DriveAssistFeature.Stability;
        if (owner == 0 || !_state.Rescuing || driver.Clutch) return false;
        var beta = _state.Beta;
        var departure = Math.Abs(beta);
        var rotatingFurther = _state.Yaw * beta < 0;
        var severity = Unit((departure - 0.12) / 0.49 + (rotatingFurther ? 0.24 * Math.Abs(_state.Yaw) : 0));
        var strength = Unit((departure - 0.075) / 0.19 + (rotatingFurther ? 0.29 : 0));
        var alignment = Math.Clamp(_profile.YawGain * (1.34 * beta - 0.50 * _state.Yaw - 0.026 * _state.YawAccel), -1, 1);
        // More steering is useful only until it catches the rotation; preserve a useful driver countersteer.
        var alreadyCountering = driver.Steer * alignment > 0 && Math.Abs(driver.Steer) >= Math.Abs(alignment);
        var steering = alreadyCountering ? driver.Steer : driver.Steer + (alignment - driver.Steer) * strength;
        demand.Steer(steering, 1, SteeringPriority.CounterSteer, WithModifiers(owner, thermal: false, profile: true),
            rate: alreadyCountering ? 36 : 19 + severity * 7);

        if (!collisionHold && driver.Throttle > 0.07)
        {
            var cut = 0.12 + 0.38 * severity;
            if (!_settings.UseGameTractionControl)
            {
                var spin = Unit((_state.DrivenSlip - 0.25) / 1.05);
                cut = Math.Max(spin * (0.40 + 0.48 * severity), cut);
            }
            if (driver.Brake > 0.18) cut = Math.Max(cut, Unit((driver.Brake - 0.08) * 1.25));
            demand.LimitThrottle(driver.Throttle * (1 - cut), owner, cutRate: 21);
        }
        if (driver.Brake > 0.14 && _state.RearBrakingSlip > 0.89 && rotatingFurther)
        {
            var release = Unit((_state.RearBrakingSlip - 0.83) / 1.35) * (0.20 + 0.30 * severity);
            demand.LimitBrake(driver.Brake * (1 - release), owner, releaseRate: 18);
        }
        return departure >= 0.20 && (rotatingFurther || departure >= 0.30);
    }

    private bool SuggestDrift(DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        if (!Has(DriveAssistFeature.DriftHold) || _state.Speed < 6 || driver.Brake > 0.22 ||
            Math.Abs(driver.Steer) < 0.08 && Math.Abs(_state.Beta) < 0.15 ||
            Math.Abs(_state.Beta) < 0.11 && Math.Abs(_state.Yaw) < 0.35) return false;
        var targetBeta = Math.Clamp(-driver.Steer * (70 * Math.PI / 180), -0.98, 0.98);
        var desiredYaw = (_state.Beta - targetBeta) * 2.2;
        var steer = driver.Steer * 0.30 + (desiredYaw - _state.Yaw) * 0.73 - _state.YawAccel * 0.028;
        demand.Steer(steer, 1, SteeringPriority.Drift, DriveAssistFeature.DriftHold, 11);
        if (driver.Brake < 0.06 && !driver.Clutch && driver.Throttle > 0.08)
        {
            var angleError = Math.Abs(targetBeta) - Math.Abs(_state.Beta);
            demand.Throttle(driver.Throttle + Math.Clamp(angleError * 0.30, -0.20, 0.13), 4, DriveAssistFeature.DriftHold);
        }
        return true;
    }

    private bool SuggestHairpin(TelemetrySample sample, DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        if (!Has(DriveAssistFeature.Hairpin)) return false;
        if (driver.Handbrake && !_hairpinReleased)
        {
            if (_state.Speed is < 4.5 or > 30 || driver.Brake > 0.4) return false;
            _hairpinAngle += _state.Yaw * _state.Dt;
            _hairpinVerified |= sample.HandBrake > 0.32;
            if (_hairpinVerified && Math.Abs(_state.Beta) > 0.13 &&
                (Math.Abs(_hairpinAngle) >= 0.57 || Math.Abs(_hairpinAngle) >= 0.37 && Math.Abs(_state.Beta) > 0.42))
            {
                _hairpinReleased = true;
                _hairpinExit = 0.36;
            }
            else return false;
        }
        if (!driver.Handbrake) { _hairpinAngle = 0; _hairpinReleased = _hairpinVerified = false; }
        if (_hairpinExit <= 0) return false;
        _hairpinExit -= _state.Dt;
        if (_state.LandingStrength > 0.30 || driver.Brake > 0.23 || driver.Clutch) return false;
        demand.ReleaseHandbrake = driver.Handbrake && _hairpinReleased;
        demand.Steer(driver.Steer * 0.32 - _state.Yaw * 0.62 + _state.Beta * 0.47 - _state.YawAccel * 0.027,
            1, SteeringPriority.Hairpin, DriveAssistFeature.Hairpin, 21);
        if (Math.Abs(_state.Beta) < 0.70 && driver.Brake < 0.06)
            demand.Throttle(driver.Throttle + 0.14 * Unit((0.70 - Math.Abs(_state.Beta)) / 0.40), 5, DriveAssistFeature.Hairpin);
        return true;
    }

    private bool ShouldPreserveTightTurnSteering(TelemetrySample sample, DriveAssistDriver driver, bool controlsAvailable, bool shifting)
    {
        const double holdSeconds = 0.10;
        // The inside tire can scrub in a tight turn while the outside tire still has grip.
        var frontOutside = driver.Steer > 0 ? sample.FrontLeft : sample.FrontRight;
        var frontLimit = _profile.FrontLimit * Math.Sqrt(_state.ThermalScale);
        var stable = controlsAvailable && !driver.Clutch && sample.VelocityZ >= 0.5 &&
            _state.Phase is not (DriveAssistPhase.Airborne or DriveAssistPhase.Landing or DriveAssistPhase.Crest) &&
            _state.LandingStrength <= 0 && !_state.Rescuing && Math.Abs(_state.Beta) <= 0.13 && _state.RearAngle <= 0.80 &&
            frontOutside.NormalizedSuspensionTravel >= 0.08;
        if (_settings.LearnTightTurnSpeed)
            _tightTurnLearning.Observe(driver, _state, frontOutside, frontLimit, stable && !shifting);

        var speedLimitKmh = LearnedTightTurnSpeedKmh ?? _settings.TightTurnSpeedLimitKmh;
        var frontAllowance = frontLimit * 1.22;
        var combinedAllowance = driver.Brake > 0.08 ? 1.55 : 1.25;
        if (!stable || _state.Speed < 3 || sample.SpeedKmh > speedLimitKmh || Math.Abs(driver.Steer) < 0.75 ||
            Math.Sign(driver.Steer) * _state.Yaw < -0.12 ||
            Math.Abs(frontOutside.SlipAngle) >= frontAllowance || Math.Abs(frontOutside.CombinedSlip) > combinedAllowance)
        {
            _tightTurnSteerTime = Math.Max(0, _tightTurnSteerTime - _state.Dt * 2.5);
            return false;
        }

        if (driver.Steer * _previousSteer <= 0) _tightTurnSteerTime = 0;
        _tightTurnSteerTime = Math.Min(holdSeconds, _tightTurnSteerTime + _state.Dt);
        return _tightTurnSteerTime >= holdSeconds;
    }

    private void SuggestSteering(TelemetrySample sample, DriveAssistDriver driver, bool collisionHold, ref DriveAssistDemands demand)
    {
        var magnitude = Math.Abs(driver.Steer);
        if (Has(DriveAssistFeature.Damper))
        {
            var speed = Unit((_state.Speed - 14) / 42);
            var load = Unit(_state.PitchRoll * (0.30 + driver.Brake));
            var strength = Math.Max(speed, load);
            if (strength > 0.04)
            {
                var rate = 13.5 - 11.0 * strength;
                var center = 1 - 0.56 * strength * _profile.CenterDamper * Math.Pow(1 - magnitude, 2);
                demand.Steer(driver.Steer * center * (1 - 0.14 * load), strength, SteeringPriority.Damper,
                    WithModifiers(DriveAssistFeature.Damper, thermal: false, profile: true), rate);
            }
        }
        if (!Has(DriveAssistFeature.Understeer) || _state.Speed < 6 || collisionHold || _state.Rescuing ||
            Math.Abs(_state.Beta) > 0.19 || _state.RearAngle > Math.Max(1.05, _state.FrontAngle * 1.20))
        {
            _frontPressure = 0;
            return;
        }
        var limit = _profile.FrontLimit * Math.Sqrt(_state.ThermalScale);
        var overPeak = Unit((_state.FrontAngle - limit) / 0.47);
        var saturated = _state.FrontAngle > limit * 0.72 && _state.SteerRise > 0.10 && _state.LateralTrend < 0.18 && _state.LateralG > 0.32 && _state.FrontCombined > 0.70;
        var severity = Math.Max(overPeak, saturated ? Unit((_state.FrontCombined - 0.66) / 0.5) : 0);
        _frontPressure = magnitude > 0.15 ? Math.Max(severity, _frontPressure - _state.Dt * 2.5) : 0;
        if (_frontPressure > 0.02)
        {
            // Keep at least 66-76% of driver steering so peak-seeking never straightens tight corners into walls.
            var maxTrim = 0.34 - 0.08 * Unit((_state.Speed - 12) / 28);
            demand.Steer(driver.Steer * (1 - maxTrim * _frontPressure), _frontPressure, SteeringPriority.Understeer,
                WithModifiers(DriveAssistFeature.Understeer, thermal: _state.ThermalScale < 0.99, profile: true), 9);
            if (Has(DriveAssistFeature.CornerTraction) && overPeak > 0.20 &&
                _state.LateralG > 0.55 * Math.Max(1.05, _estimator.EffectiveLateralGripG) &&
                driver.Throttle > 0.12 && driver.Brake < 0.24 && (sample.DrivetrainType is 0 or 2 || _state.LateralG > 0.75))
                demand.LimitThrottle(driver.Throttle * (1 - 0.16 * (overPeak - 0.20) / 0.80), DriveAssistFeature.CornerTraction);
        }
    }

    private void SuggestSurface(DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        var landing = _state.LandingStrength;
        if (Has(DriveAssistFeature.Landing) && landing > 0)
        {
            demand.Steer(driver.Steer * (1 - 0.79 * landing), landing, SteeringPriority.Landing, DriveAssistFeature.Landing, 26);
            demand.LimitThrottle(1 - 0.76 * landing, DriveAssistFeature.Landing);
        }
        if (Has(DriveAssistFeature.Crest) && _state.CrestStrength > 0)
        {
            var lift = _state.CrestStrength;
            demand.Steer(driver.Steer * (1 - 0.48 * lift), lift, SteeringPriority.Surface, DriveAssistFeature.Crest, 7);
            demand.LimitThrottle(Math.Min(1 - 0.27 * lift, _previousThrottle + _state.Dt * 1.2), DriveAssistFeature.Crest);
        }
        if (Has(DriveAssistFeature.BottomOut) && _state.Speed > 4 && _state.Bump > 0.08)
        {
            var rough = _state.Bump;
            var steer = _previousSteer + Math.Clamp(driver.Steer - _previousSteer, -0.14, 0.14);
            demand.Steer(steer * (1 - 0.30 * rough), rough, SteeringPriority.Surface, DriveAssistFeature.BottomOut, 4);
            demand.LimitThrottle(Math.Min(1 - 0.23 * rough, _previousThrottle + _state.Dt * (2 - rough)), DriveAssistFeature.BottomOut);
        }
        if (!Has(DriveAssistFeature.SplitGrip)) return;
        _splitCooldown = Math.Max(0, _splitCooldown - _state.Dt);
        if (_splitCooldown == 0 && _state.Speed > 15 && _state.SurfaceDifference > 0.36 &&
            (_state.LeftRightSlip > 0.14 || _state.LeftRightSpeed > 0.075) && Math.Abs(_state.YawAccel) > 3.2)
        {
            _splitTime = 0.115;
            _splitCooldown = 0.30;
            _splitSteer = Math.Clamp(-0.046 * _state.YawAccel, -0.44, 0.44);
        }
        if (_splitTime <= 0) return;
        var pulse = Unit(_splitTime / 0.115);
        _splitTime -= _state.Dt;
        demand.Steer(driver.Steer + _splitSteer * pulse, pulse, SteeringPriority.SplitGrip, DriveAssistFeature.SplitGrip, 29);
        if (!_settings.UseGameTractionControl)
            demand.LimitThrottle(driver.Throttle * (1 - 0.20 * pulse), DriveAssistFeature.SplitGrip);
    }

    private bool SuggestLaunch(TelemetrySample sample, DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        if (!Has(DriveAssistFeature.Launch) || sample.Gear != 1 || driver.Throttle < 0.50 || driver.Clutch ||
            !double.IsFinite(sample.Rpm) || sample.EngineMaxRpm is not (>= 1500 and <= 30000))
        {
            _launchHold = _launchIntegral = _launchBoost = 0;
            _launchAge = -1;
            return _launchStaged = false;
        }
        if (_state.Speed < 0.95 && driver.Brake > 0.61 && driver.Throttle > 0.66)
        {
            _launchHold += _state.Dt;
            if (_launchHold < 0.085) return false;
            _launchStaged = true;
            _launchAge = -1;
            var minimum = sample.EngineIdleRpm > 0 && sample.EngineIdleRpm < sample.EngineMaxRpm * 0.62 ? sample.EngineIdleRpm + 450 : 1200;
            var target = Math.Min(sample.EngineMaxRpm * 0.85, Math.Max(minimum, sample.EngineMaxRpm * _settings.LaunchRpmPercent / 100));
            var error = (target - sample.Rpm) / sample.EngineMaxRpm;
            _launchIntegral = Math.Clamp(_launchIntegral + error * _state.Dt * 2.2, -0.27, 0.25);
            var boost = double.IsFinite(sample.BoostPsi) ? Math.Clamp(sample.BoostPsi, 0, 80) : 0;
            _launchBoost = Math.Max(boost, _launchBoost - _state.Dt * 0.5);
            var pulse = _launchBoost > 0.8 && error > -0.018 && _launchHold % 0.15 < 0.065
                ? Math.Min(0.12, Math.Max(0, _launchBoost * 0.90 - boost) * 0.021) : 0;
            var throttle = sample.Rpm >= sample.EngineMaxRpm * 0.93 ? 0 : Unit(0.43 + error * 4 + _launchIntegral + pulse);
            demand.LimitThrottle(throttle, DriveAssistFeature.Launch);
            return true;
        }
        _launchHold = 0;
        if (_launchStaged && driver.Brake < 0.12)
        {
            _launchStaged = false;
            _launchAge = 0;
        }
        if (_launchAge < 0 || _launchAge > 0.85 || driver.Brake >= 0.12 || _state.Speed > 17)
        {
            if (driver.Brake >= 0.12) _launchAge = -1;
            return false;
        }
        var grip = Unit((_state.DrivenSlip - 0.89) * 0.68 + Math.Max(0, _state.DrivenCombined - 1.0) * 0.22);
        var accelerating = Unit(_state.LongitudinalG / 0.85);
        demand.LimitThrottle(Unit(0.62 + _launchAge * 0.82 + accelerating * 0.14 - grip), DriveAssistFeature.Launch);
        if (_launchAge < 0.075) demand.ClutchOwner = DriveAssistFeature.Launch;
        _launchAge += _state.Dt;
        return true;
    }

    private void SuggestCornerThrottle(TelemetrySample sample, DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        if (!Has(DriveAssistFeature.CornerTraction) || _state.Speed <= 6 || driver.Throttle <= 0.12 ||
            driver.Brake > 0.13 || driver.Clutch) return;

        var steer = Math.Abs(driver.Steer);
        var gripCeiling = Math.Max(1.05, _estimator.EffectiveLateralGripG);
        var lateralRatio = Unit((_state.LateralG / gripCeiling - 0.52) / 0.34);
        var unwinding = _state.Phase == DriveAssistPhase.CornerExit && _state.LateralTrend < -0.18 &&
            steer < 0.36 && _state.DrivenSlip < 0.55 && Math.Abs(_state.Beta) < 0.14;
        var frontPush = Unit((_state.FrontAngle - _profile.FrontLimit * 0.96) / 0.45) * Unit((steer - 0.30) / 0.45);
        var overRotate = Unit((_state.RearAngle - 0.78) / 0.52) * Unit((Math.Abs(_state.Beta) - 0.10) / 0.18);
        var driveSlip = Unit((_state.DrivenSlip - 0.62) / 0.48) * Unit((lateralRatio - 0.15) / 0.45);
        var slipDemand = sample.DrivetrainType switch
        {
            0 => Math.Max(driveSlip, frontPush * 0.72),
            1 => Math.Max(driveSlip, overRotate * 0.90),
            _ => Math.Max(driveSlip, Math.Max(overRotate * 0.70, frontPush * 0.50)),
        };

        // Keep throttle open below ~52% grip, apply only gentle apex stabilization, and restore immediately on unwind.
        var preload = unwinding ? 0 : 0.11 * Math.Pow(lateralRatio * Unit((steer - 0.24) / 0.42), 1.5);
        var cut = Math.Clamp(Math.Max(preload, 0.38 * slipDemand), 0, 0.40);
        if (cut > 0.005)
            demand.LimitThrottle(driver.Throttle * (1 - cut), DriveAssistFeature.CornerTraction,
                cutRate: slipDemand > 0.35 ? 14 : 7);
        else if (unwinding && _hasDecision && _decision.Throttle < driver.Throttle - 0.02)
            demand.ThrottleRestoreRate = 4.2;
    }

    private void SuggestTraction(TelemetrySample sample, DriveAssistDriver driver, bool drifting, ref DriveAssistDemands demand)
    {
        var throttle = Math.Max(driver.Throttle, demand.Gas);
        var tcs = Has(DriveAssistFeature.Traction);
        var lowSpeedSpin = tcs && (_state.DrivenWheelOverrun || _tractionCut > 0);
        if ((_state.Speed < 1.6 && !lowSpeedSpin) || driver.Brake > 0.13 || throttle < 0.08) { ResetTraction(); return; }
        var gripCeiling = Math.Max(1.12, _estimator.EffectiveLateralGripG * 1.04);
        var axleAngle = Math.Max(_state.FrontAngle, _state.RearAngle);
        var load = Unit(Math.Max(_state.LateralG / gripCeiling, axleAngle * 0.56));
        var corner = Has(DriveAssistFeature.CornerTraction) && load > 0.32 && !drifting;
        if (!tcs && !corner && _tractionCut <= 0) return;
        var unwinding = _state.Phase == DriveAssistPhase.CornerExit && _state.LateralTrend < -0.15 &&
            Math.Abs(driver.Steer) < 0.36 && _state.DrivenSlip < 0.70 && !_state.DrivenWheelOverrun;
        var grip = corner && !unwinding ? Math.Max(0.60, Math.Sqrt(1 - 0.76 * load * load)) : 1;
        var target = _profile.TractionLimit * _state.ThermalScale * grip;
        var error = tcs || corner ? _state.DrivenSlip - (drifting ? Math.Max(1.42, target) : target) : -0.3;
        // Combined slip can be almost entirely lateral. Use it to tighten traction control only while a driven wheel slips forward.
        if ((tcs || corner) && _state.DrivenSlip > target * 0.65)
        {
            var combinedLimit = drifting ? 1.85 : corner && !unwinding ? 1.12 : 1.18;
            var combinedError = (_state.DrivenCombined - combinedLimit) * 0.69;
            error = Math.Max(error, Math.Min(combinedError, _state.DrivenSlip * 0.51));
        }
        if ((tcs || corner) && !drifting && (unwinding || load < 0.38) && error < -0.18 &&
            _state.DrivenCombined < 0.85 && !_state.DrivenWheelOverrun)
            demand.ThrottleRestoreRate = 4.20;
        _tractionIntegral = error > 0 ? Math.Clamp(_tractionIntegral + error * _state.Dt * 1.45, 0, 0.33) :
            Math.Max(0, _tractionIntegral - _state.Dt * (0.85 - 1.5 * Math.Max(-1, error)));
        var slope = Math.Clamp((error - _tractionError) / _state.Dt, -9, 9);
        _tractionDerivative += (1 - Math.Exp(-_state.Dt / 0.055)) * (slope - _tractionDerivative);
        _tractionError = error;
        _tractionCut = Math.Clamp(error * 0.78 + _tractionIntegral + 0.024 * _tractionDerivative, 0, 0.91);
        if (_tractionCut <= 0) return;
        var owner = (tcs ? DriveAssistFeature.Traction : 0) | (corner && grip < 0.98 ? DriveAssistFeature.CornerTraction : 0);
        if (owner != 0) demand.LimitThrottle(throttle * (1 - _tractionCut),
            WithModifiers(owner, thermal: _state.ThermalScale < 0.99, profile: true),
            cutRate: !drifting && _state.DrivenWheelOverrun && _state.DrivenSlip >= 1.75 ? 18 : 0);
    }

    private void ResetTraction() => _tractionIntegral = _tractionError = _tractionDerivative = _tractionCut = 0;

    private void SuggestBraking(DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        var brake = Math.Max(driver.Brake, demand.Brake);
        if (_state.Speed < 2.1 || brake < 0.045) { _brakeCut = 0; return; }
        if (Has(DriveAssistFeature.Abs))
        {
            var threshold = 0.94 - 0.11 * (1 - _state.ThermalScale);
            var lockRisk = _state.BrakingSlip - threshold;
            if (_state.BrakingSlip > 0.75) lockRisk = Math.Max(lockRisk, (_state.WheelDrop - 4.0) * 0.095);
            if (lockRisk > 0)
                _brakeCut = Math.Min(Math.Max(0.90, _brakeCut), _brakeCut + _state.Dt * Math.Min(9, lockRisk * 10));
            else
            {
                var recover = _state.BrakingSlip < 0.76 && _state.WheelLock <= 0.01;
                _brakeCut = Math.Max(0, _brakeCut - _state.Dt * (recover ? 10 : 4));
                if (recover) demand.BrakeRestoreRate = 13;
            }
            if (_state.WheelLock > 0) _brakeCut = Math.Max(_brakeCut, 0.25 + 0.75 * _state.WheelLock);
            if (_brakeCut > 0) demand.LimitBrake(brake * (1 - _brakeCut), WithModifiers(DriveAssistFeature.Abs, _state.ThermalScale < 0.99),
                releaseRate: 28 * _state.WheelLock);
        }
        if (Has(DriveAssistFeature.TrailBrake) && !_state.Rescuing && Math.Abs(_state.Beta) <= 0.19 &&
            driver.Brake > 0.08 && Math.Abs(driver.Steer) > 0.13)
        {
            var turn = Unit((Math.Abs(driver.Steer) - 0.13) / 0.72);
            var load = Unit((_state.FrontCombined - 0.36) * 0.72 + _state.LateralG * 0.33 + Math.Max(0, _state.SteerRise) * 0.024);
            var trim = 0.53 - 0.39 * Unit((driver.Brake - 0.78) / 0.18);
            if (load > 0.03) demand.LimitBrake(driver.Brake * (1 - trim * turn * load), DriveAssistFeature.TrailBrake);
        }
    }

    private void SuggestSlipper(TelemetrySample sample, DriveAssistDriver driver, ref DriveAssistDemands demand)
    {
        if (!Has(DriveAssistFeature.Slipper) || sample.DrivetrainType == 0 || _state.Speed < 4 || driver.Clutch || _state.LandingStrength > 0.30)
            return;
        if (_state.Downshift) _downshiftWindow = 0.17;
        if (_downshiftWindow > 0 && _state.RearBrakingSlip > 0.68 && _state.RearBrakingSpike > 0.38)
        {
            _slipperTime = 0.10;
            _downshiftWindow = 0;
        }
        _downshiftWindow = Math.Max(0, _downshiftWindow - _state.Dt);
        if (_slipperTime <= 0) return;
        demand.ClutchOwner = DriveAssistFeature.Slipper;
        if (driver.Brake < 0.055 && driver.Throttle < 0.45)
            demand.Throttle(driver.Throttle + 0.14 * Unit(_slipperTime / 0.10), 2, DriveAssistFeature.Slipper);
        _slipperTime = Math.Max(0, _slipperTime - _state.Dt);
    }

    private DriveAssistFeature WithModifiers(DriveAssistFeature feature, bool thermal, bool profile = false) => feature |
        (thermal && Has(DriveAssistFeature.Thermal) ? DriveAssistFeature.Thermal : 0) |
        (profile && Has(DriveAssistFeature.Profiles) ? DriveAssistFeature.Profiles : 0);
}
