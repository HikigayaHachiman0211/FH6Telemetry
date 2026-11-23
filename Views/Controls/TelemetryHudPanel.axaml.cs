using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FH6Telemetry.ViewModels;

namespace FH6Telemetry.Views.Controls;

public partial class TelemetryHudPanel : UserControl
{
    public static readonly StyledProperty<TelemetryHudReadings> ReadingsProperty =
        AvaloniaProperty.Register<TelemetryHudPanel, TelemetryHudReadings>(nameof(Readings));

    public TelemetryHudReadings Readings
    {
        get => GetValue(ReadingsProperty);
        set => SetValue(ReadingsProperty, value);
    }

    public TelemetryHudPanel() => AvaloniaXamlLoader.Load(this);
}
