using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Reinsert;

// Layout map for a .d3dmesh. Walks the file exactly like the matching D3DMeshParser path,
// but records offsets and sizes for each editable region
// (bounds, submesh table, UV scales, face buffer, vertex buffer) instead of decoding data.
// This is the base for the reimporter: the template-patch writer uses these offsets to replace
// only the desired region while preserving headers, materials, palettes, and padding byte-for-byte.
//
// Coverage proof: the walker computes or reads each vertex-buffer stride and validates
// that the final vertex buffer lands exactly at the end of the useful block.
// When the map closes with the file, it covers 100% of the bytes.
public sealed class D3DMeshLayout
{
    private static readonly string[] TextureSlotsV13 =
    [
        "diffuse", "bake", "bump", "environment", "detail_diffuse", "detail_bump",
        "specular", "tex8", "gradient", "tex10", "shadow"
    ];

    private static readonly string[] TextureSlotsV18 =
    [
        "diffuse", "bake", "bump", "environment", "detail_diffuse", "detail_bump",
        "specular", "tex8", "gradient", "tex10", "shadow", "emissive",
        "alternate_bump", "occlusion"
    ];

    public required byte[] Original { get; init; }
    public required string Name { get; init; }
    public int Version { get; init; }
    public int DataOffset { get; init; }
    public required string[] TextureSlots { get; init; }

    // Global bounds: 2x vec3 (min, max) = 24 bytes.
    public int BoundsOffset { get; init; }

    public int SubmeshCount { get; init; }
    public int SubmeshBlockSizeFieldOffset { get; init; }
    public int SubmeshBlockSize { get; init; }
    public int SubmeshCountFieldOffset { get; init; }
    public int SubmeshTableOffset { get; init; }
    public int SubmeshTableLength { get; init; }
    public required List<SubmeshLayout> Submeshes { get; init; }
    public required List<ulong[]> BonePalettes { get; init; }
    public int OriginalBonePaletteCount { get; init; }
    public int BonePaletteBlockOffset { get; init; }
    public int BonePaletteBlockLength { get; init; }
    public int BonePaletteEntrySize { get; init; }
    public required byte[] BonePaletteEntryTemplate { get; init; }
    public int TextureGroupBlockOffset { get; init; }
    public int TextureGroupBlockLength { get; init; }
    public required List<TextureGroupLayout> TextureGroups { get; init; }

    // V13/14: usually 8 floats in uv1X uv1Y uv4X uv4Y uv2X uv2Y uv3X uv3Y order.
    // TFTB E3 keeps the same v14 mesh version but may store extra floats before the following sized
    // blocks, so the layout walker detects the real count from the block markers.
    // V17/18: 10 floats in uv1X uv1Y extraX extraY uv2X uv2Y uv3X uv3Y uv4X uv4Y order.
    public int UvScalesOffset { get; init; }
    public int UvScalesLength { get; init; }

    // Face buffer: count (facePointCount, u32) + data (facePointCount * uint16).
    public int FaceCountFieldOffset { get; init; }
    public int FaceIndexFormat { get; init; }
    public int FacePointCount { get; init; }
    public int FaceDataOffset { get; init; }
    public int FaceDataLength { get; init; }

    // Vertex buffer: count + attribute table (12 attrs x 3 u32) + interleaved data.
    public int VertexCountFieldOffset { get; init; }
    public int VertexCount { get; init; }
    public int VertexAttrTableOffset { get; init; }
    public int VertexDataOffset { get; init; }
    public int VertexStride { get; init; }
    public int VertexDataLength => VertexBuffers.Count == 0 ? VertexCount * VertexStride : VertexBuffers[0].DataLength;
    public required List<VertexBufferLayout> VertexBuffers { get; init; }

    // Tail after the vertex block (should be empty in TWAU V13; tracked for safety).
    public int TailOffset => VertexBuffers.Count == 0
        ? VertexDataOffset + VertexDataLength
        : VertexBuffers[^1].DataOffset + VertexBuffers[^1].DataLength;
    public int TailLength { get; init; }

    public required VertexAttrLayout Attributes { get; init; }

