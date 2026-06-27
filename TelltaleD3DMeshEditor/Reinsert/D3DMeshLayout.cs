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
            if (version == 25)
            {
                // Static V25 (Michonne) reinsertion uses the V25 layout/writer branch integrated below
                // and in MeshReinserter, used by single-asset "Reimport Selected" and --reinsert-prop.
                // This V13/17/18-shaped layout is only reached by the Combine-Parts / damage-variant
                // flows, which are not wired for V25 yet.
                throw new NotSupportedException("The Walking Dead: Michonne (V25): use single-asset 'Reimport Selected' for static meshes. Combine-Parts and skinned V25 reinsertion are not supported yet.");
            }

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

    public static V25MeshLayout BuildV25(byte[] data)
        => V25MeshLayout.Build(data);

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

// ---- V25 layout moved from V25MeshLayout.cs ----


// Layout map for a The Walking Dead: Michonne (.d3dmesh V25) file, built specifically for the
// V25 engine layout (T3MeshData with LODs, property-set materials, and a vertex state whose
// attribute array describes which buffer holds each stream). It walks the file read-for-read
// exactly like D3DMeshParser.ParseV25, but records byte offsets/lengths of every region that a
// static-mesh reinsert must rewrite (bounds, batch vertex/index ranges, vertex count, UV scales,
// the index/vertex buffer headers and their async payloads) instead of decoding the data.
//
// This is V25-specific on purpose: it does NOT reuse the V13/17/18 D3DMeshLayout (different file
// shape) and shares nothing with the experimental Back to the Future path.
//
// The MSV5 container stores the header (sync/"Default" section) and the buffer payloads
// (async section) separately; their sizes live at file offsets 4 and 12 respectively. The sync
// section ends exactly where the async section (faces) begins, i.e. at FaceDataStart.
public sealed class V25MeshLayout
{
    public required byte[] Original { get; init; }
    public required string Name { get; init; }
    public int Version { get; init; }
    public int DataOffset { get; init; }

    // True only when this mesh is a pure static mesh (no blend weight/index attributes and no bone
    // palettes). Skinned meshes set Reject with a clear reason; reinsertion refuses them for now.
    public bool IsStatic { get; init; }
    public string? RejectReason { get; init; }

    // Mesh-level bounding box (T3MeshData header H), per-LOD and per-batch boxes. Each is 2x vec3
    // (min,max) = 24 bytes. Patching all of them keeps in-game culling correct after a geometry swap.
    public int MeshBoundsOffset { get; init; }
    public required List<int> LodBoundsOffsets { get; init; }

    public required List<V25BatchLayout> Batches { get; init; }

    // One entry per material/property-set. Lets reinsertion rebind a material's diffuse texture or
    // duplicate a whole property set to add materials when a model has more textures than the template.
    public required List<V25MaterialLayout> Materials { get; init; }

    // Offsets/details needed to add materials (duplicate property sets + their material-group entries).
    public int MaterialCountFieldOffset { get; init; }
    public int MaterialsEndOffset { get; init; }
    public int MaterialGroupCountFieldOffset { get; init; }
    public int MaterialGroupSizeFieldOffset { get; init; }
    public int MaterialGroupEntriesEndOffset { get; init; }
    public required List<V25MaterialGroupEntryLayout> MaterialGroupEntries { get; init; }

    // T3MeshData mTextures. The game uses this resource list in addition to the property-set material
    // hash, so adding V25 materials must also add matching texture entries.
    public int TextureCountFieldOffset { get; init; }
    public int TextureBlockSizeFieldOffset { get; init; }
    public int TextureEntriesEndOffset { get; init; }
    public required List<V25TextureEntryLayout> TextureEntries { get; init; }

    // Block-size fields that enclose the LOD0 batch table, needed to rebuild it with a different batch
    // count. All sizes shrink/grow by the same byte delta when the table is resized. lodCount is 1 for
    // every shipped V25 mesh, so resizing only touches this single LOD entry.
    public int LodCount { get; init; }
    public int GeometryBlockSizeFieldOffset { get; init; }
    public int LodBlockSizeFieldOffset { get; init; }
    public int LodEntrySizeFieldOffset { get; init; }
    public int BatchCountFieldOffset { get; init; }

