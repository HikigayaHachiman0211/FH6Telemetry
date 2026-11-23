using FH6Telemetry;

internal static class DriveAssistPedalsTests
{
    public static void Register(Action<string, Action> run)
    {
        run("驾驶辅助拓展：限速只削油，并在回到限速以下时恢复", () =>
        {
            var drive = new Drive(DriveAssistFeature.SpeedLimiter);
            var gas = new DriveAssistDriver(0, 0.8, 0, false, false);
            var demand = drive.Step(Sample(26), gas);
            Check(demand.ThrottleCapOwner == DriveAssistFeature.SpeedLimiter && demand.ThrottleCap == 0 &&
                demand.BrakeOwner == 0 && demand.BrakeCapOwner == 0, "超过限速时应只削油");
            demand = drive.Step(Sample(15), gas);
            Check(demand.ThrottleCapOwner == 0, "回到限速以下应交还油门");
        });

        run("驾驶辅助拓展：标出空转驱动轮，用户目标滑移改变介入", () =>
        {
            var sample = Sample() with
            {
                FrontLeft = Wheel(1.8),
                RearLeft = Wheel(1.12) with { RotationSpeedRadPerSec = 95 },
            };
            var gas = new DriveAssistDriver(0, 0.65, 0, false, false);
            var tight = new Drive(DriveAssistFeature.AdaptiveTraction, targetSlipPercent: 60);
            var loose = new Drive(DriveAssistFeature.AdaptiveTraction, targetSlipPercent: 140);
            var restricted = tight.Step(sample, gas);
            var relaxed = loose.Step(sample, gas);
            Check(tight.Pedals.Status.SpinWheelMask == 4, "后驱仅应标出左后驱动轮空转");
            Check(restricted.ThrottleCapOwner == DriveAssistFeature.AdaptiveTraction && relaxed.ThrottleCapOwner == 0,
                "更保守的目标滑移应更早削油");
        });

        run("驾驶辅助拓展：识别双侧积水、路沿和异常胎温", () =>
        {
            var dry = Sample();
            var wet = dry with
            {
                FrontLeft = Wheel() with { PuddleDepth = 0.8, TireTempC = 122 },
                FrontRight = Wheel() with { PuddleDepth = 0.65 },
                RearRight = Wheel() with { OnRumbleStrip = true },
            };
            var drive = new Drive(DriveAssistFeature.SurfaceProtection);
            for (var i = 0; i < 20; i++) drive.Step(dry, default);
            for (var i = 0; i < 16; i++) drive.Step(wet, default);
            var status = drive.Pedals.Status;
            Check(status.Surface == DriveAssistSurface.Wet && status.WetWheelMask == 3 &&
                status.CurbWheelMask == 8 && status.TireTemperatureMask == 1 && status.GripChanged,
                "积水、路沿和温度应有独立的车轮标记，并记录路面变化");
        });

        run("驾驶辅助拓展：油门突踩和增压突升依次限油，松油马上归还", () =>
        {
            var drive = new Drive(DriveAssistFeature.PowerManagement);
            var normal = Sample() with { BoostPsi = 2 };
            drive.Step(normal, new(0, 0.2, 0, false, false));
            var sudden = drive.Step(normal, new(0, 0.9, 0, false, false));
            Check(sudden.ThrottleCapOwner == DriveAssistFeature.PowerManagement && sudden.ThrottleCap < 0.5,
                "突然踩油应经过缓升");
            for (var i = 0; i < 35; i++) drive.Step(normal, new(0, 0.9, 0, false, false));
            var boosted = drive.Step(normal with { BoostPsi = 15 }, new(0, 0.9, 0, false, false));
            Check(boosted.ThrottleCapOwner == DriveAssistFeature.PowerManagement && boosted.ThrottleCap < 0.85,
                "油门不变时的突然升压也应限制");
            Check(drive.Step(normal, default).ThrottleCapOwner == 0, "完全松油应立即归还");
        });

        run("驾驶辅助拓展：重刹后轴趋于锁止，总刹车近似退压", () =>
        {
            var drive = new Drive(DriveAssistFeature.AdaptiveBraking);
            var sample = Sample() with { RearRight = Wheel(-1.43) };
            var brake = new DriveAssistDriver(0.13, 0, 0.82, false, false);
            var demand = drive.Step(sample, brake);
            Check(demand.BrakeCapOwner == DriveAssistFeature.AdaptiveBraking && demand.BrakeCap < 0.80 &&
                demand.BrakeOwner == 0, "以单一总刹车减轻后轴饱和，不能额外踩刹车");
        });

        run("驾驶辅助拓展：落地初期只逐步恢复 ABS 控制权", () =>
        {
            var drive = new Drive(DriveAssistFeature.AdaptiveBraking);
            var brake = new DriveAssistDriver(0, 0, 0.8, false, false);
            var cap = new DriveAssistDemands();
            cap.LimitBrake(0.21, DriveAssistFeature.Abs);
            var landing = drive.Step(Sample(), brake, phase: DriveAssistPhase.Landing, landingStrength: 0.9, initial: cap);
            Check(landing.BrakeCap > 0.5 && landing.BrakeCap < 0.8, "落地初期应温和介入，而不是突然把刹车减小到目标值");
            for (var i = 0; i < 25; i++) drive.Step(Sample(), brake, initial: cap);
            Check(drive.Step(Sample(), brake, initial: cap).BrakeCap <= 0.25, "站稳后应恢复正常 ABS 退压");
        });

        run("驾驶辅助拓展：锁挡跟随漂移状态，升档定时削扭不影响降档补油", () =>
        {
            var drive = new Drive(DriveAssistFeature.TransmissionAssist);
            var gas = new DriveAssistDriver(0.4, 0.76, 0, false, false);
            drive.Step(Sample(), gas, drifting: true);
            Check(drive.Pedals.BlocksShift, "定角漂移在正常速度和转速应持挡");
            var up = drive.Step(Sample(), gas, shift: ShiftDirection.Up);
            Check(!drive.Pedals.BlocksShift && up.ThrottleCapOwner == DriveAssistFeature.TransmissionAssist &&
                up.ThrottleCap < 0.55, "升挡已经开始后仅削扭，不能阻拦确认");
            var down = drive.Step(Sample(), gas, shift: ShiftDirection.Down);
            Check(down.ThrottleCapOwner == 0, "降挡时不应削掉补油");
            drive.Step(Sample(rpm: 7750), gas, drifting: true);
            Check(!drive.Pedals.BlocksShift, "达到转速上限应允许换挡");
        });

        run("驾驶辅助拓展：确认上坡停车后短暂驻坡，驱动踏板获得控制", () =>
        {
            var drive = new Drive(DriveAssistFeature.HillStart);
            var move = new DriveAssistDriver(0, 0.16, 0, false, false);
            for (var i = 0; i < 55; i++)
                drive.Step(Sample(6) with { PositionX = i * 0.12, PositionY = i * 0.014 });
            var stopped = Sample(0.1) with { Gear = 1, PositionX = 6.48, PositionY = 0.756 };
            for (var i = 0; i < 12; i++) drive.Step(stopped, new(0, 0, 0.7, false, false));
            var released = drive.Step(stopped, default);
            Check(drive.Pedals.Status.HillHolding && released.BrakeOwner == DriveAssistFeature.HillStart && released.Brake >= 0.3,
                "需先确认坡度并实际踩刹，松刹后才短暂驻坡");
            Check(drive.Step(stopped, move with { Throttle = 0.4 }).BrakeOwner == 0, "明显起步意图立即释放驻坡");
            drive.Step(stopped, default);
            Check(!drive.Pedals.Status.HillHolding, "未重新踩刹不能重新驻坡");
        });

        run("驾驶辅助拓展：制动距离按速度和采样减速度变化，换车清空标定", () =>
        {
            var drive = new Drive(DriveAssistFeature.VehicleLearning | DriveAssistFeature.AdaptiveBraking);
            var foot = new DriveAssistDriver(0, 0, 0.72, false, false);
            for (var i = 0; i < 48; i++)
                drive.Step(Sample(22) with { Brake = 0.72, AccelerationZ = -8.2, FrontLeft = Wheel(-0.68), RearLeft = Wheel(-0.55) }, foot);
            var learned = drive.Pedals.Status;
            Check(learned.BrakeGain is > 5 && learned.StoppingDistanceMeters is > 8, "应测到制动响应和预估距离");
            drive.Step(Sample(9) with { Brake = 0.72, AccelerationZ = -8.2 }, foot);
            Check(drive.Pedals.Status.StoppingDistanceMeters < learned.StoppingDistanceMeters, "车速降低后刹停距离应变短");
            drive.Pedals.Reset(vehicleChanged: true);
            Check(drive.Pedals.Status.BrakeGain is null, "换车必须丢弃上一台车的踏板响应");
        });

        run("驾驶辅助拓展：坏标定回滚到最近已确认的制动响应", () =>
        {
            var drive = new Drive(DriveAssistFeature.VehicleLearning | DriveAssistFeature.AdaptiveBraking);
            var foot = new DriveAssistDriver(0, 0, 0.72, false, false);
            var first = Sample() with { Brake = 0.72, AccelerationZ = -8.2 };
            for (var i = 0; i < 49; i++) drive.Step(first, foot);
            var stableGain = drive.Pedals.Status.BrakeGain;
            var conflicting = first with { AccelerationZ = -2.1 };
            for (var i = 0; i < 16; i++) drive.Step(conflicting, foot);
            var status = drive.Pedals.Status;
            Check(stableGain is > 5 && status.CalibrationRolledBack && status.BrakeGain == stableGain,
                "持续矛盾值应恢复最近可信参数，并通知状态页");
        });

        run("驾驶辅助拓展：原地等候仍记住所确认坡度，驻坡到时就松开", () =>
        {
            var drive = new Drive(DriveAssistFeature.HillStart);
            for (var i = 0; i < 50; i++)
                drive.Step(Sample(6) with { PositionX = i * 0.12, PositionY = i * 0.014 });
            var stopped = Sample(0.1) with { Gear = 1, PositionX = 5.88, PositionY = 0.686 };
            for (var i = 0; i < 220; i++) drive.Step(stopped, new(0, 0, 0.7, false, false));
            var released = drive.Step(stopped);
            Check(released.BrakeOwner == DriveAssistFeature.HillStart, "停车等候几秒仍应记住这个坡道");
            for (var i = 0; i < 85; i++) drive.Step(stopped);
            Check(!drive.Pedals.Status.HillHolding, "驻坡有时限，且松踩后不能自行重新接通");
        });
    }

