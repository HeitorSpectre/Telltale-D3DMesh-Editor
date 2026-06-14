using System.Buffers.Binary;
using TelltaleToolKit;
using TelltaleToolKit.T3Types;
using TelltaleToolKit.T3Types.Meshes;
using TelltaleToolKit.T3Types.Meshes.T3Types;
using TelltaleToolKit.T3Types.Textures;

namespace TelltaleD3DMeshEditor.Formats.Mesh;

internal static class TelltaleToolkitMeshParser
{
    private static readonly object ToolkitGate = new();

    public static OldBonePaletteInfo? TryReadOldBonePaletteInfo(byte[] data)
    {
        EnsureToolkitInitialized();
        var ttkMesh = TryDeserialize(data, requireGeometry: false, out _, out _);
        if (ttkMesh is null || ttkMesh.BonePalettes.Count == 0)
        {
            return null;
        }

        var palettes = ttkMesh.BonePalettes
            .Select(palette => palette.Select(entry => ResolveBoneHash(entry)).ToArray())
            .Where(palette => palette.Length > 0)
            .ToList();
        if (palettes.Count == 0)
        {
            return null;
        }

        var triangleSetPaletteIndices = ttkMesh.TriangleSets
            .Select(triangleSet => NormalizePaletteIndex(triangleSet.BonePaletteIndex, palettes.Count))
            .ToArray();

        return new OldBonePaletteInfo(palettes, triangleSetPaletteIndices);
    }

    public static MeshData ParseOldMesh(byte[] data, string fallbackName)
    {
        EnsureToolkitInitialized();
        var ttkMesh = TryDeserialize(data, requireGeometry: true, out var lastError, out var lastStatus)
            ?? throw new InvalidDataException($"Telltale Toolkit could not read the mesh. {lastStatus}", lastError);

        return Convert(ttkMesh, fallbackName);
    }

    private static D3DMesh? TryDeserialize(byte[] data, bool requireGeometry, out Exception? lastError, out string lastStatus)
    {
        lastError = null;
        lastStatus = "No profile produced geometry.";
        var statuses = new List<string>();
        using (var stream = new MemoryStream(data))
        {
            try
            {
                var mesh = Toolkit.Instance.Deserialize<D3DMesh>(stream);
                if (mesh is not null && (!requireGeometry || HasGeometry(mesh)))
                {
                    return mesh;
                }

                statuses.Add(DescribeMesh("default", mesh));
            }
            catch (Exception ex)
            {
                lastError = ex;
                statuses.Add($"default failed: {ex.Message}");
            }
        }

        foreach (var profileName in Toolkit.Instance.GameProfiles.Keys
                     .OrderByDescending(profile => profile.Contains("future", StringComparison.OrdinalIgnoreCase) ||
                                                   profile.Contains("bttf", StringComparison.OrdinalIgnoreCase)))
        {
            Workspace workspace;
            try
            {
                workspace = Toolkit.Instance.CreateWorkspace($"d3dmesh::{profileName}", profileName);
            }
            catch
            {
                continue;
            }

            try
            {
                using var stream = new MemoryStream(data);
                var mesh = Toolkit.Instance.Deserialize<D3DMesh>(stream, workspace);
                if (mesh is not null && (!requireGeometry || HasGeometry(mesh)))
                {
                    return mesh;
                }

                statuses.Add(DescribeMesh(profileName, mesh));
            }
            catch (Exception ex)
            {
                lastError = ex;
                statuses.Add($"{profileName} failed: {ex.Message}");
                // Wrong game/version context for this old mesh; try the next profile.
            }
        }

        if (statuses.Count > 0)
        {
            lastStatus = string.Join(" ", statuses.Take(6));
        }

        return null;
    }

    private static bool HasGeometry(D3DMesh mesh)
        => mesh.TriangleSets.Count > 0 &&
           mesh.T3VertexBuffers is not null &&
           mesh.T3VertexBuffers.Any(buffer => buffer is { Buffer.Length: > 0 });