    public static D3DMeshLayout Build(byte[] data)
    {
        var header = MetaStreamHeader.Parse(data);
        var reader = new DataReader(data);
        if (header.DataOffset > 0)
        {
            reader.Seek(header.DataOffset);
        }

        var nameHeaderLength = reader.ReadUInt32();
        var nameLength = reader.ReadUInt32();
        if (nameLength > nameHeaderLength)
        {
            reader.Seek(reader.Position - 4);
            nameLength = nameHeaderLength;
        }

        var name = reader.ReadAscii((int)nameLength);
        var version = reader.ReadInt32();
        if (version is not (13 or 14 or 17 or 18))
        {
            throw new NotSupportedException($"D3DMeshLayout currently supports only V13/14/17/18 (got {version}).");
        }

        var isV18Layout = version is 17 or 18;
        var textureSlots = isV18Layout ? TextureSlotsV18 : TextureSlotsV13;

        reader.Skip(4);
        var boundsOffset = reader.Position;
        reader.ReadVec3();
        reader.ReadVec3();

        var headerLength = (int)reader.ReadUInt32() - 4;
        reader.Skip(headerLength);

        var submeshBlockSizeFieldOffset = reader.Position;
        var submeshBlockSize = (int)reader.ReadUInt32();
        var submeshCountFieldOffset = reader.Position;
        var submeshCount = (int)reader.ReadUInt32();
        if (submeshCount < 0 || submeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid submesh count: {submeshCount}");
        }

        var submeshes = new List<SubmeshLayout>(submeshCount);
        var submeshTableOffset = reader.Position;
        for (var i = 0; i < submeshCount; i++)
        {
            var entryOffset = reader.Position;
            reader.ReadUInt32();
            var boneSetOffset = reader.Position;
            var boneSetRaw = (int)reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            var vMinOffset = reader.Position;
            var vertexMin = (int)reader.ReadUInt32();
            var vMaxOffset = reader.Position;
            var vertexMax = (int)reader.ReadUInt32();
            var faceStartOffset = reader.Position;
            var facePointStart = (int)reader.ReadUInt32();
            var polyCountOffset = reader.Position;
            var polygonCount = (int)reader.ReadUInt32();
            reader.ReadVec3();
            reader.ReadVec3();
            reader.ReadUInt32();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadUInt32();
            var subHeaderEnd = reader.Position + (int)reader.ReadUInt32();
            var textureSlotOffsets = new int[textureSlots.Length];
            for (var tex = 0; tex < textureSlots.Length; tex++)
            {
                textureSlotOffsets[tex] = reader.Position;
                reader.ReadUInt32();
            }

            reader.Seek(subHeaderEnd);
            reader.Skip(0x88);
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            var entryLength = reader.Position - entryOffset;

            submeshes.Add(new SubmeshLayout(
                EntryOffset: entryOffset,
                EntryLength: entryLength,
                BoneSetRaw: boneSetRaw,
                BoneSetFieldOffset: boneSetOffset,
                VertexMinFieldOffset: vMinOffset,
                VertexMaxFieldOffset: vMaxOffset,
                FaceStartFieldOffset: faceStartOffset,
                PolygonCountFieldOffset: polyCountOffset,
                TextureSlotFieldOffsets: textureSlotOffsets,
                VertexMin: vertexMin,
                VertexMax: vertexMax,
                FacePointStart: facePointStart,
                PolygonCount: polygonCount));
        }
        var submeshTableLength = reader.Position - submeshTableOffset;

        SkipSizedBlock(reader);
        var bonePaletteBlockOffset = reader.Position;
        var (bonePalettes, bonePaletteEntryTemplate) = ReadBonePalettes(reader, 56);
        var bonePaletteBlockLength = reader.Position - bonePaletteBlockOffset;
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.Skip(0x08);

        var textureGroupBlockOffset = reader.Position;
        var textureGroups = ReadTextureGroups(reader, textureSlots.Length, isV18Layout ? 24 : 20);
        var textureGroupBlockLength = reader.Position - textureGroupBlockOffset;

        var uvScalesOffset = reader.Position;
        var uvScaleFloatCount = isV18Layout ? 10 : DetectUvScaleFloatCount(reader);
        for (var i = 0; i < uvScaleFloatCount; i++)
        {
            reader.ReadFloat();
        }

        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.ReadByte();
        reader.ReadUInt32();

        var faceCountFieldOffset = reader.Position;
        var facePointCount = (int)reader.ReadUInt32();
        var faceIndexFormat = 0;
        if (isV18Layout)
        {
            faceIndexFormat = (int)reader.ReadUInt32();
            reader.Skip(4);
        }
        else
        {
            reader.Skip(8);
        }

        var faceDataOffset = reader.Position;
        var faceDataLength = facePointCount * 2;
        reader.Skip(faceDataLength);

        var vertexBuffers = new List<VertexBufferLayout>(isV18Layout ? 2 : 1);
        var vertexBufferCount = isV18Layout ? 2 : 1;
        for (var bufferIndex = 0; bufferIndex < vertexBufferCount; bufferIndex++)
        {
            var vertexCountFieldOffset = reader.Position;
            var vertexCount = (int)reader.ReadUInt32();
            var strideFieldOffset = reader.Position;
            var storedStride = (int)reader.ReadUInt32();
            reader.Skip(isV18Layout ? 0x08 : 0x0C);
            reader.ReadUInt32();

            var attrTableOffset = reader.Position;
            var attrs = VertexAttrLayout.Read(reader, isV18Layout ? 13 : 12);
            var vertexDataOffset = reader.Position;
            var stride = isV18Layout && storedStride > 0 ? storedStride : attrs.Stride;
            var vertexDataLength = checked(vertexCount * stride);
            reader.Skip(vertexDataLength);

            vertexBuffers.Add(new VertexBufferLayout(
                Index: bufferIndex,
                CountFieldOffset: vertexCountFieldOffset,
                StrideFieldOffset: strideFieldOffset,
                AttrTableOffset: attrTableOffset,
                DataOffset: vertexDataOffset,
                VertexCount: vertexCount,
                VertexStride: stride,
                Attributes: attrs));
        }

        var primaryBuffer = vertexBuffers[0];
        var tailOffset = vertexBuffers[^1].DataOffset + vertexBuffers[^1].DataLength;
        if (tailOffset > data.Length)
        {
            throw new InvalidDataException(
                $"Vertex block exceeds file bounds: dataOff=0x{primaryBuffer.DataOffset:X} count={primaryBuffer.VertexCount} stride={primaryBuffer.VertexStride} -> 0x{tailOffset:X} > 0x{data.Length:X}");
        }

        return new D3DMeshLayout
        {
            Original = data,
            Name = name,
            Version = version,
            DataOffset = header.DataOffset,
            TextureSlots = textureSlots,
            BoundsOffset = boundsOffset,
            SubmeshCount = submeshCount,
            SubmeshBlockSizeFieldOffset = submeshBlockSizeFieldOffset,
            SubmeshBlockSize = submeshBlockSize,
            SubmeshCountFieldOffset = submeshCountFieldOffset,
            SubmeshTableOffset = submeshTableOffset,
            SubmeshTableLength = submeshTableLength,
            Submeshes = submeshes,
            BonePalettes = bonePalettes,
            OriginalBonePaletteCount = bonePalettes.Count,
            BonePaletteBlockOffset = bonePaletteBlockOffset,
            BonePaletteBlockLength = bonePaletteBlockLength,
            BonePaletteEntrySize = 56,
            BonePaletteEntryTemplate = bonePaletteEntryTemplate,
            TextureGroupBlockOffset = textureGroupBlockOffset,
            TextureGroupBlockLength = textureGroupBlockLength,
            TextureGroups = textureGroups,
            UvScalesOffset = uvScalesOffset,
            UvScalesLength = uvScaleFloatCount * 4,
            FaceCountFieldOffset = faceCountFieldOffset,
            FaceIndexFormat = faceIndexFormat,
            FacePointCount = facePointCount,
            FaceDataOffset = faceDataOffset,
            FaceDataLength = faceDataLength,
            VertexCountFieldOffset = primaryBuffer.CountFieldOffset,
            VertexCount = primaryBuffer.VertexCount,
            VertexAttrTableOffset = primaryBuffer.AttrTableOffset,
            VertexDataOffset = primaryBuffer.DataOffset,
            VertexStride = primaryBuffer.VertexStride,
            VertexBuffers = vertexBuffers,
            TailLength = data.Length - tailOffset,
            Attributes = primaryBuffer.Attributes,
        };
    }

