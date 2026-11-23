using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal enum DriveAssistGuidanceSource { Route, Vision, Fused }

internal readonly record struct DriveAssistVisionCue(long ElapsedMs, double OffsetMetres, double Confidence);

internal readonly record struct DriveAssistRoutePoint(double X, double Y, double Z, double HeadingX, double HeadingZ,
    double SpeedKmh, double Brake);

internal readonly record struct DriveAssistRouteHint(DriveAssistGuidanceSource Source, double OffsetMetres,
    double Confidence, double? ReferenceSpeedKmh, double? DistanceToBrakeMetres);

internal sealed class DriveAssistRoute(DriveAssistRoutePoint[] points)
{
    internal const int PointLimit = 4096;
    internal const int VisionLimit = 60_000;
    public int PointCount => points.Length;

    public DriveAssistRouteHint? Guide(TelemetrySample sample, double headingX, double headingZ)
    {
        var best = -1;
        var closest = 18.0 * 18.0;
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (headingX * point.HeadingX + headingZ * point.HeadingZ < .65 || Math.Abs(sample.PositionY - point.Y) > 6)
                continue;
            var dx = sample.PositionX - point.X;
            var dz = sample.PositionZ - point.Z;
            var distance = dx * dx + dz * dz;
            if (distance >= closest) continue;
            best = i;
            closest = distance;
        }
        if (best < 0) return null;

        var reference = points[best];
        var offset = (sample.PositionX - reference.X) * reference.HeadingZ - (sample.PositionZ - reference.Z) * reference.HeadingX;
        var confidence = 1 - Math.Sqrt(closest) / 20;
        double? brakingAhead = null;
        var distanceAhead = 0.0;
        for (var i = best; i < points.Length && distanceAhead <= 100; i++)
        {
            if (i > best) distanceAhead += Distance(points[i - 1], points[i]);
            if (distanceAhead <= 100 && points[i].Brake >= .28) { brakingAhead = distanceAhead; break; }
        }
        return new(DriveAssistGuidanceSource.Route, offset, confidence, reference.SpeedKmh, brakingAhead);
    }

    public static DriveAssistRouteHint? Fuse(DriveAssistRouteHint? route, IReadOnlyList<DriveAssistVisionCue> vision, long elapsedMs)
    {
        if (vision.Count == 0 || elapsedMs < 0) return route;
        var left = 0;
        var right = vision.Count;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (vision[middle].ElapsedMs < elapsedMs) left = middle + 1;
            else right = middle;
        }
        var index = left >= vision.Count ? vision.Count - 1 : left;
        if (index > 0 && Math.Abs(vision[index - 1].ElapsedMs - elapsedMs) < Math.Abs(vision[index].ElapsedMs - elapsedMs)) index--;
        var cue = vision[index];
        var age = Math.Abs(cue.ElapsedMs - elapsedMs);
        if (age > 225 || cue.Confidence < .60) return route;
        var confidence = cue.Confidence * (1 - age / 450.0);
        if (route is not { } reference)
            return new(DriveAssistGuidanceSource.Vision, cue.OffsetMetres, confidence, null, null);
        if (Math.Abs(reference.OffsetMetres - cue.OffsetMetres) > 2.5) return route;
        var weight = confidence / (reference.Confidence + confidence);
        return reference with
        {
            Source = DriveAssistGuidanceSource.Fused,
            OffsetMetres = reference.OffsetMetres + weight * (cue.OffsetMetres - reference.OffsetMetres),
            Confidence = Math.Max(reference.Confidence, confidence),
        };
    }

    public static async Task<DriveAssistVisionCue[]> LoadVisionAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Vision CSV exceeds 4 MiB.");
        using var reader = new StreamReader(stream);
        var header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(header?.Trim(), "elapsedMs,offsetMetres,confidence", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Vision CSV header must be elapsedMs,offsetMetres,confidence.");

        var cues = new List<DriveAssistVisionCue>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 128 || cues.Count == VisionLimit)
                throw new InvalidDataException("Vision CSV exceeds the supported row size or count.");
            var cells = line.Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length != 3 || !long.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out var elapsed) ||
                !double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) ||
                !double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) ||
                elapsed is < 0 or > 86_400_000 || cues.Count > 0 && elapsed <= cues[^1].ElapsedMs ||
                !double.IsFinite(offset) || Math.Abs(offset) > 25 || !(confidence is >= 0 and <= 1))
                throw new InvalidDataException("Vision cues require strictly increasing elapsed milliseconds, offsets within ±25 m, and confidence from 0 to 1.");
            cues.Add(new(elapsed, offset, confidence));
        }
        if (cues.Count == 0) throw new InvalidDataException("Vision CSV contains no cues.");
        return cues.ToArray();
    }

    public static double Distance(DriveAssistRoutePoint first, DriveAssistRoutePoint second)
    {
        var x = first.X - second.X;
        var z = first.Z - second.Z;
        return Math.Sqrt(x * x + z * z);
    }
}
