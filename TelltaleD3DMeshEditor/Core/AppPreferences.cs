using System.Text.Json;

namespace TelltaleD3DMeshEditor.Core;

public sealed record AppPreferences
{
    public GameId LastGame { get; init; } = GameId.Generic;
    public string OutputFormat { get; init; } = "Glb";
    public bool TextureAtlas { get; init; }
    public bool ViewerAntiAliasing { get; init; }
    public bool ViewerFlightCamera { get; init; }

    // When true, textures written on reimport are saved uncompressed (ARGB8) instead of DXT.
    // Needed for games like Minecraft: Story Mode whose low-resolution character ("skin") textures
    // are shipped uncompressed; DXT block compression smears their sharp pixel-art edges in-game.
    public bool UncompressedTextures { get; init; }

    // When true, an imported GLB/GLTF is uniformly rescaled (and recentered) on reimport so its overall
    // size matches the original mesh it replaces. Useful when a model carries an unintended export scale
    // (e.g. a Blender node scale of 10) and would otherwise enter the game far larger or smaller than the
    // asset it replaces. Off by default; proportions are preserved (single scale factor).
    public bool MatchOriginalModelSize { get; init; }

    // Optional facial-bone alias retarget. When enabled, character-specific facial bones such as
    // "bigby_mouthCorner_L" may be matched to the target's equivalent "rhys_mouthCorner_L". It can help
    // compatible face rigs follow target lipsync, but remains off by default because mismatched rigs can
    // deform the mouth.
    public bool NormalizeFacialBonesOnReimport { get; init; }

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

    public static void SaveToolSettings(
        string outputFormat,
        bool textureAtlas,
        bool uncompressedTextures,
        bool matchOriginalModelSize,
        bool normalizeFacialBonesOnReimport,
        bool viewerAntiAliasing,
        bool viewerFlightCamera)
    {
        var current = Load();
        Save(current with
        {
            OutputFormat = outputFormat,
            TextureAtlas = textureAtlas,
            UncompressedTextures = uncompressedTextures,
            MatchOriginalModelSize = matchOriginalModelSize,
            NormalizeFacialBonesOnReimport = normalizeFacialBonesOnReimport,
            ViewerAntiAliasing = viewerAntiAliasing,
            ViewerFlightCamera = viewerFlightCamera,
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
