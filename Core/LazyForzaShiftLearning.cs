// Learning gates, medians, and tune-change evidence adapted from LazyForza's ShiftLearner (MIT).
// Copyright (c) 2026 Laz2y. See TelemetryApp/ThirdPartyNotices.txt.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FH6Telemetry;

internal readonly record struct ShiftVehicleConfiguration(int Ordinal, int CarClass, int Pi, int Drivetrain,
    int Cylinders, int RoundedMaxRpm)
{
    public static ShiftVehicleConfiguration From(TelemetrySample sample) => new(sample.CarOrdinal,
        ResolveClass(sample.CarClass, sample.CarPerformanceIndex), sample.CarPerformanceIndex, sample.DrivetrainType,
        sample.NumCylinders, (int)(Math.Round(sample.EngineMaxRpm / 50) * 50));

    public bool Matches(ShiftVehicleConfiguration other) =>
        (this with { RoundedMaxRpm = other.RoundedMaxRpm }) == other &&
        Math.Abs(RoundedMaxRpm - other.RoundedMaxRpm) <= 200;

    private static int ResolveClass(int carClass, int pi) => carClass is >= 0 and <= 7 ? carClass : pi switch
    {
        <= 400 => 0,
        <= 500 => 1,
        <= 600 => 2,
        <= 700 => 3,
        <= 800 => 4,
        <= 900 => 5,
        <= 998 => 6,
        _ => 7,
    };
}

internal sealed class LazyForzaShiftLearning
{
    private const int BinSize = 200;
    private const int MinimumSamplesPerBin = 4;
    private const int MinimumGearSamples = 10;
    private const int RefreshMs = 100;
    private readonly Dictionary<int, List<(double Power, double Torque)>> _bins = new();
    private readonly Dictionary<int, List<double>> _gears = new();
    private readonly Dictionary<int, int> _gearMismatch = new();
    private readonly Dictionary<int, int> _powerMismatch = new();
    private readonly double[] _learnedSlopes = new double[AutomaticShiftSettings.MaximumForwardGears + 1];
    private TelemetrySample? _previous;
    private long _refreshAt;
    private bool _dirty;
    private int _stableSamples;

    public IReadOnlyList<LazyForzaShiftTarget> Targets { get; private set; } = [];
    public int ReadyBins { get; private set; }
    public int LearnedGears { get; private set; }

    /// <returns>True if the samples establish a tune change within the same vehicle configuration.</returns>
    public bool Observe(TelemetrySample sample, int engineMaxRpm, long now)
    {
        var previous = _previous;
        _previous = sample;
        var retuned = false;
        var newRatio = false;
        if (CanLearn(sample, previous))
        {
            var bin = (int)(Math.Round(sample.Rpm / BinSize) * BinSize);
            var slope = sample.Rpm / (sample.SpeedKmh / 3.6);
            var power = sample.PowerKw * 1000;

            if (HasTuneMismatch(sample.Gear, slope, bin, power))
            {
                _stableSamples = 0;
                if (_gearMismatch.Values.Count(count => count >= 8) >= 2 ||
                    _powerMismatch.Values.Count(count => count >= 8) >= 3)
                {
                    Clear();
                    _previous = sample;
                    retuned = true;
                }
            }
            else
            {
                if (++_stableSamples >= 45)
                {
                    _gearMismatch.Clear();
                    _powerMismatch.Clear();
                    _stableSamples = 0;
                }
                AddBounded(_bins, bin, (power, sample.TorqueNm), 256);
                var count = AddBounded(_gears, sample.Gear, slope, 512);
                newRatio = count == MinimumGearSamples;
                _dirty = true;
            }
        }
        if (_dirty && (now >= _refreshAt || newRatio))
            Refresh(engineMaxRpm, now);
        return retuned;
    }

