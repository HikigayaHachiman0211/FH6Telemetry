using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FH6Telemetry.ViewModels;
using FH6Telemetry.Views.Controls;

namespace FH6Telemetry.Views;

public partial class PerformanceTelemetryView : UserControl
{
    private async void ChooseAssistReplay(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } picker) return;
        var folders = await picker.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = TelemetryText.Get("EcuReplayFolder"),
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && ReferenceEquals(_viewModel, model))
            await model.LoadAssistReplayCommand.ExecuteAsync(path);
    }

    private async void ChooseAssistVision(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } picker) return;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = TelemetryText.Get("EcuLoadVision"),
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && ReferenceEquals(_viewModel, model))
            await model.LoadAssistVisionCommand.ExecuteAsync(path);
    }

    private PerformanceTelemetryViewModel? _viewModel;
    private TelemetryChart? _speedChart;
    private TelemetryChart? _gearChart;
    private TelemetryChart? _powerTorqueChart;
    private TelemetryChart? _accelerationChart;
    private TelemetryChart? _inputChart;
    private Window? _owner;

    internal TelemetryHudWindow? Hud { get; private set; }

    public PerformanceTelemetryView()
    {
        AvaloniaXamlLoader.Load(this);
        _speedChart = this.FindControl<TelemetryChart>("SpeedChart");
        _gearChart = this.FindControl<TelemetryChart>("GearChart");
        _powerTorqueChart = this.FindControl<TelemetryChart>("PowerTorqueChart");
        _accelerationChart = this.FindControl<TelemetryChart>("AccelerationChart");
        _inputChart = this.FindControl<TelemetryChart>("InputChart");
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) =>
        {
            TelemetryText.LanguageChanged += OnLanguageChanged;
            _owner = TopLevel.GetTopLevel(this) as Window;
            if (_owner is null) return;
            _owner.Screens.Changed += OnScreensChanged;
            _owner.PositionChanged += OnOwnerMoved;
            RefreshDisplays();
            RefreshForLanguageChange();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            TelemetryText.LanguageChanged -= OnLanguageChanged;
            Hud?.Close();
            if (_owner is null) return;
            _owner.Screens.Changed -= OnScreensChanged;
            _owner.PositionChanged -= OnOwnerMoved;
            _owner = null;
        };
    }

    private void OnToggleHud(object? sender, RoutedEventArgs e) => ToggleHud();

    private void ToggleHud()
    {
        if (Hud is { } openedHud) openedHud.Close();
        else ShowHud();
    }

    internal void ShowHud()
    {
        if (Hud is not null || _viewModel is null || _owner is not { } owner ||
            this.FindControl<Button>("HudToggleButton") is not { } toggle)
            return;

        RefreshMainScreen();
        var hud = new TelemetryHudWindow(_viewModel.Hud);
        void CloseHud(object? sender, EventArgs args) => hud.Close();
        Hud = hud;
        owner.Closed += CloseHud;
        toggle.Content = TelemetryText.Get("BtnCloseHud");
        hud.Closed += (_, _) =>
        {
            owner.Closed -= CloseHud;
            Hud = null;
            toggle.Content = TelemetryText.Get("BtnShowHud");
        };
        hud.Show();
    }

    private void Attach()
    {
        Hud?.Close();
        if (_viewModel is not null)
        {
            _viewModel.ChartsChanged -= OnChartsChanged;
            _viewModel.PropertyChanged -= OnPropertyChanged;
            _viewModel.HudToggleRequested -= ToggleHud;
        }
        _viewModel = DataContext as PerformanceTelemetryViewModel;
        if (_viewModel is null) return;
        _viewModel.ChartsChanged += OnChartsChanged;
        _viewModel.PropertyChanged += OnPropertyChanged;
        _viewModel.HudToggleRequested += ToggleHud;
        RefreshDisplays();
        OnChartsChanged();
    }

    private void OnScreensChanged(object? sender, EventArgs e) => RefreshDisplays();
    private void OnOwnerMoved(object? sender, PixelPointEventArgs e) => RefreshMainScreen();

    private void RefreshDisplays()
    {
        if (_owner is null || _viewModel is null) return;
        RefreshMainScreen();
        var displays = _owner.Screens.All.OrderBy(screen => screen.Bounds.X).ThenBy(screen => screen.Bounds.Y)
            .Select((screen, index) =>
            {
                var name = TelemetryText.Format("HudDisplayIndex", index + 1);
                if (!string.IsNullOrWhiteSpace(screen.DisplayName)) name += $" · {screen.DisplayName}";
                if (screen.IsPrimary) name += TelemetryText.Get("HudDisplayPrimary");
                return new TelemetryHudDisplay(TelemetryHudWindow.DisplayId(screen),
                    $"{name} · {screen.Bounds.Width} × {screen.Bounds.Height}", screen);
            }).ToArray();
        _viewModel.Hud.UpdateDisplays(displays);
    }

    private void OnLanguageChanged(TelemetryLanguage _)
    {
        var uiThread = Avalonia.Threading.Dispatcher.UIThread;
        if (!uiThread.CheckAccess())
        {
            uiThread.Post(RefreshForLanguageChange);
            return;
        }
        RefreshForLanguageChange();
    }

    private void RefreshForLanguageChange()
    {
        if (this.FindControl<Button>("HudToggleButton") is { } toggle)
            toggle.Content = TelemetryText.Get(Hud is not null ? "BtnCloseHud" : "BtnShowHud");
        RefreshDisplays();
        RefreshComboSelectionBoxes();
        RefreshTabStripLayout();
    }

    private void RefreshTabStripLayout()
    {
        if (this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is not { } tabs) return;
        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            foreach (var child in tab.GetVisualDescendants().OfType<Control>())
            {
                child.InvalidateMeasure();
                child.InvalidateArrange();
            }
            tab.InvalidateMeasure();
            tab.InvalidateArrange();
        }
        tabs.ItemsPanelRoot?.InvalidateMeasure();
        tabs.ItemsPanelRoot?.InvalidateArrange();
        tabs.InvalidateMeasure();
        tabs.InvalidateArrange();
        InvalidateMeasure();
    }

    private void RefreshComboSelectionBoxes()
    {
        if (_viewModel is null) return;
        _viewModel.SuppressIndexSideEffects = true;
        try
        {
            RefreshComboBox("ShiftOutputPicker");
            RefreshComboBox("SetupShiftOutputPicker");
            RefreshComboBox("ShiftStrategyPicker");
            RefreshComboBox("VirtualTcuModePicker");
            RefreshComboBox("RetentionPresetPicker");
            RefreshComboBox("DriveAssistPresetPicker");
        }
        finally
        {
            _viewModel.SuppressIndexSideEffects = false;
        }
    }

    private void RefreshComboBox(string name)
    {
        if (this.FindControl<ComboBox>(name) is not { } box) return;
        var index = box.SelectedIndex;
        if (index < 0) return;
        box.SelectedIndex = -1;
        box.SelectedIndex = index;
    }

    private void RefreshMainScreen()
    {
        if (_owner is not null && _viewModel is not null)
            _viewModel.Hud.MainScreen = _owner.Screens.ScreenFromWindow(_owner) ?? _owner.Screens.Primary;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceTelemetryViewModel.SelectedTab)) OnChartsChanged();
        if (e.PropertyName == nameof(PerformanceTelemetryViewModel.IsRecordingShortcut) && _viewModel?.IsRecordingShortcut == true)
            this.FindControl<Border>("ShortcutMouseCaptureArea")?.Focus();
    }

    private void OnShortcutCaptureEntered(object? sender, PointerEventArgs e) => _viewModel?.SetShortcutCapturePointer(true);

    private void OnShortcutCaptureExited(object? sender, PointerEventArgs e) => _viewModel?.SetShortcutCapturePointer(false);

    private void OnChartsChanged()
    {
        if (_viewModel is null) return;
        _speedChart?.SetSeries(_viewModel.SpeedSeries);
        _gearChart?.SetSeries(_viewModel.GearSeries);
        _powerTorqueChart?.SetSeries(_viewModel.PowerTorqueSeries);
        _accelerationChart?.SetSeries(_viewModel.AccelerationSeries);
        _inputChart?.SetSeries(_viewModel.InputSeries);
    }
}
