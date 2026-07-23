using System.Numerics;
using TelltaleD3DMeshEditor.Formats.Archives;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleToolKit;
using TelltaleToolKit.IO.Archives;
using TelltaleToolKit.IO.Archives.Formats;
using TelltaleToolKit.T3Types.Animations;

namespace TelltaleD3DMeshEditor.Export;

// Discovers .anm animations (loose files and *anichore* archives), decodes them via
// AnimationExporter, and remaps decoded tracks onto an export skeleton by bone CRC64.
// Shared by the CLI (--extract-npc-anim) and the GUI ("Extract with Animations...").
public static class AnimationCollector
{
    public sealed record Candidate(string Name, string? DiskPath, string? ArchivePath);

    private static readonly object ToolkitInitGate = new();

    private static void EnsureToolkitInitialized()
    {
        if (Toolkit.IsInitialized) return;
        lock (ToolkitInitGate)
        {
            if (!Toolkit.IsInitialized)
            {
                Toolkit.Initialize(new Toolkit.Configuration
                {
                    DataFolder = Path.Combine(AppContext.BaseDirectory, "ttk-data"),
                });
            }
        }
    }

    // Name-only scan (no decoding) so pickers can list candidates quickly.
    public static List<Candidate> FindCandidates(string folder, IReadOnlyCollection<string> searchTerms)
    {
        bool Matches(string fileName) =>
            searchTerms.Count == 0 ||
            searchTerms.Any(term => !string.IsNullOrWhiteSpace(term) &&
                                    fileName.Contains(term, StringComparison.OrdinalIgnoreCase));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.anm", SearchOption.AllDirectories)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!Matches(Path.GetFileName(file)) || !seen.Add(name)) continue;
            candidates.Add(new Candidate(name, file, null));
        }

        var archiveFiles = Directory.EnumerateFiles(folder, "*anichore*.ttarch2", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(folder, "*anichore*.ttarch", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (archiveFiles.Count > 0)
        {
            EnsureToolkitInitialized();
            foreach (var archivePath in archiveFiles)
            {
                var archive = TryOpenArchive(archivePath);
                if (archive is null) continue;
                foreach (var entry in archive.GetAllEntries()
                             .Where(e => e.Name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(entry.Name);
                    if (!Matches(entry.Name) || !seen.Add(name)) continue;
                    candidates.Add(new Candidate(name, null, archivePath));
                }
            }
        }

        return candidates;
    }

    // Decodes candidates into raw bone tracks (bone identity = CRC64 as stored in the .anm).
    public static List<(string Name, List<AnimationExporter.BoneTrack> Tracks)> Decode(
        IEnumerable<Candidate> candidates)
    {
        EnsureToolkitInitialized();
        var result = new List<(string, List<AnimationExporter.BoneTrack>)>();
        var archiveCache = new Dictionary<string, Archive?>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            try
            {
                Stream? stream = null;
                if (candidate.DiskPath is not null)
                {
                    stream = File.OpenRead(candidate.DiskPath);
                }
                else if (candidate.ArchivePath is not null)
                {
                    if (!archiveCache.TryGetValue(candidate.ArchivePath, out var archive))
                    {
                        archive = TryOpenArchive(candidate.ArchivePath);
                        archiveCache[candidate.ArchivePath] = archive;
                    }
                    stream = archive?.OpenResource(candidate.Name + ".anm");
                }
                if (stream is null) continue;

                Animation? anim;
                using (stream) { anim = Toolkit.Instance.Deserialize<Animation>(stream); }
                if (anim is null) continue;

                var tracks = AnimationExporter.ExtractTracks(anim);
                if (tracks.Count == 0) continue;
                result.Add((candidate.Name, tracks));
            }
            catch
            {
                // Undecodable animations are skipped; the caller reports counts.
            }
        }

        return result;
    }

    // Filters decoded animations against the export skeleton: drops rigs whose bone count doesn't
    // match (ratio gate), truncates zero translation tails, keeps only CRC64-matched tracks with
    // at least two frames, and renames tracks to the skeleton's bone names.
    public static List<(string Name, List<AnimationExporter.BoneTrack> Tracks)> RemapToSkeleton(
        IReadOnlyList<(string Name, List<AnimationExporter.BoneTrack> Tracks)> animations,
        SkeletonData skeleton)
    {
        var hashToBone = new Dictionary<ulong, int>();
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            hashToBone.TryAdd(skeleton.Bones[i].Hash, i);
        }

        var result = new List<(string, List<AnimationExporter.BoneTrack>)>();
        foreach (var (name, rawTracks) in animations)
        {
            var uniqueBones = rawTracks.Select(t => t.BoneHash).Distinct().Count();
            if (skeleton.Bones.Count > 0)
            {
                var ratio = (float)uniqueBones / skeleton.Bones.Count;
                if (ratio < 0.60f || ratio > 1.15f) continue;
            }

            var remapped = new List<AnimationExporter.BoneTrack>();
            foreach (var track in rawTracks)
            {
                if (!hashToBone.TryGetValue(track.BoneHash, out var boneIndex)) continue;
                if (track.Times.Count < 2) continue;

                var trimmed = TrimZeroTranslationTail(track);
                remapped.Add(new AnimationExporter.BoneTrack(
                    skeleton.Bones[boneIndex].Name, skeleton.Bones[boneIndex].Hash,
                    trimmed.Times, trimmed.Translations, trimmed.Rotations));
            }

            if (remapped.Count > 0)
            {
                result.Add((name, remapped));
            }
        }

        return result;
    }

    private static AnimationExporter.BoneTrack TrimZeroTranslationTail(AnimationExporter.BoneTrack track)
    {
        var last = track.Translations.Count - 1;
        while (last >= 0 && track.Translations[last] == Vector3.Zero) last--;
        if (last < 0 || last >= track.Times.Count - 1)
        {
            return track;
        }

        return new AnimationExporter.BoneTrack(
            track.BoneName, track.BoneHash,
            track.Times.Take(last + 1).ToList(),
            track.Translations.Take(last + 1).ToList(),
            track.Rotations.Take(last + 1).ToList());
    }

    private static Archive? TryOpenArchive(string archivePath)
    {
        foreach (var key in GameProfiles.CandidateKeys())
        {
            try
            {
                return archivePath.EndsWith(".ttarch2", StringComparison.OrdinalIgnoreCase)
                    ? Archive.Load<TTArchive2>(archivePath, key)
                    : Archive.Load<TTArchive>(archivePath, key);
            }
            catch
            {
                // Try the next known key.
            }
        }

        return null;
    }
}
