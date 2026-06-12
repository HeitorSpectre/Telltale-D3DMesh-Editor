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
    private static readonly string[] TextureSlotsV18 =
    [
        "diffuse", "bake", "bump", "environment", "detail_diffuse", "detail_bump",
        "specular", "tex8", "gradient", "tex10", "shadow", "emissive",
        "alternate_bump", "occlusion"
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
        if (version is 17 or 18)
        {
            return ParseV18(reader, name, version);
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
            if (IsIntentionallyEmptyMesh(facePointCount, infos))
            {
                return mesh;
            }

            throw new InvalidDataException("No submesh with valid triangles was found.");
        }

        return mesh;
    }

    // Minecraft: Story Mode Season 1 PC uses D3DMesh v18. It is close to the v13/v14 layout but has
    // fourteen texture groups and stores vertex attributes across two vertex-buffer blocks.
    private static MeshData ParseV18(DataReader reader, string name, int version)
    {
        reader.Skip(4);
        reader.ReadVec3();
        reader.ReadVec3();

        var headerLength = checked((int)reader.ReadUInt32() - 4);
        reader.Skip(headerLength);

        reader.ReadUInt32();
        var submeshCount = checked((int)reader.ReadUInt32());
        if (submeshCount < 0 || submeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid submesh count: {submeshCount}");
        }

        var infos = new List<SubmeshInfo>();
        for (var i = 0; i < submeshCount; i++)
        {
            reader.ReadUInt32();
            var boneSet = ReadOptionalIndexPlusOne(reader);
            reader.ReadUInt32();
            reader.ReadUInt32();
            var vertexMin = checked((int)reader.ReadUInt32() + 1);
            var vertexMax = checked((int)reader.ReadUInt32() + 1);
            var facePointStart = checked((int)reader.ReadUInt32());
            var polygonCount = checked((int)reader.ReadUInt32());
            reader.ReadVec3();
            reader.ReadVec3();
            reader.ReadUInt32();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadUInt32();
            var headerEnd = reader.Position + checked((int)reader.ReadUInt32());

            var texIndices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var tex = 0; tex < TextureSlotsV18.Length; tex++)
            {
                var index = ReadOptionalIndexPlusOne(reader);
                if (index > 0)
                {
                    texIndices[TextureSlotsV18[tex]] = $"texture_{index}";
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

        ReadTextureGroups(reader, infos, TextureSlotsV18, finalTextureSubBlockBytes: 24);

        var uv1X = reader.ReadFloat();
        var uv1Y = reader.ReadFloat();
        reader.ReadFloat();
        reader.ReadFloat();
        var uv2X = reader.ReadFloat();
        var uv2Y = reader.ReadFloat();
        var uv3X = reader.ReadFloat();
        var uv3Y = reader.ReadFloat();
        var uv4X = reader.ReadFloat();
        var uv4Y = reader.ReadFloat();
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.ReadByte();
        reader.ReadUInt32();

        var facePointCount = checked((int)reader.ReadUInt32());
        var faceType = reader.ReadUInt32();
        reader.Skip(4);
        var rawFaces = new List<(int A, int B, int C)>();
        for (var i = 0; i < facePointCount / 3; i++)
        {
            rawFaces.Add(faceType switch
            {
                0 => (reader.ReadUInt16() + 1, reader.ReadUInt16() + 1, reader.ReadUInt16() + 1),
                2 => (reader.ReadUInt16BigEndian() + 1, reader.ReadUInt16BigEndian() + 1, reader.ReadUInt16BigEndian() + 1),
                _ => throw new InvalidDataException($"Unknown face index format: {faceType}"),
            });
        }

        var vertices = ReadVerticesV18(reader, uv1X, uv1Y, uv2X, uv2Y, uv3X, uv3Y, uv4X, uv4Y);
        if (vertices.Count == 0)
        {
            if (facePointCount == 0 && infos.All(info => info.PolygonCount == 0))
            {
                var emptyMesh = new MeshData { Name = name, Version = version };
                emptyMesh.BonePalettes.AddRange(bonePalettes);
                return emptyMesh;
            }

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
            if (IsIntentionallyEmptyMesh(facePointCount, infos))
            {
                return mesh;
            }

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

        ReadTextureGroups(reader, infos, TextureSlotsV13);

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
            if (IsIntentionallyEmptyMesh(facePointCount, infos))
            {
                return mesh;
            }

            throw new InvalidDataException("No submesh with valid triangles was found.");
        }

        return mesh;
    }

    private static bool IsIntentionallyEmptyMesh(int facePointCount, IReadOnlyList<SubmeshInfo> infos)
        => facePointCount == 0 && infos.All(static info => info.PolygonCount == 0);

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

    private static void ReadTextureGroups(
        DataReader reader,
        IReadOnlyList<SubmeshInfo> infos,
        IReadOnlyList<string> textureSlots,
        int finalTextureSubBlockBytes = 20)
    {
        reader.ReadUInt32();
        var groups = textureSlots.ToDictionary(slot => slot, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        for (var group = 0; group < textureSlots.Count; group++)
        {
            var slot = textureSlots[group];
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
                reader.Skip(finalTextureSubBlockBytes);
            }
        }

        foreach (var info in infos)
        {
            foreach (var slot in textureSlots)
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

        var vertexDataStart = reader.Position;
        var stride = VertexStrideV13(position, uv1, normals, weights, bones, colors, unknown1, binormals, tangents, uv2, uv3, uv4);
        var vertices = new List<VertexData>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            if (position.Format != 1)
            {
                throw new InvalidDataException($"Unknown position format: {position.Format}");
            }

            var vertexStart = vertexDataStart + i * stride;
            reader.Seek(vertexStart + (int)position.Offset);
            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var z = reader.ReadFloat();
            reader.Seek(vertexStart + (int)uv1.Offset);
            ReadUv(reader, uv1.Format, uv1X, uv1Y, out var u, out var v);
            reader.Seek(vertexStart + (int)uv2.Offset);
            ReadUv(reader, uv2.Format, uv2X, uv2Y, out var u2, out var v2);
            reader.Seek(vertexStart + (int)uv3.Offset);
            ReadUv(reader, uv3.Format, uv3X, uv3Y, out var u3, out var v3);
            reader.Seek(vertexStart + (int)uv4.Offset);
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
            reader.Seek(vertexStart + (int)bones.Offset);
            var (bone0, bone1, bone2, bone3) = ReadBones(reader, bones.Format);
            reader.Seek(vertexStart + (int)weights.Offset);
            var (weight0, weight1, weight2, weight3) = ReadWeights(reader, weights.Format);
            reader.Seek(vertexStart + (int)colors.Offset);
            var (colorR, colorG, colorB, colorA) = ReadColor(reader, colors.Format);
            reader.Seek(vertexStart + (int)unknown1.Offset);
            var unknown1Value = ReadUnknown(reader, unknown1.Format);

            var nx = 0f;
            var ny = 1f;
            var nz = 0f;
            reader.Seek(vertexStart + (int)normals.Offset);
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

            reader.Seek(vertexStart + (int)binormals.Offset);
            var (binormalX, binormalY, binormalZ, binormalW) = ReadVector4(reader, binormals.Format);
            reader.Seek(vertexStart + (int)tangents.Offset);
            var (tangentX, tangentY, tangentZ, tangentW) = ReadVector4(reader, tangents.Format);
            vertices.Add(new VertexData(x, y, z, nx, ny, nz, u, v, u2, v2, u3, v3, u4, v4, bone0, bone1, bone2, bone3, weight0, weight1, weight2, weight3, colorR, colorG, colorB, colorA, unknown1Value, binormalX, binormalY, binormalZ, binormalW, tangentX, tangentY, tangentZ, tangentW));
        }

        reader.Seek(vertexDataStart + vertexCount * stride);

        return vertices;
    }

    private static List<VertexData> ReadVerticesV18(
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
        var sections = new List<VertexBufferSection>();
        for (var bufferIndex = 0; bufferIndex < 2; bufferIndex++)
        {
            var vertexCount = checked((int)reader.ReadUInt32());
            var vertexStride = checked((int)reader.ReadUInt32());
            reader.Skip(0x08);
            reader.ReadUInt32();

            var attrs = new Dictionary<string, AttrDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["position"] = ReadAttr(reader),
                ["uv1"] = ReadAttr(reader),
                ["normals"] = ReadAttr(reader),
                ["weights"] = ReadAttr(reader),
                ["bones"] = ReadAttr(reader),
                ["colors"] = ReadAttr(reader),
                ["unknown1"] = ReadAttr(reader),
                ["binormals"] = ReadAttr(reader),
                ["tangents"] = ReadAttr(reader),
                ["uv2"] = ReadAttr(reader),
                ["uv3"] = ReadAttr(reader),
                ["uv4"] = ReadAttr(reader),
                ["uv5"] = ReadAttr(reader),
            };

            var dataStart = reader.Position;
            sections.Add(new VertexBufferSection(vertexCount, vertexStride, dataStart, attrs));
            reader.Seek(dataStart + checked(vertexCount * vertexStride));
        }

        var totalVertices = sections.Count == 0 ? 0 : sections.Max(section => section.VertexCount);
        var vertices = new List<VertexData>(totalVertices);
        for (var i = 0; i < totalVertices; i++)
        {
            var (x, y, z) = ReadPositionFromSections(reader, sections, i);
            var (u, v) = ReadUvFromSections(reader, sections, i, "uv1", uv1X, uv1Y);
            var (u2, v2) = ReadUvFromSections(reader, sections, i, "uv2", uv2X, uv2Y);
            var (u3, v3) = ReadUvFromSections(reader, sections, i, "uv3", uv3X, uv3Y);
            var (u4, v4) = ReadUvFromSections(reader, sections, i, "uv4", uv4X, uv4Y);
            if (u2 == 0f && v2 == 0f)
            {
                u2 = u;
                v2 = v;
            }
            if (u3 == 0f && v3 == 0f)
            {
                u3 = u2;
                v3 = v2;
            }
            if (u4 == 0f && v4 == 0f)
            {
                u4 = u3;
                v4 = v3;
            }

            var (nx, ny, nz) = ReadNormalFromSections(reader, sections, i);
            var (bone0, bone1, bone2, bone3) = ReadBonesFromSectionsV18(reader, sections, i);
            var (weight0, weight1, weight2, weight3) = ReadWeightsFromSectionsV18(reader, sections, i);
            var (colorR, colorG, colorB, colorA) = ReadColorFromSections(reader, sections, i);
            var unknown1Value = ReadUnknownFromSections(reader, sections, i);
            var (binormalX, binormalY, binormalZ, binormalW) = ReadVector4FromSections(reader, sections, i, "binormals");
            var (tangentX, tangentY, tangentZ, tangentW) = ReadVector4FromSections(reader, sections, i, "tangents");

            vertices.Add(new VertexData(x, y, z, nx, ny, nz, u, v, u2, v2, u3, v3, u4, v4, bone0, bone1, bone2, bone3, weight0, weight1, weight2, weight3, colorR, colorG, colorB, colorA, unknown1Value, binormalX, binormalY, binormalZ, binormalW, tangentX, tangentY, tangentZ, tangentW));
        }

        return vertices;
    }

    private static (float X, float Y, float Z) ReadPositionFromSections(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex)
    {
        if (!TryFindAttr(sections, vertexIndex, "position", out var section, out var attr))
        {
            return (0f, 0f, 0f);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        return attr.Format switch
        {
            1 => (reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
            _ => throw new InvalidDataException($"Unknown position format: {attr.Format}"),
        };
    }

    private static (float U, float V) ReadUvFromSections(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex,
        string attrName,
        float multX,
        float multY)
    {
        if (!TryFindAttr(sections, vertexIndex, attrName, out var section, out var attr))
        {
            return (0f, 0f);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        ReadUv(reader, attr.Format, multX, multY, out var u, out var v);
        return (u, v);
    }

    private static (float X, float Y, float Z) ReadNormalFromSections(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex)
    {
        if (!TryFindAttr(sections, vertexIndex, "normals", out var section, out var attr))
        {
            return (0f, 1f, 0f);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        if (attr.Format == 2)
        {
            return (reader.ReadSByte() / 127f, reader.ReadSByte() / 127f, reader.ReadSByte() / 127f);
        }
        if (attr.Format == 4)
        {
            return (reader.ReadInt16() / 32767f, reader.ReadInt16() / 32767f, reader.ReadInt16() / 32767f);
        }

        throw new InvalidDataException($"Unknown normal format: {attr.Format}");
    }

    private static (int Bone0, int Bone1, int Bone2, int Bone3) ReadBonesFromSectionsV18(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex)
    {
        if (!TryFindAttr(sections, vertexIndex, "bones", out var section, out var attr))
        {
            return (0, 0, 0, 0);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        return attr.Format switch
        {
            3 => (reader.ReadByte() / 4, reader.ReadByte() / 4, reader.ReadByte() / 4, reader.ReadByte() / 4),
            8 => (reader.ReadByte() / 3, reader.ReadByte() / 3, reader.ReadByte() / 3, reader.ReadByte() / 3),
            _ => throw new InvalidDataException($"Unknown bone format: {attr.Format}"),
        };
    }

    private static (float Weight0, float Weight1, float Weight2, float Weight3) ReadWeightsFromSectionsV18(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex)
    {
        if (!TryFindAttr(sections, vertexIndex, "weights", out var section, out var attr))
        {
            return (1f, 0f, 0f, 0f);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        return attr.Format switch
        {
            1 => NormalizeWeights(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), 0f),
            4 => NormalizeWeights(
                reader.ReadInt16() / 32767f,
                reader.ReadInt16() / 32767f,
                reader.ReadInt16() / 32767f,
                reader.ReadInt16() / 32767f),
            5 => NormalizeWeights(
                reader.ReadUInt16() / 65535f,
                reader.ReadUInt16() / 65535f,
                reader.ReadUInt16() / 65535f,
                reader.ReadUInt16() / 65535f),
            _ => throw new InvalidDataException($"Unknown weight format: {attr.Format}"),
        };
    }

    private static (float R, float G, float B, float A) ReadColorFromSections(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex)
    {
        if (!TryFindAttr(sections, vertexIndex, "colors", out var section, out var attr))
        {
            return (1f, 1f, 1f, 1f);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        return ReadColor(reader, attr.Format);
    }

    private static float ReadUnknownFromSections(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex)
    {
        if (!TryFindAttr(sections, vertexIndex, "unknown1", out var section, out var attr))
        {
            return 0f;
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        return ReadUnknown(reader, attr.Format);
    }

    private static (float X, float Y, float Z, float W) ReadVector4FromSections(
        DataReader reader,
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex,
        string attrName)
    {
        if (!TryFindAttr(sections, vertexIndex, attrName, out var section, out var attr))
        {
            return (0f, 0f, 0f, 0f);
        }

        reader.Seek(section.DataStart + vertexIndex * section.Stride + checked((int)attr.Offset));
        return ReadVector4(reader, attr.Format);
    }

    private static bool TryFindAttr(
        IReadOnlyList<VertexBufferSection> sections,
        int vertexIndex,
        string attrName,
        out VertexBufferSection section,
        out AttrDescriptor attr)
    {
        foreach (var candidate in sections)
        {
            if (vertexIndex < candidate.VertexCount &&
                candidate.Attributes.TryGetValue(attrName, out attr) &&
                attr.Format != 0)
            {
                section = candidate;
                return true;
            }
        }

        section = null!;
        attr = default;
        return false;
    }

    private static AttrDescriptor ReadAttr(DataReader reader)
    {
        return new AttrDescriptor(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
    }

    private static int VertexStrideV13(
        AttrDescriptor position,
        AttrDescriptor uv1,
        AttrDescriptor normals,
        AttrDescriptor weights,
        AttrDescriptor bones,
        AttrDescriptor colors,
        AttrDescriptor unknown1,
        AttrDescriptor binormals,
        AttrDescriptor tangents,
        AttrDescriptor uv2,
        AttrDescriptor uv3,
        AttrDescriptor uv4)
        => new[]
        {
            AttrEnd(position, PositionSize(position.Format)),
            AttrEnd(uv1, UvSize(uv1.Format)),
            AttrEnd(normals, NormalSize(normals.Format)),
            AttrEnd(weights, WeightSize(weights.Format)),
            AttrEnd(bones, BoneSize(bones.Format)),
            AttrEnd(colors, ColorSize(colors.Format)),
            AttrEnd(unknown1, UnknownSize(unknown1.Format)),
            AttrEnd(binormals, Vector4Size(binormals.Format)),
            AttrEnd(tangents, Vector4Size(tangents.Format)),
            AttrEnd(uv2, UvSize(uv2.Format)),
            AttrEnd(uv3, UvSize(uv3.Format)),
            AttrEnd(uv4, UvSize(uv4.Format)),
        }.Max();

    private static int AttrEnd(AttrDescriptor attr, int size)
        => size == 0 ? 0 : checked((int)attr.Offset + size);

    private static int PositionSize(uint format) => format switch
    {
        0 => 0,
        1 => 12,
        _ => throw new InvalidDataException($"Unknown position format: {format}"),
    };

    private static int UvSize(uint format) => format switch
    {
        0 => 0,
        1 => 8,
        4 => 4,
        5 => 4,
        11 => 4,
        _ => throw new InvalidDataException($"Unknown UV format: {format}"),
    };

    private static int NormalSize(uint format) => format switch
    {
        0 => 0,
        2 => 4,
        4 => 8,
        _ => throw new InvalidDataException($"Unknown normal format: {format}"),
    };

    private static int WeightSize(uint format) => format switch
    {
        0 => 0,
        1 => 12,
        4 => 8,
        5 => 8,
        _ => throw new InvalidDataException($"Unknown weight format: {format}"),
    };

    private static int BoneSize(uint format) => format switch
    {
        0 => 0,
        3 => 4,
        8 => 4,
        _ => throw new InvalidDataException($"Unknown bone format: {format}"),
    };

    private static int ColorSize(uint format) => format switch
    {
        0 => 0,
        1 => 16,
        3 => 4,
        _ => throw new InvalidDataException($"Unknown color format: {format}"),
    };

    private static int UnknownSize(uint format) => format switch
    {
        0 => 0,
        1 => 4,
        _ => throw new InvalidDataException($"Unknown attribute format: {format}"),
    };

    private static int Vector4Size(uint format) => format switch
    {
        0 => 0,
        2 => 4,
        4 => 8,
        _ => throw new InvalidDataException($"Unknown vector format: {format}"),
    };

    private static void SkipSizedBlock(DataReader reader)
    {
        var size = checked((int)reader.ReadUInt32() - 4);
        reader.Skip(size);
    }

    private static int ReadOptionalIndexPlusOne(DataReader reader)
    {
        var raw = reader.ReadUInt32();
        return raw == uint.MaxValue ? 0 : checked((int)raw + 1);
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
                u = reader.ReadHalf();
                v = -reader.ReadHalf() + 1f;
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

    private sealed record VertexBufferSection(
        int VertexCount,
        int Stride,
        int DataStart,
        IReadOnlyDictionary<string, AttrDescriptor> Attributes);

    private readonly record struct AttrDescriptor(uint Offset = 0, uint Count = 0, uint Format = 0);
}
