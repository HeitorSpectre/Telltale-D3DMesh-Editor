using System.Buffers.Binary;
using System.Text;
using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Mesh;

internal static partial class BackToTheFutureMeshParser
{
    private const int MaxTextureScanBytes = 700;

    public static MeshData Parse(byte[] data, string name, int afterVersionOffset)
    {
        var reader = new DataReader(data);
        reader.Seek(afterVersionOffset);

        var marker = reader.ReadByte();
        if (marker is not (0x30 or 0x31))
        {
            throw new NotSupportedException("Back to the Future v1 mesh marker was not found.");
        }

        reader.ReadVec3();
        reader.ReadVec3();
        SkipSizedBlock(reader);

        var triangleBlockStart = reader.Position;
        var triangleBlockSize = checked((int)reader.ReadUInt32());
        var declaredSubmeshCount = checked((int)reader.ReadUInt32());
        if (triangleBlockSize < 8 || declaredSubmeshCount <= 0 || declaredSubmeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid Back to the Future triangle block: size={triangleBlockSize}, count={declaredSubmeshCount}.");
        }

        var triangleDataStart = reader.Position;
        var triangleBlockEnd = checked(triangleBlockStart + triangleBlockSize);
        if (triangleBlockEnd > data.Length)
        {
            throw new EndOfStreamException($"Back to the Future triangle block exceeds file bounds: 0x{triangleBlockEnd:X} > 0x{data.Length:X}.");
        }

        var infos = ReadTriangleSets(data, triangleDataStart, triangleBlockEnd, declaredSubmeshCount);
        var faceSection = ReadFaceSection(data, triangleBlockEnd, infos);
        var vertexBuffers = ReadVertexBuffers(data, faceSection.NextOffset);
        if (vertexBuffers.Positions.Count == 0)
        {
            throw new InvalidDataException("No Back to the Future vertices found.");
        }

        var mesh = new MeshData { Name = name, Version = 1 };
        var bonePaletteInfo = vertexBuffers.Bones.Count > 0
            ? ReadBonePaletteInfo(data, faceSection.NextOffset)
            : new BonePaletteInfo(Array.Empty<ulong[]>(), Array.Empty<int>());
        foreach (var palette in bonePaletteInfo.Palettes)
        {
            mesh.BonePalettes.Add(palette);
        }

        foreach (var info in infos)
        {
            var vertexStart = Math.Clamp(info.VertexMin, 0, vertexBuffers.Positions.Count);
            var vertexEnd = Math.Clamp(info.VertexMax, vertexStart, vertexBuffers.Positions.Count - 1);
            if (vertexEnd < vertexStart)
            {
                continue;
            }

            var materialName = info.TextureNames.TryGetValue("diffuse", out var diffuse)
                ? diffuse
                : $"material_{info.Index + 1}";
            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = ResolveBonePaletteIndex(info, bonePaletteInfo, mesh.BonePalettes.Count),
                RigidBoneIndex = info.BoneSetIndex,
                SourceSubmeshIndex = info.Index,
            };
            foreach (var texture in info.TextureNames)
            {
                submesh.TextureNames[texture.Key] = texture.Value;
            }

            for (var vertex = vertexStart; vertex <= vertexEnd; vertex++)
            {
                var p = vertexBuffers.Positions[vertex];
                var n = ValueOrDefault(vertexBuffers.Normals, vertex, (0f, 1f, 0f));
                var b = ValueOrDefault(vertexBuffers.Binormals, vertex, (0f, 0f, 0f));
                var uv = ValueOrDefault(vertexBuffers.Uv1, vertex, (0f, 0f));
                var uv2 = ValueOrDefault(vertexBuffers.Uv2, vertex, uv);
                var bones = ValueOrDefault(vertexBuffers.Bones, vertex, (0, 0, 0, 0));
                var weights = ValueOrDefault(vertexBuffers.Weights, vertex, (1f, 0f, 0f, 0f));

                submesh.Vertices.Add(new VertexData(
                    p.Item1, p.Item2, p.Item3,
                    n.Item1, n.Item2, n.Item3,
                    uv.Item1, uv.Item2,
                    uv2.Item1, uv2.Item2,
                    uv2.Item1, uv2.Item2,
                    uv2.Item1, uv2.Item2,
                    bones.Item1, bones.Item2, bones.Item3, bones.Item4,
                    weights.Item1, weights.Item2, weights.Item3, weights.Item4,
                    BinormalX: b.Item1,
                    BinormalY: b.Item2,
                    BinormalZ: b.Item3));
            }

            var listStart = Math.Max(0, info.FacePointStart);
            var listEnd = Math.Min(faceSection.Indices.Count, listStart + info.PolygonCount * 3);
            for (var index = listStart; index + 2 < listEnd; index += 3)
            {
                AddFaceIfValid(submesh, faceSection.Indices[index], faceSection.Indices[index + 1], faceSection.Indices[index + 2], vertexStart);
            }

            var facePointEnd = info.Index + 1 < infos.Count
                ? Math.Min(faceSection.Indices.Count, infos[info.Index + 1].FacePointStart)
                : faceSection.Indices.Count;
            AppendTriangleStripTail(submesh, faceSection.Indices, listEnd, facePointEnd, vertexStart, vertexBuffers.Normals);

            if (submesh.Faces.Count == 0 && info.PolygonCount > 0)
            {
                throw new InvalidDataException($"Back to the Future submesh {info.Index} has no valid triangles.");
            }

            if (submesh.Vertices.Count > 0 && submesh.Faces.Count > 0)
            {
                mesh.Submeshes.Add(submesh);
            }
        }

