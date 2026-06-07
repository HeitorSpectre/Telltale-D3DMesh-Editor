namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Finds the .skl matching a .d3dmesh by file name (same stem/prefix), preferring a skeleton
// located in the same folder as the mesh.
public static class SkeletonResolver
{
    public static string? FindForMesh(string assetFolder, string meshPath)
    {
        if (!Directory.Exists(assetFolder))
        {
            return null;
        }

        var skeletonFiles = Directory.EnumerateFiles(assetFolder, "*.skl", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return FindForMesh(meshPath, skeletonFiles);
    }

    public static string? FindForMesh(string meshPath, IReadOnlyCollection<string> skeletonFiles)
    {
        if (skeletonFiles.Count == 0)
        {
            return null;
        }

        var skeletonsByStem = skeletonFiles
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return FindForMesh(meshPath, skeletonsByStem);
    }

    public static string? FindForMesh(string meshPath, IReadOnlyDictionary<string, List<string>> skeletonsByStem)
    {
        var stem = Path.GetFileNameWithoutExtension(meshPath);
        var meshDir = Path.GetDirectoryName(meshPath);

        List<string>? candidates = null;
        if (skeletonsByStem.TryGetValue(stem, out var exact))
        {
            candidates = exact;
        }
        else
        {
            var bestKey = skeletonsByStem.Keys
                .Where(key => stem.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase) ||
                              stem.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(key => key.Length)
                .FirstOrDefault();
            if (bestKey is not null)
            {
                candidates = skeletonsByStem[bestKey];
            }
        }

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        return candidates.FirstOrDefault(path =>
                   string.Equals(Path.GetDirectoryName(path), meshDir, StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }
}
