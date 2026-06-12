using System.Numerics;
using TelltaleD3DMeshEditor.Core;
using TelltaleToolKit.T3Types;
using TtkSkeleton = TelltaleToolKit.T3Types.Skeletons.Skeleton;
using TtkTransform = TelltaleToolKit.T3Types.Skeletons.Transform;

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
        var knownHashes = new HashSet<ulong>();
        var outputIndexByHash = new Dictionary<ulong, int>();
        for (var i = 0; i < original.Entries.Count; i++)
        {
            var name = original.Entries[i].JointName;
            if (name is null)
            {
                continue;
            }

            knownHashes.Add(name.Crc64);
            outputIndexByHash.TryAdd(name.Crc64, i);
        }

        var editedByHash = edited.Bones
            .GroupBy(bone => bone.Hash)
            .ToDictionary(group => group.Key, group => group.First());
        var editedByName = edited.Bones
            .Where(bone => !string.IsNullOrWhiteSpace(bone.Name))
            .GroupBy(bone => bone.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var editedWorlds = BuildWorldMatrices(edited);
        var editedWorldByHash = new Dictionary<ulong, Matrix4x4>();
        for (var i = 0; i < edited.Bones.Count; i++)
        {
            editedWorldByHash.TryAdd(edited.Bones[i].Hash, editedWorlds[i]);
        }

        var outputWorlds = new List<Matrix4x4>(original.Entries.Count + edited.Bones.Count);
        var refreshedEntries = new HashSet<int>();

        // Native skeletons keep RestXform/BoneLength/BoneDir at zero; games whose procedural rigs
        // (e.g. the MCSM eye look-at) read those fields need them preserved untouched, so a moved
        // joint only gets its local pose updated there.
        var preserveDerivedFields = GameConfig.Current.PreserveSkeletonDerivedFieldsOnMerge;

        // Update existing joints so their final bind-pose world transform matches the edited rig.
        // This is safer than copying local TRS directly when a GLB has helper nodes or a different
        // joint parent chain, because the game skeleton keeps its original hierarchy.
        for (var i = 0; i < original.Entries.Count; i++)
        {
            var entry = original.Entries[i];
            if (TryFindEditedBone(entry, editedByHash, editedByName, out var bone))
            {
                knownHashes.Add(bone.Hash);
                outputIndexByHash.TryAdd(bone.Hash, i);
                if (editedWorldByHash.TryGetValue(bone.Hash, out var targetWorld) &&
                    TryGetLocalTransform(targetWorld, entry.ParentIndex, outputWorlds, out var position, out var rotation) &&
                    HasMoved(entry, position, rotation))
                {
                    entry.LocalPosition = position;
                    entry.LocalQuat = NormalizeRotation(rotation);
                    if (!preserveDerivedFields)
                    {
                        refreshedEntries.Add(i);
                    }
                }
            }

            outputWorlds.Add(BuildEntryWorld(entry, outputWorlds));
        }

        // Append joints that exist in the edit but not in the original (newly added bones).
        foreach (var bone in edited.Bones)
        {
            if (knownHashes.Add(bone.Hash))
            {
                var parentIndex = ResolveOutputParentIndex(bone, edited, outputIndexByHash);
                var targetWorld = editedWorldByHash.TryGetValue(bone.Hash, out var world)
                    ? world
                    : BuildLocalMatrix(new Vector3(bone.X, bone.Y, bone.Z), new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw));
                TryGetLocalTransform(targetWorld, parentIndex, outputWorlds, out var position, out var rotation);
                var entry = CreateEntry(bone, original, parentIndex, position, rotation);
                original.Entries.Add(entry);
                var entryIndex = original.Entries.Count - 1;
                outputIndexByHash.TryAdd(bone.Hash, entryIndex);
                outputWorlds.Add(BuildEntryWorld(entry, outputWorlds));
                refreshedEntries.Add(entryIndex);
            }
        }

        RefreshDerivedFields(original, refreshedEntries);
        return original;
    }

    // Replaces the target skeleton's per-bone translation scales (Global/Local/AnimTranslationScale)
    // with the donor character's, matched by joint hash. These scales retarget canonical animation
    // translations to the character's proportions; after a character swap the bind pose is the
    // donor's, so the donor's scales are the consistent set (e.g. MCSM Petra Y=1.17 over Aiden's
    // bind raises the eye pivot ~5cm and hides the pupil behind the hair).
    public static void AdoptTranslationScales(TtkSkeleton target, SkeletonData donor)
    {
        var donorByHash = donor.Bones
            .GroupBy(bone => bone.Hash)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var entry in target.Entries)
        {
            if (entry.JointName is null || !donorByHash.TryGetValue(entry.JointName.Crc64, out var bone))
            {
                continue;
            }

            entry.GlobalTranslationScale = bone.GlobalTranslationScale;
            entry.LocalTranslationScale = bone.LocalTranslationScale;
            entry.AnimTranslationScale = bone.AnimTranslationScale;
        }
    }

    private static bool TryFindEditedBone(
        TtkSkeleton.Entry entry,
        IReadOnlyDictionary<ulong, BoneData> editedByHash,
        IReadOnlyDictionary<string, BoneData> editedByName,
        out BoneData bone)
    {
        if (entry.JointName is not null &&
            editedByHash.TryGetValue(entry.JointName.Crc64, out bone!))
        {
            return true;
        }

        var name = GetSymbolName(entry.JointName);
        if (!string.IsNullOrWhiteSpace(name) &&
            editedByName.TryGetValue(name, out bone!))
        {
            return true;
        }

        bone = null!;
        return false;
    }

    private static bool HasMoved(TtkSkeleton.Entry entry, Vector3 position, Quaternion rotation)
        => Math.Abs(entry.LocalPosition.X - position.X) > TransformEpsilon
        || Math.Abs(entry.LocalPosition.Y - position.Y) > TransformEpsilon
        || Math.Abs(entry.LocalPosition.Z - position.Z) > TransformEpsilon
        || RotationDelta(entry.LocalQuat, rotation) > TransformEpsilon;

    private static float RotationDelta(Quaternion a, Quaternion b)
    {
        a = NormalizeRotation(a);
        b = NormalizeRotation(b);
        var direct =
            Math.Abs(a.X - b.X) +
            Math.Abs(a.Y - b.Y) +
            Math.Abs(a.Z - b.Z) +
            Math.Abs(a.W - b.W);
        var negated =
            Math.Abs(a.X + b.X) +
            Math.Abs(a.Y + b.Y) +
            Math.Abs(a.Z + b.Z) +
            Math.Abs(a.W + b.W);
        return Math.Min(direct, negated);
    }

    private static TtkSkeleton.Entry CreateEntry(
        BoneData bone,
        TtkSkeleton original,
        int parentIndex,
        Vector3 position,
        Quaternion rotation)
    {
        var jointSymbol = ResolveBoneSymbol(bone);
        var parentSymbol = ResolveParentSymbol(bone, original, parentIndex);
        return new TtkSkeleton.Entry
        {
            JointName = jointSymbol,
            ParentName = parentSymbol,
            ParentIndex = parentIndex,
            MirrorBoneName = Symbol.Empty,
            MirrorBoneIndex = -1,
            LocalPosition = position,
            LocalQuat = rotation,
            BoneRotationAdjustment = Quaternion.Identity,
            RestXform = new TtkTransform
            {
                Translation = position,
                Rotation = rotation,
            },
            GlobalTranslationScale = Vector3.One,
            LocalTranslationScale = Vector3.One,
            AnimTranslationScale = Vector3.One,
        };
    }

    private static Symbol ResolveBoneSymbol(BoneData bone)
        => string.IsNullOrEmpty(bone.Name) ? Symbol.FromCrc64(bone.Hash) : Symbol.FromName(bone.Name);

    private static void RefreshDerivedFields(TtkSkeleton skeleton, IReadOnlySet<int> entryIndices)
    {
        foreach (var index in entryIndices)
        {
            if (index < 0 || index >= skeleton.Entries.Count)
            {
                continue;
            }

            var entry = skeleton.Entries[index];
            var rotation = NormalizeRotation(entry.LocalQuat);
            entry.LocalQuat = rotation;
            entry.RestXform ??= new TtkTransform();
            entry.RestXform.Translation = entry.LocalPosition;
            entry.RestXform.Rotation = rotation;

            var length = entry.LocalPosition.Length();
            entry.BoneLength = length;
            entry.BoneDir = length > 0.000001f
                ? entry.LocalPosition / length
                : Vector3.Zero;

            if (entry.GlobalTranslationScale.LengthSquared() < 0.000001f)
            {
                entry.GlobalTranslationScale = Vector3.One;
            }

            if (entry.LocalTranslationScale.LengthSquared() < 0.000001f)
            {
                entry.LocalTranslationScale = Vector3.One;
            }

            if (entry.AnimTranslationScale.LengthSquared() < 0.000001f)
            {
                entry.AnimTranslationScale = Vector3.One;
            }
        }
    }

    private static Symbol ResolveParentSymbol(BoneData bone, TtkSkeleton skeleton, int parentIndex)
    {
        if (parentIndex >= 0 && parentIndex < skeleton.Entries.Count)
        {
            return skeleton.Entries[parentIndex].JointName ?? Symbol.FromCrc64(bone.ParentHash);
        }

        return bone.ParentHash != 0 ? Symbol.FromCrc64(bone.ParentHash) : Symbol.Empty;
    }

    private static int ResolveOutputParentIndex(
        BoneData bone,
        SkeletonData edited,
        IReadOnlyDictionary<ulong, int> outputIndexByHash)
    {
        if (bone.ParentHash != 0 && outputIndexByHash.TryGetValue(bone.ParentHash, out var byHash))
        {
            return byHash;
        }

        if (bone.ParentIndex >= 0 && bone.ParentIndex < edited.Bones.Count)
        {
            var parentHash = edited.Bones[bone.ParentIndex].Hash;
            if (outputIndexByHash.TryGetValue(parentHash, out var byIndex))
            {
                return byIndex;
            }
        }

        return -1;
    }

    private static Matrix4x4[] BuildWorldMatrices(SkeletonData skeleton)
    {
        var worlds = new Matrix4x4[skeleton.Bones.Count];
        var states = new byte[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            BuildWorldMatrix(i, skeleton, worlds, states);
        }

        return worlds;
    }

    private static Matrix4x4 BuildWorldMatrix(int index, SkeletonData skeleton, Matrix4x4[] worlds, byte[] states)
    {
        if (states[index] == 2)
        {
            return worlds[index];
        }

        if (states[index] == 1)
        {
            return Matrix4x4.Identity;
        }

        states[index] = 1;
        var bone = skeleton.Bones[index];
        var local = BuildLocalMatrix(
            new Vector3(bone.X, bone.Y, bone.Z),
            new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw));
        var parent = bone.ParentIndex;
        worlds[index] = parent >= 0 && parent < skeleton.Bones.Count && parent != index
            ? local * BuildWorldMatrix(parent, skeleton, worlds, states)
            : local;
        states[index] = 2;
        return worlds[index];
    }

    private static Matrix4x4 BuildEntryWorld(TtkSkeleton.Entry entry, IReadOnlyList<Matrix4x4> worlds)
    {
        var local = BuildLocalMatrix(entry.LocalPosition, entry.LocalQuat);
        var parent = entry.ParentIndex;
        return parent >= 0 && parent < worlds.Count
            ? local * worlds[parent]
            : local;
    }

    private static Matrix4x4 BuildLocalMatrix(Vector3 position, Quaternion rotation)
        => Matrix4x4.CreateFromQuaternion(NormalizeRotation(rotation)) *
           Matrix4x4.CreateTranslation(position);

    private static bool TryGetLocalTransform(
        Matrix4x4 targetWorld,
        int parentIndex,
        IReadOnlyList<Matrix4x4> outputWorlds,
        out Vector3 position,
        out Quaternion rotation)
    {
        var local = targetWorld;
        if (parentIndex >= 0 &&
            parentIndex < outputWorlds.Count &&
            Matrix4x4.Invert(outputWorlds[parentIndex], out var inverseParent))
        {
            local = targetWorld * inverseParent;
        }

        if (!Matrix4x4.Decompose(local, out _, out rotation, out position))
        {
            position = new Vector3(local.M41, local.M42, local.M43);
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = NormalizeRotation(rotation);
        return true;
    }

    private static Quaternion NormalizeRotation(Quaternion rotation)
    {
        if (rotation.LengthSquared() < 0.000001f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.Normalize(rotation);
    }

    private static string? GetSymbolName(Symbol? symbol)
    {
        if (symbol is null || string.IsNullOrWhiteSpace(symbol.DebugString))
        {
            return null;
        }

        return symbol.DebugString;
    }
}
