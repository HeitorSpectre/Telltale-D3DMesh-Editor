using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Security.Cryptography;

namespace TelltaleD3DMeshEditor.Reinsert;

public sealed record GltfDiffuseAtlasOptions(
    int Padding = 4,
    int MaxSize = 8192,
    string AtlasName = "diffuse_atlas");

public sealed record GltfDiffuseAtlasResult(
    GltfModel Model,
    bool Applied,
    int SourceTextureCount,
    int AtlasWidth,
    int AtlasHeight,
    string AtlasName,
    IReadOnlyList<string> Warnings);

public static class GltfDiffuseAtlasPacker
{
    public static GltfDiffuseAtlasResult Pack(GltfModel model, GltfDiffuseAtlasOptions? options = null)
    {
        options ??= new GltfDiffuseAtlasOptions();
        if (model.Primitives.Count == 0)
        {
            return NotApplied(model, 0, options.AtlasName);
        }

        // Pack detail/lines at their native resolution first (no downscale). Only if that overflows the
        // max atlas size do we fall back to matching detail to the diffuse size — so big line maps keep
        // their resolution in the common case, while huge models still pack instead of failing.
        var sourceImages = CollectAtlasImages(model, matchDetailToDiffuse: false);
        if (sourceImages.Count <= 1)
        {
            return NotApplied(model, sourceImages.Count, options.AtlasName);
        }

        var matchedDetailToDiffuse = false;
        var decoded = DecodeImages(sourceImages);
        Dictionary<string, AtlasPlacement> placements;
        try
        {
            placements = PackRectangles(decoded, Math.Max(0, options.Padding), options.MaxSize);
        }
        catch (InvalidOperationException)
        {
            foreach (var image in decoded)
            {
                image.Dispose();
            }

            matchedDetailToDiffuse = true;
            decoded = DecodeImages(CollectAtlasImages(model, matchDetailToDiffuse: true));
            placements = PackRectangles(decoded, Math.Max(0, options.Padding), options.MaxSize);
        }

        try
        {
            using var atlas = BuildAtlas(decoded, placements);
            var atlasImage = new GltfImage
            {
                Name = options.AtlasName,
                Data = EncodePng(atlas),
                MimeType = "image/png",
            };

            // Normal maps share UV0 with the diffuse, so they can't live in a separate region of the same
            // atlas. Instead build a PARALLEL normal-map atlas with identical placements: each material's
            // normal map sits at the exact spot its diffuse occupies in the diffuse atlas, so the same
            // remapped UV0 samples the matching normal map. Unused/detail areas stay flat normal.
            var bumpByDiffuseKey = CollectBumpByDiffuseKey(model);
            GltfImage? normalAtlasImage = null;
            if (bumpByDiffuseKey.Count > 0)
            {
                using var normalAtlas = BuildNormalAtlas(atlas.Width, atlas.Height, placements, bumpByDiffuseKey, Math.Max(0, options.Padding));
                normalAtlasImage = new GltfImage
                {
                    Name = options.AtlasName + "_nm",
                    Data = EncodePng(normalAtlas),
                    MimeType = "image/png",
                };
            }

            var warnings = new List<string>();
            if (matchedDetailToDiffuse)
            {
                warnings.Add("Detail/lines textures were downscaled to the diffuse size so the atlas would fit within its size limit.");
            }

            var newPrimitives = new List<GltfPrimitive>(model.Primitives.Count);
            for (var primitiveIndex = 0; primitiveIndex < model.Primitives.Count; primitiveIndex++)
            {
                var primitive = model.Primitives[primitiveIndex];
                if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuseImage) ||
                    primitive.Uv0 is null ||
                    !placements.TryGetValue(DiffuseAtlasKey(primitive), out var placement))
                {
                    newPrimitives.Add(ClonePrimitive(
                        primitive,
                        primitive.Uv0,
                        primitive.Uv1,
                        primitive.Uv2,
                        primitive.TextureSlots,
                        primitive.ReferencedTextures,
                        primitive.BaseColor));
                    continue;
                }

                var wrappedOutOfRangeUv = false;
                var uv0 = RemapUvsToPlacement(primitive.Uv0, placement, atlas.Width, atlas.Height, out var diffuseWrapped);
                wrappedOutOfRangeUv |= diffuseWrapped;

                if (wrappedOutOfRangeUv)
                {
                    warnings.Add($"Primitive '{primitive.MaterialName ?? primitive.SourceSubmeshIndex?.ToString() ?? "unknown"}' has UV0 outside 0..1; atlas packed it with repeat wrapping.");
                }

                Vector2[]? uv1 = primitive.Uv1;
                Vector2[]? uv2 = primitive.Uv2;
                var textureSlots = new Dictionary<string, GltfImage>(primitive.TextureSlots, StringComparer.OrdinalIgnoreCase)
                {
                    ["diffuse"] = atlasImage,
                };
                var referencedTextures = new Dictionary<string, GltfImage>(primitive.ReferencedTextures, StringComparer.OrdinalIgnoreCase);
                if (referencedTextures.ContainsKey("diffuse"))
                {
                    referencedTextures["diffuse"] = atlasImage;
                }

                RemoveDetailSlots(textureSlots);
                RemoveDetailSlots(referencedTextures);
                if (FindDetailTexture(primitive) is { } detail &&
                    SelectDetailUv(primitive) is { } detailSourceUv &&
                    detailSourceUv.Length == primitive.VertexCount &&
                    placements.TryGetValue(DetailAtlasKey(detail.Image, diffuseImage), out var detailPlacement))
                {
                    var detailAtlasUv = RemapUvsToPlacement(detailSourceUv, detailPlacement, atlas.Width, atlas.Height, out var detailWrapped);
                    uv1 = detailAtlasUv;
                    uv2 = detailAtlasUv;
                    if (detailWrapped)
                    {
                        warnings.Add($"Primitive '{primitive.MaterialName ?? primitive.SourceSubmeshIndex?.ToString() ?? "unknown"}' has detail UV outside 0..1; atlas packed it with repeat wrapping.");
                    }

                    textureSlots["detail_diffuse"] = atlasImage;
                    referencedTextures["detail_diffuse"] = atlasImage;
                }

                // Point the normal-map slot at the parallel normal atlas (sampled with the same UV0).
                if (normalAtlasImage is not null && TryGetBumpSlot(primitive, out var bumpSlot))
                {
                    textureSlots[bumpSlot] = normalAtlasImage;
                    referencedTextures[bumpSlot] = normalAtlasImage;
                }

                newPrimitives.Add(ClonePrimitive(primitive, uv0, uv1, uv2, textureSlots, referencedTextures, atlasImage));
            }

