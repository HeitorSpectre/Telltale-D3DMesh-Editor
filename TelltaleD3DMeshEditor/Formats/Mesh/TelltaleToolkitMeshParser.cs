using System.Buffers.Binary;
using TelltaleToolKit;
using TelltaleToolKit.Hashing;
using TelltaleToolKit.T3Types;
using TelltaleToolKit.T3Types.Meshes;
using TelltaleToolKit.T3Types.Meshes.T3Types;
using TelltaleToolKit.T3Types.Properties;
using TelltaleToolKit.T3Types.Textures;

namespace TelltaleD3DMeshEditor.Formats.Mesh;

internal static class TelltaleToolkitMeshParser
{
    private static readonly object ToolkitGate = new();

    public static OldBonePaletteInfo? TryReadOldBonePaletteInfo(byte[] data)
    {
        EnsureToolkitInitialized();
        var ttkMesh = TryDeserialize(data, requireGeometry: false, out _, out _);
        if (ttkMesh is null || ttkMesh.BonePalettes.Count == 0)
        {
            return null;
        }

        var palettes = ttkMesh.BonePalettes
            .Select(palette => palette.Select(entry => ResolveBoneHash(entry)).ToArray())
            .Where(palette => palette.Length > 0)
            .ToList();
        if (palettes.Count == 0)
        {
            return null;
        }

        var triangleSetPaletteIndices = ttkMesh.TriangleSets
            .Select(triangleSet => NormalizePaletteIndex(triangleSet.BonePaletteIndex, palettes.Count))
            .ToArray();

        return new OldBonePaletteInfo(palettes, triangleSetPaletteIndices);
    }

    public static MeshData ParseOldMesh(byte[] data, string fallbackName)
    {
        EnsureToolkitInitialized();
        var ttkMesh = TryDeserialize(data, requireGeometry: true, out var lastError, out var lastStatus)
            ?? throw new InvalidDataException($"Telltale Toolkit could not read the mesh. {lastStatus}", lastError);

        return Convert(ttkMesh, fallbackName);
    }

    public static MeshData ParseModernMesh(byte[] data, string fallbackName, string? preferredGameName)
    {
        EnsureToolkitInitialized();
        var ttkMesh = TryDeserialize(data, requireGeometry: true, out var lastError, out var lastStatus, preferredGameName)
            ?? throw new InvalidDataException($"Telltale Toolkit could not read the mesh. {lastStatus}", lastError);

        return Convert(ttkMesh, fallbackName);
    }

    // Raw toolkit mesh (no conversion) for debug tooling that needs material handles / internal
    // resources exactly as serialized.
    public static D3DMesh? ParseModernMeshRaw(byte[] data, string? preferredGameName)
    {
        EnsureToolkitInitialized();
        return TryDeserialize(data, requireGeometry: false, out _, out _, preferredGameName);
    }

    private static D3DMesh? TryDeserialize(
        byte[] data,
        bool requireGeometry,
        out Exception? lastError,
        out string lastStatus,
        string? preferredGameName = null)
    {
        lastError = null;
        lastStatus = "No profile produced geometry.";
        var statuses = new List<string>();
        using (var stream = new MemoryStream(data))
        {
            try
            {
                var mesh = Toolkit.Instance.Deserialize<D3DMesh>(stream);
                if (mesh is not null && (!requireGeometry || HasGeometry(mesh)))
                {
                    return mesh;
                }

                statuses.Add(DescribeMesh("default", mesh));
            }
            catch (Exception ex)
            {
                lastError = ex;
                statuses.Add($"default failed: {ex.Message}");
            }
        }

        var profileNames = Toolkit.Instance.GameProfiles.Keys
            .OrderByDescending(profile => IsPreferredProfile(profile, preferredGameName))
            .ThenByDescending(profile => profile.Contains("future", StringComparison.OrdinalIgnoreCase) ||
                                         profile.Contains("bttf", StringComparison.OrdinalIgnoreCase));
        foreach (var profileName in profileNames)
        {
            Workspace workspace;
            try
            {
                workspace = Toolkit.Instance.CreateWorkspace($"d3dmesh::{profileName}", profileName);
            }
            catch
            {
                continue;
            }

            try
            {
                using var stream = new MemoryStream(data);
                var mesh = Toolkit.Instance.Deserialize<D3DMesh>(stream, workspace);
                if (mesh is not null && (!requireGeometry || HasGeometry(mesh)))
                {
                    return mesh;
                }

                statuses.Add(DescribeMesh(profileName, mesh));
            }
            catch (Exception ex)
            {
                lastError = ex;
                statuses.Add($"{profileName} failed: {ex.Message}");
                // Wrong game/version context for this old mesh; try the next profile.
            }
        }

        if (statuses.Count > 0)
        {
            lastStatus = string.Join(" ", statuses.Take(6));
        }

        return null;
    }

