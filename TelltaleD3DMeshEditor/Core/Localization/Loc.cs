using System.Globalization;

namespace TelltaleD3DMeshEditor.Core.Localization;

/// <summary>
/// Central UI text lookup. Call <see cref="Initialize"/> once at startup, then use <see cref="T(string)"/>
/// everywhere a literal string used to be. Missing keys fall back to the English baseline, and finally to
/// the key itself, so the UI never throws because a translation is incomplete.
/// </summary>
public static class Loc
{
    private static Language _current = LanguageCatalog.LoadEmbeddedEnglish();
    private static Language _english = LanguageCatalog.LoadEmbeddedEnglish();

    /// <summary>The currently active language.</summary>
    public static Language Current => _current;

    /// <summary>The active language code (e.g. "en", "pt-BR"). Convenience for persistence.</summary>
    public static string CurrentCode => _current.Code;

    /// <summary>Languages available in the picker (lightweight <c>_meta</c> scan of the Languages folder).</summary>
    public static IReadOnlyList<LanguageInfo> AvailableLanguages => LanguageCatalog.DiscoverLanguages();

    /// <summary>
    /// Resolves and loads the active language at startup.
    /// <paramref name="savedCode"/> is the user's saved preference; when null/blank (first run) the Windows
    /// UI culture is used to pick the closest available language, otherwise English.
    /// </summary>
    public static void Initialize(string? savedCode)
    {
        _english = LanguageCatalog.LoadEmbeddedEnglish();

        var code = !string.IsNullOrWhiteSpace(savedCode)
            ? savedCode!
            : DetectSystemLanguageCode();

        SetLanguage(code);
    }

    /// <summary>Switches the active language by code, falling back to English if it cannot be loaded.</summary>
    public static void SetLanguage(string code)
    {
        _current = LanguageCatalog.Load(code) ?? _english;
    }

    /// <summary>Translated text for <paramref name="key"/>, with English then key-name fallback.</summary>
    public static string T(string key)
    {
        if (_current.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_english.Strings.TryGetValue(key, out var english))
        {
            return english;
        }

        return key;
    }

    /// <summary>
    /// Translated text with positional placeholders filled in (e.g. key "{0} of {1}" + args).
    /// Mirrors <see cref="string.Format(string, object?[])"/>; if the translated text has malformed
    /// placeholders it falls back to the raw text rather than throwing.
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    /// <summary>
    /// Picks the best available language for the current Windows UI culture: an exact code match first
    /// (e.g. "pt-BR"), then the two-letter language match (e.g. any "es-*" -> "es"), else English.
    /// </summary>
    private static string DetectSystemLanguageCode()
    {
        var available = LanguageCatalog.DiscoverLanguages();
        var culture = CultureInfo.CurrentUICulture;

        var exact = available.FirstOrDefault(l =>
            l.Code.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Code;
        }

        var twoLetter = culture.TwoLetterISOLanguageName;
        var byLanguage = available.FirstOrDefault(l =>
            l.Code.Equals(twoLetter, StringComparison.OrdinalIgnoreCase) ||
            l.Code.StartsWith(twoLetter + "-", StringComparison.OrdinalIgnoreCase));

        return byLanguage?.Code ?? LanguageCatalog.EnglishCode;
    }
}
