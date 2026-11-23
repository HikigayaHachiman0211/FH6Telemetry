using System;
using System.Collections.Generic;
using System.Linq;

namespace FH6Telemetry;

public readonly record struct DriveAssistGPoint(double LateralG, double LongitudinalG);

internal readonly record struct DriveAssistWheelReading(double? LongitudinalSlip, double? CombinedSlip, double? TemperatureC,
    bool InContact, bool Spinning, bool Locking);

internal sealed record DriveAssistAnalysisFrame(long ElapsedMs, double SpeedKmh, ShiftGamepadReport? Physical,
    ShiftGamepadReport? Output, DriveAssistFeature Applied, double? SideslipDegrees, IReadOnlyList<DriveAssistWheelReading> Wheels);

[Flags]
internal enum DriveAssistLossKind { None = 0, Sideslip = 1, WheelSpin = 2, WheelLock = 4 }

internal readonly record struct DriveAssistLossEvent(long StartMs, long EndMs, long PeakMs, double PeakSideslipDegrees,
    DriveAssistLossKind Kind, DriveAssistFeature Applied);

internal readonly record struct DriveAssistCorner(int Number, int LapNumber, int Direction, long StartMs, long DurationMs,
    double EntryKmh, double MinimumKmh, double ExitKmh, double PeakLateralG, DriveAssistFeature Applied);

internal sealed record DriveAssistLapAnalysis(int LapNumber, double Seconds, bool FullLap, IReadOnlyList<DriveAssistCorner> Corners);

internal enum DriveAssistCoachingKind { PowerDuringSlide, BrakeWhileTurning, SteeringReversal, HeavyBrakeLock }

internal readonly record struct DriveAssistCoachingTip(DriveAssistCoachingKind Kind, int Samples, bool UsesGameInput);

internal readonly record struct DriveAssistResponse(double? SteeringMilliseconds, int SteeringEvidence,
    double? ThrottleMilliseconds, int ThrottleEvidence, double? BrakeMilliseconds, int BrakeEvidence);

internal sealed record DriveAssistAnalysisSnapshot(DriveAssistAnalysisFrame? Latest, IReadOnlyList<DriveAssistGPoint> GPoints,
    IReadOnlyList<DriveAssistGPoint> GripEnvelope, IReadOnlyList<DriveAssistLossEvent> Events, IReadOnlyList<DriveAssistCorner> Corners,
    DriveAssistLapAnalysis? PreviousLap, IReadOnlyList<DriveAssistCoachingTip> Coaching, DriveAssistResponse Response,
    DriveAssistRouteHint? RouteHint);

internal sealed class DriveAssistAnalysis(long? timelineOrigin = null)
{
    private const int GapMs = 300;
    private const int EventContextMs = 1500;
    private readonly object _sync = new();
    private readonly Queue<(DriveAssistGPoint Point, bool Contact)> _gPoints = new(360);
    private readonly Queue<DriveAssistLossEvent> _events = new(36);
    private readonly Queue<DriveAssistCorner> _corners = new(40);
    private readonly List<DriveAssistRoutePoint> _routeDraft = new();
    private readonly InputResponse _steering = new(), _throttle = new(), _brake = new();
    private readonly int[] _coachingCounts = new int[Enum.GetValues<DriveAssistCoachingKind>().Length];
    private readonly bool[] _coachingGameInput = new bool[Enum.GetValues<DriveAssistCoachingKind>().Length];
    private TelemetrySample? _sample;
    private ShiftGamepadReport? _physical, _output;
    private DriveAssistFeature _applied;
    private readonly long? _timelineOrigin = timelineOrigin;
    private long? _origin = timelineOrigin;
    private long _elapsed, _lastG, _lapStart;
    private int _cornerCount;
    private bool _lapStartObserved;
    private double _headingX, _headingZ;
    private double? _beta;
    private Loss? _loss;
    private Corner? _corner;
    private DriveAssistLapAnalysis? _previousLap;
    private DriveAssistRoute? _route, _previousLapRoute;
    private IReadOnlyList<DriveAssistVisionCue> _vision = Array.Empty<DriveAssistVisionCue>();

