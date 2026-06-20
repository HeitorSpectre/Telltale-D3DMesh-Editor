using System.Drawing;
using System.Drawing.Imaging;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Texture;

namespace TelltaleD3DMeshEditor.Reinsert;

// Blender (2.92 in particular) strips the material/primitive extras that carry the secondary texture
// slots, and prunes the line/normal images that were not bound by an actual material node. After a
// round-trip the GLB keeps only the diffuse (and the line-overlay materials, whose NAME encodes the
// original line texture). The detail/line and the normal map are therefore gone from the GLB.
//
// The non-atlas reinsert path can keep the template's original bindings by name, but the Texture Atlas
// packer works purely off the image bytes in TextureSlots — so a stripped GLB packs no lines and no
// normal-map atlas. This pass reloads those textures from the template folder and injects them back as
// real slots:
//   - detail_diffuse: recovered from the line-overlay material name (GltfReader did the name recovery).
//   - bump (normal map): the GLB carries no normal identity at all, so we map each primitive to the
//     template submesh by its (diffuse + recovered line) pair and take that submesh's normal map.
public static class StrippedLineTextureRecovery
{
    private static readonly string[] DetailSlotNames =
    [
        "detail_diffuse", "tex7", "tex8", "detail", "details",
        "line", "lines", "inkline", "inklines", "ink_lines",
    ];

    private static readonly string[] BumpSlotNames =
    [
        "bump", "normal", "normalmap", "normal_map", "nm",
    ];

    // Injects the recovered line and normal-map images as real slots. Used for the Texture Atlas path,
    // which packs straight from the image bytes in TextureSlots — so the stripped textures must be loaded
    // back before packing. The non-atlas path instead rebinds these by name (see ReinsertTextureService),
    // which reuses the game's own texture files and keeps their original names.
    public static GltfModel RestoreStrippedTextures(GltfModel model, string templateMeshPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        if (folder is null || !Directory.Exists(folder))
        {
            return model;
        }

        var bumpResolver = BuildBumpResolver(templateMeshPath);
        var cache = new Dictionary<string, GltfImage?>(StringComparer.OrdinalIgnoreCase);
        var newPrimitives = new List<GltfPrimitive>(model.Primitives.Count);
        var changed = false;
        foreach (var primitive in model.Primitives)
        {
            Dictionary<string, GltfImage>? textureSlots = null;
            Dictionary<string, GltfImage>? referencedTextures = null;

            if (!string.IsNullOrWhiteSpace(primitive.RecoveredDetailLineTextureName) &&
                !HasDetailSlot(primitive) &&
                LoadTemplateImage(cache, folder, primitive.RecoveredDetailLineTextureName!) is { } lineImage)
            {
                EnsureSlotDictionaries(primitive, ref textureSlots, ref referencedTextures);
                textureSlots!["detail_diffuse"] = lineImage;
                referencedTextures!["detail_diffuse"] = lineImage;
            }

            if (!HasBumpSlot(primitive) &&
                bumpResolver.Resolve(primitive) is { } bumpName &&
                LoadTemplateImage(cache, folder, bumpName) is { } bumpImage)
            {
                EnsureSlotDictionaries(primitive, ref textureSlots, ref referencedTextures);
                textureSlots!["bump"] = bumpImage;
                referencedTextures!["bump"] = bumpImage;
            }

            if (textureSlots is null)
            {
                newPrimitives.Add(primitive);
                continue;
            }

            newPrimitives.Add(Clone(primitive, textureSlots, referencedTextures!));
            changed = true;
        }

        return changed
            ? new GltfModel { Primitives = newPrimitives, Joints = model.Joints, Skeleton = model.Skeleton }
            : model;
    }

    private static void EnsureSlotDictionaries(
        GltfPrimitive primitive,
        ref Dictionary<string, GltfImage>? textureSlots,
        ref Dictionary<string, GltfImage>? referencedTextures)
    {
        textureSlots ??= new Dictionary<string, GltfImage>(primitive.TextureSlots, StringComparer.OrdinalIgnoreCase);
        referencedTextures ??= new Dictionary<string, GltfImage>(primitive.ReferencedTextures, StringComparer.OrdinalIgnoreCase);
    }

    public static bool HasDetailSlot(GltfPrimitive primitive)
        => DetailSlotNames.Any(primitive.TextureSlots.ContainsKey);

    public static bool HasBumpSlot(GltfPrimitive primitive)
        => BumpSlotNames.Any(primitive.TextureSlots.ContainsKey) ||
           primitive.TextureSlots.Values.Any(image => IsNormalMapName(image.Name));

