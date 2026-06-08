using System.Text.Json;

namespace TelltaleD3DMeshEditor.Core;

public sealed record AppPreferences
{
    public GameId LastGame { get; init; } = GameId.Generic;
    public string OutputFormat { get; init; } = "Glb";
    public bool TextureAtlas { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string PreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleD3DMeshEditor",
        "settings.json");

    public static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return new AppPreferences();
            }

            var prefs = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesPath), JsonOptions);
            return prefs ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    public static GameConfig LoadGameConfig()
    {
        return GameConfig.FromId(Load().LastGame);
    }

    public static void SaveGameConfig(GameConfig game)
    {
        var current = Load();
        Save(current with { LastGame = game.Id });
    }

    public static void SaveToolSettings(string outputFormat, bool textureAtlas)
    {
        var current = Load();
        Save(current with
        {
            OutputFormat = outputFormat,
            TextureAtlas = textureAtlas,
        });
    }

    private static void Save(AppPreferences prefs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(prefs, JsonOptions));
        }
        catch
        {
            // Preferences are a convenience; never block the editor if the profile cannot be saved.
        }
    }
}
