namespace FH6Telemetry;

public sealed record TelemetryHudOptions
{
    public const double MinimumScale = 60;
    public const double MaximumScale = 160;
    public const double MinimumOpacity = 0;

    public string DisplayId { get; init; } = "";
    public double HorizontalPercent { get; init; } = 50;
    public double VerticalPercent { get; init; } = 100;
    public double ScalePercent { get; init; } = 100;
    public double OpacityPercent { get; init; } = 94;
    public bool ShowSpeed { get; init; } = true;
    public bool ShowGear { get; init; } = true;
    public bool ShowRpm { get; init; } = true;
    public bool ShowPedals { get; init; } = true;
    public bool ShowShiftHint { get; init; } = true;
    public bool ShowTelemetryDetails { get; init; } = true;
    public bool ShowProtectionStatus { get; init; } = true;
    public bool ShowAutoShiftStatus { get; init; } = true;
    public bool ShowVehicleIdentity { get; init; } = true;
    public bool ShowDriveAssistStatus { get; init; } = true;
    public bool ShowLearnedTightTurnSpeed { get; init; } = true;
    public bool EnableTextOutline { get; init; } = true;

    public bool HasVisibleReadings => ShowSpeed || ShowGear || ShowRpm;
}