    // Maps a template submesh's (diffuse, detail/line) pair to its normal map. The GLB carries no normal
    // identity after Blender, but the surviving diffuse name plus the recovered line name pin down which
    // body part a primitive is — including the head, which shares the body diffuse but has its own normal.
    public static TemplateBumpResolver BuildBumpResolver(string templateMeshPath)
    {
        var byPair = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byDiffuse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var mesh = D3DMeshParser.ParseFile(templateMeshPath);
            foreach (var submesh in mesh.Submeshes)
            {
                if (!submesh.TextureNames.TryGetValue("bump", out var bump) ||
                    string.IsNullOrWhiteSpace(bump) ||
                    !submesh.TextureNames.TryGetValue("diffuse", out var diffuse) ||
                    string.IsNullOrWhiteSpace(diffuse))
                {
                    continue;
                }

                var diffuseKey = NameKey(diffuse);
                byDiffuse.TryAdd(diffuseKey, bump);
                if (submesh.TextureNames.TryGetValue("detail_diffuse", out var detail) &&
                    !string.IsNullOrWhiteSpace(detail))
                {
                    byPair.TryAdd(diffuseKey + "|" + NameKey(detail), bump);
                }
            }
        }
        catch
        {
            // A template we cannot parse just yields no recovery; bindings stay as the GLB provides them.
        }

        return new TemplateBumpResolver(byPair, byDiffuse);
    }

    private static bool IsNormalMapName(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_nm", StringComparison.Ordinal) ||
               lower.EndsWith("_normal", StringComparison.Ordinal) ||
               lower.Contains("_normal_", StringComparison.Ordinal) ||
               lower.Contains("normalmap", StringComparison.Ordinal);
    }

    internal static string NameKey(string name)
        => Path.GetFileNameWithoutExtension(name).ToLowerInvariant();

    private static GltfImage? LoadTemplateImage(Dictionary<string, GltfImage?> cache, string folder, string textureName)
    {
        if (cache.TryGetValue(textureName, out var cached))
        {
            return cached;
        }

        GltfImage? image = null;
        var path = FindTemplateTexturePath(folder, textureName);
        if (path is not null)
        {
            try
            {
                var loaded = TextureLoader.Load(path);
                image = new GltfImage
                {
                    Name = textureName,
                    Data = EncodePng(loaded),
                    MimeType = "image/png",
                };
            }
            catch
            {
                image = null;
            }
        }

        cache[textureName] = image;
        return image;
    }

    private static string? FindTemplateTexturePath(string folder, string textureName)
    {
        var stem = Path.GetFileNameWithoutExtension(textureName);
        var exact = Path.Combine(folder, stem + ".d3dtx");
        if (File.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                stem,
                StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] EncodePng(TextureImage texture)
    {
        using var bitmap = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, texture.Width, texture.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(texture.Pixels, 0, data.Scan0, texture.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static GltfPrimitive Clone(
        GltfPrimitive source,
        IReadOnlyDictionary<string, GltfImage> textureSlots,
        IReadOnlyDictionary<string, GltfImage> referencedTextures)
        => new()
        {
            Positions = source.Positions,
            Normals = source.Normals,
            Uv0 = source.Uv0,
            Uv1 = source.Uv1,
            Uv2 = source.Uv2,
            Uv3 = source.Uv3,
            Color0 = source.Color0,
            Tangents = source.Tangents,
            Binormals = source.Binormals,
            Unknown1 = source.Unknown1,
            Joints0 = source.Joints0,
            Weights0 = source.Weights0,
            Indices = source.Indices,
            MaterialName = source.MaterialName,
            BonePaletteIndex = source.BonePaletteIndex,
            SourceMeshPath = source.SourceMeshPath,
            SourceSubmeshIndex = source.SourceSubmeshIndex,
            RecoveredDetailLineTextureName = source.RecoveredDetailLineTextureName,
            RecoveredDetailLineImage = source.RecoveredDetailLineImage,
            IsSkinned = source.IsSkinned,
            BaseColor = source.BaseColor,
            TextureSlots = textureSlots,
            ReferencedTextures = referencedTextures,
        };

    // Resolves the template normal-map name for a primitive from its (diffuse, recovered line) pair,
    // falling back to the diffuse name alone. Built once per template by BuildBumpResolver.
    public sealed class TemplateBumpResolver
    {
        private readonly IReadOnlyDictionary<string, string> _byDiffuseAndDetail;
        private readonly IReadOnlyDictionary<string, string> _byDiffuse;

        internal TemplateBumpResolver(
            IReadOnlyDictionary<string, string> byDiffuseAndDetail,
            IReadOnlyDictionary<string, string> byDiffuse)
        {
            _byDiffuseAndDetail = byDiffuseAndDetail;
            _byDiffuse = byDiffuse;
        }

        public string? Resolve(GltfPrimitive primitive)
        {
            if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
            {
                return null;
            }

            var diffuseKey = NameKey(diffuse.Name);
            var line = primitive.RecoveredDetailLineTextureName;
            if (!string.IsNullOrWhiteSpace(line) &&
                _byDiffuseAndDetail.TryGetValue(diffuseKey + "|" + NameKey(line), out var paired))
            {
                return paired;
            }

            return _byDiffuse.TryGetValue(diffuseKey, out var byDiffuse) ? byDiffuse : null;
        }
    }
}
