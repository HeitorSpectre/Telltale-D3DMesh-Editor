using System.Buffers.Binary;
using System.Drawing;
using System.Text;

namespace TelltaleD3DMeshEditor.Reinsert;

public static class D3dtxWriter
{
    private const uint Argb8Format = 0x0;
    private const uint Rgba8Format = 0xA;
    private const uint A8Format = 0x10;
    private const uint Dxt1Format = 0x40;
    private const uint Dxt5Format = 0x42;

    public static void WriteFromImageBytes(byte[] templateBytes, GltfImage image, string outputPath, bool forceUncompressed = false)
    {
        var pixels = DecodeImage(image.Data, out var width, out var height);
        var encoded = BuildFromTemplate(templateBytes, pixels, width, height, Path.GetFileName(outputPath), forceUncompressed);
        File.WriteAllBytes(outputPath, encoded);
    }

    public static void WriteRenamedCopy(byte[] sourceBytes, string outputPath)
    {
        // For unmodified textures from this tool's own GLB export, keep the original D3DTX
        // byte-for-byte. The D3DMesh texture table points at the new filename hash, while the
        // D3DTX payload/metadata stays exactly as the game authored it.
        File.WriteAllBytes(outputPath, sourceBytes);
    }

    private static byte[] BuildFromTemplate(byte[] template, int[] pixels, int width, int height, string textureFileName, bool forceUncompressed = false)
    {
        var tex = TtgTexture.Parse(template);
        var hasAlpha = HasMeaningfulAlpha(pixels);
        // When uncompressed output is requested, keep an already-uncompressed template format as-is
        // (ARGB8/RGBA8/A8) and convert any block-compressed template to ARGB8. Otherwise fall back to
        // the normal codec choice that mirrors the template (DXT1 without alpha, DXT5 with alpha).
        var format = forceUncompressed
            ? tex.TextureFormat switch
            {
                Argb8Format or Rgba8Format or A8Format => tex.TextureFormat,
                _ => Argb8Format,
            }
            : tex.TextureFormat switch
            {
                Argb8Format or Rgba8Format or A8Format => tex.TextureFormat,
                Dxt5Format => Dxt5Format,
                Dxt1Format => hasAlpha ? Dxt5Format : Dxt1Format,
                _ => hasAlpha ? Dxt5Format : Dxt1Format,
            };
        var mipCount = Math.Min(CountTtgMips(width, height, format), Math.Max(1, tex.Mip));
        // The mip payloads must be encoded with the same codec that the header declares.
        // When the template is DXT5 we keep DXT5 even for alpha-less images, otherwise the
        // header (16 bytes/block) would disagree with a DXT1 payload (8 bytes/block) and the
        // texture would read as garbage / fail to decode ("payload smaller than largest mip").
        var mipPayloads = BuildMipPayloads(pixels, width, height, mipCount, format);

        tex.ObjectName = NormalizeD3dtxName(textureFileName);
        tex.Width = width;
        tex.Height = height;
        tex.Mip = mipPayloads.Count;
        tex.TextureFormat = format;
        tex.TexMipCount = mipPayloads.Count;
        tex.TexSize = (uint)mipPayloads.Sum(mip => mip.Length);
        tex.Mips = BuildTtgMipRecords(tex, mipPayloads);

        return tex.Write();
    }

    private static List<byte[]> BuildMipPayloads(int[] pixels, int width, int height, int mipCount, uint format)
    {
        var mipPayloads = new List<byte[]>();
        var current = pixels;
        var currentWidth = width;
        var currentHeight = height;
        for (var mip = 0; mip < mipCount; mip++)
        {
            mipPayloads.Add(format switch
            {
                Argb8Format => EncodeBgra32(current, currentWidth, currentHeight),
                Rgba8Format => EncodeRgba32(current, currentWidth, currentHeight),
                A8Format => EncodeA8(current, currentWidth, currentHeight),
                Dxt5Format => EncodeDxt5(current, currentWidth, currentHeight),
                _ => EncodeDxt1(current, currentWidth, currentHeight),
            });
            if (mip == mipCount - 1 || (currentWidth == 1 && currentHeight == 1))
            {
                break;
            }

            current = Downsample(current, currentWidth, currentHeight, out currentWidth, out currentHeight);
        }

        return mipPayloads;
    }

