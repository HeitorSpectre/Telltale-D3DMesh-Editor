using System.Text.Json;

namespace TelltaleD3DMeshEditor.Core;

public sealed class AppPreferences
{
    public GameId LastGame { get; init; } = GameId.Generic;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string PreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleD3DMeshEditor",
        "settings.json");

    public static GameConfig LoadGameConfig()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return GameConfig.Generic;
            }

            var prefs = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesPath), JsonOptions);
            return prefs is null ? GameConfig.Generic : GameConfig.FromId(prefs.LastGame);
        }
        catch
        {
            return GameConfig.Generic;
        }
    }

    public static void SaveGameConfig(GameConfig game)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            var prefs = new AppPreferences { LastGame = game.Id };
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(prefs, JsonOptions));
        }
        catch
        {
            // Preferences are a convenience; never block the editor if the profile cannot be saved.
        }
    }
}
