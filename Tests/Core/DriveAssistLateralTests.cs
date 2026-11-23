using System;
using FH6Telemetry;

internal static class DriveAssistLateralTests
{
    public static void Register(Action<string, Action> run)
    {
        run("横向辅助：偏航趋势在既有甩尾阈值之前发出预警并开始预防", () =>
        {
            var drive = new Drive(DriveAssistFeature.Stability);
            var preSlide = Ground() with { Yaw = 0.48, ExpectedYaw = 0.27, YawAccel = 3.6, Beta = 0.11, RearAngle = 0.72 };
            drive.Step(Ground(), 0.2, gas: 0.6);
            var demand = drive.Step(preSlide, 0.2, gas: 0.6);
            Check(!preSlide.Rescuing && drive.Lateral.Status.Risk >= 0.34 &&
                demand.SteeringOwner == DriveAssistFeature.Stability && demand.Steering < 0.2,
                "预警应使用偏航趋势，并能在原有甩尾判断前小幅介入");
        });

        run("横向辅助：高速变线保留玩家方向，直线抗跑偏能纠正持续小偏航", () =>
        {
            var drive = new Drive(DriveAssistFeature.Stability);
            for (var i = 0; i < 12; i++) drive.Step(Ground() with { Yaw = 0.07 }, 0.02);
            var straight = drive.Step(Ground() with { Yaw = 0.14 }, 0.02);
            Check(straight.SteeringOwner == DriveAssistFeature.Stability && straight.Steering < 0.02,
                "中心小舵时的持续偏航应得到适度修正");
            var laneChange = drive.Step(Ground() with { Yaw = 0.46, ExpectedYaw = 0.27, YawAccel = 2.0 }, 0.34);
            Check(laneChange.SteeringOwner == DriveAssistFeature.Stability && laneChange.Steering is > 0.18 and < 0.34,
                "高速变线应抑制多余偏航并保留玩家方向");
            Check(drive.Step(Ground(), -0.9).SteeringOwner == DriveAssistFeature.None,
                "明显反向操舵应交还转向");
        });

        run("横向辅助：重刹甩尾时减小总刹车，四轮侧滑进入独立的失控等级", () =>
        {
            var drive = new Drive(DriveAssistFeature.Stability);
            var brakeSlide = Ground() with { Yaw = 0.82, ExpectedYaw = 0.25, Beta = 0.32, RearAngle = 1.1, RearBrakingSlip = 1.2 };
            var brake = drive.Step(brakeSlide, 0.18, brake: 0.75);
            Check(brake.BrakeCapOwner == DriveAssistFeature.Stability && brake.BrakeCap < 0.75 &&
                brake.SteeringOwner == DriveAssistFeature.Stability, "制动引起的甩尾需要方向和总刹车协同");
            var saturated = drive.Sample.FrontLeft with { CombinedSlip = 1.45 };
            drive.Sample = drive.Sample with { FrontLeft = saturated, FrontRight = saturated, RearLeft = saturated, RearRight = saturated };
            var spin = drive.Step(brakeSlide with { Yaw = 1.4, Beta = 0.65 }, 0.1, gas: 0.7);
            Check(drive.Lateral.Status.FourWheelSlide && drive.Lateral.Status.SpinLevel == 3 &&
                spin.ThrottleCapOwner == DriveAssistFeature.Stability && spin.Steering < 0.1,
                "四轮饱和与高级失控应得到专门的输出限制");
        });

        run("横向辅助：抓地突然恢复与回摆时限制修正幅度和再次加油", () =>
        {
            var drive = new Drive(DriveAssistFeature.Stability);
            var slippingWheel = drive.Sample.RearLeft with { CombinedSlip = 1.6 };
            drive.Sample = drive.Sample with { RearLeft = slippingWheel, RearRight = slippingWheel };
            drive.Step(Ground() with { Yaw = 0.7, Beta = 0.3, RearAngle = 0.95 }, 0.12, gas: 0.65);
            var recoveredWheel = slippingWheel with { CombinedSlip = 0.55 };
            drive.Sample = drive.Sample with { RearLeft = recoveredWheel, RearRight = recoveredWheel };
            var rebound = drive.Step(Ground() with { Yaw = -0.46, YawAccel = -3, Beta = -0.12, RearAngle = 0.52 }, 0.12, gas: 0.65);
            Check(drive.Lateral.Status.GripRecovery && rebound.ThrottleCap < 0.65 && rebound.Steering is > 0.12 and < 0.40,
                "抓地突恢复与反向偏航时应以小幅修正渡过回摆");
        });

        run("横向辅助：用户死区和短防抖可配置，大方向输入立即通过", () =>
        {
            var drive = new Drive(DriveAssistFeature.SteeringTuning, deadzone: 8, debounce: 80);
            Check(drive.Step(Ground(), 0.05).Steering == 0, "死区内输入应归零");
            Check(drive.Step(Ground(), 0.17).Steering == 0, "小幅新方向要经过防抖");
            DriveAssistDemands stable = default;
            for (var i = 0; i < 5; i++) stable = drive.Step(Ground(), 0.17);
            Check(stable.Steering is > 0 and < 0.17, "同向意图稳定后应按曲线恢复");
            Check(drive.Step(Ground(), -0.85).Steering < -0.7, "明显反向人工输入应立即通过");
        });

        run("横向辅助：辨别高速长满舵与普通回正，并在出弯加速回正", () =>
        {
            var drive = new Drive(DriveAssistFeature.SteeringTuning, deadzone: 0);
            var pushing = Ground() with { Yaw = 0.2, ExpectedYaw = 0.62, FrontCombined = 1.2 };
            DriveAssistDemands held = default;
            for (var i = 0; i < 22; i++) held = drive.Step(pushing, 0.95);
            Check(held.Steering is > 0 and < 0.8, "高速推头且持续满舵应收小转向");
            var release = drive.Step(Ground() with { Phase = DriveAssistPhase.CornerExit, Yaw = 0.18 }, 0.32, gas: 0.45);
            Check(release.Steering is > 0 and < 0.30, "安全出弯减舵时应加快回正");
            var centered = drive.Step(Ground() with { Yaw = 0.17 }, 0);
            Check(centered.Steering is < 0 and > -0.16, "居中后应轻微抑制残余偏航");
        });

        run("横向辅助：分别提示触底和侧翻风险，离地时避让既有落地控制", () =>
        {
            var drive = new Drive(DriveAssistFeature.SuspensionProtection);
            var compressed = drive.Sample.FrontLeft with { NormalizedSuspensionTravel = 0.99 };
            var unloaded = drive.Sample.FrontRight with { NormalizedSuspensionTravel = 0.08 };
            drive.Sample = drive.Sample with { FrontLeft = compressed, RearLeft = compressed, FrontRight = unloaded,
                RearRight = unloaded, Roll = 0.56, AngularVelocityZ = 1.2, AccelerationY = 16 };
            drive.Step(Ground() with { LateralG = 0.8 }, 0.4, gas: 0.6);
            var adjusted = drive.Step(Ground() with { LateralG = 0.8 }, 0.4, gas: 0.6);
            Check(drive.Lateral.Status is { BottomedOut: true, RollRisk: > 0.65, SuspensionImbalance: > 0.15 } &&
                adjusted.ThrottleCapOwner == DriveAssistFeature.SuspensionProtection && adjusted.Steering < 0.4,
                "触底、左右载荷差和侧翻风险需要可观测反馈与实际保护");
            Check(drive.Step(Ground() with { Phase = DriveAssistPhase.Airborne, LandingStrength = 1 }, 0.4, gas: 0.6).SteeringOwner == 0,
                "空中应保留落地策略的控制权");
        });

        run("横向辅助：漂移转场限油和计分，一键退漂后稳定即交还方向", () =>
        {
            var drive = new Drive(DriveAssistFeature.DriftTransitions);
            var slip = Ground() with { Speed = 16, Yaw = -0.5, Beta = -0.42, RearAngle = 0.9 };
            for (var i = 0; i < 12; i++) drive.Step(slip, 0.36, gas: 0.67, drift: true);
            Check(drive.Lateral.Status.DriftScore is >= 0 and <= 100, "漂移分数需由有效侧滑样本产生");
            var transition = drive.Step(slip, -0.36, gas: 0.67, drift: true);
            Check(transition.SteeringOwner == DriveAssistFeature.DriftTransitions &&
                transition.ThrottleCapOwner == DriveAssistFeature.DriftTransitions && transition.ThrottleCap < 0.67,
                "摇杆换向后要连续过渡，避免同时猛加油");
            drive.Lateral.RequestDriftExit();
            var leaving = drive.Step(slip, 0, gas: 0.3);
            Check(drive.Lateral.DriftExiting && leaving.SteeringOwner == DriveAssistFeature.DriftTransitions,
                "一键退漂应让出漂移保持并主动恢复");
            for (var i = 0; i < 7; i++) drive.Step(Ground(), 0);
            Check(!drive.Lateral.DriftExiting, "恢复稳定后应退出退漂状态");
        });

        run("横向辅助：主动漂移入弯与接近掉头分别处理", () =>
        {
            var drive = new Drive(DriveAssistFeature.DriftTransitions);
            var corner = Ground() with { Speed = 15, Phase = DriveAssistPhase.Corner, Yaw = 0.34, ExpectedYaw = 0.3, Beta = 0.07, RearAngle = 0.68 };
            drive.Step(corner, 0.4, gas: 0.58);
            drive.Step(corner, 0.4, gas: 0.09);
            var entry = drive.Step(corner with { Beta = 0.12 }, 0.4, gas: 0.55);
            Check(entry.SteeringOwner == DriveAssistFeature.DriftTransitions, "有明确提油再给油意图的入弯应逐步建立角度");
            var spin = drive.Step(corner with { Speed = 5.1, Yaw = 1.4, Beta = 0.83, RearAngle = 1.1 }, 0.3, gas: 0.55, drift: true);
            Check(spin.ThrottleCapOwner == DriveAssistFeature.DriftTransitions && spin.ThrottleCap < 0.36,
                "失控起势时应降低动力并执行回正");
        });

        run("横向辅助：学习左右响应差，并把失准的学习参数回退", () =>
        {
            var drive = new Drive(DriveAssistFeature.SteeringTuning | DriveAssistFeature.VehicleLearning, deadzone: 0, debounce: 0);
            var left = Ground() with { Phase = DriveAssistPhase.Corner, ExpectedYaw = -0.30, Yaw = -0.45, LateralG = 0.45 };
            var right = left with { ExpectedYaw = 0.30, Yaw = 0.24 };
            for (var i = 0; i < 36; i++) drive.Step(left, -0.35, reportedSteer: -0.35);
            for (var i = 0; i < 36; i++) drive.Step(right, 0.35, reportedSteer: 0.35);
            var corrected = drive.Step(left, -0.35, reportedSteer: -0.35);
            Check(drive.Lateral.Status.SteeringGain.HasValue && corrected.Steering is > -0.33 and < -0.10,
                "左右响应都可信后应补偿较敏感的一侧");
            for (var i = 0; i < 28; i++) drive.Step(left with { Yaw = -0.07 }, -0.35, reportedSteer: -0.35);
            var reverted = drive.Step(left with { Yaw = -0.07 }, -0.35, reportedSteer: -0.35);
            Check(reverted.SteeringOwner == DriveAssistFeature.None || reverted.Steering < -0.33, "持续不合格的学习响应应恢复默认");
            drive.Lateral.Reset(vehicleChanged: true);
            Check(drive.Lateral.Status.SteeringGain is null, "换车不能沿用原来的响应增益");
        });

        run("横向辅助：在稳定样本里估计车辆转向迟滞与死区", () =>
        {
            var drive = new Drive(DriveAssistFeature.VehicleLearning);
            for (var i = 0; i < 10; i++) drive.Step(Ground(), 0.08, reportedSteer: 0);
            for (var i = 0; i < 2; i++)
            {
                for (var j = 0; j < 10; j++) drive.Step(Ground(), 0, reportedSteer: 0);
                for (var j = 0; j < 4; j++) drive.Step(Ground() with { ExpectedYaw = 0.24 }, 0.32, reportedSteer: 0.32);
                drive.Step(Ground() with { ExpectedYaw = 0.24, Yaw = 0.18 }, 0.32, reportedSteer: 0.32);
            }
            Check(drive.Lateral.Status is { SteeringDelayMs: >= 60 and <= 120, SteeringDeadzone: >= 0.05 and <= 0.10 },
                "报告的迟滞和死区应来自原始手柄与实际车辆响应");
        });
    }

