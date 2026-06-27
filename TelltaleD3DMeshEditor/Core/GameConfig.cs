namespace TelltaleD3DMeshEditor.Core;

public enum GameId
{
    Generic = 0,
    WolfAmongUs = 1,
    WalkingDeadSeason2 = 2,
    MinecraftStoryMode = 3,
    TalesFromTheBorderlands = 4,
    TalesFromTheBorderlands2014 = 5,
    TalesFromTheBorderlandsE3 = 6,
    TalesFromTheBorderlandsOld = 7,
    BackToTheFuture = 8,
    BackToTheFutureEpisode1 = 9,
    BackToTheFutureEpisode2 = 10,
    BackToTheFutureEpisode3 = 11,
    BackToTheFutureEpisode4 = 12,
    BackToTheFutureEpisode5 = 13,
    WalkingDead = 14,
    TalesFromTheBorderlands2021 = 15,
    MinecraftStoryModeGroup = 16,
    MinecraftStoryModeSeason2 = 17,
    GameOfThrones = 18,
    WalkingDeadMichonne = 19,
}

// Per-game settings, so behaviour specific to one game stays isolated and never affects another. The
// Wolf Among Us is fully working, so its settings stay neutral; new games (The Walking Dead: Season 2)
// add their own quirks here instead of changing shared code. The active game is chosen by the user from
// a toolbar dropdown and read through GameConfig.Current.
public sealed class GameConfig
{
    public required GameId Id { get; init; }
    public required string DisplayName { get; init; }
    public string? ModernTextureToolkitGameName { get; init; }
    public bool IsBackToTheFuture => Id is
        GameId.BackToTheFuture or
        GameId.BackToTheFutureEpisode1 or
        GameId.BackToTheFutureEpisode2 or
        GameId.BackToTheFutureEpisode3 or
        GameId.BackToTheFutureEpisode4 or
        GameId.BackToTheFutureEpisode5;

    public bool IsWalkingDead => Id is
        GameId.WalkingDead or
        GameId.WalkingDeadSeason2 or
        GameId.WalkingDeadMichonne;

    public bool IsTalesFromTheBorderlands => Id is
        GameId.TalesFromTheBorderlands or
        GameId.TalesFromTheBorderlands2014 or
        GameId.TalesFromTheBorderlandsE3 or
        GameId.TalesFromTheBorderlandsOld;

    public bool IsMinecraftStoryMode => Id is
        GameId.MinecraftStoryModeGroup or
        GameId.MinecraftStoryMode or
        GameId.MinecraftStoryModeSeason2;

    public bool IsOriginalTalesFromTheBorderlandsPc => Id is
        GameId.TalesFromTheBorderlands2014 or
        GameId.TalesFromTheBorderlandsE3;

    public bool IsGameMenuGroup => Id is
        GameId.TalesFromTheBorderlands or
        GameId.BackToTheFuture or
        GameId.WalkingDead or
        GameId.MinecraftStoryModeGroup;

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

    // Some games treat authored secondary symbols (normal, brushnormal, occlusion, etc.) as part of
    // the material identity. Preserve those slot names instead of generating namespaced replacements.
    public bool PreserveSecondaryTextureNamesOnReimport { get; init; }

    // Some v17/v18 games encode bone indices with room for more than 64 entries, but their shipped
    // character meshes still cap each runtime palette at 64 bones. Keep this profile-scoped so a
    // foreign-model reimport cannot create oversized palettes only because the byte encoding allows it.
    public int? MaxSkinnedPaletteBonesOnReimport { get; init; }

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

    // TWAU-style hand ink-line overlays use the opposite alpha convention after GLB reimport, same as the
    // head lines. Flip the alpha of the hand line texture so the hand lines show and the rest of the hand
    // stays transparent. Scoped by foreign ink/line + "hand" naming so native textures are not affected.
    public bool InvertHandLineAlphaOnReimport { get; init; }

    // Existing TWAU/TWD2 environment assets often name baked/lightmap textures as adv/obj *_000.
    // Minecraft: Story Mode uses the same suffix for normal diffuse atlas names, so this must be
    // profile-specific instead of a global filename rule.
    public bool TreatAdvObj000TexturesAsBake { get; init; }

    // Minecraft character textures are deliberately low-resolution pixel art. glTF viewers default to
    // linear filtering when a sampler does not specify filters, which blurs those pixels on export.
    public bool PixelatedGltfTextures { get; init; }

