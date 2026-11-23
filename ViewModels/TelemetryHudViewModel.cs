using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FH6Telemetry.ViewModels;

public enum HudProtectionLevel { Off, Armed, Active }

public readonly record struct TelemetryHudProtections(
    HudProtectionLevel Launch = HudProtectionLevel.Off,
    HudProtectionLevel Airborne = HudProtectionLevel.Armed,
    HudProtectionLevel UpshiftGrip = HudProtectionLevel.Off,
    HudProtectionLevel Reengagement = HudProtectionLevel.Off,
    string DynamicAlert = "")
{
    private static readonly IBrush OffBackground = new SolidColorBrush(Color.Parse("#131D2B"));
    private static readonly IBrush OffBorder = new SolidColorBrush(Color.Parse("#253449"));
    private static readonly IBrush OffForeground = new SolidColorBrush(Color.Parse("#95A6BA"));
    private static readonly IBrush ArmedBackground = new SolidColorBrush(Color.Parse("#1A2D44"));
    private static readonly IBrush ArmedBorder = new SolidColorBrush(Color.Parse("#3B5D82"));
    private static readonly IBrush ArmedForeground = new SolidColorBrush(Color.Parse("#9ED8FF"));
    private static readonly IBrush ActiveBackground = new SolidColorBrush(Color.Parse("#5E310B"));
    private static readonly IBrush ActiveBorder = new SolidColorBrush(Color.Parse("#FFA63D"));
    private static readonly IBrush ActiveForeground = new SolidColorBrush(Color.Parse("#FFCB6B"));

    public bool HasDynamicAlert => !string.IsNullOrEmpty(DynamicAlert);
    public IBrush LaunchBackground => Background(Launch);
    public IBrush LaunchBorder => Border(Launch);
    public IBrush LaunchForeground => Foreground(Launch);
    public IBrush AirborneBackground => Background(Airborne);
    public IBrush AirborneBorder => Border(Airborne);
    public IBrush AirborneForeground => Foreground(Airborne);
    public IBrush UpshiftGripBackground => Background(UpshiftGrip);
    public IBrush UpshiftGripBorder => Border(UpshiftGrip);
    public IBrush UpshiftGripForeground => Foreground(UpshiftGrip);
    public IBrush ReengagementBackground => Background(Reengagement);
    public IBrush ReengagementBorder => Border(Reengagement);
    public IBrush ReengagementForeground => Foreground(Reengagement);
    public IBrush AlertBackground => ActiveBackground;
    public IBrush AlertBorder => ActiveBorder;
    public IBrush AlertForeground => ActiveForeground;

    private static IBrush Background(HudProtectionLevel level) => level switch
    {
        HudProtectionLevel.Active => ActiveBackground,
        HudProtectionLevel.Armed => ArmedBackground,
        _ => OffBackground,
    };

    private static IBrush Border(HudProtectionLevel level) => level switch
    {
        HudProtectionLevel.Active => ActiveBorder,
        HudProtectionLevel.Armed => ArmedBorder,
        _ => OffBorder,
    };

    private static IBrush Foreground(HudProtectionLevel level) => level switch
    {
        HudProtectionLevel.Active => ActiveForeground,
        HudProtectionLevel.Armed => ArmedForeground,
        _ => OffForeground,
    };
}

