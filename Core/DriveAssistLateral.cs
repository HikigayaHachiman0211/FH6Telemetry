using System;
using static FH6Telemetry.DriveAssistEstimator;

namespace FH6Telemetry;

internal readonly record struct DriveAssistLateralStatus(double Risk, int SpinLevel, double RollRisk, bool BottomedOut,
    double SuspensionImbalance, double? DriftScore, double? SteeringGain, double? SteeringDelayMs,
    double? SteeringDeadzone, bool FourWheelSlide, bool GripRecovery);

internal sealed class DriveAssistLateral
{
    private DriveAssistDriver _previous;
    private bool _hasPrevious;
    private double _previousBeta, _previousGrip;
    private double _headingError, _straightTime, _laneChangeTime, _fullLockTime;
    private double _reversalWindow, _pendulumTime, _gripRecoveryTime, _bottomOutTime, _loadDifference;
    private int _lastYawErrorSign, _steerSign, _pendingSteerSign;
    private double _steerIntentTime;
    private double _driftTime, _driftTotalScore, _driftTarget, _driftTransition, _liftTime, _exitTime, _exitStableTime;
    private double? _lastDriftScore, _steeringDelayMs, _learnedDeadzone;
    private double _stepAge, _stepYaw, _steadySteer;
    private int _stepDirection;
    private Response _left, _right;

    public DriveAssistLateralStatus Status { get; private set; }
    public bool DriftExiting => _exitTime > 0;

    public void RequestDriftExit()
    {
        _exitTime = 1.4;
        _exitStableTime = _driftTransition = 0;
    }

    public void Reset(bool vehicleChanged)
    {
        _previous = default;
        _hasPrevious = false;
        _previousBeta = _previousGrip = 0;
        _headingError = _straightTime = _laneChangeTime = _fullLockTime = 0;
        _reversalWindow = _pendulumTime = _gripRecoveryTime = _bottomOutTime = _loadDifference = 0;
        _steerIntentTime = 0;
        _lastYawErrorSign = _steerSign = _pendingSteerSign = 0;
        _driftTime = _driftTotalScore = _driftTarget = _driftTransition = _liftTime = _exitTime = _exitStableTime = 0;
        _stepAge = _stepYaw = _steadySteer = 0;
        _stepDirection = 0;
        if (vehicleChanged)
        {
            _left = _right = default;
            _lastDriftScore = _steeringDelayMs = _learnedDeadzone = null;
        }
        Status = new(0, 0, 0, false, 0, _lastDriftScore, SteeringGain(), _steeringDelayMs, _learnedDeadzone, false, false);
    }