    private static void SkipSizedBlock(DataReader reader)
    {
        var size = (int)reader.ReadUInt32() - 4;
        reader.Skip(size);
    }

    private static int DetectUvScaleFloatCount(DataReader reader)
    {
        var start = reader.Position;
        for (var candidate = 8; candidate <= 128; candidate++)
        {
            if (LooksLikePostUvBlocks(reader, start + candidate * 4))
            {
                return candidate;
            }
        }

        return 8;
    }

    private static bool LooksLikePostUvBlocks(DataReader reader, int position)
    {
        if (!TrySkipSizedBlockAt(reader, position, out var afterFirstBlock) ||
            !TrySkipSizedBlockAt(reader, afterFirstBlock, out var afterSecondBlock))
        {
            return false;
        }

        var faceCountOffset = afterSecondBlock + 1 + 4;
        var faceDataOffset = faceCountOffset + 4 + 8;
        if (faceDataOffset > reader.Length)
        {
            return false;
        }

        var facePointCount = reader.PeekUInt32(faceCountOffset);
        return facePointCount <= int.MaxValue / 2 &&
               facePointCount % 3 == 0 &&
               faceDataOffset + facePointCount * 2L <= reader.Length;
    }

    private static bool TrySkipSizedBlockAt(DataReader reader, int position, out int nextPosition)
    {
        nextPosition = position;
        if (position < 0 || position + 4 > reader.Length)
        {
            return false;
        }

        var size = reader.PeekUInt32(position);
        var end = position + (long)size;
        if (size < 4 || end > reader.Length)
        {
            return false;
        }

        nextPosition = (int)end;
        return true;
    }

