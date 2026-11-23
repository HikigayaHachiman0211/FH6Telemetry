using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FH6Telemetry.ViewModels;

public sealed record AssistWheelReading(string Wheel, string Reading, IBrush Background);
public sealed record AssistReplayEvent(string Label, double StartMs);

public partial class PerformanceTelemetryViewModel
{
    private readonly DriveAssistAnalysis _assistAnalysis = new();
    private DriveAssistReplay? _assistReplay;
    private DriveAssistVisionCue[] _assistVision = [];
    private DispatcherTimer? _assistReplayTimer;
    private long _assistReplayTick;
    private long _nextAssistAnalysisRefresh;
    private string? _replayDirectory;
    private (ShiftGamepadReport Input, ShiftGamepadReport Output, bool Estimated)? _shadowReports;

    [ObservableProperty] private string _assistInputs = "";
    [ObservableProperty] private string _assistResponse = "";
    [ObservableProperty] private string _assistRisk = "";
    [ObservableProperty] private string _assistRoad = "";
    [ObservableProperty] private string _assistCalibration = "";
    [ObservableProperty] private string _assistCoaching = "";
    [ObservableProperty] private string _assistCorners = "";
    [ObservableProperty] private string _assistRouteText = "";
    [ObservableProperty] private IReadOnlyList<AssistWheelReading> _assistWheels = Array.Empty<AssistWheelReading>();
    [ObservableProperty] private IReadOnlyList<DriveAssistGPoint> _assistGPoints = Array.Empty<DriveAssistGPoint>();
    [ObservableProperty] private IReadOnlyList<DriveAssistGPoint> _assistGripEnvelope = Array.Empty<DriveAssistGPoint>();
    [ObservableProperty] private double? _assistSideslip;
    [ObservableProperty] private bool _hasAssistReplay;
    [ObservableProperty] private bool _showAssistReplay;
    [ObservableProperty] private bool _isAssistReplayPlaying;
    [ObservableProperty] private string _assistReplayStatus = "";
    [ObservableProperty] private string _assistReplayFrameText = "";
    [ObservableProperty] private double _assistReplayPosition;
    [ObservableProperty] private double _assistReplayDuration = 1;
    [ObservableProperty] private IReadOnlyList<AssistReplayEvent> _assistReplayEvents = Array.Empty<AssistReplayEvent>();
    [ObservableProperty] private AssistReplayEvent? _selectedAssistEvent;
    [ObservableProperty] private string _assistRouteStatus = "";

    private void RecordAssistAnalysis(TelemetrySample sample, DriveAssistFrame? assist, long now)
    {
        var physical = assist?.Physical ?? _shiftInput.PhysicalDriveReport;
        lock (_driveAssistSync)
        {
            if (_readOnlyAssistArmed)
            {
                var driver = physical ?? ReportFromTelemetry(sample, _settings.DriveAssist.HandbrakeButton);
                _shadowAssist.Feed(sample, driver, now);
                var preview = _shadowAssist.Render(driver, driver, now);
                _shadowReports = _shadowAssist.HasFreshData(now) ? (driver, preview, physical is null) : null;
                if (_settings.DriveAssist.ShadowMode && _shadowReports is not null)
                    _driveRecorder?.RecordShift(new(now, "assist_preview", Gear: sample.Gear, TelemetryTimestampMs: sample.TimestampMs,
                        PhysicalReport: physical, PlannedReport: preview, AssistFresh: true));
            }
            else _shadowReports = null;
        }
        // Response times use actual device reports, never a simulated output.
        _assistAnalysis.Add(sample, physical, assist?.Output, assist?.Applied ?? 0, now);
    }

