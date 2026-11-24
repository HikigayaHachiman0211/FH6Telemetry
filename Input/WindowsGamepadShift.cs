using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace FH6Telemetry.Input;

internal sealed class WindowsGamepadShift
{
    internal const uint InjectedKeyTag = 0x46483654;
    private const string GameProcessName = "ForzaHorizon6";
    private readonly object _sync = new();
    private nint _checkedWindow;
    private uint _checkedPid;
    private bool _checkedWindowIsGame;

    public static IAutomaticShiftInput CreateInput()
    {
        var windows = new WindowsGamepadShift();
        var monitor = PhysicalInputMonitor.TryStart();
        var reliable = monitor.IsKeyboardAvailable;
        var keyboard = new KeyboardShiftInput(windows.IsGameInForeground, IsManualShiftHeld,
            reliable ? monitor.IsPhysicallyDown : PhysicalInputMonitor.IsAsyncDown, SendKeys, reliable);
        Func<byte, bool> keyDown = reliable ? monitor.IsPhysicallyDown : PhysicalInputMonitor.IsAsyncDown;
        var relay = new GamepadShiftInput(ConnectRelay, windows.IsGameInForeground, () => false,
            keyboardSteer: () => (keyDown(0x27) ? 1 : 0) - (keyDown(0x25) ? 1 : 0));
        return new WindowsShiftInputRouter(keyboard, relay, windows.IsGameInForeground, monitor);
    }

    private static IShiftGamepad ConnectRelay()
    {
        var isolation = HidHideRelaySession.Activate();
        try
        {
            var gamepad = IsolatedGamepadRelay.Connect(ReadGamepad, ProbeVisibleGamepads, VigemShiftGamepad.Connect);
            return isolation is null ? gamepad : new PairedGamepad(gamepad, isolation);
        }
        catch
        {
            isolation?.Dispose();
            throw;
        }
    }

    private sealed class PairedGamepad(IShiftGamepad gamepad, HidHideRelaySession isolation) : IShiftGamepad
    {
        public bool ForwardsPhysicalInput => gamepad.ForwardsPhysicalInput;
        public ShiftGamepadReport ReadPhysical() => gamepad.ReadPhysical();
        public void Submit(ShiftGamepadReport report) => gamepad.Submit(report);
        public void SetAssistVibration(byte strength) => gamepad.SetAssistVibration(strength);

        public void Dispose()
        {
            try { gamepad.Dispose(); }
            finally { isolation.Dispose(); }
        }
    }

    internal bool IsGameInForeground()
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (_sync)
        {
            var window = GetForegroundWindow();
            if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0 || pid > int.MaxValue)
                return false;

            if (window != _checkedWindow || pid != _checkedPid)
            {
                _checkedWindow = window;
                _checkedPid = pid;
                _checkedWindowIsGame = IsGameProcess((int)pid);
            }

            return _checkedWindowIsGame && GetForegroundWindow() == window;
        }
    }

    private static bool IsGameProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    internal static bool IsApplicationInForeground()
    {
        var window = GetForegroundWindow();
        return window != 0 && GetWindowThreadProcessId(window, out var pid) != 0 && pid == Environment.ProcessId;
    }

    private static bool IsManualShiftHeld() => (ReadGamepadButtons() & ShiftGamepadReport.ManualShiftButtons) != 0;

    internal static ushort ReadGamepadButtons()
    {
        ushort buttons = 0;
        for (var slot = 0; slot < 4; slot++)
            if (ReadGamepad(slot) is ShiftGamepadReport state) buttons |= state.Buttons;
        return buttons;
    }

    private static ShiftGamepadReport? ReadGamepad(int slot) => XInputGetState((uint)slot, out var state) == 0
        ? new(state.Gamepad.Buttons, state.Gamepad.RightTrigger, state.Gamepad.LeftTrigger,
            state.Gamepad.LeftX, state.Gamepad.LeftY, state.Gamepad.RightX, state.Gamepad.RightY)
        : null;

    private static int ProbeVisibleGamepads()
    {
        var launcher = GamepadProbePath() ?? throw new InvalidOperationException(TelemetryText.Get("WinPadNeedPackagedExe"));

        // The launcher has a different executable path and is excluded from the HidHide allowlist.
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--probe-visible-gamepads");
        using var probe = Process.Start(start) ?? throw new InvalidOperationException(TelemetryText.Get("WinPadCannotStartProbe"));
        if (!probe.WaitForExit(2000))
        {
            try { probe.Kill(); }
            catch (InvalidOperationException) { }
            throw new InvalidOperationException(TelemetryText.Get("WinPadProbeTimeout"));
        }
        if ((probe.ExitCode & ~0xF) != 0x100) throw new InvalidOperationException(TelemetryText.Get("WinPadProbeFailed"));
        return probe.ExitCode & 0xF;
    }

    internal static string? GamepadProbePath()
    {
        var launcher = Environment.GetEnvironmentVariable("FH6_TELEMETRY_LAUNCH_EXE");
        if (!string.IsNullOrWhiteSpace(launcher)) return launcher;

        var sibling = Path.Combine(AppContext.BaseDirectory, "FH6Telemetry.exe");
        return !string.Equals(Environment.ProcessPath, sibling, StringComparison.OrdinalIgnoreCase) && File.Exists(sibling) ? sibling : null;
    }

    internal static int ProbeLocalGamepads() => 0x100 | IsolatedGamepadRelay.SlotMask(ReadGamepad);

    private static int SendKeys(byte[] keys, bool release)
    {
        const uint keyboardInput = 1, scanCode = 0x0008, keyUp = 0x0002;
        var events = new Input[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            var scan = MapVirtualKeyW(keys[index], 0);
            if (scan == 0) return 0;
            events[index] = new Input
            {
                Type = keyboardInput,
                Data = new InputData { Keyboard = new KeyboardInput
                {
                    ScanCode = (ushort)scan,
                    Flags = scanCode | (release ? keyUp : 0),
                    ExtraInfo = InjectedKeyTag,
                } },
            };
        }
        return (int)SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ControllerState
    {
        public uint PacketNumber;
        public GamepadState Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GamepadState
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)] private static extern uint XInputGetState(uint slot, out ControllerState state);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint SendInput(uint count, [In] Input[] events, int size);
}

