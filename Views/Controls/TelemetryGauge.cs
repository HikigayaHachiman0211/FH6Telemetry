using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FH6Telemetry.Views.Controls;

/// <summary>环形仪表：速度/转速共用，刻度、指针、红线区与当前值全部按绑定值绘制，不做动画插值（避免掩盖真实数值）。</summary>
public sealed class TelemetryGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TelemetryGauge, double>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TelemetryGauge, double>(nameof(Maximum), 300);
    public static readonly StyledProperty<double> RedlineProperty =
        AvaloniaProperty.Register<TelemetryGauge, double>(nameof(Redline));
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<TelemetryGauge, string>(nameof(Title), "");
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<TelemetryGauge, string>(nameof(Unit), "");
    public static readonly StyledProperty<string> ValueTextProperty =
        AvaloniaProperty.Register<TelemetryGauge, string>(nameof(ValueText), "—");
    public static readonly StyledProperty<bool> IsDataValidProperty =
        AvaloniaProperty.Register<TelemetryGauge, bool>(nameof(IsDataValid), true);

    static TelemetryGauge()
    {
        AffectsRender<TelemetryGauge>(ValueProperty, MaximumProperty, RedlineProperty, TitleProperty,
            UnitProperty, ValueTextProperty, IsDataValidProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Redline { get => GetValue(RedlineProperty); set => SetValue(RedlineProperty, value); }
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public bool IsDataValid { get => GetValue(IsDataValidProperty); set => SetValue(IsDataValidProperty, value); }

    private static readonly Color AccentColor = Color.Parse("#FF9F43");
    private static readonly Color DimColor = Color.Parse("#3A4763");
    private static readonly Color TextColor = Color.Parse("#E6EBF5");
    private static readonly Color RedColor = Color.Parse("#E5534B");

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var size = Math.Min(bounds.Width, bounds.Height);
        if (size < 40) return;
        var center = new Point(bounds.Width / 2, bounds.Height / 2 + size * 0.04);
        var radius = size / 2 - 10;
        var startAngle = 135.0;
        var sweep = 270.0;
        var maximum = Maximum <= 0 ? 1 : Maximum;

        // 底环
        context.DrawGeometry(null, new Pen(new SolidColorBrush(DimColor), 8, lineCap: PenLineCap.Round),
            Arc(center, radius, startAngle, sweep));
        // 红线区
        if (Redline > 0 && Redline < maximum)
        {
            var redSweep = sweep * Redline / maximum;
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(140, RedColor.R, RedColor.G, RedColor.B)), 8, lineCap: PenLineCap.Round),
                Arc(center, radius, startAngle + redSweep, sweep - redSweep));
        }
        // 当前值
        var ratio = Math.Clamp(Value / maximum, 0, 1);
        if (IsDataValid && ratio > 0)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(AccentColor), 8, lineCap: PenLineCap.Round),
                Arc(center, radius, startAngle, sweep * ratio));
        }
        // 指针
        if (IsDataValid)
        {
            var angle = (startAngle + sweep * ratio) * Math.PI / 180.0;
            var tip = new Point(center.X + Math.Cos(angle) * (radius - 12), center.Y + Math.Sin(angle) * (radius - 12));
            context.DrawLine(new Pen(new SolidColorBrush(TextColor), 2), center, tip);
        }
        context.DrawEllipse(new SolidColorBrush(DimColor), null, center, 4, 4);

        var typeface = new Typeface(TelemetryLocalizationSource.Instance.CurrentFontFamily);
        var valueText = new FormattedText(IsDataValid ? ValueText : "—", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size * 0.145, new SolidColorBrush(TextColor));
        context.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - size * 0.10));
        if (!string.IsNullOrEmpty(Unit))
        {
            var unitText = new FormattedText(Unit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size * 0.07, new SolidColorBrush(Color.Parse("#8A96AD")));
            context.DrawText(unitText, new Point(center.X - unitText.Width / 2, center.Y + size * 0.06));
        }
        if (!string.IsNullOrEmpty(Title))
        {
            var titleText = new FormattedText(Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size * 0.075, new SolidColorBrush(Color.Parse("#8A96AD")));
            context.DrawText(titleText, new Point(center.X - titleText.Width / 2, center.Y + size * 0.20));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TelemetryText.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TelemetryText.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(TelemetryLanguage _)
    {
        var uiThread = Avalonia.Threading.Dispatcher.UIThread;
        if (!uiThread.CheckAccess())
        {
            uiThread.Post(InvalidateVisual);
            return;
        }
        InvalidateVisual();
    }

    private static StreamGeometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        var steps = Math.Max(2, (int)(sweepDegrees / 5));
        for (var i = 0; i <= steps; i++)
        {
            var angle = (startDegrees + sweepDegrees * i / steps) * Math.PI / 180.0;
            var point = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
            if (i == 0) ctx.BeginFigure(point, false);
            else ctx.LineTo(point);
        }
        ctx.EndFigure(false);
        return geometry;
    }
}