    private static bool IsPreferredProfile(string profileName, string? preferredGameName)
    {
        if (string.IsNullOrWhiteSpace(preferredGameName))
        {
            return false;
        }

        return profileName.Equals(preferredGameName, StringComparison.OrdinalIgnoreCase) ||
               profileName.Contains(preferredGameName, StringComparison.OrdinalIgnoreCase) ||
               preferredGameName.Contains(profileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasGeometry(D3DMesh mesh)
    {
        if (mesh.TriangleSets.Count > 0 &&
            mesh.T3VertexBuffers is not null &&
            mesh.T3VertexBuffers.Any(buffer => buffer is { Buffer.Length: > 0 }))
        {
            return true;
        }

        // v22+ meshes (e.g. MCSM S2 v45) store geometry in T3MeshData instead
        if (mesh.MeshData is not null &&
            mesh.MeshData.VertexStates is not null &&
            mesh.MeshData.VertexStates.Count > 0 &&
            mesh.MeshData.VertexStates.Any(state =>
                state.VertexBuffer is not null &&
                state.VertexBuffer.Any(buffer => buffer is { Buffer.Length: > 0 })))
        {
            return true;
        }

        return false;
    }

    private static string DescribeMesh(string profileName, D3DMesh? mesh)
    {
        if (mesh is null)
            return $"{profileName}: null mesh.";

        var lod0 = mesh.MeshData?.LODs?.FirstOrDefault();
        var batchInfo = lod0 is not null
            ? $"batches={lod0.Batches?.Count ?? 0}/{lod0.Batches1?.Count ?? 0}/{lod0.Batches2?.Count ?? 0}"
            : "noLOD";
        var vsInfo = mesh.MeshData?.VertexStates is { Count: > 0 } vs
            ? $"vs={vs.Count}, vb={vs[0].VertexBuffer?.Count ?? 0}, ib={vs[0].IndexBuffer?.Count ?? 0}"
            : "noVS";

        var internalResInfo = mesh.InternalResources.Count > 0
            ? $"intRes={mesh.InternalResources.Count}:[{string.Join(",", mesh.InternalResources.Take(5).Select(r => $"{r.ObjectInfo.ObjectName.DebugString ?? "?"}({r.ObjectInfo.Type?.Symbol.DebugString ?? "?"})"))}]"
            : "noIntRes";

        return $"{profileName}: version={mesh.Version}, triSets={mesh.TriangleSets.Count}, vb={mesh.T3VertexBuffers?.Length ?? 0}, {batchInfo}, {vsInfo}, {internalResInfo}.";
    }

    private static MeshData Convert(D3DMesh ttkMesh, string fallbackName)
    {
        if (ttkMesh.MeshData is not null &&
            ttkMesh.MeshData.LODs is not null &&
            ttkMesh.MeshData.LODs.Count > 0 &&
            ttkMesh.MeshData.VertexStates is not null &&
            ttkMesh.MeshData.VertexStates.Count > 0)
        {
            return ConvertFromMeshData(ttkMesh, fallbackName);
        }

        return ConvertFromLegacy(ttkMesh, fallbackName);
    }

    private static MeshData ConvertFromLegacy(D3DMesh ttkMesh, string fallbackName)
    {
        var positions = ReadPositions(GetBuffer(ttkMesh, 0));
        if (positions.Count == 0)
        {
            throw new InvalidDataException("No vertices found in Toolkit mesh.");
        }

        var normals = ReadNormals(GetBuffer(ttkMesh, 1), positions.Count);
        var weights = ReadWeights(GetBuffer(ttkMesh, 3), positions.Count);
        var bones = ReadBones(GetBuffer(ttkMesh, 4), positions.Count);
        var uv1 = ReadUvs(GetBuffer(ttkMesh, 5), positions.Count);
        var uv2 = ReadUvs(GetBuffer(ttkMesh, 6), positions.Count);
        var uv3 = ReadUvs(GetBuffer(ttkMesh, 7), positions.Count);
        var uv4 = ReadUvs(GetBuffer(ttkMesh, 8), positions.Count);
        var colors = ReadColors(GetBuffer(ttkMesh, 10), positions.Count);
        var indices = ReadIndices(ttkMesh.T3IndexBuffer);

        var mesh = new MeshData
        {
            Name = string.IsNullOrWhiteSpace(ttkMesh.Name) ? fallbackName : ttkMesh.Name,
            Version = ttkMesh.Version,
        };

        foreach (var palette in ttkMesh.BonePalettes)
        {
            mesh.BonePalettes.Add(palette.Select(entry => ResolveBoneHash(entry)).ToArray());
        }

        for (var i = 0; i < ttkMesh.TriangleSets.Count; i++)
        {
            var triangleSet = ttkMesh.TriangleSets[i];
            var vertexStart = Math.Max(0, triangleSet.MinVertIndex);
            var vertexEnd = Math.Min(positions.Count - 1, Math.Max(triangleSet.MinVertIndex, triangleSet.MaxVertIndex));
            if (vertexEnd < vertexStart)
            {
                continue;
            }

            var materialName = TextureName(triangleSet.T3DiffuseMap) ?? $"material_{i + 1}";
            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = NormalizePaletteIndex(triangleSet.BonePaletteIndex, mesh.BonePalettes.Count),
                SourceSubmeshIndex = i,
            };

            AddTextureSlot(submesh, "diffuse", triangleSet.T3DiffuseMap);
            AddTextureSlot(submesh, "detail_diffuse", triangleSet.T3DetailMap);
            AddTextureSlot(submesh, "bake", triangleSet.T3LightMap);
            AddTextureSlot(submesh, "bump", triangleSet.T3BumpMap);
            AddTextureSlot(submesh, "environment", triangleSet.T3EnvMap);

            for (var vertex = vertexStart; vertex <= vertexEnd; vertex++)
            {
                var p = positions[vertex];
                var n = ValueOrDefault(normals, vertex, (0f, 1f, 0f));
                var uv = ValueOrDefault(uv1, vertex, (0f, 0f));
                var uvb = ValueOrDefault(uv2, vertex, uv);
                var uvc = ValueOrDefault(uv3, vertex, uvb);
                var uvd = ValueOrDefault(uv4, vertex, uvc);
                var bone = ValueOrDefault(bones, vertex, (0, 0, 0, 0));
                var weight = ValueOrDefault(weights, vertex, (1f, 0f, 0f, 0f));
                var color = ValueOrDefault(colors, vertex, (1f, 1f, 1f, 1f));

                submesh.Vertices.Add(new VertexData(
                    p.Item1, p.Item2, p.Item3,
                    n.Item1, n.Item2, n.Item3,
                    uv.Item1, uv.Item2,
                    uvb.Item1, uvb.Item2,
                    uvc.Item1, uvc.Item2,
                    uvd.Item1, uvd.Item2,
                    bone.Item1, bone.Item2, bone.Item3, bone.Item4,
                    weight.Item1, weight.Item2, weight.Item3, weight.Item4,
                    color.Item1, color.Item2, color.Item3, color.Item4));
            }

            var firstTriangle = Math.Max(0, triangleSet.StartIndex / 3);
            var triangleCount = Math.Max(0, triangleSet.NumPrimitives);
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var index = firstTriangle + triangle;
                if (index < 0 || index >= indices.Count)
                {
                    continue;
                }

                var face = indices[index];
                var a = face.A - vertexStart;
                var b = face.B - vertexStart;
                var c = face.C - vertexStart;
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

    private static MeshData ConvertFromMeshData(D3DMesh ttkMesh, string fallbackName)
    {
        var meshData = ttkMesh.MeshData!;
        var vertexState = meshData.VertexStates![0];
        var lod = meshData.LODs![0];

        // Map attributes by semantic to buffer index
        var attrBySemantic = new Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams>();
        foreach (var attr in vertexState.Attributes)
        {
            attrBySemantic[attr.Attribute] = attr;
        }

        // Read vertex count from the position buffer or the first buffer
        int vertexCount = 0;
        if (attrBySemantic.TryGetValue(GFXPlatformVertexAttribute.Position, out var posAttr))
        {
            var posBuffer = vertexState.VertexBuffer[(int)posAttr.BufferIndex];
            vertexCount = (int)posBuffer.Count;
        }
        else if (vertexState.VertexBuffer.Count > 0)
        {
            vertexCount = (int)vertexState.VertexBuffer[0].Count;
        }

        if (vertexCount == 0)
        {
            throw new InvalidDataException("No vertices found in Toolkit mesh.");
        }

        // Read data from GFX buffers
        var positions = ReadGfxPositions(vertexState, attrBySemantic, vertexCount);
        var normals = ReadGfxNormals(vertexState, attrBySemantic, vertexCount);
        var uv1Raw = ReadGfxUvsRaw(vertexState, attrBySemantic, 0, vertexCount);
        var uv2Raw = ReadGfxUvsRaw(vertexState, attrBySemantic, 1, vertexCount);
        var uv3Raw = ReadGfxUvsRaw(vertexState, attrBySemantic, 2, vertexCount);
        var uv4Raw = ReadGfxUvsRaw(vertexState, attrBySemantic, 3, vertexCount);
        var bones = ReadGfxBones(vertexState, attrBySemantic, vertexCount);
        var weights = ReadGfxWeights(vertexState, attrBySemantic, vertexCount);
        var colors = ReadGfxColors(vertexState, attrBySemantic, vertexCount);
        var indices = ReadGfxIndices(vertexState);

        // Apply TexCoordTransform and V-flip per channel
        var uv1 = ApplyUvTransform(uv1Raw, meshData.TexCoordTransform[0]);
        var uv2 = ApplyUvTransform(uv2Raw, meshData.TexCoordTransform[1]);
        var uv3 = ApplyUvTransform(uv3Raw, meshData.TexCoordTransform[2]);
        var uv4 = ApplyUvTransform(uv4Raw, meshData.TexCoordTransform[3]);

        var mesh = new MeshData
        {
            Name = string.IsNullOrWhiteSpace(ttkMesh.Name) ? fallbackName : ttkMesh.Name,
            Version = ttkMesh.Version,
        };

        // Bone palettes from MeshData (mBonePalettes). When empty (common on v45), fall back to
        // mBones order so skin export can remap blend indices via skeleton hashes.
        if (meshData.BonePalettes is { Count: > 0 })
        {
            foreach (var palette in meshData.BonePalettes)
            {
                mesh.BonePalettes.Add(palette.Select(entry =>
                    entry.BoneName is { IsEmpty: false } ? entry.BoneName.Crc64 : 0UL).ToArray());
            }
        }
        else if (meshData.Bones is { Count: > 0 })
        {
            mesh.BonePalettes.Add(meshData.Bones
                .Select(bone => bone.BoneName is { IsEmpty: false } ? bone.BoneName.Crc64 : 0UL)
                .ToArray());
        }

        // Materials
        var materialNames = new List<string>();
        if (meshData.Materials is not null)
        {
            foreach (var mat in meshData.Materials)
            {
                materialNames.Add(mat.BaseMaterialName?.DebugString ?? $"material_{materialNames.Count + 1}");
            }
        }

        // Extract texture names from InternalResources (texture handles)
        var textureNames = new List<string>();
        foreach (var handle in ttkMesh.InternalResources)
        {
            if (handle is Handle<T3Texture> texHandle &&
                texHandle.ObjectInfo is not null &&
                !texHandle.ObjectInfo.ObjectName.IsEmpty)
            {
                var name = texHandle.ObjectInfo.ObjectName.DebugString
                    ?? $"0x{texHandle.ObjectInfo.ObjectName.Crc64:X16}";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    textureNames.Add(name);
                }
            }
        }

        var batches = lod.Batches;
        if (batches is null || batches.Count == 0)
        {
            batches = lod.Batches1 ?? lod.Batches2;
        }

        // If no batches were deserialized (e.g. schema mismatch for v45), treat the entire mesh as one batch
        if (batches is null || batches.Count == 0)
        {
            var materialName = materialNames.Count > 0 ? materialNames[0] : "default";
            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = 0,
                SourceSubmeshIndex = 0,
            };

            // Extract textures from the first material's property set
            if (meshData.Materials is not null && meshData.Materials.Count > 0)
            {
                var mat = meshData.Materials[0];
                if (mat.Material is not null)
                {
                    // The toolkit's GetXTexture helpers throw NullReferenceException when the
                    // material property set is not embedded in the mesh's InternalResources
                    // (external .prop materials, e.g. MCSM S2 skM1_radar/axel parts) — treat
                    // that as "no texture recorded" instead of failing the whole parse.
                    AddTextureSlot(submesh, "diffuse", SafeTexture(() => ttkMesh.GetDiffuseTexture(mat.Material)));
                    AddTextureSlot(submesh, "bump", SafeTexture(() => ttkMesh.GetNormalMapTexture(mat.Material)));
                    AddTextureSlot(submesh, "detail_diffuse", SafeTexture(() => ttkMesh.GetDetailTexture(mat.Material)));
                    AddTextureSlot(submesh, "specular", SafeTexture(() => ttkMesh.GetSpecularTexture(mat.Material)));

                    // External material: the handle's CRC64 is the CRC64 of a loose "<agent>_<material>_M.prop"
                    // file name (MCSM S2 skM1_lukas/radar/axel...). Resolve the diffuse from that prop.
                    if (!submesh.TextureNames.ContainsKey("diffuse"))
                    {
                        TryResolveExternalPropTextures(submesh, mat.Material, ttkMesh);
                    }
                }
            }

            // Also assign textures from InternalResources as fallback
            if (submesh.TextureNames.Count == 0 && textureNames.Count > 0)
            {
                submesh.TextureNames["diffuse"] = textureNames[0];
            }

            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                var p = ValueOrDefault(positions, vertex, (0f, 0f, 0f));
                var n = ValueOrDefault(normals, vertex, (0f, 1f, 0f));
                var uv = ValueOrDefault(uv1, vertex, (0f, 0f));
                var uvb = ValueOrDefault(uv2, vertex, uv);
                var uvc = ValueOrDefault(uv3, vertex, uvb);
                var uvd = ValueOrDefault(uv4, vertex, uvc);
                var bone = ValueOrDefault(bones, vertex, (0, 0, 0, 0));
                var weight = ValueOrDefault(weights, vertex, (1f, 0f, 0f, 0f));
                var color = ValueOrDefault(colors, vertex, (1f, 1f, 1f, 1f));

                submesh.Vertices.Add(new VertexData(
                    p.Item1, p.Item2, p.Item3,
                    n.Item1, n.Item2, n.Item3,
                    uv.Item1, uv.Item2,
                    uvb.Item1, uvb.Item2,
                    uvc.Item1, uvc.Item2,
                    uvd.Item1, uvd.Item2,
                    bone.Item1, bone.Item2, bone.Item3, bone.Item4,
                    weight.Item1, weight.Item2, weight.Item3, weight.Item4,
                    color.Item1, color.Item2, color.Item3, color.Item4));
            }

            foreach (var face in indices)
            {
                if (face.A < vertexCount && face.B < vertexCount && face.C < vertexCount)
                {
                    submesh.Faces.Add((face.A, face.B, face.C));
                }
            }

            if (submesh.Vertices.Count > 0 && submesh.Faces.Count > 0)
            {
                mesh.Submeshes.Add(submesh);
            }

            // A mesh whose batches all draw zero triangles is VALID: that is exactly how an
            // unassigned combined part is made invisible on reimport. Return it empty so previews
            // and combined groups keep working instead of failing the whole model.
            if (mesh.Submeshes.Count == 0 && (batches?.Count ?? 0) == 0)
            {
                throw new InvalidDataException("No submesh with valid triangles was found.");
            }

            return mesh;
        }

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var matIndex = batch.MaterialIndex;
            var materialName = matIndex >= 0 && matIndex < materialNames.Count
                ? materialNames[matIndex]
                : $"material_{batch.MaterialIndex + 1}";

            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = (int)batch.BonePaletteIndex,
                // Identity for reinsertion: the BATCH this submesh came from. (It used to store the
                // bone-palette index, which is 0 for every batch on most characters, so a round-trip
                // could not tell the batches apart.)
                SourceSubmeshIndex = batchIndex,
            };

