using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace FH6Telemetry.Views.Controls;

/// <summary>支持高对比度描边的文本控件：自动根据前景色亮度选择高对比描边色，确保透明背景下文字清晰。</summary>
public class OutlinedTextBlock : TextBlock
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, double>(nameof(StrokeThickness), double.NaN);

    public static readonly StyledProperty<bool> IsOutlineEnabledProperty =
        AvaloniaProperty.Register<OutlinedTextBlock, bool>(nameof(IsOutlineEnabled), true);

    private static readonly Color DarkStroke = Color.FromArgb(245, 5, 8, 15);
    private static readonly Color LightStroke = Color.FromArgb(245, 248, 251, 255);

    private TextLayout? _cachedLayout;
    private Geometry? _cachedStrokeGeometry;
    private Color _cachedStrokeColor;
    private double _cachedPenThickness;
    private Pen? _cachedPen;

    static OutlinedTextBlock()
    {
        AffectsRender<OutlinedTextBlock>(StrokeProperty, StrokeThicknessProperty, IsOutlineEnabledProperty);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsOutlineEnabled
    {
        get => GetValue(IsOutlineEnabledProperty);
        set => SetValue(IsOutlineEnabledProperty, value);
    }

    public static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var srgb = channel / 255.0;
            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static Color ComputeHighContrastStrokeColor(Color foreground)
    {
        var darkContrast = ContrastRatio(foreground, DarkStroke);
        var lightContrast = ContrastRatio(foreground, LightStroke);
        var chosen = darkContrast >= lightContrast ? DarkStroke : LightStroke;
        if (foreground.A == 255) return chosen;

        var scaledAlpha = (byte)Math.Round(chosen.A * (foreground.A / 255.0));
        return Color.FromArgb(scaledAlpha, chosen.R, chosen.G, chosen.B);
    }

    public Color ResolveStrokeColor()
    {
        if (Stroke is ISolidColorBrush explicitSolid)
        {
            var c = explicitSolid.Color;
            if (explicitSolid.Opacity < 1)
            {
                var alpha = (byte)Math.Round(c.A * Math.Clamp(explicitSolid.Opacity, 0, 1));
                return Color.FromArgb(alpha, c.R, c.G, c.B);
            }
            return c;
        }

        var fgColor = Foreground switch
        {
            ISolidColorBrush fgSolid => fgSolid.Color,
            IGradientBrush { GradientStops.Count: > 0 } grad => grad.GradientStops[0].Color,
            _ => Colors.White,
        };
        return ComputeHighContrastStrokeColor(fgColor);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        var outerRadius = double.IsNaN(StrokeThickness)
            ? Math.Clamp(FontSize * 0.085, 1.05, 1.8)
            : Math.Max(0, StrokeThickness);

        if (IsOutlineEnabled && outerRadius > 0 && TextLayout is { } layout && !string.IsNullOrEmpty(Text))
        {
            var strokeGeometry = GetOrBuildStrokeGeometry(layout);
            if (strokeGeometry is not null)
            {
                var strokeColor = ResolveStrokeColor();
                var penThickness = outerRadius * 2.0;
                if (_cachedPen is null || _cachedStrokeColor != strokeColor ||
                    Math.Abs(_cachedPenThickness - penThickness) > 1e-6)
                {
                    _cachedStrokeColor = strokeColor;
                    _cachedPenThickness = penThickness;
                    _cachedPen = new Pen(new SolidColorBrush(strokeColor), penThickness,
                        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                }

                using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
                    context.DrawGeometry(null, _cachedPen, strokeGeometry);
            }
        }

        base.RenderTextLayout(context, origin);
    }

    private Geometry? GetOrBuildStrokeGeometry(TextLayout layout)
    {
        if (ReferenceEquals(_cachedLayout, layout))
            return _cachedStrokeGeometry;

        var group = new GeometryGroup();
        double currentY = 0;
        foreach (var line in layout.TextLines)
        {
            var currentX = line.Start;
            foreach (var run in line.TextRuns)
            {
                if (run is ShapedTextRun shaped && shaped.ShapedBuffer.Length > 0)
                {
                    var buffer = shaped.ShapedBuffer;
                    var baselineOrigin = new Point(currentX, currentY + line.Baseline);
                    using var glyphRun = new GlyphRun(
                        buffer.GlyphTypeface, buffer.FontRenderingEmSize, buffer.Text,
                        buffer, baselineOrigin, buffer.BidiLevel);
                    if (glyphRun.BuildGeometry() is { } geometry)
                        group.Children.Add(geometry);
                }
                if (run is DrawableTextRun drawable)
                    currentX += drawable.Size.Width;
            }
            currentY += line.Height;
        }

        _cachedLayout = layout;
        _cachedStrokeGeometry = group.Children.Count > 0 ? group : null;
        return _cachedStrokeGeometry;
    }
}
