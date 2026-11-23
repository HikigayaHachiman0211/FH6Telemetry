using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FH6Telemetry;

internal sealed class TelemetrySettingsFile(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "telemetry-settings.json");
    public string GearProfilesPath { get; } = Path.Combine(directory, "speed-gear-profiles.json");
    public string UpdatesDirectory { get; } = Path.Combine(directory, "updates");

    public string GetRecordDirectory(TelemetrySettings settings) => string.IsNullOrWhiteSpace(settings.DataDirectory)
        ? Path.Combine(directory, "telemetry")
        : settings.DataDirectory;

    public TelemetrySettings Load()
    {
        var settings = new TelemetrySettings();
        try
        {
            if (!File.Exists(FilePath)) return settings;
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return settings;

            settings.Language = TelemetryText.ToCode(TelemetryText.ParseCode(ReadText(root, "Language", settings.Language)));
            settings.AutoDownloadUpdates = ReadBool(root, nameof(TelemetrySettings.AutoDownloadUpdates), settings.AutoDownloadUpdates);
            settings.AboutStarPromptDismissed = ReadBool(root, nameof(TelemetrySettings.AboutStarPromptDismissed), settings.AboutStarPromptDismissed);
            settings.ListenAddress = ReadText(root, "ListenAddress", settings.ListenAddress);
            settings.Port = ReadInt(root, "Port", settings.Port);
            settings.AutoStart = ReadBool(root, "AutoStart", settings.AutoStart);
            settings.AutomaticShiftEnabled = ReadBool(root, "AutomaticShiftEnabled", settings.AutomaticShiftEnabled);
            settings.SpeedUnit = ReadText(root, "SpeedUnit", settings.SpeedUnit);
            settings.PowerUnit = ReadText(root, "PowerUnit", settings.PowerUnit);
            settings.TorqueUnit = ReadText(root, "TorqueUnit", settings.TorqueUnit);
            settings.UiRefreshHz = ReadInt(root, "UiRefreshHz", settings.UiRefreshHz);
            settings.SmoothingAlpha = ReadInt(root, "SmoothingAlpha", settings.SmoothingAlpha);
            settings.MaxTestSeconds = ReadDouble(root, "MaxTestSeconds", settings.MaxTestSeconds);
            settings.AutoSaveRecords = ReadBool(root, "AutoSaveRecords", settings.AutoSaveRecords);
            settings.DataDirectory = ReadText(root, "DataDirectory", settings.DataDirectory);
            settings.ChartWindowSeconds = ReadInt(root, "ChartWindowSeconds", settings.ChartWindowSeconds);
            settings.ShowGearChart = ReadBool(root, "ShowGearChart", settings.ShowGearChart);
            settings.SampleRecordHz = ReadInt(root, "SampleRecordHz", settings.SampleRecordHz);
            settings.MaxStoredRecords = Math.Clamp(ReadInt(root, "MaxStoredRecords", settings.MaxStoredRecords), 1, 10000);
            if (root.TryGetProperty("DriveAssist", out var assist) && assist.ValueKind == JsonValueKind.Object)
            {
                var defaults = settings.DriveAssist;
                settings.DriveAssist = new DriveAssistSettings
                {
                    Features = assist.TryGetProperty("Features", out var features) && features.ValueKind == JsonValueKind.Number &&
                        features.TryGetUInt64(out var flags) ? (DriveAssistFeature)flags : DriveAssistFeature.None,
                    UseGameAbs = ReadBool(assist, nameof(DriveAssistSettings.UseGameAbs), defaults.UseGameAbs),
                    UseGameTractionControl = ReadBool(assist, nameof(DriveAssistSettings.UseGameTractionControl), defaults.UseGameTractionControl),
                    TargetSpeedKmh = ReadInt(assist, "TargetSpeedKmh", defaults.TargetSpeedKmh),
                    LaunchRpmPercent = ReadInt(assist, "LaunchRpmPercent", defaults.LaunchRpmPercent),
                    Skidpad = ReadBool(assist, "Skidpad", defaults.Skidpad),
                    TireTempFahrenheit = ReadBool(assist, "TireTempFahrenheit", defaults.TireTempFahrenheit),
                    HandbrakeButton = ReadEnum(assist, "HandbrakeButton", defaults.HandbrakeButton),
                    Authority = ReadEnum(assist, "Authority", defaults.Authority),
                    ShadowMode = ReadBool(assist, "ShadowMode", defaults.ShadowMode),
                    FairPlay = ReadBool(assist, "FairPlay", defaults.FairPlay),
                    SteeringDeadzonePercent = ReadInt(assist, "SteeringDeadzonePercent", defaults.SteeringDeadzonePercent),
                    SteeringDebounceMs = ReadInt(assist, "SteeringDebounceMs", defaults.SteeringDebounceMs),
                    TightTurnSpeedLimitKmh = ReadInt(assist, nameof(DriveAssistSettings.TightTurnSpeedLimitKmh), defaults.TightTurnSpeedLimitKmh),
                    LearnTightTurnSpeed = ReadBool(assist, nameof(DriveAssistSettings.LearnTightTurnSpeed), defaults.LearnTightTurnSpeed),
                    TargetSlipPercent = ReadInt(assist, "TargetSlipPercent", defaults.TargetSlipPercent),
                    BrakeBiasPercent = ReadInt(assist, "BrakeBiasPercent", defaults.BrakeBiasPercent),
                }.Validated();
            }
            if (root.TryGetProperty("Hud", out var hud) && hud.ValueKind == JsonValueKind.Object)
            {
                var defaults = settings.Hud;
                var options = new TelemetryHudOptions
                {
                    DisplayId = ReadText(hud, "DisplayId", defaults.DisplayId),
                    HorizontalPercent = Math.Clamp(ReadDouble(hud, "HorizontalPercent", defaults.HorizontalPercent), 0, 100),
                    VerticalPercent = Math.Clamp(ReadDouble(hud, "VerticalPercent", defaults.VerticalPercent), 0, 100),
                    ScalePercent = Math.Clamp(ReadDouble(hud, "ScalePercent", defaults.ScalePercent),
                        TelemetryHudOptions.MinimumScale, TelemetryHudOptions.MaximumScale),
                    OpacityPercent = Math.Clamp(ReadDouble(hud, "OpacityPercent", defaults.OpacityPercent),
                        TelemetryHudOptions.MinimumOpacity, 100),
                    ShowSpeed = ReadBool(hud, "ShowSpeed", defaults.ShowSpeed),
                    ShowGear = ReadBool(hud, "ShowGear", defaults.ShowGear),
                    ShowRpm = ReadBool(hud, "ShowRpm", defaults.ShowRpm),
                    ShowPedals = ReadBool(hud, "ShowPedals", defaults.ShowPedals),
                    ShowShiftHint = ReadBool(hud, "ShowShiftHint", defaults.ShowShiftHint),
                    ShowTelemetryDetails = ReadBool(hud, "ShowTelemetryDetails", defaults.ShowTelemetryDetails),
                    ShowProtectionStatus = ReadBool(hud, "ShowProtectionStatus", defaults.ShowProtectionStatus),
                    EnableTextOutline = ReadBool(hud, "EnableTextOutline", defaults.EnableTextOutline),
                    ShowAutoShiftStatus = ReadBool(hud, "ShowShiftModeIndicator", defaults.ShowAutoShiftStatus),
                    ShowVehicleIdentity = ReadBool(hud, "ShowVehicleIdentity", defaults.ShowVehicleIdentity),
                    ShowDriveAssistStatus = ReadBool(hud, "ShowDriveAssistStatus", defaults.ShowDriveAssistStatus),
                    ShowLearnedTightTurnSpeed = ReadBool(hud, nameof(TelemetryHudOptions.ShowLearnedTightTurnSpeed), defaults.ShowLearnedTightTurnSpeed),
                };
                settings.Hud = options.HasVisibleReadings ? options : options with { ShowGear = true };
            }
            if (root.TryGetProperty("Retention", out var retention) && retention.ValueKind == JsonValueKind.Object)
            {
                var defaults = settings.Retention;
                var preset = (TelemetryRetentionPreset)ReadInt(retention, "Preset", (int)defaults.Preset);
                if (!Enum.IsDefined(preset)) preset = TelemetryRetentionPreset.Balanced;
                var (seconds, frames) = TelemetryRetention.Limits(preset);
                if (preset == TelemetryRetentionPreset.Custom)
                {
                    seconds = Math.Clamp(ReadInt(retention, "Seconds", seconds), 1, TelemetryRetention.MaximumSeconds);
                    frames = Math.Clamp(ReadInt(retention, "Frames", frames), 10, TelemetryRetention.MaximumFrames);
                }
                settings.Retention = new TelemetryRetention
                {
                    Preset = preset,
                    Seconds = seconds,
                    Frames = frames,
                    CleanupIntervalSeconds = Math.Clamp(ReadInt(retention, "CleanupIntervalSeconds", defaults.CleanupIntervalSeconds), 5, 3600),
                    ClearCache = ReadBool(retention, "ClearCache", defaults.ClearCache),
                    CleanHistory = ReadBool(retention, "CleanHistory", defaults.CleanHistory),
                    HistoryDays = Math.Clamp(ReadInt(retention, "HistoryDays", defaults.HistoryDays), 1, 3650),
                };
            }
            if (root.TryGetProperty("AutomaticShift", out var shift) && shift.ValueKind == JsonValueKind.Object)
            {
                var defaults = settings.AutomaticShift;
                var latency = ReadOptionalInt(shift, "ShiftLatencyMs");
                var options = defaults with
                {
                    GamepadMode = ReadEnum(shift, "GamepadMode", defaults.GamepadMode),
                    PressDurationMs = ReadInt(shift, "PressDurationMs", defaults.PressDurationMs),
                    ShiftLatencyMs = latency is >= 0 and <= AutomaticShiftSettings.MaximumShiftLatencyMs ? latency : null,
                    AutomaticClutch = ReadBool(shift, "AutomaticClutch", defaults.AutomaticClutch),
                    AutoDetectRaceTransmission = ReadBool(shift, "AutoDetectRaceTransmission", defaults.AutoDetectRaceTransmission),
                    UpshiftGripProtection = ReadBool(shift, "UpshiftGripProtection", defaults.UpshiftGripProtection),
                    ProtectLaunchWheelspin = ReadBool(shift, "ProtectLaunchWheelspin", defaults.ProtectLaunchWheelspin),
                    ProtectAirborne = ReadBool(shift, "ProtectAirborne", defaults.ProtectAirborne),
                    ProtectShiftReengagement = ReadBool(shift, "ProtectShiftReengagement", defaults.ProtectShiftReengagement),
                    AdaptiveGearMatching = ReadBool(shift, "AdaptiveGearMatching", defaults.AdaptiveGearMatching),
                    StrictSpeedGearMatching = ReadBool(shift, "StrictSpeedGearMatching", defaults.StrictSpeedGearMatching),
                    DriftMode = ReadBool(shift, "DriftMode", defaults.DriftMode),
                    EnableShiftShortcut = ReadBool(shift, "EnableShiftShortcut", defaults.EnableShiftShortcut),
                    Algorithm = ReadEnum(shift, "UpshiftAlgorithm", defaults.Algorithm),
                    VirtualTcuMode = ReadEnum(shift, "VirtualTcuMode", defaults.VirtualTcuMode),
                    MaximumGear = ReadOptionalInt(shift, "MaximumGear"),
                    KeyboardUpshift = ReadText(shift, "KeyboardUpshift", defaults.KeyboardUpshift),
                    KeyboardDownshift = ReadText(shift, "KeyboardDownshift", defaults.KeyboardDownshift),
                    KeyboardClutch = ReadText(shift, "KeyboardClutch", defaults.KeyboardClutch),
                    KeyboardThrottle = ReadText(shift, "KeyboardThrottle", defaults.KeyboardThrottle),
                    ToggleKeyboardKey = ReadText(shift, "ToggleKeyboardKey", defaults.ToggleKeyboardKey),
                    ToggleGamepadButton = ReadEnum(shift, "ToggleGamepadButton", defaults.ToggleGamepadButton),
                    DriveAssistToggleKeyboardKey = ReadText(shift, "DriveAssistToggleKeyboardKey", defaults.DriveAssistToggleKeyboardKey),
                    DriveAssistToggleGamepadButton = ReadEnum(shift, "DriveAssistToggleGamepadButton", defaults.DriveAssistToggleGamepadButton),
                    EmergencyKeyboardKey = ReadText(shift, "EmergencyKeyboardKey", defaults.EmergencyKeyboardKey),
                    EmergencyGamepadButton = ReadEnum(shift, "EmergencyGamepadButton", defaults.EmergencyGamepadButton),
                    DriftExitKeyboardKey = ReadText(shift, "DriftExitKeyboardKey", defaults.DriftExitKeyboardKey),
                    DriftExitGamepadButton = ReadEnum(shift, "DriftExitGamepadButton", defaults.DriftExitGamepadButton),
                    ClutchToggleKeyboardKey = ReadText(shift, "ClutchToggleKeyboardKey", defaults.ClutchToggleKeyboardKey),
                    ClutchToggleGamepadButton = ReadEnum(shift, "ClutchToggleGamepadButton", defaults.ClutchToggleGamepadButton),
                    HudToggleKeyboardKey = ReadText(shift, "HudToggleKeyboardKey", defaults.HudToggleKeyboardKey),
                    HudToggleGamepadButton = ReadEnum(shift, "HudToggleGamepadButton", defaults.HudToggleGamepadButton),
                    StartRecordingKeyboardKey = ReadText(shift, "StartRecordingKeyboardKey", defaults.StartRecordingKeyboardKey),
                    StartRecordingGamepadButton = ReadEnum(shift, "StartRecordingGamepadButton", defaults.StartRecordingGamepadButton),
                    StopRecordingKeyboardKey = ReadText(shift, "StopRecordingKeyboardKey", defaults.StopRecordingKeyboardKey),
                    StopRecordingGamepadButton = ReadEnum(shift, "StopRecordingGamepadButton", defaults.StopRecordingGamepadButton),
                };
                if (ShiftShortcut.Validate(options) is not null)
                    options = ShiftShortcut.Repair(options);
                if (options.TryValidate(out _)) settings.AutomaticShift = options;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or malformed preferences file should not stop telemetry reception.
        }
        return settings;
    }

    public string? Save(TelemetrySettings settings)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var shift = settings.AutomaticShift;
            var hud = settings.Hud;
            var root = new JsonObject
            {
                ["Language"] = TelemetryText.ToCode(TelemetryText.ParseCode(settings.Language)),
                [nameof(TelemetrySettings.AutoDownloadUpdates)] = settings.AutoDownloadUpdates,
                [nameof(TelemetrySettings.AboutStarPromptDismissed)] = settings.AboutStarPromptDismissed,
                ["ListenAddress"] = settings.ListenAddress,
                ["Port"] = settings.Port,
                ["AutoStart"] = settings.AutoStart,
                ["AutomaticShiftEnabled"] = settings.AutomaticShiftEnabled,
                ["SpeedUnit"] = settings.SpeedUnit,
                ["PowerUnit"] = settings.PowerUnit,
                ["TorqueUnit"] = settings.TorqueUnit,
                ["UiRefreshHz"] = settings.UiRefreshHz,
                ["SmoothingAlpha"] = settings.SmoothingAlpha,
                ["MaxTestSeconds"] = settings.MaxTestSeconds,
                ["AutoSaveRecords"] = settings.AutoSaveRecords,
                ["DataDirectory"] = settings.DataDirectory,
                ["ChartWindowSeconds"] = settings.ChartWindowSeconds,
                ["ShowGearChart"] = settings.ShowGearChart,
                ["SampleRecordHz"] = settings.SampleRecordHz,
                ["MaxStoredRecords"] = settings.MaxStoredRecords,
                ["DriveAssist"] = new JsonObject
                {
                    ["Features"] = (ulong)settings.DriveAssist.Features,
                    [nameof(DriveAssistSettings.UseGameAbs)] = settings.DriveAssist.UseGameAbs,
                    [nameof(DriveAssistSettings.UseGameTractionControl)] = settings.DriveAssist.UseGameTractionControl,
                    ["TargetSpeedKmh"] = settings.DriveAssist.TargetSpeedKmh,
                    ["LaunchRpmPercent"] = settings.DriveAssist.LaunchRpmPercent,
                    ["Skidpad"] = settings.DriveAssist.Skidpad,
                    ["TireTempFahrenheit"] = settings.DriveAssist.TireTempFahrenheit,
                    ["HandbrakeButton"] = (int)settings.DriveAssist.HandbrakeButton,
                    ["Authority"] = (int)settings.DriveAssist.Authority,
                    ["ShadowMode"] = settings.DriveAssist.ShadowMode,
                    ["FairPlay"] = settings.DriveAssist.FairPlay,
                    ["SteeringDeadzonePercent"] = settings.DriveAssist.SteeringDeadzonePercent,
                    ["SteeringDebounceMs"] = settings.DriveAssist.SteeringDebounceMs,
                    [nameof(DriveAssistSettings.TightTurnSpeedLimitKmh)] = settings.DriveAssist.TightTurnSpeedLimitKmh,
                    [nameof(DriveAssistSettings.LearnTightTurnSpeed)] = settings.DriveAssist.LearnTightTurnSpeed,
                    ["TargetSlipPercent"] = settings.DriveAssist.TargetSlipPercent,
                    ["BrakeBiasPercent"] = settings.DriveAssist.BrakeBiasPercent,
                },
                ["Hud"] = new JsonObject
                {
                    ["DisplayId"] = hud.DisplayId,
                    ["HorizontalPercent"] = hud.HorizontalPercent,
                    ["VerticalPercent"] = hud.VerticalPercent,
                    ["ScalePercent"] = hud.ScalePercent,
                    ["OpacityPercent"] = hud.OpacityPercent,
                    ["ShowSpeed"] = hud.ShowSpeed,
                    ["ShowGear"] = hud.ShowGear,
                    ["ShowRpm"] = hud.ShowRpm,
                    ["ShowPedals"] = hud.ShowPedals,
                    ["ShowShiftHint"] = hud.ShowShiftHint,
                    ["ShowTelemetryDetails"] = hud.ShowTelemetryDetails,
                    ["ShowProtectionStatus"] = hud.ShowProtectionStatus,
                    ["EnableTextOutline"] = hud.EnableTextOutline,
                    ["ShowShiftModeIndicator"] = hud.ShowAutoShiftStatus,
                    ["ShowVehicleIdentity"] = hud.ShowVehicleIdentity,
                    ["ShowDriveAssistStatus"] = hud.ShowDriveAssistStatus,
                    [nameof(TelemetryHudOptions.ShowLearnedTightTurnSpeed)] = hud.ShowLearnedTightTurnSpeed,
                },
                ["Retention"] = new JsonObject
                {
                    ["Preset"] = (int)settings.Retention.Preset,
                    ["Seconds"] = settings.Retention.Seconds,
                    ["Frames"] = settings.Retention.Frames,
                    ["CleanupIntervalSeconds"] = settings.Retention.CleanupIntervalSeconds,
                    ["ClearCache"] = settings.Retention.ClearCache,
                    ["CleanHistory"] = settings.Retention.CleanHistory,
                    ["HistoryDays"] = settings.Retention.HistoryDays,
                },
                ["AutomaticShift"] = new JsonObject
                {
                    ["GamepadMode"] = (int)shift.GamepadMode,
                    ["PressDurationMs"] = shift.PressDurationMs,
                    ["ShiftLatencyMs"] = shift.ShiftLatencyMs,
                    ["AutomaticClutch"] = shift.AutomaticClutch,
                    ["AutoDetectRaceTransmission"] = shift.AutoDetectRaceTransmission,
                    ["UpshiftGripProtection"] = shift.UpshiftGripProtection,
                    ["ProtectLaunchWheelspin"] = shift.ProtectLaunchWheelspin,
                    ["ProtectAirborne"] = shift.ProtectAirborne,
                    ["ProtectShiftReengagement"] = shift.ProtectShiftReengagement,
                    ["AdaptiveGearMatching"] = shift.AdaptiveGearMatching,
                    ["StrictSpeedGearMatching"] = shift.StrictSpeedGearMatching,
                    ["DriftMode"] = shift.DriftMode,
                    ["EnableShiftShortcut"] = shift.EnableShiftShortcut,
                    ["UpshiftAlgorithm"] = (int)shift.Algorithm,
                    ["VirtualTcuMode"] = (int)shift.VirtualTcuMode,
                    ["MaximumGear"] = shift.MaximumGear,
                    ["KeyboardUpshift"] = shift.KeyboardUpshift,
                    ["KeyboardDownshift"] = shift.KeyboardDownshift,
                    ["KeyboardClutch"] = shift.KeyboardClutch,
                    ["KeyboardThrottle"] = shift.KeyboardThrottle,
                    ["ToggleKeyboardKey"] = shift.ToggleKeyboardKey,
                    ["ToggleGamepadButton"] = (int)shift.ToggleGamepadButton,
                    ["DriveAssistToggleKeyboardKey"] = shift.DriveAssistToggleKeyboardKey,
                    ["DriveAssistToggleGamepadButton"] = (int)shift.DriveAssistToggleGamepadButton,
                    ["EmergencyKeyboardKey"] = shift.EmergencyKeyboardKey,
                    ["EmergencyGamepadButton"] = (int)shift.EmergencyGamepadButton,
                    ["DriftExitKeyboardKey"] = shift.DriftExitKeyboardKey,
                    ["DriftExitGamepadButton"] = (int)shift.DriftExitGamepadButton,
                    ["ClutchToggleKeyboardKey"] = shift.ClutchToggleKeyboardKey,
                    ["ClutchToggleGamepadButton"] = (int)shift.ClutchToggleGamepadButton,
                    ["HudToggleKeyboardKey"] = shift.HudToggleKeyboardKey,
                    ["HudToggleGamepadButton"] = (int)shift.HudToggleGamepadButton,
                    ["StartRecordingKeyboardKey"] = shift.StartRecordingKeyboardKey,
                    ["StartRecordingGamepadButton"] = (int)shift.StartRecordingGamepadButton,
                    ["StopRecordingKeyboardKey"] = shift.StopRecordingKeyboardKey,
                    ["StopRecordingGamepadButton"] = (int)shift.StopRecordingGamepadButton,
                },
            };
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return TelemetryText.Format("ErrSaveSettings", ex.Message);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ReadText(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int ReadInt(JsonElement root, string name, int fallback) => ReadOptionalInt(root, name) ?? fallback;

    private static T ReadEnum<T>(JsonElement root, string name, T fallback) where T : struct, Enum
    {
        if (ReadOptionalInt(root, name) is int number && Enum.ToObject(typeof(T), number) is T value && Enum.IsDefined(value))
            return value;
        return fallback;
    }

    private static int? ReadOptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double ReadDouble(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
        double.IsFinite(number) ? number : fallback;

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
}
