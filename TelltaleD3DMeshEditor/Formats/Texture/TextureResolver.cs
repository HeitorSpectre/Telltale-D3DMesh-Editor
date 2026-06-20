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
        var diffuseReferences = BuildDiffuseReferenceStems(mesh);
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

            ApplyCompanionAlpha(set, diffuse, textureFiles, loaded, diffuseReferences);
            ApplyBackToTheFutureDiffuseAlpha(set, diffuse, mesh.Submeshes[i]);
            ApplyBackToTheFutureGlassAlpha(set, mesh.Submeshes[i]);

            if (set.Count > 0)
            {
                result[i] = set;
            }
        }

        return result;
    }

    // For games that keep opacity in a separate companion texture (e.g. The Walking Dead: Season 2 hair
    // uses "<name>Alpha"), merge that companion's mask into the diffuse's alpha channel so transparency
    // renders. Only runs for games configured this way and only when the diffuse has no usable alpha of
    // its own, so The Wolf Among Us (alpha in the diffuse) is untouched.
    private static void ApplyCompanionAlpha(
        MaterialTextureSet set,
        TextureCandidate? diffuse,
        IReadOnlyList<TextureCandidate> textureFiles,
        Dictionary<string, TextureImage> loaded,
        IReadOnlySet<string> diffuseReferences)
    {
        if (!GameConfig.Current.UsesCompanionAlphaTextures || set.Diffuse is null || diffuse is null)
        {
            return;
        }

        // The diffuse already carries its own opacity — leave it alone.
        if (set.Diffuse.AverageAlpha < 0.99f)
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(diffuse.Value.Path);
        string[] wanted = [stem + "Alpha", stem + "_alpha", stem + "_alp", stem + "Opacity"];
        var companionFile = textureFiles
            .Where(candidate => wanted.Contains(Path.GetFileNameWithoutExtension(candidate.Path), StringComparer.OrdinalIgnoreCase))
            .Where(candidate => !IsReferencedAsDiffuse(candidate, diffuseReferences))
            .Cast<TextureCandidate?>()
            .FirstOrDefault();
        if (companionFile is null)
        {
            return;
        }

        var companion = LoadMatched(companionFile, loaded);
        if (companion is null)
        {
            return;
        }

        set.Diffuse = MergeAlpha(set.Diffuse, companion);
    }

    // Returns a copy of <paramref name="diffuse"/> whose alpha comes from <paramref name="mask"/>. The
    // mask is taken from the companion's alpha channel when it varies, otherwise from its luminance (some
    // games store the opacity mask as a greyscale image instead of in the alpha channel).
    private static TextureImage MergeAlpha(TextureImage diffuse, TextureImage mask)
    {
        var useAlphaChannel = mask.AverageAlpha < 0.99f;
        var merged = new int[diffuse.Pixels.Length];
        for (var y = 0; y < diffuse.Height; y++)
        {
            var maskY = diffuse.Height == mask.Height ? y : y * mask.Height / diffuse.Height;
            for (var x = 0; x < diffuse.Width; x++)
            {
                var maskX = diffuse.Width == mask.Width ? x : x * mask.Width / diffuse.Width;
                var maskArgb = mask.Pixels[maskY * mask.Width + maskX];
                var alpha = useAlphaChannel ? (maskArgb >> 24) & 0xFF : (maskArgb >> 16) & 0xFF;
                var rgb = diffuse.Pixels[y * diffuse.Width + x] & 0x00FFFFFF;
                merged[y * diffuse.Width + x] = (alpha << 24) | rgb;
            }
        }

        return new TextureImage(diffuse.Width, diffuse.Height, merged, diffuse.SourcePath);
    }

    private static HashSet<string> BuildDiffuseReferenceStems(MeshData mesh)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.TextureNames.TryGetValue("diffuse", out var diffuse))
            {
                result.Add(NormalizeStem(diffuse));
            }

            if (!string.IsNullOrWhiteSpace(submesh.MaterialName))
            {
                result.Add(NormalizeStem(submesh.MaterialName));
            }
        }

        return result;
    }

    private static bool IsReferencedAsDiffuse(TextureCandidate candidate, IReadOnlySet<string> diffuseReferences)
    {
        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(candidate.Path));
        return diffuseReferences.Contains(stem);
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

        if (IsTftbE3PlaceholderDetail(candidate, texture))
        {
            return null;
        }

        if (IsTftbE3OpaqueDiffuseWithAuxiliaryAlpha(candidate, texture))
        {
            return ForceOpaque(texture);
        }

        return texture;
    }

    private static bool IsTftbE3OpaqueDiffuseWithAuxiliaryAlpha(TextureCandidate candidate, TextureImage texture)
    {
        if (GameConfig.Current.Id != GameId.TalesFromTheBorderlandsE3 ||
            candidate.Role != TextureRole.Diffuse ||
            texture.AverageAlpha >= 0.15f ||
            texture.NonOpaqueAlphaRatio < 0.95f ||
            texture.AlphaMode is not (0 or -1 or null))
        {
            return false;
        }

        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(candidate.Path));
        if (stem.StartsWith("ui_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("fx_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("map_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("color_", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("decal", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("_alp", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("glow", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("eye", StringComparison.OrdinalIgnoreCase) ||
            stem.Contains("lash", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        long visibleRgb = 0;
        foreach (var argb in texture.Pixels)
        {
            var r = (argb >> 16) & 0xFF;
            var g = (argb >> 8) & 0xFF;
            var b = argb & 0xFF;
            if (r + g + b > 48)
            {
                visibleRgb++;
            }
        }

        return visibleRgb > texture.Pixels.Length / 2;
    }

    private static TextureImage ForceOpaque(TextureImage source)
    {
        var pixels = new int[source.Pixels.Length];
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            pixels[i] = unchecked((int)0xFF000000) | (source.Pixels[i] & 0x00FFFFFF);
        }

        return new TextureImage(source.Width, source.Height, pixels, source.SourcePath);
    }

    private static bool IsTftbE3PlaceholderDetail(TextureCandidate candidate, TextureImage texture)
    {
        if (GameConfig.Current.Id != GameId.TalesFromTheBorderlandsE3 ||
            candidate.Role != TextureRole.Detail ||
            texture.Width > 4 ||
            texture.Height > 4 ||
            texture.Pixels.Length == 0)
        {
            return false;
        }

        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(candidate.Path));
        if (!stem.Contains("line", StringComparison.OrdinalIgnoreCase) &&
            !stem.Contains("detail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        long red = 0;
        long green = 0;
        long blue = 0;
        foreach (var argb in texture.Pixels)
        {
            red += (argb >> 16) & 0xFF;
            green += (argb >> 8) & 0xFF;
            blue += argb & 0xFF;
        }

        var count = texture.Pixels.Length;
        return red / count <= 32 &&
               green / count <= 32 &&
               blue / count >= 180;
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
            if (TryCreateSolidColorTexture(path, out texture))
            {
                return true;
            }

            texture = null!;
            return false;
        }
    }

    private static bool TryCreateSolidColorTexture(string path, out TextureImage texture)
    {
        texture = null!;
        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(path));
        if (!stem.StartsWith("color_", StringComparison.OrdinalIgnoreCase) ||
            stem.Length < "color_".Length + 6)
        {
            return false;
        }

        var hex = stem["color_".Length..];
        if (hex.Length > 6)
        {
            hex = hex[..6];
        }

        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }

        texture = new TextureImage(1, 1, [unchecked((int)(0xFF000000u | (uint)rgb))], path);
        return true;
    }

    private static void ApplyBackToTheFutureDiffuseAlpha(
        MaterialTextureSet set,
        TextureCandidate? diffuse,
        SubmeshData submesh)
    {
        if (!GameConfig.Current.IsBackToTheFuture || set.Diffuse is null || set.Diffuse.AverageAlpha < 0.99f)
        {
            return;
        }

        var names = submesh.TextureNames.Values
            .Append(submesh.MaterialName ?? "")
            .Append(submesh.Name)
            .Append(diffuse is null ? "" : Path.GetFileNameWithoutExtension(diffuse.Value.Path))
            .Select(NormalizeStem);
        if (!names.Any(IsAlphaMaskDiffuseName))
        {
            return;
        }

        set.Diffuse = AlphaFromIntensity(set.Diffuse);
    }

    private static TextureImage AlphaFromIntensity(TextureImage source)
    {
        var pixels = new int[source.Pixels.Length];
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            var argb = source.Pixels[i];
            var r = (argb >> 16) & 0xFF;
            var g = (argb >> 8) & 0xFF;
            var b = argb & 0xFF;
            var intensity = Math.Max(r, Math.Max(g, b));
            var alpha = Math.Clamp((int)MathF.Round((intensity - 8) * 4.2f), 0, 255);
            pixels[i] = (argb & 0x00FFFFFF) | (alpha << 24);
        }

        return new TextureImage(source.Width, source.Height, pixels, source.SourcePath);
    }

    private static TextureImage WithUniformAlpha(TextureImage source, float alpha)
    {
        var byteAlpha = Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        var pixels = new int[source.Pixels.Length];
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            var originalAlpha = (source.Pixels[i] >> 24) & 0xFF;
            var finalAlpha = Math.Min(originalAlpha, byteAlpha);
            pixels[i] = (source.Pixels[i] & 0x00FFFFFF) | (finalAlpha << 24);
        }

        return new TextureImage(source.Width, source.Height, pixels, source.SourcePath);
    }

    private static bool IsAlphaMaskDiffuseName(string name)
    {
        return name.EndsWith("_alpha", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_alp", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_alpha_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_alp_", StringComparison.OrdinalIgnoreCase);
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
                if (role == TextureRole.Diffuse &&
                    exact.Role == TextureRole.Normal &&
                    TryFindDiffuseSiblingForNormal(exact, allFiles, out var diffuseSibling))
                {
                    return diffuseSibling;
                }

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

    private static bool TryFindDiffuseSiblingForNormal(
        TextureCandidate normal,
        IReadOnlyList<TextureCandidate> files,
        out TextureCandidate diffuse)
    {
        diffuse = default;
        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(normal.Path));
        string[] suffixes = ["_nm", "_nrm", "_normal"];
        foreach (var suffix in suffixes)
        {
            if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseStem = stem[..^suffix.Length];
            foreach (var file in files)
            {
                if (file.Role != TextureRole.Diffuse)
                {
                    continue;
                }

                var candidateStem = NormalizeStem(Path.GetFileNameWithoutExtension(file.Path));
                if (candidateStem.Equals(baseStem, StringComparison.OrdinalIgnoreCase))
                {
                    diffuse = file;
                    return true;
                }
            }
        }

        return false;
    }

    private static void ApplyBackToTheFutureGlassAlpha(MaterialTextureSet set, SubmeshData submesh)
    {
        if (!GameConfig.Current.IsBackToTheFuture || set.Diffuse is null)
        {
            return;
        }

        var names = submesh.TextureNames.Values
            .Append(submesh.MaterialName ?? "")
            .Append(submesh.Name)
            .Select(NormalizeStem)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        var hasGlassName = names.Any(IsGlassLikeName);
        var hasSyntheticGlassColor = names.Any(IsBackToTheFutureSyntheticGlassColor);

        if (hasGlassName)
        {
            set.Diffuse = WithGlassPixelAlpha(set.Diffuse, 0.68f);
        }
        else if (hasSyntheticGlassColor)
        {
            set.Diffuse = WithUniformAlpha(set.Diffuse, 0.45f);
        }
    }

    private static TextureImage WithGlassPixelAlpha(TextureImage source, float glassAlpha)
    {
        var byteAlpha = Math.Clamp((int)MathF.Round(glassAlpha * 255f), 0, 255);
        var pixels = new int[source.Pixels.Length];
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            var argb = source.Pixels[i];
            var originalAlpha = (argb >> 24) & 0xFF;
            var finalAlpha = IsLikelyGlassPixel(argb)
                ? Math.Min(originalAlpha, byteAlpha)
                : originalAlpha;
            pixels[i] = (argb & 0x00FFFFFF) | (finalAlpha << 24);
        }

        return new TextureImage(source.Width, source.Height, pixels, source.SourcePath);
    }

    private static bool IsLikelyGlassPixel(int argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max < 42)
        {
            return false;
        }

        var coolBlueGlass = b >= r + 8 && g >= r - 5;
        var paleNeutralGlass = max > 92 && max - min < 72 && b >= r - 14 && g >= r - 18;
        if (coolBlueGlass || paleNeutralGlass)
        {
            return true;
        }

        var warmWoodOrStone = r > b + 12 && g >= b - 6;
        return !warmWoodOrStone && b >= r - 6 && g >= r - 10 && max > 55;
    }

    private static bool IsGlassLikeName(string name)
    {
        if (name.Contains("glass", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Contains("window", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("windowframe", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("windowtrim", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("window_frame", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("window_trim", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackToTheFutureSyntheticGlassColor(string name)
    {
        // BTTF apartment cabinet glass is stored as a synthetic flat color, not as a named
        // glass/window texture. Keep this exact enough to avoid making generic grey props transparent.
        return name.Equals("color_5d5d5d", StringComparison.OrdinalIgnoreCase);
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
                if (TextureFileMatchesHash(file, wantedHash))
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

    private static bool TextureFileMatchesHash(TextureCandidate file, ulong wantedHash)
    {
        if (Crc64Ecma.Compute(Path.GetFileName(file.Path)) == wantedHash)
        {
            return true;
        }

        if (GameConfig.Current.Id != GameId.TalesFromTheBorderlandsE3)
        {
            return false;
        }

        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(file.Path));
        return Crc64Ecma.Compute(stem) == wantedHash;
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
        if (GameConfig.Current.Id == GameId.TalesFromTheBorderlandsE3)
        {
            return 0;
        }

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

            if (GameConfig.Current.TreatAdvObj000TexturesAsBake &&
                lower.EndsWith("_000") &&
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
