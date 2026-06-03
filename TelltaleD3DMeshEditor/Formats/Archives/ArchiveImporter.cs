using TelltaleToolKit.IO.Archives;
using TelltaleToolKit.IO.Archives.Formats;

namespace TelltaleD3DMeshEditor.Formats.Archives;

// Opens Telltale game containers (.ttarch / .ttarch2) with the TelltaleToolKit submodule and
// extracts only the asset types the editor understands (.d3dmesh, .d3dtx, .skl) into a working
// folder, so the user does not have to unpack the archive manually with an external tool.
// Everything else (preview, edit, reimport) is still handled by this project's own parsers.
public static class ArchiveImporter
{
    // Asset extensions the editor can load from the extracted folder.
    private static readonly string[] AssetExtensions = [".d3dmesh", ".d3dtx", ".skl"];

    // Archive extensions we can open. Used to build the open-file dialog filter.
    public static readonly string[] ArchiveExtensions = [".ttarch", ".ttarch2"];

    // Known Blowfish keys per game. The only supported game today is The Wolf Among Us (key "Fables").
    // Kept here so adding more games later is a one-line change.
    public const string WolfAmongUsKey = "Fables";

    public sealed record ExtractResult(string OutputFolder, int ExtractedCount, int TotalEntries);

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
    // <paramref name="outputFolder"/>. Returns how many files were written. The folder can then be
    // handed straight to the normal folder-loading path.
    public static ExtractResult Extract(
        string archivePath,
        string outputFolder,
        string blowfishKey,
        Action<string>? onFile = null)
    {
        using Archive archive = OpenArchive(archivePath, blowfishKey);

        var entries = archive.GetAllEntries()
            .Where(e => AssetExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
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

        return new ExtractResult(outputFolder, extracted, entries.Count);
    }

    private static Archive OpenArchive(string archivePath, string blowfishKey)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        return ext switch
        {
            ".ttarch2" => Archive.Load<TTArchive2>(archivePath, blowfishKey),
            ".ttarch" => Archive.Load<TTArchive>(archivePath, blowfishKey),
            _ => throw new NotSupportedException($"Unsupported archive type: '{ext}'.")
        };
    }

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
