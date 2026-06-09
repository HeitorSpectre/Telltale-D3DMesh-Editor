using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;

namespace TelltaleD3DMeshEditor.Reinsert;

// When the diffuse Texture Atlas is active, the atlas packer bakes a primitive's ink-line (detail)
// texture into the atlas and removes the separate detail slot — so the normal reimport-time line-alpha
// inversion (which runs on the detail_diffuse write path) never gets a chance to run, and the ported
// face/hands render with the wrong alpha. This pre-pass inverts the alpha of those ink-line images in the
// model BEFORE atlasing, so the baked atlas regions already carry the correct convention.
//
//   * Face/head ink lines (e.g. "sk54_bigby_inklineshead") are inverted on the face/hair, but NOT on the
//     neck/chest skin connector — a body-skin primitive that borrows the head ink line. On that patch the
//     inverted alpha turns into an opaque black mask under the chin, so it keeps the normal convention.
//   * Hand ink lines (e.g. "sk54_bigby_inklineshands") are shared by BOTH hands but only the RIGHT hand
//     needs the inverted convention; the left hand keeps the normal one. The two hands are separate
//     primitives (skinned to _L vs _R bones), so we invert the image only for right-side primitives and
//     leave left-side ones untouched. Because the atlas keys detail regions by image content, the normal
//     and inverted copies are packed as two separate regions and each primitive samples the right one.
//   * The body line ("inklinesbody") is left untouched — the body shader expects the normal convention.
public static class CharacterLineAtlasFix
{
    public static GltfModel InvertFaceAndHandLineAlpha(GltfModel model)
    {
        var cache = new Dictionary<GltfImage, GltfImage>();
        var changed = false;

        GltfImage Invert(GltfImage image)
        {
            if (!cache.TryGetValue(image, out var inverted))
            {
                inverted = InvertAlpha(image);
                cache[image] = inverted;
            }

            changed = true;
            return inverted;
        }

        var primitives = new List<GltfPrimitive>(model.Primitives.Count);
        foreach (var primitive in model.Primitives)
        {
            bool? isRightSide = null;
            bool? isBodySkin = null;

            GltfImage Map(GltfImage image)
            {
                if (IsHeadLineTexture(image.Name))
                {
                    // The neck/chest skin connector borrows the head ink line but is a body-skin primitive;
                    // inverting it would paint a black mask under the chin, so it keeps the normal copy.
                    isBodySkin ??= IsBodySkinPrimitive(primitive);
                    return isBodySkin.Value ? image : Invert(image);
                }

                if (IsHandLineTexture(image.Name))
                {
                    // Only the right hand uses the inverted convention; the left hand stays normal.
                    isRightSide ??= IsRightSidePrimitive(primitive, model);
                    return isRightSide.Value ? Invert(image) : image;
                }

                return image;
            }

            primitives.Add(CloneWithImages(
                primitive,
                RemapImages(primitive.TextureSlots, Map),
                RemapImages(primitive.ReferencedTextures, Map),
                primitive.BaseColor is null ? null : Map(primitive.BaseColor)));
        }

        return changed
            ? new GltfModel { Primitives = primitives, Joints = model.Joints, Skeleton = model.Skeleton }
            : model;
    }

    // The neck/chest connector: skin (body) diffuse but a head ink-line detail. Mirrors
    // ReinsertTextureService.IsSourceBodyPrimitive so the atlas and non-atlas paths agree.
    private static bool IsBodySkinPrimitive(GltfPrimitive primitive)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
        {
            return false;
        }

