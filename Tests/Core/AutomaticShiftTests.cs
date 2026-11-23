using System.Text.Json;
using FH6Telemetry;

internal static class AutomaticShiftTests
{
    public static void Register(Action<string, Action> run)
    {
        run("驾驶意图 · 入弯快速收油抑制误升挡，直道轻油门仍正常升挡", () =>
        {
            foreach (var algorithm in new[] { ShiftAlgorithm.LazyForza, ShiftAlgorithm.PowerDrop, ShiftAlgorithm.VirtualTcu })
            {
                using var cornerDrive = new Drive();
                cornerDrive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, MaximumGear = 6 });
                var attack = AtPower(FullLoad with { Gear = 3, SpeedKmh = 95, Rpm = 5600, Throttle = 1.0 }, 200);
                cornerDrive.Feed(attack, 440);
                cornerDrive.Feed(attack with { Throttle = 0.20, Steer = 0.32, AccelerationX = 3.5, AngularVelocityY = 0.35 }, 280);
                Check(cornerDrive.Input.Shifts.Count == 0 &&
                      cornerDrive.Status.Intent is DriverIntentKind.CornerEntry or DriverIntentKind.MidCorner,
                    $"{algorithm} 入弯前收油伴随转向应保持三挡准备出弯，实际={cornerDrive.Status.Intent}, 换挡={cornerDrive.Input.Shifts.Count}");
            }
        });

        run("驾驶意图 · 高速持续过弯保留弯中状态，漂移模式也需要真实侧滑", () =>
        {
            var tire = FullLoad.RearLeft with { CombinedSlip = 1.56, SlipAngle = 0.29, SlipRatio = 0.12 };
            var corner = AtPower(FullLoad with
            {
                Gear = 5, SpeedKmh = 180, VelocityZ = 50, VelocityX = 1.2, Rpm = 6000, Throttle = 0.55,
                Steer = 0.30, AngularVelocityY = 0.31, AccelerationX = -11.5, RearLeft = tire, RearRight = tire,
            }, 220);
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 6 });
            var derived = new TelemetryDerived();
            var settings = new TelemetrySettings();
            foreach (var driftMode in new[] { false, true })
            {
                drive.Controller.SetDriftMode(driftMode);
                settings.AutomaticShift = settings.AutomaticShift with { DriftMode = driftMode };
                drive.Feed(corner, 400);
                derived.Update(corner with { TimestampMs = drive.Timestamp }, settings);
                Check(drive.Status.Intent == DriverIntentKind.MidCorner, $"维持转向时应处于弯中，实际={drive.Status.Intent}");
                Check(derived.State == DrivingState.TurningLeft, $"车身方向与速度一致时应判为转弯，实际={derived.State}");
            }
        });

        run("驾驶意图 · 漂移标签受模式控制，真正侧滑仍抑制非限转误升挡", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, MaximumGear = 6 });
            var driftTire = FullLoad.RearLeft with { SlipRatio = 0.65, CombinedSlip = 1.75, SlipAngle = 1.25 };
            var drifting = AtPower(FullLoad with
            {
                Gear = 2, SpeedKmh = 75, VelocityZ = 19, VelocityX = 7, Rpm = 5800, Throttle = 0.22,
                Steer = -0.38, AngularVelocityY = 0.72, AccelerationX = -6.2,
                RearLeft = driftTire, RearRight = driftTire,
            }, 140);
            drive.Feed(drifting, 360);
            Check(drive.Status.Intent != DriverIntentKind.Drifting && drive.Input.Shifts.Count == 0,
                $"漂移模式关闭时应跟踪抓地但不显示漂移，实际={drive.Status.Intent}");
            drive.Controller.SetDriftMode(true);
            drive.Feed(drifting, 180);
            Check(drive.Status.Intent == DriverIntentKind.Drifting && drive.Input.Shifts.Count == 0,
                $"漂移模式开启且持续侧滑时应识别漂移，实际={drive.Status.Intent}");
            drive.Controller.SetDriftMode(false);
            Check(drive.Status.Intent != DriverIntentKind.Drifting, "关闭漂移模式应立即清除漂移标签");
            drive.Feed(AtPower(drifting with { Rpm = 8050, Throttle = 1.0 }, 210), 40);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                "关闭抓地保护时，漂移中真实触及断油红线仍应升挡保护动力");
        });

        run("驾驶意图 · 上下坡坡度感知：上坡延后升挡兼提前降挡，下坡滑行主动发动机制动", () =>
        {
            // 上坡（VelocityY > 0）延迟轻油门升挡
            using (var uphill = new Drive())
            {
                uphill.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, MaximumGear = 6 });
                var climb = FullLoad with { Gear = 3, SpeedKmh = 90, Rpm = 5360, VelocityY = 2.6, Throttle = 0.24 };
                uphill.Feed(climb, 360);
                Check(uphill.Input.Shifts.Count == 0, "上坡时应在中等偏高转速继续保持当前挡位");
            }

            // 下坡（VelocityY < 0）收油车速略增时主动降挡加强发动机制动
            using (var downhill = new Drive())
            {
                downhill.Feed(FullLoad with { Gear = 5, SpeedKmh = 108, Rpm = 5600, Throttle = 0.30 }, 420);
                downhill.Feed(FullLoad with { Gear = 6, SpeedKmh = 108, Rpm = 4900, Throttle = 0.30 }, 420);
                downhill.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, MaximumGear = 6 });
                for (var i = 0; i < 12; i++)
                {
                    var speed = 108.0 + i * 0.12;
                    downhill.Send(FullLoad with { Gear = 6, SpeedKmh = speed, Rpm = speed * (4900 / 108.0),
                        VelocityY = -3.2, Throttle = 0, Brake = 0 }, 20, downhill.Timestamp + 20);
                }
                Check(downhill.Input.Shifts.SequenceEqual([ShiftDirection.Down]) &&
                      downhill.Status.Reason == ShiftReason.CoastDownshift,
                    $"下坡滑行车速微增时应主动降入低一挡加强发动机制动，实际={downhill.Status.Reason}");
            }
        });

        run("驾驶意图 · 出弯回正方向即识别出弯加速并提前匹配动力挡", () =>
        {
            using var drive = new Drive();
            drive.Feed(AtPower(FallbackLoad with { Gear = 2, SpeedKmh = 60, Rpm = 5600 }, 285), 420);
            drive.Feed(AtPower(FallbackLoad with { Gear = 3, SpeedKmh = 60, Rpm = 4100 }, 195), 420);
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Mode5, MaximumGear = 6 });

            var entry = AtPower(FallbackLoad with { Gear = 3, SpeedKmh = 60, Rpm = 4100, Throttle = 0, Brake = 0.45,
                Steer = 0.60, AccelerationX = 0.68 * TelemetryDerived.Gravity, AngularVelocityY = 0.6 }, 0);
            drive.Feed(entry, 160);
            for (var i = 0; i < 12; i++)
            {
                var steer = 0.56 - i * 0.04;
                drive.Send(AtPower(FallbackLoad with
                {
                    Gear = 3, SpeedKmh = 60, Rpm = 4100, Throttle = 0.65, Brake = 0,
                    Steer = steer, AccelerationX = 0.56 * TelemetryDerived.Gravity, AngularVelocityY = 0.35,
                }, 195), 20, drive.Timestamp + 20);
            }
            Check(drive.Status.Intent == DriverIntentKind.CornerExit &&
                  drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 2,
                $"正在回正方向踩油门出弯时应及时降入高动力挡，实际意图={drive.Status.Intent}, 换挡={drive.Input.Shifts.Count}");
        });

        run("驾驶意图 · 循迹制动/负滑移锁死时仍果断降挡并平滑加大补油", () =>
        {
            byte dryBlip, lockupBlip;
            using (var dry = new Drive())
            {
                LearnBrakeRatios(dry);
                dry.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, GamepadMode = GamepadShiftMode.Combination, MaximumGear = 4 });
                dry.Feed(FullLoad with { Gear = 4, Rpm = 5060, SpeedKmh = 110, Brake = 0.65, Throttle = 0 }, 220);
                dryBlip = dry.Input.LastBlip!.Value;
            }
            using (var lockup = new Drive())
            {
                LearnBrakeRatios(lockup);
                lockup.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, GamepadMode = GamepadShiftMode.Combination, MaximumGear = 4 });
                var skidding = FullLoad.RearLeft with { SlipRatio = -0.80 };
                lockup.Feed(FullLoad with { Gear = 4, Rpm = 5060, SpeedKmh = 110, Brake = 0.65, Throttle = 0,
                    RearLeft = skidding, RearRight = skidding }, 220);
                Check(lockup.Input.Shifts.SequenceEqual([ShiftDirection.Down]), "驱动轮轻微锁死期间仍须正常降挡");
                lockupBlip = lockup.Input.LastBlip!.Value;
            }
            Check(lockupBlip > dryBlip && lockupBlip <= 217,
                $"制动负滑移锁死时应适度加大补油减小顿挫 ({dryBlip} -> {lockupBlip})");
        });

        run("赛用变速箱识别：首轮升挡即无感直出判定，赛用自动免离合，常规切回离合，换车重测", () =>
        {
            foreach (var isRace in new[] { true, false })
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings
                {
                    Algorithm = ShiftAlgorithm.PowerDrop,
                    GamepadMode = GamepadShiftMode.Combination,
                    AutomaticClutch = true,
                    AutoDetectRaceTransmission = true,
                    MaximumGear = 6,
                });
                var g1 = AtPower(FullLoad with { Gear = 1, SpeedKmh = 45, Rpm = 8030 }, 240);
                drive.Feed(g1, 160);
                Check(drive.Input.ClutchStates.SequenceEqual([false]), "开启无感识别后，首轮升挡应直接免离合试探，无须先跑基准");

                if (isRace)
                {
                    drive.Feed(AtPower(FullLoad with { Gear = 2, SpeedKmh = 60, Rpm = 6200 }, 240), 60);
                    Check(drive.Status.LastShiftTo == 2 &&
                          drive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless,
                        "赛用变速箱在首轮无离合确认与动力恢复迅速时应直接锁死赛用判定");
                }
                else
                {
                    drive.Feed(AtPower(FullLoad with { Gear = 11, SpeedKmh = 58, Rpm = 7200 }, 0), 160);
                    drive.Feed(AtPower(FullLoad with { Gear = 2, SpeedKmh = 60, Rpm = 6200 }, 0), 120);
                    drive.Feed(AtPower(FullLoad with { Gear = 2, SpeedKmh = 60, Rpm = 6200 }, 240), 40);
                    Check(drive.Status.LastShiftTo == 2 &&
                          drive.Status.TransmissionClutch == TransmissionClutchVerdict.StandardRequiresClutch,
                        "常规变速箱在首轮出现长换挡延迟与断动力惩罚时应立即锁死离合换挡");
                }

                drive.Feed(AtPower(FullLoad with { Gear = 2, SpeedKmh = 90, Rpm = 8030 }, 240), 160);
                Check(drive.Input.ClutchStates.SequenceEqual([false, !isRace]),
                    isRace ? "赛用变速箱第二轮升挡继续免离合" : "常规变速箱第二轮起自动改回离合换挡");

                drive.Feed(AtPower(FullLoad with { CarOrdinal = 2512, Gear = 1, SpeedKmh = 45, Rpm = 8030 }, 240), 160);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.Probing ||
                      drive.Status.TransmissionClutch == TransmissionClutchVerdict.ReadyToProbe,
                    $"换车后应重置并在新车上重新监测，实际={drive.Status.TransmissionClutch}");
                Check(!drive.Input.ClutchStates.Last() && drive.Input.EffectiveClutchSetting == true,
                    "换车后未确认新车赛用前，默认自动离合接管仍就位，首轮升挡立即免离合试探新车");

                drive.Feed(AtPower(g1 with { Rpm = 6500 }, 240), 40);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.ReadyToProbe &&
                      drive.Input.EffectiveClutchSetting == true,
                    "切回开过的车也应重新校准，并清除上一辆车待确认的换挡");
            }
        });

        run("赛用变速箱识别：无离合未确认时改用离合，仍无确认时暂停重试", () =>
        {
            foreach (var respondsWithClutch in new[] { true, false })
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, AutoDetectRaceTransmission = true, MaximumGear = 6 });
                var first = AtPower(FullLoad with { Gear = 1, SpeedKmh = 45, Rpm = 8030 }, 240);
                drive.Feed(first, 160);
                Check(drive.Input.ClutchStates.SequenceEqual([false]), "第一轮应启动无离合探测");
                for (var frame = 0; frame < 90 && drive.Input.Shifts.Count < 2; frame++)
                    drive.Send(first, 20, unchecked(drive.Timestamp + 20));
                Check(drive.Status.Enabled && drive.Input.ClutchStates.SequenceEqual([false, true]),
                    "无离合无确认后应保持启动，并在同一安全换挡条件下改用离合");
                if (respondsWithClutch)
                {
                    var second = AtPower(first with { Gear = 2, SpeedKmh = 54, Rpm = 6200 }, 235);
                    drive.Feed(second, 60);
                    Check(drive.Status.Enabled && drive.Status.LastShiftTo == 2 &&
                          drive.Status.TransmissionClutch == TransmissionClutchVerdict.StandardRequiresClutch,
                        "必须先看到带离合的确认才固定为带离合策略");
                }
                else
                {
                    for (var frame = 0; frame < 56; frame++) drive.Send(first, 20, unchecked(drive.Timestamp + 20));
                    Check(drive.Input.Shifts.Count == 2 && drive.Status.Enabled && drive.Status.State == AutomaticShiftState.ConfirmationTimedOut,
                        "两种输入都没有得到确认时应保持开启并暂停发键");
                }
            }
        });

        run("赛用变速箱识别：同车型同 PI 经菜单或断流后重新校准，常规车恢复离合", () =>
        {
            foreach (var menuFrame in new[] { true, false })
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings
                {
                    Algorithm = ShiftAlgorithm.PowerDrop,
                    GamepadMode = GamepadShiftMode.Combination,
                    AutoDetectRaceTransmission = true,
                    MaximumGear = 6,
                });
                var g1 = AtPower(FullLoad with { Gear = 1, SpeedKmh = 45, Rpm = 8030 }, 240);
                var g2 = AtPower(FullLoad with { Gear = 2, SpeedKmh = 60, Rpm = 6200 }, 240);
                drive.Feed(g1, 160);
                drive.Feed(g2, 60);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless,
                    "换车前应已判定为赛用变速箱");

                if (menuFrame) drive.Feed(g2 with { IsRaceOn = false }, 80);
                else drive.Elapse(500);
                drive.Feed(AtPower(g1 with { Rpm = 6500 }, 240), 400);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.ReadyToProbe &&
                      drive.Input.EffectiveClutchSetting == true,
                    "暂停驾驶后即使车型信息相同，也应在新一轮升挡前恢复离合接管并等待识别");

                drive.Feed(g1, 160);
                Check(drive.Input.ClutchStates.SequenceEqual([false, false]) &&
                      drive.Status.TransmissionClutch == TransmissionClutchVerdict.Probing,
                    "新一轮首个合适的升挡应触发一次无离合试探");
                drive.Feed(AtPower(g1 with { Gear = 11, SpeedKmh = 58, Rpm = 7200 }, 0), 160);
                drive.Feed(AtPower(g2, 0), 120);
                drive.Feed(g2, 40);
                drive.Feed(AtPower(g2 with { SpeedKmh = 90, Rpm = 8030 }, 240), 160);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.StandardRequiresClutch &&
                      drive.Input.ClutchStates.SequenceEqual([false, false, true]),
                    "确认新车为常规变速箱后，下一次升挡应直接使用离合");
            }
        });

        run("赛用变速箱识别：未勾选手动离合也可 60Hz 首轮无感识别，容忍打滑和断油负功率，降挡同步免离合", () =>
        {
            static void FeedUntilShift(Drive d, TelemetrySample s, int maxMs = 480)
            {
                var before = d.Input.Shifts.Count;
                for (var t = 0; t < maxMs && d.Input.Shifts.Count == before; t += 17)
                    d.Send(s, 17, unchecked(d.Timestamp + 17));
                Check(d.Input.Shifts.Count == before + 1, $"期望从 {s.Gear} 挡换挡，实际新增 {d.Input.Shifts.Count - before} 次");
            }

            static void FeedSteps(Drive d, TelemetrySample s, int steps)
            {
                for (var i = 0; i < steps; i++)
                    d.Send(s, 17, unchecked(d.Timestamp + 17));
            }

            var slipTire = FullLoad.RearLeft with { SlipRatio = 0.55 };

            // A. 未勾选自动离合 + 赛用变速箱：首轮升挡在 60Hz、断油负功率起算与换挡打滑下直接识别，之后全免离合升降挡
            using (var raceDrive = new Drive())
            {
                raceDrive.Enable(new AutomaticShiftSettings
                {
                    Algorithm = ShiftAlgorithm.PowerDrop,
                    GamepadMode = GamepadShiftMode.Combination,
                    AutomaticClutch = false,
                    AutoDetectRaceTransmission = true,
                    ProtectLaunchWheelspin = false,
                    ProtectShiftReengagement = false,
                    MaximumGear = 6,
                });
                Check(raceDrive.Status.TransmissionClutch == TransmissionClutchVerdict.ReadyToProbe, "开启后应处于无感自动监测就绪态");
                Check(raceDrive.Input.EffectiveClutchSetting == true, "未判出赛用前应先开启自动离合接管");

                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 40, Rpm = 6200 }, 240), 14);
                FeedUntilShift(raceDrive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 48, Rpm = 8030 }, -18));
                Check(raceDrive.Input.ClutchStates.SequenceEqual([false]), "首轮升挡应直接试探无离合");

                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 49, Rpm = 8030 }, -18), 2);
                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 11, SpeedKmh = 52, Rpm = 7100 }, 0), 6); // ~102ms between gears
                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 56, Rpm = 6200, RearLeft = slipTire, RearRight = slipTire }, 0), 1);
                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 58, Rpm = 6300, RearLeft = slipTire, RearRight = slipTire }, 240), 6);
                Check(raceDrive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless,
                    $"首轮 60Hz 赛用无离合快换（含空挡102ms、负功率起算与打滑）应直接识别，实际={raceDrive.Status.TransmissionClutch} (probe={raceDrive.Status.ProbeShiftMs})");
                Check(raceDrive.Input.EffectiveClutchSetting == false, "识别赛用变速箱后应自动关闭虚拟离合接管");

                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 72, Rpm = 6500 }, 240), 26);
                FeedUntilShift(raceDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 90, Rpm = 8030 }, 240));
                Check(!raceDrive.Input.ClutchStates.Last(), "赛用变速箱确认后 2→3 升挡应免离合");

                FeedSteps(raceDrive, AtPower(FullLoad with { Gear = 3, SpeedKmh = 95, Rpm = 5400, Throttle = 0.35 }, 120), 26);
                FeedUntilShift(raceDrive, FullLoad with { Gear = 3, SpeedKmh = 52, Rpm = 2900, Throttle = 0, Brake = 0.85, PowerKw = 0 });
                Check(!raceDrive.Input.ClutchStates.Last(), "赛用变速箱确认后制动降挡也应免离合快换");
            }

            // B. 未手动勾选自动离合 + 常规变速箱：首轮出现惩罚后即切为自动离合快换
            using (var stockDrive = new Drive())
            {
                stockDrive.Enable(new AutomaticShiftSettings
                {
                    Algorithm = ShiftAlgorithm.PowerDrop,
                    GamepadMode = GamepadShiftMode.Keyboard,
                    AutomaticClutch = false,
                    AutoDetectRaceTransmission = true,
                    ProtectLaunchWheelspin = false,
                    ProtectShiftReengagement = false,
                    MaximumGear = 6,
                });
                FeedSteps(stockDrive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 40, Rpm = 6200 }, 240), 14);
                FeedUntilShift(stockDrive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 48, Rpm = 8030 }, -20));
                FeedSteps(stockDrive, AtPower(FullLoad with { Gear = 11, SpeedKmh = 50, Rpm = 7000 }, 0), 11); // ~187ms between gears
                FeedSteps(stockDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 53, Rpm = 5800 }, 0), 7);  // ~119ms no power
                FeedSteps(stockDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 56, Rpm = 5950 }, 240), 6);
                Check(stockDrive.Status.TransmissionClutch == TransmissionClutchVerdict.StandardRequiresClutch,
                    $"首轮无离合惩罚长时应立即锁死常规变速箱，实际={stockDrive.Status.TransmissionClutch}");
                Check(stockDrive.Input.EffectiveClutchSetting == true, "常规变速箱应自动保持离合接管");

                FeedSteps(stockDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 72, Rpm = 6500 }, 240), 26);
                FeedUntilShift(stockDrive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 90, Rpm = 8030 }, 240));
                Check(stockDrive.Input.ClutchStates.Last(), "常规变速箱后续升挡应自动踩离合");
            }
        });

        run("赛用变速箱识别：后台监听手动升挡，开关自动换挡保留当前车的判定", () =>
        {
            var raceCar = AtPower(FullLoad with { Gear = 1, SpeedKmh = 45, Rpm = 6500 }, 240);
            var settings = new AutomaticShiftSettings
            {
                Algorithm = ShiftAlgorithm.PowerDrop,
                GamepadMode = GamepadShiftMode.Combination,
                AutomaticClutch = false,
                AutoDetectRaceTransmission = true,
                MaximumGear = 6,
            };
            using var drive = new Drive();
            drive.Controller.SetAutoDetectRaceTransmission(true);
            drive.Feed(raceCar, 120);
            drive.Feed(AtPower(raceCar with { Gear = 11, SpeedKmh = 48, Rpm = 7200 }, 0), 80);
            drive.Feed(AtPower(raceCar with { Gear = 2, SpeedKmh = 52, Rpm = 6100 }, 240), 40);
            Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless,
                $"未开启自动换挡时也应从手动无离合升挡完成识别，实际={drive.Status.TransmissionClutch}");

            drive.Enable(settings);
            Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless &&
                  drive.Input.EffectiveClutchSetting == false,
                "启用自动换挡后应直接保留既有识别结论并免离合");
            drive.Feed(AtPower(raceCar with { Gear = 2, SpeedKmh = 85, Rpm = 8030 }, 240), 220);
            Check(drive.Input.ClutchStates.SequenceEqual([false]), "同一辆车开启自动换挡后应直接免离合");

            drive.Controller.Disable();
            drive.Enable(settings);
            Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless,
                "关闭并重新打开自动换挡后应保留当前车辆的结果");
        });

        run("赛用变速箱识别：启动时忽略旧版判定，调校改变后重新校准", () =>
        {
            var directory = Directory.CreateTempSubdirectory("fh6-race-detect-cache-");
            var path = Path.Combine(directory.FullName, "speed-gear-profiles.json");
            try
            {
                var car = AtPower(FullLoad with { Gear = 1, SpeedKmh = 45, Rpm = 6500 }, 240);
                File.WriteAllText(path, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Vehicle = ShiftVehicleConfiguration.From(car),
                        Ratios = new double[AutomaticShiftSettings.MaximumForwardGears + 1],
                        Curve = new AdaptiveShiftStrategy.OutputBin[AdaptiveShiftStrategy.BinCount],
                        Transmission = TransmissionClutchVerdict.RaceClutchless,
                    },
                }));

                using var drive = new Drive(path);
                drive.Controller.SetAutoDetectRaceTransmission(true);
                drive.Feed(car, 120);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.ReadyToProbe,
                    "应根据本次驾驶校准，即使旧配置里保存了同车型同 PI 的赛用判定");
                drive.Feed(AtPower(car with { Gear = 2, SpeedKmh = 52, Rpm = 6100 }, 240), 40);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.RaceClutchless, "新样本应产生赛用判定");

                drive.Feed(car with { CarPerformanceIndex = 700 }, 40);
                Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.ReadyToProbe,
                    "更换调校导致 PI 改变后应重新校准");
            }
            finally
            {
                File.Delete(path);
                directory.Delete();
            }
        });

        run("赛用变速箱识别：极少数落入模糊区间的试探会回退一次离合基准后再终判", () =>
        {
            static void FeedUntilShift(Drive d, TelemetrySample s, int maxMs = 480)
            {
                var before = d.Input.Shifts.Count;
                for (var t = 0; t < maxMs && d.Input.Shifts.Count == before; t += 17)
                    d.Send(s, 17, unchecked(d.Timestamp + 17));
                Check(d.Input.Shifts.Count == before + 1, $"期望从 {s.Gear} 挡换挡，实际新增 {d.Input.Shifts.Count - before} 次");
            }
            static void FeedSteps(Drive d, TelemetrySample s, int steps)
            {
                for (var i = 0; i < steps; i++)
                    d.Send(s, 17, unchecked(d.Timestamp + 17));
            }

            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings
            {
                Algorithm = ShiftAlgorithm.PowerDrop,
                GamepadMode = GamepadShiftMode.Combination,
                AutomaticClutch = false,
                AutoDetectRaceTransmission = true,
                ProtectLaunchWheelspin = false,
                ProtectShiftReengagement = false,
                MaximumGear = 6,
            });
            FeedSteps(drive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 40, Rpm = 6200 }, 240), 12);
            FeedUntilShift(drive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 48, Rpm = 8030 }, 240));
            Check(drive.Input.ClutchStates.SequenceEqual([false]), "首轮先无离合试探");

            // 落入模糊区间：confirm ~221ms (215..245), between ~153ms (145..165), recovery ~272ms (255..285)
            FeedSteps(drive, AtPower(FullLoad with { Gear = 1, SpeedKmh = 49, Rpm = 8030 }, 220), 3);
            FeedSteps(drive, AtPower(FullLoad with { Gear = 11, SpeedKmh = 51, Rpm = 7100 }, 0), 9); // 153ms neutral
            FeedSteps(drive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 54, Rpm = 6000 }, 0), 3);  // confirm at step 1 = 221ms; +2 steps = 255ms
            FeedSteps(drive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 56, Rpm = 6150 }, 240), 4); // power recovery at 272ms
            Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.NeedsBaseline,
                $"落入模糊区间时应自动转入采集一次离合基准，实际={drive.Status.TransmissionClutch}");

            FeedSteps(drive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 72, Rpm = 6500 }, 240), 26);
            FeedUntilShift(drive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 90, Rpm = 8030 }, 240));
            Check(drive.Input.ClutchStates.Last(), "回退采样应自动踩离合以测得本车基准");
            FeedSteps(drive, AtPower(FullLoad with { Gear = 2, SpeedKmh = 91, Rpm = 8030 }, 220), 3);
            FeedSteps(drive, AtPower(FullLoad with { Gear = 3, SpeedKmh = 95, Rpm = 6200, Clutch = 0.8 }, 0), 4);
            FeedSteps(drive, AtPower(FullLoad with { Gear = 3, SpeedKmh = 98, Rpm = 6300 }, 240), 4);

            FeedSteps(drive, AtPower(FullLoad with { Gear = 3, SpeedKmh = 120, Rpm = 6500 }, 240), 26);
            FeedUntilShift(drive, AtPower(FullLoad with { Gear = 3, SpeedKmh = 138, Rpm = 8030 }, 240));
            FeedSteps(drive, AtPower(FullLoad with { Gear = 11, SpeedKmh = 138, Rpm = 7200 }, 0), 8);
            FeedSteps(drive, AtPower(FullLoad with { Gear = 4, SpeedKmh = 140, Rpm = 6200 }, 0), 7);
            FeedSteps(drive, AtPower(FullLoad with { Gear = 4, SpeedKmh = 142, Rpm = 6300 }, 240), 4);
            Check(drive.Status.TransmissionClutch == TransmissionClutchVerdict.StandardRequiresClutch,
                $"第二次试探后必须果断终判，实际={drive.Status.TransmissionClutch}");
        });

        run("降档补油闭环：全策略按转速差计算补油，怠速爬行零补油，过冲后自适应收敛", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            drive.Enable(new AutomaticShiftSettings
            {
                Algorithm = ShiftAlgorithm.PowerDrop,
                GamepadMode = GamepadShiftMode.Combination,
                AutomaticClutch = true,
                MaximumGear = 4,
            });
            drive.Feed(FullLoad with { Gear = 4, Rpm = 5060, SpeedKmh = 110, Brake = 0.65, Throttle = 0 }, 220);
            var firstBlip = drive.Input.LastBlip;
            Check(firstBlip is >= 100 and <= 150, $"非换挡模式5策略也应按目标转速差输出适量补油，实际 {firstBlip}");
            drive.Input.IsShiftPulseActive = true;
            drive.Feed(FullLoad with { Gear = 3, Rpm = 7600, SpeedKmh = 108, Brake = 0.65, Throttle = 0, Clutch = 1 }, 80);
            drive.Input.IsShiftPulseActive = false;
            drive.Feed(FullLoad with { Gear = 3, Rpm = 6400, SpeedKmh = 106, Brake = 0.65, Throttle = 0, Clutch = 0 }, 260);
            drive.Feed(FullLoad with { Gear = 3, Rpm = 4500, SpeedKmh = 75, Brake = 0.65, Throttle = 0 }, 220);
            var secondBlip = drive.Input.LastBlip;
            Check(secondBlip.HasValue && secondBlip.Value < firstBlip!.Value,
                $"上一次降挡补油偏高时，下次相近转速差应自动下调补油量（{firstBlip} -> {secondBlip}）");
            drive.Feed(FullLoad with { Gear = 2, Rpm = 900, SpeedKmh = 2.0, Brake = 0.5, Throttle = 0 }, 220);
            Check(drive.Input.LastBlip == 0, "低速爬行或原地回挡不应发送多余补油");
        });

        run("车速匹配：中间挡未学到时仍能结合更高挡推断步长，中等油门拖挡和轻油门断油均不卡死", () =>
        {
            using var drive = new Drive();
            var skyline = FullLoad with { CarOrdinal = 2512, EngineMaxRpm = 9000 };
            drive.Feed(skyline with { Gear = 7, SpeedKmh = 90, Rpm = 90 * 44.03, Throttle = 0.30 }, 420);
            drive.Feed(skyline with { Gear = 5, SpeedKmh = 55, Rpm = 55 * 66.86, Throttle = 0.30 }, 420);
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, AdaptiveGearMatching = true });
            drive.Feed(skyline with { Gear = 5, SpeedKmh = 55, Rpm = 55 * 66.86, Throttle = 1 }, 240);
            Check(drive.Status.TargetGear == 4 && drive.Status.Reason == ShiftReason.SpeedMatchedLearning,
                "即使六挡未学到，也应结合七挡和五挡间距在 3677 RPM 安全降入四挡");

            using var moderate = new Drive();
            moderate.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, AdaptiveGearMatching = true });
            moderate.Feed(skyline with { Gear = 7, SpeedKmh = 55, Rpm = 1400, Throttle = 0.65 }, 300);
            Check(moderate.Status.TargetGear == 6 && moderate.Status.Reason == ShiftReason.SpeedMatchedLearning,
                "明显踩下油门且发动机严重拖挡时，即使未到 82% 地板油也应恢复动力");

            using var mode5Cruise = new Drive();
            mode5Cruise.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Mode5, MaximumGear = 4 });
            mode5Cruise.Feed(AtPower(FullLoad with { Gear = 2, SpeedKmh = 79.9, Rpm = 7990, Throttle = 0.28 }, 120), 220);
            Check(mode5Cruise.Status.TargetGear == 3 && mode5Cruise.Status.Reason == ShiftReason.Mode5Limiter,
                "换挡模式5在轻中油门触及断油时仍应正常升挡");

            using var brakingGap = new Drive();
            brakingGap.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, AdaptiveGearMatching = true });
            var speed = 58.0;
            for (var i = 0; i < 16 && brakingGap.Input.Shifts.Count == 0; i++)
            {
                speed -= 0.5;
                brakingGap.Feed(skyline with { Gear = 7, SpeedKmh = speed, Rpm = speed * 44.03, Brake = 0.70, Throttle = 0 }, 20);
            }
            Check(brakingGap.Status.TargetGear == 6 && brakingGap.Status.Reason == ShiftReason.SpeedMatchedLearning,
                "制动持续减速且高挡低转、低挡未知时，车速匹配也应安全降挡恢复发动机制动");
        });

        run("车速匹配：Skyline 55 km/h 七挡满油，只学到一个挡比也能逐挡恢复动力", () =>
        {
            var skyline = FullLoad with { CarOrdinal = 2512, CarPerformanceIndex = 600, EngineMaxRpm = 9000,
                DrivetrainType = 2, Gear = 7, SpeedKmh = 55, Rpm = 2421.65, Throttle = 0.30 };
            double[] ratios = [0, 201.24, 146.31, 108.94, 84.35, 66.86, 53.70, 44.03];
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                using var drive = new Drive();
                drive.Feed(skyline, 520);
                Check(drive.Status.LearnedGears == 1, "复现只学到七挡的状态");
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, AdaptiveGearMatching = true,
                    GamepadMode = GamepadShiftMode.Combination });
                var pull = skyline with { Throttle = 1, Brake = 0.14 };
                drive.Input.DriverPedals = new ShiftPedals(0, 0.6);
                drive.Feed(pull, 360);
                Check(drive.Input.Shifts.Count == 0, "真实扳机在制动时，残留的补油读数不能触发加速降挡");
                drive.Input.DriverPedals = new ShiftPedals(1, 0.14);
                drive.Feed(pull with { FrontLeft = pull.FrontLeft with { SlipRatio = -0.62 } }, 440);
                Check(drive.Status.TargetGear == 6 && drive.Status.Reason == ShiftReason.SpeedMatchedLearning,
                    $"{algorithm} 应识别右扳机主导的低转满油请求，开始降一挡");
                drive.Feed(pull, 240);
                Check(drive.Input.Shifts.Count == 1, "游戏确认以前只发一个换挡动作");
                for (var gear = 6; gear >= 3; gear--)
                {
                    var inGear = pull with { Gear = gear, Rpm = pull.SpeedKmh * ratios[gear] };
                    drive.Input.IsShiftPulseActive = true;
                    drive.Feed(inGear, 180);
                    Check(drive.Status.LastShiftTo == gear && drive.Input.Shifts.Count == 7 - gear,
                        "遥测确认后也要等 X 松开，才可降下一挡");
                    drive.Input.IsShiftPulseActive = false;
                    drive.Feed(inGear, 500);
                    var status = drive.Status;
                    Check(status.TargetGear == (gear == 3 ? 0 : gear - 1),
                        $"{algorithm} 在 {gear} 挡应按接合后的车速转速重新选择挡位，实际 {status.State} / {status.TargetGear} / {status.Reason} / {status.LearnedGears} 个挡比");
                }
                Check(drive.Input.Shifts.SequenceEqual(Enumerable.Repeat(ShiftDirection.Down, 4)), "按车速逐挡从七挡降到三挡");
            }
        });

        run("车速匹配：一千多转满油可恢复，样本不足时保留降挡过转保护", () =>
        {
            foreach (var (rpm, active, shouldDownshift) in new[] { (1150, true, true), (1150, false, false), (3900, true, false) })
            {
                using var drive = new Drive();
                var frame = AtPower(FullLoad with { CarOrdinal = 2512, EngineMaxRpm = 9000, Gear = 7,
                    SpeedKmh = 55, Rpm = rpm, Throttle = 1 }, 20);
                drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, AdaptiveGearMatching = active });
                drive.Feed(frame, 440);
                Check((drive.Status.TargetGear == 6) == shouldDownshift,
                    $"{rpm} RPM、自适应 {active} 应按实测余量决定能否估算低一挡");
            }
        });

        run("换挡模式5：下一挡实测更有力就升挡，持续上升的动力保留至断油附近", () =>
        {
            foreach (var rising in new[] { false, true })
            {
                using var drive = new Drive();
                var rpm = rising ? 7800.0 : 6000.0;
                var speed = rpm / 80;
                var upper = AtPower(FullLoad with { Gear = 3, Rpm = speed * 60, SpeedKmh = speed }, rising ? 180 : 230);
                var lower = AtPower(FullLoad with { Gear = 2, Rpm = rpm, SpeedKmh = speed }, rising ? 245 : 160);
                drive.Feed(upper, 440);
                drive.Feed(lower, 440);
                drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Mode5, MaximumGear = 3 });
                drive.Feed(lower, 420);
                if (rising)
                {
                    Check(drive.Input.Shifts.Count == 0, "下一挡马力更低，7800 RPM 仍应保持二挡");
                    drive.Feed(AtPower(lower with { Rpm = 7990, SpeedKmh = 7990.0 / 80 }, 245), 80);
                }
                Check(drive.Status.TargetGear == 3 && drive.Status.Reason ==
                    (rising ? ShiftReason.Mode5Limiter : ShiftReason.Mode5PowerBand),
                    "应在动力衰减时提前升挡，动力继续提升时接近断油换挡");
            }
        });

        run("换挡模式5：刹车期间估算接合转速，自动离合关闭时不发送补油", () =>
        {
            foreach (var automaticClutch in new[] { true, false })
            {
                using var drive = new Drive();
                LearnBrakeRatios(drive);
                drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Mode5, MaximumGear = 4,
                    GamepadMode = GamepadShiftMode.Combination, AutomaticClutch = automaticClutch });
                drive.Input.DriverPedals = new ShiftPedals(0.20, 0.78);
                drive.Feed(FullLoad with { Gear = 4, Rpm = 5060, SpeedKmh = 110, Brake = 0.55 }, 220);
                Check(drive.Status.DownshiftCue is { Gear: 3, LandingRpm: >= 6500 and <= 6650 } &&
                      drive.Status.Reason == ShiftReason.Braking, "刹车主导时应带着制动压力降挡");
                Check(automaticClutch ? drive.Input.LastBlip is >= 100 and <= 150 : drive.Input.LastBlip is null,
                    "仅开启内置离合时为预估的转速差发送补油量");
            }
        });

        run("墙碰减速 · 无障碍物字段时仍暂停各策略并等待动力恢复", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            foreach (var strict in new[] { false, true })
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, StrictSpeedGearMatching = strict, MaximumGear = 6 });
                var before = AtPower(FullLoad with { Gear = 4, SpeedKmh = 104, VelocityZ = 27, Rpm = 4620, Throttle = 0.64 }, 180);
                drive.Feed(before, 360);
                var learned = drive.Status;
                var shiftCount = drive.Input.Shifts.Count;
                var hit = before with { SpeedKmh = 81.5, VelocityZ = 21.3, Rpm = 4300, AccelerationX = 138, AccelerationZ = -39 };
                drive.Feed(hit, 20);
                Check(drive.Status.State == AutomaticShiftState.WheelContactHold && drive.Status.TargetGear == 0,
                    $"{algorithm} 碰撞应立刻持挡，实际={drive.Status.State}");

                var capped = hit with { SpeedKmh = 55, VelocityZ = 15, Rpm = 1500, AccelerationX = 0, AccelerationZ = -1,
                    Throttle = 1, PowerKw = 0, TorqueNm = 0 };
                drive.Feed(capped, 1180);
                Check(drive.Status is { State: AutomaticShiftState.CollisionPowerHold, TargetGear: 0, Enabled: true } &&
                    drive.Input.Shifts.Count == shiftCount, $"{algorithm} 限扭持续时应保持当前挡，实际={drive.Status.State}");
                Check(drive.Status.LearnedGears == learned.LearnedGears && drive.Status.LearnedOutputBins == learned.LearnedOutputBins &&
                    drive.Status.LearnedCutRpm == learned.LearnedCutRpm, "冲击和临时低输出不能污染挡比、限转或动力曲线");

                var restored = AtPower(capped with { Rpm = 2450, AccelerationZ = 3 }, 140);
                drive.Feed(restored, 80);
                Check(drive.Status.State == AutomaticShiftState.CollisionPowerHold, "几帧动力回弹不应立即恢复预测");
                drive.Feed(restored, 180);
                Check(drive.Status.Enabled && drive.Status.State != AutomaticShiftState.CollisionPowerHold,
                    $"{algorithm} 持续恢复动力后应继续计算换挡");
            }
        });

        run("墙碰减速 · 取消进行中的换挡，真实制动和普通断油保持独立", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 6 });
            var atRedline = FullLoad with { VelocityZ = 24, Rpm = 8025 };
            drive.Feed(atRedline, 300);
            Check(drive.Status.TargetGear == 3, "先建立正在等待确认的升挡请求");
            var releases = drive.Input.Releases;
            drive.Feed(atRedline with { SpeedKmh = 56, VelocityZ = 15, AccelerationX = -50, AccelerationZ = -110 }, 20);
            Check(drive.Status.State == AutomaticShiftState.WheelContactHold && drive.Status.TargetGear == 0 &&
                drive.Input.Releases > releases && drive.Status.LastShiftTo == 0, "碰撞应释放旧脉冲，且不将这次尝试学习为有效升挡");

            var detector = new CollisionSlowdownDetector();
            long now = 0;
            CollisionSlowdownState Feed(TelemetrySample frame, ShiftPedals pedals, bool shifting = false) =>
                detector.Observe(frame with { TimestampMs = (uint)(now += 20) }, pedals, now, shifting);
            var cruising = AtPower(atRedline with { Rpm = 5400 }, 150);
            Feed(cruising, new(1, 0));
            for (var i = 1; i <= 18; i++)
                Check(!Feed(cruising with { SpeedKmh = 85 - i * 1.05, VelocityZ = 24 - i * 0.29, Brake = 1, Throttle = 0,
                    AccelerationZ = -14 }, new(0, 1)).Holding, "持续制动本身不能触发撞墙保护");

            var rub = cruising with { SmashableMass = 1, SmashableVelDiff = 0.6 };
            Check(Feed(rub, new(1, 0)).Impact, "首次擦碰应短暂持挡");
            for (var i = 0; i < 24; i++) Feed(rub, new(1, 0));
            Check(!Feed(rub with { Rpm = 7990, TorqueNm = -150, PowerKw = -120 }, new(1, 0)).Holding,
                "残留的碰撞字段与普通限转不应重复开启保护");

            var hit = cruising with { SpeedKmh = 48, VelocityZ = 13, AccelerationZ = -95 };
            Check(Feed(hit, new(1, 0)).Impact, "应识别新碰撞");
            var decoupled = hit with { Rpm = 1500, AccelerationZ = 0, PowerKw = 0, TorqueNm = 0, Clutch = 1 };
            for (var i = 0; i < 30; i++)
                Check(!Feed(decoupled, new(1, 0), shifting: true).PowerLimited, "离合脱开期间的动力缺失不构成限扭证据");
            detector.Reset();
            Check(!Feed(decoupled with { Clutch = 0 }, new(1, 0)).Holding, "车辆或赛事切换应清除旧的碰撞证据");
        });

        run("驾驶辅助持挡期间自动换挡不发送请求，放行后恢复正常", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 4 });
            drive.Input.DriveAssistBlocksShift = true;
            var loaded = FullLoad with { Rpm = 8025 };
            drive.Feed(loaded, 640);
            Check(drive.Status.State == AutomaticShiftState.DriveAssistHold && drive.Input.Shifts.Count == 0,
                "稳定辅助主动持挡时无需等待尝试失败后才抑制请求");
            drive.Input.DriveAssistBlocksShift = false;
            drive.Feed(loaded, 320);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 3,
                "稳定辅助放行后应恢复普通换挡，不能沿用手动接管的冷却");
        });

        run("墙碰减速 · 真实统规赛特征的一挡怠速限制在倒车后仍能识别", () =>
        {
            var detector = new CollisionSlowdownDetector();
            long now = 0;
            CollisionSlowdownState Feed(TelemetrySample frame) =>
                detector.Observe(frame with { TimestampMs = (uint)(now += 20) }, new(1, 0), now, shifting: false);
            var car = FullLoad with { EngineIdleRpm = 1500, EngineMaxRpm = 8500, Rpm = 3950, SpeedKmh = 56, VelocityZ = 12.8 };
            Feed(car);
            Check(Feed(car with { SpeedKmh = 24.7, VelocityZ = 2.4, AccelerationX = 49, AccelerationZ = -135 }).Impact,
                "应识别车速突降和横纵向冲击");
            var reverse = AtPower(car with { Gear = 0, Rpm = 7827, SpeedKmh = 21, VelocityZ = -5 }, 395);
            for (var i = 0; i < 120; i++)
                Check(!Feed(reverse).PowerLimited, "不能将驾驶者主动倒车识别为限扭");

            var capped = car with { Gear = 1, Rpm = 1500, SpeedKmh = 1.8, VelocityZ = -0.5, PowerKw = 0, TorqueNm = 0 };
            var actual = default(CollisionSlowdownState);
            for (var i = 0; i < 16; i++) actual = Feed(capped);
            Check(actual.PowerLimited, "回一挡后全油门停在怠速仍无输出，应等待车辆动力恢复");
            for (var i = 0; i < 10; i++) actual = Feed(AtPower(capped with { Rpm = 4447 }, 148));
            Check(!actual.Holding, "有持续正向发动机输出后重新放行");
        });

        run("高速碰撞后：各策略在高挡近乎停住时逐挡回一挡", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, AutomaticClutch = false,
                    MaximumGear = 10, ProtectShiftReengagement = true, DriftMode = algorithm == ShiftAlgorithm.VirtualTcu });
                var fast = FullLoad with { Gear = 10, SpeedKmh = 235, Rpm = 7000, EngineMaxRpm = 9500 };
                drive.Feed(fast, 380);
                var blocked = fast with { SpeedKmh = 0.2, Rpm = 3600, SmashableMass = 1, SmashableVelDiff = 2,
                    RearLeft = fast.RearLeft with { SlipRatio = 1.2 } };
                drive.Feed(blocked with { SpeedKmh = 65, AccelerationY = -11 }, 140);
                drive.Feed(blocked with { AccelerationX = 16, AccelerationY = -11 }, 140);
                Check(drive.Input.Shifts.Count == 0, $"{algorithm} 应等车速和姿态稳定");

                drive.Feed(blocked, 200);
                Check(drive.Status.TargetGear == 9 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]),
                    $"{algorithm} 在满油、高转、无低挡比且仍有碰撞标记时应请求退到九挡，实际={drive.Status.State}");
                for (var gear = 9; gear >= 1; gear--)
                {
                    drive.Input.IsShiftPulseActive = true;
                    drive.Feed(blocked with { Gear = gear, Brake = 0.8 }, 120);
                    Check(drive.Status.LastShiftTo == gear && drive.Input.Shifts.Count == 10 - gear,
                        $"{algorithm} 应等待 {gear} 挡确认并让上一次脉冲释放");
                    drive.Input.IsShiftPulseActive = false;
                    drive.Feed(blocked with { Gear = gear, Brake = 0.8 }, 120);
                    Check(drive.Status.TargetGear == (gear == 1 ? 0 : gear - 1), $"{algorithm} 应逐挡恢复并在一挡停止");
                }
                Check(drive.Input.Shifts.Count == 9 && drive.Input.Shifts.All(shift => shift == ShiftDirection.Down),
                    $"{algorithm} 应只发送九次降挡");
            }
        });

        run("高速碰撞后：停住前的未确认换挡可以恢复，停住后的超时保留开关", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 10 });
            var fast = FullLoad with { Gear = 9, SpeedKmh = 220, Rpm = 8030 };
            drive.Feed(fast, 300);
            Check(drive.Status.TargetGear == 10 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "先模拟高速时未确认的升挡");
            var stopped = fast with { SpeedKmh = 0.2, Rpm = 2417 };
            drive.Feed(stopped, 1000);
            Check(drive.Status.Enabled && drive.Status.TargetGear == 8 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Down]),
                $"撞停前发出的旧请求到期后应能退挡，实际={drive.Status.State}");
            drive.Feed(stopped, 1100);
            Check(drive.Status.Enabled && drive.Status.State == AutomaticShiftState.ConfirmationTimedOut && drive.Input.Shifts.Count == 2,
                "停车后新发出的退挡若没有回执，应先释放输入并延迟重试");
        });

        run("自适应车速匹配：Skyline 急刹后松刹车，确认每次降挡直到一挡", () =>
        {
            using var drive = new Drive();
            var skyline = FullLoad with { CarOrdinal = 2512, CarPerformanceIndex = 600, EngineMaxRpm = 9000 };
            drive.Feed(skyline with { Gear = 9, SpeedKmh = 100, Rpm = 3107, Throttle = 0.3 }, 400);
            drive.Feed(skyline with { Gear = 10, SpeedKmh = 100, Rpm = 2687, Throttle = 0.3 }, 400);
            drive.Enable(new AutomaticShiftSettings
            {
                Algorithm = ShiftAlgorithm.PowerDrop, GamepadMode = GamepadShiftMode.Combination, AdaptiveGearMatching = true,
            });
            var braking = skyline with { Gear = 10, SpeedKmh = 4.865, Rpm = 845.4, Brake = 1, Throttle = 0 };
            drive.Feed(braking, 40);
            Check(drive.Status.TargetGear == 9, "急刹到低速应先请求降一挡");

            var stopped = braking with { SpeedKmh = 0.067, Rpm = 873.3, Brake = 0,
                RearLeft = braking.RearLeft with { SlipRatio = -1.0 } };
            for (var gear = 9; gear >= 1; gear--)
            {
                var sent = 10 - gear;
                drive.Input.IsShiftPulseActive = true;
                drive.Feed(stopped with { Gear = gear }, 160);
                Check(drive.Status.LastShiftTo == gear && drive.Input.Shifts.Count == sent,
                    "收到挡位回执后仍须等上一组手柄输入释放");
                drive.Input.IsShiftPulseActive = false;
                drive.Feed(stopped with { Gear = gear }, 120);
                Check(drive.Status.TargetGear == (gear == 1 ? 0 : gear - 1),
                    $"松开刹车后应从 {gear} 挡继续回一挡，并在一挡停止");
            }
            Check(drive.Input.Shifts.Count == 9 && drive.Input.Shifts.All(shift => shift == ShiftDirection.Down),
                "全程必须由十挡逐一降到一挡");
        });

        run("自适应车速匹配：停稳后的余转无须等到低转阈值，仍避让真实手动输入", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, AdaptiveGearMatching = true });
            var stopped = FullLoad with { Gear = 8, SpeedKmh = 0.196, Rpm = 3800, Brake = 1, Throttle = 0 };
            drive.Input.Foreground = false;
            drive.Feed(stopped, 220);
            Check(drive.Input.Shifts.Count == 0, "失焦时即使停稳也不能发换挡键");
            drive.Input.Foreground = true;
            drive.Feed(stopped with { Clutch = 1 }, 200);
            drive.Feed(stopped with { HandBrake = 1 }, 200);
            Check(drive.Input.Shifts.Count == 0, "手动踩离合或拉手刹时仍需让出控制");
            drive.Feed(stopped, 120);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 7,
                "停稳后接合转速安全，应根据路速开始逐挡降挡");
        });

        run("自适应车速匹配：Skyline 19.5 km/h、1123 RPM 在六挡轻踩油门也能退挡", () =>
        {
            using var drive = new Drive();
            var skyline = FullLoad with { CarOrdinal = 2512, CarPerformanceIndex = 600, EngineMaxRpm = 9000 };
            double[] ratios = [0, 201.24, 146.31, 108.94, 84.35, 66.86, 53.70];
            for (var gear = 1; gear <= 6; gear++)
                drive.Feed(skyline with { Gear = gear, Rpm = 4000, SpeedKmh = 4000 / ratios[gear], Throttle = 0.3 }, 400);
            drive.Enable(new AutomaticShiftSettings
            {
                Algorithm = ShiftAlgorithm.PowerDrop, GamepadMode = GamepadShiftMode.Combination, AdaptiveGearMatching = true,
            });
            var stalled = skyline with { Gear = 6, SpeedKmh = 19.5, Rpm = 1123, Throttle = 0.24,
                RearLeft = skyline.RearLeft with { SlipRatio = 0.65 } };
            drive.Feed(stalled, 240);
            Check(drive.Status.DownshiftCue is { Gear: 5, LandingRpm: >= 1250 and <= 1350 },
                "六挡发动机拖挡时应接合下一挡，积雪上的滑移读数不拦截低转回挡");
            for (var gear = 5; gear >= 2; gear--)
            {
                drive.Feed(stalled with { Gear = gear, Rpm = stalled.SpeedKmh * ratios[gear] }, 200);
                Check(drive.Status.LastShiftTo == gear && drive.Status.TargetGear == (gear == 2 ? 0 : gear - 1),
                    "逐挡恢复到可用转速后应持挡");
            }
            Check(drive.Input.Shifts.Count == 4 && drive.Input.Shifts.All(shift => shift == ShiftDirection.Down),
                "低速滚行时应按车速收敛到二挡");
        });

        run("XTSLimo：旧挡比偏高时，实车一挡断油仍须升挡", () =>
        {
            foreach (var protect in new[] { false, true })
            {
                using var drive = new Drive();
                var limo = FullLoad with { CarOrdinal = 2128, CarPerformanceIndex = 800, Gear = 1 };
                drive.Feed(AtPower(limo with { SpeedKmh = 70, Rpm = 6510 }, 1070), 420);
                drive.Enable(new AutomaticShiftSettings
                {
                    Algorithm = ShiftAlgorithm.PowerDrop, GamepadMode = GamepadShiftMode.Combination,
                    ProtectShiftReengagement = protect, ProtectLaunchWheelspin = protect,
                });
                // The recorded first-gear limiter is below the nominal 8000 RPM.
                (double Speed, double Rpm, double Power)[] frames =
                [
                    (96.549, 7569.7, 1270.474), (96.885, 7741.2, 1252.696),
                    (97.198, 7554.2, -370.652), (97.503, 7647.7, 1263.196),
                    (97.823, 7638.8, -462.21), (97.979, 7455.2, -276.626),
                    (98.125, 7626.7, 1264.71), (98.421, 7716.1, -547.039),
                    (98.605, 7505.8, -310.907), (98.732, 7689.1, 1259.102),
                    (98.985, 7614.4, -428.951),
                ];
                foreach (var (speed, rpm, power) in frames)
                    drive.Send(AtPower(limo with { SpeedKmh = speed, Rpm = rpm }, power), 16, drive.Timestamp + 16);
                Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 2,
                    $"转速匹配保护为 {protect} 时，真实断油也应恰好升一次挡");
            }
        });

        run("松油降挡：自适应关闭时也能在 100 毫秒内降到接合安全的下一挡", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                using var drive = new Drive();
                var cruise = FullLoad with { Gear = 9, Rpm = 3900, SpeedKmh = 105, Throttle = 0.30 };
                drive.Feed(cruise with { Gear = 8, Rpm = 5060 }, 420);
                drive.Feed(cruise, 420);
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm });
                drive.Feed(cruise, 120);
                Check(drive.Input.Shifts.Count == 0, $"{algorithm} 正常巡航应持挡");
                drive.Feed(cruise with { Throttle = 0 }, 100);
                Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) &&
                      drive.Status.DownshiftCue is { Gear: 8, LandingRpm: > 4900 and < 5200 },
                    $"{algorithm} 明确松油后应立即进入可用动力挡");
            }
        });

        run("松油降挡：转速保护限制升挡时，仍可按车速降挡且保留过转限制", () =>
        {
            foreach (var unsafeLanding in new[] { false, true })
            {
                using var drive = new Drive();
                var cruise = FullLoad with { Gear = 4, Rpm = 4240, SpeedKmh = 106, Throttle = 0.30 };
                drive.Feed(cruise with { Gear = 3, Rpm = unsafeLanding ? 6270 : 5400, SpeedKmh = unsafeLanding ? 70 : 106 }, 420);
                drive.Feed(cruise, 420);
                drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, ProtectShiftReengagement = true });
                drive.Feed(cruise, 120);
                drive.Feed(cruise with { Rpm = 6600, Throttle = 0 }, 100);
                Check(unsafeLanding ? drive.Input.Shifts.Count == 0 : drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]),
                    "应按车速和已学挡比判断收油降挡，预计过转时必须继续持挡");
            }
        });

        run("起步保护：各策略都拒绝打滑虚高，恢复抓地后正常升挡", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, ProtectLaunchWheelspin = true });
                var launch = AtPower(Cargo with { Gear = 1, SpeedKmh = 8, Rpm = 1024 }, 50);
                drive.Feed(launch, 220);
                var spinning = AtPower(launch with
                {
                    SpeedKmh = 25, Rpm = 10240,
                    FrontLeft = launch.FrontLeft with { RotationSpeedRadPerSec = 69, SlipRatio = 0.93 },
                    FrontRight = launch.FrontRight with { RotationSpeedRadPerSec = 69, SlipRatio = 0.93 },
                }, 470);
                drive.Feed(spinning, 560);
                Check(drive.Status.State == AutomaticShiftState.LaunchGripHold && drive.Input.Shifts.Count == 0,
                    $"{algorithm} 起步打滑时，即使可选抓地保护关闭也要持挡");
                var spin = spinning.FrontLeft with { SlipRatio = 0 };
                var rolling = launch.RearLeft with { RotationSpeedRadPerSec = 20 };
                drive.Feed(spinning with { FrontLeft = spin, FrontRight = spin, RearLeft = rolling, RearRight = rolling }, 340);
                Check(drive.Input.Shifts.Count == 0 && drive.Status.LearnedGears == 0, $"{algorithm} 驱动轮空转也不能写入挡比");
                drive.Feed(AtPower(launch with { SpeedKmh = 26, Rpm = 3328 }, 210), 420);
                drive.Feed(AtPower(launch with { SpeedKmh = 80, Rpm = 10240 }, 450), 620);
                Check(drive.Status.TargetGear == 2 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                    $"{algorithm} 抓地恢复、车速支持实测转速后应能升挡");
            }
        });

        run("独立漂移模式：可从任何策略切换并返回，升挡时机使用漂移转速", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop, MaximumGear = 6 });
            var frame = AtPower(FullLoad with { Gear = 3, Rpm = 7400, SpeedKmh = 120, Throttle = 0.72 }, 180);
            drive.Feed(frame, 320);
            Check(drive.Input.Shifts.Count == 0 && !drive.Status.DriftMode, "动力衰减策略没有动力衰减时应保持");
            drive.Controller.SetDriftMode(true);
            drive.Feed(frame, 40);
            Check(drive.Status.DriftMode && drive.Status.TargetGear == 4 && drive.Status.Reason == ShiftReason.VirtualTcuDrift,
                "启用独立漂移后应按漂移模式工作");
            drive.Controller.SetDriftMode(false);
            drive.Feed(frame with { Gear = 4, Rpm = 5900 }, 260);
            Check(!drive.Status.DriftMode && drive.Status.LastShiftTo == 4 && drive.Input.Shifts.Count == 1,
                "恢复原策略时应完成已提交的挡位确认");
        });

        run("S-Cargo：各策略阻止离合虚高连升，动力恢复后仍能正常升挡", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                using var drive = new Drive();
                var loaded = AtPower(Cargo with { Gear = 1, Rpm = 8800, SpeedKmh = 68.75 }, 495);
                drive.Feed(loaded, 420);
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, MaximumGear = 10, ProtectShiftReengagement = true });
                var limiter = AtPower(loaded with { Rpm = 10240, SpeedKmh = 80 }, 455);
                drive.Feed(limiter, 560);
                Check(drive.Status.TargetGear == 2, $"{algorithm} 应响应有实际车速支持的第一次高转");

                drive.Input.IsShiftPulseActive = true;
                var flare = AtPower(limiter with { Gear = 2, Rpm = 10223.6, SpeedKmh = 79.238, Clutch = 0 }, 460);
                drive.Feed(flare, 180);
                Check(drive.Status.LastShiftTo == 2 && drive.Status.State == AutomaticShiftState.ReleasingShiftInput,
                    $"{algorithm} 挡位可以先得到确认，但输出脉冲未结束");
                drive.Input.IsShiftPulseActive = false;
                drive.Feed(flare, 560);
                Check(drive.Input.Shifts.Count == 1 && drive.Status.LearnedGears == 1,
                    $"{algorithm} 不可因为车速几乎不变的虚高转速再升挡或学习错误挡比");

                var coupled = AtPower(flare with { Rpm = 7040, SpeedKmh = 80 }, 450);
                drive.Feed(coupled, 80);
                drive.Feed(flare, 160);
                Check(drive.Input.Shifts.Count == 1, $"{algorithm} 短暂接合后突然空转也须等待车速支持");
                drive.Feed(coupled, 240);
                Check(drive.Input.Shifts.Count == 1, $"{algorithm} 接合在动力区时应持挡");
                drive.Feed(AtPower(coupled with { Rpm = 10240, SpeedKmh = 10240 / 88.0 }, 435), 620);
                Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Up]) && drive.Status.TargetGear == 3,
                    $"{algorithm} 接合后再次真实达到换挡区应继续升挡");
            }
        });

        run("S-Cargo：九挡再加速和松油，缺相邻挡比也能安全逐挡退出", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                foreach (var pedal in new[] { 0.60, 0.0 })
                {
                    using var drive = new Drive();
                    var stalled = LearnCargoRatios(drive);
                    drive.Enable(new AutomaticShiftSettings
                    {
                        Algorithm = algorithm, MaximumGear = 10, AdaptiveGearMatching = true, ProtectShiftReengagement = true,
                    });
                    drive.Feed(stalled with { Throttle = pedal }, 280);
                    var reason = pedal > 0 ? ShiftReason.SpeedMatchedPower : ShiftReason.SpeedMatchedCoast;
                    Check(drive.Status.TargetGear == 8 && drive.Status.Reason == reason &&
                          drive.Status.SpeedMatch is { Gear: 6, SpeedBandStart: 115, Rpm: >= 5100 and <= 5350 },
                        $"{algorithm} 在 116 km/h 的九挡应朝已知动力挡逐级降挡");
                    drive.Feed(stalled with { Gear = 8, Rpm = 8910, Throttle = pedal }, 160);
                    Check(drive.Status.LastShiftTo == 8 && drive.Status.TargetGear == 0 && drive.Status.LearnedGears == 2,
                        $"{algorithm} 不可把降挡补油的空转误当成未知八挡的挡比");
                    drive.Feed(stalled with { Gear = 8, Rpm = stalled.SpeedKmh * 35.8, Throttle = pedal }, 240);
                    Check(drive.Status.LastShiftTo == 8 && drive.Status.TargetGear == 7, $"{algorithm} 应确认八挡后才降到七挡");
                    drive.Feed(stalled with { Gear = 7, Rpm = stalled.SpeedKmh * 39.9, Throttle = pedal }, 240);
                    drive.Feed(stalled with { Gear = 6, Rpm = stalled.SpeedKmh * 45, Throttle = pedal }, 280);
                    Check(drive.Status.LastShiftTo == 6 && drive.Input.Shifts.SequenceEqual(
                        [ShiftDirection.Down, ShiftDirection.Down, ShiftDirection.Down]), $"{algorithm} 应在合适的已知挡位结束");
                }
            }
        });

        run("车速匹配：低挡尚未完整学习时仍可边刹边降，过转时不试挡", () =>
        {
            using var drive = new Drive();
            var stalled = LearnCargoRatios(drive);
            drive.Enable(new AutomaticShiftSettings { AdaptiveGearMatching = true });
            var braking = stalled with { Rpm = 7680, SpeedKmh = 236, Brake = 0.64, Throttle = 0 };
            drive.Feed(braking, 300);
            Check(drive.Input.Shifts.Count == 0, "唯一已学低挡会过转时不可试挡");
            drive.Feed(braking with { SpeedKmh = 202, Rpm = 202 * stalled.Rpm / stalled.SpeedKmh }, 440);
            Check(drive.Status.TargetGear == 8 && drive.Status.Reason == ShiftReason.SpeedMatchedBraking,
                "踩着刹车且已学低挡能为中间挡提供安全上界时应换挡");
        });

        run("车速匹配：按同一速度的实测功率选挡，并只执行相邻一步", () =>
        {
            var engine = new AdaptiveShiftStrategy();
            var sample = Cargo with { SpeedKmh = 115.975, Rpm = 3779.5, Gear = 9 };
            var ratios = new double[11];
            ratios[9] = sample.Rpm / sample.SpeedKmh;
            ratios[6] = 45;
            ratios[4] = 60;
            ratios[3] = 71.9;
            foreach (var (gear, power) in new[] { (9, 232.4), (6, 338.0), (4, 480.0), (3, 552.0) })
                engine.Observe(AtPower(sample with { Gear = gear, Rpm = sample.SpeedKmh * ratios[gear] }, power),
                    0, learnCurve: true, measureOutput: true, sample.EngineMaxRpm);
            var decision = SpeedGearMatcher.Downshift(sample, ratios, engine, sample.SpeedKmh, 9950,
                braking: false, coasting: false, out var destination, out var rpm);
            Check(decision is { Target: 8, Reason: ShiftReason.SpeedMatchedPower } && destination == 3 && rpm is > 8300 and < 8400,
                "应选同车速下马力最高的安全挡位，并给游戏发送恰好一次降挡");
        });

        run("车速匹配：下次打开恢复该车调校的挡比和动力，换车或换 PI 时隔离", () =>
        {
            var directory = Directory.CreateTempSubdirectory("fh6-gear-profile-");
            try
            {
                var path = Path.Combine(directory.FullName, "speed-gear-profiles.json");
                using (var inactive = new Drive(path)) LearnCargoRatios(inactive);
                Check(!File.Exists(path), "自适应关闭时不应保存训练档案");
                using (var learning = new Drive(path))
                {
                    learning.Controller.SetAssists(launch: false, reengagement: false, adaptive: true, airborne: true, strict: false);
                    LearnCargoRatios(learning);
                }
                using (var restored = new Drive(path))
                {
                    var frame = Cargo with { Gear = 9, Throttle = 0, SpeedKmh = 115.975, Rpm = 3779.5 };
                    restored.Feed(frame, 20);
                    Check(restored.Status.LearnedGears == 0, "关闭时即使磁盘已有档案也不可使用");
                    restored.Controller.SetAssists(launch: false, reengagement: false, adaptive: true, airborne: true, strict: false);
                    Check(restored.Status.LearnedGears == 2 && restored.Status.LearnedOutputBins > 0, "重启应恢复齿比和实测动力曲线");
                    restored.Feed(frame with { CarOrdinal = 99 }, 20);
                    Check(restored.Status.LearnedGears == 0 && restored.Status.LearnedOutputBins == 0, "另一辆车不可继承该挡位匹配");
                    restored.Feed(frame with { CarPerformanceIndex = 801 }, 20);
                    Check(restored.Status.LearnedGears == 0, "不同 PI 应独立学习");
                    restored.Feed(frame, 20);
                    Check(restored.Status.LearnedGears == 2, "切回原车应马上恢复学习结果");
                    restored.Controller.SetAssists(launch: false, reengagement: false, adaptive: false, airborne: true, strict: false);
                    Check(restored.Status.LearnedGears == 0, "关闭时应即时停止使用已保存的挡比");
                    restored.Controller.SetAssists(launch: false, reengagement: false, adaptive: false, airborne: true, strict: true);
                    Check(restored.Status.StrictSpeedGearMatching && restored.Status.LearnedGears == 2, "严格模式可独立复用该车的挡比");
                }
                File.WriteAllText(path, "[null, {\"Ratios\":null}]");
                using var damaged = new Drive(path);
                damaged.Controller.SetAssists(launch: false, reengagement: false, adaptive: true, airborne: true, strict: false);
                damaged.Feed(Cargo, 20);
                Check(damaged.Status.LearnedGears == 0, "无效档案应从空表安全恢复");
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        });

        run("Virtual TCU：舒适、竞技、漂移与越野使用各自的升挡策略", () =>
        {
            (VirtualTcuMode Mode, double HoldRpm, double ShiftRpm, double Throttle, ShiftReason Reason)[] modes =
            [
                (VirtualTcuMode.Comfort, 4000, 5000, 0.55, ShiftReason.VirtualTcuComfort),
                (VirtualTcuMode.Race, 7150, 7540, 1, ShiftReason.VirtualTcuPowerBand),
                (VirtualTcuMode.Drift, 7220, 7370, 0.70, ShiftReason.VirtualTcuDrift),
                (VirtualTcuMode.OffRoad, 7000, 7230, 0.75, ShiftReason.VirtualTcuOffRoad),
            ];
            foreach (var (mode, hold, shift, throttle, reason) in modes)
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.VirtualTcu, VirtualTcuMode = mode,
                    MaximumGear = 10, GamepadMode = GamepadShiftMode.Combination });
                var frame = AtPower(FullLoad with { Gear = 3, Rpm = hold, SpeedKmh = hold / 58, Throttle = throttle }, 150);
                drive.Feed(frame, 280);
                Check(drive.Input.Shifts.Count == 0, $"{mode} 在各自目标转速之前应持挡");
                drive.Feed(AtPower(frame with { Rpm = shift, SpeedKmh = shift / 58 }, 175), 40);
                Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 4 &&
                      drive.Status.Reason == reason && drive.Input.LastMode == GamepadShiftMode.Combination,
                    $"{mode} 应使用自己的换挡理由与当前 Steam 输出");
            }
        });

        run("Virtual TCU 竞技：制动时选动力区间挡位，同时防止过转", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.VirtualTcu, VirtualTcuMode = VirtualTcuMode.Race,
                GamepadMode = GamepadShiftMode.Combination, MaximumGear = 10 });
            var frame = FullLoad with { Gear = 4, Rpm = 6302, SpeedKmh = 137, Brake = 0.66, Throttle = 0 };
            drive.Feed(frame, 320);
            Check(drive.Input.Shifts.Count == 0, "制动中当前速度的低挡转速过高时应持挡");
            drive.Feed(frame with { Rpm = 5704, SpeedKmh = 124 }, 80);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.DownshiftCue is { Gear: 3, LandingRpm: > 6500 and < 7840 }
                  && drive.Input.LastMode == GamepadShiftMode.Combination, "仍踩刹车时应降入合适的挡位并交给内置补油");
        });

        run("FH6 Auto Shift：首次实际断油即升挡，等到十挡回执才完成", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Fh6AutoShift,
                GamepadMode = GamepadShiftMode.Combination, MaximumGear = 10 });
            var frame = AtPower(FullLoad with { Gear = 9, Rpm = 7350, SpeedKmh = 270 }, 246);
            drive.Feed(frame, 240);
            drive.Feed(AtPower(frame with { Rpm = 7440, SpeedKmh = 273.3 }, 247), 40);
            Check(drive.Input.Shifts.Count == 0, "新车仍在输出动力时应等到实际断油");
            drive.Feed(AtPower(frame with { Rpm = 7360, SpeedKmh = 273.5 }, -24), 20);
            Check(drive.Status.TargetGear == 10 && drive.Status.Reason == ShiftReason.Fh6FuelCut &&
                  drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "第一帧实测断油应发送恰好一次升挡");
            drive.Controller.SetStrategy(ShiftAlgorithm.VirtualTcu, VirtualTcuMode.Race);
            drive.Feed(frame with { Gear = 11, Clutch = 1 }, 40);
            Check(drive.Status.TargetGear == 10 && drive.Input.Shifts.Count == 1, "换策略和过渡挡位不能取消已发出的换挡确认");
            drive.Feed(frame with { Gear = 10, Rpm = 6400 }, 40);
            drive.Feed(frame with { Gear = 10, Rpm = 7960 }, 400);
            Check(drive.Status.LastShiftTo == 10 && drive.Input.Shifts.Count == 1, "到达十挡后应确认且禁止再次升挡");
        });

        run("FH6 Auto Shift：按同车相邻挡的实测功率交点升挡", () =>
        {
            using var drive = new Drive();
            drive.Controller.SetStrategy(ShiftAlgorithm.Fh6AutoShift);
            for (var rpm = 4300; rpm <= 6400; rpm += 100)
                drive.Feed(AtPower(FullLoad with { Gear = 4, Rpm = rpm, SpeedKmh = rpm / 50.0 }, 200), 160);
            for (var rpm = 5400; rpm <= 7800; rpm += 100)
                drive.Feed(AtPower(FullLoad with { Gear = 3, Rpm = rpm, SpeedKmh = rpm / 65.0 }, 320 - (rpm - 5600) * 0.09), 160);
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Fh6AutoShift, MaximumGear = 10 });
            var frame = FullLoad with { Gear = 3, Rpm = 6500, SpeedKmh = 100 };
            drive.Feed(AtPower(frame, 239), 260);
            Check(drive.Input.Shifts.Count == 0, "相邻高挡尚无功率收益时应持挡");
            drive.Feed(AtPower(frame with { Rpm = 7010, SpeedKmh = 7010 / 65.0 }, 193), 40);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.Fh6PowerCrossover,
                $"有判定依据时应在相邻挡功率交点升挡，实际 {drive.Status.Reason}，已学习 {drive.Status.LearnedGears} 挡和 {drive.Status.LearnedOutputBins} 个动力区间");
        });

        run("FH6 Auto Shift：已经卡在限转时，反复断油仍能触发升挡", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Fh6AutoShift, MaximumGear = 10 });
            var frame = AtPower(FullLoad with { Gear = 4, Rpm = 7460, SpeedKmh = 152 }, 250);
            drive.Feed(frame, 220);
            var cut = AtPower(frame, -55);
            drive.Feed(cut, 20);
            Check(drive.Input.Shifts.Count == 0, "没有上升轨迹的一帧动力中断不足以判定断油");
            drive.Feed(frame, 40);
            drive.Feed(cut, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.Fh6FuelCut,
                "在相同转速交替恢复和断油时应及时升挡");
        });

        run("FH6 Auto Shift：确认断油升挡后可预测后续换挡，换车时重新学习", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Fh6AutoShift, MaximumGear = 10 });
            var frame = AtPower(FullLoad with { Gear = 3, Rpm = 7340, SpeedKmh = 122.33 }, 230);
            drive.Feed(frame, 240);
            drive.Feed(AtPower(frame with { Rpm = 7440, SpeedKmh = 124 }, 235), 40);
            drive.Feed(AtPower(frame with { Rpm = 7350, SpeedKmh = 124 }, -20), 20);
            drive.Feed(frame with { Gear = 4, Rpm = 5580, SpeedKmh = 124 }, 20);
            Check(drive.Status.LastShiftTo == 4 && drive.Status.LearnedCutRpm == 7450,
                "换挡回执应提交本车的断油转速");

            var higherGear = AtPower(frame with { Gear = 4, Rpm = 7150, SpeedKmh = 7150 / 45.0 }, 235);
            drive.Feed(higherGear, 260);
            Check(drive.Input.Shifts.Count == 1, "下一挡离已学断油点还远时应继续加速");
            drive.Feed(AtPower(higherGear with { Rpm = 7410, SpeedKmh = 7410 / 45.0 }, 233), 20);
            Check(drive.Input.Shifts.Count == 2 && drive.Status.Reason == ShiftReason.Fh6PredictedLimiter,
                "下一挡应能利用已学断油点提前计算触发时刻");

            drive.Feed(higherGear with { CarOrdinal = 2750 }, 240);
            Check(drive.Status.LearnedCutRpm == 0 && drive.Status.TargetGear == 0,
                "另一辆车不可沿用上一辆车的断油点或未确认换挡");
        });

        run("FH6 Auto Shift：用接合时的车速在刹车中降挡", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            drive.Enable(new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.Fh6AutoShift, MaximumGear = 10 });
            var frame = FullLoad with { Gear = 4, Rpm = 5750, SpeedKmh = 125, Brake = 0.52, Throttle = 0 };
            drive.Feed(frame, 280);
            Check(drive.Input.Shifts.Count == 0, "未学习断油点时，低挡超过暂用限转应持挡");
            var speed = 125.0;
            while (speed > 116.7 && drive.Input.Shifts.Count == 0)
            {
                speed -= 0.6;
                drive.Feed(frame with { Rpm = speed * 46, SpeedKmh = speed }, 20);
            }
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && speed * 60 > 7000 &&
                  drive.Status.DownshiftCue is { Gear: 3, LandingRpm: > 6550 and <= 7000 },
                "应该根据测得的减速率在松刹车前降挡，并保留 200 RPM 接合余量");
        });

        run("滞空保护：所有策略在飞坡和落地缓冲时都不再升挡", () =>
        {
            foreach (var algorithm in Enum.GetValues<ShiftAlgorithm>())
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = algorithm, MaximumGear = 6 });
                var road = FullLoad with { Gear = 2, SpeedKmh = 95, Rpm = 4100, Throttle = 0.2,
                    FrontLeft = FullLoad.FrontLeft with { NormalizedSuspensionTravel = 0.35 } };
                drive.Feed(road, 300);
                Check(drive.Input.Shifts.Count == 0, "先模拟接地巡航");
                var jump = AtPower(road with { Rpm = 8030, Throttle = 1, FrontLeft = default }, 360);
                drive.Feed(jump, 1200);
                Check(drive.Status.State == AutomaticShiftState.WheelContactHold && drive.Input.Shifts.Count == 0,
                    $"{algorithm} 应用悬挂伸展检测飞坡，满油限转也不发送升挡");
                drive.Feed(jump with { FrontLeft = road.FrontLeft }, 320);
                Check(drive.Input.Shifts.Count == 0, $"{algorithm} 应等待落地稳定");
                drive.Feed(road, 450);
                Check(drive.Status.Enabled && drive.Status.State != AutomaticShiftState.WheelContactHold, $"{algorithm} 应在落地后恢复");
                drive.Controller.SetAssists(launch: false, reengagement: false, adaptive: false, airborne: false, strict: false);
                drive.Feed(jump, 400);
                Check(drive.Status.Enabled && drive.Status.State != AutomaticShiftState.WheelContactHold,
                    $"{algorithm} 关闭滞空保护后应由策略自行判断");
            }
        });

        run("滞空保护：释放飞坡前的待确认输入，不因长时间处于过渡挡停用", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 6 });
            var road = FullLoad with { Rpm = 8030, FrontLeft = FullLoad.FrontLeft with { NormalizedSuspensionTravel = 0.35 } };
            drive.Feed(road, 300);
            Check(drive.Status.TargetGear == 3 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "先模拟飞坡前的换挡请求");
            var releases = drive.Input.Releases;
            drive.Feed(road with { Gear = 11, FrontLeft = default }, 1200);
            Check(drive.Status.Enabled && drive.Status.TargetGear == 0 && drive.Input.Releases > releases && drive.Input.Shifts.Count == 1,
                "飞坡时应释放旧指令，滞空超过确认超时也不能停用自动换挡");
        });

        run("新换挡策略：倒挡、倒溜、腾空和遥测断流时不发送按键", () =>
        {
            foreach (var strategy in new[] { ShiftAlgorithm.VirtualTcu, ShiftAlgorithm.Fh6AutoShift })
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { Algorithm = strategy, MaximumGear = 10 });
                var frame = AtPower(FullLoad with { Gear = 1, Rpm = 7990, SpeedKmh = 45 }, 223);
                drive.Feed(frame with { Gear = 0 }, 400);
                Check(drive.Status.State == AutomaticShiftState.ReverseHold, $"{strategy} 应识别倒挡");
                drive.Feed(frame with { VelocityZ = -2 }, 400);
                Check(drive.Status.State == AutomaticShiftState.ReverseHold, $"{strategy} 应在前进挡倒溜时持挡");
                drive.Feed(frame with { AccelerationY = -12 }, 800);
                Check(drive.Status.State == AutomaticShiftState.WheelContactHold && drive.Input.Shifts.Count == 0,
                    $"{strategy} 不可在腾空转速突升时换挡");
                var releases = drive.Input.Releases;
                drive.Elapse(400);
                Check(drive.Status.State == AutomaticShiftState.WaitingForTelemetry && drive.Input.Releases > releases,
                    $"{strategy} 应在数据超时时释放输出");
            }
        });

        run("自动换挡：原厂四挡的 Mustang 可从四挡自动确认至十挡", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination });
            for (var gear = 4; gear <= 10; gear++)
            {
                var frame = AtPower(FullLoad with { CarOrdinal = 2614, CarPerformanceIndex = 600,
                    Gear = gear, Rpm = 7980, SpeedKmh = 140 + (gear - 4) * 20 }, 215);
                drive.Feed(frame, 280);
                Check(drive.Status.Enabled, "验证改装挡位时应继续启用自动换挡");
                if (gear < 10)
                    Check(drive.Status.TargetGear == gear + 1, $"应逐挡请求 {gear} → {gear + 1}");
            }
            Check(drive.Status.MaximumGear == 10 && drive.Status.GearLimit == GearLimitSource.DataOut &&
                  drive.Input.LastMode == GamepadShiftMode.Combination && drive.Status.LastShiftTo == 10 &&
                  drive.Input.Shifts.Count == 6 && drive.Status.TargetGear == 0, "应确认十挡且停止继续升挡");
        });

        run("自动换挡：探测原厂挡位以上未成功时保留自动降挡", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            var peak = AtPower(FullLoad with { CarOrdinal = 2614, Gear = 4, Rpm = 7980, SpeedKmh = 140 }, 215);
            drive.Feed(peak, 3400);
            Check(drive.Input.Shifts.Count == 2 && drive.Status.Enabled && drive.Status.TargetGear == 0 &&
                  drive.Status.GearLimit == GearLimitSource.Probe && drive.Status.MaximumGear == 4,
                "上探实际不存在的挡位应有限重试后持挡，且保持自动换挡");
            drive.Feed(peak with { SpeedKmh = 2, Rpm = 900, Brake = 0.4, Throttle = 0 }, 780);
            Check(drive.Input.Shifts.Last() == ShiftDirection.Down && drive.Status.TargetGear == 3,
                "最高挡上探失败后仍应允许低速回挡");
        });

        run("自动换挡：手动挂入更高挡解除临时探测上限", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            var peak = AtPower(FullLoad with { CarOrdinal = 2614, Gear = 4, Rpm = 7980, SpeedKmh = 140 }, 215);
            drive.Feed(peak, 3400);
            Check(drive.Status.GearLimit == GearLimitSource.Probe, "两次未确认后应产生临时上限");
            drive.Feed(peak with { Gear = 5, SpeedKmh = 162 }, 1440);
            Check(drive.Status.GearLimit == GearLimitSource.Unconfirmed && drive.Status.MaximumGear == 5 &&
                  drive.Status.TargetGear == 6, "实测五挡应撤销旧上限并允许继续升挡");
        });

        run("自动换挡：手填最高前进挡是硬上限", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 4 });
            drive.Feed(AtPower(FullLoad with { CarOrdinal = 2614, Gear = 4, Rpm = 7980, SpeedKmh = 140 }, 220), 1000);
            Check(drive.Status.MaximumGear == 4 && drive.Input.Shifts.Count == 0, "手填上限不能被自动上探绕过");
        });

        run("自动换挡：复现 LazyForza 推荐值、降挡滞回和相邻挡交点", () =>
        {
            var reference = new LazyForzaShiftTarget(3, 4, 7800, 7400, 5600);
            Check(LazyForzaShiftAdvisor.Recommend([reference], 3, 7500, 8000).Gear == 4 &&
                  LazyForzaShiftAdvisor.Recommend([reference], 4, 4800, 8000).Gear == 3 &&
                  LazyForzaShiftAdvisor.Recommend([reference], 4, 5200, 8000).Gear == 4 &&
                  LazyForzaShiftAdvisor.Recommend([reference], 4, 5040, 8000).Gear == 4,
                "推荐结果须与上游的升挡、降挡、持挡及阈值边界一致");

            var curve = Enumerable.Range(10, 31).Select(bin =>
                new ShiftCurveBin(bin * 200, 900 - 20 * bin)).ToArray();
            var pair = LazyForzaShiftAdvisor.Calculate(curve, [new(9, 240), new(10, 180)], 8000).Single();
            Check(pair.ToGear == 10 && Math.Abs(pair.TargetRpm - 5142.857) < 0.1 &&
                  Math.Abs(pair.TargetRpm - pair.CueRpm - 384) < 0.001 && Math.Abs(pair.AfterShiftRpm - 3857.143) < 0.1,
                "应求出九至十挡的轮上扭矩交点、384 RPM 提前量和接合转速");
            var flatCurve = curve.Select(bin => bin with { MedianTorqueNm = 600 }).ToArray();
            var noCrossing = LazyForzaShiftAdvisor.Calculate(flatCurve, [new(9, 240), new(10, 180)], 8000).Single();
            Check(noCrossing.TargetRpm == 7900, "没有交点时应采用上游限转回退");
            Check(LazyForzaShiftAdvisor.Calculate(curve, [new(8, 240), new(10, 180)], 8000).Count == 0,
                "缺少九挡时不可跳到十挡");
        });

        run("自动换挡：Steam 升挡使用 LazyForza 轮上扭矩交点，降挡保留滞回", () =>
        {
            using var drive = new Drive();
            for (var gear = 4; gear >= 3; gear--)
                for (var rpm = 2400; rpm <= 7800; rpm += 200)
                    drive.Feed(CurveFrame(gear, rpm), 120);
            Check(drive.Status.AdvisedPairs == 1, "应从连续遥测学习三至四挡换挡点");
            drive.Elapse(300);
            drive.Feed(CurveFrame(3, 5000) with { Throttle = 0.84 }, 120);
            Check(drive.Status.SuggestedGear == 4 && drive.Input.Shifts.Count == 0, "手动驾驶应收到 LazyForza 升挡提示");
            drive.Feed(CurveFrame(4, 3300) with { Throttle = 0 }, 120);
            Check(drive.Status.SuggestedGear == 3 && drive.Input.Shifts.Count == 0, "手动驾驶应收到 LazyForza 降挡提示");
            drive.Elapse(300);
            Check(drive.Status.SuggestedGear == 0, "遥测过期时应隐藏换挡提示");
            drive.Enable(new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination });
            drive.Feed(CurveFrame(3, 5142) with { Throttle = 0.84 }, 260);
            Check(drive.Input.Shifts.Count == 0, "Steam 输出应等到扭矩交点，不能把供人反应的提前提示用作按键时机");
            drive.Feed(CurveFrame(3, 5144) with { Throttle = 0.84 }, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 4 && drive.Status.SuggestedGear == 4 &&
                  drive.Status.Reason == ShiftReason.LazyForzaUpshift, "首帧达到扭矩交点就应发送升挡");
            drive.Feed(CurveFrame(4, 3570) with { Throttle = 0.25 }, 400);
            drive.Feed(CurveFrame(4, 3472) with { Throttle = 0.25 }, 280);
            Check(drive.Input.Shifts.Count == 1 && drive.Status.LastShiftTo == 4, "接合后的滞回范围应持挡");
            drive.Feed(CurveFrame(4, 3470) with { Throttle = 0.25 }, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Down]) &&
                  drive.Status.TargetGear == 3 && drive.Status.Reason == ShiftReason.LazyForzaDownshift &&
                  drive.Input.LastMode == GamepadShiftMode.Combination, "到达降挡阈值就应发送 手柄降挡脉冲");
        });

        run("自动换挡：默认 LazyForza 全油门接近红线才升挡，实测动力衰减可运行中切换", () =>
        {
            using var drive = new Drive();
            for (var gear = 4; gear >= 3; gear--)
                for (var rpm = 2400; rpm <= 7800; rpm += 200)
                    drive.Feed(CurveFrame(gear, rpm), 120);
            drive.Enable(new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination });
            drive.Feed(AtPower(CurveFrame(3, 7040), 272), 260);
            Check(drive.Input.Shifts.Count == 0, "LazyForza 交点即使偏早，全油门且动力仍在时不应提前升挡");
            drive.Controller.SetStrategy(ShiftAlgorithm.PowerDrop);
            var atRed = AtPower(CurveFrame(3, 7980), 280);
            drive.Feed(atRed, 40);
            Check(drive.Input.Shifts.Count == 0, "实测动力衰减策略在红区有动力时应继续持挡");
            drive.Feed(AtPower(atRed, 105), 60);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.HighRpmOutputLoss,
                "功率和扭矩一起持续下降后应立即升挡");
        });

        run("自动换挡：动力采样拒绝打滑和碰撞，并识别同 PI 换调校", () =>
        {
            var learner = new LazyForzaShiftLearning();
            uint timestamp = 0;
            bool Feed(TelemetrySample sample, int count = 12)
            {
                var retuned = false;
                for (var index = 0; index < count; index++)
                {
                    timestamp += 20;
                    retuned |= learner.Observe(sample with { TimestampMs = timestamp }, 8000, timestamp);
                }
                return retuned;
            }

            var first = CurveFrame(3, 3600);
            Feed(first with { RearLeft = first.RearLeft with { CombinedSlip = 0.19 } });
            Feed(first with { FrontRight = first.FrontRight with { SlipRatio = -0.19 } });
            Feed(first with { SmashableMass = 1 });
            Feed(first with { SmashableVelDiff = 0.51 });
            Feed(first with { Throttle = 0.89 });
            Feed(first with { Clutch = 0.09 });
            Check(learner.ReadyBins == 0 && learner.LearnedGears == 0, "不合格的遥测不能参与动力或挡位比学习");
            Feed(first);
            var second = CurveFrame(4, 4400);
            Feed(second);
            Check(learner.ReadyBins == 2 && learner.LearnedGears == 2 && learner.Targets.Count == 1,
                "两个实测相邻挡和有效动力区间应生成换挡推荐");
            Check(!Feed(first with { SpeedKmh = first.SpeedKmh * 0.8 }) &&
                  Feed(second with { SpeedKmh = second.SpeedKmh * 0.8 }), "两个挡位比连续改变应识别相同 PI 的调校变更");
            Check(learner.Targets.Count == 0, "应移除上一套调校的建议");
        });

        run("自动换挡：Mustang 实际限转低于 Data Out 最大转速时仍能升挡", () =>
        {
            using var drive = new Drive();
            var mustang = FullLoad with { CarOrdinal = 2614, CarPerformanceIndex = 600, Gear = 1, SpeedKmh = 63,
                Rpm = 6860, PowerKw = 590, TorqueNm = 820,
                RearLeft = new WheelSample(SlipRatio: 0.65, 0, 0, 0, false, 0, 0, 0, 0, 0) };
            drive.Enable();
            drive.Feed(mustang, 260);
            // Two cut-and-recovery cycles from the recorded PI 600 Mustang under steady throttle.
            drive.Feed(mustang with { Rpm = 6927, PowerKw = -460, TorqueNm = -632 }, 20);
            drive.Feed(mustang with { Rpm = 6749, PowerKw = -400, TorqueNm = -565 }, 20);
            Check(drive.Input.Shifts.Count == 0, "一轮波形不足以判断限转");
            drive.Feed(mustang with { Rpm = 6722, PowerKw = 529, TorqueNm = 756 }, 20);
            drive.Feed(mustang with { Rpm = 6955, PowerKw = 580, TorqueNm = 798 }, 20);
            drive.Feed(mustang with { Rpm = 7073, PowerKw = -498, TorqueNm = -669 }, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 2 &&
                  drive.Status.Reason == ShiftReason.HighRpmOutputLoss, "应根据实际动力断续完成 1 → 2 请求");
        });

        run("自动换挡：转向、离合和驾驶员收油会清空实测限转判定", () =>
        {
            using var drive = new Drive();
            var loaded = FullLoad with { CarOrdinal = 2614, CarPerformanceIndex = 600, Gear = 1, SpeedKmh = 63,
                Rpm = 6900, PowerKw = 610, TorqueNm = 840,
                RearLeft = new WheelSample(SlipRatio: 0.65, 0, 0, 0, false, 0, 0, 0, 0, 0) };
            drive.Enable(new AutomaticShiftSettings { UpshiftGripProtection = true });
            drive.Feed(loaded, 260);
            var cut = loaded with { PowerKw = -460, TorqueNm = -632 };
            foreach (var blocked in new[] { cut with { Steer = 1 }, cut with { Clutch = 0.3 }, cut with { Throttle = 0.4 } })
            {
                for (var i = 0; i < 5; i++)
                {
                    drive.Feed(loaded, 40);
                    drive.Feed(blocked, 40);
                }
                Check(drive.Input.Shifts.Count == 0, "显著转向、离合或收油时不应误判动力断续");
            }
            drive.Feed(loaded, 40);
            drive.Feed(cut, 20);
            Check(drive.Input.Shifts.Count == 0, "退出保护后的第一轮衰减也必须重新确认");
        });

        run("自动换挡：缺少 LazyForza 换挡点时，备用升挡判断仍比较同车速动力", () =>
        {
            using var drive = new Drive();
            drive.Feed(AtPower(FallbackLoad with { Gear = 3, Rpm = 4500, SpeedKmh = 75 }, 235), 350);
            drive.Feed(AtPower(FallbackLoad with { Gear = 3, Rpm = 4800, SpeedKmh = 80 }, 195), 350);
            var weaker = AtPower(FallbackLoad with { Rpm = 7200, SpeedKmh = 75 }, 190);
            drive.Feed(weaker, 350);
            var strong = AtPower(FallbackLoad with { Rpm = 7680, SpeedKmh = 80 }, 240);
            drive.Feed(strong, 350);
            drive.Enable();
            drive.Feed(strong, 380);
            Check(drive.Input.Shifts.Count == 0, "已知下一挡更弱时，即使超过旧的转速百分比也应持挡");
            drive.Feed(weaker with { Throttle = 0.86 }, 240);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.HigherGearPull,
                "中等油门下，可以用同车速牵引力选择高一挡");
        });

        run("自动换挡：识别断续断油，忽略单帧缺口、收油及打滑", () =>
        {
            using var drive = new Drive();
            var steady = AtPower(FullLoad with { Rpm = 7400 }, 220);
            var cut = AtPower(steady, 0);
            drive.Enable();
            drive.Feed(steady, 260);
            drive.Feed(cut, 20);
            drive.Feed(steady, 100);
            drive.Feed(cut with { Throttle = 0.4 }, 240);
            drive.Feed(steady, 180);
            drive.Feed(cut with { RearLeft = new WheelSample(SlipRatio: 0.65, 0, 0, 0, false, 0, 0, 0, 0, 0) }, 240);
            Check(drive.Input.Shifts.Count == 0, "瞬时缺口、收油和车轮打滑不等于限转断油");
            drive.Feed(steady, 120);
            for (var i = 0; i < 14; i++)
            {
                drive.Feed(cut, 20);
                drive.Feed(steady, 20);
            }
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "旧转速阈值以下的反复断油也应触发升挡");
        });

        run("自动换挡：缺少 LazyForza 换挡点时，大油门可择机降挡，弯中持挡", () =>
        {
            using var drive = new Drive();
            var lower = AtPower(FallbackLoad with { Gear = 2, Rpm = 6500, SpeedKmh = 90 }, 290);
            var upper = AtPower(FallbackLoad with { Gear = 3, Rpm = 4800, SpeedKmh = 90 }, 200);
            drive.Feed(lower, 350);
            drive.Feed(upper, 350);
            drive.Enable();
            drive.Feed(upper with { AccelerationX = 5.5 }, 300);
            Check(drive.Input.Shifts.Count == 0, "较大横向加速度下应推迟动力降挡");
            drive.Feed(upper, 300);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 2 &&
                  drive.Status.Reason == ShiftReason.Kickdown,
                "转速高于旧的降挡百分比时，有安全的净牵引力收益也应降挡");
        });

        run("自动换挡：手动进入倒挡取消待确认，倒车和空挡保持人工控制", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            drive.Feed(FullLoad, 220);
            var releases = drive.Input.Releases;
            drive.Feed(FullLoad with { Gear = 0 }, 1400);
            Check(drive.Status.Gear == 0 && drive.Status.Enabled && drive.Status.TargetGear == 0 &&
                  drive.Input.Releases > releases && drive.Input.Shifts.Count == 1, "R 应取消待确认并暂停自动换挡");
            drive.Feed(FullLoad with { Gear = 255 }, 200);
            drive.Feed(FullLoad, 200);
            Check(drive.Input.Shifts.Count == 1, "进入前进挡后必须等到方向与挡位稳定");
            drive.Feed(FullLoad, 260);
            Check(drive.Input.Shifts.Count == 2, "恢复稳定前进后可继续自动换挡");
        });

        run("自动换挡：默认关闭，启用后按负荷升挡并等待遥测确认", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad, 200);
            Check(drive.Input.Shifts.Count == 0, "默认不能发送按键");
            drive.Enable();
            var belowPeak = FullLoad with { Rpm = 5500, SpeedKmh = 85 * 5500.0 / 7800 };
            drive.Feed(belowPeak, 200);
            Check(drive.Input.Shifts.Count == 0, "全油门应比轻油门晚升挡");
            drive.Feed(belowPeak with { Throttle = 0.2 }, 240);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "轻油门达到升挡转速，应请求一次手柄升挡");
            Check(drive.Status.TargetGear == 3, "应等待 3 挡确认");
            drive.Feed(FullLoad, 320);
            Check(drive.Input.Shifts.Count == 1, "未确认 3 挡时不可重复升挡");
            var nextGear = FullLoad with { Gear = 3, SpeedKmh = 106 };
            drive.Feed(nextGear, 20);
            Check(drive.Status.LastShiftFrom == 2 && drive.Status.LastShiftTo == 3, "应报告遥测确认的 2 → 3");
            drive.Feed(nextGear, 140);
            Check(drive.Input.Shifts.Count == 1, "下一次普通升挡应先等待挡位稳定及去抖");
            drive.Feed(nextGear, 20);
            Check(drive.Input.Shifts.Count == 2 && drive.Status.TargetGear == 4,
                "稳定 100 毫秒且去抖 60 毫秒后，应允许同方向连续升挡");
        });

        run("自动换挡：升挡确认后 350 毫秒内不允许反向降挡", () =>
        {
            using var drive = new Drive();
            var inThird = FullLoad with { Gear = 3, Rpm = 3900, SpeedKmh = 65 };
            drive.Feed(inThird, 380);
            drive.Feed(FullLoad, 380);
            drive.Enable();
            drive.Feed(FullLoad, 220);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "应先升到三挡");
            drive.Feed(inThird, 20);
            drive.Feed(inThird, 340);
            Check(drive.Input.Shifts.Count == 1, "反向冷却期间继续持挡");
            drive.Feed(inThird, 60);
            Check(drive.Input.Shifts.Count == 1, "冷却期间的数据不应提前完成反向决策去抖");
            drive.Feed(inThird, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Down]), "冷却结束且降挡条件仍持续时可降挡");
        });

        run("自动换挡：已经触及限转时跳过通用决策去抖", () =>
        {
            using var drive = new Drive();
            var stable = AtPower(FullLoad with { Rpm = 6400, SpeedKmh = 80 }, 220);
            drive.Enable();
            drive.Feed(stable, 320);
            Check(drive.Input.Shifts.Count == 0, "限转前应持挡");
            drive.Feed(AtPower(stable with { Rpm = 8060, SpeedKmh = 100.75 }, 220), 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.Limiter,
                "已经触及限转时应在当前采样帧请求升挡");
        });

        run("自动换挡：等待前次键盘脉冲结束时保持连续升挡", () =>
        {
            var clock = new GamepadShiftInputTests.ManualClock();
            using var input = new KeyboardShiftInput(() => true, () => false, _ => false,
                (keys, _) => keys.Length, physicalStateIsReliable: true, clock);
            using var controller = new AutomaticShiftController(input);
            var settings = new AutomaticShiftSettings { PressDurationMs = 250 };
            Check(input.ConnectAsync(settings, CancellationToken.None).GetAwaiter().GetResult() is null, "应连接键盘输出");
            Check(controller.TryEnable(settings, out var error), error);
            var limiter = AtPower(FullLoad with { Rpm = 7980 }, 220);

            Feed(limiter, 120);
            Check(controller.GetSnapshot(clock.GetTimestamp()).TargetGear == 3, "应先请求升到三挡");
            Feed(limiter, 20);
            Feed(limiter with { Gear = 3, Rpm = 5898 }, 120);
            var waiting = controller.GetSnapshot(clock.GetTimestamp());
            Check(waiting.State == AutomaticShiftState.ReleasingShiftInput && waiting.LastShiftTo == 3 && waiting.TargetGear == 0,
                "仍在释放前一次键盘组合时，不能误判为手动接管");
            Feed(limiter with { Gear = 3, SpeedKmh = 115 }, 220);
            Check(controller.GetSnapshot(clock.GetTimestamp()).TargetGear == 4,
                "前次脉冲结束后应继续响应限转升挡");

            void Feed(TelemetrySample frame, int milliseconds)
            {
                for (var elapsed = 0; elapsed < milliseconds; elapsed += 20)
                {
                    clock.Advance(20);
                    var now = clock.GetTimestamp();
                    controller.Feed(frame with { TimestampMs = (uint)now }, now);
                }
            }
        });

        run("自动换挡：全油门转速快速上升时避免套用固定提前量", () =>
        {
            using var drive = new Drive();
            for (var rpm = 3600; rpm <= 4800; rpm += 600)
                drive.Feed(AtPower(FallbackLoad with { Gear = 3, Rpm = rpm, SpeedKmh = rpm / 60.0 }, 155), 320);
            var accelerating = AtPower(FallbackLoad with { Rpm = 6000, SpeedKmh = 60 }, 220);
            drive.Feed(accelerating, 350);
            drive.Enable();
            drive.Feed(accelerating, 180);
            var requestedAt = drive.Ramp(accelerating, 6000, 8000, 500);
            Check(requestedAt >= 0.995 * FullLoad.EngineMaxRpm && drive.Status.Reason == ShiftReason.Limiter &&
                  drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "转速每秒上升 4000 时也应等到限转附近请求升挡");
        });

        run("自动换挡：转速恒定于 96% 时不因预测升挡", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            drive.Feed(AtPower(FullLoad with { Rpm = 7680 }, 240), 1100);
            Check(drive.Input.Shifts.Count == 0, "稳定的高转速且动力正常时不应凭预测换挡");
        });

        run("自动换挡：实测延迟随挡位确认更新且跨会话保留", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            Check(drive.Status.ShiftLatencyMs == 180, "实测前应使用 180 毫秒初值");
            for (var frame = 0; frame < 20 && drive.Input.Shifts.Count == 0; frame++)
                drive.Feed(FullLoad, 20);
            Check(drive.Status.TargetGear == 3, "必须先请求升挡");
            drive.Feed(FullLoad, 260);
            drive.Feed(FullLoad with { Gear = 3 }, 20);
            Check(Math.Abs(drive.Status.ShiftLatencyMs - 210) < 0.001, "280 毫秒确认耗时应使估计更新到 210 毫秒");
            drive.Controller.ResetSession();
            drive.Enable();
            drive.Feed(FullLoad with { CarOrdinal = 3852 }, 20);
            Check(Math.Abs(drive.Status.ShiftLatencyMs - 210) < 0.001, "重新启用或换车应保留输入延迟估计");
        });

        run("自动换挡：手动延迟优先，清空后恢复独立学习的延迟", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { ShiftLatencyMs = 320 });
            Check(drive.Status.ShiftLatencyMs == 320, "启用时应采用填写的延迟");
            for (var frame = 0; frame < 20 && drive.Input.Shifts.Count == 0; frame++)
                drive.Feed(FullLoad, 20);
            Check(drive.Status.TargetGear == 3, "手动延迟下应正常请求升挡");
            drive.Feed(FullLoad, 260);
            drive.Feed(FullLoad with { Gear = 3 }, 20);
            Check(drive.Status.LastShiftTo == 3 && drive.Status.ShiftLatencyMs == 320, "实测回执不应覆盖手动设置");
            drive.Enable();
            Check(Math.Abs(drive.Status.ShiftLatencyMs - 210) < 0.001, "清空后应恢复从 280 毫秒回执学到的估计");
            drive.Enable(new AutomaticShiftSettings { ShiftLatencyMs = 0 });
            Check(drive.Status.ShiftLatencyMs == 0, "0 毫秒是手动值");
            Check(!drive.Controller.TryEnable(new AutomaticShiftSettings { ShiftLatencyMs = -1 }, out _) &&
                  !drive.Controller.TryEnable(new AutomaticShiftSettings { ShiftLatencyMs = 901 }, out _), "超出范围的延迟应被拒绝");
        });

        run("自动换挡：未学到高一挡时按相邻实测挡比估算", () =>
        {
            using var drive = new Drive();
            drive.Feed(AtPower(FullLoad with { Rpm = 5140, SpeedKmh = 5140.0 / 112 }, 245), 360);
            var inThird = AtPower(FullLoad with { Gear = 3, Rpm = 7200, SpeedKmh = 90 }, 190);
            drive.Feed(inThird, 360);
            drive.Enable();
            drive.Feed(inThird with { Throttle = 0.86 }, 300);
            Check(drive.Status.LearnedGears == 2, "四挡比须保持未知");
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 4 &&
                  drive.Status.Reason == ShiftReason.HigherGearPull, "估算四挡接合转速的动力更强时，可在限转前升挡");
        });

        run("自动换挡：动力学习拒绝单帧尖峰并接受连续样本", () =>
        {
            using var drive = new Drive();
            var next = AtPower(FallbackLoad with { Gear = 3, Rpm = 4500, SpeedKmh = 75 }, 190);
            drive.Feed(next, 400);
            drive.Feed(AtPower(next, 247), 20);
            drive.Feed(next, 200);

            var current = AtPower(FallbackLoad with { Rpm = 7200, SpeedKmh = 75 }, 205);
            drive.Feed(current, 350);
            drive.Enable();
            drive.Feed(current with { Throttle = 0.86 }, 220);
            Check(drive.Input.Shifts.Count == 0, "一次尖峰不能把原本牵引力较小的高挡误判为更强");
            drive.Feed(AtPower(current, 270), 20);
            drive.Feed(current with { Throttle = 0.86 }, 200);
            Check(drive.Input.Shifts.Count == 0, "当前挡单帧尖峰也不能被误当成持续动力衰减");

            drive.Controller.Disable();
            drive.Feed(next, 350);
            drive.Feed(AtPower(next, 248), 20);
            drive.Feed(AtPower(next, 246), 20);
            drive.Feed(current, 350);
            drive.Enable();
            drive.Feed(current with { Throttle = 0.86 }, 220);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.HigherGearPull,
                "连续相近的高功率样本应更新动力曲线");
        });

        run("自动换挡：直线全油门打滑触及限转时允许升挡", () =>
        {
            using var drive = new Drive();
            var steady = AtPower(FullLoad with { Rpm = 6600, SpeedKmh = 75 }, 220);
            drive.Feed(steady, 360);
            Check(drive.Status.LearnedGears == 1, "应先学习用于对照的正常挡位比");
            drive.Enable(new AutomaticShiftSettings { UpshiftGripProtection = true });
            drive.Feed(steady, 200);
            drive.Feed(AtPower(steady with
            {
                Rpm = 7980, SpeedKmh = 87, Steer = 0.04, AccelerationX = 1.1,
                RearLeft = new WheelSample(SlipRatio: 1.2, 0, 0, 0, false, 0, 0, 0, 0, 0),
            }, 220), 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.Reason == ShiftReason.Limiter,
                "直线打滑造成转速／车速比偏离时，只允许限转升挡");
        });

        run("自动换挡：默认不因打滑和转向拦截高转升挡", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            var corner = FullLoad with { Rpm = 7980, Steer = 0.5, AccelerationX = 9.0,
                RearLeft = FullLoad.RearLeft with { SlipRatio = 1.2 } };
            drive.Feed(AtPower(corner, 220), 240);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "关闭升挡抓地保护时应在弯中响应实际限转");
        });

        run("自动换挡：转向或横向打滑到限转仍受保护", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { UpshiftGripProtection = true });
            var slipping = AtPower(FullLoad with
            {
                Rpm = 7980,
                RearLeft = new WheelSample(SlipRatio: 1.2, 0, 0, 0, false, 0, 0, 0, 0, 0),
            }, 220);
            foreach (var sample in new[]
            {
                slipping with { Steer = 0.20 },
                slipping with { AccelerationX = 0.30 * TelemetryDerived.Gravity },
                slipping with { Throttle = 0.89 },
                slipping with { Rpm = 7920 },
                slipping with { Clutch = 0.2 },
                slipping with { RearLeft = slipping.RearLeft with { SlipRatio = double.NaN } },
            })
            {
                drive.Feed(sample, 240);
                Check(drive.Input.Shifts.Count == 0, "漂移、非全油门、未触及限转、正在踩离合或读数无效时应持挡");
            }
            var corner = slipping with { Steer = 0.35 };
            drive.Feed(corner, 40);
            Check(drive.Status.State == AutomaticShiftState.UpshiftGripHold, "应显示开启的升挡抓地保护正在限制请求");
            drive.Controller.SetUpshiftGripProtection(false);
            drive.Feed(corner, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "关闭保护应立即生效，无需重新启用自动换挡");
        });

        run("自动换挡：实测挡位转速比用于制动降挡", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad with { Gear = 2, Rpm = 4200, SpeedKmh = 50, Throttle = 0.35 }, 400);
            drive.Feed(FullLoad with { Gear = 3, Rpm = 3300, SpeedKmh = 55, Throttle = 0.3 }, 400);
            drive.Enable();
            drive.Feed(FullLoad with { Gear = 3, Rpm = 2700, SpeedKmh = 45, Brake = 0.5, Throttle = 0 }, 400);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 2,
                "在制动区间且换挡后转速安全时应降一挡");
        });

        run("自动换挡：保持刹车和负向轮胎滑移时连续降挡", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            drive.Enable(new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination });
            var braking = FullLoad with { Gear = 4, Brake = 0.90, Throttle = 0, Rpm = 116 * 46, SpeedKmh = 116,
                Steer = 0.46, AccelerationX = 6.2,
                RearLeft = FullLoad.RearLeft with { SlipRatio = -3.8 }, RearRight = FullLoad.RearRight with { SlipRatio = -2.4 } };
            drive.Feed(braking, 240);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 3,
                "保持制动时应立即请求四降三，正常负向滑移和过弯应允许匹配降挡");
            Check(drive.Status.DownshiftCue is { Gear: 3, LandingRpm: > 6900 and < 7050 },
                "降挡指示应显示目标挡和预计接合转速");
            drive.Feed(braking with { Gear = 3, Rpm = 114 * 60, SpeedKmh = 114 }, 220);
            Check(drive.Input.Shifts.Count == 1 && drive.Status.LastShiftTo == 3, "车速过高时应停在三挡");
            for (var speed = 113; speed >= 85 && drive.Input.Shifts.Count == 1; speed--)
                drive.Feed(braking with { Gear = 3, Rpm = speed * 60, SpeedKmh = speed }, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down, ShiftDirection.Down]) && drive.Status.TargetGear == 2 &&
                  drive.Input.LastMode == GamepadShiftMode.Combination, "制动仍在持续时应继续三降二");
        });

        run("自动换挡：驱动轮转速补学短暂打滑的低挡，踩刹车时仍能降挡", () =>
        {
            foreach (var clutch in new[] { 0.0, 0.4 })
            {
                using var drive = new Drive();
                var spinning = FullLoad.RearLeft with { SlipRatio = 0.65, RotationSpeedRadPerSec = 57.6 };
                drive.Feed(FullLoad with { Gear = 2, Rpm = 5760, SpeedKmh = 60, Clutch = clutch,
                    RearLeft = spinning, RearRight = spinning }, 120);

                var rolling = FullLoad.RearLeft with { RotationSpeedRadPerSec = 72 };
                var third = FullLoad with { Gear = 3, Rpm = 5400, SpeedKmh = 90, Throttle = 0.34,
                    RearLeft = rolling, RearRight = rolling };
                drive.Feed(third, 420);
                drive.Enable();
                drive.Feed(third with { Throttle = 0, Brake = 1 }, 220);
                if (clutch == 0)
                {
                    Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) &&
                          drive.Status.DownshiftCue is { Gear: 2, LandingRpm: 7200 },
                        "应从打滑时的驱动轮转速学习二挡，并用滚动校准还原正确的接合转速");
                }
                else
                {
                    Check(drive.Input.Shifts.Count == 0 && drive.Status.State == AutomaticShiftState.WaitingForRatio,
                        "踩离合的低挡样本不能用于推算接合转速");
                }
            }
        });

        run("自动换挡：制动降挡预测实际接合转速并保留限转余量", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            drive.Enable();
            var braking = FullLoad with { Gear = 4, Brake = 1.0, Throttle = 0, Rpm = 4500, SpeedKmh = 131,
                RearLeft = FullLoad.RearLeft with { SlipRatio = -1.8 } };
            drive.Feed(braking, 240);
            Check(drive.Input.Shifts.Count == 0, "第四挡转速正常时仍须拦住可能使第三挡超转的请求");
            drive.Feed(braking with { Rpm = 129 * 46, SpeedKmh = 129 }, 20);
            drive.Feed(braking with { Rpm = 128 * 46, SpeedKmh = 128 }, 20);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 3,
                "持续减速且预计接合转速已进入制动区间时应提前请求降挡");
            Check(drive.Status.DownshiftCue is { Gear: 3, LandingRpm: >= 7300 and <= 7600 },
                "提示应按预计接合时车速计算转速");
        });

        run("自动换挡：刚升挡就踩刹车时可在转速合适后直接降挡", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 133, Rpm = 7980, Throttle = 0.3 }, 320);
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 4 });
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 133, Rpm = 7980 }, 160);
            Check(drive.Status.TargetGear == 4, "应先发出三升四请求");
            for (var speed = 132; speed >= 121 && drive.Input.Shifts.Count == 1; speed--)
                drive.Feed(FullLoad with { Gear = 4, SpeedKmh = speed, Rpm = speed * 46, Throttle = 0, Brake = 1 }, 20);
            Check(drive.Status.LastShiftTo == 4 && drive.Status.TargetGear == 3 &&
                  drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Down]),
                "刹车中的安全降挡应在原 350 毫秒反向冷却期间发出");
        });

        run("自动换挡：实测负扭矩较弱时保持发动机制动更强的挡位", () =>
        {
            foreach (var lowerTorque in new[] { 80.0, 430.0 })
            {
                using var drive = new Drive();
                var second = FullLoad with { Gear = 2, Rpm = 7200, SpeedKmh = 90, Throttle = 0 };
                var third = FullLoad with { Gear = 3, Rpm = 5400, SpeedKmh = 90, Throttle = 0 };
                drive.Feed(AtPower(second, -lowerTorque * second.Rpm * (2 * Math.PI / 60_000)), 620);
                drive.Feed(AtPower(third, -365 * third.Rpm * (2 * Math.PI / 60_000)), 620);
                drive.Enable();
                drive.Feed(third with { Brake = 1.0 }, 260);
                Check(drive.Input.Shifts.Count == (lowerTorque > 100 ? 1 : 0),
                    "在两个挡位的发动机制动数据齐备时，应比较轮上负扭矩收益");
                if (lowerTorque > 100)
                    Check(drive.Status.Reason == ShiftReason.EngineBraking, "应说明降挡能提高发动机制动力");
            }
        });

        run("自动换挡：制动降挡用实测的较低限转防止超转", () =>
        {
            using var drive = new Drive();
            LearnBrakeRatios(drive);
            var loaded = FullLoad with { Gear = 4, SpeedKmh = 150, Rpm = 6900, PowerKw = 550, TorqueNm = 760 };
            drive.Feed(loaded, 300);
            var cut = loaded with { Rpm = 6990, PowerKw = -400, TorqueNm = -555 };
            drive.Feed(cut, 20);
            drive.Feed(loaded, 40);
            drive.Feed(cut, 20);
            drive.Enable();
            var braking = loaded with { Brake = 1, Throttle = 0, SpeedKmh = 120, Rpm = 120 * 46 };
            drive.Feed(braking, 220);
            Check(drive.Input.Shifts.Count == 0, "第三挡的 7200 RPM 高于实测限转，应继续在四挡制动");
            drive.Feed(braking with { SpeedKmh = 107, Rpm = 107 * 46 }, 150);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 3,
                "接合转速回到实测红线以下后仍应在制动中降挡");
        });

        run("默认 LazyForza：满油拖挡且 HUD 报打滑时，安全降到有动力的低一挡", () =>
        {
            foreach (var drivetrain in new[] { 0, 1 })
            foreach (var lowerRatio in new[] { 32.0, 0.0, 85.0 })
            {
                using var drive = new Drive();
                var cargo = FullLoad with { CarOrdinal = 4160, CarPerformanceIndex = 800, DrivetrainType = drivetrain };
                if (lowerRatio > 0)
                    drive.Feed(AtPower(cargo with { Gear = 3, SpeedKmh = 75, Rpm = 75 * lowerRatio }, 285), 460);
                drive.Feed(AtPower(cargo with { Gear = 4, SpeedKmh = 125, Rpm = 125 * 1805 / 98.0, Throttle = 0.35 }, 175), 460);
                drive.Enable();
                var demand = AtPower(cargo with { Gear = 4, SpeedKmh = 98, Rpm = 1805,
                    AccelerationZ = -0.59 * TelemetryDerived.Gravity, BoostPsi = 73 }, 143.6);
                TelemetrySample Slip(double ratio) => drivetrain == 0
                    ? demand with { FrontLeft = demand.FrontLeft with { SlipRatio = ratio } }
                    : demand with { RearLeft = demand.RearLeft with { SlipRatio = ratio } };
                drive.Feed(Slip(0.55) with { Throttle = 0.30 }, 260);
                Check(drive.Input.Shifts.Count == 0, "轻踩油门时无需强行退挡");
                drive.Feed(AtPower(demand with { Rpm = 8030, AccelerationY = -11 }, 280), 520);
                Check(drive.Input.Shifts.Count == 0, "飞坡时的全油门高转不应触发升挡");
                drive.Feed(Slip(1.6), 600);
                Check(drive.Input.Shifts.Count == 0, "落地后驱动轮仍严重空转时应持挡");
                drive.Feed(Slip(0.55), 300);
                if (lowerRatio == 85.0)
                    Check(drive.Input.Shifts.Count == 0, "下一挡预计超过红线时仍应持挡");
                else
                    Check(drive.Status.TargetGear == 3 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]),
                        $"{drivetrain} 驱下一挡比为 {lowerRatio} 时应及时从四挡退到三挡，实际={drive.Status.State}");
            }
        });

        run("默认 LazyForza：全油门长时间侧滑时，转速回落后逐挡降挡", () =>
        {
            foreach (var drivetrain in new[] { 0, 1, 2 })
            foreach (var adaptive in new[] { false, true })
            {
                using var drive = new Drive();
                var car = FullLoad with { DrivetrainType = drivetrain };
                foreach (var (gear, ratio, speed) in new[] { (3, 53.0, 65.0), (4, 43.0, 80.0), (5, 32.0, 95.0) })
                    drive.Feed(WithWheelSpeed(car with { Gear = gear, SpeedKmh = speed, Rpm = speed * ratio, Throttle = 0.35 },
                        speed * 0.9, slip: 0), 460);
                drive.Enable(new AutomaticShiftSettings { AdaptiveGearMatching = adaptive });

                TelemetrySample Slide(int gear, double ratio, double rpm) => WithWheelSpeed(car with
                {
                    Gear = gear, SpeedKmh = 64, Rpm = rpm, Throttle = 1.0,
                    AccelerationX = -6.5, Steer = -0.35,
                    AngularVelocityY = drivetrain == 2 ? 0.05 : 0.90,
                    VelocityX = drivetrain == 2 ? 11 : 0,
                    VelocityZ = drivetrain == 2 ? 14 : 64 / 3.6,
                }, rpm / ratio * 0.9, slip: 1.35);

                drive.Feed(Slide(5, 32, 6100), 1200);
                Check(drive.Input.Shifts.Count == 0, "侧滑中发动机转速仍高时应持挡");
                drive.Feed(Slide(5, 32, 2450), 260);
                Check(drive.Status.TargetGear == 4 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]),
                    $"驱动形式={drivetrain}，自适应={adaptive}，一直满油也应安全从五挡退到四挡，实际={drive.Status.State}");
                drive.Feed(Slide(4, 43, 2450 * 43 / 32.0), 260);
                Check(drive.Status.LastShiftTo == 4 && drive.Status.TargetGear == 3 &&
                    drive.Input.Shifts.SequenceEqual([ShiftDirection.Down, ShiftDirection.Down]),
                    "确认四挡以后，侧滑仍让发动机拖挡时应继续判断下一挡");
                drive.Feed(Slide(3, 53, 2450 * 53 / 32.0), 1200);
                Check(drive.Status.LastShiftTo == 3 && drive.Status.Enabled && drive.Input.Shifts.Count == 2,
                    "三挡已能维持转速时应停止连续降挡");
            }
        });

        run("默认 LazyForza：低速满油打转能回一挡，轮速过高或挡比未知时持挡", () =>
        {
            foreach (var (firstRatio, wheelSpeed, shouldShift) in new[]
            {
                (105.0, 22.0, true), (105.0, 88.0, false), (0.0, 22.0, false),
            })
            {
                using var drive = new Drive();
                if (firstRatio > 0)
                    drive.Feed(WithWheelSpeed(FullLoad with { Gear = 1, SpeedKmh = 32, Rpm = 32 * firstRatio, Throttle = 0.35 },
                        32 * 0.9, slip: 0), 460);
                drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 50, Rpm = 3500, Throttle = 0.35 },
                    50 * 0.9, slip: 0), 460);
                drive.Enable(new AutomaticShiftSettings { ProtectLaunchWheelspin = true, ProtectShiftReengagement = true });

                var spin = WithWheelSpeed(FullLoad with
                {
                    Gear = 2, SpeedKmh = 8, Rpm = 1700, AngularVelocityY = 0.95, AccelerationX = -5.8,
                }, wheelSpeed, slip: 1.55);
                drive.Feed(spin with { Throttle = 0.45 }, 600);
                Check(drive.Input.Shifts.Count == 0, "中油门打转时保持挡位");
                drive.Feed(spin, 300);
                Check(shouldShift
                    ? drive.Status.TargetGear == 1 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Down])
                    : drive.Input.Shifts.Count == 0,
                    $"一挡比={firstRatio}、轮速={wheelSpeed}，有安全依据才应从二挡回一挡，实际={drive.Status.State}");
            }
        });

        run("严格速度挡位匹配：直线、侧滑和打转在同一车速及时降挡", () =>
        {
            var shiftTimes = new List<int>();
            foreach (var (slip, throttle, brake) in new[] { (0.0, 1.0, 0.0), (1.35, 1.0, 0.0), (1.55, 0.24, 0.45) })
            {
                using var drive = new Drive();
                drive.Feed(WithWheelSpeed(FullLoad with { Gear = 4, SpeedKmh = 110, Rpm = 4730, Throttle = 0.35 }, 99, slip: 0), 460);
                drive.Feed(WithWheelSpeed(FullLoad with { Gear = 5, SpeedKmh = 145, Rpm = 4640, Throttle = 0.35 }, 130.5, slip: 0), 460);
                drive.Enable(new AutomaticShiftSettings
                {
                    StrictSpeedGearMatching = true, AdaptiveGearMatching = true, DriftMode = true,
                    ProtectShiftReengagement = true,
                });
                TelemetrySample Frame(double speed)
                {
                    var rpm = slip == 0 ? speed * 32 : 5400;
                    return WithWheelSpeed(FullLoad with
                    {
                        Gear = 5, SpeedKmh = speed, Rpm = rpm, Throttle = throttle, Brake = brake,
                        AccelerationX = slip == 0 ? 0 : -6.5, AngularVelocityY = slip == 0 ? 0 : 0.90,
                        VelocityX = slip == 0 ? 0 : 22,
                    }, rpm / 32 * 0.9, slip);
                }
                drive.Feed(Frame(137), 1200);
                Check(drive.Input.Shifts.Count == 0, "车速仍高于五挡退四挡的界线时应保持");
                for (var elapsed = 20; elapsed <= 180; elapsed += 20)
                {
                    drive.Feed(Frame(133), 20);
                    if (drive.Input.Shifts.Count == 0) continue;
                    shiftTimes.Add(elapsed);
                    break;
                }
                Check(drive.Status.TargetGear == 4 && drive.Status.Reason == ShiftReason.StrictSpeedMatch &&
                      drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && !drive.Status.DriftMode,
                    $"严格模式应按相同车速逐挡降挡，打滑={slip}，实际={drive.Status.State}");
            }
            Check(shiftTimes.Count == 3 && shiftTimes.Distinct().Count() == 1 && shiftTimes[0] <= 100,
                "输入确认条件相同且转速安全时，降挡延迟应与侧滑和踏板状态无关");
        });

        run("严格速度挡位匹配：保护超转和滞空，升降挡之间保留车速区间", () =>
        {
            foreach (var learnFirst in new[] { true, false })
            {
                using var drive = new Drive();
                if (learnFirst)
                    drive.Feed(WithWheelSpeed(FullLoad with { Gear = 1, SpeedKmh = 40, Rpm = 4000, Throttle = 0.35 }, 36, slip: 0), 460);
                drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 65, Rpm = 4225, Throttle = 0.35 }, 58.5, slip: 0), 460);
                drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true, MaximumGear = 2 });
                var spin = WithWheelSpeed(FullLoad with
                {
                    Gear = 2, SpeedKmh = 45, Rpm = 7900, Throttle = 1, AngularVelocityY = 0.9, AccelerationX = -6.5,
                }, 109.4, slip: 1.35);
                drive.Feed(spin with { SpeedKmh = 0.8, AccelerationX = 0.5 }, 240);
                Check(drive.Input.Shifts.Count == 0, "车身接近停稳时，轮速仍指向超转也要保挡");
                drive.Feed(spin, 400);
                Check(drive.Input.Shifts.Count == 0, "车速偏低也必须检查实际转速和轮速是否会超转");
                var landing = WithWheelSpeed(spin with { Rpm = 3080 }, 42.65, slip: 1.35);
                drive.Feed(landing with { AccelerationY = -11 }, 540);
                drive.Feed(landing, 300);
                Check(drive.Input.Shifts.Count == 0, "严格模式仍须等待接地稳定");
                drive.Feed(landing, 180);
                if (!learnFirst)
                {
                    Check(drive.Input.Shifts.Count == 0 && drive.Status.State == AutomaticShiftState.WaitingForRatio,
                        "未知一挡挡比时应等待数据");
                    continue;
                }
                Check(drive.Status.TargetGear == 1 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]),
                    "落地且转速安全后应从二挡回一挡");
                drive.Feed(WithWheelSpeed(FullLoad with { Gear = 1, SpeedKmh = 45, Rpm = 4500 }, 40.5, slip: 0), 420);
                var coast = WithWheelSpeed(FullLoad with { Gear = 1, SpeedKmh = 73, Rpm = 7300, Throttle = 0 }, 65.7, slip: 0);
                drive.Feed(coast, 180);
                Check(drive.Status.TargetGear == 2 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Down, ShiftDirection.Up]),
                    "达到一挡的车速界线后应升挡，滑行同样使用车速");
                drive.Feed(WithWheelSpeed(coast with { Gear = 2, SpeedKmh = 64, Rpm = 4160 }, 57.6, slip: 0), 460);
                Check(drive.Status.LastShiftTo == 2 && drive.Input.Shifts.Count == 2, "在两条车速界线之间应保持二挡");
            }
        });

        run("严格车速匹配：Cobra Daytona 一挡低于标称门槛时按实测断油升挡", () =>
        {
            foreach (var (learnedRatio, accelerationY, slip, expected) in new[]
            {
                (6906 / 87.8, 0.0, 0.0, true), (65.0, 0.0, 0.0, true),
                (6906 / 87.8, -11.0, 0.0, false), (6906 / 87.8, 0.0, 0.8, false),
            })
            {
                using var drive = new Drive();
                var cobra = FullLoad with { CarOrdinal = 1155, CarPerformanceIndex = 700, Gear = 1 };
                drive.Feed(AtPower(WithWheelSpeed(cobra with { SpeedKmh = 60, Rpm = 60 * learnedRatio, Throttle = 0.45 },
                    54, slip: 0), 240), 460);
                drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true, ProtectLaunchWheelspin = true });

                var flat = WithWheelSpeed(cobra with { SpeedKmh = 87.8, Rpm = 6906, AccelerationY = accelerationY },
                    slip > 0 ? 107 : 87.8 * 0.9, slip);
                var powered = AtPower(flat, 305);
                var cut = AtPower(flat, -445.1);
                drive.Feed(powered, 340);
                drive.Feed(cut, 20);
                Check(drive.Input.Shifts.Count == 0, "一个负功率样本不足以触发升挡");
                for (var i = 0; i < 6 && drive.Input.Shifts.Count == 0; i++)
                {
                    drive.Feed(powered, 40);
                    drive.Feed(cut, 20);
                }
                Check(expected
                    ? drive.Status.TargetGear == 2 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up])
                    : drive.Input.Shifts.Count == 0,
                    $"实测断油应在接地时升到二挡，记录挡比={learnedRatio:F1}、垂向加速度={accelerationY}、滑移={slip}");
                if (!expected) continue;
                drive.Feed(powered with { Gear = 2, Rpm = 4700 }, 40);
                Check(drive.Status.LastShiftTo == 2 && drive.Input.Shifts.Count == 1, "应等待二挡确认且只发送一次升挡");
            }
        });

        run("严格车速匹配：测得较低断油点后按实测上限缩放后续升降挡车速界线", () =>
        {
            using var drive = new Drive();
            var cobra = FullLoad with { CarOrdinal = 1155, CarPerformanceIndex = 700, EngineMaxRpm = 8000 };
            drive.Feed(AtPower(WithWheelSpeed(cobra with { Gear = 2, SpeedKmh = 80, Rpm = 4800, Throttle = 0.45 }, 72, slip: 0), 240), 460);
            drive.Feed(AtPower(WithWheelSpeed(cobra with { Gear = 1, SpeedKmh = 60, Rpm = 4800, Throttle = 0.45 }, 54, slip: 0), 240), 460);
            drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true });

            var firstGear = WithWheelSpeed(cobra with { Gear = 1, SpeedKmh = 88.5, Rpm = 7080 }, 88.5 * 0.9, slip: 0);
            drive.Feed(AtPower(firstGear, 520), 300);
            drive.Feed(AtPower(firstGear with { Rpm = 6900 }, -445), 20);
            drive.Feed(AtPower(firstGear, 520), 40);
            drive.Feed(AtPower(firstGear with { Rpm = 6900 }, -445), 20);
            Check(drive.Status.TargetGear == 2 && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                "一挡实测 7080 RPM 断油应立即升二挡并校准实测上限");

            var holdSecond = AtPower(WithWheelSpeed(cobra with { Gear = 2, SpeedKmh = 70, Rpm = 4200 }, 63, slip: 0), 260);
            drive.Feed(holdSecond, 500);
            Check(drive.Input.Shifts.Count == 1, "70 km/h 时一挡预计 5600 RPM 高于缩放后降挡界线，应保持二挡不来回换挡");

            var upshiftSecond = AtPower(WithWheelSpeed(cobra with { Gear = 2, SpeedKmh = 112.5, Rpm = 6750 }, 112.5 * 0.9, slip: 0), 520);
            drive.Feed(upshiftSecond, 120);
            Check(drive.Status.TargetGear == 3 && drive.Status.Reason == ShiftReason.StrictSpeedMatch &&
                  drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Up]),
                "后续二挡应直接按缩放后的 0.95×实测上限（6726 RPM）纯车速升三挡，无需再次撞限转");

            drive.Feed(AtPower(WithWheelSpeed(cobra with { Gear = 3, SpeedKmh = 120, Rpm = 5600 }, 108, slip: 0), 300), 120);
            drive.Feed(AtPower(WithWheelSpeed(cobra with { Gear = 2, SpeedKmh = 70, Rpm = 4200 }, 63, slip: 0), 200), 1200);
            Check(drive.Input.Shifts.Count == 2, "手动退回二挡等待接管结束后，70 km/h 仍高于缩放后降挡界线应保持二挡");
            drive.Feed(AtPower(WithWheelSpeed(cobra with { Gear = 2, SpeedKmh = 65, Rpm = 3900 }, 58.5, slip: 0), 200), 120);
            Check(drive.Status.TargetGear == 1 && drive.Status.Reason == ShiftReason.StrictSpeedMatch &&
                  drive.Input.Shifts.SequenceEqual([ShiftDirection.Up, ShiftDirection.Up, ShiftDirection.Down]),
                "车速回落到缩放后降挡界线以下时应正常退回一挡");
        });

        run("严格车速匹配：未学到挡比时接地满油断油仍可升挡，低红线柴油与软限转不卡挡", () =>
        {
            // 1. 冷启动尚未学到一挡挡比时，触及真实断油仍应升二挡而不是卡在 WaitingForRatio
            using (var cold = new Drive())
            {
                cold.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true });
                var unlearned = WithWheelSpeed(FullLoad with { CarOrdinal = 1155, Gear = 1, SpeedKmh = 88.5, Rpm = 7080 }, 88.5 * 0.9, slip: 0.22);
                cold.Feed(AtPower(unlearned, 520), 120);
                cold.Feed(AtPower(unlearned with { Rpm = 6900 }, -445), 20);
                cold.Feed(AtPower(unlearned, 520), 40);
                cold.Feed(AtPower(unlearned with { Rpm = 6900 }, -445), 20);
                Check(cold.Status.LearnedGears == 0 && cold.Status.TargetGear == 2 &&
                      cold.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                    "挡比尚未学出时，接地满油断油仍应先升二挡");
            }

            // 2. 低红线柴油/卡车（实际 2600 RPM 断油 vs Data Out 上限 4000 RPM = 65%）
            using (var diesel = new Drive())
            {
                var unimog = FullLoad with { CarOrdinal = 2986, EngineMaxRpm = 4000, EngineIdleRpm = 700 };
                diesel.Feed(AtPower(WithWheelSpeed(unimog with { Gear = 2, SpeedKmh = 35, Rpm = 2275, Throttle = 0.45 }, 31.5, slip: 0), 120), 460);
                diesel.Feed(AtPower(WithWheelSpeed(unimog with { Gear = 1, SpeedKmh = 22, Rpm = 2200, Throttle = 0.45 }, 19.8, slip: 0), 120), 460);
                diesel.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true });
                var cutSample = WithWheelSpeed(unimog with { Gear = 1, SpeedKmh = 26, Rpm = 2600 }, 23.4, slip: 0);
                diesel.Feed(AtPower(cutSample, 180), 300);
                diesel.Feed(AtPower(cutSample with { Rpm = 2520 }, -160), 20);
                diesel.Feed(AtPower(cutSample, 180), 40);
                diesel.Feed(AtPower(cutSample with { Rpm = 2520 }, -160), 20);
                Check(diesel.Status.TargetGear == 2 && diesel.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                    "低红线柴油车（65% 标称转速断油）应正常识别并升二挡");
                diesel.Feed(AtPower(WithWheelSpeed(unimog with { Gear = 2, SpeedKmh = 38.5, Rpm = 2502.5 }, 34.65, slip: 0), 180), 500);
                Check(diesel.Status.TargetGear == 3 && diesel.Status.Reason == ShiftReason.StrictSpeedMatch,
                    "低红线柴油车校准后二挡应按缩放后车速界线升三挡");
            }

            // 3. 软限转（持续零输出 ≥200 ms）与风阻极速平台（高正功率不误升）
            using (var softCut = new Drive())
            {
                var ev = FullLoad with { CarOrdinal = 3445, EngineMaxRpm = 16000, EngineIdleRpm = 0, NumCylinders = 0 };
                softCut.Feed(AtPower(WithWheelSpeed(ev with { Gear = 1, SpeedKmh = 80, Rpm = 10000, Throttle = 0.45 }, 72, slip: 0), 220), 460);
                softCut.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true });
                var soft = WithWheelSpeed(ev with { Gear = 1, SpeedKmh = 104, Rpm = 13000, PowerKw = 0, TorqueNm = 0 }, 93.6, slip: 0);
                softCut.Feed(soft, 140);
                Check(softCut.Input.Shifts.Count == 0, "短于 200 ms 的零输出不应立即误触发软限转升挡");
                softCut.Feed(soft, 100);
                Check(softCut.Status.TargetGear == 2 && softCut.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                    "持续 200 ms 以上接地满油零输出应识别软限转并升挡");
            }

            using (var aero = new Drive())
            {
                var car = FullLoad with { EngineMaxRpm = 8000 };
                aero.Feed(AtPower(WithWheelSpeed(car with { Gear = 3, SpeedKmh = 180, Rpm = 5400, Throttle = 0.45 }, 162, slip: 0), 260), 460);
                aero.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true });
                var dragTop = AtPower(WithWheelSpeed(car with { Gear = 3, SpeedKmh = 220, Rpm = 6600 }, 198, slip: 0), 300);
                aero.Feed(dragTop, 600);
                Check(aero.Input.Shifts.Count == 0, "高挡风阻极速平台仍有正功率输出且未达换挡车速时不得误升挡");
            }
        });

        run("严格车速匹配：宏光 S FE 的浅断油在未学完挡比时也能升二挡", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true, ProtectLaunchWheelspin = true });
            var powered = WulingLimiterFrame();
            var cut = powered with { SpeedKmh = 201.640, Rpm = 9178.9, PowerKw = -82.321, TorqueNm = -85.541 };
            drive.Feed(powered, 160);
            drive.Feed(cut, 20);
            Check(drive.Input.Shifts.Count == 0, "一次浅反扭不足以触发升挡");
            drive.Feed(powered, 40);
            drive.Feed(cut, 20);
            Check(drive.Status.LearnedGears == 0 && drive.Status.TargetGear == 2 &&
                  drive.Status.Reason == ShiftReason.HighRpmOutputLoss && drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]),
                "约 201.5 km/h 下重复反转的浅断油应在两轴滚动速度一致时升挡");
            drive.Feed(powered with { Gear = 2, Rpm = 6070.9 }, 120);
            Check(drive.Status.LastShiftTo == 2 && drive.Input.Shifts.Count == 1, "仅由目标挡遥测确认，不能重复升挡");
        });

        run("严格车速匹配：浅断油兜底避开明显空转、主动收油、高速加速和单速电车", () =>
        {
            var powered = WulingLimiterFrame();
            var cut = powered with { SpeedKmh = 201.640, Rpm = 9178.9, PowerKw = -82.321, TorqueNm = -85.541 };
            var overspeed = powered.RearLeft with { RotationSpeedRadPerSec = 1.25 * powered.FrontLeft.RotationSpeedRadPerSec };
            var electric = powered with { CarOrdinal = 4200, NumCylinders = 0, EngineIdleRpm = 0 };
            foreach (var (source, drop, gain) in new[]
            {
                (powered with { RearLeft = overspeed, RearRight = overspeed }, cut with { RearLeft = overspeed, RearRight = overspeed }, 0.0),
                (powered, cut with { Throttle = 0.45 }, 0.0),
                (powered, cut, 4.5),
                (electric, cut with { CarOrdinal = electric.CarOrdinal, NumCylinders = 0, EngineIdleRpm = 0 }, 0.0),
            })
            {
                using var drive = new Drive();
                drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true });
                drive.Feed(source, 160);
                for (var pulse = 0; pulse < 5; pulse++)
                {
                    var offset = gain * pulse;
                    drive.Feed(source with { SpeedKmh = source.SpeedKmh + offset }, 20);
                    drive.Feed(drop with { SpeedKmh = drop.SpeedKmh + offset }, 20);
                }
                Check(drive.Input.Shifts.Count == 0, "缺乏接地限转证据或实际无下一挡时保持当前挡位");
            }
        });

        run("自动换挡：大油门请求降挡时检查预计转速", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad with { Gear = 2, Rpm = 4200, SpeedKmh = 50, Throttle = 0.35 }, 400);
            drive.Feed(FullLoad with { Gear = 3, Rpm = 3300, SpeedKmh = 55, Throttle = 0.3 }, 400);
            drive.Enable();
            var cruise = FullLoad with { Gear = 3, Rpm = 3900, SpeedKmh = 65, Throttle = 0.3 };
            drive.Feed(cruise, 300);
            Check(drive.Input.Shifts.Count == 0, "小油门巡航时应保持 3 挡");
            drive.Feed(cruise with { Throttle = 1 }, 300);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 2,
                "深踩油门且预计降挡后转速安全时应降一挡");
        });

        run("自动换挡：未学习到下一低挡时，行驶中保守持挡", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            drive.Feed(FullLoad with { Gear = 3, Rpm = 2500, SpeedKmh = 75 }, 450);
            Check(drive.Input.Shifts.Count == 0 && drive.Status.State == AutomaticShiftState.WaitingForRatio,
                "高速时不能盲目降到未知挡位");
            drive.Feed(FullLoad with { Gear = 3, Rpm = 900, SpeedKmh = 2, Brake = 0.5, Throttle = 0 }, 300);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Down]) && drive.Status.TargetGear == 2,
                "低速接近停车时应逐挡回到 1 挡");
        });

        run("自动换挡：近红线的预计降挡转速被拦截", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad with { Gear = 2, Rpm = 4200, SpeedKmh = 30 }, 400);
            drive.Feed(FullLoad with { Gear = 3, Rpm = 3600, SpeedKmh = 60 }, 400);
            drive.Enable();
            drive.Feed(FullLoad with { Gear = 3, Rpm = 3600, SpeedKmh = 60 }, 400);
            Check(drive.Input.Shifts.Count == 0, "降到 2 挡预计超过红线，任何降挡键都不应发出");
        });

        run("自动换挡：实测转速比突变时先保留挡位，直到重新学习", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad with { Gear = 2, Rpm = 4200, SpeedKmh = 35 }, 400);
            drive.Feed(FullLoad with { Gear = 3, Rpm = 3600, SpeedKmh = 60 }, 400);
            drive.Elapse(400);
            drive.Enable(new AutomaticShiftSettings { ProtectShiftReengagement = true });
            drive.Feed(FullLoad with { Gear = 3, Rpm = 3600, SpeedKmh = 40 }, 500);
            Check(drive.Input.Shifts.Count == 0 && drive.Status.State == AutomaticShiftState.WaitingForRatio,
                "换调校后不可继续依据旧低挡比例判断降挡转速");
            drive.Feed(FullLoad with { Gear = 3, Rpm = 4230, SpeedKmh = 47 }, 200);
            Check(drive.Status.LearnedGears == 1 && drive.Input.Shifts.Count == 0,
                "经多个车速证实后，应重学新挡比并清除其余旧挡比");
        });

        run("自动换挡：更换车型清除实测最高挡，未收录车型可指定挡数", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad with { Gear = 6 }, 350);
            Check(drive.Status.MaximumGear == 6 && drive.Status.GearLimit == GearLimitSource.Unconfirmed,
                "车型的实测最高挡是已观察值");
            drive.Enable();
            drive.Feed(FullLoad with { CarOrdinal = 42, Rpm = 4000, Throttle = 0.4 }, 650);
            Check(drive.Status.MaximumGear == 2 && drive.Input.Shifts.Count == 0, "换车后应只显示新车实际观察到的前进挡");
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 8, GamepadMode = GamepadShiftMode.Combination });
            drive.Elapse(300);
            drive.Feed(FullLoad with { Gear = 6, CarOrdinal = 42 }, 350);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]) && drive.Status.TargetGear == 7 &&
                drive.Input.LastMode == GamepadShiftMode.Combination, "应按配置的内置组合模式升到 7 挡");
        });

        run("自动换挡：暂停、非前进挡、前台失焦与车辆保护抑制按键", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { UpshiftGripProtection = true });
            drive.Input.Foreground = false;
            drive.Feed(FullLoad, 320);
            Check(drive.Status.State == AutomaticShiftState.WaitingForGame && drive.Input.Shifts.Count == 0, "只允许游戏窗口接收换挡键");
            drive.Input.Foreground = true;
            foreach (var sample in new[]
            {
                FullLoad with { IsRaceOn = false }, FullLoad with { Gear = 0 }, FullLoad with { Gear = 255 },
                FullLoad with { HandBrake = 0.8 }, FullLoad with { Clutch = 0.7 }, FullLoad with { Brake = 0.6 },
                FullLoad with { EngineMaxRpm = double.NaN }, FullLoad with { AccelerationX = 10.0 },
                FullLoad with { RearLeft = new WheelSample(SlipRatio: 1.2, 0, 0, 0, false, 0, 0, 0, 0, 0) },
            })
            {
                drive.Feed(sample, 350);
                Check(drive.Input.Shifts.Count == 0, "不满足驾驶或保护条件时不可换挡");
            }
            drive.Feed(FullLoad, 350);
            Check(drive.Input.Shifts.Count == 1, "条件恢复稳定后可升挡");
        });

        run("自动换挡：重复包、乱序包与长间隔会打断待触发条件", () =>
        {
            using var drive = new Drive();
            drive.Enable();
            drive.Feed(FullLoad with { Rpm = 4000, SpeedKmh = 85 * 4000.0 / 7800 }, 300);
            drive.Feed(FullLoad, 40);
            var lastTimestamp = drive.Timestamp;
            drive.Send(FullLoad, 120, lastTimestamp);
            drive.Send(FullLoad, 20, lastTimestamp - 20);
            Check(drive.Input.Shifts.Count == 0, "重复包和乱序包不能推进决策");
            drive.Elapse(300);
            Check(drive.Status.State == AutomaticShiftState.WaitingForTelemetry, "数据停止推进后应暂停");
            drive.Send(FullLoad, 20, lastTimestamp + 20);
            Check(drive.Input.Shifts.Count == 0, "恢复后的第一帧不能沿用上次触发条件");
            drive.Feed(FullLoad, 300);
            Check(drive.Input.Shifts.Count == 1, "重新收到连续有效遥测后可继续换挡");
        });

        run("自动换挡：时间戳回绕仍可连续采样", () =>
        {
            using var drive = new Drive { Timestamp = uint.MaxValue - 160 };
            drive.Enable();
            drive.Feed(FullLoad, 220);
            Check(drive.Timestamp < 100 && drive.Input.Shifts.Count == 1, "32 位时间戳回绕时应保留连续性");
        });

        run("自动换挡：首次确认超时暂停，进入赛事后不经手动启用即可恢复", () =>
        {
            using var drive = new Drive();
            var settings = new AutomaticShiftSettings { MaximumGear = 6 };
            drive.Enable(settings);
            drive.Feed(FullLoad, 400);
            drive.Feed(FullLoad, 1100);
            Check(drive.Status.State == AutomaticShiftState.ConfirmationTimedOut && drive.Status.Enabled && drive.Status.TargetGear == 0,
                "丢失确认应撤销旧请求，保留开启状态");
            drive.Feed(FullLoad, 400);
            Check(drive.Input.Shifts.Count == 1, "超时后应延迟重试");
            drive.Elapse(3200);
            Check(drive.Status.Enabled && drive.Status.State == AutomaticShiftState.WaitingForTelemetry,
                "赛事加载断流应进入等候");
            drive.Feed(FullLoad with { IsRaceOn = false }, 120);
            Check(drive.Status.Enabled && drive.Input.Shifts.Count == 1, "菜单内不能输出换挡");
            drive.Feed(FullLoad, 380);
            Check(drive.Status.Enabled && drive.Status.TargetGear == 3 && drive.Input.Shifts.Count == 2,
                "有效驾驶遥测恢复后应发出新的换挡请求");
            drive.Feed(FullLoad with { Gear = 1 }, 140);
            Check(drive.Status.State == AutomaticShiftState.ManualHold && drive.Status.Enabled && drive.Status.TargetGear == 0,
                "司机抢先换挡时应取消待确认并暂时让出控制");
        });

        run("自动换挡：手动换挡有接管时间，换车后丢弃旧转速比", () =>
        {
            using var drive = new Drive();
            drive.Feed(FullLoad with { Gear = 2, Rpm = 4200, SpeedKmh = 50 }, 400);
            drive.Enable();
            drive.Feed(FullLoad with { Gear = 3 }, 300);
            Check(drive.Status.State == AutomaticShiftState.ManualHold && drive.Input.Shifts.Count == 0, "手动换挡后应短时让出控制");
            drive.Feed(FullLoad with { CarOrdinal = 3852, Gear = 3, Rpm = 2500, SpeedKmh = 45 }, 1500);
            Check(drive.Status.State == AutomaticShiftState.WaitingForRatio && drive.Input.Shifts.Count == 0, "换车后不可使用上一辆车的挡位转速比");
        });

        run("自动换挡：带离合的短暂过渡与目标挡位确认不会提前松开组合键", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { MaximumGear = 6 });
            drive.Feed(FullLoad, 220);
            var releases = drive.Input.Releases;
            drive.Feed(FullLoad with { Gear = 255, Clutch = 1 }, 20);
            Check(drive.Status.TargetGear == 3 && drive.Status.State == AutomaticShiftState.Shifting && drive.Input.Releases == releases,
                "短暂无前进挡时应继续等待新挡位");
            drive.Feed(FullLoad with { Gear = 3, Clutch = 1 }, 40);
            Check(drive.Status.LastShiftTo == 3 && drive.Input.Releases == releases,
                "自己的离合动作不会阻止挡位确认或截断组合键");
            drive.Feed(FullLoad with { Gear = 3, IsRaceOn = false }, 20);
            Check(drive.Input.Releases > releases, "退出驾驶时应释放仍在进行的手柄组合键");
        });

        run("自动换挡：发送失败和停止接收时释放输入，保持用户开启状态", () =>
        {
            using var drive = new Drive();
            Check(!drive.Controller.TryEnable(new AutomaticShiftSettings { PressDurationMs = 0 }, out _), "按住时长须通过验证");
            drive.Input.Result = ShiftInputResult.Failed;
            drive.Enable();
            drive.Feed(FullLoad, 380);
            Check(drive.Status.State == AutomaticShiftState.InputFailed && drive.Status.Enabled, "系统拒绝按键时应暂停并报告失败");
            drive.Input.Result = ShiftInputResult.Sent;
            drive.Feed(FullLoad, 1000);
            Check(drive.Input.Shifts.Count == 0, "输出通道未恢复时不得再次发送");
            drive.Controller.ResumeOutput();
            drive.Feed(FullLoad, 300);
            var disconnects = drive.Input.Disconnects;
            drive.Controller.ResetSession();
            drive.Feed(FullLoad, 400);
            Check(drive.Input.Disconnects > disconnects && drive.Status.Enabled && drive.Input.Shifts.Count == 1,
                "停止接收应断开虚拟手柄并等待输出恢复");
        });

        run("防护状态 · 快照准确汇总开关武装与起步/滞空/抓地/转速匹配/碰撞/制动/手动接管激活态", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings());
            Check(drive.Status.Protections == ShiftProtectionFlags.AirborneArmed,
                $"默认仅武装滞空防护，实际={drive.Status.Protections}");

            drive.Enable(new AutomaticShiftSettings
            {
                ProtectLaunchWheelspin = true,
                ProtectAirborne = true,
                UpshiftGripProtection = true,
                ProtectShiftReengagement = true,
                MaximumGear = 6,
            });
            Check(drive.Status.Protections == (ShiftProtectionFlags.LaunchArmed | ShiftProtectionFlags.AirborneArmed |
                ShiftProtectionFlags.UpshiftGripArmed | ShiftProtectionFlags.ReengagementArmed),
                $"全开保护后应全部武装，实际={drive.Status.Protections}");

            // 1. 起步打滑（包括冷启动尚未学出 1 挡齿比时）
            var coldSpin = WithWheelSpeed(FullLoad with { Gear = 1, SpeedKmh = 20, Rpm = 4500, Throttle = 1 }, 60, slip: 0.85);
            drive.Feed(coldSpin, 160);
            Check((drive.Status.Protections & ShiftProtectionFlags.LaunchActive) != 0,
                $"起步严重打滑时应激活起步打滑防护，实际={drive.Status.Protections} state={drive.Status.State}");

            // 2. 滞空防护 vs 地面碰撞稳定
            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 80, Rpm = 5000, AccelerationY = -11 }, 72, slip: 0), 80);
            Check((drive.Status.Protections & ShiftProtectionFlags.AirborneActive) != 0 &&
                  (drive.Status.Protections & ShiftProtectionFlags.ImpactHold) == 0,
                $"飞坡滞空应激活滞空防护而非碰撞稳定，实际={drive.Status.Protections}");
            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 80, Rpm = 5000, AccelerationY = 0 }, 72, slip: 0), 420);
            Check((drive.Status.Protections & ShiftProtectionFlags.AirborneActive) == 0, "落地稳定后滞空激活态应清除");

            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 80, Rpm = 5000, SmashableMass = 1, SmashableVelDiff = 2 }, 72, slip: 0), 60);
            Check((drive.Status.Protections & ShiftProtectionFlags.ImpactHold) != 0 &&
                  (drive.Status.Protections & ShiftProtectionFlags.AirborneActive) == 0,
                $"地面撞击缓冲应显示碰撞稳定而非滞空，实际={drive.Status.Protections}");
            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 80, Rpm = 5000 }, 72, slip: 0), 420);

            // 3. 离合/手刹持挡与升挡抓地保护
            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 80, Rpm = 5000, HandBrake = 0.5 }, 72, slip: 0), 40);
            Check((drive.Status.Protections & ShiftProtectionFlags.ClutchOrHandbrakeHold) != 0,
                $"拉手刹时应激活离合/手刹持挡，实际={drive.Status.Protections}");

            var cornerLimit = WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 127.68, Rpm = 7980, Steer = 0.35, AccelerationX = 4.5 }, 114.9, slip: 0.9);
            drive.Feed(AtPower(cornerLimit, 220), 160);
            Check((drive.Status.Protections & ShiftProtectionFlags.UpshiftGripActive) != 0,
                $"弯中打滑触发高转暂缓升挡时应激活升挡抓地，实际={drive.Status.Protections} state={drive.Status.State}");

            // 4. 关闭升挡抓地保护后升 3 挡，转速尚未回落匹配时触发转速匹配等待；随后制动横向超限与手动切挡
            drive.Controller.SetUpshiftGripProtection(false);
            var straightTop = AtPower(WithWheelSpeed(FullLoad with { Gear = 2, SpeedKmh = 127.68, Rpm = 7980 }, 114.9, slip: 0), 220);
            drive.Feed(straightTop, 160);
            Check(drive.Input.Shifts.SequenceEqual([ShiftDirection.Up]), "关闭抓地保护后应升 3 挡");
            drive.Feed(AtPower(WithWheelSpeed(FullLoad with { Gear = 3, SpeedKmh = 127.68, Rpm = 7950 }, 114.9, slip: 0), 220), 60);
            Check((drive.Status.Protections & ShiftProtectionFlags.ReengagementActive) != 0,
                $"升挡后转速仍虚高等待接合时应激活转速匹配，实际={drive.Status.Protections} state={drive.Status.State}");

            drive.Feed(AtPower(WithWheelSpeed(FullLoad with { Gear = 3, SpeedKmh = 140, Rpm = 5600 }, 126, slip: 0), 220), 360);
            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 3, SpeedKmh = 140, Rpm = 5600, Throttle = 0, Brake = 0.85, AccelerationX = 18.0 }, 126, slip: 0), 40);
            Check((drive.Status.Protections & ShiftProtectionFlags.BrakingGripHold) != 0,
                $"重刹超高横向G时应激活制动抓地持挡，实际={drive.Status.Protections} state={drive.Status.State}");

            drive.Feed(WithWheelSpeed(FullLoad with { Gear = 4, SpeedKmh = 140, Rpm = 4500, Throttle = 0.3 }, 126, slip: 0), 60);
            Check((drive.Status.Protections & ShiftProtectionFlags.ManualHold) != 0,
                $"未发指令而挡位变化时应激活手动接管，实际={drive.Status.Protections} state={drive.Status.State}");
        });

        run("进入赛事：起步倒计时原地轰油不触发无效升挡并避免未确认退出", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = true, MaximumGear = 6 });
            var gridRev = FullLoad with
            {
                Gear = 1, SpeedKmh = 0, Rpm = 7980, Throttle = 1,
                RacePosition = 5, CurrentRaceTimeSeconds = 0, DistanceTraveledM = 0,
            };
            drive.Feed(gridRev, 1600);
            Check(drive.Status.Enabled && drive.Status.State == AutomaticShiftState.WaitingForDriving &&
                  drive.Input.Shifts.Count == 0,
                $"赛事起步倒计时原地轰油不应发升挡，实际 state={drive.Status.State} shifts={drive.Input.Shifts.Count}");

            // 即使自由漫游原地（RacePosition == 0）满油轰转，车速 0 时也不应触发严格模式升挡
            drive.Feed(gridRev with { RacePosition = 0 }, 1600);
            Check(drive.Status.Enabled && drive.Status.State != AutomaticShiftState.ConfirmationTimedOut &&
                  drive.Input.Shifts.Count == 0,
                $"0 km/h 原地拉转不应发升挡导致超时，实际 state={drive.Status.State} shifts={drive.Input.Shifts.Count}");
        });

        run("进入赛事：自由漫游切入赛事或传送复位时自动取消在途请求不退出换挡", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = false, MaximumGear = 6 });
            drive.Feed(FullLoad with { Gear = 2, SpeedKmh = 85, Rpm = 7850, DistanceTraveledM = 820, PositionX = 100, PositionY = 10, PositionZ = 100 }, 240);
            Check(drive.Input.Shifts.Count == 1 && drive.Status.TargetGear == 3, "应先发出进入赛事前的在途升挡请求");

            // 瞬间切入赛事起跑格（RacePosition 0 -> 4、里程清零、倒计时 <= 0 且传送坐标）
            var gridWait = FullLoad with
            {
                Gear = 1, SpeedKmh = 0, Rpm = 3500, Throttle = 1,
                RacePosition = 4, CurrentRaceTimeSeconds = -2.0, DistanceTraveledM = 0,
                PositionX = 1200, PositionY = 25, PositionZ = 2400,
            };
            drive.Feed(gridWait, 1400);
            Check(drive.Status.Enabled && drive.Status.State == AutomaticShiftState.WaitingForDriving &&
                  drive.Status.TargetGear == 0,
                $"切入赛事应在等待起跑期间保活并取消旧请求，实际 state={drive.Status.State}");

            // 正式发车后恢复正常升挡（PositionX 随车速连续推进，不触发传送保护）
            drive.Feed(FullLoad with { Gear = 1, SpeedKmh = 35, Rpm = 4200, RacePosition = 4, CurrentRaceTimeSeconds = 1.2, DistanceTraveledM = 30, PositionX = 1200, PositionY = 25, PositionZ = 2400 }, 400);
            drive.Feed(FullLoad with { Gear = 1, SpeedKmh = 65, Rpm = 7985, RacePosition = 4, CurrentRaceTimeSeconds = 3.0, DistanceTraveledM = 85, PositionX = 1205, PositionY = 25, PositionZ = 2400 }, 200);
            Check(drive.Status.Enabled && drive.Input.Shifts.Count == 2 && drive.Status.TargetGear == 2,
                $"正式发车后应恢复正常升挡，实际 shifts={drive.Input.Shifts.Count} target={drive.Status.TargetGear} state={drive.Status.State}");
        });

        run("进入赛事：反复锁挡延长重试间隔，遥测恢复后仍可继续换挡", () =>
        {
            using var drive = new Drive();
            drive.Enable(new AutomaticShiftSettings { StrictSpeedGearMatching = false, MaximumGear = 6 });
            drive.Feed(FullLoad with { Gear = 2, SpeedKmh = 85, Rpm = 7985 }, 300);
            Check(drive.Input.Shifts.Count == 1 && drive.Status.TargetGear == 3, "首次触发升 3 挡");
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 95, Rpm = 5600 }, 320);
            Check(drive.Status.LastShiftTo == 3, "先确认一次有效换挡证明输出通道正常");

            // 模拟赛事过场锁住挡位，并验证软件等待游戏回执。
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 135, Rpm = 7985 }, 300);
            Check(drive.Input.Shifts.Count == 2 && drive.Status.TargetGear == 4, "触发第 1 次过场期间换挡请求");
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 135, Rpm = 7985 }, 1000);
            Check(drive.Status.Enabled && drive.Status.TargetGear == 0 && drive.Status.State != AutomaticShiftState.ManualHold,
                $"已确认会话第 1 次短暂超时应保活且不误亮手动接管，实际 enabled={drive.Status.Enabled} state={drive.Status.State}");

            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 135, Rpm = 7985 }, 1100);
            Check(drive.Input.Shifts.Count == 3 && drive.Status.TargetGear == 4, "保活冷却结束后应可重新尝试换挡");
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 135, Rpm = 7985 }, 1000);
            Check(drive.Status.Enabled && drive.Status.TargetGear == 0, "第 2 次短暂超时仍应保活");

            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 135, Rpm = 7985 }, 1100);
            Check(drive.Status.TargetGear == 0 && drive.Input.Shifts.Count == 3, "连续失败时应拉长等待，避免反复试挡");
            drive.Feed(FullLoad with { Gear = 3, SpeedKmh = 135, Rpm = 7985 }, 1050);
            Check(drive.Input.Shifts.Count == 4 && drive.Status.TargetGear == 4, "第 2 次保活冷却结束后应重新请求升 4 挡");
            drive.Feed(FullLoad with { Gear = 4, SpeedKmh = 140, Rpm = 5800 }, 320);
            Check(drive.Status.Enabled && drive.Status.LastShiftTo == 4,
                $"过场结束后应正常确认新挡位，实际 lastTo={drive.Status.LastShiftTo} state={drive.Status.State}");

            // 确认后退避从头计时，三次超时后依然保持开启。
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var prepMs = attempt == 1 ? 400 : 1050 << (attempt - 2);
                drive.Feed(FullLoad with { Gear = 4, SpeedKmh = 175, Rpm = 7985 }, prepMs);
                Check(drive.Input.Shifts.Count == 4 + attempt && drive.Status.TargetGear == 5,
                    $"第 {attempt} 次超时测试前应已发出升 5 挡请求，实际 shifts={drive.Input.Shifts.Count} target={drive.Status.TargetGear} state={drive.Status.State}");
                drive.Feed(FullLoad with { Gear = 4, SpeedKmh = 175, Rpm = 7985 }, 1000);
                Check(drive.Status.Enabled && drive.Status.State == AutomaticShiftState.ConfirmationTimedOut && drive.Status.TargetGear == 0,
                    $"第 {attempt} 次超时后应保持开启和等候，实际={drive.Status.State}");
            }
            drive.Feed(FullLoad with { IsRaceOn = false }, 120);
            var racing = FullLoad with { Gear = 4, SpeedKmh = 175, Rpm = 7985, RacePosition = 4,
                CurrentRaceTimeSeconds = 3, DistanceTraveledM = 115 };
            drive.Feed(racing, 380);
            Check(drive.Status.Enabled && drive.Input.Shifts.Count == 8 && drive.Status.TargetGear == 5,
                "切入赛事并收到可驾驶遥测后应从当前挡位恢复");
            drive.Controller.Disable();
            drive.Feed(racing, 4000);
            Check(!drive.Status.Enabled && drive.Input.Shifts.Count == 8, "用户停用后须一直保持关闭");
        });
    }

    private static readonly TelemetrySample FullLoad = new()
    {
        IsRaceOn = true, CarOrdinal = 2740, CarPerformanceIndex = 578, DrivetrainType = 1,
        EngineMaxRpm = 8000, EngineIdleRpm = 850, Rpm = 7800, SpeedKmh = 85, Gear = 2, Throttle = 1,
    };

    private static readonly TelemetrySample FallbackLoad = FullLoad with
    {
        FrontLeft = FullLoad.FrontLeft with { CombinedSlip = 0.19 },
    };

    private static readonly TelemetrySample Cargo = FullLoad with
    {
        CarOrdinal = 3860, CarPerformanceIndex = 800, EngineMaxRpm = 10250, DrivetrainType = 0,
    };

    private static TelemetrySample LearnCargoRatios(Drive drive)
    {
        const double speed = 115.975;
        drive.Feed(AtPower(Cargo with { Gear = 6, SpeedKmh = speed, Rpm = 45 * speed }, 338), 440);
        var stuck = AtPower(Cargo with { Gear = 9, SpeedKmh = speed, Rpm = 3779.5 }, 232.4);
        drive.Feed(stuck, 400);
        return stuck;
    }

    private static TelemetrySample AtPower(TelemetrySample sample, double power)
        => sample with { PowerKw = power, TorqueNm = power * 60_000 / (sample.Rpm * 2 * Math.PI) };

    private static TelemetrySample WulingLimiterFrame() => WithWheelSpeed(FullLoad with
    {
        CarOrdinal = 4163, CarClass = 6, CarPerformanceIndex = 998, NumCylinders = 4, Gear = 1,
        SpeedKmh = 201.502, VelocityZ = 55.97, Rpm = 9526.9, EngineMaxRpm = 10000,
        PowerKw = 1525.649, TorqueNm = 1554.411, Throttle = 1,
    }, 184, slip: 0.42);

    private static TelemetrySample WithWheelSpeed(TelemetrySample sample, double wheelSpeed, double slip)
    {
        var wheel = sample.FrontLeft with
        {
            RotationSpeedRadPerSec = wheelSpeed, NormalizedSuspensionTravel = 0.4,
            SlipRatio = slip, CombinedSlip = slip * 1.4, SlipAngle = slip * 0.25,
        };
        return sample with { FrontLeft = wheel, FrontRight = wheel, RearLeft = wheel, RearRight = wheel };
    }

    private static TelemetrySample CurveFrame(int gear, double rpm)
    {
        var slope = gear == 3 ? 240.0 : 180.0;
        var torque = 900 - 0.10 * rpm;
        return FullLoad with { Gear = gear, Rpm = rpm, SpeedKmh = rpm / slope * 3.6,
            TorqueNm = torque, PowerKw = torque * rpm * (2 * Math.PI / 60_000) };
    }

    private static void LearnBrakeRatios(Drive drive)
    {
        drive.Feed(FullLoad with { Gear = 2, Rpm = 4800, SpeedKmh = 60, Throttle = 0.35 }, 420);
        drive.Feed(FullLoad with { Gear = 3, Rpm = 5400, SpeedKmh = 90, Throttle = 0.35 }, 420);
        drive.Feed(FullLoad with { Gear = 4, Rpm = 5060, SpeedKmh = 110, Throttle = 0.35 }, 420);
    }

    private sealed class Drive : IDisposable
    {
        private long _now;
        public uint Timestamp { get; set; }
        public TestInput Input { get; } = new();
        public AutomaticShiftController Controller { get; }
        public AutomaticShiftSnapshot Status => Controller.GetSnapshot(_now);

        public Drive(string? profilesPath = null) => Controller = new AutomaticShiftController(Input, profilesPath);

        public void Enable(AutomaticShiftSettings? settings = null)
            => Check(Controller.TryEnable(settings ?? new AutomaticShiftSettings(), out var error), error);

        public void Elapse(long milliseconds) => _now += milliseconds;

        public void Feed(TelemetrySample sample, int milliseconds)
        {
            for (var elapsed = 0; elapsed < milliseconds; elapsed += 20)
                Send(sample, 20, unchecked(Timestamp + 20));
        }

        public double? Ramp(TelemetrySample sample, double rpmFrom, double rpmTo, int milliseconds)
        {
            var shiftCount = Input.Shifts.Count;
            for (var elapsed = 20; elapsed <= milliseconds; elapsed += 20)
            {
                var rpm = rpmFrom + (rpmTo - rpmFrom) * elapsed / milliseconds;
                var frame = sample with { Rpm = rpm, SpeedKmh = sample.SpeedKmh * rpm / sample.Rpm };
                Feed(AtPower(frame, sample.PowerKw), 20);
                if (Input.Shifts.Count > shiftCount) return rpm;
            }
            return null;
        }

        public void Send(TelemetrySample sample, int elapsedMs, uint timestamp)
        {
            _now += elapsedMs;
            Timestamp = timestamp;
            Controller.Feed(sample with { TimestampMs = timestamp }, _now);
        }

        public void Dispose() => Controller.Dispose();
    }

    private sealed class TestInput : IAutomaticShiftInput
    {
        public bool Foreground { get; set; } = true;
        public bool IsGameInForeground => Foreground;
        public bool IsShiftPulseActive { get; set; }
        public bool DriveAssistBlocksShift { get; set; }
        public ShiftPedals? DriverPedals { get; set; }
        public string? Error => null;
        public ShiftInputResult Result { get; set; } = ShiftInputResult.Sent;
        public List<ShiftDirection> Shifts { get; } = new();
        public List<bool> ClutchStates { get; } = new();
        public GamepadShiftMode LastMode { get; private set; }
        public byte? LastBlip { get; private set; }
        public int Releases { get; private set; }
        public int Disconnects { get; private set; }
        public bool? EffectiveClutchSetting { get; private set; }

        public void SetAutomaticClutch(bool enabled) => EffectiveClutchSetting = enabled;
        public Task<string?> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings)
        {
            LastMode = settings.GamepadMode;
            if (Result == ShiftInputResult.Sent)
            {
                Shifts.Add(direction);
                ClutchStates.Add(settings.AutomaticClutch);
            }
            return Result;
        }

        public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger)
        {
            LastBlip = downshiftTrigger;
            return TryShift(direction, settings);
        }

        public void Release() => Releases++;
        public void Disconnect() => Disconnects++;
        public void Dispose() { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
