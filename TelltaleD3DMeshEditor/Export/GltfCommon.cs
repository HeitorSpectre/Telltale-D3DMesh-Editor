using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelltaleD3DMeshEditor.Export;

// Low-level helpers shared by GLTF writers (embedded GLB and separate GLTF + files):
// binary-buffer writes, accessor/bufferView creation, GLB container handling, etc.
internal static class GltfCommon
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static void AddFloat(List<byte> bytes, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    public static void AddUInt16(List<byte> bytes, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    // Creates a bufferView + accessor for an already serialized byte block. Returns the accessor index.
    public static int AddAccessor(
        List<byte> bin,
        List<Dictionary<string, object>> bufferViews,
        List<Dictionary<string, object>> accessors,
        byte[] bytes,
        int target,
        int componentType,
        int count,
        string type,
        Array? min,
        Array? max)
    {
        var offset = bin.Count;
        bin.AddRange(bytes);
        while (bin.Count % 4 != 0)
        {
            bin.Add(0);
        }

        var bufferViewIndex = bufferViews.Count;
        var bufferView = new Dictionary<string, object>
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = bytes.Length,
        };
        if (target != 0)
        {
            bufferView["target"] = target;
        }
        bufferViews.Add(bufferView);

        var accessor = new Dictionary<string, object>
        {
            ["bufferView"] = bufferViewIndex,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (min is not null)
        {
            accessor["min"] = min;
        }
        if (max is not null)
        {
            accessor["max"] = max;
        }

        var accessorIndex = accessors.Count;
        accessors.Add(accessor);
        return accessorIndex;
    }

    // Adds a PNG as a bufferView + images[] entry for embedded GLB mode. Returns the image index.
    public static int AddEmbeddedPngImage(
        List<byte> bin,
        List<Dictionary<string, object>> bufferViews,
        List<Dictionary<string, object>> images,
        string name,
        byte[] png)
    {
        var viewOffset = bin.Count;
        bin.AddRange(png);
        while (bin.Count % 4 != 0)
        {
            bin.Add(0);
        }
        var viewIndex = bufferViews.Count;
        bufferViews.Add(new Dictionary<string, object>
        {
            ["buffer"] = 0,
            ["byteOffset"] = viewOffset,
            ["byteLength"] = png.Length,
        });
        var imageIndex = images.Count;
        images.Add(new Dictionary<string, object>
        {
            ["bufferView"] = viewIndex,
            ["mimeType"] = "image/png",
            ["name"] = name,
        });
        return imageIndex;
    }

    public static ushort RemapJoint(int rawBone, int meshVersion, IReadOnlyList<ulong>? palette, IReadOnlyDictionary<ulong, int> boneIndexByHash)
    {
        var local = Formats.Mesh.BoneIndexConvention.ToPaletteIndex(rawBone, meshVersion);
        if (palette is null || palette.Count == 0)
        {
            return meshVersion == 1 && local >= 0 && local < boneIndexByHash.Count
                ? (ushort)local
                : (ushort)0;
        }

        if (local < 0 || local >= palette.Count) return 0;
        return boneIndexByHash.TryGetValue(palette[local], out var idx) ? (ushort)idx : (ushort)0;
    }

    // System.Numerics e row-major (v*M); GLTF espera column-major. Transpoe.
    public static IEnumerable<float> MatrixColumnMajor(Matrix4x4 m)
    {
        yield return m.M11; yield return m.M12; yield return m.M13; yield return m.M14;
        yield return m.M21; yield return m.M22; yield return m.M23; yield return m.M24;
        yield return m.M31; yield return m.M32; yield return m.M33; yield return m.M34;
        yield return m.M41; yield return m.M42; yield return m.M43; yield return m.M44;
    }

    public static string[] FloatBits(params float[] values) =>
        values.Select(value => $"0x{BitConverter.SingleToUInt32Bits(value):X8}").ToArray();

    // Escreve o container .glb (header + chunk JSON + chunk BIN) com o padding exigido pela spec.
    public static void WriteGlbContainer(Dictionary<string, object> gltf, byte[] binary, string path)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(gltf, JsonOptions).ToList();
        while (json.Count % 4 != 0)
        {
            json.Add(0x20);
        }

        var bin = binary.ToList();
        while (bin.Count % 4 != 0)
        {
            bin.Add(0);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(0x46546C67);
        writer.Write(2);
        writer.Write(12 + 8 + json.Count + 8 + bin.Count);
        writer.Write(json.Count);
        writer.Write(0x4E4F534A);
        writer.Write(json.ToArray());
        writer.Write(bin.Count);
        writer.Write(0x004E4942);
        writer.Write(bin.ToArray());
    }
}
