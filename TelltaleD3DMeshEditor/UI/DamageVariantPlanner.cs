using TelltaleD3DMeshEditor.Formats.Mesh;

namespace TelltaleD3DMeshEditor.UI;

// Plans ready-made damage/injury presets for hurt characters (Grendel, Woodsman, Bigby, Dee...).
//
// The game shows wounds by swapping a part for an alternative that occupies the SAME place on the
// body, so clean and wounded geometry must never be shown together. Different characters name these
// differently (originalState/damageState, headDamage<Spot>/<Injury>, headDamageA/B, armSevered that
// replaces the whole body), so instead of trusting names alone the planner groups parts into "spots"
// by overlapping 3D bounds: parts sharing a spot are mutually exclusive. Names are then only used to
// label which member of a spot is the clean baseline and which are wounds.
//
// Output presets (each picks exactly one variant per spot, so nothing overlaps):
//   <skel>_clean           every spot at its clean/baseline variant
//   <skel>_<wound>         one wound active, every other spot clean
//   <skel>_fullyDamaged    every spot at a wounded variant
internal static class DamageVariantPlanner
{
    // Bounds-overlap (IoU) above which two parts are treated as the same swappable spot.
    private const double SameSpotIoU = 0.30;

    // Cell size for quantising vertex positions when measuring surface coincidence.
    private const float CoincidenceCell = 0.006f;

    // Above this fraction of a damage part's surface lying on an always-on anchor, the part is a decal
    // painted onto existing geometry rather than a piece that fills a hole — combining it would overlap.
    private const double DecalCoincidence = 0.9;

    // Looser floor for recognizing the damage "base" head as a replacement of the clean head: it is the
    // same head at lower detail, so it need not be near-identical (Bigby's is only ~81% coincident).
    private const double HeadReplaceCoincidence = 0.5;

    private static readonly Dictionary<string, Geometry?> GeometryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct Bounds(
        float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ);

    // DecalParts: mesh paths whose geometry sits on the surface of an always-on part (decal wounds).
    // The combiner nudges these outward along their normals so they render over the clean skin instead
    // of z-fighting with it (otherwise the wound is hidden in the viewer/export).
    public sealed record PlannedGroup(string Name, List<ModelAsset> Parts, IReadOnlySet<string> DecalParts);

    private sealed record Geometry(Bounds Bounds, int VertexCount, HashSet<long> Cells);

    private sealed record Part(ModelAsset Asset, string Tail, Bounds Bounds, int VertexCount, HashSet<long> Cells);

    private sealed class Spot
    {
        public List<Part> Members { get; } = [];
        public List<Part> Normals { get; } = [];          // clean baseline members (may be empty)
        public Part? AnchorNormal { get; set; }           // a base part this spot replaces (e.g. teeth, body)
        public List<Part> Wounds { get; } = [];           // mutually exclusive wounded variants
        public bool IsAnchorReplacement { get; set; }     // whole-part swap that replaces an anchor
                                                          // (Grendel armSevered over body; Woodsman
                                                          // headDamageBaseBloody over head) — not a decal
    }

    // A bucket uses the damage system when it has at least one strong damage marker. Generic keywords
    // (Bloody/Broken/...) alone do not trigger it, so slot-variant characters such as cakePrince
    // (bodyBloody) keep their existing clean behaviour untouched.
    public static bool HasDamageSystem(string skeletonStem, IEnumerable<ModelAsset> assets)
    {
        return assets.Any(asset => HasStrongDamageMarker(Tail(asset.MeshPath, skeletonStem)));
    }

    // Cached axis-aligned bounds for a mesh, shared with prop/vehicle variant detection.
    public static Bounds? GetBounds(string meshPath) => LoadGeometry(meshPath)?.Bounds;