    private sealed class Drive(DriveAssistFeature features, int targetSlipPercent = 100)
    {
        private readonly DriveAssistEstimator _estimator = new();
        private readonly DriveAssistSettings _settings = new() { Features = features, TargetSlipPercent = targetSlipPercent };
        public DriveAssistPedals Pedals { get; } = new();

        public DriveAssistDemands Step(TelemetrySample sample, DriveAssistDriver driver = default,
            bool drifting = false, ShiftDirection? shift = null, DriveAssistPhase? phase = null, double landingStrength = 0,
            DriveAssistDemands initial = default)
        {
            var profile = DriveAssistProfile.From(sample, adaptive: false);
            var state = _estimator.Update(sample, driver, profile, 0.02, thermal: false, tireTempFahrenheit: false);
            if (phase.HasValue) state = state with { Phase = phase.Value, LandingStrength = landingStrength };
            Pedals.Suggest(sample, driver, state, profile, _settings, drifting, shift, ref initial);
            return initial;
        }
    }

    private static TelemetrySample Sample(double metresPerSecond = 20, double rpm = 3300) => new()
    {
        IsRaceOn = true, CarOrdinal = 19, Gear = 3, DrivetrainType = 1, EngineMaxRpm = 8000, EngineIdleRpm = 900,
        Rpm = rpm, SpeedKmh = metresPerSecond * 3.6, VelocityZ = metresPerSecond,
        FrontLeft = Wheel(), FrontRight = Wheel(), RearLeft = Wheel(), RearRight = Wheel(),
    };

    private static WheelSample Wheel(double slip = 0) => new(slip, 0, Math.Abs(slip), 62, false, 0, 0.03, 0.06, 0.38, 84);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
