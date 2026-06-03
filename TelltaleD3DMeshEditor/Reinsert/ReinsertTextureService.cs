using System.Drawing;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Texture;
using TelltaleD3DMeshEditor.Core;
using System.Security.Cryptography;

namespace TelltaleD3DMeshEditor.Reinsert;

public static class ReinsertTextureService
{
    private const int MaxGeneratedTextureNameLength = 96;

    public static ReinsertedTextures WriteAllReferencedTextures(
        GltfModel model,
        string templateMeshPath,
        string outputMeshPath)
    {
        var templateTexturePath = FindTemplateTexture(templateMeshPath);
        if (templateTexturePath is null)
        {
            return new ReinsertedTextures(
                Enumerable.Range(0, model.Primitives.Count)
                    .Select(_ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    .ToList(),
                []);
        }

        var fallbackTemplateBytes = File.ReadAllBytes(templateTexturePath);
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputMeshPath)) ?? ".";
        Directory.CreateDirectory(outputFolder);

        var textureNamespace = BuildTextureNamespace(outputMeshPath);
        var writtenByImage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = CreateUsedTextureNameSet(outputFolder);
        var writtenNames = new List<string>();
        var primitiveSlots = new List<IReadOnlyDictionary<string, string>>(model.Primitives.Count);
        var preferredOriginalNamesByImageKey = BuildPreferredOriginalNamesByImageKey(model, templateMeshPath);

        foreach (var primitive in model.Primitives)
        {
            var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var diffuseImageKeys = primitive.TextureSlots
                .Where(pair => pair.Key.Equals("diffuse", StringComparison.OrdinalIgnoreCase))
                .Select(pair => ImageKey(pair.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (reference, image) in primitive.ReferencedTextures)
            {
                if (IsGeneratedGltfHelperTexture(image.Name))
                {
                    continue;
                }

                var imageKey = ImageKey(image);
                if (!writtenByImage.TryGetValue(imageKey, out var textureName))
                {
                    var preferredNames = preferredOriginalNamesByImageKey.TryGetValue(imageKey, out var foundPreferredNames) ? foundPreferredNames : [];
                    var sourceTexturePath = FindSourceTextureTemplate(templateMeshPath, preferredNames)
                        ?? FindSourceTextureTemplate(templateMeshPath, image.Name);
                    var sourceImageMatches = sourceTexturePath is not null && ImageMatchesSourceTexture(image, sourceTexturePath);
                    var preserveDiffuseName = reference.Equals("diffuse", StringComparison.OrdinalIgnoreCase) ||
                                              diffuseImageKeys.Contains(imageKey);
                    textureName = ChooseOutputTextureName(
                        textureNamespace,
                        image.Name,
                        sourceTexturePath,
                        usedNames,
                        preferredNames,
                        preserveOriginalName: preserveDiffuseName || sourceImageMatches);
                    var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
                    WriteTexturePreservingTemplate(fallbackTemplateBytes, image, sourceTexturePath, outputTexturePath, sourceImageMatches);
                    writtenByImage[imageKey] = textureName;
                    writtenNames.Add(textureName);
                }
            }

            foreach (var (slot, image) in primitive.TextureSlots)
            {
                var imageKey = ImageKey(image);
                if (writtenByImage.TryGetValue(imageKey, out var textureName))
                {
                    slots[slot] = textureName;
                }
            }

            primitiveSlots.Add(slots);
        }

        return new ReinsertedTextures(primitiveSlots, writtenNames);
    }