    public static IReadOnlyList<PlannedGroup> Plan(string skeletonStem, IReadOnlyList<ModelAsset> assets)
    {
        var loaded = assets
            .Select(asset =>
            {
                var geometry = LoadGeometry(asset.MeshPath);
                return geometry is null
                    ? null
                    : new Part(asset, Tail(asset.MeshPath, skeletonStem), geometry.Bounds, geometry.VertexCount, geometry.Cells);
            })
            .OfType<Part>()
            .ToList();
        if (loaded.Count < 2)
        {
            return [];
        }

        // Drop the monolithic skeleton-named mesh (tail == "") — it is the whole, un-split character and
        // duplicates the split parts. Then drop anchors that are name variants of another anchor AND
        // occupy the same region (e.g. teethWolf over teeth, bodyUpperTransformation over bodyUpper);
        // those are alternatives, not always-on parts. The geometry check is essential: bodyUpper starts
        // with "body" by name but is a different region (torso vs legs), so it must be kept.
        loaded = loaded.Where(part => part.Tail.Length > 0).ToList();
        var woundCandidates = loaded.Where(part => IsWoundCandidate(part.Tail)).ToList();
        var anchors = loaded
            .Where(part => !IsWoundCandidate(part.Tail))
            .Where(part => !loaded.Any(other =>
                !IsWoundCandidate(other.Tail) &&
                IsNameExtension(other.Tail, part.Tail) &&
                IoU(other.Bounds, part.Bounds) >= SameSpotIoU))
            .ToList();
        if (woundCandidates.Count == 0)
        {
            return [];
        }

        var spots = ClusterByBounds(woundCandidates);
        foreach (var spot in spots)
        {
            ClassifySpot(spot, anchors);
        }

        if (spots.All(spot => spot.Wounds.Count == 0))
        {
            return [];
        }

        // Decal-style systems (Woodsman/Bigby/Dee): wounds are painted on top of an always-on anchor's
        // surface (the head already contains the clean face), so the "clean" members of each spot are
        // just redundant duplicates of the head. Drop those duplicates so the clean preset isn't doubled;
        // the wounds themselves still layer over the head (overlap is expected for these characters).
        // Replacement systems (Grendel — patches fill holes in the body) coincide only partially, so
        // nothing is dropped and clean/wounded swap without overlap.
        var anchorCells = anchors.SelectMany(anchor => anchor.Cells).ToHashSet();
        var surfaceParts = spots
            .Where(spot => !spot.IsAnchorReplacement)
            .SelectMany(spot => spot.Members)
            .ToList();
        var isDecalSystem = surfaceParts.Count > 0 &&
            MedianCoincidence(surfaceParts, anchorCells) >= DecalCoincidence;
        if (isDecalSystem)
        {
            foreach (var spot in spots)
            {
                spot.Normals.RemoveAll(normal => Coincidence(normal, anchorCells) >= DecalCoincidence);
            }
        }

        // Decal wounds that lie flat on the skin (≥ DecalCoincidence) are nudged out so they win the depth
        // test instead of z-fighting the clean skin. Wounds that already protrude with their own geometry
        // (an axe gash, a broken nose) are NOT nudged — pushing them out makes them float and look wrong.
        var decalParts = (isDecalSystem
                ? spots.Where(spot => !spot.IsAnchorReplacement)
                    .SelectMany(spot => spot.Wounds)
                    .Where(w => Coincidence(w, anchorCells) >= DecalCoincidence)
                : Enumerable.Empty<Part>())
            .Select(wound => Path.GetFullPath(wound.Asset.MeshPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Clean baseline: anchors that are not replaced by a spot, plus each spot's clean member(s).
        var replacedAnchors = spots
            .Select(spot => spot.AnchorNormal)
            .OfType<Part>()
            .ToHashSet();
        var baseAnchors = anchors.Where(anchor => !replacedAnchors.Contains(anchor)).ToList();

        var result = new List<PlannedGroup>();
        PlannedGroup Group(string name, IEnumerable<Part> parts) =>
            new(name, parts.Select(p => p.Asset).ToList(), decalParts);
        List<Part> CleanParts() => baseAnchors
            .Concat(spots.SelectMany(spot => spot.Normals))
            .Concat(spots.Select(spot => spot.AnchorNormal).OfType<Part>())
            .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var clean = CleanParts();
        if (clean.Count > 1)
        {
            result.Add(Group($"{skeletonStem}_default", clean));
        }

        // One preset per individual wound: that spot wounded, everything else clean.
        foreach (var spot in spots)
        {
            foreach (var wound in spot.Wounds)
            {
                var parts = CleanParts()
                    .Where(part => !SpotNormalPaths(spot).Contains(part.Asset.MeshPath))
                    .Append(wound)
                    .DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (parts.Count > 1)
                {
                    result.Add(Group($"{skeletonStem}_{CleanName(wound.Tail)}", parts));
                }
            }
        }

        // Fully damaged: every spot that can be wounded picks its most detailed wound.
        var fully = baseAnchors.ToList();
        foreach (var spot in spots)
        {
            if (spot.Wounds.Count > 0)
            {
                // Pick the heaviest-looking variant per spot for a "fully damaged" look. Many variants
                // differ only by texture (same geometry), so the choice is name-driven.
                fully.Add(spot.Wounds
                    .OrderByDescending(w => DamageRank(w.Tail))
                    .ThenByDescending(w => w.VertexCount)
                    .First());
            }
            else
            {
                fully.AddRange(spot.Normals);
                if (spot.AnchorNormal is not null)
                {
                    fully.Add(spot.AnchorNormal);
                }
            }
        }

        fully = fully.DistinctBy(part => part.Asset.MeshPath, StringComparer.OrdinalIgnoreCase).ToList();
        if (fully.Count > 1 && spots.Count(spot => spot.Wounds.Count > 0) > 1)
        {
            result.Add(Group($"{skeletonStem}_fullyDamaged", fully));
        }

        return result;
    }

    private static HashSet<string> SpotNormalPaths(Spot spot)
    {
        var paths = spot.Normals.Select(part => part.Asset.MeshPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (spot.AnchorNormal is not null)
        {
            paths.Add(spot.AnchorNormal.Asset.MeshPath);
        }

        return paths;
    }

    // Union-find clustering: parts whose bounds overlap strongly belong to the same swappable spot.
    private static List<Spot> ClusterByBounds(List<Part> parts)
    {
        var parent = Enumerable.Range(0, parts.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                if (IoU(parts[i].Bounds, parts[j].Bounds) >= SameSpotIoU)
                {
                    Union(i, j);
                }
            }
        }

        var buckets = new Dictionary<int, Spot>();
        for (var i = 0; i < parts.Count; i++)
        {
            var root = Find(i);
            if (!buckets.TryGetValue(root, out var spot))
            {
                spot = new Spot();
                buckets[root] = spot;
            }

            spot.Members.Add(parts[i]);
        }

        return buckets.Values.ToList();
    }

    // Within a spot, decide which members are the clean baseline and which are wounds.
    private static void ClassifySpot(Spot spot, IReadOnlyList<Part> anchors)
    {
        // 1) Explicit originalState/damageState pairing (Grendel).
        var originals = spot.Members.Where(p => p.Tail.StartsWith("originalState", StringComparison.OrdinalIgnoreCase)).ToList();
        if (originals.Count > 0)
        {
            spot.Normals.AddRange(originals);
            spot.Wounds.AddRange(spot.Members.Except(originals));
            return;
        }

        // 1b) The damage "base" family (headDamageBase/Bandaged/Bloody) is the whole damageable head.
        if (spot.Members.All(member => IsBaseFamily(member.Tail)))
        {
            // When it mirrors a separate clean head mesh, the damaged variants REPLACE that head (same
            // region, different state) — they are NOT layered on top of it (that draws two heads). The
            // clean head stays the baseline; bandaged/bloody become whole-head wounds; the redundant
            // clean base (headDamageBase) is dropped so it never appears in the plain _default. A loose
            // coincidence floor is enough: the damage base is always the head, just a lower-detail copy
            // (Woodsman 100%, Bigby 81%), so this must trigger even when the meshes are not identical.
            var headAnchor = anchors.FirstOrDefault(anchor =>
                anchor.Tail.Equals("head", StringComparison.OrdinalIgnoreCase) &&
                spot.Members.All(member => Coincidence(member, anchor.Cells) >= HeadReplaceCoincidence));
            if (headAnchor is not null)
            {
                spot.AnchorNormal = headAnchor;
                spot.IsAnchorReplacement = true;
                spot.Wounds.AddRange(spot.Members.Where(member => !IsBaseLayer(member.Tail)));
                return;
            }

            // Otherwise it is the only head/face geometry: the plain base is the clean layer, rest wounds.
            var bases = spot.Members.Where(member => IsBaseLayer(member.Tail)).ToList();
            spot.Normals.AddRange(bases);
            spot.Wounds.AddRange(spot.Members.Except(bases));
            return;
        }

        // 2) With several members, the one whose name is the shared base prefix of the others is the
        //    clean one (Woodsman headDamageCheekL -> headDamageCheekLCut; headDamageBase -> ...Bandaged).
        if (spot.Members.Count >= 2)
        {
            var memberNormal = spot.Members
                .Where(candidate => spot.Members.All(other =>
                    ReferenceEquals(other, candidate) || IsNameExtension(candidate.Tail, other.Tail)))
                .OrderBy(p => p.Tail.Length)
                .FirstOrDefault();
            if (memberNormal is not null)
            {
                spot.Normals.Add(memberNormal);
                spot.Wounds.AddRange(spot.Members.Where(p => !ReferenceEquals(p, memberNormal)));
                return;
            }
        }

        // 3) An anchor part that the wounds replace by name (teeth -> teethBroken) — but not when the
        //    suffix starts the damage family (head -> headDamageBase coexist, they are layers).
        var anchorNormal = anchors
            .Where(anchor => spot.Members.All(member => IsReplacementOf(anchor.Tail, member.Tail)))
            .OrderByDescending(anchor => anchor.Tail.Length)
            .FirstOrDefault();
        if (anchorNormal is not null)
        {
            spot.AnchorNormal = anchorNormal;
            spot.Wounds.AddRange(spot.Members);
            return;
        }

        // 4) Body-sized replacements (Grendel armSevered/armSevering swap the whole body).
        var body = anchors
            .FirstOrDefault(anchor => anchor.Tail.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is not null && spot.Members.All(member => IoU(member.Bounds, body.Bounds) >= SameSpotIoU))
        {
            spot.AnchorNormal = body;
            spot.IsAnchorReplacement = true;
            spot.Wounds.AddRange(spot.Members);
            return;
        }

        // 5) No clean counterpart (Dee headDamageA/headDamageB): wounds only, clean = their absence.
        spot.Wounds.AddRange(spot.Members);
    }

    private static Geometry? LoadGeometry(string meshPath)
    {
        var key = meshPath + "|" + SafeWriteTimeTicks(meshPath);
        if (GeometryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Geometry? value;
        try
        {
            var mesh = D3DMeshParser.Parse(File.ReadAllBytes(meshPath));
            var b = mesh.GetBounds();
            var cells = mesh.Submeshes
                .SelectMany(submesh => submesh.Vertices)
                .Select(v => CellKey(v.X, v.Y, v.Z))
                .ToHashSet();
            value = new Geometry(new Bounds(b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ), mesh.VertexCount, cells);
        }
        catch
        {
            value = null;
        }

        GeometryCache[key] = value;
        return value;
    }

    // Fraction of a part's surface cells that lie on the anchor surface, taken at the median across
    // parts so a single outlier does not flip the decision.
    private static double MedianCoincidence(IReadOnlyList<Part> parts, HashSet<long> anchorCells)
    {
        var ratios = parts
            .Where(part => part.Cells.Count > 0)
            .Select(part => Coincidence(part, anchorCells))
            .OrderBy(ratio => ratio)
            .ToList();
        return ratios.Count == 0 ? 0 : ratios[ratios.Count / 2];
    }

    private static double Coincidence(Part part, HashSet<long> anchorCells)
    {
        return part.Cells.Count == 0 ? 0 : (double)part.Cells.Count(anchorCells.Contains) / part.Cells.Count;
    }

    private static long CellKey(float x, float y, float z)
    {
        long qx = (long)MathF.Round(x / CoincidenceCell) + 0x20000;
        long qy = (long)MathF.Round(y / CoincidenceCell) + 0x20000;
        long qz = (long)MathF.Round(z / CoincidenceCell) + 0x20000;
        return (qx << 42) | (qy << 21) | qz;
    }

    private static long SafeWriteTimeTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static double IoU(Bounds a, Bounds b)
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
        var union = volA + volB - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // True when "longer" is "shorter" followed by an uppercase-started suffix (a new name token).
    private static bool IsNameExtension(string shorter, string longer)
    {
        return longer.Length > shorter.Length &&
               longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase) &&
               char.IsUpper(longer[shorter.Length]);
    }

    // A wound name replaces an anchor when it extends the anchor's name with an injury token, but not
    // when the suffix begins the "Damage"/"State" family (those are additive layers, not replacements).
    private static bool IsReplacementOf(string anchorTail, string woundTail)
    {
        if (!IsNameExtension(anchorTail, woundTail))
        {
            return false;
        }

        var suffix = woundTail[anchorTail.Length..];
        return !suffix.StartsWith("Damage", StringComparison.OrdinalIgnoreCase) &&
               !suffix.StartsWith("State", StringComparison.OrdinalIgnoreCase);
    }

    // The clean foundation layer of a head-damage system: tail is exactly "<prefix>Base" with no
    // trailing injury token, so it is always shown (never a wound).
    private static bool IsBaseLayer(string tail)
    {
        foreach (var prefix in new[] { "headDamage", "damageState", "originalState" })
        {
            if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                tail[prefix.Length..].Equals("Base", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Ranks a wound variant for the "fully damaged" pick (heaviest wins). Many variants are texture-only
    // on identical geometry, so this is name-driven: a damage-level letter (Dee's A < B) dominates, then a
    // raw injury (blood/cut/axe/bruise — e.g. headDamageBaseBloody) beats a treated (bandaged) one.
    private static readonly string[] RawDamageWords =
        ["bloody", "blood", "wound", "broken", "axe", "cut", "split", "bruise", "scratch", "ripped", "pop", "black"];

    private static int DamageRank(string tail)
    {
        var raw = RawDamageWords.Count(word => tail.Contains(word, StringComparison.OrdinalIgnoreCase));
        var bandaged = tail.Contains("bandaged", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return DamageLevelLetter(tail) * 100 + raw * 10 - bandaged;
    }

    // Dee names damage stages as headDamage<Level><Spot> (headDamageABrowL/headDamageBBrowL): the letter
    // right after "headDamage", when followed by another uppercase (the spot), is the stage (A=1, B=2...).
    private static int DamageLevelLetter(string tail)
    {
        const string prefix = "headDamage";
        if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            tail.Length > prefix.Length + 1 &&
            char.IsUpper(tail[prefix.Length]) &&
            char.IsUpper(tail[prefix.Length + 1]))
        {
            return char.ToUpperInvariant(tail[prefix.Length]) - 'A' + 1;
        }

        return 0;
    }

    private static bool IsBaseFamily(string tail)
    {
        foreach (var prefix in new[] { "headDamage", "damageState", "originalState" })
        {
            if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                tail[prefix.Length..].StartsWith("Base", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasStrongDamageMarker(string tail)
    {
        return tail.Contains("damage", StringComparison.OrdinalIgnoreCase) ||
               tail.StartsWith("originalState", StringComparison.OrdinalIgnoreCase) ||
               tail.StartsWith("damageState", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("sever", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWoundCandidate(string tail)
    {
        return HasStrongDamageMarker(tail) ||
               tail.Contains("wound", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("broken", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("bloody", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("bruise", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("scratch", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("ripped", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("bandaged", StringComparison.OrdinalIgnoreCase);
    }

    // Strips the noisy convention prefix from a wound name for a readable preset label.
    private static string CleanName(string tail)
    {
        foreach (var prefix in new[] { "damageState", "originalState", "headDamage", "state" })
        {
            if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && tail.Length > prefix.Length)
            {
                return tail[prefix.Length..];
            }
        }

        return tail;
    }

    private static string Tail(string meshPath, string skeletonStem)
    {
        var stem = Path.GetFileNameWithoutExtension(meshPath);
        if (stem.Equals(skeletonStem, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (stem.StartsWith(skeletonStem + "_", StringComparison.OrdinalIgnoreCase))
        {
            return stem[(skeletonStem.Length + 1)..];
        }

        return stem.StartsWith(skeletonStem, StringComparison.OrdinalIgnoreCase)
            ? stem[skeletonStem.Length..].TrimStart('_')
            : stem;
    }
}
