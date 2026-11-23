using System;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FH6Telemetry.ViewModels;

public partial class PerformanceTelemetryViewModel
{
    private ShortcutBinding[] _activeShortcuts = [];
    private readonly bool[] _shortcutReleased = new bool[Enum.GetValues<ShortcutAction>().Length];
    private ShortcutCapture? _shortcutCapture;
    private bool _mouseOverShortcutCapture;

    [ObservableProperty] private bool _isRecordingShortcut;
    [ObservableProperty] private string _emergencyKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _emergencyGamepad;
    [ObservableProperty] private string _driftExitKeyboard = "";
    [ObservableProperty] private ShiftShortcutChoice? _driftExitGamepad;

    private sealed class ShortcutCapture(ShortcutAction action)
    {
        public ShortcutAction Action { get; } = action;
        public long Deadline { get; } = Environment.TickCount64 + 15_000;
        public bool[] HeldKeys { get; } = new bool[256];
        public ushort HeldButtons { get; set; }
    }

    private bool CanUseDesktopShortcut(ShortcutBinding binding) => _shiftInput.IsGameInForeground ||
        (binding.Action == ShortcutAction.AutoShift && binding.Key is >= 0x70 and <= 0x87 &&
         SelectedTab == (int)TelemetryTab.Shortcuts && _shiftInput.IsShortcutEditorInForeground);

    private bool NeedsSafetyShortcut => IsDriveAssistArmed || IsConnectingDriveAssist ||
        IsAutoShiftEnabled || IsConnectingAutoShift || IsCheckingShiftOutput;