            var vertexStart = (int)batch.MinVertIndex;
            var vertexEnd = Math.Min(vertexCount - 1, (int)Math.Max(batch.MinVertIndex, batch.MaxVertIndex));
            if (vertexEnd < vertexStart)
            {
                continue;
            }

            // Extract textures from the material property set via the toolkit API
            if (matIndex >= 0 && matIndex < (meshData.Materials?.Count ?? 0))
            {
                var mat = meshData.Materials![matIndex];
                if (mat.Material is not null)
                {
                    // The toolkit's GetXTexture helpers throw NullReferenceException when the
                    // material property set is not embedded in the mesh's InternalResources
                    // (external .prop materials, e.g. MCSM S2 skM1_radar/axel parts) — treat
                    // that as "no texture recorded" instead of failing the whole parse.
                    AddTextureSlot(submesh, "diffuse", SafeTexture(() => ttkMesh.GetDiffuseTexture(mat.Material)));
                    AddTextureSlot(submesh, "bump", SafeTexture(() => ttkMesh.GetNormalMapTexture(mat.Material)));
                    AddTextureSlot(submesh, "detail_diffuse", SafeTexture(() => ttkMesh.GetDetailTexture(mat.Material)));
                    AddTextureSlot(submesh, "specular", SafeTexture(() => ttkMesh.GetSpecularTexture(mat.Material)));

                    // External material: the handle's CRC64 is the CRC64 of a loose "<agent>_<material>_M.prop"
                    // file name (MCSM S2 skM1_lukas/radar/axel...). Resolve the diffuse from that prop.
                    if (!submesh.TextureNames.ContainsKey("diffuse"))
                    {
                        TryResolveExternalPropTextures(submesh, mat.Material, ttkMesh);
                    }
                }
            }