    public void Suggest(TelemetrySample sample, DriveAssistDriver driver, DriveAssistState state, DriveAssistProfile profile,
        DriveAssistSettings settings, bool drifting, bool calibrationPass, ref DriveAssistDemands demand, bool limitDrive = true)
    {
        if (state.Phase is DriveAssistPhase.Airborne or DriveAssistPhase.Landing || state.LandingStrength > 0 ||
            !(state.Dt is > 0 and <= 0.12))
        {
            Reset(vehicleChanged: false);
            return;
        }

        var features = settings.EffectiveFeatures;
        if ((features & DriveAssistFeature.VehicleLearning) != 0)
            LearnSteering(sample, driver, state, calibrationPass);

        var absBeta = Math.Abs(state.Beta);
        var rearGrip = Math.Max(Math.Abs(sample.RearLeft.CombinedSlip), Math.Abs(sample.RearRight.CombinedSlip));
        var allSliding = state.Speed > 5 && absBeta > 0.15 &&
            Math.Abs(sample.FrontLeft.CombinedSlip) > 1.05 && Math.Abs(sample.FrontRight.CombinedSlip) > 1.05 &&
            Math.Abs(sample.RearLeft.CombinedSlip) > 1.05 && Math.Abs(sample.RearRight.CombinedSlip) > 1.05;
        var yawExpected = state.ExpectedYaw;
        if ((features & DriveAssistFeature.VehicleLearning) != 0)
            yawExpected *= (driver.Steer >= 0 ? _right.Estimate : _left.Estimate) ?? 1;
        var yawError = state.Yaw - yawExpected;
        var brakeYaw = driver.Brake > 0.24 && state.Speed > 6 && state.RearBrakingSlip > 0.66 &&
            Math.Abs(yawError) > 0.23 && (absBeta > 0.11 || state.RearBrakingSpike > 0.22);
        var risk = PredictiveRisk(state, yawError, brakeYaw, allSliding);
        var spinLevel = allSliding && absBeta > 0.42 || absBeta > 0.56 && Math.Abs(state.Yaw) > 0.9 ? 3 :
            risk > 0.63 || state.Rescuing ? 2 : risk >= 0.34 ? 1 : 0;

        TrackRebound(state, driver, yawError, rearGrip);
        var rollRisk = ObserveSuspension(sample, state);
        var permitted = state.Speed > 3 && !driver.Handbrake && !driver.Clutch;
        if (permitted && !drifting && !DriftExiting)
        {
            if ((features & DriveAssistFeature.SteeringTuning) != 0)
                SuggestSteeringTuning(driver, state, settings, ref demand);
            if ((features & DriveAssistFeature.Stability) != 0 && !state.Rescuing)
                SuggestStability(driver, state, profile, yawError, risk, spinLevel, brakeYaw,
                    limitDrive && !settings.UseGameTractionControl, ref demand);
        }
        else
        {
            _straightTime = _headingError = _laneChangeTime = _fullLockTime = 0;
            _steerSign = _pendingSteerSign = 0;
            _steerIntentTime = 0;
        }

        if (limitDrive && (features & (DriveAssistFeature.DriftTransitions | DriveAssistFeature.DriftHold)) != 0)
            SuggestDrift(driver, state, drifting, (features & DriveAssistFeature.DriftTransitions) != 0, ref demand);
        else _exitTime = 0;

        if (permitted && (features & DriveAssistFeature.SuspensionProtection) != 0)
            ProtectSuspension(driver, state, rollRisk, limitDrive, ref demand);

        // At a standstill, the configured deadzone still applies to noise but no self-centering is requested.
        if (state.Speed <= 3 && !driver.Handbrake && !driver.Clutch && (features & DriveAssistFeature.SteeringTuning) != 0)
            SuggestSteeringTuning(driver, state, settings, ref demand);

        Status = new(risk, spinLevel, rollRisk, _bottomOutTime > 0, _loadDifference, _lastDriftScore,
            SteeringGain(), _steeringDelayMs, _learnedDeadzone, allSliding, _gripRecoveryTime > 0);
        _previous = driver;
        _previousBeta = state.Beta;
        _previousGrip = rearGrip;
        _hasPrevious = true;
    }

    private double PredictiveRisk(DriveAssistState state, double yawError, bool brakeYaw, bool allSliding)
    {
        if (state.Speed < 5) return 0;
        var beta = Math.Abs(state.Beta);
        var rear = Unit((state.RearAngle - 0.34) / 0.62);
        if (rear < 0.08 && beta < 0.085 && !brakeYaw && !allSliding) return 0;
        var betaGrowth = _hasPrevious ? Math.Max(0, (beta - Math.Abs(_previousBeta)) / state.Dt) : 0;
        var predictedBeta = beta + 0.10 * Math.Min(1.5, betaGrowth);
        var predictedYaw = Math.Abs(yawError) + 0.12 * Math.Clamp(Math.Sign(yawError) * state.YawAccel, 0, 6);
        var angle = Unit((predictedBeta - 0.06) / 0.48);
        var rotation = Unit((predictedYaw - 0.22) / 0.78);
        return Unit((rotation * 0.58 + angle * 0.34 + rear * 0.24) * (0.55 + 0.45 * Math.Max(rear, angle)) +
            (brakeYaw ? 0.20 : 0) + (allSliding ? 0.26 : 0));
    }

    private void TrackRebound(DriveAssistState state, DriveAssistDriver driver, double yawError, double rearGrip)
    {
        _pendulumTime = Math.Max(0, _pendulumTime - state.Dt);
        _gripRecoveryTime = Math.Max(0, _gripRecoveryTime - state.Dt);
        _reversalWindow = Math.Max(0, _reversalWindow - state.Dt);
        if (state.Speed < 5)
        {
            _pendulumTime = _gripRecoveryTime = _reversalWindow = 0;
            return;
        }

        if (_hasPrevious && _previousGrip > 1.08 && rearGrip < 0.85 && _previousGrip - rearGrip > 0.36 &&
            (Math.Abs(yawError) > 0.22 || Math.Abs(state.YawAccel) > 1.25))
            _gripRecoveryTime = 0.26;

        if (Math.Abs(yawError) <= 0.25 || (state.RearAngle < 0.5 && Math.Abs(state.Beta) < 0.12 && _gripRecoveryTime == 0))
            return;
        var direction = Math.Sign(yawError);
        var deliberate = _hasPrevious && Math.Abs(driver.Steer - _previous.Steer) > 0.34;
        if (_reversalWindow > 0 && direction != _lastYawErrorSign && !deliberate)
            _pendulumTime = 0.30;
        _lastYawErrorSign = direction;
        _reversalWindow = 0.72;
    }

