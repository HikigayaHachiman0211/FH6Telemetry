using System;
using System.Collections.Generic;
using System.Linq;

namespace FH6Telemetry;

/// <summary>
/// 会话级性能统计（保留原有 API：PeakSpeedKmh / BestZeroToHundredSeconds / Add / Reset）。
/// 计时基于数据包时间戳并对阈值穿越做线性插值，不使用 UI 刷新次数。
/// </summary>
public sealed class PerformanceStats
{
    private int _car;
    private uint _lastTimestamp;
    private bool _hasPrevious;
    private double _elapsedMs;
    private double _lastSpeed;
    private bool _armed;
    private double? _startMs;

    public double PeakSpeedKmh { get; private set; }
    public double PeakPowerKw { get; private set; }
    public double PeakTorqueNm { get; private set; }
    public double PeakRpm { get; private set; }
    public double PeakLateralG { get; private set; }
    public double PeakBrakeG { get; private set; }
    public double? BestZeroToHundredSeconds { get; private set; }
    public double TotalDistanceMeters { get; private set; }
    public long SampleCount { get; private set; }

    public void Reset()
    {
        _car = 0; _hasPrevious = false; _elapsedMs = 0; _lastSpeed = 0; _armed = false; _startMs = null;
        PeakSpeedKmh = 0; PeakPowerKw = 0; PeakTorqueNm = 0; PeakRpm = 0;
        PeakLateralG = 0; PeakBrakeG = 0; BestZeroToHundredSeconds = null;
        TotalDistanceMeters = 0; SampleCount = 0;
    }

    public void InvalidateCurrentRun() { _hasPrevious = false; _armed = false; _startMs = null; }

    public void Add(TelemetrySample sample)
    {
        if (_car != sample.CarOrdinal) { Reset(); _car = sample.CarOrdinal; }
        SampleCount++;
        uint delta = _hasPrevious ? unchecked(sample.TimestampMs - _lastTimestamp) : 0;
        if (delta > 2000) { _hasPrevious = false; _armed = false; _startMs = null; delta = 0; }
        _elapsedMs += delta;
        PeakSpeedKmh = Math.Max(PeakSpeedKmh, sample.SpeedKmh);
        PeakPowerKw = Math.Max(PeakPowerKw, sample.PowerKw);
        PeakTorqueNm = Math.Max(PeakTorqueNm, Math.Abs(sample.TorqueNm));
        PeakRpm = Math.Max(PeakRpm, sample.Rpm);
        if (sample.DistanceTraveledM > TotalDistanceMeters) TotalDistanceMeters = sample.DistanceTraveledM;

        if (sample.SpeedKmh <= 1) { _armed = true; _startMs = null; }
        else if (_armed && _hasPrevious && _lastSpeed <= 1 && sample.SpeedKmh > _lastSpeed)
        {
            _startMs = _elapsedMs - delta + delta * (1 - _lastSpeed) / (sample.SpeedKmh - _lastSpeed);
            _armed = false;
        }
        if (_startMs is double start && _hasPrevious && _lastSpeed < 100 && sample.SpeedKmh >= 100 && sample.SpeedKmh > _lastSpeed)
        {
            var finish = _elapsedMs - delta + delta * (100 - _lastSpeed) / (sample.SpeedKmh - _lastSpeed);
            var seconds = (finish - start) / 1000;
            if (seconds is > 0 and <= 120 && (BestZeroToHundredSeconds is null || seconds < BestZeroToHundredSeconds))
                BestZeroToHundredSeconds = seconds;
            _startMs = null;
        }
        _lastTimestamp = sample.TimestampMs; _lastSpeed = sample.SpeedKmh; _hasPrevious = true;
    }
}

public enum PerformanceTestKind { Acceleration, Braking, Distance, TopSpeed, DriveAssist }

