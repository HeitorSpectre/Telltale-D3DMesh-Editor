using System.Buffers.Binary;
using System.Numerics;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Geometry reimporter for Back to the Future (.d3dmesh version 1, "ERTM"). Unlike the V13/17 path
// (MeshReinserter), v1 stores each vertex attribute in its own uncompressed float buffer and the
// faces in a separate section, so this writer keeps the template head (bounds, submesh table,
// material/texture descriptors) byte-for-byte except for the patched numeric fields, then rebuilds
// the tail: a raw (format 0) face section plus one vertex buffer per template attribute, reproducing
// the template's exact type/stride/flags schema with the imported geometry.
//
// Skinned meshes (Phase 2): the bone-palette block is rebuilt from the imported model's own bones (one
// palette per submesh), each submesh's bone-set index is repointed to its palette, and the weight
// (type 4) and bone-index (type 5) buffers are written from the GLB skin. The .skl is rebuilt
// separately by BttfSkeletonWriter.
public static class BttfMeshReinserter
{
    public static byte[] Reinsert(BttfMeshLayout layout, GltfModel model, IReadOnlyList<IReadOnlyList<GltfPrimitive>> groups, SkeletonData? skeleton = null)
    {
        if (model.Primitives.Count == 0)
        {
            throw new InvalidOperationException("GLB has no mesh primitives to reimport.");
        }

        if (layout.Submeshes.Count == 0)
        {
            throw new InvalidOperationException("Template has no usable submesh table.");
        }

        if (groups.Count != layout.Submeshes.Count)
        {
            throw new InvalidOperationException(
                $"Primitive grouping ({groups.Count}) does not match the template submesh count ({layout.Submeshes.Count}).");
        }

        var skinned = layout.IsSkinned;
        if (skinned && !layout.HasPaletteBlock)
        {
            throw new NotSupportedException("This skinned Back to the Future mesh has no locatable bone-palette block to rebuild.");
        }

        var verts = new List<EncVertex>();
        var faceIndices = new List<int>();
        var subInfo = new SubmeshPatch[layout.Submeshes.Count];
        var palettes = new List<ulong[]>(layout.Submeshes.Count);

        for (var s = 0; s < layout.Submeshes.Count; s++)
        {
            var vertexStart = verts.Count;
            var faceStart = faceIndices.Count;

            // Build this submesh's bone palette from the bones its primitives actually weight, then map
            // each GLB joint to a local palette index. Static submeshes get an empty palette.
            var paletteHashes = new List<ulong>();
            var hashToLocal = new Dictionary<ulong, int>();
            if (skinned)
            {
                foreach (var prim in groups[s])
                {
                    foreach (var jointHash in BttfSkinning.GetWeightedBoneHashes(prim, model, skeleton))
                    {
                        if (jointHash != 0 && !hashToLocal.ContainsKey(jointHash))
                        {
                            hashToLocal[jointHash] = paletteHashes.Count;
                            paletteHashes.Add(jointHash);
                        }
                    }
                }

                if (paletteHashes.Count > 64)
                {
                    throw new InvalidOperationException(
                        $"Submesh {s} weights {paletteHashes.Count} distinct bones; Back to the Future v1 palettes hold at most 64.");
                }

                // An empty submesh (no primitives mapped) must still get a non-empty palette: a zero-bone
                // palette breaks the contiguous palette run (the reader requires count >= 1). Bind it to the
                // skeleton root; nothing references it.
                if (paletteHashes.Count == 0 && skeleton is { Bones.Count: > 0 })
                {
                    paletteHashes.Add(skeleton.Bones[0].Hash);
                }
            }

            palettes.Add(paletteHashes.ToArray());

            foreach (var prim in groups[s])
            {
                var baseVertex = verts.Count;
                var normals = HasChannel(prim.Normals, prim.VertexCount) ? prim.Normals! : ComputeNormals(prim);
                var (tangents, binormals) = BuildTangentFrame(prim, normals);

                for (var i = 0; i < prim.VertexCount; i++)
                {
                    var uv0 = Uv(prim.Uv0, prim.VertexCount, i);
                    var skin = skinned ? BuildVertexSkin(prim, i, hashToLocal, model, skeleton) : VertexSkin.Rigid;
                    verts.Add(new EncVertex
                    {
                        Position = prim.Positions[i],
                        Normal = normals[i],
                        Tangent = tangents[i],
                        Binormal = binormals[i],
                        Uv0 = uv0,
                        Uv1 = HasChannel(prim.Uv1, prim.VertexCount) ? prim.Uv1![i] : uv0,
                        Uv2 = HasChannel(prim.Uv2, prim.VertexCount) ? prim.Uv2![i] : uv0,
                        Uv3 = HasChannel(prim.Uv3, prim.VertexCount) ? prim.Uv3![i] : uv0,
                        Skin = skin,
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

                    faceIndices.Add(baseVertex + a);
                    faceIndices.Add(baseVertex + b);
                    faceIndices.Add(baseVertex + c);
                }
            }

            var hasVerts = verts.Count > vertexStart;
            subInfo[s] = new SubmeshPatch(
                FieldBaseOffset: layout.Submeshes[s].FieldBaseOffset,
                BoneSetFieldOffset: layout.Submeshes[s].BoneSetFieldOffset,
                PaletteIndex: s,
                VertexMin: vertexStart,
                VertexMax: hasVerts ? verts.Count - 1 : vertexStart,
                FaceStart: faceStart,
                PolygonCount: (faceIndices.Count - faceStart) / 3);
        }

        if (verts.Count == 0)
        {
            throw new InvalidOperationException("No vertices were assigned to the Back to the Future submeshes.");
        }

        if (verts.Count > 65535)
        {
            throw new InvalidOperationException(
                $"Model has {verts.Count} vertices; Back to the Future v1 uses uint16 indices (max 65535). Split or decimate the mesh.");
        }

        var head = BuildHead(layout, skinned, palettes);
        PatchBounds(head, layout.BoundsOffset, verts);
        foreach (var patch in subInfo)
        {
            if (patch.FieldBaseOffset >= 0 && patch.FieldBaseOffset + 16 <= head.Length)
            {
                WriteU32(head, patch.FieldBaseOffset, patch.VertexMin);
                WriteU32(head, patch.FieldBaseOffset + 4, patch.VertexMax);
                WriteU32(head, patch.FieldBaseOffset + 8, patch.FaceStart);
                WriteU32(head, patch.FieldBaseOffset + 12, patch.PolygonCount);
            }

            // Repoint each submesh to its rebuilt palette (skinned only).
            if (skinned && patch.BoneSetFieldOffset >= 0 && patch.BoneSetFieldOffset + 4 <= head.Length)
            {
                WriteU32(head, patch.BoneSetFieldOffset, patch.PaletteIndex);
            }
        }

        var tail = BuildTail(layout, verts, faceIndices);

        var result = new byte[head.Length + tail.Length];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(tail, 0, result, head.Length, tail.Length);
        return result;
    }

    // Head = everything before the face section. For skinned meshes the bone-palette block is replaced
    // with one rebuilt from the imported model (size may differ), so the head is reassembled as
    // [pre-palette] + [new palette block] + [post-palette .. face section]. For static meshes (or when no
    // palette block was located) the head is kept verbatim.
    private static byte[] BuildHead(BttfMeshLayout layout, bool skinned, List<ulong[]> palettes)
    {
        if (!skinned || !layout.HasPaletteBlock)
        {
            return layout.Original.AsSpan(0, layout.FaceSectionOffset).ToArray();
        }

        var newPalette = BuildPaletteBlock(palettes);
        var pre = layout.PaletteBlockOffset;
        var postLength = layout.FaceSectionOffset - layout.PaletteBlockEnd;
        var head = new byte[pre + newPalette.Length + postLength];
        Buffer.BlockCopy(layout.Original, 0, head, 0, pre);
        Buffer.BlockCopy(newPalette, 0, head, pre, newPalette.Length);
        Buffer.BlockCopy(layout.Original, layout.PaletteBlockEnd, head, pre + newPalette.Length, postLength);
        return head;
    }

    // [size u32][paletteCount u32][ per palette: boneCount u32 + boneCount × (low u32, high u32, pad u32) ].
    // size counts itself and the count field, matching the template's bone-palette block layout.
    private static byte[] BuildPaletteBlock(List<ulong[]> palettes)
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        var size = 8 + palettes.Sum(palette => 4 + palette.Length * 12);
        writer.Write(size);
        writer.Write(palettes.Count);
        foreach (var palette in palettes)
        {
            writer.Write(palette.Length);
            foreach (var hash in palette)
            {
                writer.Write((uint)(hash & 0xFFFFFFFF));
                writer.Write((uint)(hash >> 32));
                writer.Write(0u);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildTail(BttfMeshLayout layout, List<EncVertex> verts, List<int> faceIndices)
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        // Face section header: "30 65" marker + 3 template bytes, then count + format(0=raw) + body.
        writer.Write(layout.Original, layout.FaceSectionOffset, 5);
        writer.Write(faceIndices.Count);
        writer.Write(0); // raw uint16 indices
        foreach (var index in faceIndices)
        {
            writer.Write((ushort)index);
        }

        // Reproduce the template's vertex-buffer schema (type/stride/flags), one occurrence at a time.
        var typeOccurrence = new Dictionary<int, int>();
        foreach (var buffer in layout.VertexBuffers)
        {
            var occurrence = typeOccurrence.TryGetValue(buffer.Type, out var seen) ? seen : 0;
            typeOccurrence[buffer.Type] = occurrence + 1;

            writer.Write(verts.Count);
            writer.Write(buffer.Stride);
            writer.Write(buffer.Type);
            writer.Write(buffer.Flags);
            foreach (var vertex in verts)
            {
                WriteVertexAttribute(writer, buffer.Type, buffer.Stride, occurrence, vertex);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    // Writes exactly `stride` bytes for one vertex of the given attribute buffer, zero-padding any
    // bytes beyond the fields we know how to fill. Mirrors BackToTheFutureMeshParser's read side.
    private static void WriteVertexAttribute(BinaryWriter writer, int type, int stride, int occurrence, EncVertex vertex)
    {
        var written = 0;
        switch (type)
        {
            case 1: // position (float3)
                written = WriteVec3(writer, vertex.Position);
                break;
            case 2: // first occurrence = normal, second = binormal (float3)
                written = WriteVec3(writer, occurrence == 0 ? vertex.Normal : vertex.Binormal);
                break;
            case 3: // uv (float2). The GLB already carries the file's native V (the extractor's 1-v and
                    // the glTF export's 1-v cancel out), so write it directly like the V13/17 writer does.
                var uv = occurrence switch
                {
                    0 => vertex.Uv0,
                    1 => vertex.Uv1,
                    2 => vertex.Uv2,
                    _ => vertex.Uv3,
                };
                writer.Write(uv.X);
                writer.Write(uv.Y);
                written = 8;
                break;
            case 4: // blend weights: 3 floats (the 4th is derived as 1 - w0 - w1 - w2 on read).
                writer.Write(vertex.Skin.Weight0);
                writer.Write(vertex.Skin.Weight1);
                writer.Write(vertex.Skin.Weight2);
                written = 12;
                break;
            case 5: // blend indices: 4 bytes, each = local palette index × 4 (the read side divides by 4).
                writer.Write((byte)(vertex.Skin.Bone0 * 4));
                writer.Write((byte)(vertex.Skin.Bone1 * 4));
                writer.Write((byte)(vertex.Skin.Bone2 * 4));
                writer.Write((byte)(vertex.Skin.Bone3 * 4));
                written = 4;
                break;
            case 6: // vertex colour / unknown 4-byte field; the read side ignores it, keep opaque white
                writer.Write(0xFFFFFFFFu);
                written = 4;
                break;
            default:
                break;
        }

        for (var pad = written; pad < stride; pad++)
        {
            writer.Write((byte)0);
        }
    }

    private static VertexSkin BuildVertexSkin(GltfPrimitive prim, int vertex, IReadOnlyDictionary<ulong, int> hashToLocal, GltfModel model, SkeletonData? skeleton)
    {
        if (prim.Joints0 is null || prim.Weights0 is null)
        {
            return VertexSkin.Rigid;
        }

        var w = prim.Weights0[vertex];
        var weights = new[] { w.X, w.Y, w.Z, w.W };
        var influences = new List<(int Local, float Weight)>(4);
        for (var k = 0; k < 4; k++)
        {
            if (weights[k] <= 1e-6f)
            {
                continue;
            }

            var hash = BttfSkinning.ResolveJointHash(prim.Joints0[vertex * 4 + k], model, skeleton);
            if (hash != 0 && hashToLocal.TryGetValue(hash, out var local))
            {
                influences.Add((local, weights[k]));
            }
        }

        if (influences.Count == 0)
        {
            return VertexSkin.Rigid;
        }

        var sum = influences.Sum(influence => influence.Weight);
        if (sum <= 1e-6f)
        {
            return VertexSkin.Rigid;
        }

        var skin = new VertexSkin();
        for (var i = 0; i < 4 && i < influences.Count; i++)
        {
            var normalized = influences[i].Weight / sum;
            switch (i)
            {
                case 0: skin.Bone0 = influences[0].Local; skin.Weight0 = normalized; break;
                case 1: skin.Bone1 = influences[1].Local; skin.Weight1 = normalized; break;
                case 2: skin.Bone2 = influences[2].Local; skin.Weight2 = normalized; break;
                default: skin.Bone3 = influences[3].Local; break; // 4th weight is derived on read
            }
        }

        return skin;
    }

    private static int WriteVec3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        return 12;
    }

    private static void PatchBounds(byte[] head, int boundsOffset, List<EncVertex> verts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in verts)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        WriteFloat(head, boundsOffset, min.X);
        WriteFloat(head, boundsOffset + 4, min.Y);
        WriteFloat(head, boundsOffset + 8, min.Z);
        WriteFloat(head, boundsOffset + 12, max.X);
        WriteFloat(head, boundsOffset + 16, max.Y);
        WriteFloat(head, boundsOffset + 20, max.Z);
    }

    private static Vector2 Uv(Vector2[]? uv, int vertexCount, int index)
        => HasChannel(uv, vertexCount) ? uv![index] : Vector2.Zero;

    private static bool HasChannel<T>(T[]? channel, int vertexCount)
        => channel is not null && channel.Length == vertexCount;

    private static bool IsValidTriangle(int a, int b, int c, int vertexCount)
        => a != b && b != c && c != a &&
           a >= 0 && b >= 0 && c >= 0 &&
           a < vertexCount && b < vertexCount && c < vertexCount;

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

            var faceNormal = Vector3.Cross(prim.Positions[b] - prim.Positions[a], prim.Positions[c] - prim.Positions[a]);
            normals[a] += faceNormal;
            normals[b] += faceNormal;
            normals[c] += faceNormal;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : new Vector3(0, 0, 1);
        }

        return normals;
    }

    // Builds per-vertex tangent and binormal frames from UV0 gradients (Lengyel's method), used for the
    // template's secondary normal-type buffer. Falls back to an arbitrary perpendicular when UVs are flat.
    private static (Vector3[] Tangents, Vector3[] Binormals) BuildTangentFrame(GltfPrimitive prim, Vector3[] normals)
    {
        var tangents = new Vector3[prim.VertexCount];
        var binormals = new Vector3[prim.VertexCount];

        if (HasChannel(prim.Binormals, prim.VertexCount))
        {
            for (var i = 0; i < prim.VertexCount; i++)
            {
                binormals[i] = new Vector3(prim.Binormals![i].X, prim.Binormals[i].Y, prim.Binormals[i].Z);
            }
        }

        var accum = new Vector3[prim.VertexCount];
        var uv = prim.Uv0;
        if (HasChannel(uv, prim.VertexCount))
        {
            for (var i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                var a = prim.Indices[i];
                var b = prim.Indices[i + 1];
                var c = prim.Indices[i + 2];
                if (!IsValidTriangle(a, b, c, prim.VertexCount))
                {
                    continue;
                }

                var e1 = prim.Positions[b] - prim.Positions[a];
                var e2 = prim.Positions[c] - prim.Positions[a];
                var du1 = uv![b].X - uv[a].X;
                var dv1 = uv[b].Y - uv[a].Y;
                var du2 = uv[c].X - uv[a].X;
                var dv2 = uv[c].Y - uv[a].Y;
                var denom = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(denom) < 1e-12f)
                {
                    continue;
                }

                var r = 1f / denom;
                var tangent = (e1 * dv2 - e2 * dv1) * r;
                accum[a] += tangent;
                accum[b] += tangent;
                accum[c] += tangent;
            }
        }

        for (var i = 0; i < prim.VertexCount; i++)
        {
            var n = normals[i];
            var t = accum[i] - n * Vector3.Dot(n, accum[i]);
            if (t.LengthSquared() < 1e-12f)
            {
                t = Vector3.Cross(n, MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX);
            }

            t = Vector3.Normalize(t);
            tangents[i] = t;
            if (binormals[i].LengthSquared() < 1e-12f)
            {
                binormals[i] = Vector3.Normalize(Vector3.Cross(n, t));
            }
        }

        return (tangents, binormals);
    }

    private static void WriteU32(byte[] buffer, int offset, int value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), unchecked((uint)value));

    private static void WriteFloat(byte[] buffer, int offset, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), value);

    private readonly record struct SubmeshPatch(
        int FieldBaseOffset,
        int BoneSetFieldOffset,
        int PaletteIndex,
        int VertexMin,
        int VertexMax,
        int FaceStart,
        int PolygonCount);

    private struct VertexSkin
    {
        public int Bone0;
        public int Bone1;
        public int Bone2;
        public int Bone3;
        public float Weight0;
        public float Weight1;
        public float Weight2;

        // A vertex with no resolved skin binds fully to local bone 0.
        public static VertexSkin Rigid => new() { Weight0 = 1f };
    }

    private struct EncVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector3 Binormal;
        public Vector2 Uv0;
        public Vector2 Uv1;
        public Vector2 Uv2;
        public Vector2 Uv3;
        public VertexSkin Skin;
    }
}
