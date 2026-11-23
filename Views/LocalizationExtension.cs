using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace FH6Telemetry.Views;

public sealed class LocalizedEntry : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs ValueChanged = new(nameof(Value));
    private readonly string _key;

    internal LocalizedEntry(string key) => _key = key;

    public string Value => TelemetryText.Get(_key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Notify() => PropertyChanged?.Invoke(this, ValueChanged);
}

public sealed class TelemetryLocalizationSource : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs FontChanged = new(nameof(CurrentFontFamily));
    private static readonly FontFamily ZhFont = new("Microsoft YaHei UI, Microsoft YaHei, Yu Gothic UI, Malgun Gothic, Inter, $Default");
    private static readonly FontFamily EnFont = new("Segoe UI, Inter, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic, $Default");
    private static readonly FontFamily JaFont = new("Yu Gothic UI, Meiryo UI, Microsoft YaHei UI, Malgun Gothic, Inter, $Default");
    private static readonly FontFamily KoFont = new("Malgun Gothic, Microsoft YaHei UI, Yu Gothic UI, Inter, $Default");

    public static TelemetryLocalizationSource Instance { get; } = new();

    private readonly ConcurrentDictionary<string, LocalizedEntry> _entries = new(StringComparer.Ordinal);

    private TelemetryLocalizationSource()
    {
        TelemetryText.LanguageChanged += OnLanguageChanged;
    }

    public FontFamily CurrentFontFamily => TelemetryText.CurrentLanguage switch
    {
        TelemetryLanguage.En => EnFont,
        TelemetryLanguage.Ja => JaFont,
        TelemetryLanguage.Ko => KoFont,
        _ => ZhFont,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizedEntry Entry(string key) => _entries.GetOrAdd(key, static k => new LocalizedEntry(k));

    private void OnLanguageChanged(TelemetryLanguage _)
    {
        var uiThread = Dispatcher.UIThread;
        if (!uiThread.CheckAccess())
        {
            uiThread.Post(NotifyAll);
            return;
        }
        NotifyAll();
    }

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, FontChanged);
        foreach (var entry in _entries.Values) entry.Notify();
    }
}

public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CompiledBinding.Create<LocalizedEntry, string>(static entry => entry.Value,
            TelemetryLocalizationSource.Instance.Entry(Key));
}

public sealed class LocFontExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CompiledBinding.Create<TelemetryLocalizationSource, FontFamily>(static source => source.CurrentFontFamily,
            TelemetryLocalizationSource.Instance);
}