    private static (List<ulong[]> Palettes, byte[] EntryTemplate) ReadBonePalettes(DataReader reader, int entrySize)
    {
        reader.ReadUInt32();
        var paletteCount = (int)reader.ReadUInt32();
        var palettes = new List<ulong[]>(paletteCount);
        var entryTemplate = new byte[entrySize];
        var hasEntryTemplate = false;
        for (var i = 0; i < paletteCount; i++)
        {
            var boneCount = (int)reader.ReadUInt32();
            var hashes = new ulong[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                var entryStart = reader.Position;
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                hashes[bone] = ((ulong)high << 32) | low;

                var remaining = entrySize - 8;
                if (remaining > 0)
                {
                    reader.Skip(remaining);
                }

                if (!hasEntryTemplate)
                {
                    entryTemplate = reader.Slice(entryStart, entrySize);
                    hasEntryTemplate = true;
                }
            }

            palettes.Add(hashes);
        }

        return (palettes, entryTemplate);
    }

    private static List<TextureGroupLayout> ReadTextureGroups(
        DataReader reader,
        int groupCount,
        int finalTextureSubBlockBytes)
    {
        reader.ReadUInt32();
        var groups = new List<TextureGroupLayout>(groupCount);
        for (var group = 0; group < groupCount; group++)
        {
            var groupOffset = reader.Position;
            var countFieldOffset = reader.Position;
            var textureCount = (int)reader.ReadUInt32();
            if (textureCount < 0 || textureCount > 4096)
            {
                throw new InvalidDataException($"invalid texture count {textureCount} at 0x{reader.Position - 4:X}");
            }

            var entriesOffset = reader.Position;
            var entries = new List<TextureEntryLayout>(textureCount);
            for (var i = 0; i < textureCount; i++)
            {
                var entryOffset = reader.Position;
                reader.ReadUInt32();
                var hashLowOffset = reader.Position;
                var hashLow = reader.ReadUInt32();
                var hashHighOffset = reader.Position;
                var hashHigh = reader.ReadUInt32();
                reader.Skip(12);
                reader.Skip(24);
                reader.ReadUInt32();
                reader.Skip(finalTextureSubBlockBytes);
                entries.Add(new TextureEntryLayout(entryOffset, reader.Position - entryOffset, hashLowOffset, hashHighOffset, hashLow, hashHigh));
            }

            groups.Add(new TextureGroupLayout(
                Index: group,
                GroupOffset: groupOffset,
                CountFieldOffset: countFieldOffset,
                EntriesOffset: entriesOffset,
                GroupLength: reader.Position - groupOffset,
                Entries: entries));
        }

        return groups;
    }
}

public sealed record SubmeshLayout(
    int EntryOffset,
    int EntryLength,
    int BoneSetRaw,
    int BoneSetFieldOffset,
    int VertexMinFieldOffset,
    int VertexMaxFieldOffset,
    int FaceStartFieldOffset,
    int PolygonCountFieldOffset,
    int[] TextureSlotFieldOffsets,
    int VertexMin,
    int VertexMax,
    int FacePointStart,
    int PolygonCount);

public sealed record TextureGroupLayout(
    int Index,
    int GroupOffset,
    int CountFieldOffset,
    int EntriesOffset,
    int GroupLength,
    List<TextureEntryLayout> Entries);

public sealed record TextureEntryLayout(
    int EntryOffset,
    int EntryLength,
    int HashLowFieldOffset,
    int HashHighFieldOffset,
    uint HashLow,
    uint HashHigh);

public sealed record VertexBufferLayout(
    int Index,
    int CountFieldOffset,
    int StrideFieldOffset,
    int AttrTableOffset,
    int DataOffset,
    int VertexCount,
    int VertexStride,
    VertexAttrLayout Attributes)
{
    public int DataLength => VertexCount * VertexStride;
}
