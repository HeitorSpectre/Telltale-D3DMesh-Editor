using System.Buffers.Binary;
using System.Numerics;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleToolKit;
using TelltaleToolKit.Meta.Serialization;
using TelltaleToolKit.Meta.Serialization.Binary;
using TelltaleToolKit.T3Types.Mathematics;
using TelltaleToolKit.T3Types.Meshes;
using TelltaleToolKit.T3Types.Meshes.T3Types;

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

    public static byte[] ReinsertV25Geometry(
        V25MeshLayout layout,
        GltfModel model,
        ReinsertedTextures textures)
        => V25MeshReinserter.Reinsert(layout, model, textures.PrimitiveSlots);

    public static byte[] ReinsertV25Geometry(
        V25MeshLayout layout,
        GltfModel model,
        IReadOnlyList<IReadOnlyDictionary<string, string>> textureSlotsByPrimitive)
        => V25MeshReinserter.Reinsert(layout, model, textureSlotsByPrimitive);

    public static byte[] ReinsertV25Geometry(
        V25MeshLayout layout,
        GltfModel model,
        IReadOnlyList<IReadOnlyDictionary<string, string>> textureSlotsByPrimitive,
        V25MeshLayout? sourceMaterialLayout)
        => V25MeshReinserter.Reinsert(layout, model, textureSlotsByPrimitive, sourceMaterialLayout);

    public static byte[] ReinsertV25Geometry(
        V25MeshLayout layout,
        GltfModel model)
        => V25MeshReinserter.Reinsert(layout, model);

    public static bool CanAddV25Materials(V25MeshLayout layout)
        => V25MeshReinserter.CanAddMaterials(layout);

    // MCSM Season 2 (.d3dmesh v45): same call shape as the other games; the heavy lifting lives in
    // V45MeshReinserter below in this file.
    public static byte[] ReinsertV45Geometry(byte[] templateBytes, GltfModel model)
        => V45MeshReinserter.Reinsert(templateBytes, model);

    public static V45MeshReinserter.ReinsertResult ReinsertV45GeometryWithAssignments(
        byte[] templateBytes,
        GltfModel model,
        IReadOnlyList<string?> batchTemplateDiffuse)
        => V45MeshReinserter.ReinsertWithAssignments(templateBytes, model, batchTemplateDiffuse);

    public static byte[] BuildInvisibleV45MeshBytes(string meshPath)
        => V45MeshReinserter.BuildInvisible(File.ReadAllBytes(meshPath));

    // Batman: The Telltale Series (.d3dmesh v46). Unlike v45, Batman stores the
    // lower LODs after LOD0 in the same vertex/index buffers, so its writer keeps
    // those suffixes intact and rebuilds only the editable LOD0 prefix.
    public static byte[] ReinsertV46Geometry(byte[] templateBytes, GltfModel model)
        => V45MeshReinserter.ReinsertV46(templateBytes, model);

    public static V45MeshReinserter.ReinsertResult ReinsertV46GeometryWithAssignments(
        byte[] templateBytes,
        GltfModel model,
        IReadOnlyList<string?> batchTemplateDiffuse)
        => V45MeshReinserter.ReinsertV46WithAssignments(templateBytes, model, batchTemplateDiffuse);

    public static byte[] BuildInvisibleV46MeshBytes(string meshPath)
        => V45MeshReinserter.BuildInvisibleV46(File.ReadAllBytes(meshPath));

    public static V25MeshLayout? TryFindV25SourceMaterialLayout(
        GltfModel model,
        string templateMeshPath,
        string? modelPath = null)
        => V25MeshReinserter.TryFindSourceMaterialLayout(model, templateMeshPath, modelPath);

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

        var preparedPrimitives = PreparePrimitives(layout, model, textureSlotsByPrimitive, referenceSkeleton, gameConfig);
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

            // When the GLB carries no per-vertex colors (the exporter drops an all-neutral COLOR_0),
            // restore the template submesh's original colors for games that store baked data there
            // (TFTB E3 env props). Only valid when the vertex count is unchanged, so the per-index
            // mapping holds; full swaps with a different count keep the neutral-white fallback.
            var templateSubmeshIndex = ResolveTemplateSubmeshIndex(layout, prim, k);
            var preserveTemplateVertexData = gameConfig?.PreserveTemplateVertexDataOnReimport == true;
            var copyTemplateColors = preserveTemplateVertexData &&
                !HasVertexChannel(prim.Color0, prim.VertexCount) &&
                TemplateSubmeshVertexCount(layout, templateSubmeshIndex) == prim.VertexCount;

            var normals = HasVertexChannel(prim.Normals, prim.VertexCount) ? prim.Normals! : ComputeNormals(prim);
            // Keep the GLB's stored tangent.w (including 0) on games whose meshes ship a zero handedness
            // (TFTB E3); other games force a unit sign so a degenerate w never zeroes the bitangent.
            var (tanX, tanY, tanZ, tanW) = HasVertexChannel(prim.Tangents, prim.VertexCount)
                ? UseTangents(prim.Tangents!, preserveStoredW: preserveTemplateVertexData)
                : ComputeTangents(prim, normals);
            var binormals = HasVertexChannel(prim.Binormals, prim.VertexCount)
                ? prim.Binormals!
                : ComputeBinormals(normals, tanX, tanY, tanZ, tanW);
            var skinning = BuildSkinningMap(layout, model, prim, k, referenceSkeleton, prepared.BonePaletteIndex, gameConfig);

            for (var i = 0; i < prim.VertexCount; i++)
            {
                var pos = prim.Positions[i];
                var nrm = normals[i];
                var uv0 = Uv(prim.Uv0, prim.VertexCount, i);
                var uv1 = HasVertexChannel(prim.Uv1, prim.VertexCount) ? prim.Uv1![i] : uv0;
                var uv2 = HasVertexChannel(prim.Uv2, prim.VertexCount) ? prim.Uv2![i] : uv1;
                var uv3 = HasVertexChannel(prim.Uv3, prim.VertexCount) ? prim.Uv3![i] : uv2;
                var col = HasVertexChannel(prim.Color0, prim.VertexCount)
                    ? prim.Color0![i]
                    : copyTemplateColors && TryReadTemplateVertexColor(layout, templateSubmeshIndex, i) is { } templateColor
                        ? templateColor
                        : new Vector4(1, 1, 1, 1);
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
            new(layout.SubmeshTableOffset, layout.SubmeshTableLength, BuildSubmeshTable(layout, subInfo, preparedTextureSlotsByPrimitive, gameConfig ?? GameConfig.Current, verts)),
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
            var textureEntryBounds = ComputeTextureEntryBounds(layout, subInfo, preparedTextureSlotsByPrimitive, verts);
            patches.Add(new RegionPatch(
                layout.TextureGroupBlockOffset,
                layout.TextureGroupBlockLength,
                BuildTextureGroupBlock(layout, texturePlansBySlot, textureEntryBounds)));
        }

        var paletteBoneBounds = ComputePaletteBoneBounds(layout, subInfo, verts);
        if (paletteBoneBounds.Count > 0 || layout.BonePalettes.Count > layout.OriginalBonePaletteCount)
        {
            patches.Add(new RegionPatch(
                layout.BonePaletteBlockOffset,
                layout.BonePaletteBlockLength,
                BuildBonePaletteBlock(layout, paletteBoneBounds)));
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
        SkeletonData? referenceSkeleton,
        GameConfig? gameConfig)
    {
        var result = new List<PreparedPrimitive>();
        int? staticImportPaletteIndex = null;
        for (var primitiveIndex = 0; primitiveIndex < model.Primitives.Count; primitiveIndex++)
        {
            var primitive = model.Primitives[primitiveIndex];
            var textureSlots = textureSlotsByPrimitive is not null && primitiveIndex < textureSlotsByPrimitive.Count
                ? textureSlotsByPrimitive[primitiveIndex]
                : null;

            // A static prop can still carry a (trivial) bone palette; the GLB exporter then writes
            // JOINTS_0/WEIGHTS_0 that all point at palette bone 0. Without any skeleton to resolve
            // those joint indices to Telltale hashes (no reference .skl, no GLB skeleton/joints), the
            // skinning analysis used to throw and the whole reinsert failed. Treat these as static and
            // bind them to the existing palette instead. Real skinned reinserts always provide a
            // skeleton, so their path is unchanged.
            if (!HasUsableSkin(primitive) || layout.BonePalettes.Count == 0 || !HasResolvableSkeleton(model, referenceSkeleton))
            {
                var paletteIndex = layout.BonePalettes.Count > 0
                    ? (staticImportPaletteIndex ??= ResolveStaticImportPaletteIndex(layout, referenceSkeleton, gameConfig))
                    : primitive.BonePaletteIndex;
                result.Add(new PreparedPrimitive(primitive, textureSlots, paletteIndex));
                continue;
            }

            var usage = AnalyzeJointUsage(primitive, model, referenceSkeleton, gameConfig);
            if (usage.UnresolvedJoints.Count > 0)
            {
                throw new InvalidOperationException(
                    "Could not resolve GLB joint(s) to Telltale bone hashes: " +
                    string.Join(", ", usage.UnresolvedJoints.Take(8)) +
                    (usage.UnresolvedJoints.Count > 8 ? "..." : ""));
            }

            if (usage.AllHashes.Count == 0)
            {
                var paletteIndex = staticImportPaletteIndex ??= ResolveStaticImportPaletteIndex(layout, referenceSkeleton, gameConfig);
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

            var maxPaletteBones = MaxPaletteBonesForLayout(layout, gameConfig);
            if (usage.AllHashes.Count <= maxPaletteBones)
            {
                var customPalette = AddCustomBonePalette(layout, usage.AllHashes, maxPaletteBones);
                result.Add(new PreparedPrimitive(primitive, textureSlots, customPalette));
                continue;
            }

            var slices = SplitPrimitiveByBonePalette(layout, primitive, usage, textureSlots, preferredPalette, maxPaletteBones);
            result.AddRange(slices);
        }

        return result;
    }

    private static bool HasUsableSkin(GltfPrimitive primitive)
        => primitive.Joints0 is { } joints &&
           primitive.Weights0 is { } weights &&
           joints.Length >= primitive.VertexCount * 4 &&
           weights.Length == primitive.VertexCount;

    // True only when there is some skeleton to resolve GLB joint indices to Telltale bone hashes:
    // an explicit reference .skl, a skeleton embedded in the GLB, or named joints. Without any of
    // these the joints cannot be skinned and the geometry must be bound statically.
    private static bool HasResolvableSkeleton(GltfModel model, SkeletonData? referenceSkeleton)
        => referenceSkeleton is { Bones.Count: > 0 } ||
           model.Skeleton is { Bones.Count: > 0 } ||
           model.Joints.Count > 0;

    private static PrimitiveJointUsage AnalyzeJointUsage(
        GltfPrimitive primitive,
        GltfModel model,
        SkeletonData? referenceSkeleton,
        GameConfig? gameConfig)
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
                if (TryResolveJointHash(gltfJoint, model, referenceSkeleton, gameConfig, out var hash) && hash != 0)
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

        // A combined import that pulls bones from many source parts (Combine Parts reimport) would
        // otherwise create one custom palette per primitive. The game's runtime supports only a few bone
        // palettes per mesh — the original Tales from the Borderlands characters never use more than three,
        // and a 7-palette result crashed it on load. So pack the new bones into an existing custom palette
        // whenever their union still fits the per-palette bone cap, instead of always appending a new one.
        // Skinning stays correct because each primitive's bone indices are resolved by hash against the
        // final palette (computed after all palettes are settled), and the original template palettes are
        // left untouched so meshes that already cover their bones are byte-unaffected.
        for (var i = layout.OriginalBonePaletteCount; i < layout.BonePalettes.Count; i++)
        {
            var union = new HashSet<ulong>(layout.BonePalettes[i]);
            union.UnionWith(paletteSet);
            if (union.Count <= maxPaletteBones)
            {
                layout.BonePalettes[i] = union.OrderBy(static hash => hash).ToArray();
                return i;
            }
        }

        layout.BonePalettes.Add(palette);
        return layout.BonePalettes.Count - 1;
    }

    private static int MaxPaletteBonesForLayout(D3DMeshLayout layout, GameConfig? gameConfig)
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

        var encodingLimit = maxIndex + 1;
        return gameConfig?.MaxSkinnedPaletteBonesOnReimport is { } profileLimit && profileLimit > 0
            ? Math.Min(encodingLimit, profileLimit)
            : encodingLimit;
    }

    private static int? ResolveStaticImportPaletteIndex(D3DMeshLayout layout, SkeletonData? referenceSkeleton, GameConfig? gameConfig)
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

            return AddCustomBonePalette(layout, [rootHash], MaxPaletteBonesForLayout(layout, gameConfig));
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
        int preferredPalette,
        int maxPaletteBones)
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
                if (triangleHashes.Count > maxPaletteBones)
                {
                    failedTriangles++;
                    continue;
                }

                paletteIndex = AddCustomBonePalette(layout, triangleHashes, maxPaletteBones);
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
        int? preparedPaletteIndex,
        GameConfig? gameConfig)
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
            var localPaletteIndex = ResolvePaletteLocalIndex(gltfJoint, model, referenceSkeleton, gameConfig, palette);
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

    // Vertex count of a template submesh (raw inclusive [VMin, VMax] range), or -1 if out of range.
    private static int TemplateSubmeshVertexCount(D3DMeshLayout layout, int templateSubmeshIndex)
    {
        if (templateSubmeshIndex < 0 || templateSubmeshIndex >= layout.Submeshes.Count)
        {
            return -1;
        }

        var sub = layout.Submeshes[templateSubmeshIndex];
        return sub.VertexMax - sub.VertexMin + 1;
    }

    // Reads the original per-vertex color of a template submesh's local vertex straight from the
    // template's interleaved vertex buffer, so a count-preserving reimport can restore baked vertex
    // colors the GLB no longer carries. Mirrors D3DMeshParser.ReadColor for formats 1 (4 floats) and
    // 3 (4 bytes). Returns null when the template has no color attribute or the index is out of range.
    private static Vector4? TryReadTemplateVertexColor(D3DMeshLayout layout, int templateSubmeshIndex, int localVertexIndex)
    {
        if (layout.VertexBuffers.Count == 0 ||
            templateSubmeshIndex < 0 ||
            templateSubmeshIndex >= layout.Submeshes.Count)
        {
            return null;
        }

        var buffer = layout.VertexBuffers[0];
        var color = buffer.Attributes.Colors;
        if (color.Format is not (1 or 3))
        {
            return null;
        }

        var bufferIndex = layout.Submeshes[templateSubmeshIndex].VertexMin + localVertexIndex;
        if (bufferIndex < 0 || bufferIndex >= buffer.VertexCount)
        {
            return null;
        }

        var offset = buffer.DataOffset + bufferIndex * buffer.VertexStride + (int)color.Offset;
        var span = layout.Original.AsSpan();
        if (offset < 0 || offset + (color.Format == 1 ? 16 : 4) > span.Length)
        {
            return null;
        }

        return color.Format == 1
            ? new Vector4(
                BinaryPrimitives.ReadSingleLittleEndian(span[offset..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(offset + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(offset + 8)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(offset + 12)..]))
            : new Vector4(span[offset] / 255f, span[offset + 1] / 255f, span[offset + 2] / 255f, span[offset + 3] / 255f);
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
        GameConfig? gameConfig,
        IReadOnlyList<ulong> palette)
    {
        if (TryResolveJointHash(gltfJoint, model, referenceSkeleton, gameConfig, out var hash))
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
        GameConfig? gameConfig,
        out ulong hash)
    {
        if ((gameConfig?.IsOriginalTalesFromTheBorderlandsPc == true ||
             gameConfig?.Id == GameId.GameOfThrones) &&
            referenceSkeleton is not null)
        {
            if (TryResolveTftbJointHash(gltfJoint, model, referenceSkeleton, gameConfig, out hash))
            {
                return true;
            }

            hash = 0;
            return false;
        }

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

    private static bool TryResolveTftbJointHash(
        int gltfJoint,
        GltfModel model,
        SkeletonData referenceSkeleton,
        GameConfig? gameConfig,
        out ulong hash)
    {
        if (TryResolveTftbJointHashDirect(gltfJoint, model, referenceSkeleton, gameConfig, out hash))
        {
            return true;
        }

        if (model.Skeleton is null ||
            gltfJoint < 0 ||
            gltfJoint >= model.Skeleton.Bones.Count)
        {
            return false;
        }

        var visited = new HashSet<int>();
        var parent = model.Skeleton.Bones[gltfJoint].ParentIndex;
        while (parent >= 0 && parent < model.Skeleton.Bones.Count && visited.Add(parent))
        {
            if (TryResolveTftbJointHashDirect(parent, model, referenceSkeleton, gameConfig, out hash))
            {
                return true;
            }

            parent = model.Skeleton.Bones[parent].ParentIndex;
        }

        return false;
    }

    private static bool TryResolveTftbJointHashDirect(
        int gltfJoint,
        GltfModel model,
        SkeletonData referenceSkeleton,
        GameConfig? gameConfig,
        out ulong hash)
    {
        if (gltfJoint >= 0 && gltfJoint < model.Joints.Count)
        {
            var joint = model.Joints[gltfJoint];
            if (joint.Hash is { } explicitHash &&
                referenceSkeleton.Bones.Any(bone => bone.Hash == explicitHash))
            {
                hash = explicitHash;
                return true;
            }

            if (TryResolveReferenceHashByName(joint.Name, referenceSkeleton, gameConfig, out hash))
            {
                return true;
            }
        }

        if (model.Skeleton is not null &&
            gltfJoint >= 0 &&
            gltfJoint < model.Skeleton.Bones.Count)
        {
            var bone = model.Skeleton.Bones[gltfJoint];
            if (referenceSkeleton.Bones.Any(reference => reference.Hash == bone.Hash))
            {
                hash = bone.Hash;
                return true;
            }

            if (TryResolveReferenceHashByName(bone.Name, referenceSkeleton, gameConfig, out hash))
            {
                return true;
            }
        }

        hash = 0;
        return false;
    }

    private static bool TryResolveReferenceHashByName(
        string? name,
        SkeletonData referenceSkeleton,
        GameConfig? gameConfig,
        out ulong hash)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var exact = referenceSkeleton.Bones.FirstOrDefault(bone =>
                string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                hash = exact.Hash;
                return true;
            }

            if (gameConfig?.DisableCharacterSpecificFacialRetargetOnReimport != true &&
                BoneNameAliases.TryGetCharacterSpecificAlias(name, out var alias))
            {
                var aliased = referenceSkeleton.Bones.FirstOrDefault(bone =>
                    BoneNameAliases.TryGetCharacterSpecificAlias(bone.Name, out var referenceAlias) &&
                    string.Equals(referenceAlias, alias, StringComparison.OrdinalIgnoreCase));
                if (aliased is not null)
                {
                    hash = aliased.Hash;
                    return true;
                }
            }
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
        GameConfig gameConfig,
        IReadOnlyList<EncVertex>? verts = null)
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

            // The v17/18 submesh entry carries its own bounds right after polyCount (min vec3,
            // max vec3, u32 kept, center vec3, radius = half diagonal). Keeping the template's box
            // leaves it around the OLD model's part; the eye quad of a swapped character can land
            // fully outside it and get culled in-game (invisible pupils). v13/14 entries keep the
            // template bytes so those games stay byte-identical.
            if (layout.Version is 17 or 18 &&
                verts is not null &&
                TryComputeRangeBox(verts, info.VMin, info.VMax, out var submeshBox))
            {
                WriteBoundsBlock(bytes, template.PolygonCountFieldOffset - template.EntryOffset + 4, submeshBox);
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
                        !TemplateProvidesSlot(layout, template, slotIndex) ||
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
                     "emissive",
                     "alternate_bump",
                     "occlusion",
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

                if (!TemplateProvidesSlot(layout, template, slotIndex))
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

    private static byte[] BuildTextureGroupBlock(
        D3DMeshLayout layout,
        IReadOnlyDictionary<string, List<TextureGroupEntryPlan>> textureNamesBySlot,
        IReadOnlyDictionary<string, BoneBox>? textureEntryBounds = null)
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

                    // v17/18 texture entries also carry the bounds of the geometry using the
                    // texture (min/max at +24, u32 kept, center+radius), same culling concern as
                    // the submesh bounds.
                    if (textureEntryBounds is not null &&
                        textureEntryBounds.TryGetValue(slot + "|" + textureName, out var box) &&
                        bytes.Length >= 68)
                    {
                        WriteBoundsBlock(bytes, 24, box);
                    }

                    ms.Write(bytes, 0, bytes.Length);
                }

                continue;
            }

            var originalGroup = layout.TextureGroups[groupIndex];
            ms.Write(layout.Original, originalGroup.GroupOffset, originalGroup.GroupLength);
        }

        var result = ms.ToArray();
        // The texture-group block's leading u32 is NOT the raw block length. In TFTB E3 (v14) the
        // original stores (blockLength + 4); writing the raw length corrupts the value the game reads
        // to locate the following blocks (uvScales/faces/vertices) and crashes on load. Preserve the
        // original leading value, shifted only by the change in block length, so the per-game
        // convention is kept whether or not the block size actually changed.
        var originalTextureLeading = BinaryPrimitives.ReadUInt32LittleEndian(
            layout.Original.AsSpan(layout.TextureGroupBlockOffset, 4));
        var textureLeading = unchecked((uint)(originalTextureLeading + (result.Length - layout.TextureGroupBlockLength)));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), textureLeading);
        return result;
    }

    private readonly record struct BoneBox(
        float MinX, float MinY, float MinZ,
        float MaxX, float MaxY, float MaxZ)
    {
        public BoneBox Include(float x, float y, float z) => new(
            Math.Min(MinX, x), Math.Min(MinY, y), Math.Min(MinZ, z),
            Math.Max(MaxX, x), Math.Max(MaxY, y), Math.Max(MaxZ, z));

        public static BoneBox Empty => new(
            float.MaxValue, float.MaxValue, float.MaxValue,
            float.MinValue, float.MinValue, float.MinValue);
    }

    // Per-bone bounds of the vertices weighted to each palette slot. The game's 56-byte palette
    // entries carry this AABB + center + radius (half diagonal) for the original geometry; keeping
    // the template's boxes after a reimport leaves them around the OLD model's body parts. Only the
    // v17/18 layout is rewritten — the v13/14 games run fine with the template entries untouched and
    // their outputs must stay byte-identical.
    private static Dictionary<(int Palette, int LocalBone), BoneBox> ComputePaletteBoneBounds(
        D3DMeshLayout layout,
        IReadOnlyList<SubmeshPatchInfo> subInfo,
        IReadOnlyList<EncVertex> verts)
    {
        var result = new Dictionary<(int, int), BoneBox>();
        if (layout.Version is not (17 or 18))
        {
            return result;
        }

        foreach (var info in subInfo)
        {
            if (info.BonePaletteIndex is not { } paletteIndex ||
                paletteIndex < 0 ||
                paletteIndex >= layout.BonePalettes.Count)
            {
                continue;
            }

            for (var i = info.VMin; i <= info.VMax && i < verts.Count; i++)
            {
                var v = verts[i];
                foreach (var (bone, weight) in new[] { (v.Bone0, v.W0), (v.Bone1, v.W1), (v.Bone2, v.W2), (v.Bone3, v.W3) })
                {
                    if (weight <= 0.000001f)
                    {
                        continue;
                    }

                    var local = Formats.Mesh.BoneIndexConvention.ToPaletteIndex(bone, layout.Version);
                    if (local < 0 || local >= layout.BonePalettes[paletteIndex].Length)
                    {
                        continue;
                    }

                    var key = (paletteIndex, local);
                    var box = result.TryGetValue(key, out var existing) ? existing : BoneBox.Empty;
                    result[key] = box.Include(v.X, v.Y, v.Z);
                }
            }
        }

        return result;
    }

    private static bool TryComputeRangeBox(IReadOnlyList<EncVertex> verts, int from, int toInclusive, out BoneBox box)
    {
        box = BoneBox.Empty;
        var any = false;
        for (var i = Math.Max(0, from); i <= toInclusive && i < verts.Count; i++)
        {
            box = box.Include(verts[i].X, verts[i].Y, verts[i].Z);
            any = true;
        }

        return any;
    }

    // Writes the recurring Telltale bounds pattern (min vec3, max vec3, u32 kept untouched,
    // center vec3, radius = half diagonal) used by submesh entries, texture entries and bone
    // palette entries alike.
    private static void WriteBoundsBlock(byte[] bytes, int offset, BoneBox box)
    {
        var span = bytes.AsSpan(offset);
        BinaryPrimitives.WriteSingleLittleEndian(span, box.MinX);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], box.MinY);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], box.MinZ);
        BinaryPrimitives.WriteSingleLittleEndian(span[12..], box.MaxX);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], box.MaxY);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..], box.MaxZ);
        BinaryPrimitives.WriteSingleLittleEndian(span[28..], (box.MinX + box.MaxX) * 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(span[32..], (box.MinY + box.MaxY) * 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(span[36..], (box.MinZ + box.MaxZ) * 0.5f);
        var dx = box.MaxX - box.MinX;
        var dy = box.MaxY - box.MinY;
        var dz = box.MaxZ - box.MinZ;
        BinaryPrimitives.WriteSingleLittleEndian(span[40..], 0.5f * MathF.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    // Bounds of the vertices using each (slot, texture) pair, for the texture-group entries of the
    // v17/18 layout. Same culling concern as the submesh bounds.
    private static Dictionary<string, BoneBox> ComputeTextureEntryBounds(
        D3DMeshLayout layout,
        IReadOnlyList<SubmeshPatchInfo> subInfo,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        IReadOnlyList<EncVertex> verts)
    {
        var result = new Dictionary<string, BoneBox>(StringComparer.OrdinalIgnoreCase);
        if (layout.Version is not (17 or 18) || textureSlotsByPrimitive is null)
        {
            return result;
        }

        for (var i = 0; i < subInfo.Count && i < textureSlotsByPrimitive.Count; i++)
        {
            if (!TryComputeRangeBox(verts, subInfo[i].VMin, subInfo[i].VMax, out var box))
            {
                continue;
            }

            foreach (var (slot, textureName) in textureSlotsByPrimitive[i])
            {
                var key = slot + "|" + textureName;
                result[key] = result.TryGetValue(key, out var existing)
                    ? existing.Include(box.MinX, box.MinY, box.MinZ).Include(box.MaxX, box.MaxY, box.MaxZ)
                    : box;
            }
        }

        return result;
    }

    private static void WriteBoneBoxIntoEntry(byte[] entry, BoneBox box, bool rewriteCenterAndRadius)
    {
        // Entry layout after the 8-byte hash: min vec3, max vec3, int (kept), center vec3,
        // radius (half diagonal), int (kept).
        var span = entry.AsSpan();
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], box.MinX);
        BinaryPrimitives.WriteSingleLittleEndian(span[12..], box.MinY);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], box.MinZ);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..], box.MaxX);
        BinaryPrimitives.WriteSingleLittleEndian(span[24..], box.MaxY);
        BinaryPrimitives.WriteSingleLittleEndian(span[28..], box.MaxZ);
        if (!rewriteCenterAndRadius)
        {
            return;
        }

        BinaryPrimitives.WriteSingleLittleEndian(span[36..], (box.MinX + box.MaxX) * 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(span[40..], (box.MinY + box.MaxY) * 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(span[44..], (box.MinZ + box.MaxZ) * 0.5f);
        var dx = box.MaxX - box.MinX;
        var dy = box.MaxY - box.MinY;
        var dz = box.MaxZ - box.MinZ;
        BinaryPrimitives.WriteSingleLittleEndian(span[48..], 0.5f * MathF.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    private static byte[] BuildBonePaletteBlock(
        D3DMeshLayout layout,
        IReadOnlyDictionary<(int Palette, int LocalBone), BoneBox> paletteBoneBounds)
    {
        using var ms = new MemoryStream();
        var originalBlock = layout.Original.AsSpan(layout.BonePaletteBlockOffset, layout.BonePaletteBlockLength).ToArray();

        // Walk the original palettes and refresh each entry's bounds for bones the new geometry uses;
        // untouched entries keep their original bytes.
        var pos = 8;
        for (var paletteIndex = 0; paletteIndex < layout.OriginalBonePaletteCount && pos + 4 <= originalBlock.Length; paletteIndex++)
        {
            var boneCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(originalBlock.AsSpan(pos, 4));
            pos += 4;
            for (var bone = 0; bone < boneCount && pos + layout.BonePaletteEntrySize <= originalBlock.Length; bone++)
            {
                if (paletteBoneBounds.TryGetValue((paletteIndex, bone), out var box))
                {
                    var entry = originalBlock.AsSpan(pos, layout.BonePaletteEntrySize).ToArray();
                    WriteBoneBoxIntoEntry(entry, box, rewriteCenterAndRadius: false);
                    entry.CopyTo(originalBlock, pos);
                }

                pos += layout.BonePaletteEntrySize;
            }
        }

        ms.Write(originalBlock, 0, originalBlock.Length);

        for (var paletteIndex = layout.OriginalBonePaletteCount; paletteIndex < layout.BonePalettes.Count; paletteIndex++)
        {
            var palette = layout.BonePalettes[paletteIndex];
            ms.Write(U32(palette.Length));
            for (var bone = 0; bone < palette.Length; bone++)
            {
                var entry = layout.BonePaletteEntryTemplate.Length == layout.BonePaletteEntrySize
                    ? layout.BonePaletteEntryTemplate.ToArray()
                    : new byte[layout.BonePaletteEntrySize];
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0, 4), unchecked((uint)palette[bone]));
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4, 4), unchecked((uint)(palette[bone] >> 32)));
                if (paletteBoneBounds.TryGetValue((paletteIndex, bone), out var box))
                {
                    WriteBoneBoxIntoEntry(entry, box, rewriteCenterAndRadius: true);
                }

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
        return TemplateProvidesSlot(layout, template, slotIndex);
    }

    private static bool TemplateProvidesSlot(D3DMeshLayout layout, SubmeshLayout template, int slotIndex)
    {
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
        var b = layout.Original.AsSpan(layout.UvScalesOffset, layout.UvScalesLength).ToArray();
        if (layout.Version is 17 or 18)
        {
            WriteUvScaleValue(b, 0, m.Uv1X);
            WriteUvScaleValue(b, 4, m.Uv1Y);
            WriteUvScaleValue(b, 16, m.Uv2X);
            WriteUvScaleValue(b, 20, m.Uv2Y);
            WriteUvScaleValue(b, 24, m.Uv3X);
            WriteUvScaleValue(b, 28, m.Uv3Y);
            WriteUvScaleValue(b, 32, m.Uv4X);
            WriteUvScaleValue(b, 36, m.Uv4Y);
            return b;
        }

        var baseOffset = V13UvScaleBaseOffset(layout);
        WriteUvScaleValue(b, baseOffset, m.Uv1X);
        WriteUvScaleValue(b, baseOffset + 4, m.Uv1Y);
        WriteUvScaleValue(b, baseOffset + 8, m.Uv4X);
        WriteUvScaleValue(b, baseOffset + 12, m.Uv4Y);
        WriteUvScaleValue(b, baseOffset + 16, m.Uv2X);
        WriteUvScaleValue(b, baseOffset + 20, m.Uv2Y);
        WriteUvScaleValue(b, baseOffset + 24, m.Uv3X);
        WriteUvScaleValue(b, baseOffset + 28, m.Uv3Y);
        return b;
    }

    private static void WriteUvScaleValue(byte[] bytes, int offset, float value)
    {
        if (offset >= 0 && offset + 4 <= bytes.Length)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        }
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
        var baseOffset = layout.Version is 17 or 18 ? 0 : V13UvScaleBaseOffset(layout);
        var uv1X = BinaryPrimitives.ReadSingleLittleEndian(span[baseOffset..]);
        var uv1Y = BinaryPrimitives.ReadSingleLittleEndian(span[(baseOffset + 4)..]);
        var uv2Offset = baseOffset + 16;
        var uv3Offset = baseOffset + 24;
        var uv4Offset = layout.Version is 17 or 18 ? 32 : baseOffset + 8;
        var uv2X = BinaryPrimitives.ReadSingleLittleEndian(span[uv2Offset..]);
        var uv2Y = BinaryPrimitives.ReadSingleLittleEndian(span[(uv2Offset + 4)..]);
        var uv3X = BinaryPrimitives.ReadSingleLittleEndian(span[uv3Offset..]);
        var uv3Y = BinaryPrimitives.ReadSingleLittleEndian(span[(uv3Offset + 4)..]);
        var uv4X = BinaryPrimitives.ReadSingleLittleEndian(span[uv4Offset..]);
        var uv4Y = BinaryPrimitives.ReadSingleLittleEndian(span[(uv4Offset + 4)..]);
        return new UvMults(uv1X, uv1Y, uv2X, uv2Y, uv3X, uv3Y, uv4X, uv4Y);
    }

    private static int V13UvScaleBaseOffset(D3DMeshLayout layout)
    {
        var floatCount = Math.Max(8, layout.UvScalesLength / 4);
        return Math.Max(0, floatCount - 8) * 4;
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
    private static (float[] X, float[] Y, float[] Z, float[] W) UseTangents(IReadOnlyList<Vector4> tangents, bool preserveStoredW = false)
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
            // preserveStoredW keeps the original handedness byte-for-byte (TFTB E3 ships tangent.w = 0);
            // otherwise a degenerate zero is promoted to +1 so the derived bitangent is never zeroed.
            w[i] = preserveStoredW || Math.Abs(t.W) > 0.000001f ? t.W : 1f;
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

// ---- V25 mesh reinsertion moved from V25MeshReinserter.cs ----


// Static-mesh reinserter for The Walking Dead: Michonne (.d3dmesh V25), written for the V25 engine
// layout from scratch (it shares nothing with the V13/17/18 path or the Back to the Future code).
//
// Strategy: template-patch. The original .d3dmesh is kept byte-for-byte except for the geometry-
// dependent fields and the async buffer payloads. The new geometry comes from a GLB whose primitives
// map 1:1 onto the template's LOD0 batches (so the property-set materials, LOD/material/bone tables
// and every header stay intact and correctly sized). The encoder is driven entirely by the vertex
// state's attribute table (mAttribute/mFormat/mBufferIndex/mBufferOffset): each stream is written at
// the exact buffer/offset/format the file declares, so any attribute layout is handled faithfully.
//
// Only pure static meshes are accepted; skinned meshes are refused by V25MeshLayout with a clear
// reason (skinned support is a deliberately separate later phase).
public static class V25MeshReinserter
{
    public static byte[] Reinsert(V25MeshLayout layout, GltfModel model)
        => Reinsert(layout, model, textureSlotsByPrimitive: null, sourceMaterialLayout: null);

    public static byte[] Reinsert(
        V25MeshLayout layout,
        GltfModel model,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        V25MeshLayout? sourceMaterialLayout = null)
    {
        // Static meshes and skinned (character) meshes are both reinsertable now. Only the genuine
        // can't-build cases (no materials / no LOD0 batches) keep a reject reason without skinning data.
        if (layout.RejectReason is not null && !layout.IsSkinned)
        {
            throw new InvalidOperationException(layout.RejectReason);
        }

        if (model.Primitives.Count == 0)
        {
            throw new InvalidOperationException("GLB has no mesh primitives to reinsert.");
        }

        if (layout.Batches.Count == 0)
        {
            throw new InvalidOperationException("The template mesh has no batch table to build from.");
        }

        if (layout.LodCount != 1 && model.Primitives.Count != layout.Batches.Count)
        {
            // Resizing the batch table is only wired for the single-LOD layout every shipped V25 mesh
            // uses. A multi-LOD template with a different part count would need each LOD rebuilt.
            throw new InvalidOperationException(
                "This V25 mesh has multiple LODs; changing the number of parts is not supported for it. " +
                "Reimport a model with the same number of parts as the original.");
        }

        // Build one global vertex/face stream by concatenating ALL the GLB primitives. Each part becomes
        // one batch that owns a contiguous vertex range and a contiguous index range, exactly what the
        // engine's mMinVertIndex/mMaxVertIndex + mStartIndex/mNumPrimitives expect. The part count may
        // differ from the template's: the batch table is rebuilt below to match.
        var partCount = model.Primitives.Count;
        var verts = new List<V25Vertex>();
        var faces = new List<int>();
        var batchRanges = new (int VMin, int VMax, int FaceStart, int PolyCount)[partCount];

        // Skinning: normally the template's bone palettes are reused as-is (the rebuilt .skl shares their
        // bones). But a foreign character carries its own rig, and the template palette is only a SUBSET of
        // the skeleton tailored to the original geometry — the imported vertices reference bones it doesn't
        // contain, which would collapse them to the root (hands moving with the legs). When that happens,
        // rebuild a single palette from the bones the model actually uses so every blend index resolves.
        var skinned = layout.IsSkinned && layout.BonePalettes.Count > 0;
        var usedHashes = skinned ? CollectUsedBoneHashes(model) : [];
        // Covered = every used bone exists in the UNION of the template's palettes (multi-palette meshes
        // split their bones across palettes, so a single-palette test would wrongly trigger a rebuild on a
        // same-model reinsert). Only rebuild for a foreign rig, and only when the template is single-palette
        // (collapsing several palettes into one would break per-submesh palette assignment).
        var templateBoneUnion = skinned
            ? layout.BonePalettes.SelectMany(p => p.BoneHashes).ToHashSet()
            : [];
        var templateCovers = skinned && usedHashes.Count > 0 && usedHashes.All(templateBoneUnion.Contains);
        var rebuildPalette = skinned && layout.BonePalettes.Count == 1 &&
                             usedHashes.Count is > 0 and <= MaxPaletteBones && !templateCovers;
        var newPaletteBones = rebuildPalette ? usedHashes.ToArray() : [];
        var paletteMaps = skinned
            ? (rebuildPalette
                ? [BuildHashIndexMap(newPaletteBones)]
                : layout.BonePalettes.Select(BuildPaletteHashIndex).ToArray())
            : [];
        var unresolvedJoints = 0;

        for (var k = 0; k < partCount; k++)
        {
            var prim = model.Primitives[k];
            var vertexStart = verts.Count;
            var faceStartTri = faces.Count / 3;
            var paletteForPrim = skinned ? (rebuildPalette ? 0 : PaletteForPrimitive(layout, k, partCount)) : -1;

            var normals = HasChannel(prim.Normals, prim.VertexCount) ? prim.Normals! : ComputeNormals(prim);
            var tangents = HasChannel(prim.Tangents, prim.VertexCount) ? prim.Tangents! : ComputeTangents(prim, normals);
            var binormals = HasChannel(prim.Binormals, prim.VertexCount)
                ? prim.Binormals!
                : ComputeBinormals(normals, tangents);

            for (var i = 0; i < prim.VertexCount; i++)
            {
                var vertex = new V25Vertex
                {
                    Position = prim.Positions[i],
                    Normal = normals[i],
                    Tangent = tangents[i],
                    Binormal = binormals[i],
                    Color = HasChannel(prim.Color0, prim.VertexCount) ? prim.Color0![i] : new Vector4(1, 1, 1, 1),
                    Uv =
                    [
                        Uv(prim.Uv0, i),
                        Uv(prim.Uv1, i),
                        Uv(prim.Uv2, i),
                        Uv(prim.Uv3, i),
                        Uv(prim.Uv4, i),
                        Uv(prim.Uv5, i),
                    ],
                };

                if (paletteForPrim >= 0 && prim.Joints0 is not null && prim.Weights0 is not null)
                {
                    AssignSkinning(vertex, model, prim, i, paletteMaps[paletteForPrim], ref unresolvedJoints);
                }

                verts.Add(vertex);
            }

            for (var i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                int a = prim.Indices[i], b = prim.Indices[i + 1], c = prim.Indices[i + 2];
                if (a == b || b == c || a == c ||
                    (uint)a >= (uint)prim.VertexCount || (uint)b >= (uint)prim.VertexCount || (uint)c >= (uint)prim.VertexCount)
                {
                    continue;
                }

                faces.Add(vertexStart + a);
                faces.Add(vertexStart + b);
                faces.Add(vertexStart + c);
            }

            var polyCount = faces.Count / 3 - faceStartTri;
            batchRanges[k] = (vertexStart, verts.Count - 1, faceStartTri, polyCount);
        }

        if (verts.Count == 0 || faces.Count == 0)
        {
            throw new InvalidOperationException("The model produced no usable geometry (no vertices/triangles).");
        }

        if (verts.Count > 65535)
        {
            throw new InvalidOperationException(
                $"The model has {verts.Count} vertices but V25 uses 16-bit indices (max 65535). Reduce the mesh density or split it.");
        }

        // UV scales: only the layers that own an mTexCoordTransform entry get a recomputed scale (those
        // are patched and encoded consistently). Quantized layers without an entry are decoded by the
        // game with identity, so they must be encoded with identity too.
        var uvScaleByLayer = new Dictionary<int, V25UvScaleValue>();
        foreach (var slot in layout.UvScaleSlots)
        {
            uvScaleByLayer[slot.Layer] = ComputeUvScale(verts, slot.Layer);
        }

        var patches = new List<RegionPatch>();

        // Bounds: mesh-level + every LOD box + every batch box.
        if (layout.MeshBoundsOffset > 0)
        {
            patches.Add(new RegionPatch(layout.MeshBoundsOffset, 24, BoundsBytes(verts, 0, verts.Count)));
        }
        foreach (var lodBoundsOffset in layout.LodBoundsOffsets)
        {
            patches.Add(new RegionPatch(lodBoundsOffset, 24, BoundsBytes(verts, 0, verts.Count)));
        }

        patches.Add(new RegionPatch(layout.VertexCountFieldOffset, 4, U32(verts.Count)));

        // Material plan: bind each part to a material that resolves its own texture. When the model has
        // more distinct textures than the template has materials, new materials (property sets) and
        // material-group entries are added so every texture is shown — matching the other games.
        var materialPlan = BuildMaterialPlan(layout, model, textureSlotsByPrimitive, sourceMaterialLayout);
        patches.AddRange(materialPlan.Patches);

        var batchGeometryDelta = 0;
        if (partCount == layout.Batches.Count)
        {
            // Same number of parts: patch each existing batch in place.
            for (var k = 0; k < layout.Batches.Count; k++)
            {
                var batch = layout.Batches[k];
                var range = batchRanges[k];
                patches.Add(new RegionPatch(batch.BoundsOffset, 24, BoundsBytes(verts, range.VMin, range.VMax + 1)));
                patches.Add(new RegionPatch(batch.VertexMinOffset, 4, U32(range.VMin)));
                patches.Add(new RegionPatch(batch.VertexMaxOffset, 4, U32(range.VMax)));
                patches.Add(new RegionPatch(batch.FaceStartOffset, 4, U32(range.FaceStart * 3)));
                patches.Add(new RegionPatch(batch.PolygonCountOffset, 4, U32(range.PolyCount)));
                patches.Add(new RegionPatch(batch.MaterialIndexOffset, 4, RawMaterialIndex(materialPlan.BatchMaterialIndex1Based[k])));
            }
        }
        else
        {
            // Different number of parts: rebuild the whole LOD0 batch table from a copy of the first
            // template entry. Each new batch keeps the texture-aware material index from the plan.
            batchGeometryDelta = RebuildBatchTable(layout, verts, batchRanges, materialPlan.BatchMaterialIndex1Based, patches);
        }

        // Foreign rig: replace the template's bone-palette block with one rebuilt from the bones the
        // imported model actually uses, so its vertices resolve correctly. All batches already point at
        // palette 0 (single-palette character parts), so only the block contents change.
        var paletteDelta = 0;
        if (rebuildPalette)
        {
            var newBlock = BuildV25PaletteBlock(newPaletteBones, verts);
            var oldLen = layout.BonePaletteBlockEnd - layout.BonePaletteBlockStart;
            patches.Add(new RegionPatch(layout.BonePaletteBlockStart, oldLen, newBlock));
            paletteDelta = newBlock.Length - oldLen;
        }

        // The geometry block grows by the batch-table change, any added material-group entries, and any
        // bone-palette resize; patch its size once with the combined delta.
        var geometryDelta = batchGeometryDelta + materialPlan.GeometryDelta + paletteDelta;
        if (geometryDelta != 0)
        {
            patches.Add(new RegionPatch(layout.GeometryBlockSizeFieldOffset, 4,
                U32(ReadU32(layout.Original, layout.GeometryBlockSizeFieldOffset) + geometryDelta)));
        }

        foreach (var slot in layout.UvScaleSlots)
        {
            patches.Add(new RegionPatch(slot.ValuesOffset, 16, UvScaleBytes(uvScaleByLayer[slot.Layer])));
        }

        // Index (face) buffer: count is the number of indices; payload is global 0-based uint16 indices.
        var faceBytes = FaceBytes(faces);
        patches.Add(new RegionPatch(layout.FaceBuffer.CountFieldOffset, 4, U32(faces.Count)));
        patches.Add(new RegionPatch(layout.FaceBuffer.PayloadOffset, layout.FaceBuffer.PayloadLength, faceBytes));

        // Vertex buffers: count = vertex count; payload encoded per the attribute table.
        // When the GLB carries no vertex colours, the mesh's colour attributes (e.g. Michonne's per-vertex
        // hair transparency in the second colour stream) are preserved verbatim from the template instead
        // of being overwritten with white — losing them leaves the hair with black blend artifacts.
        var glbHasVertexColor = model.Primitives.Any(p => HasChannel(p.Color0, p.VertexCount));
        var vertexBufferBytes = new List<byte[]>(layout.VertexBuffers.Count);
        for (var b = 0; b < layout.VertexBuffers.Count; b++)
        {
            var vb = layout.VertexBuffers[b];
            var bytes = BuildVertexBufferBytes(layout, b, vb.Stride, verts, uvScaleByLayer, glbHasVertexColor);
            vertexBufferBytes.Add(bytes);
            patches.Add(new RegionPatch(vb.CountFieldOffset, 4, U32(verts.Count)));
            patches.Add(new RegionPatch(vb.PayloadOffset, vb.PayloadLength, bytes));
        }

        var result = D3DMeshWriter.Apply(layout.Original, patches);

        // MSV5 container fixup. The async section is exactly the buffer payloads at the end of the file,
        // so its size is the summed payload bytes regardless of whether the batch table was resized; the
        // sync ("Default") section is everything before it.
        var asyncSize = faceBytes.Length + vertexBufferBytes.Sum(b => b.Length);
        var newFaceDataStart = result.Length - asyncSize;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(newFaceDataStart - layout.DataOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)asyncSize);
        return result;
    }

    // Rebuilds the LOD0 batch table to hold one entry per GLB part, copying the first template entry as
    // a base and patching its bbox / vertex range / index range / material index. The enclosing block
    // sizes (LOD entry, LOD block, geometry block) are grown/shrunk by the byte delta.
    // Returns the byte delta the rebuilt batch table adds to the enclosing geometry block.
    private static int RebuildBatchTable(
        V25MeshLayout layout,
        List<V25Vertex> verts,
        (int VMin, int VMax, int FaceStart, int PolyCount)[] batchRanges,
        int[] batchMaterialIndex1Based,
        List<RegionPatch> patches)
    {
        var template = layout.Batches[0];
        var entrySize = template.EndOffset - template.BoundsOffset;
        var boundsRel = 0;
        var vMinRel = template.VertexMinOffset - template.BoundsOffset;
        var vMaxRel = template.VertexMaxOffset - template.BoundsOffset;
        var faceStartRel = template.FaceStartOffset - template.BoundsOffset;
        var polyRel = template.PolygonCountOffset - template.BoundsOffset;
        var materialRel = template.MaterialIndexOffset - template.BoundsOffset;
        var templateEntryBytes = layout.Original.AsSpan(template.BoundsOffset, entrySize).ToArray();

        var newTable = new byte[batchRanges.Length * entrySize];
        for (var k = 0; k < batchRanges.Length; k++)
        {
            var range = batchRanges[k];
            var entry = (byte[])templateEntryBytes.Clone();

            BoundsBytes(verts, range.VMin, range.VMax + 1).CopyTo(entry.AsSpan(boundsRel, 24));
            WriteU32Into(entry, vMinRel, range.VMin);
            WriteU32Into(entry, vMaxRel, range.VMax);
            WriteU32Into(entry, faceStartRel, range.FaceStart * 3);
            WriteU32Into(entry, polyRel, range.PolyCount);

            // Material index comes from the texture-aware material plan.
            RawMaterialIndex(batchMaterialIndex1Based[k]).CopyTo(entry.AsSpan(materialRel, 4));

            entry.CopyTo(newTable.AsSpan(k * entrySize, entrySize));
        }

        var oldTableStart = template.BoundsOffset;
        var oldTableEnd = layout.Batches[^1].EndOffset;
        var oldTableLength = oldTableEnd - oldTableStart;
        var delta = newTable.Length - oldTableLength;

        patches.Add(new RegionPatch(oldTableStart, oldTableLength, newTable));
        patches.Add(new RegionPatch(layout.BatchCountFieldOffset, 4, U32(batchRanges.Length)));
        patches.Add(new RegionPatch(layout.LodEntrySizeFieldOffset, 4, U32(ReadU32(layout.Original, layout.LodEntrySizeFieldOffset) + delta)));
        patches.Add(new RegionPatch(layout.LodBlockSizeFieldOffset, 4, U32(ReadU32(layout.Original, layout.LodBlockSizeFieldOffset) + delta)));
        // The enclosing geometry-block size is patched once by the caller, combining this delta with any
        // delta from added material-group entries.
        return delta;
    }

    private sealed record MaterialPlan(List<RegionPatch> Patches, int[] BatchMaterialIndex1Based, int GeometryDelta);

    // Builds the material/texture binding plan: rebinds each material's diffuse to its part's texture and,
    // when the model has more distinct textures than the template has materials, clones extra property
    // sets and material-group entries so every texture gets its own material. Returns the patches, the
    // 1-based material index each part's batch should reference, and the byte delta added to the geometry
    // block by new material-group entries (the property-set bytes go before the geometry block).
    private static MaterialPlan BuildMaterialPlan(
        V25MeshLayout layout,
        GltfModel model,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        V25MeshLayout? sourceMaterialLayout)
    {
        var patches = new List<RegionPatch>();
        var partCount = model.Primitives.Count;
        var batchIndex = new int[partCount];

        // Distinct diffuse textures across parts, in first-appearance order. A part with no diffuse at all
        // reuses the first material rather than getting its own (which would bind to a texture that was
        // never written and render black).
        var distinct = new List<string>();
        var primTexIndex = new int[partCount];
        for (var k = 0; k < partCount; k++)
        {
            var texName = ResolveDiffuseName(model.Primitives[k], textureSlotsByPrimitive, k);
            if (string.IsNullOrWhiteSpace(texName))
            {
                primTexIndex[k] = 0;
                continue;
            }

            var idx = distinct.FindIndex(n => string.Equals(n, texName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                idx = distinct.Count;
                distinct.Add(texName);
            }

            primTexIndex[k] = idx;
        }

        // Same part count: keep the template's batch->group->material wiring intact and only rebind the
        // diffuse of the material a part actually uses, and only when the GLB changed that part's texture.
        // Groups link to materials by SYMBOL, and that order is independent of the material list order
        // (e.g. Michonne hair: groups -> materials [0,2,1]); recomputing batch indices or rebinding
        // materials positionally would swap a mesh's textures. Resolving each batch's material by symbol
        // handles any wiring, so a pure extract->reinsert is a no-op. Runs before CanRebindMaterials so the
        // non-1:1 case (which that guard rejects) is still handled correctly.
        if (partCount == layout.Batches.Count)
        {
            var data = layout.Original;
            ulong Sym(int off) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off, 8));
            var materialBySymbol = new Dictionary<ulong, int>();
            for (var i = 0; i < layout.Materials.Count; i++)
            {
                materialBySymbol.TryAdd(Sym(layout.Materials[i].SymbolOffset), i);
            }

            var rebound = new HashSet<int>();
            for (var k = 0; k < partCount; k++)
            {
                batchIndex[k] = layout.Batches[k].MaterialIndex; // keep the template's 1-based group index
                var groupIndex = layout.Batches[k].MaterialIndex - 1;
                var name = ResolveDiffuseName(model.Primitives[k], textureSlotsByPrimitive, k);
                if (name is null || name.StartsWith("__part", StringComparison.Ordinal) ||
                    groupIndex < 0 || groupIndex >= layout.MaterialGroupEntries.Count)
                {
                    continue;
                }

                if (materialBySymbol.TryGetValue(Sym(layout.MaterialGroupEntries[groupIndex].SymbolOffset), out var matIdx) &&
                    rebound.Add(matIdx) && layout.Materials[matIdx].DiffuseHashOffset >= 0)
                {
                    patches.Add(new RegionPatch(layout.Materials[matIdx].DiffuseHashOffset, 8, Hash8(name)));
                }
            }

            return new MaterialPlan(patches, batchIndex, 0);
        }

        var m = layout.Materials.Count;
        // When no part carries a real diffuse texture (e.g. a companion/viseme port that reuses the
        // target's own textures and only swaps geometry/UVs), keep the template's material bindings —
        // rebinding to synthetic "__partN" names would point the materials at textures that don't exist.
        var allSynthetic = distinct.All(n => n.StartsWith("__part", StringComparison.Ordinal));
        if (!CanRebindMaterials(layout) || allSynthetic)
        {
            // Can't safely rebind/clone materials: keep the template's, assigning each part to a material
            // by texture (capped at the template's count). Some textures may repeat — the caller warns.
            var batchCount = Math.Max(1, layout.Batches.Count);
            var slots = V25MaterialAssignment.PrimitiveToSlot(model, batchCount);
            for (var k = 0; k < partCount; k++)
            {
                batchIndex[k] = layout.Batches[Math.Min(slots[k], layout.Batches.Count - 1)].MaterialIndex;
            }

            return new MaterialPlan(patches, batchIndex, 0);
        }

        var d = distinct.Count;
        var original = layout.Original;
        var donorLayout = sourceMaterialLayout is not null &&
                          sourceMaterialLayout.Materials.Count >= d &&
                          sourceMaterialLayout.MaterialGroupEntries.Count >= d
            ? sourceMaterialLayout
            : null;
        var material0 = layout.Materials[0];
        var entry0 = layout.MaterialGroupEntries[0];
        var material0Bytes = original.AsSpan(material0.Start, material0.End - material0.Start).ToArray();
        var entry0Bytes = original.AsSpan(entry0.Start, entry0.Length).ToArray();
        var material0SymbolRel = material0.SymbolOffset - material0.Start;
        var material0DiffuseRel = material0.DiffuseHashOffset - material0.Start;
        var entry0SymbolRel = entry0.SymbolOffset - entry0.Start;

        // Rebind the existing materials to the parts' textures (no-op when the names already match, e.g.
        // re-importing the same model unchanged).
        for (var i = 0; i < Math.Min(d, m); i++)
        {
            var target = layout.Materials[i];
            if (TryBuildDonorMaterialBytes(donorLayout, i, original, target, distinct[i], out var donorBytes))
            {
                patches.Add(new RegionPatch(target.Start, target.End - target.Start, donorBytes));
            }
            else if (target.DiffuseHashOffset >= 0)
            {
                patches.Add(new RegionPatch(target.DiffuseHashOffset, 8, Hash8(distinct[i])));
            }
        }

        var geometryDelta = 0;
        if (d > m)
        {
            var newMaterials = new List<byte>();
            var newGroupEntries = new List<byte>();
            for (var i = m; i < d; i++)
            {
                var symbol = Crc64Ecma.Compute($"__v25_added_material_{i}_{distinct[i]}");

                byte[] matBytes;
                int symbolRel;
                int diffuseRel;
                if (donorLayout is not null && i < donorLayout.Materials.Count)
                {
                    var donor = donorLayout.Materials[i];
                    matBytes = donorLayout.Original.AsSpan(donor.Start, donor.End - donor.Start).ToArray();
                    symbolRel = donor.SymbolOffset - donor.Start;
                    diffuseRel = donor.DiffuseHashOffset >= 0 ? donor.DiffuseHashOffset - donor.Start : -1;
                }
                else
                {
                    matBytes = (byte[])material0Bytes.Clone();
                    symbolRel = material0SymbolRel;
                    diffuseRel = material0DiffuseRel;
                }

                BinaryPrimitives.WriteUInt64LittleEndian(matBytes.AsSpan(symbolRel, 8), symbol);
                if (diffuseRel >= 0)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(matBytes.AsSpan(diffuseRel, 8), TextureSymbolHash(distinct[i]));
                }

                newMaterials.AddRange(matBytes);

                var entryBytes = (byte[])entry0Bytes.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(entryBytes.AsSpan(entry0SymbolRel, 8), symbol);
                newGroupEntries.AddRange(entryBytes);
            }

            var added = d - m;
            geometryDelta = added * entry0.Length;

            // New property sets go right after the last material; new material-group entries at the end of
            // the pairing block. Both are zero-length inserts.
            patches.Add(new RegionPatch(layout.MaterialsEndOffset, 0, newMaterials.ToArray()));
            patches.Add(new RegionPatch(layout.MaterialGroupEntriesEndOffset, 0, newGroupEntries.ToArray()));
            patches.Add(new RegionPatch(layout.MaterialCountFieldOffset, 4, U32(m + added)));
            patches.Add(new RegionPatch(layout.MaterialGroupCountFieldOffset, 4, U32(layout.MaterialGroupEntries.Count + added)));
            patches.Add(new RegionPatch(layout.MaterialGroupSizeFieldOffset, 4,
                U32(ReadU32(original, layout.MaterialGroupSizeFieldOffset) + geometryDelta)));
        }

        // group entry i references material i (verified 1:1), so a part using texture i points at the
        // (i+1)-th material (1-based).
        for (var k = 0; k < partCount; k++)
        {
            batchIndex[k] = primTexIndex[k] + 1;
        }

        return new MaterialPlan(patches, batchIndex, geometryDelta);
    }

    private static string? ResolveDiffuseName(
        GltfPrimitive primitive,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? textureSlotsByPrimitive,
        int primitiveIndex)
    {
        if (textureSlotsByPrimitive is not null &&
            primitiveIndex >= 0 &&
            primitiveIndex < textureSlotsByPrimitive.Count &&
            textureSlotsByPrimitive[primitiveIndex].TryGetValue("diffuse", out var mappedName) &&
            !string.IsNullOrWhiteSpace(mappedName))
        {
            return StripTextureExtension(mappedName);
        }

        return V25MaterialAssignment.DiffuseName(primitive);
    }

    private static bool TryBuildDonorMaterialBytes(
        V25MeshLayout? donorLayout,
        int index,
        byte[] targetData,
        V25MaterialLayout target,
        string diffuseName,
        out byte[] bytes)
    {
        bytes = [];
        if (donorLayout is null || index < 0 || index >= donorLayout.Materials.Count)
        {
            return false;
        }

        var donor = donorLayout.Materials[index];
        var targetLength = target.End - target.Start;
        var donorLength = donor.End - donor.Start;
        if (donorLength != targetLength)
        {
            return false;
        }

        bytes = donorLayout.Original.AsSpan(donor.Start, donorLength).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(donor.SymbolOffset - donor.Start, 8),
            BinaryPrimitives.ReadUInt64LittleEndian(targetData.AsSpan(target.SymbolOffset, 8)));
        if (donor.DiffuseHashOffset >= 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(donor.DiffuseHashOffset - donor.Start, 8),
                TextureSymbolHash(diffuseName));
        }

        return true;
    }

    public static V25MeshLayout? TryFindSourceMaterialLayout(
        GltfModel model,
        string templateMeshPath,
        string? modelPath)
    {
        var templateFolder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        foreach (var candidate in EnumerateSourceMeshCandidates(model, templateFolder, modelPath))
        {
            try
            {
                if (!File.Exists(candidate) ||
                    Path.GetFullPath(candidate).Equals(Path.GetFullPath(templateMeshPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var layout = V25MeshLayout.Build(File.ReadAllBytes(candidate));
                if (layout.Version == 25 && layout.Materials.Count > 0)
                {
                    return layout;
                }
            }
            catch
            {
                // Best-effort only; the old target-material cloning path remains valid fallback.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSourceMeshCandidates(
        GltfModel model,
        string? templateFolder,
        string? modelPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in model.Primitives
                     .Select(p => p.SourceMeshPath)
                     .Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            foreach (var candidate in ExpandSourceCandidate(sourcePath!, templateFolder))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(modelPath) && templateFolder is not null)
        {
            var byStem = Path.Combine(templateFolder, Path.GetFileNameWithoutExtension(modelPath) + ".d3dmesh");
            if (seen.Add(byStem))
            {
                yield return byStem;
            }
        }
    }

    private static IEnumerable<string> ExpandSourceCandidate(string sourcePath, string? templateFolder)
    {
        if (Path.IsPathRooted(sourcePath))
        {
            yield return sourcePath;
        }
        else if (templateFolder is not null)
        {
            yield return Path.Combine(templateFolder, sourcePath);
            yield return Path.Combine(templateFolder, Path.GetFileName(sourcePath));
        }
    }

    // True when this template's materials can be rebound/cloned to add textures (the common case). When
    // false, a model with more textures than the template has materials will repeat textures instead.
    public static bool CanAddMaterials(V25MeshLayout layout) => CanRebindMaterials(layout);

    // The material<->material-group mapping must be 1:1 and in order for rebinding/cloning to be safe.
    private static bool CanRebindMaterials(V25MeshLayout layout)
    {
        if (layout.Materials.Count == 0 ||
            layout.Materials.Count != layout.MaterialGroupEntries.Count ||
            layout.Materials[0].DiffuseHashOffset < 0)
        {
            return false;
        }

        for (var i = 0; i < layout.Materials.Count; i++)
        {
            var matSym = BitConverter.ToUInt64(layout.Original, layout.Materials[i].SymbolOffset);
            var groupSym = BitConverter.ToUInt64(layout.Original, layout.MaterialGroupEntries[i].SymbolOffset);
            if (matSym != groupSym)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Hash8(string name)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, TextureSymbolHash(name));
        return bytes;
    }

    // Telltale's texture symbol is the CRC64 of the file name WITH its .d3dtx extension (the mesh stores
    // e.g. CRC64("obj_chairApartmentCoveA.d3dtx"), not CRC64("obj_chairApartmentCoveA")). Binding a
    // material to the extension-less hash leaves the engine unable to find the texture (black model).
    private static ulong TextureSymbolHash(string name)
    {
        var stem = name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? name : name + ".d3dtx";
        return Crc64Ecma.Compute(stem);
    }

    private static string StripTextureExtension(string name)
        => name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    private static byte[] RawMaterialIndex(int index1Based)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, index1Based > 0 ? (uint)(index1Based - 1) : 0xFFFFFFFFu);
        return bytes;
    }

    private static int ReadU32(byte[] data, int offset)
        => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static void WriteU32Into(byte[] bytes, int at, int value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), (uint)value);

    private sealed class V25Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector4 Binormal;
        public Vector4 Color;
        public required Vector2[] Uv; // index 0..5 = uv1..uv6
        public int[] Bones = [0, 0, 0, 0];      // indices into this part's bone palette
        public float[] Weights = [1f, 0f, 0f, 0f];
    }

    private readonly record struct V25UvScaleValue(float XMult, float YMult, float XStart, float YStart);

    private static byte[] BuildVertexBufferBytes(
        V25MeshLayout layout,
        int bufferIndexZeroBased,
        int stride,
        List<V25Vertex> verts,
        Dictionary<int, V25UvScaleValue> uvScaleByLayer,
        bool glbHasVertexColor = true)
    {
        var bufferKey = bufferIndexZeroBased + 1; // attribute Buffer field is +1 (1,2,3)
        var attrs = layout.Attributes.Where(a => a.Key.Length > 0 && a.Buffer == bufferKey).ToList();
        var bytes = new byte[verts.Count * stride];

        // Colour streams the GLB doesn't carry are filled from the template, not white. The exporter only
        // round-trips the first colour stream as alpha (and only when the mesh uses it), and never the
        // second stream — so Michonne's hair transparency lives in "colors2", which a GLB round-trip would
        // otherwise wipe to white and leave the hair with black blend artifacts. These streams are
        // constant per mesh in practice, so the template's first vertex value is replicated to every
        // output vertex (vertex counts diverge across UV/normal seam splits, so an index copy can't work).
        var original = layout.VertexBuffers[bufferIndexZeroBased];
        var templateColor = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var attr in attrs)
        {
            var preserve = attr.Key == "colors2" || (attr.Key == "colors" && !glbHasVertexColor);
            if (preserve && original.PayloadOffset > 0 && original.Count > 0 &&
                original.PayloadOffset + attr.BufferOffset + 4 <= layout.Original.Length)
            {
                var rep = new byte[4];
                Array.Copy(layout.Original, original.PayloadOffset + attr.BufferOffset, rep, 0, 4);
                templateColor[attr.Key] = rep;
            }
        }

        for (var i = 0; i < verts.Count; i++)
        {
            var vertexBase = i * stride;
            var v = verts[i];
            foreach (var attr in attrs)
            {
                var at = vertexBase + attr.BufferOffset;
                if (templateColor.TryGetValue(attr.Key, out var rep))
                {
                    Array.Copy(rep, 0, bytes, at, 4);
                    continue;
                }
                switch (attr.Key)
                {
                    case "position":
                        WriteVec3F32(bytes, at, v.Position);
                        break;
                    case "normals":
                        WriteNormalShort4(bytes, at, v.Normal.X, v.Normal.Y, v.Normal.Z, 0f);
                        break;
                    case "binormals":
                        WriteNormalShort4(bytes, at, v.Binormal.X, v.Binormal.Y, v.Binormal.Z, v.Binormal.W);
                        break;
                    case "tangents":
                        WriteNormalShort4(bytes, at, v.Tangent.X, v.Tangent.Y, v.Tangent.Z, v.Tangent.W);
                        break;
                    case "colors":
                        WriteColorUn8(bytes, at, v.Color);
                        break;
                    case "colors2":
                        WriteColorUn8(bytes, at, v.Color);
                        break;
                    case "weights":
                        WriteWeightsShort4(bytes, at, v.Weights);
                        break;
                    case "bones":
                        WriteBonesByte4(bytes, at, v.Bones);
                        break;
                    default:
                        if (attr.Key.StartsWith("uv", StringComparison.Ordinal) &&
                            int.TryParse(attr.Key.AsSpan(2), out var layer1Based))
                        {
                            WriteUv(bytes, at, attr.Format, v.Uv[Math.Clamp(layer1Based - 1, 0, 5)], layer1Based - 1, uvScaleByLayer);
                        }
                        break;
                }
            }
        }

        return bytes;
    }

    private static void WriteUv(byte[] bytes, int at, int format, Vector2 uv, int layer0Based, Dictionary<int, V25UvScaleValue> uvScaleByLayer)
    {
        // The stored ("decoded") UV the file holds equals what the GLB carries: the parser reads
        // displayV = 1 - decoded, and the exporter writes TEXCOORD V = 1 - displayV = decoded. So the
        // glTF UV already IS the stored value — write U and V straight through, no extra V flip.
        var decodedU = uv.X;
        var decodedV = uv.Y;
        switch (format)
        {
            case 2: // F32x2
                WriteFloat(bytes, at, decodedU);
                WriteFloat(bytes, at + 4, decodedV);
                break;
            case 19: // SN16x2 with optional per-layer scale (identity when the layer owns no transform)
                var scale = uvScaleByLayer.TryGetValue(layer0Based, out var s) ? s : new V25UvScaleValue(1, 1, 0, 0);
                WriteInt16(bytes, at, QuantizeUv(decodedU, scale.XStart, scale.XMult));
                WriteInt16(bytes, at + 2, QuantizeUv(decodedV, scale.YStart, scale.YMult));
                break;
            default:
                throw new InvalidDataException($"Unsupported V25 UV format {format} for reinsertion.");
        }
    }

    private static short QuantizeUv(float decoded, float start, float mult)
    {
        var unit = mult != 0f ? (decoded - start) / mult : 0f;
        return (short)Math.Clamp((int)MathF.Round(unit * 32767f), short.MinValue, short.MaxValue);
    }

    // Recomputes a tight scale for a quantized UV layer so the int16 range covers the model's UV span.
    // start = min(decoded), mult = span; encode maps [start, start+mult] onto [0, 32767].
    private static V25UvScaleValue ComputeUvScale(List<V25Vertex> verts, int layer0Based)
    {
        float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
        foreach (var v in verts)
        {
            var uv = v.Uv[Math.Clamp(layer0Based, 0, 5)];
            var decodedU = uv.X;
            var decodedV = uv.Y;
            minU = MathF.Min(minU, decodedU);
            maxU = MathF.Max(maxU, decodedU);
            minV = MathF.Min(minV, decodedV);
            maxV = MathF.Max(maxV, decodedV);
        }

        if (minU > maxU)
        {
            minU = maxU = minV = maxV = 0f;
        }

        var multU = maxU - minU;
        var multV = maxV - minV;
        return new V25UvScaleValue(multU != 0f ? multU : 1f, multV != 0f ? multV : 1f, minU, minV);
    }

    private static byte[] FaceBytes(List<int> faces)
    {
        var bytes = new byte[faces.Count * 2];
        for (var i = 0; i < faces.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)faces[i]);
        }

        return bytes;
    }

    private static byte[] BoundsBytes(List<V25Vertex> verts, int start, int endExclusive)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (var i = start; i < endExclusive && i < verts.Count; i++)
        {
            var p = verts[i].Position;
            minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); minZ = MathF.Min(minZ, p.Z);
            maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y); maxZ = MathF.Max(maxZ, p.Z);
        }

        if (minX > maxX)
        {
            minX = minY = minZ = maxX = maxY = maxZ = 0f;
        }

        var bytes = new byte[24];
        WriteFloat(bytes, 0, minX); WriteFloat(bytes, 4, minY); WriteFloat(bytes, 8, minZ);
        WriteFloat(bytes, 12, maxX); WriteFloat(bytes, 16, maxY); WriteFloat(bytes, 20, maxZ);
        return bytes;
    }

    private static byte[] UvScaleBytes(V25UvScaleValue s)
    {
        var bytes = new byte[16];
        WriteFloat(bytes, 0, s.XMult); WriteFloat(bytes, 4, s.YMult);
        WriteFloat(bytes, 8, s.XStart); WriteFloat(bytes, 12, s.YStart);
        return bytes;
    }

    private static void WriteVec3F32(byte[] bytes, int at, Vector3 v)
    {
        WriteFloat(bytes, at, v.X);
        WriteFloat(bytes, at + 4, v.Y);
        WriteFloat(bytes, at + 8, v.Z);
    }

    private static void WriteNormalShort4(byte[] bytes, int at, float x, float y, float z, float w)
    {
        WriteInt16(bytes, at, ToSNorm16(x));
        WriteInt16(bytes, at + 2, ToSNorm16(y));
        WriteInt16(bytes, at + 4, ToSNorm16(z));
        WriteInt16(bytes, at + 6, ToSNorm16(w));
    }

    private static void WriteColorUn8(byte[] bytes, int at, Vector4 c)
    {
        bytes[at] = ToUNorm8(c.X);
        bytes[at + 1] = ToUNorm8(c.Y);
        bytes[at + 2] = ToUNorm8(c.Z);
        bytes[at + 3] = ToUNorm8(c.W);
    }

    private static short ToSNorm16(float value)
        => (short)Math.Clamp((int)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f), short.MinValue, short.MaxValue);

    private static byte ToUNorm8(float value)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static void WriteFloat(byte[] bytes, int at, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at, 4), value);

    private static void WriteInt16(byte[] bytes, int at, short value)
        => BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at, 2), value);

    // Blend weights: 4x int16 normalized (value/32767 on read, so value = weight*32767). Renormalized so
    // the four weights sum to 1 (the engine expects normalized weights; the GLB's may drift slightly).
    private static void WriteWeightsShort4(byte[] bytes, int at, float[] weights)
    {
        var sum = weights[0] + weights[1] + weights[2] + weights[3];
        var inv = sum > 1e-6f ? 1f / sum : 0f;
        for (var i = 0; i < 4; i++)
        {
            var w = inv > 0f ? weights[i] * inv : (i == 0 ? 1f : 0f);
            WriteInt16(bytes, at + i * 2, (short)Math.Clamp((int)MathF.Round(w * 32767f), 0, 32767));
        }
    }

    // Blend bone indices: 4x byte, each the palette index multiplied by 3 (the parser reads byte/3). A
    // V25 palette holds at most 85 bones (255/3).
    private static void WriteBonesByte4(byte[] bytes, int at, int[] bones)
    {
        for (var i = 0; i < 4; i++)
        {
            bytes[at + i] = (byte)(Math.Clamp(bones[i], 0, 85) * 3);
        }
    }

    // Maps each bone hash in a palette to its index, so a GLB joint can be resolved back to the index the
    // vertex blend bytes must store.
    private static Dictionary<ulong, int> BuildPaletteHashIndex(V25BonePaletteLayout palette)
    {
        var map = new Dictionary<ulong, int>(palette.BoneHashes.Length);
        for (var i = 0; i < palette.BoneHashes.Length; i++)
        {
            map.TryAdd(palette.BoneHashes[i], i);
        }

        return map;
    }

    // The 0-based bone palette a part's vertices index into: the palette of the template batch this part
    // maps to (same-count = batch k; different count = clamped). BoneSetIndex is +1 (0 = none -> palette 0).
    private static int PaletteForPrimitive(V25MeshLayout layout, int primitiveIndex, int partCount)
    {
        if (layout.Batches.Count == 0)
        {
            return 0;
        }

        var batchIndex = partCount == layout.Batches.Count
            ? primitiveIndex
            : Math.Min(primitiveIndex, layout.Batches.Count - 1);
        var boneSet = layout.Batches[batchIndex].BoneSetIndex; // +1 convention
        var palette = boneSet > 0 ? boneSet - 1 : 0;
        return Math.Clamp(palette, 0, layout.BonePalettes.Count - 1);
    }

    // Resolves a GLB vertex's 4 joints/weights to the mesh's palette indices. A joint absent from the
    // palette (a bone the target skeleton doesn't have in this set) drops to weight 0; weights are
    // renormalized on write. Returns via the vertex's Bones/Weights arrays.
    private static void AssignSkinning(
        V25Vertex vertex,
        GltfModel model,
        GltfPrimitive prim,
        int vertexIndex,
        Dictionary<ulong, int> paletteHashToIndex,
        ref int unresolvedJoints)
    {
        var joints0 = prim.Joints0!;
        var weights0 = prim.Weights0!;
        var baseIndex = vertexIndex * 4;
        var w = weights0[vertexIndex];
        var weightByLane = new[] { w.X, w.Y, w.Z, w.W };
        var bones = new int[4];
        var weights = new float[4];
        for (var lane = 0; lane < 4; lane++)
        {
            var jointIndex = baseIndex + lane < joints0.Length ? joints0[baseIndex + lane] : 0;
            var paletteIndex = ResolveJointPaletteIndex(model, jointIndex, paletteHashToIndex);
            if (paletteIndex >= 0)
            {
                bones[lane] = paletteIndex;
                weights[lane] = weightByLane[lane];
            }
            else
            {
                bones[lane] = 0;
                weights[lane] = 0f;
                if (weightByLane[lane] > 0.0001f)
                {
                    unresolvedJoints++;
                }
            }
        }

        if (weights[0] + weights[1] + weights[2] + weights[3] <= 1e-6f)
        {
            weights[0] = 1f; // fully unresolved vertex: bind to the first palette bone rather than nothing
        }

        vertex.Bones = bones;
        vertex.Weights = weights;
    }

    private static int ResolveJointPaletteIndex(GltfModel model, int jointIndex, Dictionary<ulong, int> paletteHashToIndex)
    {
        if (jointIndex < 0 || jointIndex >= model.Joints.Count)
        {
            return -1;
        }

        var joint = model.Joints[jointIndex];
        if (joint.Hash is { } hash && paletteHashToIndex.TryGetValue(hash, out var byHash))
        {
            return byHash;
        }

        if (!string.IsNullOrWhiteSpace(joint.Name) &&
            paletteHashToIndex.TryGetValue(Crc64Ecma.Compute(joint.Name), out var byName))
        {
            return byName;
        }

        return -1;
    }

    private const int MaxPaletteBones = 85; // vertex bone byte = paletteIndex * 3, max 255 -> index <= 85

    // The distinct bone hashes the imported model's vertices reference (weight > 0), in first-appearance
    // order. Used to rebuild a palette that covers a foreign rig the template's subset doesn't.
    private static List<ulong> CollectUsedBoneHashes(GltfModel model)
    {
        var seen = new HashSet<ulong>();
        var ordered = new List<ulong>();
        foreach (var prim in model.Primitives)
        {
            if (prim.Joints0 is null || prim.Weights0 is null)
            {
                continue;
            }

            for (var v = 0; v < prim.VertexCount; v++)
            {
                var w = prim.Weights0[v];
                var lanes = new[] { w.X, w.Y, w.Z, w.W };
                for (var l = 0; l < 4; l++)
                {
                    if (lanes[l] <= 0.0001f)
                    {
                        continue;
                    }

                    var ji = v * 4 + l < prim.Joints0.Length ? prim.Joints0[v * 4 + l] : 0;
                    var hash = ResolveJointHash(model, ji);
                    if (hash != 0 && seen.Add(hash))
                    {
                        ordered.Add(hash);
                    }
                }
            }
        }

        return ordered;
    }

    private static ulong ResolveJointHash(GltfModel model, int jointIndex)
    {
        if (jointIndex < 0 || jointIndex >= model.Joints.Count)
        {
            return 0;
        }

        var joint = model.Joints[jointIndex];
        if (joint.Hash is { } h && h != 0)
        {
            return h;
        }

        return string.IsNullOrWhiteSpace(joint.Name) ? 0 : Crc64Ecma.Compute(joint.Name);
    }

    private static Dictionary<ulong, int> BuildHashIndexMap(ulong[] bones)
    {
        var map = new Dictionary<ulong, int>(bones.Length);
        for (var i = 0; i < bones.Length; i++)
        {
            map.TryAdd(bones[i], i);
        }

        return map;
    }

    // Builds an mBonePalettes block with a single palette of the given bones. Per the toolkit's
    // T3MeshBoneEntry, each 56-byte entry is: mBoneName(symbol, 8) + mBoundingBox(min3f,max3f, 24) +
    // a constant u32 (0x14) + mBoundingSphere(center3f,radius, 16) + mNumVerts(4). The bounds and the
    // influence count are computed from the vertices each bone actually drives.
    private static byte[] BuildV25PaletteBlock(ulong[] bones, List<V25Vertex> verts)
    {
        const uint BoneEntryConstant = 0x14; // fixed field between the box and the sphere in every entry
        var blockSize = 12 + bones.Length * 56;
        var bytes = new byte[blockSize];
        var p = 0;
        void U32w(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p, 4), value); p += 4; }
        void F32w(float value) { BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p, 4), value); p += 4; }

        U32w((uint)blockSize);
        U32w(1);
        U32w((uint)bones.Length);
        for (var i = 0; i < bones.Length; i++)
        {
            var (min, max, count) = BoneInfluence(verts, i);
            U32w((uint)(bones[i] & 0xFFFFFFFF));
            U32w((uint)(bones[i] >> 32));
            F32w(min.X); F32w(min.Y); F32w(min.Z);
            F32w(max.X); F32w(max.Y); F32w(max.Z);
            U32w(BoneEntryConstant);
            var center = (min + max) * 0.5f;
            F32w(center.X); F32w(center.Y); F32w(center.Z);
            F32w((max - min).Length() * 0.5f);
            U32w((uint)count); // mNumVerts
        }

        return bytes;
    }

    private static (Vector3 Min, Vector3 Max, int Count) BoneInfluence(List<V25Vertex> verts, int boneIndex)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var count = 0;
        foreach (var v in verts)
        {
            for (var l = 0; l < 4; l++)
            {
                if (v.Bones[l] == boneIndex && v.Weights[l] > 0.0001f)
                {
                    min = Vector3.Min(min, v.Position);
                    max = Vector3.Max(max, v.Position);
                    count++;
                    break;
                }
            }
        }

        return count > 0 ? (min, max, count) : (Vector3.Zero, Vector3.Zero, 0);
    }

    private static byte[] U32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        return bytes;
    }

    private static Vector2 Uv(Vector2[]? channel, int index)
        => channel is not null && index < channel.Length ? channel[index] : Vector2.Zero;

    private static bool HasChannel<T>(T[]? channel, int vertexCount)
        => channel is not null && channel.Length >= vertexCount && vertexCount > 0;

    private static Vector3[] ComputeNormals(GltfPrimitive prim)
    {
        var normals = new Vector3[prim.VertexCount];
        for (var i = 0; i + 2 < prim.Indices.Length; i += 3)
        {
            int a = prim.Indices[i], b = prim.Indices[i + 1], c = prim.Indices[i + 2];
            if ((uint)a >= (uint)prim.VertexCount || (uint)b >= (uint)prim.VertexCount || (uint)c >= (uint)prim.VertexCount)
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

    private static Vector4[] ComputeTangents(GltfPrimitive prim, Vector3[] normals)
    {
        // A stable orthonormal tangent per vertex (handedness +1). Adequate for static props; the GLB's
        // own TANGENT is used when present.
        var tangents = new Vector4[prim.VertexCount];
        for (var i = 0; i < tangents.Length; i++)
        {
            var n = normals[i];
            var helper = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            var t = Vector3.Normalize(Vector3.Cross(helper, n));
            tangents[i] = new Vector4(t, 1f);
        }

        return tangents;
    }

    private static Vector4[] ComputeBinormals(Vector3[] normals, Vector4[] tangents)
    {
        var binormals = new Vector4[normals.Length];
        for (var i = 0; i < binormals.Length; i++)
        {
            var n = normals[i];
            var t = new Vector3(tangents[i].X, tangents[i].Y, tangents[i].Z);
            var b = Vector3.Cross(n, t) * tangents[i].W;
            binormals[i] = new Vector4(b, 0f);
        }

        return binormals;
    }
}

// Minecraft: Story Mode Season 2 (.d3dmesh v45) reinserter.
//
// Strategy: the Telltale Toolkit re-serializes a v45 mesh byte-identical EXCEPT for a few
// material metadata spans it recomputes (verified: same size, ~117 bytes per material differ).
// So the writer is: deserialize the template, rebuild the LOD0 GFX vertex/index buffers from the
// GLB (using the template's own attribute layout and formats), update counts and bounds, let the
// toolkit serialize the result, then copy the template's bytes back over the serializer-noise
// spans (their offsets are stable because no list sizes change — only fixed-size numeric fields
// and the async buffer payload at the tail).
public static class V45MeshReinserter
{
    public sealed record TextureAssignment(string TemplateDiffuse, GltfImage? Image);

    public sealed record ReinsertResult(byte[] MeshBytes, IReadOnlyList<TextureAssignment> Textures);

    public static byte[] Reinsert(byte[] templateBytes, GltfModel model)
        => ReinsertWithAssignments(templateBytes, model, batchTemplateDiffuse: null).MeshBytes;

    public static ReinsertResult ReinsertWithAssignments(
        byte[] templateBytes,
        GltfModel model,
        IReadOnlyList<string?>? batchTemplateDiffuse)
        => ReinsertWithAssignmentsCore(
            templateBytes,
            model,
            batchTemplateDiffuse,
            Core.GameConfig.MinecraftStoryModeSeason2.ModernTextureToolkitGameName!,
            "v45",
            preserveLowerLods: false);

    public static byte[] ReinsertV46(byte[] templateBytes, GltfModel model)
        => ReinsertV46WithAssignments(templateBytes, model, batchTemplateDiffuse: null).MeshBytes;

    public static ReinsertResult ReinsertV46WithAssignments(
        byte[] templateBytes,
        GltfModel model,
        IReadOnlyList<string?>? batchTemplateDiffuse)
        => ReinsertWithAssignmentsCore(
            templateBytes,
            model,
            batchTemplateDiffuse,
            Core.GameConfig.Batman.ModernTextureToolkitGameName!,
            "v46",
            preserveLowerLods: true);

    private static ReinsertResult ReinsertWithAssignmentsCore(
        byte[] templateBytes,
        GltfModel model,
        IReadOnlyList<string?>? batchTemplateDiffuse,
        string toolkitGameName,
        string formatLabel,
        bool preserveLowerLods)
    {
        var mesh = TelltaleToolkitMeshParser.ParseModernMeshRaw(
            templateBytes, toolkitGameName)
            ?? throw new InvalidDataException($"The Telltale Toolkit could not read the {formatLabel} template mesh.");
        var meshData = mesh.MeshData ?? throw new InvalidDataException($"{formatLabel} template has no T3MeshData.");
        if (meshData.LODs is not { Count: > 0 } || meshData.VertexStates is not { Count: > 0 })
        {
            throw new InvalidDataException($"{formatLabel} template has no LODs/vertex states.");
        }

        MetaStreamParams streamParams;
        using (var paramStream = new MemoryStream(templateBytes))
        {
            streamParams = new BinaryMetaStreamReader(paramStream).Params;
        }

        // Serializer-noise spans: re-serialize the UNMODIFIED mesh and diff against the template.
        var noiseRegions = ComputeNoiseRegions(templateBytes, mesh, streamParams);

        var lod = meshData.LODs[0];
        var vertexState = meshData.VertexStates[(int)Math.Min(lod.VertexStateIndex, (uint)(meshData.VertexStates.Count - 1))];
        var templateBatches = lod.Batches is { Count: > 0 } ? lod.Batches : lod.Batches1;
        if (templateBatches is not { Count: > 0 })
        {
            throw new InvalidDataException($"{formatLabel} template LOD0 has no batches.");
        }

        var v46State = preserveLowerLods
            ? CapturePreservedLodState(meshData, vertexState, templateBatches)
            : null;

        var (batchGroups, textureAssignments) =
            GroupPrimitivesForBatches(model, templateBatches, meshData, batchTemplateDiffuse);

        // ── Build the new global vertex pool (batches index a shared pool) ──
        // Identical vertices are WELDED across primitives: the extractor duplicates the shared
        // pool per batch on export (overlapping MinVert..MaxVert ranges), so concatenating without
        // welding would inflate the count on every round-trip (and overflow 16-bit indices on
        // large environment meshes).
        var poolList = new List<PoolVertex>();
        var weldMap = new Dictionary<PoolVertex, int>();
        var globalIndices = new List<int>();
        var boneIndexByCrc = BuildBoneIndexMap(meshData);
        var totalTris = 0;

        for (var b = 0; b < templateBatches.Count; b++)
        {
            var batchMin = int.MaxValue;
            var batchMax = 0;
            var batchPoolIndices = new List<int>();
            var startIndex = globalIndices.Count;

            // A batch can receive SEVERAL GLB primitives (e.g. arms + torso merged into a body
            // part); they are concatenated into one draw range.
            foreach (var primitive in batchGroups[b])
            {
                var tangents = primitive.Tangents ?? ComputeTangents(primitive);
                var localToPool = new int[primitive.VertexCount];
                for (var v = 0; v < primitive.VertexCount; v++)
                {
                    var vertex = BuildPoolVertex(primitive, tangents, v, model, boneIndexByCrc);
                    if (!weldMap.TryGetValue(vertex, out var poolIndex))
                    {
                        poolIndex = poolList.Count;
                        poolList.Add(vertex);
                        weldMap[vertex] = poolIndex;
                    }

                    localToPool[v] = poolIndex;
                    batchPoolIndices.Add(poolIndex);
                    batchMin = Math.Min(batchMin, poolIndex);
                    batchMax = Math.Max(batchMax, poolIndex);
                }

                for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
                {
                    globalIndices.Add(localToPool[primitive.Indices[i]]);
                    globalIndices.Add(localToPool[primitive.Indices[i + 1]]);
                    globalIndices.Add(localToPool[primitive.Indices[i + 2]]);
                }
            }

            var triCount = (globalIndices.Count - startIndex) / 3;
            totalTris += triCount;

            var batch = templateBatches[b];
            batch.MinVertIndex = (uint)(batchMin == int.MaxValue ? 0 : batchMin);
            batch.MaxVertIndex = (uint)batchMax;
            batch.BaseIndex = 0;
            batch.StartIndex = (uint)startIndex;
            batch.NumPrimitives = (uint)triCount;
            batch.NumIndices = (uint)(triCount * 3);
            var (box, sphere) = ComputeBoundsFromIndices(poolList, batchPoolIndices.ToArray());
            batch.BoundingBox = box;
            batch.BoundingSphere = sphere;
        }

        var pool = poolList.ToArray();

        // Shadow batches (mBatches[1]): one covering batch is the common case; otherwise mirror the
        // default batches one-to-one.
        var shadowTris = 0;
        if (v46State is null && lod.Batches2 is { Count: > 0 })
        {
            if (lod.Batches2.Count == 1)
            {
                var shadow = lod.Batches2[0];
                shadow.MinVertIndex = 0;
                shadow.MaxVertIndex = (uint)Math.Max(0, pool.Length - 1);
                shadow.BaseIndex = 0;
                shadow.StartIndex = 0;
                shadow.NumPrimitives = (uint)totalTris;
                shadow.NumIndices = (uint)(totalTris * 3);
                var (box, sphere) = ComputeBounds(pool, 0, pool.Length);
                shadow.BoundingBox = box;
                shadow.BoundingSphere = sphere;
                shadowTris = totalTris;
            }
            else
            {
                for (var b = 0; b < lod.Batches2.Count && b < templateBatches.Count; b++)
                {
                    CopyBatchGeometry(templateBatches[b], lod.Batches2[b]);
                    shadowTris += (int)lod.Batches2[b].NumPrimitives;
                }
            }
        }

        // ── Encode the GFX buffers using the template's attribute layout ──
        if (v46State is null)
        {
            EncodeVertexBuffers(vertexState, meshData, pool);
            EncodeIndexBuffers(vertexState, globalIndices, pool.Length);
        }
        else
        {
            shadowTris = EncodeV46BuffersPreservingLowerLods(
                meshData,
                vertexState,
                lod,
                templateBatches,
                pool,
                globalIndices,
                v46State);
        }

        // ── Counts and bounds ──
        var (meshBox, meshSphere) = ComputeBounds(pool, 0, pool.Length);
        meshData.VertexCount = (uint)(pool.Length + (v46State?.PreservedVertexCount ?? 0));
        meshData.BoundingBox = meshBox;
        meshData.BoundingSphere = meshSphere;
        lod.BoundingBox = meshBox;
        lod.BoundingSphere = meshSphere;
        lod.NumPrimitives = (uint)(totalTris + shadowTris);
        if (lod.VertexCount != 0)
        {
            lod.VertexCount = (uint)pool.Length;
        }

        // ── Serialize + noise patchback ──
        byte[] output;
        using (var outputStream = new MemoryStream())
        {
            Toolkit.Instance.Serialize(mesh, outputStream, streamParams);
            output = outputStream.ToArray();
        }

        var syncLimit = Math.Min(AsyncSectionStart(templateBytes), AsyncSectionStart(output));
        foreach (var (start, length) in noiseRegions)
        {
            if (start + length <= syncLimit && start + length <= output.Length)
            {
                Array.Copy(templateBytes, start, output, start, length);
            }
        }

        // Sanity: the produced file must parse back.
        var check = TelltaleToolkitMeshParser.ParseModernMeshRaw(
            output, toolkitGameName);
        if (check?.MeshData is null)
        {
            throw new InvalidDataException($"{formatLabel} reinsertion produced a file the toolkit cannot read back.");
        }
        if (v46State is not null)
        {
            ValidatePreservedV46Output(check.MeshData, v46State);
        }

        return new ReinsertResult(output, textureAssignments);
    }

    // Makes an unassigned combined part invisible: the template keeps its buffers untouched but
    // every batch draws zero triangles. Empty GFX buffers break the toolkit's reader, so counts —
    // not payloads — are what get zeroed.
    public static byte[] BuildInvisible(byte[] templateBytes)
        => BuildInvisibleCore(
            templateBytes,
            Core.GameConfig.MinecraftStoryModeSeason2.ModernTextureToolkitGameName!,
            "v45");

    public static byte[] BuildInvisibleV46(byte[] templateBytes)
        => BuildInvisibleCore(
            templateBytes,
            Core.GameConfig.Batman.ModernTextureToolkitGameName!,
            "v46");

    private static byte[] BuildInvisibleCore(byte[] templateBytes, string toolkitGameName, string formatLabel)
    {
        var mesh = TelltaleToolkitMeshParser.ParseModernMeshRaw(templateBytes, toolkitGameName)
            ?? throw new InvalidDataException($"The Telltale Toolkit could not read the {formatLabel} template mesh.");
        var meshData = mesh.MeshData ?? throw new InvalidDataException($"{formatLabel} template has no T3MeshData.");

        MetaStreamParams streamParams;
        using (var paramStream = new MemoryStream(templateBytes))
        {
            streamParams = new BinaryMetaStreamReader(paramStream).Params;
        }

        var noiseRegions = ComputeNoiseRegions(templateBytes, mesh, streamParams);

        foreach (var lod in meshData.LODs ?? [])
        {
            foreach (var batchList in new[] { lod.Batches, lod.Batches1, lod.Batches2 })
            {
                foreach (var batch in batchList ?? [])
                {
                    batch.NumPrimitives = 0;
                    batch.NumIndices = 0;
                }
            }

            lod.NumPrimitives = 0;
        }

        byte[] output;
        using (var outputStream = new MemoryStream())
        {
            Toolkit.Instance.Serialize(mesh, outputStream, streamParams);
            output = outputStream.ToArray();
        }

        var syncLimit = Math.Min(AsyncSectionStart(templateBytes), AsyncSectionStart(output));
        foreach (var (start, length) in noiseRegions)
        {
            if (start + length <= syncLimit && start + length <= output.Length)
            {
                Array.Copy(templateBytes, start, output, start, length);
            }
        }

        return output;
    }

    // ── Primitive mapping ──
    // Distributes the GLB primitives over the template batches (a batch may receive several
    // primitives, merged into one draw range) and derives the texture that each template diffuse
    // name should carry — so geometry and texture always share the SAME mapping.
    private static (List<List<GltfPrimitive>> Groups, List<TextureAssignment> Textures) GroupPrimitivesForBatches(
        GltfModel model,
        List<T3MeshBatch> templateBatches,
        T3MeshData meshData,
        IReadOnlyList<string?>? batchTemplateDiffuse)
    {
        var candidates = model.Primitives;
        if (candidates.Count == 0)
        {
            throw new InvalidDataException("The GLB has no primitives.");
        }

        var batchCount = templateBatches.Count;
        var groups = new List<List<GltfPrimitive>>(batchCount);
        for (var b = 0; b < batchCount; b++)
        {
            groups.Add([]);
        }

        // Round-trip identity: every primitive carries the template submesh it came from and no two
        // primitives claim the SAME submesh. Batches with no primitive simply end up empty (the
        // extractor skips empty submeshes, so a scene mesh can legitimately return fewer).
        // Repeated indices mean the value is not identity information (single-asset exports emit
        // index 0 for every glTF mesh) — those fall through to the diffuse grouping below.
        var sourceIndices = candidates
            .Select(primitive => primitive.SourceSubmeshIndex)
            .ToList();
        var identityCovered = candidates.Count > 0 &&
            sourceIndices.All(index => index is { } value && value >= 0 && value < batchCount) &&
            sourceIndices.Select(index => index!.Value).Distinct().Count() == candidates.Count;
        if (identityCovered)
        {
            foreach (var primitive in candidates)
            {
                groups[primitive.SourceSubmeshIndex!.Value].Add(primitive);
            }

            return (groups, BuildTextureAssignments(groups, batchTemplateDiffuse, batchCount));
        }

        // Foreign model: group primitives by their diffuse texture and pair each group with a
        // template batch. Pairing prefers a shared name token (skin↔skinA, clothes↔clothesB,
        // hair↔hairA...), then falls back to first-appearance order; leftover groups merge into
        // the first (main) batch so no geometry is ever dropped.
        static string PrimitiveDiffuseKey(GltfPrimitive primitive) =>
            primitive.BaseColor?.Name ?? primitive.MaterialName ?? "";

        var primitiveGroups = new List<(string Key, List<GltfPrimitive> Primitives)>();
        var groupIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitive in candidates)
        {
            var key = PrimitiveDiffuseKey(primitive);
            if (!groupIndexByKey.TryGetValue(key, out var index))
            {
                index = primitiveGroups.Count;
                groupIndexByKey[key] = index;
                primitiveGroups.Add((key, []));
            }

            primitiveGroups[index].Primitives.Add(primitive);
        }

        // Distinct template diffuse identities in batch (first-appearance) order. When the caller
        // supplied resolved names, use them; otherwise fall back to the material handle CRC so the
        // order-based pairing still works.
        var slotNames = new List<string>();
        var batchSlotIndex = new int[batchCount];
        var slotIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var b = 0; b < batchCount; b++)
        {
            var name = batchTemplateDiffuse is not null && b < batchTemplateDiffuse.Count
                ? batchTemplateDiffuse[b]
                : null;
            name ??= meshData.Materials is { } materials &&
                     templateBatches[b].MaterialIndex >= 0 && templateBatches[b].MaterialIndex < materials.Count
                ? $"0x{materials[templateBatches[b].MaterialIndex].Material?.ObjectInfo?.ObjectName?.Crc64 ?? (ulong)b:X16}"
                : $"slot_{b}";
            if (!slotIndexByName.TryGetValue(name, out var slotIndex))
            {
                slotIndex = slotNames.Count;
                slotIndexByName[name] = slotIndex;
                slotNames.Add(name);
            }

            batchSlotIndex[b] = slotIndex;
        }

        // Pair GLB groups to slots: token similarity first, then order among the unpaired.
        var groupForSlot = new int[slotNames.Count];
        Array.Fill(groupForSlot, -1);
        var groupTaken = new bool[primitiveGroups.Count];
        for (var s = 0; s < slotNames.Count; s++)
        {
            var best = -1;
            var bestScore = 0;
            for (var g = 0; g < primitiveGroups.Count; g++)
            {
                if (groupTaken[g])
                {
                    continue;
                }

                var score = TextureNameTokenScore(slotNames[s], primitiveGroups[g].Key);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = g;
                }
            }

            if (best >= 0)
            {
                groupForSlot[s] = best;
                groupTaken[best] = true;
            }
        }

        for (var s = 0; s < slotNames.Count; s++)
        {
            if (groupForSlot[s] >= 0)
            {
                continue;
            }

            for (var g = 0; g < primitiveGroups.Count; g++)
            {
                if (!groupTaken[g])
                {
                    groupForSlot[s] = g;
                    groupTaken[g] = true;
                    break;
                }
            }
        }

        // First batch per slot receives the paired group; duplicate-slot batches stay empty.
        var slotAssignedToBatch = new bool[slotNames.Count];
        for (var b = 0; b < batchCount; b++)
        {
            var slot = batchSlotIndex[b];
            if (slotAssignedToBatch[slot] || groupForSlot[slot] < 0)
            {
                continue;
            }

            slotAssignedToBatch[slot] = true;
            groups[b].AddRange(primitiveGroups[groupForSlot[slot]].Primitives);
        }

        // Leftover groups (more textures than slots — should be rare with the auto-atlas) merge
        // into the first non-empty batch so their geometry still renders.
        var fallbackBatch = groups.FindIndex(group => group.Count > 0);
        if (fallbackBatch < 0)
        {
            fallbackBatch = 0;
        }

        for (var g = 0; g < primitiveGroups.Count; g++)
        {
            if (!groupTaken[g])
            {
                groups[fallbackBatch].AddRange(primitiveGroups[g].Primitives);
            }
        }

        return (groups, BuildTextureAssignments(groups, batchTemplateDiffuse, batchCount));
    }

    // Shared trailing-token similarity: "skM1_lukas100_clothes" vs "jesseFemale_clothesA" share the
    // token "clothes". Compares the alphabetic tokens of both names and scores the longest match.
    private static int TextureNameTokenScore(string templateName, string glbName)
    {
        static List<string> Tokens(string name) =>
            name.Split(['_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(piece => new string(piece.TakeWhile(char.IsLetter).ToArray()))
                .Where(token => token.Length >= 3)
                .Select(token => token.ToLowerInvariant())
                .ToList();

        var templateTokens = Tokens(templateName);
        var glbTokens = Tokens(glbName);
        var best = 0;
        foreach (var templateToken in templateTokens)
        {
            foreach (var glbToken in glbTokens)
            {
                if (templateToken == glbToken ||
                    templateToken.StartsWith(glbToken, StringComparison.Ordinal) ||
                    glbToken.StartsWith(templateToken, StringComparison.Ordinal))
                {
                    best = Math.Max(best, Math.Min(templateToken.Length, glbToken.Length));
                }
            }
        }

        return best;
    }

    // For each template diffuse name (when resolved), the texture image is the one carried by the
    // first primitive assigned to a batch using that name.
    private static List<TextureAssignment> BuildTextureAssignments(
        List<List<GltfPrimitive>> groups,
        IReadOnlyList<string?>? batchTemplateDiffuse,
        int batchCount)
    {
        var result = new List<TextureAssignment>();
        if (batchTemplateDiffuse is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var b = 0; b < batchCount && b < batchTemplateDiffuse.Count; b++)
        {
            var name = batchTemplateDiffuse[b];
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            var image = groups[b]
                .Select(primitive => primitive.BaseColor)
                .FirstOrDefault(baseColor => baseColor is not null);
            result.Add(new TextureAssignment(name, image));
        }

        return result;
    }

    // ── Vertex pool ──

    private struct PoolVertex : IEquatable<PoolVertex>
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Uv0, Uv1, Uv2, Uv3, Uv4, Uv5;
        public Vector4 Color;
        public int B0, B1, B2, B3;
        public float W0, W1, W2, W3;

        // Tangents are DERIVED per-primitive data and drift between the duplicated copies of a
        // shared vertex, so they are deliberately excluded from the weld identity (first one wins).
        public readonly bool Equals(PoolVertex other) =>
            Position == other.Position && Normal == other.Normal &&
            Uv0 == other.Uv0 && Uv1 == other.Uv1 && Uv2 == other.Uv2 && Uv3 == other.Uv3 &&
            Uv4 == other.Uv4 && Uv5 == other.Uv5 &&
            Color == other.Color &&
            B0 == other.B0 && B1 == other.B1 && B2 == other.B2 && B3 == other.B3 &&
            W0 == other.W0 && W1 == other.W1 && W2 == other.W2 && W3 == other.W3;

        public override readonly bool Equals(object? obj) => obj is PoolVertex other && Equals(other);

        public override readonly int GetHashCode() =>
            HashCode.Combine(Position, Normal, Uv0, HashCode.Combine(B0, B1, B2, B3, W0, W1));
    }

    private static (BoundingBox Box, Sphere Sphere) ComputeBoundsFromIndices(List<PoolVertex> pool, int[] poolIndices)
    {
        if (poolIndices.Length == 0)
        {
            return (new BoundingBox { Min = Vector3.Zero, Max = Vector3.Zero }, new Sphere { Center = Vector3.Zero, Radius = 0f });
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var index in poolIndices)
        {
            min = Vector3.Min(min, pool[index].Position);
            max = Vector3.Max(max, pool[index].Position);
        }

        var center = (min + max) * 0.5f;
        var radius = 0f;
        foreach (var index in poolIndices)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, pool[index].Position));
        }

        return (new BoundingBox { Min = min, Max = max }, new Sphere { Center = center, Radius = radius });
    }

    private static PoolVertex BuildPoolVertex(
        GltfPrimitive primitive,
        Vector4[] tangents,
        int v,
        GltfModel model,
        IReadOnlyDictionary<ulong, int> boneIndexByCrc)
    {
        var vertex = new PoolVertex
        {
            Position = primitive.Positions[v],
            Normal = primitive.Normals is { } normals && v < normals.Length ? SafeNormalize(normals[v]) : new Vector3(0, 1, 0),
            Tangent = v < tangents.Length ? tangents[v] : new Vector4(1, 0, 0, 1),
            Uv0 = UvOrDefault(primitive.Uv0, v),
            Uv1 = UvOrDefault(primitive.Uv1, v),
            Uv2 = UvOrDefault(primitive.Uv2, v),
            Uv3 = UvOrDefault(primitive.Uv3, v),
            Uv4 = UvOrDefault(primitive.Uv4, v),
            Uv5 = UvOrDefault(primitive.Uv5, v),
            Color = primitive.Color0 is { } colors && v < colors.Length ? colors[v] : new Vector4(1, 1, 1, 1),
            W0 = 1f,
        };

        if (primitive.Joints0 is { } joints && primitive.Weights0 is { } weights &&
            v * 4 + 3 < joints.Length && v < weights.Length)
        {
            int MapJoint(int skinJoint)
            {
                if (skinJoint < 0 || skinJoint >= model.Joints.Count)
                {
                    return 0;
                }

                var hash = model.Joints[skinJoint].Hash;
                if (hash is not { } crc)
                {
                    return 0;
                }

                if (boneIndexByCrc.TryGetValue(crc, out var boneIndex))
                {
                    return boneIndex;
                }

                // The bone is absent from THIS part's bone list (e.g. brow_L on a hair part that
                // only carries Head). Walk up the imported skeleton until an ancestor exists, so
                // the geometry follows the nearest real parent instead of collapsing onto index 0.
                var skeleton = model.Skeleton;
                if (skeleton is not null)
                {
                    var current = skeleton.Bones.FindIndex(bone => bone.Hash == crc);
                    var guard = 0;
                    while (current >= 0 && current < skeleton.Bones.Count && guard++ < 64)
                    {
                        var parentIndex = skeleton.Bones[current].ParentIndex;
                        if (parentIndex < 0 || parentIndex >= skeleton.Bones.Count)
                        {
                            break;
                        }

                        if (boneIndexByCrc.TryGetValue(skeleton.Bones[parentIndex].Hash, out var parentBone))
                        {
                            return parentBone;
                        }

                        current = parentIndex;
                    }
                }

                return 0;
            }

            vertex.B0 = MapJoint(joints[v * 4]);
            vertex.B1 = MapJoint(joints[v * 4 + 1]);
            vertex.B2 = MapJoint(joints[v * 4 + 2]);
            vertex.B3 = MapJoint(joints[v * 4 + 3]);
            var w = weights[v];
            var sum = w.X + w.Y + w.Z + w.W;
            if (sum <= 0f)
            {
                w = new Vector4(1, 0, 0, 0);
                sum = 1f;
            }

            vertex.W0 = w.X / sum;
            vertex.W1 = w.Y / sum;
            vertex.W2 = w.Z / sum;
            vertex.W3 = w.W / sum;
        }

        return vertex;
    }

    private static Dictionary<ulong, int> BuildBoneIndexMap(T3MeshData meshData)
    {
        var map = new Dictionary<ulong, int>();
        if (meshData.Bones is null)
        {
            return map;
        }

        for (var i = 0; i < meshData.Bones.Count; i++)
        {
            var symbol = meshData.Bones[i].BoneName;
            if (symbol is not null && !symbol.IsEmpty)
            {
                map.TryAdd(symbol.Crc64, i);
            }
        }

        return map;
    }

    private static Vector2 UvOrDefault(Vector2[]? uvs, int v)
        => uvs is not null && v < uvs.Length ? uvs[v] : Vector2.Zero;

    private static Vector3 SafeNormalize(Vector3 value)
    {
        var length = value.Length();
        return length > 1e-8f ? value / length : new Vector3(0, 1, 0);
    }

    // Standard per-triangle tangent accumulation over uv0 (Blender usually strips tangents).
    private static Vector4[] ComputeTangents(GltfPrimitive primitive)
    {
        var count = primitive.VertexCount;
        var accumulated = new Vector3[count];
        if (primitive.Uv0 is { } uvs && primitive.Normals is { } normals)
        {
            for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
            {
                int a = primitive.Indices[i], b = primitive.Indices[i + 1], c = primitive.Indices[i + 2];
                if (a >= count || b >= count || c >= count ||
                    a >= uvs.Length || b >= uvs.Length || c >= uvs.Length)
                {
                    continue;
                }

                var edge1 = primitive.Positions[b] - primitive.Positions[a];
                var edge2 = primitive.Positions[c] - primitive.Positions[a];
                var deltaUv1 = uvs[b] - uvs[a];
                var deltaUv2 = uvs[c] - uvs[a];
                var det = deltaUv1.X * deltaUv2.Y - deltaUv2.X * deltaUv1.Y;
                if (MathF.Abs(det) < 1e-10f)
                {
                    continue;
                }

                var r = 1f / det;
                var tangent = (edge1 * deltaUv2.Y - edge2 * deltaUv1.Y) * r;
                accumulated[a] += tangent;
                accumulated[b] += tangent;
                accumulated[c] += tangent;
            }
        }

        var result = new Vector4[count];
        for (var v = 0; v < count; v++)
        {
            var tangent = accumulated[v];
            if (tangent.LengthSquared() < 1e-10f)
            {
                result[v] = new Vector4(1, 0, 0, 1);
                continue;
            }

            tangent = Vector3.Normalize(tangent);
            if (primitive.Normals is { } normals2 && v < normals2.Length)
            {
                // Gram-Schmidt against the normal.
                var normal = SafeNormalize(normals2[v]);
                tangent -= normal * Vector3.Dot(normal, tangent);
                if (tangent.LengthSquared() < 1e-10f)
                {
                    result[v] = new Vector4(1, 0, 0, 1);
                    continue;
                }

                tangent = Vector3.Normalize(tangent);
            }

            result[v] = new Vector4(tangent, 1f);
        }

        return result;
    }

    // ── Buffer encoding ──

    private sealed record BatchGeometrySnapshot(
        uint MinVertIndex,
        uint MaxVertIndex,
        uint BaseIndex,
        uint StartIndex,
        uint NumPrimitives,
        uint NumIndices,
        uint AdjacencyStartIndex);

    private sealed class PreservedLodState
    {
        public required int OriginalLod0VertexCount { get; init; }
        public required int PreservedVertexCount { get; init; }
        public required int OriginalMainPrefixCount { get; init; }
        public required int OriginalShadowPrefixCount { get; init; }
        public required byte[][] VertexBuffers { get; init; }
        public required byte[][] IndexBuffers { get; init; }
        public required BatchGeometrySnapshot[] MainBatches { get; init; }
        public required BatchGeometrySnapshot[] ShadowBatches { get; init; }
        public required Vector2[] OriginalUvScales { get; init; }
        public required Vector2[] OriginalUvOffsets { get; init; }
        public int NewLod0VertexCount { get; set; }
        public int NewMainPrefixCount { get; set; }
        public int NewShadowPrefixCount { get; set; }
    }

    private static PreservedLodState CapturePreservedLodState(
        T3MeshData meshData,
        T3GFXVertexState vertexState,
        List<T3MeshBatch> lod0MainBatches)
    {
        if (vertexState.VertexBuffer is not { Count: > 0 } ||
            vertexState.IndexBuffer is not { Count: >= 2 })
        {
            throw new InvalidDataException(
                "Batman v46 reinsertion requires the template's vertex buffer and its main/shadow index buffers.");
        }

        var originalVertexCount = checked((int)meshData.VertexCount);
        var firstLowerVertex = (meshData.LODs ?? [])
            .Skip(1)
            .SelectMany(GetAllBatches)
            .Select(batch => (int)batch.BaseIndex)
            .Where(index => index > 0)
            .DefaultIfEmpty(originalVertexCount)
            .Min();
        firstLowerVertex = Math.Clamp(firstLowerVertex, 0, originalVertexCount);

        var mainBufferCount = checked((int)vertexState.IndexBuffer[0].Count);
        var shadowBufferCount = checked((int)vertexState.IndexBuffer[1].Count);
        var firstLowerMainIndex = (meshData.LODs ?? [])
            .Skip(1)
            .SelectMany(GetMainBatches)
            .Select(batch => (int)batch.StartIndex)
            .Where(index => index > 0)
            .DefaultIfEmpty(mainBufferCount)
            .Min();
        var firstLowerShadowIndex = (meshData.LODs ?? [])
            .Skip(1)
            .SelectMany(lod => lod.Batches2 ?? [])
            .Select(batch => (int)batch.StartIndex)
            .Where(index => index > 0)
            .DefaultIfEmpty(shadowBufferCount)
            .Min();

        return new PreservedLodState
        {
            OriginalLod0VertexCount = firstLowerVertex,
            PreservedVertexCount = originalVertexCount - firstLowerVertex,
            OriginalMainPrefixCount = Math.Clamp(firstLowerMainIndex, 0, mainBufferCount),
            OriginalShadowPrefixCount = Math.Clamp(firstLowerShadowIndex, 0, shadowBufferCount),
            VertexBuffers = vertexState.VertexBuffer
                .Select(buffer => (byte[])buffer.Buffer.Clone())
                .ToArray(),
            IndexBuffers = vertexState.IndexBuffer
                .Select(buffer => (byte[])buffer.Buffer.Clone())
                .ToArray(),
            MainBatches = lod0MainBatches.Select(Snapshot).ToArray(),
            ShadowBatches = (meshData.LODs![0].Batches2 ?? []).Select(Snapshot).ToArray(),
            OriginalUvScales = meshData.TexCoordTransform.Select(transform => transform.Scale).ToArray(),
            OriginalUvOffsets = meshData.TexCoordTransform.Select(transform => transform.Offset).ToArray(),
        };
    }

    private static IEnumerable<T3MeshBatch> GetMainBatches(T3MeshLOD lod)
        => lod.Batches is { Count: > 0 } ? lod.Batches : lod.Batches1 ?? [];

    private static IEnumerable<T3MeshBatch> GetAllBatches(T3MeshLOD lod)
        => GetMainBatches(lod).Concat(lod.Batches2 ?? []);

    private static BatchGeometrySnapshot Snapshot(T3MeshBatch batch)
        => new(
            batch.MinVertIndex,
            batch.MaxVertIndex,
            batch.BaseIndex,
            batch.StartIndex,
            batch.NumPrimitives,
            batch.NumIndices,
            batch.AdjacencyStartIndex);

    private static int EncodeV46BuffersPreservingLowerLods(
        T3MeshData meshData,
        T3GFXVertexState vertexState,
        T3MeshLOD lod0,
        List<T3MeshBatch> lod0MainBatches,
        PoolVertex[] pool,
        List<int> globalIndices,
        PreservedLodState state)
    {
        EncodeVertexBuffersPreservingSuffix(vertexState, meshData, pool, state);

        var mainPrefix = new List<int>(globalIndices.Count * 3);
        mainPrefix.AddRange(globalIndices);
        for (var batchIndex = 0; batchIndex < lod0MainBatches.Count; batchIndex++)
        {
            var batch = lod0MainBatches[batchIndex];
            var start = checked((int)batch.StartIndex);
            var count = checked((int)batch.NumIndices);
            batch.AdjacencyStartIndex = (uint)mainPrefix.Count;
            mainPrefix.AddRange(BuildTriangleAdjacency(globalIndices, start, count));
        }

        var shadowPrefix = new List<int>();
        if (lod0.Batches2 is { Count: > 0 })
        {
            for (var shadowIndex = 0; shadowIndex < lod0.Batches2.Count; shadowIndex++)
            {
                var shadow = lod0.Batches2[shadowIndex];
                BatchGeometrySnapshot? originalShadow = shadowIndex < state.ShadowBatches.Length
                    ? state.ShadowBatches[shadowIndex]
                    : null;
                var selected = SelectShadowMainBatches(state.MainBatches, originalShadow);
                var shadowVertices = new List<int>();
                var start = shadowPrefix.Count;
                foreach (var mainIndex in selected)
                {
                    if (mainIndex < 0 || mainIndex >= lod0MainBatches.Count)
                    {
                        continue;
                    }

                    var source = lod0MainBatches[mainIndex];
                    var sourceStart = checked((int)source.StartIndex);
                    var sourceCount = checked((int)source.NumIndices);
                    for (var i = sourceStart; i < sourceStart + sourceCount; i++)
                    {
                        var index = globalIndices[i];
                        shadowPrefix.Add(index);
                        shadowVertices.Add(index);
                    }
                }

                shadow.BaseIndex = 0;
                shadow.StartIndex = (uint)start;
                shadow.NumIndices = (uint)(shadowPrefix.Count - start);
                shadow.NumPrimitives = shadow.NumIndices / 3;
                shadow.MinVertIndex = shadowVertices.Count == 0 ? 0u : (uint)shadowVertices.Min();
                shadow.MaxVertIndex = shadowVertices.Count == 0 ? 0u : (uint)shadowVertices.Max();
                var (box, sphere) = ComputeBoundsFromIndices(pool.ToList(), shadowVertices.ToArray());
                shadow.BoundingBox = box;
                shadow.BoundingSphere = sphere;
            }
        }

        EncodeV46IndexBuffer(
            vertexState.IndexBuffer![0],
            mainPrefix,
            state.IndexBuffers[0],
            state.OriginalMainPrefixCount,
            pool.Length + state.PreservedVertexCount);
        EncodeV46IndexBuffer(
            vertexState.IndexBuffer[1],
            shadowPrefix,
            state.IndexBuffers[1],
            state.OriginalShadowPrefixCount,
            pool.Length + state.PreservedVertexCount);
        state.NewLod0VertexCount = pool.Length;
        state.NewMainPrefixCount = mainPrefix.Count;
        state.NewShadowPrefixCount = shadowPrefix.Count;

        var vertexDelta = pool.Length - state.OriginalLod0VertexCount;
        var mainIndexDelta = mainPrefix.Count - state.OriginalMainPrefixCount;
        var shadowIndexDelta = shadowPrefix.Count - state.OriginalShadowPrefixCount;
        foreach (var lowerLod in (meshData.LODs ?? []).Skip(1))
        {
            var shiftedMain = new HashSet<T3MeshBatch>(ReferenceEqualityComparer.Instance);
            foreach (var batchList in new[] { lowerLod.Batches, lowerLod.Batches1 })
            {
                foreach (var batch in batchList ?? [])
                {
                    if (!shiftedMain.Add(batch))
                    {
                        continue;
                    }

                    batch.BaseIndex = AddDelta(batch.BaseIndex, vertexDelta, "vertex base");
                    batch.StartIndex = AddDelta(batch.StartIndex, mainIndexDelta, "main index start");
                    if (batch.AdjacencyStartIndex != 0)
                    {
                        batch.AdjacencyStartIndex = AddDelta(
                            batch.AdjacencyStartIndex,
                            mainIndexDelta,
                            "adjacency index start");
                    }
                }
            }

            foreach (var batch in lowerLod.Batches2 ?? [])
            {
                batch.BaseIndex = AddDelta(batch.BaseIndex, vertexDelta, "shadow vertex base");
                batch.StartIndex = AddDelta(batch.StartIndex, shadowIndexDelta, "shadow index start");
            }
        }

        for (var bufferIndex = 2; bufferIndex < vertexState.IndexBuffer.Count; bufferIndex++)
        {
            vertexState.IndexBuffer[bufferIndex].Buffer = (byte[])state.IndexBuffers[bufferIndex].Clone();
            vertexState.IndexBuffer[bufferIndex].Count =
                (uint)(state.IndexBuffers[bufferIndex].Length / (int)vertexState.IndexBuffer[bufferIndex].Stride);
        }

        return shadowPrefix.Count / 3;
    }

    private static void ValidatePreservedV46Output(T3MeshData meshData, PreservedLodState state)
    {
        if (meshData.VertexStates is not { Count: > 0 })
        {
            throw new InvalidDataException("Batman v46 output lost its vertex state.");
        }

        var vertexState = meshData.VertexStates[0];
        if (vertexState.VertexBuffer is null || vertexState.IndexBuffer is not { Count: >= 2 })
        {
            throw new InvalidDataException("Batman v46 output lost its main vertex/index buffers.");
        }

        for (var bufferIndex = 0; bufferIndex < vertexState.VertexBuffer.Count; bufferIndex++)
        {
            var stride = checked((int)vertexState.VertexBuffer[bufferIndex].Stride);
            var expectedOffset = checked(state.OriginalLod0VertexCount * stride);
            var actualOffset = checked(state.NewLod0VertexCount * stride);
            var length = checked(state.PreservedVertexCount * stride);
            var expected = state.VertexBuffers[bufferIndex]
                .AsSpan(expectedOffset, length)
                .ToArray();
            var actual = vertexState.VertexBuffer[bufferIndex].Buffer
                .AsSpan(actualOffset, length)
                .ToArray();

            foreach (var attr in (vertexState.Attributes ?? [])
                         .Where(attribute =>
                             attribute.Attribute == GFXPlatformVertexAttribute.TexCoord &&
                             attribute.BufferIndex == (uint)bufferIndex))
            {
                var channel = checked((int)attr.AttributeIndex);
                var byteCount = UvFormatSize(attr);
                for (var lowerVertex = 0; lowerVertex < state.PreservedVertexCount; lowerVertex++)
                {
                    var localOffset = checked(lowerVertex * stride + (int)attr.BufferOffset);
                    var expectedUv = ReadPreservedUv(attr, state, lowerVertex);
                    var raw = ReadRawUv(actual, localOffset, attr);
                    var transform = meshData.TexCoordTransform[channel];
                    var actualUv = raw * transform.Scale + transform.Offset;
                    if (Vector2.Distance(expectedUv, actualUv) > 0.0015f)
                    {
                        throw new InvalidDataException(
                            $"Batman v46 output changed preserved lower-LOD UV channel {channel}.");
                    }

                    Array.Clear(expected, localOffset, byteCount);
                    Array.Clear(actual, localOffset, byteCount);
                }
            }

            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException(
                    $"Batman v46 output changed non-UV data in preserved lower-LOD vertex buffer {bufferIndex}.");
            }
        }

        ValidateIndexSuffix(0, state.OriginalMainPrefixCount, state.NewMainPrefixCount);
        ValidateIndexSuffix(1, state.OriginalShadowPrefixCount, state.NewShadowPrefixCount);
        return;

        void ValidateIndexSuffix(int bufferIndex, int originalPrefix, int newPrefix)
        {
            var buffer = vertexState.IndexBuffer[bufferIndex];
            var stride = checked((int)buffer.Stride);
            var expected = state.IndexBuffers[bufferIndex].AsSpan(originalPrefix * stride);
            var actual = buffer.Buffer.AsSpan(newPrefix * stride);
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidDataException(
                    $"Batman v46 output changed preserved lower-LOD index buffer {bufferIndex}.");
            }
        }

        static int UvFormatSize(GFXPlatformAttributeParams attr) => attr.Format switch
        {
            GFXPlatformFormat.F32x2 => 8,
            GFXPlatformFormat.F16x2 or GFXPlatformFormat.UN16x2 or GFXPlatformFormat.SN16x2 => 4,
            _ => throw Unsupported(attr),
        };

        static Vector2 ReadRawUv(byte[] bytes, int offset, GFXPlatformAttributeParams attr)
            => attr.Format switch
            {
                GFXPlatformFormat.F32x2 => new Vector2(
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4))),
                GFXPlatformFormat.F16x2 => new Vector2(
                    (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(offset)),
                    (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(offset + 2))),
                GFXPlatformFormat.UN16x2 => new Vector2(
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2)) / 65535f),
                GFXPlatformFormat.SN16x2 => new Vector2(
                    Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32767f),
                    Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 2)) / 32767f)),
                _ => throw Unsupported(attr),
            };
    }

    private static int[] SelectShadowMainBatches(
        BatchGeometrySnapshot[] mainBatches,
        BatchGeometrySnapshot? shadow)
    {
        if (shadow is null)
        {
            return Enumerable.Range(0, mainBatches.Length).ToArray();
        }

        var selected = mainBatches
            .Select((batch, index) => (batch, index))
            .Where(item =>
                item.batch.MinVertIndex >= shadow.MinVertIndex &&
                item.batch.MaxVertIndex <= shadow.MaxVertIndex)
            .Select(item => item.index)
            .ToArray();
        return selected.Length > 0 ? selected : Enumerable.Range(0, mainBatches.Length).ToArray();
    }

    private static List<int> BuildTriangleAdjacency(List<int> indices, int start, int count)
    {
        var triangleCount = count / 3;
        var oppositeByEdge = new Dictionary<(int A, int B), List<(int Triangle, int Opposite)>>();
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            var offset = start + triangle * 3;
            var a = indices[offset];
            var b = indices[offset + 1];
            var c = indices[offset + 2];
            AddEdge(a, b, triangle, c);
            AddEdge(b, c, triangle, a);
            AddEdge(c, a, triangle, b);
        }

        var result = new List<int>(triangleCount * 6);
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            var offset = start + triangle * 3;
            var a = indices[offset];
            var b = indices[offset + 1];
            var c = indices[offset + 2];
            result.Add(a);
            result.Add(Adjacent(a, b, triangle, c));
            result.Add(b);
            result.Add(Adjacent(b, c, triangle, a));
            result.Add(c);
            result.Add(Adjacent(c, a, triangle, b));
        }

        return result;

        void AddEdge(int a, int b, int triangle, int opposite)
        {
            var key = a <= b ? (a, b) : (b, a);
            if (!oppositeByEdge.TryGetValue(key, out var values))
            {
                values = [];
                oppositeByEdge[key] = values;
            }

            values.Add((triangle, opposite));
        }

        int Adjacent(int a, int b, int triangle, int boundaryOpposite)
        {
            var key = a <= b ? (a, b) : (b, a);
            foreach (var candidate in oppositeByEdge[key])
            {
                if (candidate.Triangle != triangle)
                {
                    return candidate.Opposite;
                }
            }

            return boundaryOpposite;
        }
    }

    private static void EncodeVertexBuffersPreservingSuffix(
        T3GFXVertexState vertexState,
        T3MeshData meshData,
        PoolVertex[] pool,
        PreservedLodState state)
    {
        if (vertexState.VertexBuffer is null || vertexState.Attributes is null)
        {
            throw new InvalidDataException("Batman v46 template vertex state has no buffers/attributes.");
        }

        FitTexCoordTransforms(vertexState, meshData, pool, allowRewrite: true, state);
        var totalVertexCount = pool.Length + state.PreservedVertexCount;
        var newBuffers = new byte[vertexState.VertexBuffer.Count][];
        for (var bufferIndex = 0; bufferIndex < vertexState.VertexBuffer.Count; bufferIndex++)
        {
            var stride = checked((int)vertexState.VertexBuffer[bufferIndex].Stride);
            var original = state.VertexBuffers[bufferIndex];
            var suffixOffset = checked(state.OriginalLod0VertexCount * stride);
            var suffixLength = checked(state.PreservedVertexCount * stride);
            if (suffixOffset + suffixLength > original.Length)
            {
                throw new InvalidDataException("Batman v46 lower-LOD vertex suffix is outside the template buffer.");
            }

            var target = new byte[checked(totalVertexCount * stride)];
            Array.Copy(original, suffixOffset, target, pool.Length * stride, suffixLength);
            newBuffers[bufferIndex] = target;
        }

        foreach (var attr in vertexState.Attributes)
        {
            var bufferIndex = checked((int)attr.BufferIndex);
            if (bufferIndex < 0 || bufferIndex >= newBuffers.Length)
            {
                continue;
            }

            var stride = checked((int)vertexState.VertexBuffer[bufferIndex].Stride);
            for (var vertex = 0; vertex < pool.Length; vertex++)
            {
                EncodeAttribute(
                    newBuffers[bufferIndex],
                    vertex * stride + checked((int)attr.BufferOffset),
                    attr,
                    pool[vertex],
                    meshData);
            }

            if (attr.Attribute == GFXPlatformVertexAttribute.TexCoord)
            {
                ReencodePreservedLowerLodUvs(
                    newBuffers[bufferIndex],
                    attr,
                    meshData,
                    state,
                    pool.Length,
                    stride);
            }
        }

        for (var bufferIndex = 0; bufferIndex < vertexState.VertexBuffer.Count; bufferIndex++)
        {
            vertexState.VertexBuffer[bufferIndex].Buffer = newBuffers[bufferIndex];
            vertexState.VertexBuffer[bufferIndex].Count = (uint)totalVertexCount;
        }
    }

    private static void ReencodePreservedLowerLodUvs(
        byte[] target,
        GFXPlatformAttributeParams attr,
        T3MeshData meshData,
        PreservedLodState state,
        int newLod0VertexCount,
        int stride)
    {
        var channel = checked((int)attr.AttributeIndex);
        if (channel >= state.OriginalUvScales.Length ||
            channel >= meshData.TexCoordTransform.Length)
        {
            return;
        }

        for (var lowerVertex = 0; lowerVertex < state.PreservedVertexCount; lowerVertex++)
        {
            var uv = ReadPreservedUv(attr, state, lowerVertex);
            var targetOffset =
                checked((newLod0VertexCount + lowerVertex) * stride + (int)attr.BufferOffset);
            EncodeUv(target, targetOffset, attr, uv, meshData);
        }
    }

    private static Vector2 ReadPreservedUv(
        GFXPlatformAttributeParams attr,
        PreservedLodState state,
        int lowerVertex)
    {
        var bufferIndex = checked((int)attr.BufferIndex);
        var channel = checked((int)attr.AttributeIndex);
        var original = state.VertexBuffers[bufferIndex];
        var vertexBufferLength = original.Length;
        var originalVertexCount = state.OriginalLod0VertexCount + state.PreservedVertexCount;
        var stride = vertexBufferLength / Math.Max(1, originalVertexCount);
        var offset = checked(
            (state.OriginalLod0VertexCount + lowerVertex) * stride +
            (int)attr.BufferOffset);
        Vector2 raw = attr.Format switch
        {
            GFXPlatformFormat.F32x2 => new Vector2(
                BinaryPrimitives.ReadSingleLittleEndian(original.AsSpan(offset)),
                BinaryPrimitives.ReadSingleLittleEndian(original.AsSpan(offset + 4))),
            GFXPlatformFormat.F16x2 => new Vector2(
                (float)BinaryPrimitives.ReadHalfLittleEndian(original.AsSpan(offset)),
                (float)BinaryPrimitives.ReadHalfLittleEndian(original.AsSpan(offset + 2))),
            GFXPlatformFormat.UN16x2 => new Vector2(
                BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(offset)) / 65535f,
                BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(offset + 2)) / 65535f),
            GFXPlatformFormat.SN16x2 => new Vector2(
                Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(offset)) / 32767f),
                Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(offset + 2)) / 32767f)),
            _ => throw Unsupported(attr),
        };

        var scale = state.OriginalUvScales[channel];
        var uvOffset = state.OriginalUvOffsets[channel];
        return raw * scale + uvOffset;
    }

    private static void EncodeV46IndexBuffer(
        T3GFXBuffer indexBuffer,
        IReadOnlyList<int> rebuiltPrefix,
        byte[] original,
        int originalPrefixCount,
        int vertexCount)
    {
        var stride = checked((int)indexBuffer.Stride);
        if (stride == 2 && vertexCount > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"The new Batman model has {vertexCount} vertices, but this mesh uses 16-bit indices (max 65535).");
        }

        var suffixOffset = checked(originalPrefixCount * stride);
        if (suffixOffset > original.Length)
        {
            throw new InvalidDataException("Batman v46 lower-LOD index suffix is outside the template buffer.");
        }

        var suffixLength = original.Length - suffixOffset;
        var bytes = new byte[checked(rebuiltPrefix.Count * stride + suffixLength)];
        for (var index = 0; index < rebuiltPrefix.Count; index++)
        {
            if (stride == 2)
            {
                WriteU16(bytes, index * 2, checked((ushort)rebuiltPrefix[index]));
            }
            else if (stride == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), checked((uint)rebuiltPrefix[index]));
            }
            else
            {
                throw new NotSupportedException($"Batman v46 index stride {stride} is not supported.");
            }
        }

        Array.Copy(original, suffixOffset, bytes, rebuiltPrefix.Count * stride, suffixLength);
        indexBuffer.Buffer = bytes;
        indexBuffer.Count = (uint)(bytes.Length / stride);
    }

    private static uint AddDelta(uint value, int delta, string field)
    {
        var result = (long)value + delta;
        if (result < 0 || result > uint.MaxValue)
        {
            throw new InvalidDataException($"Batman v46 {field} overflowed while preserving lower LODs.");
        }

        return (uint)result;
    }

    private static void EncodeVertexBuffers(T3GFXVertexState vertexState, T3MeshData meshData, PoolVertex[] pool)
    {
        if (vertexState.VertexBuffer is null || vertexState.Attributes is null)
        {
            throw new InvalidDataException("v45 template vertex state has no buffers/attributes.");
        }

        // Refit each used TexCoordTransform to the incoming UV range. Normalized UV formats
        // (UN16x2/SN16x2) can only store raw values in [0,1]; foreign models (or edits) whose final
        // UVs fall outside the template's scale/offset window would clamp and distort. The transform
        // is plain per-channel scale/offset data, so it is rewritten to exactly cover the new range
        // (a same-model round-trip reproduces the original window within float precision).
        FitTexCoordTransforms(vertexState, meshData, pool, allowRewrite: true);

        // Fresh zeroed buffers with the template strides.
        var newBuffers = new byte[vertexState.VertexBuffer.Count][];
        for (var b = 0; b < vertexState.VertexBuffer.Count; b++)
        {
            newBuffers[b] = new byte[pool.Length * (int)vertexState.VertexBuffer[b].Stride];
        }

        foreach (var attr in vertexState.Attributes)
        {
            var bufferIndex = (int)attr.BufferIndex;
            if (bufferIndex < 0 || bufferIndex >= newBuffers.Length)
            {
                continue;
            }

            var stride = (int)vertexState.VertexBuffer[bufferIndex].Stride;
            var target = newBuffers[bufferIndex];
            for (var v = 0; v < pool.Length; v++)
            {
                EncodeAttribute(target, v * stride + (int)attr.BufferOffset, attr, pool[v], meshData);
            }
        }

        for (var b = 0; b < vertexState.VertexBuffer.Count; b++)
        {
            vertexState.VertexBuffer[b].Buffer = newBuffers[b];
            vertexState.VertexBuffer[b].Count = (uint)pool.Length;
        }
    }

    private static void EncodeAttribute(byte[] target, int offset, GFXPlatformAttributeParams attr, in PoolVertex vertex, T3MeshData meshData)
    {
        switch (attr.Attribute)
        {
            case GFXPlatformVertexAttribute.Position:
                RequireFormat(attr, GFXPlatformFormat.F32x3);
                WriteF32(target, offset, vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                break;

            case GFXPlatformVertexAttribute.Normal:
                var normalValue = attr.AttributeIndex == 0
                    ? vertex.Normal
                    : SafeNormalize(
                        Vector3.Cross(
                            vertex.Normal,
                            new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z))) *
                      (vertex.Tangent.W < 0f ? -1f : 1f);
                EncodeSnormVector(target, offset, attr, normalValue, 0f);
                break;

            case GFXPlatformVertexAttribute.Tangent:
                EncodeSnormVector(target, offset, attr,
                    new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), vertex.Tangent.W);
                break;

            case GFXPlatformVertexAttribute.BlendWeight:
                EncodeWeights(target, offset, attr, vertex);
                break;

            case GFXPlatformVertexAttribute.BlendIndex:
                switch (attr.Format)
                {
                    case GFXPlatformFormat.U8x4:
                        target[offset] = ClampByte(vertex.B0);
                        target[offset + 1] = ClampByte(vertex.B1);
                        target[offset + 2] = ClampByte(vertex.B2);
                        target[offset + 3] = ClampByte(vertex.B3);
                        break;
                    // UN8x4 stores the bone index times 3 (the reader divides by 3); writing the
                    // raw index would make every vertex resolve to a different bone.
                    case GFXPlatformFormat.UN8x4:
                        target[offset] = ClampByte(vertex.B0 * 3);
                        target[offset + 1] = ClampByte(vertex.B1 * 3);
                        target[offset + 2] = ClampByte(vertex.B2 * 3);
                        target[offset + 3] = ClampByte(vertex.B3 * 3);
                        break;
                    case GFXPlatformFormat.U16x4:
                        WriteU16(target, offset, (ushort)Math.Clamp(vertex.B0, 0, ushort.MaxValue));
                        WriteU16(target, offset + 2, (ushort)Math.Clamp(vertex.B1, 0, ushort.MaxValue));
                        WriteU16(target, offset + 4, (ushort)Math.Clamp(vertex.B2, 0, ushort.MaxValue));
                        WriteU16(target, offset + 6, (ushort)Math.Clamp(vertex.B3, 0, ushort.MaxValue));
                        break;
                    default:
                        throw Unsupported(attr);
                }
                break;

            case GFXPlatformVertexAttribute.TexCoord:
                EncodeUv(target, offset, attr, GetUvChannel(vertex, (int)attr.AttributeIndex), meshData);
                break;

            case GFXPlatformVertexAttribute.Color:
                switch (attr.Format)
                {
                    case GFXPlatformFormat.UN8x4 or GFXPlatformFormat.D3DCOLOR:
                        target[offset] = UnormByte(vertex.Color.X);
                        target[offset + 1] = UnormByte(vertex.Color.Y);
                        target[offset + 2] = UnormByte(vertex.Color.Z);
                        target[offset + 3] = UnormByte(vertex.Color.W);
                        break;
                    case GFXPlatformFormat.F32x4:
                        WriteF32(target, offset, vertex.Color.X, vertex.Color.Y, vertex.Color.Z);
                        BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset + 12), vertex.Color.W);
                        break;
                    default:
                        throw Unsupported(attr);
                }
                break;

            default:
                // Unknown semantics keep their zeroed bytes; the game treats them as defaults.
                break;
        }
    }

    private static Vector2 GetUvChannel(in PoolVertex vertex, int channel) => channel switch
    {
        0 => vertex.Uv0,
        1 => vertex.Uv1,
        2 => vertex.Uv2,
        3 => vertex.Uv3,
        4 => vertex.Uv4,
        5 => vertex.Uv5,
        // Higher TexCoord semantics exist on some scene meshes (wind/lightmap channels) but carry
        // no glTF data; zero keeps them neutral instead of duplicating another channel's UVs.
        _ => Vector2.Zero,
    };

    // Normalized UV formats can only store raw values inside their own range, so a model whose
    // final UVs fall outside the template's scale/offset window would clamp and smear. The
    // transform is plain per-channel scale/offset data, so it is refitted to exactly cover the
    // incoming range — but ONLY when the template window cannot hold it (scene meshes with
    // tiling/lightmap channels keep their authored transform untouched).
    private static void FitTexCoordTransforms(
        T3GFXVertexState vertexState,
        T3MeshData meshData,
        PoolVertex[] pool,
        bool allowRewrite,
        PreservedLodState? preservedState = null)
    {
        if (pool.Length == 0 || vertexState.Attributes is null)
        {
            return;
        }

        foreach (var attr in vertexState.Attributes)
        {
            if (attr.Attribute != GFXPlatformVertexAttribute.TexCoord)
            {
                continue;
            }

            // Only channels 0..3 have a transform; F32x2 stores any value, so it never needs one.
            var channel = (int)attr.AttributeIndex;
            if (channel >= meshData.TexCoordTransform.Length || attr.Format == GFXPlatformFormat.F32x2)
            {
                continue;
            }

            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            var hasData = false;
            for (var v = 0; v < pool.Length; v++)
            {
                var uv = GetUvChannel(pool[v], channel);
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
                hasData = true;
            }
            if (preservedState is not null)
            {
                for (var lowerVertex = 0;
                     lowerVertex < preservedState.PreservedVertexCount;
                     lowerVertex++)
                {
                    var uv = ReadPreservedUv(attr, preservedState, lowerVertex);
                    min = Vector2.Min(min, uv);
                    max = Vector2.Max(max, uv);
                    hasData = true;
                }
            }

            if (!hasData)
            {
                continue;
            }

            // Raw range this format can represent.
            var signed = attr.Format is GFXPlatformFormat.SN16x2 or GFXPlatformFormat.F16x2;
            var rawMin = signed ? -1f : 0f;
            const float rawMax = 1f;

            var current = meshData.TexCoordTransform[channel];
            var currentScale = new Vector2(
                MathF.Abs(current.Scale.X) > 1e-8f ? current.Scale.X : 1f,
                MathF.Abs(current.Scale.Y) > 1e-8f ? current.Scale.Y : 1f);
            var rawLow = (min - current.Offset) / currentScale;
            var rawHigh = (max - current.Offset) / currentScale;
            const float tolerance = 0.002f;
            var fits =
                MathF.Min(rawLow.X, rawHigh.X) >= rawMin - tolerance &&
                MathF.Min(rawLow.Y, rawHigh.Y) >= rawMin - tolerance &&
                MathF.Max(rawLow.X, rawHigh.X) <= rawMax + tolerance &&
                MathF.Max(rawLow.Y, rawHigh.Y) <= rawMax + tolerance;
            if (fits)
            {
                continue;
            }

            var span = Vector2.Max(max - min, new Vector2(1e-5f));
            meshData.TexCoordTransform[channel] = signed
                ? new T3MeshTexCoordTransform { Scale = span * 0.5f, Offset = (min + max) * 0.5f }
                : new T3MeshTexCoordTransform { Scale = span, Offset = min };
        }
    }

    private static void EncodeUv(byte[] target, int offset, GFXPlatformAttributeParams attr, Vector2 uv, T3MeshData meshData)
    {
        // Invert the export transform. The parser produces V' = 1-(raw.v*scale+off) and the GLB
        // writer flips again (1-V'), so the glTF V that arrives here is raw.v*scale+off directly.
        var channel = Math.Min((int)attr.AttributeIndex, meshData.TexCoordTransform.Length - 1);
        var transform = meshData.TexCoordTransform[channel];
        var scaleX = MathF.Abs(transform.Scale.X) > 1e-8f ? transform.Scale.X : 1f;
        var scaleY = MathF.Abs(transform.Scale.Y) > 1e-8f ? transform.Scale.Y : 1f;
        var rawU = (uv.X - transform.Offset.X) / scaleX;
        var rawV = (uv.Y - transform.Offset.Y) / scaleY;

        switch (attr.Format)
        {
            case GFXPlatformFormat.F32x2:
                BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset), rawU);
                BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset + 4), rawV);
                break;
            case GFXPlatformFormat.F16x2:
                BinaryPrimitives.WriteHalfLittleEndian(target.AsSpan(offset), (Half)rawU);
                BinaryPrimitives.WriteHalfLittleEndian(target.AsSpan(offset + 2), (Half)rawV);
                break;
            case GFXPlatformFormat.UN16x2:
                WriteU16(target, offset, (ushort)Math.Clamp(MathF.Round(rawU * 65535f), 0f, 65535f));
                WriteU16(target, offset + 2, (ushort)Math.Clamp(MathF.Round(rawV * 65535f), 0f, 65535f));
                break;
            case GFXPlatformFormat.SN16x2:
                WriteU16(target, offset, unchecked((ushort)(short)Math.Clamp(MathF.Round(rawU * 32767f), -32767f, 32767f)));
                WriteU16(target, offset + 2, unchecked((ushort)(short)Math.Clamp(MathF.Round(rawV * 32767f), -32767f, 32767f)));
                break;
            default:
                throw Unsupported(attr);
        }
    }

    private static void EncodeSnormVector(byte[] target, int offset, GFXPlatformAttributeParams attr, Vector3 value, float w)
    {
        switch (attr.Format)
        {
            case GFXPlatformFormat.F32x3:
                WriteF32(target, offset, value.X, value.Y, value.Z);
                break;
            case GFXPlatformFormat.SN8x4:
            case GFXPlatformFormat.UN8x4:
                target[offset] = SnormByte(value.X);
                target[offset + 1] = SnormByte(value.Y);
                target[offset + 2] = SnormByte(value.Z);
                target[offset + 3] = SnormByte(w);
                break;
            case GFXPlatformFormat.SN16x4:
            case GFXPlatformFormat.UN16x4:
                WriteU16(target, offset, unchecked((ushort)(short)Math.Clamp(MathF.Round(value.X * 32767f), -32767f, 32767f)));
                WriteU16(target, offset + 2, unchecked((ushort)(short)Math.Clamp(MathF.Round(value.Y * 32767f), -32767f, 32767f)));
                WriteU16(target, offset + 4, unchecked((ushort)(short)Math.Clamp(MathF.Round(value.Z * 32767f), -32767f, 32767f)));
                WriteU16(target, offset + 6, unchecked((ushort)(short)Math.Clamp(MathF.Round(w * 32767f), -32767f, 32767f)));
                break;
            default:
                throw Unsupported(attr);
        }
    }

    private static void EncodeWeights(byte[] target, int offset, GFXPlatformAttributeParams attr, in PoolVertex vertex)
    {
        switch (attr.Format)
        {
            case GFXPlatformFormat.UN16x4:
            {
                // Round while keeping the sum exactly at 65535 (adjust the largest weight).
                var w0 = (int)MathF.Round(vertex.W0 * 65535f);
                var w1 = (int)MathF.Round(vertex.W1 * 65535f);
                var w2 = (int)MathF.Round(vertex.W2 * 65535f);
                var w3 = (int)MathF.Round(vertex.W3 * 65535f);
                var drift = 65535 - (w0 + w1 + w2 + w3);
                if (w0 >= w1 && w0 >= w2 && w0 >= w3) w0 += drift;
                else if (w1 >= w2 && w1 >= w3) w1 += drift;
                else if (w2 >= w3) w2 += drift;
                else w3 += drift;
                WriteU16(target, offset, (ushort)Math.Clamp(w0, 0, 65535));
                WriteU16(target, offset + 2, (ushort)Math.Clamp(w1, 0, 65535));
                WriteU16(target, offset + 4, (ushort)Math.Clamp(w2, 0, 65535));
                WriteU16(target, offset + 6, (ushort)Math.Clamp(w3, 0, 65535));
                break;
            }
            case GFXPlatformFormat.UN8x4:
                target[offset] = UnormByte(vertex.W0);
                target[offset + 1] = UnormByte(vertex.W1);
                target[offset + 2] = UnormByte(vertex.W2);
                target[offset + 3] = UnormByte(vertex.W3);
                break;
            case GFXPlatformFormat.F32x4:
                WriteF32(target, offset, vertex.W0, vertex.W1, vertex.W2);
                BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset + 12), vertex.W3);
                break;
            default:
                throw Unsupported(attr);
        }
    }

    private static void EncodeIndexBuffers(T3GFXVertexState vertexState, List<int> globalIndices, int vertexCount)
    {
        if (vertexState.IndexBuffer is not { Count: > 0 })
        {
            throw new InvalidDataException("v45 template has no index buffer.");
        }

        var stride = (int)vertexState.IndexBuffer[0].Stride;
        if (stride == 2 && vertexCount > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"The new model has {vertexCount} vertices, but this mesh uses 16-bit indices (max 65535).");
        }

        var bytes = new byte[globalIndices.Count * stride];
        for (var i = 0; i < globalIndices.Count; i++)
        {
            if (stride == 2)
            {
                WriteU16(bytes, i * 2, (ushort)globalIndices[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)globalIndices[i]);
            }
        }

        // Every index buffer gets the same rebuilt content (buffer 1 is the shadow list; identical
        // ordering keeps the shadow pass valid for the rebuilt geometry).
        foreach (var indexBuffer in vertexState.IndexBuffer)
        {
            indexBuffer.Buffer = (byte[])bytes.Clone();
            indexBuffer.Count = (uint)globalIndices.Count;
        }
    }

    // ── Bounds / helpers ──

    private static (BoundingBox Box, Sphere Sphere) ComputeBounds(PoolVertex[] pool, int start, int count)
    {
        if (count <= 0)
        {
            return (new BoundingBox { Min = Vector3.Zero, Max = Vector3.Zero }, new Sphere { Center = Vector3.Zero, Radius = 0f });
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = start; i < start + count; i++)
        {
            min = Vector3.Min(min, pool[i].Position);
            max = Vector3.Max(max, pool[i].Position);
        }

        var center = (min + max) * 0.5f;
        var radius = 0f;
        for (var i = start; i < start + count; i++)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, pool[i].Position));
        }

        return (new BoundingBox { Min = min, Max = max }, new Sphere { Center = center, Radius = radius });
    }

    private static void CopyBatchGeometry(T3MeshBatch source, T3MeshBatch target)
    {
        target.MinVertIndex = source.MinVertIndex;
        target.MaxVertIndex = source.MaxVertIndex;
        target.BaseIndex = source.BaseIndex;
        target.StartIndex = source.StartIndex;
        target.NumPrimitives = source.NumPrimitives;
        target.NumIndices = source.NumIndices;
        target.BoundingBox = source.BoundingBox;
        target.BoundingSphere = source.BoundingSphere;
    }

    private static List<(int Start, int Length)> ComputeNoiseRegions(byte[] template, D3DMesh mesh, MetaStreamParams streamParams)
    {
        byte[] control;
        using (var controlStream = new MemoryStream())
        {
            Toolkit.Instance.Serialize(mesh, controlStream, streamParams);
            control = controlStream.ToArray();
        }

        if (control.Length != template.Length)
        {
            throw new InvalidDataException(
                "The toolkit's rewrite of this v45 template changed the file size; reinsertion would corrupt it. " +
                "Please report this mesh.");
        }

        var regions = new List<(int, int)>();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == control[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < template.Length && template[i] != control[i])
            {
                i++;
            }

            regions.Add((start, i - start));
        }

        return regions;
    }

    private static int AsyncSectionStart(byte[] data)
    {
        if (data.Length < 20)
        {
            return data.Length;
        }

        var metaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var versionCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var metaStart = 20 + versionCount * 12;
        var asyncStart = metaStart + metaSize;
        return asyncStart > 0 && asyncStart <= data.Length ? asyncStart : data.Length;
    }

    private static NotSupportedException Unsupported(GFXPlatformAttributeParams attr)
        => new($"v45 reinsertion does not support attribute {attr.Attribute}[{attr.AttributeIndex}] in format {attr.Format} yet.");

    private static void RequireFormat(GFXPlatformAttributeParams attr, GFXPlatformFormat expected)
    {
        if (attr.Format != expected)
        {
            throw Unsupported(attr);
        }
    }

    private static void WriteF32(byte[] target, int offset, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset), x);
        BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset + 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset + 8), z);
    }

    private static void WriteU16(byte[] target, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), value);

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static byte UnormByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    private static byte SnormByte(float value)
        => unchecked((byte)(sbyte)Math.Clamp(MathF.Round(value * 127f), -127f, 127f));
}
