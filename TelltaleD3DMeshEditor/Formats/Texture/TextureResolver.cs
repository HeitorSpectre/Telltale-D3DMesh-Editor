using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Formats.Texture;

// Matches on-disk textures (.d3dtx/.dds/.png, next to the mesh or in the input tree) to each
// submesh, classifying their role (diffuse / detail / normal / bake / shadow) from file names
// and internal mesh references. Result: one MaterialTextureSet per submesh.
public static class TextureResolver
{
    private static readonly string[] SupportedExtensions = [".d3dtx", ".png", ".dds"];

    public static Dictionary<int, MaterialTextureSet> ResolveForMesh(string inputRoot, string meshPath, MeshData mesh)
    {
        var textureFiles = EnumerateTextureFiles(inputRoot, meshPath)
            .Select(TextureCandidate.FromPath)
            .OrderBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var diffuseFiles = textureFiles
            .Where(candidate => candidate.Role == TextureRole.Diffuse)
            .ToList();
        var detailFiles = textureFiles
            .Where(candidate => candidate.Role == TextureRole.Detail)
            .ToList();
        var normalFiles = textureFiles
            .Where(candidate => candidate.Role == TextureRole.Normal)
            .ToList();
        var bakeFiles = textureFiles
            .Where(candidate => candidate.Role == TextureRole.Bake)
            .ToList();
        var shadowFiles = textureFiles
            .Where(candidate => candidate.Role == TextureRole.Shadow)
            .ToList();
        var result = new Dictionary<int, MaterialTextureSet>();
        if (textureFiles.Count == 0)
        {
            return result;
        }

        var loaded = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mesh.Submeshes.Count; i++)
        {
            var diffuse = FindBestTexture(textureFiles, diffuseFiles, mesh.Submeshes[i], TextureRole.Diffuse)
                ?? (diffuseFiles.Count == 1 ? diffuseFiles[0] : null);
            var set = new MaterialTextureSet
            {
                Diffuse = LoadMatched(diffuse, loaded),
                Detail = LoadMatched(FindBestTexture(textureFiles, detailFiles, mesh.Submeshes[i], TextureRole.Detail), loaded),
                Normal = LoadMatched(FindBestTexture(textureFiles, normalFiles, mesh.Submeshes[i], TextureRole.Normal), loaded),
                Bake = LoadMatched(FindBestTexture(textureFiles, bakeFiles, mesh.Submeshes[i], TextureRole.Bake), loaded),
                Shadow = LoadMatched(FindBestTexture(textureFiles, shadowFiles, mesh.Submeshes[i], TextureRole.Shadow), loaded),
            };

            if (set.Count > 0)
            {
                result[i] = set;
            }
        }