    // T3MeshData mVertexCount (header H).
    public int VertexCountFieldOffset { get; init; }

    // mTexCoordTransform entries actually present in the file (layer index + offset of its 4 floats:
    // xMult,yMult,xStart,yStart). Only quantized (fmt19) UV layers that own an entry get rescaled.
    public required List<V25UvScaleSlot> UvScaleSlots { get; init; }

    // Vertex state attribute table (mAttributes) — drives the encoder: which buffer/format/layer holds
    // each stream. Recorded in the same +1 convention the parser uses.
    public required List<V25AttributeLayout> Attributes { get; init; }

    // Index (face) buffer and the vertex buffers, in file order.
    public required V25BufferLayout FaceBuffer { get; init; }
    public required List<V25BufferLayout> VertexBuffers { get; init; }

    // Bone palettes (mBonePalettes), reused as-is when reinserting a skinned model rigged to the same
    // skeleton. Empty for static meshes. The block start/end bound the whole region for diagnostics.
    public List<V25BonePaletteLayout> BonePalettes { get; init; } = [];
    public int BonePaletteBlockStart { get; init; }
    public int BonePaletteBlockEnd { get; init; }

    // True when the mesh carries blend weight/index attributes and bone palettes (a character mesh).
    public bool IsSkinned { get; init; }

    // Start of the async section (= first index byte) and the trailing bytes after the last payload.
    public int FaceDataStart { get; init; }
    public int TailOffset { get; init; }
    public int TailLength { get; init; }

    public static V25MeshLayout Build(byte[] data)
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
        if (version != 25)
        {
            throw new NotSupportedException($"V25MeshLayout only supports V25 (got {version}).");
        }

        reader.Skip(1);
        var materialCountFieldOffset = reader.Position;
        var materialCount = checked((int)reader.ReadUInt32());
        if (materialCount == 0)
        {
            return Rejected(data, name, header.DataOffset, "Mesh has no materials/geometry to reinsert.");
        }

        // Material/property-set block. Walk each material and record its full byte range, symbol offset
        // and diffuse-hash offset so reinsertion can rebind a material's texture or duplicate a whole
        // property set to add new materials/textures.
        var materials = new List<V25MaterialLayout>(materialCount);
        for (var i = 0; i < materialCount; i++)
        {
            var start = reader.Position;
            var diffuseHashOffset = ReadMaterialDiffuseHashOffset(reader, out var materialEndForMat);
            materials.Add(new V25MaterialLayout(start, materialEndForMat, start, diffuseHashOffset));
            reader.Seek(materialEndForMat);
        }

        var materialsEndOffset = reader.Position;

        // Geometry block (T3MeshData): inclusive sized block; async/face data begins at its end.
        reader.ReadUInt32();
        var geometryBlockSizeFieldOffset = reader.Position;
        var geometryBlockSize = checked((int)reader.ReadUInt32());
        var faceDataStart = reader.Position + geometryBlockSize - 4;

