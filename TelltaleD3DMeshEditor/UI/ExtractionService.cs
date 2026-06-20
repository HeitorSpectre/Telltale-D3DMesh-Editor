using System.Drawing.Imaging;
using System.Text;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Export;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleD3DMeshEditor.Formats.Texture;

namespace TelltaleD3DMeshEditor.UI;

// Output format selected by the user through the toolbar toggle.
public enum ExportFormat
{
    // Everything in one .glb file (mesh + textures + skeleton embedded). Self-contained.
    Glb,
    // Separate .gltf (JSON) + .bin + .png files. Useful for inspection/editing.
    GltfSeparate,
}

// Orchestrates full asset extraction: parses the .d3dmesh, resolves/exports textures
// (.d3dtx), parses the matching .skl, and writes the selected format (GLB or GLTF + files).
public static class ExtractionService
{
    private static readonly string[] SupportedTextureExtensions = [".d3dtx", ".dds", ".png"];

    public sealed record PreviewAsset(MeshData Mesh, SkeletonData? Skeleton, Dictionary<int, MaterialTextureSet> Textures);

    public static string ExtractAsset(ModelAsset asset, string inputRoot, string outputRoot, ExportFormat format)
    {
        Directory.CreateDirectory(outputRoot);
        var extension = format == ExportFormat.Glb ? ".glb" : ".gltf";
        var outputPath = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(asset.MeshPath) + extension);
        return ExtractAssetToPath(asset, inputRoot, outputPath, format);
    }

    public static string ExtractAssetToPath(ModelAsset asset, string inputRoot, string outputPath, ExportFormat format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        WriteCompleteAsset(asset, inputRoot, outputPath, format);
        return outputPath;
    }

    public static string ExtractAssetGroup(ModelAssetGroup group, string inputRoot, string outputRoot, ExportFormat format)
    {
        Directory.CreateDirectory(outputRoot);
        var extension = format == ExportFormat.Glb ? ".glb" : ".gltf";
        var outputPath = Path.Combine(outputRoot, group.OutputStem + extension);
        return ExtractAssetGroupToPath(group, inputRoot, outputPath, format);
    }

    public static string ExtractAssetGroupToPath(ModelAssetGroup group, string inputRoot, string outputPath, ExportFormat format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        WriteCompleteAssetGroup(group, inputRoot, outputPath, format);
        return outputPath;
    }

    public static PreviewAsset BuildPreviewAsset(ModelAssetGroup group, string inputRoot)
    {
        var combined = BuildCombinedAsset(group, inputRoot);
        return new PreviewAsset(combined.Mesh, combined.Skeleton, combined.Textures);
    }

    public static string ExtractAll(
        IReadOnlyList<ModelAsset> assets,
        string inputRoot,
        string outputRoot,
        ExportFormat format,
        IProgress<string>? progress)
    {
        Directory.CreateDirectory(outputRoot);
        var ok = 0;
        var failed = 0;
        var reportPath = Path.Combine(outputRoot, "extraction.tsv");
        using var report = new StreamWriter(reportPath, false, new UTF8Encoding(false));
        report.WriteLine("status\tmesh\toutput\tdetails");

        var usedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.OrderBy(asset => asset.MeshPath, StringComparer.OrdinalIgnoreCase))
        {
            var relativeMesh = Path.GetRelativePath(inputRoot, asset.MeshPath);
            var extension = format == ExportFormat.Glb ? ".glb" : ".gltf";
            var outputPath = GetUniqueOutputPath(outputRoot, asset.MeshPath, extension, usedOutputPaths);
            try
            {
                WriteCompleteAsset(asset, inputRoot, outputPath, format);
                ok++;
                progress?.Report($"OK {relativeMesh}");
                WriteReport(report, "OK", asset.MeshPath, outputPath, "mesh + textures + skeleton (when available)");
            }
            catch (Exception ex)
            {
                failed++;
                progress?.Report($"FAIL {relativeMesh}: {ex.Message}");
                WriteReport(report, "FAIL", asset.MeshPath, outputPath, ex.ToString());
            }
        }

        return string.Join(Environment.NewLine,
            $"Output: {outputRoot}",
            $"Report: {reportPath}",
            $"Assets OK: {ok}/{assets.Count}, failed {failed}");
    }

    private static string GetUniqueOutputPath(
        string outputRoot,
        string meshPath,
        string extension,
        HashSet<string> usedOutputPaths)
    {
        var stem = Path.GetFileNameWithoutExtension(meshPath);
        var candidate = Path.Combine(outputRoot, stem + extension);
        var suffix = 2;
        while (!usedOutputPaths.Add(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(outputRoot, $"{stem}_{suffix++}{extension}");
        }

        return candidate;
    }

    private static void WriteCompleteAsset(ModelAsset asset, string inputRoot, string outputPath, ExportFormat format)
    {
        var mesh = WithSourceMetadata(D3DMeshParser.ParseFile(asset.MeshPath), inputRoot, asset.MeshPath);
        var textureSets = TextureResolver.ResolveForMesh(inputRoot, asset.MeshPath, mesh);
        // Faithful export: diffuse and lines/detail textures remain separate, matching Telltale materials.
        var baseColorByName = BaseColorExporter.BuildRawDiffuse(mesh, textureSets);
        var auxiliaryTextures = BuildAuxiliaryTexturePngsForExport(mesh, inputRoot, asset.MeshPath, baseColorByName.Keys);

        var skeleton = LoadOptionalSkeleton(asset.SkeletonPath, mesh.Version);

        if (format == ExportFormat.Glb)
        {
            GltfWriter.WriteCompleteAssetGlb(mesh, skeleton, baseColorByName, outputPath, auxiliaryTextures);
        }
        else
        {
            GltfSeparateWriter.WriteCompleteAssetGltf(mesh, skeleton, baseColorByName, outputPath, auxiliaryTextures);
        }
    }

    private static MeshData WithSourceMetadata(MeshData mesh, string inputRoot, string meshPath)
    {
        var sourceMesh = Path.GetRelativePath(inputRoot, meshPath);
        var result = new MeshData
        {
            Name = mesh.Name,
            Version = mesh.Version,
        };
        foreach (var palette in mesh.BonePalettes)
        {
            result.BonePalettes.Add((ulong[])palette.Clone());
        }

        for (var i = 0; i < mesh.Submeshes.Count; i++)
        {
            var source = mesh.Submeshes[i];
            var copy = new SubmeshData
            {
                Name = source.Name,
                MaterialName = source.MaterialName,
                BonePaletteIndex = source.BonePaletteIndex,
                RigidBoneIndex = source.RigidBoneIndex,
                SourceMeshPath = string.IsNullOrWhiteSpace(source.SourceMeshPath) ? sourceMesh : source.SourceMeshPath,
                SourceSubmeshIndex = source.SourceSubmeshIndex >= 0 ? source.SourceSubmeshIndex : i,
            };
            foreach (var texture in source.TextureNames)
            {
                copy.TextureNames[texture.Key] = texture.Value;
            }

            copy.Vertices.AddRange(source.Vertices);
            copy.Faces.AddRange(source.Faces);
            result.Submeshes.Add(copy);
        }

        return result;
    }

    private static void WriteCompleteAssetGroup(ModelAssetGroup group, string inputRoot, string outputPath, ExportFormat format)
    {
        var combined = BuildCombinedAsset(group, inputRoot);
        var baseColorByName = BaseColorExporter.BuildRawDiffuse(combined.Mesh, combined.Textures);
        var auxiliaryTextures = BuildAuxiliaryTexturePngsForExport(
            combined.Mesh,
            inputRoot,
            group.Assets[0].MeshPath,
            baseColorByName.Keys);

        if (format == ExportFormat.Glb)
        {
            GltfWriter.WriteCompleteAssetGlb(combined.Mesh, combined.Skeleton, baseColorByName, outputPath, auxiliaryTextures);
        }
        else
        {
            GltfSeparateWriter.WriteCompleteAssetGltf(combined.Mesh, combined.Skeleton, baseColorByName, outputPath, auxiliaryTextures);
        }
    }

    private static CombinedAsset BuildCombinedAsset(ModelAssetGroup group, string inputRoot)
    {
        if (group.Assets.Count == 0)
        {
            throw new InvalidDataException("Combined group has no mesh parts.");
        }

        var parsed = group.Assets
            .Select(asset =>
            {
                var mesh = D3DMeshParser.ParseFile(asset.MeshPath);
                var textures = TextureResolver.ResolveForMesh(inputRoot, asset.MeshPath, mesh);
                return new ParsedPart(asset, mesh, textures);
            })
            .ToList();

        var combinedMesh = new MeshData
        {
            Name = group.OutputStem,
            Version = parsed[0].Mesh.Version,
        };
        var combinedTextures = new Dictionary<int, MaterialTextureSet>();

        // Decal wounds share the clean skin's surface; nudge them out along their normals so they win
        // the depth test (in the viewer and exports) instead of being hidden by the clean part beneath.
        // They are stacked by size: a whole-face layer (e.g. headDamageBaseBloody) gets the smallest nudge
        // and local wounds (a cut, a gash) get a larger one, so the small wounds render on top of it
        // instead of fighting it — which is what made "fully damaged" hide most wounds.
        var decalOffsets = BuildDecalOffsets(parsed, group.DecalOffsetMeshPaths);

        foreach (var part in parsed)
        {
            decalOffsets.TryGetValue(Path.GetFullPath(part.Asset.MeshPath), out var partDecalOffset);
            var paletteMap = new int[part.Mesh.BonePalettes.Count];
            for (var i = 0; i < part.Mesh.BonePalettes.Count; i++)
            {
                paletteMap[i] = combinedMesh.BonePalettes.Count;
                combinedMesh.BonePalettes.Add((ulong[])part.Mesh.BonePalettes[i].Clone());
            }

            var sourceMesh = Path.GetRelativePath(inputRoot, part.Asset.MeshPath);
            var sourceStem = Path.GetFileNameWithoutExtension(part.Asset.MeshPath);
            for (var s = 0; s < part.Mesh.Submeshes.Count; s++)
            {
                var source = part.Mesh.Submeshes[s];
                var paletteIndex = source.BonePaletteIndex >= 0 && source.BonePaletteIndex < paletteMap.Length
                    ? paletteMap[source.BonePaletteIndex]
                    : source.BonePaletteIndex;
                var copy = new SubmeshData
                {
                    Name = $"{sourceStem}/{source.Name}",
                    MaterialName = source.MaterialName,
                    BonePaletteIndex = paletteIndex,
                    RigidBoneIndex = source.RigidBoneIndex,
                    SourceMeshPath = sourceMesh,
                    SourceSubmeshIndex = s,
                };

                foreach (var texture in source.TextureNames)
                {
                    copy.TextureNames[texture.Key] = texture.Value;
                }
                copy.Vertices.AddRange(partDecalOffset > 0f
                    ? source.Vertices.Select(vertex => OffsetAlongNormal(vertex, partDecalOffset))
                    : source.Vertices);
                copy.Faces.AddRange(source.Faces);

                var combinedSubmeshIndex = combinedMesh.Submeshes.Count;
                combinedMesh.Submeshes.Add(copy);
                if (part.Textures.TryGetValue(s, out var textureSet))
                {
                    combinedTextures[combinedSubmeshIndex] = textureSet;
                }
            }
        }

        var skeleton = LoadOptionalSkeleton(
            File.Exists(group.SkeletonPath) ? group.SkeletonPath : null,
            combinedMesh.Version);

        return new CombinedAsset(combinedMesh, skeleton, combinedTextures);
    }

    private static SkeletonData? LoadOptionalSkeleton(string? skeletonPath, int meshVersion)
    {
        if (string.IsNullOrWhiteSpace(skeletonPath))
        {
            return null;
        }

        try
        {
            return SkeletonLoader.Load(skeletonPath, meshVersion);
        }
        catch when (GameConfig.Current.IsBackToTheFuture)
        {
            return null;
        }
        // The Tales from the Borderlands source leak ships static props (carTannen, gunGlock, handcuffs)
        // with .skl files in an unrecognised layout. They carry no skinning, so a skeleton failure should
        // not block the mesh+texture export — fall back to a skeleton-less asset.
        catch when (GameConfig.Current.Id == GameId.TalesFromTheBorderlandsOld)
        {
            return null;
        }
    }

    // Per-part outward nudge for decal wounds, keyed by full mesh path. The base nudge is a small
    // fraction of the model size (enough to clear the viewer's depth-tie epsilon, small enough to still
    // read as sitting on the skin). Parts are then ranked by area: the largest decal (a whole-face layer)
    // gets the base nudge and the smallest (a local wound) gets up to double, so local wounds stack on
    // top of full-face layers instead of z-fighting them.
    private static Dictionary<string, float> BuildDecalOffsets(
        IReadOnlyList<ParsedPart> parsed,
        IReadOnlySet<string> decalMeshPaths)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (decalMeshPaths.Count == 0)
        {
            return result;
        }

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var part in parsed)
        {
            var b = part.Mesh.GetBounds();
            minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY); minZ = Math.Min(minZ, b.MinZ);
            maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY); maxZ = Math.Max(maxZ, b.MaxZ);
        }

        if (maxX < minX)
        {
            return result;
        }

        var diagonal = MathF.Sqrt(
            (maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
        var baseOffset = 0.001f * diagonal;

        var decals = parsed
            .Where(part => decalMeshPaths.Contains(Path.GetFullPath(part.Asset.MeshPath)))
            .OrderByDescending(part => BoundsVolume(part.Mesh.GetBounds()))
            .ToList();
        for (var i = 0; i < decals.Count; i++)
        {
            var fraction = decals.Count <= 1 ? 0f : (float)i / (decals.Count - 1);
            result[Path.GetFullPath(decals[i].Asset.MeshPath)] = baseOffset * (1f + fraction);
        }

        return result;
    }

    private static float BoundsVolume((float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) b)
    {
        return Math.Max(0f, b.MaxX - b.MinX) * Math.Max(0f, b.MaxY - b.MinY) * Math.Max(0f, b.MaxZ - b.MinZ);
    }

    private static VertexData OffsetAlongNormal(VertexData vertex, float offset)
    {
        var length = MathF.Sqrt(vertex.Nx * vertex.Nx + vertex.Ny * vertex.Ny + vertex.Nz * vertex.Nz);
        if (length < 1e-6f)
        {
            return vertex;
        }

        var scale = offset / length;
        return vertex with
        {
            X = vertex.X + vertex.Nx * scale,
            Y = vertex.Y + vertex.Ny * scale,
            Z = vertex.Z + vertex.Nz * scale,
        };
    }

    // Auxiliary PNGs: for every mesh-referenced texture that exists on disk (.d3dtx/.dds/.png)
    // and was not already exported as baseColor/diffuse, decode it and include it as an extra image.
    internal static Dictionary<string, byte[]> BuildAuxiliaryTexturePngsForExport(
        MeshData mesh,
        string inputRoot,
        string meshPath,
        IEnumerable<string> alreadyEmbedded)
    {
        var filesByStem = BuildTextureFileIndex(inputRoot, meshPath);
        var skip = new HashSet<string>(alreadyEmbedded, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var textureName in GetReferencedTextureNames(mesh))
        {
            if (skip.Contains(textureName) || result.ContainsKey(textureName) ||
                !filesByStem.TryGetValue(textureName, out var path))
            {
                continue;
            }

            try
            {
                var texture = TextureLoader.Load(path);
                using var bitmap = texture.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                result[textureName] = ms.ToArray();
            }
            catch
            {
                // Optional auxiliary texture: if decoding fails, just skip it.
            }
        }

        return result;
    }

    private static Dictionary<string, string> BuildTextureFileIndex(string inputRoot, string meshPath)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        var meshDir = Path.GetDirectoryName(meshPath);
        if (!string.IsNullOrEmpty(meshDir))
        {
            roots.Add(meshDir);
        }
        if (Directory.Exists(inputRoot))
        {
            roots.Add(inputRoot);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!SupportedTextureExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stem = Stem(path);
                index.TryAdd(stem, path);
            }
        }

        return index;
    }

    private static IEnumerable<string> GetReferencedTextureNames(MeshData mesh)
    {
        return mesh.Submeshes
            .SelectMany(submesh => submesh.TextureNames.Values)
            .Select(Stem)
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                           !name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase) &&
                           !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
    }

    private static string Stem(string pathOrName)
    {
        var stem = Path.GetFileNameWithoutExtension(pathOrName);
        while (stem.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }

        return stem;
    }

    private static void WriteReport(StreamWriter report, string status, string mesh, string output, string details)
    {
        report.WriteLine(string.Join('\t',
            status,
            mesh,
            output,
            details.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ")));
    }

    private sealed record ParsedPart(ModelAsset Asset, MeshData Mesh, Dictionary<int, MaterialTextureSet> Textures);
    private sealed record CombinedAsset(MeshData Mesh, SkeletonData? Skeleton, Dictionary<int, MaterialTextureSet> Textures);
}
