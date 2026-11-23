using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FH6Telemetry.ViewModels;

public sealed partial class DriveAssistOption : ObservableObject
{
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isAvailable = true;

    internal DriveAssistOption(DriveAssistFeature feature, bool enabled, Action<DriveAssistFeature, bool> change)
    {
        Feature = feature;
        _isEnabled = enabled;
        _change = change;
    }

    private readonly Action<DriveAssistFeature, bool> _change;
    internal DriveAssistFeature Feature { get; }
    public string Label => TelemetryText.Get("Ecu" + Feature);
    public string Description => TelemetryText.Get("EcuDesc" + Feature);
    partial void OnIsEnabledChanged(bool value) => _change(Feature, value);
    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
    }
}

public sealed class DriveAssistPreset : ObservableObject
{
    private readonly string _nameKey;
    private readonly DriveAssistFeature _features;
    private readonly bool? _skidpad;
    private readonly DriveAssistAuthority _authority;
    private readonly bool _shadow;
    private readonly bool _fair;
    private readonly int? _targetSlip;

    internal DriveAssistPreset(string nameKey, DriveAssistFeature features, bool? skidpad = null,
        DriveAssistAuthority authority = DriveAssistAuthority.Assisted, bool shadow = false, bool fair = false, int? targetSlip = null)
    {
        _nameKey = nameKey;
        _features = features;
        _skidpad = skidpad;
        _authority = authority;
        _shadow = shadow;
        _fair = fair;
        _targetSlip = targetSlip;
    }

    public string Label => TelemetryText.Get(_nameKey);
    public string Description => TelemetryText.Get(_nameKey + "Help");
    public string Contents => string.Join(" · ", Enum.GetValues<DriveAssistFeature>()
        .Where(feature => feature != DriveAssistFeature.None && _features.HasFlag(feature))
        .Select(feature => TelemetryText.Get("Ecu" + feature)));

    internal bool Matches(DriveAssistSettings settings) =>
        settings.Features == _features && (_skidpad is null || settings.Skidpad == _skidpad.Value) &&
        settings.Authority == _authority && settings.ShadowMode == _shadow && settings.FairPlay == _fair &&
        (_targetSlip is null || settings.TargetSlipPercent == _targetSlip.Value);

    internal DriveAssistSettings ApplyTo(DriveAssistSettings settings) =>
        settings with { Features = _features, Skidpad = _skidpad ?? settings.Skidpad, Authority = _authority,
            ShadowMode = _shadow, FairPlay = _fair, TargetSlipPercent = _targetSlip ?? settings.TargetSlipPercent };

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Contents));
    }
}

public partial class PerformanceTelemetryViewModel
{
    private readonly object _driveAssistSync = new();
    private readonly DriveAssistController _shadowAssist = new(new());
    private bool _readOnlyAssistArmed;

