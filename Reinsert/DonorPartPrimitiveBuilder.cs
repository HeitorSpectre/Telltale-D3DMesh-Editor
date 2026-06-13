using System.Numerics;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Converts a parsed .d3dmesh into reimport-ready GltfPrimitives, so a character's sibling part files
// (e.g. the MCSM mouth visemes, which exist next to the imported model's source meshes but are never
// inside a combined GLB) can be ported through the same template-patch path as real GLB primitives.
// Joints are emitted as indices into the reference skeleton — the convention MeshReinserter already
// resolves through TryResolveJointHash when the model carries no glTF joint list.
public static class DonorPartPrimitiveBuilder
{
    public static List<GltfPrimitive> Build(MeshData mesh, SkeletonData skeleton)
    {
        var indexByHash = new Dictionary<ulong, ushort>();
        for (var i = 0; i < skeleton.Bones.Count && i <= ushort.MaxValue; i++)
        {
            indexByHash.TryAdd(skeleton.Bones[i].Hash, (ushort)i);
        }

        var result = new List<GltfPrimitive>();
        foreach (var submesh in mesh.Submeshes)
        {
            var count = submesh.Vertices.Count;
            if (count == 0 || submesh.Faces.Count == 0)
            {
                continue;
            }

            var palette = submesh.BonePaletteIndex >= 0 && submesh.BonePaletteIndex < mesh.BonePalettes.Count
                ? mesh.BonePalettes[submesh.BonePaletteIndex]
                : [];

            var positions = new Vector3[count];
            var normals = new Vector3[count];
            var uv0 = new Vector2[count];
            var uv1 = new Vector2[count];
            var uv2 = new Vector2[count];
            var uv3 = new Vector2[count];
            var colors = new Vector4[count];
            var joints = new ushort[count * 4];
            var weights = new Vector4[count];

            for (var i = 0; i < count; i++)
            {
                var v = submesh.Vertices[i];
                positions[i] = new Vector3(v.X, v.Y, v.Z);
                normals[i] = new Vector3(v.Nx, v.Ny, v.Nz);
                // Parsed V is in Telltale sample space; primitives carry glTF V (top-left), the same
                // 1-V flip AssetGltfBuilder applies on export.
                uv0[i] = new Vector2(v.U, 1f - v.V);
                uv1[i] = new Vector2(v.U2, 1f - v.V2);
                uv2[i] = new Vector2(v.U3, 1f - v.V3);
                uv3[i] = new Vector2(v.U4, 1f - v.V4);
                colors[i] = new Vector4(v.ColorR, v.ColorG, v.ColorB, v.ColorA);

                var rawBones = new[] { v.Bone0, v.Bone1, v.Bone2, v.Bone3 };
                var rawWeights = new[] { v.Weight0, v.Weight1, v.Weight2, v.Weight3 };
                for (var influence = 0; influence < 4; influence++)
                {
                    var jointIndex = (ushort)0;
                    var weight = rawWeights[influence];
                    var local = BoneIndexConvention.ToPaletteIndex(rawBones[influence], mesh.Version);
                    if (local >= 0 &&
                        local < palette.Length &&
                        indexByHash.TryGetValue(palette[local], out var bySkeleton))
                    {
                        jointIndex = bySkeleton;
                    }
                    else if (weight > 0.000001f)
                    {
                        // A bone the reference skeleton does not know cannot drive anything sensible.
                        weight = 0f;
                    }

                    joints[i * 4 + influence] = jointIndex;
                    rawWeights[influence] = weight;
                }

                weights[i] = new Vector4(rawWeights[0], rawWeights[1], rawWeights[2], rawWeights[3]);
            }

            var indices = new int[submesh.Faces.Count * 3];
            for (var i = 0; i < submesh.Faces.Count; i++)
            {
                var (a, b, c) = submesh.Faces[i];
                indices[i * 3] = a;
                indices[i * 3 + 1] = b;
                indices[i * 3 + 2] = c;
            }

            result.Add(new GltfPrimitive
            {
                Positions = positions,
                Normals = normals,
                Uv0 = uv0,
                Uv1 = uv1,
                Uv2 = uv2,
                Uv3 = uv3,
                Color0 = colors,
                Joints0 = joints,
                Weights0 = weights,
                Indices = indices,
                MaterialName = submesh.MaterialName,
                IsSkinned = palette.Length > 0,
            });
        }

        return result;
    }
}