        var lodBlockSizeFieldOffset = reader.Position;
        var lodBlockEnd = reader.Position + checked((int)reader.ReadUInt32());
        var lodCount = checked((int)reader.ReadUInt32());
        var batches = new List<V25BatchLayout>();
        var lodBoundsOffsets = new List<int>();
        var anySkinnedBatch = false;
        var lodEntrySizeFieldOffset = 0;
        var batchCountFieldOffset = 0;
        for (var lod = 1; lod <= lodCount; lod++)
        {
            if (lod == 1)
            {
                lodEntrySizeFieldOffset = reader.Position;
            }

            var lodEntryEnd = reader.Position + checked((int)reader.ReadUInt32() - 4);
            if (lod == 1)
            {
                batchCountFieldOffset = reader.Position;
            }

            var submeshCount = checked((int)reader.ReadUInt32());
            for (var i = 0; i < submeshCount; i++)
            {
                var batchBoundsOffset = reader.Position;
                reader.ReadVec3();
                reader.ReadVec3();
                reader.ReadUInt32();
                reader.Skip(16);
                reader.ReadUInt32();
                var vMinOffset = reader.Position;
                reader.ReadUInt32();
                var vMaxOffset = reader.Position;
                reader.ReadUInt32();
                var faceStartOffset = reader.Position;
                var facePointStart = checked((int)reader.ReadUInt32());
                var polyCountOffset = reader.Position;
                var polygonCount = checked((int)reader.ReadUInt32());
                var headerLength2 = reader.ReadUInt32();
                if (headerLength2 == 0x10)
                {
                    reader.Skip(8);
                }

                var textureIndicesOffset = reader.Position;
                var textureIndicesRaw = reader.ReadUInt32();
                var materialIndexOffset = reader.Position;
                var materialIndex = ReadOptionalIndexPlusOne(reader);
                var boneSetOffset = reader.Position;
                var boneSet = ReadOptionalIndexPlusOne(reader);
                reader.ReadUInt32();
                var batchEndOffset = reader.Position;

                if (boneSet != 0)
                {
                    anySkinnedBatch = true;
                }

                if (lod == 1)
                {
                    batches.Add(new V25BatchLayout(
                        BoundsOffset: batchBoundsOffset,
                        VertexMinOffset: vMinOffset,
                        VertexMaxOffset: vMaxOffset,
                        FaceStartOffset: faceStartOffset,
                        PolygonCountOffset: polyCountOffset,
                        TextureIndicesOffset: textureIndicesOffset,
                        MaterialIndexOffset: materialIndexOffset,
                        EndOffset: batchEndOffset,
                        FacePointStart: facePointStart,
                        PolygonCount: polygonCount,
                        TextureIndicesRaw: textureIndicesRaw,
                        MaterialIndex: materialIndex,
                        BoneSetOffset: boneSetOffset,
                        BoneSetIndex: boneSet));
                }
            }

            // LOD footer: usage, index, bbox (24), sphere(16+pad), trailing.
            reader.ReadUInt32();
            reader.ReadUInt32();
            lodBoundsOffsets.Add(reader.Position);
            reader.ReadVec3();
            reader.ReadVec3();
            reader.ReadUInt32();
            reader.Skip(16);
            reader.Skip(8);
            if (reader.Position < lodEntryEnd)
            {
                reader.Seek(lodEntryEnd);
            }
        }
        if (reader.Position < lodBlockEnd)
        {
            reader.Seek(lodBlockEnd);
        }

        // mTextures table (T3MeshTexture, 76 bytes/entry).
        var textureBlockSizeFieldOffset = reader.Position;
        var textureBlockEnd = reader.Position + checked((int)reader.ReadUInt32() - 4);
        var textureCountFieldOffset = reader.Position;
        var textureCount = checked((int)reader.ReadUInt32());
        var textureEntries = new List<V25TextureEntryLayout>(textureCount);
        for (var i = 0; i < textureCount; i++)
        {
            var entryStart = reader.Position;
            var typeOffset = reader.Position;
            reader.ReadUInt32();               // mTextureType
            reader.Skip(16);                  // mhTexture
            var symbolOffset = reader.Position; // mNameSymbol
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.Skip(48);                  // bounds, sphere, metrics
            textureEntries.Add(new V25TextureEntryLayout(entryStart, typeOffset, symbolOffset, reader.Position - entryStart));
        }

        var textureEntriesEndOffset = reader.Position;
        if (reader.Position < textureBlockEnd)
        {
            reader.Seek(textureBlockEnd);
        }

