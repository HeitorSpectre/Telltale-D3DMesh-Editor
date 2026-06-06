using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Telltale .skl binary parser. Reads bones (hash, parent, local translation, local quaternion)
// and resolves each bone name through the bundled offline BoneNames database, falling back to
// "bone_<hash>" when a name is not available. Read/extract only.
public static class SkeletonParser
{
    public static SkeletonData Parse(byte[] data, int version)
    {
        var header = MetaStreamHeader.Parse(data);
        var reader = new DataReader(data);
        if (header.DataOffset > 0)
        {
            reader.Seek(header.DataOffset);
        }

        reader.ReadUInt32();
        var boneCount = checked((int)reader.ReadUInt32());
        if (boneCount < 0 || boneCount > 4096)
        {
            throw new InvalidDataException($"Invalid bone count: {boneCount}");
        }

        var skeleton = new SkeletonData();
        for (var i = 0; i < boneCount; i++)
        {
            var low = reader.ReadUInt32();
            var high = reader.ReadUInt32();
            var hash = ((ulong)high << 32) | low;
            if (version < 13)
            {
                reader.Skip(4);
            }

            var parentLow = reader.ReadUInt32();
            var parentHigh = reader.ReadUInt32();
            var parentHash = ((ulong)parentHigh << 32) | parentLow;
            if (version < 13)
            {
                reader.Skip(4);
            }

            var parentIndex = reader.ReadInt32();
            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var z = reader.ReadFloat();
            var qx = reader.ReadFloat();
            var qy = reader.ReadFloat();
            var qz = reader.ReadFloat();
            var qw = reader.ReadFloat();

            reader.ReadUInt32();
            reader.Skip(3 * 4);
            reader.ReadFloat();
            reader.Skip(3 * 4);
            reader.Skip(9 * 4);

            reader.ReadUInt32();
            var ikCount = checked((int)reader.ReadUInt32());
            for (var ik = 0; ik < ikCount; ik++)
            {
                var nameLength = checked((int)reader.ReadUInt32());
                reader.Skip(nameLength);
                reader.ReadFloat();
            }

            if (version > 1)
            {
                reader.ReadUInt32();
                var piAmount = checked((int)reader.ReadUInt32());
                reader.Skip(piAmount * 12);
                reader.ReadUInt32();
                reader.Skip(24);
                reader.ReadFloat();
            }

            var name = BoneHashDatabase.Resolve(hash) ?? $"bone_{hash:X16}";
            skeleton.Bones.Add(new BoneData(name, hash, parentIndex, x, y, z, qx, qy, qz, qw, parentHash));
        }

        return skeleton;
    }
}
