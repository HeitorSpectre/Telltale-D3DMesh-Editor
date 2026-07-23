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
    private static readonly HashSet<string> TextureSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "diffuse", "bake", "bump", "environment", "detail_diffuse", "detail_bump",
        "specular", "tex8", "gradient", "tex10", "shadow", "emissive",
        "alternate_bump", "occlusion"
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
        if ((gameConfig ?? GameConfig.Current).Id == GameId.WalkingDeadMichonne)
        {
            return ToReinsertedTextures(
                WriteV25ReferencedTextures(model, templateMeshPath, outputMeshPath, options.ForceUncompressed));
        }

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
        var semanticTemplateNames = nameMode == ResolvedTextureNameMode.SemanticTemplateNames ||
                                    (gameConfig ?? GameConfig.Current).Id == GameId.GameOfThrones
            ? BuildSemanticTemplateNames(templateMeshPath, gameConfig)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bumpResolver = StrippedLineTextureRecovery.BuildBumpResolver(templateMeshPath);

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
                    var gotSharedTemplateName = ResolveGameOfThronesSharedTemplateName(
                        image.Name,
                        normalizedReference,
                        semanticTemplateNames,
                        gameConfig);
                    if (TryResolveGameProvidedTextureName(templateMeshPath, gotSharedTemplateName ?? image.Name, out var gameProvidedName))
                    {
                        if (ShouldMapWithoutEmittingGameProvidedTexture(gameProvidedName) ||
                            CopyGameProvidedTextureIfAvailable(templateMeshPath, outputFolder, gameProvidedName, writtenNames))
                        {
                            writtenByImage[imageKey] = gameProvidedName;
                            continue;
                        }
                    }

                    var namingSlot = diffuseImageKeys.Contains(imageKey)
                        ? "diffuse"
                        : normalizedReference;
                    var preferredNames = preferredOriginalNamesByImageKey.TryGetValue(imageKey, out var foundPreferredNames) ? foundPreferredNames : [];
                    var originalNameCandidates = useOriginalNameTickets
                        ? BuildOriginalNameCandidates(namingSlot, preferredNames, originalTextureNamePool)
                        : preferredNames;
                    var semanticTemplateName = ResolveSemanticTemplateName(primitive, image.Name, namingSlot, semanticTemplateNames, gameConfig);
                    if (ShouldSkipUnmappedSecondaryTexture(gameConfig, nameMode, namingSlot, semanticTemplateName))
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
                        preserveTemplateName: preserveDiffuseName ||
                                              sourceImageMatches ||
                                              gameConfig?.PreserveSecondaryTextureNamesOnReimport == true);
                    if (FindSourceTextureTemplate(templateMeshPath, textureName) is { } chosenSourceTexturePath)
                    {
                        sourceTexturePath = chosenSourceTexturePath;
                        sourceImageMatches = ImageMatchesSourceTexture(image, sourceTexturePath);
                    }

                    if (TryResolveGameProvidedTextureName(templateMeshPath, textureName, out var chosenGameProvidedName))
                    {
                        if (ShouldMapWithoutEmittingGameProvidedTexture(chosenGameProvidedName) ||
                            CopyGameProvidedTextureIfAvailable(templateMeshPath, outputFolder, chosenGameProvidedName, writtenNames))
                        {
                            writtenByImage[imageKey] = chosenGameProvidedName;
                            continue;
                        }
                    }

                    var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
                    if (ShouldInvertCharacterLineAlpha(gameConfig, primitive, normalizedReference, image.Name))
                    {
                        var templateBytes = sourceTexturePath is not null
                            ? File.ReadAllBytes(sourceTexturePath)
                            : fallbackTemplateBytes;
                        D3dtxWriter.WriteFromImageBytes(templateBytes, InvertImageAlpha(image), outputTexturePath, options.ForceUncompressed);
                    }
                    else
                    {
                        WriteTexturePreservingTemplate(fallbackTemplateBytes, image, sourceTexturePath, outputTexturePath, sourceImageMatches, options.ForceUncompressed);
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

                var slotImageKey = ImageKey(image);
                if (!normalizedSlot.Equals("diffuse", StringComparison.OrdinalIgnoreCase) &&
                    diffuseImageKeys.Contains(slotImageKey))
                {
                    if (writtenByImage.TryGetValue(slotImageKey, out var sharedDiffuseTextureName))
                    {
                        slots[normalizedSlot] = sharedDiffuseTextureName;
                    }
                    continue;
                }

                if (IsNativeGameOfThronesSharedTextureName(image.Name, gameConfig) &&
                    writtenByImage.TryGetValue(slotImageKey, out var nativeGotSharedTextureName))
                {
                    slots[normalizedSlot] = nativeGotSharedTextureName;
                    continue;
                }

                var semanticSlotName = ResolveSemanticTemplateName(primitive, image.Name, normalizedSlot, semanticTemplateNames, gameConfig);
                var gotSharedTemplateName = ResolveGameOfThronesSharedTemplateName(
                    image.Name,
                    normalizedSlot,
                    semanticTemplateNames,
                    gameConfig);
                if (TryResolveGameProvidedTextureName(templateMeshPath, gotSharedTemplateName ?? semanticSlotName ?? image.Name, out var slotGameProvidedName))
                {
                    if (ShouldMapWithoutEmittingGameProvidedTexture(slotGameProvidedName) ||
                        CopyGameProvidedTextureIfAvailable(templateMeshPath, outputFolder, slotGameProvidedName, writtenNames))
                    {
                        slots[normalizedSlot] = slotGameProvidedName;
                        continue;
                    }
                }

                if (TryWriteBodyLineInvertedAlphaTexture(
                    gameConfig,
                    primitive,
                    image,
                    normalizedSlot,
                    templateMeshPath,
                    fallbackTemplateBytes,
                    outputFolder,
                    writtenNames,
                    semanticTemplateNames,
                    out var bodyLineTextureName))
                {
                    slots[normalizedSlot] = bodyLineTextureName;
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

                if (ShouldSkipUnmappedSecondaryTexture(gameConfig, nameMode, normalizedSlot, semanticSlotName))
                {
                    continue;
                }

                if (writtenByImage.TryGetValue(slotImageKey, out var textureName))
                {
                    slots[normalizedSlot] = textureName;
                }
            }

            RecoverStrippedDetailLineSlot(
                primitive,
                slots,
                templateMeshPath,
                outputFolder,
                options,
                writtenNames);

            RecoverStrippedBumpSlot(
                primitive,
                slots,
                templateMeshPath,
                outputFolder,
                options,
                writtenNames,
                bumpResolver);

            RecoverTemplateGradientSlot(
                gameConfig,
                slots,
                templateMeshPath,
                options,
                semanticTemplateNames);

            primitiveSlots.Add(slots);
        }

        return new ReinsertedTextures(primitiveSlots, writtenNames);
    }

    // Blender strips the material/primitive extras that carry the detail/line slot, keeping only the
    // line-overlay material name. GltfReader recovers the original line texture name from it; here we
    // bind it back so the character outlines survive the round-trip. Binding the original name keeps
    // the d3dmesh pointing at the game's own (unmodified) line texture, and also marks the slot as
    // provided so the reinserter does not clear the template's detail_diffuse entry. When the original
    // .d3dtx is found next to the template it is copied out verbatim, keeping the export self-contained.
    private static void RecoverStrippedDetailLineSlot(
        GltfPrimitive primitive,
        Dictionary<string, string> slots,
        string templateMeshPath,
        string outputFolder,
        ReinsertTextureOptions options,
        List<string> writtenNames)
    {
        const string slot = "detail_diffuse";
        var lineName = primitive.RecoveredDetailLineTextureName;
        if (string.IsNullOrWhiteSpace(lineName) ||
            slots.ContainsKey(slot) ||
            !options.IncludesSlot(slot))
        {
            return;
        }

        slots[slot] = lineName;
        if (writtenNames.Contains(lineName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceTexturePath = FindSourceTextureTemplate(templateMeshPath, lineName);
        var recoveredLineImage = primitive.RecoveredDetailLineImage;
        if (sourceTexturePath is null && recoveredLineImage is null)
        {
            return;
        }

        var outputTexturePath = Path.Combine(outputFolder, lineName + ".d3dtx");
        if (sourceTexturePath is not null)
        {
            if (!Path.GetFullPath(sourceTexturePath).Equals(Path.GetFullPath(outputTexturePath), StringComparison.OrdinalIgnoreCase))
            {
                D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(sourceTexturePath), outputTexturePath);
            }
        }
        else
        {
            if (recoveredLineImage is null)
            {
                return;
            }

            var templateTexturePath = FindDetailTextureTemplate(templateMeshPath) ?? FindTemplateTexture(templateMeshPath);
            if (templateTexturePath is null)
            {
                return;
            }

            var templateBytes = File.ReadAllBytes(templateTexturePath);
            D3dtxWriter.WriteFromImageBytes(templateBytes, recoveredLineImage, outputTexturePath, options.ForceUncompressed);
        }

        writtenNames.Add(lineName);
    }

    // The normal map gets the same treatment as the line: Blender drops the bump slot and prunes the _nm
    // image, and the GLB carries no normal identity to recover from. We map the primitive back to the
    // template submesh by its (diffuse + recovered line) pair, bind that submesh's normal map by name so
    // the d3dmesh keeps pointing at the game's own _nm, and copy the original .d3dtx out verbatim. Runs
    // only when the GLB itself supplied no normal map (so genuine imported normals are untouched), and
    // only off the atlas path — with the atlas active the normal map is packed into the _nm atlas instead.
    private static void RecoverStrippedBumpSlot(
        GltfPrimitive primitive,
        Dictionary<string, string> slots,
        string templateMeshPath,
        string outputFolder,
        ReinsertTextureOptions options,
        List<string> writtenNames,
        StrippedLineTextureRecovery.TemplateBumpResolver bumpResolver)
    {
        const string slot = "bump";
        if (slots.ContainsKey(slot) ||
            !options.IncludesSlot(slot) ||
            StrippedLineTextureRecovery.HasBumpSlot(primitive) ||
            bumpResolver.Resolve(primitive) is not { } bumpName ||
            string.IsNullOrWhiteSpace(bumpName))
        {
            return;
        }

        var normalizedBumpName = StripKnownTextureExtension(Path.GetFileName(bumpName));
        slots[slot] = normalizedBumpName;
        if (writtenNames.Contains(normalizedBumpName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceTexturePath = FindSourceTextureTemplate(templateMeshPath, bumpName);
        if (sourceTexturePath is null)
        {
            return;
        }

        var outputTexturePath = Path.Combine(outputFolder, normalizedBumpName + ".d3dtx");
        if (!Path.GetFullPath(sourceTexturePath).Equals(Path.GetFullPath(outputTexturePath), StringComparison.OrdinalIgnoreCase))
        {
            D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(sourceTexturePath), outputTexturePath);
        }

        writtenNames.Add(normalizedBumpName);
    }

    private static void RecoverTemplateGradientSlot(
        GameConfig? gameConfig,
        Dictionary<string, string> slots,
        string templateMeshPath,
        ReinsertTextureOptions options,
        IReadOnlyDictionary<string, string> semanticTemplateNames)
    {
        const string slot = "gradient";
        if (gameConfig?.Id != GameId.WolfAmongUs ||
            slots.ContainsKey(slot) ||
            !options.IncludesSlot(slot))
        {
            return;
        }

        var gradientName = semanticTemplateNames
            .Where(pair => pair.Key.EndsWith(":gradient", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(gradientName) ||
            !TryResolveGameProvidedTextureName(templateMeshPath, gradientName, out var resolvedGradientName))
        {
            return;
        }

        slots[slot] = resolvedGradientName;
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

    private static bool TryWriteBodyLineInvertedAlphaTexture(
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
            !IsBodyLineTargetPrimitive(primitive))
        {
            return false;
        }

        var semanticTemplateName = ResolveSemanticTemplateName(primitive, image.Name, normalizedSlot, semanticTemplateNames, gameConfig);
        var baseName = SanitizeName(StripKnownTextureExtension(semanticTemplateName ?? image.Name));
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
                lower.Contains("_line_", StringComparison.Ordinal) ||
                lower.Contains("inklines", StringComparison.Ordinal) ||
                lower.Contains("ink_lines", StringComparison.Ordinal);
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
        if (!normalizedSlot.Equals("detail_diffuse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (gameConfig?.InvertHeadLineAlphaOnReimport == true && IsHeadLineTexture(textureName))
        {
            return true;
        }

        return gameConfig?.InvertBodyLineAlphaOnReimport == true &&
               IsLineOrDetailTexture(textureName) &&
               IsBodyLineTargetPrimitive(primitive);
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

        return true;
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

    private static bool IsBodyLineTargetPrimitive(GltfPrimitive primitive)
    {
        if (primitive.TextureSlots.TryGetValue("diffuse", out var diffuse) &&
            IsBodyLineTargetName(diffuse.Name))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(primitive.MaterialName) &&
            IsBodyLineTargetName(primitive.MaterialName))
        {
            return true;
        }

        var sourceMesh = Path.GetFileNameWithoutExtension(primitive.SourceMeshPath ?? "").ToLowerInvariant();
        return sourceMesh.Contains("body", StringComparison.Ordinal) ||
               sourceMesh.Contains("torso", StringComparison.Ordinal) ||
               sourceMesh.Contains("arm", StringComparison.Ordinal) ||
               sourceMesh.Contains("leg", StringComparison.Ordinal) ||
               sourceMesh.Contains("foot", StringComparison.Ordinal) ||
               sourceMesh.Contains("feet", StringComparison.Ordinal) ||
               sourceMesh.Contains("pant", StringComparison.Ordinal) ||
               sourceMesh.Contains("cloth", StringComparison.Ordinal);
    }

    private static bool IsBodyLineTargetName(string name)
    {
        var lower = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return !lower.Contains("head", StringComparison.Ordinal) &&
               !lower.Contains("hair", StringComparison.Ordinal) &&
               (lower.Contains("body", StringComparison.Ordinal) ||
                lower.Contains("neck", StringComparison.Ordinal) ||
                lower.Contains("hand", StringComparison.Ordinal) ||
                lower.Contains("arm", StringComparison.Ordinal) ||
                lower.Contains("leg", StringComparison.Ordinal) ||
                lower.Contains("foot", StringComparison.Ordinal) ||
                lower.Contains("feet", StringComparison.Ordinal) ||
                lower.Contains("pant", StringComparison.Ordinal) ||
                lower.Contains("cloth", StringComparison.Ordinal));
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
    {
        if (slot.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return "bump";
        }

        // D3DMesh stores the baked lighting channel as "bake". Accept the common GLB/Toolkit
        // aliases so an imported material writes the user's lightmap into that actual slot.
        return slot.Equals("lightmap", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("light_map", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("lighting", StringComparison.OrdinalIgnoreCase) ||
               slot.Equals("lighting_map", StringComparison.OrdinalIgnoreCase)
            ? "bake"
            : slot;
    }

    private static bool IsSupportedTextureSlot(string slot) => TextureSlots.Contains(slot);

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

    // Resolves the diffuse name the packed atlas should reuse so its .d3dtx carries a real texture name the
    // game already references - never a lines/detail map. The normal atlas uses a compatible original
    // normal-map name from the same texture family when one exists, otherwise it falls back to "<diffuse>_nm".
    public static AtlasTextureNames? ResolveAtlasTextureNames(string templateMeshPath)
    {
        MeshData? templateMesh;
        try
        {
            templateMesh = D3DMeshParser.ParseFile(templateMeshPath);
        }
        catch
        {
            templateMesh = null;
        }

        string? bestDiffuse = null;
        var bestDiffusePriority = int.MaxValue;
        foreach (var submesh in templateMesh?.Submeshes ?? [])
        {
            var diffuse = UsableSlotTextureName(submesh, "diffuse", requireNormalMap: false);
            if (diffuse is not null)
            {
                var diffusePriority = OriginalReplacementTexturePriority(diffuse);
                if (diffusePriority < bestDiffusePriority)
                {
                    bestDiffuse = diffuse;
                    bestDiffusePriority = diffusePriority;
                }
            }
        }

        if (bestDiffuse is null)
        {
            var templateTexturePath = FindTemplateTexture(templateMeshPath);
            var fallback = templateTexturePath is null
                ? null
                : StripKnownTextureExtension(Path.GetFileName(templateTexturePath));
            if (string.IsNullOrWhiteSpace(fallback) || IsLineOrDetailTexture(fallback))
            {
                return null;
            }

            bestDiffuse = fallback;
        }

        return new AtlasTextureNames(
            SanitizeName(bestDiffuse),
            ResolveCompatibleAtlasNormalName(templateMesh, bestDiffuse),
            ResolveCompatibleAtlasDetailName(templateMesh, bestDiffuse));
    }

    // Finds an existing detail/lines texture name from the template to reuse for the separate detail atlas,
    // so the detail section keeps a real detail map name (and the writer reuses its original .d3dtx format,
    // which for lines is often A8/alpha rather than colour BC). Prefers a name in the same texture family as
    // the chosen diffuse; otherwise returns the first detail/lines name found.
    private static string? ResolveCompatibleAtlasDetailName(MeshData? templateMesh, string atlasDiffuseName)
    {
        if (templateMesh is null)
        {
            return null;
        }

        var atlasFamily = TextureFamilyKey(StripKnownTextureExtension(Path.GetFileName(atlasDiffuseName)));
        string? firstDetail = null;
        foreach (var submesh in templateMesh.Submeshes)
        {
            foreach (var slot in new[] { "detail_diffuse", "detail_bump", "tex8" })
            {
                if (!submesh.TextureNames.TryGetValue(slot, out var name) ||
                    !IsSafeOriginalReplacementTextureName(name) ||
                    !IsLineOrDetailTexture(name))
                {
                    continue;
                }

                var stem = StripKnownTextureExtension(Path.GetFileName(name));
                firstDetail ??= stem;
                if (!string.IsNullOrWhiteSpace(atlasFamily) &&
                    TextureFamilyKey(stem).Equals(atlasFamily, StringComparison.OrdinalIgnoreCase))
                {
                    return SanitizeName(stem);
                }
            }
        }

        return firstDetail is null ? null : SanitizeName(firstDetail);
    }

    private static string? ResolveCompatibleAtlasNormalName(MeshData? templateMesh, string atlasDiffuseName)
    {
        if (templateMesh is null)
        {
            return null;
        }

        var atlasDiffuseStem = StripKnownTextureExtension(Path.GetFileName(atlasDiffuseName));
        var atlasFamily = TextureFamilyKey(atlasDiffuseStem);
        string? bestNormal = null;
        var bestPriority = int.MaxValue;

        foreach (var submesh in templateMesh.Submeshes)
        {
            var normal = UsableSlotTextureName(submesh, "bump", requireNormalMap: true);
            if (normal is null)
            {
                continue;
            }

            var normalBase = StripNormalSuffix(normal);
            if (normalBase.Equals(atlasDiffuseStem, StringComparison.OrdinalIgnoreCase))
            {
                return SanitizeName(normal);
            }

            if (string.IsNullOrWhiteSpace(atlasFamily) ||
                !TextureFamilyKey(normalBase).Equals(atlasFamily, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var priority = OriginalReplacementTexturePriority(normalBase);
            if (priority < bestPriority)
            {
                bestNormal = normal;
                bestPriority = priority;
            }
        }

        return bestNormal is null ? null : SanitizeName(bestNormal);
    }

    private static string StripNormalSuffix(string textureName)
    {
        var stem = StripKnownTextureExtension(Path.GetFileName(textureName));
        return stem.EndsWith("_nm", StringComparison.OrdinalIgnoreCase)
            ? stem[..^3]
            : stem.EndsWith("_normal", StringComparison.OrdinalIgnoreCase)
                ? stem[..^7]
                : stem;
    }

    private static string TextureFamilyKey(string textureName)
    {
        var stem = StripKnownTextureExtension(Path.GetFileName(textureName)).ToLowerInvariant();
        foreach (var suffix in new[]
                 {
                     "_hair_alpha", "_eyelashesmale", "_eyelashes", "_eyebrown",
                     "_haircap", "_alphahair", "_detail", "_body", "_head",
                     "_hands", "_hand", "_hair", "_mouth", "_eye"
                 })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
            {
                return stem[..^suffix.Length];
            }
        }

        return stem;
    }

    // A submesh's texture name for a slot, only when it is a real, reusable name and not a lines/detail map.
    // When requireNormalMap is set, the name must also look like a normal map (e.g. "*_nm").
    private static string? UsableSlotTextureName(SubmeshData submesh, string slot, bool requireNormalMap)
    {
        if (!submesh.TextureNames.TryGetValue(slot, out var name) ||
            !IsSafeOriginalReplacementTextureName(name) ||
            IsLineOrDetailTexture(name) ||
            (requireNormalMap && !IsNormalMapName(name)))
        {
            return null;
        }

        return StripKnownTextureExtension(Path.GetFileName(name));
    }

    private static bool IsNormalMapName(string textureName)
    {
        var lower = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        return lower.EndsWith("_nm", StringComparison.Ordinal) ||
               lower.EndsWith("_normal", StringComparison.Ordinal) ||
               lower.Contains("_normal_", StringComparison.Ordinal) ||
               lower.Contains("normalmap", StringComparison.Ordinal);
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
            var mesh = D3DMeshParser.ParseFile(templateMeshPath);
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

    private static string? FindDetailTextureTemplate(string templateMeshPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            var mesh = D3DMeshParser.ParseFile(templateMeshPath);
            foreach (var submesh in mesh.Submeshes)
            {
                if (submesh.TextureNames.TryGetValue("detail_diffuse", out var detailName) &&
                    FindSourceTextureTemplate(templateMeshPath, detailName) is { } detailPath)
                {
                    return detailPath;
                }
            }
        }
        catch
        {
            // Fallback below keeps the recovery usable for meshes we cannot parse here.
        }

        return Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly)
            .Where(path => IsLineOrDetailTexture(Path.GetFileNameWithoutExtension(path)))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
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
        bool sourceImageMatches,
        bool forceUncompressed = false)
    {
        // An exact, unmodified match of the game's own texture is copied verbatim: it already renders
        // correctly in-game (whatever format it shipped in), so we never recompress it. Forcing
        // uncompressed only applies when we actually re-encode an imported/modified image below.
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
        D3dtxWriter.WriteFromImageBytes(templateBytes, image, outputTexturePath, forceUncompressed);
    }

    private static void CopyTextureVerbatim(string sourceTexturePath, string outputTexturePath)
    {
        if (Path.GetFullPath(sourceTexturePath).Equals(Path.GetFullPath(outputTexturePath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(sourceTexturePath), outputTexturePath);
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
            semanticTemplateName is not null)
        {
            if (TryReservePreservedName(semanticTemplateName, reservedNames) is { } semanticName)
            {
                return semanticName;
            }

            if (sourceTexturePath is not null &&
                TryReservePreservedName(Path.GetFileName(sourceTexturePath), reservedNames) is { } sourceSemanticFallback)
            {
                return sourceSemanticFallback;
            }

            foreach (var preferred in preferredOriginalNames)
            {
                if (TryReservePreservedName(preferred, reservedNames) is { } preferredSemanticFallback)
                {
                    return preferredSemanticFallback;
                }
            }

            if (TryReservePreservedName(rawName, reservedNames) is { } rawSemanticFallback)
            {
                return rawSemanticFallback;
            }
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
            if (preserveTemplateName &&
                sourceTexturePath is not null &&
                TexturePathStemEquals(sourceTexturePath, rawName) &&
                TryReservePreservedName(Path.GetFileName(sourceTexturePath), reservedNames) is { } exactSourceName)
            {
                return exactSourceName;
            }

            if (semanticTemplateName is not null &&
                TryReservePreservedName(semanticTemplateName, reservedNames) is { } semanticName)
            {
                return semanticName;
            }

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

    private static bool TexturePathStemEquals(string path, string name)
        => StripKnownTextureExtension(Path.GetFileName(path))
            .Equals(StripKnownTextureExtension(Path.GetFileName(name)), StringComparison.OrdinalIgnoreCase);

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
            templateMesh = D3DMeshParser.ParseFile(templateMeshPath);
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

        if (semanticTemplateNames.TryGetValue(SemanticKey(semantic, slot), out var templateName))
        {
            return templateName;
        }

        foreach (var fallbackSemantic in FallbackSemantics(semantic))
        {
            if (semanticTemplateNames.TryGetValue(SemanticKey(fallbackSemantic, slot), out templateName))
            {
                return templateName;
            }
        }

        return null;
    }

    private static string? ResolveGameOfThronesSharedTemplateName(
        string imageName,
        string slot,
        IReadOnlyDictionary<string, string> semanticTemplateNames,
        GameConfig? gameConfig)
    {
        if ((gameConfig ?? GameConfig.Current).Id != GameId.GameOfThrones ||
            semanticTemplateNames.Count == 0 ||
            !IsGameOfThronesSharedSourceName(imageName))
        {
            return null;
        }

        var semantic = ClassifySourceTextureSemantic(imageName, gameConfig);
        if (semantic is null)
        {
            return null;
        }

        var normalizedSlot = NormalizeTextureSlotName(slot);
        if (semanticTemplateNames.TryGetValue(SemanticKey(semantic, normalizedSlot), out var templateName))
        {
            return templateName;
        }

        foreach (var fallbackSemantic in FallbackSemantics(semantic))
        {
            if (semanticTemplateNames.TryGetValue(SemanticKey(fallbackSemantic, normalizedSlot), out templateName))
            {
                return templateName;
            }
        }

        return null;
    }

    private static bool IsGameOfThronesSharedSourceName(string name)
    {
        var stem = StripKnownTextureExtension(Path.GetFileName(name)).ToLowerInvariant();
        return stem is "map_1px_alpha" or "color_000" ||
               stem.StartsWith("sk_sharedparts", StringComparison.Ordinal) ||
               stem.StartsWith("bmap_sk_sharedparts", StringComparison.Ordinal);
    }

    private static bool IsNativeGameOfThronesSharedTextureName(string name, GameConfig? gameConfig)
    {
        if ((gameConfig ?? GameConfig.Current).Id != GameId.GameOfThrones)
        {
            return false;
        }

        var stem = StripKnownTextureExtension(Path.GetFileName(name)).ToLowerInvariant();
        return stem.StartsWith("sk_gotsharedparts", StringComparison.Ordinal) ||
               stem.StartsWith("bmap_sk_gotsharedparts", StringComparison.Ordinal);
    }

    private static IEnumerable<string> FallbackSemantics(string semantic)
    {
        if (semantic.Equals("eyelens", StringComparison.OrdinalIgnoreCase) ||
            semantic.Equals("eyespupil", StringComparison.OrdinalIgnoreCase))
        {
            yield return "eye";
        }

        if (semantic.Equals("teeth", StringComparison.OrdinalIgnoreCase))
        {
            yield return "mouth";
        }

        if (semantic.Equals("bodyupper", StringComparison.OrdinalIgnoreCase) ||
            semantic.Equals("bodylower", StringComparison.OrdinalIgnoreCase))
        {
            yield return "body";
        }
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
        if (gameConfig?.Id == GameId.GameOfThrones)
        {
            if (lower.Contains("bodyupper") || lower.Contains("body_upper") || lower.Contains("upperbody") || lower.Contains("upper_body"))
            {
                return "bodyupper";
            }

            if (lower.Contains("bodylower") || lower.Contains("body_lower") || lower.Contains("lowerbody") || lower.Contains("lower_body"))
            {
                return "bodylower";
            }

            if (lower.Contains("hands") || lower.Contains("_hand") || lower.Contains("hand_"))
            {
                return "hands";
            }

            if (lower.Contains("eyespupil") || lower.Contains("eye_pupil") || lower.Contains("pupil") || lower.Contains("color_000"))
            {
                return "eyespupil";
            }

            if (lower.Contains("eyelens") || lower.Contains("eye_lens") || lower.Contains("map_1px_alpha"))
            {
                return "eyelens";
            }
        }

        if (lower.Contains("eyelash") || lower.Contains("eyelashes"))
        {
            return "eyelashes";
        }

        if (lower.Contains("eye"))
        {
            return "eye";
        }

        if (lower.Contains("mouth"))
        {
            return "mouth";
        }

        if (lower.Contains("teeth") || lower.Contains("tooth"))
        {
            return "teeth";
        }

        if (lower.Contains("alphahair") || lower.Contains("hair"))
        {
            return "hair";
        }

        if (lower.Contains("face"))
        {
            return "face";
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
            templateMesh = D3DMeshParser.ParseFile(templateMeshPath);
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
            templateMesh = D3DMeshParser.ParseFile(templateMeshPath);
        }
        catch
        {
            return OriginalTextureNamePool.Empty;
        }

        var allNames = new List<string>();
        var bySlot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in TextureSlots)
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
               !stem.StartsWith("sk_gotsharedparts_", StringComparison.OrdinalIgnoreCase) &&
               !stem.StartsWith("bmap_sk_gotsharedparts_", StringComparison.OrdinalIgnoreCase) &&
               !stem.StartsWith("map_gradient", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameProvidedTexture(string? name)
        => TryGetGameProvidedTextureName(name, out _);

    private static bool TryGetGameProvidedTextureName(string? name, out string textureName)
    {
        textureName = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        textureName = StripKnownTextureExtension(Path.GetFileName(name));
        var stem = textureName.ToLowerInvariant();
        return stem.StartsWith("color_", StringComparison.Ordinal) ||
               stem.StartsWith("map_", StringComparison.Ordinal) ||
               stem.StartsWith("sk_sharedparts", StringComparison.Ordinal) ||
               stem.StartsWith("bmap_sk_sharedparts", StringComparison.Ordinal) ||
               stem.StartsWith("sk_gotsharedparts", StringComparison.Ordinal) ||
               stem.StartsWith("bmap_sk_gotsharedparts", StringComparison.Ordinal);
    }

    private static bool ShouldMapWithoutEmittingGameProvidedTexture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var stem = StripKnownTextureExtension(Path.GetFileName(name)).ToLowerInvariant();
        return stem.StartsWith("color_", StringComparison.Ordinal) ||
               stem.StartsWith("map_", StringComparison.Ordinal);
    }

    private static bool TryResolveGameProvidedTextureName(string templateMeshPath, string? name, out string textureName)
    {
        if (!TryGetGameProvidedTextureName(name, out textureName))
        {
            return false;
        }

        if (FindSourceTextureTemplate(templateMeshPath, textureName) is { } sourceTexturePath)
        {
            textureName = Path.GetFileNameWithoutExtension(sourceTexturePath);
        }

        return true;
    }

    private static bool CopyGameProvidedTextureIfAvailable(
        string templateMeshPath,
        string outputFolder,
        string textureName,
        List<string> writtenNames)
    {
        if (writtenNames.Contains(textureName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourceTexturePath = FindSourceTextureTemplate(templateMeshPath, textureName);
        if (sourceTexturePath is null)
        {
            return false;
        }

        var outputTexturePath = Path.Combine(outputFolder, textureName + ".d3dtx");
        if (!Path.GetFullPath(sourceTexturePath).Equals(Path.GetFullPath(outputTexturePath), StringComparison.OrdinalIgnoreCase))
        {
            D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(sourceTexturePath), outputTexturePath);
        }

        writtenNames.Add(textureName);
        return true;
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

        var canonical = Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                stem,
                StringComparison.OrdinalIgnoreCase));
        if (canonical is not null)
        {
            return canonical;
        }

        var exact = Path.Combine(folder, stem + ".d3dtx");
        return File.Exists(exact) ? exact : null;
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

    public static V25TextureReinserter.Result WriteV25ReferencedTextures(
        GltfModel model,
        string templateMeshPath,
        string outputMeshPath,
        bool forceUncompressed)
        => V25TextureReinserter.WriteReferencedTextures(model, templateMeshPath, outputMeshPath, forceUncompressed);

    public static int DistinctV25TextureCount(GltfModel model)
        => V25MaterialAssignment.DistinctTextureCount(model);

    // MCSM Season 2 (v45): writes the textures the geometry mapping selected — each template
    // diffuse name receives the image of the primitives assigned to its batch, so mesh and
    // textures can never disagree. Names without an image keep a copy of the original .d3dtx
    // (when it can be found) so the output folder previews complete.
    public static List<string> WriteV45AssignedTextures(
        IReadOnlyList<V45MeshReinserter.TextureAssignment> assignments,
        string templateMeshPath,
        string outputMeshPath,
        bool forceUncompressed)
    {
        var written = new List<string>();
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputMeshPath)) ?? ".";
        Directory.CreateDirectory(outputFolder);
        var meshFolder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));

        string? FindTemplateTexture(string diffuseName)
        {
            if (string.IsNullOrWhiteSpace(meshFolder))
            {
                return null;
            }

            var fileName = diffuseName.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
                ? diffuseName
                : diffuseName + ".d3dtx";
            var direct = Path.Combine(meshFolder, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            try
            {
                var scanRoot = Path.GetDirectoryName(meshFolder) ?? meshFolder;
                return Directory.EnumerateFiles(scanRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        foreach (var assignment in assignments)
        {
            var targetName = assignment.TemplateDiffuse.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(assignment.TemplateDiffuse)
                : assignment.TemplateDiffuse;
            var outputPath = Path.Combine(outputFolder, targetName + ".d3dtx");
            var templateTexture = FindTemplateTexture(targetName);

            if (assignment.Image is { } image && templateTexture is not null)
            {
                D3dtxWriter.WriteFromImageBytes(File.ReadAllBytes(templateTexture), image, outputPath, forceUncompressed);
                written.Add(targetName);
            }
            else if (templateTexture is not null &&
                     !File.Exists(outputPath) &&
                     !Path.GetFullPath(templateTexture).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                // Only seed a texture that nothing has written yet. Several parts can share one
                // diffuse name (the Lukas head reuses "_hair"), and overwriting here would restore
                // the template over the image another part already imported.
                File.Copy(templateTexture, outputPath, overwrite: false);
            }
        }

        return written;
    }

    private static ReinsertedTextures ToReinsertedTextures(V25TextureReinserter.Result result)
        => new(result.PrimitiveSlots, result.Written);

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

// Real template texture names the packed atlas reuses: the diffuse map name, the matching normal map name,
// and the matching detail/lines map name (when present). The diffuse/normal are real diffuse/normal names;
// the detail name is intentionally an existing lines/detail map so the separate detail atlas lands in the
// mesh's detail section with a detail-appropriate texture format.
public sealed record AtlasTextureNames(string Diffuse, string? Normal, string? Detail);

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

    // When true, re-encoded textures are written uncompressed (ARGB8) instead of DXT. Used for games
    // like Minecraft: Story Mode whose low-res character textures ship uncompressed.
    public bool ForceUncompressed { get; init; }

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

// ---- V25 material assignment moved from V25MaterialAssignment.cs ----

// Decides which template material slot each GLB part should use when reinserting a V25 mesh.
//
// The assignment is texture-aware, not positional: parts that share the same diffuse texture map to the
// same slot, and each distinct texture gets its own slot, capped at the number of slots the template
// provides (one per template submesh/material). This keeps each part's geometry bound to its own texture
// even when the part order or count differs from the template — a purely positional mapping would, for
// example, send a console's second "body" part to the "screen" material.
//
// Both the mesh reinserter (which writes each batch's material index) and the texture reinserter (which
// writes each part's image under the slot's bound texture name) call this so they stay in lockstep.
public static class V25MaterialAssignment
{
    // Returns, per GLB primitive, the template slot index in [0, slotCount). slotCount must be >= 1.
    public static int[] PrimitiveToSlot(GltfModel model, int slotCount)
    {
        var cap = Math.Max(0, slotCount - 1);
        var distinct = new List<string>();
        var result = new int[model.Primitives.Count];
        for (var k = 0; k < model.Primitives.Count; k++)
        {
            var name = DiffuseName(model.Primitives[k]) ?? $"__part{k}";
            var idx = distinct.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                idx = distinct.Count;
                distinct.Add(name);
            }

            result[k] = Math.Min(idx, cap);
        }

        return result;
    }

    // Number of distinct diffuse textures referenced across the GLB parts.
    public static int DistinctTextureCount(GltfModel model)
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prim in model.Primitives)
        {
            distinct.Add(DiffuseName(prim) ?? Guid.NewGuid().ToString());
        }

        return distinct.Count;
    }

    public static string? DiffuseName(GltfPrimitive primitive)
    {
        if (primitive.ReferencedTextures.TryGetValue("diffuse", out var image) && image is not null)
        {
            return StripExtension(image.Name);
        }

        return null;
    }

    private static string StripExtension(string name)
        => name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
}

// ---- V25 texture reinsertion moved from V25TextureReinserter.cs ----


// Texture reinsertion for The Walking Dead: Michonne (V25), following the same naming logic as the
// other games: every texture is written under the RECEIVER/template's own original slot name (diffuse,
// detail_diffuse, normal, ...), so an unmodified extract->reinsert reproduces the original names and the
// mesh's material bindings (left untouched) keep resolving. A brand-new name is only invented when the
// model brings more textures than the template's slots provide. The bake/lightmap is the one exception:
// the engine resolves it by the <meshname>_000 convention, so it is written under <output>_000.
//
// Each image is written into a copy of a real .d3dtx template. When the texture's OWN original file is
// found next to the mesh its exact format is preserved (so an A8 mask like eyelashes stays A8); only a
// fallback template (a brand-new texture) is steered away from A8 to avoid an all-black colour map.
public static class V25TextureReinserter
{
    public sealed class Result
    {
        public List<string> Written { get; } = [];
        public List<string> TemplateNotFound { get; } = [];
        public List<IReadOnlyDictionary<string, string>> PrimitiveSlots { get; } = [];
    }

    public static Result WriteReferencedTextures(
        GltfModel model,
        string templateMeshPath,
        string outputMeshPath,
        bool forceUncompressed)
    {
        var result = new Result();
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputMeshPath)) ?? ".";
        Directory.CreateDirectory(outputFolder);
        var meshFolder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        var fallbackTemplate = FindFallbackTemplate(meshFolder);
        var templateStem = Path.GetFileNameWithoutExtension(templateMeshPath);
        var outputStem = Path.GetFileNameWithoutExtension(outputMeshPath);
        var writtenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lightmapImages = CollectLightmapImages(model);

        var templateLightmap = FindTemplateByName(meshFolder, templateStem + "_000");
        var glbLightmap = FindLightmapImage(model);
        var lightmapTemplate = templateLightmap ?? fallbackTemplate;
        if (lightmapTemplate is not null && glbLightmap is not null)
        {
            var lightmapTarget = outputStem + "_000";
            var lightmapOutput = Path.Combine(outputFolder, lightmapTarget + ".d3dtx");
            D3dtxWriter.WriteFromImageBytes(File.ReadAllBytes(lightmapTemplate), glbLightmap, lightmapOutput, forceUncompressed);
            writtenTargets.Add(lightmapTarget);
            result.Written.Add(lightmapTarget);
            if (!string.IsNullOrWhiteSpace(glbLightmap.Name))
            {
                lightmapImages.Add(glbLightmap.Name);
            }
        }
        else if (templateLightmap is not null)
        {
            var lightmapTarget = outputStem + "_000";
            var lightmapOutput = Path.Combine(outputFolder, lightmapTarget + ".d3dtx");
            D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(templateLightmap), lightmapOutput);
            writtenTargets.Add(lightmapTarget);
            result.Written.Add(lightmapTarget);
        }

        // Diffuse naming follows the other games: the receiver part's own distinct diffuse names form a
        // pool, and each distinct GLB diffuse texture takes the next pool name. So a same-model reinsert
        // reproduces the originals, a foreign model with as many textures maps onto the receiver's slots
        // (e.g. console->couch uses the couch's names), and a foreign model with MORE textures than the
        // part has (e.g. Lee's head: stubble+eye+mouth onto Michonne's single head slot) keeps the extra
        // textures distinct under their own names instead of collapsing them all onto one slot. Other
        // slots (detail/normal) keep their own names.
        var templateSubs = SafeParseSubmeshes(templateMeshPath, meshFolder);
        var templatePool = new List<string>();
        foreach (var sub in templateSubs)
        {
            if (sub.TextureNames.TryGetValue("diffuse", out var dn) && !string.IsNullOrWhiteSpace(dn))
            {
                var stripped = StripTextureExtension(dn);
                if (!templatePool.Contains(stripped, StringComparer.OrdinalIgnoreCase))
                {
                    templatePool.Add(stripped);
                }
            }
        }

        var glbDistinct = new List<string>();
        foreach (var primitive in model.Primitives)
        {
            var name = V25MaterialAssignment.DiffuseName(primitive);
            if (!string.IsNullOrWhiteSpace(name) && !glbDistinct.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                glbDistinct.Add(name);
            }
        }

        // A GLB texture whose name already matches a template slot keeps that name (same-model / shared
        // texture, by IDENTITY not position — robust to primitive reordering). A foreign texture takes the
        // next template slot no GLB texture matches; once those run out it keeps its own name.
        var glbSet = new HashSet<string>(glbDistinct, StringComparer.OrdinalIgnoreCase);
        var freePool = templatePool.Where(p => !glbSet.Contains(p)).ToList();
        var freeIdx = 0;
        var diffuseTargetByImage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in glbDistinct)
        {
            if (templatePool.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                diffuseTargetByImage[name] = name;
            }
            else
            {
                diffuseTargetByImage[name] = freeIdx < freePool.Count ? freePool[freeIdx++] : name;
            }
        }

        for (var k = 0; k < model.Primitives.Count; k++)
        {
            var primitive = model.Primitives[k];
            var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (slot, image) in EnumerateTextureSlots(primitive))
            {
                if (image is null || string.IsNullOrWhiteSpace(image.Name) ||
                    IsGeneratedHelper(image.Name) || IsLightmapSlot(slot) ||
                    lightmapImages.Contains(image.Name) || slots.ContainsKey(slot))
                {
                    continue;
                }

                // The diffuse takes its pooled target (receiver slot name or, beyond the pool, its own);
                // all other slots keep the GLB's own original name.
                var ownName = StripTextureExtension(image.Name);
                var target = slot.Equals("diffuse", StringComparison.OrdinalIgnoreCase) &&
                             diffuseTargetByImage.TryGetValue(ownName, out var mapped)
                    ? mapped
                    : ownName;

                slots[slot] = target;
                if (!writtenTargets.Add(target))
                {
                    continue;
                }

                // The texture's own original file gives the exact format (A8 masks stay A8); a fallback
                // template is steered away from A8 so a brand-new colour map never lands all black.
                var ownTemplate = FindTemplateByName(meshFolder, target);
                var template = ownTemplate ?? fallbackTemplate;
                if (template is null)
                {
                    result.TemplateNotFound.Add(target);
                    continue;
                }

                var outputPath = Path.Combine(outputFolder, target + ".d3dtx");
                D3dtxWriter.WriteFromImageBytes(
                    File.ReadAllBytes(template),
                    image,
                    outputPath,
                    forceUncompressed,
                    allowA8TemplateFormat: ownTemplate is not null);
                result.Written.Add(target);
            }

            result.PrimitiveSlots.Add(slots);
        }

        return result;
    }

    // The mesh's texture slots come through either map depending on the exporter path; union them so
    // diffuse, detail_diffuse, normal etc. are all written, preferring the resolved ReferencedTextures.
    private static IEnumerable<(string Slot, GltfImage? Image)> EnumerateTextureSlots(GltfPrimitive primitive)
    {
        foreach (var pair in primitive.ReferencedTextures)
        {
            yield return (pair.Key, pair.Value);
        }

        foreach (var pair in primitive.TextureSlots)
        {
            if (!primitive.ReferencedTextures.ContainsKey(pair.Key))
            {
                yield return (pair.Key, pair.Value);
            }
        }
    }

    private static IReadOnlyList<SubmeshData> SafeParseSubmeshes(string templateMeshPath, string? meshFolder)
    {
        try
        {
            // Resolve texture hashes against the actual .d3dtx files so the names keep their original
            // case (the embedded hash DB only knows the lowercase form the CRC64 is computed from).
            using (Core.TextureHashDatabase.UseTextureFolder(meshFolder))
            {
                return D3DMeshParser.Parse(File.ReadAllBytes(templateMeshPath)).Submeshes;
            }
        }
        catch
        {
            return [];
        }
    }

    private static string? FindTemplateByName(string? meshFolder, string stem)
    {
        if (meshFolder is null || !Directory.Exists(meshFolder))
        {
            return null;
        }

        return Directory.EnumerateFiles(meshFolder, "*.d3dtx", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path), stem, StringComparison.OrdinalIgnoreCase));
    }

    // Any .d3dtx next to the mesh works as a format/container template when the texture's own file is
    // absent (e.g. a brand-new texture from a replacement model). Prefer a larger one (a real diffuse,
    // not a tiny bake) so mip layout is representative.
    private static string? FindFallbackTemplate(string? meshFolder)
    {
        if (meshFolder is null || !Directory.Exists(meshFolder))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(meshFolder, "*.d3dtx", SearchOption.TopDirectoryOnly).ToList();
        return files
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("_000", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => new FileInfo(p).Length)
            .FirstOrDefault()
            ?? files.OrderByDescending(p => new FileInfo(p).Length).FirstOrDefault();
    }

    private static HashSet<string> CollectLightmapImages(GltfModel model)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitive in model.Primitives)
        {
            foreach (var (slot, image) in primitive.ReferencedTextures)
            {
                if (image is not null && IsLightmapSlot(slot) && !string.IsNullOrWhiteSpace(image.Name))
                {
                    result.Add(image.Name);
                    result.Add(StripTextureExtension(image.Name));
                }
            }

            foreach (var (slot, image) in primitive.TextureSlots)
            {
                if (image is not null && IsLightmapSlot(slot) && !string.IsNullOrWhiteSpace(image.Name))
                {
                    result.Add(image.Name);
                    result.Add(StripTextureExtension(image.Name));
                }
            }
        }

        return result;
    }

    private static GltfImage? FindLightmapImage(GltfModel model)
    {
        foreach (var primitive in model.Primitives)
        {
            foreach (var slot in new[] { "bake", "lightmap", "light_map", "lighting", "lighting_map", "occlusion" })
            {
                if (primitive.TextureSlots.TryGetValue(slot, out var texture) ||
                    primitive.ReferencedTextures.TryGetValue(slot, out texture))
                {
                    return texture;
                }
            }
        }

        return null;
    }

    private static string StripTextureExtension(string name)
        => name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    private static bool IsLightmapSlot(string slot)
        => slot is "bake" or "lightmap" or "light_map" or "lighting" or "lighting_map";

    private static bool IsGeneratedHelper(string name)
        => name.Contains("_atlas", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("gltf_", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("__tt_lines", StringComparison.OrdinalIgnoreCase);
}