    private void OnDesktopShortcutPressed(byte key)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _shortcutCapture) is { } capture)
        {
            if (!CanCaptureKey(key)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_shortcutCapture, capture) && Environment.TickCount64 < capture.Deadline &&
                    _shiftInput.IsShortcutEditorInForeground) CaptureKey(capture, key);
            }, DispatcherPriority.Input);
            return;
        }
        if (!_settings.AutomaticShift.EnableShiftShortcut && !NeedsSafetyShortcut) return;

        var bindings = Volatile.Read(ref _activeShortcuts);
        foreach (var binding in bindings)
        {
            if (binding.Key != key || !CanUseDesktopShortcut(binding)) continue;
            Dispatcher.UIThread.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0 && ReferenceEquals(_activeShortcuts, bindings) &&
                    _shortcutCapture is null && CanUseDesktopShortcut(binding))
                    RunShortcut(binding.Action, ShiftShortcut.Name(key)!);
            }, DispatcherPriority.Input);
            return;
        }
    }

    internal void PollShiftShortcut()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_shortcutCapture is { } capture)
        {
            PollShortcutCapture(capture);
            return;
        }
        if (!_settings.AutomaticShift.EnableShiftShortcut && !NeedsSafetyShortcut) return;

        var bindings = _activeShortcuts;
        var buttons = bindings.Any(binding => binding.Button != ShiftShortcutButton.None) ? _shiftInput.ReadShortcutButtons() : 0;
        foreach (var binding in bindings)
        {
            var desktop = binding.Key != 0 && !_shiftInput.BuffersShortcutKey(binding.Key) && _shiftInput.IsShortcutKeyDown(binding.Key);
            var pad = (buttons & (ushort)binding.Button) != 0;
            var index = (int)binding.Action;
            var pressed = desktop || pad;
            var trigger = pressed && _shortcutReleased[index];
            _shortcutReleased[index] = !pressed;
            if (!trigger) continue;
            if (desktop && CanUseDesktopShortcut(binding)) RunShortcut(binding.Action, ShiftShortcut.Name(binding.Key)!);
            else if (pad && _shiftInput.IsGameInForeground) RunShortcut(binding.Action, PadChoice(binding.Button).Label);
            if (binding.Action == ShortcutAction.Emergency && _shiftInput.IsGameInForeground)
            {
                Array.Clear(_shortcutReleased);
                return;
            }
        }
    }

    private void RunShortcut(ShortcutAction action, string label)
    {
        AutoShiftShortcutStatus = TelemetryText.Format("ScTriggered", label, ShortcutActionLabel(action));
        switch (action)
        {
            case ShortcutAction.AutoShift:
                RefreshAutoShift();
                if (IsAutoShiftEnabled || IsConnectingAutoShift) DisableAutoShift();
                else if (IsConnectingDriveAssist || IsCheckingShiftOutput) AutoShiftShortcutStatus = TelemetryText.Get("ScShiftBusy");
                else if (EnableAutoShiftCommand.CanExecute(null)) EnableAutoShiftCommand.Execute(null);
                break;
            case ShortcutAction.Clutch:
                ToggleAutomaticClutchByShortcut();
                break;
            case ShortcutAction.Hud:
                HudToggleRequested?.Invoke();
                break;
            case ShortcutAction.RecordStart:
                StartDriveRecording();
                break;
            case ShortcutAction.RecordStop:
                _ = StopDriveRecordingAsync();
                break;
            case ShortcutAction.DriveAssist:
                if (IsDriveAssistArmed || IsConnectingDriveAssist) DisarmDriveAssist();
                else if (!HasDriveAssistSelections)
                {
                    DriveAssistError = TelemetryText.Get("EcuChooseFeatures");
                    RefreshDriveAssist(includeReports: false);
                }
                else if (ArmDriveAssistCommand.CanExecute(null)) ArmDriveAssistCommand.Execute(null);
                break;
            case ShortcutAction.Emergency:
                EmergencyDriveAssist();
                break;
            case ShortcutAction.DriftExit:
                ExitAssistDrift();
                break;
        }
    }

    [RelayCommand]
    private void RecordShortcut(ShortcutAction action)
    {
        if (IsRecordingShortcut || !Enum.IsDefined(action)) return;
        var capture = new ShortcutCapture(action) { HeldButtons = _shiftInput.ReadShortcutButtons() };
        foreach (var key in ShiftShortcut.RecordableKeys)
            if (!_shiftInput.BuffersShortcutKey(key)) capture.HeldKeys[key] = _shiftInput.IsShortcutKeyDown(key);
        Volatile.Write(ref _shortcutCapture, capture);
        IsRecordingShortcut = true;
        AutoShiftShortcutStatus = TelemetryText.Format("ScCaptureWaiting", ShortcutActionLabel(action));
        _shiftShortcutTimer.Start();
    }

    private void PollShortcutCapture(ShortcutCapture capture)
    {
        if (Environment.TickCount64 >= capture.Deadline || !_shiftInput.IsShortcutEditorInForeground)
        {
            EndShortcutCapture(TelemetryText.Get("ScCaptureEnded"));
            return;
        }

        foreach (var key in ShiftShortcut.RecordableKeys)
        {
            if (_shiftInput.BuffersShortcutKey(key)) continue;
            var down = _shiftInput.IsShortcutKeyDown(key);
            var wasDown = capture.HeldKeys[key];
            capture.HeldKeys[key] = down;
            if (!down || wasDown || !CanCaptureKey(key)) continue;
            CaptureKey(capture, key);
            return;
        }

        var buttons = (ushort)(_shiftInput.ReadShortcutButtons() & ShiftShortcut.DigitalButtons);
        var pressed = buttons & ~capture.HeldButtons;
        capture.HeldButtons = buttons;
        if (pressed == 0 || (pressed & (pressed - 1)) != 0) return;
        var pad = PadChoice((ShiftShortcutButton)pressed);
        SetCapturedBinding(capture.Action, null, pad);
        EndShortcutCapture(TelemetryText.Format("ScCaptureResult", ShortcutActionLabel(capture.Action), pad.Label));
    }

    private bool CanCaptureKey(byte key) => _shiftInput.IsShortcutEditorInForeground &&
        (key == ShiftShortcut.Escape || (ShiftShortcut.Name(key) is not null &&
         (Array.IndexOf(ShiftShortcut.MouseKeys, key) < 0 || Volatile.Read(ref _mouseOverShortcutCapture))));

    internal void SetShortcutCapturePointer(bool over) => Volatile.Write(ref _mouseOverShortcutCapture, over && IsRecordingShortcut);

    private void CaptureKey(ShortcutCapture capture, byte key)
    {
        if (key == ShiftShortcut.Escape) { CancelShortcutCapture(); return; }
        if (ShiftShortcut.Name(key) is not { } name) return;
        SetCapturedBinding(capture.Action, name, null);
        EndShortcutCapture(TelemetryText.Format("ScCaptureResult", ShortcutActionLabel(capture.Action), name));
    }

    private void SetCapturedBinding(ShortcutAction action, string? key, ShiftShortcutChoice? pad)
    {
        switch (action)
        {
            case ShortcutAction.AutoShift:
                if (key is not null) AutoShiftToggleKeyboard = key; else AutoShiftToggleGamepad = pad;
                break;
            case ShortcutAction.Clutch:
                if (key is not null) AutoShiftClutchToggleKeyboard = key; else AutoShiftClutchToggleGamepad = pad;
                break;
            case ShortcutAction.Hud:
                if (key is not null) HudToggleKeyboard = key; else HudToggleGamepad = pad;
                break;
            case ShortcutAction.RecordStart:
                if (key is not null) StartRecordingKeyboard = key; else StartRecordingGamepad = pad;
                break;
            case ShortcutAction.RecordStop:
                if (key is not null) StopRecordingKeyboard = key; else StopRecordingGamepad = pad;
                break;
            case ShortcutAction.DriveAssist:
                if (key is not null) DriveAssistToggleKeyboard = key; else DriveAssistToggleGamepad = pad;
                break;
            case ShortcutAction.Emergency:
                if (key is not null) EmergencyKeyboard = key; else EmergencyGamepad = pad;
                break;
            case ShortcutAction.DriftExit:
                if (key is not null) DriftExitKeyboard = key; else DriftExitGamepad = pad;
                break;
        }
    }

    [RelayCommand]
    private void CancelShortcutCapture()
    {
        if (IsRecordingShortcut) EndShortcutCapture(TelemetryText.Get("ScCaptureEnded"));
    }

    private void EndShortcutCapture(string status)
    {
        Volatile.Write(ref _shortcutCapture, null);
        SetShortcutCapturePointer(false);
        IsRecordingShortcut = false;
        Array.Clear(_shortcutReleased);
        if (_activeShortcuts.Length == 0) _shiftShortcutTimer.Stop();
        AutoShiftShortcutStatus = status;
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value != (int)TelemetryTab.Shortcuts) CancelShortcutCapture();
        if (value == (int)TelemetryTab.DriveAssist) RefreshDriveAssist(includeReports: true);
        else if (IsAssistReplayPlaying) StopAssistPlayback();
    }

    private void ApplyShortcutSettings()
    {
        var settings = _settings.AutomaticShift;
        var bindings = ShiftShortcut.Bindings(settings).Where(binding =>
            (settings.EnableShiftShortcut || binding.Action is ShortcutAction.Emergency or ShortcutAction.DriftExit) &&
            (binding.Key != 0 || binding.Button != ShiftShortcutButton.None)).ToArray();
        Volatile.Write(ref _activeShortcuts, bindings);
        Array.Clear(_shortcutReleased);
        if (bindings.Length > 0 || IsRecordingShortcut) _shiftShortcutTimer.Start();
        else _shiftShortcutTimer.Stop();
        RefreshHudShortcut();
    }

    private ShiftShortcutChoice PadChoice(ShiftShortcutButton button) => ShiftShortcutChoices.First(choice => choice.Button == button);

    private static string ShortcutActionLabel(ShortcutAction action) => TelemetryText.Get(action switch
    {
        ShortcutAction.AutoShift => "ScAutoShift",
        ShortcutAction.Clutch => "ScAutoClutch",
        ShortcutAction.Hud => "ScHud",
        ShortcutAction.RecordStart => "ScStartRec",
        ShortcutAction.RecordStop => "ScStopRec",
        ShortcutAction.Emergency => "EcuEmergency",
        ShortcutAction.DriftExit => "EcuDriftExit",
        _ => "ScDriveAssist",
    });
}
