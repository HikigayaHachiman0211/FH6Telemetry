using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FH6Telemetry.Input;

namespace FH6Telemetry;

public sealed partial class TelemetryApplication : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var launchDirectory = Environment.GetEnvironmentVariable("FH6_TELEMETRY_LAUNCH_DIR");
            var programDirectory = launchDirectory is not null && Path.IsPathFullyQualified(launchDirectory)
                ? launchDirectory
                : AppContext.BaseDirectory;
            try
            {
                var legacyDirectory = desktop.Args is ["--import-trainer-telemetry"]
                    ? Path.Combine(appData, "FH6AllInOneTrainer")
                    : null;
                var preferences = StandaloneTelemetryStorage.Open(Path.Combine(appData, "FH6Telemetry"),
                    legacyDirectory, out var notice, Path.Combine(programDirectory, "Data"));
                desktop.MainWindow = new TelemetryWindow(preferences, WindowsGamepadShift.CreateInput(), notice);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                desktop.MainWindow = new Window
                {
                    Title = TelemetryText.Get("StorageErrTitle"),
                    Width = 520,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new TextBlock
                    {
                        Text = TelemetryText.Format("StorageErrBody", ex.Message),
                        Margin = new Thickness(24),
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
