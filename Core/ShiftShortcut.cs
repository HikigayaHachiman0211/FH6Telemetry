using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FH6Telemetry;

public enum ShiftShortcutButton : ushort
{
    None = 0, DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
    Menu = 0x0010, View = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LeftBumper = 0x0100, RightBumper = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

public enum ShortcutAction { AutoShift, Clutch, Hud, RecordStart, RecordStop, DriveAssist, Emergency, DriftExit }

internal readonly record struct ShortcutBinding(ShortcutAction Action, string KeyName, ShiftShortcutButton Button)
{
    public byte Key { get; } = (byte)Math.Max(0, ShiftShortcut.ParseKey(KeyName));
}

internal static class ShiftShortcut
{
    internal const byte Escape = 0x1B;
    internal const ushort DigitalButtons = 0xF3FF;
    internal static readonly byte[] MouseKeys = [0x01, 0x02, 0x04, 0x05, 0x06];
    private static readonly Dictionary<byte, string> KeyNames = new()
    {
        [0x01] = "Mouse1", [0x02] = "Mouse2", [0x04] = "Mouse3", [0x05] = "Mouse4", [0x06] = "Mouse5",
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x14] = "CapsLock",
        [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x2C] = "PrintScreen",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x6A] = "NumMultiply", [0x6B] = "NumAdd",
        [0x6D] = "NumSubtract", [0x6E] = "NumDecimal", [0x6F] = "NumDivide", [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xA0] = "LShift", [0xA1] = "RShift", [0xA2] = "LCtrl", [0xA3] = "RCtrl", [0xA4] = "LAlt", [0xA5] = "RAlt",
        [0xAD] = "VolumeMute", [0xAE] = "VolumeDown", [0xAF] = "VolumeUp", [0xB0] = "MediaNext",
        [0xB1] = "MediaPrevious", [0xB2] = "MediaStop", [0xB3] = "MediaPlayPause",
        [0xBA] = "Oem1", [0xBB] = "OemPlus", [0xBC] = "OemComma", [0xBD] = "OemMinus", [0xBE] = "OemPeriod",
        [0xBF] = "Oem2", [0xC0] = "Oem3", [0xDB] = "Oem4", [0xDC] = "Oem5", [0xDD] = "Oem6", [0xDE] = "Oem7", [0xE2] = "Oem102",
    };
    private static readonly Dictionary<string, byte> NamedKeys = KeyNames.ToDictionary(entry => entry.Value, entry => entry.Key,
        StringComparer.OrdinalIgnoreCase);

    internal static readonly byte[] RecordableKeys = Enumerable.Range(1, 0xFE)
        .Select(key => (byte)key).Where(key => key == Escape || Name(key) is not null).ToArray();

    public static string? Name(byte key) => key switch
    {
        (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F).ToString(CultureInfo.InvariantCulture),
        >= 0x60 and <= 0x69 => "NumPad" + (key - 0x60).ToString(CultureInfo.InvariantCulture),
        _ => KeyNames.GetValueOrDefault(key),
    };

    public static int ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        var name = key.Trim().ToUpperInvariant();
        if (name.Length == 1 && name[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) return name[0];
        if (name[0] == 'F' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number is >= 1 and <= 24)
            return 0x70 + number - 1;
        if (name.Length == 7 && name.StartsWith("NUMPAD", StringComparison.Ordinal) && name[6] is >= '0' and <= '9')
            return 0x60 + name[6] - '0';
        return NamedKeys.TryGetValue(name, out var namedKey) ? namedKey : -1;
    }

    public static ShortcutBinding[] Bindings(AutomaticShiftSettings settings) =>
    [
        new(ShortcutAction.Emergency, settings.EmergencyKeyboardKey, settings.EmergencyGamepadButton),
        new(ShortcutAction.DriftExit, settings.DriftExitKeyboardKey, settings.DriftExitGamepadButton),
        new(ShortcutAction.AutoShift, settings.ToggleKeyboardKey, settings.ToggleGamepadButton),
        new(ShortcutAction.Clutch, settings.ClutchToggleKeyboardKey, settings.ClutchToggleGamepadButton),
        new(ShortcutAction.Hud, settings.HudToggleKeyboardKey, settings.HudToggleGamepadButton),
        new(ShortcutAction.RecordStart, settings.StartRecordingKeyboardKey, settings.StartRecordingGamepadButton),
        new(ShortcutAction.RecordStop, settings.StopRecordingKeyboardKey, settings.StopRecordingGamepadButton),
        new(ShortcutAction.DriveAssist, settings.DriveAssistToggleKeyboardKey, settings.DriveAssistToggleGamepadButton),
    ];

    public static string? Validate(AutomaticShiftSettings settings)
    {
        var keys = new HashSet<int>();
        var buttons = new HashSet<ShiftShortcutButton>();
        foreach (var (_, key, button) in Bindings(settings))
        {
            var parsedKey = ParseKey(key);
            if (ValidateKey(parsedKey, settings) is { } error) return error;
            if (!Enum.IsDefined(button)) return TelemetryText.Get("ScValInvalidPad");
            if (parsedKey > 0 && !keys.Add(parsedKey)) return TelemetryText.Get("ScValDupKb");
            if (button != ShiftShortcutButton.None && !buttons.Add(button)) return TelemetryText.Get("ScValDupPad");
        }
        return null;
    }

    internal static AutomaticShiftSettings Repair(AutomaticShiftSettings settings)
    {
        var keys = new HashSet<int>();
        var pads = new HashSet<ShiftShortcutButton>();

        string KeepKey(string name, string fallback = "")
        {
            var key = ParseKey(name);
            if (ValidateKey(key, settings) is not null) key = ParseKey(fallback);
            return key == 0 || !keys.Add(key) ? "" : Name((byte)key)!;
        }

        ShiftShortcutButton KeepPad(ShiftShortcutButton button) => Enum.IsDefined(button) && pads.Add(button)
            ? button : ShiftShortcutButton.None;

        return settings with
        {
            ToggleKeyboardKey = KeepKey(settings.ToggleKeyboardKey, "F9"), ToggleGamepadButton = KeepPad(settings.ToggleGamepadButton),
            ClutchToggleKeyboardKey = KeepKey(settings.ClutchToggleKeyboardKey), ClutchToggleGamepadButton = KeepPad(settings.ClutchToggleGamepadButton),
            HudToggleKeyboardKey = KeepKey(settings.HudToggleKeyboardKey), HudToggleGamepadButton = KeepPad(settings.HudToggleGamepadButton),
            StartRecordingKeyboardKey = KeepKey(settings.StartRecordingKeyboardKey), StartRecordingGamepadButton = KeepPad(settings.StartRecordingGamepadButton),
            StopRecordingKeyboardKey = KeepKey(settings.StopRecordingKeyboardKey), StopRecordingGamepadButton = KeepPad(settings.StopRecordingGamepadButton),
            DriveAssistToggleKeyboardKey = KeepKey(settings.DriveAssistToggleKeyboardKey), DriveAssistToggleGamepadButton = KeepPad(settings.DriveAssistToggleGamepadButton),
            EmergencyKeyboardKey = KeepKey(settings.EmergencyKeyboardKey, "Pause"), EmergencyGamepadButton = KeepPad(settings.EmergencyGamepadButton),
            DriftExitKeyboardKey = KeepKey(settings.DriftExitKeyboardKey, "F11"), DriftExitGamepadButton = KeepPad(settings.DriftExitGamepadButton),
        };
    }

    private static string? ValidateKey(int parsedKey, AutomaticShiftSettings settings)
    {
        if (parsedKey < 0) return TelemetryText.Get("ScValInvalidKb");
        if (parsedKey > 0 && settings.GamepadMode == GamepadShiftMode.Keyboard &&
            KeyboardShiftKeys.TryParse(settings, out var output) &&
            (parsedKey == output.Up || parsedKey == output.Down || parsedKey == output.Clutch || parsedKey == output.Throttle))
            return TelemetryText.Get("ScValConflictKb");
        return null;
    }
}
