using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FH6Telemetry.ViewModels;
using FH6Telemetry.Views;

namespace FH6Telemetry;

internal sealed partial class TelemetryWindow : Window
{
    public TelemetryWindow(TelemetrySettingsFile preferences, IAutomaticShiftInput input, string? startupNotice = null)
    {
        AvaloniaXamlLoader.Load(this);
        var settings = preferences.Load();
        var model = new PerformanceTelemetryViewModel(preferences, input, settings);
        if (startupNotice is not null) model.StatusText = startupNotice;
        DataContext = model;

        var view = (PerformanceTelemetryView)Content!;
        var about = new AboutViewModel(preferences, settings);
        var aboutTab = (TabItem)Resources["AboutTab"]!;
        aboutTab.DataContext = about;
        view.FindControl<TabControl>("TelemetryTabs")!.Items.Add(aboutTab);

        Opened += (_, _) =>
        {
            view.ShowHud();
            about.CheckAtStartup();
        };
        Closed += (_, _) =>
        {
            about.Dispose();
            model.Dispose();
        };
    }
}
