using TelltaleToolKit.IO.Archives;
using TelltaleToolKit.IO.Archives.Formats;

namespace TelltaleD3DMeshEditor.Formats.Archives;

// Opens Telltale game containers (.ttarch / .ttarch2) with the TelltaleToolKit submodule and
// extracts only the asset types the editor understands (.d3dmesh, .d3dtx, .skl) into a working
// folder, so the user does not have to unpack the archive manually with an external tool.
// Everything else (preview, edit, reimport) is still handled by this project's own parsers.
public static class ArchiveImporter
{
    // Asset extensions the editor can load from the extracted folder. Animations (.anm) come along
    // so "Extract with Animations..." can find them next to the meshes.
    private static readonly string[] AssetExtensions = [".d3dmesh", ".d3dtx", ".skl", ".anm"];

    // Character material property sets live outside the mesh on some games (e.g. MCSM S2
    // skM1_radar/axel): pull sk*.prop alongside so external materials can be resolved later.
    private static bool IsWantedProp(string entryName)
        => entryName.EndsWith(".prop", StringComparison.OrdinalIgnoreCase) &&
           Path.GetFileName(entryName).StartsWith("sk", StringComparison.OrdinalIgnoreCase);

    // Archive extensions we can open. Used to build the open-file dialog filter.
    public static readonly string[] ArchiveExtensions = [".ttarch", ".ttarch2"];

    public sealed record ExtractResult(string OutputFolder, int ExtractedCount, int TotalEntries, string DetectedGame);

    public static bool IsSupportedArchive(string path)
        => ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Derives a logical "section" name from a Telltale archive file name so that archives belonging to
    // the same scene/episode are grouped together in the viewer. Telltale names archives as
    // "<game>_<platform>_<section>_<type>" (for example Fables_pc_Boot_txmesh, Fables_pc_Fables101_data,
    // Fables_pc_Menu_tx), where <type> is data / mesh / tx / txmesh / ms / anichore / etc. Everything
    // between the platform token and the trailing type token is the section: "Boot", "Fables101", "Menu".
    // Extracting every archive of a section into a folder named after that section makes the tree show
    // one clean group per scene (with meshes and their textures side by side) instead of raw file names.
    public static string GetSectionName(string archivePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        var tokens = baseName.Split('_', StringSplitOptions.RemoveEmptyEntries);

        // Need at least game + platform + section + type to safely strip the surrounding tokens.
        if (tokens.Length >= 4)
        {
            return string.Join('_', tokens[2..^1]);
        }

        return baseName;
    }

    // Extracts the editor-relevant assets from <paramref name="archivePath"/> into
    // <paramref name="outputFolder"/>. The game (and its Blowfish key) is detected automatically from
    // the TelltaleToolKit catalogue, so any catalogued game can be opened without the user picking one.
    // Returns how many files were written; the folder can then be handed to the normal folder loader.
    public static ExtractResult Extract(
        string archivePath,
        string outputFolder,
        Action<string>? onFile = null)
    {
        var (archive, key) = OpenArchiveAuto(archivePath);
        using var _ = archive;

        // Read entries in ascending archive-offset order. Many assets are tiny, so jumping around the
        // file by entry order makes the I/O / decompression read pages backward and miss the cache;
        // walking forward by offset keeps reads sequential and speeds up extraction (suggested by the
        // TelltaleToolKit author).
        var entries = archive.GetAllEntries()
            .Where(e => AssetExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase) ||
                        IsWantedProp(e.Name))
            .OrderBy(e => e.Offset)
            .ToList();

        // Note: the output folder is created lazily (per file below) so that archives without any
        // .d3dmesh/.d3dtx/.skl never leave an empty folder behind in the destination directory.

        var extracted = 0;
        foreach (var entry in entries)
        {
            using Stream? data = archive.OpenResource(entry.NameCrc);
            if (data is null)
            {
                continue;
            }

            var destPath = Path.Combine(outputFolder, SanitizeRelativePath(entry.Name));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            data.Position = 0;
            using (var output = File.Create(destPath))
            {
                data.CopyTo(output);
            }

            extracted++;
            onFile?.Invoke(Path.GetFileName(destPath));
        }

        return new ExtractResult(outputFolder, extracted, entries.Count, GameProfiles.DescribeKey(key));
    }

    // Opens the archive by trying every Blowfish key catalogued by the toolkit until one yields a valid
    // entry table (sane file names). This detects the game automatically. If no key works, the archive
    // uses an encryption/compression the bundled toolkit can't read yet, and a clear error is raised.
    private static (Archive Archive, string Key) OpenArchiveAuto(string archivePath)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext is not (".ttarch" or ".ttarch2"))
        {
            throw new NotSupportedException($"Unsupported archive type: '{ext}'.");
        }

        Exception? lastError = null;
        foreach (var key in GameProfiles.CandidateKeys())
        {
            Archive? archive = null;
            try
            {
                archive = ext == ".ttarch"
                    ? Archive.Load<TTArchive>(archivePath, key)
                    : Archive.Load<TTArchive2>(archivePath, key);

                if (LooksValid(archive))
                {
                    return (archive, key);
                }

                archive.Dispose();
            }
            catch (Exception ex)
            {
                lastError = ex;
                archive?.Dispose();
            }
        }

        throw new InvalidDataException(
            "Could not open this archive with any Telltale game key known to the bundled TelltaleToolKit. "
            + "It likely uses an encryption or compression that isn't supported yet.",
            lastError);
    }

    // A wrong key usually throws while parsing the entry table, but can occasionally produce garbage
    // entries instead. Treat the archive as correctly decrypted only when most sampled entry names look
    // like real file names.
    private static bool LooksValid(Archive archive)
    {
        var sample = archive.GetAllEntries().Take(24).ToList();
        if (sample.Count == 0)
        {
            return false;
        }

        var saneNames = sample.Count(entry => IsSaneName(entry.Name));
        return saneNames * 2 >= sample.Count;
    }

    private static bool IsSaneName(string name)
        => !string.IsNullOrEmpty(name)
           && name.Length <= 260
           && name.Contains('.')
           && name.All(ch => ch is >= ' ' and < (char)127);

    // Turns an archive entry name into a safe relative path under the output folder, stripping any
    // leading separators or drive roots so it can never escape the destination directory.
    private static string SanitizeRelativePath(string entryName)
    {
        var normalized = entryName.Replace('\\', Path.DirectorySeparatorChar)
                                  .Replace('/', Path.DirectorySeparatorChar)
                                  .TrimStart(Path.DirectorySeparatorChar);

        var segments = normalized
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..");

        var safe = Path.Combine(segments.ToArray());
        return string.IsNullOrEmpty(safe) ? Path.GetFileName(entryName) : safe;
    }
}
