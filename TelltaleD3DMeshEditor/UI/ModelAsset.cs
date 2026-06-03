using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.UI;

// Represents a discovered model: the .d3dmesh and its matching .skl, when one can be found.
public sealed class ModelAsset
{
    private ModelAsset(string meshPath, string? skeletonPath)
    {
        MeshPath = meshPath;
        SkeletonPath = skeletonPath;
    }

    public string MeshPath { get; }
    public string? SkeletonPath { get; }

    public static List<ModelAsset> Discover(string inputPath)
    {
        if (!Directory.Exists(inputPath))
        {
            return [];
        }

        var meshFiles = Directory.EnumerateFiles(inputPath, "*.d3dmesh", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skeletonFiles = Directory.EnumerateFiles(inputPath, "*.skl", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skeletonsByStem = skeletonFiles
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var skeletonHashes = BuildSkeletonHashIndex(skeletonFiles);

        return meshFiles
            .Select(mesh => new ModelAsset(mesh, ResolveSkeleton(mesh, skeletonsByStem, skeletonHashes)))
            .ToList();
    }

    public override string ToString()
    {
        var name = Path.GetFileNameWithoutExtension(MeshPath);
        return SkeletonPath is null ? name : name + "  + SKL";
    }

    public bool Matches(string inputRoot, string query)
    {
        var fileName = Path.GetFileNameWithoutExtension(MeshPath);
        var relative = Path.GetRelativePath(inputRoot, MeshPath);
        return fileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               relative.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (SkeletonPath is not null &&
                Path.GetFileNameWithoutExtension(SkeletonPath).Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveSkeleton(
        string meshPath,
        IReadOnlyDictionary<string, List<string>> skeletonsByStem,
        IReadOnlyDictionary<string, HashSet<ulong>> skeletonHashes)
    {
        var namedMatch = SkeletonResolver.FindForMesh(meshPath, skeletonsByStem);
        if (namedMatch is not null)
        {
            return namedMatch;
        }

        try
        {
            var mesh = D3DMeshParser.Parse(File.ReadAllBytes(meshPath));
            return FindUniquePaletteMatch(mesh, skeletonHashes);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, HashSet<ulong>> BuildSkeletonHashIndex(IReadOnlyList<string> skeletonFiles)
    {
        var result = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var skeletonPath in skeletonFiles)
        {
            try
            {
                var skeleton = SkeletonParser.Parse(File.ReadAllBytes(skeletonPath), version: 13);
                result[skeletonPath] = skeleton.Bones
                    .Select(bone => bone.Hash)
                    .Where(hash => hash != 0)
                    .ToHashSet();
            }
            catch
            {
                // Keep discovery resilient: a bad SKL should not stop the folder from loading.
            }
        }

        return result;
    }

    private static string? FindUniquePaletteMatch(
        MeshData mesh,
        IReadOnlyDictionary<string, HashSet<ulong>> skeletonHashes)
    {
        var required = mesh.BonePalettes
            .SelectMany(palette => palette)
            .Where(hash => hash != 0)
            .Distinct()
            .ToList();
        if (required.Count == 0 || skeletonHashes.Count == 0)
        {
            return null;
        }

        var exactMatches = skeletonHashes
            .Where(pair => required.All(pair.Value.Contains))
            .Select(pair => pair.Key)
            .ToList();
        return exactMatches.Count == 1 ? exactMatches[0] : null;
    }
}
