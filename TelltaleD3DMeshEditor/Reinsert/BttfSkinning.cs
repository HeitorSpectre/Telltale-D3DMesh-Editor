using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Shared skinning helpers for the Back to the Future (v1) reinsertion path: resolving a GLB joint to its
// Telltale bone hash, and collecting the distinct weighted bones a primitive uses. Used by both the
// part/palette mapper (to keep each submesh's palette within the engine's per-draw bone limit) and the
// mesh writer (to build palettes and per-vertex bone indices), so the two always agree.
public static class BttfSkinning
{
    public const float WeightEpsilon = 1e-6f;

    public static ulong ResolveJointHash(int joint, GltfModel model, SkeletonData? skeleton)
    {
        if (joint >= 0 && joint < model.Joints.Count)
        {
            var gltfJoint = model.Joints[joint];
            if (gltfJoint.Hash is { } explicitHash)
            {
                return explicitHash;
            }

            if (!string.IsNullOrWhiteSpace(gltfJoint.Name) && skeleton is not null)
            {
                var byName = skeleton.Bones.FirstOrDefault(bone =>
                    string.Equals(bone.Name, gltfJoint.Name, StringComparison.OrdinalIgnoreCase));
                if (byName is not null)
                {
                    return byName.Hash;
                }
            }
        }

        if (skeleton is not null && joint >= 0 && joint < skeleton.Bones.Count)
        {
            return skeleton.Bones[joint].Hash;
        }

        return 0;
    }

    // Distinct weighted bone hashes a primitive uses, in first-seen order (the order palettes are built in).
    public static List<ulong> GetWeightedBoneHashes(GltfPrimitive prim, GltfModel model, SkeletonData? skeleton)
    {
        var result = new List<ulong>();
        var seen = new HashSet<ulong>();
        if (prim.Joints0 is null || prim.Weights0 is null ||
            prim.Joints0.Length < prim.VertexCount * 4 ||
            prim.Weights0.Length != prim.VertexCount)
        {
            return result;
        }

        for (var v = 0; v < prim.VertexCount; v++)
        {
            var w = prim.Weights0[v];
            var weights = new[] { w.X, w.Y, w.Z, w.W };
            for (var k = 0; k < 4; k++)
            {
                if (weights[k] <= WeightEpsilon)
                {
                    continue;
                }

                var hash = ResolveJointHash(prim.Joints0[v * 4 + k], model, skeleton);
                if (hash != 0 && seen.Add(hash))
                {
                    result.Add(hash);
                }
            }
        }

        return result;
    }
}
