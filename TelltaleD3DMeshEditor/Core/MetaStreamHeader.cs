namespace TelltaleD3DMeshEditor.Core;

// Telltale MetaStream header. Assets (.d3dmesh / .skl / .d3dtx) start with a MetaStream
// container (magic "5VSM"=MSV5, "6VSM"=MSV6, or "ERTM"/"MTRE") that describes serialized
// classes. We only need to skip this header up to the first useful data offset.
public sealed class MetaStreamHeader
{
    public string Version { get; init; } = "NONE";
    public int DataOffset { get; init; }

    public static MetaStreamHeader Parse(byte[] data)
    {
        var reader = new DataReader(data);
        var magic = reader.ReadAscii(4);
        if (magic is "5VSM" or "6VSM")
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            var classCount = checked((int)reader.ReadUInt32());
            reader.Skip(classCount * 12);
            return new MetaStreamHeader { Version = magic == "5VSM" ? "MSV5" : "MSV6", DataOffset = reader.Position };
        }

        if (magic is "ERTM" or "MTRE")
        {
            var count = checked((int)reader.ReadUInt32());
            for (var i = 0; i < count; i++)
            {
                var peek = reader.PeekUInt32();
                if (peek > 128)
                {
                    reader.Skip(12);
                }
                else
                {
                    var length = checked((int)reader.ReadUInt32());
                    reader.Skip(length + 4);
                }
            }

            return new MetaStreamHeader { Version = "MTRE", DataOffset = reader.Position };
        }

        return new MetaStreamHeader { Version = "NONE", DataOffset = 0 };
    }
}
