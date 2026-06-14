using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.UI;

// Virtual extraction group for meshes that belong together in the same source folder.
// This lets character parts such as body/head be exported as one editable GLB/GLTF.
public sealed class ModelAssetGroup
{
    private readonly string _searchText;

    private static readonly string[] KnownSlots =
    [
        "sharedParts",
        "headHands",
        "bodyUpper",
        "bodyLower",
        "faceParts",
        "armRight",
        "armLeft",
        "handRight",
        "handLeft",
        "legRight",
        "legLeft",
        "footRight",
        "footLeft",
        "wingRight",
        "wingLeft",
        "teeth",
        "tongue",
        "backpack",
        "haircut",
        "shoulder",
        "neck",
        "nose",
        "mouth",
        "brow",
        "cheek",
        "eyelids",
        "eyelashes",
        "brows",
        "ear",
        "body",
        "torso",
        "chest",
        "head",
        "hands",
        "arms",
        "legs",
        "feet",
        "eyes",
        "eye",
        "glasses",
        "hair",
        "hat",
        "holster",
        "revolver",
        "pistol",
        "weapon",
        "gun",
        "tail",
        "wings",
        "wing",
        "arm",
        "leg",
        "hand",
        "foot",
    ];

    private ModelAssetGroup(
        string name,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<ModelAsset> assets,
        IReadOnlySet<string>? decalOffsetMeshPaths = null)
    {
        Name = name;
        SkeletonPath = skeletonPath;
        RelativeDirectory = relativeDirectory;
        Assets = assets;
        DecalOffsetMeshPaths = decalOffsetMeshPaths ?? new HashSet<string>();
        _searchText = BuildSearchText(Name, SkeletonPath, RelativeDirectory, Assets);
    }

    public string Name { get; }
    public string SkeletonPath { get; }
    public string RelativeDirectory { get; }
    public IReadOnlyList<ModelAsset> Assets { get; }

    // Full paths of parts that are surface decals (damage wounds) and must be nudged out along their
    // normals when combined, so they render over the clean skin instead of z-fighting it. Empty for
    // ordinary groups.
    public IReadOnlySet<string> DecalOffsetMeshPaths { get; }

    public string OutputStem => Sanitize(Name + "_combined");

    // <paramref name="progress"/> (optional) receives a 0..1 fraction as buckets are grouped; some
    // groupers parse mesh bounds, so this can be slow on large folders and is reported on the progress
    // bar. Throttled to whole-percent changes to avoid adding any overhead.
    public static List<ModelAssetGroup> Discover(
        IReadOnlyList<ModelAsset> assets,
        string inputRoot,
        IProgress<double>? progress = null)
    {
        var buckets = new Dictionary<string, (string SkeletonPath, string RelativeDirectory, List<ModelAsset> Assets)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.SkeletonPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(asset.MeshPath) ?? "";
            var relativeDirectory = Path.GetRelativePath(inputRoot, directory);
            if (relativeDirectory == ".")
            {
                relativeDirectory = "";
            }

            var key = Path.GetFullPath(directory) + "\0" + Path.GetFullPath(asset.SkeletonPath);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = (asset.SkeletonPath, relativeDirectory, []);
                buckets[key] = bucket;
            }

