using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Mesh;

// Telltale .d3dmesh binary parser. Supports the generic MSV layout and versions 13/14
// (used by The Wolf Among Us and sibling engine builds). Reads geometry, UVs (1-4), normals,
// skinning bones/weights, and bone palettes. Read/extract only.
public static class D3DMeshParser
{
    private static readonly string[] TextureSlots =
    [
        "diffuse", "specular", "detail_diffuse", "detail_bump", "bake", "bump",
        "tex7", "tex8", "gradient", "environment", "sss"
    ];
    private static readonly string[] TextureSlotsV13 =
    [
        "diffuse", "bake", "bump", "environment", "detail_diffuse", "detail_bump",
        "specular", "tex8", "gradient", "tex10", "shadow"
    ];

    public static MeshData Parse(byte[] data)
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
        if (version < 2)
        {
            throw new NotSupportedException($"D3DMESH version {version} is not wired into this extractor yet.");
        }

        return ParseMsvMesh(reader, name, version);
    }

    private static MeshData ParseMsvMesh(DataReader reader, string name, int version)
    {
        if (version is 13 or 14)
        {
            return ParseV13(reader, name, version);
        }

        reader.Skip(version >= 13 ? 4 : 5);
        reader.ReadVec3();
        reader.ReadVec3();

        var headerALength = checked((int)reader.ReadUInt32() - 4);
        reader.Skip(headerALength);

        var submeshBlockStart = reader.Position;
        var submeshBlockSize = reader.ReadUInt32();
        var submeshCount = checked((int)reader.ReadUInt32());
        if (submeshCount < 0 || submeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid submesh count: {submeshCount}");
        }

        var infos = new List<SubmeshInfo>();
        for (var i = 0; i < submeshCount; i++)
        {
            reader.Skip(0x18);
            var boneSet = (int)reader.ReadUInt32();
            reader.Skip(0x08);
            var vertexMin = (int)reader.ReadUInt32();
            var vertexMax = (int)reader.ReadUInt32();
            var facePointStart = (int)reader.ReadUInt32();
            var polygonCount = (int)reader.ReadUInt32();
            reader.ReadVec3();
            reader.ReadVec3();
            reader.ReadVec3();

            reader.ReadUInt32();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            var matNum = (int)reader.ReadUInt32();
            reader.Skip(0x1C);

            var texNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var slot = 0; slot < TextureSlots.Length; slot++)
            {
                if (slot == 9)
                {
                    reader.Skip(0x19);
                }
                if (slot == 10)
                {
                    reader.Skip(0xB8);
                }

                var matHeaderLength = reader.ReadUInt32();
                var rawLength = reader.ReadUInt32();
                if (matHeaderLength > 8 && rawLength >= 6 && rawLength < 4096)
                {
                    var matNameLength = (int)rawLength - 6;
                    texNames[TextureSlots[slot]] = reader.ReadAscii(matNameLength);
                    reader.Skip(6);
                }
            }

            reader.ReadByte();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.Skip(0x08);

            infos.Add(new SubmeshInfo(
                Index: i,
                BoneSetIndex: boneSet,
                VertexMin: vertexMin,
                VertexMax: vertexMax,
                PolygonStart: facePointStart / 3,
                PolygonCount: polygonCount,
                MaterialIndex: matNum,
                TextureNames: texNames));
        }

        var submeshBlockEnd = submeshBlockStart + 4 + (int)Math.Min(submeshBlockSize, int.MaxValue - submeshBlockStart - 4);
        if (submeshBlockEnd > reader.Position)
        {
            reader.Seek(submeshBlockEnd);
        }

        SkipSizedBlock(reader);

        var bonePalettes = ReadBonePalettes(reader, 12);

        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.Skip(0x11);

        var materialGroupEnd = reader.Position + checked((int)reader.ReadUInt32());
        var materialGroupCount = checked((int)reader.ReadUInt32());
        for (var i = 0; i < materialGroupCount; i++)
        {
            reader.ReadUInt32();
            var matNameLength = checked((int)reader.ReadUInt32() - 6);
            if (matNameLength > 0)
            {
                reader.ReadAscii(matNameLength);
            }
            reader.Skip(6);
            for (var f = 0; f < 6; f++)
            {
                reader.ReadFloat();
            }
            SkipSizedBlock(reader);
            reader.Skip(6);
        }
        if (reader.Position < materialGroupEnd)
        {
            reader.Seek(materialGroupEnd);
        }

        for (var i = 0; i < 7; i++)
        {
            SkipSizedBlock(reader);
        }

        reader.ReadByte();
        reader.ReadByte();
        reader.Skip(0x0C);
        var uv1X = reader.ReadFloat();
        var uv1Y = reader.ReadFloat();
        var uv3X = reader.ReadFloat();
        var uv3Y = reader.ReadFloat();
        var uv2X = reader.ReadFloat();
        var uv2Y = reader.ReadFloat();
        var uv4X = 1f;
        var uv4Y = 1f;

        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadUInt32();

        var facePointCount = checked((int)reader.ReadUInt32());
        reader.Skip(8);
        var rawFaces = new List<(int A, int B, int C)>();
        for (var i = 0; i < facePointCount / 3; i++)
        {
            rawFaces.Add((reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()));
        }

        var vertices = ReadVertices(reader, uv1X, uv1Y, uv2X, uv2Y, uv3X, uv3Y, uv4X, uv4Y);
        if (vertices.Count == 0)
        {
            throw new InvalidDataException("No vertices found.");
        }

        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        foreach (var info in infos)
        {
            var submesh = new SubmeshData
            {
                Name = info.TextureNames.TryGetValue("diffuse", out var diffuse) ? diffuse : $"submesh_{info.Index}",
                MaterialName = info.TextureNames.TryGetValue("diffuse", out diffuse) ? diffuse : null,
                BonePaletteIndex = NormalizePaletteIndex(info.BoneSetIndex, bonePalettes.Count),
            };
            foreach (var texture in info.TextureNames)
            {
                submesh.TextureNames[texture.Key] = texture.Value;
            }

            var start = Math.Max(0, info.VertexMin);
            var endExclusive = Math.Min(vertices.Count, info.VertexMax + 1);
            for (var i = start; i < endExclusive; i++)
            {
                submesh.Vertices.Add(vertices[i]);
            }

            for (var i = 0; i < info.PolygonCount; i++)
            {
                var faceIndex = info.PolygonStart + i;
                if (faceIndex < 0 || faceIndex >= rawFaces.Count)
                {
                    continue;
                }

                var face = rawFaces[faceIndex];
                var a = face.A - start;
                var b = face.B - start;
                var c = face.C - start;
                if (a >= 0 && b >= 0 && c >= 0 &&
                    a < submesh.Vertices.Count && b < submesh.Vertices.Count && c < submesh.Vertices.Count)
                {
                    submesh.Faces.Add((a, b, c));
                }
            }

            if (submesh.Vertices.Count > 0 && submesh.Faces.Count > 0)
            {
                mesh.Submeshes.Add(submesh);
            }
        }

        if (mesh.Submeshes.Count == 0)
        {
            throw new InvalidDataException("No submesh with valid triangles was found.");
        }

        return mesh;
    }

    private static MeshData ParseV13(DataReader reader, string name, int version)
    {
        reader.Skip(4);
        reader.ReadVec3();
        reader.ReadVec3();

        var headerLength = (int)reader.ReadUInt32() - 4;
        reader.Skip(headerLength);

        reader.ReadUInt32();
        var submeshCount = (int)reader.ReadUInt32();
        if (submeshCount < 0 || submeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid submesh count: {submeshCount}");
        }

        var infos = new List<SubmeshInfo>();
        for (var i = 0; i < submeshCount; i++)
        {
            reader.ReadUInt32();
            var boneSet = (int)reader.ReadUInt32() + 1;
            reader.ReadUInt32();
            reader.ReadUInt32();
            var vertexMin = (int)reader.ReadUInt32() + 1;
            var vertexMax = (int)reader.ReadUInt32() + 1;
            var facePointStart = (int)reader.ReadUInt32();
            var polygonCount = (int)reader.ReadUInt32();
            reader.ReadVec3();
            reader.ReadVec3();
            reader.ReadUInt32();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadUInt32();
            var headerEnd = reader.Position + (int)reader.ReadUInt32();

            var texIndices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var tex = 0; tex < 11; tex++)
            {
                var index = (int)reader.ReadUInt32() + 1;
                if (index > 0)
                {
                    texIndices[TextureSlotsV13[Math.Min(tex, TextureSlotsV13.Length - 1)]] = $"texture_{index}";
                }
            }

            reader.Seek(headerEnd);
            reader.Skip(0x88);
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();

            infos.Add(new SubmeshInfo(
                Index: i,
                BoneSetIndex: boneSet,
                VertexMin: vertexMin,
                VertexMax: vertexMax,
                PolygonStart: facePointStart / 3 + 1,
                PolygonCount: polygonCount,
                MaterialIndex: 0,
                TextureNames: texIndices));
        }

        SkipSizedBlock(reader);

        var bonePalettes = ReadBonePalettes(reader, 56);

        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.Skip(0x08);

        ReadV13TextureGroups(reader, infos);

        var uv1X = reader.ReadFloat();
        var uv1Y = reader.ReadFloat();
        var uv4X = reader.ReadFloat();
        var uv4Y = reader.ReadFloat();
        var uv2X = reader.ReadFloat();
        var uv2Y = reader.ReadFloat();
        var uv3X = reader.ReadFloat();
        var uv3Y = reader.ReadFloat();
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.ReadByte();
        reader.ReadUInt32();

        var facePointCount = (int)reader.ReadUInt32();
        reader.Skip(8);
        var rawFaces = new List<(int A, int B, int C)>();
        for (var i = 0; i < facePointCount / 3; i++)
        {
            rawFaces.Add((reader.ReadUInt16() + 1, reader.ReadUInt16() + 1, reader.ReadUInt16() + 1));
        }

        var vertices = ReadVerticesV13(reader, uv1X, uv1Y, uv2X, uv2Y, uv3X, uv3Y, uv4X, uv4Y);
        if (vertices.Count == 0)
        {
            throw new InvalidDataException("No vertices found.");
        }

        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        foreach (var info in infos)
        {
            var materialName = info.TextureNames.TryGetValue("diffuse", out var diffuse) ? diffuse : $"material_{info.Index + 1}";
            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = NormalizePaletteIndex(info.BoneSetIndex, bonePalettes.Count),
            };
            foreach (var texture in info.TextureNames)
            {
                submesh.TextureNames[texture.Key] = texture.Value;
            }

            for (var vertex = info.VertexMin; vertex <= info.VertexMax; vertex++)
            {
                var index = vertex - 1;
                if (index >= 0 && index < vertices.Count)
                {
                    submesh.Vertices.Add(vertices[index]);
                }
            }

            for (var i = 0; i < info.PolygonCount; i++)
            {
                var faceIndex = info.PolygonStart - 1 + i;
                if (faceIndex < 0 || faceIndex >= rawFaces.Count)
                {
                    continue;
                }

                var face = rawFaces[faceIndex];
                var a = face.A - info.VertexMin;
                var b = face.B - info.VertexMin;
                var c = face.C - info.VertexMin;
                if (a >= 0 && b >= 0 && c >= 0 &&
                    a < submesh.Vertices.Count && b < submesh.Vertices.Count && c < submesh.Vertices.Count)
                {
                    submesh.Faces.Add((a, b, c));
                }
            }

            if (submesh.Vertices.Count > 0 && submesh.Faces.Count > 0)
            {
                mesh.Submeshes.Add(submesh);
            }
        }

        if (mesh.Submeshes.Count == 0)
        {
            throw new InvalidDataException("No submesh with valid triangles was found.");
        }

        return mesh;
    }

    private static List<VertexData> ReadVertices(
        DataReader reader,
        float uv1X,
        float uv1Y,
        float uv2X,
        float uv2Y,
        float uv3X,
        float uv3Y,
        float uv4X,
        float uv4Y)
    {
        var vertexCount = checked((int)reader.ReadUInt32());
        var vertexStride = checked((int)reader.ReadUInt32());
        reader.Skip(0x0C);
        reader.ReadUInt32();

        var names = new[]
        {
            "position", "uv1", "normals", "weights", "bones", "unknown1", "colors",
            "binormals", "tangents", "uv2", "uv3", "uv4", "unknown2"
        };
        var attrs = names.ToDictionary(name => name, _ => new AttrDescriptor());
        foreach (var attrName in names)
        {
            attrs[attrName] = new AttrDescriptor(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        }

        var vertices = new List<VertexData>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var x = 0f;
            var y = 0f;
            var z = 0f;
            var nx = 0f;
            var ny = 1f;
            var nz = 0f;
            var u = 0f;
            var v = 0f;

            if (attrs["position"].Format == 1)
            {
                x = reader.ReadFloat();
                y = reader.ReadFloat();
                z = reader.ReadFloat();
            }
            else
            {
                throw new InvalidDataException($"Unknown position format: {attrs["position"].Format}");
            }

            ReadUv(reader, attrs["uv1"].Format, uv1X, uv1Y, out u, out v);
            ReadUv(reader, attrs["uv2"].Format, uv2X, uv2Y, out var u2, out var v2);
            ReadUv(reader, attrs["uv3"].Format, uv3X, uv3Y, out var u3, out var v3);
            ReadUv(reader, attrs["uv4"].Format, uv4X, uv4Y, out var u4, out var v4);
            if (attrs["uv2"].Format == 0)
            {
                u2 = u;
                v2 = v;
            }
            if (attrs["uv3"].Format == 0)
            {
                u3 = u2;
                v3 = v2;
            }
            if (attrs["uv4"].Format == 0)
            {
                u4 = u3;
                v4 = v3;
            }
            var (bone0, bone1, bone2, bone3) = ReadBones(reader, attrs["bones"].Format);
            var (weight0, weight1, weight2, weight3) = ReadWeights(reader, attrs["weights"].Format);
            var (colorR, colorG, colorB, colorA) = ReadColor(reader, attrs["colors"].Format);
            var unknown1Value = ReadUnknown(reader, attrs["unknown1"].Format);

            if (attrs["normals"].Format == 2)
            {
                nx = reader.ReadSByte() / 127f;
                ny = reader.ReadSByte() / 127f;
                nz = reader.ReadSByte() / 127f;
                reader.ReadSByte();
            }
            else if (attrs["normals"].Format == 4)
            {
                nx = reader.ReadInt16() / 32767f;
                ny = reader.ReadInt16() / 32767f;
                nz = reader.ReadInt16() / 32767f;
                reader.ReadInt16();
            }
            else if (attrs["normals"].Format != 0)
            {
                throw new InvalidDataException($"Unknown normal format: {attrs["normals"].Format}");
            }

            var (binormalX, binormalY, binormalZ, binormalW) = ReadVector4(reader, attrs["binormals"].Format);
            var (tangentX, tangentY, tangentZ, tangentW) = ReadVector4(reader, attrs["tangents"].Format);
            SkipUnknown(reader, attrs["unknown2"].Format);

            vertices.Add(new VertexData(x, y, z, nx, ny, nz, u, v, u2, v2, u3, v3, u4, v4, bone0, bone1, bone2, bone3, weight0, weight1, weight2, weight3, colorR, colorG, colorB, colorA, unknown1Value, binormalX, binormalY, binormalZ, binormalW, tangentX, tangentY, tangentZ, tangentW));
        }

        _ = vertexStride;
        return vertices;
    }

    private static void ReadV13TextureGroups(DataReader reader, IReadOnlyList<SubmeshInfo> infos)
    {
        reader.ReadUInt32();
        var groups = TextureSlotsV13.ToDictionary(slot => slot, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        for (var group = 0; group < TextureSlotsV13.Length; group++)
        {
            var slot = TextureSlotsV13[group];
            var textureCount = checked((int)reader.ReadUInt32());
            if (textureCount < 0 || textureCount > 4096)
            {
                throw new InvalidDataException($"invalid texture count {textureCount} at 0x{reader.Position - 4:X}");
            }

            for (var i = 0; i < textureCount; i++)
            {
                reader.ReadUInt32();
                var hashLow = reader.ReadUInt32();
                var hashHigh = reader.ReadUInt32();
                groups[slot].Add(TextureHashDatabase.Resolve(hashLow, hashHigh));
                reader.Skip(12);
                reader.Skip(24);
                reader.ReadUInt32();
                reader.Skip(20);
            }
        }

        foreach (var info in infos)
        {
            foreach (var slot in TextureSlotsV13)
            {
                if (!info.TextureNames.TryGetValue(slot, out var reference) ||
                    !reference.StartsWith("texture_", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(reference["texture_".Length..], out var index) ||
                    index <= 0)
                {
                    continue;
                }

                var names = groups[slot];
                if (index <= names.Count && !string.IsNullOrWhiteSpace(names[index - 1]))
                {
                    info.TextureNames[slot] = names[index - 1];
                }
            }
        }
    }

    private static List<VertexData> ReadVerticesV13(
        DataReader reader,
        float uv1X,
        float uv1Y,
        float uv2X,
        float uv2Y,
        float uv3X,
        float uv3Y,
        float uv4X,
        float uv4Y)
    {
        var vertexCount = (int)reader.ReadUInt32();
        reader.ReadUInt32();
        reader.Skip(0x0C);
        reader.ReadUInt32();

        var position = ReadAttr(reader);
        var uv1 = ReadAttr(reader);
        var normals = ReadAttr(reader);
        var weights = ReadAttr(reader);
        var bones = ReadAttr(reader);
        var colors = ReadAttr(reader);
        var unknown1 = ReadAttr(reader);
        var binormals = ReadAttr(reader);
        var tangents = ReadAttr(reader);
        var uv2 = ReadAttr(reader);
        var uv3 = ReadAttr(reader);
        var uv4 = ReadAttr(reader);

        var vertices = new List<VertexData>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            if (position.Format != 1)
            {
                throw new InvalidDataException($"Unknown position format: {position.Format}");
            }

            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var z = reader.ReadFloat();
            ReadUv(reader, uv1.Format, uv1X, uv1Y, out var u, out var v);
            ReadUv(reader, uv2.Format, uv2X, uv2Y, out var u2, out var v2);
            ReadUv(reader, uv3.Format, uv3X, uv3Y, out var u3, out var v3);
            ReadUv(reader, uv4.Format, uv4X, uv4Y, out var u4, out var v4);
            if (uv2.Format == 0)
            {
                u2 = u;
                v2 = v;
            }
            if (uv3.Format == 0)
            {
                u3 = u2;
                v3 = v2;
            }
            if (uv4.Format == 0)
            {
                u4 = u3;
                v4 = v3;
            }
            var (bone0, bone1, bone2, bone3) = ReadBones(reader, bones.Format);
            var (weight0, weight1, weight2, weight3) = ReadWeights(reader, weights.Format);
            var (colorR, colorG, colorB, colorA) = ReadColor(reader, colors.Format);
            var unknown1Value = ReadUnknown(reader, unknown1.Format);

            var nx = 0f;
            var ny = 1f;
            var nz = 0f;
            if (normals.Format == 2)
            {
                nx = reader.ReadSByte() / 127f;
                ny = reader.ReadSByte() / 127f;
                nz = reader.ReadSByte() / 127f;
                reader.ReadSByte();
            }
            else if (normals.Format == 4)
            {
                nx = reader.ReadInt16() / 32767f;
                ny = reader.ReadInt16() / 32767f;
                nz = reader.ReadInt16() / 32767f;
                reader.ReadInt16();
            }
            else if (normals.Format != 0)
            {
                throw new InvalidDataException($"Unknown normal format: {normals.Format}");
            }

            var (binormalX, binormalY, binormalZ, binormalW) = ReadVector4(reader, binormals.Format);
            var (tangentX, tangentY, tangentZ, tangentW) = ReadVector4(reader, tangents.Format);
            vertices.Add(new VertexData(x, y, z, nx, ny, nz, u, v, u2, v2, u3, v3, u4, v4, bone0, bone1, bone2, bone3, weight0, weight1, weight2, weight3, colorR, colorG, colorB, colorA, unknown1Value, binormalX, binormalY, binormalZ, binormalW, tangentX, tangentY, tangentZ, tangentW));
        }

        return vertices;
    }

    private static AttrDescriptor ReadAttr(DataReader reader)
    {
        return new AttrDescriptor(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
    }

    private static void SkipSizedBlock(DataReader reader)
    {
        var size = checked((int)reader.ReadUInt32() - 4);
        reader.Skip(size);
    }

    private static List<ulong[]> ReadBonePalettes(DataReader reader, int entrySize)
    {
        reader.ReadUInt32();
        var paletteCount = checked((int)reader.ReadUInt32());
        var palettes = new List<ulong[]>(paletteCount);
        for (var i = 0; i < paletteCount; i++)
        {
            var boneCount = checked((int)reader.ReadUInt32());
            var hashes = new ulong[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                hashes[bone] = ((ulong)high << 32) | low;
                var remaining = entrySize - 8;
                if (remaining > 0)
                {
                    reader.Skip(remaining);
                }
            }

            palettes.Add(hashes);
        }

        return palettes;
    }

    private static int NormalizePaletteIndex(int rawIndex, int paletteCount)
    {
        if (paletteCount <= 0)
        {
            return 0;
        }

        if (rawIndex > 0 && rawIndex - 1 < paletteCount)
        {
            return rawIndex - 1;
        }

        if (rawIndex >= 0 && rawIndex < paletteCount)
        {
            return rawIndex;
        }

        return 0;
    }

    private static void ReadUv(DataReader reader, uint format, float multX, float multY, out float u, out float v)
    {
        u = 0f;
        v = 0f;
        switch (format)
        {
            case 0:
                return;
            case 1:
                u = reader.ReadFloat();
                v = -reader.ReadFloat() + 1f;
                return;
            case 4:
                u = reader.ReadInt16() / 32767f * multX;
                v = -(reader.ReadInt16() / 32767f * multY) + 1f;
                return;
            case 5:
                u = reader.ReadUInt16() / 65535f * multX;
                v = -(reader.ReadUInt16() / 65535f * multY) + 1f;
                return;
            case 11:
                u = reader.ReadHalf() * 2f;
                v = -(reader.ReadHalf() * 2f) + 1f;
                return;
            default:
                throw new InvalidDataException($"Unknown UV format: {format}");
        }
    }

    private static (int Bone0, int Bone1, int Bone2, int Bone3) ReadBones(DataReader reader, uint format)
    {
        switch (format)
        {
            case 0:
                return (0, 0, 0, 0);
            case 3:
            case 8:
                return (reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            default:
                throw new InvalidDataException($"Unknown bone format: {format}");
        }
    }

    private static (float Weight0, float Weight1, float Weight2, float Weight3) ReadWeights(DataReader reader, uint format)
    {
        switch (format)
        {
            case 0:
                return (1f, 0f, 0f, 0f);
            case 1:
                {
                    var w0 = reader.ReadFloat();
                    var w1 = reader.ReadFloat();
                    var w2 = reader.ReadFloat();
                    var w3 = Math.Clamp(1f - w0 - w1 - w2, 0f, 1f);
                    return NormalizeWeights(w0, w1, w2, w3);
                }
            case 4:
            case 5:
                return NormalizeWeights(
                    reader.ReadUInt16() / 65535f,
                    reader.ReadUInt16() / 65535f,
                    reader.ReadUInt16() / 65535f,
                    reader.ReadUInt16() / 65535f);
            default:
                throw new InvalidDataException($"Unknown weight format: {format}");
        }
    }

    private static (float Weight0, float Weight1, float Weight2, float Weight3) NormalizeWeights(float w0, float w1, float w2, float w3)
    {
        var total = w0 + w1 + w2 + w3;
        if (total <= 0.000001f)
        {
            return (1f, 0f, 0f, 0f);
        }

        return (w0 / total, w1 / total, w2 / total, w3 / total);
    }

    private static (float R, float G, float B, float A) ReadColor(DataReader reader, uint format)
    {
        switch (format)
        {
            case 0:
                return (1f, 1f, 1f, 1f);
            case 1:
                return (
                    Math.Clamp(reader.ReadFloat(), 0f, 1f),
                    Math.Clamp(reader.ReadFloat(), 0f, 1f),
                    Math.Clamp(reader.ReadFloat(), 0f, 1f),
                    Math.Clamp(reader.ReadFloat(), 0f, 1f));
            case 3:
                return (
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f);
            default:
                throw new InvalidDataException($"Unknown color format: {format}");
        }
    }

    private static (float X, float Y, float Z, float W) ReadVector4(DataReader reader, uint format)
    {
        switch (format)
        {
            case 0:
                return (0f, 0f, 0f, 0f);
            case 2:
                return (
                    reader.ReadSByte() / 127f,
                    reader.ReadSByte() / 127f,
                    reader.ReadSByte() / 127f,
                    reader.ReadSByte() / 127f);
            case 4:
                return (
                    reader.ReadInt16() / 32767f,
                    reader.ReadInt16() / 32767f,
                    reader.ReadInt16() / 32767f,
                    reader.ReadInt16() / 32767f);
            default:
                throw new InvalidDataException($"Unknown vector format: {format}");
        }
    }

    private static float ReadUnknown(DataReader reader, uint format)
    {
        switch (format)
        {
            case 0:
                return 0f;
            case 1:
                return reader.ReadFloat();
            default:
                throw new InvalidDataException($"Unknown attribute present: {format}");
        }
    }

    private static void SkipUnknown(DataReader reader, uint format)
    {
        switch (format)
        {
            case 0:
                return;
            case 1:
                reader.Skip(4);
                return;
            default:
                throw new InvalidDataException($"Unknown attribute present: {format}");
        }
    }

    private sealed record SubmeshInfo(
        int Index,
        int BoneSetIndex,
        int VertexMin,
        int VertexMax,
        int PolygonStart,
        int PolygonCount,
        int MaterialIndex,
        Dictionary<string, string> TextureNames);

    private readonly record struct AttrDescriptor(uint Offset = 0, uint Count = 0, uint Format = 0);
}
