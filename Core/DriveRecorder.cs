using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal readonly record struct ShiftTraceEvent(long AtMs, string Action, int? Gear = null, int? TargetGear = null,
    ShiftReason? Reason = null, ShiftAlgorithm? Algorithm = null, VirtualTcuMode? DrivingMode = null,
    AutomaticShiftState? State = null, GamepadShiftMode? OutputMode = null, double? LandingRpm = null,
    ushort? InjectedButtons = null, byte? InjectedRightTrigger = null, ShiftGamepadReport? Report = null,
    string? VirtualKeysHex = null, int? AcceptedKeys = null, string? Detail = null, AutomaticShiftSettings? Settings = null,
    double? DriverThrottle = null, double? DriverBrake = null, bool? UsedClutch = null,
    uint? TelemetryTimestampMs = null, ShiftGamepadReport? PhysicalReport = null, DriveAssistFeature? AppliedAssist = null,
    bool? AssistFresh = null, ShiftGamepadReport? PlannedReport = null);

internal sealed class DriveRecorder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly long _startMs = Environment.TickCount64;
    private readonly AutomaticShiftSettings _initialSettings;
    private readonly DriveAssistSettings? _initialDriveAssist;
    private readonly Task _writer;
    private int _accepting = 1;
    private long _stoppedAtMs;
    private long _frames;
    private long _actions;
    private string? _error;

    public string Folder { get; }
    public bool IsAccepting => Volatile.Read(ref _accepting) != 0;
    public long Frames => Interlocked.Read(ref _frames);
    public long Actions => Interlocked.Read(ref _actions);
    public long DurationMs => RelativeTime(IsAccepting ? Environment.TickCount64 : Interlocked.Read(ref _stoppedAtMs));
    public string? Error => Volatile.Read(ref _error);

    public DriveRecorder(string root, AutomaticShiftSettings settings, DriveAssistSettings? driveAssist = null)
    {
        _initialSettings = settings;
        _initialDriveAssist = driveAssist;
        var name = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N")[..8];
        Folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        WriteSummary("recording");
        _writer = Task.Run(WriteAsync);
    }

    public void RecordTelemetry(byte[] datagram, TelemetrySample? decoded, long arrivalMs) =>
        Enqueue(new(arrivalMs, datagram, decoded, null));

    public void RecordShift(ShiftTraceEvent action) => Enqueue(new(action.AtMs, null, null, action));

    public Task StopAsync()
    {
        Complete();
        return _writer;
    }

    private long RelativeTime(long time) => Math.Max(0, time - _startMs);

    private void Enqueue(Entry entry)
    {
        if (entry.AtMs < _startMs || !IsAccepting || _queue.Writer.TryWrite(entry)) return;
        if (IsAccepting) Complete(TelemetryText.Get("RecDiskFull"));
    }

    private void Complete(string? error = null)
    {
        if (error is not null) Interlocked.CompareExchange(ref _error, error, null);
        if (Interlocked.Exchange(ref _accepting, 0) == 0) return;
        Interlocked.Exchange(ref _stoppedAtMs, Environment.TickCount64);
        _queue.Writer.TryComplete();
    }

    private async Task WriteAsync()
    {
        try
        {
            using var telemetry = new BufferedStream(new GZipStream(File.Create(Path.Combine(Folder, "telemetry.jsonl.gz")),
                CompressionLevel.Fastest), bufferSize: 64 * 1024);
            using var shifts = new BufferedStream(File.Create(Path.Combine(Folder, "shift-actions.jsonl")), bufferSize: 16 * 1024);
            var buffer = new ArrayBufferWriter<byte>(8 * 1024);
            using var writer = new Utf8JsonWriter(buffer);
            await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var elapsedMs = RelativeTime(entry.AtMs);
                if (entry.Datagram is { } datagram)
                {
                    WriteLine(telemetry, new { elapsedMs, datagram, sample = entry.Sample });
                    Interlocked.Increment(ref _frames);
                }
                else if (entry.Shift is { } action)
                {
                    WriteLine(shifts, new { elapsedMs, input = action });
                    Interlocked.Increment(ref _actions);
                }
            }

            void WriteLine<T>(Stream stream, T entry)
            {
                buffer.ResetWrittenCount();
                writer.Reset();
                JsonSerializer.Serialize(writer, entry, Json);
                writer.Flush();
                stream.Write(buffer.WrittenSpan);
                stream.WriteByte((byte)'\n');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Complete(TelemetryText.Format("RecSaveDataFailed", ex.Message));
        }

        try { WriteSummary(Error is null ? "complete" : "incomplete"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Complete(TelemetryText.Format("RecSaveSummaryFailed", ex.Message));
        }
    }

    private void WriteSummary(string state)
    {
        var temporary = Path.Combine(Folder, "session.json.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                version = 1, state, startedUtc = _startedUtc, durationMs = DurationMs, telemetryFrames = Frames, actions = Actions,
                error = Error, automaticShift = _initialSettings, driveAssist = _initialDriveAssist,
            }, Json));
            File.Move(temporary, Path.Combine(Folder, "session.json"), overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private readonly record struct Entry(long AtMs, byte[]? Datagram, TelemetrySample? Sample, ShiftTraceEvent? Shift);
}
