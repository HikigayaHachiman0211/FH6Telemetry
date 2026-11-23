using System;

namespace FH6Telemetry;

internal readonly record struct SpeedGearMatch(int Gear, int SpeedBandStart, int Rpm);

internal static class SpeedGearMatcher
{
    public const int BandKmh = 5;

    public static ShiftDecision Downshift(TelemetrySample sample, ReadOnlySpan<double> ratios,
        AdaptiveShiftStrategy engine, double speedAtEngagement, double hardLimit, bool braking, bool coasting,
        out int destination, out double destinationRpm, AdaptiveShiftStrategy.OutputBin[]? savedCurve = null,
        bool kickdown = false, DriverIntentSnapshot intent = default)
    {
        destination = 0;
        destinationRpm = 0;
        var currentRatio = ratios[sample.Gear];
        if (currentRatio == 0) currentRatio = sample.Rpm / sample.SpeedKmh;

        var (torquePeak, powerPeak) = engine.PeakFractions(savedCurve);
        var nominal = sample.EngineMaxRpm;
        var preferred = braking ? 0.94
            : intent.CornerExitWindow || intent.ThrottleStomp ? (powerPeak > 0 ? Math.Max(powerPeak, 0.78) : 0.78)
            : powerPeak > 0 ? powerPeak : 0.73;
        if (coasting) preferred = Math.Min(intent.RoadGrade < -0.045 ? 0.82 : 0.77, preferred < 0.77 && intent.RoadGrade < -0.045 ? 0.82 : preferred);
        var ceiling = braking ? hardLimit * (1 - Math.Min(0.07, Math.Abs(sample.AccelerationX) / 220))
            : hardLimit * (coasting ? (intent.RoadGrade < -0.045 ? 0.92 : 0.89) : 1);
        var targetRpm = Math.Min(nominal * preferred, ceiling * 0.985);
        var currentRpm = speedAtEngagement * currentRatio;
        var currentPower = braking || coasting ? 0 : engine.Estimate(currentRpm / nominal, savedCurve);
        if (kickdown && sample.Throttle >= 0.90 && double.IsFinite(sample.PowerKw) &&
            Math.Abs(currentRpm - sample.Rpm) <= nominal * 0.03)
            currentPower = Math.Max(currentPower, sample.PowerKw);
        var weakBase = Math.Max(nominal * Math.Clamp(torquePeak * 0.9, coasting ? 0.53 : 0.60, 0.68),
            sample.EngineIdleRpm * 1.7);
        var weak = currentRpm < weakBase + (!braking && !coasting && intent.RoadGrade > 0.05 ? nominal * 0.05 : 0);
        var minimumGain = nominal * (kickdown && currentRpm < nominal * 0.43 ? 0.015 : 0.045);
        var powerGainRatio = kickdown || intent.CornerExitWindow || intent.ThrottleStomp ? 1.06 : 1.12;
        var previousRatio = currentRatio;
        var best = 0.0;
        var next = sample.Gear - 1;
        var adjacentRpm = 0.0;
        for (var gear = next; gear >= 1; gear--)
        {
            var ratio = ratios[gear];
            if (ratio == 0) continue;
            if (ratio <= previousRatio || Math.Max(sample.SpeedKmh, speedAtEngagement) * ratio >= hardLimit) break;
            previousRatio = ratio;
            var rpm = speedAtEngagement * ratio;
            if (rpm > ceiling) break;
            var power = !braking && !coasting ? engine.Estimate(rpm / nominal, savedCurve) : 0;
            var improvement = Math.Abs(targetRpm - currentRpm) - Math.Abs(targetRpm - rpm);
            var score = 0.0;
            if (currentPower > 0 && power > 0)
            {
                if (power >= Math.Max(currentPower * powerGainRatio, currentPower + 4))
                    score = power / currentPower;
            }
            else if ((braking || weak) && improvement >= minimumGain)
                score = power > 0 ? 1 + power : improvement / nominal;
            if (score <= best) continue;

            // shortcut: infer missing steps between measured ratios, then verify each step until its own ratio is known.
            var adjacentRatio = ratios[next] > 0 ? ratios[next] : currentRatio * Math.Pow(ratio / currentRatio, 1.0 / (sample.Gear - gear));
            if (speedAtEngagement * adjacentRatio <= currentRpm * 1.015) continue;
            best = score;
            destination = gear;
            destinationRpm = rpm;
            adjacentRpm = speedAtEngagement * adjacentRatio;
        }

        return destination == 0 ? default : new(next,
            braking ? ShiftReason.SpeedMatchedBraking : coasting ? ShiftReason.SpeedMatchedCoast : ShiftReason.SpeedMatchedPower,
            LandingRpm: adjacentRpm);
    }

    public static ShiftDecision LearnLowerGear(TelemetrySample sample, ReadOnlySpan<double> ratios,
        double speedAtEngagement, double hardLimit, double maxRpmFraction = 0.58)
    {
        var gear = sample.Gear;
        if (gear <= 2 || ratios[gear - 1] > 0) return default;

        var roadRatio = ratios[gear];
        var conservativeRatio = Math.Max(roadRatio, sample.Rpm / sample.SpeedKmh);
        var speedForSafety = Math.Max(sample.SpeedKmh, speedAtEngagement);
        var rpmInCurrentGear = speedForSafety * conservativeRatio;
        if (rpmInCurrentGear >= sample.EngineMaxRpm * maxRpmFraction) return default;

        var upperRatio = 0.0;
        var upperStepOffset = 1;
        for (var k = gear + 1; k < ratios.Length; k++)
        {
            if (ratios[k] > 0)
            {
                upperRatio = ratios[k];
                upperStepOffset = k - gear;
                break;
            }
        }
        var knownStep = upperRatio > 0 && roadRatio > 0 && roadRatio > upperRatio
            ? (upperStepOffset == 1 ? roadRatio / upperRatio : Math.Pow(roadRatio / upperRatio, 1.0 / upperStepOffset))
            : 0;
        if (knownStep >= 2.0) return default;

        // shortcut: bound a single unknown step conservatively until telemetry measures the resulting ratio.
        var assumedStep = knownStep > 1 ? knownStep : 1.25;
        var upperStep = knownStep > 1
            ? (upperStepOffset == 1 ? Math.Max(1.65, 1.45 * knownStep) : Math.Max(1.85, 1.55 * knownStep))
            : 2.5;
        if (upperStepOffset == 1 && knownStep > 1 && gear + 2 < ratios.Length && ratios[gear + 2] > 0)
        {
            var earlierStep = upperRatio / ratios[gear + 2];
            if (earlierStep > 1 && Math.Abs(earlierStep / knownStep - 1) < 0.12)
                upperStep = Math.Max(1.60, 1.30 * Math.Max(knownStep, earlierStep));
        }
        if (rpmInCurrentGear * upperStep >= hardLimit) return default;
        return new(gear - 1, ShiftReason.SpeedMatchedLearning, LandingRpm: speedAtEngagement * conservativeRatio * assumedStep);
    }
}