    private static ShiftGamepadReport ReportFromTelemetry(TelemetrySample sample, ShiftShortcutButton handbrake)
    {
        var buttons = (sample.HandBrake > 0.3 ? (ushort)handbrake : 0) |
            (sample.Clutch > 0.3 ? (ushort)ShiftShortcutButton.LeftStick : 0);
        return new((ushort)buttons, Pedal(sample.Throttle), Pedal(sample.Brake),
            (short)Math.Round(32_767 * (double.IsFinite(sample.Steer) ? Math.Clamp(sample.Steer, -1, 1) : 0)));
    }

    private static byte Pedal(double value) => double.IsFinite(value) ? (byte)Math.Round(Math.Clamp(value, 0, 1) * 255) : (byte)0;

    private void RefreshAssistAnalysis(DriveAssistStatus status, bool force = false)
    {
        if (ShowAssistReplay || SelectedTab != (int)TelemetryTab.DriveAssist && !force) return;
        var now = Environment.TickCount64;
        if (!force && now < _nextAssistAnalysisRefresh) return;
        _nextAssistAnalysisRefresh = now + 200;
        var analysis = _assistAnalysis.Snapshot();
        var latest = analysis.Latest;
        ShowAssistGraphs(analysis, latest?.SideslipDegrees);
        AssistWheels = latest is null ? [] : latest.Wheels.Select((wheel, i) => WheelDisplay(i, wheel.CombinedSlip,
            AssistTireCelsius(wheel.TemperatureC), wheel.InContact, wheel.Spinning, wheel.Locking)).ToArray();
        lock (_driveAssistSync)
        {
            AssistInputs = _shadowReports is { } ghost && status.Fresh
                ? TelemetryText.Format(ghost.Estimated ? "EcuGhostInputsEstimated" : "EcuGhostInputs", Axes(ghost.Input), Axes(ghost.Output))
                : TelemetryText.Format("EcuInputs", Axes(latest?.Physical), Axes(latest?.Output));
        }
        AssistRouteText = RouteText(analysis.RouteHint);
        ShowAssistSummary(analysis);
        var lateral = status.Lateral;
        var pedal = status.Pedal;
        AssistRisk = TelemetryText.Format("EcuRiskValues", lateral.Risk * 100, lateral.SpinLevel, lateral.RollRisk * 100,
            lateral.BottomedOut ? TelemetryText.Get("EcuBottomAlert") : "—", lateral.SuspensionImbalance * 100, Number(lateral.DriftScore));
        AssistRoad = TelemetryText.Format("EcuRoadValues", TelemetryText.Get("EcuSurface" + pedal.Surface),
            Number(pedal.GripEstimate, 2), pedal.GripConfidence * 100, Number(pedal.StoppingDistanceMeters),
            WheelNames(pedal.SpinWheelMask), WheelNames(pedal.WetWheelMask), WheelNames(pedal.CurbWheelMask),
            WheelNames(pedal.TireTemperatureMask), Number(pedal.RoadGrade * 100), Number(pedal.FrontRearGripDelta, 2),
            pedal.HillHolding ? TelemetryText.Get("EcuHillHolding") : "—",
            pedal.GripChanged || lateral.GripRecovery ? TelemetryText.Get("EcuGripChange") : "—");
        AssistCalibration = TelemetryText.Format("EcuCalibrationValues", Number(lateral.SteeringGain, 2),
            Number(lateral.SteeringDelayMs), Number(lateral.SteeringDeadzone * 100), Number(pedal.ThrottleGain, 2),
            Number(pedal.BrakeGain, 2), status.Fresh ? Number(pedal.BrakeSlipThreshold, 2) : "—",
            pedal.CalibrationRolledBack ? TelemetryText.Get("EcuRolledBack") : "—");
    }

    private void ShowAssistGraphs(DriveAssistAnalysisSnapshot analysis, double? beta)
    {
        AssistSideslip = beta;
        AssistGPoints = analysis.GPoints;
        AssistGripEnvelope = analysis.GripEnvelope;
    }

