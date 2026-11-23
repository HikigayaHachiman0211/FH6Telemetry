using System;

namespace FH6Telemetry;

/// <summary>Learns the transmission ratio from driven wheels, then calibrates their rolling speed against the road.</summary>
internal sealed class WheelGearRatioLearning
{
    private readonly double[] _rpmPerWheelRad = new double[AutomaticShiftSettings.MaximumForwardGears + 1];
    private double _rollingRate;
    private double _rollingCandidate;
    private int _rollingSamples;
    private double _gearCandidate;
    private int _candidateGear;
    private uint _candidateSince;
    private uint _lastTimestamp;
    private int _candidateSamples;

    public void Observe(TelemetrySample sample, double drivenSlip)
    {
        if (sample.SpeedKmh < 12 || sample.Clutch >= 0.08 || sample.HandBrake >= 0.08 ||
            sample.Brake >= BrakeShiftStrategy.MinimumBrake || sample.SmashableMass > 0 || sample.SmashableVelDiff > 0.5)
        {
            BreakSequence();
            return;
        }

        var wheelRate = DrivenWheelRate(sample);
        if (wheelRate <= 0)
        {
            BreakSequence();
            return;
        }

        if (sample.SpeedKmh >= 20 && drivenSlip <= 0.16)
            LearnRollingRate(wheelRate / sample.SpeedKmh);

        if (sample.Rpm < sample.EngineIdleRpm * 1.2 || (sample.Throttle < 0.10 && sample.TorqueNm >= -1))
        {
            BreakSequence();
            return;
        }

        var ratio = sample.Rpm / wheelRate;
        var interval = unchecked(sample.TimestampMs - _lastTimestamp);
        if (_candidateGear != sample.Gear || interval is 0 or > 100 || Math.Abs(ratio / _gearCandidate - 1) > 0.035)
        {
            _candidateGear = sample.Gear;
            _candidateSince = sample.TimestampMs;
            _gearCandidate = ratio;
            _candidateSamples = 1;
        }
        else
        {
            _gearCandidate += (ratio - _gearCandidate) * 0.22;
            _candidateSamples = Math.Min(4, _candidateSamples + 1);
            if (_candidateSamples >= 4 && unchecked(sample.TimestampMs - _candidateSince) >= 45)
                _rpmPerWheelRad[sample.Gear] = _gearCandidate;
        }
        _lastTimestamp = sample.TimestampMs;
    }

    private void LearnRollingRate(double rate)
    {
        // Wheel angular speed per km/h depends on tire radius and stays close across gears.
        if (rate is < 0.20 or > 2.40) return;
        if (_rollingSamples == 0 || Math.Abs(rate / _rollingCandidate - 1) > 0.06)
        {
            _rollingCandidate = rate;
            _rollingSamples = 1;
            return;
        }
        _rollingCandidate += (rate - _rollingCandidate) * 0.16;
        _rollingSamples = Math.Min(4, _rollingSamples + 1);
        if (_rollingSamples >= 4) _rollingRate = _rollingCandidate;
    }

    private static double DrivenWheelRate(TelemetrySample sample)
    {
        var front = AxleRate(sample.FrontLeft, sample.FrontRight);
        var rear = AxleRate(sample.RearLeft, sample.RearRight);
        return sample.DrivetrainType switch
        {
            0 => front,
            1 => rear,
            2 when front > 0 && rear > 0 && Math.Abs(front - rear) <= 0.15 * (front + rear) / 2 => (front + rear) / 2,
            _ => 0,
        };
    }

    private static double AxleRate(WheelSample left, WheelSample right)
    {
        var average = (left.RotationSpeedRadPerSec + right.RotationSpeedRadPerSec) / 2;
        return double.IsFinite(average) && left.RotationSpeedRadPerSec > 2 && right.RotationSpeedRadPerSec > 2 &&
               Math.Abs(left.RotationSpeedRadPerSec - right.RotationSpeedRadPerSec) <= 0.30 * average ? average : 0;
    }

    public double RpmPerKmh(int gear) => _rpmPerWheelRad[gear] * _rollingRate;
    public double RpmFromDrivenWheels(TelemetrySample sample, int gear) => _rpmPerWheelRad[gear] * DrivenWheelRate(sample);

    public bool DrivenWheelsOutrunRoad(TelemetrySample sample)
    {
        var driven = DrivenWheelRate(sample);
        if (_rollingRate > 0 && driven / _rollingRate > Math.Max(15, sample.SpeedKmh * 1.22 + 3)) return true;
        if (sample.SpeedKmh < 10 && driven > 20) return true;
        var free = UndrivenWheelRate(sample);
        return free > 0 && driven > free * 1.32;
    }

    public bool HasRollingSpeedAgreement(TelemetrySample sample)
    {
        var driven = DrivenWheelRate(sample);
        var expected = _rollingRate > 0 ? sample.SpeedKmh * _rollingRate : UndrivenWheelRate(sample);
        return driven > 0 && expected > 0 && Math.Abs(driven / expected - 1) <= 0.10;
    }

    private static double UndrivenWheelRate(TelemetrySample sample) =>
        sample.DrivetrainType switch
        {
            0 => AxleRate(sample.RearLeft, sample.RearRight),
            1 => AxleRate(sample.FrontLeft, sample.FrontRight),
            _ => 0,
        };

    public void BreakSequence() => _candidateGear = 0;

    public void Clear()
    {
        BreakSequence();
        _rollingRate = 0;
        _rollingSamples = 0;
        Array.Clear(_rpmPerWheelRad);
    }
}
