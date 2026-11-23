using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FH6Telemetry.Input;

internal sealed class PhysicalInputMonitor : IDisposable
{
    private const string WindowClassName = "FH6Telemetry.PhysicalInput";
    private const uint WmInput = 0x00FF, WmClose = 0x0010, WmDestroy = 0x0002;
    private const uint RidInput = 0x10000003, RidevInputSink = 0x0100, RidevRemove = 0x0001;
    private const ushort GenericDesktop = 0x01, KeyboardUsage = 0x06, MouseUsage = 0x02;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RawInputHeader>();
    private static readonly uint KeyboardPacketSize = HeaderSize + (uint)Marshal.SizeOf<RawKeyboard>();
    private static readonly uint MousePacketSize = HeaderSize + (uint)Marshal.SizeOf<RawMouse>();
    private static readonly uint InputSize = (uint)Marshal.SizeOf<RawInputPacket>();
    private static readonly uint DeviceSize = (uint)Marshal.SizeOf<RawInputDevice>();

    private readonly bool[] _down = new bool[256];
    private readonly ManualResetEventSlim? _ready;
    private readonly WindowProcedure _windowProcedure;
    private readonly Thread? _thread;
    private nint _window;
    private bool _keyboardAvailable, _mouseAvailable;
    private int _stop;
    private int _disposed;

