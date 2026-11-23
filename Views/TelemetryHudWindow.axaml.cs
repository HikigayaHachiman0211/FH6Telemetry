using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using FH6Telemetry.ViewModels;

namespace FH6Telemetry.Views;

internal sealed partial class TelemetryHudWindow : Window
{
    private const int GwlExStyle = -20;
    private const uint Transparent = 0x20;
    private const uint Layered = 0x00080000;
    private const uint NoRedirectionBitmap = 0x00200000;
    private const uint NoActivate = 0x08000000;
    private const uint LayerAlpha = 2;
    private readonly TelemetryHudViewModel _model;

    public TelemetryHudWindow(TelemetryHudViewModel model)
    {
        Win32Properties.AddWindowStylesCallback(this, (style, extended) =>
            (style, extended | Transparent | Layered | NoActivate));
        AvaloniaXamlLoader.Load(this);
        _model = model;
        DataContext = model;
        ApplyPlacement();
        model.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => model.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TelemetryHudViewModel.Options) or nameof(TelemetryHudViewModel.ActiveScreen) or
            nameof(TelemetryHudViewModel.PanelHeight))
            ApplyPlacement();
    }

    private void ApplyPlacement()
    {
        if (_model.ActiveScreen is { } screen && screen.WorkingArea.Width > 0 && screen.WorkingArea.Height > 0)
        {
            var area = screen.WorkingArea;
            var bounds = _model.Placement(new Size(area.Width, area.Height), screen.Scaling);
            Width = bounds.Width / screen.Scaling;
            Height = bounds.Height / screen.Scaling;
            Position = new PixelPoint(area.X + (int)Math.Round(bounds.X), area.Y + (int)Math.Round(bounds.Y));
        }
        else
        {
            Width = _model.PreviewWidth;
            Height = _model.PreviewHeight;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        ApplyLayeredClickThrough();
        base.OnOpened(e);
    }

    private void ApplyLayeredClickThrough()
    {
        // A layered, transparent HWND lets clicks reach the game in another process.
        // Under DirectComposition (WS_EX_NOREDIRECTIONBITMAP), per-pixel alpha comes from the
        // transparent swapchain; only initialize LWA_ALPHA=255 on non-DirectComposition fallback.
        if (TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var window })
        {
            var exStyle = (ulong)GetExtendedStyle(window);
            if ((exStyle & NoRedirectionBitmap) == 0)
                SetLayeredWindowAttributes(window, 0, 255, LayerAlpha);
        }
    }

    private static nint GetExtendedStyle(nint window) =>
        nint.Size == 8 ? GetWindowLongPtrW(window, GwlExStyle) : GetWindowLongW(window, GwlExStyle);

    internal static string DisplayId(Screen screen)
    {
        // The friendly name may be identical on two connected displays.
        if (screen.TryGetPlatformHandle() is { HandleDescriptor: "HMonitor", Handle: var monitor })
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info) && !string.IsNullOrEmpty(info.Device)) return info.Device;
        }
        return $"{screen.DisplayName}|{screen.Bounds.X},{screen.Bounds.Y}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