    public static IReadOnlyList<string?> WriteDiffuseTextures(
        GltfModel model,
        string templateMeshPath,
        string outputMeshPath)
    {
        var templateTexturePath = FindTemplateTexture(templateMeshPath);
        if (templateTexturePath is null)
        {
            return Enumerable.Repeat<string?>(null, model.Primitives.Count).ToList();
        }

        var fallbackTemplateBytes = File.ReadAllBytes(templateTexturePath);
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputMeshPath)) ?? ".";
        Directory.CreateDirectory(outputFolder);

        var textureNamespace = BuildTextureNamespace(outputMeshPath);
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = CreateUsedTextureNameSet(outputFolder);
        var result = new List<string?>(model.Primitives.Count);

        foreach (var primitive in model.Primitives)
        {
            if (primitive.BaseColor is null)
            {
                result.Add(null);
                continue;
            }

            var imageKey = ImageKey(primitive.BaseColor);
            if (!written.TryGetValue(imageKey, out var textureName))
            {
                var sourceTexturePath = FindSourceTextureTemplate(templateMeshPath, primitive.BaseColor.Name);
                var sourceImageMatches = sourceTexturePath is not null && ImageMatchesSourceTexture(primitive.BaseColor, sourceTexturePath);
                textureName = ChooseOutputTextureName(textureNamespace, primitive.BaseColor.Name, sourceTexturePath, usedNames, [], preserveOriginalName: true);
                var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
                WriteTexturePreservingTemplate(fallbackTemplateBytes, primitive.BaseColor, sourceTexturePath, outputTexturePath, sourceImageMatches);
                written[imageKey] = textureName;
            }

            result.Add(textureName);
        }

        return result;
    }

    private static string? FindTemplateTexture(string templateMeshPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        var meshStem = Path.GetFileNameWithoutExtension(templateMeshPath);
        var exact = Path.Combine(folder, meshStem + ".d3dtx");
        if (File.Exists(exact))
        {
            return exact;
        }

        var byMeshHash = FindDiffuseTextureByMeshHash(folder, templateMeshPath);
        if (byMeshHash is not null)
        {
            return byMeshHash;
        }

        try
        {
            var mesh = D3DMeshParser.Parse(File.ReadAllBytes(templateMeshPath));
            var resolved = TextureResolver.ResolveForMesh(folder, templateMeshPath, mesh);
            var diffuse = resolved.Values.FirstOrDefault(set => set.Diffuse is not null)?.Diffuse?.SourcePath;
            if (!string.IsNullOrWhiteSpace(diffuse) && File.Exists(diffuse))
            {
                return diffuse;
            }
        }
        catch
        {
            // Fallback below keeps reimport usable even when the source mesh cannot be preview-parsed.
        }

        return Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("_lines", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("_shadow", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("_detail", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("_nm", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileNameWithoutExtension(path).StartsWith("color_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault()
            ?? Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }

    private static string? FindDiffuseTextureByMeshHash(string folder, string templateMeshPath)
    {
        try
        {
            var layout = D3DMeshLayout.Build(File.ReadAllBytes(templateMeshPath));
            var diffuseHash = layout.TextureGroups
                .FirstOrDefault(group => group.Index == 0)?
                .Entries
                .FirstOrDefault();
            if (diffuseHash is null)
            {
                return null;
            }

            var expected = ((ulong)diffuseHash.HashHigh << 32) | diffuseHash.HashLow;
            var swapped = ((ulong)diffuseHash.HashLow << 32) | diffuseHash.HashHigh;
            foreach (var candidate in Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly))
            {
                var fileNameHash = Crc64Ecma.Compute(Path.GetFileName(candidate));
                if (fileNameHash == expected || fileNameHash == swapped)
                {
                    return candidate;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void WriteTexturePreservingTemplate(
        byte[] fallbackTemplateBytes,
        GltfImage image,
        string? sourceTexturePath,
        string outputTexturePath,
        bool sourceImageMatches)
    {
        if (sourceTexturePath is not null && sourceImageMatches)
        {
            if (Path.GetFullPath(sourceTexturePath).Equals(Path.GetFullPath(outputTexturePath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(sourceTexturePath), outputTexturePath);
            return;
        }

        var templateBytes = sourceTexturePath is not null
            ? File.ReadAllBytes(sourceTexturePath)
            : fallbackTemplateBytes;
        D3dtxWriter.WriteFromImageBytes(templateBytes, image, outputTexturePath);
    }

    private static string ChooseOutputTextureName(
        string textureNamespace,
        string rawName,
        string? sourceTexturePath,
        HashSet<string> usedNames,
        IReadOnlyList<string> preferredOriginalNames,
        bool preserveOriginalName)
    {
        if (preserveOriginalName)
        {
            foreach (var preferred in preferredOriginalNames)
            {
                var preferredName = SanitizeName(StripKnownTextureExtension(Path.GetFileName(preferred)));
                if (!string.IsNullOrWhiteSpace(preferredName))
                {
                    usedNames.Add(preferredName);
                    return preferredName;
                }
            }

            if (sourceTexturePath is not null)
            {
                var originalName = StripKnownTextureExtension(Path.GetFileName(sourceTexturePath));
                originalName = SanitizeName(originalName);
                if (!string.IsNullOrWhiteSpace(originalName))
                {
                    usedNames.Add(originalName);
                    return originalName;
                }
            }

            var rawOriginalName = SanitizeName(StripKnownTextureExtension(Path.GetFileName(rawName)));
            if (!string.IsNullOrWhiteSpace(rawOriginalName))
            {
                usedNames.Add(rawOriginalName);
                return rawOriginalName;
            }
        }

        return MakeUniqueTextureName(textureNamespace, rawName, usedNames);
    }

    private static Dictionary<string, List<string>> BuildPreferredOriginalNamesByImageKey(GltfModel model, string templateMeshPath)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        MeshData? templateMesh = null;
        try
        {
            templateMesh = D3DMeshParser.Parse(File.ReadAllBytes(templateMeshPath));
        }
        catch
        {
            templateMesh = null;
        }

        for (var primitiveIndex = 0; primitiveIndex < model.Primitives.Count; primitiveIndex++)
        {
            var primitive = model.Primitives[primitiveIndex];
            foreach (var (slot, image) in primitive.TextureSlots)
            {
                if (IsGeneratedGltfHelperTexture(image.Name))
                {
                    continue;
                }

                var imageKey = ImageKey(image);
                if (!result.TryGetValue(imageKey, out var names))
                {
                    names = [];
                    result[imageKey] = names;
                }

                if (templateMesh is not null &&
                    primitiveIndex < templateMesh.Submeshes.Count &&
                    templateMesh.Submeshes[primitiveIndex].TextureNames.TryGetValue(slot, out var templateTextureName) &&
                    IsUsableOriginalTextureName(templateTextureName) &&
                    !names.Contains(templateTextureName, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(templateTextureName);
                }

            }
        }

        return result;
    }

    private static bool IsUsableOriginalTextureName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               !name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }

    private static string ImageKey(GltfImage image)
        => image.Name + ":" + image.Data.Length + ":" + Convert.ToHexString(SHA256.HashData(image.Data));

    private static string? FindSourceTextureTemplate(string templateMeshPath, string imageName)
    {
        if (IsGeneratedGltfHelperTexture(imageName))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        var stem = StripKnownTextureExtension(Path.GetFileName(imageName));
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var exact = Path.Combine(folder, stem + ".d3dtx");
        if (File.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                stem,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindSourceTextureTemplate(string templateMeshPath, IReadOnlyList<string> imageNames)
    {
        foreach (var imageName in imageNames)
        {
            var path = FindSourceTextureTemplate(templateMeshPath, imageName);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static bool ImageMatchesSourceTexture(GltfImage image, string sourceTexturePath)
    {
        try
        {
            var source = TextureLoader.Load(sourceTexturePath);
            var pixels = DecodeImagePixels(image.Data, out var width, out var height);
            if (width != source.Width ||
                height != source.Height ||
                pixels.Length != source.Pixels.Length)
            {
                return false;
            }

            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] != source.Pixels[i])
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int[] DecodeImagePixels(byte[] data, out int width, out int height)
    {
        using var stream = new MemoryStream(data);
        using var source = new Bitmap(stream);
        width = source.Width;
        height = source.Height;
        var pixels = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = source.GetPixel(x, y).ToArgb();
            }
        }

        return pixels;
    }

    private static bool IsGeneratedGltfHelperTexture(string name)
    {
        return name.Contains("__tt_gltf_normal", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("__tt_lines_overlay", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTextureNamespace(string outputMeshPath)
    {
        var stem = SanitizeName(Path.GetFileNameWithoutExtension(outputMeshPath));
        return string.IsNullOrWhiteSpace(stem) ? "texture" : ShortenTextureName(stem + "_tex");
    }

    private static HashSet<string> CreateUsedTextureNameSet(string outputFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outputFolder))
        {
            return names;
        }

        foreach (var path in Directory.EnumerateFiles(outputFolder, "*.d3dtx", SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                names.Add(stem);
            }
        }

        return names;
    }

    private static string MakeUniqueTextureName(string textureNamespace, string rawName, HashSet<string> usedNames)
    {
        var baseName = StripKnownTextureExtension(Path.GetFileName(rawName));

        baseName = SanitizeName(string.IsNullOrWhiteSpace(baseName) ? "reimport_texture" : baseName);
        if (!baseName.StartsWith(textureNamespace + "_", StringComparison.OrdinalIgnoreCase))
        {
            baseName = textureNamespace + "_" + baseName;
        }

        baseName = ShortenTextureName(baseName);
        var name = baseName;
        var suffix = 2;
        while (!usedNames.Add(name))
        {
            name = ShortenTextureName($"{baseName}_{suffix++}");
        }

        return name;
    }

    private static string ShortenTextureName(string name)
    {
        if (name.Length <= MaxGeneratedTextureNameLength)
        {
            return name;
        }

        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        var keep = Math.Max(8, MaxGeneratedTextureNameLength - hash.Length - 1);
        return name[..keep] + "_" + hash;
    }

    private static string StripKnownTextureExtension(string name)
    {
        foreach (var ext in new[] { ".d3dtx", ".dds", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }

        return name;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) || ch > 0x7F ? '_' : ch).ToArray();
        return new string(chars).Trim('_');
    }
}

public sealed record ReinsertedTextures(
    IReadOnlyList<IReadOnlyDictionary<string, string>> PrimitiveSlots,
    IReadOnlyList<string> WrittenNames);
