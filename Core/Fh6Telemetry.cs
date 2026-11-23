using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace FH6Telemetry;

/// <summary>
/// FH6 Data Out 324 字节数据包（Forza "Data Out" 文档格式：Sled + Dash + Horizon 扩展）。
///
/// 字节偏移全部来自官方文档与社区实现，未经校验的字段一律不读取：
///   0 IsRaceOn(i32) 4 TimestampMS(u32) 8 EngineMaxRpm 12 EngineIdleRpm 16 CurrentEngineRpm
///   20/24/28 Acceleration X/Y/Z 32/36/40 Velocity X/Y/Z 44/48/52 AngularVelocity X/Y/Z
///   56 Yaw 60 Pitch 64 Roll
///   68..80 NormalizedSuspensionTravel FL/FR/RL/RR
///   84..96 TireSlipRatio，100..112 WheelRotationSpeed(rad/s)
///   116..128 WheelOnRumbleStrip(i32) 132..144 WheelInPuddle(i32) 148..160 SurfaceRumble
///   164..176 TireSlipAngle 180..192 TireCombinedSlip 196..208 SuspensionTravelMeters
///   212 CarOrdinal(i32) 216 CarClass(i32) 220 CarPerformanceIndex(i32) 224 DrivetrainType(i32) 228 NumCylinders(i32)
///   232 CarGroup(i32) 236 SmashableVelDiff 240 SmashableMass 244/248/252 Position X/Y/Z
///   256 Speed(m/s) 260 Power(watt) 264 Torque(Nm) 268..280 TireTemp FL/FR/RL/RR
///   284 Boost 288 Fuel 292 DistanceTraveled 296 BestLap 300 LastLap 304 CurrentLap 308 CurrentRaceTime
///   312 LapNumber(u16) 314 RacePosition(u8) 315 Accel 316 Brake 317 Clutch 318 HandBrake
///   319 Gear(u8，0=倒挡) 320 Steer(i8，-127..127) 321 NormalizedDrivingLine 322 NormalizedAIBrakeDifference
///
/// 单位约定（模型层统一）：速度 km/h、功率 kW、扭矩 N·m、温度 °C、加速度 m/s²、距离 m、时间 ms。
/// </summary>
public readonly record struct WheelSample(
    double SlipRatio, double SlipAngle, double CombinedSlip, double RotationSpeedRadPerSec,
    bool OnRumbleStrip, double PuddleDepth, double SurfaceRumble,
    double SuspensionTravelMeters, double NormalizedSuspensionTravel, double TireTempC);

/// <summary>一帧标准化遥测数据：直接采集字段为原值换算，衍生字段由 <see cref="TelemetryDerived"/> 计算。</summary>
public sealed record TelemetrySample
{
    public uint TimestampMs { get; init; }
    public bool IsRaceOn { get; init; }
    public int CarOrdinal { get; init; }
    public int CarClass { get; init; }
    public int CarPerformanceIndex { get; init; }
    public int DrivetrainType { get; init; }       // 0=FWD 1=RWD 2=AWD
    public int NumCylinders { get; init; }
    public int CarGroup { get; init; }
    public double SmashableVelDiff { get; init; }
    public double SmashableMass { get; init; }
    public double EngineMaxRpm { get; init; }
    public double EngineIdleRpm { get; init; }
    public double Rpm { get; init; }
    public double SpeedKmh { get; init; }
    public double PowerKw { get; init; }
    public double TorqueNm { get; init; }
    public double BoostPsi { get; init; }
    public double FuelRatio { get; init; }
    public double DistanceTraveledM { get; init; }
    public double BestLapSeconds { get; init; }
    public double LastLapSeconds { get; init; }
    public double CurrentLapSeconds { get; init; }
    public double CurrentRaceTimeSeconds { get; init; }
    public int LapNumber { get; init; }
    public int RacePosition { get; init; }
    public double Throttle { get; init; }          // 0..1
    public double Brake { get; init; }             // 0..1
    public double Clutch { get; init; }            // 0..1
    public double HandBrake { get; init; }         // 0..1
    public int Gear { get; init; }                 // 0=倒挡
    public double Steer { get; init; }             // -1..1
    public double PositionX { get; init; }
    public double PositionY { get; init; }
    public double PositionZ { get; init; }
    public double AccelerationX { get; init; }     // m/s²（右）
    public double AccelerationY { get; init; }     // m/s²（上）
    public double AccelerationZ { get; init; }     // m/s²（前）
    public double VelocityX { get; init; }
    public double VelocityY { get; init; }
    public double VelocityZ { get; init; }
    public double AngularVelocityX { get; init; }  // rad/s（俯仰角速度）
    public double AngularVelocityY { get; init; }  // rad/s（横摆角速度）
    public double AngularVelocityZ { get; init; }  // rad/s（侧倾角速度）
    public double Yaw { get; init; }
    public double Pitch { get; init; }
    public double Roll { get; init; }
    public WheelSample FrontLeft { get; init; }
    public WheelSample FrontRight { get; init; }
    public WheelSample RearLeft { get; init; }
    public WheelSample RearRight { get; init; }

