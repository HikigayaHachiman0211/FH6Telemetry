using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal sealed record DriveAssistReplayFrame(long ElapsedMs, TelemetrySample Sample, ShiftGamepadReport? Physical,
    ShiftGamepadReport? RecordedOutput, ShiftGamepadReport? SimulatedOutput, DriveAssistFeature RecordedAssist,
    DriveAssistFeature SimulatedAssist, ShiftGamepadReport? RecordedProposal = null);

internal sealed class DriveAssistReplay
{
    private const int FrameLimit = 60_000;
    private const int ActionLimit = 180_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly List<DriveAssistReplayFrame> _frames;
    private readonly DriveAssistRoute? _route;

    private DriveAssistReplay(List<DriveAssistReplayFrame> frames, DriveAssistAnalysisSnapshot summary,
        DriveAssistRoute? route, bool truncated, int invalidLines, int simulatedFrames)
    {
        _frames = frames;
        Summary = summary;
        _route = route;
        IsTruncated = truncated;
        InvalidLines = invalidLines;
        SimulatedFrames = simulatedFrames;
    }

    public IReadOnlyList<DriveAssistReplayFrame> Frames => _frames;
    public DriveAssistAnalysisSnapshot Summary { get; }
    public bool IsTruncated { get; }
    public int InvalidLines { get; }
    public int SimulatedFrames { get; }
    public DriveAssistRoute? LearnRoute() => _route;
    public DriveAssistReplayFrame FrameAt(long elapsedMs) => _frames[FrameIndexAt(elapsedMs)];

