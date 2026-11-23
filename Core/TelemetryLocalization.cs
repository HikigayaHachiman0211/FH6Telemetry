using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace FH6Telemetry;

public enum TelemetryLanguage { ZhCn = 0, En = 1, Ja = 2, Ko = 3 }

public static partial class TelemetryText
{
    private static int _currentLanguage = (int)TelemetryLanguage.ZhCn;

    public static TelemetryLanguage CurrentLanguage => (TelemetryLanguage)Volatile.Read(ref _currentLanguage);

    public static event Action<TelemetryLanguage>? LanguageChanged;

    public static IReadOnlyList<TelemetryLanguage> SupportedLanguages { get; } =
    [
        TelemetryLanguage.ZhCn,
        TelemetryLanguage.En,
        TelemetryLanguage.Ja,
        TelemetryLanguage.Ko,
    ];

    public static void SetLanguage(TelemetryLanguage language)
    {
        if (!Enum.IsDefined(language)) language = TelemetryLanguage.ZhCn;
        var previous = Interlocked.Exchange(ref _currentLanguage, (int)language);
        if (previous != (int)language) LanguageChanged?.Invoke(language);
    }

    public static TelemetryLanguage ParseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return TelemetryLanguage.ZhCn;
        var trimmed = code.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("en", StringComparison.Ordinal)) return TelemetryLanguage.En;
        if (trimmed.StartsWith("ja", StringComparison.Ordinal) || trimmed.StartsWith("jp", StringComparison.Ordinal))
            return TelemetryLanguage.Ja;
        if (trimmed.StartsWith("ko", StringComparison.Ordinal) || trimmed.StartsWith("kr", StringComparison.Ordinal))
            return TelemetryLanguage.Ko;
        return TelemetryLanguage.ZhCn;
    }

    public static string ToCode(TelemetryLanguage language) => language switch
    {
        TelemetryLanguage.En => "en",
        TelemetryLanguage.Ja => "ja",
        TelemetryLanguage.Ko => "ko",
        _ => "zh-CN",
    };

    public static string NativeDisplayName(TelemetryLanguage language) => language switch
    {
        TelemetryLanguage.En => "English",
        TelemetryLanguage.Ja => "日本語",
        TelemetryLanguage.Ko => "한국어",
        _ => "简体中文",
    };

    public static string HtmlLang(TelemetryLanguage language) => ToCode(language);

    public static IReadOnlyDictionary<string, string> DictionaryFor(TelemetryLanguage language) => language switch
    {
        TelemetryLanguage.En => En,
        TelemetryLanguage.Ja => Ja,
        TelemetryLanguage.Ko => Ko,
        _ => ZhCn,
    };

    public static string Get(string key) => Get(key, CurrentLanguage);

    public static string Get(string key, TelemetryLanguage language)
    {
        var dict = DictionaryFor(language);
        if (dict.TryGetValue(key, out var value)) return value;
        return ZhCn.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);
}