/// <summary>测试项目定义：阈值使用真实单位（加速度/制动为 km/h，距离为 m，极速为 km/h 目标参考值）。</summary>
public sealed record TestDefinition(string Key, string Title, PerformanceTestKind Kind,
    double StartValue, double EndValue, string Unit, bool Enabled = true)
{
    public string RangeText => Kind switch
    {
        PerformanceTestKind.Acceleration => StartValue <= 1 ? $"0–{EndValue:0} km/h" : $"{StartValue:0}–{EndValue:0} km/h",
        PerformanceTestKind.Braking => $"{StartValue:0}–0 km/h",
        PerformanceTestKind.Distance => StartValue <= 1 ? $"0–{EndValue:0} m" : $"{StartValue:0}–{EndValue:0} m",
        _ => TelemetryText.Format("RangeTopSpeedFmt", EndValue),
    };

    public string LocalizedTitle => TestCatalog.LocalizedTitle(this);
}

public static class TestCatalog
{
    public static IReadOnlyList<TestDefinition> All { get; } =
    [
        new("a0_60", "0–60 加速", PerformanceTestKind.Acceleration, 0, 60, "km/h"),
        new("a0_100", "0–100 加速", PerformanceTestKind.Acceleration, 0, 100, "km/h"),
        new("a0_160", "0–160 加速", PerformanceTestKind.Acceleration, 0, 160, "km/h"),
        new("a0_200", "0–200 加速", PerformanceTestKind.Acceleration, 0, 200, "km/h"),
        new("a0_300", "0–300 加速", PerformanceTestKind.Acceleration, 0, 300, "km/h"),
        new("a60_100", "60–100 再加速", PerformanceTestKind.Acceleration, 60, 100, "km/h"),
        new("a80_120", "80–120 超车加速", PerformanceTestKind.Acceleration, 80, 120, "km/h"),
        new("a100_200", "100–200 再加速", PerformanceTestKind.Acceleration, 100, 200, "km/h"),
        new("a100_300", "100–300 动力测试", PerformanceTestKind.Acceleration, 100, 300, "km/h"),
        new("b60_0", "60–0 制动", PerformanceTestKind.Braking, 60, 0, "km/h"),
        new("b100_0", "100–0 制动", PerformanceTestKind.Braking, 100, 0, "km/h"),
        new("b120_0", "120–0 制动", PerformanceTestKind.Braking, 120, 0, "km/h"),
        new("b200_0", "200–0 制动", PerformanceTestKind.Braking, 200, 0, "km/h"),
        new("d100m", "0–100 米", PerformanceTestKind.Distance, 0, 100, "m"),
        new("d200m", "0–200 米", PerformanceTestKind.Distance, 0, 200, "m"),
        new("d400m", "0–400 米", PerformanceTestKind.Distance, 0, 400, "m"),
        new("d402m", "0–1/4 英里（402.336 米）", PerformanceTestKind.Distance, 0, 402.336, "m"),
        new("d800m", "0–800 米", PerformanceTestKind.Distance, 0, 800, "m"),
        new("d1609m", "0–1 英里（1609.344 米）", PerformanceTestKind.Distance, 0, 1609.344, "m"),
        new("topspeed", "最高速度", PerformanceTestKind.TopSpeed, 0, 0, "km/h"),
    ];

    public static TestDefinition CustomAcceleration(double from, double to) =>
        new($"ac_{from:0}_{to:0}", TelemetryText.Format("TestCustomAccelFmt", from, to), PerformanceTestKind.Acceleration, from, to, "km/h");
    public static TestDefinition CustomBraking(double from) =>
        new($"bc_{from:0}", TelemetryText.Format("TestCustomBrakeFmt", from), PerformanceTestKind.Braking, from, 0, "km/h");
    public static TestDefinition CustomDistance(double meters) =>
        new($"dc_{meters:0}", TelemetryText.Format("TestCustomDistFmt", meters), PerformanceTestKind.Distance, 0, meters, "m");

    public static string LocalizedTitle(TestDefinition definition) =>
        LocalizedTitle(definition.Key, definition.StartValue, definition.EndValue, definition.Title);

