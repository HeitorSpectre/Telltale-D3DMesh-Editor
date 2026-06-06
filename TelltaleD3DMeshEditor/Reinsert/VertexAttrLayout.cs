using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Reinsert;

// V13 vertex declaration table: 12 attributes, each (offset, count, format) in 3 u32 values.
// The writer needs the exact size of each attribute to compute stride and locate each field inside
// the vertex. Sizes mirror the readers in D3DMeshParser.ReadVerticesV13.
public sealed class VertexAttrLayout
{
    public required AttrDescriptor Position { get; init; }
    public required AttrDescriptor Uv1 { get; init; }
    public required AttrDescriptor Normals { get; init; }
    public required AttrDescriptor Weights { get; init; }
    public required AttrDescriptor Bones { get; init; }
    public required AttrDescriptor Colors { get; init; }
    public required AttrDescriptor Unknown1 { get; init; }
    public required AttrDescriptor Binormals { get; init; }
    public required AttrDescriptor Tangents { get; init; }
    public required AttrDescriptor Uv2 { get; init; }
    public required AttrDescriptor Uv3 { get; init; }
    public required AttrDescriptor Uv4 { get; init; }

    public int Stride => new[]
    {
        End(Position, PositionSize(Position.Format)),
        End(Uv1, UvSize(Uv1.Format)),
        End(Normals, NormalSize(Normals.Format)),
        End(Weights, WeightSize(Weights.Format)),
        End(Bones, BoneSize(Bones.Format)),
        End(Colors, ColorSize(Colors.Format)),
        End(Unknown1, UnknownSize(Unknown1.Format)),
        End(Binormals, Vector4Size(Binormals.Format)),
        End(Tangents, Vector4Size(Tangents.Format)),
        End(Uv2, UvSize(Uv2.Format)),
        End(Uv3, UvSize(Uv3.Format)),
        End(Uv4, UvSize(Uv4.Format)),
    }.Max();

    public static VertexAttrLayout Read(DataReader reader)
    {
        return new VertexAttrLayout
        {
            Position = ReadAttr(reader),
            Uv1 = ReadAttr(reader),
            Normals = ReadAttr(reader),
            Weights = ReadAttr(reader),
            Bones = ReadAttr(reader),
            Colors = ReadAttr(reader),
            Unknown1 = ReadAttr(reader),
            Binormals = ReadAttr(reader),
            Tangents = ReadAttr(reader),
            Uv2 = ReadAttr(reader),
            Uv3 = ReadAttr(reader),
            Uv4 = ReadAttr(reader),
        };
    }

    private static AttrDescriptor ReadAttr(DataReader reader)
        => new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());

    private static int End(AttrDescriptor attr, int size)
        => size == 0 ? 0 : checked((int)attr.Offset + size);

    private static int PositionSize(uint format) => format switch
    {
        0 => 0,
        1 => 12,
        _ => throw new InvalidDataException($"Unknown position format: {format}"),
    };

    private static int UvSize(uint format) => format switch
    {
        0 => 0,
        1 => 8,
        4 => 4,
        5 => 4,
        11 => 4,
        _ => throw new InvalidDataException($"Unknown UV format: {format}"),
    };

    private static int NormalSize(uint format) => format switch
    {
        0 => 0,
        2 => 4,
        4 => 8,
        _ => throw new InvalidDataException($"Unknown normal format: {format}"),
    };

    private static int WeightSize(uint format) => format switch
    {
        0 => 0,
        1 => 12,
        4 => 8,
        5 => 8,
        _ => throw new InvalidDataException($"Unknown weight format: {format}"),
    };

    private static int BoneSize(uint format) => format switch
    {
        0 => 0,
        3 => 4,
        8 => 4,
        _ => throw new InvalidDataException($"Unknown bone format: {format}"),
    };

    private static int ColorSize(uint format) => format switch
    {
        0 => 0,
        1 => 16,
        3 => 4,
        _ => throw new InvalidDataException($"Unknown color format: {format}"),
    };

    private static int UnknownSize(uint format) => format switch
    {
        0 => 0,
        1 => 4,
        _ => throw new InvalidDataException($"Unknown attribute present: {format}"),
    };

    private static int Vector4Size(uint format) => format switch
    {
        0 => 0,
        2 => 4,
        4 => 8,
        _ => throw new InvalidDataException($"Unknown vector format: {format}"),
    };
}

public readonly record struct AttrDescriptor(uint Offset = 0, uint Count = 0, uint Format = 0);
