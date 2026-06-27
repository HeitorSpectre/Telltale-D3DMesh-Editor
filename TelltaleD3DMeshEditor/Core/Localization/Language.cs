namespace TelltaleD3DMeshEditor.Core.Localization;

/// <summary>
/// One loaded UI language: its <c>_meta</c> declaration plus the flat key -> text map read
/// from a <c>Languages/&lt;code&gt;.json</c> file (or the embedded English baseline).
/// </summary>
public sealed class Language
{
    public Language(
        string code,
        string nativeName,
        string englishName,
        string author,
        string baseVersion,
        IReadOnlyDictionary<string, string> strings)
    {
        Code = code;
        NativeName = nativeName;
        EnglishName = englishName;
        Author = author;
        BaseVersion = baseVersion;
        Strings = strings;
    }

    /// <summary>BCP-47-ish code, e.g. "en", "pt-BR", "es". Matches the file name and the saved preference.</summary>
    public string Code { get; }

    /// <summary>Name shown in the language picker, written in the language itself (e.g. "Português do Brasil").</summary>
    public string NativeName { get; }

    /// <summary>Name of the language in English (e.g. "Brazilian Portuguese"), for reference.</summary>
    public string EnglishName { get; }

    /// <summary>Who contributed the translation. Free text; informational only.</summary>
    public string Author { get; }

    /// <summary>Tool version this translation was last synced against. Informational only.</summary>
    public string BaseVersion { get; }

    /// <summary>Translated UI strings, keyed by stable dotted ids (e.g. "settings.title").</summary>
    public IReadOnlyDictionary<string, string> Strings { get; }

    public override string ToString() => NativeName;
}