    public WheelSample Wheel(int index) => index switch
    {
        0 => FrontLeft, 1 => FrontRight, 2 => RearLeft, _ => RearRight,
    };

    internal bool IsPreRaceGrid => RacePosition > 0 &&
        (CurrentRaceTimeSeconds < 0 || CurrentRaceTimeSeconds == 0 && (SpeedKmh < 12 || DistanceTraveledM < 25));

    internal bool StartsNewDrivingSegment(TelemetrySample previous, long elapsedMs)
    {
        if ((previous.RacePosition == 0) != (RacePosition == 0) ||
            double.IsFinite(previous.CurrentRaceTimeSeconds) && double.IsFinite(CurrentRaceTimeSeconds) &&
                previous.CurrentRaceTimeSeconds > 0.5 && CurrentRaceTimeSeconds + 0.5 < previous.CurrentRaceTimeSeconds ||
            double.IsFinite(previous.DistanceTraveledM) && double.IsFinite(DistanceTraveledM) &&
                previous.DistanceTraveledM > 20 && DistanceTraveledM + 10 < previous.DistanceTraveledM ||
            previous.LapNumber > 0 && LapNumber < previous.LapNumber)
            return true;

        if (!previous.HasReportedPosition || !HasReportedPosition) return false;
        var dx = PositionX - previous.PositionX;
        var dy = PositionY - previous.PositionY;
        var dz = PositionZ - previous.PositionZ;
        var seconds = Math.Clamp(elapsedMs / 1000.0, 0.01, 1.0);
        var maximumTravel = Math.Max(45.0, SpeedKmh / 3.6 * seconds * 4.0 + 30.0);
        return dx * dx + dy * dy + dz * dz > maximumTravel * maximumTravel;
    }

    private bool HasReportedPosition => double.IsFinite(PositionX) && double.IsFinite(PositionY) && double.IsFinite(PositionZ) &&
        Math.Abs(PositionX) + Math.Abs(PositionY) + Math.Abs(PositionZ) > 1;

    public string DrivetrainName => DrivetrainType switch
    {
        0 => TelemetryText.Get("DriveFwd"),
        1 => TelemetryText.Get("DriveRwd"),
        2 => TelemetryText.Get("DriveAwd"),
        _ => TelemetryText.Get("DriveUnknown"),
    };
}

public static class Fh6Telemetry
{
    public const int PacketLength = 324;

