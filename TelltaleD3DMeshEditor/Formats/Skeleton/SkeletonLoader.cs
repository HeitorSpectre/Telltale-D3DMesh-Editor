namespace TelltaleD3DMeshEditor.Formats.Skeleton;

public static class SkeletonLoader
{
    public static SkeletonData Load(string skeletonPath, int version)
    {
        try
        {
            return SkeletonParser.Parse(File.ReadAllBytes(skeletonPath), version);
        }
        catch
        {
            return SkeletonRebuilder.ParseWithToolkit(skeletonPath);
        }
    }
}