    private void SuggestStability(DriveAssistDriver driver, DriveAssistState state, DriveAssistProfile profile,
        double yawError, double risk, int spinLevel, bool brakeYaw, bool limitDrive, ref DriveAssistDemands demand)
    {
        var absSteer = Math.Abs(driver.Steer);
        var manual = absSteer > 0.76 || (_hasPrevious && Math.Abs(driver.Steer - _previous.Steer) > 0.46);
        var rebound = _pendulumTime > 0 || _gripRecoveryTime > 0;
        if (spinLevel > 0 || rebound)
        {
            _straightTime = _headingError = _laneChangeTime = 0;
            var confidence = Math.Max(risk, rebound ? 0.62 : spinLevel >= 2 ? 0.48 : 0.34);
            var cap = rebound ? 0.24 : spinLevel == 3 ? 0.63 : spinLevel == 2 ? 0.44 : 0.2;
            var correction = profile.YawGain * (-0.62 * yawError - 0.037 * state.YawAccel + 0.37 * state.Beta) * confidence;
            if (correction * yawError > 0 || Math.Abs(yawError) < 0.07)
                correction = -0.16 * yawError;
            if (!manual)
                demand.Steer(driver.Steer + Math.Clamp(correction, -cap, cap), confidence,
                    SteeringPriority.Stability, DriveAssistFeature.Stability, rebound ? 8 : 13);
            if (brakeYaw)
                demand.LimitBrake(driver.Brake * (1 - 0.48 * confidence), DriveAssistFeature.Stability, releaseRate: 14);
            if (limitDrive && driver.Throttle > 0.12 && (spinLevel >= 2 || rebound))
                demand.LimitThrottle(driver.Throttle * (1 - (spinLevel == 3 ? 0.65 : 0.35) * confidence),
                    DriveAssistFeature.Stability);
            return;
        }

        var inLine = state.Phase == DriveAssistPhase.Straight && absSteer < 0.105 && state.Speed > 16 &&
            state.LateralG < 0.27 && Math.Abs(state.Beta) < 0.1 && driver.Brake < 0.12;
        _straightTime = inLine ? _straightTime + state.Dt : 0;
        _headingError = inLine ? Math.Clamp(_headingError + state.Yaw * state.Dt, -0.26, 0.26) : 0;
        if (_hasPrevious && state.Speed > 24 && absSteer is >= 0.18 and <= 0.60 &&
            Math.Abs(_previous.Steer) < 0.12 && !manual && driver.Brake < 0.18 && Math.Abs(state.Beta) < 0.12)
            _laneChangeTime = 0.65;
        else _laneChangeTime = Math.Max(0, _laneChangeTime - state.Dt);
        if (manual || absSteer > 0.66 || driver.Brake >= 0.18) _laneChangeTime = 0;

        if (_laneChangeTime > 0 && absSteer >= 0.14 && yawError * driver.Steer > 0.014)
        {
            var correction = Math.Min(absSteer * 0.40, Math.Abs(yawError) * 0.27 + Math.Max(0, Math.Sign(driver.Steer) * state.YawAccel) * 0.016);
            demand.Steer(driver.Steer - Math.CopySign(correction, driver.Steer), 0.55,
                SteeringPriority.Stability, DriveAssistFeature.Stability, 9);
        }
        else if (_straightTime >= 0.12)
        {
            var correction = Math.Clamp(-0.22 * state.Yaw - 0.72 * _headingError - state.Beta * 0.13, -0.145, 0.145);
            if (Math.Abs(correction) > 0.012)
                demand.Steer(driver.Steer + correction, 0.40, SteeringPriority.Stability, DriveAssistFeature.Stability, 5);
        }
    }

