namespace TelltaleD3DMeshEditor.Core;

public enum GameId
{
    Generic,
    WolfAmongUs,
    WalkingDeadSeason2,
}

// Per-game settings, so behaviour specific to one game stays isolated and never affects another. The
// Wolf Among Us is fully working, so its settings stay neutral; new games (The Walking Dead: Season 2)
// add their own quirks here instead of changing shared code. The active game is chosen by the user from
// a toolbar dropdown and read through GameConfig.Current.
public sealed class GameConfig
{
    public required GameId Id { get; init; }
    public required string DisplayName { get; init; }

    // Some games store a material's opacity (hair strands, lens, etc.) in a separate companion texture
    // named "<diffuse>Alpha" / "<diffuse>_alpha" rather than in the diffuse's own alpha channel. When
    // true, that companion is merged into the diffuse's alpha so transparency renders correctly.
    public bool UsesCompanionAlphaTextures { get; init; }

    public static readonly GameConfig Generic = new()
    {
        Id = GameId.Generic,
        DisplayName = "Auto / Generic",
        UsesCompanionAlphaTextures = false,
    };

    public static readonly GameConfig WolfAmongUs = new()
    {
        Id = GameId.WolfAmongUs,
        DisplayName = "The Wolf Among Us",
        UsesCompanionAlphaTextures = false,
    };

    public static readonly GameConfig WalkingDeadSeason2 = new()
    {
        Id = GameId.WalkingDeadSeason2,
        DisplayName = "The Walking Dead: Season 2",
        UsesCompanionAlphaTextures = true,
    };

    public static readonly IReadOnlyList<GameConfig> All = [Generic, WolfAmongUs, WalkingDeadSeason2];

    // The active game. Defaults to Generic, which behaves exactly as the tool did before per-game config.
    public static GameConfig Current { get; set; } = Generic;
}
