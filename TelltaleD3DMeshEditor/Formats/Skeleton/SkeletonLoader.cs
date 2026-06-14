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
                return ValidateLoadedSkeleton(SkeletonRebuilder.ParseWithToolkit(skeletonPath));
            }
            catch
            {
                // Keep the older direct reader as a resilience fallback when Toolkit metadata is absent.
            }
        }

        try
        {
            return ValidateLoadedSkeleton(SkeletonParser.Parse(File.ReadAllBytes(skeletonPath), version));
        }
        catch
        {
            return ValidateLoadedSkeleton(SkeletonRebuilder.ParseWithToolkit(skeletonPath));
        }
    }

    private static bool ShouldPreferToolkit(string skeletonPath)
        => GameConfig.Current.Id == GameId.MinecraftStoryMode ||
           skeletonPath.Contains("MCSM", StringComparison.OrdinalIgnoreCase) ||
           skeletonPath.Contains("Minecraft", StringComparison.OrdinalIgnoreCase);

    private static SkeletonData ValidateLoadedSkeleton(SkeletonData skeleton)
    {
        if (LooksLikeMisalignedLegacySkeleton(skeleton))
        {
            throw new InvalidDataException("Skeleton layout is not supported by the available Telltale skeleton readers.");
        }

        return skeleton;
    }

    private static bool LooksLikeMisalignedLegacySkeleton(SkeletonData skeleton)
    {
        if (skeleton.Bones.Count == 0)
        {
            return false;
        }

        return skeleton.Bones.All(bone =>
            bone.Hash == 0 &&
            Math.Abs(bone.X) <= 0.000001f &&
            Math.Abs(bone.Y) <= 0.000001f &&
            Math.Abs(bone.Z) <= 0.000001f);
    }
}
