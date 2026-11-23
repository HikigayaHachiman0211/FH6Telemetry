using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using FH6Telemetry;

internal static class TelemetryModuleTests
{
    public static void Register(Action<string, Action> run)
    {
        run("行驶记录：原始 UDP 无抽样，保留完整字段、时序与独立输入动作", () =>
        {
            var directory = Directory.CreateTempSubdirectory("fh6-drive-capture-");
            try
            {
                var settings = new AutomaticShiftSettings { Algorithm = ShiftAlgorithm.PowerDrop };
                var recorder = new DriveRecorder(directory.FullName, settings);
                var packet = FullPacket();
                Check(Fh6Telemetry.TryRead(packet, out var frame), "应有一组完整的遥测样本");
                recorder.RecordTelemetry(packet, frame with { PowerKw = double.NaN }, Environment.TickCount64);
                for (var frameIndex = 1; frameIndex < 710; frameIndex++)
                    recorder.RecordTelemetry(packet, frame, Environment.TickCount64);
                recorder.RecordTelemetry([0x12, 0x34], null, Environment.TickCount64);
                recorder.RecordShift(new(Environment.TickCount64, "shift_request", Gear: 4, TargetGear: 5,
                    Reason: ShiftReason.HighRpmOutputLoss));
                recorder.RecordShift(new(Environment.TickCount64, "gamepad_apply", InjectedButtons: 0x2040));
                recorder.StopAsync().GetAwaiter().GetResult();

                using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.Folder, "session.json")));
                Check(session.RootElement.GetProperty("state").GetString() == "complete" &&
                      session.RootElement.GetProperty("telemetryFrames").GetInt32() == 711 &&
                      session.RootElement.GetProperty("actions").GetInt32() == 2, "摘要应统计所有包和换挡事件");
                using var raw = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(recorder.Folder, "telemetry.jsonl.gz")),
                    CompressionMode.Decompress));
                using var first = JsonDocument.Parse(raw.ReadLine()!);
                Check(first.RootElement.GetProperty("datagram").GetBytesFromBase64().SequenceEqual(packet) &&
                      first.RootElement.GetProperty("sample").GetProperty("powerKw").GetString() == "NaN" &&
                      first.RootElement.GetProperty("sample").GetProperty("rearRight").TryGetProperty("slipRatio", out _) &&
                      first.RootElement.GetProperty("elapsedMs").GetInt64() >= 0, "逐帧记录应可回放原始数据与车轮字段");
                var count = 1;
                var last = "";
                while (raw.ReadLine() is { } line) { count++; last = line; }
                Check(count == 711 && !JsonDocument.Parse(last).RootElement.TryGetProperty("sample", out _),
                    "重复时间戳和无法解码的包也要完整保留");
                var actions = File.ReadAllLines(Path.Combine(recorder.Folder, "shift-actions.jsonl"));
                Check(actions[0].Contains("HighRpmOutputLoss") && actions[1].Contains("gamepad_apply"),
                    "诊断动作与遥测应共享同一开始时钟");
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        });

        run("遥测协议：解析 324 字节包的全部文档字段", () =>
        {
            var packet = FullPacket();
            Check(Fh6Telemetry.TryRead(packet, out var sample), "完整数据包应能解析");
            Near(100, sample.SpeedKmh, 0.01);             // 27.777 m/s × 3.6
            Near(200, sample.PowerKw, 0.001);             // 200000 W
            Near(400, sample.TorqueNm, 0.001);
            Near(7000, sample.Rpm, 0.001);
            Near(8500, sample.EngineMaxRpm, 0.001);
            Near(900, sample.EngineIdleRpm, 0.001);
            Check(sample.Gear == 4, "挡位错误");
            Near(1.0, sample.Throttle, 0.01);
            Near(0.0, sample.Brake, 0.01);
            Near(-0.5, sample.Steer, 0.02);
            Check(sample.CarOrdinal == 3852 && sample.CarPerformanceIndex == 283, "车辆字段错误");
            Check(sample.DrivetrainType == 1 && sample.NumCylinders == 3, "驱动/气缸字段错误");
            Near(123.4, sample.DistanceTraveledM, 0.01);
            Near(23.5, sample.FrontLeft.TireTempC, 0.01);
            Near(0.25, sample.RearLeft.SlipRatio, 0.001);
            Near(12.5, sample.FrontRight.RotationSpeedRadPerSec, 0.01);
            Check(sample.FrontLeft.OnRumbleStrip, "路肩标志错误");
            Near(1.5, sample.BoostPsi, 0.001);
            Near(0.5, sample.FuelRatio, 0.001);
        });

        run("遥测协议：拒绝截断、非法速度与非车辆包", () =>
        {
            var packet = FullPacket();
            Check(!Fh6Telemetry.TryRead(packet[..323], out _), "截断包必须拒绝");
            var nanSpeed = FullPacket();
            WriteFloat(nanSpeed, 256, float.NaN);
            Check(!Fh6Telemetry.TryRead(nanSpeed, out _), "NaN 速度必须拒绝");
            var noCar = FullPacket();
            BinaryPrimitives.WriteInt32LittleEndian(noCar.AsSpan(212), 0);
            Check(!Fh6Telemetry.TryRead(noCar, out _), "无车型编号必须拒绝");
            var hugeSpeed = FullPacket();
            WriteFloat(hugeSpeed, 256, 500f);
            Check(!Fh6Telemetry.TryRead(hugeSpeed, out _), "超出物理上限的速度必须拒绝");
        });

        run("遥测衍生：G 值来自加速度字段并做低通滤波", () =>
        {
            var derived = new TelemetryDerived();
            var settings = new TelemetrySettings { SmoothingAlpha = 100 };
            Fh6Telemetry.TryRead(FullPacket(), out var sample);
            derived.Update(sample, settings);
            Near(sample.AccelerationZ / 9.80665, derived.LongitudinalG, 0.0001);
            Near(-sample.AccelerationX / 9.80665, derived.LateralG, 0.0001);
            Near(Math.Sqrt(derived.LongitudinalG * derived.LongitudinalG + derived.LateralG * derived.LateralG),
                derived.CombinedG, 0.0001);
        });

        run("遥测衍生：数据中断不计算速度差分", () =>
        {
            var derived = new TelemetryDerived();
            var settings = new TelemetrySettings { SmoothingAlpha = 100 };
            var packet = FullPacket();
            Fh6Telemetry.TryRead(packet, out var first);
            derived.Update(first, settings);
            WriteFloat(packet, 256, 30f);                  // 108 km/h
            WriteTimestamp(packet, 4000);                  // 3.9 s 后
            Fh6Telemetry.TryRead(packet, out var second);
            derived.Update(second, settings);
            Check(derived.SpeedDerivedAccelerationMps2 is null, "中断后不应计算速度差分");
            Check(derived.DataInterrupted, "应标记数据中断");
        });

        run("单位换算：速度/功率/扭矩/温度", () =>
        {
            Near(62.137, TelemetryUnits.Speed(100, "mph"), 0.001);
            Near(27.778, TelemetryUnits.Speed(100, "m/s"), 0.001);
            Near(134.102, TelemetryUnits.Power(100, "hp"), 0.001);
            Near(135.962, TelemetryUnits.Power(100, "PS"), 0.01);
            Near(73.756, TelemetryUnits.Torque(100, "lb-ft"), 0.01);
            Near(212, TelemetryUnits.Temperature(100, "°F"), 0.001);
            Near(100, TelemetryUnits.Speed(100, "km/h"), 0.001);
        });

        run("测试引擎：0–100 加速按时间戳插值计时", () =>
        {
            var settings = new TelemetrySettings();
            var engine = new PerformanceTestEngine(settings);
            engine.SetEnabled(TestCatalog.All.Where(t => t.Key == "a0_100"));
            var results = new List<TestResult>();
            // 从 0 到 100 km/h 线性加速，用 4.0 秒，60 Hz 采样。
            for (var i = 0; i <= 240; i++)
            {
                var t = (uint)i * 16;
                var speed = i / 240.0 * 100.0;
                results.AddRange(engine.Feed(Sample(t, speed), new TelemetryDerived()));
            }
            Check(results.Count == 1, $"应产生 1 条成绩，实际 {results.Count}");
            // 0–100 按官方口径从约 1 km/h 起计：4.0 s 全程减去约 0.038 s 起步段 ≈ 3.80 s
            Near(3.802, results[0].DurationSeconds, 0.02);
            Check(results[0].Valid, "成绩应有效");
        });

        run("测试引擎：数据中断使进行中的测试失效", () =>
        {
            var engine = new PerformanceTestEngine(new TelemetrySettings());
            engine.SetEnabled(TestCatalog.All.Where(t => t.Key == "a0_100"));
            var derived = new TelemetryDerived();
            var results = new List<TestResult>();
            for (var i = 0; i <= 60; i++) results.AddRange(engine.Feed(Sample((uint)i * 16, i / 60.0 * 50), derived));
            // 3 秒后从中断处继续（速度仍 50），不应因跨越阈值而伪造成绩
            for (var i = 0; i <= 60; i++) results.AddRange(engine.Feed(Sample(4000 + (uint)i * 16, 50 + i / 60.0 * 40), derived));
            Check(results.Count == 0, "中断后的数据不得伪造测试成绩");
        });

        run("测试引擎：制动测试按 100→0 计算用时和距离", () =>
        {
            var engine = new PerformanceTestEngine(new TelemetrySettings());
            engine.SetEnabled(TestCatalog.All.Where(t => t.Key == "b100_0"));
            var results = new List<TestResult>();
            var distance = 0.0;
            for (var i = 0; i <= 200; i++)
            {
                var speed = 100 - i * 0.5;                 // 100 → 0，约 3.2 s
                distance += speed / 3.6 * 0.016;           // 距离随速度积分
                results.AddRange(engine.Feed(Sample((uint)(i * 16), speed, distance), new TelemetryDerived()));
            }
            Check(results.Count == 1, $"应产生 1 条制动成绩，实际 {results.Count}");
            Check(results[0].DurationSeconds is > 3.0 and < 3.4, $"制动用时异常：{results[0].DurationSeconds}");
            Check(results[0].DistanceMeters > 0, "制动距离应有值");
        });

        run("测试引擎：距离测试使用游戏累计里程", () =>
        {
            var engine = new PerformanceTestEngine(new TelemetrySettings());
            engine.SetEnabled(TestCatalog.All.Where(t => t.Key == "d400m"));
            var results = new List<TestResult>();
            for (var i = 0; i <= 400; i++)
            {
                var distance = i * 1.2;                    // 游戏累计里程
                var speed = i * 0.2;                       // 从 0 起步，跨过起步判定阈值
                results.AddRange(engine.Feed(Sample((uint)(i * 16), speed, distance), new TelemetryDerived()));
            }
            Check(results.Count == 1, $"应产生 1 条距离成绩，实际 {results.Count}");
            Check(results[0].DistanceMeters is > 390 and < 410, $"终点距离异常：{results[0].DistanceMeters}");
        });

        run("测试记录存储：保存、读取、导出与删除", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "fh6-telemetry-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new TelemetryStore(directory);
                var record = new TestResult
                {
                    Key = "a0_100", Title = "0–100 加速", Kind = PerformanceTestKind.Acceleration,
                    DurationSeconds = 4.32, DistanceMeters = 60.5, EndSpeedKmh = 100.2, PeakPowerKw = 320,
                    PeakTorqueNm = 800, CarOrdinal = 3852, CarName = "HON_Beat_91",
                    VehicleParameters = new Dictionary<string, string> { ["TorqueScale"] = "913" },
                };
                store.Save(record, [Sample(0, 0), Sample(16, 5), Sample(32, 20)]);
                var reloaded = new TelemetryStore(directory);
                Check(reloaded.Records.Count == 1, "重新载入后应有 1 条记录");
                Near(4.32, reloaded.Records[0].DurationSeconds, 0.0001);
                Check(reloaded.Records[0].VehicleParameters["TorqueScale"] == "913", "车辆参数快照应保留");
                Check(reloaded.SamplePath(record.Id) is not null, "原始采样文件应存在");
                var csv = reloaded.ExportCsv(reloaded.Records);
                var json = reloaded.ExportJson(reloaded.Records);
                var html = reloaded.ExportHtmlReport(reloaded.Records);
                Check(File.Exists(csv) && File.Exists(json) && File.Exists(html), "导出文件应生成");
                Check(File.ReadAllText(json).Contains("HON_Beat_91"), "JSON 应包含车辆名称");
                reloaded.Delete(record.Id);
                Check(new TelemetryStore(directory).Records.Count == 0, "删除后不应残留记录");
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        });

        run("UDP 接收：本机回环能收到并解析数据包", () =>
        {
            var settings = new TelemetrySettings { Port = FreeUdpPort(), TimeoutMs = 3000 };
            using var receiver = new TelemetryReceiver(settings);
            var received = new List<TelemetrySample>();
            receiver.SampleReceived += sample => { lock (received) received.Add(sample); };
            Check(receiver.Start(out var error), "接收器应能开始监听：" + error);
            using var sender = new System.Net.Sockets.UdpClient();
            var packet = FullPacket();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && received.Count < 3)
            {
                sender.Send(packet, packet.Length, "127.0.0.1", settings.Port);
                Thread.Sleep(30);
            }
            receiver.Stop();
            lock (received)
            {
                Check(received.Count > 0, $"应收到数据包（收到 {receiver.PacketsReceived}，失败 {receiver.ParseFailures}，错误 {receiver.LastError}）");
                Near(100, received[0].SpeedKmh, 0.01);
            }
        });

        run("UDP 接收：停止后可立即在同一端口重新开始", () =>
        {
            var settings = new TelemetrySettings { Port = FreeUdpPort() };
            using var receiver = new TelemetryReceiver(settings);
            Check(receiver.Start(out var error), error);
            receiver.Stop();
            Check(!receiver.IsRunning, "停止返回时监听应已关闭");
            Check(receiver.Start(out error) && receiver.IsRunning, "应能在同一端口重新开始：" + error);
        });

        run("性能测试：未产生成绩的采样复用空结果", () =>
        {
            var engine = new PerformanceTestEngine(new());
            var sample = new TelemetrySample();
            var derived = new TelemetryDerived();
            Check(ReferenceEquals(engine.Feed(sample, derived), engine.Feed(sample, derived)), "每个采样无需分配空成绩列表");
        });

        run("环形缓冲：容量有界且按时间窗口裁剪", () =>
        {
            var buffer = new TelemetryRingBuffer(120);
            var derived = new TelemetryDerived();
            var sample = new TelemetrySample { SpeedKmh = 100, Rpm = 5000 };
            for (var i = 0; i < 500; i++) buffer.Add(i * 0.1, sample, derived);
            Check(buffer.Count == 120, "环形缓冲容量必须有界");
            var (times, values) = buffer.Window(TelemetryRingBuffer.Channel.Speed, 2.0);
            Check(times.Length > 0 && times.Length <= 120, "窗口裁剪后点数异常");
            Check(times[^1] - times[0] <= 2.01, "窗口时间跨度超出设定");
            Near(100, values[0], 0.001);
        });

        run("遥测缓存：同时限制秒数和帧数，绘图抽样保留动力尖峰", () =>
        {
            var curve = new TelemetryRingBuffer(1200);
            var raw = new TelemetrySampleBuffer();
            var retention = new TelemetryRetention { Preset = TelemetryRetentionPreset.Custom, Seconds = 3, Frames = 50 };
            var derived = new TelemetryDerived();
            for (var index = 0; index < 400; index++)
            {
                var sample = new TelemetrySample { TimestampMs = (uint)(index * 20), PowerKw = index == 381 ? -390 : 200 };
                curve.Add(index * 0.02, sample, derived);
                raw.Add(sample, index * 20, retention);
            }
            curve.Trim(5, retention.Frames);
            var (times, values) = curve.Window(TelemetryRingBuffer.Channel.Power, 3, 12);
            Check(curve.Count == 50 && raw.Snapshot().Length == 50 && times.Length <= 12 && values.Min() == -390,
                "缓存应保留最新指定帧数，缩减绘图点数后仍须保留断油信号");
            raw.Trim(10_500, retention);
            curve.Trim(7.5, retention.Frames);
            Check(raw.Snapshot().Length == 25 && curve.Count == 25, "停止接收后也应按时间清掉过期采样");
        });

        run("挡位曲线：保留准确的变挡时间，标明倒挡和过渡挡，有点数上限", () =>
        {
            var buffer = new TelemetryRingBuffer(1200);
            var derived = new TelemetryDerived();
            for (var index = 0; index < 600; index++)
            {
                var gear = index < 200 ? 9 : index == 200 ? 255 : index < 450 ? 8 : 0;
                buffer.Add(index / 100.0, new TelemetrySample { Gear = gear }, derived);
            }
            var (times, values) = buffer.GearWindow(6, 10);
            Check(times.SequenceEqual([0.0, 2.0, 2.01, 4.50, 5.99]) &&
                values.SequenceEqual([9.0, -1.0, 8.0, 0.0, 0.0]), "变挡时间必须保留，N 和 R 不能当作高挡绘制");
            for (var index = 600; index < 1000; index++)
                buffer.Add(index / 100.0, new TelemetrySample { Gear = index % 2 + 8 }, derived);
            var crowded = buffer.GearWindow(10, 10);
            Check(crowded.Values.Length <= 10 && crowded.Times[^1] == 9.99, "曲线必须限制绘制点且保留最后时刻");
        });

        run("成绩保留：清理过期成绩时一并删除其采样文件", () =>
        {
            var directory = Directory.CreateTempSubdirectory("fh6-telemetry-retention-");
            try
            {
                var store = new TelemetryStore(directory.FullName);
                var old = new TestResult { RecordedAtLocal = new DateTime(2024, 1, 1), Title = "过期" };
                var latest = new TestResult { RecordedAtLocal = new DateTime(2024, 3, 1), Title = "保留" };
                var sample = new TelemetrySample { TimestampMs = 10 };
                store.Save(old, [sample]);
                store.Save(latest, [sample]);
                Check(store.PruneHistory(new DateTime(2024, 2, 1), 500) == 1 && store.SamplePath(old.Id) is null,
                    "应删除超期索引和对应采样文件");
                var reopened = new TelemetryStore(directory.FullName);
                Check(reopened.Records.Single().Id == latest.Id && reopened.SamplePath(latest.Id) is not null,
                    "回读索引后近期成绩和采样文件应仍可用");
            }
            finally { directory.Delete(recursive: true); }
        });
    }

    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static TelemetrySample Sample(uint timestamp, double speedKmh, double distance = 0)
        => new() { TimestampMs = timestamp, CarOrdinal = 1, SpeedKmh = speedKmh, DistanceTraveledM = distance };

    private static byte[] FullPacket()
    {
        var bytes = new byte[324];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        WriteTimestamp(bytes, 1000);
        WriteFloat(bytes, 8, 8500); WriteFloat(bytes, 12, 900); WriteFloat(bytes, 16, 7000);
        WriteFloat(bytes, 20, -4.2f); WriteFloat(bytes, 24, 9.7f); WriteFloat(bytes, 28, 3.1f);
        WriteFloat(bytes, 32, 1); WriteFloat(bytes, 36, 2); WriteFloat(bytes, 40, 27.777f);
        WriteFloat(bytes, 44, 0.1f); WriteFloat(bytes, 48, 0.2f); WriteFloat(bytes, 52, 0.3f);
        WriteFloat(bytes, 56, 0.4f); WriteFloat(bytes, 60, 0.05f); WriteFloat(bytes, 64, 0.02f);
        WriteFloat(bytes, 68, 0.3f); WriteFloat(bytes, 72, 0.31f); WriteFloat(bytes, 76, 0.32f); WriteFloat(bytes, 80, 0.33f);
        WriteFloat(bytes, 84, 0.2f); WriteFloat(bytes, 88, 0.21f); WriteFloat(bytes, 92, 0.25f); WriteFloat(bytes, 96, 0.26f);
        WriteFloat(bytes, 100, 11.5f); WriteFloat(bytes, 104, 12.5f); WriteFloat(bytes, 108, 11.8f); WriteFloat(bytes, 112, 12.8f);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(116), 1);
        WriteFloat(bytes, 132, 0.02f);
        WriteFloat(bytes, 148, 0.01f);
        WriteFloat(bytes, 164, 0.03f); WriteFloat(bytes, 168, 0.04f);
        WriteFloat(bytes, 180, 0.5f); WriteFloat(bytes, 184, 0.6f);
        WriteFloat(bytes, 196, 0.012f); WriteFloat(bytes, 200, 0.014f);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(212), 3852);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(216), 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(220), 283);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(224), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(228), 3);
        WriteFloat(bytes, 244, 100); WriteFloat(bytes, 248, 5); WriteFloat(bytes, 252, 200);
        WriteFloat(bytes, 256, 27.777f); WriteFloat(bytes, 260, 200000); WriteFloat(bytes, 264, 400);
        WriteFloat(bytes, 268, 23.5f); WriteFloat(bytes, 272, 24.5f); WriteFloat(bytes, 276, 25.5f); WriteFloat(bytes, 280, 26.5f);
        WriteFloat(bytes, 284, 1.5f); WriteFloat(bytes, 288, 0.5f); WriteFloat(bytes, 292, 123.4f);
        WriteFloat(bytes, 296, 90.5f); WriteFloat(bytes, 300, 91.2f); WriteFloat(bytes, 304, 12.3f); WriteFloat(bytes, 308, 45.6f);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(312), 2);
        bytes[314] = 3; bytes[315] = 255; bytes[316] = 0; bytes[317] = 0; bytes[318] = 0;
        bytes[319] = 4; bytes[320] = unchecked((byte)(sbyte)-64);
        return bytes;
    }

    private static void WriteFloat(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
    private static void WriteTimestamp(byte[] bytes, uint timestamp)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), timestamp);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance = 0.000001)
        => Check(Math.Abs(expected - actual) <= tolerance, $"预期 {expected}，实际 {actual}");
}