    private void SuggestSteeringTuning(DriveAssistDriver driver, DriveAssistState state, DriveAssistSettings settings, ref DriveAssistDemands demand)
    {
        var absSteer = Math.Abs(driver.Steer);
        var deadzone = Math.Clamp(settings.SteeringDeadzonePercent, 0, 25) / 100.0;
        var target = Math.CopySign(Unit((absSteer - deadzone) / (1 - deadzone)), driver.Steer);
        var direction = Math.Sign(target);
        var debounce = Math.Clamp(settings.SteeringDebounceMs, 0, 120) / 1000.0;
        if (direction == 0 || absSteer >= 0.27 || debounce == 0)
        {
            _steerSign = direction;
            _pendingSteerSign = 0;
            _steerIntentTime = 0;
        }
        else if (direction != _steerSign)
        {
            _steerIntentTime = _pendingSteerSign == direction ? _steerIntentTime + state.Dt : state.Dt;
            _pendingSteerSign = direction;
            if (_steerIntentTime >= debounce) _steerSign = direction;
            else target = 0;
        }
        else
        {
            _pendingSteerSign = 0;
            _steerIntentTime = 0;
        }

        if ((settings.EffectiveFeatures & DriveAssistFeature.VehicleLearning) != 0 &&
            _left.Estimate is double left && _right.Estimate is double right)
            target *= Math.Clamp(Math.Min(left, right) / (target > 0 ? right : left), 0.82, 1);

        _fullLockTime = state.Speed > 16 && absSteer >= 0.80 ? _fullLockTime + state.Dt : 0;
        if (_fullLockTime >= 0.33 && state.FrontCombined > 0.75)
            target = Math.CopySign(Math.Min(Math.Abs(target), 0.76 - 0.23 * Unit((state.FrontCombined - 0.75) / 0.65)), target);

        var recenter = state.Phase == DriveAssistPhase.CornerExit && absSteer is > 0.08 and < 0.48 && driver.Throttle > 0.28 &&
            _hasPrevious && Math.Abs(_previous.Steer) > absSteer + 0.01 && Math.Abs(state.Beta) < 0.13;
        if (recenter) target *= 0.78;
        if (target == 0 && absSteer < 0.075 && state.Speed > 7 && Math.Abs(state.Beta) < 0.13 && Math.Abs(state.Yaw) > 0.04)
            target = Math.Clamp(-0.38 * state.Yaw, -0.13, 0.13);

        if (Math.Abs(target - driver.Steer) > 0.0005)
            demand.Steer(target, 0.70, SteeringPriority.SteeringTuning, DriveAssistFeature.SteeringTuning, recenter ? 17 : 11);
    }

    private double ObserveSuspension(TelemetrySample sample, DriveAssistState state)
    {
        // shortcut: compression estimates load, replace it when telemetry exposes individual wheel forces.
        var left = (sample.FrontLeft.NormalizedSuspensionTravel + sample.RearLeft.NormalizedSuspensionTravel) * 0.5;
        var right = (sample.FrontRight.NormalizedSuspensionTravel + sample.RearRight.NormalizedSuspensionTravel) * 0.5;
        var difference = Math.Clamp((left - right) / Math.Max(0.20, left + right), -1, 1);
        var blend = 1 - Math.Exp(-state.Dt / 0.075);
        _loadDifference += (difference - _loadDifference) * blend;
        var maxTravel = Math.Max(Math.Max(sample.FrontLeft.NormalizedSuspensionTravel, sample.FrontRight.NormalizedSuspensionTravel),
            Math.Max(sample.RearLeft.NormalizedSuspensionTravel, sample.RearRight.NormalizedSuspensionTravel));
        _bottomOutTime = state.Speed > 5 && maxTravel >= 0.95 ? 0.20 : Math.Max(0, _bottomOutTime - state.Dt);
        if (state.Speed < 5 || !double.IsFinite(sample.Roll)) return 0;

        var roll = Math.IEEERemainder(sample.Roll, 2 * Math.PI);
        var outwardRate = Math.Max(0, Math.Sign(roll) * sample.AngularVelocityZ);
        var projected = Math.Abs(roll) + 0.22 * Math.Min(outwardRate, 4);
        var lateralLoad = Math.Max(Unit((state.LateralG - 0.34) / 0.65), Math.Abs(difference));
        var rollRisk = Unit((projected - 0.30) / 0.55) * (0.30 + 0.70 * lateralLoad);
        return Math.Max(rollRisk, Unit((Math.Abs(difference) - 0.50) / 0.45) * Unit(outwardRate / 1.2));
    }