    private void ShowAssistSummary(DriveAssistAnalysisSnapshot analysis)
    {
        var response = analysis.Response;
        AssistResponse = TelemetryText.Format("EcuMeasureDelay", Number(response.SteeringMilliseconds), response.SteeringEvidence,
            Number(response.ThrottleMilliseconds), response.ThrottleEvidence, Number(response.BrakeMilliseconds), response.BrakeEvidence);
        AssistCoaching = analysis.Coaching.Count == 0 ? TelemetryText.Get("EcuNoCoaching") :
            string.Join(Environment.NewLine, analysis.Coaching.Select(tip =>
                TelemetryText.Format("EcuCoachTip", TelemetryText.Get("EcuCoach" + tip.Kind), tip.Samples,
                    TelemetryText.Get(tip.UsesGameInput ? "EcuEstimatedInput" : "EcuMeasuredInput"))));
        var corners = analysis.PreviousLap?.Corners ?? analysis.Corners;
        var rows = corners.TakeLast(10).Select(corner => TelemetryText.Format("EcuCornerResult", corner.Number,
            TelemetryText.Get(corner.Direction < 0 ? "EcuTurnLeft" : "EcuTurnRight"), corner.EntryKmh,
            corner.MinimumKmh, corner.ExitKmh, corner.PeakLateralG)).ToList();
        if (analysis.PreviousLap is { } lap)
            rows.Insert(0, TelemetryText.Format(lap.FullLap ? "EcuLapComplete" : "EcuLapPartial", lap.LapNumber, lap.Seconds));
        AssistCorners = rows.Count == 0 ? TelemetryText.Get("EcuNoCorners") : string.Join(Environment.NewLine, rows);
    }

    private static string Number(double? value, int decimals = 1) => value is double n && double.IsFinite(n)
        ? n.ToString(decimals == 2 ? "0.00" : "0.0", CultureInfo.CurrentCulture) : "—";

    private static string Axes(ShiftGamepadReport? report) => report is { } pad
        ? TelemetryText.Format("EcuAxes", pad.LeftX * (100.0 / 32768), pad.RightTrigger * (100.0 / 255), pad.LeftTrigger * (100.0 / 255))
        : "—";

    private static string WheelNames(int mask) => mask == 0 ? "—" :
        string.Join(", ", WheelLabels.Where((_, i) => (mask & (1 << i)) != 0));

    private static readonly string[] WheelLabels = ["FL", "FR", "RL", "RR"];

    private double? AssistTireCelsius(double? value) => _settings.DriveAssist.TireTempFahrenheit ? (value - 32) * (5.0 / 9) : value;

    private static AssistWheelReading WheelDisplay(int index, double? slip, double? temp, bool contact, bool spinning, bool locking)
    {
        var load = slip is double grip && double.IsFinite(grip) ? Math.Clamp(Math.Abs(grip), 0, 2) : 0;
        var color = !contact ? Color.FromArgb(30, 120, 130, 148) : load >= 1 ? Color.FromArgb(90, 241, 91, 61) :
            load >= 0.58 ? Color.FromArgb(72, 230, 161, 54) : Color.FromArgb(58, 60, 154, 122);
        var state = !contact ? "EcuWheelAir" : locking ? "EcuWheelLock" : spinning ? "EcuWheelSpin" : "EcuWheelGround";
        return new(WheelLabels[index], TelemetryText.Format("EcuWheelReading", Number(slip, 2), Number(temp), TelemetryText.Get(state)),
            new SolidColorBrush(color));
    }

