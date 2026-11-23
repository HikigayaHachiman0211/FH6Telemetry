using System;
using System.Collections.Generic;
using System.Numerics;

namespace FH6Telemetry;

internal enum DriveAssistRunMode { Segment, Lap, Cruise, Skidpad }

internal readonly record struct DriveAssistShare(DriveAssistFeature Feature, DriveAssistPhase Phase, double Seconds, double PhaseSeconds);

internal sealed record DriveAssistRun
{
    public DriveAssistRunMode Mode { get; init; }
    public int CarOrdinal { get; init; }
    public int CarClass { get; init; }
    public int PerformanceIndex { get; init; }
    public int Drivetrain { get; init; }
    public int LapNumber { get; init; } = -1;
    public int SampleCount { get; init; }
    public double Seconds { get; init; }
    public double TargetSpeedKmh { get; init; }
    public double RadiusMeters { get; init; }
    public double MeanSpeedKmh { get; init; }
    public double PeakSpeedKmh { get; init; }
    public double SpeedErrorKmh { get; init; }
    public double MeanLateralG { get; init; }
    public double PeakLateralG { get; init; }
    public double BestSteadyLateralG { get; init; }
    public DriveAssistShare[] Shares { get; init; } = [];
    public bool HasFeedback { get; init; }
    public bool IsPreview { get; init; }
}

internal readonly record struct DriveAssistFrame(double Seconds, DriveAssistPhase Phase, DriveAssistFeature Applied, double LateralG,
    ShiftGamepadReport? Physical = null, ShiftGamepadReport? Output = null);

internal readonly record struct DriveAssistStatus(bool Armed, bool Fresh, DriveAssistPhase Phase, DriveAssistFeature Active,
    bool CruisePending, bool CruiseActive, DriveAssistRun? Segment, DriveAssistRun? PreviousLap, DriveAssistRun? CruiseResult,
    DriveAssistLateralStatus Lateral = default, DriveAssistPedalStatus Pedal = default, CollisionSlowdownState Collision = default,
    int? LearnedTightTurnSpeedKmh = null);

internal sealed class DriveAssistAccumulator(DriveAssistRunMode mode = DriveAssistRunMode.Segment)
{
    private static readonly int PhaseCount = Enum.GetValues<DriveAssistPhase>().Length;
    private readonly double[] _phaseTime = new double[PhaseCount];
    private readonly double[,] _activeTime = new double[PhaseCount, DriveAssistSettings.FeatureCount];
    private double _seconds, _speedSum, _latGSum, _speedErrorSq, _peakSpeed, _peakLatG;
    private double _target, _radius;
    private double _steadyTime, _steadyG, _bestSteadyG;
    private int _count, _lap = -1;
    private TelemetrySample? _last;
    private bool _hasFeedback;

    public bool HasSamples => _last is not null;
    public int CarOrdinal => _last?.CarOrdinal ?? 0;
    public int PerformanceIndex => _last?.CarPerformanceIndex ?? 0;
    public int LapNumber => _lap;

    public void Add(TelemetrySample sample, DriveAssistFrame frame, bool recordInterventions = true, double target = 0,
        double radius = 0, bool steady = false)
    {
        if (_last is null && sample.RacePosition > 0) _lap = sample.LapNumber;
        _last = sample;
        _seconds += frame.Seconds;
        _speedSum += frame.Seconds * sample.SpeedKmh;
        _latGSum += frame.Seconds * frame.LateralG;
        _peakSpeed = Math.Max(_peakSpeed, sample.SpeedKmh);
        _peakLatG = Math.Max(_peakLatG, frame.LateralG);
        if (target > 0) _speedErrorSq += frame.Seconds * Math.Pow(sample.SpeedKmh - target, 2);
        _target = target;
        _radius = radius;
        _count++;
        if (steady)
        {
            _steadyTime += frame.Seconds;
            _steadyG += frame.Seconds / Math.Min(_steadyTime, 0.85) * (frame.LateralG - _steadyG);
            if (_steadyTime >= 0.85) _bestSteadyG = Math.Max(_bestSteadyG, _steadyG);
        }
        else _steadyTime = _steadyG = 0;
        if (!recordInterventions) return;
        _hasFeedback = true;
        var phase = (int)frame.Phase;
        _phaseTime[phase] += frame.Seconds;
        var active = (ulong)frame.Applied & DriveAssistSettings.FeatureMask;
        while (active != 0)
        {
            var bit = BitOperations.TrailingZeroCount(active);
            _activeTime[phase, bit] += frame.Seconds;
            active &= active - 1;
        }
    }

    public DriveAssistRun? Snapshot(DriveAssistRunMode? completedAs = null)
    {
        if (_last is null || _seconds <= 0) return null;
        var shares = new List<DriveAssistShare>();
        for (var phase = 0; phase < PhaseCount; phase++)
        for (var feature = 0; feature < DriveAssistSettings.FeatureCount; feature++)
        {
            var seconds = _activeTime[phase, feature];
            if (seconds > 0)
                shares.Add(new((DriveAssistFeature)(1UL << feature), (DriveAssistPhase)phase, seconds, _phaseTime[phase]));
        }
        return new()
        {
            Mode = completedAs ?? mode, CarOrdinal = _last.CarOrdinal, CarClass = _last.CarClass, PerformanceIndex = _last.CarPerformanceIndex,
            Drivetrain = _last.DrivetrainType, LapNumber = _lap, Seconds = _seconds, SampleCount = _count, HasFeedback = _hasFeedback,
            TargetSpeedKmh = _target, RadiusMeters = _radius, MeanSpeedKmh = _speedSum / _seconds, PeakSpeedKmh = _peakSpeed,
            SpeedErrorKmh = Math.Sqrt(_speedErrorSq / _seconds), MeanLateralG = _latGSum / _seconds, PeakLateralG = _peakLatG,
            BestSteadyLateralG = _bestSteadyG, Shares = shares.ToArray(),
        };
    }
}