    public static string LocalizedTitle(string key, double startValue, double endValue, string fallbackTitle)
    {
        if (key is "ecu_segment" or "ecu_lap" or "ecu_cruise" or "ecu_skidpad")
            return TelemetryText.Get(key);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var resourceKey = "TestTitle_" + key;
            var translated = TelemetryText.Get(resourceKey);
            if (!string.Equals(translated, resourceKey, StringComparison.Ordinal)) return translated;
            if (key.StartsWith("ac_", StringComparison.Ordinal))
                return TelemetryText.Format("TestCustomAccelFmt", startValue, endValue);
            if (key.StartsWith("bc_", StringComparison.Ordinal))
                return TelemetryText.Format("TestCustomBrakeFmt", startValue);
            if (key.StartsWith("dc_", StringComparison.Ordinal))
                return TelemetryText.Format("TestCustomDistFmt", endValue);
        }
        return fallbackTitle;
    }
}

public enum TestRunState { Idle, Armed, Running, Completed, Invalid, Timeout, Cancelled }

/// <summary>一次完成的测试成绩：所有字段都来自真实采样或插值，保留数据质量信息。</summary>
public sealed record TestResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public PerformanceTestKind Kind { get; init; }
    public double StartValue { get; init; }
    public double EndValue { get; init; }
    public double DurationSeconds { get; init; }
    public double DistanceMeters { get; init; }
    public double StartSpeedKmh { get; init; }
    public double EndSpeedKmh { get; init; }
    public double PeakSpeedKmh { get; init; }
    public double MaxAccelerationG { get; init; }
    public double MaxBrakingG { get; init; }
    public double PeakPowerKw { get; init; }
    public double PeakTorqueNm { get; init; }
    public double PeakRpm { get; init; }
    public int CarOrdinal { get; init; }
    public string CarName { get; init; } = "";
    public int CarClass { get; init; }
    public int CarPerformanceIndex { get; init; }
    public int DrivetrainType { get; init; }
    public int GearAtFinish { get; init; }
    public long SampleCount { get; init; }
    public bool Valid { get; init; } = true;
    public string InvalidReason { get; init; } = "";
    public DateTime RecordedAtLocal { get; init; } = DateTime.Now;
    public string Note { get; init; } = "";
    public string LocalizedTitle => TestCatalog.LocalizedTitle(Key, StartValue, EndValue, Title);
    /// <summary>测试开始前的车辆参数快照（用于调校前后对比）。</summary>
    public Dictionary<string, string> VehicleParameters { get; init; } = new();
    internal DriveAssistRun? DriveAssist { get; init; }
}

/// <summary>
/// 性能测试状态机：每个启用的测试项目独立运行，自动触发/计时/判定终点/超时/中断失效。
/// 计时完全基于数据包时间戳（对阈值穿越插值），不依赖 UI 刷新率。
/// </summary>
public sealed class PerformanceTestEngine
{
    private sealed class Monitor
    {
        public required TestDefinition Definition;
        public TestRunState State = TestRunState.Armed;
        public uint StartTimestamp;
        public double StartDistance;
        public double StartSpeed;
        public double PeakSpeed;
        public double MaxG;
        public double MaxBrakeG;
        public double PeakPower;
        public double PeakTorque;
        public double PeakRpm;
        public int Samples;
        public bool HasPrevious;
        public uint LastTimestamp;
        public double PreviousSpeed;
        public double PreviousDistance;
        public uint PreviousTimestamp;
        public int Gear = -1;
        public DriveAssistAccumulator? Assist;
    }

    private readonly List<Monitor> _monitors = new();
    private readonly TelemetrySettings _settings;
    private int _car = -1;
    private readonly List<TestResult> _recent = new();
    private string _carName = "";

    public PerformanceTestEngine(TelemetrySettings settings) { _settings = settings; }

