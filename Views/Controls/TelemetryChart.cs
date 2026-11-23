using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FH6Telemetry.Views.Controls;

/// <summary>曲线序列：values 与 times 一一对应，单位用于轴标签，独立缩放避免不同量纲同轴误导。</summary>
public sealed record ChartSeries(string Name, double[] Times, double[] Values, Color Color, string Unit, bool OwnAxis = true,
    bool GearSteps = false);

/// <summary>
/// 轻量折线图：自绘，无第三方依赖。支持多序列、自动缩放、网格、图例与悬停十字光标。
/// 数据来自有界环形缓冲，绘制成本只与点数相关，长时间运行不会增长。
/// </summary>
public sealed class TelemetryChart : Control
{
    private Point? _hover;
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private PlotDrawing? _plotDrawing;

    public static readonly StyledProperty<string> XUnitProperty =
        AvaloniaProperty.Register<TelemetryChart, string>(nameof(XUnit), "s");
    public static readonly StyledProperty<double> XMinimumProperty =
        AvaloniaProperty.Register<TelemetryChart, double>(nameof(XMinimum), double.NaN);
    public static readonly StyledProperty<double> XMaximumProperty =
        AvaloniaProperty.Register<TelemetryChart, double>(nameof(XMaximum), double.NaN);

    static TelemetryChart() { AffectsRender<TelemetryChart>(XUnitProperty, XMinimumProperty, XMaximumProperty); }

    public string XUnit { get => GetValue(XUnitProperty); set => SetValue(XUnitProperty, value); }
    public double XMinimum { get => GetValue(XMinimumProperty); set => SetValue(XMinimumProperty, value); }
    public double XMaximum { get => GetValue(XMaximumProperty); set => SetValue(XMaximumProperty, value); }