            // Also check meshData.Textures for lightmap/shadow
            if (meshData.Textures is not null)
            {
                foreach (var tex in meshData.Textures)
                {
                    var texName = tex.NameSymbol?.DebugString;
                    if (string.IsNullOrWhiteSpace(texName)) continue;
                    var slot = tex.TextureType switch
                    {
                        T3MeshTextureType.LightMap => "bake",
                        T3MeshTextureType.ShadowMap => "shadow",
                        _ => null,
                    };
                    if (slot is not null)
                    {
                        submesh.TextureNames[slot] = texName;
                    }
                }
            }

            for (var vertex = vertexStart; vertex <= vertexEnd; vertex++)
            {
                var p = ValueOrDefault(positions, vertex, (0f, 0f, 0f));
                var n = ValueOrDefault(normals, vertex, (0f, 1f, 0f));
                var uv = ValueOrDefault(uv1, vertex, (0f, 0f));
                var uvb = ValueOrDefault(uv2, vertex, uv);
                var uvc = ValueOrDefault(uv3, vertex, uvb);
                var uvd = ValueOrDefault(uv4, vertex, uvc);
                var bone = ValueOrDefault(bones, vertex, (0, 0, 0, 0));
                var weight = ValueOrDefault(weights, vertex, (1f, 0f, 0f, 0f));
                var color = ValueOrDefault(colors, vertex, (1f, 1f, 1f, 1f));

                submesh.Vertices.Add(new VertexData(
                    p.Item1, p.Item2, p.Item3,
                    n.Item1, n.Item2, n.Item3,
                    uv.Item1, uv.Item2,
                    uvb.Item1, uvb.Item2,
                    uvc.Item1, uvc.Item2,
                    uvd.Item1, uvd.Item2,
                    bone.Item1, bone.Item2, bone.Item3, bone.Item4,
                    weight.Item1, weight.Item2, weight.Item3, weight.Item4,
                    color.Item1, color.Item2, color.Item3, color.Item4));
            }

            // Map indices
            var firstTriangle = Math.Max(0, (int)batch.StartIndex / 3);
            var triangleCount = Math.Max(0, (int)batch.NumPrimitives);
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var index = firstTriangle + triangle;
                if (index < 0 || index >= indices.Count)
                {
                    continue;
                }

                var face = indices[index];
                var a = face.A - vertexStart;
                var b = face.B - vertexStart;
                var c = face.C - vertexStart;
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

        // A mesh whose batches all draw zero triangles is VALID: that is exactly how an
        // unassigned combined part is made invisible on reimport. Return it empty so previews
        // and combined groups keep working instead of failing the whole model.
        if (mesh.Submeshes.Count == 0 && batches.Count == 0)
        {
            throw new InvalidDataException("No submesh with valid triangles was found.");
        }