internal sealed class WindowsShiftInputRouter(IAutomaticShiftInput keyboard, GamepadShiftInput relay,
    Func<bool> isGameInForeground, PhysicalInputMonitor monitor) : IAutomaticShiftInput
{
    private IAutomaticShiftInput? _active;
    private volatile bool _driveAssistReadOnly;

    public bool IsGameInForeground => isGameInForeground();
    public bool ManualShiftHeld => Volatile.Read(ref _active)?.ManualShiftHeld == true;
    public bool IsShiftPulseActive => Volatile.Read(ref _active)?.IsShiftPulseActive == true;
    public bool DriveAssistBlocksShift => Volatile.Read(ref _active)?.DriveAssistBlocksShift == true;
    public ShiftPedals? DriverPedals => Volatile.Read(ref _active)?.DriverPedals;
    public void SetAutomaticClutch(bool enabled) => relay.SetAutomaticClutch(enabled);
    public void SetTraceSink(Action<ShiftTraceEvent>? sink)
    {
        keyboard.SetTraceSink(sink);
        relay.SetTraceSink(sink);
    }
    public event Action<byte>? ShortcutKeyPressed
    {
        add => monitor.KeyPressed += value;
        remove => monitor.KeyPressed -= value;
    }
    public bool BuffersShortcutKey(byte key) => monitor.BuffersKey(key);
    public bool IsShortcutKeyDown(byte key) => OperatingSystem.IsWindows() && PhysicalInputMonitor.IsAsyncDown(key);
    public bool IsShortcutEditorInForeground => OperatingSystem.IsWindows() && WindowsGamepadShift.IsApplicationInForeground();
    public ushort ReadShortcutButtons() => !OperatingSystem.IsWindows() ? (ushort)0 :
        relay.TryGetForwardedButtons(out var buttons) ? buttons : WindowsGamepadShift.ReadGamepadButtons();
    public bool IsForwarding => Volatile.Read(ref _active)?.IsForwarding == true;
    public bool ConfigureDriveAssist(DriveAssistSettings? settings) => relay.ConfigureDriveAssist(settings);
    public ShiftGamepadReport? PhysicalDriveReport => relay.PhysicalDriveReport;
    public void SetDriveAssistReadOnly(bool readOnly)
    {
        _driveAssistReadOnly = readOnly;
        if (readOnly) keyboard.Release();
        relay.SetDriveAssistReadOnly(readOnly);
    }
    public void ExitDriveAssistDrift() => relay.ExitDriveAssistDrift();
    public void ResetDriveAssistCalibration() => relay.ResetDriveAssistCalibration();
    public DriveAssistFrame? FeedDriveAssist(TelemetrySample sample) => relay.FeedDriveAssist(sample);
    public DriveAssistStatus GetDriveAssistStatus(bool includeReports) => relay.GetDriveAssistStatus(includeReports);
    public void SetDriveAssistCruise(bool enable) => relay.SetDriveAssistCruise(enable);
    public void ResetDriveAssistLog() => relay.ResetDriveAssistLog();
    public bool HasPairedGamepad => HidHideRelaySession.IsPaired;
    public string? Error => Volatile.Read(ref _active)?.Error;
    public string? RoutingPath => WindowsGamepadShift.GamepadProbePath() is not null
        ? Environment.ProcessPath : null;

    public Task<string?> ConnectAsync(CancellationToken cancellationToken)
        => ConnectAsync(new AutomaticShiftSettings(), cancellationToken);

    public async Task<string?> ConnectAsync(AutomaticShiftSettings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return TelemetryText.Get("WinPadNeedWindows");
        cancellationToken.ThrowIfCancellationRequested();
        IAutomaticShiftInput input = settings.GamepadMode == GamepadShiftMode.Keyboard ? keyboard : relay;
        var previous = Interlocked.Exchange(ref _active, input);
        if (previous != input) previous?.Disconnect();
        if (input == keyboard && HasPairedGamepad)
        {
            try { await Task.Run(HidHideRelaySession.RestoreInterrupted, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { return error.Message; }
        }
        return await input.ConnectAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings) =>
        TryShift(direction, settings, null);

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger) =>
        TryShift(direction, settings, downshiftTrigger, null);

    public ShiftInputResult TryShift(ShiftDirection direction, AutomaticShiftSettings settings, byte? downshiftTrigger,
        ShiftTorquePlan? torquePlan) =>
        _driveAssistReadOnly ? ShiftInputResult.NotInForeground :
            Volatile.Read(ref _active)?.TryShift(direction, settings, downshiftTrigger, torquePlan) ?? ShiftInputResult.Failed;

    public void Release() => Volatile.Read(ref _active)?.Release();
    public void Disconnect() => Volatile.Read(ref _active)?.Disconnect();

    public void Dispose()
    {
        Interlocked.Exchange(ref _active, null);
        keyboard.Dispose();
        relay.Dispose();
        monitor.Dispose();
    }
}

internal sealed class VigemShiftGamepad : IShiftGamepad
{
    private readonly object _feedbackSync = new();
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _controller;
    private readonly uint _physicalSlot;
    private Vibration _gameVibration;
    private ushort _assistVibration;
    private bool _disposed;

    private VigemShiftGamepad(ViGEmClient client, IXbox360Controller controller, int physicalSlot)
    {
        _client = client;
        _controller = controller;
        _physicalSlot = (uint)physicalSlot;
        _controller.FeedbackReceived += ForwardVibration;
    }

    public static IShiftGamepad Connect(int physicalSlot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(TelemetryText.Get("WinPadNeedWindows"));
        ViGEmClient client;
        try { client = new ViGEmClient(); }
        catch (VigemBusNotFoundException ex) { throw new InvalidOperationException(TelemetryText.Get("WinPadNoViGEm"), ex); }
        catch (VigemBusAccessFailedException ex) { throw new InvalidOperationException(TelemetryText.Get("WinPadAccessViGEm"), ex); }
        catch (VigemBusVersionMismatchException ex) { throw new InvalidOperationException(TelemetryText.Get("WinPadMismatchViGEm"), ex); }

        IXbox360Controller? controller = null;
        try
        {
            controller = client.CreateXbox360Controller();
            controller.AutoSubmitReport = false;
            controller.Connect();
            return new VigemShiftGamepad(client, controller, physicalSlot);
        }
        catch (Exception ex)
        {
            Close(client, controller);
            if (ex is VigemNoFreeSlotException)
                throw new InvalidOperationException(TelemetryText.Get("WinPadNoFreeSlot"), ex);
            throw;
        }
    }

    public void Submit(ShiftGamepadReport report)
    {
        _controller.ButtonState = report.Buttons;
        _controller.LeftTrigger = report.LeftTrigger;
        _controller.RightTrigger = report.RightTrigger;
        _controller.LeftThumbX = report.LeftX;
        _controller.LeftThumbY = report.LeftY;
        _controller.RightThumbX = report.RightX;
        _controller.RightThumbY = report.RightY;
        _controller.SubmitReport();
    }

    private void ForwardVibration(object? sender, Xbox360FeedbackReceivedEventArgs args)
    {
        lock (_feedbackSync)
        {
            if (_disposed) return;
            _gameVibration = new Vibration { Left = (ushort)(args.LargeMotor * 257), Right = (ushort)(args.SmallMotor * 257) };
            SendVibration();
        }
    }

    public void SetAssistVibration(byte strength)
    {
        lock (_feedbackSync)
        {
            if (_disposed) return;
            _assistVibration = (ushort)(strength * 257);
            SendVibration();
        }
    }

    private void SendVibration()
    {
        var vibration = new Vibration { Left = Math.Max(_gameVibration.Left, _assistVibration), Right = _gameVibration.Right };
        XInputSetState(_physicalSlot, in vibration);
    }

    public void Dispose()
    {
        lock (_feedbackSync)
        {
            if (_disposed) return;
            _disposed = true;
            _controller.FeedbackReceived -= ForwardVibration;
            var vibration = new Vibration();
            XInputSetState(_physicalSlot, in vibration);
        }
        Close(_client, _controller);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration
    {
        public ushort Left, Right;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)] private static extern uint XInputSetState(uint slot, in Vibration vibration);

    private static void Close(ViGEmClient client, IXbox360Controller? controller)
    {
        try { controller?.Disconnect(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            try { (controller as IDisposable)?.Dispose(); }
            finally { client.Dispose(); }
        }
    }
}
