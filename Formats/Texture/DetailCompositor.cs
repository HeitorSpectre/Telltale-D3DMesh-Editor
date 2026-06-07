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