        return mesh;
    }

    private static T3VertexBuffer? GetBuffer(D3DMesh mesh, int index)
        => mesh.T3VertexBuffers is not null && index >= 0 && index < mesh.T3VertexBuffers.Length
            ? mesh.T3VertexBuffers[index]
            : null;

    private static List<(float X, float Y, float Z)> ReadPositions(T3VertexBuffer? buffer)
    {
        var result = new List<(float X, float Y, float Z)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var stride = Math.Max(buffer.VertSize, 12);
        for (var i = 0; i < buffer.NumVerts; i++)
        {
            var offset = i * stride;
            if (offset + 12 > buffer.Buffer.Length)
            {
                break;
            }

            result.Add((ReadSingle(buffer.Buffer, offset), ReadSingle(buffer.Buffer, offset + 4), ReadSingle(buffer.Buffer, offset + 8)));
        }

        return result;
    }

    private static List<(float X, float Y, float Z)> ReadNormals(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float X, float Y, float Z)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadVector3(buffer.Buffer, offset, component.Type, out var value))
            {
                break;
            }

            result.Add(value);
        }

        return result;
    }

    private static List<(float U, float V)> ReadUvs(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float U, float V)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadUv(buffer.Buffer, offset, component.Type, out var uv))
            {
                break;
            }

            result.Add(uv);
        }

        return result;
    }

    private static List<(int A, int B, int C, int D)> ReadBones(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(int A, int B, int C, int D)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (offset + 4 > buffer.Buffer.Length)
            {
                break;
            }

            result.Add(component.Type switch
            {
                T3VertexComponent.EnumType.VTypeS8NBones => (buffer.Buffer[offset] / 3, buffer.Buffer[offset + 1] / 3, buffer.Buffer[offset + 2] / 3, buffer.Buffer[offset + 3] / 3),
                T3VertexComponent.EnumType.VTypeU8N => (buffer.Buffer[offset] / 4, buffer.Buffer[offset + 1] / 4, buffer.Buffer[offset + 2] / 4, buffer.Buffer[offset + 3] / 4),
                _ => (buffer.Buffer[offset], buffer.Buffer[offset + 1], buffer.Buffer[offset + 2], buffer.Buffer[offset + 3]),
            });
        }

        return result;
    }

    private static List<(float A, float B, float C, float D)> ReadWeights(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float A, float B, float C, float D)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadWeights(buffer.Buffer, offset, component.Type, out var weight))
            {
                break;
            }

            result.Add(Normalize(weight));
        }

        return result;
    }

    private static List<(float R, float G, float B, float A)> ReadColors(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float R, float G, float B, float A)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadColor(buffer.Buffer, offset, component.Type, out var color))
            {
                break;
            }

            result.Add(color);
        }

        return result;
    }

    private static List<(int A, int B, int C)> ReadIndices(T3IndexBuffer? buffer)
    {
        var result = new List<(int A, int B, int C)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var indexSize = buffer.Format == 102 ? 4 : 2;
        for (var i = 0; i + indexSize * 3 <= buffer.Buffer.Length; i += indexSize * 3)
        {
            result.Add((
                ReadIndex(buffer.Buffer, i, indexSize),
                ReadIndex(buffer.Buffer, i + indexSize, indexSize),
                ReadIndex(buffer.Buffer, i + indexSize * 2, indexSize)));
        }

        return result;
    }

    private static T3VertexComponent FirstComponent(T3VertexBuffer buffer)
        => buffer.VertexComponents.FirstOrDefault(component => component.Type != T3VertexComponent.EnumType.VTypeNone)
           ?? new T3VertexComponent { Count = 1, Type = GuessType(buffer) };

    private static T3VertexComponent.EnumType GuessType(T3VertexBuffer buffer)
        => buffer.VertSize switch
        {
            4 => T3VertexComponent.EnumType.VTypeS8N,
            8 => T3VertexComponent.EnumType.VTypeS16N,
            12 => T3VertexComponent.EnumType.VTypeFloat,
            16 => T3VertexComponent.EnumType.VTypeFloat,
            _ => T3VertexComponent.EnumType.VTypeFloat,
        };

    private static bool TryReadVector3(byte[] data, int offset, T3VertexComponent.EnumType type, out (float X, float Y, float Z) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 12 > data.Length) return false;
                value = (ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));
                return true;
            case T3VertexComponent.EnumType.VTypeS8N:
            case T3VertexComponent.EnumType.VTypeS8NBones:
                if (offset + 3 > data.Length) return false;
                value = (unchecked((sbyte)data[offset]) / 127f, unchecked((sbyte)data[offset + 1]) / 127f, unchecked((sbyte)data[offset + 2]) / 127f);
                return true;
            case T3VertexComponent.EnumType.VTypeS16N:
                if (offset + 6 > data.Length) return false;
                value = (ReadInt16(data, offset) / 32767f, ReadInt16(data, offset + 2) / 32767f, ReadInt16(data, offset + 4) / 32767f);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadUv(byte[] data, int offset, T3VertexComponent.EnumType type, out (float U, float V) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 8 > data.Length) return false;
                value = (ReadSingle(data, offset), 1f - ReadSingle(data, offset + 4));
                return true;
            case T3VertexComponent.EnumType.VTypeS16N:
                if (offset + 4 > data.Length) return false;
                value = (ReadInt16(data, offset) / 32767f, 1f - ReadInt16(data, offset + 2) / 32767f);
                return true;
            case T3VertexComponent.EnumType.VTypeU16N:
                if (offset + 4 > data.Length) return false;
                value = (ReadUInt16(data, offset) / 65535f, 1f - ReadUInt16(data, offset + 2) / 65535f);
                return true;
            case T3VertexComponent.EnumType.VTypeSF16:
                if (offset + 4 > data.Length) return false;
                value = (ReadHalf(data, offset), 1f - ReadHalf(data, offset + 2));
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadWeights(byte[] data, int offset, T3VertexComponent.EnumType type, out (float A, float B, float C, float D) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 12 > data.Length) return false;
                value = (ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8), 0f);
                return true;
            case T3VertexComponent.EnumType.VTypeS16N:
                if (offset + 8 > data.Length) return false;
                value = (ReadInt16(data, offset) / 32767f, ReadInt16(data, offset + 2) / 32767f, ReadInt16(data, offset + 4) / 32767f, ReadInt16(data, offset + 6) / 32767f);
                return true;
            case T3VertexComponent.EnumType.VTypeU16N:
                if (offset + 8 > data.Length) return false;
                value = (ReadUInt16(data, offset) / 65535f, ReadUInt16(data, offset + 2) / 65535f, ReadUInt16(data, offset + 4) / 65535f, ReadUInt16(data, offset + 6) / 65535f);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadColor(byte[] data, int offset, T3VertexComponent.EnumType type, out (float R, float G, float B, float A) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 16 > data.Length) return false;
                value = (ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8), ReadSingle(data, offset + 12));
                return true;
            case T3VertexComponent.EnumType.VTypeU8N:
                if (offset + 4 > data.Length) return false;
                value = (data[offset] / 255f, data[offset + 1] / 255f, data[offset + 2] / 255f, data[offset + 3] / 255f);
                return true;
            default:
                return false;
        }
    }

    private static int ComponentSize(T3VertexComponent.EnumType type, uint count)
    {
        var itemSize = type switch
        {
            T3VertexComponent.EnumType.VTypeFloat => 4,
            T3VertexComponent.EnumType.VTypeS8N or T3VertexComponent.EnumType.VTypeU8N or T3VertexComponent.EnumType.VTypeS8NBones => 1,
            T3VertexComponent.EnumType.VTypeS16N or T3VertexComponent.EnumType.VTypeU16N or T3VertexComponent.EnumType.VTypeSF16 => 2,
            _ => 0,
        };

        return itemSize * Math.Max(1, checked((int)count));
    }

    private static (float A, float B, float C, float D) Normalize((float A, float B, float C, float D) value)
    {
        var total = value.A + value.B + value.C + value.D;
        if (total <= 0.000001f)
        {
            return (1f, 0f, 0f, 0f);
        }

        return (value.A / total, value.B / total, value.C / total, value.D / total);
    }

    private static T ValueOrDefault<T>(IReadOnlyList<T> values, int index, T fallback)
        => index >= 0 && index < values.Count ? values[index] : fallback;

    // Shields callers from toolkit texture lookups that dereference a missing internal resource.
    private static Handle<T3Texture>? SafeTexture(Func<Handle<T3Texture>?> getter)
    {
        try { return getter(); } catch { return null; }
    }

    // ── External .prop material resolution (MCSM S2) ──
    // Some characters keep their material PropertySets in loose "<agent>_<material>_M.prop" files
    // instead of the mesh's InternalResources. The mesh's material handle CRC64 equals the CRC64 of
    // that prop FILE NAME, and inside the prop "Material - Diffuse Texture" holds the CRC64 of the
    // .d3dtx file name. Both were verified against skM1_lukas100 (handle C7ADAD4D2C4F3170 =
    // crc64("skm1_lukas100_skm1_lukas100_clothes_m.prop"), diffuse 22FF96C2A42EA795 =
    // crc64("skm1_lukas100_clothes.d3dtx")).

    private static readonly ulong DiffuseTextureKeyCrc = Crc64.Compute("Material - Diffuse Texture");

    private static readonly object ExternalPropGate = new();
    // Both caches are keyed by scan root and kept for the whole session: alternating between
    // sibling folders (combined groups, tree preview) must never re-walk the extracted tree.
    private static readonly Dictionary<string, Dictionary<ulong, string>> ExternalPropFilesByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<ulong, ulong?>> ExternalPropDiffuseByRoot =
        new(StringComparer.OrdinalIgnoreCase);

    private static void TryResolveExternalPropTextures(SubmeshData submesh, Handle<PropertySet>? material, D3DMesh ttkMesh)
    {
        try
        {
            var handleName = material?.ObjectInfo?.ObjectName;
            if (handleName is null || handleName.IsEmpty)
            {
                return;
            }

            var meshStem = Path.GetFileNameWithoutExtension(ttkMesh.Name ?? "");
            var diffuseCrc = LookupExternalPropDiffuse(handleName.Crc64, meshStem);
            if (diffuseCrc is not { } crc || crc == 0)
            {
                return;
            }

            submesh.TextureNames["diffuse"] =
                Core.TextureHashDatabase.Resolve((uint)(crc & 0xFFFFFFFF), (uint)(crc >> 32));
        }
        catch
        {
            // External material resolution is best-effort; the mesh still loads untextured.
        }
    }

    // When the .prop file itself is absent (folders extracted without materials), the material can
    // still be identified: its handle CRC64 is the hash of the prop FILE NAME, which follows the
    // "<agent>_<textureStem>_M.prop" convention. Hashing that pattern for every sibling .d3dtx and
    // comparing against the handle recovers the exact texture — no guessing by name similarity.
    private static ulong? LookupPropNameByConvention(ulong materialHandleCrc, string meshFolder, string meshStem)
    {
        var agents = new List<string> { meshStem };
        var lastUnderscore = meshStem.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            agents.Add(meshStem[..lastUnderscore]);
        }

        var scanFolders = new List<string> { meshFolder };
        var parent = Path.GetDirectoryName(meshFolder);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            scanFolders.Add(parent);
        }

        foreach (var folder in scanFolders)
        {
            IEnumerable<string> textures;
            try
            {
                textures = Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var texturePath in textures)
            {
                var textureStem = Path.GetFileNameWithoutExtension(texturePath);
                foreach (var agent in agents)
                {
                    if (Crc64.Compute($"{agent}_{textureStem}_M.prop") == materialHandleCrc)
                    {
                        return Crc64.Compute(textureStem + ".d3dtx");
                    }
                }
            }
        }

        return null;
    }

    private static ulong? LookupExternalPropDiffuse(ulong materialHandleCrc, string meshStem)
    {
        lock (ExternalPropGate)
        {
            // The ambient folder is the mesh's own directory; props often sit in a SIBLING folder
            // (e.g. Minecraft201_Eng next to Minecraft201), so scan from the parent when possible.
            var meshFolder = Core.TextureHashDatabase.CurrentFolder;
            if (string.IsNullOrWhiteSpace(meshFolder))
            {
                return null;
            }

            var scanRoot = Path.GetDirectoryName(meshFolder) ?? meshFolder;
            if (!ExternalPropFilesByRoot.TryGetValue(scanRoot, out var propFilesByCrc))
            {
                propFilesByCrc = new Dictionary<ulong, string>();
                ExternalPropFilesByRoot[scanRoot] = propFilesByCrc;
                ExternalPropDiffuseByRoot[scanRoot] = new Dictionary<ulong, ulong?>();
                try
                {
                    foreach (var propPath in Directory.EnumerateFiles(scanRoot, "*.prop", SearchOption.AllDirectories))
                    {
                        propFilesByCrc.TryAdd(Crc64.Compute(Path.GetFileName(propPath)), propPath);
                    }
                }
                catch
                {
                    // Unreadable subfolders are skipped; the index keeps whatever was collected.
                }
            }

            var diffuseCache = ExternalPropDiffuseByRoot[scanRoot];
            if (diffuseCache.TryGetValue(materialHandleCrc, out var cached))
            {
                return cached;
            }

            ulong? result = null;
            if (propFilesByCrc.TryGetValue(materialHandleCrc, out var matchedPropPath))
            {
                try
                {
                    using var stream = File.OpenRead(matchedPropPath);
                    var props = Toolkit.Instance.Deserialize<PropertySet>(stream);
                    result = FindDiffuseHandleCrc(props, depth: 0);
                }
                catch
                {
                    result = null;
                }
            }

            // No .prop on disk: recover the texture from the prop NAME convention (exact, hash-verified).
            result ??= LookupPropNameByConvention(materialHandleCrc, meshFolder, meshStem);

            diffuseCache[materialHandleCrc] = result;
            return result;
        }
    }

    private static ulong? FindDiffuseHandleCrc(PropertySet? props, int depth)
    {
        if (props is null || depth > 4)
        {
            return null;
        }

        foreach (var (key, entry) in props.Properties)
        {
            if (key.Crc64 == DiffuseTextureKeyCrc && entry.Value is HandleBase handle)
            {
                return handle.ObjectInfo?.ObjectName?.Crc64;
            }
        }

        return FindDiffuseHandleCrc(props.ParentProperties, depth + 1);
    }

    private static void AddTextureSlot(SubmeshData submesh, string slot, Handle<T3Texture>? handle)
    {
        var name = TextureName(handle);
        if (!string.IsNullOrWhiteSpace(name))
        {
            submesh.TextureNames[slot] = name;
        }
    }

    private static string? TextureName(Handle<T3Texture>? handle)
    {
        if (handle is null || handle.ObjectInfo.ObjectName.IsEmpty)
        {
            return null;
        }

        if (handle.ObjectInfo.ObjectName.DebugString is { } debugName)
        {
            return debugName;
        }

        // Unresolved symbol: run the CRC64 through the texture hash DB (folder-aware, so a
        // matching .d3dtx next to the mesh yields the real, original-case name).
        var crc = handle.ObjectInfo.ObjectName.Crc64;
        return Core.TextureHashDatabase.Resolve((uint)(crc & 0xFFFFFFFF), (uint)(crc >> 32));
    }

    private static ulong ResolveBoneHash(D3DMesh.PaletteEntry entry)
    {
        if (entry.SymbolBoneName is not null && !entry.SymbolBoneName.IsEmpty)
        {
            return entry.SymbolBoneName.Crc64;
        }

        return string.IsNullOrWhiteSpace(entry.BoneName) ? 0UL : TelltaleToolKit.Hashing.Crc64.Compute(entry.BoneName);
    }

    private static int NormalizePaletteIndex(int rawIndex, int paletteCount)
    {
        if (paletteCount <= 0)
        {
            return 0;
        }

        if (rawIndex >= 0 && rawIndex < paletteCount)
        {
            return rawIndex;
        }

        if (rawIndex > 0 && rawIndex - 1 < paletteCount)
        {
            return rawIndex - 1;
        }

        return 0;
    }

    private static float ReadSingle(byte[] data, int offset)
        => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

    private static short ReadInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));

    private static ushort ReadUInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static float ReadHalf(byte[] data, int offset)
        => (float)BitConverter.UInt16BitsToHalf(ReadUInt16(data, offset));

    private static int ReadIndex(byte[] data, int offset, int indexSize)
        => indexSize == 4
            ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static void EnsureToolkitInitialized()
    {
        if (Toolkit.IsInitialized)
        {
            return;
        }

        lock (ToolkitGate)
        {
            if (!Toolkit.IsInitialized)
            {
                Toolkit.Initialize(new Toolkit.Configuration
                {
                    DataFolder = Path.Combine(AppContext.BaseDirectory, "ttk-data"),
                });
            }
        }
    }

    // ── GFX buffer readers (T3MeshData / v22+ path) ──

    private static GFXPlatformAttributeParams? FindGfxAttribute(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        GFXPlatformVertexAttribute semantic,
        uint attributeIndex = 0)
    {
        // For TexCoord, search all attributes since multiple TexCoords share the same semantic
        if (semantic == GFXPlatformVertexAttribute.TexCoord)
        {
            return vertexState.Attributes.FirstOrDefault(a =>
                a.Attribute == semantic && a.AttributeIndex == attributeIndex);
        }

        return attrBySemantic.TryGetValue(semantic, out var attr) ? attr : null;
    }

    private static byte[] GetGfxBufferBytes(T3GFXVertexState vertexState, GFXPlatformAttributeParams attr)
    {
        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        int start = (int)attr.BufferOffset;
        int length = buffer.Buffer.Length - start;
        return buffer.Buffer.AsSpan(start, length).ToArray();
    }

    private static List<(float X, float Y, float Z)> ReadGfxPositions(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        int vertexCount)
    {
        var result = new List<(float, float, float)>();
        var attr = FindGfxAttribute(vertexState, attrBySemantic, GFXPlatformVertexAttribute.Position);
        if (attr is null) return result;

        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        var data = buffer.Buffer;
        int offset = (int)attr.BufferOffset;
        int stride = Math.Max((int)buffer.Stride, 12);

        for (int i = 0; i < vertexCount; i++)
        {
            int pos = offset + i * stride;
            if (pos + 12 > data.Length) break;
            result.Add((
                ReadSingle(data, pos),
                ReadSingle(data, pos + 4),
                ReadSingle(data, pos + 8)));
        }

        return result;
    }

    private static List<(float X, float Y, float Z)> ReadGfxNormals(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        int vertexCount)
    {
        var result = new List<(float, float, float)>();
        var attr = FindGfxAttribute(vertexState, attrBySemantic, GFXPlatformVertexAttribute.Normal);
        if (attr is null) return result;

        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        var data = buffer.Buffer;
        int offset = (int)attr.BufferOffset;
        int stride = Math.Max((int)buffer.Stride, 4);

        for (int i = 0; i < vertexCount; i++)
        {
            int pos = offset + i * stride;
            switch (attr.Format)
            {
                case GFXPlatformFormat.F32x3:
                    if (pos + 12 > data.Length) return result;
                    result.Add((ReadSingle(data, pos), ReadSingle(data, pos + 4), ReadSingle(data, pos + 8)));
                    break;
                case GFXPlatformFormat.SN8x4:
                case GFXPlatformFormat.UN8x4:
                    if (pos + 4 > data.Length) return result;
                    result.Add((
                        unchecked((sbyte)data[pos]) / 127f,
                        unchecked((sbyte)data[pos + 1]) / 127f,
                        unchecked((sbyte)data[pos + 2]) / 127f));
                    break;
                case GFXPlatformFormat.SN16x4:
                case GFXPlatformFormat.UN16x4:
                    if (pos + 8 > data.Length) return result;
                    result.Add((
                        ReadInt16(data, pos) / 32767f,
                        ReadInt16(data, pos + 2) / 32767f,
                        ReadInt16(data, pos + 4) / 32767f));
                    break;
                default:
                    return result;
            }
        }

        return result;
    }

    private static List<(float U, float V)> ReadGfxUvsRaw(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        int uvChannel,
        int vertexCount)
    {
        var result = new List<(float, float)>();
        var attr = FindGfxAttribute(vertexState, attrBySemantic, GFXPlatformVertexAttribute.TexCoord, (uint)uvChannel);
        if (attr is null) return result;

        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        var data = buffer.Buffer;
        int offset = (int)attr.BufferOffset;
        int minStride = attr.Format switch
        {
            GFXPlatformFormat.F32x2 => 8,
            _ => 4, // SN16x2, UN16x2, F16x2
        };
        int stride = Math.Max((int)buffer.Stride, minStride);

        for (int i = 0; i < vertexCount; i++)
        {
            int pos = offset + i * stride;
            switch (attr.Format)
            {
                case GFXPlatformFormat.F32x2:
                    if (pos + 8 > data.Length) return result;
                    result.Add((ReadSingle(data, pos), ReadSingle(data, pos + 4)));
                    break;
                case GFXPlatformFormat.SN16x2:
                    if (pos + 4 > data.Length) return result;
                    result.Add((ReadInt16(data, pos) / 32767f, ReadInt16(data, pos + 2) / 32767f));
                    break;
                case GFXPlatformFormat.UN16x2:
                    if (pos + 4 > data.Length) return result;
                    result.Add((ReadUInt16(data, pos) / 65535f, ReadUInt16(data, pos + 2) / 65535f));
                    break;
                case GFXPlatformFormat.F16x2:
                    if (pos + 4 > data.Length) return result;
                    result.Add((ReadHalf(data, pos), ReadHalf(data, pos + 2)));
                    break;
                default:
                    return result;
            }
        }

        return result;
    }

    private static List<(float U, float V)> ApplyUvTransform(
        List<(float U, float V)> rawUvs,
        T3MeshTexCoordTransform transform)
    {
        if (rawUvs.Count == 0) return rawUvs;
        var result = new List<(float, float)>(rawUvs.Count);
        foreach (var (u, v) in rawUvs)
        {
            var finalU = u * transform.Scale.X + transform.Offset.X;
            var finalV = 1f - (v * transform.Scale.Y + transform.Offset.Y);
            result.Add((finalU, finalV));
        }
        return result;
    }

    private static List<(int A, int B, int C, int D)> ReadGfxBones(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        int vertexCount)
    {
        var result = new List<(int, int, int, int)>();
        var attr = FindGfxAttribute(vertexState, attrBySemantic, GFXPlatformVertexAttribute.BlendIndex);
        if (attr is null) return result;

        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        var data = buffer.Buffer;
        int offset = (int)attr.BufferOffset;
        int stride = Math.Max((int)buffer.Stride, 4);

        for (int i = 0; i < vertexCount; i++)
        {
            int pos = offset + i * stride;
            if (pos + 4 > data.Length) break;
            switch (attr.Format)
            {
                case GFXPlatformFormat.U8x4:
                    result.Add((data[pos], data[pos + 1], data[pos + 2], data[pos + 3]));
                    break;
                case GFXPlatformFormat.UN8x4:
                    result.Add((data[pos] / 3, data[pos + 1] / 3, data[pos + 2] / 3, data[pos + 3] / 3));
                    break;
                case GFXPlatformFormat.U16x4:
                    if (pos + 8 > data.Length) return result;
                    result.Add((
                        ReadUInt16(data, pos),
                        ReadUInt16(data, pos + 2),
                        ReadUInt16(data, pos + 4),
                        ReadUInt16(data, pos + 6)));
                    break;
                default:
                    result.Add((data[pos], data[pos + 1], data[pos + 2], data[pos + 3]));
                    break;
            }
        }

        return result;
    }

    private static List<(float A, float B, float C, float D)> ReadGfxWeights(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        int vertexCount)
    {
        var result = new List<(float, float, float, float)>();
        var attr = FindGfxAttribute(vertexState, attrBySemantic, GFXPlatformVertexAttribute.BlendWeight);
        if (attr is null) return result;

        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        var data = buffer.Buffer;
        int offset = (int)attr.BufferOffset;
        int stride = Math.Max((int)buffer.Stride, 4);

        for (int i = 0; i < vertexCount; i++)
        {
            int pos = offset + i * stride;
            switch (attr.Format)
            {
                case GFXPlatformFormat.F32x4:
                    if (pos + 16 > data.Length) return result;
                    result.Add(Normalize((
                        ReadSingle(data, pos),
                        ReadSingle(data, pos + 4),
                        ReadSingle(data, pos + 8),
                        ReadSingle(data, pos + 12))));
                    break;
                case GFXPlatformFormat.UN8x4:
                    if (pos + 4 > data.Length) return result;
                    result.Add(Normalize((
                        data[pos] / 255f,
                        data[pos + 1] / 255f,
                        data[pos + 2] / 255f,
                        data[pos + 3] / 255f)));
                    break;
                case GFXPlatformFormat.UN16x4:
                    if (pos + 8 > data.Length) return result;
                    result.Add(Normalize((
                        ReadUInt16(data, pos) / 65535f,
                        ReadUInt16(data, pos + 2) / 65535f,
                        ReadUInt16(data, pos + 4) / 65535f,
                        ReadUInt16(data, pos + 6) / 65535f)));
                    break;
                default:
                    return result;
            }
        }

        return result;
    }

    private static List<(float R, float G, float B, float A)> ReadGfxColors(
        T3GFXVertexState vertexState,
        Dictionary<GFXPlatformVertexAttribute, GFXPlatformAttributeParams> attrBySemantic,
        int vertexCount)
    {
        var result = new List<(float, float, float, float)>();
        var attr = FindGfxAttribute(vertexState, attrBySemantic, GFXPlatformVertexAttribute.Color);
        if (attr is null) return result;

        var buffer = vertexState.VertexBuffer[(int)attr.BufferIndex];
        var data = buffer.Buffer;
        int offset = (int)attr.BufferOffset;
        int stride = Math.Max((int)buffer.Stride, 4);

        for (int i = 0; i < vertexCount; i++)
        {
            int pos = offset + i * stride;
            switch (attr.Format)
            {
                case GFXPlatformFormat.F32x4:
                    if (pos + 16 > data.Length) return result;
                    result.Add((
                        ReadSingle(data, pos),
                        ReadSingle(data, pos + 4),
                        ReadSingle(data, pos + 8),
                        ReadSingle(data, pos + 12)));
                    break;
                case GFXPlatformFormat.UN8x4:
                case GFXPlatformFormat.D3DCOLOR:
                    if (pos + 4 > data.Length) return result;
                    result.Add((
                        data[pos] / 255f,
                        data[pos + 1] / 255f,
                        data[pos + 2] / 255f,
                        data[pos + 3] / 255f));
                    break;
                default:
                    return result;
            }
        }

        return result;
    }

    private static List<(int A, int B, int C)> ReadGfxIndices(T3GFXVertexState vertexState)
    {
        var result = new List<(int, int, int)>();
        if (vertexState.IndexBuffer.Count == 0) return result;
        var idxBuffer = vertexState.IndexBuffer[0];
        var data = idxBuffer.Buffer;
        int indexSize = (int)idxBuffer.Stride; // 2 for U16, 4 for U32

        for (int i = 0; i + indexSize * 3 <= data.Length; i += (int)indexSize * 3)
        {
            result.Add((
                indexSize == 4
                    ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)))
                    : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i, 2)),
                indexSize == 4
                    ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + indexSize, 4)))
                    : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + indexSize, 2)),
                indexSize == 4
                    ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + indexSize * 2, 4)))
                    : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + indexSize * 2, 2))));
        }

        return result;
    }

    internal sealed record OldBonePaletteInfo(
        IReadOnlyList<ulong[]> Palettes,
        IReadOnlyList<int> TriangleSetPaletteIndices);
}