    public void Add(TelemetrySample sample, ShiftGamepadReport? physical, ShiftGamepadReport? output, DriveAssistFeature applied, long now)
    {
        lock (_sync)
        {
            var previous = _sample;
            if (!sample.IsRaceOn || sample.CarOrdinal <= 0 || !double.IsFinite(sample.SpeedKmh) || sample.SpeedKmh < 0)
            {
                BreakSequence();
                _sample = null;
                return;
            }
            _origin ??= now;
            var elapsed = Math.Max(0, now - _origin.Value);
            if (elapsed < _elapsed) return;
            var step = previous is null ? 0 : unchecked((int)(sample.TimestampMs - previous.TimestampMs));
            if (previous is not null && step == 0 && elapsed - _elapsed <= GapMs) return;

            var split = previous is null || step is <= 0 or > GapMs || elapsed - _elapsed > GapMs ||
                previous.CarOrdinal != sample.CarOrdinal || previous.CarPerformanceIndex != sample.CarPerformanceIndex ||
                PositionChangedAbruptly(previous, sample, elapsed - _elapsed);
            if (split)
            {
                BreakSequence();
                _lapStart = elapsed;
                _cornerCount = 0;
                _lapStartObserved = sample.RacePosition > 0 && sample.CurrentLapSeconds is >= 0 and < 1.5;
                previous = null;
            }
            else if (previous is { RacePosition: > 0 } && sample.RacePosition > 0 && sample.LapNumber == previous.LapNumber + 1)
            {
                FinishCorner(_elapsed);
                var seconds = sample.LastLapSeconds is > 0 and < 1200 ? sample.LastLapSeconds : (elapsed - _lapStart) / 1000.0;
                _previousLap = new(previous.LapNumber, seconds, _lapStartObserved, _corners.Where(c => c.StartMs >= _lapStart).ToArray());
                _previousLapRoute = _lapStartObserved ? BuildRoute() : null;
                _routeDraft.Clear();
                _lapStart = elapsed;
                _cornerCount = 0;
                _lapStartObserved = true;
            }

            var oldPhysical = split ? null : _physical;
            _sample = sample;
            _elapsed = elapsed;
            _physical = physical;
            _output = output;
            _applied = applied;
            _beta = VehicleSideSlip.Angle(sample) * 180 / Math.PI;
            var lateralG = sample.AccelerationX / TelemetryDerived.Gravity;
            var longitudinalG = sample.AccelerationZ / TelemetryDerived.Gravity;
            var kind = DriveAssistLossKind.None;
            var contacting = 0;
            for (var i = 0; i < 4; i++)
            {
                var wheel = sample.Wheel(i);
                if (wheel.NormalizedSuspensionTravel <= .08 || !double.IsFinite(wheel.NormalizedSuspensionTravel)) continue;
                contacting++;
                if (Math.Abs(wheel.RotationSpeedRadPerSec) > 4 && wheel.SlipRatio >= 1.5) kind |= DriveAssistLossKind.WheelSpin;
                if (wheel.SlipRatio <= -1.25) kind |= DriveAssistLossKind.WheelLock;
            }
            if (Math.Abs(_beta ?? 0) >= (applied.HasFlag(DriveAssistFeature.DriftHold) ? 39 : 23) &&
                Math.Abs(sample.AngularVelocityY) >= .40) kind |= DriveAssistLossKind.Sideslip;
            if (sample.SpeedKmh < 18 || sample.Gear == 0) kind = DriveAssistLossKind.None;
            ObserveLoss(kind);
            ObserveCorner(sample, lateralG);
            ObserveCoaching(sample, physical, oldPhysical, kind, lateralG);
            ObserveOutput(sample, output);

            if (Math.Abs(lateralG) <= 5 && Math.Abs(longitudinalG) <= 5 && sample.SpeedKmh >= 5 &&
                (_gPoints.Count == 0 || elapsed - _lastG >= 65))
            {
                Enqueue(_gPoints, (new(lateralG, longitudinalG), contacting >= 3), 360);
                _lastG = elapsed;
            }
            if (previous is not null) ObserveRoute(previous, sample);
        }
    }