    public int FrameIndexAt(long elapsedMs)
    {
        var left = 0;
        var right = _frames.Count;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (_frames[middle].ElapsedMs <= elapsedMs) left = middle + 1;
            else right = middle;
        }
        return Math.Max(0, left - 1);
    }

    public DriveAssistRouteHint? GuideAt(int frameIndex, DriveAssistRoute? route, IReadOnlyList<DriveAssistVisionCue> vision)
    {
        var index = Math.Clamp(frameIndex, 0, _frames.Count - 1);
        var frame = _frames[index];
        DriveAssistRouteHint? hint = null;
        if (index > 0 && route is not null && frame.Sample.SpeedKmh > 10 && _frames[index - 1] is { } previous &&
            frame.ElapsedMs - previous.ElapsedMs is > 0 and <= 300)
        {
            var dx = frame.Sample.PositionX - previous.Sample.PositionX;
            var dz = frame.Sample.PositionZ - previous.Sample.PositionZ;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance is >= .05 and <= 26) hint = route.Guide(frame.Sample, dx / distance, dz / distance);
        }
        return DriveAssistRoute.Fuse(hint, vision, frame.ElapsedMs);
    }

    public static async Task<DriveAssistReplay> LoadAsync(string folder, DriveAssistSettings settings, CancellationToken cancellationToken)
    {
        var actions = new Dictionary<uint, Queue<RecordedInput>>();
        var attached = false;
        var invalid = 0;
        var truncated = false;
        var injected = default(ShiftGamepadReport);
        var actionsPath = Path.Combine(folder, "shift-actions.jsonl");
        if (File.Exists(actionsPath))
        {
            using var stream = File.OpenRead(actionsPath);
            var seen = 0;
            await foreach (var line in ReadLinesAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (seen++ == ActionLimit) { truncated = true; break; }
                try
                {
                    var entry = JsonSerializer.Deserialize<ActionEntry>(line, Json);
                    if (entry is not { ElapsedMs: >= 0 and <= 86_400_000 }) { invalid++; continue; }
                    var trace = entry.Input;
                    if (trace.Action is "gamepad_apply" or "gamepad_release")
                        injected = new(trace.InjectedButtons ?? 0, trace.InjectedRightTrigger ?? 0);
                    if (trace.Action is not ("assist_frame" or "assist_preview") || trace.TelemetryTimestampMs is not uint timestamp) continue;
                    if (!actions.TryGetValue(timestamp, out var inputs)) actions[timestamp] = inputs = new();
                    var actual = trace.Action == "assist_frame";
                    inputs.Enqueue(new(entry.ElapsedMs, trace.PhysicalReport, actual ? trace.Report : null,
                        actual ? trace.AppliedAssist ?? 0 : 0, trace.AssistFresh != false, actual ? injected : default, trace.PlannedReport));
                }
                catch (JsonException) { invalid++; }
            }
        }

        var controller = new DriveAssistController(settings);
        var analysis = new DriveAssistAnalysis(timelineOrigin: 0);
        var frames = new List<DriveAssistReplayFrame>();
        var simulated = 0;
        using var telemetry = File.OpenRead(Path.Combine(folder, "telemetry.jsonl.gz"));
        using var gzip = new GZipStream(telemetry, CompressionMode.Decompress);
        var telemetrySeen = 0;
        await foreach (var line in ReadLinesAsync(gzip, cancellationToken).ConfigureAwait(false))
        {
            if (telemetrySeen++ == FrameLimit) { truncated = true; break; }
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("elapsedMs", out var time) ||
                    time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var elapsed) || elapsed is < 0 or > 86_400_000 ||
                    frames.Count > 0 && elapsed < frames[^1].ElapsedMs || !root.TryGetProperty("datagram", out var datagram) ||
                    datagram.ValueKind != JsonValueKind.String || !datagram.TryGetBytesFromBase64(out var packet) ||
                    packet is null || !Fh6Telemetry.TryRead(packet, out var sample))
                {
                    invalid++;
                    continue;
                }

                var input = FindInput(actions, sample.TimestampMs, elapsed);
                ShiftGamepadReport? preview = null;
                var previewAssist = DriveAssistFeature.None;
                if (input is { Fresh: true, Physical: { } physical })
                {
                    var pulse = (input.Value.Injected.Buttons & 0x6000) != 0;
                    var direction = (input.Value.Injected.Buttons & 0x2000) != 0 ? ShiftDirection.Up :
                        (input.Value.Injected.Buttons & 0x4000) != 0 ? ShiftDirection.Down : (ShiftDirection?)null;
                    controller.Feed(sample, physical, elapsed, pulse, direction);
                    if (controller.HasFreshData(elapsed))
                    {
                        preview = controller.Render(physical, physical.WithShift(input.Value.Injected), elapsed, pulse,
                            input.Value.Injected.RightTrigger);
                        previewAssist = controller.Active;
                        simulated++;
                    }
                    attached = true;
                }
                else if (attached && input is { Physical: { } transitionInput } && sample.Gear is 0 or 11 or byte.MaxValue)
                {
                    controller.Feed(sample, transitionInput, elapsed, shifting: true);
                }
                else if (attached)
                {
                    controller.Suspend();
                    attached = false;
                }
                var frame = new DriveAssistReplayFrame(elapsed, sample, input?.Physical, input?.Output, preview, input?.Applied ?? 0,
                    previewAssist, input?.Proposal);
                frames.Add(frame);
                analysis.Add(sample, frame.Physical, frame.RecordedOutput, frame.RecordedAssist, elapsed);
            }
            catch (JsonException) { invalid++; }
        }
        if (frames.Count == 0) throw new InvalidDataException("Recording has no valid FH6 telemetry.");
        analysis.LearnRoute();
        return new(frames, analysis.Snapshot(), analysis.Route, truncated, invalid, simulated);
    }

    private static RecordedInput? FindInput(Dictionary<uint, Queue<RecordedInput>> actions, uint timestamp, long elapsed)
    {
        if (!actions.TryGetValue(timestamp, out var inputs)) return null;
        while (inputs.Count > 0 && inputs.Peek().ElapsedMs < elapsed - 70) inputs.Dequeue();
        var matched = inputs.Count > 0 && inputs.Peek().ElapsedMs <= elapsed + 120 ? inputs.Dequeue() : (RecordedInput?)null;
        if (inputs.Count == 0) actions.Remove(timestamp);
        return matched;
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int maximumLineLength = 16_384;
        using var reader = new StreamReader(stream, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newline = Array.IndexOf(buffer, '\n', start, read - start);
                var end = newline < 0 ? read : newline;
                if (line.Length + end - start > maximumLineLength) throw new InvalidDataException("Recording line exceeds 16 Ki characters.");
                line.Append(buffer, start, end - start);
                if (newline < 0) break;
                yield return line.ToString();
                line.Clear();
                start = newline + 1;
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private sealed record ActionEntry(long ElapsedMs, ShiftTraceEvent Input);

    private readonly record struct RecordedInput(long ElapsedMs, ShiftGamepadReport? Physical, ShiftGamepadReport? Output,
        DriveAssistFeature Applied, bool Fresh, ShiftGamepadReport Injected, ShiftGamepadReport? Proposal);
}