    private static bool CanLearn(TelemetrySample sample, TelemetrySample? previous)
    {
        if (!sample.IsRaceOn || sample.Throttle < 0.90 || sample.SpeedKmh < 36 ||
            !double.IsFinite(sample.PowerKw) || !double.IsFinite(sample.TorqueNm) ||
            sample.PowerKw <= 0 || sample.TorqueNm <= 0 || sample.Clutch > 0.08 ||
            sample.Gear is < 1 or > AutomaticShiftSettings.MaximumForwardGears ||
            sample.SmashableMass > 0 || sample.SmashableVelDiff > 0.5 || previous is null || previous.Gear != sample.Gear)
            return false;

        var interval = unchecked(sample.TimestampMs - previous.TimestampMs);
        return interval is > 0 and <= 250 &&
            Math.Abs(previous.PositionX - sample.PositionX) <= 25 &&
            Math.Abs(previous.PositionZ - sample.PositionZ) <= 25 &&
            HasGrip(sample.FrontLeft) && HasGrip(sample.FrontRight) && HasGrip(sample.RearLeft) && HasGrip(sample.RearRight);
    }

    private static bool HasGrip(WheelSample wheel) =>
        Math.Abs(wheel.SlipRatio) <= 0.18 && Math.Abs(wheel.CombinedSlip) <= 0.18;

    private bool HasTuneMismatch(int gear, double slope, int bin, double power)
    {
        var mismatch = false;
        if (_gears.TryGetValue(gear, out var slopes) && slopes.Count >= MinimumGearSamples)
        {
            var expected = Median(slopes);
            if (expected > 1 && Math.Abs(slope - expected) / expected >= 0.10)
            {
                _gearMismatch[gear] = _gearMismatch.GetValueOrDefault(gear) + 1;
                mismatch = true;
            }
        }
        if (_bins.TryGetValue(bin, out var samples) && samples.Count >= MinimumSamplesPerBin)
        {
            var expected = Median(samples.Select(value => value.Power));
            var difference = Math.Abs(power - expected);
            if (Math.Abs(expected) >= 30_000 && difference >= 30_000 && difference / Math.Abs(expected) >= 0.25)
            {
                _powerMismatch[bin] = _powerMismatch.GetValueOrDefault(bin) + 1;
                mismatch = true;
            }
        }
        return mismatch;
    }

    private void Refresh(int engineMaxRpm, long now)
    {
        var curve = _bins.Select(pair => new ShiftCurveBin(pair.Key, Median(pair.Value.Select(value => value.Torque)))).ToArray();
        var gears = _gears.Where(pair => pair.Value.Count >= MinimumGearSamples)
            .Select(pair => new ShiftGearModel(pair.Key, Median(pair.Value))).ToArray();
        ReadyBins = _bins.Values.Count(bucket => bucket.Count >= MinimumSamplesPerBin);
        LearnedGears = gears.Length;
        foreach (var gear in gears) _learnedSlopes[gear.Gear] = gear.RpmPerMeterPerSecond;
        Targets = LazyForzaShiftAdvisor.Calculate(curve, gears, engineMaxRpm);
        _refreshAt = now + RefreshMs;
        _dirty = false;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    private static int AddBounded<T>(Dictionary<int, List<T>> groups, int key, T value, int limit)
    {
        if (!groups.TryGetValue(key, out var bucket))
            groups[key] = bucket = new List<T>();
        if (bucket.Count == limit) bucket.RemoveAt(0);
        bucket.Add(value);
        return bucket.Count;
    }

    public void BreakSequence() => _previous = null;

    public bool MatchesGearSlope(TelemetrySample sample)
    {
        var slope = _learnedSlopes[sample.Gear];
        return slope == 0 || sample.SpeedKmh < 12 ||
            Math.Abs(sample.Rpm / (sample.SpeedKmh / 3.6 * slope) - 1) < 0.10;
    }

    public double RpmPerKmh(int gear) => _learnedSlopes[gear] / 3.6;

    public void Clear()
    {
        _bins.Clear();
        _gears.Clear();
        _gearMismatch.Clear();
        _powerMismatch.Clear();
        Array.Clear(_learnedSlopes);
        _stableSamples = 0;
        _dirty = false;
        _refreshAt = 0;
        Targets = [];
        ReadyBins = LearnedGears = 0;
        BreakSequence();
    }
}
