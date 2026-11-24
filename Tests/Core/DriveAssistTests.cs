using FH6Telemetry;

internal static class DriveAssistTests
{
    private const ushort Handbrake = (ushort)ShiftShortcutButton.A;
    private const ushort Clutch = (ushort)ShiftShortcutButton.LeftStick;

    public static void Register(Action<string, Action> run)
    {
        run("驾驶辅助：默认关闭并原样传递每个手柄轴和按钮", () =>
        {
            var drive = new Drive();
            var pad = new ShiftGamepadReport(0x1020, 242, 11, -32768, -522, 14, 830);
            var skid = Frame() with { AngularVelocityY = 1.6, VelocityX = -9, RearLeft = Wheel(1.8, 1.5, 2) };
            Check(drive.Step(skid, pad, 20) == pad && drive.Step(skid, pad, 40) == pad, "默认选项不能抢手柄");
        });

        run("驾驶辅助：反打方向正确且后驱动力甩尾时收油", () =>
        {
            var drive = new Drive(DriveAssistFeature.CounterSteer);
            var pad = new ShiftGamepadReport(0, 220, LeftX: 8500);
            drive.Step(Frame(), pad, 20);
            var skid = Frame() with { AngularVelocityY = 2.4, VelocityX = -11, RearLeft = Wheel(1.5, 1.8, 2), RearRight = Wheel(1.4, 1.5, 2) };
            var output = drive.Repeat(skid, pad, 40, 240);
            Check(output.LeftX < 0 && output.RightTrigger < pad.RightTrigger, "右转甩尾应向左反打并收油");
            drive = new Drive(DriveAssistFeature.CounterSteer);
            drive.Step(Frame(), pad with { LeftX = -8500 }, 20);
            output = drive.Repeat(skid with { AngularVelocityY = -2.4, VelocityX = 11 }, pad with { LeftX = -8500 }, 40, 240);
            Check(output.LeftX > 0, "左转时应镜像反打");
        });

        run("驾驶辅助：前轮过滑时限幅并保留同向转向", () =>
        {
            var drive = new Drive(DriveAssistFeature.Understeer);
            var pad = new ShiftGamepadReport(0, 70, LeftX: 32767);
            var overloaded = Frame() with { AccelerationX = 6, FrontLeft = Wheel(angle: 1.4, combined: 1.5), FrontRight = Wheel(angle: 1.3) };
            var output = drive.Repeat(overloaded, pad, 20, 140);
            Check(output.LeftX is > 0 and < 30000 && output.RightTrigger == pad.RightTrigger, "推头只收同向转向");
            Check(drive.Step(overloaded, pad with { LeftX = 0 }, 160).LeftX == 0, "驾驶员回中时限幅不能继续替他转向");
        });

        run("低速急弯：外侧前轮仍抓地时保留持续的实体满舵", () =>
        {
            const DriveAssistFeature assists = DriveAssistFeature.Understeer | DriveAssistFeature.Abs;
            var outside = Wheel(angle: 0.55, combined: 0.65);
            var inside = Wheel(slip: -1.15, angle: 1.4, combined: 1.7);
            foreach (var direction in new[] { -1, 1 })
            {
                var corner = Frame() with
                {
                    SpeedKmh = 30, VelocityZ = 30 / 3.6, AccelerationX = direction * 6, AngularVelocityY = direction * 0.8,
                    FrontLeft = direction > 0 ? outside : inside, FrontRight = direction > 0 ? inside : outside,
                };
                var pad = new ShiftGamepadReport(0, 0, LeftTrigger: 220, LeftX: direction > 0 ? short.MaxValue : short.MinValue);
                var baseline = new Drive(assists).Repeat(corner, pad, 20, 280);
                Check(Math.Abs((int)baseline.LeftX) < 26000, "默认仍按前轮防推头收舵");

                var drive = new Drive(assists | DriveAssistFeature.TightTurnSteering);
                var early = drive.Repeat(corner, pad, 20, 80);
                Check(Math.Abs((int)early.LeftX) < Math.Abs((int)pad.LeftX), "应先确认同向大舵持续");
                var supported = drive.Repeat(corner, pad, 100, 280);
                Check(supported.LeftX == pad.LeftX && supported.LeftTrigger == baseline.LeftTrigger, "应还回满舵并保留刹车辅助");

                var fast = corner with { SpeedKmh = 72, VelocityZ = 20 };
                Check(Math.Abs((int)drive.Step(fast, pad, 300).LeftX) < Math.Abs((int)pad.LeftX), "速度升高时仍应收舵");
            }
        });

        run("低速急弯：外侧前轮饱和或后轮甩尾时让保护接管", () =>
        {
            var pad = new ShiftGamepadReport(0, 70, LeftX: short.MaxValue);
            var corner = Frame() with
            {
                SpeedKmh = 30, VelocityZ = 30 / 3.6, AccelerationX = 6, AngularVelocityY = 0.8,
                FrontLeft = Wheel(angle: 0.55, combined: 0.65), FrontRight = Wheel(angle: 1.4, combined: 1.5),
            };
            var drive = new Drive(DriveAssistFeature.Understeer | DriveAssistFeature.CounterSteer | DriveAssistFeature.TightTurnSteering);
            drive.Repeat(corner, pad, 20, 200);
            var saturated = corner with { FrontLeft = corner.FrontRight };
            Check(drive.Repeat(saturated, pad, 220, 280).LeftX < 26000, "外侧轮失去抓地时保留前轮限舵");
            var sliding = corner with
            {
                VelocityX = -4, AngularVelocityY = 1.8, RearLeft = Wheel(1.3, 1.6, 2), RearRight = Wheel(1.2, 1.5, 2),
            };
            var rescued = drive.Repeat(sliding, pad, 300, 420);
            Check(rescued.LeftX < 0 && drive.Controller.Active.HasFlag(DriveAssistFeature.CounterSteer), "后轮甩尾时仍能反打救车");
        });

        run("低速急弯：手动上限决定保留转向的速度", () =>
        {
            var turn = TightTurnProbe(48);
            var pad = new ShiftGamepadReport(0, 0, LeftX: short.MaxValue);
            foreach (var speedLimit in new[] { 38, 64 })
            {
                var drive = new Drive();
                drive.Controller.Configure(new()
                {
                    Features = DriveAssistFeature.Understeer | DriveAssistFeature.TightTurnSteering,
                    TightTurnSpeedLimitKmh = speedLimit,
                });
                var output = drive.Repeat(turn, pad, 20, 280);
                Check((output.LeftX == pad.LeftX) == (speedLimit > turn.SpeedKmh), "手动上限应直接控制急弯是否保留转向");
            }
        });

        run("低速急弯：自动上限随转弯抓地变化，换调校时重新学习", () =>
        {
            var settings = new DriveAssistSettings
            {
                Features = DriveAssistFeature.Understeer | DriveAssistFeature.TightTurnSteering,
                LearnTightTurnSpeed = true,
            };
            var pad = new ShiftGamepadReport(0, 0, LeftX: short.MaxValue);
            foreach (var strongGrip in new[] { false, true })
            {
                var probe = TightTurnProbe(strongGrip ? 60 : 48);
                var lateralAcceleration = (strongGrip ? 1.30 : 0.60) * TelemetryDerived.Gravity;
                var learningTurn = probe with
                {
                    AccelerationX = lateralAcceleration, AngularVelocityY = lateralAcceleration / probe.VelocityZ,
                    FrontLeft = Wheel(angle: 0.90, combined: 0.92), FrontRight = Wheel(angle: 0.90, combined: 0.92),
                };
                var drive = new Drive();
                drive.Controller.Configure(settings);
                drive.Repeat(learningTurn, pad, 20, 1000);
                var initial = drive.Repeat(probe, pad, 1020, 1200);
                Check((initial.LeftX == pad.LeftX) != strongGrip, "一次转弯的许多帧仍应使用手动上限");

                var straight = learningTurn with { AccelerationX = 0, AngularVelocityY = 0 };
                foreach (var start in new[] { 1220, 1920 })
                {
                    drive.Repeat(straight, default, start, start + 180);
                    drive.Repeat(learningTurn, pad, start + 200, start + 680);
                }
                Check(drive.Controller.Snapshot(2600, armed: true, includeReports: false).LearnedTightTurnSpeedKmh is null,
                    "学习上限应在离开第三个弯以后生效");
                drive.Repeat(straight, default, 2620, 2800);
                var learned = drive.Repeat(probe, pad, 2820, 3060);
                var learnedLimit = drive.Controller.Snapshot(3060, armed: true, includeReports: false).LearnedTightTurnSpeedKmh;
                Check(learnedLimit is int limit && (limit > probe.SpeedKmh) == strongGrip &&
                    (learned.LeftX == pad.LeftX) == strongGrip, "多次稳定转弯后，抓地强应提高上限，抓地弱应降低上限");

                var retuned = drive.Repeat(probe with { CarPerformanceIndex = probe.CarPerformanceIndex + 10 }, pad, 3080, 3340);
                Check((retuned.LeftX == pad.LeftX) != strongGrip, "调校变动后应先用手动上限");
            }
        });

        run("驾驶辅助：赛事侧滑跨过推头阈值时优先救车，保留同向的实体反打", () =>
        {
            var drive = new Drive(DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability | DriveAssistFeature.Understeer |
                DriveAssistFeature.Traction | DriveAssistFeature.CornerTraction);
            var pad = new ShiftGamepadReport(0, 255, LeftX: 32767);
            var entering = Frame() with { SpeedKmh = 117.2, VelocityZ = 22.03, Gear = 5, AccelerationX = 8.5,
                AngularVelocityY = 0.23, FrontLeft = Wheel(angle: 1.3), FrontRight = Wheel(angle: 1.2) };
            drive.Repeat(entering, pad, 20, 100);
            var sliding = entering with { VelocityX = -23.9, AngularVelocityY = 0.66,
                FrontLeft = Wheel(0.07, 3.7, 3.82), FrontRight = Wheel(0.07, 4.16, 4.16),
                RearLeft = Wheel(0.12, 4.73, 4.75), RearRight = Wheel(0.13, 4.98, 4.99) };
            var rescue = drive.Repeat(sliding, pad, 120, 220);
            Check(rescue.LeftX < -12000 && rescue.RightTrigger is > 65 and < 190 && drive.Controller.BlocksShift &&
                drive.Controller.Active.HasFlag(DriveAssistFeature.CounterSteer) &&
                !drive.Controller.Active.HasFlag(DriveAssistFeature.Understeer), "方向需要越过中点反打，纯侧向滑移不能把油门钉在死区");
            var correctStick = drive.Repeat(sliding, pad with { LeftX = -32768 }, 240, 320);
            Check(correctStick.LeftX == -32768 && !drive.Controller.Active.HasFlag(DriveAssistFeature.Understeer),
                "驾驶员已在正确方向打满时不能被误当成推头而削弱");
            drive.Repeat(entering with { AngularVelocityY = 0.15 }, pad, 340, 620);
            Check(!drive.Controller.BlocksShift && drive.Controller.Active.HasFlag(DriveAssistFeature.Understeer),
                "横摆稳定后回到正常的推头辅助并交还换挡");
        });

        run("驾驶辅助：单纯横滑时牵引力控制不把组合滑移当空转", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction | DriveAssistFeature.CornerTraction | DriveAssistFeature.AdaptiveTraction);
            var pad = new ShiftGamepadReport(0, 255, LeftX: -32768);
            var sideways = Frame() with { VelocityX = -12, AngularVelocityY = 0.54, AccelerationX = 17,
                RearLeft = Wheel(0.07, 2.8, 2.81), RearRight = Wheel(0.12, 2.5, 2.51) };
            Check(drive.Repeat(sideways, pad, 20, 220).RightTrigger == pad.RightTrigger,
                "轮胎主要横滑且纵向滑移很小时不能因为组合滑移而误判空转");
        });

        run("驾驶辅助：高速猛打时阻尼更强", () =>
        {
            var drive = new Drive(DriveAssistFeature.Damper);
            var fast = Frame() with { SpeedKmh = 200, AngularVelocityX = 0.65 };
            drive.Step(fast, default, 20);
            var output = drive.Step(fast, new(0, 0, 195, 26000), 40);
            Check(output.LeftX > 0 && output.LeftX < 13000, "高速重刹后转向应渐进");
        });

        run("驾驶辅助：漂移角超过目标时收敛，回打摇杆可反向切换", () =>
        {
            var drive = new Drive(DriveAssistFeature.DriftHold | DriveAssistFeature.CounterSteer);
            var pad = new ShiftGamepadReport(0, 100, LeftX: 16384);
            var skid = Frame() with { AngularVelocityY = 1.25, VelocityX = -18, VelocityZ = 12, RearLeft = Wheel(angle: 1.4) };
            var output = drive.Repeat(skid, pad, 20, 160);
            Check(output.LeftX < 0 && drive.Controller.Active.HasFlag(DriveAssistFeature.DriftHold), "定角控制应接过转向");
            output = drive.Repeat(skid with { AngularVelocityY = -1.25, VelocityX = 18 }, pad with { LeftX = -16384 }, 180, 340);
            Check(output.LeftX > 0, "切到另一侧的漂移应在相反方向收敛");
        });

        run("驾驶辅助：发卡弯短按自动松手刹，长按立即交还", () =>
        {
            var drive = new Drive(DriveAssistFeature.Hairpin | DriveAssistFeature.CounterSteer);
            var pad = new ShiftGamepadReport(Handbrake, 145, LeftX: 24000);
            drive.Step(Frame(), pad with { Buttons = 0 }, 20);
            var turning = Frame() with { AngularVelocityY = 2.2, HandBrake = 1, VelocityX = -10, RearLeft = Wheel(angle: 1.2) };
            var output = drive.Repeat(turning, pad, 40, 340);
            Check((output.Buttons & Handbrake) == 0 && output.LeftX < pad.LeftX, "转够角度后应松手刹并反打");
            output = drive.Repeat(turning, pad, 360, 700);
            Check(output == pad, "持续按住手刹应还原全部实体输入");
        });

        run("驾驶辅助：TCS 围绕抓地峰值减油且随抓地恢复放开", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction);
            var pad = new ShiftGamepadReport(0, 244);
            var grip = Frame();
            drive.Step(grip, pad, 20);
            Check(drive.Step(grip with { FrontLeft = Wheel(1.6, combined: 1.8) }, pad, 40) == pad, "后驱前轮空转不能触发 TCS");
            var slippery = grip with { RearLeft = Wheel(1.6, combined: 1.8), RearRight = Wheel(1.35, combined: 1.6) };
            Check(drive.Repeat(slippery, pad, 60, 180).RightTrigger < 185, "驱动轮空转必须被闭环压住");
            Check(drive.Repeat(grip, pad, 200, 1300).RightTrigger == pad.RightTrigger, "恢复抓地后须平顺还满油");
        });

        run("驾驶辅助：P50 起步先证实驱动轮空转，正常起步与被制动预备均给足输入", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction);
            var pad = new ShiftGamepadReport(0, 255);
            var stopped = Frame() with { Gear = 1, SpeedKmh = 0.04, VelocityZ = 0.01,
                FrontLeft = Wheel(rotation: 0), FrontRight = Wheel(rotation: 0), RearLeft = Wheel(rotation: 0), RearRight = Wheel(rotation: 0) };
            Check(drive.Step(stopped, pad, 20) == pad, "普通起步不能预先收油");
            var breakout = stopped with { SpeedKmh = 0.41, VelocityZ = 0.11, AccelerationZ = 1.7,
                RearLeft = Wheel(4.42, combined: 4.42, rotation: 22.8), RearRight = Wheel(4.35, combined: 4.35, rotation: 22.5) };
            Check(drive.Step(breakout, pad with { LeftTrigger = 255 }, 40).RightTrigger == 255, "同时踩刹车预备时保留驾驶员油门");
            Check(drive.Step(breakout, pad, 60).RightTrigger < 200 && drive.Controller.Active.HasFlag(DriveAssistFeature.Traction),
                "即使车速仅 0.41 km/h，可信的驱动轮空转也应在松刹后立即减油");
            var restoredGrip = stopped with { SpeedKmh = 3.6, VelocityZ = 1, RearLeft = Wheel(rotation: 7), RearRight = Wheel(rotation: 7) };
            Check(drive.Step(restoredGrip, pad, 80).RightTrigger < 255, "低速初步恢复抓地时不能一帧跳回满油");
            Check(drive.Repeat(restoredGrip, pad, 100, 600) == pad, "抓地恢复后及时交还满油");
        });

        run("驾驶辅助：下一挡确认前保留 TCS，正常赛道车干净升挡保持原油门", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction);
            var pad = new ShiftGamepadReport(0, 255);
            var up = pad.WithShift(ShiftGamepadReport.For(ShiftDirection.Up));
            var gear = Frame() with { Gear = 1, SpeedKmh = 117, VelocityZ = 32.5 };
            drive.Step(gear, pad, 20);
            Check(drive.Controller.Render(pad, up, 22, shifting: true) == up, "抓地充足的换挡不能增加任何限扭");
            var slipping = gear with { RearLeft = Wheel(2.4, combined: 2.5, rotation: 305) };
            var limited = drive.Repeat(slipping, pad, 40, 120).RightTrigger;
            var whileShifting = drive.Controller.Render(pad, up, 125, shifting: true);
            Check(limited < 200 && whileShifting.RightTrigger <= limited && whileShifting.Buttons == up.Buttons,
                "开始离合升挡时沿用 TCS 上限，B 和 L3 不受影响");
            drive.Controller.Feed(slipping with { Gear = 11, TimestampMs = 140 }, pad, 140, shifting: true);
            Check(drive.Controller.Render(pad, up, 140, shifting: true).RightTrigger < 200, "短暂空挡不能清空已经生效的 TCS");
            var recoveryCap = drive.Controller.Render(pad, up, 140, shifting: true).RightTrigger;
            for (var ms = 160; ms <= 220; ms += 20)
            {
                drive.Controller.Feed(gear with { Gear = 11, TimestampMs = (uint)ms, Clutch = 1 }, pad, ms);
                Check(drive.Controller.Render(pad, up, ms, shifting: true).RightTrigger <= recoveryCap,
                    "离合断开时暂时没有滑移，不能提前释放之前的实测扭矩上限");
            }
            Check(drive.Step(gear with { Gear = 2 }, pad, 240).RightTrigger <= recoveryCap,
                "下一挡首帧应等待载荷重新建立");
            Check(drive.Repeat(gear with { Gear = 2 }, pad, 260, 680).RightTrigger == 255,
                "干净接合后应平滑归还驾驶员满油");
        });

        run("驾驶辅助：直道重获抓地后及时还油，避免空转已结束仍拖慢加速", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction);
            var pad = new ShiftGamepadReport(0, 255);
            var grip = Frame();
            drive.Step(grip, pad, 20);
            var spin = grip with { RearLeft = Wheel(3.0, combined: 3.0, rotation: 200) };
            Check(drive.Repeat(spin, pad, 40, 180).RightTrigger < 30, "严重空转时应压住油门");
            var first = drive.Step(grip, pad, 200);
            Check(first.RightTrigger is > 30 and < 80, "抓地恢复时应线性还油");
            Check(drive.Repeat(grip, pad, 220, 440).RightTrigger == 255, "持续找到抓地后应在约四分之一秒内恢复满油");
        });

        run("驾驶辅助：过热车的严重抱死跨短暂降挡仍退压，健康制动不降压", () =>
        {
            var drive = new Drive(DriveAssistFeature.Abs);
            var brake = new ShiftGamepadReport(0, 0, 255);
            var steady = Frame() with { SpeedKmh = 142.7, VelocityZ = 39.6, AccelerationZ = -10.6,
                FrontLeft = Wheel(-0.32, rotation: 132), FrontRight = Wheel(-0.33, rotation: 131) };
            Check(drive.Repeat(steady, brake, 20, 100) == brake, "普通赛道的高 G 制动应保留满刹");
            var locking = steady with { FrontLeft = Wheel(-7.49, rotation: 0), FrontRight = Wheel(-7.45, rotation: 0) };
            Check(drive.Step(locking, brake, 120).LeftTrigger < 175, "可信的真抱死应加快退压");
            var held = drive.Repeat(locking, brake, 140, 220);
            Check(held.LeftTrigger <= 25 && drive.Controller.Active.HasFlag(DriveAssistFeature.Abs), "持续抱死应维持退压");
            var neutral = locking with { Gear = 11, Clutch = 1 };
            Check(drive.Repeat(neutral, brake, 240, 360).LeftTrigger <= 25, "降挡空挡约 140 ms 时仍需保住退压");
            Check(drive.Step(locking with { Gear = 2 }, brake, 380).LeftTrigger <= 25, "挂入下一挡继续此前的 ABS 压力");
            Check(drive.Step(locking with { Gear = 0 }, brake, 400) == brake, "倒挡应立即交还实体输入");
            drive.Repeat(locking, brake, 420, 460);
            Check(drive.Repeat(neutral, brake, 480, 800) == brake, "长时间空挡应结束换挡接管");
        });

        run("驾驶辅助：小轮滚动半径下限允许真实抓地估计", () =>
        {
            var road = Frame() with { SpeedKmh = 116, VelocityZ = 116 / 3.6,
                FrontLeft = Wheel(rotation: 222.2), FrontRight = Wheel(rotation: 222.2),
                RearLeft = Wheel(rotation: 222.2), RearRight = Wheel(rotation: 222.2) };
            var estimator = new DriveAssistEstimator();
            var profile = DriveAssistProfile.From(road, false);
            estimator.Update(road, default, profile, 0.022, false, false);
            var spin = road with { RearRight = Wheel(0.54, combined: 0.54, rotation: 305) };
            var state = estimator.Update(spin, new(0, 1, 0, false, false), profile, 0.022, false, false);
            Check(state.DrivenSlip > 1.5, "0.145 m 的可信小轮标定应能识别轮周明显超速");
        });

        run("驾驶辅助：同值的持续 ABS / TCS 限制在新遥测到达时会重申", () =>
        {
            var target = new RelayPad { Physical = new(0, 0, 255) };
            var clock = new GamepadShiftInputTests.ManualClock();
            using var relay = new GamepadShiftInput(() => target, () => true, () => false, clock);
            relay.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            relay.ConfigureDriveAssist(new() { Features = DriveAssistFeature.Abs });
            var events = new List<ShiftTraceEvent>();
            relay.SetTraceSink(events.Add);
            void Feed(TelemetrySample sample)
            {
                clock.Advance(20);
                relay.FeedDriveAssist(sample with { TimestampMs = (uint)clock.GetTimestamp() });
            }
            var normal = Frame();
            Feed(normal);
            var sent = target.ReportCount;
            Feed(normal);
            Check(target.ReportCount == sent, "正常抓地时相同的输入继续去重");
            var locked = normal with { FrontRight = Wheel(-4.4, combined: 4.4, rotation: 0) };
            for (var i = 0; i < 12; i++) Feed(locked);
            sent = target.ReportCount;
            var limit = target.Last;
            Feed(locked);
            Check(target.Last == limit && limit.LeftTrigger < 30 && target.ReportCount == sent + 1,
                "抱死时应持续发送已计算的退刹值，不能只在数值变化时提交");
            Check(events.Last() is { Action: "assist_frame", Detail: "refreshed", PhysicalReport.LeftTrigger: 255, AssistFresh: true } trace &&
                trace.Report == limit && trace.AppliedAssist == DriveAssistFeature.Abs && trace.TelemetryTimestampMs == clock.GetTimestamp(),
                "记录应把实体刹车、重申的虚拟输出和遥测时间戳对齐");
        });

        run("驾驶辅助：弯中限扭可以单独打开并在线性出弯时归还动力", () =>
        {
            var drive = new Drive(DriveAssistFeature.CornerTraction);
            var pad = new ShiftGamepadReport(0, 240, LeftX: 17000);
            var softSlip = Frame() with { RearLeft = Wheel(0.71, 0.8, 0.9), RearRight = Wheel(0.68, 0.7, 0.84) };
            Check(drive.Repeat(softSlip, pad with { LeftX = 0 }, 20, 140) == pad with { LeftX = 0 }, "直道不收动力");
            Check(drive.Repeat(softSlip with { AccelerationX = 10, FrontLeft = Wheel(angle: 0.83) }, pad, 160, 280).RightTrigger < pad.RightTrigger,
                "同样滑移在高横向负载时必须收油");
        });

        run("驾驶辅助：游戏 ABS 接管后原样传递每次实体刹车输入", () =>
        {
            var target = new RelayPad { Physical = new(0, 0, 255, LeftX: 17000) };
            var clock = new GamepadShiftInputTests.ManualClock();
            using var relay = new GamepadShiftInput(() => target, () => true, () => false, clock);
            Check(relay.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult() is null, "应连接实体转发");
            var settings = new DriveAssistSettings { Features = DriveAssistFeature.Abs | DriveAssistFeature.AdaptiveBraking |
                DriveAssistFeature.TrailBrake | DriveAssistFeature.Stability };
            var normal = Frame();
            var locked = normal with { AccelerationX = 8, AccelerationZ = -9, FrontRight = Wheel(-4.4, combined: 4.4, rotation: 0) };

            void Feed(TelemetrySample sample, int frameCount)
            {
                for (var i = 0; i < frameCount; i++)
                {
                    clock.Advance(20);
                    relay.FeedDriveAssist(sample with { TimestampMs = (uint)clock.GetTimestamp() });
                }
            }

            Check(relay.ConfigureDriveAssist(settings), "应启用软件制动辅助");
            Feed(normal, 1);
            Feed(locked, 12);
            Check(target.Last.LeftTrigger < target.Physical.LeftTrigger, "对照组必须已退刹");

            Check(relay.ConfigureDriveAssist(settings with { UseGameAbs = true }) && target.Last.LeftTrigger == 255,
                "切换到游戏 ABS 应立即清除旧的退刹值");
            Feed(locked, 12);
            Check(target.Last.LeftTrigger == 255, "即使在弯中抱死，仍应把满刹交给游戏");
            target.Physical = target.Physical with { LeftTrigger = 140 };
            clock.Advance(8);
            Check(target.Last.LeftTrigger == 140, "没有新的遥测时也应实时转发实体刹车变化");
            target.Physical = target.Physical with { LeftTrigger = 0 };
            Feed(locked with { VelocityX = -9, AngularVelocityY = 1.6 }, 4);
            Check(target.Last.LeftTrigger == 0, "驾驶辅助不应在游戏 ABS 模式下擅自补刹");

            target.Physical = target.Physical with { LeftTrigger = 255 };
            relay.ConfigureDriveAssist(settings);
            Feed(normal, 1);
            Feed(locked, 12);
            Check(target.Last.LeftTrigger < target.Physical.LeftTrigger, "退出游戏 ABS 模式应恢复原有的辅助选项");
        });

        run("驾驶辅助：游戏 TCS 保留弯中油门控制，直道和松油交还给实体扳机", () =>
        {
            var drive = new Drive();
            drive.Controller.Configure(new() { UseGameTractionControl = true, TargetSpeedKmh = 100,
                Features = DriveAssistFeature.Traction | DriveAssistFeature.AdaptiveTraction |
                    DriveAssistFeature.CornerTraction | DriveAssistFeature.Cruise });
            var gas = new ShiftGamepadReport(0, 240);
            var straight = Frame();
            var turning = gas with { LeftX = 20000 };
            var loadedCorner = straight with { AccelerationX = 9, AngularVelocityY = 0.44 };
            var first = drive.Step(loadedCorner, turning, 20).RightTrigger;
            Check(first > gas.RightTrigger * 0.60 && first < gas.RightTrigger, "在弯中开启辅助时从实体油门平缓收油");
            drive.Controller.Suspend();

            var spinning = straight with { RearLeft = Wheel(2.4, combined: 2.5, rotation: 150) };
            Check(drive.Repeat(spinning, gas, 40, 240) == gas, "直道空转时把原始油门交给游戏 TCS");

            var limited = drive.Repeat(loadedCorner, turning, 260, 540);
            Check(limited.RightTrigger > 0 && limited.RightTrigger < gas.RightTrigger && limited.LeftX == turning.LeftX &&
                  drive.Controller.Active.HasFlag(DriveAssistFeature.CornerTraction), "有横向负载时可在车轮空转前限油");
            var released = turning with { RightTrigger = 0 };
            Check(drive.Controller.Render(released, released, 548) == released, "松开油门应立即归零，不等待下一帧遥测");
            Check(drive.Repeat(straight, gas, 560, 1100) == gas, "出弯应归还驾驶员请求的全部动力");

            drive.Controller.RequestCruise(1100);
            Check(drive.Repeat(straight with { SpeedKmh = 54, VelocityZ = 15 }, default, 1120, 1320) == default,
                "即使以前选过巡航，游戏 TCS 模式也不应在松油时自行加油");
        });

        run("驾驶辅助：启用游戏 ABS 和 TCS 后自动降挡仍正常补油", () =>
        {
            var target = new RelayPad { Physical = new(0, 240, LeftX: 20000, RightX: 230) };
            var clock = new GamepadShiftInputTests.ManualClock();
            using var relay = new GamepadShiftInput(() => target, () => true, () => false, clock);
            var shift = new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination, AutomaticClutch = true };
            Check(relay.ConnectAsync(shift, default).GetAwaiter().GetResult() is null, "应连接实体转发");
            Check(relay.ConfigureDriveAssist(new() { UseGameAbs = true, UseGameTractionControl = true,
                Features = DriveAssistFeature.Abs | DriveAssistFeature.Traction | DriveAssistFeature.CornerTraction }), "应启用配合模式");
            var corner = Frame() with { AccelerationX = 9, AngularVelocityY = 0.44 };
            for (var ms = 20; ms <= 240; ms += 20)
            {
                clock.Advance(20);
                relay.FeedDriveAssist(corner with { TimestampMs = (uint)ms });
            }
            const byte blip = 235;
            Check(target.Last.RightTrigger < blip, "换挡前应有弯中限油");
            target.Physical = target.Physical with { RightTrigger = 0, LeftTrigger = 75 };
            Check(relay.TryShift(ShiftDirection.Down, shift, downshiftTrigger: blip) == ShiftInputResult.Sent &&
                  target.Last == target.Physical.WithShift(ShiftGamepadReport.For(ShiftDirection.Down, true, blip)),
                "换挡脉冲应保留降挡、离合、补油和实体刹车");
            relay.Release();
            Check(target.Last == target.Physical, "松开换挡组合后应恢复实体报告");
        });

        run("驾驶辅助：弹射用转速控油并在松刹时短按离合", () =>
        {
            var drive = new Drive(DriveAssistFeature.Launch);
            var pad = new ShiftGamepadReport(0, 255, 255);
            var stopped = Frame() with { SpeedKmh = 0, VelocityZ = 0, Gear = 1, Rpm = 4950, BoostPsi = 10 };
            var staged = drive.Repeat(stopped, pad, 20, 220);
            Check(staged.RightTrigger < 250 && staged.LeftTrigger == 255 && drive.Controller.BlocksShift, "蓄压期间应保留刹车并限制转速");
            var released = drive.Step(stopped, pad with { LeftTrigger = 0 }, 240);
            Check((released.Buttons & Clutch) != 0 && released.RightTrigger > 0, "松刹后需要短促的 L3 衰减联动");
            var moving = stopped with { SpeedKmh = 15, VelocityZ = 4, AccelerationZ = 8 };
            Check((drive.Repeat(moving, pad with { LeftTrigger = 0 }, 260, 420).Buttons & Clutch) == 0, "起步后必须松开离合");
        });

        run("驾驶辅助：任一轮逼近抱死则线性退刹，松刹立即归零", () =>
        {
            var drive = new Drive(DriveAssistFeature.Abs | DriveAssistFeature.Traction | DriveAssistFeature.TrailBrake);
            var pad = new ShiftGamepadReport(0, 0, 235, 17000);
            drive.Step(Frame(), pad, 20);
            var lockup = Frame() with { FrontLeft = Wheel(-1.55, rotation: 19), AccelerationX = 5, AccelerationZ = -9 };
            var output = drive.Repeat(lockup, pad, 40, 120);
            Check(output.LeftTrigger > 0 && output.LeftTrigger < 170 && drive.Controller.Active.HasFlag(DriveAssistFeature.Abs), "单轮抱死应由 ABS 胜出");
            Check(drive.Step(lockup, pad with { LeftTrigger = 0 }, 140).LeftTrigger == 0, "松开实体 LT 不得继续刹车");
        });

        run("驾驶辅助：循迹入弯随转向和前轴负载加深而退刹", () =>
        {
            var drive = new Drive(DriveAssistFeature.TrailBrake);
            var brake = new ShiftGamepadReport(0, 0, 200);
            drive.Repeat(Frame(), brake, 20, 100);
            var corner = Frame() with { FrontLeft = Wheel(combined: 1), FrontRight = Wheel(combined: 1.1), AccelerationX = 9 };
            Check(drive.Repeat(corner, brake with { LeftX = 29000 }, 120, 260).LeftTrigger < 175, "转入抓地已紧的弯时应平滑退刹");
        });

        run("驾驶辅助：抱死解除后尽快还刹，满刹不会被循迹辅助持续削弱", () =>
        {
            var drive = new Drive(DriveAssistFeature.Abs | DriveAssistFeature.TrailBrake | DriveAssistFeature.AdaptiveBraking |
                DriveAssistFeature.Understeer | DriveAssistFeature.Thermal);
            var brake = new ShiftGamepadReport(0, 0, 255, -32768);
            var healthy = Frame() with { SpeedKmh = 127.52, VelocityZ = 35.33, VelocityX = 2.48, Rpm = 4886,
                Gear = 5, AngularVelocityY = -0.62, AccelerationX = -13.92, AccelerationZ = -7.45,
                FrontLeft = Wheel(-0.33, 0.57, 0.63, 110) with { TireTempC = 132 }, FrontRight = Wheel(-0.31, 0.59, 0.65, 107),
                RearLeft = Wheel(-0.24, 0.31, 0.39, 110), RearRight = Wheel(-0.22, 0.29, 0.36, 110) };
            drive.Repeat(healthy, brake, 20, 100);
            var locked = healthy with { FrontLeft = Wheel(-4.2, 0.6, 4.22, 0) };
            Check(drive.Repeat(locked, brake, 120, 220).LeftTrigger < 65, "真实抱死时仍需要快速退压");
            var result = drive.Repeat(healthy, brake, 240, 380);
            Check(result.LeftTrigger >= 224 && drive.Step(healthy, brake with { LeftTrigger = 0 }, 400).LeftTrigger == 0,
                "车轮恢复转动后，约 150 ms 应还回足够的重刹，并允许玩家立即松开");
        });

        run("驾驶辅助：撞击后动力受限时暂停动力辅助，车轮恢复后继续接受脚刹", () =>
        {
            var drive = new Drive(DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability | DriveAssistFeature.Traction |
                DriveAssistFeature.AdaptiveTraction | DriveAssistFeature.PowerManagement | DriveAssistFeature.Abs);
            var throttle = new ShiftGamepadReport(0, 220);
            var loaded = Frame() with { Gear = 2, Rpm = 4200, Throttle = 0.86, TorqueNm = 240, PowerKw = 105, AccelerationZ = 3 };
            drive.Repeat(loaded, throttle, 20, 100);
            var afterHit = loaded with { SpeedKmh = 44, VelocityZ = 44 / 3.6, Rpm = 1510, TorqueNm = 1, PowerKw = 0.16,
                FrontLeft = Wheel(0.04, rotation: 37), FrontRight = Wheel(0.06, rotation: 36),
                RearLeft = Wheel(0.08, rotation: 36), RearRight = Wheel(0.09, rotation: 37) };
            drive.Step(afterHit with { AccelerationX = 65, AccelerationZ = -51 }, throttle, 120);
            var output = drive.Repeat(afterHit with { AccelerationZ = -0.4 }, throttle, 140, 460);
            Check(output.RightTrigger == throttle.RightTrigger &&
                drive.Controller.Snapshot(460, armed: true, includeReports: false).Collision.PowerLimited,
                "撞后的低动力是游戏侧输出迹象，不能再通过辅助削弱玩家油门");
            var brake = new ShiftGamepadReport(0, 0, 255);
            Check(drive.Step(afterHit with { Brake = 1, Throttle = 0, AccelerationZ = -7 }, brake, 480) == brake,
                "动力恢复期间仍应保留正常脚刹");
            var restored = afterHit with { Rpm = 2400, PowerKw = 73, TorqueNm = 290, AccelerationZ = 2.1 };
            drive.Repeat(restored, throttle, 500, 800);
            Check(!drive.Controller.Snapshot(800, armed: true, includeReports: false).Collision.Holding,
                "动力稳定恢复后应退出撞击保护");
        });

        run("驾驶辅助：后轴负滑移尖峰只在降挡后触发补油或离合", () =>
        {
            var drive = new Drive(DriveAssistFeature.Slipper);
            drive.Step(Frame(), default, 20);
            var spike = Frame() with { Gear = 2, RearLeft = Wheel(-1.45), RearRight = Wheel(-1.2) };
            var output = drive.Step(spike, default, 40);
            Check((output.Buttons & Clutch) != 0 || output.RightTrigger > 0, "降挡抱死需要卸掉传动冲击");
            output = drive.Repeat(spike, default, 60, 360);
            Check(output == default, "持续负滑移不能重复自动补油");
        });

        run("驾驶辅助：落地优先于反打，悬架稳定后恢复实体输入", () =>
        {
            var drive = new Drive(DriveAssistFeature.Landing | DriveAssistFeature.CounterSteer);
            var pad = new ShiftGamepadReport(0, 255, LeftX: 31000);
            drive.Step(Frame(), pad, 20);
            drive.Repeat(WithTravel(Frame(), 0) with { AccelerationY = -10 }, pad, 40, 100);
            var touchdown = WithTravel(Frame(), 0.65) with { AccelerationY = 20, AngularVelocityX = 1.5, AngularVelocityY = 2, VelocityX = -12,
                RearLeft = Wheel(1.3, 1.4, travel: 0.65) };
            var output = drive.Step(touchdown, pad, 120);
            Check(output.LeftX >= 0 && output.LeftX < 17500 && output.RightTrigger < 160 &&
                drive.Controller.Active.HasFlag(DriveAssistFeature.Landing), "落地时的减幅应盖过反打");
            output = drive.Repeat(Frame(), pad, 140, 1100);
            Check(output == pad, "落地稳定后应恢复满油和转向");
        });

        run("驾驶辅助：落地检查前后轴轮速差，四轮同步后才提前松开保护", () =>
        {
            var drive = new Drive(DriveAssistFeature.Landing);
            var pad = new ShiftGamepadReport(0, 255, LeftX: 20000);
            drive.Step(Frame(), pad, 20);
            drive.Repeat(WithTravel(Frame(), 0) with { AccelerationY = -10 }, pad, 40, 100);
            var spinning = Frame() with { RearLeft = Wheel(rotation: 94), RearRight = Wheel(rotation: 94) };
            Check(drive.Repeat(spinning, pad, 120, 420).RightTrigger < 85, "左右轮速相同但前后轴不同步时，仍要限制油门");
            Check(drive.Repeat(Frame(), pad, 440, 800).RightTrigger == 255, "整车轮速恢复后应放开限幅");
        });

        run("驾驶辅助：单侧水洼加轮速差和偏航突变才触发脉冲", () =>
        {
            var drive = new Drive(DriveAssistFeature.SplitGrip);
            var pad = new ShiftGamepadReport(0, 180);
            drive.Repeat(Frame(), pad, 20, 60);
            var puddle = Frame() with { FrontRight = Wheel(-0.5, rotation: 39) with { PuddleDepth = 1 }, AngularVelocityY = 0.8 };
            var result = drive.Step(puddle, pad, 80);
            Check(result.LeftX < 0 && result.RightTrigger < pad.RightTrigger, "右拉偏必须给左向短脉冲");
            Check(drive.Repeat(Frame(), pad, 100, 480) == pad, "离开水洼后不应保留转向偏置");
        });

        run("驾驶辅助：坡顶失重和悬架打底可分别缓和输入", () =>
        {
            var pad = new ShiftGamepadReport(0, 220, LeftX: 22000);
            var fast = Frame() with { SpeedKmh = 130, VelocityZ = 36 };
            var crest = new Drive(DriveAssistFeature.Crest);
            crest.Repeat(fast, pad with { RightTrigger = 120 }, 20, 100);
            var result = crest.Step(WithTravel(fast, 0.17) with { AccelerationY = -6 }, pad, 120);
            Check(result.LeftX < pad.LeftX && result.RightTrigger < pad.RightTrigger, "坡顶应同时限制猛打和猛补油");
            var bottom = new Drive(DriveAssistFeature.BottomOut);
            bottom.Step(fast, default, 20);
            result = bottom.Step(WithTravel(fast, 0.99), pad, 40);
            Check(result.LeftX < pad.LeftX && result.RightTrigger < pad.RightTrigger, "打底应过滤方向和油门跳变");
        });

        run("驾驶辅助：冷胎、高温和驱动预设修改介入阈值", () =>
        {
            var pad = new ShiftGamepadReport(0, 240, LeftX: 18000);
            var barelySlip = Frame() with { RearLeft = Wheel(0.78, combined: 0.75), RearRight = Wheel(0.76, combined: 0.75) };
            var hot = new Drive(DriveAssistFeature.Traction | DriveAssistFeature.Thermal);
            var cold = new Drive(DriveAssistFeature.Traction | DriveAssistFeature.Thermal);
            var baseCar = new Drive(DriveAssistFeature.Traction);
            Check(baseCar.Repeat(barelySlip, pad, 20, 180).RightTrigger == pad.RightTrigger, "暖胎在阈值内应保持油门");
            Check(cold.Repeat(barelySlip with { RearLeft = barelySlip.RearLeft with { TireTempC = 36 } }, pad, 20, 180).RightTrigger < pad.RightTrigger &&
                hot.Repeat(barelySlip with { RearRight = barelySlip.RearRight with { TireTempC = 132 } }, pad, 20, 180).RightTrigger < pad.RightTrigger,
                "偏冷或过热的轮胎应收紧可用滑移");
            var fwd = Frame() with { DrivetrainType = 0, CarOrdinal = 3852 };
            var rwd = fwd with { DrivetrainType = 1 };
            var profile = DriveAssistProfile.From(fwd, true);
            Check(profile.FrontWeight != 0.5 && profile.MassKg > 0 && profile.FrontLimit < DriveAssistProfile.From(rwd, true).FrontLimit,
                "实时驱动形式和车型配重要影响前轴设定");
            var fahrenheit = new Drive(DriveAssistFeature.Traction | DriveAssistFeature.Thermal, fahrenheit: true);
            var otherUnit = barelySlip with { RearLeft = barelySlip.RearLeft with { TireTempC = 179.6 }, RearRight = barelySlip.RearRight with { TireTempC = 179.6 },
                FrontLeft = barelySlip.FrontLeft with { TireTempC = 179.6 }, FrontRight = barelySlip.FrontRight with { TireTempC = 179.6 } };
            Check(fahrenheit.Repeat(otherUnit, pad, 20, 180).RightTrigger == pad.RightTrigger, "应支持华氏遥测而避免把暖胎当成过热");
        });

        run("驾驶辅助：循迹刹车识别带负号的综合滑移", () =>
        {
            var drive = new Drive(DriveAssistFeature.TrailBrake);
            var pad = new ShiftGamepadReport(0, 0, 200, 27000);
            var corner = Frame() with { AccelerationX = 3.5, FrontLeft = Wheel(combined: -1.2), FrontRight = Wheel(combined: -1.1) };
            Check(drive.Repeat(corner, pad, 20, 200).LeftTrigger < 155, "综合滑移的绝对值代表轮胎负荷");
        });

        run("驾驶辅助：巡航需显式启动，踩刹车后不会自行重接管", () =>
        {
            var drive = new Drive(DriveAssistFeature.Cruise);
            var slow = Frame() with { SpeedKmh = 54, VelocityZ = 15 };
            Check(drive.Repeat(slow, default, 20, 80) == default, "勾选后不能立刻加油");
            drive.Controller.RequestCruise(80);
            Check(drive.Repeat(slow, default, 100, 260).RightTrigger > 15, "低于目标时自动加油");
            var fast = slow with { SpeedKmh = 77, VelocityZ = 21 };
            Check(drive.Repeat(fast, default, 280, 580).LeftTrigger > 15, "超过目标时自动减速");
            var manual = new ShiftGamepadReport(0, 0, 153);
            Check(drive.Step(fast, manual, 600) == manual && drive.Repeat(slow, default, 620, 800) == default, "刹车取消巡航且需重新启动");
        });

        run("驾驶辅助：定圆从真实运动轨迹建立半径并产出稳态试验", () =>
        {
            var drive = new Drive(DriveAssistFeature.Cruise, skidpad: true);
            var pad = new ShiftGamepadReport(0, 0, LeftX: 12000);
            drive.Controller.RequestCruise(0);
            for (var ms = 20; ms <= 2240; ms += 20)
            {
                var angle = 0.65 * ms / 1000;
                var corner = Frame() with { SpeedKmh = 58.5, VelocityZ = 16.25, AngularVelocityY = 0.65,
                    AccelerationX = 10.56, PositionX = 25 * (1 - Math.Cos(angle)), PositionZ = 25 * Math.Sin(angle) };
                drive.Step(corner, pad, ms);
            }
            var run = drive.Controller.CruiseResult;
            Check(run is { Mode: DriveAssistRunMode.Skidpad, Seconds: > 2, RadiusMeters: > 22 and < 28, MeanLateralG: > 1, BestSteadyLateralG: > 1 },
                "应能记录实测半径和横向 G，供两次调校对比");
        });

        run("驾驶辅助：反馈按实际介入时长计分，并在换圈时封存上一圈", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction | DriveAssistFeature.Feedback);
            var pad = new ShiftGamepadReport(0, 230);
            var lap = Frame() with { RacePosition = 2, LapNumber = 1, CurrentRaceTimeSeconds = 4 };
            drive.Repeat(lap, pad, 20, 100);
            drive.Repeat(lap with { RearLeft = Wheel(1.5, combined: 1.8) }, pad, 120, 200);
            drive.Step(lap with { LapNumber = 2 }, pad, 220);
            var previous = drive.Controller.PreviousLap;
            Check(previous is { LapNumber: 1, Seconds: > 0.1 } && previous.Shares.Any(share =>
                share.Feature == DriveAssistFeature.Traction && share.Seconds > 0 && share.Seconds < share.PhaseSeconds),
                "日志需区分有介入与无介入的驾驶时间");
        });

        run("驾驶辅助：遥测超时、重包、倒挡和非法值均交还实体手柄", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction);
            var pad = new ShiftGamepadReport(0, 255, LeftY: 3000, RightY: -4000);
            var sliding = Frame() with { RearLeft = Wheel(1.8, combined: 2.1) };
            Check(drive.Repeat(sliding, pad, 20, 120).RightTrigger < 240, "先建立有效介入");
            drive.Controller.Feed(sliding with { TimestampMs = 120 }, pad, 210);
            Check(drive.Controller.Render(pad, pad, 230) == pad, "重复游戏时间戳不能续命");
            Check(drive.Step(sliding with { Gear = 0 }, pad, 250) == pad, "倒挡须完全让出");
            Check(drive.Step(sliding with { AngularVelocityY = double.NaN }, pad, 270) == pad, "无效数值须完全让出");
        });

        run("驾驶辅助：离地时立即退出已生效的 ABS", () =>
        {
            var drive = new Drive(DriveAssistFeature.Abs);
            var brake = new ShiftGamepadReport(0, 0, 235);
            var lockup = Frame() with { FrontLeft = Wheel(-1.6, rotation: 16) };
            Check(drive.Repeat(lockup, brake, 20, 120).LeftTrigger < brake.LeftTrigger, "先建立接地时的退刹");
            var airborne = WithTravel(lockup, 0) with { AccelerationY = -13 };
            Check(drive.Step(airborne, brake, 140) == brake && drive.Controller.Phase == DriveAssistPhase.Airborne,
                "车轮离地后必须立即撤销退刹");
        });

        run("驾驶辅助：同一辆车传送时立即旁路，过渡期结束后才恢复", () =>
        {
            var drive = new Drive(DriveAssistFeature.Traction);
            var pad = new ShiftGamepadReport(0, 240);
            var sliding = Frame() with { PositionX = 10, RearLeft = Wheel(1.8, combined: 2.1) };
            Check(drive.Repeat(sliding, pad, 20, 120).RightTrigger < pad.RightTrigger, "先建立传送前的限油");
            var jumped = sliding with { PositionX = 900 };
            Check(drive.Repeat(jumped, pad, 140, 620) == pad, "位置突变后整段过渡期必须原样通过");
            Check(drive.Repeat(jumped, pad, 640, 740).RightTrigger < pad.RightTrigger, "新场景稳定后恢复辅助");
        });

        run("驾驶辅助：共享手柄转发保留无关轴，失焦和超时不保留修正", () =>
        {
            var target = new RelayPad { Physical = new(0x20, 220, LeftY: -4400, RightX: 302, RightY: -804) };
            var clock = new GamepadShiftInputTests.ManualClock();
            var focused = true;
            using var relay = new GamepadShiftInput(() => target, () => focused, () => false, clock);
            Check(relay.ConnectAsync(new AutomaticShiftSettings { AutomaticClutch = false }, default).GetAwaiter().GetResult() is null,
                "测试应连接实体输入转发");
            var slip = Frame() with { RearLeft = Wheel(1.8, combined: 2) };
            relay.FeedDriveAssist(slip);
            Check(target.Last == target.Physical, "未启用时虚拟手柄必须逐轴转发");
            Check(relay.ConfigureDriveAssist(new() { Features = DriveAssistFeature.Traction | DriveAssistFeature.Feedback }), "应可独立武装辅助");
            DriveAssistFrame? frame = null;
            for (var t = 20; t <= 120; t += 20)
            {
                clock.Advance(20);
                frame = relay.FeedDriveAssist(slip with { TimestampMs = (uint)t });
            }
            Check(target.Last.RightTrigger < 195 && target.Last with { RightTrigger = target.Physical.RightTrigger } == target.Physical &&
                frame?.Applied.HasFlag(DriveAssistFeature.Traction) == true, "一个输出报告应同时包含 ECU 油门和全部原样转发的轴");
            focused = false;
            clock.Advance(8);
            Check(target.Last == target.Physical, "失去游戏焦点后下一轮手柄采样应消除介入");
            focused = true;
            clock.Advance(12);
            relay.FeedDriveAssist(slip with { TimestampMs = 140 });
            Check(target.Last.RightTrigger < target.Physical.RightTrigger, "恢复有效驾驶数据后可再次观察介入");
            clock.Advance(110);
            Check(target.Last == target.Physical, "没有新遥测时，轮询须恢复实体油门");
            relay.ConfigureDriveAssist(null);
            Check(relay.IsForwarding && !relay.GetDriveAssistStatus(false).Armed && target.Last == target.Physical, "停止辅助不能中断手柄转发");
        });

        run("驾驶辅助：公平模式阻止接管和换挡脉冲，保留手柄直通", () =>
        {
            var pad = new RelayPad { Physical = new(0, 220, LeftX: 1234) };
            using var relay = new GamepadShiftInput(() => pad, () => true, () => false);
            var shift = new AutomaticShiftSettings { AutomaticClutch = true };
            Check(relay.ConnectAsync(shift, default).GetAwaiter().GetResult() is null, "应能转发手柄");
            Check(relay.TryShift(ShiftDirection.Up, shift) == ShiftInputResult.Sent, "先建立在途输出");
            Check(!relay.ConfigureDriveAssist(new() { Features = DriveAssistFeature.Traction, FairPlay = true }),
                "公平模式不能武装主动辅助");
            Check(pad.Last == pad.Physical && relay.TryShift(ShiftDirection.Down, shift) != ShiftInputResult.Sent,
                "公平模式必须清除在途输出并拒绝新的换挡输出");
        });

        run("驾驶辅助：键盘转向渐进输出，实体摇杆与失焦立即接管", () =>
        {
            var pad = new RelayPad();
            var clock = new GamepadShiftInputTests.ManualClock();
            var focused = true;
            using var relay = new GamepadShiftInput(() => pad, () => focused, () => false, clock, keyboardSteer: () => 1);
            Check(relay.ConnectAsync(new AutomaticShiftSettings(), default).GetAwaiter().GetResult() is null, "应连接实体转发");
            relay.ConfigureDriveAssist(new() { Features = DriveAssistFeature.KeyboardSteering });
            clock.Advance(20);
            relay.FeedDriveAssist(Frame() with { TimestampMs = 20 });
            clock.Advance(32);
            Check(pad.Last.LeftX is > 0 and < 12_000, "按下方向键后应逐步加舵");
            pad.Physical = pad.Physical with { LeftX = -16_000 };
            clock.Advance(8);
            Check(pad.Last == pad.Physical, "实体摇杆应立即获得控制");
            pad.Physical = default;
            focused = false;
            clock.Advance(8);
            Check(pad.Last == pad.Physical, "切出游戏时方向键不再改变手柄输出");
        });

        run("驾驶辅助：高胎温震动按脉冲发出，断流立即清除", () =>
        {
            var pad = new RelayPad();
            var clock = new GamepadShiftInputTests.ManualClock();
            using var relay = new GamepadShiftInput(() => pad, () => true, () => false, clock);
            Check(relay.ConnectAsync(new AutomaticShiftSettings(), default).GetAwaiter().GetResult() is null, "应连接实体转发");
            relay.ConfigureDriveAssist(new() { Features = DriveAssistFeature.VibrationWarning });
            var sample = Frame();
            clock.Advance(20);
            relay.FeedDriveAssist(sample with { TimestampMs = 20, RearLeft = sample.RearLeft with { TireTempC = 125 } });
            Check(pad.Vibration > 0, "过热胎应触发震动");
            clock.Advance(110);
            Check(pad.Vibration == 0, "遥测过期时不能维持震动");
            clock.Advance(8);
            relay.FeedDriveAssist(sample with { TimestampMs = 138 });
            Check(pad.Vibration == 0, "恢复正常遥测不得重播旧的震动");
        });

        run("驾驶辅助：设置和介入成绩往返保存，旧文件仍按全关加载", () =>
        {
            var root = Directory.CreateTempSubdirectory("fh6-ecu-tests-");
            try
            {
                var settings = new TelemetrySettingsFile(root.FullName);
                Check(settings.Load().DriveAssist.Features == DriveAssistFeature.None, "缺省设置全关");
                File.WriteAllText(settings.FilePath, "{\"Port\":5301}");
                Check(settings.Load().DriveAssist is { Features: DriveAssistFeature.None, UseGameAbs: false, UseGameTractionControl: false,
                    TightTurnSpeedLimitKmh: 54, LearnTightTurnSpeed: false },
                    "旧配置全关");
                var selected = new DriveAssistSettings { Features = DriveAssistFeature.Crest | DriveAssistFeature.Feedback, TargetSpeedKmh = 87 };
                Check(settings.Save(new TelemetrySettings { DriveAssist = selected }) is null && settings.Load().DriveAssist == selected, "选项应能往返保存");
                foreach (var gameAssist in new[] { selected with { UseGameAbs = true }, selected with { UseGameTractionControl = true } })
                    Check(settings.Save(new TelemetrySettings { DriveAssist = gameAssist }) is null && settings.Load().DriveAssist == gameAssist,
                        "两个游戏辅助选项应能独立保存");
                ulong highFlag = 1UL << 32;
                var expanded = selected with { Features = selected.Features | (DriveAssistFeature)highFlag };
                Check(settings.Save(new TelemetrySettings { DriveAssist = expanded }) is null &&
                    ((ulong)settings.Load().DriveAssist.Features & highFlag) == highFlag,
                    "新增的高位功能必须和旧选项一起保存");
                var store = new TelemetryStore(root.FullName);
                var report = new DriveAssistRun { Seconds = 5, Shares = [new(DriveAssistFeature.Traction, DriveAssistPhase.CornerExit, 1.25, 2)] };
                store.Save(new TestResult { DriveAssist = report, Key = "ecu_segment" });
                store.Save(new TestResult { Key = "older" });
                var stored = new TelemetryStore(root.FullName).Records;
                Check(stored.Count == 2 && stored[0].DriveAssist?.Shares.Single().Seconds == 1.25 && stored[1].DriveAssist is null,
                    "历史中只给有数据的成绩写入介入摘要");
            }
            finally { root.Delete(recursive: true); }
        });

        run("驾驶辅助：原有性能测试记录本次运行的介入数据", () =>
        {
            var settings = new TelemetrySettings();
            var derived = new TelemetryDerived();
            var engine = new PerformanceTestEngine(settings);
            engine.SetEnabled([TestCatalog.CustomAcceleration(20, 40)]);
            for (var speed = 15; speed <= 45; speed += 5)
            {
                var sample = Frame() with { TimestampMs = (uint)speed * 4, SpeedKmh = speed };
                derived.Update(sample, settings);
                engine.Feed(sample, derived, new DriveAssistFrame(0.020, DriveAssistPhase.CornerExit, DriveAssistFeature.Traction, 0.64));
            }
            var result = engine.Recent.Single();
            Check(result.DriveAssist is { Seconds: > 0.07 and <= 0.10 } log && log.Shares.Single().Feature == DriveAssistFeature.Traction,
                "成绩应有本段测试期间的 ECU 介入记录");
        });

        run("驾驶辅助：高抓地赛车四轮均匀推头不误触反打，真甩尾立即救车", () =>
        {
            var drive = new Drive(DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability |
                DriveAssistFeature.Understeer | DriveAssistFeature.TightTurnSteering);
            var pad = new ShiftGamepadReport(0, 220, LeftX: short.MaxValue);
            var push = Frame() with
            {
                CarPerformanceIndex = 988, SpeedKmh = 140, VelocityZ = 38.5, VelocityX = -5.2,
                AccelerationX = 16.2, AngularVelocityY = 0.65,
                FrontLeft = Wheel(angle: 1.05, combined: 1.15), FrontRight = Wheel(angle: 1.12, combined: 1.22),
                RearLeft = Wheel(angle: 0.92, combined: 1.0), RearRight = Wheel(angle: 0.98, combined: 1.05),
            };
            var pushOut = drive.Repeat(push, pad, 20, 240);
            Check(pushOut.LeftX > 20000 && !drive.Controller.Active.HasFlag(DriveAssistFeature.CounterSteer),
                "高抓地四轮中性推头不能误触发反打把车顶向弯外");

            var slide = push with
            {
                VelocityX = -15, AngularVelocityY = 1.4,
                RearLeft = Wheel(angle: 1.45, combined: 1.6), RearRight = Wheel(angle: 1.40, combined: 1.55),
            };
            var rescue = drive.Repeat(slide, pad, 260, 420);
            Check(rescue.LeftX < 0 && drive.Controller.Active.HasFlag(DriveAssistFeature.CounterSteer),
                "真正甩尾失控时仍能迅速反打救车");
        });

        run("低速急弯：带刹循迹入弯压路肩仍可学出高抓上限并保留转向", () =>
        {
            var drive = new Drive();
            drive.Controller.Configure(new()
            {
                Features = DriveAssistFeature.Understeer | DriveAssistFeature.TightTurnSteering,
                LearnTightTurnSpeed = true,
            });
            var pad = new ShiftGamepadReport(0, 0, LeftTrigger: 128, LeftX: short.MaxValue);
            var corner = TightTurnProbe(96) with
            {
                CarPerformanceIndex = 988, AccelerationX = 2.15 * TelemetryDerived.Gravity,
                AngularVelocityY = 2.15 * TelemetryDerived.Gravity / (96 / 3.6), Brake = 0.50,
                FrontLeft = new WheelSample(0, 0.65, 1.32, 95, true, 0.08, 0.02, 0.04, 0.42, 85),
                FrontRight = Wheel(angle: 0.95, combined: 1.35, rotation: 90),
            };
            var straight = corner with { AccelerationX = 0, AngularVelocityY = 0, Brake = 0 };
            for (var turn = 0; turn < 3; turn++)
            {
                var t0 = 20 + turn * 700;
                drive.Repeat(corner, pad, t0, t0 + 480);
                drive.Repeat(straight, default, t0 + 500, t0 + 680);
            }
            var learnedLimit = drive.Controller.Snapshot(2200, armed: true, includeReports: false).LearnedTightTurnSpeedKmh;
            Check(learnedLimit is > 90 and <= 105 && DriveAssistSettings.MaximumTightTurnSpeedKmh == 90,
                "带刹压路肩的高抓急弯应学会高于 90 的上限且保留原界面滑块范围");
            var retained = drive.Repeat(corner, pad, 2220, 2400);
            Check(retained.LeftX == pad.LeftX, "循迹带刹时应继续保留完整急弯转向");
        });

        run("驾驶辅助：弯中限扭低负载不抢油、回正快还油，且不阻塞车辆学习", () =>
        {
            var gameTcs = new Drive();
            gameTcs.Controller.Configure(new()
            {
                UseGameTractionControl = true,
                Features = DriveAssistFeature.CornerTraction,
            });
            var gas = new ShiftGamepadReport(0, 255, LeftX: 14000);
            var cleanTurn = Frame() with { CarPerformanceIndex = 988, AccelerationX = 6.2, AngularVelocityY = 0.30 };
            Check(gameTcs.Repeat(cleanTurn, gas, 20, 200).RightTrigger == 255,
                "抓地远未饱和的干净过弯不应白白限扭");

            var learnDrive = new Drive();
            learnDrive.Controller.Configure(new()
            {
                Features = DriveAssistFeature.SteeringTuning | DriveAssistFeature.Damper | DriveAssistFeature.VehicleLearning,
                SteeringDeadzonePercent = 0,
                SteeringDebounceMs = 0,
            });
            var leftPad = new ShiftGamepadReport(0, 120, LeftX: (short)Math.Round(-0.35 * 32767));
            var rightPad = leftPad with { LeftX = (short)Math.Round(0.35 * 32767) };
            var left = Frame() with { SpeedKmh = 108, VelocityZ = 30, AccelerationX = -4.4, AngularVelocityY = -0.45, Steer = -0.35 };
            var right = Frame() with { SpeedKmh = 108, VelocityZ = 30, AccelerationX = 4.4, AngularVelocityY = 0.24, Steer = 0.35 };
            learnDrive.Repeat(left, leftPad, 20, 740);
            learnDrive.Repeat(right, rightPad, 760, 1480);
            Check(learnDrive.Controller.LateralStatus.SteeringGain.HasValue,
                "开启阻尼和转向微调时仍应正常完成左右转向响应学习");
        });
    }

    private sealed class Drive(DriveAssistFeature features = DriveAssistFeature.None, bool skidpad = false, bool fahrenheit = false)
    {
        public DriveAssistController Controller { get; } = new(new DriveAssistSettings { Features = features, Skidpad = skidpad, TireTempFahrenheit = fahrenheit });

        public ShiftGamepadReport Step(TelemetrySample sample, ShiftGamepadReport pad, int ms)
        {
            Controller.Feed(sample with { TimestampMs = (uint)ms }, pad, ms);
            return Controller.Render(pad, pad, ms);
        }

        public ShiftGamepadReport Repeat(TelemetrySample sample, ShiftGamepadReport pad, int start, int end)
        {
            var result = pad;
            for (var ms = start; ms <= end; ms += 20) result = Step(sample, pad, ms);
            return result;
        }
    }

    private static TelemetrySample Frame() => new()
    {
        IsRaceOn = true, CarOrdinal = 247, DrivetrainType = 1, CarPerformanceIndex = 680,
        SpeedKmh = 72, VelocityZ = 20, Gear = 3, Rpm = 3600, EngineMaxRpm = 8000, EngineIdleRpm = 900,
        FrontLeft = Wheel(), FrontRight = Wheel(), RearLeft = Wheel(), RearRight = Wheel(),
    };

    private static TelemetrySample TightTurnProbe(double speedKmh) => Frame() with
    {
        SpeedKmh = speedKmh, VelocityZ = speedKmh / 3.6, AccelerationX = 6, AngularVelocityY = 0.55,
        FrontLeft = Wheel(angle: 0.55, combined: 0.65), FrontRight = Wheel(angle: 1.4, combined: 1.5),
    };

    private static TelemetrySample WithTravel(TelemetrySample s, double travel) => s with
    {
        FrontLeft = s.FrontLeft with { NormalizedSuspensionTravel = travel }, FrontRight = s.FrontRight with { NormalizedSuspensionTravel = travel },
        RearLeft = s.RearLeft with { NormalizedSuspensionTravel = travel }, RearRight = s.RearRight with { NormalizedSuspensionTravel = travel },
    };

    private static WheelSample Wheel(double slip = 0, double angle = 0.1, double combined = 0.2, double rotation = 58, double travel = 0.38) =>
        new(slip, angle, combined, rotation, false, 0, 0.06, 0.1, travel, 82);

    private sealed class RelayPad : IShiftGamepad
    {
        public ShiftGamepadReport Physical { get; set; }
        public ShiftGamepadReport Last { get; private set; }
        public int ReportCount { get; private set; }
        public byte Vibration { get; private set; }
        public bool ForwardsPhysicalInput => true;
        public ShiftGamepadReport ReadPhysical() => Physical;
        public void Submit(ShiftGamepadReport report) { Last = report; ReportCount++; }
        public void SetAssistVibration(byte strength) => Vibration = strength;
        public void Dispose() { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
