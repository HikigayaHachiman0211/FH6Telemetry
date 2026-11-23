using System;
using Avalonia;
using FH6Telemetry.Input;

namespace FH6Telemetry;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--probe-visible-gamepads"]) return WindowsGamepadShift.ProbeLocalGamepads();
        return AppBuilder.Configure<TelemetryApplication>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}
