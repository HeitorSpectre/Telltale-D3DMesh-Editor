using System.Buffers.Binary;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Writes a Back to the Future (.skl version 1, "ERTM") skeleton from a GLB-imported SkeletonData. The
// MetaStream/ERTM header (magic + class names) is reused verbatim from a reference v1 .skl (the target's
// own skeleton), since it is identical for every v1 skeleton. Per-bone records carry the meaningful data
// from the GLB (hash, parent, bind pose); the variable tail blocks the format also stores
// (rich transform, animation-group membership, rotation constraints) are written as neutral defaults:
// identity translation scales, no group membership, no constraints. Validate by re-parsing with
// BackToTheFutureSkeletonParser.
public static class BttfSkeletonWriter
{
    // Mirrors BackToTheFutureSkeletonParser.SkipEntryTail: a 0x44 rich-transform block, a sized
    // resource-group block, a sized constraints block, then 4 bytes.
    private const int RichBlockSize = 0x44;

    public static byte[] Build(byte[] referenceSkeletonBytes, SkeletonData skeleton)
    {
        var header = MetaStreamHeader.Parse(referenceSkeletonBytes);
        if (header.Version is not "MTRE" || header.DataOffset <= 0 || header.DataOffset > referenceSkeletonBytes.Length)
        {
            throw new NotSupportedException("Reference skeleton is not a Back to the Future (v1/ERTM) .skl.");
        }

        // The u32 right after the ERTM header (before the bone count) is reused from the reference.
        var leadingValue = header.DataOffset + 4 <= referenceSkeletonBytes.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(referenceSkeletonBytes.AsSpan(header.DataOffset, 4))
            : 0u;

        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        writer.Write(referenceSkeletonBytes, 0, header.DataOffset); // ERTM header (magic + class names)
        writer.Write(leadingValue);
        writer.Write(skeleton.Bones.Count);

        foreach (var bone in skeleton.Bones)
        {
            WriteSymbolHash(writer, bone.Hash);
            WriteSymbolHash(writer, bone.ParentHash);
            writer.Write(bone.ParentIndex);
            writer.Write(bone.X);
            writer.Write(bone.Y);
            writer.Write(bone.Z);
            writer.Write(bone.Qx);
            writer.Write(bone.Qy);
            writer.Write(bone.Qz);
            writer.Write(bone.Qw);
            WriteRichTransformBlock(writer, bone);
            WriteEmptySizedBlock(writer); // resource-group membership: none
            WriteEmptySizedBlock(writer); // constraints: none
            writer.Write(0); // per-bone trailing u32
        }

        writer.Flush();
        return stream.ToArray();
    }

    // The 0x44 block holds per-bone rich transform data. Layout observed in real v1 skeletons:
    // flags(u32=0x20), pad vec3, 1.0f, pad vec3, then three translation-scale vec3s
    // (global, local, anim). GLB import does not carry these, so identity scales are written.
    private static void WriteRichTransformBlock(BinaryWriter writer, BoneData bone)
    {
        var start = writer.BaseStream.Position;
        writer.Write(0x20u);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        WriteVec3(writer, bone.GlobalTranslationScale.X, bone.GlobalTranslationScale.Y, bone.GlobalTranslationScale.Z);
        WriteVec3(writer, bone.LocalTranslationScale.X, bone.LocalTranslationScale.Y, bone.LocalTranslationScale.Z);
        WriteVec3(writer, bone.AnimTranslationScale.X, bone.AnimTranslationScale.Y, bone.AnimTranslationScale.Z);

        // Pad to exactly 0x44 in case the layout above is short on some build.
        while (writer.BaseStream.Position - start < RichBlockSize)
        {
            writer.Write((byte)0);
        }
    }

    // A sized block whose size field counts itself: an empty block is size=8 (4-byte size + 4-byte count=0),
    // matching BackToTheFutureSkeletonParser.SkipSizedBlock (requires size >= 4, skips size-4 bytes).
    private static void WriteEmptySizedBlock(BinaryWriter writer)
    {
        writer.Write(8);
        writer.Write(0);
    }

    private static void WriteSymbolHash(BinaryWriter writer, ulong hash)
    {
        writer.Write((uint)(hash & 0xFFFFFFFF));
        writer.Write((uint)(hash >> 32));
        writer.Write(0u); // padding the reader skips
    }

    private static void WriteVec3(BinaryWriter writer, float x, float y, float z)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
    }
}
