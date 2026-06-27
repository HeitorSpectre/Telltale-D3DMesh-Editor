using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Export;

// Result of building a complete asset: the glTF document (without the final buffer/image
// locations, which each writer chooses), the binary geometry buffer, and the ordered PNG image
// list whose index matches gltf["images"].
internal sealed class BuiltAsset
{
    public required Dictionary<string, object> Gltf { get; init; }
    public required List<byte> Bin { get; init; }
    // Index == image index in glTF. Material-referenced images come first; auxiliary images follow.
    public required List<(string Name, byte[] Png)> Images { get; init; }
}

// Builds a complete glTF asset (mesh + baseColor textures + skeleton with skin/inverseBind)
// independently of the output format. The embedded GLB and separate GLTF writers share this.
internal static class AssetGltfBuilder
{
    private const float LineOverlayNormalOffset = 0.0015f;
    private const float SourceAlphaLineOverlayStrength = 1.0f;
    private static readonly string[] LightmapSlots = ["bake", "lightmap", "light_map", "lighting", "lighting_map"];

    public static BuiltAsset Build(
        MeshData mesh,
        SkeletonData? skeleton,
        IReadOnlyDictionary<string, (byte[] Png, float AvgAlpha)> baseColorPngByName,
        IReadOnlyDictionary<string, byte[]>? auxiliaryPngByName)
    {
        var bin = new List<byte>();
        var bufferViews = new List<Dictionary<string, object>>();
        var accessors = new List<Dictionary<string, object>>();
        var primitives = new List<Dictionary<string, object>>();
        var materials = new List<Dictionary<string, object>>();
        var images = new List<(string Name, byte[] Png)>();
        var textures = new List<Dictionary<string, object>>();

        var boneIndexByHash = new Dictionary<ulong, int>();
        if (skeleton is not null)
        {
            for (var i = 0; i < skeleton.Bones.Count; i++)
            {
                boneIndexByHash.TryAdd(skeleton.Bones[i].Hash, i);
            }
        }

        // Textures: diffuse/baseColor and, when present, separate Telltale slots
        // (detail/lines, bump, bake, shadow, etc.) as extra images/textures.
        var textureIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var imageIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pngByName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var lowAlphaByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var lineOverlayAlphaModeByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in baseColorPngByName)
        {
            lowAlphaByName[name] = entry.AvgAlpha < 0.95f;
            var imageIndex = images.Count;
            images.Add((name, entry.Png));
            pngByName[name] = entry.Png;
            imageIndexByName[name] = imageIndex;
            textureIndexByName[name] = textures.Count;
            textures.Add(new Dictionary<string, object> { ["source"] = imageIndex, ["sampler"] = 0 });
        }
        if (auxiliaryPngByName is not null)
        {
            foreach (var (name, png) in auxiliaryPngByName.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!imageIndexByName.ContainsKey(name))
                {
                    imageIndexByName[name] = images.Count;
                    images.Add((name, png));
                    pngByName[name] = png;
                }
                if (!textureIndexByName.ContainsKey(name))
                {
                    textureIndexByName[name] = textures.Count;
                    textures.Add(new Dictionary<string, object> { ["source"] = imageIndexByName[name], ["sampler"] = 0 });
                }
            }
        }

        var materialMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exportSkinning = skeleton is { Bones.Count: > 0 };
        var usesLineOverlayMaterials = false;
        foreach (var submesh in mesh.Submeshes)
        {
            var vertexCount = submesh.Vertices.Count;
            var positions = new List<byte>();
            var linePositions = new List<byte>();
            var normals = new List<byte>();
            var binormals = new List<byte>();
            var unknown1s = new List<byte>();
            var uvs = new List<byte>();
            var uv2s = new List<byte>();
            var uv3s = new List<byte>();
            var uv4s = new List<byte>();
            var uv5s = new List<byte>();
            var uv6s = new List<byte>();
            var colors = new List<byte>();
            var tangents = BuildTangents(submesh, out var originalTangentWs);
            var hasStoredBinormals = submesh.Vertices.Any(HasStoredBinormal);
            var hasStoredUnknown1 = submesh.Vertices.Any(vertex => Math.Abs(vertex.Unknown1) > 0.000001f);
            var joints = new List<byte>();
            var weights = new List<byte>();
            var xs = new List<float>();
            var ys = new List<float>();
            var zs = new List<float>();
            var lineXs = new List<float>();
            var lineYs = new List<float>();
            var lineZs = new List<float>();
            var hasVertexColors = false;
            var exportVertexColors = GameConfig.Current.Id != GameId.WalkingDeadMichonne;
            var exportMichonneVertexAlpha = GameConfig.Current.Id == GameId.WalkingDeadMichonne &&
                                            submesh.Vertices.Any(vertex => vertex.ColorA < 0.98f);

            var palette = submesh.BonePaletteIndex >= 0 && submesh.BonePaletteIndex < mesh.BonePalettes.Count
                ? mesh.BonePalettes[submesh.BonePaletteIndex]
                : null;

            foreach (var v in submesh.Vertices)
            {
                var vertexColor = NormalizeVertexColor(v);
                GltfCommon.AddFloat(positions, v.X); GltfCommon.AddFloat(positions, v.Y); GltfCommon.AddFloat(positions, v.Z);
                GltfCommon.AddFloat(normals, v.Nx); GltfCommon.AddFloat(normals, v.Ny); GltfCommon.AddFloat(normals, v.Nz);
                if (hasStoredBinormals)
                {
                    GltfCommon.AddFloat(binormals, v.BinormalX);
                    GltfCommon.AddFloat(binormals, v.BinormalY);
                    GltfCommon.AddFloat(binormals, v.BinormalZ);
                    GltfCommon.AddFloat(binormals, v.BinormalW);
                }
                if (hasStoredUnknown1)
                {
                    GltfCommon.AddFloat(unknown1s, v.Unknown1);
                    GltfCommon.AddFloat(unknown1s, 0f);
                    GltfCommon.AddFloat(unknown1s, 0f);
                    GltfCommon.AddFloat(unknown1s, 0f);
                }
                var overlayNormal = SafeNormalize(new Vector3(v.Nx, v.Ny, v.Nz), Vector3.Zero);
                var lineX = v.X + overlayNormal.X * LineOverlayNormalOffset;
                var lineY = v.Y + overlayNormal.Y * LineOverlayNormalOffset;
                var lineZ = v.Z + overlayNormal.Z * LineOverlayNormalOffset;
                GltfCommon.AddFloat(linePositions, lineX); GltfCommon.AddFloat(linePositions, lineY); GltfCommon.AddFloat(linePositions, lineZ);
                // Inverted V: Telltale/preview samples in S-row space; GLTF samples in V (top-left).
                GltfCommon.AddFloat(uvs, v.U); GltfCommon.AddFloat(uvs, 1f - v.V);
                GltfCommon.AddFloat(uv2s, v.U2); GltfCommon.AddFloat(uv2s, 1f - v.V2);
                GltfCommon.AddFloat(uv3s, v.U3); GltfCommon.AddFloat(uv3s, 1f - v.V3);
                GltfCommon.AddFloat(uv4s, v.U4); GltfCommon.AddFloat(uv4s, 1f - v.V4);
                GltfCommon.AddFloat(uv5s, v.U5); GltfCommon.AddFloat(uv5s, 1f - v.V5);
                GltfCommon.AddFloat(uv6s, v.U6); GltfCommon.AddFloat(uv6s, 1f - v.V6);
                if (exportVertexColors)
                {
                    GltfCommon.AddFloat(colors, vertexColor.R); GltfCommon.AddFloat(colors, vertexColor.G);
                    GltfCommon.AddFloat(colors, vertexColor.B); GltfCommon.AddFloat(colors, vertexColor.A);
                    hasVertexColors |= Math.Abs(vertexColor.R - 1f) > 0.001f ||
                                       Math.Abs(vertexColor.G - 1f) > 0.001f ||
                                       Math.Abs(vertexColor.B - 1f) > 0.001f ||
                                       Math.Abs(vertexColor.A - 1f) > 0.001f;
                }
                else if (exportMichonneVertexAlpha)
                {
                    GltfCommon.AddFloat(colors, 1f); GltfCommon.AddFloat(colors, 1f);
                    GltfCommon.AddFloat(colors, 1f); GltfCommon.AddFloat(colors, vertexColor.A);
                    hasVertexColors |= Math.Abs(vertexColor.A - 1f) > 0.001f;
                }
                if (exportSkinning)
                {
                    AddWeightedJoint(joints, v.Weight0, GltfCommon.RemapJoint(v.Bone0, mesh.Version, palette, boneIndexByHash));
                    AddWeightedJoint(joints, v.Weight1, GltfCommon.RemapJoint(v.Bone1, mesh.Version, palette, boneIndexByHash));
                    AddWeightedJoint(joints, v.Weight2, GltfCommon.RemapJoint(v.Bone2, mesh.Version, palette, boneIndexByHash));
                    AddWeightedJoint(joints, v.Weight3, GltfCommon.RemapJoint(v.Bone3, mesh.Version, palette, boneIndexByHash));
                    GltfCommon.AddFloat(weights, v.Weight0); GltfCommon.AddFloat(weights, v.Weight1);
                    GltfCommon.AddFloat(weights, v.Weight2); GltfCommon.AddFloat(weights, v.Weight3);
                }
                xs.Add(v.X); ys.Add(v.Y); zs.Add(v.Z);
                lineXs.Add(lineX); lineYs.Add(lineY); lineZs.Add(lineZ);
            }

            var posAccessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, positions.ToArray(), 34962, 5126, vertexCount, "VEC3",
                new[] { xs.Min(), ys.Min(), zs.Min() }, new[] { xs.Max(), ys.Max(), zs.Max() });
            var normalAccessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, normals.ToArray(), 34962, 5126, vertexCount, "VEC3", null, null);
            var tangentAccessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, tangents, 34962, 5126, vertexCount, "VEC4", null, null);
            var originalTangentWAccessor = originalTangentWs is not null
                ? GltfCommon.AddAccessor(bin, bufferViews, accessors, originalTangentWs, 34962, 5126, vertexCount, "VEC4", null, null)
                : (int?)null;
            var binormalAccessor = hasStoredBinormals
                ? GltfCommon.AddAccessor(bin, bufferViews, accessors, binormals.ToArray(), 34962, 5126, vertexCount, "VEC4", null, null)
                : (int?)null;
            var unknown1Accessor = hasStoredUnknown1
                ? GltfCommon.AddAccessor(bin, bufferViews, accessors, unknown1s.ToArray(), 34962, 5126, vertexCount, "VEC4", null, null)
                : (int?)null;
            var uvAccessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, uvs.ToArray(), 34962, 5126, vertexCount, "VEC2", null, null);
            var uv2Accessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, uv2s.ToArray(), 34962, 5126, vertexCount, "VEC2", null, null);
            var uv3Accessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, uv3s.ToArray(), 34962, 5126, vertexCount, "VEC2", null, null);
            var uv4Accessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, uv4s.ToArray(), 34962, 5126, vertexCount, "VEC2", null, null);
            var uv5Accessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, uv5s.ToArray(), 34962, 5126, vertexCount, "VEC2", null, null);
            var uv6Accessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, uv6s.ToArray(), 34962, 5126, vertexCount, "VEC2", null, null);
            var colorAccessor = hasVertexColors
                ? GltfCommon.AddAccessor(bin, bufferViews, accessors, colors.ToArray(), 34962, 5126, vertexCount, "VEC4", null, null)
                : (int?)null;
            var jointAccessor = exportSkinning
                ? GltfCommon.AddAccessor(bin, bufferViews, accessors, joints.ToArray(), 34962, 5123, vertexCount, "VEC4", null, null)
                : (int?)null;
            var weightAccessor = exportSkinning
                ? GltfCommon.AddAccessor(bin, bufferViews, accessors, weights.ToArray(), 34962, 5126, vertexCount, "VEC4", null, null)
                : (int?)null;
            int? linePosAccessor = null;

            var indexBytes = new List<byte>();
            var allIndices = new List<int>();
            foreach (var (a, b, c) in submesh.Faces)
            {
                GltfCommon.AddUInt16(indexBytes, (ushort)a); GltfCommon.AddUInt16(indexBytes, (ushort)b); GltfCommon.AddUInt16(indexBytes, (ushort)c);
                allIndices.Add(a); allIndices.Add(b); allIndices.Add(c);
            }
            var indexAccessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, indexBytes.ToArray(), 34963, 5123, allIndices.Count, "SCALAR",
                new[] { allIndices.Min() }, new[] { allIndices.Max() });

            var materialName = submesh.MaterialName ?? submesh.Name;
            var materialKey = BuildMaterialKey(submesh, materialName);
            if (exportMichonneVertexAlpha)
            {
                materialKey += "|__twdm_vertex_alpha";
            }
            if (!materialMap.TryGetValue(materialKey, out var materialIndex))
            {
                materialIndex = materials.Count;
                materialMap[materialKey] = materialIndex;
                var pbr = new Dictionary<string, object>
                {
                    ["baseColorFactor"] = new[] { 1f, 1f, 1f, 1f },
                    ["metallicFactor"] = 0.0f,
                    ["roughnessFactor"] = 1.0f,
                };
                var diffuseName = submesh.TextureNames.TryGetValue("diffuse", out var dn) ? dn : materialName;
                var materialDict = new Dictionary<string, object>
                {
                    ["name"] = materialName,
                    ["pbrMetallicRoughness"] = pbr,
                    ["doubleSided"] = true,
                };
                var renderDiffuseName = diffuseName;
                if (IsDarkAlphaMaterial(materialName, diffuseName))
                {
                    renderDiffuseName = GetOrCreateDarkAlphaTexture(
                        diffuseName,
                        pngByName,
                        images,
                        textures,
                        imageIndexByName,
                        textureIndexByName);
                }
                if (textureIndexByName.TryGetValue(renderDiffuseName, out var texIndex))
                {
                    pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = texIndex };
                    var isBttfBlendAlpha = IsBackToTheFutureBlendAlphaMaterial(submesh, materialName, diffuseName);
                    if ((lowAlphaByName.TryGetValue(diffuseName, out var low) && low) || isBttfBlendAlpha)
                    {
                        if (isBttfBlendAlpha)
                        {
                            materialDict["alphaMode"] = "BLEND";
                        }
                        else
                        {
                            materialDict["alphaMode"] = "MASK";
                            materialDict["alphaCutoff"] = 0.5f;
                        }
                        materialDict["doubleSided"] = true;
                    }
                }
                if (exportMichonneVertexAlpha)
                {
                    materialDict["alphaMode"] = "BLEND";
                    materialDict["doubleSided"] = true;
                }
                if (TryGetSlotTexture(submesh.TextureNames, textureIndexByName, "bump", out var normalTextureName, out var normalTexIndex))
                {
                    if (!ShouldSkipGltfNormalTexture(normalTextureName, materialName))
                    {
                        var gltfNormalTexIndex = GetOrCreateGltfNormalTexture(
                            normalTextureName,
                            normalTexIndex,
                            pngByName,
                            images,
                            textures,
                            imageIndexByName,
                            textureIndexByName);
                        materialDict["normalTexture"] = new Dictionary<string, object>
                        {
                            ["index"] = gltfNormalTexIndex,
                            ["texCoord"] = TexCoordForSlot("bump"),
                        };
                    }
                }
                if (TryGetSlotTexture(submesh.TextureNames, textureIndexByName, "occlusion", out _, out var occlusionTexIndex))
                {
                    materialDict["occlusionTexture"] = new Dictionary<string, object>
                    {
                        ["index"] = occlusionTexIndex,
                        ["texCoord"] = TexCoordForSlot("occlusion"),
                    };
                }
                else if (TryGetLightmapSlotTexture(submesh.TextureNames, textureIndexByName, out _, out var bakeTexIndex))
                {
                    // glTF has no official Telltale-style baked lightmap slot. Bind the bake as the
                    // closest standard material texture so GLB viewers/editors carry it visibly, while
                    // extras.telltaleTextures below still preserves the original "bake" slot for reimport.
                    materialDict["occlusionTexture"] = new Dictionary<string, object>
                    {
                        ["index"] = bakeTexIndex,
                        ["texCoord"] = TexCoordForSlot("bake"),
                    };
                }
                var telltaleTextures = BuildTelltaleTextureExtras(submesh.TextureNames, textureIndexByName);
                if (telltaleTextures.Count > 0)
                {
                    materialDict["extras"] = new Dictionary<string, object>
                    {
                        ["telltaleTextures"] = telltaleTextures,
                    };
                }
                materials.Add(materialDict);
            }

            var attributes = new Dictionary<string, object>
            {
                ["POSITION"] = posAccessor,
                ["NORMAL"] = normalAccessor,
                ["TANGENT"] = tangentAccessor,
                ["TEXCOORD_0"] = uvAccessor,
                ["TEXCOORD_1"] = uv2Accessor,
                ["TEXCOORD_2"] = uv3Accessor,
                ["TEXCOORD_3"] = uv4Accessor,
                ["TEXCOORD_4"] = uv5Accessor,
                ["TEXCOORD_5"] = uv6Accessor,
            };
            if (jointAccessor is not null && weightAccessor is not null)
            {
                attributes["JOINTS_0"] = jointAccessor.Value;
                attributes["WEIGHTS_0"] = weightAccessor.Value;
            }
            if (binormalAccessor is not null)
            {
                attributes["_TT_BINORMAL"] = binormalAccessor.Value;
            }
            if (unknown1Accessor is not null)
            {
                attributes["_TT_UNKNOWN1"] = unknown1Accessor.Value;
            }
            if (originalTangentWAccessor is not null)
            {
                attributes["_TT_TANGENT_W"] = originalTangentWAccessor.Value;
            }
            if (colorAccessor is not null)
            {
                attributes["COLOR_0"] = colorAccessor.Value;
            }

            int? lineOverlayMaterialIndex = null;
            var lineOverlaySlot = "";
            if (TryGetDetailOverlayTexture(submesh.TextureNames, textureIndexByName, out lineOverlaySlot, out var lineTextureName, out var lineTextureIndex))
            {
                var lineTexCoord = TexCoordForSlot(lineOverlaySlot);
                var lineOverlayTexture = GetOrCreateLineOverlayTexture(
                    lineTextureName,
                    lineTextureIndex,
                    pngByName,
                    images,
                    textures,
                    imageIndexByName,
                    textureIndexByName,
                    lineOverlayAlphaModeByName);
                var lineMaterialKey = materialKey + "|__telltale_lines_overlay";
                if (!materialMap.TryGetValue(lineMaterialKey, out var existingLineMaterialIndex))
                {
                    existingLineMaterialIndex = materials.Count;
                    materialMap[lineMaterialKey] = existingLineMaterialIndex;
                    materials.Add(BuildLineOverlayMaterial(materialName, lineOverlaySlot, lineTextureName, lineOverlayTexture.TextureIndex, lineTextureIndex, lineTexCoord, lineOverlayTexture.AlphaMode));
                }
                usesLineOverlayMaterials = true;
                lineOverlayMaterialIndex = existingLineMaterialIndex;
            }

            var submeshExtras = new Dictionary<string, object>
            {
                ["name"] = submesh.Name,
                ["materialName"] = submesh.MaterialName ?? "",
                ["bonePaletteIndex"] = submesh.BonePaletteIndex,
            };
            if (!string.IsNullOrWhiteSpace(submesh.SourceMeshPath))
            {
                submeshExtras["sourceMesh"] = submesh.SourceMeshPath;
                submeshExtras["sourceSubmeshIndex"] = submesh.SourceSubmeshIndex;
            }
            if (submesh.TextureNames.Count > 0)
            {
                submeshExtras["textureSlots"] = submesh.TextureNames
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            primitives.Add(new Dictionary<string, object>
            {
                ["attributes"] = attributes,
                ["indices"] = indexAccessor,
                ["material"] = materialIndex,
                ["mode"] = 4,
                ["extras"] = new Dictionary<string, object>
                {
                    ["telltaleSubmesh"] = submeshExtras,
                },
            });
            if (lineOverlayMaterialIndex is not null)
            {
                linePosAccessor ??= GltfCommon.AddAccessor(bin, bufferViews, accessors, linePositions.ToArray(), 34962, 5126, vertexCount, "VEC3",
                    new[] { lineXs.Min(), lineYs.Min(), lineZs.Min() }, new[] { lineXs.Max(), lineYs.Max(), lineZs.Max() });
                var lineAttributes = BuildLineOverlayAttributes(attributes, linePosAccessor.Value, uvAccessor, uv2Accessor, uv3Accessor, uv4Accessor, TexCoordForSlot(lineOverlaySlot));
                primitives.Add(new Dictionary<string, object>
                {
                    ["attributes"] = lineAttributes,
                    ["indices"] = indexAccessor,
                    ["material"] = lineOverlayMaterialIndex.Value,
                    ["mode"] = 4,
                    ["extras"] = new Dictionary<string, object>
                    {
                        ["telltaleLineOverlayFor"] = submeshExtras,
                    },
                });
            }
        }

        // Nodes: 0 = mesh node. Then one node per bone when a skeleton is present.
        var nodes = new List<Dictionary<string, object>>();
        var meshNode = new Dictionary<string, object> { ["mesh"] = 0, ["name"] = mesh.Name };
        nodes.Add(meshNode);
        var rootNodes = new List<int> { 0 };

        Dictionary<string, object>? skin = null;
        if (skeleton is not null && skeleton.Bones.Count > 0)
        {
            var boneNodeBase = nodes.Count;
            var childrenByBone = new List<List<int>>();
            for (var i = 0; i < skeleton.Bones.Count; i++) childrenByBone.Add([]);
            for (var i = 0; i < skeleton.Bones.Count; i++)
            {
                var p = skeleton.Bones[i].ParentIndex;
                if (p >= 0 && p < skeleton.Bones.Count) childrenByBone[p].Add(boneNodeBase + i);
            }

            var world = BuildBoneWorldMatrices(skeleton);

            var ibmBytes = new List<byte>();
            for (var i = 0; i < skeleton.Bones.Count; i++)
            {
                Matrix4x4.Invert(world[i], out var inv);
                inv = NormalizeInverseBindMatrix(inv);
                foreach (var f in GltfCommon.MatrixColumnMajor(inv)) GltfCommon.AddFloat(ibmBytes, f);
            }
            var ibmAccessor = GltfCommon.AddAccessor(bin, bufferViews, accessors, ibmBytes.ToArray(), 0, 5126, skeleton.Bones.Count, "MAT4", null, null);

            var joints = new List<int>();
            for (var i = 0; i < skeleton.Bones.Count; i++)
            {
                var b = skeleton.Bones[i];
                var extras = new Dictionary<string, object>
                {
                    ["hash"] = $"0x{b.Hash:X16}",
                    ["parentHash"] = $"0x{b.ParentHash:X16}",
                    ["translationBits"] = GltfCommon.FloatBits(b.X, b.Y, b.Z),
                    ["rotationBits"] = GltfCommon.FloatBits(b.Qx, b.Qy, b.Qz, b.Qw),
                };
                if (b.HasRichSkeletonData)
                {
                    extras["telltaleSkeleton"] = BuildRichSkeletonExtras(b);
                }

                var node = new Dictionary<string, object>
                {
                    ["name"] = b.Name,
                    ["translation"] = new[] { b.X, b.Y, b.Z },
                    ["rotation"] = new[] { b.Qx, b.Qy, b.Qz, b.Qw },
                    ["extras"] = extras,
                };
                if (childrenByBone[i].Count > 0) node["children"] = childrenByBone[i];
                nodes.Add(node);
                joints.Add(boneNodeBase + i);
                if (b.ParentIndex < 0 || b.ParentIndex >= skeleton.Bones.Count) rootNodes.Add(boneNodeBase + i);
            }

            skin = new Dictionary<string, object> { ["joints"] = joints, ["inverseBindMatrices"] = ibmAccessor };
            meshNode["skin"] = 0;
        }

        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object>
            {
                ["version"] = "2.0",
                ["generator"] = "TelltaleD3DMeshEditor",
                ["extras"] = new Dictionary<string, object>
                {
                    ["telltale"] = new Dictionary<string, object>
                    {
                        ["kind"] = "asset",
                        ["meshVersion"] = mesh.Version,
                        ["hasSkeleton"] = skeleton is not null,
                    },
                },
            },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = rootNodes } },
            ["nodes"] = nodes,
            ["meshes"] = new[] { new Dictionary<string, object> { ["name"] = mesh.Name, ["primitives"] = primitives } },
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
        };
        if (textures.Count > 0)
        {
            gltf["textures"] = textures;
            gltf["samplers"] = new[] { BuildSampler(), BuildLineOverlaySampler() };
        }
        if (usesLineOverlayMaterials)
        {
            gltf["extensionsUsed"] = new[] { "KHR_materials_unlit" };
        }
        if (skin is not null) gltf["skins"] = new[] { skin };

        return new BuiltAsset { Gltf = gltf, Bin = bin, Images = images };
    }

    private static Dictionary<string, object> BuildRichSkeletonExtras(BoneData bone)
        => new()
        {
            ["mirrorHash"] = $"0x{bone.MirrorBoneHash:X16}",
            ["mirrorBoneIndex"] = bone.MirrorBoneIndex,
            ["boneLength"] = bone.BoneLength,
            ["boneDir"] = new[] { bone.BoneDir.X, bone.BoneDir.Y, bone.BoneDir.Z },
            ["boneRotationAdjustment"] = new[]
            {
                bone.BoneRotationAdjustment.X,
                bone.BoneRotationAdjustment.Y,
                bone.BoneRotationAdjustment.Z,
                bone.BoneRotationAdjustment.W,
            },
            ["restTranslation"] = new[] { bone.RestTranslation.X, bone.RestTranslation.Y, bone.RestTranslation.Z },
            ["restRotation"] = new[] { bone.RestRotation.X, bone.RestRotation.Y, bone.RestRotation.Z, bone.RestRotation.W },
            ["globalTranslationScale"] = new[] { bone.GlobalTranslationScale.X, bone.GlobalTranslationScale.Y, bone.GlobalTranslationScale.Z },
            ["localTranslationScale"] = new[] { bone.LocalTranslationScale.X, bone.LocalTranslationScale.Y, bone.LocalTranslationScale.Z },
            ["animTranslationScale"] = new[] { bone.AnimTranslationScale.X, bone.AnimTranslationScale.Y, bone.AnimTranslationScale.Z },
        };

    private static Matrix4x4[] BuildBoneWorldMatrices(SkeletonData skeleton)
    {
        var world = new Matrix4x4[skeleton.Bones.Count];
        var state = new byte[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            BuildBoneWorldMatrix(skeleton, i, world, state);
        }

        return world;
    }

    private static Matrix4x4 BuildBoneWorldMatrix(SkeletonData skeleton, int index, Matrix4x4[] world, byte[] state)
    {
        if (state[index] == 2)
        {
            return world[index];
        }

        if (state[index] == 1)
        {
            return Matrix4x4.Identity;
        }

        state[index] = 1;
        var bone = skeleton.Bones[index];
        var rotation = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw);
        rotation = rotation.LengthSquared() > 0.000001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        var local = Matrix4x4.CreateFromQuaternion(rotation) *
                    Matrix4x4.CreateTranslation(bone.X, bone.Y, bone.Z);
        world[index] = bone.ParentIndex >= 0 && bone.ParentIndex < skeleton.Bones.Count
            ? local * BuildBoneWorldMatrix(skeleton, bone.ParentIndex, world, state)
            : local;
        state[index] = 2;
        return world[index];
    }

    private static Dictionary<string, object> BuildSampler()
    {
        var sampler = new Dictionary<string, object>
        {
            ["wrapS"] = 10497,
            ["wrapT"] = 10497,
        };
        if (GameConfig.Current.PixelatedGltfTextures)
        {
            sampler["magFilter"] = 9728;
            sampler["minFilter"] = 9728;
        }

        return sampler;
    }

    private static Dictionary<string, object> BuildLineOverlaySampler()
        => new()
        {
            // Avoid mipmap averaging on thin ink/detail alpha masks; otherwise lines fade out
            // aggressively in external glTF viewers when the camera moves away.
            ["magFilter"] = 9729,
            ["minFilter"] = 9729,
            ["wrapS"] = 10497,
            ["wrapT"] = 10497,
        };

    private static (float R, float G, float B, float A) NormalizeVertexColor(VertexData vertex)
    {
        var r = vertex.ColorR;
        var g = vertex.ColorG;
        var b = vertex.ColorB;
        if (r + g + b <= 0.001f)
        {
            r = 1f;
            g = 1f;
            b = 1f;
        }

        return (r, g, b, vertex.ColorA);
    }

    private static bool HasStoredBinormal(VertexData vertex)
    {
        return Math.Abs(vertex.BinormalX) > 0.000001f ||
               Math.Abs(vertex.BinormalY) > 0.000001f ||
               Math.Abs(vertex.BinormalZ) > 0.000001f ||
               Math.Abs(vertex.BinormalW) > 0.000001f;
    }

    private static bool HasStoredTangent(VertexData vertex)
    {
        return Math.Abs(vertex.TangentX) > 0.000001f ||
               Math.Abs(vertex.TangentY) > 0.000001f ||
               Math.Abs(vertex.TangentZ) > 0.000001f ||
               Math.Abs(vertex.TangentW) > 0.000001f;
    }

    private static void AddWeightedJoint(List<byte> joints, float weight, int joint)
    {
        // glTF validators require joints with zero weight to be zero as well. Telltale sometimes
        // leaves placeholder joint indices in unused influence slots; reimport ignores them.
        GltfCommon.AddUInt16(joints, weight > 0.000001f ? (ushort)joint : (ushort)0);
    }

    private static Matrix4x4 NormalizeInverseBindMatrix(Matrix4x4 matrix)
    {
        matrix.M14 = 0f;
        matrix.M24 = 0f;
        matrix.M34 = 0f;
        matrix.M44 = 1f;
        return matrix;
    }

    private static string BuildMaterialKey(SubmeshData submesh, string materialName)
    {
        var slots = submesh.TextureNames
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}");
        return materialName + "|" + string.Join("|", slots);
    }

    private static bool IsBackToTheFutureBlendAlphaMaterial(SubmeshData submesh, string materialName, string diffuseName)
    {
        if (!GameConfig.Current.IsBackToTheFuture)
        {
            return false;
        }

        var names = submesh.TextureNames.Values
            .Append(materialName)
            .Append(diffuseName)
            .Append(submesh.Name)
            .Select(NormalizeTextureStem)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        return names.Any(IsBackToTheFutureGlassName);
    }

    private static bool IsBackToTheFutureGlassName(string name)
    {
        if (name.Contains("glass", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Contains("window", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("windowframe", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("windowtrim", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("window_frame", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("window_trim", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTextureStem(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value);
        while (stem.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }

        return stem;
    }

    private static byte[] BuildTangents(SubmeshData submesh, out byte[]? originalTangentWs)
    {
        originalTangentWs = null;
        if (submesh.Vertices.Any(HasStoredTangent))
        {
            var stored = new List<byte>(submesh.Vertices.Count * 16);
            var originalW = new List<byte>();
            var hasNonGltfW = false;
            foreach (var vertex in submesh.Vertices)
            {
                GltfCommon.AddFloat(stored, vertex.TangentX);
                GltfCommon.AddFloat(stored, vertex.TangentY);
                GltfCommon.AddFloat(stored, vertex.TangentZ);
                GltfCommon.AddFloat(stored, ToGltfTangentSign(vertex.TangentW));

                if (!IsGltfTangentSign(vertex.TangentW))
                {
                    hasNonGltfW = true;
                }

                // Keep this VEC4, not SCALAR. Blender's importer can misalign custom attributes
                // of different widths on a multi-primitive mesh; all _TT_* attributes stay VEC4.
                GltfCommon.AddFloat(originalW, vertex.TangentW);
                GltfCommon.AddFloat(originalW, 0f);
                GltfCommon.AddFloat(originalW, 0f);
                GltfCommon.AddFloat(originalW, 0f);
            }

            originalTangentWs = hasNonGltfW ? originalW.ToArray() : null;
            return stored.ToArray();
        }

        var tangentAccum = new Vector3[submesh.Vertices.Count];
        var bitangentAccum = new Vector3[submesh.Vertices.Count];

        foreach (var (a, b, c) in submesh.Faces)
        {
            if (a < 0 || b < 0 || c < 0 ||
                a >= submesh.Vertices.Count || b >= submesh.Vertices.Count || c >= submesh.Vertices.Count)
            {
                continue;
            }

            var v0 = submesh.Vertices[a];
            var v1 = submesh.Vertices[b];
            var v2 = submesh.Vertices[c];
            var p0 = new Vector3(v0.X, v0.Y, v0.Z);
            var p1 = new Vector3(v1.X, v1.Y, v1.Z);
            var p2 = new Vector3(v2.X, v2.Y, v2.Z);
            var uv0 = new Vector2(v0.U, 1f - v0.V);
            var uv1 = new Vector2(v1.U, 1f - v1.V);
            var uv2 = new Vector2(v2.U, 1f - v2.V);

            var edge1 = p1 - p0;
            var edge2 = p2 - p0;
            var duv1 = uv1 - uv0;
            var duv2 = uv2 - uv0;
            var determinant = duv1.X * duv2.Y - duv2.X * duv1.Y;
            if (MathF.Abs(determinant) < 0.000001f)
            {
                continue;
            }

            var r = 1f / determinant;
            var tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * r;
            var bitangent = (edge2 * duv1.X - edge1 * duv2.X) * r;
            tangentAccum[a] += tangent;
            tangentAccum[b] += tangent;
            tangentAccum[c] += tangent;
            bitangentAccum[a] += bitangent;
            bitangentAccum[b] += bitangent;
            bitangentAccum[c] += bitangent;
        }

        var bytes = new List<byte>(submesh.Vertices.Count * 16);
        for (var i = 0; i < submesh.Vertices.Count; i++)
        {
            var vertex = submesh.Vertices[i];
            var normal = SafeNormalize(new Vector3(vertex.Nx, vertex.Ny, vertex.Nz), Vector3.UnitZ);
            var tangent = tangentAccum[i] - normal * Vector3.Dot(normal, tangentAccum[i]);
            tangent = SafeNormalize(tangent, BuildFallbackTangent(normal));
            var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangentAccum[i]) < 0f ? -1f : 1f;

            GltfCommon.AddFloat(bytes, tangent.X);
            GltfCommon.AddFloat(bytes, tangent.Y);
            GltfCommon.AddFloat(bytes, tangent.Z);
            GltfCommon.AddFloat(bytes, handedness);
        }

        return bytes.ToArray();
    }

    private static bool IsGltfTangentSign(float value)
        => Math.Abs(value - 1f) <= 0.000001f || Math.Abs(value + 1f) <= 0.000001f;

    private static float ToGltfTangentSign(float value)
        => value < 0f ? -1f : 1f;

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared() > 0.00000001f ? Vector3.Normalize(value) : fallback;
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal)
    {
        var axis = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        return SafeNormalize(Vector3.Cross(axis, normal), Vector3.UnitX);
    }

    private static Dictionary<string, object> BuildLineOverlayAttributes(
        IReadOnlyDictionary<string, object> baseAttributes,
        int positionAccessor,
        int uvAccessor,
        int uv2Accessor,
        int uv3Accessor,
        int uv4Accessor,
        int sourceTexCoord)
    {
        var result = new Dictionary<string, object>();
        result["POSITION"] = positionAccessor;
        foreach (var key in new[] { "NORMAL", "TANGENT", "JOINTS_0", "WEIGHTS_0" })
        {
            if (baseAttributes.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }

        result["TEXCOORD_0"] = sourceTexCoord switch
        {
            1 => uv2Accessor,
            2 => uv3Accessor,
            3 => uv4Accessor,
            _ => uvAccessor,
        };
        return result;
    }

    private static Dictionary<string, object> BuildTelltaleTextureExtras(
        IReadOnlyDictionary<string, string> textureNames,
        IReadOnlyDictionary<string, int> textureIndexByName)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, name) in textureNames.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var entry = new Dictionary<string, object>
            {
                ["name"] = name,
                ["texCoord"] = TexCoordForSlot(slot),
            };
            if (textureIndexByName.TryGetValue(name, out var textureIndex))
            {
                entry["textureIndex"] = textureIndex;
            }
            result[slot] = entry;
        }

        return result;
    }

    private static bool TryGetSlotTexture(
        IReadOnlyDictionary<string, string> textureNames,
        IReadOnlyDictionary<string, int> textureIndexByName,
        string slot,
        out string textureName,
        out int textureIndex)
    {
        textureName = "";
        textureIndex = -1;
        if (!textureNames.TryGetValue(slot, out var foundTextureName) ||
            string.IsNullOrWhiteSpace(foundTextureName))
        {
            return false;
        }

        textureName = foundTextureName;
        return
               textureIndexByName.TryGetValue(textureName, out textureIndex);
    }

    // Toolkit and imported assets occasionally call the same Telltale bake channel "lightmap"
    // or "lighting_map". Export it through the normal glTF carrier regardless of that alias.
    private static bool TryGetLightmapSlotTexture(
        IReadOnlyDictionary<string, string> textureNames,
        IReadOnlyDictionary<string, int> textureIndexByName,
        out string textureName,
        out int textureIndex)
    {
        foreach (var slot in LightmapSlots)
        {
            if (TryGetSlotTexture(textureNames, textureIndexByName, slot, out textureName, out textureIndex))
            {
                return true;
            }
        }

        textureName = "";
        textureIndex = -1;
        return false;
    }

    private static bool TryGetDetailOverlayTexture(
        IReadOnlyDictionary<string, string> textureNames,
        IReadOnlyDictionary<string, int> textureIndexByName,
        out string slot,
        out string textureName,
        out int textureIndex)
    {
        foreach (var candidateSlot in new[] { "detail_diffuse", "tex7", "tex8" })
        {
            if (TryGetSlotTexture(textureNames, textureIndexByName, candidateSlot, out textureName, out textureIndex) &&
                IsDetailOverlayTextureName(textureName))
            {
                slot = candidateSlot;
                return true;
            }
        }

        slot = "";
        textureName = "";
        textureIndex = -1;
        return false;
    }

    private static bool IsDetailOverlayTextureName(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_lines", StringComparison.Ordinal) ||
               lower.EndsWith("_line", StringComparison.Ordinal) ||
               lower.Contains("_lines_", StringComparison.Ordinal) ||
               lower.Contains("_line_", StringComparison.Ordinal) ||
               lower.Contains("inklines", StringComparison.Ordinal) ||
               lower.Contains("ink_lines", StringComparison.Ordinal) ||
               lower.EndsWith("_detail", StringComparison.Ordinal) ||
               lower.Contains("_detail_", StringComparison.Ordinal);
    }

    private static Dictionary<string, object> BuildLineOverlayMaterial(
        string materialName,
        string slot,
        string lineTextureName,
        int overlayTextureIndex,
        int sourceTextureIndex,
        int texCoord,
        string overlayAlphaMode)
    {
        return new Dictionary<string, object>
        {
            ["name"] = materialName + "__tt_lines_" + lineTextureName,
            ["pbrMetallicRoughness"] = new Dictionary<string, object>
            {
                ["baseColorFactor"] = new[] { 1f, 1f, 1f, 1f },
                ["metallicFactor"] = 0.0f,
                ["roughnessFactor"] = 1.0f,
                ["baseColorTexture"] = new Dictionary<string, object>
                {
                    ["index"] = overlayTextureIndex,
                },
            },
            ["alphaMode"] = "BLEND",
            ["doubleSided"] = true,
            ["extensions"] = new Dictionary<string, object>
            {
                ["KHR_materials_unlit"] = new Dictionary<string, object>(),
            },
            ["extras"] = new Dictionary<string, object>
            {
                ["telltaleLineOverlay"] = new Dictionary<string, object>
                {
                    ["slot"] = slot,
                    ["name"] = lineTextureName,
                    ["texCoord"] = texCoord,
                    ["textureIndex"] = sourceTextureIndex,
                    ["overlayTextureIndex"] = overlayTextureIndex,
                    ["overlayTexCoord"] = 0,
                    ["overlayAlpha"] = overlayAlphaMode,
                    ["overlayNormalOffset"] = LineOverlayNormalOffset,
                },
            },
        };
    }

    private static (int TextureIndex, string AlphaMode) GetOrCreateLineOverlayTexture(
        string lineTextureName,
        int fallbackTextureIndex,
        IReadOnlyDictionary<string, byte[]> pngByName,
        List<(string Name, byte[] Png)> images,
        List<Dictionary<string, object>> textures,
        Dictionary<string, int> imageIndexByName,
        Dictionary<string, int> textureIndexByName,
        Dictionary<string, string> lineOverlayAlphaModeByName)
    {
        var overlayName = lineTextureName + "__tt_lines_overlay";
        if (textureIndexByName.TryGetValue(overlayName, out var existingTextureIndex))
        {
            return (existingTextureIndex, lineOverlayAlphaModeByName.TryGetValue(overlayName, out var existingAlphaMode)
                ? existingAlphaMode
                : "auto");
        }
        if (!pngByName.TryGetValue(lineTextureName, out var sourcePng))
        {
            return (fallbackTextureIndex, "sourceAlpha");
        }

        var (overlayPng, alphaMode) = BuildLineOverlayPng(sourcePng);
        var imageIndex = images.Count;
        images.Add((overlayName, overlayPng));
        imageIndexByName[overlayName] = imageIndex;

        var textureIndex = textures.Count;
        textures.Add(new Dictionary<string, object> { ["source"] = imageIndex, ["sampler"] = 1 });
        textureIndexByName[overlayName] = textureIndex;
        lineOverlayAlphaModeByName[overlayName] = alphaMode;
        return (textureIndex, alphaMode);
    }

    private static (byte[] Png, string AlphaMode) BuildLineOverlayPng(byte[] sourcePng)
    {
        using var input = new MemoryStream(sourcePng);
        using var source = new Bitmap(input);
        using var overlay = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var invertAlpha = AverageAlpha(source) > 160f;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var alpha = invertAlpha
                    ? 255 - pixel.A
                    : ApplySourceAlphaLineOverlayStrength(pixel.A);
                overlay.SetPixel(x, y, Color.FromArgb(alpha, pixel.R, pixel.G, pixel.B));
            }
        }

        using var output = new MemoryStream();
        overlay.Save(output, ImageFormat.Png);
        return (output.ToArray(), invertAlpha ? "invertedSourceAlpha" : "sourceAlpha");
    }

    private static int ApplySourceAlphaLineOverlayStrength(int alpha)
    {
        if (alpha <= 0)
        {
            return 0;
        }
        return Math.Clamp((int)MathF.Round(alpha * SourceAlphaLineOverlayStrength), 0, 255);
    }

    private static float AverageAlpha(Bitmap bitmap)
    {
        var total = bitmap.Width * bitmap.Height;
        if (total == 0)
        {
            return 0f;
        }

        var step = Math.Max(1, total / Math.Min(20000, total));
        var samples = 0;
        var sum = 0L;
        for (var idx = 0; idx < total; idx += step)
        {
            var x = idx % bitmap.Width;
            var y = idx / bitmap.Width;
            sum += bitmap.GetPixel(x, y).A;
            samples++;
        }

        return sum / (float)Math.Max(1, samples);
    }

    private static int GetOrCreateGltfNormalTexture(
        string normalTextureName,
        int fallbackTextureIndex,
        IReadOnlyDictionary<string, byte[]> pngByName,
        List<(string Name, byte[] Png)> images,
        List<Dictionary<string, object>> textures,
        Dictionary<string, int> imageIndexByName,
        Dictionary<string, int> textureIndexByName)
    {
        var gltfName = normalTextureName + "__tt_gltf_normal";
        if (textureIndexByName.TryGetValue(gltfName, out var existingTextureIndex))
        {
            return existingTextureIndex;
        }
        if (!pngByName.TryGetValue(normalTextureName, out var sourcePng))
        {
            return fallbackTextureIndex;
        }

        var normalPng = BuildGltfNormalPng(sourcePng);
        var imageIndex = images.Count;
        images.Add((gltfName, normalPng));
        imageIndexByName[gltfName] = imageIndex;

        var textureIndex = textures.Count;
        textures.Add(new Dictionary<string, object> { ["source"] = imageIndex, ["sampler"] = 0 });
        textureIndexByName[gltfName] = textureIndex;
        return textureIndex;
    }

    private static byte[] BuildGltfNormalPng(byte[] sourcePng)
    {
        using var input = new MemoryStream(sourcePng);
        using var source = new Bitmap(input);
        using var normal = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var swizzled = IsLikelyTelltaleSwizzledNormal(source);

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var nxByte = swizzled ? pixel.A : pixel.R;
                var nyByte = swizzled ? pixel.B : pixel.G;
                var nx = nxByte / 127.5f - 1f;
                // Telltale and Direct3D use the opposite Y direction from glTF/OpenGL normal maps.
                var ny = -(nyByte / 127.5f - 1f);
                var nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));

                normal.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        255,
                        ToNormalByte(nx),
                        ToNormalByte(ny),
                        ToNormalByte(nz)));
            }
        }

        using var output = new MemoryStream();
        normal.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static bool IsLikelyTelltaleSwizzledNormal(Bitmap bitmap)
    {
        var total = bitmap.Width * bitmap.Height;
        var step = Math.Max(1, total / Math.Min(10000, total));
        var samples = 0;
        var sumR = 0L;
        var sumG = 0L;
        var sumB = 0L;
        var sumA = 0L;

        for (var idx = 0; idx < total; idx += step)
        {
            var x = idx % bitmap.Width;
            var y = idx / bitmap.Width;
            var pixel = bitmap.GetPixel(x, y);
            sumR += pixel.R;
            sumG += pixel.G;
            sumB += pixel.B;
            sumA += pixel.A;
            samples++;
        }

        if (samples == 0)
        {
            return false;
        }

        var avgR = sumR / (float)samples;
        var avgG = sumG / (float)samples;
        var avgB = sumB / (float)samples;
        var avgA = sumA / (float)samples;
        return avgR > 240f &&
               avgG > 220f &&
               avgB is > 70f and < 185f &&
               avgA is > 70f and < 185f;
    }

    private static int ToNormalByte(float value)
    {
        return Math.Clamp((int)MathF.Round((value * 0.5f + 0.5f) * 255f), 0, 255);
    }

    private static bool ShouldSkipGltfNormalTexture(string normalTextureName, string materialName)
    {
        // Telltale normal maps are not standard glTF tangent-space normals — the packing/convention
        // (and per-material swizzle) differs and isn't always detectable, so binding them as a glTF
        // normalTexture gives some parts an artificial bright/metallic look in glTF viewers (Blender).
        // The official 3ds Max importer (RandomTBush) doesn't apply them either — it uses diffuse+alpha
        // only. So we don't bind them; the original normal is still preserved in extras.telltaleTextures
        // for anyone who wants to wire it up manually.
        _ = normalTextureName;
        _ = materialName;
        return true;
    }

    private static bool IsDarkAlphaMaterial(string materialName, string diffuseName)
    {
        return materialName.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
               materialName.Contains("eyelashes", StringComparison.OrdinalIgnoreCase) ||
               materialName.Contains("lashes", StringComparison.OrdinalIgnoreCase) ||
               diffuseName.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
               diffuseName.Contains("eyelashes", StringComparison.OrdinalIgnoreCase) ||
               diffuseName.Contains("lashes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOrCreateDarkAlphaTexture(
        string sourceTextureName,
        IReadOnlyDictionary<string, byte[]> pngByName,
        List<(string Name, byte[] Png)> images,
        List<Dictionary<string, object>> textures,
        Dictionary<string, int> imageIndexByName,
        Dictionary<string, int> textureIndexByName)
    {
        var darkName = sourceTextureName + "__tt_dark_alpha";
        if (textureIndexByName.ContainsKey(darkName))
        {
            return darkName;
        }
        if (!pngByName.TryGetValue(sourceTextureName, out var sourcePng))
        {
            return sourceTextureName;
        }

        var darkPng = BuildDarkAlphaPng(sourcePng);
        var imageIndex = images.Count;
        images.Add((darkName, darkPng));
        imageIndexByName[darkName] = imageIndex;

        var textureIndex = textures.Count;
        textures.Add(new Dictionary<string, object> { ["source"] = imageIndex, ["sampler"] = 0 });
        textureIndexByName[darkName] = textureIndex;
        return darkName;
    }

    private static byte[] BuildDarkAlphaPng(byte[] sourcePng)
    {
        using var input = new MemoryStream(sourcePng);
        using var source = new Bitmap(input);
        using var dark = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                dark.SetPixel(x, y, Color.FromArgb(pixel.A, 0, 0, 0));
            }
        }

        using var output = new MemoryStream();
        dark.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static int TexCoordForSlot(string slot)
    {
        return slot.ToLowerInvariant() switch
        {
            "bake" or "lightmap" or "light_map" or "lighting" or "lighting_map"
                => GameConfig.Current.Id == GameId.WalkingDeadMichonne ? 5 : 1,
            "detail_diffuse" => 2,
            "detail_bump" => 2,
            "shadow" => 3,
            _ => 0,
        };
    }
}
