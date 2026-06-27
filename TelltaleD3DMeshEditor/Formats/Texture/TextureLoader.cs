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
    private static readonly Dictionary<string, Workspace> ModernTextureWorkspaces = new(StringComparer.OrdinalIgnoreCase);

    public static TextureFormatInfo InspectFormat(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".d3dtx", StringComparison.OrdinalIgnoreCase))
        {
            return InspectD3dtx(path);
        }

        if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return InspectDds(path);
        }

        using var source = new Bitmap(path);
        return new TextureFormatInfo(
            Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            source.PixelFormat.ToString(),
            0,
            "Unknown",
            source.Width,
            source.Height,
            1,
            [new TextureRegionInfo(0, 0, 1, 0, 0, 0, source.Width, source.Height)]);
    }

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
        if (!TryDecodeDdsAt(data, 0, out var width, out var height, out var pixels))
        {
            throw new NotSupportedException("Unsupported or invalid DDS texture.");
        }

        return new TextureImage(width, height, pixels, path);
    }

    // Decodes a DDS image starting at ddsOffset (the 'DDS ' magic). Reused both for standalone .dds
    // files and for the DDS payloads embedded in legacy (ERTM) color-swatch d3dtx files.
    private static bool TryDecodeDdsAt(byte[] data, int ddsOffset, out int width, out int height, out int[] pixels)
    {
        width = 0;
        height = 0;
        pixels = [];
        if (ddsOffset < 0 || ddsOffset + 128 > data.Length ||
            data[ddsOffset] != 'D' || data[ddsOffset + 1] != 'D' || data[ddsOffset + 2] != 'S' || data[ddsOffset + 3] != ' ')
        {
            return false;
        }

        height = ReadInt(data, ddsOffset + 12);
        width = ReadInt(data, ddsOffset + 16);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
        {
            return false;
        }

        var pixelFlags = ReadUInt(data, ddsOffset + 80);
        var fourCc = ReadFourCc(data, ddsOffset + 84);
        var rgbBitCount = ReadInt(data, ddsOffset + 88);
        var redMask = ReadUInt(data, ddsOffset + 92);
        var greenMask = ReadUInt(data, ddsOffset + 96);
        var blueMask = ReadUInt(data, ddsOffset + 100);
        var alphaMask = ReadUInt(data, ddsOffset + 104);
        var payloadOffset = ddsOffset + 128;

        if (fourCc == "DXT1")
        {
            pixels = DecodeDxt1(data, payloadOffset, width, height);
        }
        else if (fourCc == "DXT3")
        {
            pixels = DecodeDxt3(data, payloadOffset, width, height);
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
            return false;
        }

        return true;
    }

    private static TextureFormatInfo InspectDds(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 128 ||
            data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
        {
            throw new InvalidDataException("Invalid DDS texture.");
        }

        var height = ReadInt(data, 12);
        var width = ReadInt(data, 16);
        var fourCc = ReadFourCc(data, 84).TrimEnd('\0');
        var rgbBitCount = ReadInt(data, 88);
        var pixelFlags = ReadUInt(data, 80);
        var formatName = !string.IsNullOrWhiteSpace(fourCc)
            ? fourCc
            : ((pixelFlags & 0x2) != 0 ? $"A{rgbBitCount}" : $"RGB{rgbBitCount}");
        return new TextureFormatInfo(
            "DDS",
            formatName,
            0,
            "Unknown",
            width,
            height,
            1,
            [new TextureRegionInfo(0, 0, 1, data.Length - 128, 0, data.Length - 128, width, height)]);
    }

    private static TextureImage LoadD3dtx(string path)
    {
        if (!string.IsNullOrWhiteSpace(GameConfig.Current.ModernTextureToolkitGameName) &&
            TryLoadModernD3dtx(path, GameConfig.Current.ModernTextureToolkitGameName) is { } modernTexture)
        {
            return modernTexture;
        }

        var data = File.ReadAllBytes(path);
        if (IsLegacyErtm(data))
        {
            // ERTM holds two distinct texture bodies: the TWD-era OldT3Texture (legacy path) and the
            // newer NewT3Texture (same body as 5VSM, used by the console UI textures). Try legacy first
            // since it covers most ERTM files; fall back to the shared NewT3Texture reader for the rest.
            try
            {
                return LoadLegacyErtmD3dtx(data, path);
            }
            catch
            {
                // fall through to the NewT3Texture reader below
            }
        }

        var info = ReadD3dtxInfo(data);
        return DecodeD3dtxImage(data, info, path);
    }

    private static TextureImage DecodeD3dtxImage(byte[] data, D3dtxInfo info, string path)
    {
        if (info.Format == 0xFFFFFFFF)
        {
            // Empty/stub texture: 1x1 fully transparent placeholder so the referencing mesh still loads.
            return new TextureImage(1, 1, [0], path, info.AlphaMode);
        }

        // Uncompressed formats are sized per pixel (A8=1 byte, ARGB8/RGBA8=4 bytes); DXT is block-based.
        var mip0Size = info.Format switch
        {
            0x10 => info.Width * info.Height,
            0x0 or 0xA => info.Width * info.Height * 4,
            _ => MipSizeOf(info.Width, info.Height, info.BlockBytes),
        };
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
            // 0x0 = ARGB8 (BGRA byte order), 0xA = RGBA8. Used by the solid-colour "color_*" swatches.
            0x0 => DecodeBgra8(data, largestMipOffset, info.Width, info.Height, forceOpaque: false),
            0xA => DecodeRgba8(data, largestMipOffset, info.Width, info.Height),
            _ => throw new NotSupportedException($"Unsupported D3DTX texture format 0x{info.Format:X}."),
        };
        return new TextureImage(info.Width, info.Height, pixels, path, info.AlphaMode);
    }

    private static TextureFormatInfo InspectD3dtx(string path)
    {
        var modernInfo =
            TryInspectModernD3dtx(path, GameConfig.Current.ModernTextureToolkitGameName) ??
            TryInspectModernD3dtx(path, GameConfig.WalkingDeadMichonne.ModernTextureToolkitGameName) ??
            TryInspectModernD3dtx(path, GameConfig.GameOfThrones.ModernTextureToolkitGameName) ??
            TryInspectModernD3dtx(path, GameConfig.TalesFromTheBorderlands2014.ModernTextureToolkitGameName) ??
            TryInspectModernD3dtx(path, GameConfig.MinecraftStoryMode.ModernTextureToolkitGameName);
        if (modernInfo is not null)
        {
            return modernInfo;
        }

        var data = File.ReadAllBytes(path);
        if (IsLegacyErtm(data))
        {
            try
            {
                return InspectLegacyErtmD3dtx(data, path);
            }
            catch
            {
                // fall through to the NewT3Texture reader (console UI textures in ERTM)
            }
        }

        var info = ReadD3dtxInfo(data);
        return new TextureFormatInfo(
            "D3DTX",
            D3dtxFormatName(info.Format),
            info.Format,
            "Unknown",
            info.Width,
            info.Height,
            1,
            [new TextureRegionInfo(0, 0, info.MipCount, info.PixelLength, 0, info.PixelLength, info.Width, info.Height)]);
    }

    private static TextureImage? TryLoadModernD3dtx(string path, string? toolkitGameName)
    {
        if (string.IsNullOrWhiteSpace(toolkitGameName))
        {
            return null;
        }

        try
        {
            var workspace = GetModernTextureWorkspace(toolkitGameName);
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

            return pixels is null ? null : new TextureImage(width, height, pixels, path, texture.AlphaMode);
        }
        catch
        {
            return null;
        }
    }

    private static TextureFormatInfo? TryInspectModernD3dtx(string path, string? toolkitGameName)
    {
        if (string.IsNullOrWhiteSpace(toolkitGameName))
        {
            return null;
        }

        try
        {
            var workspace = GetModernTextureWorkspace(toolkitGameName);
            var texture = Toolkit.Instance.Deserialize<T3Texture>(path, workspace);
            if (texture is null || texture.RegionHeaders.Count == 0)
            {
                return null;
            }

            var regions = texture.RegionHeaders
                .Select(region =>
                {
                    var dimensions = GetModernRegionDimensions(texture, region);
                    return new TextureRegionInfo(
                        region.FaceIndex,
                        region.MipIndex,
                        Math.Max(region.MipCount, 1),
                        region.RegionData.Length > 0 ? region.RegionData.Length : region.DataSize,
                        region.Pitch,
                        region.SlicePitch,
                        dimensions.Width,
                        dimensions.Height);
                })
                .OrderBy(region => region.FaceIndex)
                .ThenBy(region => region.MipIndex)
                .ToList();

            return new TextureFormatInfo(
                "D3DTX",
                texture.SurfaceFormat.ToString(),
                unchecked((uint)texture.SurfaceFormat),
                texture.SurfaceGamma.ToString(),
                Math.Max(1, checked((int)texture.Width)),
                Math.Max(1, checked((int)texture.Height)),
                texture.RegionHeaders.Count,
                regions);
        }
        catch
        {
            return null;
        }
    }

    private static TextureImage LoadLegacyErtmD3dtx(byte[] data, string path)
    {
        // Solid-colour "color_*" swatches embed a complete DDS payload instead of the usual T3Texture
        // header; decode that directly when the normal legacy parse cannot interpret the file.
        if (!CanReadLegacyErtmD3dtxInfo(data))
        {
            var ddsOffset = IndexOf(data, "DDS ");
            if (ddsOffset >= 0 && TryDecodeDdsAt(data, ddsOffset, out var dw, out var dh, out var dpx))
            {
                return new TextureImage(dw, dh, dpx, path);
            }

            // The remaining "color_*" swatches are a single solid colour stored as the trailing BGRA
            // pixel; represent them as a 1x1 texture of that colour.
            if (IsColorSwatch(path) && data.Length >= 4)
            {
                var o = data.Length - 4;
                var argb = (data[o + 3] << 24) | (data[o + 2] << 16) | (data[o + 1] << 8) | data[o];
                return new TextureImage(1, 1, [argb], path);
            }
        }

        var info = ReadLegacyErtmD3dtxInfo(data);
        var pixels = info.Format switch
        {
            0x40 => DecodeDxt1(data, info.PixelStart, info.Width, info.Height),
            0x41 => DecodeDxt3(data, info.PixelStart, info.Width, info.Height),
            0x42 => DecodeDxt5(data, info.PixelStart, info.Width, info.Height),
            0x15 => DecodeBgra8(data, info.PixelStart, info.Width, info.Height, forceOpaque: false),
            0x16 => DecodeBgra8(data, info.PixelStart, info.Width, info.Height, forceOpaque: true),
            0x17 => DecodeRgba8(data, info.PixelStart, info.Width, info.Height),
            0x10 => DecodeA8Mask(data, info.PixelStart, info.Width, info.Height),
            0x50 => DecodeL8Gray(data, info.PixelStart, info.Width, info.Height),
            0x51 => DecodeA8L8(data, info.PixelStart, info.Width, info.Height),
            _ => throw new NotSupportedException($"Unsupported legacy D3DTX texture format 0x{info.Format:X}."),
        };

        return new TextureImage(info.Width, info.Height, pixels, path);
    }

    private static TextureFormatInfo InspectLegacyErtmD3dtx(byte[] data, string path)
    {
        if (!CanReadLegacyErtmD3dtxInfo(data))
        {
            var ddsOffset = IndexOf(data, "DDS ");
            if (ddsOffset >= 0 && TryDecodeDdsAt(data, ddsOffset, out var dw, out var dh, out _))
            {
                return new TextureFormatInfo(
                    "DDS",
                    ReadFourCc(data, ddsOffset + 84) is "DXT1" or "DXT3" or "DXT5" ? ReadFourCc(data, ddsOffset + 84) : "Uncompressed",
                    0,
                    "Unknown",
                    dw,
                    dh,
                    1,
                    [new TextureRegionInfo(0, 0, 1, 0, 0, 0, dw, dh)]);
            }

            if (IsColorSwatch(path) && data.Length >= 4)
            {
                return new TextureFormatInfo(
                    "D3DTX",
                    "ARGB8 (solid)",
                    0x0,
                    "Unknown",
                    1,
                    1,
                    1,
                    [new TextureRegionInfo(0, 0, 1, 4, 0, 4, 1, 1)]);
            }
        }

        var info = ReadLegacyErtmD3dtxInfo(data);
        return new TextureFormatInfo(
            "D3DTX",
            D3dtxFormatName(info.Format),
            info.Format,
            "Unknown",
            info.Width,
            info.Height,
            1,
            [new TextureRegionInfo(0, 0, info.MipCount, info.PixelLength, 0, info.PixelLength, info.Width, info.Height)]);
    }

    private static bool IsColorSwatch(string path)
        => Path.GetFileName(path).StartsWith("color_", StringComparison.OrdinalIgnoreCase);

    private static bool CanReadLegacyErtmD3dtxInfo(byte[] data)
    {
        try
        {
            ReadLegacyErtmD3dtxInfo(data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static D3dtxInfo ReadLegacyErtmD3dtxInfo(byte[] data)
    {
        var dxtOffset = IndexOf(data, "DXT1");
        var format = 0x40u;
        var blockBytes = 8;
        var dxt3Offset = IndexOf(data, "DXT3");
        var dxt5Offset = IndexOf(data, "DXT5");
        if (dxtOffset < 0 || (dxt3Offset >= 0 && dxt3Offset < dxtOffset))
        {
            dxtOffset = dxt3Offset;
            format = 0x41;
            blockBytes = 16;
        }

        if (dxtOffset < 0 || (dxt5Offset >= 0 && dxt5Offset < dxtOffset))
        {
            dxtOffset = dxt5Offset;
            format = 0x42;
            blockBytes = 16;
        }

        if (dxtOffset < 4)
        {
            // No DXT FourCC: the legacy (ERTM / TWD-era) texture is uncompressed (A8R8G8B8, A8, ...).
            // Its header stores [mNumMipLevels, mD3DFormat, mWidth, mHeight] as four consecutive u32,
            // with mD3DFormat a D3D9 LegacyFormat code; scan for it and decode from there.
            if (TryReadLegacyUncompressedD3dtxInfo(data, out var uncompressed))
            {
                return uncompressed;
            }

            throw new NotSupportedException("Legacy ERTM D3DTX texture does not contain a supported payload.");
        }

        var mipCount = checked((int)ReadUInt(data, dxtOffset - 4));
        var width = ReadInt(data, dxtOffset + 4);
        var height = ReadInt(data, dxtOffset + 8);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || mipCount <= 0 || mipCount > 32)
        {
            throw new InvalidDataException($"Invalid legacy D3DTX dimensions or mip count: {width}x{height}, mips={mipCount}.");
        }

        var baseMipLength = MipSizeOf(width, height, blockBytes);
        var pixelLength = LegacyMipChainSize(width, height, blockBytes, mipCount);
        if (pixelLength > data.Length || data.Length - pixelLength < dxtOffset)
        {
            pixelLength = baseMipLength;
            mipCount = 1;
        }

        var pixelStart = data.Length - pixelLength;
        if (pixelStart < 0 || pixelStart >= data.Length)
        {
            throw new InvalidDataException("Legacy D3DTX pixel payload is outside the file.");
        }

        return new D3dtxInfo(width, height, mipCount, format, blockBytes, pixelStart, pixelLength);
    }

    // Reads an uncompressed legacy (ERTM / TWD-era) D3DTX by locating the
    // [mNumMipLevels, mD3DFormat, mWidth, mHeight] header quadruple, where mD3DFormat is a D3D9
    // LegacyFormat code. Returns the largest-mip-first payload (the legacy mip order).
    private static bool TryReadLegacyUncompressedD3dtxInfo(byte[] data, out D3dtxInfo info)
        // Two passes: first only well-known D3D9 formats; then, as a last resort, format 0 (ARGB8) used
        // by the 1x1 solid-colour "color_*" swatches (matching 0 too eagerly in pass 1 risks false hits).
        => TryReadLegacyUncompressedD3dtxInfo(data, allowUnknownFormat: false, out info) ||
           TryReadLegacyUncompressedD3dtxInfo(data, allowUnknownFormat: true, out info);

    private static bool TryReadLegacyUncompressedD3dtxInfo(byte[] data, bool allowUnknownFormat, out D3dtxInfo info)
    {
        info = default!;
        var limit = Math.Min(data.Length - 16, 4096);
        for (var p = 8; p <= limit; p++)
        {
            var mipCount = (int)ReadUInt(data, p);
            var formatCode = ReadUInt(data, p + 4);
            var width = ReadInt(data, p + 8);
            var height = ReadInt(data, p + 12);
            if (mipCount < 1 || mipCount > 16 || width < 1 || width > 8192 || height < 1 || height > 8192)
            {
                continue;
            }

            uint format;
            int bytesPerPixel;
            if (TryMapLegacyUncompressedFormat(formatCode, out format, out bytesPerPixel))
            {
                // known format
            }
            else if (allowUnknownFormat && formatCode == 0)
            {
                format = 0x15; // ARGB8 (BGRA byte order)
                bytesPerPixel = 4;
            }
            else
            {
                continue;
            }

            var pixelLength = LegacyUncompressedChainSize(width, height, bytesPerPixel, mipCount);
            var baseMip = width * height * bytesPerPixel;
            if (pixelLength > data.Length || data.Length - pixelLength < p + 16)
            {
                pixelLength = baseMip;
                mipCount = 1;
            }

            if (pixelLength <= 0 || pixelLength > data.Length || data.Length - pixelLength < p + 16)
            {
                continue;
            }

            var pixelStart = data.Length - pixelLength;
            info = new D3dtxInfo(width, height, mipCount, format, bytesPerPixel, pixelStart, pixelLength);
            return true;
        }

        return false;
    }

    // Maps a D3D9 LegacyFormat code to our internal format id + bytes-per-pixel (uncompressed only).
    private static bool TryMapLegacyUncompressedFormat(uint legacyFormat, out uint format, out int bytesPerPixel)
    {
        switch (legacyFormat)
        {
            case 21: // A8R8G8B8
                format = 0x15; bytesPerPixel = 4; return true;
            case 22: // X8R8G8B8
                format = 0x16; bytesPerPixel = 4; return true;
            case 32: // A8B8G8R8 (RGBA byte order)
                format = 0x17; bytesPerPixel = 4; return true;
            case 28: // A8
                format = 0x10; bytesPerPixel = 1; return true;
            case 50: // L8
                format = 0x50; bytesPerPixel = 1; return true;
            case 51: // A8L8
                format = 0x51; bytesPerPixel = 2; return true;
            default:
                format = 0; bytesPerPixel = 0; return false;
        }
    }

    private static int LegacyUncompressedChainSize(int width, int height, int bytesPerPixel, int mipCount)
    {
        var total = 0;
        var w = width;
        var h = height;
        for (var mip = 0; mip < mipCount; mip++)
        {
            total = checked(total + w * h * bytesPerPixel);
            if (w == 1 && h == 1)
            {
                break;
            }

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        return total;
    }

    // D3D A8R8G8B8 / X8R8G8B8 store bytes as B,G,R,A (little-endian). forceOpaque is used for the X8
    // variant where the high byte is undefined.
    private static int[] DecodeBgra8(byte[] data, int offset, int width, int height, bool forceOpaque)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var o = offset + i * 4;
            int b = data[o], g = data[o + 1], r = data[o + 2], a = forceOpaque ? 255 : data[o + 3];
            pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }

        return pixels;
    }

    // D3D A8B8G8R8 stores bytes as R,G,B,A (little-endian DWORD 0xAABBGGRR).
    private static int[] DecodeRgba8(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var o = offset + i * 4;
            int r = data[o], g = data[o + 1], b = data[o + 2], a = data[o + 3];
            pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }

        return pixels;
    }

    private static int[] DecodeL8Gray(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            int l = data[offset + i];
            pixels[i] = (255 << 24) | (l << 16) | (l << 8) | l;
        }

        return pixels;
    }

    private static int[] DecodeA8L8(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            int l = data[offset + i * 2];
            int a = data[offset + i * 2 + 1];
            pixels[i] = (a << 24) | (l << 16) | (l << 8) | l;
        }

        return pixels;
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

    private static Workspace GetModernTextureWorkspace(string toolkitGameName)
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

            if (!ModernTextureWorkspaces.TryGetValue(toolkitGameName, out var workspace))
            {
                workspace = Toolkit.Instance.CreateWorkspace($"texture::{toolkitGameName}", toolkitGameName);
                ModernTextureWorkspaces[toolkitGameName] = workspace;
            }

            return workspace;
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
        if (magic is not ("5VSM" or "6VSM" or "4VSM" or "ERTM" or "MTRE"))
        {
            throw new InvalidDataException($"Invalid D3DTX MetaStream magic '{magic}'.");
        }

        int metaStart;
        int pixelStart;
        int pixelSize;
        if (magic is "ERTM" or "MTRE")
        {
            // Console UI textures use the NewT3Texture body (same as 5VSM) inside an MTRE container, which
            // has no section-size fields. Locate the body via the meta header; the pixel payload is the
            // rest of the file with the largest mip last (same convention as 4VSM).
            metaStart = MetaStreamHeader.Parse(data).DataOffset;
            if (metaStart < 8 || metaStart >= data.Length)
            {
                throw new InvalidDataException("Unexpected D3DTX MetaStream layout.");
            }

            pixelStart = metaStart;
            pixelSize = data.Length - metaStart;
        }
        else if (magic is "4VSM")
        {
            // MSV4 (Tales from the Borderlands source-code leak): one fewer u32 in the preamble, so the
            // class-name count sits at offset 12 and the meta section follows at 16 + count*12. The body
            // (T3Texture) is identical to MSV5. There is no async-size field; the pixel payload is simply
            // the rest of the file, with the largest mip last, so spanning pixelStart..EOF decodes right.
            var versionCount4 = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            metaStart = 16 + versionCount4 * 12;
            if (metaStart < 16 || metaStart >= data.Length)
            {
                throw new InvalidDataException("Unexpected D3DTX MetaStream layout.");
            }

            pixelStart = metaStart;
            pixelSize = data.Length - metaStart;
        }
        else
        {
            var metaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            pixelSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            var versionCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
            metaStart = 20 + versionCount * 12;
            pixelStart = metaStart + metaSize;
            // Some bake/atlas textures append a small trailing block after the pixel payload, so allow
            // trailing bytes (pixelStart + pixelSize may be < file length). The largest mip is still at
            // the end of the pixel section, so decoding is unaffected.
            if (metaStart < 20 || metaStart >= data.Length ||
                pixelStart < metaStart || pixelStart > data.Length ||
                pixelStart + pixelSize > data.Length)
            {
                throw new InvalidDataException("Unexpected D3DTX MetaStream layout.");
            }
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
        // 0xFFFFFFFF marks an empty/stub texture (no pixel data, mbHasTextureData = false). Report it as
        // a 1x1 placeholder so a mesh that references it still previews instead of erroring out.
        if (format == 0xFFFFFFFF)
        {
            return new D3dtxInfo(1, 1, 1, 0xFFFFFFFF, 0, pixelStart, 0, null);
        }

        var alphaMode = ReadAlphaMode();
        var blockBytes = format switch
        {
            0x10 => 1, // A8: not block-compressed; mip calculated separately (1 byte/pixel).
            0x0 or 0xA => 4, // ARGB8 / RGBA8: uncompressed 4 bytes/pixel (solid-colour "color_*" swatches).
            0x40 or 0x43 => 8,
            0x41 or 0x42 or 0x44 or 0x45 or 0x46 => 16,
            _ => throw new NotSupportedException($"Unsupported D3DTX format 0x{format:X}."),
        };

        return new D3dtxInfo(width, height, mipCount, format, blockBytes, pixelStart, pixelSize, alphaMode);

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

        int? ReadAlphaMode()
        {
            var saved = p;
            try
            {
                if (p + 32 > pixelStart)
                {
                    return null;
                }

                p += 4 * 4; // layout, gamma, resource usage, type
                p += 4; // normal map format
                p += 4 * 3; // specular exponent, HDR lightmap scale, toon gradient cutoff
                var value = ReadI32();
                return value is 0 or 1 or 2 ? value : null;
            }
            finally
            {
                p = saved;
            }
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

    private static int[] DecodeDxt3(byte[] data, int offset, int width, int height)
    {
        var pixels = new int[width * height];
        var pos = offset;
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                ulong alphaBits = 0;
                for (var i = 0; i < 8; i++)
                {
                    alphaBits |= (ulong)data[pos + i] << (8 * i);
                }

                var c0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 8));
                var c1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 10));
                var colorBits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12));
                pos += 16;

                var colors = BuildDxt5Colors(c0, c1);
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
                        var alpha = (int)((alphaBits >> (4 * pixel)) & 0xF) * 17;
                        var color = Color.FromArgb(colors[colorIndex]);
                        pixels[ty * width + tx] = Color.FromArgb(alpha, color.R, color.G, color.B).ToArgb();
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

    private static int LegacyMipChainSize(int width, int height, int blockBytes, int mipCount)
    {
        var total = 0;
        for (var mip = 0; mip < mipCount; mip++)
        {
            total = checked(total + MipSizeOf(width, height, blockBytes));
            if (width == 1 && height == 1)
            {
                break;
            }

            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return total;
    }

    private static bool IsLegacyErtm(byte[] data)
        => data.Length >= 4 &&
           data[0] == (byte)'E' &&
           data[1] == (byte)'R' &&
           data[2] == (byte)'T' &&
           data[3] == (byte)'M';

    private static int IndexOf(byte[] data, string value)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(value);
        for (var i = 0; i <= data.Length - needle.Length; i++)
        {
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static string D3dtxFormatName(uint format) => format switch
    {
        0x0 => "ARGB8",
        0xA => "RGBA8",
        0x10 => "A8",
        0x15 => "A8R8G8B8",
        0x16 => "X8R8G8B8",
        0x17 => "A8B8G8R8",
        0x50 => "L8",
        0x51 => "A8L8",
        0x40 => "BC1/DXT1",
        0x41 => "BC2/DXT3",
        0x42 => "BC3/DXT5",
        0x43 => "BC4",
        0x44 => "BC5",
        0x45 => "CTX1",
        0x46 => "BC6",
        0x47 => "BC7",
        _ => $"0x{format:X}",
    };

    private readonly record struct D3dtxInfo(
        int Width,
        int Height,
        int MipCount,
        uint Format,
        int BlockBytes,
        int PixelStart,
        int PixelLength,
        int? AlphaMode = null);
}

public sealed record TextureFormatInfo(
    string Container,
    string FormatName,
    uint FormatValue,
    string GammaName,
    int Width,
    int Height,
    int RegionCount,
    IReadOnlyList<TextureRegionInfo> Regions);

public sealed record TextureRegionInfo(
    int FaceIndex,
    int MipIndex,
    int MipCount,
    int DataSize,
    int Pitch,
    int SlicePitch,
    int Width,
    int Height);