    /// <summary>解析一帧 Data Out 数据；格式不符或数值非法时返回 false（调用方计入解析失败）。</summary>
    public static bool TryRead(ReadOnlySpan<byte> packet, out TelemetrySample sample)
    {
        sample = null!;
        if (packet.Length != PacketLength) return false;
        // 官方文档：前 4 字节为 IsRaceOn（1=比赛中/自由漫游），部分场景为 0；仅拒绝完全无意义的值。
        var raceOn = BinaryPrimitives.ReadInt32LittleEndian(packet);
        if (raceOn is not (0 or 1)) return false;

        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        var speed = Float(packet, 256); var power = Float(packet, 260); var torque = Float(packet, 264);
        var rpm = Float(packet, 16);
        if (!float.IsFinite(speed) || speed is < 0 or > 200 ||        // m/s，200 m/s = 720 km/h 上限
            !float.IsFinite(power) || Math.Abs(power) > 100_000_000 ||
            !float.IsFinite(torque) || Math.Abs(torque) > 1_000_000 ||
            !float.IsFinite(rpm) || rpm is < 0 or > 100_000) return false;

        var car = BinaryPrimitives.ReadInt32LittleEndian(packet[212..]);
        if (car <= 0) return false;

        sample = new TelemetrySample
        {
            TimestampMs = timestamp,
            IsRaceOn = raceOn == 1,
            CarOrdinal = car,
            CarClass = BinaryPrimitives.ReadInt32LittleEndian(packet[216..]),
            CarPerformanceIndex = BinaryPrimitives.ReadInt32LittleEndian(packet[220..]),
            DrivetrainType = BinaryPrimitives.ReadInt32LittleEndian(packet[224..]),
            NumCylinders = BinaryPrimitives.ReadInt32LittleEndian(packet[228..]),
            CarGroup = BinaryPrimitives.ReadInt32LittleEndian(packet[232..]),
            SmashableVelDiff = Float(packet, 236),
            SmashableMass = Float(packet, 240),
            EngineMaxRpm = Float(packet, 8),
            EngineIdleRpm = Float(packet, 12),
            Rpm = rpm,
            SpeedKmh = speed * 3.6,
            PowerKw = power / 1000.0,
            TorqueNm = torque,
            BoostPsi = Float(packet, 284),
            FuelRatio = Float(packet, 288),
            DistanceTraveledM = Float(packet, 292),
            BestLapSeconds = Float(packet, 296),
            LastLapSeconds = Float(packet, 300),
            CurrentLapSeconds = Float(packet, 304),
            CurrentRaceTimeSeconds = Float(packet, 308),
            LapNumber = BinaryPrimitives.ReadUInt16LittleEndian(packet[312..]),
            RacePosition = packet[314],
            Throttle = packet[315] / 255.0,
            Brake = packet[316] / 255.0,
            Clutch = packet[317] / 255.0,
            HandBrake = packet[318] / 255.0,
            Gear = packet[319],
            Steer = (sbyte)packet[320] / 127.0,
            PositionX = Float(packet, 244), PositionY = Float(packet, 248), PositionZ = Float(packet, 252),
            AccelerationX = Float(packet, 20), AccelerationY = Float(packet, 24), AccelerationZ = Float(packet, 28),
            VelocityX = Float(packet, 32), VelocityY = Float(packet, 36), VelocityZ = Float(packet, 40),
            AngularVelocityX = Float(packet, 44), AngularVelocityY = Float(packet, 48), AngularVelocityZ = Float(packet, 52),
            Yaw = Float(packet, 56), Pitch = Float(packet, 60), Roll = Float(packet, 64),
            FrontLeft = Wheel(packet, 68, 84, 100, 116, 132, 148, 164, 180, 196, 268),
            FrontRight = Wheel(packet, 72, 88, 104, 120, 136, 152, 168, 184, 200, 272),
            RearLeft = Wheel(packet, 76, 92, 108, 124, 140, 156, 172, 188, 204, 276),
            RearRight = Wheel(packet, 80, 96, 112, 128, 144, 160, 176, 192, 208, 280),
        };
        return true;

        static WheelSample Wheel(ReadOnlySpan<byte> p, int normalizedTravel, int slipRatio, int rotation,
            int rumble, int puddle, int surface, int slipAngle, int combinedSlip, int travelMeters, int tireTemp)
            => new(
                SlipRatio: Float(p, slipRatio), SlipAngle: Float(p, slipAngle), CombinedSlip: Float(p, combinedSlip),
                RotationSpeedRadPerSec: Float(p, rotation),
                OnRumbleStrip: BinaryPrimitives.ReadInt32LittleEndian(p[rumble..]) != 0,
                PuddleDepth: BinaryPrimitives.ReadInt32LittleEndian(p[puddle..]) == 1 ? 1 : Float(p, puddle),
                SurfaceRumble: Float(p, surface),
                SuspensionTravelMeters: Float(p, travelMeters), NormalizedSuspensionTravel: Float(p, normalizedTravel),
                TireTempC: Float(p, tireTemp));
    }

