using System.Buffers.Binary;
using FH6Telemetry;

internal static class DriveAssistAnalysisTests
{
    public static void Register(Action<string, Action> run)
    {
        run("驾驶分析：配对实体和输出，保留四轮和有符号 G 数据", () =>
        {
            var analysis = new DriveAssistAnalysis();
            var raw = new ShiftGamepadReport(0, 200, LeftX: -9000);
            var output = raw with { RightTrigger = 100, LeftX = -6000 };
            analysis.Add(Frame() with { AccelerationX = -7, AccelerationZ = -4, VelocityX = 3,
                FrontLeft = new(-1.1, 0, 1.1, 40, false, 0, 0, .15, .2, 75) },
                raw, output, DriveAssistFeature.Traction, 1500);
            var snapshot = analysis.Snapshot();
            var latest = snapshot.Latest ?? throw new InvalidOperationException("应有有效帧");
            Check(latest.Physical == raw && latest.Output == output && latest.Applied == DriveAssistFeature.Traction,
                "应保留实际输入与输出");
            Check(latest.Wheels.Count == 4 && latest.Wheels[0].LongitudinalSlip == -1.1 && latest.SideslipDegrees > 0,
                "应分别显示每轮与有符号侧滑");
            Check(snapshot.GPoints.Single() is { LateralG: < 0, LongitudinalG: < 0 }, "制动左弯应落在对应 G 象限");
            analysis.Add(Frame() with { TimestampMs = 20 }, null, null, 0, 1520);
            Check(analysis.Snapshot().Latest is { Physical: null, Output: null }, "没有输入时不应生成手柄读数");
        });

        run("驾驶分析：持续失稳生成有前后文的事件与分弯报告", () =>
        {
            var analysis = new DriveAssistAnalysis();
            for (uint ms = 0; ms <= 6000; ms += 50)
            {
                var inTurn = ms >= 1000 && ms <= 4200;
                var loose = ms >= 2600 && ms <= 3200;
                var sample = Frame() with { TimestampMs = ms, PositionZ = 30 + ms * .02,
                    AccelerationX = inTurn ? -7 : 0, AngularVelocityY = loose ? 1.1 : inTurn ? -.4 : 0,
                    VelocityX = loose ? 10 : 0, Throttle = loose ? .95 : .35 };
                analysis.Add(sample, null, null, 0, ms);
            }
            var report = analysis.Snapshot();
            Check(report.Events.Single() is { StartMs: < 2600, EndMs: > 3200, PeakSideslipDegrees: > 22 },
                "持续失稳片段应包含介入前后的帧");
            Check(report.Corners.Single() is { Direction: -1, DurationMs: > 2500 }, "一段持续左弯应输出一条分弯");
            Check(report.Coaching.Any(tip => tip.Kind == DriveAssistCoachingKind.PowerDuringSlide && tip.Samples > 0),
                "大油门侧滑应输出对应驾驶提示");
        });

        run("驾驶分析：只在手柄输出之后的遥测响应匹配时估计延迟", () =>
        {
            var analysis = new DriveAssistAnalysis();
            var lifted = new ShiftGamepadReport(0, 0);
            var stepped = lifted with { RightTrigger = 204 };
            foreach (var ms in new[] { 0, 25, 50, 100, 125, 160 })
            {
                var output = ms >= 100 ? stepped : lifted;
                var observed = ms switch { 125 => .2, 160 => .6, _ => 0 };
                analysis.Add(Frame() with { TimestampMs = (uint)ms, Throttle = observed }, output, output, 0, ms);
            }
            var response = analysis.Snapshot().Response;
            Check(response is { ThrottleMilliseconds: 60, ThrottleEvidence: 1, BrakeMilliseconds: null, SteeringMilliseconds: null },
                "应记录 60ms 匹配证据，未变化的信号无估计");
        });

        run("驾驶分析：路线同时要求附近位置与同向，融合邻近视觉线索", () =>
        {
            var training = new DriveAssistAnalysis();
            for (uint ms = 0; ms <= 5200; ms += 50)
                training.Add(Frame() with { TimestampMs = ms, PositionX = 5, PositionZ = 30 + ms * .020 }, null, null, 0, ms);
            Check(training.LearnRoute(), "有足够连续移动轨迹才允许学习线路");
            training.SetVision([new(5300, .4, .9)]);
            for (uint ms = 5250; ms <= 5300; ms += 50)
                training.Add(Frame() with { TimestampMs = ms, PositionX = 6, PositionZ = 134 + (ms - 5200) * .020 }, null, null, 0, ms);
            Check(training.Snapshot().RouteHint is { Source: DriveAssistGuidanceSource.Fused, OffsetMetres: > .4 and < 1 },
                "近处同向路线应和同一时钟内的视觉偏移融合");
            training.Add(Frame() with { TimestampMs = 5350, PositionX = 6, PositionZ = 132 }, null, null, 0, 5350);
            Check(training.Snapshot().RouteHint is not { Source: DriveAssistGuidanceSource.Fused }, "逆向离场时不复用旧路段");
        });

        run("驾驶分析：回放读取真实录制格式且不给旧录制捏造实体输入", () =>
        {
            var root = Directory.CreateTempSubdirectory("fh6-assist-replay-test-");
            try
            {
                var settings = new DriveAssistSettings { Features = DriveAssistFeature.Understeer };
                var writer = new DriveRecorder(root.FullName, new(), settings);
                var now = Environment.TickCount64;
                var raw = new ShiftGamepadReport(0, 100, LeftX: 26000);
                var recorded = raw with { LeftX = 19000 };
                try
                {
                    for (uint frame = 0; frame < 6; frame++)
                    {
                        var stamp = 1000 + frame * 20;
                        writer.RecordTelemetry(Packet(stamp), null, now + frame * 20);
                        if (frame != 0 && frame != 4)
                            writer.RecordShift(new(now + frame * 20 + 1, "assist_frame", Report: recorded, TelemetryTimestampMs: stamp,
                                PhysicalReport: raw, AppliedAssist: DriveAssistFeature.Understeer, AssistFresh: true));
                        if (frame == 4)
                            writer.RecordShift(new(now + frame * 20 + 1, "assist_frame", Report: recorded, TelemetryTimestampMs: stamp,
                                PhysicalReport: raw, AssistFresh: false));
                    }
                }
                finally { writer.StopAsync().GetAwaiter().GetResult(); }
                var replay = DriveAssistReplay.LoadAsync(writer.Folder, settings, CancellationToken.None).GetAwaiter().GetResult();
                Check(replay.Frames.Count == 6 && replay.Frames[0] is { Physical: null, SimulatedOutput: null },
                    "无配对日志的帧仍可看遥测，只跳过无法重放的控制");
                Check(replay.Frames[3] is { Physical: { }, RecordedOutput: { }, SimulatedOutput: { } } &&
                    replay.Frames[3].RecordedOutput == recorded && replay.Frames[3].SimulatedOutput?.LeftX < raw.LeftX,
                    "待比较的值必须是同步录制输出和本次控制器演算值");
                Check(replay.Frames[4].SimulatedOutput is null && replay.FrameAt(replay.Frames[3].ElapsedMs) == replay.Frames[3],
                    "失焦或过期帧不模拟输出，回放游标按时间定位");
            }
            finally { root.Delete(recursive: true); }
        });

        run("驾驶分析：视觉提示文件校验时钟、偏移与置信度", () =>
        {
            var root = Directory.CreateTempSubdirectory("fh6-assist-vision-test-");
            try
            {
                var file = Path.Combine(root.FullName, "vision.csv");
                File.WriteAllText(file, "elapsedMs,offsetMetres,confidence\n30,0.5,0.8\n60,-0.2,0.9\n");
                var cues = DriveAssistRoute.LoadVisionAsync(file, CancellationToken.None).GetAwaiter().GetResult();
                Check(cues.Length == 2 && cues[1] == new DriveAssistVisionCue(60, -.2, .9), "合法提示应保留时间及有符号偏移");
                File.WriteAllText(file, "elapsedMs,offsetMetres,confidence\n60,NaN,1.2\n");
                try { DriveAssistRoute.LoadVisionAsync(file, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException("应拒绝无效视觉提示");
            }
            finally { root.Delete(recursive: true); }
        });
    }

    private static TelemetrySample Frame() => new()
    {
        IsRaceOn = true, CarOrdinal = 1, CarPerformanceIndex = 700, Gear = 3, SpeedKmh = 72,
        EngineMaxRpm = 8000, EngineIdleRpm = 900, Rpm = 3000, VelocityZ = 20,
    };

    private static byte[] Packet(uint timestamp)
    {
        var packet = new byte[Fh6Telemetry.PacketLength];
        void Write(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), value);
        BinaryPrimitives.WriteInt32LittleEndian(packet, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(212), 1);
        Write(8, 8000); Write(12, 900); Write(16, 3000); Write(40, 20); Write(256, 20);
        Write(68, .2f); Write(72, .2f); Write(76, .2f); Write(80, .2f);
        Write(164, 1.8f); Write(168, 1.8f);
        packet[319] = 3;
        return packet;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