            return new GltfDiffuseAtlasResult(
                new GltfModel
                {
                    Primitives = newPrimitives,
                    Joints = model.Joints,
                    Skeleton = model.Skeleton,
                },
                Applied: true,
                SourceTextureCount: sourceImages.Count,
                AtlasWidth: atlas.Width,
                AtlasHeight: atlas.Height,
                AtlasName: options.AtlasName,
                Warnings: warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        finally
        {
            foreach (var image in decoded)
            {
                image.Dispose();
            }
        }
    }

    private static GltfDiffuseAtlasResult NotApplied(GltfModel model, int sourceTextureCount, string atlasName)
        => new(model, Applied: false, sourceTextureCount, 0, 0, atlasName, []);

    private static List<AtlasSourceImage> CollectAtlasImages(GltfModel model, bool matchDetailToDiffuse)
    {
        var result = new List<AtlasSourceImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitive in model.Primitives)
        {
            if (primitive.TextureSlots.TryGetValue("diffuse", out var diffuse) &&
                primitive.Uv0 is not null)
            {
                var diffuseKey = DiffuseAtlasKey(diffuse);
                if (seen.Add(diffuseKey))
                {
                    result.Add(new AtlasSourceImage(diffuseKey, diffuse, PreserveAlpha: ShouldPreserveDiffuseAlpha(diffuse.Name)));
                }

                if (FindDetailTexture(primitive) is { } detail)
                {
                    var detailKey = DetailAtlasKey(detail.Image, diffuse);
                    if (seen.Add(detailKey))
                    {
                        // Keep the detail/lines texture at its native resolution. It gets its own atlas
                        // region and its own remapped UV (uv1/uv2), so it does NOT need to match the
                        // diffuse size — forcing that downscaled high-res line maps (e.g. 1024 lines onto
                        // a 512 diffuse). Only the overflow fallback re-matches it to the diffuse size.
                        result.Add(new AtlasSourceImage(
                            detailKey,
                            detail.Image,
                            PreserveAlpha: true,
                            MatchSizeOf: matchDetailToDiffuse ? diffuse : null));
                    }
                }
            }
        }

