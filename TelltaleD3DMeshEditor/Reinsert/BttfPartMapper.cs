namespace TelltaleD3DMeshEditor.Reinsert;

// Maps imported GLB primitives onto a Back to the Future template's submeshes by texture "part"
// (body/face/hair/eyes/teeth/…) rather than by position. A character swap (e.g. Doc into Marty's slot)
// has a different submesh order than the template, so positional mapping lands each part on a submesh
// that references the wrong texture. Grouping by part keeps each part's geometry on submeshes that
// reference that part's texture, and lets the texture writer put the imported part's image under the
// template part's name — so geometry and textures line up in-game.
public static class BttfPartMapper
{
    // Specific tokens first so e.g. "hairbangs" wins over "hair" and "eyeshadow"/"eyelid" over "eye".
    private static readonly string[] PartTokens =
    [
        "hairbangs", "eyeshadow", "eyelash", "eyebrow", "eyelid", "tongue", "teeth", "tooth",
        "beard", "mustache", "moustache", "hair", "brow", "eye", "mouth", "nose", "ear",
        "face", "head", "neck", "glove", "hand", "finger", "arm", "leg", "foot", "shoe", "boot",
        "shirt", "pants", "jacket", "coat", "hat", "cap", "vest", "tie", "belt", "body", "cloth", "skin",
    ];

    // primitiveBones supplies each primitive's distinct bone hashes and maxBonesPerSubmesh the template's
    // proven per-draw palette size (usually much smaller than the v1 hard limit). Within a part, primitives are distributed
    // across the part's submeshes so each submesh's combined bone set stays within the limit, preventing
    // an oversized palette that the skinning shader cannot address (which shows as stretched/black spikes).
    // Pass null/0 to disable the bone-size constraint (static meshes).
    public static List<List<GltfPrimitive>> Map(
        IReadOnlyList<string?> templateDiffuseNames,
        IReadOnlyList<GltfPrimitive> primitives,
        Func<GltfPrimitive, IReadOnlyCollection<ulong>>? primitiveBones = null,
        int maxBonesPerSubmesh = 0)
    {
        var submeshCount = templateDiffuseNames.Count;
        var groups = new List<List<GltfPrimitive>>(submeshCount);
        var binBones = new List<HashSet<ulong>>(submeshCount);
        for (var i = 0; i < submeshCount; i++)
        {
            groups.Add([]);
            binBones.Add([]);
        }

        if (submeshCount == 0)
        {
            return groups;
        }

        var boneLimit = maxBonesPerSubmesh > 0 ? maxBonesPerSubmesh : int.MaxValue;
        var allTargets = Enumerable.Range(0, submeshCount).ToList();

        // Template submesh indices grouped by part (preserving submesh order within each part).
        var templateByPart = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < submeshCount; i++)
        {
            var part = ExtractPartKey(templateDiffuseNames[i]);
            if (!templateByPart.TryGetValue(part, out var indices))
            {
                indices = [];
                templateByPart[part] = indices;
            }

            indices.Add(i);
        }

        // Imported primitives grouped by part (preserving primitive order, and part first-seen order).
        var primsByPart = new Dictionary<string, List<GltfPrimitive>>(StringComparer.OrdinalIgnoreCase);
        var partOrder = new List<string>();
        foreach (var prim in primitives)
        {
            var part = ExtractPartKey(GetDiffuseName(prim));
            if (!primsByPart.TryGetValue(part, out var list))
            {
                list = [];
                primsByPart[part] = list;
                partOrder.Add(part);
            }

            list.Add(prim);
        }

        foreach (var part in partOrder)
        {
            var prims = primsByPart[part];
            var targets = FindTargets(templateByPart, part);
            if (targets.Count == 0)
            {
                // No template submesh references this part's texture: keep the geometry rather than drop it
                // by folding it into the first submesh (its texture may not match, but nothing disappears).
                targets = [0];
            }

            // First-fit-decreasing: place the bone-heaviest primitives first so a primitive that alone
            // nearly fills a palette gets its own submesh instead of being paired and overflowing.
            var ordered = prims
                .Select(prim => (Prim: prim, Bones: primitiveBones?.Invoke(prim) ?? (IReadOnlyCollection<ulong>)[]))
                .OrderByDescending(entry => entry.Bones.Count)
                .ToList();

            foreach (var (prim, bones) in ordered)
            {
                var target = ChooseTarget(targets, allTargets, binBones, bones, boneLimit);
                groups[target].Add(prim);
                foreach (var bone in bones)
                {
                    binBones[target].Add(bone);
                }
            }
        }

