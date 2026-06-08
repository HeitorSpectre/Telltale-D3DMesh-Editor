using System.Numerics;
using TelltaleToolKit;
using TelltaleToolKit.Meta.Serialization;
using TelltaleD3DMeshEditor.Core;
using TelltaleToolKit.T3Types;
using TtkSkeleton = TelltaleToolKit.T3Types.Skeletons.Skeleton;

namespace TelltaleD3DMeshEditor.Formats.Skeleton;

// Real .skl reconstruction, using the TelltaleToolKit's official Skeleton MetaClass as the format
// reference. A .skl is read into a fully-typed skeleton object (joints, transforms, constraints,
// flags) and written back out from that structure — a genuine rebuild, never a copy. Validated as
// byte-identical to the original across every test skeleton (The Wolf Among Us + The Walking Dead:
// Season 2), so when the skeleton is unmodified the rebuilt file matches the game's exactly.
public static class SkeletonRebuilder
{
    private static readonly object Gate = new();
    private static List<Workspace>? _workspaces;

    // Reads <paramref name="sklPath"/> and writes a freshly-reconstructed .skl to <paramref name="outputPath"/>.
    public static void Rebuild(string sklPath, string outputPath)
    {
        var (skeleton, config, _) = Read(sklPath);
        Toolkit.Instance.Serialize(skeleton, outputPath, config);
    }

    // Reconstructs a .skl from the original plus an edited joint set (e.g. from a reimported GLB),
    // and returns the rebuilt bytes. Unchanged joints keep all their original data, so an unmodified
    // skeleton comes out byte-identical to the game's file; moved/added joints are applied faithfully.
    public static byte[] RebuildWithEdits(string originalSklPath, SkeletonData edited)
    {
        var (skeleton, config, _) = Read(originalSklPath);
        SkeletonMerger.Merge(skeleton, edited);
        using var output = new MemoryStream();
        Toolkit.Instance.Serialize(skeleton, output, config);
        return output.ToArray();
    }

    // True when reconstructing the skeleton (without changes) reproduces the original file byte-for-byte.
    // This is the real proof that the tool reads, interprets and rebuilds the format correctly.
    public static bool ValidateRoundTrip(string sklPath)
    {
        var (skeleton, config, _) = Read(sklPath);
        using var rebuilt = new MemoryStream();
        Toolkit.Instance.Serialize(skeleton, rebuilt, config);
        return File.ReadAllBytes(sklPath).AsSpan().SequenceEqual(rebuilt.ToArray());
    }

    public static SkeletonData ParseWithToolkit(string sklPath)
    {
        var (skeleton, _, _) = Read(sklPath);
        var result = new SkeletonData();
        for (var i = 0; i < skeleton.Entries.Count; i++)
        {
            var entry = skeleton.Entries[i];
            var hash = entry.JointName?.Crc64 ?? 0;
            var parentHash = entry.ParentName?.Crc64 ?? 0;
            if (parentHash == 0 &&
                entry.ParentIndex >= 0 &&
                entry.ParentIndex < skeleton.Entries.Count)
            {
                parentHash = skeleton.Entries[entry.ParentIndex].JointName?.Crc64 ?? 0;
            }

            var name = GetSymbolName(entry.JointName) ??
                       BoneHashDatabase.Resolve(hash) ??
                       $"bone_{hash:X16}";
            var mirrorHash = entry.MirrorBoneName?.Crc64 ?? 0;
            result.Bones.Add(new BoneData(
                name,
                hash,
                entry.ParentIndex,
                entry.LocalPosition.X,
                entry.LocalPosition.Y,
                entry.LocalPosition.Z,
                entry.LocalQuat.X,
                entry.LocalQuat.Y,
                entry.LocalQuat.Z,
                entry.LocalQuat.W,
                parentHash)
            {
                MirrorBoneHash = mirrorHash,
                MirrorBoneIndex = mirrorHash != 0 ? entry.MirrorBoneIndex : -1,
                BoneLength = entry.BoneLength,
                BoneDir = entry.BoneDir,
                BoneRotationAdjustment = NormalizeRotationOrIdentity(entry.BoneRotationAdjustment),
                RestTranslation = entry.RestXform?.Translation ?? default,
                RestRotation = NormalizeRotationOrIdentity(entry.RestXform?.Rotation ?? default),
                GlobalTranslationScale = NonZeroOrOne(entry.GlobalTranslationScale),
                LocalTranslationScale = NonZeroOrOne(entry.LocalTranslationScale),
                AnimTranslationScale = NonZeroOrOne(entry.AnimTranslationScale),
            });
        }

        return result;
    }

    private static Vector3 NonZeroOrOne(Vector3 value)
        => value.LengthSquared() > 0.000001f ? value : Vector3.One;

    private static Quaternion NormalizeRotationOrIdentity(Quaternion rotation)
        => rotation.LengthSquared() > 0.000001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;

