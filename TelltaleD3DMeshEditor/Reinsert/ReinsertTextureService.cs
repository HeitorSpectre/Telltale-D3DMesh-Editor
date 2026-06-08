using System.Drawing;
using System.Drawing.Imaging;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Texture;
using TelltaleD3DMeshEditor.Core;
using System.Security.Cryptography;

namespace TelltaleD3DMeshEditor.Reinsert;

public static class ReinsertTextureService
{
    private const int MaxGeneratedTextureNameLength = 96;
    private static readonly HashSet<string> TextureSlotsV13 = new(StringComparer.OrdinalIgnoreCase)
    {
        "diffuse", "bake", "bump", "environment", "detail_diffuse", "detail_bump",
        "specular", "tex8", "gradient", "tex10", "shadow"
    };
    private static readonly HashSet<string> SharedOriginalReplacementTextureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "map_1px_alpha",
        "color_000",
    };

    public static ReinsertedTextures WriteAllReferencedTextures(
        GltfModel model,
        string templateMeshPath,
        string outputMeshPath,
        GameConfig? gameConfig = null,
        ReinsertTextureOptions? options = null)
    {
        options ??= ReinsertTextureOptions.Default;
        var nameMode = ResolveNameMode(gameConfig, options.NameMode);
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
        var existingNames = CreateUsedTextureNameSet(outputFolder);
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writtenNames = new List<string>();
        var primitiveSlots = new List<IReadOnlyDictionary<string, string>>(model.Primitives.Count);
        var useOriginalNameTickets = ShouldUseOriginalNameTickets(model, options, nameMode);
        var preferredOriginalNamesByImageKey = nameMode == ResolvedTextureNameMode.PreferTemplateNames || useOriginalNameTickets
            ? BuildPreferredOriginalNamesByImageKey(model, templateMeshPath)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var originalTextureNamePool = useOriginalNameTickets
            ? BuildOriginalTextureNamePool(templateMeshPath)
            : OriginalTextureNamePool.Empty;
        var semanticTemplateNames = nameMode == ResolvedTextureNameMode.SemanticTemplateNames
            ? BuildSemanticTemplateNames(templateMeshPath, gameConfig)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                var normalizedReference = NormalizeTextureSlotName(reference);
                if (!IsSupportedTextureSlot(normalizedReference) || !options.IncludesSlot(normalizedReference))
                {
                    continue;
                }

                var imageKey = ImageKey(image);
                if (!writtenByImage.TryGetValue(imageKey, out var textureName))
                {
                    var preferredNames = preferredOriginalNamesByImageKey.TryGetValue(imageKey, out var foundPreferredNames) ? foundPreferredNames : [];
                    var originalNameCandidates = useOriginalNameTickets
                        ? BuildOriginalNameCandidates(normalizedReference, preferredNames, originalTextureNamePool)
                        : preferredNames;
                    var semanticTemplateName = ResolveSemanticTemplateName(primitive, image.Name, normalizedReference, semanticTemplateNames, gameConfig);
                    if (ShouldSkipUnmappedSecondaryTexture(gameConfig, nameMode, normalizedReference, semanticTemplateName))
                    {
                        continue;
                    }

                    var sourceTexturePath = FindSourceTextureTemplateForMode(templateMeshPath, image.Name, originalNameCandidates, semanticTemplateName, nameMode);
                    var sourceImageMatches = sourceTexturePath is not null && ImageMatchesSourceTexture(image, sourceTexturePath);
                    var preserveDiffuseName = reference.Equals("diffuse", StringComparison.OrdinalIgnoreCase) ||
                                              diffuseImageKeys.Contains(imageKey);
                    textureName = ChooseOutputTextureName(
                        textureNamespace,
                        image.Name,
                        sourceTexturePath,
                        existingNames,
                        reservedNames,
                        originalNameCandidates,
                        semanticTemplateName,
                        nameMode,
                        useOriginalNameTickets,
                        preserveTemplateName: preserveDiffuseName || sourceImageMatches);
                    if (FindSourceTextureTemplate(templateMeshPath, textureName) is { } chosenSourceTexturePath)
                    {
                        sourceTexturePath = chosenSourceTexturePath;
                        sourceImageMatches = ImageMatchesSourceTexture(image, sourceTexturePath);
                    }

                    var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
                    if (ShouldInvertCharacterLineAlpha(gameConfig, primitive, normalizedReference, image.Name))
                    {
                        var templateBytes = sourceTexturePath is not null
                            ? File.ReadAllBytes(sourceTexturePath)
                            : fallbackTemplateBytes;
                        D3dtxWriter.WriteFromImageBytes(templateBytes, InvertImageAlpha(image), outputTexturePath);
                    }
                    else
                    {
                        WriteTexturePreservingTemplate(fallbackTemplateBytes, image, sourceTexturePath, outputTexturePath, sourceImageMatches);
                    }
                    writtenByImage[imageKey] = textureName;
                    writtenNames.Add(textureName);
                }
            }

            foreach (var (slot, image) in primitive.TextureSlots)
            {
                var normalizedSlot = NormalizeTextureSlotName(slot);
                if (!IsSupportedTextureSlot(normalizedSlot) || !options.IncludesSlot(normalizedSlot))
                {
                    continue;
                }

                var semanticSlotName = ResolveSemanticTemplateName(primitive, image.Name, normalizedSlot, semanticTemplateNames, gameConfig);
                if (ShouldSkipUnmappedSecondaryTexture(gameConfig, nameMode, normalizedSlot, semanticSlotName))
                {
                    continue;
                }

                if (TryWriteHandLineInvertedAlphaTexture(
                    gameConfig,
                    primitive,
                    image,
                    normalizedSlot,
                    templateMeshPath,
                    fallbackTemplateBytes,
                    outputFolder,
                    writtenNames,
                    semanticTemplateNames,
                    out var handLineTextureName))
                {
                    slots[normalizedSlot] = handLineTextureName;
                    continue;
                }

                if (TryWriteSplitBodyLineAlphaTexture(
                    gameConfig,
                    primitive,
                    image,
                    normalizedSlot,
                    templateMeshPath,
                    fallbackTemplateBytes,
                    textureNamespace,
                    outputFolder,
                    existingNames,
                    reservedNames,
                    writtenNames,
                    semanticTemplateNames,
                    out var splitBodyLineTextureName))
                {
                    slots[normalizedSlot] = splitBodyLineTextureName;
                    continue;
                }

                if (TryWriteBodySkinHeadLineNonInverted(
                    gameConfig,
                    primitive,
                    image,
                    normalizedSlot,
                    templateMeshPath,
                    fallbackTemplateBytes,
                    textureNamespace,
                    outputFolder,
                    existingNames,
                    reservedNames,
                    writtenNames,
                    semanticTemplateNames,
                    out var bodySkinHeadLineTextureName))
                {
                    slots[normalizedSlot] = bodySkinHeadLineTextureName;
                    continue;
                }

                var imageKey = ImageKey(image);
                if (writtenByImage.TryGetValue(imageKey, out var textureName))
                {
                    slots[normalizedSlot] = textureName;
                }
            }

            primitiveSlots.Add(slots);
        }

        return new ReinsertedTextures(primitiveSlots, writtenNames);
    }

    private static bool TryWriteSplitBodyLineAlphaTexture(
        GameConfig? gameConfig,
        GltfPrimitive primitive,
        GltfImage image,
        string normalizedSlot,
        string templateMeshPath,
        byte[] fallbackTemplateBytes,
        string textureNamespace,
        string outputFolder,
        HashSet<string> existingNames,
        HashSet<string> reservedNames,
        List<string> writtenNames,
        IReadOnlyDictionary<string, string> semanticTemplateNames,
        out string textureName)
    {
        textureName = "";
        if (gameConfig?.SplitBodyLineAlphaOnReimport != true ||
            !normalizedSlot.Equals("detail_diffuse", StringComparison.OrdinalIgnoreCase) ||
            !IsBodyLineTexture(image.Name) ||
            !IsSourceBodyPrimitive(primitive))
        {
            return false;
        }

        var semanticTemplateName = ResolveSemanticTemplateName(primitive, image.Name, normalizedSlot, semanticTemplateNames, gameConfig);
        var sourceTexturePath = FindSourceTextureTemplateForMode(
            templateMeshPath,
            image.Name,
            [],
            semanticTemplateName,
            ResolvedTextureNameMode.SemanticTemplateNames);
        var templateBytes = sourceTexturePath is not null
            ? File.ReadAllBytes(sourceTexturePath)
            : fallbackTemplateBytes;

        var baseName = (semanticTemplateName ?? image.Name) + "_body_alpha_invert";
        textureName = TryReservePreservedName(baseName, reservedNames)
                      ?? MakeUniqueTextureName(textureNamespace, baseName, existingNames, reservedNames);
        var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
        D3dtxWriter.WriteFromImageBytes(templateBytes, InvertImageAlpha(image), outputTexturePath);
        if (!writtenNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
        {
            writtenNames.Add(textureName);
        }

        return true;
    }

    // The neck/chest skin connector exported from TWAU uses a body (skin) diffuse but borrows the head
    // ink line texture. The shared head copy is alpha-inverted for the face shader; on this body-shader
    // skin patch that inverted alpha turns into an opaque black mask under the chin. Give this primitive
    // its own NON-inverted copy of the line so the skin shows through, without touching the face copy.
    private static bool TryWriteBodySkinHeadLineNonInverted(
        GameConfig? gameConfig,
        GltfPrimitive primitive,
        GltfImage image,
        string normalizedSlot,
        string templateMeshPath,
        byte[] fallbackTemplateBytes,
        string textureNamespace,
        string outputFolder,
        HashSet<string> existingNames,
        HashSet<string> reservedNames,
        List<string> writtenNames,
        IReadOnlyDictionary<string, string> semanticTemplateNames,
        out string textureName)
    {
        textureName = "";
        if (gameConfig?.InvertHeadLineAlphaOnReimport != true ||
            !normalizedSlot.Equals("detail_diffuse", StringComparison.OrdinalIgnoreCase) ||
            !IsHeadLineTexture(image.Name) ||
            !IsSourceBodyPrimitive(primitive))
        {
            return false;
        }

        var semanticTemplateName = ResolveSemanticTemplateName(primitive, image.Name, normalizedSlot, semanticTemplateNames, gameConfig);
        var sourceTexturePath = FindSourceTextureTemplateForMode(
            templateMeshPath,
            image.Name,
            [],
            semanticTemplateName,
            ResolvedTextureNameMode.SemanticTemplateNames);
        var templateBytes = sourceTexturePath is not null
            ? File.ReadAllBytes(sourceTexturePath)
            : fallbackTemplateBytes;

        var baseName = (semanticTemplateName ?? image.Name) + "_skin";
        textureName = TryReservePreservedName(baseName, reservedNames)
                      ?? MakeUniqueTextureName(textureNamespace, baseName, existingNames, reservedNames);
        var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
        D3dtxWriter.WriteFromImageBytes(templateBytes, image, outputTexturePath);
        if (!writtenNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
        {
            writtenNames.Add(textureName);
        }

        return true;
    }

    private static bool TryWriteHandLineInvertedAlphaTexture(
        GameConfig? gameConfig,
        GltfPrimitive primitive,
        GltfImage image,
        string normalizedSlot,
        string templateMeshPath,
        byte[] fallbackTemplateBytes,
        string outputFolder,
        List<string> writtenNames,
        IReadOnlyDictionary<string, string> semanticTemplateNames,
        out string textureName)
    {
        textureName = "";
        if (gameConfig?.InvertBodyLineAlphaOnReimport != true ||
            !normalizedSlot.Equals("detail_diffuse", StringComparison.OrdinalIgnoreCase) ||
            !IsLineOrDetailTexture(image.Name) ||
            !IsSourceHandPrimitive(primitive))
        {
            return false;
        }

        var semanticTemplateName = ResolveSemanticTemplateName(primitive, image.Name, normalizedSlot, semanticTemplateNames, gameConfig);
        var baseName = SanitizeName(StripKnownTextureExtension((semanticTemplateName ?? image.Name) + "_hands"));
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        textureName = baseName;
        if (writtenNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourceTexturePath = FindSourceTextureTemplateForMode(
            templateMeshPath,
            image.Name,
            [],
            semanticTemplateName,
            ResolvedTextureNameMode.SemanticTemplateNames);
        var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
        var templateBytes = sourceTexturePath is not null
            ? File.ReadAllBytes(sourceTexturePath)
            : fallbackTemplateBytes;
        D3dtxWriter.WriteFromImageBytes(templateBytes, InvertImageAlpha(image), outputTexturePath);

        writtenNames.Add(textureName);
        return true;
    }

    private static bool IsLineTexture(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_lines", StringComparison.Ordinal) ||
                lower.EndsWith("_line", StringComparison.Ordinal) ||
                lower.Contains("_lines_", StringComparison.Ordinal) ||
                lower.Contains("_line_", StringComparison.Ordinal);
    }

    private static bool IsLineOrDetailTexture(string textureName)
    {
        return IsLineTexture(textureName) || IsDetailTexture(textureName);
    }

    private static bool IsDetailTexture(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_detail", StringComparison.Ordinal) ||
               lower.Contains("_detail_", StringComparison.Ordinal);
    }

    private static bool IsBodyLineTexture(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.Contains("body", StringComparison.Ordinal) && IsLineTexture(textureName);
    }

    private static bool ShouldInvertCharacterLineAlpha(
        GameConfig? gameConfig,
        GltfPrimitive primitive,
        string normalizedSlot,
        string textureName)
    {
        // TWAU-style ink line overlays placed onto a TWD S2 head use the opposite alpha convention:
        // after reimport the face renders as an opaque black mask (what should be invisible is opaque,
        // the actual lines are transparent). Flip the alpha of the face line texture to fix it.
        // Detection is by the foreign "ink/line + head" naming, so TWD S2 native "*_head_detail"
        // round-trips are left untouched.
        if (gameConfig?.InvertHeadLineAlphaOnReimport != true ||
            !normalizedSlot.Equals("detail_diffuse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsHeadLineTexture(textureName);
    }

    private static bool IsHeadLineTexture(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.Contains("head", StringComparison.Ordinal) &&
               (lower.Contains("line", StringComparison.Ordinal) ||
                lower.Contains("ink", StringComparison.Ordinal));
    }

    private static bool ShouldSkipUnmappedSecondaryTexture(
        GameConfig? gameConfig,
        ResolvedTextureNameMode nameMode,
        string normalizedSlot,
        string? semanticTemplateName)
    {
        if (nameMode != ResolvedTextureNameMode.SemanticTemplateNames ||
            normalizedSlot.Equals("diffuse", StringComparison.OrdinalIgnoreCase) ||
            semanticTemplateName is not null)
        {
            return false;
        }

        return gameConfig?.Id != GameId.WolfAmongUs;
    }

    private static bool IsSourceBodyPrimitive(GltfPrimitive primitive)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
        {
            return false;
        }

        var lower = Path.GetFileNameWithoutExtension(diffuse.Name).ToLowerInvariant();
        return lower.Contains("body", StringComparison.Ordinal) &&
               !lower.Contains("hand", StringComparison.Ordinal) &&
               !lower.Contains("hair", StringComparison.Ordinal) &&
               !lower.Contains("head", StringComparison.Ordinal);
    }

    private static bool IsBodyOrHandPrimitive(GltfPrimitive primitive)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
        {
            return false;
        }

        var lower = Path.GetFileNameWithoutExtension(diffuse.Name).ToLowerInvariant();
        return lower.Contains("body", StringComparison.Ordinal) ||
               lower.Contains("hand", StringComparison.Ordinal);
    }

    private static bool IsSourceHandPrimitive(GltfPrimitive primitive)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
        {
            return false;
        }

        var diffuseName = Path.GetFileNameWithoutExtension(diffuse.Name).ToLowerInvariant();
        var sourceMesh = Path.GetFileNameWithoutExtension(primitive.SourceMeshPath ?? "").ToLowerInvariant();
        // Clementine-style combined exports keep the hand skin inside arm meshes while reusing the
        // head diffuse/detail pair for the exposed hand. Sleeve submeshes use body diffuse and skip this.
        return sourceMesh.Contains("arml", StringComparison.Ordinal) &&
               diffuseName.Contains("head", StringComparison.Ordinal);
    }

    private static GltfImage InvertImageAlpha(GltfImage image)
    {
        using var input = new MemoryStream(image.Data);
        using var source = new Bitmap(input);
        using var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);
                output.SetPixel(x, y, Color.FromArgb(255 - color.A, color.R, color.G, color.B));
            }
        }

        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        return new GltfImage
        {
            Name = image.Name,
            Data = stream.ToArray(),
            MimeType = "image/png",
        };
    }

    private static string NormalizeTextureSlotName(string slot)
        => slot.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "bump" : slot;

    private static bool IsSupportedTextureSlot(string slot) => TextureSlotsV13.Contains(slot);

    private static bool ShouldUseOriginalNameTickets(
        GltfModel model,
        ReinsertTextureOptions options,
        ResolvedTextureNameMode nameMode)
    {
        if (nameMode is not ResolvedTextureNameMode.PreferTemplateNames and
            not ResolvedTextureNameMode.SemanticTemplateNames)
        {
            return false;
        }

        var textureCount = CountImportedTextureImages(model, options);
        return textureCount is > 0 and <= 2;
    }

    private static int CountImportedTextureImages(GltfModel model, ReinsertTextureOptions options)
    {
        var imageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitive in model.Primitives)
        {
            foreach (var (slot, image) in primitive.ReferencedTextures)
            {
                if (IsGeneratedGltfHelperTexture(image.Name))
                {
                    continue;
                }

                var normalizedSlot = NormalizeTextureSlotName(slot);
                if (!IsSupportedTextureSlot(normalizedSlot) || !options.IncludesSlot(normalizedSlot))
                {
                    continue;
                }

                imageKeys.Add(ImageKey(image));
                if (imageKeys.Count > 2)
                {
                    return imageKeys.Count;
                }
            }
        }

        return imageKeys.Count;
    }

    private static ResolvedTextureNameMode ResolveNameMode(GameConfig? gameConfig, ReinsertTextureNameMode requested)
    {
        return requested switch
        {
            ReinsertTextureNameMode.SemanticTemplateNames => ResolvedTextureNameMode.SemanticTemplateNames,
            ReinsertTextureNameMode.PreferTemplateNames => ResolvedTextureNameMode.PreferTemplateNames,
            ReinsertTextureNameMode.PreferGltfNames => ResolvedTextureNameMode.PreferGltfNames,
            ReinsertTextureNameMode.GeneratedNames => ResolvedTextureNameMode.GeneratedNames,
            _ => (gameConfig ?? GameConfig.Current).PreferSemanticTemplateTextureNamesOnReimport
                ? ResolvedTextureNameMode.SemanticTemplateNames
                : (gameConfig ?? GameConfig.Current).PreferGltfTextureNamesOnReimport
                ? ResolvedTextureNameMode.PreferGltfNames
                : ResolvedTextureNameMode.PreferTemplateNames,
        };
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
        var existingNames = CreateUsedTextureNameSet(outputFolder);
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                textureName = ChooseOutputTextureName(
                    textureNamespace,
                    primitive.BaseColor.Name,
                    sourceTexturePath,
                    existingNames,
                    reservedNames,
                    [],
                    semanticTemplateName: null,
                    ResolvedTextureNameMode.PreferTemplateNames,
                    useOriginalNameTickets: false,
                    preserveTemplateName: true);
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
        HashSet<string> existingNames,
        HashSet<string> reservedNames,
        IReadOnlyList<string> preferredOriginalNames,
        string? semanticTemplateName,
        ResolvedTextureNameMode nameMode,
        bool useOriginalNameTickets,
        bool preserveTemplateName)
    {
        if (nameMode == ResolvedTextureNameMode.SemanticTemplateNames &&
            semanticTemplateName is not null &&
            TryReservePreservedName(semanticTemplateName, reservedNames) is { } semanticName)
        {
            return semanticName;
        }

        if (nameMode == ResolvedTextureNameMode.SemanticTemplateNames &&
            semanticTemplateName is null)
        {
            if (useOriginalNameTickets)
            {
                foreach (var preferred in preferredOriginalNames)
                {
                    if (TryReserveOriginalReplacementName(preferred, reservedNames) is { } preferredName)
                    {
                        return preferredName;
                    }
                }
            }

            if (TryReservePreservedName(rawName, reservedNames) is { } rawSemanticFallback)
            {
                return rawSemanticFallback;
            }

            if (sourceTexturePath is not null &&
                TryReservePreservedName(Path.GetFileName(sourceTexturePath), reservedNames) is { } sourceSemanticFallback)
            {
                return sourceSemanticFallback;
            }
        }

        if (nameMode == ResolvedTextureNameMode.PreferGltfNames)
        {
            if (TryReservePreservedName(rawName, reservedNames) is { } gltfName)
            {
                return gltfName;
            }

            if (sourceTexturePath is not null &&
                TryReservePreservedName(Path.GetFileName(sourceTexturePath), reservedNames) is { } sourceName)
            {
                return sourceName;
            }
        }

        if (nameMode == ResolvedTextureNameMode.PreferTemplateNames)
        {
            if (useOriginalNameTickets)
            {
                foreach (var preferred in preferredOriginalNames)
                {
                    if (TryReserveOriginalReplacementName(preferred, reservedNames) is { } preferredName)
                    {
                        return preferredName;
                    }
                }
            }

            if (preserveTemplateName && !useOriginalNameTickets)
            {
                foreach (var preferred in preferredOriginalNames)
                {
                    if (TryReservePreservedName(preferred, reservedNames) is { } preferredName)
                    {
                        return preferredName;
                    }
                }
            }

            if (preserveTemplateName && sourceTexturePath is not null)
            {
                var sourceName = Path.GetFileName(sourceTexturePath);
                var originalName = useOriginalNameTickets
                    ? TryReserveOriginalReplacementName(sourceName, reservedNames)
                    : TryReservePreservedName(sourceName, reservedNames);
                if (originalName is not null)
                {
                    return originalName;
                }
            }

            if (preserveTemplateName &&
                (useOriginalNameTickets
                    ? TryReserveOriginalReplacementName(rawName, reservedNames)
                    : TryReservePreservedName(rawName, reservedNames)) is { } rawOriginalName)
            {
                return rawOriginalName;
            }
        }

        return MakeUniqueTextureName(textureNamespace, rawName, existingNames, reservedNames);
    }

    private static string? TryReservePreservedName(string name, HashSet<string> reservedNames)
    {
        var sanitized = SanitizeName(StripKnownTextureExtension(Path.GetFileName(name)));
        return !string.IsNullOrWhiteSpace(sanitized) && reservedNames.Add(sanitized)
            ? sanitized
            : null;
    }

    private static string? TryReserveOriginalReplacementName(string name, HashSet<string> reservedNames)
    {
        return IsSafeOriginalReplacementTextureName(name)
            ? TryReservePreservedName(name, reservedNames)
            : null;
    }

    private static string? FindSourceTextureTemplateForMode(
        string templateMeshPath,
        string imageName,
        IReadOnlyList<string> preferredOriginalNames,
        string? semanticTemplateName,
        ResolvedTextureNameMode nameMode)
    {
        if (nameMode == ResolvedTextureNameMode.SemanticTemplateNames && semanticTemplateName is not null)
        {
            return FindSourceTextureTemplate(templateMeshPath, imageName)
                   ?? FindSourceTextureTemplate(templateMeshPath, semanticTemplateName)
                   ?? FindSourceTextureTemplate(templateMeshPath, preferredOriginalNames);
        }

        if (nameMode == ResolvedTextureNameMode.PreferTemplateNames)
        {
            return FindSourceTextureTemplate(templateMeshPath, preferredOriginalNames)
                   ?? FindSourceTextureTemplate(templateMeshPath, imageName);
        }

        return FindSourceTextureTemplate(templateMeshPath, imageName)
               ?? FindSourceTextureTemplate(templateMeshPath, preferredOriginalNames);
    }

    private static Dictionary<string, string> BuildSemanticTemplateNames(string templateMeshPath, GameConfig? gameConfig)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MeshData templateMesh;
        try
        {
            templateMesh = D3DMeshParser.Parse(File.ReadAllBytes(templateMeshPath));
        }
        catch
        {
            return result;
        }

        foreach (var submesh in templateMesh.Submeshes)
        {
            foreach (var (slot, textureName) in submesh.TextureNames)
            {
                if (!IsUsableOriginalTextureName(textureName))
                {
                    continue;
                }

                var normalizedSlot = NormalizeTextureSlotName(slot);
                var semantic = ClassifyTemplateTextureSemantic(submesh.Name, textureName, gameConfig);
                if (semantic is null)
                {
                    continue;
                }

                var key = SemanticKey(semantic, normalizedSlot);
                if (!result.ContainsKey(key))
                {
                    result[key] = textureName;
                }
            }
        }

        return result;
    }

    private static string? ResolveSemanticTemplateName(
        GltfPrimitive primitive,
        string imageName,
        string slot,
        IReadOnlyDictionary<string, string> semanticTemplateNames,
        GameConfig? gameConfig)
    {
        if (semanticTemplateNames.Count == 0)
        {
            return null;
        }

        var primitiveSemantic = ResolvePrimitiveSemantic(primitive, gameConfig);
        var imageSemantic = ClassifySourceTextureSemantic(imageName, gameConfig);
        var semantic = slot.Equals("diffuse", StringComparison.OrdinalIgnoreCase)
            ? imageSemantic ?? primitiveSemantic
            : primitiveSemantic ?? imageSemantic;
        if (semantic is null)
        {
            return null;
        }

        return semanticTemplateNames.TryGetValue(SemanticKey(semantic, slot), out var templateName)
            ? templateName
            : null;
    }

    private static string? ResolvePrimitiveSemantic(GltfPrimitive primitive, GameConfig? gameConfig)
    {
        if (primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
        {
            var semantic = ClassifySourceTextureSemantic(diffuse.Name, gameConfig);
            if (semantic is not null)
            {
                return semantic;
            }
        }

        return string.IsNullOrWhiteSpace(primitive.MaterialName)
            ? null
            : ClassifySourceTextureSemantic(primitive.MaterialName, gameConfig);
    }

    private static string? ClassifyTemplateTextureSemantic(string submeshName, string textureName, GameConfig? gameConfig)
        => ClassifySemantic(submeshName + " " + textureName, template: true, gameConfig);

    private static string? ClassifySourceTextureSemantic(string name, GameConfig? gameConfig)
        => ClassifySemantic(name, template: false, gameConfig);

    private static string? ClassifySemantic(string text, bool template, GameConfig? gameConfig)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("eye"))
        {
            return "eye";
        }

        if (lower.Contains("mouth"))
        {
            return "mouth";
        }

        if (lower.Contains("alphahair") || lower.Contains("hair"))
        {
            return "hair";
        }

        if (gameConfig?.Id == GameId.WolfAmongUs && lower.Contains("hand"))
        {
            return "hands";
        }

        if (!template && (lower.Contains("hand") || lower.Contains("beastb")))
        {
            return "body";
        }

        if (lower.Contains("body"))
        {
            return "body";
        }

        if (lower.Contains("head"))
        {
            return "head";
        }

        return null;
    }

    private static string SemanticKey(string semantic, string slot)
        => semantic + ":" + NormalizeTextureSlotName(slot);

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

    private static OriginalTextureNamePool BuildOriginalTextureNamePool(string templateMeshPath)
    {
        MeshData templateMesh;
        try
        {
            templateMesh = D3DMeshParser.Parse(File.ReadAllBytes(templateMeshPath));
        }
        catch
        {
            return OriginalTextureNamePool.Empty;
        }

        var allNames = new List<string>();
        var bySlot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in TextureSlotsV13)
        {
            var slotNames = new List<string>();
            foreach (var submesh in templateMesh.Submeshes)
            {
                if (!submesh.TextureNames.TryGetValue(slot, out var textureName) ||
                    !IsSafeOriginalReplacementTextureName(textureName))
                {
                    continue;
                }

                if (!slotNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
                {
                    slotNames.Add(textureName);
                }
                if (!allNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
                {
                    allNames.Add(textureName);
                }
            }

            if (slotNames.Count > 0)
            {
                bySlot[slot] = slotNames;
            }
        }

        return allNames.Count == 0
            ? OriginalTextureNamePool.Empty
            : new OriginalTextureNamePool(bySlot, allNames);
    }

    private static IReadOnlyList<string> BuildOriginalNameCandidates(
        string slot,
        IReadOnlyList<string> preferredOriginalNames,
        OriginalTextureNamePool pool)
    {
        var result = new List<string>();
        AddSafeOriginalCandidates(result, preferredOriginalNames);

        if (pool.AllNames.Count == 0)
        {
            return result;
        }

        if (pool.NamesBySlot.TryGetValue(slot, out var slotNames))
        {
            AddSafeOriginalCandidates(result, slotNames);
        }
        AddSafeOriginalCandidates(result, pool.AllNames);
        return result;
    }

    private static void AddSafeOriginalCandidates(List<string> target, IEnumerable<string> names)
    {
        foreach (var name in names.OrderBy(OriginalReplacementTexturePriority))
        {
            if (IsSafeOriginalReplacementTextureName(name) &&
                !target.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(name);
            }
        }
    }

    private static bool IsUsableOriginalTextureName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               !name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeOriginalReplacementTextureName(string name)
    {
        if (!IsUsableOriginalTextureName(name))
        {
            return false;
        }

        var stem = StripKnownTextureExtension(Path.GetFileName(name)).ToLowerInvariant();
        return !SharedOriginalReplacementTextureNames.Contains(stem) &&
               !stem.StartsWith("map_1px", StringComparison.OrdinalIgnoreCase) &&
               !stem.StartsWith("sk_sharedparts_", StringComparison.OrdinalIgnoreCase) &&
               !stem.StartsWith("map_gradient", StringComparison.OrdinalIgnoreCase);
    }

    private static int OriginalReplacementTexturePriority(string name)
    {
        var stem = StripKnownTextureExtension(Path.GetFileName(name)).ToLowerInvariant();
        if (stem.Contains("body") || stem.Contains("torso") || stem.Contains("skin"))
        {
            return 0;
        }

        if (stem.Contains("head") || stem.Contains("face"))
        {
            return 1;
        }

        if (stem.Contains("hair"))
        {
            return 2;
        }

        if (stem.Contains("hand") || stem.Contains("arm"))
        {
            return 3;
        }

        return 4;
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

    private static string MakeUniqueTextureName(
        string textureNamespace,
        string rawName,
        HashSet<string> existingNames,
        HashSet<string> reservedNames)
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
        while (existingNames.Contains(name) || reservedNames.Contains(name))
        {
            name = ShortenTextureName($"{baseName}_{suffix++}");
        }

        reservedNames.Add(name);
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

internal sealed record OriginalTextureNamePool(
    IReadOnlyDictionary<string, IReadOnlyList<string>> NamesBySlot,
    IReadOnlyList<string> AllNames)
{
    public static OriginalTextureNamePool Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), []);
}

public sealed class ReinsertTextureOptions
{
    public static ReinsertTextureOptions Default { get; } = new();

    public ReinsertTextureNameMode NameMode { get; init; } = ReinsertTextureNameMode.GameDefault;
    public IReadOnlySet<string>? IncludedSlots { get; init; }

    public bool IncludesSlot(string slot)
    {
        return IncludedSlots is null ||
               IncludedSlots.Contains(slot) ||
               IncludedSlots.Contains(slot.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "bump" : slot);
    }
}

public enum ReinsertTextureNameMode
{
    GameDefault,
    SemanticTemplateNames,
    PreferTemplateNames,
    PreferGltfNames,
    GeneratedNames,
}

internal enum ResolvedTextureNameMode
{
    SemanticTemplateNames,
    PreferTemplateNames,
    PreferGltfNames,
    GeneratedNames,
}