    public IReadOnlyList<TestResult> Recent => _recent;
    public TestRunState CurrentState => _monitors.Any(m => m.State == TestRunState.Running)
        ? TestRunState.Running
        : _monitors.Any(m => m.State == TestRunState.Armed) ? TestRunState.Armed : TestRunState.Idle;
    public string ActiveTestTitles => string.Join(
        TelemetryText.CurrentLanguage is TelemetryLanguage.ZhCn or TelemetryLanguage.Ja ? "、" : ", ",
        _monitors.Where(m => m.State == TestRunState.Running).Select(m => m.Definition.LocalizedTitle));

    public void SetEnabled(IEnumerable<TestDefinition> definitions)
    {
        _monitors.Clear();
        foreach (var definition in definitions) _monitors.Add(new Monitor { Definition = definition });
    }

    public void SetCarName(string name) => _carName = name;

    public void Reset()
    {
        _recent.Clear();
        foreach (var monitor in _monitors) ResetMonitor(monitor);
    }

    private static void ResetMonitor(Monitor monitor)
    {
        monitor.State = TestRunState.Armed;
        monitor.StartTimestamp = 0; monitor.StartDistance = 0; monitor.StartSpeed = 0;
        monitor.PeakSpeed = 0; monitor.MaxG = 0; monitor.MaxBrakeG = 0;
        monitor.PeakPower = 0; monitor.PeakTorque = 0; monitor.PeakRpm = 0;
        monitor.Samples = 0; monitor.HasPrevious = false; monitor.Gear = -1;
        monitor.Assist = null;
    }

    /// <summary>喂入一帧数据，返回本帧新完成的有效成绩。</summary>
    public IReadOnlyList<TestResult> Feed(TelemetrySample sample, TelemetryDerived derived) => Feed(sample, derived, null);

    internal IReadOnlyList<TestResult> Feed(TelemetrySample sample, TelemetryDerived derived, DriveAssistFrame? assist)
    {
        if (_car >= 0 && _car != sample.CarOrdinal)
        {
            _car = sample.CarOrdinal;
            foreach (var monitor in _monitors) ResetMonitor(monitor);
        }
        _car = sample.CarOrdinal;

        List<TestResult>? completed = null;
        foreach (var monitor in _monitors)
        {
            var definition = monitor.Definition;
            var speed = sample.SpeedKmh;

            if (!monitor.HasPrevious)
            {
                monitor.HasPrevious = true;
                monitor.PreviousSpeed = speed;
                monitor.PreviousDistance = sample.DistanceTraveledM;
                monitor.PreviousTimestamp = sample.TimestampMs;
                continue;
            }

            var deltaMs = unchecked(sample.TimestampMs - monitor.PreviousTimestamp);
            if (deltaMs > 2000)
            {
                // 数据中断：正在跑的测试作废，重新等待起步条件。
                if (monitor.State == TestRunState.Running) ResetMonitor(monitor);
                monitor.PreviousSpeed = speed;
                monitor.PreviousDistance = sample.DistanceTraveledM;
                monitor.PreviousTimestamp = sample.TimestampMs;
                continue;
            }
            if (deltaMs == 0)
            {
                monitor.PreviousSpeed = speed;
                monitor.PreviousDistance = sample.DistanceTraveledM;
                continue;
            }

            var wasRunning = monitor.State == TestRunState.Running;
            if (assist is { } driving && wasRunning)
                (monitor.Assist ??= new()).Add(sample, driving);
            var finish = Transition(monitor, sample, derived, deltaMs);
            if (assist is { } first && !wasRunning && monitor.State == TestRunState.Running)
            {
                var seconds = Math.Min(first.Seconds, unchecked(sample.TimestampMs - monitor.StartTimestamp) / 1000.0);
                if (seconds > 0) (monitor.Assist ??= new()).Add(sample, first with { Seconds = seconds });
            }
            if (finish is not null) (completed ??= new()).Add(finish);

            monitor.PreviousSpeed = speed;
            monitor.PreviousDistance = sample.DistanceTraveledM;
            monitor.PreviousTimestamp = sample.TimestampMs;
            monitor.LastTimestamp = sample.TimestampMs;
            monitor.Samples++;
        }

        if (completed is null) return Array.Empty<TestResult>();
        _recent.AddRange(completed);
        if (_recent.Count > 200) _recent.RemoveRange(0, _recent.Count - 200);
        return completed;
    }

