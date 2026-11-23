using System;

namespace FH6Telemetry;

/// <summary>Recognizes recurring torque reversals under load around the car's actual limiter.</summary>
internal sealed class RevLimiterEvidence
{
    private long? _poweredAt;
    private double _poweredRpm;
    private double _poweredSpeed;
    private double _power;
    private double _torque;
    private long _lastCutAt;
    private double _lastCutRpm;
    private int _cuts;
    private (long At, double Rpm, double SpeedKmh) _lastShallowCut;
    private int _shallowCuts;

    public double LastCutRpm => _lastCutRpm;

    public bool Observe(TelemetrySample sample, long now, bool underLoad, bool allowShallowCut, out double shallowCutRpm)
    {
        shallowCutRpm = 0;
        if (!underLoad || sample.Rpm < Math.Max(sample.EngineIdleRpm * 1.8, sample.EngineMaxRpm * 0.62) ||
            !double.IsFinite(sample.PowerKw) || !double.IsFinite(sample.TorqueNm))
        {
            Reset();
            return false;
        }

        var rpmBand = sample.EngineMaxRpm * 0.065;
        if (now - _lastCutAt > 420 || Math.Abs(sample.Rpm - _lastCutRpm) > 1.4 * rpmBand)
            _cuts = 0;
        var speedBand = Math.Max(0.8, sample.SpeedKmh * 0.008);
        if (!allowShallowCut || now - _lastShallowCut.At > 420 ||
            Math.Abs(sample.Rpm - _lastShallowCut.Rpm) > 1.4 * rpmBand ||
            Math.Abs(sample.SpeedKmh - _lastShallowCut.SpeedKmh) > speedBand)
            _shallowCuts = 0;

        if (sample.PowerKw > 0 && sample.TorqueNm > 0)
        {
            _poweredAt = now;
            _poweredRpm = sample.Rpm;
            _poweredSpeed = sample.SpeedKmh;
            _power = sample.PowerKw;
            _torque = sample.TorqueNm;
        }
        else if (_poweredAt is long poweredAt && now - poweredAt <= 90 &&
            Math.Abs(sample.Rpm - _poweredRpm) <= rpmBand)
        {
            if (sample.PowerKw <= -0.15 * _power && sample.TorqueNm <= -0.15 * _torque)
            {
                _poweredAt = null;
                _lastCutAt = now;
                _lastCutRpm = Math.Max(sample.Rpm, _poweredRpm);
                _cuts++;
            }
            // A shallow cut needs a clear tach drop with almost no change in road speed.
            else if (allowShallowCut && _power >= 6 && _torque >= 20 && sample.PowerKw <= -2 && sample.TorqueNm <= -5 &&
                _poweredRpm >= sample.EngineMaxRpm * 0.88 && _poweredRpm - sample.Rpm >= sample.EngineMaxRpm * 0.008 &&
                Math.Abs(sample.SpeedKmh - _poweredSpeed) <= speedBand)
            {
                _poweredAt = null;
                _lastShallowCut = (now, _poweredRpm, sample.SpeedKmh);
                _shallowCuts++;
            }
        }

        if (_shallowCuts >= 2 && now - _lastShallowCut.At <= 120)
            shallowCutRpm = _lastShallowCut.Rpm;
        return _cuts >= 2 && now - _lastCutAt <= 120;
    }

    public void Reset()
    {
        _poweredAt = null;
        _cuts = _shallowCuts = 0;
    }
}