    private static float Float(ReadOnlySpan<byte> data, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
}

/// <summary>
/// 衍生指标：只在数据真实可算时给出值，不支持的字段为 null（UI 显示“暂不支持”）。
/// 平滑使用一阶低通滤波，采样间隔异常（> 500 ms）时重置，避免跳变被当作加速度。
/// </summary>
public sealed class TelemetryDerived
{
    private double _smoothLongitudinal;
    private double _smoothLateral;
    private double _smoothCombined;
    private double? _lastSpeedKmh;
    private uint? _lastTimestamp;
    private int _car;
    private VehicleSideSlip _sideSlip;

    public double LongitudinalG { get; private set; }
    public double LateralG { get; private set; }
    public double VerticalG { get; private set; }
    public double CombinedG { get; private set; }
    public double SmoothedLongitudinalG { get; private set; }
    public double SmoothedLateralG { get; private set; }
    public double SmoothedCombinedG { get; private set; }
    /// <summary>由速度差分得到的纵向加速度（m/s²）；Data Out 的 Acceleration 字段本身已是 m/s²，两者互为校验。</summary>
    public double? SpeedDerivedAccelerationMps2 { get; private set; }
    public double? SlipAngleDeg { get; private set; }
    public double? SteerAngleDeg { get; private set; }
    public double YawRateDegPerSec { get; private set; }
    public double PitchRateDegPerSec { get; private set; }
    public double RollRateDegPerSec { get; private set; }
    public DrivingState State { get; private set; } = DrivingState.Idle;
    public bool DataInterrupted { get; private set; }

    public const double Gravity = 9.80665;

    public void Reset()
    {
        _smoothLongitudinal = _smoothLateral = _smoothCombined = 0;
        _sideSlip = default;
        _lastSpeedKmh = null; _lastTimestamp = null; _car = 0;
        LongitudinalG = LateralG = VerticalG = CombinedG = 0;
        SmoothedLongitudinalG = SmoothedLateralG = SmoothedCombinedG = 0;
        SpeedDerivedAccelerationMps2 = null; SlipAngleDeg = null; SteerAngleDeg = null;
        YawRateDegPerSec = PitchRateDegPerSec = RollRateDegPerSec = 0;
        State = DrivingState.Idle; DataInterrupted = false;
    }

    /// <summary>数据中断后调用：丢弃差分基准，避免把暂停时间算进加速度/曲线。</summary>
    public void InvalidateSession()
    {
        _sideSlip = default;
        _lastTimestamp = null;
        _lastSpeedKmh = null;
        SpeedDerivedAccelerationMps2 = null;
    }

