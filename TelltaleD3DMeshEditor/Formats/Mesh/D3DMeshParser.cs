using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Mesh;

// Telltale .d3dmesh binary parser. Supports the generic MSV layout plus the v13/v14 and
// v17/v18 Telltale layouts. Reads geometry, UVs, normals, skinning bones/weights, and
// bone palettes. Read/extract only.
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
        if (version < 1)
        {
            throw new NotSupportedException($"D3DMESH version {version} is not wired into this extractor yet.");
        }

        if (version == 1)
        {
            try
            {
                return BackToTheFutureMeshParser.Parse(data, name, reader.Position);
            }
            catch (NotSupportedException)
            {
                return TelltaleToolkitMeshParser.ParseOldMesh(data, name);
            }
        }

        // The Tales from the Borderlands source leak stores some meshes in the older MTRE (ERTM)
        // container across several versions (5/6/9/10/11/12). They share one body layout (multi-stream
        // vertices, 304/288-byte submesh entries), so route every MTRE mesh through the dedicated reader.
        // The MTRE magic is unambiguous, so this must not depend on the selected profile (version 1 MTRE is
        // Back to the Future and is already handled above).
        if (header.Version == "MTRE")
        {
            return ParseErtmV5(reader, name, version);
        }

        return ParseMsvMesh(reader, name, version, data);
    }

    public static MeshData ParseFile(string path)
    {
        using var textureFolder = TextureHashDatabase.UseTextureFolder(Path.GetDirectoryName(Path.GetFullPath(path)));
        return Parse(File.ReadAllBytes(path));
    }

    private static MeshData ParseMsvMesh(DataReader reader, string name, int version, byte[] data)
    {
        if (version is 13 or 14)
        {
            return ParseV13(reader, name, version);
        }
        // The Tales from the Borderlands source-code leak ships v12 meshes (5VSM/4VSM) and v5 meshes
        // (ERTM container) that share the v13/14 body layout (4-byte lead, bounds, header, submesh table,
        // string/hash texture groups), unlike the older generic path. Route them through the V13 reader
        // so they open; other games keep the generic path.
        if (version == 12 && GameConfig.Current.Id == GameId.TalesFromTheBorderlandsOld)
        {
            return ParseV13(reader, name, version);
        }
        if (version is 17 or 18)
        {
            return ParseV18(reader, name, version);
        }
        if (version == 25)
        {
            return ParseV25(reader, name, version);
        }

        reader.Skip(version == 1 ? 1 : version >= 13 ? 4 : 5);
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
            var materialTint = new MaterialTint(
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat());
            reader.Skip(0x08);

            infos.Add(new SubmeshInfo(
                Index: i,
                BoneSetIndex: boneSet,
                VertexMin: vertexMin,
                VertexMax: vertexMax,
                PolygonStart: facePointStart / 3,
                PolygonCount: polygonCount,
                MaterialIndex: matNum,
                MaterialTint: materialTint,
                TextureNames: texNames));
        }

        var submeshBlockEnd = submeshBlockStart + 4 + (int)Math.Min(submeshBlockSize, int.MaxValue - submeshBlockStart - 4);
        if (submeshBlockEnd > reader.Position)
        {
            reader.Seek(submeshBlockEnd);
        }

        SkipSizedBlock(reader);

        var bonePaletteData = ReadBonePalettes(reader, 12);
        var bonePalettes = bonePaletteData.Palettes;

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

        var vertices = ReadVertices(reader, uv1X, uv1Y, uv2X, uv2Y, uv3X, uv3Y, uv4X, uv4Y, signedFormat4Weights: version <= 2);
        if (vertices.Count == 0)
        {
            throw new InvalidDataException("No vertices found.");
        }

        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        mesh.BonePaletteEntries.AddRange(bonePaletteData.Entries);
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
                submesh.Vertices.Add(ApplyMaterialTint(vertices[i], info.MaterialTint));
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

    // Tales from the Borderlands 2014/2015 and Minecraft: Story Mode use the v17/v18 family:
    // fourteen texture groups and vertex attributes stored across two vertex-buffer blocks.
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
            var materialTint = new MaterialTint(
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat());

            infos.Add(new SubmeshInfo(
                Index: i,
                BoneSetIndex: boneSet,
                VertexMin: vertexMin,
                VertexMax: vertexMax,
                PolygonStart: facePointStart / 3 + 1,
                PolygonCount: polygonCount,
                MaterialIndex: 0,
                MaterialTint: materialTint,
                TextureNames: texIndices));
        }

        SkipSizedBlock(reader);

        var bonePaletteData = ReadBonePalettes(reader, 56);
        var bonePalettes = bonePaletteData.Palettes;

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
                emptyMesh.BonePaletteEntries.AddRange(bonePaletteData.Entries);
                return emptyMesh;
            }

            throw new InvalidDataException("No vertices found.");
        }

        var multiStream = false;
        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        mesh.BonePaletteEntries.AddRange(bonePaletteData.Entries);
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

            AppendSubmeshGeometry(submesh, info, vertices, rawFaces, multiStream);

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

    private static MeshData ParseV25(DataReader reader, string name, int version)
    {
        reader.Skip(1);
        var materialCount = checked((int)reader.ReadUInt32());
        if (materialCount == 0)
        {
            return new MeshData { Name = name, Version = version };
        }

        var materialsByHash = new Dictionary<ulong, IReadOnlyDictionary<string, string>>();
        for (var i = 0; i < materialCount; i++)
        {
            var matLow = reader.ReadUInt32();
            var matHigh = reader.ReadUInt32();
            reader.Skip(8);
            var materialEnd = reader.Position + checked((int)reader.ReadUInt32());
            reader.Skip(12);
            var materialHashCount = checked((int)reader.ReadUInt32());
            reader.Skip(materialHashCount * 8);
            var parameterCount = checked((int)reader.ReadUInt32());
            var materialTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var p = 0; p < parameterCount && reader.Position < materialEnd; p++)
            {
                var sectionLow = reader.ReadUInt32();
                var sectionHigh = reader.ReadUInt32();
                var sectionCount = checked((int)reader.ReadUInt32());
                if (sectionHigh == 0x52A09151 && sectionLow == 0xF1C3F2C7)
                {
                    for (var t = 0; t < sectionCount; t++)
                    {
                        var typeLow = reader.ReadUInt32();
                        var typeHigh = reader.ReadUInt32();
                        var texLow = reader.ReadUInt32();
                        var texHigh = reader.ReadUInt32();
                        var slot = GetV25TextureSlot(typeHigh, typeLow);
                        if (slot is not null)
                        {
                            materialTextures[slot] = TextureHashDatabase.Resolve(texLow, texHigh);
                        }
                    }
                }
                else
                {
                    SkipV25MaterialParameter(reader, sectionHigh, sectionLow, sectionCount, materialEnd);
                }
            }

            materialsByHash[((ulong)matHigh << 32) | matLow] = materialTextures;
            reader.Seek(materialEnd);
        }

        // The geometry block (T3MeshData mesh data) is an inclusive sized block whose index/face buffer
        // begins exactly at its end. RTB's Michonne importer derives the face-data offset the same way:
        // the size is measured from the size field itself, so blockEnd = sizeFieldOffset + size. After
        // reading the leading u32 and the size u32, reader.Position is sizeFieldOffset + 4, hence
        // faceDataStart = reader.Position + size - 4.
        reader.ReadUInt32();
        var geometryBlockSize = checked((int)reader.ReadUInt32());
        var faceDataStart = reader.Position + geometryBlockSize - 4;

        var lodBlockEnd = reader.Position + checked((int)reader.ReadUInt32());
        var lodCount = checked((int)reader.ReadUInt32());
        var infos = new List<SubmeshInfo>();
        for (var lod = 1; lod <= lodCount; lod++)
        {
            var lodEntryEnd = reader.Position + checked((int)reader.ReadUInt32() - 4);
            var submeshCount = checked((int)reader.ReadUInt32());
            for (var i = 0; i < submeshCount; i++)
            {
                reader.ReadVec3();
                reader.ReadVec3();
                reader.ReadUInt32();
                reader.Skip(16);
                reader.ReadUInt32();
                var vertexMin = checked((int)reader.ReadUInt32() + 1);
                var vertexMax = checked((int)reader.ReadUInt32() + 1);
                var facePointStart = checked((int)reader.ReadUInt32());
                var polygonCount = checked((int)reader.ReadUInt32());
                var headerLength2 = reader.ReadUInt32();
                if (headerLength2 == 0x10)
                {
                    reader.Skip(8);
                }

                reader.ReadUInt32();
                var materialIndex = ReadOptionalIndexPlusOne(reader);
                var boneSet = ReadOptionalIndexPlusOne(reader);
                reader.ReadUInt32();

                if (lod == 1)
                {
                    infos.Add(new SubmeshInfo(
                        Index: infos.Count,
                        BoneSetIndex: boneSet,
                        VertexMin: vertexMin,
                        VertexMax: vertexMax,
                        PolygonStart: facePointStart / 3 + 1,
                        PolygonCount: polygonCount,
                        MaterialIndex: materialIndex,
                        MaterialTint: MaterialTint.White,
                        TextureNames: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                }
            }

            reader.ReadUInt32();
            reader.ReadUInt32();
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

        SkipV25SizedBlock(reader, static (r, count) => r.Skip(count * 76));

        var materialGroupEnd = reader.Position + checked((int)reader.ReadUInt32() - 4);
        var materialGroupCount = checked((int)reader.ReadUInt32());
        var materialTexturesByGroup = new List<IReadOnlyDictionary<string, string>>(materialGroupCount);
        for (var i = 0; i < materialGroupCount; i++)
        {
            reader.ReadUInt32();
            var matLow = reader.ReadUInt32();
            var matHigh = reader.ReadUInt32();
            reader.Skip(8);
            reader.Skip(24);
            reader.ReadUInt32();
            reader.Skip(16);
            reader.ReadUInt32();
            var key = ((ulong)matHigh << 32) | matLow;
            materialTexturesByGroup.Add(materialsByHash.TryGetValue(key, out var materialTextures)
                ? materialTextures
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        if (reader.Position < materialGroupEnd)
        {
            reader.Seek(materialGroupEnd);
        }

        SkipV25SizedBlock(reader, static (r, count) => r.Skip(count * 16));

        var bonePaletteBlockStart = reader.Position;
        reader.ReadUInt32();
        var bonePaletteCount = checked((int)reader.ReadUInt32());
        var bonePalettes = new List<ulong[]>(bonePaletteCount);
        var bonePaletteEntries = new List<List<BonePaletteEntryData>>(bonePaletteCount);
        for (var palette = 0; palette < bonePaletteCount; palette++)
        {
            var boneCount = checked((int)reader.ReadUInt32());
            var hashes = new ulong[boneCount];
            var entries = new List<BonePaletteEntryData>(boneCount);
            for (var bone = 0; bone < boneCount; bone++)
            {
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                var hash = ((ulong)high << 32) | low;
                hashes[bone] = hash;
                var minX = reader.ReadFloat();
                var minY = reader.ReadFloat();
                var minZ = reader.ReadFloat();
                var maxX = reader.ReadFloat();
                var maxY = reader.ReadFloat();
                var maxZ = reader.ReadFloat();
                reader.ReadUInt32();
                var centerX = reader.ReadFloat();
                var centerY = reader.ReadFloat();
                var centerZ = reader.ReadFloat();
                var radius = reader.ReadFloat();
                reader.ReadUInt32();
                entries.Add(new BonePaletteEntryData(hash, minX, minY, minZ, maxX, maxY, maxZ, centerX, centerY, centerZ, radius));
            }

            bonePalettes.Add(hashes);
            bonePaletteEntries.Add(entries);
        }

        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadVec3();
        reader.ReadVec3();
        reader.ReadUInt32();
        reader.Skip(16);
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        var vertexCount = checked((int)reader.ReadUInt32());
        reader.ReadUInt32();
        var uvLayerCount = checked((int)reader.ReadUInt32());
        var uvScales = new V25UvScale[6];
        for (var i = 0; i < uvScales.Length; i++)
        {
            uvScales[i] = new V25UvScale(1f, 1f, 0f, 0f);
        }

        for (var i = 0; i < uvLayerCount; i++)
        {
            var layer = checked((int)reader.ReadUInt32());
            var scale = new V25UvScale(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
            if (layer >= 0 && layer < uvScales.Length)
            {
                uvScales[layer] = scale;
            }
        }

        var descriptors = ReadV25VertexDescriptor(reader);
        reader.ReadByte();

        reader.Skip(12);
        var facePointCount = checked((int)reader.ReadUInt32());
        reader.ReadUInt32();
        reader.Skip(12);
        var v25VertexCount = checked((int)reader.ReadUInt32());
        reader.ReadUInt32();
        reader.Skip(12);
        var normalCount = checked((int)reader.ReadUInt32());
        reader.ReadUInt32();
        reader.Skip(12);
        var uvCount = checked((int)reader.ReadUInt32());
        reader.ReadUInt32();
        var totalVertices = Math.Max(vertexCount, v25VertexCount);
        reader.Seek(faceDataStart);
        var rawFaces = new List<(int A, int B, int C)>();
        for (var i = 0; i < facePointCount / 3; i++)
        {
            rawFaces.Add((reader.ReadUInt16() + 1, reader.ReadUInt16() + 1, reader.ReadUInt16() + 1));
        }

        var vertices = ReadV25Vertices(reader, totalVertices, normalCount, uvCount, descriptors, uvScales);
        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        mesh.BonePaletteEntries.AddRange(bonePaletteEntries);

        foreach (var info in infos)
        {
            var materialTextures = info.MaterialIndex > 0 && info.MaterialIndex <= materialTexturesByGroup.Count
                ? materialTexturesByGroup[info.MaterialIndex - 1]
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var materialName = materialTextures.TryGetValue("diffuse", out var diffuseName) &&
                               !string.IsNullOrWhiteSpace(diffuseName) &&
                               !diffuseName.Equals("undefined", StringComparison.OrdinalIgnoreCase)
                ? diffuseName
                : $"material_{info.Index + 1}";
            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = NormalizePaletteIndex(info.BoneSetIndex, bonePalettes.Count),
                SourceSubmeshIndex = info.Index,
            };
            foreach (var (slot, textureName) in materialTextures)
            {
                if (!string.IsNullOrWhiteSpace(textureName) &&
                    !textureName.Equals("undefined", StringComparison.OrdinalIgnoreCase))
                {
                    submesh.TextureNames[slot] = textureName;
                }
            }

            AppendSubmeshGeometry(submesh, info, vertices, rawFaces, multiStream: true);
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
            var materialTint = new MaterialTint(
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat());

            infos.Add(new SubmeshInfo(
                Index: i,
                BoneSetIndex: boneSet,
                VertexMin: vertexMin,
                VertexMax: vertexMax,
                PolygonStart: facePointStart / 3 + 1,
                PolygonCount: polygonCount,
                MaterialIndex: 0,
                MaterialTint: materialTint,
                TextureNames: texIndices));
        }

        SkipSizedBlock(reader);

        var bonePaletteData = ReadBonePalettes(reader, 56);
        var bonePalettes = bonePaletteData.Palettes;

        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        SkipSizedBlock(reader);
        reader.Skip(0x08);

        ReadTextureGroups(reader, infos, TextureSlotsV13);

        var uvScales = ReadUvScalesV13(reader);
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

        // The Tales from the Borderlands source-code leak (an older 2014 build) stores each vertex
        // attribute as its own non-interleaved buffer (position, uv, normal, ... each in a separate
        // 168-byte-header block) instead of one interleaved buffer. Detect that here and read all the
        // streams; otherwise fall back to the normal single interleaved buffer.
        var multiStream = IsMultiStreamVertexLayout(reader);
        var vertices = multiStream
            ? ReadVerticesMultiStream(reader)
            : ReadVerticesV13(
                reader,
                uvScales.Uv1X,
                uvScales.Uv1Y,
                uvScales.Uv2X,
                uvScales.Uv2Y,
                uvScales.Uv3X,
                uvScales.Uv3Y,
                uvScales.Uv4X,
                uvScales.Uv4Y);
        if (vertices.Count == 0)
        {
            throw new InvalidDataException("No vertices found.");
        }

        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        mesh.BonePaletteEntries.AddRange(bonePaletteData.Entries);
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

            AppendSubmeshGeometry(submesh, info, vertices, rawFaces, multiStream);

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

    // Fills a submesh with the geometry its faces reference. The normal (interleaved) path slices the
    // shared vertex buffer by the stored vertexMin/vertexMax range. The multi-stream (source-leak) path
    // ignores those stored ranges (which are unreliable there) and instead pulls in exactly the vertices
    // each face uses, remapping them to a compact local range.
    private static void AppendSubmeshGeometry(
        SubmeshData submesh,
        SubmeshInfo info,
        List<VertexData> vertices,
        List<(int A, int B, int C)> rawFaces,
        bool multiStream)
    {
        if (multiStream)
        {
            var localByGlobal = new Dictionary<int, int>();
            for (var i = 0; i < info.PolygonCount; i++)
            {
                var faceIndex = info.PolygonStart - 1 + i;
                if (faceIndex < 0 || faceIndex >= rawFaces.Count)
                {
                    continue;
                }

                var face = rawFaces[faceIndex];
                if (!TryRemapFaceVertex(face.A, vertices, localByGlobal, submesh, info.MaterialTint, out var a) ||
                    !TryRemapFaceVertex(face.B, vertices, localByGlobal, submesh, info.MaterialTint, out var b) ||
                    !TryRemapFaceVertex(face.C, vertices, localByGlobal, submesh, info.MaterialTint, out var c))
                {
                    continue;
                }

                submesh.Faces.Add((a, b, c));
            }

            return;
        }

        for (var vertex = info.VertexMin; vertex <= info.VertexMax; vertex++)
        {
            var index = vertex - 1;
            if (index >= 0 && index < vertices.Count)
            {
                submesh.Vertices.Add(ApplyMaterialTint(vertices[index], info.MaterialTint));
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
    }

    private static bool TryRemapFaceVertex(
        int rawFaceIndex,
        List<VertexData> vertices,
        Dictionary<int, int> localByGlobal,
        SubmeshData submesh,
        MaterialTint tint,
        out int local)
    {
        local = 0;
        var globalIndex = rawFaceIndex - 1;
        if (globalIndex < 0 || globalIndex >= vertices.Count)
        {
            return false;
        }

        if (!localByGlobal.TryGetValue(globalIndex, out local))
        {
            local = submesh.Vertices.Count;
            submesh.Vertices.Add(ApplyMaterialTint(vertices[globalIndex], tint));
            localByGlobal[globalIndex] = local;
        }

        return true;
    }

    private static VertexData ApplyMaterialTint(VertexData vertex, MaterialTint tint)
    {
        if (GameConfig.Current.Id != GameId.TalesFromTheBorderlandsE3 || tint.IsWhite)
        {
            return vertex;
        }

        return vertex with
        {
            ColorR = Math.Clamp(vertex.ColorR * SafeTint(tint.R), 0f, 1f),
            ColorG = Math.Clamp(vertex.ColorG * SafeTint(tint.G), 0f, 1f),
            ColorB = Math.Clamp(vertex.ColorB * SafeTint(tint.B), 0f, 1f),
            ColorA = Math.Clamp(vertex.ColorA * SafeTint(tint.A), 0f, 1f),
        };
    }

    private static float SafeTint(float value)
        => float.IsFinite(value) ? value : 1f;

    private static List<VertexData> ReadVertices(
        DataReader reader,
        float uv1X,
        float uv1Y,
        float uv2X,
        float uv2Y,
        float uv3X,
        float uv3Y,
        float uv4X,
        float uv4Y,
        bool signedFormat4Weights)
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
            var (weight0, weight1, weight2, weight3) = ReadWeights(reader, attrs["weights"].Format, signedFormat4Weights);
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

    // A texture-group entry begins with a "symbol" name block: a u32 block size (including itself)
    // followed by either an 8-byte CRC64 hash (modern builds) or an inline string (the Tales from the
    // Borderlands source-code leak / older builds: [u32 length][ascii name]). Reads whichever form is
    // present and returns the resolved/clean texture name.
    private static string ReadTextureEntryName(DataReader reader)
    {
        var blockSize = checked((int)reader.ReadUInt32());
        var contentLength = blockSize - 4;
        if (contentLength == 8)
        {
            var hashLow = reader.ReadUInt32();
            var hashHigh = reader.ReadUInt32();
            return TextureHashDatabase.Resolve(hashLow, hashHigh);
        }

        if (contentLength >= 4)
        {
            var nameLength = checked((int)reader.ReadUInt32());
            var available = contentLength - 4;
            if (nameLength >= 0 && nameLength <= available)
            {
                var name = reader.ReadAscii(nameLength);
                var remaining = available - nameLength;
                if (remaining > 0)
                {
                    reader.Skip(remaining);
                }

                return StripTextureExtension(name);
            }

            reader.Skip(available);
        }
        else if (contentLength > 0)
        {
            reader.Skip(contentLength);
        }

        return "";
    }

    private static string StripTextureExtension(string name)
        => name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    // After the name block, each texture-group entry carries a fixed trailing: a marker, the geometry
    // AABB (6 floats), an optional center vec3-ish pair present only on newer (5VSM) entries, a 0x14
    // marker, and a final sub-block. The optional pair is detected by whether the 0x14 marker is the
    // next u32, which keeps both the source-leak 4VSM and the 5VSM layouts working without per-container
    // branching.
    private static void SkipTextureEntryTrailing(DataReader reader, int finalTextureSubBlockBytes)
    {
        reader.ReadUInt32(); // leading marker (2)
        reader.Skip(24);     // AABB min/max (6 floats)
        if (reader.PeekUInt32() != 0x14)
        {
            reader.Skip(8);  // extra center pair, present on 5VSM entries only
        }

        reader.ReadUInt32(); // 0x14 marker
        reader.Skip(finalTextureSubBlockBytes);
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
                groups[slot].Add(ReadTextureEntryName(reader));
                SkipTextureEntryTrailing(reader, finalTextureSubBlockBytes);
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

    // V25 (Michonne) material textures are identified by hashes rather than a fixed texture table.
    // These labels come from the same V25 material map documented by the RTB importer.
    private static string? GetV25TextureSlot(uint typeHigh, uint typeLow)
        => (typeHigh, typeLow) switch
        {
            (0x8648FA82, 0xD1DBEE1A) or
            (0xDC6E83A0, 0x253F163A) or
            (0x94A590DE, 0x74B1F5C1) => "diffuse",
            (0x4930B970, 0xA7FD511F) or
            (0xDF7E4122, 0x56E87E74) => "detail_diffuse",
            (0x1E3F6B9F, 0x2550389D) or
            (0xB8B04DDF, 0x1796F446) => "bump",
            (0xCAAAE643, 0x2AF348C0) or
            (0x62C49575, 0x78189F07) => "occlusion",
            (0x257C2A45, 0x683F7D2F) or
            (0x13EEE658, 0x65DFC90F) => "environment",
            (0xB3022EA7, 0xFD418B40) or
            (0xBDB4C92A, 0x546FB889) => "emissive",
            (0xFF787A61, 0xEAC8A5B5) => "ink",
            _ => null,
        };

    private static void SkipV25MaterialParameter(DataReader reader, uint high, uint low, int count, int materialEnd)
    {
        var bytesPerEntry = (high, low) switch
        {
            (0x00000000, 0x00000000) => 0,
            (0xA98F0652, 0x295DE685) => 0,
            (0xFA21E4C8, 0x8AE64D31) => 0,
            (0x254EDC51, 0x7B59BB47) => 0,
            (0x7CACEEBC, 0xD26D075C) => 0,
            (0xDED5E193, 0x7B1689EF) => 0,
            (0x181AFB3E, 0x0BB8F90AE) => 0,
            (0x8C44858F, 0x42CD32D5) => 0,
            (0xB76E07D6, 0xBB899BFE) => 24,
            (0x004F0234, 0x63D89FB0) => 16,
            (0xBAE4CBD7, 0x7F139A91) => 12,
            (0x9004C558, 0x7575D6C0) => 9,
            (0x394C43AF, 0x4FF52C94) => 20,
            (0x7BBCA244, 0xE61F1A07) => 16,
            (0xC16762F7, 0x763D62AB) => 24,
            (0xE2BA743E, 0x952F9338) => 24,
            _ => -1,
        };

        if (bytesPerEntry < 0)
        {
            reader.Seek(materialEnd);
            return;
        }

        reader.Skip(checked(count * bytesPerEntry));
    }

    private static void SkipV25SizedBlock(DataReader reader, Action<DataReader, int> readEntries)
    {
        var end = reader.Position + checked((int)reader.ReadUInt32() - 4);
        var count = checked((int)reader.ReadUInt32());
        readEntries(reader, count);
        if (reader.Position < end)
        {
            reader.Seek(end);
        }
    }

    private static Dictionary<string, V25VertexElement> ReadV25VertexDescriptor(DataReader reader)
    {
        reader.Skip(12);
        var count = checked((int)reader.ReadUInt32());
        var result = new Dictionary<string, V25VertexElement>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var type = checked((int)reader.ReadUInt32() + 1);
            var format = checked((int)reader.ReadUInt32() + 1);
            var layer = checked((int)reader.ReadUInt32() + 1);
            var buffer = checked((int)reader.ReadUInt32() + 1);
            var offset = checked((int)reader.ReadUInt32() + 1);
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
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = new V25VertexElement(buffer, format, offset);
            }
        }

        return result;
    }

    private static List<VertexData> ReadV25Vertices(
        DataReader reader,
        int vertexCount,
        int normalCount,
        int uvCount,
        IReadOnlyDictionary<string, V25VertexElement> elements,
        IReadOnlyList<V25UvScale> uvScales)
    {
        var positions = new (float X, float Y, float Z)[vertexCount];
        var bones = Enumerable.Repeat((0, 0, 0, 0), vertexCount).ToArray();
        var weights = Enumerable.Repeat((1f, 0f, 0f, 0f), vertexCount).ToArray();
        for (var i = 0; i < vertexCount; i++)
        {
            if (IsV25Element(elements, "position", 1, 3))
            {
                positions[i] = (reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
            }
            if (IsV25Element(elements, "weights", 1, 21))
            {
                weights[i] = NormalizeWeights(
                    reader.ReadInt16() / 32767f,
                    reader.ReadInt16() / 32767f,
                    reader.ReadInt16() / 32767f,
                    reader.ReadInt16() / 32767f);
            }
            if (IsV25Element(elements, "bones", 1, 24))
            {
                bones[i] = (reader.ReadByte() / 3, reader.ReadByte() / 3, reader.ReadByte() / 3, reader.ReadByte() / 3);
            }
        }

        var normals = Enumerable.Repeat((0f, 1f, 0f), vertexCount).ToArray();
        for (var i = 0; i < normalCount; i++)
        {
            if (IsV25Element(elements, "normals", 2, 21))
            {
                var normal = (reader.ReadInt16() / 32767f, reader.ReadInt16() / 32767f, reader.ReadInt16() / 32767f);
                reader.ReadInt16();
                if (i < normals.Length)
                {
                    normals[i] = normal;
                }
            }
            if (IsV25Element(elements, "binormals", 2, 21))
            {
                reader.Skip(8);
            }
        }

        var colors = Enumerable.Repeat((1f, 1f, 1f, 1f), vertexCount).ToArray();
        var uv1 = new (float U, float V)[vertexCount];
        var uv2 = new (float U, float V)[vertexCount];
        var uv3 = new (float U, float V)[vertexCount];
        var uv4 = new (float U, float V)[vertexCount];
        var uv5 = new (float U, float V)[vertexCount];
        var uv6 = new (float U, float V)[vertexCount];
        for (var i = 0; i < uvCount; i++)
        {
            if (IsV25Element(elements, "tangents", 3, 21))
            {
                reader.Skip(8);
            }

            // RTB's Michonne importer reads the third buffer in this exact order:
            // tangents, UV6, UV5, color, color2, UV1, UV2, UV3, UV4.
            // Reading UV1-4 before the color slots shifts the stream and corrupts texture UVs.
            ReadV25UvIfPresent(reader, elements, uvScales, 6, i, vertexCount, uv6);
            ReadV25UvIfPresent(reader, elements, uvScales, 5, i, vertexCount, uv5);

            if (IsV25Element(elements, "colors", 3, 26))
            {
                var color = (reader.ReadByte() / 255f, reader.ReadByte() / 255f, reader.ReadByte() / 255f, reader.ReadByte() / 255f);
                if (i < vertexCount)
                {
                    colors[i] = color;
                }
            }
            if (IsV25Element(elements, "colors2", 3, 26))
            {
                reader.Skip(4);
            }

            ReadV25UvIfPresent(reader, elements, uvScales, 1, i, vertexCount, uv1);
            ReadV25UvIfPresent(reader, elements, uvScales, 2, i, vertexCount, uv2);
            ReadV25UvIfPresent(reader, elements, uvScales, 3, i, vertexCount, uv3);
            ReadV25UvIfPresent(reader, elements, uvScales, 4, i, vertexCount, uv4);
        }

        static void ReadV25UvIfPresent(
            DataReader reader,
            IReadOnlyDictionary<string, V25VertexElement> elements,
            IReadOnlyList<V25UvScale> uvScales,
            int layer,
            int index,
            int vertexCount,
            (float U, float V)[] target)
        {
            if (!elements.TryGetValue($"uv{layer}", out var element) || element.Buffer != 3)
            {
                return;
            }

            var uv = ReadV25Uv(reader, element.Format, uvScales[Math.Clamp(layer - 1, 0, uvScales.Count - 1)]);
            if (index < vertexCount)
            {
                target[index] = uv;
            }
        }

        var vertices = new List<VertexData>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            if (uv2[i] == default) uv2[i] = uv1[i];
            if (uv3[i] == default) uv3[i] = uv2[i];
            if (uv4[i] == default) uv4[i] = uv3[i];
            vertices.Add(new VertexData(
                positions[i].X, positions[i].Y, positions[i].Z,
                normals[i].Item1, normals[i].Item2, normals[i].Item3,
                uv1[i].U, uv1[i].V,
                uv2[i].U, uv2[i].V,
                uv3[i].U, uv3[i].V,
                uv4[i].U, uv4[i].V,
                bones[i].Item1, bones[i].Item2, bones[i].Item3, bones[i].Item4,
                weights[i].Item1, weights[i].Item2, weights[i].Item3, weights[i].Item4,
                colors[i].Item1, colors[i].Item2, colors[i].Item3, colors[i].Item4,
                U5: uv5[i].U, V5: uv5[i].V,
                U6: uv6[i].U, V6: uv6[i].V));
        }

        return vertices;
    }

    private static bool IsV25Element(IReadOnlyDictionary<string, V25VertexElement> elements, string key, int buffer, int format)
        => elements.TryGetValue(key, out var element) && element.Buffer == buffer && element.Format == format;

    private static (float U, float V) ReadV25Uv(DataReader reader, int format, V25UvScale scale)
        => format switch
        {
            2 => (reader.ReadFloat(), 1f - reader.ReadFloat()),
            19 => (
                reader.ReadInt16() / 32767f * scale.XMult + scale.XStart,
                1f - (reader.ReadInt16() / 32767f * scale.YMult + scale.YStart)),
            _ => throw new InvalidDataException($"Unknown V25 UV format: {format}"),
        };

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
            var (weight0, weight1, weight2, weight3) = ReadWeights(reader, weights.Format, signedFormat4: false);
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

    // The TFTB source-leak ERTM (version 5) meshes: an MTRE container with non-interleaved vertex streams
    // like the 5VSM build, but a different 304-byte submesh entry (vMin/vMax are 0-based at +12/+16,
    // facePointStart at +20, polygonCount at +24) and a 172 stream marker (14 attribute slots). Geometry
    // is recovered by reading the submesh table, locating the face buffer and the multi-stream vertices,
    // then building each submesh from the vertices its own faces reference.
    private static MeshData ParseErtmV5(DataReader reader, string name, int version)
    {
        reader.Skip(4);
        reader.ReadVec3();
        reader.ReadVec3();

        var headerLength = checked((int)reader.ReadUInt32() - 4);
        reader.Skip(headerLength);

        var submeshBlockSize = checked((int)reader.ReadUInt32());
        var submeshCount = checked((int)reader.ReadUInt32());
        if (submeshCount <= 0 || submeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid submesh count: {submeshCount}");
        }

        var tableStart = reader.Position;
        var tableBytes = submeshBlockSize - 8;
        var entrySize = tableBytes / submeshCount;
        if (entrySize <= 28)
        {
            throw new InvalidDataException($"Invalid ERTM submesh entry size: {entrySize}");
        }

        // The submesh entry carries a (vMin, vMax, facePointStart, polygonCount) quad. Its position inside
        // the entry shifted by 4 bytes between the ERTM revisions: the 304-byte entries (v5/v6) start it at
        // +12, the 288-byte entries (v9-v12) at +16.
        var fieldBase = entrySize >= 300 ? 12 : 16;

        var infos = new List<SubmeshInfo>(submeshCount);
        var totalFacePoints = 0;
        var vertexCountHint = 0;
        for (var i = 0; i < submeshCount; i++)
        {
            var e = tableStart + i * entrySize;
            var vMin = checked((int)reader.PeekUInt32(e + fieldBase));
            var vMax = checked((int)reader.PeekUInt32(e + fieldBase + 4));
            var facePointStart = checked((int)reader.PeekUInt32(e + fieldBase + 8));
            var polygonCount = checked((int)reader.PeekUInt32(e + fieldBase + 12));
            totalFacePoints += polygonCount * 3;
            if (vMax >= 0 && vMax < 1_000_000)
            {
                vertexCountHint = Math.Max(vertexCountHint, vMax + 1);
            }

            // The submesh's material index sits 64 bytes past the (vMin,vMax,…) quad. It indexes the
            // ordered diffuse list parsed from the texture block below (validated on lee head/body/legs and
            // the Glock prop). Treat out-of-range/garbage values as material 0.
            var materialIndex = (int)reader.PeekUInt32(e + fieldBase + 64);
            if (materialIndex < 0 || materialIndex > 4096)
            {
                materialIndex = 0;
            }

            // 8 bytes further is the bone-palette index for skinned submeshes (-1 for unskinned, e.g. hair).
            var paletteIndex = (int)reader.PeekUInt32(e + fieldBase + 72);
            if (paletteIndex < 0 || paletteIndex > 4096)
            {
                paletteIndex = 0;
            }

            infos.Add(new SubmeshInfo(
                Index: i,
                BoneSetIndex: paletteIndex,
                VertexMin: vMin + 1,
                VertexMax: vMax + 1,
                PolygonStart: facePointStart / 3 + 1,
                PolygonCount: polygonCount,
                MaterialIndex: materialIndex,
                MaterialTint: MaterialTint.White,
                TextureNames: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }

        reader.Seek(tableStart + tableBytes);

        // The blocks between the submesh table and the face buffer (palettes, materials, uvScales) vary in
        // size, and the face point count is often a small value that recurs as stray data, so scanning for
        // it directly is unreliable. Instead locate the first vertex stream (its header carries the marker
        // and the shared vertex count) and derive the face buffer immediately preceding it.
        var firstVertexStream = FindFirstVertexStream(reader, vertexCountHint);
        if (firstVertexStream < 0)
        {
            throw new InvalidDataException("ERTM mesh vertex stream not found.");
        }

        var faceDataOffset = firstVertexStream - totalFacePoints * 2;
        if (faceDataOffset < reader.Position)
        {
            throw new InvalidDataException("ERTM mesh face buffer not found.");
        }

        reader.Seek(faceDataOffset);
        var rawFaces = new List<(int A, int B, int C)>(totalFacePoints / 3);
        for (var i = 0; i < totalFacePoints / 3; i++)
        {
            rawFaces.Add((reader.ReadUInt16() + 1, reader.ReadUInt16() + 1, reader.ReadUInt16() + 1));
        }

        var vertices = ReadVerticesMultiStream(reader);
        if (vertices.Count == 0)
        {
            throw new InvalidDataException("No vertices found.");
        }

        // The texture block sits between the submesh table and the face buffer. Names are stored inline as
        // strings ("<name>.d3dtx"); the ordered list of non-normal-map names is the material's diffuse list,
        // indexed by each submesh's material index. The normal map is left for the texture resolver to match
        // by name (e.g. "<diffuse>_nm").
        var diffuseNames = ReadErtmDiffuseNames(reader, tableStart + tableBytes, faceDataOffset);

        // The bone palette block sits at the start of that same region (before the texture names): each
        // palette maps the submesh's local bone indices to skeleton bones by hash, which the viewer and the
        // glTF rig need so posing the skeleton actually deforms the mesh.
        var bonePalettes = ReadErtmBonePalettes(reader, tableStart + tableBytes);

        var mesh = new MeshData { Name = name, Version = version };
        mesh.BonePalettes.AddRange(bonePalettes);
        foreach (var info in infos)
        {
            var diffuse = diffuseNames.Count > 0
                ? diffuseNames[Math.Min(info.MaterialIndex, diffuseNames.Count - 1)]
                : null;
            if (diffuse is not null)
            {
                info.TextureNames["diffuse"] = diffuse;
            }

            var submesh = new SubmeshData
            {
                Name = diffuse ?? $"submesh_{info.Index}",
                MaterialName = diffuse,
                BonePaletteIndex = bonePalettes.Count > 0
                    ? Math.Clamp(info.BoneSetIndex, 0, bonePalettes.Count - 1)
                    : 0,
            };
            foreach (var texture in info.TextureNames)
            {
                submesh.TextureNames[texture.Key] = texture.Value;
            }

            AppendSubmeshGeometry(submesh, info, vertices, rawFaces, multiStream: true);
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

    // Scans the texture block (between the submesh table and the face buffer) for inline "<name>.d3dtx"
    // strings and returns the ordered diffuse list — every name that is not a normal map (_nm/_nrm/_normal).
    // The order matches the material index stored per submesh, so submesh.MaterialIndex selects its diffuse.
    private static List<string> ReadErtmDiffuseNames(DataReader reader, int start, int end)
    {
        var result = new List<string>();
        if (start < 0 || end <= start || end > reader.Length)
        {
            return result;
        }

        var block = reader.Slice(start, end - start);
        var seen = new HashSet<int>();
        for (var i = 0; i + 6 <= block.Length; i++)
        {
            // Match the ".d3dtx" extension, then walk backwards over the filename characters.
            if (block[i] != (byte)'.' ||
                block[i + 1] != (byte)'d' || block[i + 2] != (byte)'3' || block[i + 3] != (byte)'d' ||
                block[i + 4] != (byte)'t' || block[i + 5] != (byte)'x')
            {
                continue;
            }

            var nameStart = i;
            while (nameStart > 0 && IsTextureNameByte(block[nameStart - 1]))
            {
                nameStart--;
            }

            if (nameStart == i || !seen.Add(nameStart))
            {
                continue;
            }

            var name = System.Text.Encoding.ASCII.GetString(block, nameStart, i - nameStart);
            if (name.EndsWith("_nm", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_nrm", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_normal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(name);
        }

        return result;
    }

    private static bool IsTextureNameByte(byte b)
        => b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')
            or (>= (byte)'0' and <= (byte)'9') or (byte)'_';

    // Reads the ERTM bone-palette block at the start of the post-table region. Layout:
    // [u32][u32][u32 blockSize][u32 paletteCount], then each palette is [u32 entryCount] followed by
    // entryCount entries of [hash u64][nameLen u32][name]. Returns one hash array per palette; the
    // per-vertex bone index (after BoneIndexConvention) selects an entry, whose hash maps to a skeleton
    // bone. Static meshes store paletteCount 0 and return an empty list.
    private static List<ulong[]> ReadErtmBonePalettes(DataReader reader, int blockStart)
    {
        var result = new List<ulong[]>();
        if (blockStart + 16 > reader.Length)
        {
            return result;
        }

        var paletteCount = (int)reader.PeekUInt32(blockStart + 12);
        if (paletteCount <= 0 || paletteCount > 256)
        {
            return result;
        }

        var pos = blockStart + 16;
        for (var p = 0; p < paletteCount; p++)
        {
            if (pos + 4 > reader.Length)
            {
                break;
            }

            var entryCount = (int)reader.PeekUInt32(pos);
            pos += 4;
            if (entryCount < 0 || entryCount > 4096)
            {
                break;
            }

            var palette = new ulong[entryCount];
            for (var e = 0; e < entryCount; e++)
            {
                if (pos + 12 > reader.Length)
                {
                    break;
                }

                var low = reader.PeekUInt32(pos);
                var high = reader.PeekUInt32(pos + 4);
                palette[e] = ((ulong)high << 32) | low;
                var nameLen = (int)reader.PeekUInt32(pos + 8);
                pos += 12 + (nameLen < 0 || nameLen > 256 ? 0 : nameLen);
            }

            result.Add(palette);
        }

        return result;
    }

    // Scans forward from the current position for the first vertex stream header and returns its offset.
    // A stream header is [count u32][stride u32][8 bytes][marker u32][attrCount*12 attribute table]; the
    // marker equals attrCount*12 + 4. The shared vertex count disambiguates the real stream from stray
    // data. The face buffer sits immediately before this header, so the caller derives the index data by
    // subtracting its size from this offset.
    private static int FindFirstVertexStream(DataReader reader, int vertexCount)
    {
        if (vertexCount <= 0)
        {
            return -1;
        }

        for (var p = reader.Position; p + 0x18 + 12 * 12 <= reader.Length; p++)
        {
            if (reader.PeekUInt32(p) != (uint)vertexCount)
            {
                continue;
            }

            var stride = reader.PeekUInt32(p + 4);
            if (stride is 0 or > 256)
            {
                continue;
            }

            var marker = reader.PeekUInt32(p + 20);
            if (marker < 148 || marker > 400 || (marker - 4) % 12 != 0)
            {
                continue;
            }

            return p;
        }

        return -1;
    }

    // The first vertex buffer of the TFTB source-leak build declares ONLY the position attribute (the
    // other eleven slots are zero), because every attribute lives in its own separate stream. A normal
    // interleaved buffer (TWAU/E3/MCSM) always declares several attributes in the one buffer. Peek the
    // attribute table without consuming it and report which case this is.
    private static bool IsMultiStreamVertexLayout(DataReader reader)
    {
        var start = reader.Position;
        try
        {
            if (start + 0x18 + 12 * 12 > reader.Length)
            {
                return false;
            }

            reader.ReadUInt32(); // vertex count
            reader.ReadUInt32(); // stride
            reader.Skip(0x0C);
            reader.ReadUInt32();

            var nonZero = 0;
            var positionDeclared = false;
            for (var slot = 0; slot < 12; slot++)
            {
                var attr = ReadAttr(reader);
                if (attr.Count == 0 && attr.Format == 0)
                {
                    continue;
                }

                nonZero++;
                if (slot == 0)
                {
                    positionDeclared = true;
                }
            }

            return nonZero == 1 && positionDeclared;
        }
        finally
        {
            reader.Seek(start);
        }
    }

    // Reads the TFTB source-leak vertex section: a run of separate, non-interleaved streams, each one a
    // standard 168-byte vertex-buffer header (count, stride, padding, marker, 12-slot attribute table)
    // followed by count*stride bytes. Exactly one attribute slot is set per stream; its index gives the
    // semantic (0=position, 1=uv1, 2=normals, 3=weights, 4=bones, 5=colors, 6=unknown1, 7=binormals,
    // 8=tangents, 9=uv2). All streams share the same vertex count. Floats are stored uncompressed.
    private static List<VertexData> ReadVerticesMultiStream(DataReader reader)
    {
        var vertexCount = -1;
        Vector3D[]? position = null;
        Vector3D[]? normals = null;
        Vector2D[]? uv1 = null;
        Vector2D[]? uv2 = null;
        Vector2D[]? uv3 = null;
        Vector2D[]? uv4 = null;
        Vector4D[]? colors = null;
        Vector4D[]? binormals = null;
        Vector4D[]? tangents = null;
        int[]? bones = null;
        Vector4D[]? weights = null;
        float[]? unknown1 = null;

        while (reader.Position + 0x18 + 12 * 12 <= reader.Length)
        {
            var blockStart = reader.Position;
            var count = checked((int)reader.ReadUInt32());
            var stride = checked((int)reader.ReadUInt32());
            if (count <= 0 || stride <= 0 || stride > 256 || (vertexCount != -1 && count != vertexCount))
            {
                reader.Seek(blockStart);
                break;
            }

            reader.Skip(0x0C);
            // The marker encodes the attribute-table size: markerValue = attrCount*12 + 4. Older 5VSM/4VSM
            // streams use 148 (12 attributes); the ERTM (v5) streams use 172 (14). Read exactly that many
            // attribute slots so the data start lands correctly for both.
            var marker = checked((int)reader.ReadUInt32());
            var attrCount = (marker - 4) / 12;
            if (attrCount < 12 || attrCount > 32)
            {
                attrCount = 12;
            }

            var slot = -1;
            var attr = new AttrDescriptor();
            for (var i = 0; i < attrCount; i++)
            {
                var candidate = ReadAttr(reader);
                if (slot < 0 && i < 12 && (candidate.Count != 0 || candidate.Format != 0))
                {
                    slot = i;
                    attr = candidate;
                }
            }

            // Static ERTM meshes (props, fx) leave the geometry streams' attribute tables zeroed and rely
            // on a fixed stream order: position, normal, uv1, tangent, then an explicitly-tagged colour
            // stream. When no slot is declared, infer the semantic from the stream's stride and which
            // semantics have already been filled.
            if (slot < 0)
            {
                slot = stride switch
                {
                    8 => uv1 is null ? 1 : 9,
                    4 => 5,
                    16 => 3,
                    12 => position is null ? 0 : normals is null ? 2 : tangents is null ? 8 : 7,
                    _ => -1,
                };
            }

            var dataStart = reader.Position;
            if (slot < 0 || dataStart + (long)count * stride > reader.Length)
            {
                reader.Seek(blockStart);
                break;
            }

            if (vertexCount == -1)
            {
                vertexCount = count;
            }

            switch (slot)
            {
                case 0:
                    position = ReadFloatVec3Stream(reader, count, stride);
                    break;
                case 1:
                    uv1 = ReadFloatVec2Stream(reader, count, stride, flipV: true);
                    break;
                case 2:
                    normals = ReadFloatVec3Stream(reader, count, stride);
                    break;
                case 3:
                    weights = ReadFloatVec4Stream(reader, count, stride);
                    break;
                case 4:
                    bones = ReadByteIndexStream(reader, count, stride);
                    break;
                case 5:
                    colors = ReadColorStream(reader, count, stride, attr.Format);
                    break;
                case 6:
                    unknown1 = ReadFloatScalarStream(reader, count, stride);
                    break;
                case 7:
                    binormals = ReadFloatVec4Stream(reader, count, stride);
                    break;
                case 8:
                    tangents = ReadFloatVec4Stream(reader, count, stride);
                    break;
                case 9:
                    uv2 = ReadFloatVec2Stream(reader, count, stride, flipV: true);
                    break;
                case 10:
                    uv3 = ReadFloatVec2Stream(reader, count, stride, flipV: true);
                    break;
                case 11:
                    uv4 = ReadFloatVec2Stream(reader, count, stride, flipV: true);
                    break;
            }

            reader.Seek(dataStart + count * stride);
        }

        if (vertexCount <= 0 || position is null)
        {
            return [];
        }

        var vertices = new List<VertexData>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var p = position[i];
            var n = normals is not null ? normals[i] : new Vector3D(0f, 1f, 0f);
            var t1 = uv1 is not null ? uv1[i] : default;
            var t2 = uv2 is not null ? uv2[i] : t1;
            var t3 = uv3 is not null ? uv3[i] : t2;
            var t4 = uv4 is not null ? uv4[i] : t3;
            var (cr, cg, cb, ca) = colors is not null ? Tuple4(colors[i]) : (1f, 1f, 1f, 1f);
            var bn = binormals is not null ? binormals[i] : default;
            var tg = tangents is not null ? tangents[i] : default;
            var boneBase = bones is not null ? i * 4 : -1;
            var b0 = boneBase >= 0 ? bones![boneBase] : 0;
            var b1 = boneBase >= 0 ? bones![boneBase + 1] : 0;
            var b2 = boneBase >= 0 ? bones![boneBase + 2] : 0;
            var b3 = boneBase >= 0 ? bones![boneBase + 3] : 0;
            var (w0, w1, w2, w3) = weights is not null
                ? NormalizeWeights(weights[i].X, weights[i].Y, weights[i].Z, weights[i].W)
                : (1f, 0f, 0f, 0f);
            var unk = unknown1 is not null ? unknown1[i] : 0f;

            vertices.Add(new VertexData(
                p.X, p.Y, p.Z,
                n.X, n.Y, n.Z,
                t1.X, t1.Y, t2.X, t2.Y, t3.X, t3.Y, t4.X, t4.Y,
                b0, b1, b2, b3,
                w0, w1, w2, w3,
                cr, cg, cb, ca,
                unk,
                bn.X, bn.Y, bn.Z, bn.W,
                tg.X, tg.Y, tg.Z, tg.W));
        }

        return vertices;
    }

    private readonly record struct Vector2D(float X, float Y);
    private readonly record struct Vector3D(float X, float Y, float Z);
    private readonly record struct Vector4D(float X, float Y, float Z, float W);

    private static (float, float, float, float) Tuple4(Vector4D v) => (v.X, v.Y, v.Z, v.W);

    private static Vector3D[] ReadFloatVec3Stream(DataReader reader, int count, int stride)
    {
        var result = new Vector3D[count];
        var dataStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            reader.Seek(dataStart + i * stride);
            result[i] = new Vector3D(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
        }

        return result;
    }

    private static Vector2D[] ReadFloatVec2Stream(DataReader reader, int count, int stride, bool flipV)
    {
        var result = new Vector2D[count];
        var dataStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            reader.Seek(dataStart + i * stride);
            var u = reader.ReadFloat();
            var v = reader.ReadFloat();
            result[i] = new Vector2D(u, flipV ? -v + 1f : v);
        }

        return result;
    }

    private static Vector4D[] ReadFloatVec4Stream(DataReader reader, int count, int stride)
    {
        var result = new Vector4D[count];
        var dataStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            reader.Seek(dataStart + i * stride);
            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var z = reader.ReadFloat();
            var w = stride >= 16 ? reader.ReadFloat() : 0f;
            result[i] = new Vector4D(x, y, z, w);
        }

        return result;
    }

    private static float[] ReadFloatScalarStream(DataReader reader, int count, int stride)
    {
        var result = new float[count];
        var dataStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            reader.Seek(dataStart + i * stride);
            result[i] = reader.ReadFloat();
        }

        return result;
    }

    private static int[] ReadByteIndexStream(DataReader reader, int count, int stride)
    {
        var result = new int[count * 4];
        var dataStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            reader.Seek(dataStart + i * stride);
            result[i * 4] = reader.ReadByte();
            result[i * 4 + 1] = reader.ReadByte();
            result[i * 4 + 2] = reader.ReadByte();
            result[i * 4 + 3] = reader.ReadByte();
        }

        return result;
    }

    private static Vector4D[] ReadColorStream(DataReader reader, int count, int stride, uint format)
    {
        var result = new Vector4D[count];
        var dataStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            reader.Seek(dataStart + i * stride);
            if (format == 1)
            {
                result[i] = new Vector4D(
                    Math.Clamp(reader.ReadFloat(), 0f, 1f),
                    Math.Clamp(reader.ReadFloat(), 0f, 1f),
                    Math.Clamp(reader.ReadFloat(), 0f, 1f),
                    Math.Clamp(reader.ReadFloat(), 0f, 1f));
            }
            else
            {
                result[i] = new Vector4D(
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f);
            }
        }

        return result;
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

    private static UvScaleSet ReadUvScalesV13(DataReader reader)
    {
        var floatCount = DetectUvScaleFloatCount(reader);
        var values = new float[floatCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadFloat();
        }

        var scaleStart = values.Length >= 9 ? values.Length - 8 : 0;
        return new UvScaleSet(
            Uv1X: values[scaleStart],
            Uv1Y: values[scaleStart + 1],
            Uv2X: values[scaleStart + 4],
            Uv2Y: values[scaleStart + 5],
            Uv3X: values[scaleStart + 6],
            Uv3Y: values[scaleStart + 7],
            Uv4X: values[scaleStart + 2],
            Uv4Y: values[scaleStart + 3]);
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

    private static int ReadOptionalIndexPlusOne(DataReader reader)
    {
        var raw = reader.ReadUInt32();
        return raw == uint.MaxValue ? 0 : checked((int)raw + 1);
    }

    private static BonePaletteReadResult ReadBonePalettes(DataReader reader, int entrySize)
    {
        reader.ReadUInt32();
        var paletteCount = checked((int)reader.ReadUInt32());
        var palettes = new List<ulong[]>(paletteCount);
        var entries = new List<List<BonePaletteEntryData>>(paletteCount);
        for (var i = 0; i < paletteCount; i++)
        {
            var boneCount = checked((int)reader.ReadUInt32());
            var hashes = new ulong[boneCount];
            var paletteEntries = new List<BonePaletteEntryData>(boneCount);
            for (var bone = 0; bone < boneCount; bone++)
            {
                var low = reader.ReadUInt32();
                var high = reader.ReadUInt32();
                var hash = ((ulong)high << 32) | low;
                hashes[bone] = hash;
                var entryStart = reader.Position - 8;
                var entry = new BonePaletteEntryData(hash, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                if (entrySize >= 56)
                {
                    entry = new BonePaletteEntryData(
                        hash,
                        reader.PeekFloat(entryStart + 8),
                        reader.PeekFloat(entryStart + 12),
                        reader.PeekFloat(entryStart + 16),
                        reader.PeekFloat(entryStart + 20),
                        reader.PeekFloat(entryStart + 24),
                        reader.PeekFloat(entryStart + 28),
                        reader.PeekFloat(entryStart + 36),
                        reader.PeekFloat(entryStart + 40),
                        reader.PeekFloat(entryStart + 44),
                        reader.PeekFloat(entryStart + 48));
                }

                paletteEntries.Add(entry);
                var remaining = entrySize - 8;
                if (remaining > 0)
                {
                    reader.Skip(remaining);
                }
            }

            palettes.Add(hashes);
            entries.Add(paletteEntries);
        }

        return new BonePaletteReadResult(palettes, entries);
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

    private static (float Weight0, float Weight1, float Weight2, float Weight3) ReadWeights(DataReader reader, uint format, bool signedFormat4)
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
                if (signedFormat4)
                {
                    return NormalizeWeights(
                        reader.ReadInt16() / 32767f,
                        reader.ReadInt16() / 32767f,
                        reader.ReadInt16() / 32767f,
                        reader.ReadInt16() / 32767f);
                }

                return NormalizeWeights(
                    reader.ReadUInt16() / 65535f,
                    reader.ReadUInt16() / 65535f,
                    reader.ReadUInt16() / 65535f,
                    reader.ReadUInt16() / 65535f);
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
        MaterialTint MaterialTint,
        Dictionary<string, string> TextureNames);

    private sealed record BonePaletteReadResult(
        List<ulong[]> Palettes,
        List<List<BonePaletteEntryData>> Entries);

    private readonly record struct MaterialTint(float R, float G, float B, float A)
    {
        public static readonly MaterialTint White = new(1f, 1f, 1f, 1f);

        public bool IsWhite =>
            MathF.Abs(R - 1f) < 0.0001f &&
            MathF.Abs(G - 1f) < 0.0001f &&
            MathF.Abs(B - 1f) < 0.0001f &&
            MathF.Abs(A - 1f) < 0.0001f;
    }

    private sealed record VertexBufferSection(
        int VertexCount,
        int Stride,
        int DataStart,
        IReadOnlyDictionary<string, AttrDescriptor> Attributes);

    private readonly record struct UvScaleSet(
        float Uv1X,
        float Uv1Y,
        float Uv2X,
        float Uv2Y,
        float Uv3X,
        float Uv3Y,
        float Uv4X,
        float Uv4Y);

    private readonly record struct AttrDescriptor(uint Offset = 0, uint Count = 0, uint Format = 0);
    private readonly record struct V25UvScale(float XMult, float YMult, float XStart, float YStart);
    private readonly record struct V25VertexElement(int Buffer, int Format, int Offset);
}
