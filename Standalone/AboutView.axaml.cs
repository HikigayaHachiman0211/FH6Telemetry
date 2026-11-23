using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FH6Telemetry;

internal sealed partial class AboutView : UserControl
{
    public AboutView() => AvaloniaXamlLoader.Load(this);
}