    [RelayCommand]
    private async Task LoadAssistReplayAsync(string? folder, CancellationToken cancellationToken)
    {
        try
        {
            folder ??= Directory.Exists(DriveRecordingRoot) ? Directory.EnumerateDirectories(DriveRecordingRoot)
                .Where(path => File.Exists(Path.Combine(path, "telemetry.jsonl.gz")))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).FirstOrDefault() : null;
            if (folder is null) { AssistReplayStatus = TelemetryText.Get("EcuReplayNoLogs"); return; }
            StopAssistPlayback();
            AssistReplayStatus = TelemetryText.Get("EcuReplayLoading");
            var result = await Task.Run(() => DriveAssistReplay.LoadAsync(folder, _settings.DriveAssist, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _assistReplay = result;
            _replayDirectory = folder;
            _assistVision = [];
            HasAssistReplay = true;
            AssistReplayDuration = Math.Max(1, result.Frames[^1].ElapsedMs);
            AssistReplayPosition = 0;
            SelectedAssistEvent = null;
            ShowAssistReplay = true;
            AssistReplayEvents = result.Summary.Events.Select(e => new AssistReplayEvent(
                TelemetryText.Format("EcuReplayEvent", e.PeakMs / 1000.0, LossKinds(e.Kind), e.PeakSideslipDegrees), e.StartMs)).ToArray();
            AssistReplayStatus = TelemetryText.Format("EcuReplayLoaded", result.Frames.Count, result.SimulatedFrames,
                result.InvalidLines, result.IsTruncated ? TelemetryText.Get("EcuReplayTruncated") : "");
            ShowAssistGraphs(result.Summary, null);
            ShowAssistSummary(result.Summary);
            UpdateAssistReplayFrame();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException)
        {
            AssistReplayStatus = TelemetryText.Format("EcuReplayFailed", error.Message);
        }
    }

    [RelayCommand]
    private Task ReloadAssistReplayAsync(CancellationToken cancellationToken) =>
        _replayDirectory is not null ? LoadAssistReplayAsync(_replayDirectory, cancellationToken) : Task.CompletedTask;

    [RelayCommand]
    private async Task LoadAssistVisionAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !HasAssistReplay) return;
        try
        {
            _assistVision = await DriveAssistRoute.LoadVisionAsync(path, cancellationToken);
            AssistRouteStatus = TelemetryText.Format("EcuVisionLoaded", _assistVision.Length);
            UpdateAssistReplayFrame();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException)
        {
            AssistRouteStatus = TelemetryText.Format("EcuReplayFailed", error.Message);
        }
    }

    [RelayCommand]
    private void LearnAssistRoute()
    {
        if (ShowAssistReplay && _assistReplay?.LearnRoute() is { } route) _assistAnalysis.SetRoute(route);
        else if (ShowAssistReplay || !_assistAnalysis.LearnRoute())
        {
            AssistRouteStatus = TelemetryText.Get("EcuRouteNeedData");
            return;
        }

        AssistRouteStatus = TelemetryText.Format("EcuRouteLearned", _assistAnalysis.Route?.PointCount ?? 0);
        if (ShowAssistReplay) UpdateAssistReplayFrame();
        else RefreshAssistAnalysis(CurrentAssistStatus(false), force: true);
    }

    [RelayCommand]
    private void ClearAssistRoute()
    {
        _assistAnalysis.SetRoute(null);
        _assistVision = [];
        AssistRouteStatus = "";
        if (ShowAssistReplay) UpdateAssistReplayFrame();
        else RefreshAssistAnalysis(CurrentAssistStatus(false), force: true);
    }

    [RelayCommand]
    private void PlayAssistReplay()
    {
        if (_assistReplay is null) return;
        ShowAssistReplay = true;
        ShowAssistGraphs(_assistReplay.Summary, null);
        ShowAssistSummary(_assistReplay.Summary);
        if (AssistReplayPosition >= AssistReplayDuration) AssistReplayPosition = 0;
        UpdateAssistReplayFrame();
        _assistReplayTick = Environment.TickCount64;
        if (_assistReplayTimer is null)
        {
            _assistReplayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _assistReplayTimer.Tick += (_, _) => TickAssistPlayback();
        }
        IsAssistReplayPlaying = true;
        _assistReplayTimer.Start();
    }

    [RelayCommand]
    private void StopAssistPlayback()
    {
        _assistReplayTimer?.Stop();
        IsAssistReplayPlaying = false;
    }

    private void TickAssistPlayback()
    {
        var now = Environment.TickCount64;
        AssistReplayPosition = Math.Min(AssistReplayDuration, AssistReplayPosition + (now - _assistReplayTick));
        _assistReplayTick = now;
        if (AssistReplayPosition >= AssistReplayDuration) StopAssistPlayback();
    }

    [RelayCommand]
    private void StepAssistReplay(string? direction)
    {
        if (_assistReplay is not { } replay) return;
        StopAssistPlayback();
        ShowAssistReplay = true;
        var index = replay.FrameIndexAt((long)AssistReplayPosition);
        var next = Math.Clamp(index + (direction == "back" ? -1 : 1), 0, replay.Frames.Count - 1);
        AssistReplayPosition = replay.Frames[next].ElapsedMs;
        UpdateAssistReplayFrame();
    }

    [RelayCommand]
    private void ShowAssistLive()
    {
        StopAssistPlayback();
        ShowAssistReplay = false;
        RefreshAssistAnalysis(CurrentAssistStatus(false), force: true);
    }

    partial void OnAssistReplayPositionChanged(double value) => UpdateAssistReplayFrame();
    partial void OnShowAssistReplayChanged(bool value)
    {
        if (!value || _assistReplay is not { } replay) return;
        ShowAssistGraphs(replay.Summary, null);
        ShowAssistSummary(replay.Summary);
        UpdateAssistReplayFrame();
    }
    partial void OnSelectedAssistEventChanged(AssistReplayEvent? value)
    {
        if (value is null) return;
        StopAssistPlayback();
        ShowAssistReplay = true;
        AssistReplayPosition = value.StartMs;
        UpdateAssistReplayFrame();
    }

    private void UpdateAssistReplayFrame()
    {
        if (!ShowAssistReplay || _assistReplay is not { } replay) return;
        var index = replay.FrameIndexAt((long)AssistReplayPosition);
        var frame = replay.Frames[index];
        var beta = VehicleSideSlip.Angle(frame.Sample) * (180 / Math.PI);
        AssistSideslip = beta;
        AssistWheels = Enumerable.Range(0, 4).Select(i =>
        {
            var wheel = frame.Sample.Wheel(i);
            return WheelDisplay(i, wheel.CombinedSlip, AssistTireCelsius(wheel.TireTempC), wheel.NormalizedSuspensionTravel > 0.08,
                wheel.SlipRatio > 1, wheel.SlipRatio < -1);
        }).ToArray();
        AssistReplayFrameText = TelemetryText.Format("EcuReplayFrame", frame.ElapsedMs / 1000.0, frame.Sample.SpeedKmh,
            Axes(frame.Physical), Axes(frame.RecordedOutput), Axes(frame.SimulatedOutput));
        if (frame.RecordedProposal is { } proposal)
            AssistReplayFrameText += Environment.NewLine + TelemetryText.Format("EcuReplayProposal", Axes(proposal));
        AssistRouteText = RouteText(replay.GuideAt(index, _assistAnalysis.Route, _assistVision));
    }

    private static string RouteText(DriveAssistRouteHint? hint) => hint is { } cue
        ? TelemetryText.Format("EcuRouteHint", TelemetryText.Get("EcuHint" + cue.Source), cue.OffsetMetres, cue.Confidence * 100,
            Number(cue.ReferenceSpeedKmh), Number(cue.DistanceToBrakeMetres)) : TelemetryText.Get("EcuNoRouteHint");

    private static string LossKinds(DriveAssistLossKind kind) => string.Join(" · ",
        Enum.GetValues<DriveAssistLossKind>().Where(value => value != 0 && (kind & value) != 0)
            .Select(value => TelemetryText.Get("EcuLoss" + value)));

    private void DisposeAssistAnalysis()
    {
        LoadAssistReplayCommand.Cancel();
        ReloadAssistReplayCommand.Cancel();
        LoadAssistVisionCommand.Cancel();
        StopAssistPlayback();
    }
}