    private TestResult? Transition(Monitor monitor, TelemetrySample sample, TelemetryDerived derived, uint deltaMs)
    {
        var definition = monitor.Definition;
        var speed = sample.SpeedKmh;
        var previousSpeed = monitor.PreviousSpeed;

        // 运行中：累计峰值、检查超时。
        if (monitor.State == TestRunState.Running)
        {
            monitor.PeakSpeed = Math.Max(monitor.PeakSpeed, speed);
            monitor.MaxG = Math.Max(monitor.MaxG, derived.SmoothedLongitudinalG);
            monitor.MaxBrakeG = Math.Min(monitor.MaxBrakeG, derived.SmoothedLongitudinalG);
            monitor.PeakPower = Math.Max(monitor.PeakPower, sample.PowerKw);
            monitor.PeakTorque = Math.Max(monitor.PeakTorque, Math.Abs(sample.TorqueNm));
            monitor.PeakRpm = Math.Max(monitor.PeakRpm, sample.Rpm);
            if (sample.Gear > 0) monitor.Gear = sample.Gear;
            var elapsed = unchecked(sample.TimestampMs - monitor.StartTimestamp);
            if (elapsed > _settings.MaxTestSeconds * 1000)
            {
                ResetMonitor(monitor);
                return null;
            }
        }

        return definition.Kind switch
        {
            PerformanceTestKind.Acceleration => AccelerationStep(monitor, sample, derived, deltaMs),
            PerformanceTestKind.Braking => BrakingStep(monitor, sample, derived, deltaMs),
            PerformanceTestKind.Distance => DistanceStep(monitor, sample, derived, deltaMs),
            _ => TopSpeedStep(monitor, sample, derived, deltaMs),
        };
    }

    private TestResult? AccelerationStep(Monitor monitor, TelemetrySample sample, TelemetryDerived derived, uint deltaMs)
    {
        var definition = monitor.Definition;
        var speed = sample.SpeedKmh;
        var previous = monitor.PreviousSpeed;
        var startThreshold = definition.StartValue <= 1 ? _settings.StartSpeedKmh : definition.StartValue;

        if (monitor.State == TestRunState.Armed)
        {
            if (previous < startThreshold && speed >= startThreshold)
            {
                monitor.State = TestRunState.Running;
                monitor.StartTimestamp = InterpolateTimestamp(monitor.PreviousTimestamp, sample.TimestampMs, previous, speed, startThreshold);
                monitor.StartDistance = sample.DistanceTraveledM;
                monitor.StartSpeed = startThreshold;
                monitor.PeakSpeed = speed; monitor.MaxG = 0; monitor.MaxBrakeG = 0;
                monitor.PeakPower = sample.PowerKw; monitor.PeakTorque = Math.Abs(sample.TorqueNm); monitor.PeakRpm = sample.Rpm;
            }
            return null;
        }

        if (monitor.State != TestRunState.Running) return null;
        if (previous < definition.EndValue && speed >= definition.EndValue)
        {
            var finishTimestamp = InterpolateTimestamp(monitor.PreviousTimestamp, sample.TimestampMs, previous, speed, definition.EndValue);
            var duration = unchecked(finishTimestamp - monitor.StartTimestamp) / 1000.0;
            var distance = Math.Max(0, sample.DistanceTraveledM - monitor.StartDistance);
            var result = Build(monitor, definition, duration, distance, definition.EndValue, sample, derived);
            ResetMonitor(monitor);
            return result;
        }
        return null;
    }