        // mMaterials (material pairing) block — each entry links a batch's material index to a material
        // (property-set) by symbol. Recorded so reinsertion can add entries for duplicated materials.
        var materialGroupSizeFieldOffset = reader.Position;
        var materialGroupEnd = reader.Position + checked((int)reader.ReadUInt32() - 4);
        var materialGroupCountFieldOffset = reader.Position;
        var materialGroupCount = checked((int)reader.ReadUInt32());
        var materialGroupEntries = new List<V25MaterialGroupEntryLayout>(materialGroupCount);
        for (var i = 0; i < materialGroupCount; i++)
        {
            var entryStart = reader.Position;
            reader.ReadUInt32();               // entry length
            var symbolOffset = reader.Position;
            reader.ReadUInt32();               // matLow (symbol)
            reader.ReadUInt32();               // matHigh
            reader.Skip(8);
            reader.Skip(24);
            reader.ReadUInt32();
            reader.Skip(16);
            reader.ReadUInt32();
            materialGroupEntries.Add(new V25MaterialGroupEntryLayout(entryStart, symbolOffset, reader.Position - entryStart));
        }

        var materialGroupEntriesEndOffset = reader.Position;
        if (reader.Position < materialGroupEnd)
        {
            reader.Seek(materialGroupEnd);
        }

        // mMaterialOverrides (16 bytes/entry).
        SkipV25SizedBlock(reader, 16);

        // mBones / bone palettes. Each bone entry is 56 bytes: hash(8) + bounds(24) + pad(4) +
        // center(12) + radius(4) + pad(4). The vertex bone bytes index into a submesh's palette, so the
        // skinned encoder needs each palette's hash list to map GLB joints back to palette indices.
        var bonePaletteBlockStart = reader.Position;
        reader.ReadUInt32();
        var bonePaletteCount = checked((int)reader.ReadUInt32());
        var bonePalettes = new List<V25BonePaletteLayout>(bonePaletteCount);
        for (var palette = 0; palette < bonePaletteCount; palette++)
        {
            var paletteStart = reader.Position;
            var boneCount = checked((int)reader.ReadUInt32());
            var hashes = new ulong[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                hashes[bone] = ((ulong)high << 32) | low;
                reader.Skip(48); // bounds(24) + pad(4) + center(12) + radius(4) + pad(4)
            }

            bonePalettes.Add(new V25BonePaletteLayout(paletteStart, hashes));
        }

        var bonePaletteBlockEnd = reader.Position;

