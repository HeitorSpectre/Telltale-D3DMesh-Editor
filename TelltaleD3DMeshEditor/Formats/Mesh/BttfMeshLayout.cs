using System.Text;
using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Mesh;

// Deterministic offset map for a Back to the Future (.d3dmesh version 1, "ERTM" container) mesh.
// Mirrors the walk performed by BackToTheFutureMeshParser.Parse, but records byte offsets/sizes for
// each editable region instead of decoding geometry. This is the base for the v1 reimporter
// (BttfMeshReinserter): the head (everything before the face section) is preserved byte-for-byte
// except the patched numeric fields, then the tail (face section + per-attribute vertex buffers) is
// rebuilt. The walker closes exactly at EOF for every Ep1 mesh, which is the coverage proof.
public sealed class BttfMeshLayout
{
    public required byte[] Original { get; init; }
    public required string Name { get; init; }
    public int Version { get; init; }
    public int DataOffset { get; init; }

    // Global bounds: vec3 min + vec3 max = 24 bytes.
    public int BoundsOffset { get; init; }

    public int TriangleBlockSizeFieldOffset { get; init; }
    public int TriangleBlockSize { get; init; }
    public int SubmeshCountFieldOffset { get; init; }
    public int DeclaredSubmeshCount { get; init; }

    // One entry per triangle set the extractor located. FieldBaseOffset points at the contiguous
    // vertexMin/vertexMax/facePointStart/polygonCount u32 fields.
    public required IReadOnlyList<BttfSubmeshLayout> Submeshes { get; init; }

    // Start of the face section header ("30 65" marker). Everything up to here is the immutable head.
    public int FaceSectionOffset { get; init; }

    // Ordered vertex buffer descriptors (type/stride/flags define the schema to reproduce).
    public required IReadOnlyList<BttfVertexBufferLayout> VertexBuffers { get; init; }

    // Bone-palette block (skinned meshes). Located right after the triangle block (past an 8-byte empty
    // sized block) as [size u32][count u32][palettes...], where each palette is [boneCount u32] followed
    // by boneCount × 12 bytes (low u32 + high u32 CRC64 + 4 pad). Offsets are -1 when absent.
    public int PaletteBlockOffset { get; init; } = -1;
    public int PaletteBlockSize { get; init; }
    public int PaletteBlockEnd { get; init; } = -1;
    public int PaletteCount { get; init; }
    public bool HasPaletteBlock => PaletteBlockOffset >= 0 && PaletteBlockEnd > PaletteBlockOffset;

    public int TailEnd { get; init; }
    public bool ClosesAtEof => TailEnd == Original.Length;
    public bool IsSkinned => VertexBuffers.Any(buffer => buffer.Type is 4 or 5);
}

public sealed record BttfSubmeshLayout(
    int Index,
    int FieldBaseOffset,
    int VertexMin,
    int VertexMax,
    int FacePointStart,
    int PolygonCount,
    int BoneSetFieldOffset = -1);

public sealed record BttfVertexBufferLayout(
    int HeaderOffset,
    int DataOffset,
    int Count,
    int Stride,
    int Type,
    int Flags)
{
    public int DataLength => Count * Stride;
}

