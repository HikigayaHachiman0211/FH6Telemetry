using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using FH6Telemetry.ViewModels;

namespace FH6Telemetry.Views.Controls;

public partial class TelemetryHudSettingsView : UserControl
{
    private readonly Grid _frame;
    private readonly Canvas _map;
    private readonly Border _outline;
    private readonly Border _marker;
    private TelemetryHudViewModel? _model;
    private IPointer? _dragPointer;
    private Vector _grabOffset;
    private Rect _travel;

    public TelemetryHudSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        _frame = this.FindControl<Grid>("MapFrame")!;
        _map = this.FindControl<Canvas>("ScreenMap")!;
        _outline = this.FindControl<Border>("MapOutline")!;
        _marker = this.FindControl<Border>("MapMarker")!;
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) SetModel(DataContext as TelemetryHudViewModel);
        };
        AttachedToVisualTree += (_, _) => SetModel(DataContext as TelemetryHudViewModel);
        DetachedFromVisualTree += (_, _) =>
        {
            _dragPointer?.Capture(null);
            SetModel(null);
        };
    }

    private void SetModel(TelemetryHudViewModel? model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        RefreshMap();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TelemetryHudViewModel.Options) or nameof(TelemetryHudViewModel.ActiveScreen) or
            nameof(TelemetryHudViewModel.ShowTransmissionStatus))
            RefreshMap();
    }

    private void OnMapSizeChanged(object? sender, SizeChangedEventArgs e) => RefreshMap();

    private void RefreshMap()
    {
        if (_model is null || _frame.Bounds.Width <= 0 || _frame.Bounds.Height <= 0) return;
        var screen = _model.ActiveScreen;
        var work = screen?.WorkingArea;
        var area = work is { Width: > 0, Height: > 0 } bounds
            ? new Size(bounds.Width, bounds.Height) : new Size(1920, 1080);
        var dpi = screen?.Scaling ?? 1;
        var fit = Math.Min(_frame.Bounds.Width / area.Width, _frame.Bounds.Height / area.Height);
        var map = new Size(area.Width * fit, area.Height * fit);
        var hud = _model.Placement(area, dpi);
        _map.Width = _outline.Width = map.Width;
        _map.Height = _outline.Height = map.Height;
        _marker.Width = Math.Min(map.Width, Math.Max(36, hud.Width * fit));
        _marker.Height = Math.Min(map.Height, Math.Max(14, hud.Height * fit));
        _travel = TelemetryHudViewModel.TravelWithin(map, new Size(_marker.Width, _marker.Height), dpi * fit);
        Canvas.SetLeft(_marker, _travel.X + _travel.Width * _model.HorizontalPercent / 100);
        Canvas.SetTop(_marker, _travel.Y + _travel.Height * _model.VerticalPercent / 100);
    }

    private void OnMapPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is null || !e.GetCurrentPoint(_map).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(_map);
        var marker = new Rect(Canvas.GetLeft(_marker), Canvas.GetTop(_marker), _marker.Width, _marker.Height);
        _grabOffset = marker.Contains(point) ? point - marker.Position : new Vector(marker.Width / 2, marker.Height / 2);
        _dragPointer = e.Pointer;
        _dragPointer.Capture(_map);
        MoveMarker(point);
        e.Handled = true;
    }

    private void OnMapMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer == _dragPointer) MoveMarker(e.GetPosition(_map));
    }

    private void OnMapReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _dragPointer) return;
        MoveMarker(e.GetPosition(_map));
        _dragPointer = null;
        e.Pointer.Capture(null);
    }

    private void OnMapCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragPointer = null;

    private void MoveMarker(Point point)
    {
        if (_model is null) return;
        var target = point - _grabOffset;
        var horizontal = _travel.Width > 0 ? (target.X - _travel.X) / _travel.Width * 100 : _model.HorizontalPercent;
        var vertical = _travel.Height > 0 ? (target.Y - _travel.Y) / _travel.Height * 100 : _model.VerticalPercent;
        _model.Move(horizontal, vertical);
    }
}