            bucket.Assets.Add(asset);
        }

        var bucketList = buckets.Values.ToList();
        var groups = new List<ModelAssetGroup>();
        var lastPercent = -1;
        for (var i = 0; i < bucketList.Count; i++)
        {
            var bucket = bucketList[i];
            groups.AddRange(BuildGroups(bucket.SkeletonPath, bucket.RelativeDirectory, bucket.Assets));

            if (progress is null)
            {
                continue;
            }

            var percent = (int)((i + 1) * 100L / bucketList.Count);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress.Report(percent / 100.0);
            }
        }

        return groups
            .OrderBy(group => group.RelativeDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public override string ToString()
    {
        return $"Combined: {Name} ({Assets.Count} parts)";
    }

    public bool Matches(string inputRoot, string query)
    {
        _ = inputRoot;
        return _searchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchText(
        string name,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<ModelAsset> assets)
    {
        var parts = new List<string>
        {
            $"Combined: {name}",
            name,
            relativeDirectory,
            Path.GetFileNameWithoutExtension(skeletonPath),
            Path.GetFileName(skeletonPath),
        };

        foreach (var asset in assets)
        {
            parts.Add(Path.GetFileNameWithoutExtension(asset.MeshPath));
            parts.Add(Path.GetFileName(asset.MeshPath));
            if (!string.IsNullOrWhiteSpace(relativeDirectory))
            {
                parts.Add(Path.Combine(relativeDirectory, Path.GetFileName(asset.MeshPath)));
            }

            var directory = Path.GetDirectoryName(asset.MeshPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                parts.Add(Path.GetFileName(directory));
            }

            if (!string.IsNullOrWhiteSpace(asset.SkeletonPath))
            {
                parts.Add(Path.GetFileNameWithoutExtension(asset.SkeletonPath));
                parts.Add(Path.GetFileName(asset.SkeletonPath));
            }
        }

        return string.Join(
            '\n',
            parts.Where(part => !string.IsNullOrWhiteSpace(part))
                 .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // Held props that the character can appear with or without (e.g. Nerissa's lily flower). Each is
    // offered as an optional add-on: every group gets a twin that includes it, keeping the plain one too.
    private static readonly string[] OptionalAccessoryTails =
        ["lily", "flower", "rose", "bouquet", "petal"];

    private static IEnumerable<ModelAssetGroup> BuildGroups(
        string skeletonPath,
        string relativeDirectory,
        List<ModelAsset> assets)
    {
        // Drop parts that must never be merged into a combined model before any grouping runs. They
        // still appear individually in the tree; they just never join a group.
        var combinable = assets
            .Where(asset => !IsExcludedFromCombine(Path.GetFileNameWithoutExtension(asset.MeshPath)))
            .ToList();

        var core = BuildGroupsCore(skeletonPath, relativeDirectory, combinable).ToList();
        return AppendOptionalAccessories(core, skeletonPath, relativeDirectory, combinable);
    }

    // Parts that should never be combined. Headlight beam cones ("..._lightsBeamsHeadlights") are
    // volumetric light projections, not body geometry: combined, they show up as large stray shapes
    // that do not fit the car. They are kept out of every group and remain available on their own.
    private static bool IsExcludedFromCombine(string stem)
        => stem.Contains("BeamsHeadlights", StringComparison.OrdinalIgnoreCase);


    private static IEnumerable<ModelAssetGroup> AppendOptionalAccessories(
        List<ModelAssetGroup> core,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<ModelAsset> assets)
    {
        if (core.Count == 0)
        {
            return core;
        }

        var skeletonStem = Path.GetFileNameWithoutExtension(skeletonPath);
        var accessories = assets
            .Where(asset => OptionalAccessoryTails.Contains(
                ExtractTail(Path.GetFileNameWithoutExtension(asset.MeshPath), skeletonStem),
                StringComparer.OrdinalIgnoreCase))
            .DistinctBy(asset => asset.MeshPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accessories.Count == 0)
        {
            return core;
        }

        var result = new List<ModelAssetGroup>(core);
        foreach (var accessory in accessories)
        {
            var tail = ExtractTail(Path.GetFileNameWithoutExtension(accessory.MeshPath), skeletonStem);
            var suffix = "_with" + char.ToUpperInvariant(tail[0]) + tail[1..];
            foreach (var group in core)
            {
                if (group.Assets.Any(asset =>
                        asset.MeshPath.Equals(accessory.MeshPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(CreateGroup(
                    group.Name + suffix,
                    skeletonPath,
                    relativeDirectory,
                    group.Assets.Append(accessory)));
            }
        }

        return result;
    }

    private static IEnumerable<ModelAssetGroup> BuildGroupsCore(
        string skeletonPath,
        string relativeDirectory,
        List<ModelAsset> assets)
    {
        if (assets.Count <= 1)
        {
            return [];
        }

        var skeletonStem = Path.GetFileNameWithoutExtension(skeletonPath);

        // Several character versions can share one skeleton (sk54_lee is used by sk54_lee, sk54_lee105,
        // sk54_lee104...). Split the bucket by model stem so each version combines as its own complete
        // model instead of all collapsing into the same slots. A single model (the common case, incl.
        // every Wolf Among Us character and the cars) yields one partition and behaves exactly as before.
        var byModel = assets
            .GroupBy(asset => ModelStem(Path.GetFileNameWithoutExtension(asset.MeshPath), skeletonStem),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (byModel.Count > 1)
        {
            return byModel.SelectMany(group =>
                BuildGroupsForModel(skeletonPath, group.Key, relativeDirectory, group.ToList()));
        }

        return BuildGroupsForModel(skeletonPath, skeletonStem, relativeDirectory, assets);
    }

    // Builds the groups for one model (identified by <paramref name="stem"/>, which is the skeleton name
    // possibly plus an episode-number infix). The stem — not the raw skeleton name — drives part-name
    // classification and group naming.
    private static IEnumerable<ModelAssetGroup> BuildGroupsForModel(
        string skeletonPath,
        string stem,
        string relativeDirectory,
        List<ModelAsset> assets)
    {
        if (assets.Count <= 1)
        {
            return [];
        }

        var skeletonStem = stem;

        if (GameConfig.Current.IsBackToTheFuture)
        {
            return [];
        }

        // The Wither Storm is a single giant creature whose parts (three heads, eyelids, mouths, armour,
        // command block, growths, debris, tentacles) all coexist in one rig. The generic slot classifier
        // mistakes its Left/Middle/Right heads for mutually-exclusive variants and drops the many unnamed
        // structural pieces, so the default combine came out as a lone head. Assemble the whole model
        // instead, with the mouth state (and the transformed middle head) as the only real alternatives.
        if (skeletonStem.Contains("witherstorm", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWitherStormGroups(skeletonStem, skeletonPath, relativeDirectory, assets).ToList();
        }

        var parts = assets
            .Select(asset => ClassifyPart(asset, skeletonStem))
            .ToList();
        var recognized = parts
            .Where(part => part.IsRecognized)
            .ToList();

        // Hurt characters (Grendel, Woodsman, Bigby, Dee...): geometry-driven damage presets that pick
        // exactly one variant per body spot, so clean and wounded parts never overlap. When this system
        // is in play it owns the clean baseline (_clean), so the plain _default and the old additive
        // handling are suppressed; non-damage slot variants (e.g. teethWolf) are still kept.
        if (DamageVariantPlanner.HasDamageSystem(skeletonStem, assets))
        {
            var damagePresets = DamageVariantPlanner
                .Plan(skeletonStem, assets)
                .Select(planned => CreateGroup(
                    planned.Name, skeletonPath, relativeDirectory, planned.Parts, planned.DecalParts))
                .ToList();
            // Only take over when the geometry actually supports clean swaps (replacement systems like
            // Grendel). Decal systems return nothing here and fall through to the normal clean combine.
            if (damagePresets.Count > 0)
            {
                var groups = new List<ModelAssetGroup>();
                if (recognized.Count > 0)
                {
                    groups.AddRange(BuildCharacterGroups(
                        skeletonStem, skeletonPath, relativeDirectory, recognized, parts, includeDamagePresets: false));
                }

                groups.AddRange(damagePresets);
                return groups;
            }
        }

        // Characters: split into recognized body slots (body/head/arms/...), with variant handling.
        var characterGroups = recognized.Count > 0
            ? BuildCharacterGroups(skeletonStem, skeletonPath, relativeDirectory, recognized, parts).ToList()
            : [];
        if (characterGroups.Count > 0)
        {
            return characterGroups;
        }

        // Prop/vehicle fallback: parts named after the shared skeleton (e.g. cars split into
        // obj_carPolice_carBody / _wheels / _lightBarOff). A stray slot-like name such as a car's
        // "_body" alone never forms a character group, so these correctly land here and are
        // combined by model prefix. Used for previewing, exporting and reimporting as one model.
        var prefixGroups = BuildGenericPrefixGroups(skeletonStem, skeletonPath, relativeDirectory, assets).ToList();
        if (prefixGroups.Count > 0)
        {
            return prefixGroups;
        }

        if (assets.Count <= 3 && ShouldCreateUnrecognizedGroup(skeletonStem, assets))
        {
            return [CreateGroup(skeletonStem, skeletonPath, relativeDirectory, assets)];
        }

        return [];
    }

    // Assembles the complete Wither Storm from its part meshes. Every structural piece (body, heads,
    // eyelids, armour, command block, growths, debris, tentacles) is kept; only the per-head mouth state
    // (Default vs Open) and the transformed middle head are mutually-exclusive, so they become the
    // variant axis. This produces a whole creature instead of the single-head fragment the slot
    // classifier used to emit.
    private static IEnumerable<ModelAssetGroup> BuildWitherStormGroups(
        string skeletonStem,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<ModelAsset> assets)
    {
        var tailed = assets
            .Select(asset => (Asset: asset, Tail: ExtractTail(Path.GetFileNameWithoutExtension(asset.MeshPath), skeletonStem)))
            .Where(part => !string.IsNullOrEmpty(part.Tail))
            .ToList();
        if (tailed.Count <= 1)
        {
            return [];
        }

        bool IsMouth(string tail, string state) => tail.StartsWith("mouth" + state, StringComparison.OrdinalIgnoreCase);
        bool IsTransformed(string tail) => tail.Contains("Transformed", StringComparison.OrdinalIgnoreCase);
        bool IsMiddle(string tail) => tail.Contains("Middle", StringComparison.OrdinalIgnoreCase);
        // The debris clusters are the loose blocks scattered far around the creature (bounds span ~5-11
        // units vs the ~2.5 body). They clutter the silhouette, so they form a removable layer and a
        // "clean" group leaves just the recognisable Wither body, growths, armour and command block.
        bool IsDebris(string tail) => tail.StartsWith("debris", StringComparison.OrdinalIgnoreCase);
        // Armour plates and growths are the bulky outer masses. A "core" group drops them (and the debris)
        // to expose just the body, heads and the command block at the centre.
        bool IsBulk(string tail) => IsDebris(tail)
            || tail.StartsWith("armor", StringComparison.OrdinalIgnoreCase)
            || tail.StartsWith("growth", StringComparison.OrdinalIgnoreCase);

        var mouthDefault = tailed.Where(part => IsMouth(part.Tail, "Default")).ToList();
        var mouthOpen = tailed.Where(part => IsMouth(part.Tail, "Open")).ToList();
        var transformed = tailed.Where(part => IsTransformed(part.Tail)).ToList();
        // Structural = everything that is neither a mouth state nor the transformed head. These pieces are
        // always present and define the bulk of the creature.
        var structural = tailed
            .Where(part => !IsMouth(part.Tail, "Default") && !IsMouth(part.Tail, "Open") && !IsTransformed(part.Tail))
            .Select(part => part.Asset)
            .ToList();
        var structuralNoDebris = tailed
            .Where(part => !IsMouth(part.Tail, "Default") && !IsMouth(part.Tail, "Open")
                           && !IsTransformed(part.Tail) && !IsDebris(part.Tail))
            .Select(part => part.Asset)
            .ToList();
        var structuralCore = tailed
            .Where(part => !IsMouth(part.Tail, "Default") && !IsMouth(part.Tail, "Open")
                           && !IsTransformed(part.Tail) && !IsBulk(part.Tail))
            .Select(part => part.Asset)
            .ToList();
        var hasDebris = structural.Count != structuralNoDebris.Count;
        var hasBulk = structural.Count != structuralCore.Count;

        var primaryMouth = (mouthDefault.Count > 0 ? mouthDefault : mouthOpen)
            .Select(part => part.Asset)
            .ToList();

        var groups = new List<ModelAssetGroup>();

        // The complete model (canonical name = the skeleton stem), closed mouths when available.
        groups.Add(CreateGroup(skeletonStem, skeletonPath, relativeDirectory, structural.Concat(primaryMouth)));

        // Clean version without the scattered debris blocks: just the Wither body and its growths.
        if (hasDebris)
        {
            groups.Add(CreateGroup(
                $"{skeletonStem}_clean",
                skeletonPath,
                relativeDirectory,
                structuralNoDebris.Concat(primaryMouth)));
        }

        // Core version: drops the armour, growths and debris to leave only the body, heads and command
        // block at the centre.
        if (hasBulk && structuralCore.Count != structuralNoDebris.Count)
        {
            groups.Add(CreateGroup(
                $"{skeletonStem}_core",
                skeletonPath,
                relativeDirectory,
                structuralCore.Concat(primaryMouth)));
        }

        // Alternate mouth state: same creature with every mouth open.
        if (mouthDefault.Count > 0 && mouthOpen.Count > 0)
        {
            groups.Add(CreateGroup(
                $"{skeletonStem}_mouthOpen",
                skeletonPath,
                relativeDirectory,
                structural.Concat(mouthOpen.Select(part => part.Asset))));
        }

        // Transformed head: the middle head (and its eyelids/mouth) is swapped for the transformed mesh.
        if (transformed.Count > 0)
        {
            var withoutMiddle = structural.Where(asset =>
                !IsMiddle(ExtractTail(Path.GetFileNameWithoutExtension(asset.MeshPath), skeletonStem)));
            var primaryMouthNoMiddle = primaryMouth.Where(asset =>
                !IsMiddle(ExtractTail(Path.GetFileNameWithoutExtension(asset.MeshPath), skeletonStem)));
            groups.Add(CreateGroup(
                $"{skeletonStem}_headTransformed",
                skeletonPath,
                relativeDirectory,
                withoutMiddle.Concat(primaryMouthNoMiddle).Concat(transformed.Select(part => part.Asset))));
        }

        return groups;
    }

    private static IEnumerable<ModelAssetGroup> BuildCharacterGroups(
        string skeletonStem,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<PartInfo> recognized,
        IReadOnlyList<PartInfo> allParts,
        bool includeDamagePresets = true)
    {
        // One default part per slot. Many fragmented characters (e.g. Walking Dead Season 2) have no
        // plain base for a slot — only stated variants (headHat/headNoHat, armLSleeveUp/Down + bites) —
        // so the default picks the cleanest representative (prefer no variant, then the fewest
        // damage/state markers) instead of dropping the slot and leaving the body missing limbs.
        var defaultParts = recognized
            .GroupBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(PickSlotDefault)
            .ToList();

        // When DamageVariantPlanner owns this character it produces the clean baseline (_clean), so the
        // plain _default and the additive presets below are skipped to avoid a duplicate/weaker clean.
        if (includeDamagePresets)
        {
            var defaultGroupParts = BuildDefaultGroupParts(defaultParts, recognized).ToList();
            if (defaultGroupParts.Count > 1)
            {
                yield return CreateGroup(
                    $"{skeletonStem}_default",
                    skeletonPath,
                    relativeDirectory,
                    defaultGroupParts.Select(part => part.Asset));
            }

            // Damage/injury states for the simple originalState/damageState convention (kept as a fallback
            // for characters the geometry planner cannot load; the planner is preferred when available).
            foreach (var damageGroup in BuildDamageStateGroups(
                         skeletonStem,
                         skeletonPath,
                         relativeDirectory,
                         defaultParts,
                         allParts))
            {
                yield return damageGroup;
            }
        }

        var completeVariantGroups = BuildCompleteVariantGroups(
                skeletonStem,
                skeletonPath,
                relativeDirectory,
                defaultParts,
                recognized)
            .ToList();
        var completeVariantPartPaths = completeVariantGroups
            .SelectMany(group => group.Group.Assets)
            .Select(asset => asset.MeshPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var completeVariantGroup in completeVariantGroups)
        {
            yield return completeVariantGroup.Group;
        }

        // Additive variants (e.g. Beast's headBeastOutBrokenNose) are normally left out of variant
        // groups, but for characters not driven by the geometry damage planner they are the only way to
        // surface coupled states like "beast out + broken nose". They still need a related sibling
        // variant to combine with, so lone additive parts never form a group.
        var variantParts = recognized
            .Where(part => !string.IsNullOrEmpty(part.Variant) && (includeDamagePresets || !part.IsAdditive))
            .ToList();
        foreach (var variantGroup in variantParts.GroupBy(part => part.Variant, StringComparer.OrdinalIgnoreCase))
        {
            var variants = variantGroup.ToList();
            if (variants.Any(part => completeVariantPartPaths.Contains(part.Asset.MeshPath)))
            {
                continue;
            }

            var relatedVariants = FindRelatedVariantParts(variants, variantParts).ToList();
            // A variant occupying a slot replaces that slot's default part. Additive damage decals are
            // never in this loop for planner-driven characters, so additive variants reaching here (e.g.
            // Beast's headBeastOutBrokenNose) are real replacements and must override their slot.
            var overriddenSlots = variants
                .Concat(relatedVariants)
                .Select(part => part.Slot)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groupParts = defaultParts
                .Where(part => !overriddenSlots.Contains(part.Slot))
                .ToList();
            groupParts.AddRange(relatedVariants);
            groupParts.AddRange(variants);

            // Keep exactly one part per slot so a preset never ends up with two left arms / two heads.
            // Prefer the explicitly selected variant, then a related variant, then the default part.
            var variantPaths = variants.Select(part => part.Asset.MeshPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relatedPaths = relatedVariants.Select(part => part.Asset.MeshPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = groupParts
                .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                .GroupBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
                .Select(slotGroup => slotGroup
                    .OrderByDescending(part => variantPaths.Contains(part.Asset.MeshPath) ? 2
                        : relatedPaths.Contains(part.Asset.MeshPath) ? 1 : 0)
                    .ThenBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                    .First())
                .ToList();
            if (unique.Count <= 1)
            {
                continue;
            }

            var variantName = variants.Count == 1
                ? variants[0].Tail
                : variantGroup.Key;
            yield return CreateGroup(
                $"{skeletonStem}_{variantName}",
                skeletonPath,
                relativeDirectory,
                unique.Select(part => part.Asset));
        }
    }

    // Builds damage/injury presets for characters with additive state parts (e.g. sk15_grendel).
    // Each wound location appears as a clean/wounded pair (originalState<Spot> / damageState<Spot>)
    // that the game swaps at the same spot. Standalone additive states (e.g. armSevered) have no
    // clean counterpart. Generated presets, all on top of the clean baseline:
    //   <skel>_fullyDamaged           every wound active
    //   <skel>_<Spot>                 only that one wound active (rest clean)
    //   <skel>_<state>                one standalone state active (e.g. armSevered)
    private static IEnumerable<ModelAssetGroup> BuildDamageStateGroups(
        string skeletonStem,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<PartInfo> defaultParts,
        IReadOnlyList<PartInfo> allParts)
    {
        // Detect state parts straight from the name so we also catch ones the slot classifier leaves
        // unrecognized (e.g. armSevered, whose "evered" tail looks like a lowercase variant).
        var stateParts = allParts
            .Where(part => !string.IsNullOrEmpty(part.Tail) && LooksLikeAdditiveStatePart(part.Tail))
            .ToList();
        var spots = new Dictionary<string, (PartInfo? Original, PartInfo? Damage)>(StringComparer.OrdinalIgnoreCase);
        var standalone = new List<PartInfo>();
        foreach (var part in stateParts)
        {
            if (TryGetStateSpot(part.Tail, "damageState", out var damageSpot))
            {
                var entry = spots.GetValueOrDefault(damageSpot);
                spots[damageSpot] = (entry.Original, part);
            }
            else if (TryGetStateSpot(part.Tail, "originalState", out var originalSpot))
            {
                var entry = spots.GetValueOrDefault(originalSpot);
                spots[originalSpot] = (part, entry.Damage);
            }
            else
            {
                standalone.Add(part);
            }
        }

        var damageSpots = spots
            .Where(spot => spot.Value.Damage is not null)
            .OrderBy(spot => spot.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only the explicit originalState/damageState paired convention is auto-presetted: clean and
        // wounded patches are unambiguous and mutually exclusive per spot, so swapping never overlaps.
        // Characters using other ad-hoc damage schemes (e.g. bigby's many headDamage* decals) are left
        // untouched to avoid flooding the tree with dozens of speculative combinations. Standalone
        // states (e.g. armSevered) are only offered alongside a recognized paired system.
        if (damageSpots.Count == 0)
        {
            yield break;
        }

        // Clean baseline = default parts + every spot's clean (original) patch. Matches _default.
        var cleanBase = defaultParts
            .Concat(spots.Values.Select(value => value.Original).OfType<PartInfo>())
            .ToList();

        // Fully damaged: swap every wound spot to its damaged patch (clean fallback when none).
        if (damageSpots.Count > 0)
        {
            var fullyDamaged = defaultParts
                .Concat(spots.Values.Select(value => value.Damage ?? value.Original).OfType<PartInfo>())
                .ToList();
            yield return CreateGroup(
                $"{skeletonStem}_fullyDamaged",
                skeletonPath,
                relativeDirectory,
                fullyDamaged.Select(part => part.Asset));
        }

        // One preset per wound: that spot damaged, every other spot clean.
        foreach (var spot in damageSpots)
        {
            var parts = defaultParts
                .Concat(spots
                    .Where(other => !other.Key.Equals(spot.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(other => other.Value.Original)
                    .OfType<PartInfo>())
                .Append(spot.Value.Damage!)
                .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (parts.Count > 1)
            {
                yield return CreateGroup(
                    $"{skeletonStem}_{spot.Key}",
                    skeletonPath,
                    relativeDirectory,
                    parts.Select(part => part.Asset));
            }
        }

        // One preset per standalone additive state (e.g. armSevered) on the clean baseline.
        foreach (var part in standalone)
        {
            var parts = cleanBase
                .Append(part)
                .DistinctBy(item => item.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (parts.Count > 1)
            {
                yield return CreateGroup(
                    $"{skeletonStem}_{part.Tail}",
                    skeletonPath,
                    relativeDirectory,
                    parts.Select(item => item.Asset));
            }
        }
    }

    private static bool TryGetStateSpot(string tail, string prefix, out string spot)
    {
        if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && tail.Length > prefix.Length)
        {
            spot = tail[prefix.Length..];
            return true;
        }

        spot = "";
        return false;
    }

    // Groups prop/vehicle parts that are not recognized as character slots. Parts that share a
    // model prefix derived from the skeleton (e.g. obj_carPolice_carBody, obj_carPolice_wheels)
    // are combined, while differently-prefixed variants under the same skeleton stay separate
    // (e.g. obj_carPolice vs obj_carPoliceWithInterior).
    private static IEnumerable<ModelAssetGroup> BuildGenericPrefixGroups(
        string skeletonStem,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<ModelAsset> assets)
    {
        var byPrefix = new Dictionary<string, List<ModelAsset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var stem = Path.GetFileNameWithoutExtension(asset.MeshPath);

            // A vehicle's identity is its skeleton. Group the bare body (named exactly like the
            // skeleton, e.g. obj_carGenericB) together with every "<skeleton>_<component>" part under a
            // single prefix. This both keeps the body in the group (it used to be dropped, leaving the
            // car invisible) and pulls in two-segment components like "_lightsTurnSignal_L"/"_R" that
            // GetModelPrefix would otherwise split into their own group. Parts with a genuinely
            // different model prefix that merely begins with the skeleton name (e.g.
            // obj_carPoliceWithInterior_* sharing obj_carPolice.skl) keep their own prefix and stay
            // separate, because they do not start with "<skeleton>_".
            string? prefix;
            if (stem.Equals(skeletonStem, StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith(skeletonStem + "_", StringComparison.OrdinalIgnoreCase))
            {
                prefix = skeletonStem;
            }
            else
            {
                prefix = GetModelPrefix(stem);
                if (prefix is null || !prefix.StartsWith(skeletonStem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!byPrefix.TryGetValue(prefix, out var list))
            {
                list = [];
                byPrefix[prefix] = list;
            }

            list.Add(asset);
        }

        foreach (var pair in byPrefix.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Value.Count <= 1)
            {
                continue;
            }

            foreach (var group in BuildPrefixVariantGroups(pair.Key, skeletonPath, relativeDirectory, pair.Value))
            {
                yield return group;
            }
        }
    }

    // Splits a prop/vehicle prefix group into mutually-exclusive variants. Some parts share a spot and
    // must not be combined together (a taxi's roof sign A — the medallion — vs roof sign B — the ad
    // banner; a police car's lightBarOff vs lightBarOn). Such parts are detected as "sibling" names
    // (a shared base plus a short distinguishing suffix like A/B/Off/On) that ALSO overlap in space.
    // Each variant yields its own complete combined model; parts that are not variants stay in every one.
    private static IEnumerable<ModelAssetGroup> BuildPrefixVariantGroups(
        string prefix,
        string skeletonPath,
        string relativeDirectory,
        List<ModelAsset> parts)
    {
        var items = parts
            .Select(asset => new VariantItem(
                asset,
                Path.GetFileNameWithoutExtension(asset.MeshPath),
                DamageVariantPlanner.GetBounds(asset.MeshPath)))
            .ToList();

        // The single largest mesh is the prop's main body and must always be present, so it never acts
        // as a swappable variant. This stops a small proxy part (e.g. obj_carMary_obj, a bumper-sized
        // mesh sitting inside the full obj_carMary_car body) from pairing with the body and producing a
        // bodyless combination. Real alternates that are not the body (a taxi's roof sign A vs B, a
        // police car's light bar on vs off) are unaffected, since the body never paired with them anyway.
        var dominantIndex = -1;
        var dominantVolume = double.NegativeInfinity;
        for (var i = 0; i < items.Count; i++)
        {
            var volume = items[i].Bounds is { } bounds ? Volume(bounds) : 0;
            if (volume > dominantVolume)
            {
                dominantVolume = volume;
                dominantIndex = i;
            }
        }

        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                if (i != dominantIndex && j != dominantIndex && AreExclusiveVariants(items[i], items[j]))
                {
                    parent[Find(i)] = Find(j);
                }
            }
        }

        var components = Enumerable.Range(0, items.Count)
            .GroupBy(Find)
            .Select(group => group.Select(index => items[index]).ToList())
            .ToList();
        var variantSets = components.Where(component => component.Count >= 2).ToList();
        if (variantSets.Count == 0)
        {
            yield return CreateGroup(prefix, skeletonPath, relativeDirectory, parts);
            yield break;
        }

        var alwaysOn = components.Where(component => component.Count == 1).SelectMany(component => component).ToList();
        foreach (var combo in CartesianProduct(variantSets))
        {
            var groupParts = alwaysOn.Concat(combo).Select(item => item.Asset);
            var name = prefix + "_" + string.Join("_", combo.Select(item => TailAfterPrefix(item.Stem, prefix)));
            yield return CreateGroup(name, skeletonPath, relativeDirectory, groupParts);
        }
    }

    // Two parts are mutually-exclusive variants when they occupy the same space (one largely inside the
    // other) AND their names are siblings: a shared base with a short distinguishing tail (A/B, Off/On).
    // The geometry test keeps left/right pairs (no overlap) apart; the short-tail test keeps a part nested
    // inside another but with its own name (e.g. a seat inside the body) from being treated as a variant.
    private static bool AreExclusiveVariants(VariantItem a, VariantItem b)
    {
        if (a.Bounds is null || b.Bounds is null || Containment(a.Bounds.Value, b.Bounds.Value) < 0.5)
        {
            return false;
        }

        var common = CommonPrefixLength(a.Stem, b.Stem);
        var tailA = a.Stem.Length - common;
        var tailB = b.Stem.Length - common;
        return common >= 3 && tailA is >= 1 and <= 4 && tailB is >= 1 and <= 4;
    }

    // Overlap relative to the smaller part: ~1 when the small part sits inside the larger one.
    private static double Containment(DamageVariantPlanner.Bounds a, DamageVariantPlanner.Bounds b)
    {
        var ix = Math.Max(0f, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var iy = Math.Max(0f, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var iz = Math.Max(0f, Math.Min(a.MaxZ, b.MaxZ) - Math.Max(a.MinZ, b.MinZ));
        var inter = (double)ix * iy * iz;
        if (inter <= 0)
        {
            return 0;
        }

        var volA = (double)(a.MaxX - a.MinX) * (a.MaxY - a.MinY) * (a.MaxZ - a.MinZ);
        var volB = (double)(b.MaxX - b.MinX) * (b.MaxY - b.MinY) * (b.MaxZ - b.MinZ);
        var minVolume = Math.Min(volA, volB);
        return minVolume <= 0 ? 0 : inter / minVolume;
    }

    private static double Volume(DamageVariantPlanner.Bounds b)
        => (double)(b.MaxX - b.MinX) * (b.MaxY - b.MinY) * (b.MaxZ - b.MinZ);

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }

        return i;
    }

    private static string TailAfterPrefix(string stem, string prefix)
    {
        return stem.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
            ? stem[(prefix.Length + 1)..]
            : stem;
    }

    private static IEnumerable<List<VariantItem>> CartesianProduct(IReadOnlyList<List<VariantItem>> sets)
    {
        IEnumerable<List<VariantItem>> result = [[]];
        foreach (var set in sets)
        {
            result = result.SelectMany(combo => set.Select(item => new List<VariantItem>(combo) { item }));
        }

        return result;
    }

    private sealed record VariantItem(ModelAsset Asset, string Stem, DamageVariantPlanner.Bounds? Bounds);

    // Returns the model prefix for a part stem by dropping the trailing "_part" segment, e.g.
    // "obj_carPolice_carBody" -> "obj_carPolice". Returns null when there is no part segment.
    private static string? GetModelPrefix(string stem)
    {
        var lastUnderscore = stem.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore >= stem.Length - 1)
        {
            return null;
        }

        return stem[..lastUnderscore];
    }

    private static IEnumerable<CompleteVariantGroup> BuildCompleteVariantGroups(
        string skeletonStem,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<PartInfo> defaultParts,
        IReadOnlyList<PartInfo> recognized)
    {
        var variantParts = recognized
            .Where(part => !string.IsNullOrEmpty(part.Variant) && IsCompleteVariantSlot(part.Slot))
            .ToList();
        foreach (var variantGroup in variantParts.GroupBy(part => part.Variant, StringComparer.OrdinalIgnoreCase))
        {
            var variants = variantGroup
                .GroupBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase).First())
                .ToList();
            if (variants.Select(part => part.Slot).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            {
                continue;
            }

            var overriddenSlots = variants
                .Select(part => part.Slot)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groupParts = defaultParts
                .Where(part => !overriddenSlots.Contains(part.Slot))
                .ToList();
            groupParts.AddRange(variants);

            var unique = groupParts
                .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unique.Count <= 1)
            {
                continue;
            }

            yield return new CompleteVariantGroup(
                CreateGroup(
                    $"{skeletonStem}_{variantGroup.Key}",
                    skeletonPath,
                    relativeDirectory,
                    unique.Select(part => part.Asset)),
                variants);
        }
    }

    private static IEnumerable<PartInfo> BuildDefaultGroupParts(
        IReadOnlyList<PartInfo> defaultParts,
        IReadOnlyList<PartInfo> recognized)
    {
        return defaultParts
            .Concat(recognized.Where(part => IsOriginalStatePart(part.Tail)))
            .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCompleteVariantSlot(string slot)
    {
        return slot.Equals("body", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("bodyUpper", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("bodyLower", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("head", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("headHands", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("hands", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("arms", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("legs", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("feet", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("eyes", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("eye", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("hair", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("hat", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("glasses", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("holster", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("revolver", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("pistol", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("weapon", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("gun", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<PartInfo> FindRelatedVariantParts(
        IReadOnlyList<PartInfo> selectedVariants,
        IReadOnlyList<PartInfo> allVariants)
    {
        // The selected variant may be additive (e.g. Beast's headBeastOutBrokenNose); we still want to
        // pull in its non-additive siblings from other slots (eyesBeastOut) so the coupled state is whole.
        foreach (var variant in selectedVariants)
        {
            foreach (var candidate in allVariants)
            {
                if (candidate.IsAdditive ||
                    candidate.Asset == variant.Asset ||
                    candidate.Slot.Equals(variant.Slot, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(candidate.Variant))
                {
                    continue;
                }

                if (variant.Variant.StartsWith(candidate.Variant, StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static ModelAssetGroup CreateGroup(
        string name,
        string skeletonPath,
        string relativeDirectory,
        IEnumerable<ModelAsset> assets,
        IReadOnlySet<string>? decalOffsetMeshPaths = null)
    {
        return new ModelAssetGroup(
            Sanitize(name),
            skeletonPath,
            relativeDirectory,
            assets
                .DistinctBy(asset => asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            decalOffsetMeshPaths);
    }

    private static PartInfo ClassifyPart(ModelAsset asset, string skeletonStem)
    {
        var stem = Path.GetFileNameWithoutExtension(asset.MeshPath);
        var tail = ExtractTail(stem, skeletonStem);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return new PartInfo(asset, tail, "", "", false);
        }

        // Left/right parts (legL/legR, neckL/neckR, armLSleeveUp, earLeft, eyeRightDamageA...) are
        // distinct sides that coexist, so the side belongs to the slot identity, not the variant.
        if (TryClassifySided(tail) is { } sided)
        {
            return new PartInfo(asset, tail, sided.Slot, sided.Variant, true, LooksLikeAdditiveStatePart(tail));
        }

        var slot = KnownSlots
            .OrderByDescending(value => value.Length)
            .FirstOrDefault(candidate =>
                tail.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                tail.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            if (LooksLikeAdditiveStatePart(tail))
            {
                return new PartInfo(asset, tail, "state", tail, true, true);
            }

            return new PartInfo(asset, tail, "", "", false);
        }

        var variant = tail.Length == slot.Length ? "" : tail[slot.Length..];
        if (variant.Length > 0 && char.IsLower(variant[0]))
        {
            return new PartInfo(asset, tail, "", "", false);
        }

        return new PartInfo(asset, tail, slot, variant, true, LooksLikeAdditiveStatePart(tail));
    }

    // Body parts that come in left/right copies. A part named "<base><Side><rest>" is the <Side> copy of
    // that part; both sides are kept (they coexist), and <rest> is the part's variant.
    private static readonly string[] SidedBases =
    [
        "shoulder", "clavicle", "forearm", "elbow", "thigh", "knee", "ankle", "wrist",
        "leg", "arm", "hand", "foot", "neck", "ear", "eye", "brow", "cheek", "lip", "horn",
    ];
    // NOTE: "mouth" is deliberately NOT a sided base. Character mouths are visemes (mouthAA, mouthLL,
    // mouthI...), and treating "mouthLL" as "mouth" + side "L" split it into its own slot, so the combine
    // ended up with two mouths. The Wither Storm's positional mouths are handled by its own planner.

    // Side tokens, longest first so "Left"/"Right" win before the single-letter "L"/"R".
    private static readonly (string Token, string Canonical)[] SideTokens =
    [
        ("Left", "Left"), ("Right", "Right"), ("L", "L"), ("R", "R"),
    ];

    private static (string Slot, string Variant)? TryClassifySided(string tail)
    {
        foreach (var bone in SidedBases.OrderByDescending(value => value.Length))
        {
            if (!tail.StartsWith(bone, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var afterBone = tail[bone.Length..];
            foreach (var (token, _) in SideTokens)
            {
                if (!afterBone.StartsWith(token, StringComparison.Ordinal))
                {
                    continue;
                }

                var afterSide = afterBone[token.Length..];

                // The side must end the word or be followed by a new uppercase token, so "leg"+"L"+"Bite"
                // matches but "leg"+"acy" (legacy) or "arm"+"or" (armor) does not.
                if (afterSide.Length == 0 || char.IsUpper(afterSide[0]))
                {
                    return (tail[..(bone.Length + token.Length)], afterSide);
                }
            }
        }

        return null;
    }

    // Damage/state words used to tell a clean part from a damaged/alternate one when choosing the
    // default for a slot that has no plain base part.
    private static readonly string[] StateWords =
    [
        "Damage", "Bite", "Bitten", "Bandaged", "Sewn", "Dog", "Dried", "Broken", "Bloody", "Blood",
        "Cut", "Amputated", "Gutted", "Ripped", "Torn", "Tourniqet", "Wound", "Scratch", "Bruise",
        "Sever", "Rotten", "Burn", "Gun", "Stomach", "NoBandage",
    ];

    // Picks the most "default-looking" part for a slot: prefer no variant, then the fewest damage/state
    // markers, then the shortest variant — so a clean limb/head wins over its wounded or alternate forms.
    private static PartInfo PickSlotDefault(IEnumerable<PartInfo> slotParts)
    {
        return slotParts
            .OrderBy(part => string.IsNullOrEmpty(part.Variant) || IsNeutralVariant(part.Variant) ? 0 : 1)
            .ThenBy(part => StateWords.Count(word => part.Variant.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(part => part.Variant.Length)
            .ThenBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    // A "neutral" variant is the resting/closed form a slot should default to (e.g. the mouth viseme
    // "Default" or "None"), so the combined model shows a calm face instead of a random open viseme.
    private static bool IsNeutralVariant(string variant)
    {
        return variant.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
               variant.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               variant.Equals("Neutral", StringComparison.OrdinalIgnoreCase) ||
               variant.Equals("Closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAdditiveStatePart(string tail)
    {
        return tail.StartsWith("damageState", StringComparison.OrdinalIgnoreCase) ||
               tail.StartsWith("originalState", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Damage", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Wound", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Scratch", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Bloody", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Broken", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Bandaged", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Bruise", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Cut", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Ripped", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Sever", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("Transform", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOriginalStatePart(string tail)
    {
        return tail.StartsWith("originalState", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCreateUnrecognizedGroup(string skeletonStem, IReadOnlyList<ModelAsset> assets)
    {
        var tails = assets
            .Select(asset => Path.GetFileNameWithoutExtension(asset.MeshPath))
            .Select(stem => GetDirectSkeletonTail(stem, skeletonStem))
            .ToList();
        return tails.All(tail => tail is not null && IsAllowedUnrecognizedPartTail(tail)) &&
               tails.Any(tail => !string.IsNullOrEmpty(tail));
    }

    private static string? GetDirectSkeletonTail(string meshStem, string skeletonStem)
    {
        if (meshStem.Equals(skeletonStem, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (meshStem.StartsWith(skeletonStem + "_", StringComparison.OrdinalIgnoreCase))
        {
            return meshStem[(skeletonStem.Length + 1)..];
        }

        if (meshStem.StartsWith(skeletonStem, StringComparison.OrdinalIgnoreCase) &&
            meshStem.Length > skeletonStem.Length &&
            char.IsUpper(meshStem[skeletonStem.Length]))
        {
            return meshStem[skeletonStem.Length..];
        }

        return null;
    }

    private static bool IsAllowedUnrecognizedPartTail(string? tail)
    {
        if (tail is null)
        {
            return false;
        }

        return tail.Length == 0 ||
               tail.Equals("root", StringComparison.OrdinalIgnoreCase) ||
               tail.StartsWith("piece", StringComparison.OrdinalIgnoreCase) ||
               tail.StartsWith("part", StringComparison.OrdinalIgnoreCase) ||
               tail.Equals("cap", StringComparison.OrdinalIgnoreCase) ||
               tail.Equals("cylinder", StringComparison.OrdinalIgnoreCase) ||
               tail.Equals("pages", StringComparison.OrdinalIgnoreCase) ||
               tail.Equals("armSevered", StringComparison.OrdinalIgnoreCase) ||
               tail.Equals("fleshArm", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTail(string meshStem, string skeletonStem)
    {
        if (meshStem.Equals(skeletonStem, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (meshStem.StartsWith(skeletonStem + "_", StringComparison.OrdinalIgnoreCase))
        {
            return meshStem[(skeletonStem.Length + 1)..];
        }

        if (meshStem.StartsWith(skeletonStem, StringComparison.OrdinalIgnoreCase))
        {
            return meshStem[skeletonStem.Length..].TrimStart('_');
        }

        return meshStem;
    }

    // The model identity for a part, which may differ from the skeleton name when several character
    // versions share one skeleton (skeleton "sk54_lee" is used by "sk54_lee", "sk54_lee105", ...).
    // Adds an episode-number infix to the skeleton stem so each version combines as its own model.
    private static string ModelStem(string meshStem, string skeletonStem)
    {
        if (!meshStem.StartsWith(skeletonStem, StringComparison.OrdinalIgnoreCase) ||
            meshStem.Length <= skeletonStem.Length)
        {
            return skeletonStem;
        }

        var rest = meshStem[skeletonStem.Length..];
        var digits = 0;
        while (digits < rest.Length && char.IsDigit(rest[digits]))
        {
            digits++;
        }

        return digits > 0 ? skeletonStem + rest[..digits] : skeletonStem;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "combined_asset" : sanitized;
    }

    private sealed record PartInfo(
        ModelAsset Asset,
        string Tail,
        string Slot,
        string Variant,
        bool IsRecognized,
        bool IsAdditive = false);

    private sealed record CompleteVariantGroup(
        ModelAssetGroup Group,
        IReadOnlyList<PartInfo> VariantParts);
}
