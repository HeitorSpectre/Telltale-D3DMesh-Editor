using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

internal static class BttfPrimitiveSplitter
{
    public const int HardPaletteLimit = 64;

    public static List<GltfPrimitive> SplitByBonePalette(
        IReadOnlyList<GltfPrimitive> primitives,
        GltfModel model,
        SkeletonData? skeleton,
        int maxBones = HardPaletteLimit)
    {
        if (maxBones <= 0)
        {
            maxBones = HardPaletteLimit;
        }

        var result = new List<GltfPrimitive>(primitives.Count);
        foreach (var primitive in primitives)
        {
            if (BttfSkinning.GetWeightedBoneHashes(primitive, model, skeleton).Count <= maxBones)
            {
                result.Add(primitive);
                continue;
            }

            result.AddRange(SplitPrimitive(primitive, model, skeleton, maxBones));
        }

        return result;
    }

    private static IEnumerable<GltfPrimitive> SplitPrimitive(
        GltfPrimitive primitive,
        GltfModel model,
        SkeletonData? skeleton,
        int maxBones)
    {
        var triangles = BuildTriangles(primitive, model, skeleton);
        if (triangles.Count == 0)
        {
            yield return primitive;
            yield break;
        }

        var batch = new List<TriangleRef>();
        var batchBones = new HashSet<ulong>();
        foreach (var triangle in triangles.OrderByDescending(triangle => triangle.Bones.Count))
        {
            if (batch.Count > 0 && CountUnion(batchBones, triangle.Bones) > maxBones)
            {
                yield return BuildPrimitiveSlice(primitive, batch);
                batch.Clear();
                batchBones.Clear();
            }

            batch.Add(triangle);
            foreach (var bone in triangle.Bones)
            {
                batchBones.Add(bone);
            }
        }

        if (batch.Count > 0)
        {
            yield return BuildPrimitiveSlice(primitive, batch);
        }
    }

    private static List<TriangleRef> BuildTriangles(GltfPrimitive primitive, GltfModel model, SkeletonData? skeleton)
    {
        var triangles = new List<TriangleRef>(primitive.Indices.Length / 3);
        for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
        {
            var a = primitive.Indices[i];
            var b = primitive.Indices[i + 1];
            var c = primitive.Indices[i + 2];
            if (a < 0 || b < 0 || c < 0 ||
                a >= primitive.VertexCount || b >= primitive.VertexCount || c >= primitive.VertexCount ||
                a == b || b == c || a == c)
            {
                continue;
            }

            var bones = new HashSet<ulong>();
            AddVertexBones(primitive, a, model, skeleton, bones);
            AddVertexBones(primitive, b, model, skeleton, bones);
            AddVertexBones(primitive, c, model, skeleton, bones);
            triangles.Add(new TriangleRef(a, b, c, bones));
        }

        return triangles;
    }

    private static void AddVertexBones(
        GltfPrimitive primitive,
        int vertex,
        GltfModel model,
        SkeletonData? skeleton,
        HashSet<ulong> bones)
    {
        if (primitive.Joints0 is null || primitive.Weights0 is null ||
            primitive.Joints0.Length < primitive.VertexCount * 4 ||
            primitive.Weights0.Length != primitive.VertexCount)
        {
            return;
        }

        var weights = primitive.Weights0[vertex];
        var offset = vertex * 4;
        AddInfluence(primitive.Joints0[offset], weights.X);
        AddInfluence(primitive.Joints0[offset + 1], weights.Y);
        AddInfluence(primitive.Joints0[offset + 2], weights.Z);
        AddInfluence(primitive.Joints0[offset + 3], weights.W);

        void AddInfluence(ushort joint, float weight)
        {
            if (weight <= 1e-6f)
            {
                return;
            }

            var hash = BttfSkinning.ResolveJointHash(joint, model, skeleton);
            if (hash != 0)
            {
                bones.Add(hash);
            }
        }
    }

    private static int CountUnion(HashSet<ulong> existing, IReadOnlyCollection<ulong> incoming)
    {
        var count = existing.Count;
        foreach (var bone in incoming)
        {
            if (!existing.Contains(bone))
            {
                count++;
            }
        }

        return count;
    }

    private static GltfPrimitive BuildPrimitiveSlice(GltfPrimitive source, IReadOnlyList<TriangleRef> triangles)
    {
        var vertexMap = new Dictionary<int, int>();
        var sourceVertices = new List<int>();
        var indices = new int[triangles.Count * 3];
        var write = 0;

        foreach (var triangle in triangles)
        {
            indices[write++] = MapVertex(triangle.A);
            indices[write++] = MapVertex(triangle.B);
            indices[write++] = MapVertex(triangle.C);
        }

        return new GltfPrimitive
        {
            Positions = SliceRequiredChannel(source.Positions, sourceVertices),
            Normals = SliceChannel(source.Normals, sourceVertices),
            Uv0 = SliceChannel(source.Uv0, sourceVertices),
            Uv1 = SliceChannel(source.Uv1, sourceVertices),
            Uv2 = SliceChannel(source.Uv2, sourceVertices),
            Uv3 = SliceChannel(source.Uv3, sourceVertices),
            Color0 = SliceChannel(source.Color0, sourceVertices),
            Tangents = SliceChannel(source.Tangents, sourceVertices),
            Binormals = SliceChannel(source.Binormals, sourceVertices),
            Unknown1 = SliceChannel(source.Unknown1, sourceVertices),
            Joints0 = SliceJoints(source.Joints0, sourceVertices),
            Weights0 = SliceChannel(source.Weights0, sourceVertices),
            Indices = indices,
            MaterialName = source.MaterialName,
            BonePaletteIndex = source.BonePaletteIndex,
            SourceMeshPath = source.SourceMeshPath,
            SourceSubmeshIndex = source.SourceSubmeshIndex,
            RecoveredDetailLineTextureName = source.RecoveredDetailLineTextureName,
            RecoveredDetailLineImage = source.RecoveredDetailLineImage,
            IsSkinned = source.IsSkinned,
            BaseColor = source.BaseColor,
            TextureSlots = source.TextureSlots,
            ReferencedTextures = source.ReferencedTextures,
        };

        int MapVertex(int sourceIndex)
        {
            if (vertexMap.TryGetValue(sourceIndex, out var mapped))
            {
                return mapped;
            }

            mapped = sourceVertices.Count;
            vertexMap[sourceIndex] = mapped;
            sourceVertices.Add(sourceIndex);
            return mapped;
        }
    }

    private static T[]? SliceChannel<T>(T[]? source, IReadOnlyList<int> sourceVertices)
    {
        if (source is null)
        {
            return null;
        }

        var result = new T[sourceVertices.Count];
        for (var i = 0; i < sourceVertices.Count; i++)
        {
            result[i] = source[sourceVertices[i]];
        }

        return result;
    }

    private static T[] SliceRequiredChannel<T>(T[] source, IReadOnlyList<int> sourceVertices)
    {
        var result = new T[sourceVertices.Count];
        for (var i = 0; i < sourceVertices.Count; i++)
        {
            result[i] = source[sourceVertices[i]];
        }

        return result;
    }

    private static ushort[]? SliceJoints(ushort[]? source, IReadOnlyList<int> sourceVertices)
    {
        if (source is null)
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

    private sealed record TriangleRef(int A, int B, int C, HashSet<ulong> Bones);
}
