namespace TelltaleD3DMeshEditor.UI;

// Virtual extraction group for meshes that belong together in the same source folder.
// This lets character parts such as body/head be exported as one editable GLB/GLTF.
public sealed class ModelAssetGroup
{
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
        "body",
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

    public static List<ModelAssetGroup> Discover(IReadOnlyList<ModelAsset> assets, string inputRoot)
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

        return buckets.Values
            .SelectMany(bucket => BuildGroups(bucket.SkeletonPath, bucket.RelativeDirectory, bucket.Assets))
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
        return ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
               RelativeDirectory.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileNameWithoutExtension(SkeletonPath).Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Assets.Any(asset => asset.Matches(inputRoot, query));
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
        var core = BuildGroupsCore(skeletonPath, relativeDirectory, assets).ToList();
        return AppendOptionalAccessories(core, skeletonPath, relativeDirectory, assets);
    }

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

    private static IEnumerable<ModelAssetGroup> BuildCharacterGroups(
        string skeletonStem,
        string skeletonPath,
        string relativeDirectory,
        IReadOnlyList<PartInfo> recognized,
        IReadOnlyList<PartInfo> allParts,
        bool includeDamagePresets = true)
    {
        var defaultParts = recognized
            .Where(part => string.IsNullOrEmpty(part.Variant))
            .GroupBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase).First())
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

            var unique = groupParts
                .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
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
            var prefix = GetModelPrefix(stem);
            if (prefix is null || !prefix.StartsWith(skeletonStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
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

        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                if (AreExclusiveVariants(items[i], items[j]))
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
