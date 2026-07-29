using System.Drawing;

namespace TelltaleD3DMeshEditor.Formats.Texture;

// Composites the Telltale "detail/lines" layer over albedo. Used by the preview
// (MeshPreviewControl) to approximate the game's toon look; GLB/GLTF exports keep it separate.
//
// Line textures (`_lines` and `_lines_alp`) store ink color in RGB (almost always black) and line
// coverage in ALPHA. Two variants are detected by average alpha:
//  - `_lines_alp` (avgA < 0.5): transparent background; ink is where alpha is high. coverage = alpha.
//  - `_lines`     (avgA >= 0.5): opaque background; ink is where alpha is low. coverage = 1 - alpha.
// In both cases, coverage comes only from alpha. Never darken from RGB darkness: since RGB is black
// almost everywhere, that would darken the entire surface by mistake. The composition is an ink
// "over" pass on top of albedo, with a floor to preserve object shape.
public static class DetailCompositor
{
    // Global line-layer strength. Attenuates ink so it does not dominate.
    private const float LineStrength = 0.8f;
    // Floor: a line never darkens a channel below this diffuse fraction. Preserves object shape
    // when ink covers a broad area instead of turning it into a black blob.
    private const float MinKeep = 0.4f;

    public static int Apply(int baseArgb, TextureImage detail, float u, float v)
    {
        var detailArgb = detail.Sample(u, v);
        var alpha = ((detailArgb >> 24) & 0xFF) / 255f;
        var dr = (detailArgb >> 16) & 0xFF;
        var dg = (detailArgb >> 8) & 0xFF;
        var db = detailArgb & 0xFF;

        // Batman and the rest of the GotG family do not use the alpha-keyed ink-line layer this
        // compositor was built for, so the treatment is chosen by the SOURCE FORMAT:
        //  - BC5 is a two-channel DERIVATIVE NORMAL map. Its "yellow lines on a blue field" look is the
        //    reconstructed Z, not ink; multiplying it into albedo painted black creases and blotches.
        //    It belongs to shading, never to albedo.
        //  - BC4 is a single-channel grime/wear map: real colour detail that multiplies albedo, and the
        //    layer that makes clothing and environment surfaces stop looking flat.
        if (Core.GameConfig.Current.Id == Core.GameId.Batman)
        {
            var ba = (baseArgb >> 24) & 0xFF;
            var bbr = (baseArgb >> 16) & 0xFF;
            var bbg = (baseArgb >> 8) & 0xFF;
            var bbb = baseArgb & 0xFF;

            // BC5 whose two channels are identical is a duplicated single channel: a coverage mask for
            // stubble, pores, seams and grime. It darkens the albedo where the detail sits. The strength
            // stays moderate on purpose — these masks are sparse and high-frequency, so a harsh factor
            // stamps isolated pixels as black dots instead of reading as surface texture.
            if (detail.IsTwoChannelDerivativeMap && detail.HasDuplicatedChannels)
            {
                var coverage = ((detail.Sample(u, v) >> 16) & 0xFF) / 255f;
                if (coverage < 0.01f)
                {
                    return baseArgb;
                }

                var shade = Math.Clamp(1f - coverage * 0.45f, 0.55f, 1f);
                return Color.FromArgb(ba, (int)(bbr * shade), (int)(bbg * shade), (int)(bbb * shade)).ToArgb();
            }

            // A genuine two-channel map describes relief, which the shading path handles.
            if (detail.IsTwoChannelDerivativeMap)
            {
                return baseArgb;
            }

            if (detail.IsSingleChannelMap)
            {
                var lum = (0.299f * dr + 0.587f * dg + 0.114f * db) / 255f;

                // Batman also uses BC4 as a black-background COVERAGE map on scenery: zero is empty and
                // brighter pixels mark grime, plaster specks and wear. Treating it as ordinary AO made
                // the black background darken the whole object while the authored detail stayed bright.
                if (detail.AverageLuminance < 0.5f)
                {
                    if (lum < 0.01f)
                    {
                        return baseArgb;
                    }

                    var shade = Math.Clamp(1f - lum * 0.45f, 0.55f, 1f);
                    return Color.FromArgb(ba, (int)(bbr * shade), (int)(bbg * shade), (int)(bbb * shade)).ToArgb();
                }

                // Bright-background BC4 maps already follow the ambient-occlusion convention: white
                // leaves albedo untouched and darker values occlude.
                var factor = Math.Clamp(0.5f + lum * 0.5f, 0.5f, 1f);
                return Color.FromArgb(ba, Math.Clamp((int)(bbr * factor), 0, 255),
                    Math.Clamp((int)(bbg * factor), 0, 255), Math.Clamp((int)(bbb * factor), 0, 255)).ToArgb();
            }

            // Any other format: leave albedo alone rather than guess. Guessing is what produced the
            // black blotches and the inverted-looking coverage.
            return baseArgb;
        }

        var a = (baseArgb >> 24) & 0xFF;
        var br = (baseArgb >> 16) & 0xFF;
        var bg = (baseArgb >> 8) & 0xFF;
        var bb = baseArgb & 0xFF;

        // Ink coverage comes only from alpha; the direction depends on the texture background.
        var cov = detail.AverageAlpha < 0.5f ? alpha : 1f - alpha;
        cov = Math.Clamp(cov, 0f, 1f) * LineStrength;
        if (IsSurfaceDetailMask(detail))
        {
            cov *= 0.55f; // floor/wall grime should be more subtle
        }
        if (cov <= 0.01f)
        {
            return baseArgb;
        }

        // Ink "over" composition on albedo, with a floor (MinKeep) to preserve shape.
        var or = br * (1f - cov) + dr * cov;
        var og = bg * (1f - cov) + dg * cov;
        var ob = bb * (1f - cov) + db * cov;
        return Color.FromArgb(
            a,
            (int)MathF.Max(or, br * MinKeep),
            (int)MathF.Max(og, bg * MinKeep),
            (int)MathF.Max(ob, bb * MinKeep)).ToArgb();
    }

    private static bool IsSurfaceDetailMask(TextureImage detail)
    {
        var name = Path.GetFileNameWithoutExtension(detail.SourcePath).ToLowerInvariant();
        return name.Contains("road", StringComparison.Ordinal)
            || name.Contains("street", StringComparison.Ordinal)
            || name.Contains("sidewalk", StringComparison.Ordinal)
            || name.Contains("walkway", StringComparison.Ordinal)
            || name.Contains("pavement", StringComparison.Ordinal)
            || name.Contains("asphalt", StringComparison.Ordinal)
            || name.Contains("ground", StringComparison.Ordinal)
            || name.Contains("floor", StringComparison.Ordinal);
    }
}