    private static byte[] EncodeBgra32(int[] pixels, int width, int height)
    {
        var data = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var color = Color.FromArgb(pixels[i]);
            var p = i * 4;
            data[p] = color.B;
            data[p + 1] = color.G;
            data[p + 2] = color.R;
            data[p + 3] = color.A;
        }

        return data;
    }

    private static byte[] EncodeRgba32(int[] pixels, int width, int height)
    {
        var data = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var color = Color.FromArgb(pixels[i]);
            var p = i * 4;
            data[p] = color.R;
            data[p + 1] = color.G;
            data[p + 2] = color.B;
            data[p + 3] = color.A;
        }

        return data;
    }

    private static byte[] EncodeA8(int[] pixels, int width, int height)
    {
        var useAlpha = HasMeaningfulAlpha(pixels);
        var data = new byte[checked(width * height)];
        for (var i = 0; i < data.Length; i++)
        {
            var color = Color.FromArgb(pixels[i]);
            data[i] = useAlpha
                ? color.A
                : (byte)((color.R * 30 + color.G * 59 + color.B * 11) / 100);
        }

        return data;
    }

    private static bool HasMeaningfulAlpha(int[] pixels)
    {
        var alphaPixels = 0;
        foreach (var pixel in pixels)
        {
            if (Color.FromArgb(pixel).A < 250)
            {
                alphaPixels++;
            }
        }

        return alphaPixels > Math.Max(16, pixels.Length / 1000);
    }

    private static TtgMipRecord[] BuildTtgMipRecords(TtgTexture tex, IReadOnlyList<byte[]> mipPayloads)
    {
        var records = new TtgMipRecord[mipPayloads.Count];
        var w = tex.Width;
        var h = tex.Height;
        for (var i = 0; i < mipPayloads.Count; i++)
        {
            GetSizeAndPitch(w, h, (int)tex.TextureFormat, out var mipSize, out var blockSize);
            records[i] = new TtgMipRecord
            {
                SubTexNum = i < tex.Mips.Length ? tex.Mips[i].SubTexNum : 0,
                CurrentMip = i,
                One = i < tex.Mips.Length ? tex.Mips[i].One : 1,
                MipSize = mipSize > 0 ? mipSize : mipPayloads[i].Length,
                BlockSize = blockSize,
                Block = mipPayloads[i],
            };

            if (records[i].MipSize != records[i].Block.Length)
            {
                records[i].MipSize = records[i].Block.Length;
            }

            if (w > 1)
            {
                w /= 2;
            }

            if (h > 1)
            {
                h /= 2;
            }
        }

        return records;
    }

    private static int[] DecodeImage(byte[] data, out int width, out int height)
    {
        using var stream = new MemoryStream(data);
        using var source = new Bitmap(stream);
        width = source.Width;
        height = source.Height;
        var pixels = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = source.GetPixel(x, y).ToArgb();
            }
        }

        return pixels;
    }

    private static byte[] EncodeDxt5(int[] pixels, int width, int height)
    {
        using var ms = new MemoryStream(MipSizeOf(width, height, 16));
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                WriteDxt5Block(ms, pixels, width, height, bx, by);
            }
        }

        return ms.ToArray();
    }

    private static byte[] EncodeDxt1(int[] pixels, int width, int height)
    {
        using var ms = new MemoryStream(MipSizeOf(width, height, 8));
        var blockColors = new Color[16];
        var colorBytes = new byte[8];
        for (var by = 0; by < height; by += 4)
        {
            for (var bx = 0; bx < width; bx += 4)
            {
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var tx = Math.Min(width - 1, bx + x);
                        var ty = Math.Min(height - 1, by + y);
                        blockColors[y * 4 + x] = Color.FromArgb(pixels[ty * width + tx]);
                    }
                }

                var (c0, c1) = ChooseColorEndpoints(blockColors);
                BinaryPrimitives.WriteUInt16LittleEndian(colorBytes, c0);
                BinaryPrimitives.WriteUInt16LittleEndian(colorBytes.AsSpan(2), c1);
                var palette = BuildColorPalette(c0, c1);
                uint colorBits = 0;
                for (var i = 0; i < 16; i++)
                {
                    colorBits |= (uint)NearestColor(palette, blockColors[i]) << (i * 2);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(colorBytes.AsSpan(4), colorBits);
                ms.Write(colorBytes);
            }
        }

        return ms.ToArray();
    }

    private static void WriteDxt5Block(Stream stream, int[] pixels, int width, int height, int bx, int by)
    {
        Span<byte> alphas = stackalloc byte[16];
        var colors = new Color[16];
        var minA = 255;
        var maxA = 0;
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var tx = Math.Min(width - 1, bx + x);
                var ty = Math.Min(height - 1, by + y);
                var color = Color.FromArgb(pixels[ty * width + tx]);
                var i = y * 4 + x;
                colors[i] = color;
                alphas[i] = color.A;
                minA = Math.Min(minA, color.A);
                maxA = Math.Max(maxA, color.A);
            }
        }

        stream.WriteByte((byte)maxA);
        stream.WriteByte((byte)minA);
        var alphaPalette = BuildAlphaPalette((byte)maxA, (byte)minA);
        ulong alphaBits = 0;
        for (var i = 0; i < 16; i++)
        {
            alphaBits |= (ulong)NearestAlpha(alphaPalette, alphas[i]) << (i * 3);
        }
        for (var i = 0; i < 6; i++)
        {
            stream.WriteByte((byte)((alphaBits >> (i * 8)) & 0xFF));
        }

        var (c0, c1) = ChooseColorEndpoints(colors);
        Span<byte> colorBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(colorBytes, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(colorBytes[2..], c1);
        var palette = BuildColorPalette(c0, c1);
        uint colorBits = 0;
        for (var i = 0; i < 16; i++)
        {
            colorBits |= (uint)NearestColor(palette, colors[i]) << (i * 2);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(colorBytes[4..], colorBits);
        stream.Write(colorBytes);
    }

    private static byte[] BuildAlphaPalette(byte a0, byte a1)
    {
        var result = new byte[8];
        result[0] = a0;
        result[1] = a1;
        for (var i = 1; i <= 6; i++)
        {
            result[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }

        return result;
    }

    private static int NearestAlpha(byte[] palette, byte value)
    {
        var best = 0;
        var bestDist = int.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            var dist = Math.Abs(palette[i] - value);
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }

        return best;
    }

    private static (ushort C0, ushort C1) ChooseColorEndpoints(Color[] colors)
    {
        var min = colors[0];
        var max = colors[0];
        var minLum = Luminance(min);
        var maxLum = minLum;
        foreach (var color in colors)
        {
            var lum = Luminance(color);
            if (lum < minLum)
            {
                min = color;
                minLum = lum;
            }
            if (lum > maxLum)
            {
                max = color;
                maxLum = lum;
            }
        }

        var c0 = ToRgb565(max);
        var c1 = ToRgb565(min);
        if (c0 == c1)
        {
            c1 = c0 == 0 ? (ushort)1 : (ushort)(c0 - 1);
        }

        return c0 > c1 ? (c0, c1) : (c1, c0);
    }

    private static int Luminance(Color color) => color.R * 30 + color.G * 59 + color.B * 11;

    private static ushort ToRgb565(Color color)
    {
        var r = color.R * 31 / 255;
        var g = color.G * 63 / 255;
        var b = color.B * 31 / 255;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static Color[] BuildColorPalette(ushort c0, ushort c1)
    {
        var a = FromRgb565(c0);
        var b = FromRgb565(c1);
        return
        [
            a,
            b,
            Color.FromArgb(255, (2 * a.R + b.R) / 3, (2 * a.G + b.G) / 3, (2 * a.B + b.B) / 3),
            Color.FromArgb(255, (a.R + 2 * b.R) / 3, (a.G + 2 * b.G) / 3, (a.B + 2 * b.B) / 3),
        ];
    }

    private static Color FromRgb565(ushort value)
    {
        var r = (value >> 11) & 0x1F;
        var g = (value >> 5) & 0x3F;
        var b = value & 0x1F;
        return Color.FromArgb(255, r * 255 / 31, g * 255 / 63, b * 255 / 31);
    }

    private static int NearestColor(Color[] palette, Color color)
    {
        var best = 0;
        var bestDist = int.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            var p = palette[i];
            var dr = p.R - color.R;
            var dg = p.G - color.G;
            var db = p.B - color.B;
            var dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }

        return best;
    }

    private static int[] Downsample(int[] src, int width, int height, out int outWidth, out int outHeight)
    {
        outWidth = Math.Max(1, width / 2);
        outHeight = Math.Max(1, height / 2);
        var dst = new int[outWidth * outHeight];
        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                dst[y * outWidth + x] = Average2x2(src, width, height, x * 2, y * 2);
            }
        }

        return dst;
    }

    private static int Average2x2(int[] src, int width, int height, int sx, int sy)
    {
        var a = 0;
        var r = 0;
        var g = 0;
        var b = 0;
        var count = 0;
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var tx = Math.Min(width - 1, sx + x);
                var ty = Math.Min(height - 1, sy + y);
                var c = Color.FromArgb(src[ty * width + tx]);
                a += c.A; r += c.R; g += c.G; b += c.B;
                count++;
            }
        }

        return Color.FromArgb(a / count, r / count, g / count, b / count).ToArgb();
    }

    private static int CountBlockMips(int width, int height)
    {
        var count = 1;
        while (width > 4 || height > 4)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }

        return count;
    }

    private static int CountFullMips(int width, int height)
    {
        var count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }

        return count;
    }

    private static int CountTtgMips(int width, int height, uint format)
        => IsBlockCompressedFormat(format) ? CountBlockMips(width, height) : CountFullMips(width, height);

    private static int MipSizeOf(int width, int height, int blockBytes) =>
        Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;

    private static bool IsBlockCompressedFormat(uint format)
        => format is Dxt1Format or Dxt5Format or 0x41 or 0x43 or 0x44 or 0x45 or 0x46 or 0x47;

    private static string NormalizeD3dtxName(string textureFileName) =>
        textureFileName.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
            ? textureFileName
            : textureFileName + ".d3dtx";

    private static void GetSizeAndPitch(int width, int height, int format, out int mipSize, out int pitch)
    {
        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        switch (format)
        {
            case 0x0:
            case 0xA:
                mipSize = checked(w * h * 4);
                pitch = checked(w * 4);
                break;
            case 0x10:
                mipSize = checked(w * h);
                pitch = w;
                break;
            case 0x40:
            case 0x43:
            case 0x70:
                mipSize = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;
                pitch = Math.Max(1, (w + 3) / 4) * 8;
                break;
            case 0x42:
            case 0x44:
                mipSize = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16;
                pitch = Math.Max(1, (w + 3) / 4) * 16;
                break;
            default:
                mipSize = 0;
                pitch = 0;
                break;
        }
    }

    private sealed class TtgTexture
    {
        public required string CheckHeader { get; init; }
        public required byte[] OuterHeader { get; init; }
        public int SomeValue { get; set; }
        public int UnknownFlagsBlockSize { get; set; }
        public uint UnknownFlagsBlock { get; set; }
        public int PlatformBlockSize { get; set; }
        public uint Platform { get; set; }
        public string ObjectName { get; set; } = "";
        public string SubObjectName { get; set; } = "";
        public float OneValue { get; set; }
        public int Zero { get; set; } = 48;
        public byte OneByte { get; set; }
        public UnknownDataBlock? UnknownData { get; set; }
        public int Mip { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Faces { get; set; }
        public int ArrayMembers { get; set; }
        public uint TextureFormat { get; set; }
        public int Unknown1 { get; set; }
        public byte[] Block { get; set; } = [];
        public byte[] SubBlock2 { get; set; } = [];
        public byte[] SubBlock { get; set; } = [];
        public int TexMipCount { get; set; }
        public int TexSomeData { get; set; }
        public uint TexSize { get; set; }
        public TtgMipRecord[] Mips { get; set; } = [];
        public byte[][] TexSubBlocks { get; set; } = [];
        public bool HasOneValueTex { get; set; }

        public static TtgTexture Parse(byte[] data)
        {
            if (data.Length < 32)
            {
                throw new InvalidDataException("D3DTX file is too small.");
            }

            var checkHeader = Encoding.ASCII.GetString(data, 0, 4);
            if (checkHeader is not ("5VSM" or "6VSM" or "ERTM"))
            {
                throw new InvalidDataException($"Unsupported D3DTX header: {checkHeader}");
            }

            var versionCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));
            var metaStart = 20 + versionCount * 12;
            if (metaStart <= 0 || metaStart >= data.Length)
            {
                throw new InvalidDataException("Invalid MetaStream header.");
            }

            var reader = new TtgReader(data, metaStart);
            var tex = new TtgTexture
            {
                CheckHeader = checkHeader,
                OuterHeader = data[..metaStart],
                SomeValue = reader.ReadInt32(),
                UnknownFlagsBlockSize = reader.ReadInt32(),
                UnknownFlagsBlock = reader.ReadUInt32(),
                PlatformBlockSize = reader.ReadInt32(),
                Platform = reader.ReadUInt32(),
            };

            tex.ObjectName = reader.ReadNameBlock();
            tex.SubObjectName = reader.ReadNameBlock();
            tex.OneValue = reader.ReadSingle();

            if (checkHeader == "5VSM" && tex.SomeValue > 4)
            {
                tex.Zero = 48;
                var marker = reader.PeekByte();
                if (marker != 0x30 && marker != 0x31)
                {
                    tex.Zero = reader.ReadInt32();
                }
            }

            tex.OneByte = reader.ReadByte();
            if (tex.OneByte == 0x31)
            {
                tex.UnknownData = new UnknownDataBlock
                {
                    Count = reader.ReadInt32(),
                    Unknown1 = reader.ReadInt32(),
                    Data = reader.ReadLengthPrefixedRaw(),
                };
            }

            tex.Mip = reader.ReadInt32();
            tex.Width = reader.ReadInt32();
            tex.Height = reader.ReadInt32();

            if ((checkHeader == "5VSM" || checkHeader == "6VSM") && tex.SomeValue >= 8)
            {
                tex.Faces = reader.ReadInt32();
                tex.ArrayMembers = reader.ReadInt32();
            }

            tex.TextureFormat = reader.ReadUInt32();
            tex.Unknown1 = reader.ReadInt32();
            tex.Block = reader.ReadBytes(GetMainBlockSize(tex.SomeValue, checkHeader));

            if (tex.SomeValue >= 8)
            {
                tex.SubBlock2 = reader.ReadInclusiveBlock();
            }

            tex.SubBlock = reader.ReadInclusiveBlock();
            tex.TexMipCount = reader.ReadInt32();
            tex.TexSomeData = reader.ReadInt32();
            tex.TexSize = reader.ReadUInt32();
            tex.Mips = new TtgMipRecord[Math.Max(0, tex.TexMipCount)];

            if (tex.Platform == 15)
            {
                for (var i = 0; i < tex.Mips.Length; i++)
                {
                    tex.Mips[i] = tex.ReadMipHeader(reader);
                }
            }
            else
            {
                for (var i = tex.Mips.Length - 1; i >= 0; i--)
                {
                    tex.Mips[i] = tex.ReadMipHeader(reader);
                }
            }

            tex.TexSubBlocks = new byte[Math.Max(0, tex.TexSomeData)][];
            for (var i = 0; i < tex.TexSubBlocks.Length; i++)
            {
                tex.TexSubBlocks[i] = reader.ReadSizedPayloadBlock();
            }

            return tex;
        }

        public byte[] Write()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
            writer.Write(OuterHeader);
            var metadataStart = ms.Position;

            writer.Write(SomeValue);
            writer.Write(UnknownFlagsBlockSize);
            writer.Write(UnknownFlagsBlock);
            writer.Write(PlatformBlockSize);
            writer.Write(Platform);
            WriteNameBlock(writer, ObjectName);
            WriteNameBlock(writer, SubObjectName);
            writer.Write(OneValue);

            if (CheckHeader == "5VSM" && SomeValue > 4 && Zero != 48)
            {
                writer.Write(Zero);
            }

            writer.Write(OneByte);
            if (OneByte == 0x31 && UnknownData is not null)
            {
                writer.Write(UnknownData.Count);
                writer.Write(UnknownData.Unknown1);
                writer.Write(UnknownData.Data);
            }

            writer.Write(Mip);
            writer.Write(Width);
            writer.Write(Height);
            if ((CheckHeader == "5VSM" || CheckHeader == "6VSM") && SomeValue >= 8)
            {
                writer.Write(Faces);
                writer.Write(ArrayMembers);
            }

            writer.Write(TextureFormat);
            writer.Write(Unknown1);
            writer.Write(Block);
            if (SomeValue >= 8)
            {
                writer.Write(SubBlock2);
            }

            writer.Write(SubBlock);
            writer.Write(TexMipCount);
            writer.Write(TexSomeData);
            writer.Write(TexSize);

            if (Platform == 15)
            {
                for (var i = 0; i < Mips.Length; i++)
                {
                    WriteMipHeader(writer, Mips[i]);
                }
            }
            else
            {
                for (var i = Mips.Length - 1; i >= 0; i--)
                {
                    WriteMipHeader(writer, Mips[i]);
                }
            }

            foreach (var subBlock in TexSubBlocks)
            {
                writer.Write(subBlock.Length);
                writer.Write(subBlock);
            }

            var headerSize = (uint)(ms.Position - metadataStart);
            uint textureSize = 0;
            if (Platform == 15)
            {
                foreach (var mip in Mips)
                {
                    writer.Write(mip.Block);
                    textureSize += (uint)mip.Block.Length;
                }
            }
            else
            {
                for (var i = Mips.Length - 1; i >= 0; i--)
                {
                    writer.Write(Mips[i].Block);
                    textureSize += (uint)Mips[i].Block.Length;
                }
            }

            writer.Flush();
            var result = ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), headerSize);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), textureSize);
            return result;
        }

        private TtgMipRecord ReadMipHeader(TtgReader reader)
        {
            var record = new TtgMipRecord();
            if (SomeValue >= 5)
            {
                record.SubTexNum = reader.ReadInt32();
            }

            record.CurrentMip = reader.ReadInt32();
            var next = reader.ReadInt32();
            if (next == 1 || Platform == 4)
            {
                record.One = next;
                HasOneValueTex = true;
                next = reader.ReadInt32();
            }
            else
            {
                record.One = 1;
            }

            record.MipSize = next;
            record.BlockSize = reader.ReadInt32();

            if ((CheckHeader == "5VSM" || CheckHeader == "6VSM") && SomeValue >= 8)
            {
                _ = reader.ReadInt32();
            }

            return record;
        }

        private void WriteMipHeader(BinaryWriter writer, TtgMipRecord record)
        {
            if (SomeValue >= 5)
            {
                writer.Write(record.SubTexNum);
            }

            writer.Write(record.CurrentMip);
            if (HasOneValueTex)
            {
                writer.Write(record.One);
            }

            writer.Write(record.MipSize);
            writer.Write(record.BlockSize);
            if ((CheckHeader == "5VSM" || CheckHeader == "6VSM") && SomeValue >= 8)
            {
                writer.Write(record.MipSize);
            }
        }

        private static int GetMainBlockSize(int someValue, string checkHeader) =>
            someValue switch
            {
                3 => checkHeader == "ERTM" ? 0x24 : 0x28,
                4 => 0x28,
                5 or 7 => 0x34,
                8 or 9 => 0x38,
                _ => 0x24,
            };

        private static void WriteNameBlock(BinaryWriter writer, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write(bytes.Length + 8);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }

    private sealed class TtgReader(byte[] data, int position)
    {
        private int _position = position;

        public byte PeekByte()
        {
            Ensure(1);
            return data[_position];
        }

        public byte ReadByte()
        {
            Ensure(1);
            return data[_position++];
        }

        public int ReadInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public float ReadSingle()
        {
            Ensure(4);
            var value = BitConverter.ToSingle(data, _position);
            _position += 4;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw new InvalidDataException("Negative size in D3DTX.");
            }

            Ensure(count);
            var bytes = data.AsSpan(_position, count).ToArray();
            _position += count;
            return bytes;
        }

        public string ReadNameBlock()
        {
            var total = ReadInt32();
            var length = ReadInt32();
            if (length < 0 || total < length + 8)
            {
                throw new InvalidDataException("Invalid D3DTX name block.");
            }

            var text = Encoding.ASCII.GetString(ReadBytes(length));
            var padding = total - length - 8;
            if (padding > 0)
            {
                _ = ReadBytes(padding);
            }

            return text;
        }

        public byte[] ReadLengthPrefixedRaw()
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position, 4));
            return ReadBytes(length);
        }

        public byte[] ReadInclusiveBlock()
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position, 4));
            return ReadBytes(length);
        }

        public byte[] ReadSizedPayloadBlock()
        {
            var length = ReadInt32();
            return ReadBytes(length);
        }

        private void Ensure(int count)
        {
            if (_position < 0 || count < 0 || _position + count > data.Length)
            {
                throw new InvalidDataException("D3DTX is truncated or has invalid offsets.");
            }
        }
    }

    private sealed class UnknownDataBlock
    {
        public int Count { get; init; }
        public int Unknown1 { get; init; }
        public byte[] Data { get; init; } = [];
    }

    private sealed class TtgMipRecord
    {
        public int SubTexNum { get; set; }
        public int CurrentMip { get; set; }
        public int One { get; set; } = 1;
        public int MipSize { get; set; }
        public int BlockSize { get; set; }
        public byte[] Block { get; set; } = [];
    }
}
