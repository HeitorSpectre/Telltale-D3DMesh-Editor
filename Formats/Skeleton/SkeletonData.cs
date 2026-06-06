namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Skeleton extracted from .skl: bones with local transform (translation + quaternion)
// and parent index/hash. Independent of output format.
public sealed class SkeletonData
{
    public List<BoneData> Bones { get; } = [];
}

public sealed record BoneData(
    string Name,
    ulong Hash,
    int ParentIndex,
    float X,
    float Y,
    float Z,
    float Qx,
    float Qy,
    float Qz,
    float Qw,
    ulong ParentHash = 0);
