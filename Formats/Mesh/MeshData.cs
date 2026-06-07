namespace TelltaleD3DMeshEditor.Formats.Mesh;

// Neutral data model for a mesh extracted from .d3dmesh: submeshes, bone palettes,
// and counters. Independent of output format (GLTF/preview consume this).
public sealed class MeshData
{
    public string Name { get; init; } = "";
    public int Version { get; init; }
    public List<SubmeshData> Submeshes { get; } = [];
    public List<ulong[]> BonePalettes { get; } = [];
    public int VertexCount => Submeshes.Sum(s => s.Vertices.Count);
    public int FaceCount => Submeshes.Sum(s => s.Faces.Count);

    public (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) GetBounds()
    {
        var vertices = Submeshes.SelectMany(s => s.Vertices).ToList();
        if (vertices.Count == 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        return (
            vertices.Min(v => v.X),
            vertices.Min(v => v.Y),
            vertices.Min(v => v.Z),
            vertices.Max(v => v.X),
            vertices.Max(v => v.Y),
            vertices.Max(v => v.Z));
    }
}

public sealed class SubmeshData
{
    public string Name { get; init; } = "";
    public string? MaterialName { get; init; }
    public int BonePaletteIndex { get; init; }
    public string? SourceMeshPath { get; init; }
    public int SourceSubmeshIndex { get; init; } = -1;
    public Dictionary<string, string> TextureNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VertexData> Vertices { get; } = [];
    public List<(int A, int B, int C)> Faces { get; } = [];

    public (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) GetBounds()
    {
        if (Vertices.Count == 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        return (
            Vertices.Min(v => v.X),
            Vertices.Min(v => v.Y),
            Vertices.Min(v => v.Z),
            Vertices.Max(v => v.X),
            Vertices.Max(v => v.Y),
            Vertices.Max(v => v.Z));
    }
}

public readonly record struct VertexData(
    float X,
    float Y,
    float Z,
    float Nx,
    float Ny,
    float Nz,
    float U,
    float V,
    float U2,
    float V2,
    float U3,
    float V3,
    float U4,
    float V4,
    int Bone0,
    int Bone1,
    int Bone2,
    int Bone3,
    float Weight0,
    float Weight1,
    float Weight2,
    float Weight3,
    float ColorR = 1f,
    float ColorG = 1f,
    float ColorB = 1f,
    float ColorA = 1f,
    float Unknown1 = 0f,
    float BinormalX = 0f,
    float BinormalY = 0f,
    float BinormalZ = 0f,
    float BinormalW = 0f,
    float TangentX = 0f,
    float TangentY = 0f,
    float TangentZ = 0f,
    float TangentW = 0f);