    public void Update(TelemetrySample sample, TelemetrySettings settings)
    {
        if (_car != 0 && _car != sample.CarOrdinal) Reset();
        _car = sample.CarOrdinal;

        // Data Out 的 +Z 指向车头。横向显示沿用左转为正。
        var longitudinalMps2 = sample.AccelerationZ;
        var lateralMps2 = -sample.AccelerationX;
        var verticalMps2 = sample.AccelerationY;
        LongitudinalG = longitudinalMps2 / Gravity;
        LateralG = lateralMps2 / Gravity;
        VerticalG = verticalMps2 / Gravity;
        CombinedG = Math.Sqrt(LateralG * LateralG + LongitudinalG * LongitudinalG);

        var alpha = SetSmoothing(settings.SmoothingAlpha);
        _smoothLongitudinal += (LongitudinalG - _smoothLongitudinal) * alpha;
        _smoothLateral += (LateralG - _smoothLateral) * alpha;
        _smoothCombined += (CombinedG - _smoothCombined) * alpha;
        SmoothedLongitudinalG = _smoothLongitudinal;
        SmoothedLateralG = _smoothLateral;
        SmoothedCombinedG = _smoothCombined;

        // 时间戳回绕/跳变：超过 500 ms 视为中断，不据此计算差分。
        if (_lastTimestamp is uint previous)
        {
            var deltaMs = (uint)(sample.TimestampMs - previous);
            DataInterrupted = deltaMs > 500;
            if (deltaMs is > 0 and <= 500 && _lastSpeedKmh is double lastKmh)
                SpeedDerivedAccelerationMps2 = (sample.SpeedKmh - lastKmh) / 3.6 / (deltaMs / 1000.0);
            else
                SpeedDerivedAccelerationMps2 = null;
        }
        else
        {
            SpeedDerivedAccelerationMps2 = null;
            DataInterrupted = false;
        }
        _lastTimestamp = sample.TimestampMs;
        _lastSpeedKmh = sample.SpeedKmh;

        var bodyAngle = VehicleSideSlip.Angle(sample);
        SlipAngleDeg = bodyAngle * 180.0 / Math.PI;

        // 转向角：Data Out 只给 -1..1 的归一化转向输入，没有方向盘角度，按最大转向角估算并标注“估算”。
        SteerAngleDeg = settings.MaxSteerDegrees is > 0 ? sample.Steer * settings.MaxSteerDegrees.Value : null;

        YawRateDegPerSec = sample.AngularVelocityY * 180.0 / Math.PI;
        PitchRateDegPerSec = sample.AngularVelocityX * 180.0 / Math.PI;
        RollRateDegPerSec = sample.AngularVelocityZ * 180.0 / Math.PI;

        if (DataInterrupted) _sideSlip = default;
        var rearSliding = _sideSlip.Observe(sample, bodyAngle, settings.DriftSlipThreshold);
        State = Classify(sample, settings, rearSliding);
    }

    private static double SetSmoothing(int alphaPercent) => Math.Clamp(alphaPercent, 1, 100) / 100.0;