        return mesh.Submeshes.Count == 0
            ? BuildFallbackMesh(name, vertexBuffers, faceSection)
            : mesh;
    }

    private static List<TriangleSetInfo> ReadTriangleSets(byte[] data, int start, int end, int declaredCount)
    {
        var infos = new List<TriangleSetInfo>(declaredCount);
        var lastStart = -1;
        var expectedVertexMin = 0;
        for (var offset = start; offset + 0x80 < end && infos.Count < declaredCount; offset++)
        {
            if (lastStart >= 0 && offset - lastStart < 0x80)
            {
                continue;
            }

            if (!TryReadTriangleSetHeader(data, offset, expectedVertexMin, out var candidate, out var fieldBaseOffset, out var boneSetOffset))
            {
                continue;
            }

            var scanEnd = Math.Min(end, offset + MaxTextureScanBytes);
            if (infos.Count == 0 && !ContainsD3dtx(data, offset, scanEnd))
            {
                continue;
            }

            lastStart = offset;
            expectedVertexMin = checked(candidate.VertexMax + 1);
            var nextStart = FindNextTriangleSetStart(data, offset + 0x80, end, expectedVertexMin);
            var entryEnd = nextStart > offset ? nextStart : scanEnd;
            var textures = ReadTextureNames(data, offset, Math.Min(entryEnd, scanEnd));
            infos.Add(new TriangleSetInfo(
                infos.Count,
                BoneSetIndex: candidate.BoneSetIndex,
                VertexMin: candidate.VertexMin,
                VertexMax: candidate.VertexMax,
                FacePointStart: candidate.FacePointStart,
                PolygonCount: candidate.PolygonCount,
                TextureNames: textures,
                FieldBaseOffset: fieldBaseOffset,
                BoneSetFieldOffset: boneSetOffset));
        }

        if (infos.Count == 0)
        {
            throw new InvalidDataException("No Back to the Future triangle sets were found.");
        }

        return infos;
    }

    private static BonePaletteInfo ReadBonePaletteInfo(byte[] data, int firstVertexBufferOffset)
    {
        var palettes = ReadEmbeddedBonePalettes(data, firstVertexBufferOffset);
        if (palettes.Count > 0)
        {
            return new BonePaletteInfo(palettes, Array.Empty<int>());
        }

        var toolkitBoneInfo = TelltaleToolkitMeshParser.TryReadOldBonePaletteInfo(data);
        if (toolkitBoneInfo is not null)
        {
            return new BonePaletteInfo(toolkitBoneInfo.Palettes, toolkitBoneInfo.TriangleSetPaletteIndices);
        }

        return new BonePaletteInfo(Array.Empty<ulong[]>(), Array.Empty<int>());
    }

    private static List<ulong[]> ReadEmbeddedBonePalettes(byte[] data, int firstVertexBufferOffset)
    {
        var scanEnd = firstVertexBufferOffset > 0
            ? Math.Min(firstVertexBufferOffset, data.Length)
            : data.Length;
        var candidates = new List<BonePaletteCandidate>();
        for (var offset = 0; offset + 16 <= scanEnd; offset++)
        {
            var count = ReadUInt32(data, offset);
            if (count is < 1 or > 128)
            {
                continue;
            }

            var end = offset + 4 + (int)count * 12;
            if (end > scanEnd)
            {
                continue;
            }

            var palette = new ulong[count];
            var uniqueHashes = new HashSet<ulong>();
            var valid = true;
            for (var i = 0; i < count; i++)
            {
                var entryOffset = offset + 4 + i * 12;
                var low = ReadUInt32(data, entryOffset);
                var high = ReadUInt32(data, entryOffset + 4);
                var pad = ReadUInt32(data, entryOffset + 8);
                var hash = ((ulong)high << 32) | low;
                if (hash == 0 || pad != 0)
                {
                    valid = false;
                    break;
                }

                palette[i] = hash;
                uniqueHashes.Add(hash);
            }

            if (valid && uniqueHashes.Count >= Math.Min((int)count, 4))
            {
                candidates.Add(new BonePaletteCandidate(offset, end, palette));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var byStart = candidates.ToDictionary(candidate => candidate.Start);
        var bestRun = new List<BonePaletteCandidate>();
        foreach (var candidate in candidates)
        {
            var run = new List<BonePaletteCandidate>();
            var current = candidate;
            while (true)
            {
                run.Add(current);
                if (!byStart.TryGetValue(current.End, out var next))
                {
                    break;
                }

                current = next;
            }

            if (run.Count > bestRun.Count ||
                run.Count == bestRun.Count && run.Sum(item => item.Palette.Length) > bestRun.Sum(item => item.Palette.Length))
            {
                bestRun = run;
            }
        }

        return bestRun.Count == 0
            ? []
            : bestRun.Select(candidate => candidate.Palette).ToList();
    }

    private static int ResolveBonePaletteIndex(
        TriangleSetInfo info,
        BonePaletteInfo bonePaletteInfo,
        int paletteCount)
    {
        if (bonePaletteInfo.TriangleSetPaletteIndices.Count > 0 &&
            info.Index >= 0 &&
            info.Index < bonePaletteInfo.TriangleSetPaletteIndices.Count)
        {
            return bonePaletteInfo.TriangleSetPaletteIndices[info.Index];
        }

        if (paletteCount <= 0)
        {
            return 0;
        }

        var rawIndex = Math.Max(0, info.BoneSetIndex);
        if (rawIndex < paletteCount)
        {
            return rawIndex;
        }

        return rawIndex > 0 && rawIndex - 1 < paletteCount ? rawIndex - 1 : 0;
    }

    private static int FindNextTriangleSetStart(byte[] data, int start, int end, int expectedVertexMin)
    {
        for (var offset = start; offset + 0x80 < end; offset++)
        {
            if (TryReadTriangleSetHeader(data, offset, expectedVertexMin, out _, out _, out _))
            {
                return offset;
            }
        }

        return -1;
    }

    private static bool TryReadTriangleSetHeader(byte[] data, int offset, int expectedVertexMin, out TriangleSetHeader header)
        => TryReadTriangleSetHeader(data, offset, expectedVertexMin, out header, out _, out _);

    // fieldBaseOffset receives the absolute byte offset of the vertexMin field for the matched header,
    // so the reinserter can patch vertexMin/vertexMax/facePointStart/polygonCount in place. The four
    // u32 fields are always contiguous from this offset, regardless of which header variant matched.
    // boneSetOffset receives the offset of the bone-set (palette) index field, so skinned reinsertion can
    // repoint each submesh to its rebuilt palette.
    private static bool TryReadTriangleSetHeader(byte[] data, int offset, int expectedVertexMin, out TriangleSetHeader header, out int fieldBaseOffset, out int boneSetOffset)
    {
        header = default;
        if (TryReadCompactTriangleSetHeader(data, offset, expectedVertexMin, out header, out fieldBaseOffset, out boneSetOffset))
        {
            return true;
        }

        if (HasCompactTriangleSetHeaderAhead(data, offset, expectedVertexMin))
        {
            boneSetOffset = offset;
            return false;
        }

        if (TryReadTriangleSetHeaderVariant(
                data,
                offset,
                expectedVertexMin,
                fieldOffset: 0x24,
                boneSetIndex: checked((int)Math.Min(ReadUInt32(data, offset + 0x18), int.MaxValue)),
                requireLegacyMarker: true,
                out header,
                out fieldBaseOffset,
                out boneSetOffset))
        {
            return true;
        }

        return TryReadTriangleSetHeaderVariant(
            data,
            offset,
            expectedVertexMin,
            fieldOffset: 0x20,
            boneSetIndex: checked((int)Math.Min(ReadUInt32(data, offset + 0x18), int.MaxValue)),
            requireLegacyMarker: false,
            out header,
            out fieldBaseOffset,
            out boneSetOffset);
    }

    private static bool TryReadCompactTriangleSetHeader(
        byte[] data,
        int offset,
        int expectedVertexMin,
        out TriangleSetHeader header,
        out int fieldBaseOffset,
        out int boneSetOffset)
    {
        header = default;
        fieldBaseOffset = offset + 8;
        boneSetOffset = offset;
        if (offset + 24 > data.Length)
        {
            return false;
        }

        var rawBoneSet = ReadUInt32(data, offset);
        var singleBindMarker = ReadUInt32(data, offset + 4);
        if (rawBoneSet > 512 || singleBindMarker != 1)
        {
            return false;
        }

        var rawVertexMin = ReadUInt32(data, offset + 8);
        var rawVertexMax = ReadUInt32(data, offset + 12);
        var rawFacePointStart = ReadUInt32(data, offset + 16);
        var rawPolygonCount = ReadUInt32(data, offset + 20);
        if (rawVertexMin > int.MaxValue ||
            rawVertexMax > int.MaxValue ||
            rawFacePointStart > int.MaxValue ||
            rawPolygonCount > int.MaxValue)
        {
            return false;
        }

        var vertexMin = (int)rawVertexMin;
        var vertexMax = (int)rawVertexMax;
        var facePointStart = (int)rawFacePointStart;
        var polygonCount = (int)rawPolygonCount;
        if (vertexMin != expectedVertexMin ||
            vertexMin > vertexMax ||
            vertexMax > ushort.MaxValue ||
            facePointStart > 2000000 ||
            polygonCount <= 0 ||
            polygonCount > 200000)
        {
            return false;
        }

        header = new TriangleSetHeader(
            checked((int)rawBoneSet),
            vertexMin,
            vertexMax,
            facePointStart,
            polygonCount);
        return true;
    }

    private static bool HasCompactTriangleSetHeaderAhead(byte[] data, int offset, int expectedVertexMin)
    {
        var end = Math.Min(data.Length - 24, offset + 0x40);
        for (var lookahead = offset + 1; lookahead <= end; lookahead++)
        {
            if (TryReadCompactTriangleSetHeader(data, lookahead, expectedVertexMin, out _, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadTriangleSetHeaderVariant(
        byte[] data,
        int offset,
        int expectedVertexMin,
        int fieldOffset,
        int boneSetIndex,
        bool requireLegacyMarker,
        out TriangleSetHeader header,
        out int fieldBaseOffset,
        out int boneSetOffset)
    {
        header = default;
        fieldBaseOffset = offset + fieldOffset;
        boneSetOffset = offset + 0x18;
        if (offset + fieldOffset + 16 > data.Length)
        {
            return false;
        }

        if (requireLegacyMarker &&
            (ReadUInt32(data, offset + 0x1C) != uint.MaxValue ||
             ReadUInt32(data, offset + 0x20) != 1))
        {
            return false;
        }

        if (!requireLegacyMarker)
        {
            var possibleBoneSet = ReadUInt32(data, offset + 0x18);
            var possibleSingleBind = ReadUInt32(data, offset + 0x1C);
            if (possibleBoneSet > 512 || possibleSingleBind > 512)
            {
                return false;
            }
        }

        var rawVertexMin = ReadUInt32(data, offset + fieldOffset);
        var rawVertexMax = ReadUInt32(data, offset + fieldOffset + 4);
        var rawFacePointStart = ReadUInt32(data, offset + fieldOffset + 8);
        var rawPolygonCount = ReadUInt32(data, offset + fieldOffset + 12);
        if (rawVertexMin > int.MaxValue ||
            rawVertexMax > int.MaxValue ||
            rawFacePointStart > int.MaxValue ||
            rawPolygonCount > int.MaxValue)
        {
            return false;
        }

        var vertexMin = (int)rawVertexMin;
        var vertexMax = (int)rawVertexMax;
        var facePointStart = (int)rawFacePointStart;
        var polygonCount = (int)rawPolygonCount;
        if (vertexMin != expectedVertexMin ||
            vertexMin > vertexMax ||
            vertexMax > ushort.MaxValue ||
            facePointStart > 2000000 ||
            polygonCount <= 0 ||
            polygonCount > 200000)
        {
            return false;
        }

        header = new TriangleSetHeader(
            Math.Max(0, boneSetIndex),
            vertexMin,
            vertexMax,
            facePointStart,
            polygonCount);
        return true;
    }

    private static Dictionary<string, string> ReadTextureNames(byte[] data, int start, int end)
    {
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        for (var offset = start; offset + 6 <= end; offset++)
        {
            if (!IsD3dtxSuffix(data, offset))
            {
                continue;
            }

            var nameStart = offset;
            while (nameStart > start && IsTextureNameByte(data[nameStart - 1]))
            {
                nameStart--;
            }

            var length = offset + 6 - nameStart;
            if (length <= 6 || length > 128)
            {
                continue;
            }

            var name = Encoding.ASCII.GetString(data, nameStart, length);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var slot = i == 0 ? "diffuse" : ClassifyTextureSlot(name, textures);
            textures[slot] = name;
        }

        return textures;
    }

    private static string ClassifyTextureSlot(string name, Dictionary<string, string> existing)
    {
        var lower = name.ToLowerInvariant();
        if ((lower.StartsWith("bmap_", StringComparison.Ordinal) ||
             lower.Contains("_bump", StringComparison.Ordinal) ||
             lower.Contains("_nm", StringComparison.Ordinal)) &&
            !existing.ContainsKey("bump"))
        {
            return "bump";
        }

        if ((lower.Contains("spec", StringComparison.Ordinal) ||
             lower.Contains("gloss", StringComparison.Ordinal)) &&
            !existing.ContainsKey("specular"))
        {
            return "specular";
        }

        if ((lower.Contains("environ", StringComparison.Ordinal) ||
             lower.Contains("reflect", StringComparison.Ordinal)) &&
            !existing.ContainsKey("environment"))
        {
            return "environment";
        }

        if (!existing.ContainsKey("bake"))
        {
            return "bake";
        }

        return $"tex{existing.Count + 1}";
    }

    private static FaceSection ReadFaceSection(byte[] data, int scanStart, IReadOnlyList<TriangleSetInfo> infos)
    {
        FaceSection? fallback = null;
        for (var offset = scanStart; offset + 17 < data.Length; offset++)
        {
            if (data[offset] != 0x30 || data[offset + 1] != 0x65)
            {
                continue;
            }

            var rawFaceCount = ReadUInt32(data, offset + 5);
            var faceFormat = ReadUInt32(data, offset + 9);
            if (rawFaceCount == 0 || rawFaceCount > 1000000 || faceFormat > 1)
            {
                continue;
            }

            var faceCount = (int)rawFaceCount;
            var bodyOffset = offset + 13;
            List<int> indices;
            int nextOffset;
            try
            {
                indices = faceFormat == 1
                    ? ReadCompressedIndices(data, bodyOffset, faceCount, out nextOffset)
                    : ReadRawIndices(data, bodyOffset, faceCount, out nextOffset);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            catch (EndOfStreamException)
            {
                continue;
            }

            if (!LooksLikeFirstVertexBufferHeader(data, nextOffset))
            {
                continue;
            }

            var vertexCount = ReadUInt32(data, nextOffset);
            var maxVertex = infos.Max(info => info.VertexMax);
            var minFacePointCount = infos.Max(info => (long)info.FacePointStart + info.PolygonCount * 3L);
            if (maxVertex >= vertexCount || minFacePointCount > indices.Count)
            {
                fallback ??= new FaceSection(indices, nextOffset, offset);
                continue;
            }

            return new FaceSection(indices, nextOffset, offset);
        }

        if (fallback is not null)
        {
            return fallback;
        }

        throw new InvalidDataException("Back to the Future face section was not found.");
    }

    private static bool LooksLikeFirstVertexBufferHeader(byte[] data, int offset)
    {
        if (offset + 16 > data.Length)
        {
            return false;
        }

        var count = ReadUInt32(data, offset);
        var stride = ReadUInt32(data, offset + 4);
        var type = ReadUInt32(data, offset + 8);
        var flags = ReadUInt32(data, offset + 12);
        if (count == 0 ||
            count > 200000 ||
            stride < 12 ||
            stride > 128 ||
            type != 1 ||
            flags > 16)
        {
            return false;
        }

        var dataEnd = (long)offset + 16 + (long)count * stride;
        return dataEnd <= data.Length;
    }

    private static List<int> ReadCompressedIndices(byte[] data, int offset, int targetCount, out int nextOffset)
    {
        var first = ReadUInt16(data, offset);
        var bodySize = checked((int)ReadUInt32(data, offset + 2));
        var bodyOffset = offset + 6;
        if (bodySize == 4 && first == 0)
        {
            nextOffset = bodyOffset + bodySize;
            return [0, 1, 2, 2, 1, 3];
        }

        if (bodyOffset + bodySize > data.Length)
        {
            throw new EndOfStreamException("Back to the Future compressed face stream exceeds file bounds.");
        }

        var indices = new List<int>(targetCount) { first };
        var accumulator = first;
        var bitPosition = 0;
        var totalBits = bodySize * 8;
        while (bitPosition + 11 <= totalBits && indices.Count < targetCount)
        {
            var deltaWidth = ReadBits(data, bodyOffset, bitPosition, 4);
            bitPosition += 4;
            var groupCount = ReadBits(data, bodyOffset, bitPosition, 7);
            bitPosition += 7;
            if (groupCount == 0)
            {
                break;
            }

            var bitsPerIndex = 1 + deltaWidth;
            for (var i = 0; i < groupCount && indices.Count < targetCount; i++)
            {
                if (bitPosition + bitsPerIndex > totalBits)
                {
                    break;
                }

                var sign = ReadBits(data, bodyOffset, bitPosition, 1);
                bitPosition++;
                var magnitude = deltaWidth > 0 ? ReadBits(data, bodyOffset, bitPosition, deltaWidth) : 0;
                bitPosition += deltaWidth;
                accumulator = (ushort)((accumulator + (sign == 1 ? -magnitude : magnitude)) & 0xFFFF);
                indices.Add(accumulator);
            }
        }

        nextOffset = bodyOffset + bodySize;
        return indices;
    }

    private static List<int> ReadRawIndices(byte[] data, int offset, int count, out int nextOffset)
    {
        if (offset + count * 2 > data.Length)
        {
            throw new EndOfStreamException("Back to the Future raw face stream exceeds file bounds.");
        }

        var indices = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            indices.Add(ReadUInt16(data, offset + i * 2));
        }

        nextOffset = offset + count * 2;
        return indices;
    }

    private static VertexBuffers ReadVertexBuffers(byte[] data, int offset)
    {
        var positions = new List<(float X, float Y, float Z)>();
        var normals = new List<(float X, float Y, float Z)>();
        var binormals = new List<(float X, float Y, float Z)>();
        var uv1 = new List<(float U, float V)>();
        var uv2 = new List<(float U, float V)>();
        var weights = new List<(float A, float B, float C, float D)>();
        var bones = new List<(int A, int B, int C, int D)>();
        var uvLayer = 0;
        var normalLayer = 0;

        while (offset + 16 <= data.Length)
        {
            var rawCount = ReadUInt32(data, offset);
            var rawStride = ReadUInt32(data, offset + 4);
            var rawType = ReadUInt32(data, offset + 8);
            var flags = ReadUInt32(data, offset + 12);
            if (rawCount > int.MaxValue || rawStride > int.MaxValue || rawType > int.MaxValue)
            {
                break;
            }

            var count = (int)rawCount;
            var stride = (int)rawStride;
            var type = (int)rawType;
            if (count <= 0 || count > 200000 || stride <= 0 || stride > 128 || type <= 0 || type > 20 || flags > 16)
            {
                break;
            }

            var dataOffset = offset + 16;
            var dataLength = (long)count * stride;
            if (dataOffset + dataLength > data.Length)
            {
                break;
            }

            switch (type)
            {
                case 1 when stride >= 12:
                    positions = ReadVec3Array(data, dataOffset, count, stride);
                    break;
                case 2 when stride >= 12:
                    if (normalLayer == 0)
                    {
                        normals = ReadVec3Array(data, dataOffset, count, stride);
                    }
                    else if (normalLayer == 1)
                    {
                        binormals = ReadVec3Array(data, dataOffset, count, stride);
                    }
                    normalLayer++;
                    break;
                case 3 when stride >= 8:
                    var target = uvLayer == 0 ? uv1 : uv2;
                    target.AddRange(ReadUvArray(data, dataOffset, count, stride));
                    uvLayer++;
                    break;
                case 4 when stride >= 12:
                    weights = ReadWeightArray(data, dataOffset, count, stride);
                    break;
                case 5 when stride >= 4:
                    bones = ReadBoneArray(data, dataOffset, count, stride);
                    break;
            }

            offset = dataOffset + checked((int)dataLength);
        }

        return new VertexBuffers(positions, normals, binormals, uv1, uv2, weights, bones);
    }

    private static MeshData BuildFallbackMesh(string name, VertexBuffers vertexBuffers, FaceSection faceSection)
    {
        var mesh = new MeshData { Name = name, Version = 1 };
        var submesh = new SubmeshData { Name = "mesh", MaterialName = "mesh" };
        for (var vertex = 0; vertex < vertexBuffers.Positions.Count; vertex++)
        {
            var p = vertexBuffers.Positions[vertex];
            var n = ValueOrDefault(vertexBuffers.Normals, vertex, (0f, 1f, 0f));
            var b = ValueOrDefault(vertexBuffers.Binormals, vertex, (0f, 0f, 0f));
            var uv = ValueOrDefault(vertexBuffers.Uv1, vertex, (0f, 0f));
            var uv2 = ValueOrDefault(vertexBuffers.Uv2, vertex, uv);
            var bones = ValueOrDefault(vertexBuffers.Bones, vertex, (0, 0, 0, 0));
            var weights = ValueOrDefault(vertexBuffers.Weights, vertex, (1f, 0f, 0f, 0f));
            submesh.Vertices.Add(new VertexData(
                p.Item1, p.Item2, p.Item3,
                n.Item1, n.Item2, n.Item3,
                uv.Item1, uv.Item2,
                uv2.Item1, uv2.Item2,
                uv2.Item1, uv2.Item2,
                uv2.Item1, uv2.Item2,
                bones.Item1, bones.Item2, bones.Item3, bones.Item4,
                weights.Item1, weights.Item2, weights.Item3, weights.Item4,
                BinormalX: b.Item1,
                BinormalY: b.Item2,
                BinormalZ: b.Item3));
        }

        for (var index = 0; index + 2 < faceSection.Indices.Count; index += 3)
        {
            AddFaceIfValid(submesh, faceSection.Indices[index], faceSection.Indices[index + 1], faceSection.Indices[index + 2], vertexStart: 0);
        }

        mesh.Submeshes.Add(submesh);
        return mesh;
    }

    private static void AppendTriangleStripTail(
        SubmeshData submesh,
        IReadOnlyList<int> indices,
        int start,
        int end,
        int vertexStart,
        IReadOnlyList<(float X, float Y, float Z)> normals)
    {
        if (end - start <= 2)
        {
            return;
        }

        var f1 = indices[start];
        var f2 = indices[start + 1];
        var direction = 1;
        for (var index = start + 2; index < end; index++)
        {
            var f3 = indices[index];
            direction *= -1;
            var a = direction > 0 ? f3 : f2;
            var b = direction > 0 ? f2 : f3;
            var c = f1;
            if (ShouldFlipByNormals(submesh, normals, a, b, c, vertexStart))
            {
                (a, b) = (b, a);
            }

            AddFaceIfValid(submesh, a, b, c, vertexStart);
            f1 = f2;
            f2 = f3;
        }
    }

    private static bool ShouldFlipByNormals(
        SubmeshData submesh,
        IReadOnlyList<(float X, float Y, float Z)> normals,
        int a,
        int b,
        int c,
        int vertexStart)
    {
        var localA = a - vertexStart;
        var localB = b - vertexStart;
        var localC = c - vertexStart;
        if (localA < 0 || localB < 0 || localC < 0 ||
            localA >= submesh.Vertices.Count || localB >= submesh.Vertices.Count || localC >= submesh.Vertices.Count ||
            a >= normals.Count || b >= normals.Count || c >= normals.Count)
        {
            return false;
        }

        var va = submesh.Vertices[localA];
        var vb = submesh.Vertices[localB];
        var vc = submesh.Vertices[localC];
        var ux = vb.X - va.X;
        var uy = vb.Y - va.Y;
        var uz = vb.Z - va.Z;
        var vx = vc.X - va.X;
        var vy = vc.Y - va.Y;
        var vz = vc.Z - va.Z;
        var fx = uy * vz - uz * vy;
        var fy = uz * vx - ux * vz;
        var fz = ux * vy - uy * vx;
        var normalX = normals[a].X + normals[b].X + normals[c].X;
        var normalY = normals[a].Y + normals[b].Y + normals[c].Y;
        var normalZ = normals[a].Z + normals[b].Z + normals[c].Z;
        return fx * normalX + fy * normalY + fz * normalZ < 0f;
    }

    private static void AddFaceIfValid(SubmeshData submesh, int a, int b, int c, int vertexStart)
    {
        if (a == b || b == c || c == a)
        {
            return;
        }

        var localA = a - vertexStart;
        var localB = b - vertexStart;
        var localC = c - vertexStart;
        if (localA >= 0 && localB >= 0 && localC >= 0 &&
            localA < submesh.Vertices.Count && localB < submesh.Vertices.Count && localC < submesh.Vertices.Count)
        {
            submesh.Faces.Add((localA, localB, localC));
        }
    }

    private static List<(float X, float Y, float Z)> ReadVec3Array(byte[] data, int offset, int count, int stride)
    {
        var result = new List<(float X, float Y, float Z)>(count);
        for (var i = 0; i < count; i++)
        {
            var vertexOffset = offset + i * stride;
            result.Add((ReadSingle(data, vertexOffset), ReadSingle(data, vertexOffset + 4), ReadSingle(data, vertexOffset + 8)));
        }

        return result;
    }

    private static List<(float U, float V)> ReadUvArray(byte[] data, int offset, int count, int stride)
    {
        var result = new List<(float U, float V)>(count);
        for (var i = 0; i < count; i++)
        {
            var vertexOffset = offset + i * stride;
            result.Add((ReadSingle(data, vertexOffset), 1f - ReadSingle(data, vertexOffset + 4)));
        }

        return result;
    }

    private static List<(float A, float B, float C, float D)> ReadWeightArray(byte[] data, int offset, int count, int stride)
    {
        var result = new List<(float A, float B, float C, float D)>(count);
        for (var i = 0; i < count; i++)
        {
            var vertexOffset = offset + i * stride;
            var a = ReadSingle(data, vertexOffset);
            var b = ReadSingle(data, vertexOffset + 4);
            var c = ReadSingle(data, vertexOffset + 8);
            var d = Math.Clamp(1f - a - b - c, 0f, 1f);
            result.Add((a, b, c, d));
        }

        return result;
    }

    private static List<(int A, int B, int C, int D)> ReadBoneArray(byte[] data, int offset, int count, int stride)
    {
        var result = new List<(int A, int B, int C, int D)>(count);
        for (var i = 0; i < count; i++)
        {
            var vertexOffset = offset + i * stride;
            result.Add((data[vertexOffset] / 4, data[vertexOffset + 1] / 4, data[vertexOffset + 2] / 4, data[vertexOffset + 3] / 4));
        }

        return result;
    }

    private static T ValueOrDefault<T>(IReadOnlyList<T> values, int index, T fallback)
        => index >= 0 && index < values.Count ? values[index] : fallback;

    private static bool ContainsD3dtx(byte[] data, int start, int end)
    {
        for (var offset = start; offset + 6 <= end; offset++)
        {
            if (IsD3dtxSuffix(data, offset))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsD3dtxSuffix(byte[] data, int offset)
        => offset + 6 <= data.Length &&
           data[offset] == (byte)'.' &&
           char.ToLowerInvariant((char)data[offset + 1]) == 'd' &&
           data[offset + 2] == (byte)'3' &&
           char.ToLowerInvariant((char)data[offset + 3]) == 'd' &&
           char.ToLowerInvariant((char)data[offset + 4]) == 't' &&
           char.ToLowerInvariant((char)data[offset + 5]) == 'x';

    private static bool IsTextureNameByte(byte value)
        => value is >= (byte)'a' and <= (byte)'z' ||
           value is >= (byte)'A' and <= (byte)'Z' ||
           value is >= (byte)'0' and <= (byte)'9' ||
           value is (byte)'_' or (byte)'-' or (byte)'.';

    private static int ReadBits(byte[] data, int offset, int bitOffset, int bitCount)
    {
        var result = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var absoluteBit = bitOffset + i;
            var byteIndex = offset + (absoluteBit >> 3);
            var bitIndex = absoluteBit & 7;
            if ((data[byteIndex] & (1 << bitIndex)) != 0)
            {
                result |= 1 << i;
            }
        }

        return result;
    }

    private static void SkipSizedBlock(DataReader reader)
    {
        var size = checked((int)reader.ReadUInt32());
        if (size < 4)
        {
            throw new InvalidDataException($"Invalid Back to the Future sized block at 0x{reader.Position - 4:X}: {size}.");
        }

        reader.Skip(size - 4);
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static ushort ReadUInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static float ReadSingle(byte[] data, int offset)
        => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

    private sealed record TriangleSetInfo(
        int Index,
        int BoneSetIndex,
        int VertexMin,
        int VertexMax,
        int FacePointStart,
        int PolygonCount,
        Dictionary<string, string> TextureNames,
        int FieldBaseOffset = -1,
        int BoneSetFieldOffset = -1);

    private readonly record struct TriangleSetHeader(
        int BoneSetIndex,
        int VertexMin,
        int VertexMax,
        int FacePointStart,
        int PolygonCount);

    private sealed record FaceSection(List<int> Indices, int NextOffset, int SectionOffset = -1);

    private sealed record BonePaletteInfo(
        IReadOnlyList<ulong[]> Palettes,
        IReadOnlyList<int> TriangleSetPaletteIndices);

    private sealed record BonePaletteCandidate(int Start, int End, ulong[] Palette);

    private sealed record VertexBuffers(
        List<(float X, float Y, float Z)> Positions,
        List<(float X, float Y, float Z)> Normals,
        List<(float X, float Y, float Z)> Binormals,
        List<(float U, float V)> Uv1,
        List<(float U, float V)> Uv2,
        List<(float A, float B, float C, float D)> Weights,
        List<(int A, int B, int C, int D)> Bones);
}