    public IReadOnlyList<DriveAssistOption> SteeringAssists { get; private set; } = [];
    public IReadOnlyList<DriveAssistOption> PedalAssists { get; private set; } = [];
    public IReadOnlyList<DriveAssistOption> SurfaceAssists { get; private set; } = [];
    public IReadOnlyList<DriveAssistOption> VehicleAssists { get; private set; } = [];
    public IReadOnlyList<DriveAssistOption> ExperimentalAssists { get; private set; } = [];
    public IReadOnlyList<DriveAssistPreset> DriveAssistPresets { get; } =
    [
        new("EcuPresetOff", DriveAssistFeature.None),
        new("EcuPresetComfort", DriveAssistFeature.Damper | DriveAssistFeature.SteeringTuning |
            DriveAssistFeature.PowerManagement | DriveAssistFeature.Profiles),
        new("EcuPresetRoad", DriveAssistFeature.Damper | DriveAssistFeature.CounterSteer |
            DriveAssistFeature.Understeer | DriveAssistFeature.Profiles | DriveAssistFeature.SteeringTuning | DriveAssistFeature.Stability),
        new("EcuPresetSport", DriveAssistFeature.Stability | DriveAssistFeature.CounterSteer | DriveAssistFeature.Traction |
            DriveAssistFeature.CornerTraction | DriveAssistFeature.AdaptiveTraction | DriveAssistFeature.TransmissionAssist |
            DriveAssistFeature.SteeringTuning | DriveAssistFeature.Profiles, targetSlip: 100),
        new("EcuPresetTrack", DriveAssistFeature.CounterSteer | DriveAssistFeature.Understeer |
            DriveAssistFeature.Traction | DriveAssistFeature.CornerTraction | DriveAssistFeature.Abs |
            DriveAssistFeature.TrailBrake | DriveAssistFeature.Profiles | DriveAssistFeature.Thermal | DriveAssistFeature.Feedback |
            DriveAssistFeature.Stability | DriveAssistFeature.AdaptiveTraction | DriveAssistFeature.AdaptiveBraking |
            DriveAssistFeature.TransmissionAssist | DriveAssistFeature.VehicleLearning, targetSlip: 100),
        new("EcuPresetRally", DriveAssistFeature.Damper | DriveAssistFeature.Traction | DriveAssistFeature.Landing |
            DriveAssistFeature.SplitGrip | DriveAssistFeature.Crest | DriveAssistFeature.BottomOut |
            DriveAssistFeature.Profiles | DriveAssistFeature.Feedback | DriveAssistFeature.SurfaceProtection |
            DriveAssistFeature.SuspensionProtection | DriveAssistFeature.TransmissionAssist | DriveAssistFeature.HillStart |
            DriveAssistFeature.Abs | DriveAssistFeature.AdaptiveBraking),
        new("EcuPresetDrift", DriveAssistFeature.DriftHold | DriveAssistFeature.Hairpin | DriveAssistFeature.Traction |
            DriveAssistFeature.DriftTransitions | DriveAssistFeature.TransmissionAssist | DriveAssistFeature.Feedback, targetSlip: 112),
        new("EcuPresetLaunch", DriveAssistFeature.Launch | DriveAssistFeature.Traction |
            DriveAssistFeature.Profiles | DriveAssistFeature.Thermal | DriveAssistFeature.Feedback |
            DriveAssistFeature.PowerManagement | DriveAssistFeature.AdaptiveTraction),
        new("EcuPresetBeginner", DriveAssistFeature.Damper | DriveAssistFeature.SteeringTuning |
            DriveAssistFeature.Stability | DriveAssistFeature.CounterSteer | DriveAssistFeature.Understeer |
            DriveAssistFeature.Traction | DriveAssistFeature.AdaptiveTraction | DriveAssistFeature.Abs |
            DriveAssistFeature.VibrationWarning | DriveAssistFeature.Landing | DriveAssistFeature.HillStart, targetSlip: 82),
        new("EcuPresetCruise", DriveAssistFeature.Cruise | DriveAssistFeature.AdaptiveBraking, skidpad: false,
            authority: DriveAssistAuthority.Automatic),
        new("EcuPresetSkidpad", DriveAssistFeature.Cruise | DriveAssistFeature.Feedback, skidpad: true,
            authority: DriveAssistAuthority.Automatic),
        new("EcuPresetShadow", DriveAssistFeature.CounterSteer | DriveAssistFeature.Stability |
            DriveAssistFeature.Traction | DriveAssistFeature.Abs | DriveAssistFeature.Feedback, shadow: true),
        new("EcuPresetFair", DriveAssistFeature.Feedback, authority: DriveAssistAuthority.Manual, fair: true),
    ];
    public IReadOnlyList<string> AssistHandbrakeButtons { get; } = ["A", "Y", "LB", "RB"];
    private static readonly ShiftShortcutButton[] HandbrakeChoices =
        [ShiftShortcutButton.A, ShiftShortcutButton.Y, ShiftShortcutButton.LeftBumper, ShiftShortcutButton.RightBumper];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConnectDriveAssist)), NotifyPropertyChangedFor(nameof(CanRequestCruise)),
        NotifyPropertyChangedFor(nameof(CanCheckShiftOutput))]
    private bool _isDriveAssistArmed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditAutoShift)), NotifyPropertyChangedFor(nameof(CanConnectDriveAssist)),
        NotifyPropertyChangedFor(nameof(CanCheckShiftOutput))]
    private bool _isConnectingDriveAssist;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConnectDriveAssist))] private bool _hasDriveAssistSelections;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRequestCruise))] private bool _isCruiseSelected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRequestCruise))] private bool _isCruiseBusy;
    [ObservableProperty] private string _driveAssistStatus = "";
    [ObservableProperty] private string _driveAssistError = "";
    [ObservableProperty] private string _driveAssistReport = "";
    [ObservableProperty] private string _driveAssistLapReport = "";
    [ObservableProperty] private string _driveAssistTrialReport = "";
    [ObservableProperty] private bool _hasDriveAssistReport;
    [ObservableProperty] private bool _hasDriveAssistLapReport;
    [ObservableProperty] private bool _hasDriveAssistTrialReport;
    [ObservableProperty] private int _driveAssistTargetSpeed;
    [ObservableProperty] private int _driveAssistLaunchRpm;
    [ObservableProperty] private bool _driveAssistSkidpad;
    [ObservableProperty] private bool _driveAssistTireTempFahrenheit;
    [ObservableProperty] private int _driveAssistHandbrakeIndex;
    [ObservableProperty] private int _driveAssistAuthorityIndex;
    [ObservableProperty] private bool _driveAssistShadow;
    [ObservableProperty] private bool _driveAssistFairPlay;
    [ObservableProperty] private bool _driveAssistUseGameAbs;
    [ObservableProperty] private bool _driveAssistUseGameTractionControl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConnectDriveAssist))] private bool _assistOfflineConfirmed;
    [ObservableProperty] private int _driveAssistSteeringDeadzone;
    [ObservableProperty] private int _driveAssistSteeringDebounce;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DriveAssistTightTurnSpeedStatus))]
    private double _driveAssistTightTurnSpeedLimit;
    [ObservableProperty] private bool _driveAssistLearnTightTurnSpeed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DriveAssistTightTurnSpeedStatus))]
    private int? _learnedTightTurnSpeedKmh;
    [ObservableProperty] private int _driveAssistTargetSlip;
    [ObservableProperty] private int _driveAssistBrakeBias;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyDriveAssistPreset))]
    private DriveAssistPreset? _selectedDriveAssistPreset;

    public bool CanConnectDriveAssist => HasDriveAssistSelections && !IsDriveAssistArmed && !IsConnectingDriveAssist &&
        !IsConnectingAutoShift && !IsCheckingShiftOutput && (_settings.DriveAssist.ReadOnly || AssistOfflineConfirmed);
    public bool CanRequestCruise => IsDriveAssistArmed && IsCruiseSelected && !IsCruiseBusy &&
        (_settings.DriveAssist.ShadowMode || !_settings.DriveAssist.ReadOnly &&
            _settings.DriveAssist.Authority == DriveAssistAuthority.Automatic);
    public bool CanApplyDriveAssistPreset => SelectedDriveAssistPreset is { } preset && !preset.Matches(_settings.DriveAssist);
    public string DriveAssistPresetStatus => TelemetryText.Format("EcuPresetStatus", MatchingDriveAssistPreset?.Label ?? TelemetryText.Get("EcuPresetCustom"));
    public string DriveAssistTightTurnSpeedStatus => LearnedTightTurnSpeedKmh is int learned
        ? TelemetryText.Format("EcuTightTurnSpeedLearned", learned)
        : TelemetryText.Format("EcuTightTurnSpeedLearning", DriveAssistTightTurnSpeedLimit);

    private DriveAssistPreset? MatchingDriveAssistPreset => DriveAssistPresets.FirstOrDefault(preset => preset.Matches(_settings.DriveAssist));
    private IEnumerable<DriveAssistOption> DriveAssistOptions =>
        SteeringAssists.Concat(PedalAssists).Concat(SurfaceAssists).Concat(VehicleAssists).Concat(ExperimentalAssists);

    private void LoadDriveAssist()
    {
        var settings = _settings.DriveAssist;
        var options = Enum.GetValues<DriveAssistFeature>().Skip(1)
            .Select(f => new DriveAssistOption(f, settings.Features.HasFlag(f), ChangeDriveAssistFeature)).ToArray();
        const DriveAssistFeature steering = DriveAssistFeature.CounterSteer | DriveAssistFeature.Understeer |
            DriveAssistFeature.Damper | DriveAssistFeature.DriftHold | DriveAssistFeature.Hairpin |
            DriveAssistFeature.Stability | DriveAssistFeature.SteeringTuning | DriveAssistFeature.KeyboardSteering |
            DriveAssistFeature.DriftTransitions;
        const DriveAssistFeature pedals = DriveAssistFeature.Traction | DriveAssistFeature.CornerTraction |
            DriveAssistFeature.Launch | DriveAssistFeature.Abs | DriveAssistFeature.TrailBrake | DriveAssistFeature.Slipper |
            DriveAssistFeature.PowerManagement | DriveAssistFeature.AdaptiveTraction | DriveAssistFeature.AdaptiveBraking |
            DriveAssistFeature.TransmissionAssist | DriveAssistFeature.SpeedLimiter | DriveAssistFeature.HillStart;
        const DriveAssistFeature surface = DriveAssistFeature.Landing | DriveAssistFeature.SplitGrip | DriveAssistFeature.Crest |
            DriveAssistFeature.BottomOut | DriveAssistFeature.SurfaceProtection | DriveAssistFeature.SuspensionProtection;
        const DriveAssistFeature experimental = DriveAssistFeature.TightTurnSteering;
        SteeringAssists = options.Where(option => (option.Feature & steering) != 0).ToArray();
        PedalAssists = options.Where(option => (option.Feature & pedals) != 0).ToArray();
        SurfaceAssists = options.Where(option => (option.Feature & surface) != 0).ToArray();
        VehicleAssists = options.Where(option => (option.Feature & (steering | pedals | surface | experimental)) == 0).ToArray();
        ExperimentalAssists = options.Where(option => (option.Feature & experimental) != 0).ToArray();
        DriveAssistTargetSpeed = settings.TargetSpeedKmh;
        DriveAssistLaunchRpm = settings.LaunchRpmPercent;
        DriveAssistSkidpad = settings.Skidpad;
        DriveAssistTireTempFahrenheit = settings.TireTempFahrenheit;
        DriveAssistHandbrakeIndex = Array.IndexOf(HandbrakeChoices, settings.HandbrakeButton);
        SyncAdvancedAssistSettings(settings);
        _shiftInput.SetDriveAssistReadOnly(settings.ReadOnly);
        if (settings.ReadOnly && _settings.AutomaticShiftEnabled) DisableAutoShift();
        RefreshAvailableDriveAssists(settings);
        SelectedDriveAssistPreset = MatchingDriveAssistPreset ?? DriveAssistPresets[0];
        RefreshDriveAssist(includeReports: true);
    }

    [RelayCommand]
    private void ApplyDriveAssistPreset()
    {
        if (SelectedDriveAssistPreset is not { } preset || preset.Matches(_settings.DriveAssist)) return;
        var settings = preset.ApplyTo(_settings.DriveAssist);
        SaveDriveAssistSettings(settings);
        // Apply once to the relay, then synchronize the checkboxes with the already-saved settings.
        foreach (var option in DriveAssistOptions) option.IsEnabled = settings.Features.HasFlag(option.Feature);
        SyncAdvancedAssistSettings(settings);
    }

    private void SyncAdvancedAssistSettings(DriveAssistSettings settings)
    {
        DriveAssistSkidpad = settings.Skidpad;
        DriveAssistAuthorityIndex = (int)settings.Authority;
        DriveAssistShadow = settings.ShadowMode;
        DriveAssistFairPlay = settings.FairPlay;
        DriveAssistUseGameAbs = settings.UseGameAbs;
        DriveAssistUseGameTractionControl = settings.UseGameTractionControl;
        DriveAssistSteeringDeadzone = settings.SteeringDeadzonePercent;
        DriveAssistSteeringDebounce = settings.SteeringDebounceMs;
        DriveAssistTightTurnSpeedLimit = settings.TightTurnSpeedLimitKmh;
        DriveAssistLearnTightTurnSpeed = settings.LearnTightTurnSpeed;
        DriveAssistTargetSlip = settings.TargetSlipPercent;
        DriveAssistBrakeBias = settings.BrakeBiasPercent;
    }

    private void RefreshDriveAssistLanguage()
    {
        foreach (var option in DriveAssistOptions) option.RefreshLanguage();
        foreach (var preset in DriveAssistPresets) preset.RefreshLanguage();
        OnPropertyChanged(nameof(DriveAssistPresetStatus));
        OnPropertyChanged(nameof(DriveAssistTightTurnSpeedStatus));
    }

    private void ChangeDriveAssistFeature(DriveAssistFeature feature, bool enabled)
    {
        var settings = _settings.DriveAssist;
        SaveDriveAssistSettings(settings with { Features = enabled ? settings.Features | feature : settings.Features & ~feature });
    }

    partial void OnDriveAssistTargetSpeedChanged(int value) => SaveDriveAssistSettings(_settings.DriveAssist with { TargetSpeedKmh = value });
    partial void OnDriveAssistLaunchRpmChanged(int value) => SaveDriveAssistSettings(_settings.DriveAssist with { LaunchRpmPercent = value });
    partial void OnDriveAssistSkidpadChanged(bool value) => SaveDriveAssistSettings(_settings.DriveAssist with { Skidpad = value });
    partial void OnDriveAssistTireTempFahrenheitChanged(bool value) => SaveDriveAssistSettings(_settings.DriveAssist with { TireTempFahrenheit = value });
    partial void OnDriveAssistAuthorityIndexChanged(int value)
    {
        if (Enum.IsDefined((DriveAssistAuthority)value))
            SaveDriveAssistSettings(_settings.DriveAssist with { Authority = (DriveAssistAuthority)value });
    }
    partial void OnDriveAssistShadowChanged(bool value) => SaveDriveAssistSettings(_settings.DriveAssist with { ShadowMode = value });
    partial void OnDriveAssistFairPlayChanged(bool value) => SaveDriveAssistSettings(_settings.DriveAssist with { FairPlay = value });
    partial void OnDriveAssistUseGameAbsChanged(bool value) => SaveDriveAssistSettings(_settings.DriveAssist with { UseGameAbs = value });
    partial void OnDriveAssistUseGameTractionControlChanged(bool value) =>
        SaveDriveAssistSettings(_settings.DriveAssist with { UseGameTractionControl = value });
    partial void OnAssistOfflineConfirmedChanged(bool value)
    {
        if (!value && !_settings.DriveAssist.ReadOnly && (IsDriveAssistArmed || IsConnectingDriveAssist))
            EmergencyDriveAssist();
    }
    partial void OnDriveAssistSteeringDeadzoneChanged(int value) => SaveDriveAssistSettings(_settings.DriveAssist with { SteeringDeadzonePercent = value });
    partial void OnDriveAssistSteeringDebounceChanged(int value) => SaveDriveAssistSettings(_settings.DriveAssist with { SteeringDebounceMs = value });
    partial void OnDriveAssistTightTurnSpeedLimitChanged(double value) =>
        SaveDriveAssistSettings(_settings.DriveAssist with { TightTurnSpeedLimitKmh = (int)Math.Round(value) });
    partial void OnDriveAssistLearnTightTurnSpeedChanged(bool value) =>
        SaveDriveAssistSettings(_settings.DriveAssist with { LearnTightTurnSpeed = value });
    partial void OnDriveAssistTargetSlipChanged(int value) => SaveDriveAssistSettings(_settings.DriveAssist with { TargetSlipPercent = value });
    partial void OnDriveAssistBrakeBiasChanged(int value) => SaveDriveAssistSettings(_settings.DriveAssist with { BrakeBiasPercent = value });
    partial void OnDriveAssistHandbrakeIndexChanged(int value)
    {
        if (value >= 0 && value < HandbrakeChoices.Length)
            SaveDriveAssistSettings(_settings.DriveAssist with { HandbrakeButton = HandbrakeChoices[value] });
    }

    private void SaveDriveAssistSettings(DriveAssistSettings settings)
    {
        settings = settings.Validated();
        if (_settings.DriveAssist == settings) return;
        var old = _settings.DriveAssist;
        _settings.DriveAssist = settings;
        RefreshAvailableDriveAssists(settings);
        OnPropertyChanged(nameof(DriveAssistPresetStatus));
        OnPropertyChanged(nameof(CanApplyDriveAssistPreset));
        OnPropertyChanged(nameof(CanRequestCruise));
        OnPropertyChanged(nameof(CanConnectDriveAssist));
        OnPropertyChanged(nameof(CanCheckShiftOutput));
        if (old.ReadOnly != settings.ReadOnly || old.Authority != settings.Authority)
        {
            _shiftInput.SetDriveAssistReadOnly(true);
            DisarmDriveAssist();
            DisableAutoShift();
        }
        else if (IsDriveAssistArmed)
        {
            if (!HasDriveAssistSelections) DisarmDriveAssist();
            else if (settings.ReadOnly)
            {
                lock (_driveAssistSync) _shadowAssist.Configure(PreviewOptions(settings));
            }
            else _shiftInput.ConfigureDriveAssist(settings);
        }
        DriveAssistError = _preferences.Save(_settings) ?? "";
        RefreshDriveAssist(includeReports: false);
    }

    private void RefreshAvailableDriveAssists(DriveAssistSettings settings)
    {
        var available = (settings with { Features = (DriveAssistFeature)DriveAssistSettings.FeatureMask }).EffectiveFeatures;
        foreach (var option in DriveAssistOptions)
            option.IsAvailable = available.HasFlag(option.Feature);
        HasDriveAssistSelections = settings.EffectiveFeatures != DriveAssistFeature.None;
        IsCruiseSelected = settings.EffectiveFeatures.HasFlag(DriveAssistFeature.Cruise);
    }

    [RelayCommand]
    private async Task ArmDriveAssistAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_settings.DriveAssist.ReadOnly && !AssistOfflineConfirmed)
        {
            DriveAssistError = TelemetryText.Get("EcuOfflineConfirm");
            return;
        }
        if (!CanConnectDriveAssist) return;
        DriveAssistError = "";
        if (_settings.DriveAssist.ReadOnly)
        {
            _shiftInput.SetDriveAssistReadOnly(true);
            Start();
            if (!IsListening) { DriveAssistError = StatusText; return; }
            lock (_driveAssistSync)
            {
                _shadowAssist.Configure(PreviewOptions(_settings.DriveAssist));
                _readOnlyAssistArmed = true;
            }
            RefreshDriveAssist(includeReports: true);
            return;
        }
        if (IsAutoShiftEnabled && !_shiftInput.IsForwarding)
        {
            DriveAssistError = TelemetryText.Get("EcuShiftModeConflict");
            return;
        }
        IsConnectingDriveAssist = true;
        _shiftInput.SetDriveAssistReadOnly(false);
        RefreshDriveAssist(includeReports: false);
        try
        {
            Start();
            if (!IsListening) { DriveAssistError = StatusText; return; }
            if (!_shiftInput.IsForwarding)
            {
                var error = await _shiftInput.ConnectAsync(_settings.AutomaticShift with
                    { GamepadMode = GamepadShiftMode.Combination, AutomaticClutch = false }, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                if (error is not null) { DriveAssistError = error; return; }
            }
            if (HasDriveAssistSelections && !_shiftInput.ConfigureDriveAssist(_settings.DriveAssist))
                DriveAssistError = _shiftInput.Error ?? TelemetryText.Get("EcuNeedsRelay");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            IsConnectingDriveAssist = false;
            RefreshAutoShift();
            RefreshDriveAssist(includeReports: true);
        }
    }

    [RelayCommand]
    private void DisarmDriveAssist()
    {
        ArmDriveAssistCommand.Cancel();
        lock (_driveAssistSync)
        {
            _readOnlyAssistArmed = false;
            _shadowAssist.Configure(new());
        }
        _shiftInput.ConfigureDriveAssist(null);
        DriveAssistError = "";
        RefreshDriveAssist(includeReports: true);
    }

    private static DriveAssistSettings PreviewOptions(DriveAssistSettings settings) =>
        settings.ShadowMode ? settings : settings with { Features = settings.Features & DriveAssistFeature.Feedback };

    [RelayCommand]
    private void EmergencyDriveAssist()
    {
        _shiftInput.SetDriveAssistReadOnly(true);
        CheckShiftOutputCommand.Cancel();
        DisarmDriveAssist();
        DisableAutoShift();
        DriveAssistError = TelemetryText.Get("EcuEmergencyStatus");
    }

    [RelayCommand]
    private void ExitAssistDrift()
    {
        if (!IsDriveAssistArmed && !IsAutoShiftEnabled) return;
        lock (_driveAssistSync)
            if (_readOnlyAssistArmed) _shadowAssist.RequestDriftExit();
        _shiftInput.ExitDriveAssistDrift();
        if (AutoShiftDriftMode) AutoShiftDriftMode = false;
        if (AutoShiftVirtualTcuModeIndex == (int)VirtualTcuMode.Drift)
            AutoShiftVirtualTcuModeIndex = (int)VirtualTcuMode.Comfort;
    }

    [RelayCommand]
    private void ResetAssistCalibration()
    {
        lock (_driveAssistSync) _shadowAssist.ResetCalibration();
        _shiftInput.ResetDriveAssistCalibration();
        RefreshDriveAssist(includeReports: false);
    }

    [RelayCommand]
    private void RequestCruise()
    {
        if (!CanRequestCruise) return;
        if (_settings.DriveAssist.ReadOnly)
        {
            lock (_driveAssistSync) _shadowAssist.RequestCruise(Environment.TickCount64);
        }
        else _shiftInput.SetDriveAssistCruise(true);
        RefreshDriveAssist(includeReports: false);
    }

    [RelayCommand]
    private void CancelCruise()
    {
        lock (_driveAssistSync) _shadowAssist.CancelCruise();
        _shiftInput.SetDriveAssistCruise(false);
        RefreshDriveAssist(includeReports: true);
    }

    [RelayCommand]
    private void ClearDriveAssistLog()
    {
        lock (_driveAssistSync) _shadowAssist.ResetLog();
        _shiftInput.ResetDriveAssistLog();
        _assistAnalysis.Reset();
        RefreshDiagnostics();
    }

    [RelayCommand]
    private void SaveDriveAssistRun(string? source)
    {
        var reports = CurrentAssistStatus(includeReports: true);
        var run = source switch { "trial" => reports.CruiseResult, "lap" => reports.PreviousLap, _ => reports.Segment };
        if (run is not { Seconds: >= 0.15 }) return;
        if (_settings.DriveAssist.ShadowMode) run = run with { IsPreview = true };
        var key = "ecu_" + run.Mode.ToString().ToLowerInvariant();
        _store.Save(new TestResult
        {
            Key = key, Title = TelemetryText.Get(key), Kind = PerformanceTestKind.DriveAssist,
            CarOrdinal = run.CarOrdinal, CarName = VehicleCatalog.Find(run.CarOrdinal)?.Name ?? TelemetryText.Format("CarUnknownTitle", run.CarOrdinal),
            CarClass = run.CarClass, CarPerformanceIndex = run.PerformanceIndex, DrivetrainType = run.Drivetrain,
            DurationSeconds = run.Seconds, PeakSpeedKmh = run.PeakSpeedKmh, SampleCount = run.SampleCount, DriveAssist = run,
        }, settings: _settings);
        DriveAssistError = _store.LastError ?? "";
        if (DriveAssistError.Length > 0) { StatusText = DriveAssistError; return; }
        RefreshHistory(force: true);
        StatusText = TelemetryText.Get("EcuSavedRun");
    }

    private DriveAssistStatus CurrentAssistStatus(bool includeReports)
    {
        lock (_driveAssistSync)
        {
            if (_readOnlyAssistArmed)
                return _shadowAssist.Snapshot(Environment.TickCount64, armed: true, includeReports);
        }
        return _shiftInput.GetDriveAssistStatus(includeReports);
    }

    private DriveAssistStatus RefreshDriveAssist(bool includeReports)
    {
        var state = CurrentAssistStatus(includeReports);
        RefreshAssistAnalysis(state, force: includeReports);
        LearnedTightTurnSpeedKmh = state.Armed ? state.LearnedTightTurnSpeedKmh : null;
        if (IsDriveAssistArmed && !state.Armed && _shiftInput.Error is { } error) DriveAssistError = error;
        IsDriveAssistArmed = state.Armed;
        IsCruiseBusy = state.CruiseActive || state.CruisePending;
        var activeLabels = state.Active == 0 ? Array.Empty<string>() : Enum.GetValues<DriveAssistFeature>()
            .Where(f => f != 0 && state.Active.HasFlag(f)).Select(f => TelemetryText.Get("Ecu" + f)).ToArray();
        var collisionStatus = state.Collision.PowerLimited ? TelemetryText.Get("EcuPhasePowerLimited") :
            state.Collision.Holding ? TelemetryText.Get("EcuPhaseImpactRecovery") : "";
        DriveAssistStatus = IsConnectingDriveAssist ? TelemetryText.Get("ConnectingRelay") : !state.Armed ? TelemetryText.Get("EcuDisarmed") :
            state.CruisePending ? TelemetryText.Get(_settings.DriveAssist.Skidpad ? "EcuSkidpadPending" : "EcuCruisePending") :
            !state.Fresh ? TelemetryText.Get("EcuWaiting") :
            TelemetryText.Format(_settings.DriveAssist.ReadOnly ? "EcuPreviewLive" : "EcuLive",
                collisionStatus.Length > 0 ? collisionStatus : TelemetryText.Get("EcuPhase" + state.Phase),
                activeLabels.Length == 0 ? TelemetryText.Get("NoneText") : string.Join(", ", activeLabels));
        var hudAssistStatus = IsConnectingDriveAssist ? TelemetryText.Get("HudAssistConnecting") : !state.Armed
            ? TelemetryText.Get(DriveAssistError.Length == 0 ? "HudAssistOff" :
                HasDriveAssistSelections ? "HudAssistFailed" : "HudAssistNoSelection")
            : _settings.DriveAssist.ReadOnly && state.Fresh ? TelemetryText.Get("EcuReadOnly")
            : !state.Fresh || state.CruisePending ? TelemetryText.Get("HudAssistWaiting")
            : collisionStatus.Length > 0 ? collisionStatus
            : activeLabels.Length > 0 ? TelemetryText.Format("HudAssistActiveFmt", activeLabels.Length == 1 ? activeLabels[0] : $"{activeLabels[0]} +{activeLabels.Length - 1}")
            : TelemetryText.Get(state.CruiseActive ? "HudAssistCruise" : "HudAssistReady");
        Hud.Live = Hud.Live with
        {
            DriveAssistStatus = hudAssistStatus,
            LearnedTightTurnSpeedText = LearnedTightTurnSpeedKmh is int speed
                ? TelemetryText.Format("HudLearnedTightTurnSpeedFmt", speed)
                : "",
        };
        if (!includeReports) return state;
        HasDriveAssistReport = state.Segment is { Seconds: >= 0.15 };
        HasDriveAssistLapReport = state.PreviousLap is { Seconds: >= 0.15 };
        HasDriveAssistTrialReport = state.CruiseResult is { Seconds: >= 1 };
        var preview = _settings.DriveAssist.ShadowMode;
        DriveAssistReport = state.Segment is { } segment ? FormatDriveAssistRun(segment with { IsPreview = preview }) : TelemetryText.Get("EcuNoLog");
        DriveAssistLapReport = state.PreviousLap is { } lap ? FormatDriveAssistRun(lap with { IsPreview = preview }) : "";
        DriveAssistTrialReport = state.CruiseResult is { } trial ? FormatDriveAssistRun(trial with { IsPreview = preview }) : TelemetryText.Get("EcuNoTrial");
        return state;
    }

    private static string FormatDriveAssistRun(DriveAssistRun run)
    {
        var rows = new List<string> { TelemetryText.Format("EcuRunStats", run.Seconds, run.MeanSpeedKmh, run.MeanLateralG, run.PeakLateralG) };
        if (run.IsPreview) rows.Insert(0, TelemetryText.Get("EcuGhostRun"));
        if (run.Mode is DriveAssistRunMode.Cruise or DriveAssistRunMode.Skidpad)
            rows.Add(TelemetryText.Format(run.Mode == DriveAssistRunMode.Skidpad ? "EcuRadiusStats" : "EcuSpeedStats",
                run.TargetSpeedKmh, run.SpeedErrorKmh, run.RadiusMeters));
        if (run.Mode == DriveAssistRunMode.Skidpad && run.BestSteadyLateralG > 0)
            rows.Add(TelemetryText.Format("EcuBestSteadyG", run.BestSteadyLateralG));
        if (run.Mode == DriveAssistRunMode.Lap) rows.Add(TelemetryText.Format("EcuLapNumber", run.LapNumber));
        foreach (var share in run.Shares.OrderByDescending(s => s.Seconds))
            rows.Add(TelemetryText.Format("EcuShare", TelemetryText.Get("EcuPhase" + share.Phase),
                TelemetryText.Get("Ecu" + share.Feature), share.Seconds, share.PhaseSeconds, share.Seconds / share.PhaseSeconds));
        if (run.HasFeedback && run.Shares.Length == 0) rows.Add(TelemetryText.Get("EcuNoIntervention"));
        var meaningful = run.Shares.Where(s => s.PhaseSeconds >= 0.6 && s.Seconds / s.PhaseSeconds >= 0.25).ToArray();
        if (meaningful.Any(s => s.Feature == DriveAssistFeature.Understeer && s.Phase is DriveAssistPhase.Corner or DriveAssistPhase.Trail))
            rows.Add(TelemetryText.Get("EcuTuneFront"));
        if (meaningful.Any(s => s.Feature is DriveAssistFeature.Traction or DriveAssistFeature.CornerTraction && s.Phase == DriveAssistPhase.CornerExit))
            rows.Add(TelemetryText.Get(run.Drivetrain == 1 ? "EcuTuneRearDiff" : "EcuTuneDriveDiff"));
        if (meaningful.Any(s => s.Feature == DriveAssistFeature.Abs)) rows.Add(TelemetryText.Get("EcuTuneBrake"));
        if (meaningful.Any(s => s.Feature == DriveAssistFeature.BottomOut)) rows.Add(TelemetryText.Get("EcuTuneTravel"));
        return string.Join(Environment.NewLine, rows);
    }
}