    /// <summary>驾驶状态判定：阈值可配置，多字段联合判断，不依赖单一字段。</summary>
    private static DrivingState Classify(TelemetrySample sample, TelemetrySettings settings, bool rearSliding)
    {
        if (sample.SpeedKmh < settings.IdleSpeedKmh) return DrivingState.Idle;
        var longitudinal = sample.AccelerationZ / Gravity;
        var lateral = -sample.AccelerationX / Gravity;
        if (rearSliding)
            return settings.AutomaticShift.DriftPredictionEnabled ? DrivingState.DriftCandidate : DrivingState.RearSliding;
        if (sample.Brake > settings.PedalThreshold && longitudinal < -settings.HardBrakeG)
            return DrivingState.HardBraking;
        if (sample.Brake > settings.PedalThreshold && sample.Brake > sample.Throttle * 0.55)
            return Math.Abs(lateral) > settings.CorneringLateralGThreshold ? DrivingState.TrailBraking : DrivingState.Braking;
        if (Math.Abs(lateral) > settings.CorneringLateralGThreshold)
            return lateral > 0 ? DrivingState.TurningLeft : DrivingState.TurningRight;
        if (sample.Throttle > settings.PedalThreshold && longitudinal > settings.HardAccelG)
            return DrivingState.HardAcceleration;
        if (sample.Throttle > settings.PedalThreshold) return DrivingState.Accelerating;
        return DrivingState.Cruising;
    }
}

public enum DrivingState
{
    Idle, Accelerating, HardAcceleration, Cruising, Braking, HardBraking, TurningLeft, TurningRight, DriftCandidate,
    RearSliding, TrailBraking,
}

/// <summary>模块设置：全部持久化，默认值按官方 Data Out 精度与常见驾驶习惯选取。</summary>
public sealed class TelemetrySettings
{
    public AutomaticShiftSettings AutomaticShift { get; set; } = new()
    {
        StrictSpeedGearMatching = true,
        AutoDetectRaceTransmission = true,
        ProtectLaunchWheelspin = true,
        EnableShiftShortcut = true,
    };
    internal DriveAssistSettings DriveAssist { get; set; } = new();
    public TelemetryRetention Retention { get; set; } = new();
    public TelemetryHudOptions Hud { get; set; } = new();
    public string Language { get; set; } = "zh-CN";
    public bool AutoDownloadUpdates { get; set; }
    public bool AboutStarPromptDismissed { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 20440;
    public bool AutoStart { get; set; }
    public bool AutomaticShiftEnabled { get; set; }
    public int TimeoutMs { get; set; } = 3000;
    public bool LogDebug { get; set; }
    public string SpeedUnit { get; set; } = "km/h";        // km/h | mph | m/s
    public string PowerUnit { get; set; } = "kW";          // kW | hp | PS
    public string TorqueUnit { get; set; } = "N·m";        // N·m | lb-ft
    public string TemperatureUnit { get; set; } = "°C";    // °C | °F
    public int Decimals { get; set; } = 1;
    public int SmoothingAlpha { get; set; } = 25;
    public int ChartWindowSeconds { get; set; } = 30;
    public bool ShowGearChart { get; set; }
    public int UiRefreshHz { get; set; } = 30;
    public double StartSpeedKmh { get; set; } = 1.0;
    public double MaxTestSeconds { get; set; } = 180;
    public double IdleSpeedKmh { get; set; } = 1.0;
    public double PedalThreshold { get; set; } = 0.05;
    public double HardAccelG { get; set; } = 0.35;
    public double HardBrakeG { get; set; } = 0.45;
    public double CorneringLateralGThreshold { get; set; } = 0.45;
    public double DriftSlipThreshold { get; set; } = VehicleSideSlip.DefaultCombinedThreshold;
    public double? MaxSteerDegrees { get; set; } = 40;
    public bool AutoSaveRecords { get; set; } = true;
    public int MaxStoredRecords { get; set; } = 500;
    public int SampleRecordHz { get; set; } = 20;
    public string DataDirectory { get; set; } = "";
    public bool CompactMode { get; set; }
}

public static class TelemetryUnits
{
    public static double Speed(double kmh, string unit) => unit switch
    {
        "mph" => kmh / 1.609344,
        "m/s" => kmh / 3.6,
        _ => kmh,
    };
    public static string SpeedSymbol(string unit) => unit switch { "mph" => "mph", "m/s" => "m/s", _ => "km/h" };
    public static double Power(double kw, string unit) => unit switch
    {
        "hp" => kw / 0.745699872,     // 机械马力
        "PS" => kw / 0.73549875,      // 公制马力
        _ => kw,
    };
    public static string PowerSymbol(string unit) => unit switch { "hp" => "hp", "PS" => "PS", _ => "kW" };
    public static double Torque(double nm, string unit) => unit switch
    {
        "lb-ft" => nm / 1.3558179483,
        _ => nm,
    };
    public static string TorqueSymbol(string unit) => unit == "lb-ft" ? "lb-ft" : "N·m";
    public static double Temperature(double celsius, string unit) => unit switch
    {
        "°F" => celsius * 9 / 5 + 32,
        _ => celsius,
    };
    public static string TemperatureSymbol(string unit) => unit == "°F" ? "°F" : "°C";
    public static double Distance(double meters, string unit) => unit switch
    {
        "mi" => meters / 1609.344,
        "ft" => meters / 0.3048,
        _ => meters,
    };
    public static string DistanceSymbol(string unit) => unit switch { "mi" => "mi", "ft" => "ft", _ => "m" };
}

public static class DrivetrainNames
{
    public static string Name(int type) => type switch
    {
        0 => TelemetryText.Get("DriveFwd"),
        1 => TelemetryText.Get("DriveRwd"),
        2 => TelemetryText.Get("DriveAwd"),
        _ => TelemetryText.Get("DriveUnknown"),
    };
    public static string DrivetrainIcon(int type) => type switch { 0 => "FWD", 1 => "RWD", 2 => "AWD", _ => "—" };
}