        return result;
    }

    private static TextureImage? LoadMatched(TextureCandidate? match, Dictionary<string, TextureImage> loaded)
    {
        if (match is null)
        {
            return null;
        }

        var candidate = match.Value;
        if (!loaded.TryGetValue(candidate.Path, out var texture))
        {
            if (!TryLoad(candidate.Path, out texture))
            {
                return null;
            }
            loaded[candidate.Path] = texture;
        }

        return texture;
    }

    private static bool TryLoad(string path, out TextureImage texture)
    {
        try
        {
            texture = TextureLoader.Load(path);
            return true;
        }
        catch
        {
            texture = null!;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateTextureFiles(string inputRoot, string meshPath)
    {
        var meshDir = Path.GetDirectoryName(meshPath);
        if (!string.IsNullOrEmpty(meshDir) && Directory.Exists(meshDir))
        {
            foreach (var file in Directory.EnumerateFiles(meshDir, "*.*", SearchOption.TopDirectoryOnly).Where(IsSupported))
            {
                yield return file;
            }
        }

        if (Directory.Exists(inputRoot))
        {
            foreach (var file in Directory.EnumerateFiles(inputRoot, "*.*", SearchOption.AllDirectories).Where(IsSupported))
            {
                if (!string.Equals(Path.GetDirectoryName(file), meshDir, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static TextureCandidate? FindBestTexture(
        IReadOnlyList<TextureCandidate> allFiles,
        IReadOnlyList<TextureCandidate> roleFiles,
        SubmeshData submesh,
        TextureRole role)
    {
        foreach (var slot in SlotsForRole(role))
        {
            if (submesh.TextureNames.TryGetValue(slot, out var indexedName) &&
                TryResolveTextureIndex(indexedName, roleFiles, out var indexed))
            {
                return indexed;
            }
        }

        foreach (var slot in SlotsForRole(role))
        {
            if (submesh.TextureNames.TryGetValue(slot, out var name) &&
                TryFindExactTexture(name, allFiles, out var exact))
            {
                return exact;
            }
        }

        var wanted = new List<string>();
        foreach (var slot in SlotsForRole(role))
        {
            if (submesh.TextureNames.TryGetValue(slot, out var name))
            {
                wanted.Add(name);
            }
        }
        if (!string.IsNullOrWhiteSpace(submesh.MaterialName))
        {
            wanted.Add(submesh.MaterialName);
        }
        wanted.Add(submesh.Name);

        var filteredWanted = wanted
            .Select(NormalizeStem)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("texture", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var bestScore = 0;
        TextureCandidate? bestFile = null;
        foreach (var file in roleFiles)
        {
            var stem = NormalizeStem(Path.GetFileNameWithoutExtension(file.Path));
            foreach (var name in filteredWanted)
            {
                var score = Score(stem, name);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFile = file;
                }
            }
        }

        if (bestFile is not null)
        {
            return bestFile;
        }

        return null;
    }

    private static bool TryFindExactTexture(string value, IReadOnlyList<TextureCandidate> files, out TextureCandidate candidate)
    {
        candidate = default;
        var wanted = NormalizeStem(value);
        if (string.IsNullOrWhiteSpace(wanted) || wanted.StartsWith("texture_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryParseHashReference(wanted, out var wantedHash))
        {
            foreach (var file in files)
            {
                var fileHash = Crc64Ecma.Compute(Path.GetFileName(file.Path));
                if (fileHash == wantedHash)
                {
                    candidate = file;
                    return true;
                }
            }
        }

        foreach (var file in files)
        {
            var stem = NormalizeStem(Path.GetFileNameWithoutExtension(file.Path));
            if (stem.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                candidate = file;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHashReference(string value, out ulong hash)
    {
        hash = 0;
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ulong.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out hash);
    }

    private static string[] SlotsForRole(TextureRole role)
    {
        return role switch
        {
            TextureRole.Diffuse => ["diffuse"],
            TextureRole.Detail => ["detail_diffuse", "tex7", "tex8"],
            TextureRole.Normal => ["bump", "detail_bump"],
            TextureRole.Bake => ["bake"],
            TextureRole.Shadow => ["shadow"],
            _ => []
        };
    }

    private static bool TryResolveTextureIndex(string value, IReadOnlyList<TextureCandidate> files, out TextureCandidate candidate)
    {
        candidate = default;
        var name = NormalizeStem(value);
        if (!name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(name["texture_".Length..], out var textureIndex) ||
            textureIndex <= 0 ||
            textureIndex > files.Count)
        {
            return false;
        }

        candidate = files[textureIndex - 1];
        return true;
    }

    private static bool IsSupported(string path)
    {
        return SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static int Score(string fileStem, string wanted)
    {
        if (fileStem.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return 100;
        if (fileStem.EndsWith("_" + wanted, StringComparison.OrdinalIgnoreCase)) return 90;
        if (fileStem.Contains(wanted, StringComparison.OrdinalIgnoreCase)) return 75;
        if (wanted.Contains(fileStem, StringComparison.OrdinalIgnoreCase)) return 60;
        return 0;
    }

    private static string NormalizeStem(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value);
        while (stem.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }

        return stem;
    }

    private enum TextureRole
    {
        Diffuse,
        Detail,
        Normal,
        Bake,
        Shadow,
        Other
    }

    private readonly record struct TextureCandidate(string Path, TextureRole Role)
    {
        public static TextureCandidate FromPath(string path)
        {
            return new TextureCandidate(path, Classify(path));
        }

        private static TextureRole Classify(string path)
        {
            var stem = NormalizeStem(System.IO.Path.GetFileNameWithoutExtension(path));
            var lower = stem.ToLowerInvariant();
            if (lower.Contains("_shadow"))
            {
                return TextureRole.Shadow;
            }

            if (lower.EndsWith("_000") &&
                (lower.StartsWith("obj_", StringComparison.OrdinalIgnoreCase) ||
                 lower.StartsWith("adv_", StringComparison.OrdinalIgnoreCase)))
            {
                return TextureRole.Bake;
            }

            if (lower.EndsWith("_nm") ||
                lower.EndsWith("_nrm") ||
                lower.EndsWith("_normal") ||
                lower.Contains("_normal_") ||
                lower.Contains("_nm_"))
            {
                return TextureRole.Normal;
            }

            if (lower.EndsWith("_lines") ||
                lower.EndsWith("_line") ||
                lower.Contains("_lines_") ||
                lower.Contains("_line_") ||
                lower.Contains("inklines") ||
                lower.Contains("ink_lines") ||
                lower.EndsWith("_detail") ||
                lower.Contains("_detail_"))
            {
                return TextureRole.Detail;
            }

            if (lower.EndsWith("_spec") ||
                lower.EndsWith("_mask") ||
                lower.EndsWith("_masks") ||
                lower.EndsWith("_ao") ||
                lower.EndsWith("_rough") ||
                lower.EndsWith("_metal") ||
                lower.EndsWith("_bump"))
            {
                return TextureRole.Other;
            }

            return TextureRole.Diffuse;
        }
    }
}
