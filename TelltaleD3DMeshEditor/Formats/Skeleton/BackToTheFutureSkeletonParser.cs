using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Back to the Future uses early MTRE/v1 Skeleton entries. They share the same high-level fields as
// later games, but the tail has variable-sized resource group and constraint blocks. Keep this reader
// isolated so modern Telltale skeleton layouts stay on the established generic parser/toolkit path.
internal static class BackToTheFutureSkeletonParser
{
    public static SkeletonData Parse(byte[] data, int dataOffset)
    {
        var reader = new DataReader(data);
        if (dataOffset > 0)
        {
            reader.Seek(dataOffset);
        }

        reader.ReadUInt32();
        var boneCount = checked((int)reader.ReadUInt32());
        if (boneCount < 0 || boneCount > 4096)
        {
            throw new InvalidDataException($"Invalid Back to the Future bone count: {boneCount}");
        }

        var skeleton = new SkeletonData();
        for (var i = 0; i < boneCount; i++)
        {
            var hash = ReadSymbolHash(reader);
            var parentHash = ReadSymbolHash(reader);
            var parentIndex = reader.ReadInt32();
            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var z = reader.ReadFloat();
            var qx = reader.ReadFloat();
            var qy = reader.ReadFloat();
            var qz = reader.ReadFloat();
            var qw = reader.ReadFloat();

            var name = BoneHashDatabase.Resolve(hash) ?? $"bone_{hash:X16}";
            skeleton.Bones.Add(new BoneData(name, hash, parentIndex, x, y, z, qx, qy, qz, qw, parentHash));
            SkipEntryTail(reader);
        }

        return skeleton;
    }

    private static void SkipEntryTail(DataReader reader)
    {
        reader.Skip(0x44);
        SkipSizedBlock(reader, "resource group membership");
        SkipSizedBlock(reader, "constraints");
        reader.Skip(4);
    }

    private static void SkipSizedBlock(DataReader reader, string name)
    {
        var size = checked((int)reader.ReadUInt32());
        if (size < 4 || size > 65536)
        {
            throw new InvalidDataException($"Invalid Back to the Future skeleton {name} block size: {size}");
        }

        reader.Skip(size - 4);
    }

    private static ulong ReadSymbolHash(DataReader reader)
    {
        var low = reader.ReadUInt32();
        var high = reader.ReadUInt32();
        reader.Skip(4);
        return ((ulong)high << 32) | low;
    }
}
