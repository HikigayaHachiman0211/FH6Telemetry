// Adapted from LazyForza's ShiftPointCalculator and DashboardHudState (MIT).
// Copyright (c) 2026 Laz2y. See TelemetryApp/ThirdPartyNotices.txt.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FH6Telemetry;

internal readonly record struct ShiftCurveBin(int RpmCenter, double MedianTorqueNm);
internal readonly record struct ShiftGearModel(int Gear, double RpmPerMeterPerSecond);
internal sealed record LazyForzaShiftTarget(int FromGear, int ToGear, double TargetRpm, double CueRpm, double AfterShiftRpm);
internal readonly record struct LazyForzaRecommendation(int Gear, LazyForzaShiftTarget? Upshift, LazyForzaShiftTarget? Downshift);

internal static class LazyForzaShiftAdvisor
{
    public static LazyForzaRecommendation Recommend(IReadOnlyList<LazyForzaShiftTarget> targets, int gear,
        double rpm, double maxRpm)
    {
        LazyForzaShiftTarget? upshift = null, downshift = null;
        foreach (var target in targets)
        {
            if (target.FromGear == gear) upshift = target;
            if (target.ToGear == gear) downshift = target;
        }
        var recommendedGear = upshift is not null && rpm >= upshift.CueRpm && rpm <= maxRpm * 1.05
            ? upshift.ToGear
            : downshift is not null && rpm < downshift.AfterShiftRpm * 0.90 ? downshift.FromGear : gear;
        return new(recommendedGear, upshift, downshift);
    }

    public static IReadOnlyList<LazyForzaShiftTarget> Calculate(IReadOnlyList<ShiftCurveBin> curve,
        IReadOnlyList<ShiftGearModel> gears, double engineMaxRpm, double rpmRiseRate = 2400,
        double totalLatencySeconds = 0.16)
    {
        if (curve.Count < 2 || gears.Count < 2 || engineMaxRpm <= 0) return [];

        var orderedCurve = curve.OrderBy(bin => bin.RpmCenter).ToArray();
        var orderedGears = gears.OrderBy(gear => gear.Gear).ToArray();
        var results = new List<LazyForzaShiftTarget>(orderedGears.Length - 1);

        for (var index = 0; index < orderedGears.Length - 1; index++)
        {
            var current = orderedGears[index];
            var next = orderedGears[index + 1];
            if (next.Gear != current.Gear + 1 || current.RpmPerMeterPerSecond <= next.RpmPerMeterPerSecond)
                continue;

            var ratio = next.RpmPerMeterPerSecond / current.RpmPerMeterPerSecond;
            var minimum = Math.Max(orderedCurve[0].RpmCenter / ratio, orderedCurve[0].RpmCenter);
            var maximum = Math.Min(engineMaxRpm - 75, orderedCurve[^1].RpmCenter);
            double? crossing = null;
            var previousRpm = minimum;
            var previousDifference = Difference(previousRpm);

            for (var rpm = minimum + 25; rpm <= maximum; rpm += 25)
            {
                var difference = Difference(rpm);
                if (double.IsFinite(previousDifference) && double.IsFinite(difference) && previousDifference > 0 && difference <= 0)
                {
                    var fraction = previousDifference / (previousDifference - difference);
                    crossing = previousRpm + (rpm - previousRpm) * fraction;
                    break;
                }
                previousRpm = rpm;
                previousDifference = difference;
            }

            var targetRpm = crossing ?? Math.Max(minimum, Math.Min(engineMaxRpm - 100, maximum));
            var cueRpm = Math.Clamp(targetRpm - Math.Max(0, rpmRiseRate) * Math.Max(0, totalLatencySeconds), minimum, targetRpm);
            results.Add(new(current.Gear, next.Gear, targetRpm, cueRpm, targetRpm * ratio));

            double Difference(double rpm) =>
                InterpolateTorque(orderedCurve, rpm) * current.RpmPerMeterPerSecond -
                InterpolateTorque(orderedCurve, rpm * ratio) * next.RpmPerMeterPerSecond;
        }
        return results;
    }

    private static double InterpolateTorque(IReadOnlyList<ShiftCurveBin> curve, double rpm)
    {
        if (rpm < curve[0].RpmCenter || rpm > curve[^1].RpmCenter) return double.NaN;
        for (var index = 1; index < curve.Count; index++)
        {
            var upper = curve[index];
            if (rpm > upper.RpmCenter) continue;
            var lower = curve[index - 1];
            var span = upper.RpmCenter - lower.RpmCenter;
            if (span <= 0) return lower.MedianTorqueNm;
            return lower.MedianTorqueNm +
                ((rpm - lower.RpmCenter) / span) * (upper.MedianTorqueNm - lower.MedianTorqueNm);
        }
        return curve[^1].MedianTorqueNm;
    }
}
