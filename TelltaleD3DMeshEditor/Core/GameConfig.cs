namespace TelltaleD3DMeshEditor.Core;

public enum GameId
{
    Generic,
    WolfAmongUs,
    WalkingDeadSeason2,
    MinecraftStoryMode,
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

    // TWD S2 environment props often use a baked/lightmap texture in the "bake" slot. When a
    // different model is reimported into that prop, keeping the old bake map paints the old object's
    // lighting/dirt over the new mesh. Only games that need this quirk should enable it.
    public bool ClearInheritedBakeOnReimport { get; init; }

    // TWD S2 character/material templates can carry normal/detail/specular/ink helper slots from the
    // original model. If an imported GLB does not provide that same slot, keeping the template slot
    // paints the original character's ink/normal pattern over the new character.
    public bool ClearInheritedSecondaryTexturesOnReimport { get; init; }

    // Some game/material pipelines are happier when a character imported from another exported
    // Telltale GLB keeps the source texture symbols instead of being renamed to the replaced
    // character's template symbols. TWD S2 character swaps currently need this for eyes/shared parts.
    public bool PreferGltfTextureNamesOnReimport { get; init; }

    // TWD S2 character swaps need the imported image data, but several shaders still expect the
    // replaced character's original texture symbols. This enables a semantic source->template remap
    // (eye/body/head/etc.) instead of a fragile primitive-index remap.
    public bool PreferSemanticTemplateTextureNamesOnReimport { get; init; }

    // Some exported TWD S2 character GLBs include small eye helper primitives (white/alpha shells and
    // pupil masks). They can become opaque in-game after reinsertion and cover the actual iris.
    public bool RemoveEyeHelperPrimitivesOnReimport { get; init; }

    // In TWD S2, TWAU-style body line overlays need inverted alpha on the body material. Split only the
    // body line slot into a separate alpha-inverted texture so shared head/hand lines remain unchanged.
    public bool SplitBodyLineAlphaOnReimport { get; init; }

    // TWAU body line overlays use the opposite alpha convention after GLB reimport. Rewrite the body
    // lines under the template filename so details stay visible without renaming the texture.
    public bool InvertBodyLineAlphaOnReimport { get; init; }

    // TWAU-style ink line overlays mapped onto a TWD S2 head render as an opaque black face mask
    // (inverted alpha) after reimport. Flip the alpha of the face line texture so the lines show and
    // the rest of the face stays transparent. Scoped by foreign ink/line naming, so TWD S2 native
    // "*_head_detail" textures are not affected.
    public bool InvertHeadLineAlphaOnReimport { get; init; }

    // Existing TWAU/TWD2 environment assets often name baked/lightmap textures as adv/obj *_000.
    // Minecraft: Story Mode uses the same suffix for normal diffuse atlas names, so this must be
    // profile-specific instead of a global filename rule.
    public bool TreatAdvObj000TexturesAsBake { get; init; }

    // Minecraft character textures are deliberately low-resolution pixel art. glTF viewers default to
    // linear filtering when a sampler does not specify filters, which blurs those pixels on export.
    public bool PixelatedGltfTextures { get; init; }

    public static readonly GameConfig Generic = new()
    {
        Id = GameId.Generic,
        DisplayName = "Auto / Generic",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
    };

    public static readonly GameConfig WolfAmongUs = new()
    {
        Id = GameId.WolfAmongUs,
        DisplayName = "The Wolf Among Us",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = true,
        RemoveEyeHelperPrimitivesOnReimport = true,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = true,
        InvertHeadLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
    };

    public static readonly GameConfig WalkingDeadSeason2 = new()
    {
        Id = GameId.WalkingDeadSeason2,
        DisplayName = "The Walking Dead: Season 2",
        UsesCompanionAlphaTextures = true,
        ClearInheritedBakeOnReimport = true,
        ClearInheritedSecondaryTexturesOnReimport = true,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = true,
        RemoveEyeHelperPrimitivesOnReimport = true,
        SplitBodyLineAlphaOnReimport = true,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = true,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
    };

    public static readonly GameConfig MinecraftStoryMode = new()
    {
        Id = GameId.MinecraftStoryMode,
        DisplayName = "Minecraft: Story Mode",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = false,
        PixelatedGltfTextures = true,
    };

    public static readonly IReadOnlyList<GameConfig> All = [Generic, WolfAmongUs, WalkingDeadSeason2, MinecraftStoryMode];

    // The active game. Defaults to Generic, which behaves exactly as the tool did before per-game config.
    public static GameConfig Current { get; set; } = Generic;

    public static GameConfig FromId(GameId id)
        => All.FirstOrDefault(game => game.Id == id) ?? Generic;
}
