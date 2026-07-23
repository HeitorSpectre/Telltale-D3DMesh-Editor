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

    // Bone HASHES only, for indexing (palette matching during folder discovery). The lightweight
    // reader recovers hashes correctly even for the modern layouts whose POSE it cannot rebuild,
    // and it is much cheaper than the toolkit — folder loads with hundreds of .skl depend on this.
    public static SkeletonData LoadForHashIndex(string skeletonPath, int version)
    {
        try
        {
            return ValidateLoadedSkeleton(SkeletonParser.Parse(File.ReadAllBytes(skeletonPath), version));
        }
        catch
        {
            return Load(skeletonPath, version);
        }
    }

    private static bool ShouldPreferToolkit(string skeletonPath)
        => GameConfig.Current.Id == GameId.MinecraftStoryMode ||
           // MCSM Season 2 (.skl v45+) stores its bind pose in the rich RestXform fields the
           // lightweight reader skips; without the toolkit the skeleton comes out lying down
           // (and a rebuilt .skl inherits the wrong pose). Game-config based, not path based.
           GameConfig.Current.Id == GameId.MinecraftStoryModeSeason2 ||
           // Batman: The Telltale Series (.skl v46) stores its bind pose in the rich RestXform/translation
           // scale fields that the lightweight reader skips; the Telltale Toolkit reconstructs the full
           // bind pose (arms/legs/face land on the mesh), so prefer it for this profile.
           GameConfig.Current.Id == GameId.Batman ||
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