        var lower = Stem(diffuse.Name);
        return lower.Contains("body", StringComparison.Ordinal) &&
               !lower.Contains("hand", StringComparison.Ordinal) &&
               !lower.Contains("hair", StringComparison.Ordinal) &&
               !lower.Contains("head", StringComparison.Ordinal);
    }

    private static bool IsHeadLineTexture(string textureName)
        => IsLine(textureName) && Stem(textureName).Contains("head", StringComparison.Ordinal);

    private static bool IsHandLineTexture(string textureName)
        => IsLine(textureName) && Stem(textureName).Contains("hand", StringComparison.Ordinal);

    private static bool IsLine(string textureName)
    {
        var stem = Stem(textureName);
        return stem.Contains("line", StringComparison.Ordinal) || stem.Contains("ink", StringComparison.Ordinal);
    }

    private static string Stem(string textureName)
        => Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();

    // Decides whether a primitive belongs to the character's right side by tallying the skin weights of
    // its _L vs _R joints. Used to invert the hand ink line for the right hand only. Falls back to "right"
    // (invert) when there is no skin data, so the requested right-hand inversion is never silently lost.
    private static bool IsRightSidePrimitive(GltfPrimitive primitive, GltfModel model)
    {
        if (primitive.Joints0 is not { Length: > 0 } joints || model.Joints.Count == 0)
        {
            return true;
        }

        var weights = primitive.Weights0;
        double left = 0;
        double right = 0;
        var vertexCount = primitive.VertexCount;
        for (var v = 0; v < vertexCount; v++)
        {
            for (var j = 0; j < 4; j++)
            {
                var flat = v * 4 + j;
                if (flat >= joints.Length)
                {
                    break;
                }

                var weight = weights is not null && v < weights.Length ? Component(weights[v], j) : 1.0;
                if (weight <= 0)
                {
                    continue;
                }

                var jointIndex = joints[flat];
                if (jointIndex >= model.Joints.Count)
                {
                    continue;
                }

                var name = model.Joints[jointIndex].Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var lower = name.ToLowerInvariant();
                if (lower.EndsWith("_r", StringComparison.Ordinal) || lower.Contains("right", StringComparison.Ordinal))
                {
                    right += weight;
                }
                else if (lower.EndsWith("_l", StringComparison.Ordinal) || lower.Contains("left", StringComparison.Ordinal))
                {
                    left += weight;
                }
            }
        }

        // Ties (and the no-side case) resolve to right/invert, so the right hand always gets inverted.
        return right >= left;
    }

    private static double Component(Vector4 weights, int index) => index switch
    {
        0 => weights.X,
        1 => weights.Y,
        2 => weights.Z,
        _ => weights.W,
    };

    private static GltfImage InvertAlpha(GltfImage image)
    {
        using var input = new MemoryStream(image.Data);
        using var source = new Bitmap(input);
        using var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);
                output.SetPixel(x, y, Color.FromArgb(255 - color.A, color.R, color.G, color.B));
            }
        }

        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        return new GltfImage
        {
            Name = image.Name,
            Data = stream.ToArray(),
            MimeType = "image/png",
        };
    }

    private static IReadOnlyDictionary<string, GltfImage> RemapImages(
        IReadOnlyDictionary<string, GltfImage> slots,
        Func<GltfImage, GltfImage> map)
    {
        var result = new Dictionary<string, GltfImage>(slots.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, image) in slots)
        {
            result[slot] = map(image);
        }

        return result;
    }

    private static GltfPrimitive CloneWithImages(
        GltfPrimitive primitive,
        IReadOnlyDictionary<string, GltfImage> textureSlots,
        IReadOnlyDictionary<string, GltfImage> referencedTextures,
        GltfImage? baseColor)
    {
        return new GltfPrimitive
        {
            Positions = primitive.Positions,
            Normals = primitive.Normals,
            Uv0 = primitive.Uv0,
            Uv1 = primitive.Uv1,
            Uv2 = primitive.Uv2,
            Uv3 = primitive.Uv3,
            Color0 = primitive.Color0,
            Tangents = primitive.Tangents,
            Binormals = primitive.Binormals,
            Unknown1 = primitive.Unknown1,
            Joints0 = primitive.Joints0,
            Weights0 = primitive.Weights0,
            Indices = primitive.Indices,
            MaterialName = primitive.MaterialName,
            BonePaletteIndex = primitive.BonePaletteIndex,
            SourceMeshPath = primitive.SourceMeshPath,
            SourceSubmeshIndex = primitive.SourceSubmeshIndex,
            IsSkinned = primitive.IsSkinned,
            BaseColor = baseColor,
            TextureSlots = textureSlots,
            ReferencedTextures = referencedTextures,
        };
    }
}