        // mLocalTransforms + mMaterialRequirements style sized blocks.
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);

        // Header H: mesh bounds + vertex count + UV transforms.
        reader.ReadUInt32();
        reader.ReadUInt32();
        var meshBoundsOffset = reader.Position;
        reader.ReadVec3();
        reader.ReadVec3();
        reader.ReadUInt32();
        reader.Skip(16);
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        var vertexCountFieldOffset = reader.Position;
        reader.ReadUInt32();
        reader.ReadUInt32();
        var uvLayerCount = checked((int)reader.ReadUInt32());
        var uvScaleSlots = new List<V25UvScaleSlot>(uvLayerCount);
        for (var i = 0; i < uvLayerCount; i++)
        {
            var layer = checked((int)reader.ReadUInt32());
            var valuesOffset = reader.Position;
            reader.Skip(16); // 4 floats
            uvScaleSlots.Add(new V25UvScaleSlot(layer, valuesOffset));
        }

        // The vertex state declares how many vertex buffers follow; the count is not fixed (a mesh with
        // no UVs has fewer vertex buffers). The index/face buffer is gated by a trailing bool byte
        // (hasIndexBuffer), matching the T3GFXVertexState serializer's "no mIndexBufferCount" branch.
        var (attributes, vertexBufferCount) = ReadAttributes(reader);
        var hasIndexBuffer = reader.ReadByte() != 0;
        var indexBufferCount = hasIndexBuffer ? 1 : 0;

        var indexBuffers = new List<V25BufferLayout>(indexBufferCount);
        for (var i = 0; i < indexBufferCount; i++)
        {
            indexBuffers.Add(ReadBufferHeader(reader));
        }

        var vertexBufferHeaders = new List<V25BufferLayout>(vertexBufferCount);
        for (var i = 0; i < vertexBufferCount; i++)
        {
            vertexBufferHeaders.Add(ReadBufferHeader(reader));
        }

        // Async payloads begin at FaceDataStart in buffer order: index buffer(s), then vertex buffers.
        var cursor = faceDataStart;
        V25BufferLayout faceBufferLaid = default;
        for (var i = 0; i < indexBuffers.Count; i++)
        {
            var laid = indexBuffers[i] with { PayloadOffset = cursor, PayloadLength = indexBuffers[i].Count * indexBuffers[i].Stride };
            cursor = laid.PayloadOffset + laid.PayloadLength;
            if (i == 0)
            {
                faceBufferLaid = laid;
            }
        }

        var vertexBuffers = new List<V25BufferLayout>(vertexBufferHeaders.Count);
        foreach (var vb in vertexBufferHeaders)
        {
            var laid = vb with { PayloadOffset = cursor, PayloadLength = vb.Count * vb.Stride };
            cursor = laid.PayloadOffset + laid.PayloadLength;
            vertexBuffers.Add(laid);
        }

        var tailOffset = cursor;

        var hasSkinningAttr = attributes.Any(a => a.Key is "weights" or "bones");
        string? reject = null;
        if (hasSkinningAttr || bonePaletteCount > 0 || anySkinnedBatch)
        {
            reject = "Only static V25 meshes can be reinserted for now (this mesh is skinned: it has bone weights/palettes). Skinned support is planned for a later phase.";
        }
        else if (batches.Count == 0)
        {
            reject = "Mesh has no LOD0 batches to reinsert.";
        }

        return new V25MeshLayout
        {
            Original = data,
            Name = name,
            Version = version,
            DataOffset = header.DataOffset,
            IsStatic = reject is null,
            RejectReason = reject,
            MeshBoundsOffset = meshBoundsOffset,
            LodBoundsOffsets = lodBoundsOffsets,
            Batches = batches,
            Materials = materials,
            MaterialCountFieldOffset = materialCountFieldOffset,
            MaterialsEndOffset = materialsEndOffset,
            MaterialGroupCountFieldOffset = materialGroupCountFieldOffset,
            MaterialGroupSizeFieldOffset = materialGroupSizeFieldOffset,
            MaterialGroupEntriesEndOffset = materialGroupEntriesEndOffset,
            MaterialGroupEntries = materialGroupEntries,
            TextureCountFieldOffset = textureCountFieldOffset,
            TextureBlockSizeFieldOffset = textureBlockSizeFieldOffset,
            TextureEntriesEndOffset = textureEntriesEndOffset,
            TextureEntries = textureEntries,
            LodCount = lodCount,
            GeometryBlockSizeFieldOffset = geometryBlockSizeFieldOffset,
            LodBlockSizeFieldOffset = lodBlockSizeFieldOffset,
            LodEntrySizeFieldOffset = lodEntrySizeFieldOffset,
            BatchCountFieldOffset = batchCountFieldOffset,
            VertexCountFieldOffset = vertexCountFieldOffset,
            UvScaleSlots = uvScaleSlots,
            Attributes = attributes,
            FaceBuffer = faceBufferLaid,
            VertexBuffers = vertexBuffers,
            BonePalettes = bonePalettes,
            BonePaletteBlockStart = bonePaletteBlockStart,
            BonePaletteBlockEnd = bonePaletteBlockEnd,
            IsSkinned = hasSkinningAttr || bonePaletteCount > 0 || anySkinnedBatch,
            FaceDataStart = faceDataStart,
            TailOffset = tailOffset,
            TailLength = data.Length - tailOffset,
        };
    }

    private static V25MeshLayout Rejected(byte[] data, string name, int dataOffset, string reason) => new()
    {
        Original = data,
        Name = name,
        Version = 25,
        DataOffset = dataOffset,
        IsStatic = false,
        RejectReason = reason,
        MeshBoundsOffset = 0,
        LodBoundsOffsets = [],
        Batches = [],
        Materials = [],
        MaterialGroupEntries = [],
        TextureEntries = [],
        VertexCountFieldOffset = 0,
        UvScaleSlots = [],
        Attributes = [],
        FaceBuffer = new V25BufferLayout(0, 0, 0, 0, 0, 0),
        VertexBuffers = [],
        FaceDataStart = 0,
        TailOffset = data.Length,
        TailLength = 0,
    };

    // Reads a T3GFXBuffer header (mResourceUsage, mBufferFormat, mBufferUsage, mCount, mStride) and
    // records the offsets of the mCount and mStride fields. The payload lives later, in the async
    // section; PayloadOffset/Length are filled in by the caller in buffer order.
    private static V25BufferLayout ReadBufferHeader(DataReader reader)
    {
        reader.Skip(12); // mResourceUsage, mBufferFormat, mBufferUsage
        var countFieldOffset = reader.Position;
        var count = checked((int)reader.ReadUInt32());
        var strideFieldOffset = reader.Position;
        var stride = checked((int)reader.ReadUInt32());
        return new V25BufferLayout(countFieldOffset, strideFieldOffset, count, stride, PayloadOffset: 0, PayloadLength: 0);
    }

    private static (List<V25AttributeLayout> Attributes, int VertexBufferCount) ReadAttributes(DataReader reader)
    {
        reader.ReadUInt32(); // mVertexCountPerInstance
        reader.ReadUInt32(); // unused count slot (0 in WDM)
        var vertexBufferCount = checked((int)reader.ReadUInt32()); // mVertexBufferCount
        var count = checked((int)reader.ReadUInt32()); // mAttributeCount
        var result = new List<V25AttributeLayout>(count);
        for (var i = 0; i < count; i++)
        {
            var type = checked((int)reader.ReadUInt32() + 1);
            var format = checked((int)reader.ReadUInt32() + 1);
            var layer = checked((int)reader.ReadUInt32() + 1);
            var buffer = checked((int)reader.ReadUInt32() + 1);
            var bufferOffset = checked((int)reader.ReadUInt32()); // mBufferOffset (raw byte offset within its buffer stride)
            var key = type switch
            {
                1 => "position",
                2 when layer == 1 => "normals",
                2 when layer == 2 => "binormals",
                3 => "tangents",
                4 => "weights",
                5 => "bones",
                6 when layer == 1 => "colors",
                6 when layer == 2 => "colors2",
                7 => $"uv{layer}",
                _ => "",
            };
            result.Add(new V25AttributeLayout(key, type, format, layer, buffer, bufferOffset));
        }

        return (result, vertexBufferCount);
    }

    private static int ReadOptionalIndexPlusOne(DataReader reader)
    {
        var raw = reader.ReadUInt32();
        return raw == uint.MaxValue ? 0 : checked((int)raw + 1);
    }

    // Walks one material/property-set: reads its 16-byte prefix (symbol + type hash) and inclusive block
    // size, then scans the block for the texture-parameter section (marker F1C3F2C7/52A09151) and returns
    // the byte offset of the diffuse entry's texture hash (texLow), or -1 if the material binds no diffuse.
    private static int ReadMaterialDiffuseHashOffset(DataReader reader, out int materialEnd)
    {
        reader.ReadUInt32(); // symbol low
        reader.ReadUInt32(); // symbol high
        reader.Skip(8);      // type CRC64
        materialEnd = reader.Position + checked((int)reader.ReadUInt32());

        var diffuseHashOffset = -1;
        for (var scan = reader.Position; scan + 12 <= materialEnd; scan += 4)
        {
            if (reader.PeekUInt32(scan) != 0xF1C3F2C7 || reader.PeekUInt32(scan + 4) != 0x52A09151)
            {
                continue;
            }

            var count = checked((int)reader.PeekUInt32(scan + 8));
            var entry = scan + 12;
            for (var t = 0; t < count && entry + 16 <= materialEnd; t++, entry += 16)
            {
                var typeLow = reader.PeekUInt32(entry);
                var typeHigh = reader.PeekUInt32(entry + 4);
                if (IsDiffuseTypeHash(typeHigh, typeLow))
                {
                    diffuseHashOffset = entry + 8; // texLow lives 8 bytes into the 16-byte entry
                    break;
                }
            }

            break;
        }

        return diffuseHashOffset;
    }

    // The V25 material type hashes that mean "diffuse / base colour" (same set the parser maps to the
    // diffuse slot). Tuple order is (typeHigh, typeLow) to match the file's low-then-high field order.
    private static bool IsDiffuseTypeHash(uint typeHigh, uint typeLow)
        => (typeHigh, typeLow) is
            (0x8648FA82, 0xD1DBEE1A) or
            (0xDC6E83A0, 0x253F163A) or
            (0x94A590DE, 0x74B1F5C1);

    private static void SkipSizedBlock(DataReader reader)
    {
        var size = checked((int)reader.ReadUInt32() - 4);
        reader.Skip(size);
    }

    private static void SkipV25SizedBlock(DataReader reader, int bytesPerEntry)
    {
        var end = reader.Position + checked((int)reader.ReadUInt32() - 4);
        var count = checked((int)reader.ReadUInt32());
        reader.Skip(count * bytesPerEntry);
        if (reader.Position < end)
        {
            reader.Seek(end);
        }
    }
}