    private static DriveAssistState Ground() => new(
        Dt: 0.02, Phase: DriveAssistPhase.Straight, Speed: 30, Yaw: 0, YawAccel: 0, ExpectedYaw: 0,
        Beta: 0, LateralG: 0.05, LateralTrend: 0, LongitudinalG: 0, PitchRoll: 0, FrontAngle: 0.1,
        RearAngle: 0.1, FrontCombined: 0.2, DrivenCombined: 0.2, DrivenSlip: 0.1, BrakingSlip: 0,
        RearBrakingSlip: 0, RearBrakingSpike: 0, WheelDrop: 0, LeftRightSlip: 0, LeftRightSpeed: 0,
        SurfaceDifference: 0, Bump: 0, LandingStrength: 0, CrestStrength: 0, ThermalScale: 1, SteerRise: 0,
        Downshift: false, Rescuing: false, DrivenWheelOverrun: false, WheelLock: 0);

    private sealed class Drive(DriveAssistFeature features, int deadzone = 5, int debounce = 0)
    {
        private readonly DriveAssistSettings _settings = new()
        {
            Features = features,
            SteeringDeadzonePercent = deadzone,
            SteeringDebounceMs = debounce,
        };

        public DriveAssistLateral Lateral { get; } = new();
        public TelemetrySample Sample { get; set; } = new()
        {
            FrontLeft = Wheel(), FrontRight = Wheel(), RearLeft = Wheel(), RearRight = Wheel(),
        };

        public DriveAssistDemands Step(DriveAssistState state, double steer, double gas = 0, double brake = 0,
            bool drift = false, double reportedSteer = 0)
        {
            var demand = new DriveAssistDemands();
            Lateral.Suggest(Sample with { Steer = reportedSteer }, new(steer, gas, brake, false, false),
                state, DriveAssistProfile.From(Sample, false), _settings, drift, calibrationPass: true, ref demand);
            return demand;
        }
    }

    private static WheelSample Wheel() => new(0, 0.1, 0.2, 70, false, 0, 0.05, 0.14, 0.4, 80);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
