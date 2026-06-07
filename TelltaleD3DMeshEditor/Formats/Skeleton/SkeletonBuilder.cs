using System.Numerics;
using TelltaleD3DMeshEditor.Core;
using TelltaleToolKit.T3Types;
using TtkSkeleton = TelltaleToolKit.T3Types.Skeletons.Skeleton;
using TtkTransform = TelltaleToolKit.T3Types.Skeletons.Transform;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Builds a complete, valid Telltale skeleton from scratch out of a generic joint set — for a foreign
// rig (e.g. a model downloaded from the internet) that has no matching original .skl to merge with.
// Names, hierarchy and the local pose come from the input; the game-specific rich fields the game
// tolerates as defaults are left neutral. Real game skeletons carry these as zero/identity anyway:
// RestXform, BoneDir, BoneLength and BoneRotationAdjustment were all default across every tested
// skeleton, so a from-scratch skeleton matches that shape. Translation scales are set to 1 (no
// scaling) and mirror bones are cleared (a foreign rig has no engine mirror pairs).
//
// Caveat (by design): the engine drives animations by bone name, so a foreign rig only animates with
// the game's own animations when its bone names match the original skeleton. With different names the
// model still loads and shows in its bind pose, but in-game animations won't line up.
public static class SkeletonBuilder
{
    public static TtkSkeleton Build(SkeletonData skeleton)
    {
        var result = new TtkSkeleton();

        foreach (var bone in skeleton.Bones)
        {
            var jointSymbol = ResolveBoneSymbol(bone);
            var parentSymbol = ResolveParentSymbol(bone, skeleton);
            result.Entries.Add(new TtkSkeleton.Entry
            {
                JointNameS = jointSymbol,
                JointName = ResolveBoneName(bone, jointSymbol),
                ParentNameS = parentSymbol,
                ParentName = parentSymbol.DebugString ?? BoneHashDatabase.Resolve(parentSymbol.Crc64) ?? "",
                ParentIndex = bone.ParentIndex,
                MirrorBoneNameS = Symbol.Empty,
                MirrorBoneName = "",
                MirrorBoneIndex = -1,
                LocalPosition = new Vector3(bone.X, bone.Y, bone.Z),
                LocalQuat = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw),
                BoneRotationAdjustment = Quaternion.Identity,
                RestXform = new TtkTransform(),
                GlobalTranslationScale = Vector3.One,
                LocalTranslationScale = Vector3.One,
                AnimTranslationScale = Vector3.One,
            });
        }

        return result;
    }

    private static Symbol ResolveBoneSymbol(BoneData bone)
        => string.IsNullOrEmpty(bone.Name) ? Symbol.FromCrc64(bone.Hash) : Symbol.FromName(bone.Name);

    private static string ResolveBoneName(BoneData bone, Symbol symbol)
        => !string.IsNullOrEmpty(bone.Name)
            ? bone.Name
            : symbol.DebugString ?? BoneHashDatabase.Resolve(symbol.Crc64) ?? $"bone_{symbol.Crc64:X16}";

    private static Symbol ResolveParentSymbol(BoneData bone, SkeletonData skeleton)
    {
        if (bone.ParentHash != 0)
        {
            return Symbol.FromCrc64(bone.ParentHash);
        }

        if (bone.ParentIndex >= 0 && bone.ParentIndex < skeleton.Bones.Count)
        {
            var parent = skeleton.Bones[bone.ParentIndex];
            return string.IsNullOrEmpty(parent.Name) ? Symbol.FromCrc64(parent.Hash) : Symbol.FromName(parent.Name);
        }

        return Symbol.Empty;
    }
}