public readonly record struct TelemetryHudReadings(
    string Status, string Speed, string Gear, string Rpm,
    double RpmValue, double RpmLimit, string ShiftHint, string AutomaticShiftStatus = "",
    double ThrottlePercent = 0, double BrakePercent = 0, double SteerRatio = 0,
    string DetailText = "", string IntentBadge = "",
    double PowerKw = double.NaN, double TorqueNm = double.NaN, int DrivetrainType = -1,
    TelemetryHudProtections Protections = default, string VehicleName = "", int PerformanceIndex = 0, string DriveAssistStatus = "",
    string LearnedTightTurnSpeedText = "")
{
    public const double PedalBarMaxHeight = 34;
    private static readonly IBrush CyanRpmBrush = new SolidColorBrush(Color.Parse("#4ADAF4"));
    private static readonly IBrush AmberRpmBrush = new SolidColorBrush(Color.Parse("#FFB86B"));
    private static readonly IBrush RedRpmBrush = new SolidColorBrush(Color.Parse("#FF5468"));

    public bool HasData => Status is "";
    public string PerformanceIndexText => PerformanceIndex > 0 ? $"PI {PerformanceIndex}" : "";
    public double ThrottleBarHeight => Math.Clamp(ThrottlePercent, 0, 100) / 100.0 * PedalBarMaxHeight;
    public double BrakeBarHeight => Math.Clamp(BrakePercent, 0, 100) / 100.0 * PedalBarMaxHeight;
    public string ThrottlePercentText => $"{Math.Round(Math.Clamp(ThrottlePercent, 0, 100)):0}%";
    public string BrakePercentText => $"{Math.Round(Math.Clamp(BrakePercent, 0, 100)):0}%";
    public double SteerLeftWidth => Math.Clamp(-SteerRatio, 0, 1) * 17.5;
    public double SteerRightWidth => Math.Clamp(SteerRatio, 0, 1) * 17.5;
    public bool HasIntentBadge => !string.IsNullOrEmpty(IntentBadge);
    public bool HasLearnedTightTurnSpeed => !string.IsNullOrEmpty(LearnedTightTurnSpeedText);
    public string PowertrainText
    {
        get
        {
            var power = double.IsFinite(PowerKw)
                ? $"{PowerKw:0.0} kW / {TelemetryUnits.Power(PowerKw, "PS"):0.0} PS"
                : TelemetryText.Get("HudPowertrainMissingPwr");
            var torque = double.IsFinite(TorqueNm)
                ? $"{TorqueNm:0.0} N·m"
                : TelemetryText.Get("HudPowertrainMissingTrq");
            return TelemetryText.Format("HudPowertrainFmt", power, torque, DrivetrainNames.DrivetrainIcon(DrivetrainType));
        }
    }
    public IBrush RpmBarBrush => RpmLimit > 0 && RpmValue / RpmLimit >= 0.95 ? RedRpmBrush
        : RpmLimit > 0 && RpmValue / RpmLimit >= 0.85 ? AmberRpmBrush : CyanRpmBrush;
}

public sealed record TelemetryHudDisplay(string Id, string Name, Screen? Screen);

public sealed partial class TelemetryHudViewModel : ObservableObject
{
    public const double SpeedWidth = 116;
    public const double GearWidth = 86;
    public const double RpmWidth = 114;
    public const double PedalsWidth = 68;
    public const double BaseHeight = 80;
    private const double LearnedTightTurnRowHeight = 17;

    private readonly TelemetrySettingsFile _preferences;
    private readonly TelemetrySettings _settings;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private TelemetryHudOptions _options;

