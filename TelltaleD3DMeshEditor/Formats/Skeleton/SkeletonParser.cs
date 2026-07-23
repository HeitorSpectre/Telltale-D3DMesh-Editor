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
        if (version <= 1 && header.Version == "MTRE")
        {
            return BackToTheFutureSkeletonParser.Parse(data, header.DataOffset);
        }

        // The Tales from the Borderlands source leak (and its props) ship the older TWD-2012 ERTM skeleton
        // format, which no toolkit game profile can interpret. It is a keyed layout, so read it directly.
        // Back to the Future also uses MTRE but with a different layout (handled above for version <= 1);
        // fall back to its reader if the TWD-2012 walk yields nothing usable.
        if (header.Version == "MTRE")
        {
            try
            {
                var ertm = ParseErtmSkeleton(data, header.DataOffset);
                if (ertm.Bones.Any(bone => bone.Hash != 0 || bone.X != 0f || bone.Y != 0f || bone.Z != 0f))
                {
                    return ertm;
                }
            }
            catch
            {
                // Not a TWD-2012 ERTM skeleton; try the Back to the Future reader below.
            }

            return BackToTheFutureSkeletonParser.Parse(data, header.DataOffset);
        }

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

            // Batman: The Telltale Series and the rest of the Telltale "GotG" family (mesh versions
            // 42/45/46) repeat the bone's name symbol as an 8-byte hash right after the parent index,
            // which the older (v13-v25) skeleton layout does not carry.
            if (version >= 42)
            {
                reader.Skip(8);
            }

            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var z = reader.ReadFloat();
            var qx = reader.ReadFloat();
            var qy = reader.ReadFloat();
            var qz = reader.ReadFloat();
            var qw = reader.ReadFloat();

            // Note: the GotG family (Batman, .skl v46) stores its real bind pose in the rich
            // RestXform/translation-scale fields skipped below, so this lightweight local pose alone does
            // not reproduce the mesh's pose. SkeletonLoader routes those skeletons through the Telltale
            // Toolkit instead; this reader stays a structural fallback (keeps the v46 walk aligned).

            reader.ReadUInt32();
            reader.Skip(3 * 4);
            reader.ReadFloat();
            reader.Skip(3 * 4);
            reader.Skip(9 * 4);

            // The GotG-family rich transform carries one extra 4-byte field before the IK/animation
            // group table.
            if (version >= 42)
            {
                reader.Skip(4);
            }

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

    // Reads the TWD-2012 ERTM skeleton. Bones are stored sequentially; each begins with the signature
    // [nameLen u32][name][hash u64][parentLen u32][parentName], followed by [flag i32][localPos vec3]
    // [localRot quat xyzw] and a variable-length tail (rest pose, translation scales, IK/animation-group
    // data). The tail length varies per bone, so the next bone is found by scanning forward for the next
    // valid signature. Parent links are resolved by name (root nodes carry the sentinel "relativeNode").
    private static SkeletonData ParseErtmSkeleton(byte[] data, int dataOffset)
    {
        var boneCount = (int)BitConverter.ToUInt32(data, dataOffset + 4);
        if (boneCount < 0 || boneCount > 4096)
        {
            throw new InvalidDataException($"Invalid ERTM skeleton bone count: {boneCount}");
        }

        // Locate the first bone signature after the small count preamble.
        var cursor = dataOffset + 8;
        while (cursor < data.Length - 60 && !TryReadBoneSignature(data, cursor, out _, out _, out _, out _))
        {
            cursor++;
        }

        var raw = new List<(string Name, ulong Hash, string Parent, float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw)>(boneCount);
        for (var i = 0; i < boneCount; i++)
        {
            if (cursor >= data.Length - 32 || !TryReadBoneSignature(data, cursor, out var name, out var hash, out var parent, out var afterParent))
            {
                break;
            }

            var x = BitConverter.ToSingle(data, afterParent + 4);
            var y = BitConverter.ToSingle(data, afterParent + 8);
            var z = BitConverter.ToSingle(data, afterParent + 12);
            var qx = BitConverter.ToSingle(data, afterParent + 16);
            var qy = BitConverter.ToSingle(data, afterParent + 20);
            var qz = BitConverter.ToSingle(data, afterParent + 24);
            var qw = BitConverter.ToSingle(data, afterParent + 28);
            raw.Add((name, hash, parent, x, y, z, qx, qy, qz, qw));

            // Advance past this bone's transform and scan for the next signature.
            cursor = afterParent + 32;
            while (cursor < data.Length - 60 && !TryReadBoneSignature(data, cursor, out _, out _, out _, out _))
            {
                cursor++;
            }
        }

        if (raw.Count == 0)
        {
            throw new InvalidDataException("No bones found in ERTM skeleton.");
        }

        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < raw.Count; i++)
        {
            indexByName[raw[i].Name] = i;
        }

        var skeleton = new SkeletonData();
        foreach (var b in raw)
        {
            // The mesh bone palettes key bones by CRC64-ECMA of the lowercased name. The hash stored next
            // to the name in the .skl is not reliable for every bone (some carry a placeholder), so compute
            // the symbol hash from the name to guarantee the palette→skeleton match used for skinning.
            var hash = Crc64Ecma.Compute(b.Name);
            var parentIndex = indexByName.TryGetValue(b.Parent, out var pi) ? pi : -1;
            var parentHash = parentIndex >= 0 ? Crc64Ecma.Compute(raw[parentIndex].Name) : 0UL;
            skeleton.Bones.Add(new BoneData(b.Name, hash, parentIndex, b.X, b.Y, b.Z, b.Qx, b.Qy, b.Qz, b.Qw, parentHash));
        }

        return skeleton;
    }

    // A bone signature is [nameLen u32][name][hash u64][parentLen u32][parentName]. On success, outputs the
    // bone name, its symbol hash, the parent name, and the offset just past the parent name.
    private static bool TryReadBoneSignature(byte[] data, int p, out string name, out ulong hash, out string parent, out int afterParent)
    {
        name = "";
        parent = "";
        hash = 0;
        afterParent = 0;
        if (!TryReadIdentifier(data, p, out name, out var nameLen))
        {
            return false;
        }

        var hashPos = p + 4 + nameLen;
        if (hashPos + 8 + 4 > data.Length)
        {
            return false;
        }

        var parentLenPos = hashPos + 8;
        if (!TryReadIdentifier(data, parentLenPos, out parent, out var parentLen))
        {
            return false;
        }

        afterParent = parentLenPos + 4 + parentLen;
        if (afterParent + 32 > data.Length)
        {
            return false;
        }

        hash = BitConverter.ToUInt64(data, hashPos);
        return true;
    }

    // Reads a [u32 length][ASCII name] identifier. The name must be a plausible bone/symbol token: 2-40
    // characters, starting with a letter or underscore, containing only letters, digits, '_' or ':'.
    private static bool TryReadIdentifier(byte[] data, int p, out string name, out int length)
    {
        name = "";
        length = 0;
        if (p + 4 > data.Length)
        {
            return false;
        }

        var len = (int)BitConverter.ToUInt32(data, p);
        if (len < 2 || len > 40 || p + 4 + len > data.Length)
        {
            return false;
        }

        for (var i = 0; i < len; i++)
        {
            var b = data[p + 4 + i];
            var isLetter = b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z');
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isExtra = b is (byte)'_' or (byte)':';
            if (!isLetter && !isDigit && !isExtra)
            {
                return false;
            }

            if (i == 0 && !isLetter && b != (byte)'_')
            {
                return false;
            }
        }

        name = System.Text.Encoding.ASCII.GetString(data, p + 4, len);
        length = len;
        return true;
    }
}
