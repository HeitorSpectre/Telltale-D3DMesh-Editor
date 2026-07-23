using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TelltaleD3DMeshEditor.Formats.Texture;

// Decoded RGBA image (from .d3dtx / .dds / .png), with UV sampling used by the preview
// and albedo export. Pixels are row-major ARGB ints.
public sealed class TextureImage
{
    public TextureImage(int width, int height, int[] pixels, string sourcePath, int? alphaMode = null, uint? sourceFormat = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        SourcePath = sourcePath;
        AlphaMode = alphaMode;
        SourceFormat = sourceFormat;
        AverageAlpha = ComputeAverageAlpha(pixels);
        NonOpaqueAlphaRatio = ComputeNonOpaqueAlphaRatio(pixels);
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }
    public string SourcePath { get; }
    public int? AlphaMode { get; }

    // The .d3dtx pixel format this image was decoded from (0x43 = BC4, 0x44 = BC5, ...), when known.
    // The channel count it implies is what distinguishes a one-channel grime map from a two-channel
    // derivative-normal map once both are expanded to RGBA.
    public uint? SourceFormat { get; }

    // BC5 stores two channels (R,G); the tool's decoder reconstructs Z into B. Telltale's newer engine
    // uses that layout for detail maps that perturb the surface normal, not for colour that belongs in
    // albedo. Compositing one as ink paints black blotches where the perturbation is strongest.
    public bool IsTwoChannelDerivativeMap => SourceFormat == 0x44;

    // BC4 is a single channel expanded to grey; Telltale uses it for grime/wear that multiplies albedo.
    public bool IsSingleChannelMap => SourceFormat == 0x43;

    // Batman ships TWO different kinds of detail map, both as BC5, so the format alone cannot tell them
    // apart — the difference is in the content:
    //  - R == G everywhere: one channel duplicated. An intensity/coverage mask (stubble, pores, seams,
    //    grime) that darkens albedo where it is present.
    //  - R != G: a genuine two-channel tangent-space map that describes relief and belongs to shading.
    // Treating the first kind as relief made it vanish (the two channels cancel in any directional
    // term); treating the second as coverage painted flat smudges.
    public bool HasDuplicatedChannels => _duplicatedChannels ??= ComputeDuplicatedChannels(Pixels);

    private bool? _duplicatedChannels;

    private static bool ComputeDuplicatedChannels(int[] pixels)
    {
        if (pixels.Length == 0)
        {
            return false;
        }

        // Sampling is enough to separate the two cases: a genuine two-channel map differs across most of
        // its active area, while a duplicated one matches on every single pixel.
        var step = Math.Max(1, pixels.Length / 4096);
        for (var i = 0; i < pixels.Length; i += step)
        {
            if (((pixels[i] >> 16) & 0xFF) != ((pixels[i] >> 8) & 0xFF))
            {
                return false;
            }
        }

        return true;
    }

    public float AverageAlpha { get; }
    public float NonOpaqueAlphaRatio { get; }

    // True when the alpha channel carries packed DATA rather than opacity. Telltale's newer engine
    // stores a per-pixel shading term (gloss/scatter) in the diffuse alpha of skin materials: Bruce
    // Wayne's face and hands average ~0.65 with NO fully opaque and NO fully transparent pixel.
    // A real opacity mask always keeps a large population at 255 (and a cutout also at 0), because
    // most of the surface is simply solid. Reading such a data channel as opacity turned faces and
    // hands see-through. Content-based on purpose: it needs no name list to stay correct.
    public bool HasPackedDataAlpha => _packedDataAlpha ??= ComputePackedDataAlpha(Pixels);

    private bool? _packedDataAlpha;

    private static bool ComputePackedDataAlpha(int[] pixels)
    {
        if (pixels.Length == 0)
        {
            return false;
        }

        var opaque = 0;
        var transparent = 0;
        foreach (var pixel in pixels)
        {
            var alpha = (pixel >> 24) & 0xFF;
            if (alpha >= 250)
            {
                opaque++;
            }
            else if (alpha <= 5)
            {
                transparent++;
            }
        }

        // Under 1% of the surface at either extreme means nothing is actually solid or actually cut
        // away, so the channel is not describing coverage.
        return opaque < pixels.Length / 100 && transparent < pixels.Length / 100;
    }

    public int Sample(float u, float v)
    {
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);
        var x = Math.Clamp((int)(u * Width), 0, Width - 1);
        var y = Math.Clamp((int)((1f - v) * Height), 0, Height - 1);
        return Pixels[y * Width + x];
    }

    public int SampleClamped(float u, float v)
    {
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        var x = Math.Clamp((int)(u * Width), 0, Width - 1);
        var y = Math.Clamp((int)((1f - v) * Height), 0, Height - 1);
        return Pixels[y * Width + x];
    }

    public Bitmap ToBitmap()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, Width, Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(Pixels, 0, data.Scan0, Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static float ComputeAverageAlpha(int[] pixels)
    {
        if (pixels.Length == 0)
        {
            return 1f;
        }

        var sum = 0L;
        foreach (var pixel in pixels)
        {
            sum += (pixel >> 24) & 0xFF;
        }

        return sum / (pixels.Length * 255f);
    }

    private static float ComputeNonOpaqueAlphaRatio(int[] pixels)
    {
        if (pixels.Length == 0)
        {
            return 0f;
        }

        var count = 0;
        foreach (var pixel in pixels)
        {
            if (((pixel >> 24) & 0xFF) < 250)
            {
                count++;
            }
        }

        return count / (float)pixels.Length;
    }
}

// Resolved texture set for a material/submesh (layers composited by the preview).
public sealed class MaterialTextureSet
{
    public TextureImage? Diffuse { get; set; }
    public TextureImage? Detail { get; set; }
    public TextureImage? Normal { get; set; }
    public TextureImage? Bake { get; set; }
    public TextureImage? Shadow { get; set; }
    public TextureImage? Occlusion { get; set; }
    public Dictionary<string, TextureImage> Auxiliary { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Count =>
        (Diffuse is null ? 0 : 1) +
        (Detail is null ? 0 : 1) +
        (Normal is null ? 0 : 1) +
        (Bake is null ? 0 : 1) +
        (Shadow is null ? 0 : 1) +
        (Occlusion is null ? 0 : 1) +
        Auxiliary.Count;
}
