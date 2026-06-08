using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

public static class SkeletonLoader
{
    public static SkeletonData Load(string skeletonPath, int version)
    {
        if (ShouldPreferToolkit(skeletonPath))
        {
            try
            {
                return SkeletonRebuilder.ParseWithToolkit(skeletonPath);
            }
            catch
            {
                // Keep the older direct reader as a resilience fallback when Toolkit metadata is absent.
            }
        }

        try
        {
            return SkeletonParser.Parse(File.ReadAllBytes(skeletonPath), version);
        }
        catch
        {
            return SkeletonRebuilder.ParseWithToolkit(skeletonPath);
        }
    }

    private static bool ShouldPreferToolkit(string skeletonPath)
        => GameConfig.Current.Id == GameId.MinecraftStoryMode ||
           skeletonPath.Contains("MCSM", StringComparison.OrdinalIgnoreCase) ||
           skeletonPath.Contains("Minecraft", StringComparison.OrdinalIgnoreCase);
}
