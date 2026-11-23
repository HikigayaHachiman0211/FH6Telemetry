using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FH6Telemetry.Views.Controls;

public sealed class DriveAssistAnalysisPlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<DriveAssistGPoint>> PointsProperty =
        AvaloniaProperty.Register<DriveAssistAnalysisPlot, IReadOnlyList<DriveAssistGPoint>>(nameof(Points), []);
    public static readonly StyledProperty<IReadOnlyList<DriveAssistGPoint>> EnvelopeProperty =
        AvaloniaProperty.Register<DriveAssistAnalysisPlot, IReadOnlyList<DriveAssistGPoint>>(nameof(Envelope), []);
    public static readonly StyledProperty<double?> SideslipProperty =
        AvaloniaProperty.Register<DriveAssistAnalysisPlot, double?>(nameof(Sideslip));
    public static readonly StyledProperty<IBrush> AxisBrushProperty =
        AvaloniaProperty.Register<DriveAssistAnalysisPlot, IBrush>(nameof(AxisBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush> SignalBrushProperty =
        AvaloniaProperty.Register<DriveAssistAnalysisPlot, IBrush>(nameof(SignalBrush), Brushes.DodgerBlue);

    static DriveAssistAnalysisPlot() =>
        AffectsRender<DriveAssistAnalysisPlot>(PointsProperty, EnvelopeProperty, SideslipProperty, AxisBrushProperty, SignalBrushProperty);

    public IReadOnlyList<DriveAssistGPoint> Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<DriveAssistGPoint> Envelope { get => GetValue(EnvelopeProperty); set => SetValue(EnvelopeProperty, value); }
    public double? Sideslip { get => GetValue(SideslipProperty); set => SetValue(SideslipProperty, value); }
    public IBrush AxisBrush { get => GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public IBrush SignalBrush { get => GetValue(SignalBrushProperty); set => SetValue(SignalBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var diameter = Math.Min(Bounds.Width * 0.59, Bounds.Height - 22);
        if (diameter < 40) return;
        var radius = (diameter - 12) / 2;
        var center = new Point(Bounds.Width * 0.29, Bounds.Height / 2);
        const double maximumG = 3;
        var grid = new Pen(AxisBrush, 1);
        var signal = new Pen(SignalBrush, 2);
        context.DrawLine(grid, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        context.DrawLine(grid, new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        for (var g = 1; g <= maximumG; g++)
        {
            var tick = radius * g / maximumG;
            context.DrawEllipse(null, grid, center, tick, tick);
            Text(context, g.ToString(CultureInfo.InvariantCulture) + "g", new(center.X + tick + 2, center.Y - 16));
        }

        Point Map(DriveAssistGPoint value) => new(center.X + Math.Clamp(value.LateralG, -maximumG, maximumG) / maximumG * radius,
            center.Y - Math.Clamp(value.LongitudinalG, -maximumG, maximumG) / maximumG * radius);
        foreach (var point in Points)
            if (double.IsFinite(point.LateralG) && double.IsFinite(point.LongitudinalG))
                context.DrawEllipse(SignalBrush, null, Map(point), 1.6, 1.6);
        for (var i = 1; i < Envelope.Count; i++)
            context.DrawLine(signal, Map(Envelope[i - 1]), Map(Envelope[i]));
        if (Envelope.Count > 2) context.DrawLine(signal, Map(Envelope[^1]), Map(Envelope[0]));

        var dial = new Point(Bounds.Width * 0.80, Bounds.Height * 0.50);
        var size = Math.Min(Bounds.Width * 0.13, Bounds.Height * 0.32);
        context.DrawEllipse(null, grid, dial, size, size);
        context.DrawLine(grid, dial, new(dial.X, dial.Y - size));
        var active = Sideslip is double angle && double.IsFinite(angle);
        var radians = active ? Math.Clamp(Sideslip!.Value, -90, 90) * Math.PI / 180 : 0;
        if (active)
            context.DrawLine(signal, dial, new(dial.X + Math.Sin(radians) * size, dial.Y - Math.Cos(radians) * size));
        Text(context, active ? Sideslip!.Value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°" : "—",
            new(dial.X - 16, dial.Y + size + 4));
    }

    private void Text(DrawingContext context, string caption, Point position)
    {
        var text = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, AxisBrush);
        context.DrawText(text, position);
    }
}