        return result;
    }

    private static readonly string[] BumpSlotNames = ["bump", "normal", "normalmap", "normal_map", "nm"];

    // Maps each unique diffuse (by its atlas key) to a normal-map image, so the normal atlas can place
    // that normal map at the same spot the diffuse occupies in the diffuse atlas.
    private static Dictionary<string, GltfImage> CollectBumpByDiffuseKey(GltfModel model)
    {
        var map = new Dictionary<string, GltfImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitive in model.Primitives)
        {
            if (primitive.TextureSlots.TryGetValue("diffuse", out var diffuse) &&
                primitive.Uv0 is not null &&
                TryGetBumpSlot(primitive, out var bumpSlot))
            {
                map.TryAdd(DiffuseAtlasKey(diffuse), primitive.TextureSlots[bumpSlot]);
            }
        }

        return map;
    }

    private static bool TryGetBumpSlot(GltfPrimitive primitive, out string slot)
    {
        foreach (var candidate in BumpSlotNames)
        {
            if (primitive.TextureSlots.ContainsKey(candidate))
            {
                slot = candidate;
                return true;
            }
        }

        foreach (var (key, image) in primitive.TextureSlots)
        {
            if (IsNormalMapName(image.Name))
            {
                slot = key;
                return true;
            }
        }

        slot = "";
        return false;
    }

    private static bool IsNormalMapName(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_nm", StringComparison.Ordinal) ||
               lower.EndsWith("_normal", StringComparison.Ordinal) ||
               lower.Contains("_normal_", StringComparison.Ordinal) ||
               lower.Contains("normalmap", StringComparison.Ordinal);
    }

    // Builds a normal-map atlas the same size as the diffuse atlas, with each material's normal map drawn
    // at its diffuse placement. Everything else is filled with a flat tangent-space normal (128,128,255).
    private static Bitmap BuildNormalAtlas(
        int width,
        int height,
        IReadOnlyDictionary<string, AtlasPlacement> placements,
        IReadOnlyDictionary<string, GltfImage> bumpByDiffuseKey,
        int padding)
    {
        var atlas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(atlas))
        {
            graphics.Clear(Color.FromArgb(255, 128, 128, 255));
        }

        foreach (var (diffuseKey, bump) in bumpByDiffuseKey)
        {
            if (!placements.TryGetValue(diffuseKey, out var placement))
            {
                continue;
            }

            using var stream = new MemoryStream(bump.Data);
            using var source = new Bitmap(stream);
            var normalBitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(normalBitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            if (normalBitmap.Width != placement.SourceWidth || normalBitmap.Height != placement.SourceHeight)
            {
                var resized = ResizeBitmap(normalBitmap, placement.SourceWidth, placement.SourceHeight);
                normalBitmap.Dispose();
                normalBitmap = resized;
            }

            using (var graphics = Graphics.FromImage(atlas))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImageUnscaled(normalBitmap, placement.X, placement.Y);
            }

            ReplicatePadding(atlas, normalBitmap, placement);
            normalBitmap.Dispose();
        }

        return atlas;
    }

    private static List<DecodedImage> DecodeImages(IReadOnlyList<AtlasSourceImage> images)
    {
        var result = new List<DecodedImage>(images.Count);
        foreach (var surface in images)
        {
            try
            {
                var image = surface.Image;
                using var stream = new MemoryStream(image.Data);
                using var source = new Bitmap(stream);
                var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImage(source, 0, 0, source.Width, source.Height);
                }

                if (!surface.PreserveAlpha)
                {
                    ForceOpaqueAlpha(bitmap);
                }

                if (surface.MatchSizeOf is not null)
                {
                    using var targetStream = new MemoryStream(surface.MatchSizeOf.Data);
                    using var target = new Bitmap(targetStream);
                    if (bitmap.Width != target.Width || bitmap.Height != target.Height)
                    {
                        var resized = ResizeBitmap(bitmap, target.Width, target.Height);
                        bitmap.Dispose();
                        bitmap = resized;
                    }
                }

                result.Add(new DecodedImage(surface.Key, image.Name, bitmap));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Could not decode texture '{surface.Image.Name}' for atlas packing: {ex.Message}", ex);
            }
        }

        return result;
    }

    private static DetailTexture? FindDetailTexture(GltfPrimitive primitive)
    {
        foreach (var slot in new[] { "detail_diffuse", "tex7", "tex8", "detail", "details", "line", "lines", "inkline", "inklines", "ink_lines" })
        {
            if (primitive.TextureSlots.TryGetValue(slot, out var image))
            {
                return new DetailTexture(slot, image);
            }
        }

        foreach (var (slot, image) in primitive.TextureSlots)
        {
            if (IsDetailOrLineTextureName(image.Name))
            {
                return new DetailTexture(slot, image);
            }
        }

        return null;
    }

    private static bool IsDetailOrLineTextureName(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_lines", StringComparison.Ordinal) ||
               lower.EndsWith("_line", StringComparison.Ordinal) ||
               lower.Contains("_lines_", StringComparison.Ordinal) ||
               lower.Contains("_line_", StringComparison.Ordinal) ||
               lower.Contains("inklines", StringComparison.Ordinal) ||
               lower.Contains("ink_lines", StringComparison.Ordinal) ||
               lower.EndsWith("_detail", StringComparison.Ordinal) ||
               lower.Contains("_detail_", StringComparison.Ordinal);
    }

    private static Vector2[]? SelectDetailUv(GltfPrimitive primitive)
    {
        return primitive.Uv2 is { Length: > 0 } uv2
            ? uv2
            : primitive.Uv1 is { Length: > 0 } uv1
                ? uv1
                : primitive.Uv0;
    }

    private static Vector2[] RemapUvsToPlacement(
        IReadOnlyList<Vector2> source,
        AtlasPlacement placement,
        int atlasWidth,
        int atlasHeight,
        out bool wrappedOutOfRangeUv)
    {
        var result = new Vector2[source.Count];
        wrappedOutOfRangeUv = false;
        var scale = new Vector2(
            (float)placement.SourceWidth / atlasWidth,
            (float)placement.SourceHeight / atlasHeight);
        var offset = new Vector2(
            (float)placement.X / atlasWidth,
            (float)placement.Y / atlasHeight);

        for (var i = 0; i < source.Count; i++)
        {
            var uv = source[i];
            if (uv.X < -0.0001f || uv.X > 1.0001f || uv.Y < -0.0001f || uv.Y > 1.0001f)
            {
                wrappedOutOfRangeUv = true;
            }

            result[i] = new Vector2(offset.X + Wrap01(uv.X) * scale.X, offset.Y + Wrap01(uv.Y) * scale.Y);
        }

        return result;
    }

    private static void RemoveDetailSlots(IDictionary<string, GltfImage> slots)
    {
        foreach (var slot in new[] { "detail_diffuse", "tex7", "tex8", "detail", "details", "line", "lines", "inkline", "inklines", "ink_lines" })
        {
            slots.Remove(slot);
        }

        foreach (var slot in slots
                     .Where(pair => IsDetailOrLineTextureName(pair.Value.Name))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            slots.Remove(slot);
        }
    }

    private static void BakeDetails(Bitmap diffuse, SurfaceImage surface)
    {
        if (surface.DetailLayers.Count == 0)
        {
            return;
        }

        var detailCache = new Dictionary<string, (Bitmap Bitmap, float AverageAlpha)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var layer in surface.DetailLayers)
            {
                var key = ImageKey(layer.Detail);
                if (!detailCache.TryGetValue(key, out var detailEntry))
                {
                    using var detailStream = new MemoryStream(layer.Detail.Data);
                    using var detailSource = new Bitmap(detailStream);
                    var detailBitmap = new Bitmap(detailSource.Width, detailSource.Height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(detailBitmap))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImage(detailSource, 0, 0, detailSource.Width, detailSource.Height);
                    }

                    detailEntry = (detailBitmap, AverageAlpha(detailBitmap));
                    detailCache[key] = detailEntry;
                }

                BakeDetailLayer(diffuse, detailEntry.Bitmap, detailEntry.AverageAlpha, layer);
            }
        }
        finally
        {
            foreach (var (bitmap, _) in detailCache.Values)
            {
                bitmap.Dispose();
            }
        }
    }

    private static void BakeDetailLayer(Bitmap diffuse, Bitmap detail, float detailAverageAlpha, SurfaceDetailLayer layer)
    {
        for (var i = 0; i + 2 < layer.Indices.Length; i += 3)
        {
            var ia = layer.Indices[i];
            var ib = layer.Indices[i + 1];
            var ic = layer.Indices[i + 2];
            if ((uint)ia >= layer.DiffuseUv.Length ||
                (uint)ib >= layer.DiffuseUv.Length ||
                (uint)ic >= layer.DiffuseUv.Length)
            {
                continue;
            }

            BakeDetailTriangle(
                diffuse,
                detail,
                detailAverageAlpha,
                layer.DiffuseUv[ia],
                layer.DiffuseUv[ib],
                layer.DiffuseUv[ic],
                layer.DetailUv[ia],
                layer.DetailUv[ib],
                layer.DetailUv[ic]);
        }
    }

    private static void BakeDetailTriangle(
        Bitmap diffuse,
        Bitmap detail,
        float detailAverageAlpha,
        Vector2 diffuseA,
        Vector2 diffuseB,
        Vector2 diffuseC,
        Vector2 detailA,
        Vector2 detailB,
        Vector2 detailC)
    {
        var ax = Wrap01(diffuseA.X) * diffuse.Width;
        var ay = (1f - Wrap01(diffuseA.Y)) * diffuse.Height;
        var bx = Wrap01(diffuseB.X) * diffuse.Width;
        var by = (1f - Wrap01(diffuseB.Y)) * diffuse.Height;
        var cx = Wrap01(diffuseC.X) * diffuse.Width;
        var cy = (1f - Wrap01(diffuseC.Y)) * diffuse.Height;
        var minX = Math.Clamp((int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), 0, diffuse.Width - 1);
        var maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))), 0, diffuse.Width - 1);
        var minY = Math.Clamp((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), 0, diffuse.Height - 1);
        var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))), 0, diffuse.Height - 1);
        var area = Edge(ax, ay, bx, by, cx, cy);
        if (MathF.Abs(area) < 0.00001f)
        {
            return;
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;
                var w0 = Edge(bx, by, cx, cy, px, py) / area;
                var w1 = Edge(cx, cy, ax, ay, px, py) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < -0.0001f || w1 < -0.0001f || w2 < -0.0001f)
                {
                    continue;
                }

                var detailUv = detailA * w0 + detailB * w1 + detailC * w2;
                var baseColor = diffuse.GetPixel(x, y).ToArgb();
                var detailColor = SampleBitmap(detail, detailUv.X, detailUv.Y);
                diffuse.SetPixel(x, y, Color.FromArgb(ApplyDetail(baseColor, detailColor, detailAverageAlpha)));
            }
        }
    }

    private static int SampleBitmap(Bitmap bitmap, float u, float v)
    {
        u = Wrap01(u);
        v = Wrap01(v);
        var x = Math.Clamp((int)(u * bitmap.Width), 0, bitmap.Width - 1);
        var y = Math.Clamp((int)((1f - v) * bitmap.Height), 0, bitmap.Height - 1);
        return bitmap.GetPixel(x, y).ToArgb();
    }

    private static int ApplyDetail(int baseArgb, int detailArgb, float detailAverageAlpha)
    {
        const float lineStrength = 0.8f;
        const float minKeep = 0.4f;
        var alpha = ((detailArgb >> 24) & 0xFF) / 255f;
        var dr = (detailArgb >> 16) & 0xFF;
        var dg = (detailArgb >> 8) & 0xFF;
        var db = detailArgb & 0xFF;
        var a = (baseArgb >> 24) & 0xFF;
        var br = (baseArgb >> 16) & 0xFF;
        var bg = (baseArgb >> 8) & 0xFF;
        var bb = baseArgb & 0xFF;
        var cov = (detailAverageAlpha < 0.5f ? alpha : 1f - alpha) * lineStrength;
        if (cov <= 0.01f)
        {
            return baseArgb;
        }

        var or = br * (1f - cov) + dr * cov;
        var og = bg * (1f - cov) + dg * cov;
        var ob = bb * (1f - cov) + db * cov;
        return Color.FromArgb(
            a,
            (int)MathF.Max(or, br * minKeep),
            (int)MathF.Max(og, bg * minKeep),
            (int)MathF.Max(ob, bb * minKeep)).ToArgb();
    }

    private static float AverageAlpha(Bitmap bitmap)
    {
        var sum = 0L;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                sum += bitmap.GetPixel(x, y).A;
            }
        }

        return sum / (bitmap.Width * bitmap.Height * 255f);
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy)
        => (cx - ax) * (by - ay) - (cy - ay) * (bx - ax);

    private static void ForceOpaqueAlpha(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A != 255)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(255, color.R, color.G, color.B));
                }
            }
        }
    }

    private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
    {
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static bool ShouldPreserveDiffuseAlpha(string imageName)
    {
        var lower = Path.GetFileNameWithoutExtension(imageName).ToLowerInvariant();
        return lower.Contains("alpha") ||
               lower.Contains("hair") ||
               lower.Contains("fur") ||
               lower.Contains("lash") ||
               lower.Contains("whisker");
    }

    private static float Wrap01(float value)
        => value - MathF.Floor(value);

    private static Dictionary<string, AtlasPlacement> PackRectangles(
        IReadOnlyList<DecodedImage> images,
        int padding,
        int maxSize)
    {
        if (maxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), "Atlas max size must be greater than zero.");
        }

        var ordered = images
            .OrderByDescending(image => image.Bitmap.Height)
            .ThenByDescending(image => image.Bitmap.Width)
            .ToList();
        var minWidth = ordered.Max(image => image.Bitmap.Width + padding * 2);
        var totalArea = ordered.Sum(image => (long)(image.Bitmap.Width + padding * 2) * (image.Bitmap.Height + padding * 2));
        var startWidth = NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(totalArea)));
        startWidth = Math.Max(NextPowerOfTwo(minWidth), Math.Min(startWidth, maxSize));

        for (var width = Math.Max(1, startWidth); width <= maxSize; width *= 2)
        {
            if (TryPackShelves(ordered, padding, width, maxSize, out var placements, out var usedHeight))
            {
                var atlasHeight = NextPowerOfTwo(Math.Max(1, usedHeight));
                if (atlasHeight <= maxSize)
                {
                    foreach (var key in placements.Keys.ToList())
                    {
                        placements[key] = placements[key] with { AtlasWidth = width, AtlasHeight = atlasHeight };
                    }

                    return placements;
                }
            }

            if (width == maxSize)
            {
                break;
            }
        }

        throw new InvalidOperationException($"Diffuse textures do not fit in a {maxSize}x{maxSize} atlas. Reduce texture sizes or increase the atlas limit.");
    }

    private static bool TryPackShelves(
        IReadOnlyList<DecodedImage> images,
        int padding,
        int atlasWidth,
        int maxSize,
        out Dictionary<string, AtlasPlacement> placements,
        out int usedHeight)
    {
        placements = new Dictionary<string, AtlasPlacement>(StringComparer.OrdinalIgnoreCase);
        var x = padding;
        var y = padding;
        var rowHeight = 0;

        foreach (var image in images)
        {
            var packedWidth = image.Bitmap.Width + padding * 2;
            var packedHeight = image.Bitmap.Height + padding * 2;
            if (packedWidth > atlasWidth || packedHeight > maxSize)
            {
                usedHeight = 0;
                return false;
            }

            if (x + image.Bitmap.Width + padding > atlasWidth)
            {
                x = padding;
                y += rowHeight + padding * 2;
                rowHeight = 0;
            }

            if (y + image.Bitmap.Height + padding > maxSize)
            {
                usedHeight = 0;
                return false;
            }

            placements[image.Key] = new AtlasPlacement(
                X: x,
                Y: y,
                SourceWidth: image.Bitmap.Width,
                SourceHeight: image.Bitmap.Height,
                AtlasWidth: atlasWidth,
                AtlasHeight: maxSize,
                Padding: padding);
            x += image.Bitmap.Width + padding * 2;
            rowHeight = Math.Max(rowHeight, image.Bitmap.Height);
        }

        usedHeight = y + rowHeight + padding;
        return true;
    }

    private static Bitmap BuildAtlas(IReadOnlyList<DecodedImage> images, IReadOnlyDictionary<string, AtlasPlacement> placements)
    {
        var firstPlacement = placements.Values.First();
        var atlas = new Bitmap(firstPlacement.AtlasWidth, firstPlacement.AtlasHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(atlas))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (var image in images)
            {
                var placement = placements[image.Key];
                graphics.DrawImageUnscaled(image.Bitmap, placement.X, placement.Y);
            }
        }

        foreach (var image in images)
        {
            var placement = placements[image.Key];
            ReplicatePadding(atlas, image.Bitmap, placement);
        }

        return atlas;
    }

    private static void ReplicatePadding(Bitmap atlas, Bitmap source, AtlasPlacement placement)
    {
        for (var x = 0; x < source.Width; x++)
        {
            var top = source.GetPixel(x, 0);
            var bottom = source.GetPixel(x, source.Height - 1);
            var atlasX = placement.X + x;
            for (var y = Math.Max(0, placement.Y - placement.Padding); y < placement.Y; y++)
            {
                atlas.SetPixel(atlasX, y, top);
            }
            for (var y = placement.Y + source.Height; y < Math.Min(atlas.Height, placement.Y + source.Height + placement.Padding); y++)
            {
                atlas.SetPixel(atlasX, y, bottom);
            }
        }

        for (var y = 0; y < source.Height; y++)
        {
            var left = source.GetPixel(0, y);
            var right = source.GetPixel(source.Width - 1, y);
            var atlasY = placement.Y + y;
            for (var x = Math.Max(0, placement.X - placement.Padding); x < placement.X; x++)
            {
                atlas.SetPixel(x, atlasY, left);
            }
            for (var x = placement.X + source.Width; x < Math.Min(atlas.Width, placement.X + source.Width + placement.Padding); x++)
            {
                atlas.SetPixel(x, atlasY, right);
            }
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static int NextPowerOfTwo(int value)
    {
        value = Math.Max(1, value);
        var result = 1;
        while (result < value && result < 16384)
        {
            result <<= 1;
        }

        return result;
    }

    private static GltfPrimitive ClonePrimitive(
        GltfPrimitive source,
        Vector2[]? uv0,
        Vector2[]? uv1,
        Vector2[]? uv2,
        IReadOnlyDictionary<string, GltfImage> textureSlots,
        IReadOnlyDictionary<string, GltfImage> referencedTextures,
        GltfImage? baseColor)
        => new()
        {
            Positions = source.Positions,
            Normals = source.Normals,
            Uv0 = uv0,
            Uv1 = uv1,
            Uv2 = uv2,
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
            IsSkinned = source.IsSkinned,
            BaseColor = baseColor,
            TextureSlots = textureSlots,
            ReferencedTextures = referencedTextures,
        };

    private static string ImageKey(GltfImage image)
        => image.Name + ":" + image.Data.Length + ":" + Convert.ToHexString(SHA256.HashData(image.Data));

    private static string DiffuseAtlasKey(GltfPrimitive primitive)
        => primitive.TextureSlots.TryGetValue("diffuse", out var diffuse)
            ? DiffuseAtlasKey(diffuse)
            : "diffuse:none";

    private static string DiffuseAtlasKey(GltfImage image)
        => "diffuse:" + ImageKey(image);

    private static string DetailAtlasKey(GltfImage image, GltfImage diffuse)
        => "detail:" + ImageKey(image) + ":match:" + ImageKey(diffuse);

    private sealed record AtlasSourceImage(string Key, GltfImage Image, bool PreserveAlpha, GltfImage? MatchSizeOf = null);

    private sealed record DetailTexture(string Slot, GltfImage Image);

    private sealed record SurfaceImage(
        string Key,
        GltfImage Diffuse,
        IReadOnlyList<SurfaceDetailLayer> DetailLayers);

    private sealed record SurfaceDetailLayer(
        GltfImage Detail,
        Vector2[] DiffuseUv,
        Vector2[] DetailUv,
        int[] Indices,
        string Name);

    private sealed class SurfaceGroup(string key, GltfImage diffuse)
    {
        public string Key { get; } = key;
        public GltfImage Diffuse { get; } = diffuse;
        public List<SurfaceDetailLayer> DetailLayers { get; } = [];
    }

    private sealed record DecodedImage(string Key, string Name, Bitmap Bitmap) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    private sealed record AtlasPlacement(
        int X,
        int Y,
        int SourceWidth,
        int SourceHeight,
        int AtlasWidth,
        int AtlasHeight,
        int Padding);
}