    public DriveAssistAnalysisSnapshot Snapshot()
    {
        lock (_sync)
        {
            DriveAssistAnalysisFrame? frame = null;
            DriveAssistRouteHint? hint = null;
            if (_sample is { } sample)
            {
                var wheels = new DriveAssistWheelReading[4];
                for (var i = 0; i < 4; i++) wheels[i] = ReadWheel(sample.Wheel(i));
                frame = new(_elapsed, sample.SpeedKmh, _physical, _output, _applied, _beta, wheels);
                if (sample.SpeedKmh > 10 && (_headingX != 0 || _headingZ != 0))
                    hint = DriveAssistRoute.Fuse(_route?.Guide(sample, _headingX, _headingZ), _vision, _elapsed);
            }
            var events = _events.ToList();
            if (_loss is { } loss && loss.LastMs - loss.FromMs >= 100) events.Add(Slice(loss));
            var coaching = new List<DriveAssistCoachingTip>();
            for (var i = 0; i < _coachingCounts.Length; i++)
                if (_coachingCounts[i] > 1) coaching.Add(new((DriveAssistCoachingKind)i, _coachingCounts[i], _coachingGameInput[i]));
            return new(frame, _gPoints.Select(g => g.Point).ToArray(), Envelope(), events.ToArray(), _corners.ToArray(),
                _previousLap, coaching.ToArray(), new(_steering.Delay, _steering.Evidence, _throttle.Delay, _throttle.Evidence,
                    _brake.Delay, _brake.Evidence), hint);
        }
    }

    public bool LearnRoute()
    {
        lock (_sync)
        {
            var route = _previousLapRoute ?? BuildRoute();
            if (route is null) return false;
            _route = route;
            return true;
        }
    }

    public DriveAssistRoute? Route { get { lock (_sync) return _route; } }
    public void SetRoute(DriveAssistRoute? route) { lock (_sync) _route = route; }
    public void SetVision(DriveAssistVisionCue[] cues) { lock (_sync) _vision = cues; }

    public void Reset()
    {
        lock (_sync)
        {
            BreakSequence();
            _events.Clear();
            _gPoints.Clear();
            _corners.Clear();
            _previousLap = null;
            _previousLapRoute = null;
            _sample = null;
            _origin = _timelineOrigin;
            _elapsed = _lastG = 0;
            Array.Clear(_coachingCounts);
            Array.Clear(_coachingGameInput);
            _steering.Reset(clearEvidence: true);
            _throttle.Reset(clearEvidence: true);
            _brake.Reset(clearEvidence: true);
        }
    }

    private void BreakSequence()
    {
        FinishCorner(_elapsed);
        FinishLoss();
        _routeDraft.Clear();
        _headingX = _headingZ = 0;
        _steering.Reset();
        _throttle.Reset();
        _brake.Reset();
    }

    private void ObserveLoss(DriveAssistLossKind kind)
    {
        if (kind == DriveAssistLossKind.None)
        {
            if (_loss is { } old && _elapsed - old.LastMs > 200) FinishLoss();
            return;
        }
        var severity = Math.Abs(_beta ?? 0);
        var loss = _loss ?? new(_elapsed, _elapsed, _elapsed, severity, kind, _applied);
        _loss = loss with
        {
            LastMs = _elapsed, Kind = loss.Kind | kind, Applied = loss.Applied | _applied,
            PeakMs = severity > loss.Severity ? _elapsed : loss.PeakMs,
            Severity = Math.Max(loss.Severity, severity),
        };
    }

    private void FinishLoss()
    {
        if (_loss is { } loss && loss.LastMs - loss.FromMs >= 100) Enqueue(_events, Slice(loss), 36);
        _loss = null;
    }

    private static DriveAssistLossEvent Slice(Loss loss) => new(Math.Max(0, loss.FromMs - EventContextMs),
        loss.LastMs + EventContextMs, loss.PeakMs, loss.Severity, loss.Kind, loss.Applied);

    private void ObserveCorner(TelemetrySample sample, double lateralG)
    {
        var magnitude = Math.Abs(lateralG);
        var inCorner = sample.SpeedKmh > 20 && magnitude is >= .28 and <= 5 && Math.Abs(sample.AngularVelocityY) >= .13;
        var direction = inCorner ? Math.Sign(lateralG) : 0;
        if (_corner is { } corner && (direction != 0 && direction != corner.Direction || _elapsed - corner.LastMs > 280))
            FinishCorner(corner.LastMs);
        if (!inCorner) return;
        var active = _corner ?? new(_elapsed, _elapsed, direction, sample.RacePosition > 0 ? sample.LapNumber : -1,
            sample.SpeedKmh, sample.SpeedKmh, sample.SpeedKmh, magnitude, 0);
        _corner = active with { LastMs = _elapsed, Minimum = Math.Min(sample.SpeedKmh, active.Minimum), Exit = sample.SpeedKmh,
            PeakG = Math.Max(magnitude, active.PeakG), Applied = active.Applied | _applied };
    }