    private void ProtectSuspension(DriveAssistDriver driver, DriveAssistState state, double rollRisk, bool limitDrive,
        ref DriveAssistDemands demand)
    {
        var pitch = Unit((state.PitchRoll - 0.72) / 1.9);
        var chassis = state.Speed > 10 ? Math.Max(rollRisk, pitch * Unit(state.CrestStrength + state.Bump) * 0.8) : 0;
        var protection = Math.Max(chassis, _bottomOutTime > 0 ? 0.45 : 0);
        if (protection <= 0.09) return;
        if (Math.Abs(driver.Steer) < 0.78)
            demand.Steer(driver.Steer * (1 - 0.58 * protection), protection,
                SteeringPriority.Surface, DriveAssistFeature.SuspensionProtection, 8);
        if (limitDrive && driver.Throttle > 0.12)
            demand.LimitThrottle(driver.Throttle * (1 - 0.55 * protection), DriveAssistFeature.SuspensionProtection);
    }

    private void SuggestDrift(DriveAssistDriver driver, DriveAssistState state, bool drifting, bool enhanced, ref DriveAssistDemands demand)
    {
        var absBeta = Math.Abs(state.Beta);
        var absSteer = Math.Abs(driver.Steer);
        var absYaw = Math.Abs(state.Yaw);
        var stable = absBeta < 0.08 && absYaw < 0.18;
        _liftTime = _hasPrevious && _previous.Throttle - driver.Throttle > 0.22 ? 0.42 : Math.Max(0, _liftTime - state.Dt);
        var askedEntry = enhanced && _liftTime > 0 && driver.Throttle > 0.42 && absSteer > 0.28 &&
            state.Speed is > 8 and < 28 && state.RearAngle > 0.50 && absBeta is > 0.075 and < 0.30 &&
            Math.Sign(driver.Steer) == Math.Sign(state.Yaw);
        var sliding = drifting || absBeta > 0.18 && state.RearAngle > 0.48;
        var desired = Math.Clamp(-driver.Steer * 1.22, -0.98, 0.98);

        if (driver.Brake > 0.30 || driver.Handbrake || driver.Clutch || state.Speed < 3)
        {
            _driftTime = _driftTotalScore = _driftTarget = _driftTransition = _exitTime = _exitStableTime = 0;
            return;
        }

        var overRotation = enhanced && absBeta > 0.65 && absYaw > 0.8;
        if (enhanced && (overRotation || (_driftTime >= 0.12 && absSteer < 0.07 && driver.Throttle < 0.38)) && !DriftExiting)
            RequestDriftExit();
        if (DriftExiting)
        {
            _exitTime = Math.Max(0, _exitTime - state.Dt);
            _exitStableTime = stable ? _exitStableTime + state.Dt : 0;
            if (_exitTime == 0 || _exitStableTime >= 0.09 || absSteer > 0.78)
            {
                _exitTime = _driftTime = _driftTotalScore = _driftTarget = 0;
                return;
            }
            var correction = Math.Clamp(-0.62 * state.Yaw + 0.43 * state.Beta - 0.032 * state.YawAccel, -0.58, 0.58);
            demand.Steer(driver.Steer * 0.35 + correction, 1, SteeringPriority.DriftTransition, DriveAssistFeature.DriftTransitions, 11);
            if (driver.Throttle > 0.10 && !stable)
                demand.LimitThrottle(driver.Throttle * (1 - 0.62 * Unit(absBeta / 0.66)), DriveAssistFeature.DriftTransitions);
            return;
        }

        if (!sliding && !askedEntry)
        {
            _driftTime = _driftTotalScore = _driftTarget = _driftTransition = 0;
            return;
        }

        if (sliding)
        {
            var quality = Unit(1 - 0.78 * Math.Abs(state.Beta - desired) - 0.05 * Math.Abs(state.YawAccel));
            _driftTime += state.Dt;
            _driftTotalScore += quality * state.Dt;
            if (_driftTime >= 0.12) _lastDriftScore = 100 * _driftTotalScore / _driftTime;
        }
        if (!enhanced || absSteer > 0.82) return;

        if (_driftTarget * desired < -0.045) _driftTransition = 0.36;
        else _driftTransition = Math.Max(0, _driftTransition - state.Dt);
        _driftTarget += Math.Clamp(desired - _driftTarget, -3.5 * state.Dt, 3.5 * state.Dt);
        if (!askedEntry && _driftTransition <= 0) return;
        var yawWanted = (state.Beta - _driftTarget) * 1.9;
        var target = driver.Steer * (askedEntry ? 0.64 : 0.28) + (yawWanted - state.Yaw) * 0.62 - 0.027 * state.YawAccel;
        demand.Steer(target, 1, SteeringPriority.DriftTransition, DriveAssistFeature.DriftTransitions, askedEntry ? 9 : 7);
        if (driver.Throttle > 0.15)
            demand.LimitThrottle(driver.Throttle * (_driftTransition > 0 ? 0.72 : 0.88), DriveAssistFeature.DriftTransitions);
    }

