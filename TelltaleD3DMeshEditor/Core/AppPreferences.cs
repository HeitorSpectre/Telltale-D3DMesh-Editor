using System.Text.Json;

namespace TelltaleD3DMeshEditor.Core;

public sealed record AppPreferences
{
    public GameId LastGame { get; init; } = GameId.Generic;
    public string OutputFormat { get; init; } = "Glb";
    public bool TextureAtlas { get; init; }

    // When true, textures written on reimport are saved uncompressed (ARGB8) instead of DXT.
    // Needed for games like Minecraft: Story Mode whose low-resolution character ("skin") textures
    // are shipped uncompressed; DXT block compression smears their sharp pixel-art edges in-game.
    public bool UncompressedTextures { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleD3DMeshEditor");

    public static string PreferencesPath => Path.Combine(
        AppDataFolder,
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

    public static void SaveToolSettings(string outputFormat, bool textureAtlas, bool uncompressedTextures)
    {
        var current = Load();
        Save(current with
        {
            OutputFormat = outputFormat,
            TextureAtlas = textureAtlas,
            UncompressedTextures = uncompressedTextures,
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
