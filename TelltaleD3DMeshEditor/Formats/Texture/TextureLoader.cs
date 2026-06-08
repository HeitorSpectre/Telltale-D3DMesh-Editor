using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TelltaleD3DMeshEditor.Core;
using TelltaleToolKit;
using TelltaleToolKit.T3Types.Textures;
using TelltaleToolKit.T3Types.Textures.T3Types;

namespace TelltaleD3DMeshEditor.Formats.Texture;

// Texture decoder to RGBA. Supports:
//  - Telltale .d3dtx (MetaStream + T3Texture header + DXT1/DXT5 payload);
//  - .dds (DXT1, DXT5, A8, and uncompressed RGBA32);
//  - .png/.bmp/etc via GDI+.
// Fully offline, with no external dependencies.
public static class TextureLoader
{
    private static readonly object ToolkitGate = new();
    private static Workspace? _minecraftStoryModeWorkspace;

    public static TextureImage Load(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".d3dtx", StringComparison.OrdinalIgnoreCase))
        {
            return LoadD3dtx(path);
        }

        return ext.Equals(".dds", StringComparison.OrdinalIgnoreCase)
            ? LoadDds(path)
            : LoadBitmap(path);
    }

    private static TextureImage LoadBitmap(string path)
    {
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.PageUnit = GraphicsUnit.Pixel;
            g.DrawImage(
                source,
                new Rectangle(0, 0, source.Width, source.Height),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel);
        }

        var pixels = ReadBitmapPixels(bitmap);
        return new TextureImage(bitmap.Width, bitmap.Height, pixels, path);
    }

    private static TextureImage LoadDds(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 128 ||
            data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
        {
            throw new InvalidDataException("Invalid DDS texture.");
        }

        var height = ReadInt(data, 12);
        var width = ReadInt(data, 16);
        var pixelFlags = ReadUInt(data, 80);
        var fourCc = ReadFourCc(data, 84);
        var rgbBitCount = ReadInt(data, 88);
        var redMask = ReadUInt(data, 92);
        var greenMask = ReadUInt(data, 96);
        var blueMask = ReadUInt(data, 100);
        var alphaMask = ReadUInt(data, 104);
        var payloadOffset = 128;

        int[] pixels;
        if (fourCc == "DXT1")
        {
            pixels = DecodeDxt1(data, payloadOffset, width, height);
        }
        else if (fourCc == "DXT5")
        {
            pixels = DecodeDxt5(data, payloadOffset, width, height);
        }
        else if (rgbBitCount == 8 && (pixelFlags & 0x2) != 0)
        {
            pixels = DecodeA8(data, payloadOffset, width, height);
        }
        else if (rgbBitCount == 32)
        {
            pixels = DecodeUncompressed32(data, payloadOffset, width, height, redMask, greenMask, blueMask, alphaMask);
        }
        else
        {
            throw new NotSupportedException($"Unsupported DDS format: {fourCc}, {rgbBitCount} bpp.");
        }

        return new TextureImage(width, height, pixels, path);
    }

    private static TextureImage LoadD3dtx(string path)
    {
        if (GameConfig.Current.Id == GameId.MinecraftStoryMode &&
            TryLoadModernD3dtx(path) is { } modernTexture)
        {
            return modernTexture;
        }

        var data = File.ReadAllBytes(path);
        var info = ReadD3dtxInfo(data);
        // 0x10 = A8 (1 byte/pixel, uncompressed); the others are block-compressed (DXT).
        var mip0Size = info.Format == 0x10
            ? info.Width * info.Height
            : MipSizeOf(info.Width, info.Height, info.BlockBytes);
        if (info.PixelLength < mip0Size)
        {
            throw new InvalidDataException("D3DTX pixel payload is smaller than its largest mip.");
        }

        // Telltale MetaStream stores mips in small-to-large order; the largest mip is at the end.
        var largestMipOffset = info.PixelStart + info.PixelLength - mip0Size;
        var pixels = info.Format switch
        {
            0x40 => DecodeDxt1(data, largestMipOffset, info.Width, info.Height),
            0x42 => DecodeDxt5(data, largestMipOffset, info.Width, info.Height),
            // A8: alpha only. Used as a cutout mask (for example eyelashes) -> black + alpha,
            // so eyelashes render black with the correct cutout instead of defaulting to gray/white.
            0x10 => DecodeA8Mask(data, largestMipOffset, info.Width, info.Height),
            _ => throw new NotSupportedException($"Unsupported D3DTX texture format 0x{info.Format:X}."),
        };
        return new TextureImage(info.Width, info.Height, pixels, path);
    }

    private static TextureImage? TryLoadModernD3dtx(string path)
    {
        try
        {
            var workspace = GetMinecraftStoryModeWorkspace();
            var texture = Toolkit.Instance.Deserialize<T3Texture>(path, workspace);
            if (texture is null || texture.RegionHeaders.Count == 0)
            {
                return null;
            }

            var region = texture.RegionHeaders
                .Where(region => region.FaceIndex == 0 && region.RegionData.Length > 0)
                .Select(region => new
                {
                    Header = region,
                    Dimensions = GetModernRegionDimensions(texture, region),
                })
                .OrderByDescending(region => region.Dimensions.Width * region.Dimensions.Height)
                .ThenByDescending(region => region.Header.DataSize)
                .FirstOrDefault();
            if (region is null || region.Header.RegionData.Length == 0)
            {
                return null;
            }

            var width = region.Dimensions.Width;
            var height = region.Dimensions.Height;
            var pixels = texture.SurfaceFormat switch
            {
                T3SurfaceFormat.ARGB8 => DecodeBgra32(region.Header.RegionData, 0, width, height),
                T3SurfaceFormat.RGBA8 => DecodeRgba32(region.Header.RegionData, 0, width, height),
                T3SurfaceFormat.BC1 => DecodeDxt1(region.Header.RegionData, 0, width, height),
                T3SurfaceFormat.BC3 => DecodeDxt5(region.Header.RegionData, 0, width, height),
                T3SurfaceFormat.A8 => DecodeA8Mask(region.Header.RegionData, 0, width, height),
                _ => null,
            };

            return pixels is null ? null : new TextureImage(width, height, pixels, path);
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height) GetModernRegionDimensions(T3Texture texture, T3Texture.RegionStreamHeader region)
    {
        var baseWidth = Math.Max(1, checked((int)texture.Width));
        var baseHeight = Math.Max(1, checked((int)texture.Height));
        var mipIndex = Math.Max(0, region.MipIndex);
        var expectedWidth = Math.Max(1, baseWidth >> mipIndex);
        var expectedHeight = Math.Max(1, baseHeight >> mipIndex);
        var dataSize = region.RegionData.Length > 0 ? region.RegionData.Length : region.DataSize;
        if (ModernTextureDataSize(texture.SurfaceFormat, expectedWidth, expectedHeight) == dataSize)
        {
            return (expectedWidth, expectedHeight);
        }

        var mipCount = Math.Max(region.MipCount, 1);
        var maxMip = Math.Max(16, mipIndex + mipCount + 1);
        for (var mip = 0; mip <= maxMip; mip++)
        {
            var width = Math.Max(1, baseWidth >> mip);
            var height = Math.Max(1, baseHeight >> mip);
            if (ModernTextureDataSize(texture.SurfaceFormat, width, height) == dataSize)
            {
                return (width, height);
            }

            if (width == 1 && height == 1)
            {
                break;
            }
        }

        return (expectedWidth, expectedHeight);
    }

    private static int ModernTextureDataSize(T3SurfaceFormat format, int width, int height)
    {
        return format switch
        {
            T3SurfaceFormat.ARGB8 or T3SurfaceFormat.RGBA8 => checked(width * height * 4),
            T3SurfaceFormat.A8 => checked(width * height),
            T3SurfaceFormat.BC1 => checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8),
            T3SurfaceFormat.BC3 => checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16),
            _ => -1,
        };
    }

    private static Workspace GetMinecraftStoryModeWorkspace()
    {
        lock (ToolkitGate)
        {
            if (!Toolkit.IsInitialized)
            {
                Toolkit.Initialize(new Toolkit.Configuration
                {
                    DataFolder = ResolveToolkitDataFolder(),
                });
            }

            return _minecraftStoryModeWorkspace ??=
                Toolkit.Instance.CreateWorkspace("texture::Minecraft Story Mode", "Minecraft: Story Mode");
        }
    }

    private static string ResolveToolkitDataFolder()
    {
        var assemblyFolder = Path.GetDirectoryName(typeof(TextureLoader).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyFolder))
        {
            var assemblyData = Path.Combine(assemblyFolder, "ttk-data");
            if (Directory.Exists(assemblyData))
            {
                return assemblyData;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "ttk-data");
    }

    private static D3dtxInfo ReadD3dtxInfo(byte[] data)
    {
        if (data.Length < 20)
        {
            throw new InvalidDataException("D3DTX file is too small.");
        }

        var magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
        if (magic is not ("5VSM" or "6VSM"))
        {
            throw new InvalidDataException($"Invalid D3DTX MetaStream magic '{magic}'.");
        }

        var metaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var pixelSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var versionCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var metaStart = 20 + versionCount * 12;
        var pixelStart = metaStart + metaSize;
        if (metaStart < 20 || metaStart >= data.Length ||
            pixelStart < metaStart || pixelStart > data.Length ||
            pixelStart + pixelSize != data.Length)
        {
            throw new InvalidDataException("Unexpected D3DTX MetaStream layout.");
        }

        var p = metaStart;
        int ReadI32()
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            p += 4;
            return value;
        }

        uint ReadU32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
            p += 4;
            return value;
        }

        var someValue = ReadI32();
        var unknownFlagsBlockSize = ReadI32();
        p += Math.Max(0, unknownFlagsBlockSize - 4);
        var platformBlockSize = ReadI32();
        p += Math.Max(0, platformBlockSize - 4);
        SkipNameBlock();
        SkipNameBlock();
        _ = ReadU32();
        p += 1;
        if (p <= data.Length && data[p - 1] == 0x31)
        {
            p += 8;
            var extraLength = ReadI32();
            p += Math.Max(0, extraLength);
        }

        var mipCount = ReadI32();
        var width = ReadI32();
        var height = ReadI32();
        if ((magic is "5VSM" or "6VSM") && someValue >= 8)
        {
            p += 8;
        }

        var format = ReadU32();
        var blockBytes = format switch
        {
            0x10 => 1, // A8: not block-compressed; mip calculated separately (1 byte/pixel).
            0x40 or 0x43 => 8,
            0x41 or 0x42 or 0x44 or 0x45 or 0x46 => 16,
            _ => throw new NotSupportedException($"Unsupported D3DTX format 0x{format:X}."),
        };

        return new D3dtxInfo(width, height, mipCount, format, blockBytes, pixelStart, pixelSize);

        void SkipNameBlock()
        {
            var total = ReadI32();
            var length = ReadI32();
            if (length < 0 || total < length + 8)
            {
                throw new InvalidDataException("Invalid D3DTX name block.");
            }

            p += total - 8;
        }
    }

    private static int[] DecodeDxt1(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        var pos = offset;
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                var c0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
                var c1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
                pos += 8;
                var colors = BuildDxt1Colors(c0, c1);
                WriteDxtBlock(pixels, width, height, bx, by, bits, colors);
            }
        }
        return pixels;
    }

    private static int[] DecodeDxt5(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        var pos = offset;
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                var a0 = data[pos];
                var a1 = data[pos + 1];
                ulong alphaBits = 0;
                for (var i = 0; i < 6; i++)
                {
                    alphaBits |= (ulong)data[pos + 2 + i] << (8 * i);
                }

                var c0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 8));
                var c1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 10));
                var colorBits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12));
                pos += 16;

                var colors = BuildDxt5Colors(c0, c1);
                var alphas = BuildDxt5Alphas(a0, a1);
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var pixel = y * 4 + x;
                        var tx = bx + x;
                        var ty = by + y;
                        if (tx >= width || ty >= height)
                        {
                            continue;
                        }

                        var colorIndex = (int)((colorBits >> (2 * pixel)) & 0x3);
                        var alphaIndex = (int)((alphaBits >> (3 * pixel)) & 0x7);
                        var color = Color.FromArgb(colors[colorIndex]);
                        pixels[ty * width + tx] = Color.FromArgb(alphas[alphaIndex], color.R, color.G, color.B).ToArgb();
                    }
                }
            }
        }
        return pixels;
    }

    private static int[] DecodeA8(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length && offset + i < data.Length; i++)
        {
            var alpha = data[offset + i];
            pixels[i] = Color.FromArgb(alpha, 255, 255, 255).ToArgb();
        }
        return pixels;
    }

    // Treat .d3dtx A8 as a cutout mask for dark features (eyelashes): black RGB, byte alpha.
    // This keeps eyelashes black with the right cutout instead of turning into default gray/white.
    private static int[] DecodeA8Mask(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length && offset + i < data.Length; i++)
        {
            var alpha = data[offset + i];
            pixels[i] = Color.FromArgb(alpha, 0, 0, 0).ToArgb();
        }
        return pixels;
    }

    private static int[] DecodeUncompressed32(byte[] data, int offset, int width, int height, uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4));
            var r = ExtractMaskedByte(value, redMask);
            var g = ExtractMaskedByte(value, greenMask);
            var b = ExtractMaskedByte(value, blueMask);
            var a = alphaMask == 0 ? 255 : ExtractMaskedByte(value, alphaMask);
            pixels[i] = Color.FromArgb(a, r, g, b).ToArgb();
        }
        return pixels;
    }

    private static int[] DecodeBgra32(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length && offset + i * 4 + 3 < data.Length; i++)
        {
            var p = offset + i * 4;
            pixels[i] = Color.FromArgb(data[p + 3], data[p + 2], data[p + 1], data[p]).ToArgb();
        }
        return pixels;
    }

    private static int[] DecodeRgba32(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length && offset + i * 4 + 3 < data.Length; i++)
        {
            var p = offset + i * 4;
            pixels[i] = Color.FromArgb(data[p + 3], data[p], data[p + 1], data[p + 2]).ToArgb();
        }
        return pixels;
    }

    private static int[] ReadBitmapPixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static int[] BuildDxt1Colors(ushort c0, ushort c1)
    {
        var colors = new int[4];
        var a = Rgb565(c0);
        var b = Rgb565(c1);
        colors[0] = a.ToArgb();
        colors[1] = b.ToArgb();
        colors[2] = c0 > c1
            ? Color.FromArgb(255, (2 * a.R + b.R) / 3, (2 * a.G + b.G) / 3, (2 * a.B + b.B) / 3).ToArgb()
            : Color.FromArgb(255, (a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2).ToArgb();
        colors[3] = c0 > c1
            ? Color.FromArgb(255, (a.R + 2 * b.R) / 3, (a.G + 2 * b.G) / 3, (a.B + 2 * b.B) / 3).ToArgb()
            : Color.Transparent.ToArgb();
        return colors;
    }

    private static int[] BuildDxt5Colors(ushort c0, ushort c1)
    {
        var colors = new int[4];
        var a = Rgb565(c0);
        var b = Rgb565(c1);
        colors[0] = a.ToArgb();
        colors[1] = b.ToArgb();
        colors[2] = Color.FromArgb(255, (2 * a.R + b.R) / 3, (2 * a.G + b.G) / 3, (2 * a.B + b.B) / 3).ToArgb();
        colors[3] = Color.FromArgb(255, (a.R + 2 * b.R) / 3, (a.G + 2 * b.G) / 3, (a.B + 2 * b.B) / 3).ToArgb();
        return colors;
    }

    private static byte[] BuildDxt5Alphas(byte a0, byte a1)
    {
        var alphas = new byte[8];
        alphas[0] = a0;
        alphas[1] = a1;
        if (a0 > a1)
        {
            for (var i = 1; i <= 6; i++)
            {
                alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
        }
        else
        {
            for (var i = 1; i <= 4; i++)
            {
                alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            }
            alphas[6] = 0;
            alphas[7] = 255;
        }
        return alphas;
    }

    private static void WriteDxtBlock(int[] pixels, int width, int height, int bx, int by, uint bits, int[] colors)
    {
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var tx = bx + x;
                var ty = by + y;
                if (tx >= width || ty >= height)
                {
                    continue;
                }

                var index = (int)((bits >> (2 * (y * 4 + x))) & 0x3);
                pixels[ty * width + tx] = colors[index];
            }
        }
    }

    private static Color Rgb565(ushort value)
    {
        var r = (value >> 11) & 0x1F;
        var g = (value >> 5) & 0x3F;
        var b = value & 0x1F;
        return Color.FromArgb(255, r * 255 / 31, g * 255 / 63, b * 255 / 31);
    }

    private static byte ExtractMaskedByte(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        var shift = 0;
        var temp = mask;
        while ((temp & 1) == 0)
        {
            shift++;
            temp >>= 1;
        }

        var max = temp;
        var raw = (value & mask) >> shift;
        return (byte)(raw * 255 / max);
    }

    private static int ReadInt(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
    private static uint ReadUInt(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static string ReadFourCc(byte[] data, int offset) => new(data.Skip(offset).Take(4).Select(b => (char)b).ToArray());
    private static int MipSizeOf(int width, int height, int blockBytes) =>
        Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;

    private readonly record struct D3dtxInfo(
        int Width,
        int Height,
        int MipCount,
        uint Format,
        int BlockBytes,
        int PixelStart,
        int PixelLength);
}
