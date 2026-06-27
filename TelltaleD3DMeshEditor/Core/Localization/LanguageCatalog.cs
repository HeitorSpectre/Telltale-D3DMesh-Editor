using System.Reflection;
using System.Text.Json;

namespace TelltaleD3DMeshEditor.Core.Localization;

/// <summary>
/// Discovers and loads UI languages from the <c>Languages/</c> folder next to the executable, so the
/// community can add a new language by dropping a <c>&lt;code&gt;.json</c> file there (no rebuild needed).
/// English is always available: the embedded <c>en.json</c> baseline is used as a fallback if the folder
/// or file is missing.
/// </summary>
public static class LanguageCatalog
{
    public const string EnglishCode = "en";

    private const string EmbeddedEnglishResource = "TelltaleD3DMeshEditor.Resources.Languages.en.json";

    /// <summary>Folder scanned for language files, next to the running executable.</summary>
    public static string LanguagesFolder => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>
    /// Lightweight scan: reads only the <c>_meta</c> of every <c>Languages/*.json</c> to build the picker
    /// list, without parsing all strings. English is guaranteed to be present even if its file is absent.
    /// Ordered with English first, then the rest by native name.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> DiscoverLanguages()
    {
        var byCode = new Dictionary<string, LanguageInfo>(StringComparer.OrdinalIgnoreCase);

        // Embedded English baseline first, so the picker always offers it.
        var embeddedEnglish = TryReadEmbeddedEnglishMeta();
        if (embeddedEnglish is not null)
        {
            byCode[embeddedEnglish.Code] = embeddedEnglish;
        }

        if (Directory.Exists(LanguagesFolder))
        {
            foreach (var file in Directory.EnumerateFiles(LanguagesFolder, "*.json"))
            {
                var info = TryReadMeta(file);
                if (info is not null)
                {
                    // A file on disk overrides the embedded baseline for the same code.
                    byCode[info.Code] = info;
                }
            }
        }

        return byCode.Values
            .OrderBy(info => info.Code.Equals(EnglishCode, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(info => info.NativeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Loads a full language (meta + all strings) by code, or null if it cannot be found/parsed.</summary>
    public static Language? Load(string code)
    {
        var file = Path.Combine(LanguagesFolder, code + ".json");
        if (File.Exists(file))
        {
            try
            {
                return Parse(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, $"Failed to load language file '{file}'");
            }
        }

        if (code.Equals(EnglishCode, StringComparison.OrdinalIgnoreCase))
        {
            return LoadEmbeddedEnglish();
        }

        return null;
    }

    /// <summary>The English baseline from the embedded resource; the ultimate fallback that always works.</summary>
    public static Language LoadEmbeddedEnglish()
    {
        var json = ReadEmbeddedEnglishJson();
        if (json is null)
        {
            // Should never happen (resource is embedded at build time), but never crash the UI over it.
            return new Language(EnglishCode, "English", "English", "HeitorSpectre", string.Empty,
                new Dictionary<string, string>());
        }

        return Parse(json);
    }

    private static LanguageInfo? TryReadMeta(string file)
    {
        try
        {
            return ReadMeta(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Failed to read language meta '{file}'");
            return null;
        }
    }

    private static LanguageInfo? TryReadEmbeddedEnglishMeta()
    {
        var json = ReadEmbeddedEnglishJson();
        if (json is null)
        {
            return null;
        }

        try
        {
            return ReadMeta(json);
        }
        catch
        {
            return null;
        }
    }

    private static LanguageInfo? ReadMeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("_meta", out var meta))
        {
            return null;
        }

        var code = GetString(meta, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return new LanguageInfo(
            code,
            FirstNonEmpty(GetString(meta, "nativeName"), code),
            FirstNonEmpty(GetString(meta, "englishName"), code),
            GetString(meta, "author"),
            GetString(meta, "baseVersion"));
    }

    private static Language Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = EnglishCode;
        var nativeName = "English";
        var englishName = "English";
        var author = string.Empty;
        var baseVersion = string.Empty;

        if (root.TryGetProperty("_meta", out var meta))
        {
            code = FirstNonEmpty(GetString(meta, "code"), code);
            nativeName = FirstNonEmpty(GetString(meta, "nativeName"), nativeName);
            englishName = FirstNonEmpty(GetString(meta, "englishName"), englishName);
            author = GetString(meta, "author");
            baseVersion = GetString(meta, "baseVersion");
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("_meta") || prop.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            strings[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        return new Language(code, nativeName, englishName, author, baseVersion, strings);
    }

    private static string? ReadEmbeddedEnglishJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedEnglishResource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstNonEmpty(string a, string b)
        => string.IsNullOrWhiteSpace(a) ? b : a;
}

/// <summary>Picker-list entry: the <c>_meta</c> of a language without its full string table.</summary>
public sealed record LanguageInfo(
    string Code,
    string NativeName,
    string EnglishName,
    string Author,
    string BaseVersion)
{
    public override string ToString() => NativeName;
}