    private void FinishCorner(long elapsed)
    {
        if (_corner is { } active && active.LastMs - active.FromMs >= 450)
            Enqueue(_corners, new(++_cornerCount, active.Lap, active.Direction, active.FromMs, elapsed - active.FromMs,
                active.Entry, active.Minimum, active.Exit, active.PeakG, active.Applied), 40);
        _corner = null;
    }

    private void ObserveCoaching(TelemetrySample sample, ShiftGamepadReport? physical, ShiftGamepadReport? prior,
        DriveAssistLossKind loss, double lateralG)
    {
        var gas = physical is { } input ? input.RightTrigger / 255.0 : sample.Throttle;
        var brake = physical is { } controls ? controls.LeftTrigger / 255.0 : sample.Brake;
        if (gas >= .72 && (loss & (DriveAssistLossKind.Sideslip | DriveAssistLossKind.WheelSpin)) != 0)
            CountTip(DriveAssistCoachingKind.PowerDuringSlide, !physical.HasValue);
        if (brake >= .55 && Math.Abs(lateralG) is >= .55 and <= 5 &&
            sample.FrontLeft.CombinedSlip > 1 && sample.FrontRight.CombinedSlip > 1)
            CountTip(DriveAssistCoachingKind.BrakeWhileTurning, !physical.HasValue);
        if (brake >= .72 && loss.HasFlag(DriveAssistLossKind.WheelLock)) CountTip(DriveAssistCoachingKind.HeavyBrakeLock, !physical.HasValue);
        if (physical is { } next && prior is { } previous && loss.HasFlag(DriveAssistLossKind.Sideslip) &&
            Math.Sign(next.LeftX) != Math.Sign(previous.LeftX) && Math.Abs(next.LeftX - previous.LeftX) > 14_000)
            CountTip(DriveAssistCoachingKind.SteeringReversal, false);
    }

    private void CountTip(DriveAssistCoachingKind kind, bool gameInput)
    {
        var index = (int)kind;
        _coachingCounts[index]++;
        _coachingGameInput[index] |= gameInput;
    }

    private void ObserveOutput(TelemetrySample sample, ShiftGamepadReport? output)
    {
        if (output is not { } report)
        {
            _steering.Reset(); _throttle.Reset(); _brake.Reset();
            return;
        }
        _steering.Add(report.LeftX / 32768.0, sample.Steer, _elapsed);
        _throttle.Add(report.RightTrigger / 255.0, sample.Throttle, _elapsed);
        _brake.Add(report.LeftTrigger / 255.0, sample.Brake, _elapsed);
    }

    private void ObserveRoute(TelemetrySample previous, TelemetrySample sample)
    {
        var dx = sample.PositionX - previous.PositionX;
        var dz = sample.PositionZ - previous.PositionZ;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (sample.SpeedKmh < 10 || sample.Gear == 0 || !(distance is >= .05 and <= 26) || !double.IsFinite(sample.PositionY))
        {
            _headingX = _headingZ = 0;
            return;
        }
        _headingX = dx / distance;
        _headingZ = dz / distance;
        var point = new DriveAssistRoutePoint(sample.PositionX, sample.PositionY, sample.PositionZ, _headingX, _headingZ,
            sample.SpeedKmh, sample.Brake is >= 0 and <= 1 ? sample.Brake : 0);
        if (_routeDraft.Count > 0)
        {
            var between = DriveAssistRoute.Distance(_routeDraft[^1], point);
            if (between < 4) return;
            if (between > 30) _routeDraft.Clear();
        }
        if (_routeDraft.Count == DriveAssistRoute.PointLimit) _routeDraft.RemoveRange(0, 256);
        _routeDraft.Add(point);
    }