    private TestResult? BrakingStep(Monitor monitor, TelemetrySample sample, TelemetryDerived derived, uint deltaMs)
    {
        var definition = monitor.Definition;
        var speed = sample.SpeedKmh;
        var previous = monitor.PreviousSpeed;

        if (monitor.State == TestRunState.Armed)
        {
            // 制动测试：从高于阈值的速度开始，速度向下穿越阈值后开始计时。
            if (previous >= definition.StartValue && speed < definition.StartValue)
            {
                monitor.State = TestRunState.Running;
                monitor.StartTimestamp = InterpolateTimestamp(monitor.PreviousTimestamp, sample.TimestampMs, previous, speed, definition.StartValue);
                monitor.StartDistance = sample.DistanceTraveledM;
                monitor.StartSpeed = definition.StartValue;
                monitor.PeakSpeed = previous;
                monitor.MaxG = 0; monitor.MaxBrakeG = derived.SmoothedLongitudinalG;
                monitor.PeakPower = sample.PowerKw; monitor.PeakTorque = Math.Abs(sample.TorqueNm); monitor.PeakRpm = sample.Rpm;
            }
            return null;
        }
        if (monitor.State != TestRunState.Running) return null;
        if (previous > 1 && speed <= 1)
        {
            var finishTimestamp = InterpolateTimestamp(monitor.PreviousTimestamp, sample.TimestampMs, previous, speed, 1);
            var duration = unchecked(finishTimestamp - monitor.StartTimestamp) / 1000.0;
            var distance = Math.Max(0, sample.DistanceTraveledM - monitor.StartDistance);
            var result = Build(monitor, definition, duration, distance, 0, sample, derived);
            ResetMonitor(monitor);
            return result;
        }
        return null;
    }

    private TestResult? DistanceStep(Monitor monitor, TelemetrySample sample, TelemetryDerived derived, uint deltaMs)
    {
        var definition = monitor.Definition;
        var distance = sample.DistanceTraveledM;
        var previous = monitor.PreviousDistance;
        var speed = sample.SpeedKmh;

        if (monitor.State == TestRunState.Armed)
        {
            if (monitor.PreviousSpeed < _settings.StartSpeedKmh && speed >= _settings.StartSpeedKmh)
            {
                monitor.State = TestRunState.Running;
                monitor.StartTimestamp = InterpolateTimestamp(monitor.PreviousTimestamp, sample.TimestampMs,
                    monitor.PreviousSpeed, speed, _settings.StartSpeedKmh);
                monitor.StartDistance = previous;
                monitor.StartSpeed = _settings.StartSpeedKmh;
                monitor.PeakSpeed = speed; monitor.MaxG = 0; monitor.MaxBrakeG = 0;
                monitor.PeakPower = sample.PowerKw; monitor.PeakTorque = Math.Abs(sample.TorqueNm); monitor.PeakRpm = sample.Rpm;
            }
            return null;
        }
        if (monitor.State != TestRunState.Running) return null;

        var traveled = distance - monitor.StartDistance;
        if (previous - monitor.StartDistance < definition.EndValue && traveled >= definition.EndValue)
        {
            var travelledBefore = previous - monitor.StartDistance;
            var fraction = definition.EndValue - travelledBefore;
            var deltaDistance = traveled - travelledBefore;
            var fractionMs = deltaDistance <= 0 ? 0 : deltaMs * (fraction / deltaDistance);
            var finishTimestamp = monitor.PreviousTimestamp + (uint)Math.Round(fractionMs);
            var duration = unchecked(finishTimestamp - monitor.StartTimestamp) / 1000.0;
            var result = Build(monitor, definition, duration, definition.EndValue, sample.SpeedKmh, sample, derived);
            ResetMonitor(monitor);
            return result;
        }
        return null;
    }

