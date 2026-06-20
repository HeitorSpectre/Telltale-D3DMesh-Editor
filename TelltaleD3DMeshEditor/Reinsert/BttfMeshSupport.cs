using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Reinsert;

// Routing helpers for the Back to the Future (.d3dmesh version 1, "ERTM") reinsertion path. The V13/17
// pipeline (D3DMeshLayout/MeshReinserter) cannot read v1, so callers check IsBackToTheFutureMesh first
// and dispatch geometry to BttfMeshReinserter. Texture writing is shared: ReinsertTextureService and
// D3dtxWriter already handle the ERTM .d3dtx container.
public static class BttfMeshSupport
{
    // True when the bytes are a Telltale "ERTM"/"MTRE" container holding a version-1 mesh (Back to the
    // Future). Reads the version field after the embedded name, mirroring D3DMeshParser's header walk.
    public static bool IsBackToTheFutureMesh(byte[] data)
    {
        try
        {
            if (data.Length < 8)
            {
                return false;
            }

            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic is not ("ERTM" or "MTRE"))
            {
                return false;
            }

            var header = Core.MetaStreamHeader.Parse(data);
            var o = header.DataOffset;
            if (o + 12 > data.Length)
            {
                return false;
            }

            var nameHeaderLength = BitConverter.ToUInt32(data, o);
            var nameLength = BitConverter.ToUInt32(data, o + 4);
            o += 8;
            if (nameLength > nameHeaderLength)
            {
                o -= 4;
                nameLength = nameHeaderLength;
            }

            o += checked((int)nameLength);
            if (o + 4 > data.Length)
            {
                return false;
            }

            return BitConverter.ToInt32(data, o) == 1;
        }
        catch
        {
            return false;
        }
    }

    // Builds the v1 layout and rewrites geometry from the imported model. For skinned meshes pass the GLB
    // skeleton so the rebuilt bone palettes resolve the model's joints. Returns the new .d3dmesh bytes;
    // the caller writes textures separately through the shared ReinsertTextureService and the .skl through
    // BttfSkeletonWriter.
    public static byte[] ReinsertGeometry(byte[] templateBytes, GltfModel model, SkeletonData? skeleton = null)
    {
        var layout = BackToTheFutureMeshParser.BuildLayout(templateBytes);
        var groups = MapPrimitivesByPart(templateBytes, model, skeleton, layout.Submeshes.Count);
        return BttfMeshReinserter.Reinsert(layout, model, groups, skeleton);
    }

    // Part-aware, palette-size-aware mapping of GLB primitives onto the template submeshes. Shared by the
    // geometry writer and the texture writer (both compute it identically) so geometry and textures land
    // on the same submeshes, and so no submesh's rebuilt bone palette exceeds the template's per-draw bone
    // limit (which would otherwise stretch part of a skinned model into spikes in-game).
    private static List<List<GltfPrimitive>> MapPrimitivesByPart(byte[] templateBytes, GltfModel model, SkeletonData? skeleton, int submeshCount)
    {
        var diffuseNames = GetTemplateDiffuseNames(templateBytes, submeshCount);
        var maxBones = GetMaxPaletteBones(templateBytes);
        if (maxBones <= 0)
        {
            maxBones = BttfPrimitiveSplitter.HardPaletteLimit;
        }
        var primitives = BttfPrimitiveSplitter.SplitByBonePalette(model.Primitives, model, skeleton, maxBones);
        var boneCache = new Dictionary<GltfPrimitive, IReadOnlyCollection<ulong>>();

        IReadOnlyCollection<ulong> BonesOf(GltfPrimitive prim)
        {
            if (!boneCache.TryGetValue(prim, out var bones))
            {
                bones = BttfSkinning.GetWeightedBoneHashes(prim, model, skeleton);
                boneCache[prim] = bones;
            }

            return bones;
        }

        return BttfPartMapper.Map(diffuseNames, primitives, BonesOf, maxBones);
    }

    // Largest bone palette in the template = the engine's per-draw bone limit for this asset.
    private static int GetMaxPaletteBones(byte[] templateBytes)
    {
        try
        {
            var palettes = D3DMeshParser.Parse(templateBytes).BonePalettes;
            return palettes.Count > 0 ? palettes.Max(palette => palette.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static List<string?> GetTemplateDiffuseNames(byte[] templateBytes, int submeshCount)
    {
        var names = new List<string?>(submeshCount);
        try
        {
            var mesh = D3DMeshParser.Parse(templateBytes);
            for (var i = 0; i < submeshCount; i++)
            {
                names.Add(i < mesh.Submeshes.Count && mesh.Submeshes[i].TextureNames.TryGetValue("diffuse", out var name)
                    ? name
                    : null);
            }
        }
        catch
        {
            for (var i = names.Count; i < submeshCount; i++)
            {
                names.Add(null);
            }
        }

        return names;
    }

    // Writes the imported model's textures under the TEMPLATE submeshes' own slot names, using the same
    // part-aware mapping as the geometry. For each template submesh, the primitive mapped to it supplies
    // each slot's image (diffuse, bump, specular, …) which is written under the template's name for that
    // slot. Because Back to the Future binds textures by name, this makes every part show the imported
    // model's matching texture in-game without editing the mesh's texture references. The "bake" slot is
    // left to the separate inherited-bake handling. Returns the number of .d3dtx files written.
    public static int WriteAlignedTextures(string templateMeshPath, string outputMeshPath, GltfModel model, bool uncompressed)
    {
        byte[] templateBytes;
        MeshData mesh;
        try
        {
            templateBytes = File.ReadAllBytes(templateMeshPath);
            mesh = D3DMeshParser.Parse(templateBytes);
        }
        catch
        {
            return 0;
        }

        var groups = MapPrimitivesByPart(templateBytes, model, model.Skeleton, mesh.Submeshes.Count);

        var meshDir = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath)) ?? ".";
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputMeshPath)) ?? ".";
        var fallbackTemplate = Directory.EnumerateFiles(meshDir, "*.d3dtx").FirstOrDefault();
        var writtenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;

        for (var s = 0; s < mesh.Submeshes.Count && s < groups.Count; s++)
        {
            if (groups[s].Count == 0)
            {
                continue;
            }

            var prim = groups[s][0];
            foreach (var (slot, textureName) in mesh.Submeshes[s].TextureNames)
            {
                if (slot.Equals("bake", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(textureName))
                {
                    continue;
                }

                if (!BttfPartMapper.TryGetSlotImage(prim, slot, out var image))
                {
                    continue;
                }

                var fileName = textureName.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? textureName : textureName + ".d3dtx";
                if (!writtenNames.Add(fileName))
                {
                    continue; // each distinct template name is written once (shared across submeshes)
                }

                var sourceTexture = ResolveBakeTemplate(meshDir, fileName) ?? fallbackTemplate;
                if (sourceTexture is null)
                {
                    continue;
                }

                try
                {
                    D3dtxWriter.WriteFromImageBytes(File.ReadAllBytes(sourceTexture), image, Path.Combine(outputDir, fileName), uncompressed);
                    written++;
                }
                catch
                {
                    // Skip a texture that cannot be encoded rather than failing the whole reimport.
                }
            }
        }

        return written;
    }

    // True when the template mesh is skinned (has bone palettes / weight buffers), so callers know to
    // rebuild the .skl alongside it.
    public static bool IsSkinnedMesh(byte[] templateBytes)
    {
        try
        {
            return BackToTheFutureMeshParser.BuildLayout(templateBytes).IsSkinned;
        }
        catch
        {
            return false;
        }
    }

    // Overrides every "bake" lightmap the template references with a flat white texture, written under
    // the slot's own name next to the output mesh. Back to the Future multiplies diffuse x bake in-game,
    // so an imported model that inherits the original prop's dark baked lighting renders black (the tool's
    // viewer shows diffuse only, hiding the problem). A white bake is a multiply-neutral replacement, so
    // the new model shows its real diffuse. The bake .d3dtx is named after the prop, so it ships with the
    // mod and overrides the dark one. Returns the number of bake textures written.
    public static int NeutralizeInheritedBake(string templateMeshPath, string outputMeshPath)
    {
        MeshData mesh;
        try
        {
            mesh = D3DMeshParser.ParseFile(templateMeshPath);
        }
        catch
        {
            return 0;
        }

        var bakeNames = mesh.Submeshes
            .Select(submesh => submesh.TextureNames.TryGetValue("bake", out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (bakeNames.Count == 0)
        {
            return 0;
        }

        var meshDir = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath)) ?? ".";
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputMeshPath)) ?? ".";
        var white = BuildSolidColorPng(Color.White, 4);
        var written = 0;
        foreach (var bakeName in bakeNames)
        {
            var fileName = bakeName!.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? bakeName : bakeName + ".d3dtx";
            var templateTexture = ResolveBakeTemplate(meshDir, fileName);
            if (templateTexture is null)
            {
                continue;
            }

            try
            {
                var image = new GltfImage { Name = Path.GetFileNameWithoutExtension(fileName), Data = white };
                D3dtxWriter.WriteFromImageBytes(File.ReadAllBytes(templateTexture), image, Path.Combine(outputDir, fileName));
                written++;
            }
            catch
            {
                // A bake we cannot rewrite is left to the game's original; skip it rather than fail reimport.
            }
        }

        return written;
    }

    // True when the imported model declares a "bake" texture slot, i.e. it brought its own bake (e.g. a
    // model extracted from the game by this tool, whose material extras preserve the bake binding). In
    // that case the bake is kept; only an inherited bake on an unrelated/external model is removed. This
    // looks at the model's declared slots rather than what was written, because a round-tripped game model
    // keeps the bake binding even when the bake pixels are loaded from the game's own archive.
    public static bool ModelDeclaresBake(GltfModel model)
        => model.Primitives.Any(primitive =>
            primitive.TextureSlots.Keys.Any(key => key.Equals("bake", StringComparison.OrdinalIgnoreCase)) ||
            primitive.ReferencedTextures.Keys.Any(key => key.Equals("bake", StringComparison.OrdinalIgnoreCase)));

    // Variant B (test): instead of overriding the inherited bake with a neutral texture, break the mesh's
    // reference to it so the game never loads the original dark lightmap. The bake name is replaced
    // in place by a same-length placeholder that resolves to no texture, keeping every offset/size field
    // byte-identical (structurally safe). No bake .d3dtx is shipped. Returns the modified bytes and how
    // many name occurrences were replaced.
    public static (byte[] Bytes, int Replaced) BreakInheritedBakeReference(byte[] meshBytes, string templateMeshPath)
    {
        var bakeNames = GetBakeTextureNames(templateMeshPath);
        if (bakeNames.Count == 0)
        {
            return (meshBytes, 0);
        }

        var result = (byte[])meshBytes.Clone();
        var replaced = 0;
        foreach (var bakeName in bakeNames)
        {
            var needle = Encoding.ASCII.GetBytes(bakeName);
            var placeholder = BuildMissingTexturePlaceholder(bakeName.Length);
            var index = 0;
            while ((index = IndexOf(result, needle, index)) >= 0)
            {
                Array.Copy(placeholder, 0, result, index, placeholder.Length);
                replaced++;
                index += placeholder.Length;
            }
        }

        return (result, replaced);
    }

    private static List<string> GetBakeTextureNames(string templateMeshPath)
    {
        try
        {
            return D3DMeshParser.ParseFile(templateMeshPath).Submeshes
                .Select(submesh => submesh.TextureNames.TryGetValue("bake", out var name) ? name : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? name : name + ".d3dtx")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // A same-length, definitely-absent texture name ("removed_bake_…", padded, keeping the .d3dtx tail).
    private static byte[] BuildMissingTexturePlaceholder(int length)
    {
        const string ext = ".d3dtx";
        var stemLength = Math.Max(0, length - ext.Length);
        var padded = "removed_inherited_bake_" + new string('_', stemLength);
        var name = padded[..stemLength] + ext;
        return Encoding.ASCII.GetBytes(name);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // Prefers the bake's own .d3dtx (for an exact ERTM header match); otherwise reuses any sibling .d3dtx
    // next to the template mesh just for its header, since only flat white pixels are written.
    private static string? ResolveBakeTemplate(string meshDir, string bakeFileName)
    {
        var exact = Path.Combine(meshDir, bakeFileName);
        if (File.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateFiles(meshDir, "*.d3dtx").FirstOrDefault();
    }

    private static byte[] BuildSolidColorPng(Color color, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    // Re-parses the produced bytes and confirms the deterministic walk closes exactly at EOF.
    public static bool VerifyClosesAtEof(byte[] producedBytes)
    {
        try
        {
            return BackToTheFutureMeshParser.BuildLayout(producedBytes).ClosesAtEof;
        }
        catch
        {
            return false;
        }
    }
}
