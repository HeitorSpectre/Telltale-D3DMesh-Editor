using System.Numerics;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Skeleton extracted from .skl. Local TRS stays the portable core, while optional rich fields
// mirror the Toolkit Skeleton MetaClass for games that need better preview/export diagnostics.
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
    ulong ParentHash = 0)
{
    public ulong MirrorBoneHash { get; init; }
    public int MirrorBoneIndex { get; init; } = -1;
    public float BoneLength { get; init; }
    public Vector3 BoneDir { get; init; } = Vector3.Zero;
    public Quaternion BoneRotationAdjustment { get; init; } = Quaternion.Identity;
    public Vector3 RestTranslation { get; init; } = Vector3.Zero;
    public Quaternion RestRotation { get; init; } = Quaternion.Identity;
    public Vector3 GlobalTranslationScale { get; init; } = Vector3.One;
    public Vector3 LocalTranslationScale { get; init; } = Vector3.One;
    public Vector3 AnimTranslationScale { get; init; } = Vector3.One;

    public bool HasRichSkeletonData =>
        MirrorBoneHash != 0 ||
        MirrorBoneIndex >= 0 ||
        BoneLength > 0.000001f ||
        BoneDir.LengthSquared() > 0.000001f ||
        RestTranslation.LengthSquared() > 0.000001f ||
        Math.Abs(RestRotation.X) > 0.000001f ||
        Math.Abs(RestRotation.Y) > 0.000001f ||
        Math.Abs(RestRotation.Z) > 0.000001f ||
        Math.Abs(RestRotation.W - 1f) > 0.000001f;
}