// One LOD0 batch (T3MeshBatch). Offsets point at the u32 fields the reinserter rewrites; BoundsOffset
// and EndOffset bound the whole entry so it can be copied as a template when rebuilding the table.
public readonly record struct V25BatchLayout(
    int BoundsOffset,
    int VertexMinOffset,
    int VertexMaxOffset,
    int FaceStartOffset,
    int PolygonCountOffset,
    int TextureIndicesOffset,
    int MaterialIndexOffset,
    int EndOffset,
    int FacePointStart,
    int PolygonCount,
    uint TextureIndicesRaw,
    int MaterialIndex,
    int BoneSetOffset = 0,
    int BoneSetIndex = 0);

// One bone palette (mBonePalettes entry): the bone hashes it contains, in order. Vertex bone indices
// are indices into this list. Reused as-is when reinserting a skinned model rigged to the same skeleton.
public readonly record struct V25BonePaletteLayout(int Start, ulong[] BoneHashes);

// One material/property-set: its byte range [Start, End), the offset of its symbol (object-name hash,
// 8 bytes) and the offset of its diffuse texture hash (texLow, 8 bytes), or -1 if it binds no diffuse.
public readonly record struct V25MaterialLayout(int Start, int End, int SymbolOffset, int DiffuseHashOffset);