    // Native .skl files keep RestXform/BoneLength/BoneDir at zero; the merger historically filled
    // them from the local pose on moved joints. MCSM's procedural face rigs (eye look-at) consume
    // these fields, so invented values displace the eye quads in-game. When enabled, merged joints
    // keep their original derived fields untouched and only the local pose is updated.
    public bool PreserveSkeletonDerivedFieldsOnMerge { get; init; }

    // MCSM skeletons carry per-bone translation scales (GlobalTranslationScale/AnimTranslationScale,
    // e.g. Petra Y=1.17) that retarget canonical animations to the character's proportions. Keeping
    // the TARGET's scales over the DONOR's bind pose displaces every bone-driven offset (the eye
    // pivot rises ~5cm and the pupil hides behind the hair fringe). When enabled, a combined
    // reimport adopts the donor skeleton's scales (read from the donor's own .skl next to the
    // imported model's source meshes).
    public bool PortTranslationScalesOnSkeletonMerge { get; init; }

    // MCSM animates lipsync by swapping per-viseme mouth meshes (mouthAA..mouthU). A combined group
    // only carries the Default viseme, so after a character swap the other viseme files would keep the
    // old character's UVs over the replaced atlas and the mouth breaks while talking. When enabled,
    // sibling part files that exist next to the imported model's source meshes (same part suffix) are
    // ported into the target's matching files during a combined reimport.
    public bool PortCompanionVariantPartsOnReimport { get; init; }

    // TFTB E3 (v14) static props need a faithful per-vertex round-trip that the generic GLB pipeline
    // cannot give: (1) baked colors are mostly black and the exporter normalizes black->white and drops
    // COLOR_0, and (2) the original tangent.w is 0 but the importer forces it to 1. When enabled, a
    // count-preserving reimport (submesh vertex count unchanged) restores the template's original
    // per-vertex colors when the GLB carries none, and keeps the GLB's stored tangent.w (including 0)
    // instead of forcing 1. Full swaps with a different vertex count keep the generic behaviour.
    // Defaults off so the v13/14 character games (TWAU/TWD2/MCSM) stay byte-identical.
    public bool PreserveTemplateVertexDataOnReimport { get; init; }

    // Original TFTB PC drives facial animation very aggressively from character-specific mouth/face
    // joints. Mapping another character's mouthCorner/jaw/tongue aliases onto the target character's
    // facial rig makes the imported face follow the target lipsync, but the donor mouth is usually a
    // different shape and collapses/recedes in-game. Keep exact bone matches, but do not retarget those
    // character-specific face aliases for this profile.
    public bool DisableCharacterSpecificFacialRetargetOnReimport { get; init; } = true;