    private PhysicalInputMonitor()
    {
        _windowProcedure = WindowProc;
        if (!OperatingSystem.IsWindows()) return;
        _ready = new ManualResetEventSlim();
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "FH6 physical input" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(1))) RequestStop();
    }

    public static PhysicalInputMonitor TryStart() => new();

    public event Action<byte>? KeyPressed;

    public bool IsKeyboardAvailable => Volatile.Read(ref _keyboardAvailable) && Volatile.Read(ref _stop) == 0;

    public bool BuffersKey(byte key) => Array.IndexOf(ShiftShortcut.MouseKeys, key) >= 0
        ? Volatile.Read(ref _mouseAvailable) && Volatile.Read(ref _stop) == 0 : IsKeyboardAvailable;

    public bool IsPhysicallyDown(byte key) => Volatile.Read(ref _down[key]) && IsAsyncDown(key);

    internal static bool IsAsyncDown(byte key) => GetAsyncKeyState(key) < 0;

    private void RunMessageLoop()
    {
        var instance = GetModuleHandleW(null);
        nint window = 0;
        var registeredClass = false;
        var registeredKeyboard = false;
        var registeredMouse = false;
        try
        {
            if (instance == 0 || !ReadRegistrations(out var devices)) return;
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                ClassName = WindowClassName,
            };
            registeredClass = RegisterClassExW(in windowClass) != 0;
            if (!registeredClass) return;
            window = CreateWindowExW(0, WindowClassName, "", 0, 0, 0, 0, 0, (nint)(-3), 0, instance, 0);
            if (window == 0) return;
            Volatile.Write(ref _window, window);
            if (Volatile.Read(ref _stop) != 0) return;

            registeredKeyboard = TryRegister(devices, KeyboardUsage, window);
            registeredMouse = TryRegister(devices, MouseUsage, window);
            if (!registeredKeyboard && !registeredMouse) return;
            Volatile.Write(ref _keyboardAvailable, registeredKeyboard);
            Volatile.Write(ref _mouseAvailable, registeredMouse);
            _ready!.Set();

            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Debug.WriteLine(error);
        }
        finally
        {
            Volatile.Write(ref _keyboardAvailable, false);
            Volatile.Write(ref _mouseAvailable, false);
            if (registeredKeyboard) UnregisterDevice(window, KeyboardUsage);
            if (registeredMouse) UnregisterDevice(window, MouseUsage);
            var remainingWindow = Interlocked.Exchange(ref _window, 0);
            if (remainingWindow != 0) DestroyWindow(remainingWindow);
            if (registeredClass) UnregisterClassW(WindowClassName, instance);
            _ready!.Set();
        }
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInput:
                ReadInput(lParam);
                break;
            case WmClose:
                UnregisterDevice(window, KeyboardUsage);
                UnregisterDevice(window, MouseUsage);
                DestroyWindow(window);
                return 0;
            case WmDestroy:
                Volatile.Write(ref _window, 0);
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ReadInput(nint handle)
    {
        var size = InputSize;
        var read = GetRawInputData(handle, RidInput, out var raw, ref size, HeaderSize);
        if (read == uint.MaxValue) return;
        if (raw.Header.Type == 1 && read >= KeyboardPacketSize && raw.Header.Device != 0 &&
            raw.Data.Keyboard.ExtraInformation != WindowsGamepadShift.InjectedKeyTag)
            ReadKeyboard(raw.Data.Keyboard);
        else if (raw.Header.Type == 0 && read == MousePacketSize && raw.Data.Mouse.ButtonFlags != 0 &&
                 raw.Data.Mouse.ExtraInformation != WindowsGamepadShift.InjectedKeyTag)
            ReadMouse(raw.Data.Mouse.ButtonFlags);
    }

    private void ReadKeyboard(RawKeyboard keyboard)
    {
        var extended = (keyboard.Flags & 0x02) != 0;
        var key = keyboard.VKey switch
        {
            0x10 => keyboard.MakeCode == 0x36 ? 0xA1 : 0xA0,
            0x11 => extended ? 0xA3 : 0xA2,
            0x12 => extended ? 0xA5 : 0xA4,
            _ => keyboard.VKey,
        };
        if (key is > 0 and < 0xFF)
            UpdateKey((byte)key, (keyboard.Flags & 0x01) == 0);
    }

    private void ReadMouse(ushort flags)
    {
        for (var index = 0; index < ShiftShortcut.MouseKeys.Length; index++)
        {
            var down = 1 << (index * 2);
            var key = ShiftShortcut.MouseKeys[index];
            if ((flags & down) != 0) UpdateKey(key, true);
            if ((flags & (down << 1)) != 0) UpdateKey(key, false);
        }
    }

    private void UpdateKey(byte key, bool down)
    {
        if (Volatile.Read(ref _down[key]) == down) return;
        Volatile.Write(ref _down[key], down);
        if (down) KeyPressed?.Invoke(key);
    }

    private static bool MatchesUsage(RawInputDevice device, ushort usage) =>
        device.UsagePage == GenericDesktop && device.Usage == usage;

    private static bool TryRegister(RawInputDevice[] devices, ushort usage, nint window)
    {
        if (Array.Exists(devices, device => MatchesUsage(device, usage))) return false;
        var device = new RawInputDevice { UsagePage = GenericDesktop, Usage = usage, Flags = RidevInputSink, Target = window };
        return RegisterRawInputDevices(in device, 1, DeviceSize);
    }

    private static bool ReadRegistrations(out RawInputDevice[] devices)
    {
        devices = [];
        uint count = 0;
        if (GetRegisteredRawInputDevices(null, ref count, DeviceSize) == uint.MaxValue) return false;
        if (count == 0) return true;
        devices = new RawInputDevice[count];
        return GetRegisteredRawInputDevices(devices, ref count, DeviceSize) != uint.MaxValue;
    }

    private static void UnregisterDevice(nint window, ushort usage)
    {
        if (!ReadRegistrations(out var devices) || !Array.Exists(devices, device => MatchesUsage(device, usage) && device.Target == window))
            return;
        var device = new RawInputDevice { UsagePage = GenericDesktop, Usage = usage, Flags = RidevRemove };
        RegisterRawInputDevices(in device, 1, DeviceSize);
    }

    private void RequestStop()
    {
        Volatile.Write(ref _stop, 1);
        var window = Volatile.Read(ref _window);
        if (window != 0) PostMessageW(window, WmClose, 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        RequestStop();
        if (_thread?.Join(TimeSpan.FromSeconds(1)) == true) _ready!.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type, Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode, Flags, Reserved, VKey;
        public uint Message, ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawMouse
    {
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawInputData
    {
        [FieldOffset(0)] public RawKeyboard Keyboard;
        [FieldOffset(0)] public RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputPacket
    {
        public RawInputHeader Header;
        public RawInputData Data;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern ushort RegisterClassExW(in WindowClass windowClass);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMessageW(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(in Message message);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool RegisterRawInputDevices(in RawInputDevice devices, uint count, uint size);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] RawInputDevice[]? devices, ref uint count, uint deviceSize);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetRawInputData(nint input, uint command, out RawInputPacket data, ref uint size, uint headerSize);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);
}
