using System;
using System.Collections.Generic;
using System.Linq;

namespace FH6Telemetry;

public enum TelemetryRetentionPreset { ExtremePerformance, Performance, Balanced, DataDetail, Custom }

public sealed record TelemetryRetention
{
    public const int MaximumSeconds = 600;
    public const int MaximumFrames = 60_000;

    public TelemetryRetentionPreset Preset { get; init; } = TelemetryRetentionPreset.Balanced;
    public int Seconds { get; init; } = 90;
    public int Frames { get; init; } = 6000;
    public int CleanupIntervalSeconds { get; init; } = 60;
    public bool ClearCache { get; init; }
    public bool CleanHistory { get; init; }
    public int HistoryDays { get; init; } = 30;

    public static (int Seconds, int Frames) Limits(TelemetryRetentionPreset preset) => preset switch
    {
        TelemetryRetentionPreset.ExtremePerformance => (15, 600),
        TelemetryRetentionPreset.Performance => (30, 1800),
        TelemetryRetentionPreset.DataDetail => (300, 24000),
        _ => (90, 6000),
    };

    internal int ChartPointLimit => Preset switch
    {
        TelemetryRetentionPreset.ExtremePerformance => 400,
        TelemetryRetentionPreset.Performance => 700,
        TelemetryRetentionPreset.Balanced => 1000,
        _ => 1600,
    };
}

internal sealed class TelemetrySampleBuffer
{
    private readonly Queue<(long ReceivedAt, TelemetrySample Sample)> _samples = new();

    public void Add(TelemetrySample sample, long now, TelemetryRetention retention)
    {
        _samples.Enqueue((now, sample));
        Trim(now, retention);
    }

    public void Trim(long now, TelemetryRetention retention)
    {
        var oldest = now - retention.Seconds * 1000L;
        while (_samples.TryPeek(out var first) && (_samples.Count > retention.Frames || first.ReceivedAt < oldest))
            _samples.Dequeue();
    }

    public TelemetrySample[] Snapshot() => _samples.Select(entry => entry.Sample).ToArray();
    public void Clear() => _samples.Clear();
}