    [ObservableProperty] private TelemetryHudReadings _live = CreateWaitingReadings();
    [ObservableProperty] private string _transmissionStatus = "";
    [ObservableProperty] private string _autoShiftShortcutText = "";
    [ObservableProperty] private IReadOnlyList<TelemetryHudDisplay> _displays = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveScreen))]
    private Screen? _mainScreen;
    [ObservableProperty] private string _saveStatus = "";

    public TelemetryHudReadings Example => new(
        "", "148 km/h", "4", "6280 rpm", 6280, 8000,
        TelemetryText.Get("HudExampleHint"), TelemetryText.Get("HudExampleAutoStatus"),
        86, 12, 0.35, TelemetryText.Get("HudExampleDetail"), TelemetryText.Get("HudExampleIntent"), 268, 407.5, 2,
        new TelemetryHudProtections(Launch: HudProtectionLevel.Armed, Airborne: HudProtectionLevel.Active),
        VehicleCatalog.Find(2740) is { } car ? $"{car.MakeName} {car.Model}" : "Abarth 124 Spider", 685,
        TelemetryText.Format("HudAssistActiveFmt", TelemetryText.Get("EcuUndersteer")),
        TelemetryText.Format("HudLearnedTightTurnSpeedFmt", 66));

    internal TelemetryHudViewModel(TelemetrySettingsFile preferences, TelemetrySettings settings)
    {
        _preferences = preferences;
        _settings = settings;
        _options = settings.Hud;
        _live = CreateWaitingReadings();
    }

    private static TelemetryHudReadings CreateWaitingReadings() => new(
        TelemetryText.Get("HudWaitingTelemetry"), "—", "—", "—", 0, 8000, "", TelemetryText.Get("HudAutoOff"));

    public bool HasChanges => Options != _settings.Hud;
    public bool HasVisibleReadings => Options.HasVisibleReadings;
    public double HorizontalPercent { get => Options.HorizontalPercent; set => Move(value, VerticalPercent); }
    public double VerticalPercent { get => Options.VerticalPercent; set => Move(HorizontalPercent, value); }
    public double ScalePercent { get => Options.ScalePercent; set => Options = Options with { ScalePercent = value }; }
    public double OpacityPercent { get => Options.OpacityPercent; set => Options = Options with { OpacityPercent = value }; }
    public bool ShowSpeed { get => Options.ShowSpeed; set => Options = Options with { ShowSpeed = value }; }
    public bool ShowGear { get => Options.ShowGear; set => Options = Options with { ShowGear = value }; }
    public bool ShowRpm { get => Options.ShowRpm; set => Options = Options with { ShowRpm = value }; }
    public bool ShowPedals { get => Options.ShowPedals; set => Options = Options with { ShowPedals = value }; }
    public bool ShowShiftHint { get => Options.ShowShiftHint; set => Options = Options with { ShowShiftHint = value }; }
    public bool ShowTelemetryDetails { get => Options.ShowTelemetryDetails; set => Options = Options with { ShowTelemetryDetails = value }; }
    public bool ShowProtectionStatus { get => Options.ShowProtectionStatus; set => Options = Options with { ShowProtectionStatus = value }; }
    public bool ShowAutoShiftStatus { get => Options.ShowAutoShiftStatus; set => Options = Options with { ShowAutoShiftStatus = value }; }
    public bool ShowVehicleIdentity { get => Options.ShowVehicleIdentity; set => Options = Options with { ShowVehicleIdentity = value }; }
    public bool ShowDriveAssistStatus { get => Options.ShowDriveAssistStatus; set => Options = Options with { ShowDriveAssistStatus = value }; }
    public bool ShowLearnedTightTurnSpeed { get => Options.ShowLearnedTightTurnSpeed; set => Options = Options with { ShowLearnedTightTurnSpeed = value }; }
    public bool EnableTextOutline { get => Options.EnableTextOutline; set => Options = Options with { EnableTextOutline = value }; }
    public bool ShowTransmissionStatus => !string.IsNullOrEmpty(TransmissionStatus);
    public double OpacityFactor => OpacityPercent / 100;
    public IBrush RpmTrackBrush => WithAlpha(0x30, 0x41, 0x58, Math.Max(0.28, OpacityFactor));
    public IBrush SurfaceTrackBrush => WithAlpha(0x1A, 0x27, 0x38, OpacityFactor);
    public IBrush SurfaceBorderBrush => WithAlpha(0x4A, 0x61, 0x7D, Math.Max(0.42, OpacityFactor));

    private static SolidColorBrush WithAlpha(byte r, byte g, byte b, double factor) =>
        new(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(factor, 0, 1)), r, g, b));
    public double PanelWidth
    {
        get
        {
            var columns = (ShowSpeed ? 1 : 0) + (ShowGear ? 1 : 0) + (ShowRpm ? 1 : 0) + (ShowPedals ? 1 : 0);
            var widths = (ShowSpeed ? SpeedWidth : 0) + (ShowGear ? GearWidth : 0) +
                         (ShowRpm ? RpmWidth : 0) + (ShowPedals ? PedalsWidth : 0);
            var contentWidth = 30 + widths + 11 * Math.Max(0, columns - 1);
            var minimum = ShowTelemetryDetails ? 336 : ShowProtectionStatus ? 310 : ShowAutoShiftStatus || ShowVehicleIdentity ? 242 :
                ShowTransmissionStatus || ShowDriveAssistStatus ? 226 : 158;
            return Math.Max(minimum, contentWidth);
        }
    }

    public double PreviewWidth => PanelWidth * ScalePercent / 100;
    private double PanelBaseHeight => BaseHeight + (ShowTelemetryDetails ? 38 : 0) + (ShowProtectionStatus ? 25 : 0) +
        (ShowAutoShiftStatus ? 17 : 0) + (ShowTransmissionStatus ? 20 : 0) + (ShowVehicleIdentity ? 20 : 0) +
        (ShowDriveAssistStatus ? 17 : 0);
    public double PanelHeight => PanelBaseHeight + (ShowLearnedTightTurnSpeed && Live.HasLearnedTightTurnSpeed ? LearnedTightTurnRowHeight : 0);
    public double PreviewHeight => PanelHeight * ScalePercent / 100;
    public double ExamplePanelHeight => PanelBaseHeight + (ShowLearnedTightTurnSpeed ? LearnedTightTurnRowHeight : 0);
    public double ExamplePreviewHeight => ExamplePanelHeight * ScalePercent / 100;
    public Screen? ActiveScreen => SelectedDisplay?.Screen ?? MainScreen;

    public TelemetryHudDisplay? SelectedDisplay
    {
        get => Displays.FirstOrDefault(display => display.Id == Options.DisplayId);
        set
        {
            if (value is not null) Options = Options with { DisplayId = value.Id };
        }
    }

    partial void OnTransmissionStatusChanged(string? oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue) == string.IsNullOrEmpty(newValue)) return;
        OnPropertyChanged(nameof(PanelWidth));
        OnPropertyChanged(nameof(PreviewWidth));
        NotifyPanelHeightsChanged();
        OnPropertyChanged(nameof(ShowTransmissionStatus));
    }

    partial void OnLiveChanged(TelemetryHudReadings oldValue, TelemetryHudReadings newValue)
    {
        if (!ShowLearnedTightTurnSpeed || oldValue.HasLearnedTightTurnSpeed == newValue.HasLearnedTightTurnSpeed) return;
        OnPropertyChanged(nameof(PanelHeight));
        OnPropertyChanged(nameof(PreviewHeight));
    }

    private void NotifyPanelHeightsChanged()
    {
        OnPropertyChanged(nameof(PanelHeight));
        OnPropertyChanged(nameof(PreviewHeight));
        OnPropertyChanged(nameof(ExamplePanelHeight));
        OnPropertyChanged(nameof(ExamplePreviewHeight));
    }

    partial void OnOptionsChanged(TelemetryHudOptions? oldValue, TelemetryHudOptions newValue)
    {
        var before = oldValue ?? newValue;
        var after = newValue;
        if (before.HorizontalPercent != after.HorizontalPercent) OnPropertyChanged(nameof(HorizontalPercent));
        if (before.VerticalPercent != after.VerticalPercent) OnPropertyChanged(nameof(VerticalPercent));
        if (before.ScalePercent != after.ScalePercent)
        {
            OnPropertyChanged(nameof(ScalePercent));
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            OnPropertyChanged(nameof(ExamplePreviewHeight));
        }
        if (before.OpacityPercent != after.OpacityPercent)
        {
            OnPropertyChanged(nameof(OpacityPercent));
            OnPropertyChanged(nameof(OpacityFactor));
            OnPropertyChanged(nameof(RpmTrackBrush));
            OnPropertyChanged(nameof(SurfaceTrackBrush));
            OnPropertyChanged(nameof(SurfaceBorderBrush));
        }
        if (before.ShowSpeed != after.ShowSpeed) OnPropertyChanged(nameof(ShowSpeed));
        if (before.ShowGear != after.ShowGear) OnPropertyChanged(nameof(ShowGear));
        if (before.ShowRpm != after.ShowRpm) OnPropertyChanged(nameof(ShowRpm));
        if (before.ShowPedals != after.ShowPedals) OnPropertyChanged(nameof(ShowPedals));
        if (before.ShowShiftHint != after.ShowShiftHint) OnPropertyChanged(nameof(ShowShiftHint));
        if (before.ShowTelemetryDetails != after.ShowTelemetryDetails) OnPropertyChanged(nameof(ShowTelemetryDetails));
        if (before.ShowProtectionStatus != after.ShowProtectionStatus) OnPropertyChanged(nameof(ShowProtectionStatus));
        if (before.ShowAutoShiftStatus != after.ShowAutoShiftStatus) OnPropertyChanged(nameof(ShowAutoShiftStatus));
        if (before.ShowVehicleIdentity != after.ShowVehicleIdentity) OnPropertyChanged(nameof(ShowVehicleIdentity));
        if (before.ShowDriveAssistStatus != after.ShowDriveAssistStatus) OnPropertyChanged(nameof(ShowDriveAssistStatus));
        if (before.ShowLearnedTightTurnSpeed != after.ShowLearnedTightTurnSpeed) OnPropertyChanged(nameof(ShowLearnedTightTurnSpeed));
        if (before.EnableTextOutline != after.EnableTextOutline) OnPropertyChanged(nameof(EnableTextOutline));
        if (before.ShowTelemetryDetails != after.ShowTelemetryDetails ||
            before.ShowProtectionStatus != after.ShowProtectionStatus ||
            before.ShowAutoShiftStatus != after.ShowAutoShiftStatus || before.ShowVehicleIdentity != after.ShowVehicleIdentity ||
            before.ShowDriveAssistStatus != after.ShowDriveAssistStatus || before.ShowLearnedTightTurnSpeed != after.ShowLearnedTightTurnSpeed)
            NotifyPanelHeightsChanged();
        if (before.ShowSpeed != after.ShowSpeed || before.ShowGear != after.ShowGear ||
            before.ShowRpm != after.ShowRpm || before.ShowPedals != after.ShowPedals ||
            before.ShowTelemetryDetails != after.ShowTelemetryDetails ||
            before.ShowProtectionStatus != after.ShowProtectionStatus || before.ShowAutoShiftStatus != after.ShowAutoShiftStatus ||
            before.ShowVehicleIdentity != after.ShowVehicleIdentity || before.ShowDriveAssistStatus != after.ShowDriveAssistStatus)
        {
            OnPropertyChanged(nameof(PanelWidth));
            OnPropertyChanged(nameof(PreviewWidth));
        }
        if (before.HasVisibleReadings != after.HasVisibleReadings) OnPropertyChanged(nameof(HasVisibleReadings));
        if (before.DisplayId != after.DisplayId)
        {
            OnPropertyChanged(nameof(SelectedDisplay));
            OnPropertyChanged(nameof(ActiveScreen));
        }
        SaveStatus = "";
    }

    internal void UpdateDisplays(IReadOnlyList<TelemetryHudDisplay> connectedDisplays)
    {
        var displays = new List<TelemetryHudDisplay> { new("", TelemetryText.Get("HudDisplayPrimaryApp"), null) };
        displays.AddRange(connectedDisplays);
        if (!displays.Any(display => display.Id == Options.DisplayId))
            displays.Add(new(Options.DisplayId, TelemetryText.Get("HudDisplayDisconnected"), null));
        Displays = displays;
        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(ActiveScreen));
    }

    internal void RefreshLocalization()
    {
        if (!Live.HasData)
        {
            Live = Live with
            {
                Status = string.IsNullOrWhiteSpace(Live.Status) ? TelemetryText.Get("HudWaitingTelemetry") : Live.Status,
            };
        }
        OnPropertyChanged(nameof(Example));
        OnPropertyChanged(nameof(Live));
        UpdateDisplays(Displays.Where(display => display.Screen is not null).ToArray());
        SaveStatus = "";
    }

    internal void Move(double horizontal, double vertical) => Options = Options with
    {
        HorizontalPercent = Math.Round(Math.Clamp(horizontal, 0, 100)),
        VerticalPercent = Math.Round(Math.Clamp(vertical, 0, 100)),
    };

    internal Rect Placement(Size workingArea, double pixelsPerDip)
    {
        var width = PreviewWidth * pixelsPerDip;
        var height = PreviewHeight * pixelsPerDip;
        var fit = Math.Min(1, Math.Min(workingArea.Width / width, workingArea.Height / height));
        width *= fit;
        height *= fit;
        var travel = TravelWithin(workingArea, new Size(width, height), pixelsPerDip);
        return new Rect(travel.X + travel.Width * HorizontalPercent / 100,
            travel.Y + travel.Height * VerticalPercent / 100, width, height);
    }

    internal static Rect TravelWithin(Size area, Size hud, double pixelsPerDip)
    {
        var spareWidth = Math.Max(0, area.Width - hud.Width);
        var spareHeight = Math.Max(0, area.Height - hud.Height);
        var insetX = Math.Min(28 * pixelsPerDip, spareWidth / 2);
        var insetY = Math.Min(28 * pixelsPerDip, spareHeight / 2);
        return new Rect(insetX, insetY, spareWidth - 2 * insetX, spareHeight - 2 * insetY);
    }

    [RelayCommand]
    private void PlaceAt(string location)
    {
        var (x, y) = location switch
        {
            "TopLeft" => (0, 0),
            "TopRight" => (100, 0),
            "Center" => (50, 50),
            "BottomLeft" => (0, 100),
            "BottomRight" => (100, 100),
            _ => (50, 100),
        };
        Move(x, y);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Options = new();
        UpdateDisplays(Displays.Where(display => display.Screen is not null).ToArray());
    }

    [RelayCommand]
    private void Save()
    {
        if (!HasVisibleReadings)
        {
            SaveStatus = TelemetryText.Get("HudErrNeedMetric");
            return;
        }
        var prior = _settings.Hud;
        _settings.Hud = Options;
        var error = _preferences.Save(_settings);
        if (error is not null) _settings.Hud = prior;
        OnPropertyChanged(nameof(HasChanges));
        SaveStatus = error ?? TelemetryText.Get("HudSavedMsg");
    }
}