    private static string DescribeMesh(string profileName, D3DMesh? mesh)
        => mesh is null
            ? $"{profileName}: null mesh."
            : $"{profileName}: version={mesh.Version}, triangleSets={mesh.TriangleSets.Count}, vertexBuffers={mesh.T3VertexBuffers?.Length ?? 0}, nonEmptyBuffers={mesh.T3VertexBuffers?.Count(buffer => buffer is { Buffer.Length: > 0 }) ?? 0}.";

    private static MeshData Convert(D3DMesh ttkMesh, string fallbackName)
    {
        var positions = ReadPositions(GetBuffer(ttkMesh, 0));
        if (positions.Count == 0)
        {
            throw new InvalidDataException("No vertices found in Toolkit mesh.");
        }

        var normals = ReadNormals(GetBuffer(ttkMesh, 1), positions.Count);
        var weights = ReadWeights(GetBuffer(ttkMesh, 3), positions.Count);
        var bones = ReadBones(GetBuffer(ttkMesh, 4), positions.Count);
        var uv1 = ReadUvs(GetBuffer(ttkMesh, 5), positions.Count);
        var uv2 = ReadUvs(GetBuffer(ttkMesh, 6), positions.Count);
        var uv3 = ReadUvs(GetBuffer(ttkMesh, 7), positions.Count);
        var uv4 = ReadUvs(GetBuffer(ttkMesh, 8), positions.Count);
        var colors = ReadColors(GetBuffer(ttkMesh, 10), positions.Count);
        var indices = ReadIndices(ttkMesh.T3IndexBuffer);

        var mesh = new MeshData
        {
            Name = string.IsNullOrWhiteSpace(ttkMesh.Name) ? fallbackName : ttkMesh.Name,
            Version = ttkMesh.Version,
        };

        foreach (var palette in ttkMesh.BonePalettes)
        {
            mesh.BonePalettes.Add(palette.Select(entry => ResolveBoneHash(entry)).ToArray());
        }

        for (var i = 0; i < ttkMesh.TriangleSets.Count; i++)
        {
            var triangleSet = ttkMesh.TriangleSets[i];
            var vertexStart = Math.Max(0, triangleSet.MinVertIndex);
            var vertexEnd = Math.Min(positions.Count - 1, Math.Max(triangleSet.MinVertIndex, triangleSet.MaxVertIndex));
            if (vertexEnd < vertexStart)
            {
                continue;
            }

            var materialName = TextureName(triangleSet.T3DiffuseMap) ?? $"material_{i + 1}";
            var submesh = new SubmeshData
            {
                Name = materialName,
                MaterialName = materialName,
                BonePaletteIndex = NormalizePaletteIndex(triangleSet.BonePaletteIndex, mesh.BonePalettes.Count),
                SourceSubmeshIndex = i,
            };

            AddTextureSlot(submesh, "diffuse", triangleSet.T3DiffuseMap);
            AddTextureSlot(submesh, "detail_diffuse", triangleSet.T3DetailMap);
            AddTextureSlot(submesh, "bake", triangleSet.T3LightMap);
            AddTextureSlot(submesh, "bump", triangleSet.T3BumpMap);
            AddTextureSlot(submesh, "environment", triangleSet.T3EnvMap);

            for (var vertex = vertexStart; vertex <= vertexEnd; vertex++)
            {
                var p = positions[vertex];
                var n = ValueOrDefault(normals, vertex, (0f, 1f, 0f));
                var uv = ValueOrDefault(uv1, vertex, (0f, 0f));
                var uvb = ValueOrDefault(uv2, vertex, uv);
                var uvc = ValueOrDefault(uv3, vertex, uvb);
                var uvd = ValueOrDefault(uv4, vertex, uvc);
                var bone = ValueOrDefault(bones, vertex, (0, 0, 0, 0));
                var weight = ValueOrDefault(weights, vertex, (1f, 0f, 0f, 0f));
                var color = ValueOrDefault(colors, vertex, (1f, 1f, 1f, 1f));

                submesh.Vertices.Add(new VertexData(
                    p.Item1, p.Item2, p.Item3,
                    n.Item1, n.Item2, n.Item3,
                    uv.Item1, uv.Item2,
                    uvb.Item1, uvb.Item2,
                    uvc.Item1, uvc.Item2,
                    uvd.Item1, uvd.Item2,
                    bone.Item1, bone.Item2, bone.Item3, bone.Item4,
                    weight.Item1, weight.Item2, weight.Item3, weight.Item4,
                    color.Item1, color.Item2, color.Item3, color.Item4));
            }

            var firstTriangle = Math.Max(0, triangleSet.StartIndex / 3);
            var triangleCount = Math.Max(0, triangleSet.NumPrimitives);
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var index = firstTriangle + triangle;
                if (index < 0 || index >= indices.Count)
                {
                    continue;
                }

                var face = indices[index];
                var a = face.A - vertexStart;
                var b = face.B - vertexStart;
                var c = face.C - vertexStart;
                if (a >= 0 && b >= 0 && c >= 0 &&
                    a < submesh.Vertices.Count && b < submesh.Vertices.Count && c < submesh.Vertices.Count)
                {
                    submesh.Faces.Add((a, b, c));
                }
            }

            if (submesh.Vertices.Count > 0 && submesh.Faces.Count > 0)
            {
                mesh.Submeshes.Add(submesh);
            }
        }

