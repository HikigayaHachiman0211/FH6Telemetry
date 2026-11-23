using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FH6Telemetry.ViewModels;

public partial class PerformanceTelemetryViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCheckShiftOutput)), NotifyPropertyChangedFor(nameof(CanEditAutoShift)),
        NotifyPropertyChangedFor(nameof(CanConnectDriveAssist))]
    private bool _isCheckingShiftOutput;
    [ObservableProperty] private string _shiftOutputCheckStatus = "";

    public bool CanCheckShiftOutput => CanEditAutoShift && !IsDriveAssistArmed && !_settings.DriveAssist.ReadOnly;
    public string PadSetupProfileText => TelemetryText.Get(_shiftInput.HasPairedGamepad ? "VpPairedProfile" : "VpManualProfile");

    private AutomaticShiftSettings? ReadShiftOptions(out string error)
    {
        if (!int.TryParse(AutoShiftPressDuration, out var duration))
        {
            error = TelemetryText.Get("ErrIntPressDuration");
            return null;
        }
        if (!TryReadOptionalInt(AutoShiftLatency, out var latency))
        {
            error = TelemetryText.Get("ErrIntShiftLatency");
            return null;
        }
        if (!TryReadOptionalInt(AutoShiftMaximumGear, out var maximumGear))
        {
            error = TelemetryText.Get("ErrRangeMaxGear");
            return null;
        }
        var options = _settings.AutomaticShift with
        {
            GamepadMode = IsKeyboardShiftMode ? GamepadShiftMode.Keyboard : GamepadShiftMode.Combination,
            PressDurationMs = duration, ShiftLatencyMs = latency, MaximumGear = maximumGear,
            AutomaticClutch = AutoShiftAutomaticClutch,
            AutoDetectRaceTransmission = AutoShiftAutoDetectRaceTransmission,
            UpshiftGripProtection = AutoShiftUpshiftGripProtection,
            DriftMode = AutoShiftDriftMode,
            Algorithm = (ShiftAlgorithm)AutoShiftAlgorithmIndex,
            VirtualTcuMode = (VirtualTcuMode)AutoShiftVirtualTcuModeIndex,
            KeyboardUpshift = AutoShiftKeyboardUp.Trim(), KeyboardDownshift = AutoShiftKeyboardDown.Trim(),
            KeyboardClutch = AutoShiftKeyboardClutch.Trim(), KeyboardThrottle = AutoShiftKeyboardThrottle.Trim(),
        };
        return options.TryValidate(out error) ? options : null;
    }

    private static bool TryReadOptionalInt(string text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text, out var entered)) return false;
        value = entered;
        return true;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckShiftOutputAsync(CancellationToken cancellationToken)
    {
        if (!CanCheckShiftOutput || Volatile.Read(ref _disposed) != 0) return;
        _shiftInput.SetDriveAssistReadOnly(false);
        if (ReadShiftOptions(out var error) is not { } options)
        {
            ShiftOutputCheckStatus = error;
            return;
        }
        if (options.MaximumGear == 1)
        {
            ShiftOutputCheckStatus = TelemetryText.Get("VpCheckTwoGears");
            return;
        }
        IsCheckingShiftOutput = true;
        ShiftOutputCheckStatus = TelemetryText.Get("VpCheckConnecting");
        try
        {
            Start();
            if (!IsListening) { ShiftOutputCheckStatus = StatusText; return; }
            var testSettings = options with { AutomaticClutch = options.AutomaticClutch || options.AutoDetectRaceTransmission };
            var connectError = await _shiftInput.ConnectAsync(testSettings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (connectError is not null) { ShiftOutputCheckStatus = connectError; return; }
            RefreshAutoShift();

            ShiftOutputCheckStatus = TelemetryText.Get("VpCheckWaiting");
            var until = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < until)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_shiftInput.Error is { } inputError) { ShiftOutputCheckStatus = inputError; return; }
                if (_shiftInput.IsGameInForeground && CurrentShiftCheckFrame() is { Gear: 1, Clutch: < 0.08 } sample)
                {
                    var result = _shiftInput.TryShift(ShiftDirection.Up, testSettings, downshiftTrigger: 0);
                    if (result == ShiftInputResult.Sent)
                    {
                        ShiftOutputCheckStatus = TelemetryText.Get("VpCheckSent");
                        await ConfirmShiftOutputAsync(sample, options.GamepadMode, cancellationToken);
                        return;
                    }
                    if (result == ShiftInputResult.Failed)
                    {
                        ShiftOutputCheckStatus = _shiftInput.Error ?? TelemetryText.Get("StateInputFailed");
                        return;
                    }
                }
                await Task.Delay(30, cancellationToken);
            }
            ShiftOutputCheckStatus = TelemetryText.Get("VpCheckPreconditions");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShiftOutputCheckStatus = TelemetryText.Get("VpCheckCancelled");
        }
        finally
        {
            _shiftInput.Release();
            IsCheckingShiftOutput = false;
            if (Volatile.Read(ref _disposed) == 0) RefreshAutoShift();
        }
    }

    private TelemetrySample? CurrentShiftCheckFrame()
    {
        lock (_sync)
            return Environment.TickCount64 - _lastArrivalAt <= 250 &&
                _latest is { IsRaceOn: true, SpeedKmh: <= 2.0, Throttle: <= 0.03, Brake: >= 0.25, HandBrake: < 0.08 }
                ? _latest : null;
    }

    private async Task ConfirmShiftOutputAsync(TelemetrySample before, GamepadShiftMode mode, CancellationToken cancellationToken)
    {
        var until = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(25, cancellationToken);
            if (_shiftInput.Error is { } error) { ShiftOutputCheckStatus = error; return; }
            if (!_shiftInput.IsGameInForeground || CurrentShiftCheckFrame() is not { } frame ||
                frame.CarOrdinal != before.CarOrdinal || frame.CarPerformanceIndex != before.CarPerformanceIndex)
            {
                ShiftOutputCheckStatus = TelemetryText.Get("VpCheckInterrupted");
                return;
            }
            if (frame.TimestampMs == before.TimestampMs) continue;
            if (frame.Gear == 2)
            {
                ShiftOutputCheckStatus = TelemetryText.Get("VpCheckConfirmed");
                return;
            }
            if (frame.Gear is not (1 or 11 or byte.MaxValue))
            {
                ShiftOutputCheckStatus = TelemetryText.Get("VpCheckUnexpectedGear");
                return;
            }
        }
        ShiftOutputCheckStatus = TelemetryText.Get(mode == GamepadShiftMode.Keyboard ? "VpCheckUnconfirmedKeyboard" : "VpCheckUnconfirmedRelay");
    }
}