// One mMaterials (material-pairing) entry: its byte range and the offset of the material symbol it
// references. New entries are cloned from an existing one when materials are added.
public readonly record struct V25MaterialGroupEntryLayout(int Start, int SymbolOffset, int Length);

// One T3MeshData mTextures entry. SymbolOffset points at mNameSymbol; added entries are cloned from a
// real entry and all copies of the cloned texture hash inside the entry are rewritten to the new hash.
public readonly record struct V25TextureEntryLayout(int Start, int TypeOffset, int SymbolOffset, int Length);

// One mTexCoordTransform entry present in the file: the UV layer it applies to (0-based as stored)
// and the offset of its 4 floats (xMult, yMult, xStart, yStart).
public readonly record struct V25UvScaleSlot(int Layer, int ValuesOffset);

// One vertex-state attribute (mAttributes). Key is the parser's semantic name; Type/Format/Layer/
// Buffer are in the +1 convention (raw value + 1) the parser/RTB importer use.
public readonly record struct V25AttributeLayout(string Key, int Type, int Format, int Layer, int Buffer, int BufferOffset);

// A T3GFXBuffer: offsets of its mCount/mStride header fields and its async payload location.
public readonly record struct V25BufferLayout(
    int CountFieldOffset,
    int StrideFieldOffset,
    int Count,
    int Stride,
    int PayloadOffset,
    int PayloadLength);