    private DriveAssistRoute? BuildRoute() => _routeDraft.Count >= 8 ? new(_routeDraft.ToArray()) : null;

    private DriveAssistGPoint[] Envelope()
    {
        const int directions = 12;
        Span<double> largest = stackalloc double[directions];
        Span<double> second = stackalloc double[directions];
        Span<int> counts = stackalloc int[directions];
        largest.Clear(); second.Clear(); counts.Clear();
        foreach (var reading in _gPoints)
        {
            if (!reading.Contact) continue;
            var point = reading.Point;
            var distance = Math.Sqrt(point.LateralG * point.LateralG + point.LongitudinalG * point.LongitudinalG);
            if (distance < .12) continue;
            var angle = Math.Atan2(point.LongitudinalG, point.LateralG);
            var bin = Math.Clamp((int)((angle + Math.PI) * directions / Math.Tau), 0, directions - 1);
            counts[bin]++;
            if (distance >= largest[bin]) { second[bin] = largest[bin]; largest[bin] = distance; }
            else second[bin] = Math.Max(second[bin], distance);
        }
        var envelope = new List<DriveAssistGPoint>(directions);
        for (var i = 0; i < directions; i++)
        {
            if (counts[i] < 3) continue;
            var angle = (i + .5) * Math.Tau / directions - Math.PI;
            envelope.Add(new(Math.Cos(angle) * second[i], Math.Sin(angle) * second[i]));
        }
        return envelope.ToArray();
    }

    private static bool PositionChangedAbruptly(TelemetrySample previous, TelemetrySample sample, long elapsed)
    {
        var dx = sample.PositionX - previous.PositionX;
        var dz = sample.PositionZ - previous.PositionZ;
        var limit = 30 + Math.Max(previous.SpeedKmh, sample.SpeedKmh) / 3.6 * elapsed / 1000 * 4;
        return dx * dx + dz * dz > limit * limit;
    }

    private static DriveAssistWheelReading ReadWheel(WheelSample wheel)
    {
        var grounded = double.IsFinite(wheel.NormalizedSuspensionTravel) && wheel.NormalizedSuspensionTravel > .08;
        return new(double.IsFinite(wheel.SlipRatio) ? wheel.SlipRatio : null,
            double.IsFinite(wheel.CombinedSlip) ? Math.Abs(wheel.CombinedSlip) : null,
            wheel.TireTempC is >= -80 and <= 260 and not 0 ? wheel.TireTempC : null, grounded,
            grounded && wheel.SlipRatio >= 1.5 && Math.Abs(wheel.RotationSpeedRadPerSec) > 4,
            grounded && wheel.SlipRatio <= -1.25);
    }

    private static void Enqueue<T>(Queue<T> queue, T item, int capacity)
    {
        if (queue.Count == capacity) queue.Dequeue();
        queue.Enqueue(item);
    }

    private readonly record struct Loss(long FromMs, long LastMs, long PeakMs, double Severity, DriveAssistLossKind Kind, DriveAssistFeature Applied);
    private readonly record struct Corner(long FromMs, long LastMs, int Direction, int Lap, double Entry, double Minimum, double Exit,
        double PeakG, DriveAssistFeature Applied);

    private sealed class InputResponse
    {
        private double? _lastOutput;
        private (long At, double Baseline, double Target)? _pending;

        public double? Delay { get; private set; }
        public int Evidence { get; private set; }

        public void Add(double output, double observed, long now)
        {
            if (!double.IsFinite(observed)) { Reset(); return; }
            if (_lastOutput is double previous && Math.Abs(output - previous) >= .16)
                _pending = Math.Abs(previous - observed) <= .10 ? (now, observed, output) : null;
            else if (_pending is { } step)
            {
                var age = now - step.At;
                if (age > 350 || Math.Abs(output - step.Target) > .07) _pending = null;
                else if (age > 0 && (observed - step.Baseline) / (step.Target - step.Baseline) >= .45)
                {
                    Delay = Delay is double average ? average + (age - average) * .25 : age;
                    Evidence++;
                    _pending = null;
                }
            }
            _lastOutput = output;
        }

        public void Reset(bool clearEvidence = false)
        {
            _pending = null;
            _lastOutput = null;
            if (!clearEvidence) return;
            Delay = null;
            Evidence = 0;
        }
    }
}