internal static partial class BackToTheFutureMeshParser
{
    // Builds an offset map for a v1 mesh, reusing the same triangle-set/face detection as Parse so the
    // submesh list (and its order) matches what extraction produces.
    public static BttfMeshLayout BuildLayout(byte[] data)
    {
        var header = MetaStreamHeader.Parse(data);
        var o = header.DataOffset;

        var nameHeaderLength = ReadUInt32(data, o);
        o += 4;
        var nameLength = ReadUInt32(data, o);
        o += 4;
        if (nameLength > nameHeaderLength)
        {
            o -= 4;
            nameLength = nameHeaderLength;
        }

        var name = Encoding.ASCII.GetString(data, o, checked((int)nameLength));
        o += checked((int)nameLength);
        var version = checked((int)ReadUInt32(data, o));
        o += 4;
        if (version != 1)
        {
            throw new NotSupportedException($"BttfMeshLayout only supports Back to the Future v1 meshes (got {version}).");
        }

        var marker = data[o];
        if (marker is not (0x30 or 0x31))
        {
            throw new NotSupportedException("Back to the Future v1 mesh marker was not found.");
        }

        o += 1;
        var boundsOffset = o;
        o += 24; // vec3 min + vec3 max

        var sizedSize = checked((int)ReadUInt32(data, o));
        if (sizedSize < 4)
        {
            throw new InvalidDataException($"Invalid Back to the Future sized block at 0x{o:X}: {sizedSize}.");
        }

        o += sizedSize;

        var triangleBlockSizeFieldOffset = o;
        var triangleBlockSize = checked((int)ReadUInt32(data, o));
        var submeshCountFieldOffset = o + 4;
        var declaredSubmeshCount = checked((int)ReadUInt32(data, o + 4));
        if (triangleBlockSize < 8 || declaredSubmeshCount <= 0 || declaredSubmeshCount > 4096)
        {
            throw new InvalidDataException($"Invalid Back to the Future triangle block: size={triangleBlockSize}, count={declaredSubmeshCount}.");
        }

        var triangleDataStart = o + 8;
        var triangleBlockEnd = checked(triangleBlockSizeFieldOffset + triangleBlockSize);
        if (triangleBlockEnd > data.Length)
        {
            throw new EndOfStreamException($"Back to the Future triangle block exceeds file bounds: 0x{triangleBlockEnd:X} > 0x{data.Length:X}.");
        }

        var infos = ReadTriangleSets(data, triangleDataStart, triangleBlockEnd, declaredSubmeshCount);
        var faceSection = ReadFaceSection(data, triangleBlockEnd, infos);

        var submeshes = infos
            .Select(info => new BttfSubmeshLayout(
                info.Index,
                info.FieldBaseOffset,
                info.VertexMin,
                info.VertexMax,
                info.FacePointStart,
                info.PolygonCount,
                info.BoneSetFieldOffset))
            .ToList();

        var (paletteOffset, paletteSize, paletteEnd, paletteCount) =
            LocatePaletteBlock(data, triangleBlockEnd, faceSection.SectionOffset);

        var vertexBuffers = ReadVertexBufferLayouts(data, faceSection.NextOffset, out var tailEnd);

        return new BttfMeshLayout
        {
            Original = data,
            Name = name,
            Version = version,
            DataOffset = header.DataOffset,
            BoundsOffset = boundsOffset,
            TriangleBlockSizeFieldOffset = triangleBlockSizeFieldOffset,
            TriangleBlockSize = triangleBlockSize,
            SubmeshCountFieldOffset = submeshCountFieldOffset,
            DeclaredSubmeshCount = declaredSubmeshCount,
            Submeshes = submeshes,
            FaceSectionOffset = faceSection.SectionOffset,
            PaletteBlockOffset = paletteOffset,
            PaletteBlockSize = paletteSize,
            PaletteBlockEnd = paletteEnd,
            PaletteCount = paletteCount,
            VertexBuffers = vertexBuffers,
            TailEnd = tailEnd,
        };
    }

    // The bone-palette block follows the triangle block, past an 8-byte empty sized block, as
    // [size u32][count u32][palettes...]. Returns offsets describing it, or (-1, 0, -1, 0) when the
    // structure is not present (e.g. a static mesh) so callers fall back to keeping the head verbatim.
    private static (int Offset, int Size, int End, int Count) LocatePaletteBlock(byte[] data, int triangleBlockEnd, int faceSectionOffset)
    {
        try
        {
            var o = triangleBlockEnd;
            if (o + 8 > data.Length)
            {
                return (-1, 0, -1, 0);
            }

            var sizedBlock = checked((int)ReadUInt32(data, o));
            if (sizedBlock < 4)
            {
                return (-1, 0, -1, 0);
            }

            o += sizedBlock; // skip the empty sized block
            if (o + 8 > data.Length)
            {
                return (-1, 0, -1, 0);
            }

            var paletteBlockSize = checked((int)ReadUInt32(data, o));
            var paletteCount = checked((int)ReadUInt32(data, o + 4));
            var end = o + paletteBlockSize;
            if (paletteBlockSize < 8 || paletteCount < 0 || paletteCount > 512 ||
                (faceSectionOffset > 0 && end > faceSectionOffset) || end > data.Length)
            {
                return (-1, 0, -1, 0);
            }

            return (o, paletteBlockSize, end, paletteCount);
        }
        catch
        {
            return (-1, 0, -1, 0);
        }
    }

    // Walks the per-attribute vertex buffers (same validation as ReadVertexBuffers) recording each
    // header's offset/count/stride/type/flags. tailEnd is where the walk stops (should equal EOF).
    private static List<BttfVertexBufferLayout> ReadVertexBufferLayouts(byte[] data, int offset, out int tailEnd)
    {
        var buffers = new List<BttfVertexBufferLayout>();
        while (offset + 16 <= data.Length)
        {
            var rawCount = ReadUInt32(data, offset);
            var rawStride = ReadUInt32(data, offset + 4);
            var rawType = ReadUInt32(data, offset + 8);
            var flags = ReadUInt32(data, offset + 12);
            if (rawCount > int.MaxValue || rawStride > int.MaxValue || rawType > int.MaxValue)
            {
                break;
            }

            var count = (int)rawCount;
            var stride = (int)rawStride;
            var type = (int)rawType;
            if (count <= 0 || count > 200000 || stride <= 0 || stride > 128 || type <= 0 || type > 20 || flags > 16)
            {
                break;
            }

            var dataOffset = offset + 16;
            var dataLength = (long)count * stride;
            if (dataOffset + dataLength > data.Length)
            {
                break;
            }

            buffers.Add(new BttfVertexBufferLayout(offset, dataOffset, count, stride, type, (int)flags));
            offset = dataOffset + checked((int)dataLength);
        }

        tailEnd = offset;
        return buffers;
    }
}