    private void LearnSteering(TelemetrySample sample, DriveAssistDriver driver, DriveAssistState state, bool calibrationPass)
    {
        var steerChange = !_hasPrevious ? 0 : driver.Steer - _previous.Steer;
        _steadySteer = _hasPrevious && Math.Abs(steerChange) < 0.015 ? _steadySteer + state.Dt : 0;
        if (!calibrationPass || state.Speed is < 8 or > 46 || driver.Brake > 0.10 || driver.Handbrake || driver.Clutch ||
            Math.Abs(state.Beta) > 0.11 || state.RearAngle > 0.70 || state.FrontCombined > 0.75 || !double.IsFinite(sample.Steer))
        {
            _stepDirection = 0;
            return;
        }

        var absSteer = Math.Abs(driver.Steer);
        if (_steadySteer >= 0.105 && absSteer is >= 0.022 and <= 0.16 && Math.Abs(sample.Steer) < 0.016 && Math.Abs(state.Yaw) < 0.05)
            _learnedDeadzone = Math.Max(_learnedDeadzone ?? 0, absSteer);
        else if (_steadySteer >= 0.105 && _learnedDeadzone is double inferred && absSteer is >= 0.02 and <= 0.16 &&
            Math.Abs(sample.Steer) > 0.025 && absSteer <= inferred)
            _learnedDeadzone = absSteer * 0.8;

        if (Math.Abs(steerChange) >= 0.16 && Math.Abs(_previous.Steer) < 0.10 && Math.Abs(state.Yaw) < 0.085)
        {
            _stepDirection = Math.Sign(steerChange);
            _stepYaw = state.Yaw;
            _stepAge = 0;
        }
        else if (_stepDirection != 0)
        {
            _stepAge += state.Dt;
            if (_stepAge > 0.35 || driver.Steer * _stepDirection < 0.12) _stepDirection = 0;
            else if ((state.Yaw - _stepYaw) * _stepDirection >= 0.10)
            {
                var observed = _stepAge * 1000;
                _steeringDelayMs = _steeringDelayMs is double prior ? prior + (observed - prior) * 0.22 : observed;
                _stepDirection = 0;
            }
        }

        if (_steadySteer < 0.08 || absSteer is < 0.15 or > 0.65 || Math.Abs(state.ExpectedYaw) < 0.14 ||
            Math.Sign(sample.Steer) != Math.Sign(driver.Steer) || Math.Abs(sample.Steer) < 0.10 ||
            Math.Abs(state.YawAccel) > 1.7 || state.LateralG > 0.76)
            return;
        var response = state.Yaw / (state.ExpectedYaw * (sample.Steer / driver.Steer));
        ref var side = ref (driver.Steer > 0 ? ref _right : ref _left);
        side.Observe(response, state.Dt);
    }

    private double? SteeringGain() => _left.Estimate is double left && _right.Estimate is double right
        ? (left + right) * 0.5 : _left.Estimate ?? _right.Estimate;

    private struct Response
    {
        private double _seconds, _gain, _disagreement;
        public readonly double? Estimate => _seconds >= 0.35 ? _gain : null;

        public void Observe(double gain, double dt)
        {
            if (!(gain is >= 0.4 and <= 2.1) || Estimate is double learned && Math.Abs(learned - gain) > Math.Max(0.40, learned * 0.43))
            {
                _disagreement += dt;
                if (_disagreement >= 0.4) this = default;
                return;
            }
            _disagreement = Math.Max(0, _disagreement - dt * 2);
            _gain = _seconds == 0 ? gain : _gain + (gain - _gain) * Math.Min(1, dt / 0.55);
            _seconds += dt;
        }
    }
}