        return groups;
    }

    // Picks the part submesh for a primitive: the first whose palette stays within the bone limit after
    // adding it (first-fit). If none fit, the one whose palette grows the least (minimise overflow).
    private static int ChooseTarget(
        List<int> targets,
        List<int> allTargets,
        List<HashSet<ulong>> binBones,
        IReadOnlyCollection<ulong> bones,
        int boneLimit)
    {
        foreach (var target in targets)
        {
            if (CountUnion(binBones[target], bones) <= boneLimit)
            {
                return target;
            }
        }

        // If a single texture/part target cannot hold another split piece, prefer another draw call over
        // failing the reimport. The texture writer follows this same mapping, so the borrowed submesh gets
        // the imported primitive's texture data under that template slot name.
        foreach (var target in allTargets)
        {
            if (CountUnion(binBones[target], bones) <= boneLimit)
            {
                return target;
            }
        }

        var fallback = targets[0];
        var fallbackUnion = int.MaxValue;
        foreach (var target in allTargets)
        {
            var union = CountUnion(binBones[target], bones);
            if (union < fallbackUnion)
            {
                fallbackUnion = union;
                fallback = target;
            }
        }

        return fallback;
    }

    private static int CountUnion(HashSet<ulong> existing, IReadOnlyCollection<ulong> bones)
    {
        var count = existing.Count;
        foreach (var bone in bones)
        {
            if (!existing.Contains(bone))
            {
                count++;
            }
        }

        return count;
    }

    private static List<int> FindTargets(Dictionary<string, List<int>> templateByPart, string part)
    {
        if (templateByPart.TryGetValue(part, out var exact))
        {
            return exact;
        }

        // Fuzzy: accept a template part that contains or is contained by the primitive's part.
        foreach (var (key, indices) in templateByPart)
        {
            if (key.Length > 0 && part.Length > 0 &&
                (key.Contains(part, StringComparison.OrdinalIgnoreCase) ||
                 part.Contains(key, StringComparison.OrdinalIgnoreCase)))
            {
                return indices;
            }
        }

        return [];
    }

    // Reduces a texture name to a coarse body-part key. Falls back to the whole stripped name so two
    // identical names (a same-character round-trip) still map to each other.
    public static string ExtractPartKey(string? textureName)
    {
        if (string.IsNullOrWhiteSpace(textureName))
        {
            return "";
        }

        var name = textureName.ToLowerInvariant();
        var dot = name.IndexOf(".d3dtx", StringComparison.Ordinal);
        if (dot >= 0)
        {
            name = name[..dot];
        }

        foreach (var token in PartTokens)
        {
            if (name.Contains(token, StringComparison.Ordinal))
            {
                return token;
            }
        }

        return name;
    }

    public static string? GetDiffuseName(GltfPrimitive prim)
    {
        if (prim.TextureSlots.TryGetValue("diffuse", out var slot) && !string.IsNullOrWhiteSpace(slot.Name))
        {
            return slot.Name;
        }

        if (prim.ReferencedTextures.TryGetValue("diffuse", out var referenced) && !string.IsNullOrWhiteSpace(referenced.Name))
        {
            return referenced.Name;
        }

        return prim.MaterialName;
    }

    public static bool TryGetSlotImage(GltfPrimitive prim, string slot, out GltfImage image)
    {
        if (prim.TextureSlots.TryGetValue(slot, out var fromSlots))
        {
            image = fromSlots;
            return true;
        }

        if (prim.ReferencedTextures.TryGetValue(slot, out var fromReferenced))
        {
            image = fromReferenced;
            return true;
        }

        image = null!;
        return false;
    }
}
