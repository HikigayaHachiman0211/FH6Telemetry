using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FH6Telemetry;

internal sealed partial class AboutViewModel : ObservableObject, IDisposable
{
    private readonly TelemetrySettingsFile _preferences;
    private readonly TelemetrySettings _settings;
    private readonly GitHubUpdates _updates;
    private readonly Version _currentVersion;
    private string _statusKey = "";
    private string _statusArgument = "";

    public AboutViewModel(TelemetrySettingsFile preferences, TelemetrySettings settings, GitHubUpdates? updates = null)
    {
        _preferences = preferences;
        _settings = settings;
        var assembly = typeof(AboutViewModel).Assembly;
        var repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitHubRepository")?.Value;
        _updates = updates ?? new GitHubUpdates(repository);
        _currentVersion = assembly.GetName().Version ?? new Version(0, 0, 0);
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText = $"v{build?.Split('+', 2)[0] ?? _currentVersion.ToString(3)}";
        _showUpdatesFolder = Directory.Exists(preferences.UpdatesDirectory);
        TelemetryText.LanguageChanged += OnLanguageChanged;
    }

    public string VersionText { get; }
    public Uri? ProjectPage => _updates.ProjectPage;
    public Uri? ReleasesPage => _updates.ReleasesPage;
    public Uri? LatestReleasePage => _updates.LatestReleasePage;
    public bool HasRepository => ProjectPage is not null;
    public bool ShowStarPrompt => HasRepository && !_settings.AboutStarPromptDismissed;
    public string UpdateStatus => _statusKey.Length == 0 ? "" : TelemetryText.Format(_statusKey, _statusArgument);

    [ObservableProperty]
    private bool _showUpdatesFolder;

    public bool AutoDownloadUpdates
    {
        get => _settings.AutoDownloadUpdates;
        set
        {
            if (_settings.AutoDownloadUpdates == value) return;
            _settings.AutoDownloadUpdates = value;
            if (_preferences.Save(_settings) is not null)
            {
                _settings.AutoDownloadUpdates = !value;
                SetStatus("AboutSaveFailed");
            }
            OnPropertyChanged();
        }
    }

    public void CheckAtStartup()
    {
        if (AutoDownloadUpdates && CheckForUpdatesCommand.CanExecute(null))
            CheckForUpdatesCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(HasRepository))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        SetStatus("AboutChecking");
        try
        {
            var release = await _updates.FindLatestAsync(cancellationToken);
            if (release is null)
            {
                SetStatus("AboutNoRelease");
                return;
            }
            if (release.Version <= _currentVersion)
            {
                SetStatus("AboutUpToDate", release.Tag);
                return;
            }
            if (!AutoDownloadUpdates)
            {
                SetStatus("AboutUpdateAvailable", release.Tag);
                return;
            }
            if (release.PackageSize is null)
            {
                SetStatus("AboutNoPackage", release.Tag);
                return;
            }

            SetStatus("AboutDownloading", release.Tag);
            await _updates.DownloadAsync(release, _preferences.UpdatesDirectory, cancellationToken);
            ShowUpdatesFolder = true;
            SetStatus("AboutDownloaded", release.Tag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window closed while the request was active.
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            SetStatus("AboutUpdateFailed");
        }
    }

    [RelayCommand]
    private void DismissStarPrompt()
    {
        _settings.AboutStarPromptDismissed = true;
        if (_preferences.Save(_settings) is not null)
        {
            _settings.AboutStarPromptDismissed = false;
            SetStatus("AboutSaveFailed");
        }
        OnPropertyChanged(nameof(ShowStarPrompt));
    }

    [RelayCommand]
    private void OpenUpdatesFolder()
    {
        try
        {
            using var explorer = Process.Start(new ProcessStartInfo(_preferences.UpdatesDirectory) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            SetStatus("AboutOpenFailed");
        }
    }

    private void SetStatus(string key, string argument = "")
    {
        _statusKey = key;
        _statusArgument = argument;
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void OnLanguageChanged(TelemetryLanguage language) => OnPropertyChanged(nameof(UpdateStatus));

    public void Dispose()
    {
        TelemetryText.LanguageChanged -= OnLanguageChanged;
        CheckForUpdatesCommand.Cancel();
    }
}