    public static readonly GameConfig Generic = new()
    {
        Id = GameId.Generic,
        DisplayName = "Auto / Generic",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreserveSecondaryTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = true,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
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
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    public static readonly GameConfig WalkingDead = new()
    {
        Id = GameId.WalkingDead,
        DisplayName = "The Walking Dead",
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
        InvertHandLineAlphaOnReimport = true,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    public static readonly GameConfig WalkingDeadMichonne = new()
    {
        Id = GameId.WalkingDeadMichonne,
        DisplayName = "The Walking Dead: Michonne",
        ModernTextureToolkitGameName = "The Walking Dead: Michonne",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = true,
        PreferGltfTextureNamesOnReimport = false,
        PreserveSecondaryTextureNamesOnReimport = true,
        MaxSkinnedPaletteBonesOnReimport = 64,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = true,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    public static readonly GameConfig MinecraftStoryMode = new()
    {
        Id = GameId.MinecraftStoryMode,
        DisplayName = "Minecraft: Story Mode - Season 1",
        ModernTextureToolkitGameName = "Minecraft: Story Mode",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = false,
        PixelatedGltfTextures = true,
        PreserveSkeletonDerivedFieldsOnMerge = true,
        PortTranslationScalesOnSkeletonMerge = true,
        PortCompanionVariantPartsOnReimport = true,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    public static readonly GameConfig MinecraftStoryModeGroup = new()
    {
        Id = GameId.MinecraftStoryModeGroup,
        DisplayName = "Minecraft: Story Mode",
    };

    public static readonly GameConfig MinecraftStoryModeSeason2 = new()
    {
        Id = GameId.MinecraftStoryModeSeason2,
        DisplayName = "Minecraft: Story Mode - Season 2",
    };

    public static readonly GameConfig TalesFromTheBorderlands2014 = new()
    {
        Id = GameId.TalesFromTheBorderlands2014,
        DisplayName = "Tales from the Borderlands (2014/2015)",
        ModernTextureToolkitGameName = "Borderlands",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = false,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    public static readonly GameConfig TalesFromTheBorderlandsE3 = new()
    {
        Id = GameId.TalesFromTheBorderlandsE3,
        DisplayName = "Tales from the Borderlands (E3)",
        ModernTextureToolkitGameName = "Borderlands",
        PreserveTemplateVertexDataOnReimport = true,
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = false,
        PreferGltfTextureNamesOnReimport = false,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = false,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    // The 2014 source-code leak build. It uses an older serialization (non-interleaved vertex streams,
    // 4VSM/ERTM containers). There is no playable build, so this profile is view/extract + GLB export
    // only; reinsertion is intentionally not supported. Behaviour flags stay neutral.
    public static readonly GameConfig TalesFromTheBorderlandsOld = new()
    {
        Id = GameId.TalesFromTheBorderlandsOld,
        DisplayName = "Tales from the Borderlands (Old)",
        ModernTextureToolkitGameName = "Borderlands",
    };

    public static readonly GameConfig TalesFromTheBorderlands2021 = new()
    {
        Id = GameId.TalesFromTheBorderlands2021,
        DisplayName = "Tales from the Borderlands (2021)",
    };

    public static readonly GameConfig TalesFromTheBorderlands = new()
    {
        Id = GameId.TalesFromTheBorderlands,
        DisplayName = "Tales from the Borderlands",
    };

    public static readonly GameConfig GameOfThrones = new()
    {
        Id = GameId.GameOfThrones,
        DisplayName = "Game of Thrones - A Telltale Games Series",
        ModernTextureToolkitGameName = "Game of Thrones (2015)",
        UsesCompanionAlphaTextures = false,
        ClearInheritedBakeOnReimport = false,
        ClearInheritedSecondaryTexturesOnReimport = true,
        PreferGltfTextureNamesOnReimport = false,
        PreserveSecondaryTextureNamesOnReimport = true,
        MaxSkinnedPaletteBonesOnReimport = 64,
        PreferSemanticTemplateTextureNamesOnReimport = false,
        RemoveEyeHelperPrimitivesOnReimport = false,
        SplitBodyLineAlphaOnReimport = false,
        InvertBodyLineAlphaOnReimport = false,
        InvertHeadLineAlphaOnReimport = false,
        InvertHandLineAlphaOnReimport = false,
        TreatAdvObj000TexturesAsBake = false,
        PixelatedGltfTextures = false,
        PreserveSkeletonDerivedFieldsOnMerge = false,
        PortTranslationScalesOnSkeletonMerge = false,
        PortCompanionVariantPartsOnReimport = false,
        DisableCharacterSpecificFacialRetargetOnReimport = true,
    };

    public static readonly GameConfig BackToTheFuture = CreateBackToTheFuture(GameId.BackToTheFuture, "Back to the Future: The Game");
    public static readonly GameConfig BackToTheFutureEpisode1 = CreateBackToTheFuture(GameId.BackToTheFutureEpisode1, "Back to the Future: Episode 1 - It's About Time");
    public static readonly GameConfig BackToTheFutureEpisode2 = CreateBackToTheFuture(GameId.BackToTheFutureEpisode2, "Back to the Future: Episode 2 - Get Tannen!");
    public static readonly GameConfig BackToTheFutureEpisode3 = CreateBackToTheFuture(GameId.BackToTheFutureEpisode3, "Back to the Future: Episode 3 - Citizen Brown");
    public static readonly GameConfig BackToTheFutureEpisode4 = CreateBackToTheFuture(GameId.BackToTheFutureEpisode4, "Back to the Future: Episode 4 - Double Visions");
    public static readonly GameConfig BackToTheFutureEpisode5 = CreateBackToTheFuture(GameId.BackToTheFutureEpisode5, "Back to the Future: Episode 5 - OUTATIME");

    public static readonly IReadOnlyList<GameConfig> All =
    [
        Generic,
        WolfAmongUs,
        WalkingDead,
        WalkingDeadSeason2,
        WalkingDeadMichonne,
        MinecraftStoryModeGroup,
        MinecraftStoryMode,
        TalesFromTheBorderlands,
        TalesFromTheBorderlands2014,
        TalesFromTheBorderlandsE3,
        TalesFromTheBorderlandsOld,
        GameOfThrones,
        BackToTheFuture,
        BackToTheFutureEpisode1,
        BackToTheFutureEpisode2,
        BackToTheFutureEpisode3,
        BackToTheFutureEpisode4,
        BackToTheFutureEpisode5,
    ];

    // The active game. Defaults to Generic, which behaves exactly as the tool did before per-game config.
    public static GameConfig Current { get; set; } = Generic;

    public static GameConfig FromId(GameId id)
        => All.FirstOrDefault(game => game.Id == id) ?? Generic;

    public GameConfig WithNormalizeFacialBonesOnReimport(bool enabled)
    {
        if (!enabled)
        {
            return this;
        }

        return new GameConfig
        {
            Id = Id,
            DisplayName = DisplayName,
            ModernTextureToolkitGameName = ModernTextureToolkitGameName,
            UsesCompanionAlphaTextures = UsesCompanionAlphaTextures,
            ClearInheritedBakeOnReimport = ClearInheritedBakeOnReimport,
            ClearInheritedSecondaryTexturesOnReimport = ClearInheritedSecondaryTexturesOnReimport,
            PreferGltfTextureNamesOnReimport = PreferGltfTextureNamesOnReimport,
            PreserveSecondaryTextureNamesOnReimport = PreserveSecondaryTextureNamesOnReimport,
            MaxSkinnedPaletteBonesOnReimport = MaxSkinnedPaletteBonesOnReimport,
            PreferSemanticTemplateTextureNamesOnReimport = PreferSemanticTemplateTextureNamesOnReimport,
            RemoveEyeHelperPrimitivesOnReimport = RemoveEyeHelperPrimitivesOnReimport,
            SplitBodyLineAlphaOnReimport = SplitBodyLineAlphaOnReimport,
            InvertBodyLineAlphaOnReimport = InvertBodyLineAlphaOnReimport,
            InvertHeadLineAlphaOnReimport = InvertHeadLineAlphaOnReimport,
            InvertHandLineAlphaOnReimport = InvertHandLineAlphaOnReimport,
            TreatAdvObj000TexturesAsBake = TreatAdvObj000TexturesAsBake,
            PixelatedGltfTextures = PixelatedGltfTextures,
            PreserveSkeletonDerivedFieldsOnMerge = PreserveSkeletonDerivedFieldsOnMerge,
            PortTranslationScalesOnSkeletonMerge = PortTranslationScalesOnSkeletonMerge,
            PortCompanionVariantPartsOnReimport = PortCompanionVariantPartsOnReimport,
            PreserveTemplateVertexDataOnReimport = PreserveTemplateVertexDataOnReimport,
            DisableCharacterSpecificFacialRetargetOnReimport = false,
        };
    }

    private static GameConfig CreateBackToTheFuture(GameId id, string displayName)
        => new()
        {
            Id = id,
            DisplayName = displayName,
            UsesCompanionAlphaTextures = true,
            // BTTF environment props use a baked lightmap in the "bake" slot. Keeping the original object's
            // dark lightmap over a reimported model multiplies it down to black in-game (the viewer shows
            // diffuse only, so it looks fine there). Clearing it neutralizes the inherited lighting.
            ClearInheritedBakeOnReimport = true,
            ClearInheritedSecondaryTexturesOnReimport = false,
            PreferGltfTextureNamesOnReimport = false,
            PreferSemanticTemplateTextureNamesOnReimport = true,
            RemoveEyeHelperPrimitivesOnReimport = false,
            SplitBodyLineAlphaOnReimport = false,
            InvertBodyLineAlphaOnReimport = false,
            InvertHeadLineAlphaOnReimport = false,
            InvertHandLineAlphaOnReimport = false,
            TreatAdvObj000TexturesAsBake = true,
            PixelatedGltfTextures = false,
            PreserveSkeletonDerivedFieldsOnMerge = false,
            PortTranslationScalesOnSkeletonMerge = false,
            PortCompanionVariantPartsOnReimport = false,
            DisableCharacterSpecificFacialRetargetOnReimport = true,
        };
}
