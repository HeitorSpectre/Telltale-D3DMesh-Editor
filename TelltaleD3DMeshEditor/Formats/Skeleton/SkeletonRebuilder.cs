using System.Buffers.Binary;
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
    private const float LegacyTransformEpsilon = 1e-5f;
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
    // <paramref name="translationScaleDonor"/> (optional) supplies per-bone translation scales from
    // the imported model's own skeleton, replacing the target's retarget scales bone-by-bone.
    public static byte[] RebuildWithEdits(string originalSklPath, SkeletonData edited, SkeletonData? translationScaleDonor = null)
    {
        var (skeleton, config, _) = Read(originalSklPath);
        SkeletonMerger.Merge(skeleton, edited);
        if (translationScaleDonor is not null)
        {
            SkeletonMerger.AdoptTranslationScales(skeleton, translationScaleDonor);
        }

        using var output = new MemoryStream();
        Toolkit.Instance.Serialize(skeleton, output, config);
        return output.ToArray();
    }

    public static byte[] RebuildWithEdits(string originalSklPath, SkeletonData edited, GameConfig gameConfig)
    {
        if (gameConfig.IsOriginalTalesFromTheBorderlandsPc || gameConfig.Id == GameId.GameOfThrones)
        {
            // Original v17 PC builds use a legacy MSV skeleton layout that the toolkit cannot serialize yet.
            // Rebuild it by applying the imported GLB's local bone transforms onto the target .skl
            // entry table, matching the same partial-skeleton workflow used by the other games.
            return RebuildLegacyMsvSkeletonWithEdits(
                originalSklPath,
                edited,
                allowCharacterSpecificAliases: !gameConfig.DisableCharacterSpecificFacialRetargetOnReimport);
        }

        return RebuildWithEdits(originalSklPath, edited);
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

    private static byte[] RebuildLegacyMsvSkeletonWithEdits(
        string originalSklPath,
        SkeletonData edited,
        bool allowCharacterSpecificAliases)
    {
        var bytes = File.ReadAllBytes(originalSklPath);
        var records = ReadLegacyMsvBoneRecords(bytes);
        var editedByHash = edited.Bones
            .Select((bone, index) => (Bone: bone, Index: index))
            .Where(item => item.Bone.Hash != 0)
            .GroupBy(item => item.Bone.Hash)
            .ToDictionary(group => group.Key, group => group.First());
        var editedByName = edited.Bones
            .Select((bone, index) => (Bone: bone, Index: index))
            .Where(item => !string.IsNullOrWhiteSpace(item.Bone.Name))
            .GroupBy(item => item.Bone.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var editedByAlias = allowCharacterSpecificAliases
            ? edited.Bones
                .Select((bone, index) => (Bone: bone, Index: index, HasAlias: BoneNameAliases.TryGetCharacterSpecificAlias(bone.Name, out var alias), Alias: alias))
                .Where(item => item.HasAlias)
                .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (group.First().Bone, group.First().Index), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, (BoneData Bone, int Index)>(StringComparer.OrdinalIgnoreCase);
        var editedWorlds = BuildLegacyWorldMatrices(edited);
        var outputWorlds = new List<Matrix4x4>(records.Count);

        var matchedBones = 0;
        foreach (var record in records)
        {
            var position = record.LocalPosition;
            var rotation = record.LocalRotation;
            if (TryFindEditedBone(record, editedByHash, editedByName, editedByAlias, allowCharacterSpecificAliases, out var match))
            {
                matchedBones++;
                var targetWorld = editedWorlds[match.Index];
                if (!TryGetLegacyLocalTransform(targetWorld, record.ParentIndex, outputWorlds, out position, out rotation))
                {
                    position = new Vector3(match.Bone.X, match.Bone.Y, match.Bone.Z);
                    rotation = NormalizeLegacyRotation(new Quaternion(match.Bone.Qx, match.Bone.Qy, match.Bone.Qz, match.Bone.Qw));
                }

                if (!LegacyTransformMatches(record, position, rotation))
                {
                    WriteLegacyF32(bytes, record.PositionOffset, position.X);
                    WriteLegacyF32(bytes, record.PositionOffset + 4, position.Y);
                    WriteLegacyF32(bytes, record.PositionOffset + 8, position.Z);
                    WriteLegacyF32(bytes, record.RotationOffset, rotation.X);
                    WriteLegacyF32(bytes, record.RotationOffset + 4, rotation.Y);
                    WriteLegacyF32(bytes, record.RotationOffset + 8, rotation.Z);
                    WriteLegacyF32(bytes, record.RotationOffset + 12, rotation.W);
                }
            }

            outputWorlds.Add(BuildLegacyEntryWorld(position, rotation, record.ParentIndex, outputWorlds));
        }

        if (matchedBones == 0 && edited.Bones.Count > 0)
        {
            throw new InvalidDataException(
                $"TFTB skeleton rebuild could not match any imported GLB bones to '{Path.GetFileName(originalSklPath)}'.");
        }

        return bytes;
    }

    private static List<LegacyMsvBoneRecord> ReadLegacyMsvBoneRecords(byte[] data)
    {
        var header = MetaStreamHeader.Parse(data);
        if (header.Version == "MTRE")
        {
            throw new InvalidDataException("Legacy MSV skeleton rebuild does not support MTRE/ERTM skeletons.");
        }

        var reader = new DataReader(data);
        if (header.DataOffset > 0)
        {
            reader.Seek(header.DataOffset);
        }

        reader.ReadUInt32();
        var boneCount = checked((int)reader.ReadUInt32());
        if (boneCount < 0 || boneCount > 4096)
        {
            throw new InvalidDataException($"Invalid legacy MSV skeleton bone count: {boneCount}");
        }

        var records = new List<LegacyMsvBoneRecord>(boneCount);
        for (var i = 0; i < boneCount; i++)
        {
            var hash = ReadLegacySymbolHash(reader);
            ReadLegacySymbolHash(reader);
            var parentIndex = reader.ReadInt32();
            var positionOffset = reader.Position;
            var position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
            var rotationOffset = reader.Position;
            var rotation = NormalizeLegacyRotation(new Quaternion(
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat(),
                reader.ReadFloat()));

            reader.ReadUInt32();
            reader.Skip(3 * 4);
            reader.ReadFloat();
            reader.Skip(3 * 4);
            reader.Skip(9 * 4);

            reader.ReadUInt32();
            var ikCount = checked((int)reader.ReadUInt32());
            for (var ik = 0; ik < ikCount; ik++)
            {
                var nameLength = checked((int)reader.ReadUInt32());
                reader.Skip(nameLength);
                reader.ReadFloat();
            }

            reader.ReadUInt32();
            var piAmount = checked((int)reader.ReadUInt32());
            reader.Skip(piAmount * 12);
            reader.ReadUInt32();
            reader.Skip(24);
            reader.ReadFloat();

            records.Add(new LegacyMsvBoneRecord(
                hash,
                BoneHashDatabase.Resolve(hash) ?? $"bone_{hash:X16}",
                parentIndex,
                position,
                rotation,
                positionOffset,
                rotationOffset));
        }

        return records;
    }

    private static bool TryFindEditedBone(
        LegacyMsvBoneRecord record,
        IReadOnlyDictionary<ulong, (BoneData Bone, int Index)> editedByHash,
        IReadOnlyDictionary<string, (BoneData Bone, int Index)> editedByName,
        IReadOnlyDictionary<string, (BoneData Bone, int Index)> editedByAlias,
        bool allowCharacterSpecificAliases,
        out (BoneData Bone, int Index) bone)
    {
        if (record.Hash != 0 && editedByHash.TryGetValue(record.Hash, out bone!))
        {
            return true;
        }

        if (editedByName.TryGetValue(record.Name, out bone!))
        {
            return true;
        }

        if (allowCharacterSpecificAliases &&
            BoneNameAliases.TryGetCharacterSpecificAlias(record.Name, out var alias) &&
            editedByAlias.TryGetValue(alias, out bone!))
        {
            return true;
        }

        bone = default;
        return false;
    }

    private static bool LegacyTransformMatches(LegacyMsvBoneRecord record, Vector3 position, Quaternion rotation)
        => Math.Abs(record.LocalPosition.X - position.X) <= LegacyTransformEpsilon &&
           Math.Abs(record.LocalPosition.Y - position.Y) <= LegacyTransformEpsilon &&
           Math.Abs(record.LocalPosition.Z - position.Z) <= LegacyTransformEpsilon &&
           LegacyRotationDelta(record.LocalRotation, rotation) <= LegacyTransformEpsilon;

    private static float LegacyRotationDelta(Quaternion a, Quaternion b)
    {
        a = NormalizeLegacyRotation(a);
        b = NormalizeLegacyRotation(b);
        var direct =
            Math.Abs(a.X - b.X) +
            Math.Abs(a.Y - b.Y) +
            Math.Abs(a.Z - b.Z) +
            Math.Abs(a.W - b.W);
        var negated =
            Math.Abs(a.X + b.X) +
            Math.Abs(a.Y + b.Y) +
            Math.Abs(a.Z + b.Z) +
            Math.Abs(a.W + b.W);
        return Math.Min(direct, negated);
    }

    private static Matrix4x4[] BuildLegacyWorldMatrices(SkeletonData skeleton)
    {
        var worlds = new Matrix4x4[skeleton.Bones.Count];
        var states = new byte[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            BuildLegacyWorldMatrix(i, skeleton, worlds, states);
        }

        return worlds;
    }

    private static Matrix4x4 BuildLegacyWorldMatrix(int index, SkeletonData skeleton, Matrix4x4[] worlds, byte[] states)
    {
        if (states[index] == 2)
        {
            return worlds[index];
        }

        if (states[index] == 1)
        {
            return Matrix4x4.Identity;
        }

        states[index] = 1;
        var bone = skeleton.Bones[index];
        var local = BuildLegacyLocalMatrix(
            new Vector3(bone.X, bone.Y, bone.Z),
            new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw));
        var parent = bone.ParentIndex;
        worlds[index] = parent >= 0 && parent < skeleton.Bones.Count
            ? local * BuildLegacyWorldMatrix(parent, skeleton, worlds, states)
            : local;
        states[index] = 2;
        return worlds[index];
    }

    private static Matrix4x4 BuildLegacyEntryWorld(
        Vector3 position,
        Quaternion rotation,
        int parentIndex,
        IReadOnlyList<Matrix4x4> outputWorlds)
    {
        var local = BuildLegacyLocalMatrix(position, rotation);
        return parentIndex >= 0 && parentIndex < outputWorlds.Count
            ? local * outputWorlds[parentIndex]
            : local;
    }

    private static Matrix4x4 BuildLegacyLocalMatrix(Vector3 position, Quaternion rotation)
        => Matrix4x4.CreateFromQuaternion(NormalizeLegacyRotation(rotation)) *
           Matrix4x4.CreateTranslation(position);

    private static bool TryGetLegacyLocalTransform(
        Matrix4x4 targetWorld,
        int parentIndex,
        IReadOnlyList<Matrix4x4> outputWorlds,
        out Vector3 position,
        out Quaternion rotation)
    {
        var local = targetWorld;
        if (parentIndex >= 0 &&
            parentIndex < outputWorlds.Count &&
            Matrix4x4.Invert(outputWorlds[parentIndex], out var inverseParent))
        {
            local = targetWorld * inverseParent;
        }

        if (!Matrix4x4.Decompose(local, out _, out rotation, out position))
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = NormalizeLegacyRotation(rotation);
        return true;
    }

    private static Quaternion NormalizeLegacyRotation(Quaternion rotation)
    {
        if (rotation.LengthSquared() < 0.000001f)
        {
            return Quaternion.Identity;
        }

        return Quaternion.Normalize(rotation);
    }

    private static ulong ReadLegacySymbolHash(DataReader reader)
    {
        var low = reader.ReadUInt32();
        var high = reader.ReadUInt32();
        return ((ulong)high << 32) | low;
    }

    private static void WriteLegacyF32(byte[] data, int offset, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);

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

internal sealed record LegacyMsvBoneRecord(
    ulong Hash,
    string Name,
    int ParentIndex,
    System.Numerics.Vector3 LocalPosition,
    System.Numerics.Quaternion LocalRotation,
    int PositionOffset,
    int RotationOffset);
