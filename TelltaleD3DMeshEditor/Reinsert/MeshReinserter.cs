using System.Buffers.Binary;
using System.Numerics;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Geometry reimporter. Takes the template layout plus the edited GLB and returns a new .d3dmesh
// through template patching: rewrites vertex/face buffers, the submesh table
// (vMin/vMax/faceStart/poly), bounds, and uvScales, then fixes the MetaStream size.
// When the GLB has JOINTS_0/WEIGHTS_0, it remaps those joints to the template bone palettes,
// keeping the file bound to the original/reference .skl.
// If the GLB has more submeshes than the template, extra entries inherit material/textures from
// the last original submesh; if it has fewer, the table shrinks to the GLB count.
public static class MeshReinserter
{
    public static byte[] ReinsertGeometry(
        D3DMeshLayout layout,
        GltfModel model,
        IReadOnlyList<string?>? diffuseTextureNames = null,
        SkeletonData? referenceSkeleton = null,
        GameConfig? gameConfig = null)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive = null;
        if (diffuseTextureNames is not null)
        {
            textureSlotsByPrimitive = diffuseTextureNames
                .Select(name =>
                    string.IsNullOrWhiteSpace(name)
                        ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["diffuse"] = name })
                .ToList();
        }

        return ReinsertGeometry(layout, model, textureSlotsByPrimitive, referenceSkeleton, gameConfig);
    }

    public static byte[] ReinsertGeometry(
        D3DMeshLayout layout,
        GltfModel model,
        ReinsertedTextures textures,
        SkeletonData? referenceSkeleton = null,
        GameConfig? gameConfig = null)
        => ReinsertGeometry(layout, model, textures.PrimitiveSlots, referenceSkeleton, gameConfig);

    private static byte[] ReinsertGeometry(
        D3DMeshLayout layout,
        GltfModel model,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        SkeletonData? referenceSkeleton,
        GameConfig? gameConfig)
    {
        if (model.Primitives.Count == 0)
        {
            throw new InvalidOperationException("GLB has no mesh primitives to reimport.");
        }

        if (layout.Submeshes.Count == 0)
        {
            throw new InvalidOperationException("Template has no submesh table to use as a base.");
        }

        var preparedPrimitives = PreparePrimitives(layout, model, textureSlotsByPrimitive, referenceSkeleton);
        var preparedTextureSlotsByPrimitive = textureSlotsByPrimitive is null
            ? null
            : preparedPrimitives
                .Select(primitive => primitive.TextureSlots ?? EmptyTextureSlots)
                .ToList();

        var verts = new List<EncVertex>();
        var faceIndices = new List<int>(); // global 0-based, flattened (3 per triangle)
        var subInfo = new SubmeshPatchInfo[preparedPrimitives.Count];

        for (var k = 0; k < preparedPrimitives.Count; k++)
        {
            var prepared = preparedPrimitives[k];
            var prim = prepared.Primitive;
            var vertexStart = verts.Count;
            var faceStart = faceIndices.Count;

            var normals = HasVertexChannel(prim.Normals, prim.VertexCount) ? prim.Normals! : ComputeNormals(prim);
            var (tanX, tanY, tanZ, tanW) = HasVertexChannel(prim.Tangents, prim.VertexCount)
                ? UseTangents(prim.Tangents!)
                : ComputeTangents(prim, normals);
            var binormals = HasVertexChannel(prim.Binormals, prim.VertexCount)
                ? prim.Binormals!
                : ComputeBinormals(normals, tanX, tanY, tanZ, tanW);
            var skinning = BuildSkinningMap(layout, model, prim, k, referenceSkeleton, prepared.BonePaletteIndex);

            for (var i = 0; i < prim.VertexCount; i++)
            {
                var pos = prim.Positions[i];
                var nrm = normals[i];
                var uv0 = Uv(prim.Uv0, prim.VertexCount, i);
                var uv1 = HasVertexChannel(prim.Uv1, prim.VertexCount) ? prim.Uv1![i] : uv0;
                var uv2 = HasVertexChannel(prim.Uv2, prim.VertexCount) ? prim.Uv2![i] : uv1;
                var uv3 = HasVertexChannel(prim.Uv3, prim.VertexCount) ? prim.Uv3![i] : uv2;
                var col = HasVertexChannel(prim.Color0, prim.VertexCount) ? prim.Color0![i] : new Vector4(1, 1, 1, 1);
                var unknown1 = HasVertexChannel(prim.Unknown1, prim.VertexCount) ? prim.Unknown1![i] : 0f;
                var binormal = binormals[i];
                var skin = ReadSkinning(prim, i, skinning);

                verts.Add(new EncVertex
                {
                    X = pos.X, Y = pos.Y, Z = pos.Z,
                    Nx = nrm.X, Ny = nrm.Y, Nz = nrm.Z,
                    Tx = tanX[i], Ty = tanY[i], Tz = tanZ[i], Tw = tanW[i],
                    Bx = binormal.X, By = binormal.Y, Bz = binormal.Z, Bw = binormal.W,
                    Unknown1 = unknown1,
                    U0 = uv0.X, V0 = uv0.Y,
                    U1 = uv1.X, V1 = uv1.Y,
                    U2 = uv2.X, V2 = uv2.Y,
                    U3 = uv3.X, V3 = uv3.Y,
                    ColR = col.X, ColG = col.Y, ColB = col.Z, ColA = col.W,
                    Bone0 = skin.Bone0, Bone1 = skin.Bone1, Bone2 = skin.Bone2, Bone3 = skin.Bone3,
                    W0 = skin.Weight0, W1 = skin.Weight1, W2 = skin.Weight2, W3 = skin.Weight3,
                });
            }

            for (var i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                var a = prim.Indices[i];
                var b = prim.Indices[i + 1];
                var c = prim.Indices[i + 2];
                if (!IsValidTriangle(a, b, c, prim.VertexCount))
                {
                    continue;
                }

                faceIndices.Add(vertexStart + a);
                faceIndices.Add(vertexStart + b);
                faceIndices.Add(vertexStart + c);
            }

            subInfo[k] = new SubmeshPatchInfo(
                VMin: vertexStart,
                VMax: verts.Count - 1,
                FaceStart: faceStart,
                PolyCount: (faceIndices.Count - faceStart) / 3,
                BonePaletteIndex: prepared.BonePaletteIndex,
                TemplateSubmeshIndex: ResolveTemplateSubmeshIndex(layout, prim, k));
        }

        if (verts.Count > 65535)
        {
            throw new InvalidOperationException($"Model has {verts.Count} vertices; uint16 indices support up to 65535 (v1).");
        }

        var mults = ChooseUvMults(layout, verts);

        // Face buffer (global 0-based uint16; MCSM can store the indices big-endian).
        var faceBytes = BuildFaceBytes(layout, faceIndices);

        var patches = new List<RegionPatch>
        {
            new(layout.BoundsOffset, 24, BuildBoundsBytes(verts)),
            new(layout.SubmeshBlockSizeFieldOffset, 4, U32(layout.SubmeshBlockSize + BuildSubmeshTableSizeDelta(layout, subInfo, preparedTextureSlotsByPrimitive))),
            new(layout.SubmeshCountFieldOffset, 4, U32(subInfo.Length)),
            new(layout.SubmeshTableOffset, layout.SubmeshTableLength, BuildSubmeshTable(layout, subInfo, preparedTextureSlotsByPrimitive, gameConfig ?? GameConfig.Current)),
            new(layout.UvScalesOffset, layout.UvScalesLength, BuildUvScaleBytes(layout, mults)),
            new(layout.FaceCountFieldOffset, 4, U32(faceIndices.Count)),
            new(layout.FaceDataOffset, layout.FaceDataLength, faceBytes),
        };

        foreach (var vertexBuffer in layout.VertexBuffers)
        {
            var vertexBytes = BuildVertexBufferBytes(layout, vertexBuffer, verts, mults);
            patches.Add(new RegionPatch(vertexBuffer.CountFieldOffset, 4, U32(verts.Count)));
            patches.Add(new RegionPatch(vertexBuffer.DataOffset, vertexBuffer.DataLength, vertexBytes));
        }

        var texturePlansBySlot = BuildTextureGroupPlans(layout, subInfo, preparedTextureSlotsByPrimitive);
        if (texturePlansBySlot.Count > 0)
        {
            patches.Add(new RegionPatch(
                layout.TextureGroupBlockOffset,
                layout.TextureGroupBlockLength,
                BuildTextureGroupBlock(layout, texturePlansBySlot)));
        }

        if (layout.BonePalettes.Count > layout.OriginalBonePaletteCount)
        {
            patches.Add(new RegionPatch(
                layout.BonePaletteBlockOffset,
                layout.BonePaletteBlockLength,
                BuildBonePaletteBlock(layout)));
        }

        var result = D3DMeshWriter.Apply(layout, patches);

        // MetaStream fixup: offset 4 = payload size (= length - DataOffset).
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - layout.DataOffset));
        return result;
    }

    private readonly record struct SubmeshPatchInfo(int VMin, int VMax, int FaceStart, int PolyCount, int? BonePaletteIndex, int TemplateSubmeshIndex);
    private sealed record TextureGroupEntryPlan(string TextureName, TextureEntryLayout TemplateEntry, int SortKey, int EncounterIndex);
    private sealed record PreparedPrimitive(
        GltfPrimitive Primitive,
        IReadOnlyDictionary<string, string>? TextureSlots,
        int? BonePaletteIndex);

    private sealed class PrimitiveJointUsage
    {
        public required HashSet<ulong>[] VertexHashes { get; init; }
        public required HashSet<ulong> AllHashes { get; init; }
        public required List<string> UnresolvedJoints { get; init; }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTextureSlots =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private const int MaxPaletteBonesForByteEncoding = 86;

    private sealed class PrimitiveSkinning
    {
        public required Dictionary<int, int> D3DMeshBoneByGltfJoint { get; init; }
    }

    private readonly record struct SkinVertex(
        int Bone0,
        int Bone1,
        int Bone2,
        int Bone3,
        float Weight0,
        float Weight1,
        float Weight2,
        float Weight3);

    private static bool HasVertexChannel<T>(T[]? channel, int vertexCount) => channel is { Length: var length } && length == vertexCount;

    private static Vector2 Uv(Vector2[]? channel, int vertexCount, int i)
        => HasVertexChannel(channel, vertexCount) ? channel![i] : Vector2.Zero;

    private static bool IsValidTriangle(int a, int b, int c, int vertexCount)
        => a >= 0 && b >= 0 && c >= 0 && a < vertexCount && b < vertexCount && c < vertexCount;

    private static List<PreparedPrimitive> PreparePrimitives(
        D3DMeshLayout layout,
        GltfModel model,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        SkeletonData? referenceSkeleton)
    {
        var result = new List<PreparedPrimitive>();
        int? staticImportPaletteIndex = null;
        for (var primitiveIndex = 0; primitiveIndex < model.Primitives.Count; primitiveIndex++)
        {
            var primitive = model.Primitives[primitiveIndex];
            var textureSlots = textureSlotsByPrimitive is not null && primitiveIndex < textureSlotsByPrimitive.Count
                ? textureSlotsByPrimitive[primitiveIndex]
                : null;

            if (!HasUsableSkin(primitive) || layout.BonePalettes.Count == 0)
            {
                var paletteIndex = layout.BonePalettes.Count > 0
                    ? (staticImportPaletteIndex ??= ResolveStaticImportPaletteIndex(layout, referenceSkeleton))
                    : primitive.BonePaletteIndex;
                result.Add(new PreparedPrimitive(primitive, textureSlots, paletteIndex));
                continue;
            }

            var usage = AnalyzeJointUsage(primitive, model, referenceSkeleton);
            if (usage.UnresolvedJoints.Count > 0)
            {
                throw new InvalidOperationException(
                    "Could not resolve GLB joint(s) to Telltale bone hashes: " +
                    string.Join(", ", usage.UnresolvedJoints.Take(8)) +
                    (usage.UnresolvedJoints.Count > 8 ? "..." : ""));
            }

            if (usage.AllHashes.Count == 0)
            {
                var paletteIndex = staticImportPaletteIndex ??= ResolveStaticImportPaletteIndex(layout, referenceSkeleton);
                result.Add(new PreparedPrimitive(primitive, textureSlots, paletteIndex));
                continue;
            }

            var preferredPalette = ResolvePrimitivePaletteIndex(layout, primitive, primitiveIndex);

            // A GLB carries the bone-palette index of the model it was exported from. That index is
            // only meaningful on the same template (a round-trip); porting a model onto a different
            // template (e.g. a The Wolf Among Us mesh into a The Walking Dead skeleton) lands on a
            // palette that holds entirely different bones. So honour the declared palette only when it
            // actually covers all weighted joints, otherwise fall through to find or build one that does.
            if (primitive.BonePaletteIndex is { } explicitPalette &&
                explicitPalette >= 0 &&
                explicitPalette < layout.BonePalettes.Count &&
                PaletteCovers(layout.BonePalettes[explicitPalette], usage.AllHashes))
            {
                result.Add(new PreparedPrimitive(primitive, textureSlots, explicitPalette));
                continue;
            }

            if (FindCoveringPalette(layout, usage.AllHashes, preferredPalette) is { } fullPalette)
            {
                result.Add(new PreparedPrimitive(primitive, textureSlots, fullPalette));
                continue;
            }

            var maxPaletteBones = MaxPaletteBonesForLayout(layout);
            if (usage.AllHashes.Count <= maxPaletteBones)
            {
                var customPalette = AddCustomBonePalette(layout, usage.AllHashes, maxPaletteBones);
                result.Add(new PreparedPrimitive(primitive, textureSlots, customPalette));
                continue;
            }

            var slices = SplitPrimitiveByBonePalette(layout, primitive, usage, textureSlots, preferredPalette);
            result.AddRange(slices);
        }

        return result;
    }

    private static bool HasUsableSkin(GltfPrimitive primitive)
        => primitive.Joints0 is { } joints &&
           primitive.Weights0 is { } weights &&
           joints.Length >= primitive.VertexCount * 4 &&
           weights.Length == primitive.VertexCount;

    private static PrimitiveJointUsage AnalyzeJointUsage(
        GltfPrimitive primitive,
        GltfModel model,
        SkeletonData? referenceSkeleton)
    {
        var vertexHashes = new HashSet<ulong>[primitive.VertexCount];
        var allHashes = new HashSet<ulong>();
        var unresolved = new List<string>();
        var unresolvedSeen = new HashSet<int>();

        for (var vertex = 0; vertex < primitive.VertexCount; vertex++)
        {
            var hashes = new HashSet<ulong>();
            vertexHashes[vertex] = hashes;
            if (primitive.Joints0 is null || primitive.Weights0 is null)
            {
                continue;
            }

            var jointOffset = vertex * 4;
            var weights = primitive.Weights0[vertex];
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = influence switch
                {
                    0 => weights.X,
                    1 => weights.Y,
                    2 => weights.Z,
                    _ => weights.W,
                };
                if (weight <= 0.000001f)
                {
                    continue;
                }

                var gltfJoint = primitive.Joints0[jointOffset + influence];
                if (TryResolveJointHash(gltfJoint, model, referenceSkeleton, out var hash) && hash != 0)
                {
                    hashes.Add(hash);
                    allHashes.Add(hash);
                }
                else if (unresolvedSeen.Add(gltfJoint))
                {
                    unresolved.Add(DescribeGltfJoint(gltfJoint, model));
                }
            }
        }

        return new PrimitiveJointUsage
        {
            VertexHashes = vertexHashes,
            AllHashes = allHashes,
            UnresolvedJoints = unresolved,
        };
    }

    private static IEnumerable<int> EnumerateWeightedJoints(GltfPrimitive primitive)
    {
        if (primitive.Joints0 is null || primitive.Weights0 is null)
        {
            yield break;
        }

        for (var vertex = 0; vertex < primitive.VertexCount; vertex++)
        {
            var jointOffset = vertex * 4;
            var weights = primitive.Weights0[vertex];
            if (weights.X > 0.000001f) yield return primitive.Joints0[jointOffset];
            if (weights.Y > 0.000001f) yield return primitive.Joints0[jointOffset + 1];
            if (weights.Z > 0.000001f) yield return primitive.Joints0[jointOffset + 2];
            if (weights.W > 0.000001f) yield return primitive.Joints0[jointOffset + 3];
        }
    }

    private static string DescribeGltfJoint(int jointIndex, GltfModel model)
    {
        if (jointIndex >= 0 &&
            jointIndex < model.Joints.Count &&
            !string.IsNullOrWhiteSpace(model.Joints[jointIndex].Name))
        {
            return $"{jointIndex} ({model.Joints[jointIndex].Name})";
        }

        return jointIndex.ToString();
    }

    private static int? FindCoveringPalette(D3DMeshLayout layout, IReadOnlySet<ulong> hashes, int preferredPalette)
    {
        if (preferredPalette >= 0 &&
            preferredPalette < layout.BonePalettes.Count &&
            PaletteCovers(layout.BonePalettes[preferredPalette], hashes))
        {
            return preferredPalette;
        }

        for (var i = 0; i < layout.BonePalettes.Count; i++)
        {
            if (PaletteCovers(layout.BonePalettes[i], hashes))
            {
                return i;
            }
        }

        return null;
    }

    private static bool PaletteCovers(IReadOnlyList<ulong> palette, IReadOnlySet<ulong> hashes)
    {
        if (hashes.Count == 0)
        {
            return true;
        }

        var paletteHashes = palette.ToHashSet();
        return hashes.All(paletteHashes.Contains);
    }

    private static int AddCustomBonePalette(D3DMeshLayout layout, IEnumerable<ulong> hashes, int maxPaletteBones)
    {
        var palette = hashes
            .Where(hash => hash != 0)
            .Distinct()
            .OrderBy(hash => hash)
            .ToArray();
        if (palette.Length > maxPaletteBones)
        {
            throw new InvalidOperationException(
                $"The GLB skin uses {palette.Length} weighted bones in one primitive; this template can encode at most {maxPaletteBones} bones per palette.");
        }

        var paletteSet = palette.ToHashSet();
        for (var i = 0; i < layout.BonePalettes.Count; i++)
        {
            if (layout.BonePalettes[i].Length == palette.Length &&
                paletteSet.SetEquals(layout.BonePalettes[i]))
            {
                return i;
            }
        }

        layout.BonePalettes.Add(palette);
        return layout.BonePalettes.Count - 1;
    }

    private static int MaxPaletteBonesForLayout(D3DMeshLayout layout)
    {
        var maxIndex = MaxPaletteBonesForByteEncoding - 1;
        foreach (var vertexBuffer in layout.VertexBuffers)
        {
            var format = vertexBuffer.Attributes.Bones.Format;
            if (format == 0 || layout.Version is not (17 or 18))
            {
                continue;
            }

            maxIndex = Math.Min(maxIndex, format switch
            {
                3 => 63,
                8 => 85,
                _ => maxIndex,
            });
        }

        return maxIndex + 1;
    }

    private static int? ResolveStaticImportPaletteIndex(D3DMeshLayout layout, SkeletonData? referenceSkeleton)
    {
        if (layout.BonePalettes.Count == 0)
        {
            return null;
        }

        if (TryGetRootBoneHash(referenceSkeleton, out var rootHash))
        {
            for (var i = 0; i < layout.BonePalettes.Count; i++)
            {
                var palette = layout.BonePalettes[i];
                if (palette.Length > 0 && palette[0] == rootHash)
                {
                    return i;
                }
            }

            return AddCustomBonePalette(layout, [rootHash], MaxPaletteBonesForLayout(layout));
        }

        for (var i = 0; i < layout.BonePalettes.Count; i++)
        {
            if (layout.BonePalettes[i].Length > 0)
            {
                return i;
            }
        }

        return 0;
    }

    private static bool TryGetRootBoneHash(SkeletonData? skeleton, out ulong hash)
    {
        hash = 0;
        if (skeleton is null || skeleton.Bones.Count == 0)
        {
            return false;
        }

        var root = skeleton.Bones.FirstOrDefault(bone => bone.ParentIndex < 0) ?? skeleton.Bones[0];
        hash = root.Hash;
        return hash != 0;
    }

    private static List<PreparedPrimitive> SplitPrimitiveByBonePalette(
        D3DMeshLayout layout,
        GltfPrimitive primitive,
        PrimitiveJointUsage usage,
        IReadOnlyDictionary<string, string>? textureSlots,
        int preferredPalette)
    {
        var trianglesByPalette = new Dictionary<int, List<int>>();
        var failedTriangles = 0;
        for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
        {
            var a = primitive.Indices[i];
            var b = primitive.Indices[i + 1];
            var c = primitive.Indices[i + 2];
            if (!IsValidTriangle(a, b, c, primitive.VertexCount))
            {
                continue;
            }

            var triangleHashes = new HashSet<ulong>();
            triangleHashes.UnionWith(usage.VertexHashes[a]);
            triangleHashes.UnionWith(usage.VertexHashes[b]);
            triangleHashes.UnionWith(usage.VertexHashes[c]);

            if (FindCoveringPalette(layout, triangleHashes, preferredPalette) is not { } paletteIndex)
            {
                failedTriangles++;
                continue;
            }

            if (!trianglesByPalette.TryGetValue(paletteIndex, out var indices))
            {
                indices = [];
                trianglesByPalette[paletteIndex] = indices;
            }

            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }

        if (failedTriangles > 0 || trianglesByPalette.Count == 0)
        {
            throw new InvalidOperationException(
                $"The GLB skin uses bones that cannot fit the template bone palettes ({failedTriangles} triangle(s) failed). " +
                "Keep the original Telltale bone names and split the model by compatible body parts, or use a template with matching palettes.");
        }

        return trianglesByPalette
            .OrderBy(pair => pair.Key == preferredPalette ? -1 : pair.Key)
            .Select(pair => new PreparedPrimitive(
                SlicePrimitive(primitive, pair.Value, pair.Key),
                textureSlots,
                pair.Key))
            .ToList();
    }

    private static GltfPrimitive SlicePrimitive(GltfPrimitive source, IReadOnlyList<int> triangleIndices, int paletteIndex)
    {
        var remap = new Dictionary<int, int>();
        var sourceVertices = new List<int>();
        foreach (var index in triangleIndices)
        {
            if (!remap.ContainsKey(index))
            {
                remap[index] = sourceVertices.Count;
                sourceVertices.Add(index);
            }
        }

        var joints = SliceJoints(source.Joints0, sourceVertices, source.VertexCount);
        return new GltfPrimitive
        {
            Positions = SliceChannel(source.Positions, sourceVertices, source.VertexCount)!,
            Normals = SliceChannel(source.Normals, sourceVertices, source.VertexCount),
            Uv0 = SliceChannel(source.Uv0, sourceVertices, source.VertexCount),
            Uv1 = SliceChannel(source.Uv1, sourceVertices, source.VertexCount),
            Uv2 = SliceChannel(source.Uv2, sourceVertices, source.VertexCount),
            Uv3 = SliceChannel(source.Uv3, sourceVertices, source.VertexCount),
            Color0 = SliceChannel(source.Color0, sourceVertices, source.VertexCount),
            Tangents = SliceChannel(source.Tangents, sourceVertices, source.VertexCount),
            Binormals = SliceChannel(source.Binormals, sourceVertices, source.VertexCount),
            Unknown1 = SliceChannel(source.Unknown1, sourceVertices, source.VertexCount),
            Joints0 = joints,
            Weights0 = SliceChannel(source.Weights0, sourceVertices, source.VertexCount),
            Indices = triangleIndices.Select(index => remap[index]).ToArray(),
            MaterialName = source.MaterialName,
            BonePaletteIndex = paletteIndex,
            SourceMeshPath = source.SourceMeshPath,
            SourceSubmeshIndex = source.SourceSubmeshIndex,
            IsSkinned = source.IsSkinned,
            BaseColor = source.BaseColor,
            TextureSlots = source.TextureSlots,
            ReferencedTextures = source.ReferencedTextures,
        };
    }

    private static T[]? SliceChannel<T>(T[]? source, IReadOnlyList<int> sourceVertices, int vertexCount)
    {
        if (!HasVertexChannel(source, vertexCount))
        {
            return null;
        }

        var result = new T[sourceVertices.Count];
        for (var i = 0; i < sourceVertices.Count; i++)
        {
            result[i] = source![sourceVertices[i]];
        }

        return result;
    }

    private static ushort[]? SliceJoints(ushort[]? source, IReadOnlyList<int> sourceVertices, int vertexCount)
    {
        if (source is null || source.Length < vertexCount * 4)
        {
            return null;
        }

        var result = new ushort[sourceVertices.Count * 4];
        for (var i = 0; i < sourceVertices.Count; i++)
        {
            Array.Copy(source, sourceVertices[i] * 4, result, i * 4, 4);
        }

        return result;
    }

    private static PrimitiveSkinning? BuildSkinningMap(
        D3DMeshLayout layout,
        GltfModel model,
        GltfPrimitive prim,
        int primitiveIndex,
        SkeletonData? referenceSkeleton,
        int? preparedPaletteIndex)
    {
        if (prim.Joints0 is null ||
            prim.Weights0 is null ||
            prim.Joints0.Length < prim.VertexCount * 4 ||
            prim.Weights0.Length != prim.VertexCount ||
            layout.BonePalettes.Count == 0)
        {
            return null;
        }

        var paletteIndex = ResolvePrimitivePaletteIndex(layout, prim, primitiveIndex, preparedPaletteIndex);
        var palette = layout.BonePalettes[paletteIndex];
        if (palette.Length == 0)
        {
            return null;
        }

        // The value handed to VertexEncoder must match each version's stored-raw convention:
        // v13/14 store rawIndex = paletteIndex*3 (encoder factor 1), while v17/18 store
        // paletteIndex*formatFactor (format 3 -> x4, format 8 -> x3, applied by the encoder),
        // so on v17/18 the encoder must receive the DIRECT palette index, never pre-multiplied.
        var rawMultiplier = layout.Version is 17 or 18 ? 1 : 3;
        var mapped = new Dictionary<int, int>();
        foreach (var gltfJoint in EnumerateWeightedJoints(prim).Distinct())
        {
            var localPaletteIndex = ResolvePaletteLocalIndex(gltfJoint, model, referenceSkeleton, palette);
            mapped[gltfJoint] = localPaletteIndex >= 0 ? localPaletteIndex * rawMultiplier : -1;
        }

        return new PrimitiveSkinning { D3DMeshBoneByGltfJoint = mapped };
    }

    private static int ResolvePrimitivePaletteIndex(D3DMeshLayout layout, GltfPrimitive prim, int primitiveIndex)
        => ResolvePrimitivePaletteIndex(layout, prim, primitiveIndex, preparedPaletteIndex: null);

    private static int ResolveTemplateSubmeshIndex(D3DMeshLayout layout, GltfPrimitive prim, int primitiveIndex)
    {
        if (prim.SourceSubmeshIndex is { } sourceIndex &&
            sourceIndex >= 0 &&
            sourceIndex < layout.Submeshes.Count)
        {
            return sourceIndex;
        }

        return Math.Min(primitiveIndex, layout.Submeshes.Count - 1);
    }

    private static int ResolvePrimitivePaletteIndex(D3DMeshLayout layout, GltfPrimitive prim, int primitiveIndex, int? preparedPaletteIndex)
    {
        if (preparedPaletteIndex is { } prepared &&
            prepared >= 0 &&
            prepared < layout.BonePalettes.Count)
        {
            return prepared;
        }

        if (prim.BonePaletteIndex is { } explicitIndex &&
            explicitIndex >= 0 &&
            explicitIndex < layout.BonePalettes.Count)
        {
            return explicitIndex;
        }

        var template = GetSubmeshTemplate(layout, ResolveTemplateSubmeshIndex(layout, prim, primitiveIndex));
        return NormalizePaletteIndex(template.BoneSetRaw + 1, layout.BonePalettes.Count);
    }

    private static int ResolvePaletteLocalIndex(
        int gltfJoint,
        GltfModel model,
        SkeletonData? referenceSkeleton,
        IReadOnlyList<ulong> palette)
    {
        if (TryResolveJointHash(gltfJoint, model, referenceSkeleton, out var hash))
        {
            for (var i = 0; i < palette.Count; i++)
            {
                if (palette[i] == hash)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool TryResolveJointHash(
        int gltfJoint,
        GltfModel model,
        SkeletonData? referenceSkeleton,
        out ulong hash)
    {
        if (gltfJoint >= 0 && gltfJoint < model.Joints.Count)
        {
            var joint = model.Joints[gltfJoint];
            if (joint.Hash is { } explicitHash)
            {
                hash = explicitHash;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(joint.Name) &&
                referenceSkeleton is not null)
            {
                var byName = referenceSkeleton.Bones.FirstOrDefault(bone =>
                    string.Equals(bone.Name, joint.Name, StringComparison.OrdinalIgnoreCase));
                if (byName is not null)
                {
                    hash = byName.Hash;
                    return true;
                }
            }
        }

        if (referenceSkeleton is not null &&
            gltfJoint >= 0 &&
            gltfJoint < referenceSkeleton.Bones.Count)
        {
            hash = referenceSkeleton.Bones[gltfJoint].Hash;
            return true;
        }

        hash = 0;
        return false;
    }

    private static SkinVertex ReadSkinning(GltfPrimitive prim, int vertexIndex, PrimitiveSkinning? skinning)
    {
        if (skinning is null || prim.Joints0 is null || prim.Weights0 is null)
        {
            return StaticSkin();
        }

        var jointOffset = vertexIndex * 4;
        var joints = new[]
        {
            prim.Joints0[jointOffset],
            prim.Joints0[jointOffset + 1],
            prim.Joints0[jointOffset + 2],
            prim.Joints0[jointOffset + 3],
        };
        var weightsVec = prim.Weights0[vertexIndex];
        var weights = new[] { weightsVec.X, weightsVec.Y, weightsVec.Z, weightsVec.W };
        var influences = new List<(int Bone, float Weight)>(4);

        for (var i = 0; i < 4; i++)
        {
            if (weights[i] <= 0.000001f ||
                !skinning.D3DMeshBoneByGltfJoint.TryGetValue(joints[i], out var d3dmeshBone) ||
                d3dmeshBone < 0)
            {
                continue;
            }

            influences.Add((d3dmeshBone, weights[i]));
        }

        if (influences.Count == 0)
        {
            for (var i = 0; i < 4; i++)
            {
                if (skinning.D3DMeshBoneByGltfJoint.TryGetValue(joints[i], out var d3dmeshBone) &&
                    d3dmeshBone >= 0)
                {
                    return new SkinVertex(d3dmeshBone, 0, 0, 0, 1f, 0f, 0f, 0f);
                }
            }

            return StaticSkin();
        }

        var total = influences.Sum(influence => influence.Weight);
        if (total <= 0.000001f)
        {
            return StaticSkin();
        }

        var bones = new[] { 0, 0, 0, 0 };
        var normalized = new[] { 0f, 0f, 0f, 0f };
        for (var i = 0; i < Math.Min(4, influences.Count); i++)
        {
            bones[i] = influences[i].Bone;
            normalized[i] = influences[i].Weight / total;
        }

        return new SkinVertex(
            bones[0], bones[1], bones[2], bones[3],
            normalized[0], normalized[1], normalized[2], normalized[3]);
    }

    private static SkinVertex StaticSkin() => new(0, 0, 0, 0, 1f, 0f, 0f, 0f);

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

    private static byte[] U32(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value);
        return b;
    }

    private static int BuildSubmeshTableSizeDelta(
        D3DMeshLayout layout,
        IReadOnlyList<SubmeshPatchInfo> subInfo,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive)
        => BuildSubmeshTableLength(layout, subInfo, textureSlotsByPrimitive) - layout.SubmeshTableLength;

    private static int BuildSubmeshTableLength(
        D3DMeshLayout layout,
        IReadOnlyList<SubmeshPatchInfo> subInfo,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive)
    {
        var total = 0;
        for (var i = 0; i < subInfo.Count; i++)
        {
            total += GetSubmeshTemplate(layout, subInfo[i].TemplateSubmeshIndex, TextureSlotsForPrimitive(textureSlotsByPrimitive, i)).EntryLength;
        }

        return total;
    }

    private static byte[] BuildSubmeshTable(
        D3DMeshLayout layout,
        IReadOnlyList<SubmeshPatchInfo> subInfo,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        GameConfig gameConfig)
    {
        using var ms = new MemoryStream(BuildSubmeshTableLength(layout, subInfo, textureSlotsByPrimitive));
        var texturePlansBySlot = BuildTextureGroupPlans(layout, subInfo, textureSlotsByPrimitive);
        var rewrittenTextureSlots = texturePlansBySlot.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textureIndexBySlot = texturePlansBySlot
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Select((name, index) => (name, index))
                    .ToDictionary(item => item.name.TextureName, item => item.index, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < subInfo.Count; i++)
        {
            var info = subInfo[i];
            var template = GetSubmeshTemplate(layout, info.TemplateSubmeshIndex, TextureSlotsForPrimitive(textureSlotsByPrimitive, i));
            var bytes = layout.Original.AsSpan(template.EntryOffset, template.EntryLength).ToArray();

            WriteU32(bytes, template.VertexMinFieldOffset - template.EntryOffset, info.VMin);
            WriteU32(bytes, template.VertexMaxFieldOffset - template.EntryOffset, info.VMax);
            WriteU32(bytes, template.FaceStartFieldOffset - template.EntryOffset, info.FaceStart);
            WriteU32(bytes, template.PolygonCountFieldOffset - template.EntryOffset, info.PolyCount);
            if (info.BonePaletteIndex is { } bonePaletteIndex &&
                bonePaletteIndex >= 0 &&
                bonePaletteIndex < layout.BonePalettes.Count)
            {
                WriteU32(bytes, template.BoneSetFieldOffset - template.EntryOffset, bonePaletteIndex);
            }

            if (i >= layout.Submeshes.Count)
            {
            ClearAccessoryLineTextureSlots(layout, bytes, template);
        }

            ClearStaleTemplateTextureSlots(layout, gameConfig, bytes, template, textureSlotsByPrimitive, i);
            ClearSlotsRewrittenByGltf(layout, bytes, template, textureSlotsByPrimitive, i, rewrittenTextureSlots);

            if (textureSlotsByPrimitive is not null && i < textureSlotsByPrimitive.Count)
            {
                foreach (var (slot, textureName) in textureSlotsByPrimitive[i])
                {
                    var slotIndex = Array.FindIndex(layout.TextureSlots, candidate => string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase));
                    if (slotIndex < 0 ||
                        slotIndex >= template.TextureSlotFieldOffsets.Length ||
                        !textureIndexBySlot.TryGetValue(slot, out var indexByName) ||
                        !indexByName.TryGetValue(textureName, out var textureIndex))
                    {
                        continue;
                    }

                    WriteU32(bytes, template.TextureSlotFieldOffsets[slotIndex] - template.EntryOffset, textureIndex);
                }
            }
            ms.Write(bytes, 0, bytes.Length);
        }

        return ms.ToArray();
    }

    private static void ClearStaleTemplateTextureSlots(
        D3DMeshLayout layout,
        GameConfig gameConfig,
        byte[] bytes,
        SubmeshLayout template,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        int primitiveIndex)
    {
        if (gameConfig.ClearInheritedBakeOnReimport &&
            !PrimitiveProvidesSlot(textureSlotsByPrimitive, primitiveIndex, "bake"))
        {
            ClearTextureSlot(layout, bytes, template, "bake");
        }

        if (!gameConfig.ClearInheritedSecondaryTexturesOnReimport)
        {
            return;
        }

        foreach (var slot in new[]
                 {
                     "bump",
                     "environment",
                     "detail_diffuse",
                     "detail_bump",
                     "specular",
                     "tex8",
                     "gradient",
                     "tex10",
                     "shadow",
                 })
        {
            if (!PrimitiveProvidesSlot(textureSlotsByPrimitive, primitiveIndex, slot))
            {
                ClearTextureSlot(layout, bytes, template, slot);
            }
        }
    }

    private static void ClearSlotsRewrittenByGltf(
        D3DMeshLayout layout,
        byte[] bytes,
        SubmeshLayout template,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        int primitiveIndex,
        IReadOnlySet<string> rewrittenTextureSlots)
    {
        foreach (var slot in rewrittenTextureSlots)
        {
            if (!PrimitiveProvidesSlot(textureSlotsByPrimitive, primitiveIndex, slot))
            {
                ClearTextureSlot(layout, bytes, template, slot);
            }
        }
    }

    private static void ClearAccessoryLineTextureSlots(D3DMeshLayout layout, byte[] bytes, SubmeshLayout template)
    {
        foreach (var slot in new[] { "detail_diffuse", "detail_bump", "tex8" })
        {
            ClearTextureSlot(layout, bytes, template, slot);
        }
    }

    private static bool PrimitiveProvidesSlot(
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        int primitiveIndex,
        string slot)
    {
        return textureSlotsByPrimitive is not null &&
               primitiveIndex >= 0 &&
               primitiveIndex < textureSlotsByPrimitive.Count &&
               textureSlotsByPrimitive[primitiveIndex].ContainsKey(slot);
    }

    private static void ClearTextureSlot(D3DMeshLayout layout, byte[] bytes, SubmeshLayout template, string slot)
    {
        var slotIndex = Array.FindIndex(layout.TextureSlots, candidate => string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase));
        if (slotIndex < 0 || slotIndex >= template.TextureSlotFieldOffsets.Length)
        {
            return;
        }

        var fieldOffset = template.TextureSlotFieldOffsets[slotIndex];
        WriteU32(bytes, fieldOffset - template.EntryOffset, -1);
    }

    private static Dictionary<string, List<TextureGroupEntryPlan>> BuildTextureGroupPlans(
        D3DMeshLayout layout,
        IReadOnlyList<SubmeshPatchInfo> subInfo,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive)
    {
        var result = new Dictionary<string, List<TextureGroupEntryPlan>>(StringComparer.OrdinalIgnoreCase);
        if (textureSlotsByPrimitive is null)
        {
            return result;
        }

        var fallbackEntry = layout.TextureGroups.SelectMany(group => group.Entries).FirstOrDefault()
            ?? throw new InvalidOperationException("Template has no texture entry to copy as a base.");
        var encounterIndex = 0;

        for (var primitiveIndex = 0; primitiveIndex < textureSlotsByPrimitive.Count; primitiveIndex++)
        {
            var primitiveSlots = textureSlotsByPrimitive[primitiveIndex];
            var templateIndex = primitiveIndex < subInfo.Count
                ? subInfo[primitiveIndex].TemplateSubmeshIndex
                : primitiveIndex;
            var template = GetSubmeshTemplate(layout, templateIndex, primitiveSlots);
            foreach (var (slot, textureName) in primitiveSlots)
            {
                if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(textureName))
                {
                    continue;
                }

                var slotIndex = Array.FindIndex(layout.TextureSlots, candidate => string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase));
                if (slotIndex < 0)
                {
                    continue;
                }

                if (!result.TryGetValue(slot, out var names))
                {
                    names = [];
                    result[slot] = names;
                }

                if (names.Any(existing => string.Equals(existing.TextureName, textureName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var group = slotIndex < layout.TextureGroups.Count ? layout.TextureGroups[slotIndex] : null;
                var sourceIndex = ReadTemplateTextureSlotIndex(layout, template, slotIndex);
                var templateEntry = group is not null &&
                                    sourceIndex >= 0 &&
                                    sourceIndex < group.Entries.Count
                    ? group.Entries[sourceIndex]
                    : group?.Entries.FirstOrDefault() ?? fallbackEntry;
                var sortKey = sourceIndex >= 0 ? sourceIndex : int.MaxValue;
                names.Add(new TextureGroupEntryPlan(textureName, templateEntry, sortKey, encounterIndex++));
            }
        }

        foreach (var slot in result.Keys.ToArray())
        {
            result[slot] = result[slot]
                .OrderBy(entry => entry.SortKey)
                .ThenBy(entry => entry.EncounterIndex)
                .ToList();
        }

        return result;
    }

    private static int ReadTemplateTextureSlotIndex(D3DMeshLayout layout, SubmeshLayout template, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= template.TextureSlotFieldOffsets.Length)
        {
            return -1;
        }

        return (int)BinaryPrimitives.ReadUInt32LittleEndian(
            layout.Original.AsSpan(template.TextureSlotFieldOffsets[slotIndex], 4));
    }

    private static byte[] BuildTextureGroupBlock(D3DMeshLayout layout, IReadOnlyDictionary<string, List<TextureGroupEntryPlan>> textureNamesBySlot)
    {
        using var ms = new MemoryStream();
        ms.Write(U32(0));
        var countBytes = new byte[4];
        for (var groupIndex = 0; groupIndex < layout.TextureGroups.Count; groupIndex++)
        {
            var slot = groupIndex < layout.TextureSlots.Length ? layout.TextureSlots[groupIndex] : "";
            if (!string.IsNullOrWhiteSpace(slot) &&
                textureNamesBySlot.TryGetValue(slot, out var texturePlans) &&
                texturePlans.Count > 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(countBytes, (uint)texturePlans.Count);
                ms.Write(countBytes);
                foreach (var texturePlan in texturePlans)
                {
                    var textureName = texturePlan.TextureName;
                    var templateEntry = texturePlan.TemplateEntry;
                    var bytes = layout.Original.AsSpan(templateEntry.EntryOffset, templateEntry.EntryLength).ToArray();
                    var hash = Crc64Ecma.Compute(textureName.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
                        ? textureName
                        : textureName + ".d3dtx");
                    WriteU32(bytes, templateEntry.HashLowFieldOffset - templateEntry.EntryOffset, (int)(hash & 0xFFFFFFFF));
                    WriteU32(bytes, templateEntry.HashHighFieldOffset - templateEntry.EntryOffset, (int)(hash >> 32));
                    ms.Write(bytes, 0, bytes.Length);
                }

                continue;
            }

            var originalGroup = layout.TextureGroups[groupIndex];
            ms.Write(layout.Original, originalGroup.GroupOffset, originalGroup.GroupLength);
        }

        var result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)result.Length);
        return result;
    }

    private static byte[] BuildBonePaletteBlock(D3DMeshLayout layout)
    {
        using var ms = new MemoryStream();
        ms.Write(layout.Original, layout.BonePaletteBlockOffset, layout.BonePaletteBlockLength);

        for (var paletteIndex = layout.OriginalBonePaletteCount; paletteIndex < layout.BonePalettes.Count; paletteIndex++)
        {
            var palette = layout.BonePalettes[paletteIndex];
            ms.Write(U32(palette.Length));
            foreach (var hash in palette)
            {
                var entry = layout.BonePaletteEntryTemplate.Length == layout.BonePaletteEntrySize
                    ? layout.BonePaletteEntryTemplate.ToArray()
                    : new byte[layout.BonePaletteEntrySize];
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0, 4), unchecked((uint)hash));
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4, 4), unchecked((uint)(hash >> 32)));
                ms.Write(entry, 0, entry.Length);
            }
        }

        var result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)layout.BonePalettes.Count);
        return result;
    }

    private static IReadOnlyDictionary<string, string>? TextureSlotsForPrimitive(
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        int primitiveIndex)
        => textureSlotsByPrimitive is not null &&
           primitiveIndex >= 0 &&
           primitiveIndex < textureSlotsByPrimitive.Count
            ? textureSlotsByPrimitive[primitiveIndex]
            : null;

    private static SubmeshLayout GetSubmeshTemplate(
        D3DMeshLayout layout,
        int index,
        IReadOnlyDictionary<string, string>? primitiveSlots = null)
    {
        var byIndex = layout.Submeshes[Math.Min(index, layout.Submeshes.Count - 1)];
        if (primitiveSlots is null ||
            !primitiveSlots.ContainsKey("detail_diffuse") ||
            !ShouldUseDetailCapableTemplate(index, primitiveSlots) ||
            TemplateProvidesSlot(layout, byIndex, "detail_diffuse"))
        {
            return byIndex;
        }

        return layout.Submeshes
            .Where(template => TemplateProvidesSlot(layout, template, "detail_diffuse"))
            .OrderByDescending(template => template.PolygonCount)
            .FirstOrDefault()
            ?? byIndex;
    }

    private static bool ShouldUseDetailCapableTemplate(int primitiveIndex, IReadOnlyDictionary<string, string> primitiveSlots)
    {
        if ((primitiveIndex == 1 || primitiveIndex == 2) &&
            primitiveSlots.ContainsKey("detail_diffuse"))
        {
            return true;
        }

        if (primitiveSlots.TryGetValue("detail_diffuse", out var detailName) &&
            IsArmOrSleeveTextureName(detailName))
        {
            return true;
        }

        return primitiveSlots.TryGetValue("diffuse", out var diffuseName) &&
               IsArmOrSleeveTextureName(diffuseName);
    }

    private static bool IsArmOrSleeveTextureName(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.Contains("sleeve", StringComparison.Ordinal) ||
               lower.Contains("arm", StringComparison.Ordinal);
    }

    private static bool TemplateProvidesSlot(D3DMeshLayout layout, SubmeshLayout template, string slot)
    {
        var slotIndex = Array.FindIndex(layout.TextureSlots, candidate => string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase));
        return ReadTemplateTextureSlotIndex(layout, template, slotIndex) >= 0;
    }

    private static void WriteU32(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)value);

    private static byte[] BuildFaceBytes(D3DMeshLayout layout, IReadOnlyList<int> faceIndices)
    {
        var faceBytes = new byte[faceIndices.Count * 2];
        for (var i = 0; i < faceIndices.Count; i++)
        {
            var span = faceBytes.AsSpan(i * 2, 2);
            if (layout.FaceIndexFormat == 2)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)faceIndices[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)faceIndices[i]);
            }
        }

        return faceBytes;
    }

    private static byte[] BuildVertexBufferBytes(
        D3DMeshLayout layout,
        VertexBufferLayout vertexBuffer,
        IReadOnlyList<EncVertex> verts,
        UvMults mults)
    {
        var encoder = new VertexEncoder(vertexBuffer.Attributes, mults, layout.Version, vertexBuffer.VertexStride);
        var vertexBytes = new byte[checked(verts.Count * encoder.Stride)];
        for (var i = 0; i < verts.Count; i++)
        {
            encoder.Write(vertexBytes.AsSpan(i * encoder.Stride, encoder.Stride), verts[i]);
        }

        return vertexBytes;
    }

    private static byte[] BuildBoundsBytes(List<EncVertex> verts)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var v in verts)
        {
            minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y); minZ = Math.Min(minZ, v.Z);
            maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y); maxZ = Math.Max(maxZ, v.Z);
        }

        var b = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), minX);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), minY);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), minZ);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), maxX);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), maxY);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), maxZ);
        return b;
    }

    private static byte[] BuildUvScaleBytes(D3DMeshLayout layout, UvMults m)
    {
        var b = new byte[layout.UvScalesLength];
        var values = layout.UvScalesLength >= 40
            ? BuildV18UvScaleValues(layout, m)
            : [m.Uv1X, m.Uv1Y, m.Uv4X, m.Uv4Y, m.Uv2X, m.Uv2Y, m.Uv3X, m.Uv3Y];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
        }

        return b;
    }

    private static float[] BuildV18UvScaleValues(D3DMeshLayout layout, UvMults m)
    {
        var span = layout.Original.AsSpan(layout.UvScalesOffset, layout.UvScalesLength);
        var extraX = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
        var extraY = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
        return [m.Uv1X, m.Uv1Y, extraX, extraY, m.Uv2X, m.Uv2Y, m.Uv3X, m.Uv3Y, m.Uv4X, m.Uv4Y];
    }

    private static UvMults ChooseUvMults(D3DMeshLayout layout, List<EncVertex> verts)
    {
        var original = ReadOriginalUvMults(layout);
        return CanEncodeUvs(layout, original, verts)
            ? original
            : ComputeUvMults(verts);
    }

    private static UvMults ReadOriginalUvMults(D3DMeshLayout layout)
    {
        var span = layout.Original.AsSpan(layout.UvScalesOffset, layout.UvScalesLength);
        var uv1X = BinaryPrimitives.ReadSingleLittleEndian(span[0..]);
        var uv1Y = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
        const int uv2Offset = 16;
        const int uv3Offset = 24;
        var uv4Offset = layout.UvScalesLength >= 40 ? 32 : 8;
        var uv2X = BinaryPrimitives.ReadSingleLittleEndian(span[uv2Offset..]);
        var uv2Y = BinaryPrimitives.ReadSingleLittleEndian(span[(uv2Offset + 4)..]);
        var uv3X = BinaryPrimitives.ReadSingleLittleEndian(span[uv3Offset..]);
        var uv3Y = BinaryPrimitives.ReadSingleLittleEndian(span[(uv3Offset + 4)..]);
        var uv4X = BinaryPrimitives.ReadSingleLittleEndian(span[uv4Offset..]);
        var uv4Y = BinaryPrimitives.ReadSingleLittleEndian(span[(uv4Offset + 4)..]);
        return new UvMults(uv1X, uv1Y, uv2X, uv2Y, uv3X, uv3Y, uv4X, uv4Y);
    }

    private static bool CanEncodeUvs(D3DMeshLayout layout, UvMults mults, IReadOnlyList<EncVertex> verts)
    {
        foreach (var vertexBuffer in layout.VertexBuffers)
        {
            var attrs = vertexBuffer.Attributes;
            foreach (var v in verts)
            {
                if (!CanEncodeUv(attrs.Uv1.Format, v.U0, v.V0, mults.Uv1X, mults.Uv1Y) ||
                    !CanEncodeUv(attrs.Uv2.Format, v.U1, v.V1, mults.Uv2X, mults.Uv2Y) ||
                    !CanEncodeUv(attrs.Uv3.Format, v.U2, v.V2, mults.Uv3X, mults.Uv3Y) ||
                    !CanEncodeUv(attrs.Uv4.Format, v.U3, v.V3, mults.Uv4X, mults.Uv4Y) ||
                    !CanEncodeUv(attrs.Uv5.Format, v.U3, v.V3, mults.Uv4X, mults.Uv4Y))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool CanEncodeUv(uint format, float u, float v, float multX, float multY)
    {
        return format switch
        {
            0 or 1 or 11 => true,
            4 => CanEncodeSignedUv(u, multX) && CanEncodeSignedUv(v, multY),
            5 => CanEncodeUnsignedUv(u, multX) && CanEncodeUnsignedUv(v, multY),
            _ => false,
        };
    }

    private static bool CanEncodeSignedUv(float value, float mult)
    {
        if (Math.Abs(mult) <= 1e-9f)
        {
            return Math.Abs(value) <= 1e-6f;
        }

        var scaled = value / mult;
        return scaled >= -1.0001f && scaled <= 1.0001f;
    }

    private static bool CanEncodeUnsignedUv(float value, float mult)
    {
        if (Math.Abs(mult) <= 1e-9f)
        {
            return Math.Abs(value) <= 1e-6f;
        }

        var scaled = value / mult;
        return scaled >= -0.0001f && scaled <= 1.0001f;
    }

    // Recomputes UV multipliers for the model's real range (only relevant for integer formats;
    // float formats ignore the multiplier). max(|coord|) per channel, with a 1.0 floor.
    private static UvMults ComputeUvMults(List<EncVertex> verts)
    {
        float u1 = 1, v1 = 1, u2 = 1, v2 = 1, u3 = 1, v3 = 1, u4 = 1, v4 = 1;
        foreach (var v in verts)
        {
            u1 = Math.Max(u1, Math.Abs(v.U0)); v1 = Math.Max(v1, Math.Abs(v.V0));
            u2 = Math.Max(u2, Math.Abs(v.U1)); v2 = Math.Max(v2, Math.Abs(v.V1));
            u3 = Math.Max(u3, Math.Abs(v.U2)); v3 = Math.Max(v3, Math.Abs(v.V2));
            u4 = Math.Max(u4, Math.Abs(v.U3)); v4 = Math.Max(v4, Math.Abs(v.V3));
        }

        return new UvMults(u1, v1, u2, v2, u3, v3, u4, v4);
    }

    private static Vector3[] ComputeNormals(GltfPrimitive prim)
    {
        var normals = new Vector3[prim.VertexCount];
        for (var i = 0; i + 2 < prim.Indices.Length; i += 3)
        {
            var a = prim.Indices[i];
            var b = prim.Indices[i + 1];
            var c = prim.Indices[i + 2];
            if (!IsValidTriangle(a, b, c, prim.VertexCount))
            {
                continue;
            }

            var n = Vector3.Cross(prim.Positions[b] - prim.Positions[a], prim.Positions[c] - prim.Positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : new Vector3(0, 1, 0);
        }

        return normals;
    }

    // Per-vertex tangents using Lengyel's method (UV0 gradient). w = handedness (+1/-1).
    private static (float[] X, float[] Y, float[] Z, float[] W) UseTangents(IReadOnlyList<Vector4> tangents)
    {
        var x = new float[tangents.Count];
        var y = new float[tangents.Count];
        var z = new float[tangents.Count];
        var w = new float[tangents.Count];
        for (var i = 0; i < tangents.Count; i++)
        {
            var t = tangents[i];
            x[i] = t.X;
            y[i] = t.Y;
            z[i] = t.Z;
            w[i] = Math.Abs(t.W) > 0.000001f ? t.W : 1f;
        }

        return (x, y, z, w);
    }

    private static Vector4[] ComputeBinormals(Vector3[] normals, float[] tanX, float[] tanY, float[] tanZ, float[] tanW)
    {
        var result = new Vector4[normals.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var normal = normals[i].LengthSquared() > 0.00000001f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
            var tangent = new Vector3(tanX[i], tanY[i], tanZ[i]);
            tangent = tangent.LengthSquared() > 0.00000001f ? Vector3.Normalize(tangent) : Vector3.UnitX;
            var bitangent = Vector3.Cross(normal, tangent) * (tanW[i] < 0f ? -1f : 1f);
            bitangent = bitangent.LengthSquared() > 0.00000001f ? Vector3.Normalize(bitangent) : Vector3.UnitZ;
            result[i] = new Vector4(bitangent, 0f);
        }

        return result;
    }

    // Per-vertex tangents using Lengyel's method (UV0 gradient). w = handedness (+1/-1).
    private static (float[] X, float[] Y, float[] Z, float[] W) ComputeTangents(GltfPrimitive prim, Vector3[] normals)
    {
        var n = prim.VertexCount;
        var tan = new Vector3[n];
        var bitan = new Vector3[n];
        var uv = HasVertexChannel(prim.Uv0, prim.VertexCount) ? prim.Uv0 : null;

        if (uv != null)
        {
            for (var i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                var i0 = prim.Indices[i];
                var i1 = prim.Indices[i + 1];
                var i2 = prim.Indices[i + 2];
                if (!IsValidTriangle(i0, i1, i2, prim.VertexCount))
                {
                    continue;
                }

                var e1 = prim.Positions[i1] - prim.Positions[i0];
                var e2 = prim.Positions[i2] - prim.Positions[i0];
                var du1 = uv[i1] - uv[i0];
                var du2 = uv[i2] - uv[i0];
                var denom = du1.X * du2.Y - du2.X * du1.Y;
                var r = MathF.Abs(denom) > 1e-9f ? 1f / denom : 0f;
                var t = (e1 * du2.Y - e2 * du1.Y) * r;
                var bt = (e2 * du1.X - e1 * du2.X) * r;
                tan[i0] += t; tan[i1] += t; tan[i2] += t;
                bitan[i0] += bt; bitan[i1] += bt; bitan[i2] += bt;
            }
        }

        var x = new float[n];
        var y = new float[n];
        var z = new float[n];
        var w = new float[n];
        for (var i = 0; i < n; i++)
        {
            var nrm = normals[i];
            var t = tan[i];
            t -= nrm * Vector3.Dot(nrm, t); // orthogonalize (Gram-Schmidt)
            if (t.LengthSquared() > 1e-12f)
            {
                t = Vector3.Normalize(t);
            }
            else
            {
                t = new Vector3(1, 0, 0);
            }

            x[i] = t.X; y[i] = t.Y; z[i] = t.Z;
            w[i] = Vector3.Dot(Vector3.Cross(nrm, t), bitan[i]) < 0f ? -1f : 1f;
        }

        return (x, y, z, w);
    }
}