    private static readonly Color GridColor = Color.Parse("#22304A");
    private static readonly Color BorderColor = Color.Parse("#2A3550");
    private static readonly Color TextColor = Color.Parse("#8A96AD");
    private static readonly Color LegendColor = Color.Parse("#C7D0E0");
    private static readonly Color HoverTextColor = Color.Parse("#E6EBF5");
    private static readonly IBrush PlotBackground = new SolidColorBrush(Color.Parse("#111828"));
    private static readonly Pen GridPen = new(new SolidColorBrush(GridColor), 1);
    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1);
    private static readonly Pen HoverPen = new(new SolidColorBrush(Color.Parse("#5A6A8C")), 1, new DashStyle([3, 3], 0));

    public void SetSeries(IReadOnlyList<ChartSeries> series)
    {
        _series = series ?? Array.Empty<ChartSeries>();
        _plotDrawing = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width < 40 || bounds.Height < 30) return;
        const double left = 44, right = 12, top = 10, bottom = 22;
        var plot = new Rect(left, top, Math.Max(1, bounds.Width - left - right), Math.Max(1, bounds.Height - top - bottom));
        var min = XMinimum;
        var max = XMaximum;
        if (_plotDrawing is not { } drawing || drawing.Area != plot ||
            !drawing.RequestedMinimum.Equals(min) || !drawing.RequestedMaximum.Equals(max))
            _plotDrawing = drawing = CreatePlotDrawing(plot, min, max);

        drawing.Background.Draw(context);
        if (_hover is { } hover && plot.Contains(hover) && drawing.Series.Length > 0)
            DrawHover(context, drawing, hover);
    }

    private PlotDrawing CreatePlotDrawing(Rect plot, double requestedMin, double requestedMax)
    {
        var background = new DrawingGroup();
        using var context = background.Open();
        context.FillRectangle(PlotBackground, plot);
        var visible = _series.Where(s => s.Values.Length > 0).ToArray();
        if (visible.Length == 0)
        {
            DrawText(context, TelemetryText.Get("ChartEmpty"), new Point(plot.X + 12, plot.Y + 12), 12, TextColor);
            return new(background, plot, requestedMin, requestedMax, 0, 1, visible);
        }

        double xMin = double.IsNaN(requestedMin) ? visible.Min(s => s.Times.Length > 0 ? s.Times[0] : 0) : requestedMin;
        double xMax = double.IsNaN(requestedMax) ? visible.Max(s => s.Times.Length > 0 ? s.Times[^1] : 1) : requestedMax;
        if (xMax - xMin < 0.001) xMax = xMin + 1;

        // 网格与 X 轴刻度
        if (visible.Length == 1 && visible[0].GearSteps)
        {
            ReadOnlySpan<int> labels = [10, 8, 6, 4, 2, 0, -1];
            foreach (var gear in labels)
            {
                var y = plot.Bottom - (gear + 1) * plot.Height / 11;
                context.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
                DrawText(context, GearLabel(gear), new Point(plot.X - 20, y - 6), 10, TextColor);
            }
        }
        else for (var i = 0; i <= 4; i++)
        {
            var y = plot.Y + plot.Height * i / 4;
            context.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.X + plot.Width * i / 4;
            context.DrawLine(GridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            var value = xMin + (xMax - xMin) * i / 4;
            DrawText(context, value.ToString("0.#", CultureInfo.InvariantCulture), new Point(x - 10, plot.Bottom + 3), 10, TextColor);
        }
        context.DrawRectangle(null, BorderPen, plot);

        // 每个序列独立缩放，避免把不同单位画在同一刻度上
        foreach (var series in visible)
            context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Color), 1.6, lineJoin: PenLineJoin.Bevel),
                CreateSeriesGeometry(series, plot, xMin, xMax));

        // 图例
        var legendX = plot.X + 6;
        var legendY = plot.Y + 4;
        foreach (var series in visible)
        {
            context.FillRectangle(new SolidColorBrush(series.Color), new Rect(legendX, legendY + 3, 10, 3));
            DrawText(context, series.Name + (string.IsNullOrEmpty(series.Unit) ? "" : $" ({series.Unit})"),
                new Point(legendX + 15, legendY - 2), 10, LegendColor);
            legendY += 13;
        }
        return new(background, plot, requestedMin, requestedMax, xMin, xMax, visible);
    }

    private static StreamGeometry CreateSeriesGeometry(ChartSeries series, Rect plot, double xMin, double xMax)
    {
        var min = series.GearSteps ? -1 : series.Values.Min();
        var max = series.GearSteps ? 10 : series.Values.Max();
        var xScale = plot.Width / (xMax - xMin);
        var yScale = plot.Height / (max - min < 1e-6 ? 1 : max - min);
        // Preserve the time order and extrema when several vertices share a small strip of the plot.
        var bucketWidth = Math.Max(2.5, plot.Width / 480);
        var aggregate = !series.GearSteps && series.Values.Length > Math.Ceiling(plot.Width / bucketWidth);
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        var started = false;
        var column = -1;
        var minIndex = 0;
        var maxIndex = 0;
        Point low = default;
        Point high = default;
        Point last = default;

        void Append(Point point)
        {
            if (!started) { path.BeginFigure(point, false); started = true; }
            else if (point != last)
            {
                if (series.GearSteps && point.Y != last.Y && point.X != last.X) path.LineTo(new Point(point.X, last.Y));
                path.LineTo(point);
            }
            last = point;
        }

        void AppendBucket()
        {
            if (column < 0) return;
            Append(minIndex <= maxIndex ? low : high);
            if (minIndex != maxIndex) Append(minIndex <= maxIndex ? high : low);
        }

        Point? end = null;
        for (var i = 0; i < series.Values.Length; i++)
        {
            var time = i < series.Times.Length ? series.Times[i] : i;
            if (time < xMin || time > xMax) continue;
            var x = (time - xMin) * xScale;
            var point = new Point(plot.X + x, plot.Bottom - (series.Values[i] - min) * yScale);
            if (!aggregate) { Append(point); continue; }

            if (!started) Append(point);
            end = point;
            var nextColumn = (int)(x / bucketWidth);
            if (nextColumn != column)
            {
                AppendBucket();
                column = nextColumn;
                low = high = point;
                minIndex = maxIndex = i;
                continue;
            }
            if (point.Y < low.Y) { low = point; minIndex = i; }
            if (point.Y > high.Y) { high = point; maxIndex = i; }
        }
        AppendBucket();
        if (end is { } finalPoint) Append(finalPoint);
        if (started) path.EndFigure(false);
        return geometry;
    }

    private void DrawHover(DrawingContext context, PlotDrawing drawing, Point hover)
    {
        var plot = drawing.Area;
        var timeAt = drawing.Minimum + (hover.X - plot.X) / plot.Width * (drawing.Maximum - drawing.Minimum);
        context.DrawLine(HoverPen, new Point(hover.X, plot.Y), new Point(hover.X, plot.Bottom));
        DrawText(context, $"{timeAt:0.##} {XUnit}", new Point(Math.Min(hover.X + 6, plot.Right - 70), plot.Y + 4), 10, HoverTextColor);
        var lineY = plot.Y + 16;
        foreach (var series in drawing.Series)
        {
            var index = NearestIndex(series, timeAt);
            if (index < 0) continue;
            var label = series.GearSteps ? $"{series.Name} {GearLabel(series.Values[index])}"
                : $"{series.Name}: {series.Values[index]:0.##} {series.Unit}";
            DrawText(context, label, new Point(Math.Min(hover.X + 6, plot.Right - 130), lineY), 10, series.Color);
            lineY += 12;
        }
    }

    private static int NearestIndex(ChartSeries series, double target)
    {
        var count = Math.Min(series.Times.Length, series.Values.Length);
        if (count == 0) return -1;
        var index = Array.BinarySearch(series.Times, 0, count, target);
        if (index >= 0) return index;
        var next = ~index;
        if (next == 0) return 0;
        if (next == count || series.GearSteps) return next - 1;
        return target - series.Times[next - 1] <= series.Times[next] - target ? next - 1 : next;
    }

    private static string GearLabel(double gear) => gear switch
    {
        0 => "R",
        -1 => "N",
        _ => gear.ToString("0", CultureInfo.InvariantCulture),
    };

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
            uiThread.Post(InvalidateForLocale);
            return;
        }
        InvalidateForLocale();
    }

    private void InvalidateForLocale()
    {
        _plotDrawing = null;
        InvalidateVisual();
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double size, Color color)
    {
        var typeface = new Typeface(TelemetryLocalizationSource.Instance.CurrentFontFamily);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, new SolidColorBrush(color));
        context.DrawText(formatted, origin);
    }

    private sealed record PlotDrawing(DrawingGroup Background, Rect Area, double RequestedMinimum, double RequestedMaximum,
        double Minimum, double Maximum, ChartSeries[] Series);
}