        if (mesh.Submeshes.Count == 0)
        {
            throw new InvalidDataException("No submesh with valid triangles was found.");
        }

        return mesh;
    }

    private static T3VertexBuffer? GetBuffer(D3DMesh mesh, int index)
        => mesh.T3VertexBuffers is not null && index >= 0 && index < mesh.T3VertexBuffers.Length
            ? mesh.T3VertexBuffers[index]
            : null;

    private static List<(float X, float Y, float Z)> ReadPositions(T3VertexBuffer? buffer)
    {
        var result = new List<(float X, float Y, float Z)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var stride = Math.Max(buffer.VertSize, 12);
        for (var i = 0; i < buffer.NumVerts; i++)
        {
            var offset = i * stride;
            if (offset + 12 > buffer.Buffer.Length)
            {
                break;
            }

            result.Add((ReadSingle(buffer.Buffer, offset), ReadSingle(buffer.Buffer, offset + 4), ReadSingle(buffer.Buffer, offset + 8)));
        }

        return result;
    }

    private static List<(float X, float Y, float Z)> ReadNormals(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float X, float Y, float Z)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadVector3(buffer.Buffer, offset, component.Type, out var value))
            {
                break;
            }

            result.Add(value);
        }

        return result;
    }

    private static List<(float U, float V)> ReadUvs(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float U, float V)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadUv(buffer.Buffer, offset, component.Type, out var uv))
            {
                break;
            }

            result.Add(uv);
        }

        return result;
    }

    private static List<(int A, int B, int C, int D)> ReadBones(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(int A, int B, int C, int D)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (offset + 4 > buffer.Buffer.Length)
            {
                break;
            }

            result.Add(component.Type switch
            {
                T3VertexComponent.EnumType.VTypeS8NBones => (buffer.Buffer[offset] / 3, buffer.Buffer[offset + 1] / 3, buffer.Buffer[offset + 2] / 3, buffer.Buffer[offset + 3] / 3),
                T3VertexComponent.EnumType.VTypeU8N => (buffer.Buffer[offset] / 4, buffer.Buffer[offset + 1] / 4, buffer.Buffer[offset + 2] / 4, buffer.Buffer[offset + 3] / 4),
                _ => (buffer.Buffer[offset], buffer.Buffer[offset + 1], buffer.Buffer[offset + 2], buffer.Buffer[offset + 3]),
            });
        }

        return result;
    }

    private static List<(float A, float B, float C, float D)> ReadWeights(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float A, float B, float C, float D)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadWeights(buffer.Buffer, offset, component.Type, out var weight))
            {
                break;
            }

            result.Add(Normalize(weight));
        }

        return result;
    }

    private static List<(float R, float G, float B, float A)> ReadColors(T3VertexBuffer? buffer, int count)
    {
        var result = new List<(float R, float G, float B, float A)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var component = FirstComponent(buffer);
        var stride = Math.Max(buffer.VertSize, ComponentSize(component.Type, component.Count));
        for (var i = 0; i < Math.Min(count, buffer.NumVerts); i++)
        {
            var offset = i * stride + checked((int)component.Offset);
            if (!TryReadColor(buffer.Buffer, offset, component.Type, out var color))
            {
                break;
            }

            result.Add(color);
        }

        return result;
    }

    private static List<(int A, int B, int C)> ReadIndices(T3IndexBuffer? buffer)
    {
        var result = new List<(int A, int B, int C)>();
        if (buffer is null || buffer.Buffer.Length == 0)
        {
            return result;
        }

        var indexSize = buffer.Format == 102 ? 4 : 2;
        for (var i = 0; i + indexSize * 3 <= buffer.Buffer.Length; i += indexSize * 3)
        {
            result.Add((
                ReadIndex(buffer.Buffer, i, indexSize),
                ReadIndex(buffer.Buffer, i + indexSize, indexSize),
                ReadIndex(buffer.Buffer, i + indexSize * 2, indexSize)));
        }

        return result;
    }

    private static T3VertexComponent FirstComponent(T3VertexBuffer buffer)
        => buffer.VertexComponents.FirstOrDefault(component => component.Type != T3VertexComponent.EnumType.VTypeNone)
           ?? new T3VertexComponent { Count = 1, Type = GuessType(buffer) };

    private static T3VertexComponent.EnumType GuessType(T3VertexBuffer buffer)
        => buffer.VertSize switch
        {
            4 => T3VertexComponent.EnumType.VTypeS8N,
            8 => T3VertexComponent.EnumType.VTypeS16N,
            12 => T3VertexComponent.EnumType.VTypeFloat,
            16 => T3VertexComponent.EnumType.VTypeFloat,
            _ => T3VertexComponent.EnumType.VTypeFloat,
        };

    private static bool TryReadVector3(byte[] data, int offset, T3VertexComponent.EnumType type, out (float X, float Y, float Z) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 12 > data.Length) return false;
                value = (ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));
                return true;
            case T3VertexComponent.EnumType.VTypeS8N:
            case T3VertexComponent.EnumType.VTypeS8NBones:
                if (offset + 3 > data.Length) return false;
                value = (unchecked((sbyte)data[offset]) / 127f, unchecked((sbyte)data[offset + 1]) / 127f, unchecked((sbyte)data[offset + 2]) / 127f);
                return true;
            case T3VertexComponent.EnumType.VTypeS16N:
                if (offset + 6 > data.Length) return false;
                value = (ReadInt16(data, offset) / 32767f, ReadInt16(data, offset + 2) / 32767f, ReadInt16(data, offset + 4) / 32767f);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadUv(byte[] data, int offset, T3VertexComponent.EnumType type, out (float U, float V) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 8 > data.Length) return false;
                value = (ReadSingle(data, offset), 1f - ReadSingle(data, offset + 4));
                return true;
            case T3VertexComponent.EnumType.VTypeS16N:
                if (offset + 4 > data.Length) return false;
                value = (ReadInt16(data, offset) / 32767f, 1f - ReadInt16(data, offset + 2) / 32767f);
                return true;
            case T3VertexComponent.EnumType.VTypeU16N:
                if (offset + 4 > data.Length) return false;
                value = (ReadUInt16(data, offset) / 65535f, 1f - ReadUInt16(data, offset + 2) / 65535f);
                return true;
            case T3VertexComponent.EnumType.VTypeSF16:
                if (offset + 4 > data.Length) return false;
                value = (ReadHalf(data, offset), 1f - ReadHalf(data, offset + 2));
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadWeights(byte[] data, int offset, T3VertexComponent.EnumType type, out (float A, float B, float C, float D) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 12 > data.Length) return false;
                value = (ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8), 0f);
                return true;
            case T3VertexComponent.EnumType.VTypeS16N:
                if (offset + 8 > data.Length) return false;
                value = (ReadInt16(data, offset) / 32767f, ReadInt16(data, offset + 2) / 32767f, ReadInt16(data, offset + 4) / 32767f, ReadInt16(data, offset + 6) / 32767f);
                return true;
            case T3VertexComponent.EnumType.VTypeU16N:
                if (offset + 8 > data.Length) return false;
                value = (ReadUInt16(data, offset) / 65535f, ReadUInt16(data, offset + 2) / 65535f, ReadUInt16(data, offset + 4) / 65535f, ReadUInt16(data, offset + 6) / 65535f);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadColor(byte[] data, int offset, T3VertexComponent.EnumType type, out (float R, float G, float B, float A) value)
    {
        value = default;
        switch (type)
        {
            case T3VertexComponent.EnumType.VTypeFloat:
                if (offset + 16 > data.Length) return false;
                value = (ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8), ReadSingle(data, offset + 12));
                return true;
            case T3VertexComponent.EnumType.VTypeU8N:
                if (offset + 4 > data.Length) return false;
                value = (data[offset] / 255f, data[offset + 1] / 255f, data[offset + 2] / 255f, data[offset + 3] / 255f);
                return true;
            default:
                return false;
        }
    }

    private static int ComponentSize(T3VertexComponent.EnumType type, uint count)
    {
        var itemSize = type switch
        {
            T3VertexComponent.EnumType.VTypeFloat => 4,
            T3VertexComponent.EnumType.VTypeS8N or T3VertexComponent.EnumType.VTypeU8N or T3VertexComponent.EnumType.VTypeS8NBones => 1,
            T3VertexComponent.EnumType.VTypeS16N or T3VertexComponent.EnumType.VTypeU16N or T3VertexComponent.EnumType.VTypeSF16 => 2,
            _ => 0,
        };

        return itemSize * Math.Max(1, checked((int)count));
    }

    private static (float A, float B, float C, float D) Normalize((float A, float B, float C, float D) value)
    {
        var total = value.A + value.B + value.C + value.D;
        if (total <= 0.000001f)
        {
            return (1f, 0f, 0f, 0f);
        }

        return (value.A / total, value.B / total, value.C / total, value.D / total);
    }

    private static T ValueOrDefault<T>(IReadOnlyList<T> values, int index, T fallback)
        => index >= 0 && index < values.Count ? values[index] : fallback;

    private static void AddTextureSlot(SubmeshData submesh, string slot, Handle<T3Texture>? handle)
    {
        var name = TextureName(handle);
        if (!string.IsNullOrWhiteSpace(name))
        {
            submesh.TextureNames[slot] = name;
        }
    }

    private static string? TextureName(Handle<T3Texture>? handle)
    {
        if (handle is null || handle.ObjectInfo.ObjectName.IsEmpty)
        {
            return null;
        }

        return handle.ObjectInfo.ObjectName.DebugString ?? $"0x{handle.ObjectInfo.ObjectName.Crc64:X16}";
    }

    private static ulong ResolveBoneHash(D3DMesh.PaletteEntry entry)
    {
        if (entry.SymbolBoneName is not null && !entry.SymbolBoneName.IsEmpty)
        {
            return entry.SymbolBoneName.Crc64;
        }

        return string.IsNullOrWhiteSpace(entry.BoneName) ? 0UL : TelltaleToolKit.Hashing.Crc64.Compute(entry.BoneName);
    }

    private static int NormalizePaletteIndex(int rawIndex, int paletteCount)
    {
        if (paletteCount <= 0)
        {
            return 0;
        }

        if (rawIndex >= 0 && rawIndex < paletteCount)
        {
            return rawIndex;
        }

        if (rawIndex > 0 && rawIndex - 1 < paletteCount)
        {
            return rawIndex - 1;
        }

        return 0;
    }

    private static float ReadSingle(byte[] data, int offset)
        => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

    private static short ReadInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));

    private static ushort ReadUInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static float ReadHalf(byte[] data, int offset)
        => (float)BitConverter.UInt16BitsToHalf(ReadUInt16(data, offset));

    private static int ReadIndex(byte[] data, int offset, int indexSize)
        => indexSize == 4
            ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static void EnsureToolkitInitialized()
    {
        if (Toolkit.IsInitialized)
        {
            return;
        }

        lock (ToolkitGate)
        {
            if (!Toolkit.IsInitialized)
            {
                Toolkit.Initialize(new Toolkit.Configuration
                {
                    DataFolder = Path.Combine(AppContext.BaseDirectory, "ttk-data"),
                });
            }
        }
    }

    internal sealed record OldBonePaletteInfo(
        IReadOnlyList<ulong[]> Palettes,
        IReadOnlyList<int> TriangleSetPaletteIndices);
}
