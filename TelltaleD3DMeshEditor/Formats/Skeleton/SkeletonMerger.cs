using System.Numerics;
using TelltaleToolKit.T3Types;
using TtkSkeleton = TelltaleToolKit.T3Types.Skeletons.Skeleton;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Merges an edited joint set (e.g. parsed back from a reimported GLB) onto the original full-fidelity
// skeleton read by the toolkit. The original stays the source of truth for every rich field the GLB
// can't carry (constraints, mirror bones, rest transform, bone dir/length, flags). Only a joint whose
// local transform actually moved gets its LocalPos/LocalQuat updated, and brand-new joints are
// appended. So an untouched skeleton rebuilds byte-for-byte identical to the game's original .skl,
// while genuine edits are applied faithfully — no copy, no guesswork on unchanged data.
public static class SkeletonMerger
{
    // Joints within this tolerance of the original are treated as unchanged, so float noise from a GLB
    // round-trip never rewrites a joint (which would break the byte-exact guarantee).
    private const float TransformEpsilon = 1e-5f;

    public static TtkSkeleton Merge(TtkSkeleton original, SkeletonData edited)
    {
        var knownHashes = original.Entries
            .Where(entry => entry.JointName is not null)
            .Select(entry => entry.JointName.Crc64)
            .ToHashSet();

        var editedByHash = edited.Bones
            .GroupBy(bone => bone.Hash)
            .ToDictionary(group => group.Key, group => group.First());

        // Update transforms of existing joints that actually moved; leave everything else untouched.
        foreach (var entry in original.Entries)
        {
            if (entry.JointName is not null &&
                editedByHash.TryGetValue(entry.JointName.Crc64, out var bone) &&
                HasMoved(entry, bone))
            {
                entry.LocalPosition = new Vector3(bone.X, bone.Y, bone.Z);
                entry.LocalQuat = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw);
            }
        }

        // Append joints that exist in the edit but not in the original (newly added bones).
        foreach (var bone in edited.Bones)
        {
            if (knownHashes.Add(bone.Hash))
            {
                original.Entries.Add(CreateEntry(bone, original));
            }
        }

        return original;
    }

    private static bool HasMoved(TtkSkeleton.Entry entry, BoneData bone)
        => Math.Abs(entry.LocalPosition.X - bone.X) > TransformEpsilon
        || Math.Abs(entry.LocalPosition.Y - bone.Y) > TransformEpsilon
        || Math.Abs(entry.LocalPosition.Z - bone.Z) > TransformEpsilon
        || Math.Abs(entry.LocalQuat.X - bone.Qx) > TransformEpsilon
        || Math.Abs(entry.LocalQuat.Y - bone.Qy) > TransformEpsilon
        || Math.Abs(entry.LocalQuat.Z - bone.Qz) > TransformEpsilon
        || Math.Abs(entry.LocalQuat.W - bone.Qw) > TransformEpsilon;

    private static TtkSkeleton.Entry CreateEntry(BoneData bone, TtkSkeleton original)
    {
        var parentIndex = bone.ParentHash != 0
            ? original.Entries.FindIndex(entry => entry.JointName is not null && entry.JointName.Crc64 == bone.ParentHash)
            : bone.ParentIndex;

        return new TtkSkeleton.Entry
        {
            JointName = string.IsNullOrEmpty(bone.Name) ? Symbol.FromCrc64(bone.Hash) : Symbol.FromName(bone.Name),
            ParentName = bone.ParentHash != 0 ? Symbol.FromCrc64(bone.ParentHash) : Symbol.Empty,
            ParentIndex = parentIndex,
            LocalPosition = new Vector3(bone.X, bone.Y, bone.Z),
            LocalQuat = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw),
        };
    }
}