    public static IReadOnlyList<SkeletonEntryDiagnostics> ReadEntryDiagnostics(string sklPath)
    {
        var (skeleton, _, _) = Read(sklPath);
        return skeleton.Entries
            .Select((entry, index) => new SkeletonEntryDiagnostics(
                index,
                GetSymbolName(entry.JointName) ?? BoneHashDatabase.Resolve(entry.JointName?.Crc64 ?? 0) ?? $"bone_{entry.JointName?.Crc64 ?? 0:X16}",
                entry.JointName?.Crc64 ?? 0,
                entry.ParentIndex,
                entry.LocalPosition,
                entry.LocalQuat,
                entry.RestXform?.Translation ?? default,
                entry.RestXform?.Rotation ?? default,
                entry.BoneLength,
                entry.BoneDir,
                entry.BoneRotationAdjustment,
                entry.GlobalTranslationScale,
                entry.LocalTranslationScale,
                entry.AnimTranslationScale))
            .ToList();
    }

    private static string? GetSymbolName(Symbol? symbol)
    {
        if (symbol is null || string.IsNullOrWhiteSpace(symbol.DebugString))
        {
            return null;
        }

        return symbol.DebugString;
    }

    // Builds a brand-new .skl from a foreign joint set that has no original to merge with (e.g. a rig
    // from a downloaded model) and returns the bytes, written in the target game's MetaStream format.
    public static byte[] WriteNewSkeleton(SkeletonData skeleton, string? preferredGame = null)
    {
        EnsureInitialized();
        var workspace = ResolveWorkspace(preferredGame);
        var built = SkeletonBuilder.Build(skeleton);
        using var output = new MemoryStream();
        Toolkit.Instance.Serialize(built, output, workspace.DefaultMetaStreamConfig);
        return output.ToArray();
    }

    private static Workspace ResolveWorkspace(string? preferredGame)
    {
        var all = Workspaces();
        if (!string.IsNullOrEmpty(preferredGame))
        {
            var match = all.FirstOrDefault(workspace =>
                workspace.Name.Contains(preferredGame, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return all.First();
    }

    // Number of joints in a skeleton file (0 if it can't be interpreted).
    public static int CountJoints(string sklPath)
    {
        try
        {
            return Read(sklPath).Skeleton.Entries.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static (TtkSkeleton Skeleton, MetaStreamParams Config, Workspace Workspace) Read(string sklPath)
    {
        EnsureInitialized();

        // The skeleton MetaClass layout is game-version specific, so try each catalogued game's context
        // until one interprets the file into real joints.
        foreach (var workspace in Workspaces())
        {
            try
            {
                var (skeleton, config) = Toolkit.Instance.DeserializeWithConfig<TtkSkeleton>(sklPath, workspace);
                if (skeleton is { Entries.Count: > 0 } && config is not null)
                {
                    return (skeleton, config, workspace);
                }
            }
            catch
            {
                // Wrong game/version context for this file; try the next one.
            }
        }

        throw new InvalidDataException(
            $"Could not interpret '{Path.GetFileName(sklPath)}' as a Telltale skeleton with any known game profile.");
    }

    private static void EnsureInitialized()
    {
        if (Toolkit.IsInitialized)
        {
            return;
        }

        lock (Gate)
        {
            if (!Toolkit.IsInitialized)
            {
                // The toolkit looks for its data ("game_profiles", "versiondb", "hashdb") relative to the
                // working directory by default; point it at the copy that ships next to our .exe.
                Toolkit.Initialize(new Toolkit.Configuration
                {
                    DataFolder = Path.Combine(AppContext.BaseDirectory, "ttk-data"),
                });
            }
        }
    }

    private static List<Workspace> Workspaces()
    {
        lock (Gate)
        {
            if (_workspaces is not null)
            {
                return _workspaces;
            }

            _workspaces = [];
            foreach (var profileName in Toolkit.Instance.GameProfiles.Keys)
            {
                try
                {
                    _workspaces.Add(Toolkit.Instance.CreateWorkspace($"skl::{profileName}", profileName));
                }
                catch
                {
                    // Skip profiles whose version database can't be loaded.
                }
            }

            return _workspaces;
        }
    }
}

public sealed record SkeletonEntryDiagnostics(
    int Index,
    string Name,
    ulong Hash,
    int ParentIndex,
    System.Numerics.Vector3 LocalPosition,
    System.Numerics.Quaternion LocalRotation,
    System.Numerics.Vector3 RestTranslation,
    System.Numerics.Quaternion RestRotation,
    float BoneLength,
    System.Numerics.Vector3 BoneDir,
    System.Numerics.Quaternion BoneRotationAdjustment,
    System.Numerics.Vector3 GlobalTranslationScale,
    System.Numerics.Vector3 LocalTranslationScale,
    System.Numerics.Vector3 AnimTranslationScale);
