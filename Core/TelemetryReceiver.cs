using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

/// <summary>UDP 接收与诊断：后台任务独立于 UI 线程，所有异常都在内部消化并计入诊断。</summary>
public sealed class TelemetryReceiver : IDisposable
{
    private readonly TelemetrySettings _settings;
    private CancellationTokenSource? _cancellation;
    private UdpClient? _socket;
    private readonly object _sync = new();

    public long PacketsReceived { get; private set; }
    public long PacketsAccepted { get; private set; }
    public long ParseFailures { get; private set; }
    public long WrongSource { get; private set; }
    public int LastPacketLength { get; private set; }
    public DateTime LastPacketUtc { get; private set; } = DateTime.MinValue;
    public DateTime StartedUtc { get; private set; }
    public string ListenEndpoint { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public bool IsRunning => Volatile.Read(ref _socket) is not null;
    public double AveragePacketsPerSecond { get; private set; }

    /// <summary>新采样回调：在接收线程上调用，订阅方不得执行阻塞操作。</summary>
    public event Action<TelemetrySample>? SampleReceived;
    internal event Action<byte[], TelemetrySample?, long>? DatagramReceived;

    public TelemetryReceiver(TelemetrySettings settings) { _settings = settings; }

    public bool Start(out string error)
    {
        error = "";
        lock (_sync)
        {
            if (IsRunning) return true;
            if (!IPAddress.TryParse(_settings.ListenAddress, out var address))
            {
                error = TelemetryText.Format("RecvInvalidIp", _settings.ListenAddress);
                return false;
            }
            if (_settings.Port is < 1024 or > 65535)
            {
                error = TelemetryText.Get("RecvInvalidPort");
                return false;
            }
            UdpClient? socket = null;
            try
            {
                socket = new UdpClient(address.AddressFamily);
                socket.ExclusiveAddressUse = true;
                socket.Client.Bind(new IPEndPoint(address, _settings.Port));
                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;
                StartedUtc = DateTime.UtcNow;
                ListenEndpoint = $"{_settings.ListenAddress}:{_settings.Port}";
                LastError = "";
                _socket = socket;
                _ = Task.Run(() => ReceiveLoop(socket, token));
                return true;
            }
            catch (Exception ex)
            {
                _socket = null;
                _cancellation?.Dispose();
                _cancellation = null;
                socket?.Dispose();
                error = TelemetryText.Format("RecvListenFailed", _settings.ListenAddress, _settings.Port, ex.Message);
                LastError = error;
                return false;
            }
        }
    }

    private async Task ReceiveLoop(UdpClient socket, CancellationToken token)
    {
        using (socket)
        {
            var windowStart = DateTime.UtcNow;
            long windowPackets = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { continue; }
                    catch (ObjectDisposedException) { break; }

                    lock (_sync)
                    {
                        if (_socket != socket) break;
                        PacketsReceived++;
                        windowPackets++;
                        LastPacketLength = result.Buffer.Length;
                    }
                    if (!IPAddress.IsLoopback(result.RemoteEndPoint.Address))
                    {
                        lock (_sync) WrongSource++;
                        continue;
                    }
                    var decoded = Fh6Telemetry.TryRead(result.Buffer, out var sample);
                    DatagramReceived?.Invoke(result.Buffer, decoded ? sample : null, Environment.TickCount64);
                    if (!decoded)
                    {
                        lock (_sync) ParseFailures++;
                        continue;
                    }
                    lock (_sync)
                    {
                        if (_socket != socket) break;
                        PacketsAccepted++;
                        LastPacketUtc = DateTime.UtcNow;
                        var seconds = (DateTime.UtcNow - windowStart).TotalSeconds;
                        if (seconds >= 1.0)
                        {
                            AveragePacketsPerSecond = windowPackets / seconds;
                            windowStart = DateTime.UtcNow;
                            windowPackets = 0;
                        }
                    }
                    if (token.IsCancellationRequested) break;
                    try { SampleReceived?.Invoke(sample!); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SAMPLE-HANDLER-ERROR: " + ex); Console.WriteLine("SAMPLE-HANDLER-ERROR: " + ex); }
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                    if (_socket == socket) LastError = TelemetryText.Format("RecvThreadException", ex.Message);
            }
            finally
            {
                lock (_sync)
                {
                    if (_socket == socket)
                    {
                        socket.Dispose();
                        _socket = null;
                        _cancellation?.Dispose();
                        _cancellation = null;
                    }
                }
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            var socket = _socket;
            _socket = null;
            _cancellation?.Cancel();
            socket?.Dispose();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public bool IsTimedOut => IsRunning && LastPacketUtc != DateTime.MinValue &&
                              (DateTime.UtcNow - LastPacketUtc).TotalMilliseconds > _settings.TimeoutMs;

    public bool HasEverReceived => LastPacketUtc != DateTime.MinValue;

    public void ResetCounters()
    {
        lock (_sync)
        {
            PacketsReceived = PacketsAccepted = ParseFailures = WrongSource = 0;
            AveragePacketsPerSecond = 0;
            LastPacketLength = 0;
            LastPacketUtc = DateTime.MinValue;
            StartedUtc = DateTime.UtcNow;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// 有界环形缓冲：实时曲线只保留窗口内的点，内存占用固定，长时间运行不增长。
/// </summary>
public sealed class TelemetryRingBuffer
{
    private readonly int _capacity;
    private readonly double[] _timestamps;
    private readonly double[] _speed;
    private readonly double[] _rpm;
    private readonly double[] _power;
    private readonly double[] _torque;
    private readonly double[] _longitudinalG;
    private readonly double[] _lateralG;
    private readonly double[] _throttle;
    private readonly double[] _brake;
    private readonly double[] _steer;
    private readonly int[] _gear;
    private int _start;
    private int _count;

    public TelemetryRingBuffer(int capacity = 3600)
    {
        _capacity = Math.Max(60, capacity);
        _timestamps = new double[_capacity];
        _speed = new double[_capacity];
        _rpm = new double[_capacity];
        _power = new double[_capacity];
        _torque = new double[_capacity];
        _longitudinalG = new double[_capacity];
        _lateralG = new double[_capacity];
        _throttle = new double[_capacity];
        _brake = new double[_capacity];
        _steer = new double[_capacity];
        _gear = new int[_capacity];
    }

    public int Count => _count;
    public void Clear() { _start = 0; _count = 0; }

    public void Trim(double cutoff, int maximumFrames)
    {
        var remove = Math.Max(FindFirst(cutoff), Math.Max(0, _count - Math.Max(0, maximumFrames)));
        _start = (_start + remove) % _capacity;
        _count -= remove;
    }

    public void Add(double timeSeconds, TelemetrySample sample, TelemetryDerived derived)
    {
        var index = (_start + _count) % _capacity;
        if (_count == _capacity) { _start = (_start + 1) % _capacity; index = (_start + _count - 1) % _capacity; }
        else _count++;
        _timestamps[index] = timeSeconds;
        _speed[index] = sample.SpeedKmh;
        _rpm[index] = sample.Rpm;
        _power[index] = sample.PowerKw;
        _torque[index] = sample.TorqueNm;
        _longitudinalG[index] = derived.SmoothedLongitudinalG;
        _lateralG[index] = derived.SmoothedLateralG;
        _throttle[index] = sample.Throttle;
        _brake[index] = sample.Brake;
        _steer[index] = sample.Steer;
        _gear[index] = sample.Gear;
    }

    public enum Channel { Speed, Rpm, Power, Torque, LongitudinalG, LateralG, Throttle, Brake, Steer, Gear }

    public double[] Snapshot(Channel channel, out double[] times)
    {
        var (timestamps, values) = ReadRange(channel, 0, int.MaxValue);
        times = timestamps;
        return values;
    }

    /// <summary>按时间窗口裁剪（秒），用于滚动显示。</summary>
    public (double[] Times, double[] Values) Window(Channel channel, double windowSeconds, int maxPoints = int.MaxValue)
    {
        if (_count == 0) return ([], []);
        return ReadRange(channel, FindFirst(TimeAt(_count - 1) - windowSeconds), Math.Max(4, maxPoints));
    }

    internal (double[] Times, double[] Values) GearWindow(double windowSeconds, int maxPoints)
    {
        if (_count == 0) return ([], []);
        var first = FindFirst(TimeAt(_count - 1) - windowSeconds);
        var times = new double[Math.Min(_count - first, maxPoints)];
        var values = new double[times.Length];
        var written = 0;
        for (var point = first; point < _count; point++)
        {
            var gear = ChartGear(ValueAt(Channel.Gear, point));
            if (written > 0 && point < _count - 1 && values[written - 1] == gear) continue;
            if (written == times.Length)
            {
                (times, values) = ReadRange(Channel.Gear, first, maxPoints);
                for (var index = 0; index < values.Length; index++) values[index] = ChartGear(values[index]);
                return (times, values);
            }
            times[written] = TimeAt(point);
            values[written++] = gear;
        }
        return written == times.Length ? (times, values) : (times[..written], values[..written]);
    }

    private static double ChartGear(double gear) => gear is >= 0 and <= AutomaticShiftSettings.MaximumForwardGears ? gear : -1;

    private (double[] Times, double[] Values) ReadRange(Channel channel, int first, int maxPoints)
    {
        var count = _count - first;
        var times = new double[Math.Min(count, maxPoints)];
        var values = new double[times.Length];
        var written = 0;
        void Write(int point)
        {
            times[written] = TimeAt(point);
            values[written++] = ValueAt(channel, point);
        }

        if (count <= maxPoints)
        {
            for (var point = first; point < _count; point++) Write(point);
            return (times, values);
        }

        // Keep the min and max in each time-ordered bucket so short power drops stay visible.
        Write(first);
        var buckets = (maxPoints - 2) / 2;
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = first + 1 + (int)((long)(count - 2) * bucket / buckets);
            var end = first + 1 + (int)((long)(count - 2) * (bucket + 1) / buckets);
            var low = start;
            var high = start;
            var min = ValueAt(channel, start);
            var max = min;
            for (var point = start + 1; point < end; point++)
            {
                var value = ValueAt(channel, point);
                if (value < min) { min = value; low = point; }
                if (value > max) { max = value; high = point; }
            }
            Write(Math.Min(low, high));
            if (low != high) Write(Math.Max(low, high));
        }
        Write(_count - 1);
        return written == times.Length ? (times, values) : (times[..written], values[..written]);
    }

    private double TimeAt(int point) => _timestamps[(_start + point) % _capacity];

    private int FindFirst(double cutoff)
    {
        var lo = 0;
        var hi = _count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (TimeAt(mid) < cutoff) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private double ValueAt(Channel channel, int point)
    {
        var index = (_start + point) % _capacity;
        return channel switch
        {
            Channel.Speed => _speed[index], Channel.Rpm => _rpm[index], Channel.Power => _power[index],
            Channel.Torque => _torque[index], Channel.LongitudinalG => _longitudinalG[index], Channel.LateralG => _lateralG[index],
            Channel.Throttle => _throttle[index], Channel.Brake => _brake[index], Channel.Steer => _steer[index], _ => _gear[index],
        };
    }
}
