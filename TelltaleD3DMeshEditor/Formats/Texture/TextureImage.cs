using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TelltaleD3DMeshEditor.Formats.Texture;

// Decoded RGBA image (from .d3dtx / .dds / .png), with UV sampling used by the preview
// and albedo export. Pixels are row-major ARGB ints.
public sealed class TextureImage
{
    public TextureImage(int width, int height, int[] pixels, string sourcePath, int? alphaMode = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        SourcePath = sourcePath;
        AlphaMode = alphaMode;
        AverageAlpha = ComputeAverageAlpha(pixels);
        NonOpaqueAlphaRatio = ComputeNonOpaqueAlphaRatio(pixels);
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }
    public string SourcePath { get; }
    public int? AlphaMode { get; }
    public float AverageAlpha { get; }
    public float NonOpaqueAlphaRatio { get; }

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