    private TestResult? TopSpeedStep(Monitor monitor, TelemetrySample sample, TelemetryDerived derived, uint deltaMs)
    {
        // 极速测试：持续记录本次会话最高速度，不自动结束（由用户点击“完成极速测试”）。
        monitor.PeakSpeed = Math.Max(monitor.PeakSpeed, sample.SpeedKmh);
        monitor.MaxG = Math.Max(monitor.MaxG, derived.SmoothedLongitudinalG);
        monitor.PeakPower = Math.Max(monitor.PeakPower, sample.PowerKw);
        monitor.PeakTorque = Math.Max(monitor.PeakTorque, Math.Abs(sample.TorqueNm));
        monitor.PeakRpm = Math.Max(monitor.PeakRpm, sample.Rpm);
        if (sample.Gear > 0) monitor.Gear = sample.Gear;
        if (monitor.State != TestRunState.Running && sample.SpeedKmh >= _settings.StartSpeedKmh)
        {
            monitor.State = TestRunState.Running;
            monitor.StartTimestamp = sample.TimestampMs;
            monitor.StartDistance = sample.DistanceTraveledM;
        }
        return null;
    }

    /// <summary>手动结束极速测试，生成成绩。</summary>
    public TestResult? FinishTopSpeed(TelemetrySample? lastSample = null)
    {
        var monitor = _monitors.FirstOrDefault(m => m.Definition.Kind == PerformanceTestKind.TopSpeed);
        if (monitor is null || monitor.State != TestRunState.Running) return null;
        var sample = lastSample ?? new TelemetrySample { TimestampMs = monitor.LastTimestamp };
        var duration = unchecked(sample.TimestampMs - monitor.StartTimestamp) / 1000.0;
        var result = Build(monitor, monitor.Definition, duration, Math.Max(0, sample.DistanceTraveledM - monitor.StartDistance),
            sample.SpeedKmh, sample, new TelemetryDerived());
        ResetMonitor(monitor);
        _recent.Add(result);
        return result;
    }

    private TestResult Build(Monitor monitor, TestDefinition definition, double duration, double distance,
        double endSpeed, TelemetrySample sample, TelemetryDerived derived)
    {
        var valid = duration > 0.05 && duration <= _settings.MaxTestSeconds && monitor.Samples >= 3;
        return new TestResult
        {
            Key = definition.Key,
            Title = definition.Title,
            Kind = definition.Kind,
            StartValue = definition.StartValue,
            EndValue = definition.Kind == PerformanceTestKind.Braking ? 0 : definition.EndValue,
            DurationSeconds = duration,
            DistanceMeters = distance,
            StartSpeedKmh = monitor.StartSpeed,
            EndSpeedKmh = endSpeed,
            PeakSpeedKmh = monitor.PeakSpeed,
            MaxAccelerationG = monitor.MaxG,
            MaxBrakingG = Math.Abs(monitor.MaxBrakeG),
            PeakPowerKw = monitor.PeakPower,
            PeakTorqueNm = monitor.PeakTorque,
            PeakRpm = monitor.PeakRpm,
            CarOrdinal = sample.CarOrdinal,
            CarName = _carName,
            CarClass = sample.CarClass,
            CarPerformanceIndex = sample.CarPerformanceIndex,
            DrivetrainType = sample.DrivetrainType,
            GearAtFinish = sample.Gear,
            SampleCount = monitor.Samples,
            DriveAssist = monitor.Assist?.Snapshot(),
            Valid = valid,
            InvalidReason = valid ? "" : TelemetryText.Get("ErrInvalidTestSample"),
        };
    }

    /// <summary>两个阈值之间的时间戳插值：数据包间隔内的穿越时刻，精度受采样率限制。</summary>
    private static uint InterpolateTimestamp(uint t0, uint t1, double v0, double v1, double target)
    {
        var span = unchecked(t1 - t0);
        if (v1 == v0) return t1;
        var fraction = Math.Clamp((target - v0) / (v1 - v0), 0, 1);
        return t0 + (uint)Math.Round(span * fraction);
    }
}
