using System.Buffers.Binary;
using FH6Telemetry;

internal static class TelemetryTests
{
    public static void Register(Action<string, Action> run)
    {
        run("FH6 数据输出解码速度、功率与车辆类型", () =>
        {
            var packet = Packet(1000, 20, 212000, 480, 5);
            Float(packet, 236, 0.75f);
            Float(packet, 240, 18.5f);
            packet[319] = 10;
            Check(Fh6Telemetry.TryRead(packet, out var sample), "有效 324 字节数据包应能读取");
            Near(72, sample.SpeedKmh); Near(212, sample.PowerKw); Near(480, sample.TorqueNm);
            Near(0.75, sample.SmashableVelDiff); Near(18.5, sample.SmashableMass);
            Check(sample.CarOrdinal == 5 && sample.Gear == 10, "应解析车型编号和十挡");
            Check(!Fh6Telemetry.TryRead(packet[..323], out _), "截断的数据包不能读取");
            Float(packet, 256, float.NaN);
            Check(!Fh6Telemetry.TryRead(packet, out _), "非有限速度必须拒绝");
        });
        run("零百测速依据时间插值并按车型和中断重置", () =>
        {
            var stats = new PerformanceStats();
            Fh6Telemetry.TryRead(Packet(1000, 0, 0, 0, 1), out var a); stats.Add(a);
            Fh6Telemetry.TryRead(Packet(1100, 1, 1000, 20, 1), out var b); stats.Add(b);
            for (uint t = 1200; t <= 4000; t += 100)
            {
                var speed = 1 + (t - 1100) * (27f / 2900f);
                Fh6Telemetry.TryRead(Packet(t, speed, 1000, 20, 1), out var frame); stats.Add(frame);
            }
            Check(stats.BestZeroToHundredSeconds is > 2.7 and < 3.1, "应在有效数据中计算约 3 秒的零百");
            Check(stats.PeakSpeedKmh > 100, "最高速度应跟随采样更新");
            Fh6Telemetry.TryRead(Packet(4100, 0, 0, 0, 2), out var another); stats.Add(another);
            Check(stats.BestZeroToHundredSeconds is null && stats.PeakSpeedKmh == 0, "车型变化应清除先前成绩");
            Fh6Telemetry.TryRead(Packet(4200, 1, 0, 0, 2), out var start); stats.Add(start);
            Fh6Telemetry.TryRead(Packet(8000, 30, 0, 0, 2), out var gap); stats.Add(gap);
            Check(stats.BestZeroToHundredSeconds is null, "数据中断时不能伪造零百时间");
        });
    }
    private static byte[] Packet(uint timestamp, float speed, float power, float torque, int car)
    {
        var bytes = new byte[324]; BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(212), car);
        Float(bytes, 16, 3500); Float(bytes, 256, speed); Float(bytes, 260, power); Float(bytes, 264, torque);
        return bytes;
    }
    private static void Float(byte[] b, int offset, float value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .001, $"预期 {expected}，实际 {actual}");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
