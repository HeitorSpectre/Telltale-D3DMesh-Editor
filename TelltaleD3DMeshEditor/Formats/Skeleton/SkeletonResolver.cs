using TelltaleD3DMeshEditor.Core;

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
        else if (GameConfig.Current.IsBackToTheFuture)
        {
            var bestKey = FindBackToTheFutureSkeletonKey(stem, skeletonsByStem.Keys);
            if (bestKey is not null)
            {
                candidates = skeletonsByStem[bestKey];
            }
        }
        if (candidates is null && !GameConfig.Current.IsBackToTheFuture)
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

    private static string? FindBackToTheFutureSkeletonKey(string stem, IEnumerable<string> keys)
    {
        var keyList = keys.ToList();
        foreach (var preferred in BackToTheFuturePreferredSkeletonKeys(stem))
        {
            var match = keyList.FirstOrDefault(key => key.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return keyList
            .Where(key => stem.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase) ||
                          IsBackToTheFuturePrefixSkeletonMatch(stem, key))
            .OrderByDescending(key => key.Length)
            .FirstOrDefault();
    }

    private static bool IsBackToTheFuturePrefixSkeletonMatch(string stem, string key)
    {
        if (!stem.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (key.Equals("obj_delorean", StringComparison.OrdinalIgnoreCase))
        {
            return stem.Equals(key, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static IEnumerable<string> BackToTheFuturePreferredSkeletonKeys(string stem)
    {
        if (stem.StartsWith("obj_inventionjetdrillpiece", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("obj_inventionjetdrill", StringComparison.OrdinalIgnoreCase))
        {
            yield return "obj_inventionjetdrilljets";
        }

        if (stem.Contains("deloreaninteriorui", StringComparison.OrdinalIgnoreCase))
        {
            if (stem.Contains("clock", StringComparison.OrdinalIgnoreCase))
            {
                yield return "obj_deloreaninterioruiclock";
            }

            if (stem.Contains("needle", StringComparison.OrdinalIgnoreCase) ||
                stem.Contains("pedal", StringComparison.OrdinalIgnoreCase) ||
                stem.Contains("gear", StringComparison.OrdinalIgnoreCase) ||
                stem.Contains("steering", StringComparison.OrdinalIgnoreCase))
            {
                yield return "obj_deloreaninterioruiparts";
            }
        }
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var i = 0;
        while (i < length && char.ToUpperInvariant(left[i]) == char.ToUpperInvariant(right[i]))
        {
            i++;
        }

        return i;
    }
}
