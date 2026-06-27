using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Numerics;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleD3DMeshEditor.Formats.Texture;
using TelltaleD3DMeshEditor.UI;

namespace TelltaleD3DMeshEditor.Reinsert;

public static class ReinsertCli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (!AttachConsole(-1))
        {
            AllocConsole();
        }

        Console.WriteLine();
        try
        {
            Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(ex);
        }

        return true;
    }

    private static void Dispatch(string[] args)
    {
        switch (args[0])
        {
            case "--dump-layout":
                Require(args, 2);
                DumpLayout(args[1]);
                return;
            case "--dump-glb":
                Require(args, 2);
                DumpGlb(args[1]);
                return;
            case "--dump-materials":
                Require(args, 2);
                DumpMaterials(args[1]);
                return;
            case "--dump-uv-ranges":
                Require(args, 2);
                DumpUvRanges(args[1]);
                return;
            case "--dump-bone-palettes":
                Require(args, 2);
                DumpBonePalettes(args[1], args.Length >= 3 ? args[2] : null);
                return;
            case "--dump-skeleton":
                Require(args, 2);
                DumpSkeleton(args[1], args.Length >= 3 ? args[2] : null);
                return;
            case "--validate-skl":
            {
                Require(args, 2);
                var sklFiles = Directory.Exists(args[1])
                    ? Directory.EnumerateFiles(args[1], "*.skl", SearchOption.TopDirectoryOnly).ToList()
                    : [args[1]];
                int ok = 0, fail = 0, err = 0;
                foreach (var f in sklFiles)
                {
                    try
                    {
                        if (Formats.Skeleton.SkeletonRebuilder.ValidateRoundTrip(f)) ok++;
                        else { fail++; if (fail <= 8) Console.WriteLine($"  MISMATCH: {Path.GetFileName(f)}"); }
                    }
                    catch (Exception ex) { err++; if (err <= 8) Console.WriteLine($"  ERROR {Path.GetFileName(f)}: {ex.Message}"); }
                }
                Console.WriteLine($".skl round-trip: {ok} byte-identical, {fail} mismatch, {err} error (of {sklFiles.Count})");
                return;
            }
            case "--dump-skeleton-summary":
                Require(args, 2);
                DumpSkeletonSummary(args[1], HasFlag(args, "--recursive"));
                return;
            case "--dump-texture-formats":
                Require(args, 2);
                DumpTextureFormats(args[1], HasFlag(args, "--recursive"));
                return;
            case "--export-texture-png":
                Require(args, 3);
                ExportTexturePng(args[1], args[2]);
                return;
            case "--dump-combine-groups":
                Require(args, 2);
                DumpCombineGroups(args[1], args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null);
                return;
            case "--dump-part-classification":
                Require(args, 3);
                DumpPartClassification(args[1], args[2]);
                return;
            case "--rewrite-texture":
                Require(args, 4);
                RewriteTexture(args[1], args[2], args[3], args.Length > 4 && args[4] == "--uncompressed");
                return;
            case "--extract-asset":
                Require(args, 3);
                ExtractAsset(args[1], args[2], args.Length >= 4 ? args[3] : null);
                return;
            case "--reinsert-prop":
                Require(args, 4);
                ReinsertProp(args[1], args[2], args[3], HasFlag(args, "--diffuse-atlas"), HasFlag(args, "--match-original-size"));
                return;
            case "--bttf-texture-tests":
                Require(args, 4);
                ReinsertBttfTextureTests(args[1], args[2], args[3], HasFlag(args, "--match-original-size"));
                return;
            case "--reinsert-character":
                Require(args, 5);
                ReinsertCharacter(args[1], args[2], args[3], args[4], HasFlag(args, "--diffuse-atlas"));
                return;
            case "--reinsert-character-texture-tests":
                Require(args, 5);
                ReinsertCharacterTextureTests(args[1], args[2], args[3], args[4]);
                return;
            case "--reinsert-combined":
                Require(args, 5);
                ReinsertCombined(args[1], args[2], args[3], args[4], HasFlag(args, "--diffuse-atlas"));
                return;
            case "--extract-combined":
            {
                Require(args, 4);
                GameConfig.Current = ApplySavedReimportSettings(InferGameConfig(args[1]));
                var exAssets = UI.ModelAsset.Discover(args[1]);
                var exGroups = UI.ModelAssetGroup.Discover(exAssets, args[1]);
                var exGroup = exGroups.FirstOrDefault(g => g.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase))
                    ?? exGroups.FirstOrDefault(g => g.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No group matches '{args[2]}'.");
                UI.ExtractionService.ExtractAssetGroupToPath(exGroup, args[1], args[3], UI.ExportFormat.Glb);
                Console.WriteLine($"extracted   : {exGroup.Name} -> {args[3]}");
                return;
            }
            case "--dump-skeleton-rich":
                Require(args, 2);
                DumpSkeletonRich(args[1], args.Length >= 3 ? args[2] : null);
                return;
            case "--dump-v25-layout":
                Require(args, 2);
                DumpV25Layout(args[1]);
                return;
            case "--validate-v25-roundtrip":
                Require(args, 2);
                ValidateV25Roundtrip(args[1]);
                return;
            default:
                Console.WriteLine("Unknown command: " + args[0]);
                Console.WriteLine("Usage:");
                Console.WriteLine("  --dump-layout <mesh.d3dmesh>");
                Console.WriteLine("  --dump-glb <model.glb|model.gltf>");
                Console.WriteLine("  --dump-materials <mesh.d3dmesh>");
                Console.WriteLine("  --dump-uv-ranges <mesh.d3dmesh>");
                Console.WriteLine("  --dump-bone-palettes <mesh.d3dmesh> [skeleton.skl]");
                Console.WriteLine("  --dump-skeleton <model.glb|model.gltf|skeleton.skl> [mesh.d3dmesh]");
                Console.WriteLine("  --dump-skeleton-summary <folder> [--recursive]");
                Console.WriteLine("  --dump-texture-formats <texture.d3dtx|texture.dds|folder> [--recursive]");
                Console.WriteLine("  --export-texture-png <texture.d3dtx|texture.dds> <output.png>");
                Console.WriteLine("  --dump-combine-groups <folder> [filter]");
                Console.WriteLine("  --dump-part-classification <folder> <filter>");
                Console.WriteLine("  --rewrite-texture <template.d3dtx> <image.png> <output.d3dtx>");
                Console.WriteLine("  --extract-asset <mesh.d3dmesh> <output.glb|output.gltf> [skeleton.skl]");
                Console.WriteLine("  --reinsert-prop <template.d3dmesh> <model.glb|model.gltf> <output.d3dmesh> [--diffuse-atlas]");
                Console.WriteLine("  --reinsert-character <template.d3dmesh> <template.skl> <model.glb|model.gltf> <output.d3dmesh> [--diffuse-atlas]");
                Console.WriteLine("  --reinsert-character-texture-tests <template.d3dmesh> <template.skl> <model.glb|model.gltf> <output-folder>");
                Console.WriteLine("  --reinsert-combined <input-root-folder> <group-name> <model.glb|model.gltf> <output-folder> [--diffuse-atlas]");
                return;
        }
    }

    private static void DumpV25Layout(string input)
    {
        var data = File.ReadAllBytes(input);
        var layout = D3DMeshLayout.BuildV25(data);
        var off4 = BitConverter.ToUInt32(data, 4);
        var off12 = BitConverter.ToUInt32(data, 12);
        Console.WriteLine($"file        : {Path.GetFileName(input)}");
        Console.WriteLine($"version     : {layout.Version}");
        Console.WriteLine($"static      : {layout.IsStatic}{(layout.RejectReason is null ? "" : $" ({layout.RejectReason})")}");
        Console.WriteLine($"dataOffset  : {layout.DataOffset}");
        Console.WriteLine($"defaultSize : {off4} (sync) -> syncEnd={layout.DataOffset + off4}");
        Console.WriteLine($"asyncSize   : {off12}");
        Console.WriteLine($"faceStart   : {layout.FaceDataStart}  (expect syncEnd={layout.DataOffset + off4})");
        Console.WriteLine($"tail        : offset={layout.TailOffset} len={layout.TailLength} fileLen={data.Length}");
        Console.WriteLine($"meshBounds  : {layout.MeshBoundsOffset}  lodBounds={layout.LodBoundsOffsets.Count}");
        Console.WriteLine($"vertexCount@: {layout.VertexCountFieldOffset}");
        Console.WriteLine($"batches     : {layout.Batches.Count}");
        for (var b = 0; b < layout.Batches.Count; b++)
        {
            var batch = layout.Batches[b];
            Console.WriteLine($"  batch {b}  : texIdxRaw=0x{batch.TextureIndicesRaw:X8} texIdx@{batch.TextureIndicesOffset} mat={batch.MaterialIndex} mat@{batch.MaterialIndexOffset}");
        }
        for (var m = 0; m < layout.Materials.Count; m++)
        {
            var mat = layout.Materials[m];
            var diffHash = mat.DiffuseHashOffset > 0 ? BitConverter.ToUInt64(data, mat.DiffuseHashOffset) : 0;
            var sym = BitConverter.ToUInt64(data, mat.SymbolOffset);
            Console.WriteLine($"material {m}  : sym=0x{sym:X16} range=[{mat.Start},{mat.End}] diffHash@{mat.DiffuseHashOffset}=0x{diffHash:X16}");
        }
        Console.WriteLine($"matCount@   : {layout.MaterialCountFieldOffset}  matsEnd={layout.MaterialsEndOffset}");
        Console.WriteLine($"textures    : count@{layout.TextureCountFieldOffset} size@{layout.TextureBlockSizeFieldOffset} entriesEnd={layout.TextureEntriesEndOffset} entries={layout.TextureEntries.Count}");
        for (var t = 0; t < layout.TextureEntries.Count; t++)
        {
            var e = layout.TextureEntries[t];
            var typeRaw = BitConverter.ToUInt32(data, e.TypeOffset);
            var sym = BitConverter.ToUInt64(data, e.SymbolOffset);
            Console.WriteLine($"  texture {t} : typeRaw={typeRaw} sym=0x{sym:X16} len={e.Length} start={e.Start}");
        }
        Console.WriteLine($"matGroup    : count@{layout.MaterialGroupCountFieldOffset} size@{layout.MaterialGroupSizeFieldOffset} entriesEnd={layout.MaterialGroupEntriesEndOffset} entries={layout.MaterialGroupEntries.Count}");
        for (var g = 0; g < layout.MaterialGroupEntries.Count; g++)
        {
            var e = layout.MaterialGroupEntries[g];
            var sym = BitConverter.ToUInt64(data, e.SymbolOffset);
            Console.WriteLine($"  group {g}   : sym=0x{sym:X16} len={e.Length} start={e.Start}");
        }
        Console.WriteLine($"uvScaleSlots: {string.Join(", ", layout.UvScaleSlots.Select(s => $"layer{s.Layer}@{s.ValuesOffset}"))}");
        Console.WriteLine($"attributes  : {string.Join(", ", layout.Attributes.Where(a => a.Key.Length > 0).Select(a => $"{a.Key}(b{a.Buffer} f{a.Format} @{a.BufferOffset})"))}");
        Console.WriteLine($"palettes    : block@{layout.BonePaletteBlockStart}..{layout.BonePaletteBlockEnd} (len={layout.BonePaletteBlockEnd - layout.BonePaletteBlockStart}) count={layout.BonePalettes.Count} bones={string.Join("/", layout.BonePalettes.Select(p => p.BoneHashes.Length))}");
        if (layout.BonePaletteBlockStart > 0 && layout.BonePaletteBlockStart + 8 <= layout.Original.Length)
        {
            Console.WriteLine($"  paletteLeadingU32={BitConverter.ToUInt32(layout.Original, layout.BonePaletteBlockStart)} (blockLen would be {layout.BonePaletteBlockEnd - layout.BonePaletteBlockStart})");
        }
        Console.WriteLine($"faceBuffer  : count={layout.FaceBuffer.Count} stride={layout.FaceBuffer.Stride} payload@{layout.FaceBuffer.PayloadOffset} len={layout.FaceBuffer.PayloadLength}");
        for (var i = 0; i < layout.VertexBuffers.Count; i++)
        {
            var vb = layout.VertexBuffers[i];
            Console.WriteLine($"vertexBuf{i}  : count={vb.Count} stride={vb.Stride} payload@{vb.PayloadOffset} len={vb.PayloadLength}");
        }
    }

    // Two-part V25 reinsertion self-check (mesh only, no texture writes):
    //  1) structural: the sync section ends where faces begin, payloads tile to EOF, async-size matches.
    //  2) geometry round-trip: extract the static mesh to a GLB, reinsert it back into itself, reparse,
    //     and confirm the submesh/vertex/triangle counts and bounding box survive the trip.
    private static void ValidateV25Roundtrip(string inputPathOrFolder)
    {
        var files = Directory.Exists(inputPathOrFolder)
            ? Directory.GetFiles(inputPathOrFolder, "*.d3dmesh", SearchOption.AllDirectories)
            : [inputPathOrFolder];
        int structOk = 0, structFail = 0, rtOk = 0, rtFail = 0, skipped = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), "v25rt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var file in files)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    var layout = D3DMeshLayout.BuildV25(data);
                    if (layout.FaceDataStart == 0)
                    {
                        skipped++;
                        continue;
                    }

                    // 1) structural invariants.
                    var off4 = BitConverter.ToUInt32(data, 4);
                    var off12 = BitConverter.ToUInt32(data, 12);
                    var syncEnd = layout.DataOffset + (int)off4;
                    var structProblems = new List<string>();
                    if (layout.FaceDataStart != syncEnd) structProblems.Add($"faceStart {layout.FaceDataStart}!=syncEnd {syncEnd}");
                    if (layout.TailLength != 0) structProblems.Add($"tail {layout.TailLength}!=0");
                    var payloadSum = layout.FaceBuffer.PayloadLength + layout.VertexBuffers.Sum(v => v.PayloadLength);
                    if (payloadSum != (int)off12) structProblems.Add($"payloadSum {payloadSum}!=asyncSize {off12}");
                    if (structProblems.Count == 0) structOk++;
                    else { structFail++; Console.WriteLine($"STRUCT-FAIL {Path.GetFileName(file)}: {string.Join("; ", structProblems)}"); }

                    if (!layout.IsStatic && !layout.IsSkinned)
                    {
                        skipped++;
                        continue;
                    }

                    // 2) geometry round-trip (mesh only).
                    var orig = D3DMeshParser.ParseFile(file);
                    var glbPath = Path.Combine(tempDir, "rt.glb");
                    GameConfig.Current = InferGameConfig(file);
                    // Extract with the character's own skeleton so the GLB carries proper joints/bind pose
                    // (a skinned mesh without its .skl exports degraded skinning, breaking the round-trip).
                    var siblingSkl = ResolveExtractSkeletonPath(Path.GetFullPath(file), null);
                    var asset = UI.ModelAsset.FromPaths(Path.GetFullPath(file), siblingSkl);
                    UI.ExtractionService.ExtractAssetToPath(asset, Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".", glbPath, UI.ExportFormat.Glb);
                    var model = GltfReader.Load(glbPath);
                    var result = MeshReinserter.ReinsertV25Geometry(layout, model);
                    var rt = D3DMeshParser.Parse(result);

                    var rtProblems = new List<string>();
                    if (rt.Submeshes.Count != orig.Submeshes.Count) rtProblems.Add($"submeshes {rt.Submeshes.Count}!={orig.Submeshes.Count}");
                    if (rt.VertexCount != orig.VertexCount) rtProblems.Add($"verts {rt.VertexCount}!={orig.VertexCount}");
                    if (rt.FaceCount != orig.FaceCount) rtProblems.Add($"tris {rt.FaceCount}!={orig.FaceCount}");
                    var ob = orig.GetBounds();
                    var rb = rt.GetBounds();
                    var boundsDelta = MathF.Max(MathF.Max(MathF.Abs(ob.MinX - rb.MinX), MathF.Abs(ob.MaxX - rb.MaxX)),
                        MathF.Max(MathF.Abs(ob.MinY - rb.MinY), MathF.Abs(ob.MaxY - rb.MaxY)));
                    if (boundsDelta > 0.01f) rtProblems.Add($"bounds drift {boundsDelta:0.####}");

                    // UV drift: the diffuse maps via uv1, so verify it survives the trip (quantized layers
                    // lose a little precision; a real swap/flip shows up as a large delta).
                    var uvDelta = MaxUvDrift(orig, rt);
                    if (uvDelta > 0.02f) rtProblems.Add($"uv1 drift {uvDelta:0.####}");

                    // Skinning round-trip: palettes preserved, and matched vertices keep their dominant
                    // bone + weight (a wrong joint->palette mapping shows up as dominant-bone mismatches).
                    if (layout.IsSkinned)
                    {
                        if (rt.BonePalettes.Count != orig.BonePalettes.Count)
                            rtProblems.Add($"palettes {rt.BonePalettes.Count}!={orig.BonePalettes.Count}");
                        var (wDrift, boneMismatch, comparedSkin) = MaxSkinDrift(orig, rt);
                        if (comparedSkin > 0 && boneMismatch > comparedSkin * 0.02f)
                            rtProblems.Add($"dominant-bone mismatch {boneMismatch}/{comparedSkin}");
                        if (wDrift > 0.05f) rtProblems.Add($"weight drift {wDrift:0.###}");
                    }

                    if (rtProblems.Count == 0) rtOk++;
                    else { rtFail++; Console.WriteLine($"RT-FAIL {Path.GetFileName(file)}: {string.Join("; ", rtProblems)}"); }
                }
                catch (Exception ex)
                {
                    rtFail++;
                    Console.WriteLine($"ERROR {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine($"V25 structural: {structOk} ok, {structFail} fail | geometry round-trip: {rtOk} ok, {rtFail} fail | skipped {skipped} (of {files.Length})");
    }

    // Compares uv1 between two parses by matching vertices on position (reinsertion may reorder/dedupe
    // vertices, so index-by-index would be unreliable). Returns the largest |Δu|/|Δv| found.
    private static float MaxUvDrift(MeshData a, MeshData b)
    {
        static string Key(VertexData v) =>
            $"{MathF.Round(v.X, 3)},{MathF.Round(v.Y, 3)},{MathF.Round(v.Z, 3)}";
        // A position can host several UVs (a UV seam), so collect ALL uv1 values per position and match
        // each original vertex to the closest reinserted uv at the same position.
        var bByPos = new Dictionary<string, List<(float U, float V)>>();
        foreach (var sm in b.Submeshes)
        {
            foreach (var v in sm.Vertices)
            {
                var key = Key(v);
                if (!bByPos.TryGetValue(key, out var list))
                {
                    bByPos[key] = list = [];
                }

                list.Add((v.U, v.V));
            }
        }

        var maxDelta = 0f;
        foreach (var sm in a.Submeshes)
        {
            foreach (var v in sm.Vertices)
            {
                if (!bByPos.TryGetValue(Key(v), out var candidates))
                {
                    continue;
                }

                var best = float.MaxValue;
                foreach (var c in candidates)
                {
                    best = MathF.Min(best, MathF.Max(MathF.Abs(v.U - c.U), MathF.Abs(v.V - c.V)));
                }

                maxDelta = MathF.Max(maxDelta, best);
            }
        }

        return maxDelta;
    }

    // Compares skinning between two parses per submesh, vertex-by-vertex by INDEX. A V25 same-model
    // round-trip preserves submesh and vertex order (counts match), so index comparison is exact —
    // position-keyed matching produces false mismatches on dense meshes where many verts round to the
    // same key. Compares the dominant bone's resolved hash and the largest single weight. Returns
    // (max weight delta, dominant-bone mismatches, vertices compared).
    private static (float WeightDrift, int BoneMismatch, int Compared) MaxSkinDrift(MeshData a, MeshData b)
    {
        static ulong DominantHash(VertexData v, IReadOnlyList<ulong[]> palettes, int paletteIndex)
        {
            var bones = new[] { v.Bone0, v.Bone1, v.Bone2, v.Bone3 };
            var weights = new[] { v.Weight0, v.Weight1, v.Weight2, v.Weight3 };
            var best = 0;
            for (var i = 1; i < 4; i++) if (weights[i] > weights[best]) best = i;
            var bone = bones[best];
            return paletteIndex >= 0 && paletteIndex < palettes.Count && bone >= 0 && bone < palettes[paletteIndex].Length
                ? palettes[paletteIndex][bone]
                : 0;
        }
        static float MaxWeight(VertexData v) =>
            MathF.Max(MathF.Max(v.Weight0, v.Weight1), MathF.Max(v.Weight2, v.Weight3));

        float weightDrift = 0f;
        int boneMismatch = 0, compared = 0;
        var n = Math.Min(a.Submeshes.Count, b.Submeshes.Count);
        for (var s = 0; s < n; s++)
        {
            var av = a.Submeshes[s].Vertices;
            var bv = b.Submeshes[s].Vertices;
            int aPi = a.Submeshes[s].BonePaletteIndex, bPi = b.Submeshes[s].BonePaletteIndex;
            var vn = Math.Min(av.Count, bv.Count);
            for (var i = 0; i < vn; i++)
            {
                compared++;
                if (DominantHash(av[i], a.BonePalettes, aPi) != DominantHash(bv[i], b.BonePalettes, bPi))
                {
                    boneMismatch++;
                }

                weightDrift = MathF.Max(weightDrift, MathF.Abs(MaxWeight(av[i]) - MaxWeight(bv[i])));
            }
        }

        return (weightDrift, boneMismatch, compared);
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static void Require(string[] args, int count)
    {
        if (args.Length < count)
        {
            throw new ArgumentException($"Command {args[0]} expects {count - 1} argument(s).");
        }
    }

    private static void DumpCombineGroups(string folder, string? filter)
    {
        var assets = ModelAsset.Discover(folder);
        var groups = ModelAssetGroup.Discover(assets, folder);
        foreach (var group in groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filter) &&
                !group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !group.Assets.Any(asset => Path.GetFileNameWithoutExtension(asset.MeshPath).Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Console.WriteLine($"Combined: {group.Name} ({group.Assets.Count} parts)");
            foreach (var asset in group.Assets.OrderBy(asset => asset.MeshPath, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("  " + Path.GetFileNameWithoutExtension(asset.MeshPath));
            }
        }
    }

    private static void DumpPartClassification(string folder, string filter)
    {
        var assets = ModelAsset.Discover(folder)
            .Where(asset => Path.GetFileNameWithoutExtension(asset.MeshPath).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var method = typeof(ModelAssetGroup).GetMethod(
            "ClassifyPart",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ModelAssetGroup), "ClassifyPart");
        var modelStemMethod = typeof(ModelAssetGroup).GetMethod(
            "ModelStem",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ModelAssetGroup), "ModelStem");
        foreach (var asset in assets)
        {
            var skeletonStem = Path.GetFileNameWithoutExtension(asset.SkeletonPath ?? asset.MeshPath);
            var meshStem = Path.GetFileNameWithoutExtension(asset.MeshPath);
            var classificationStem = modelStemMethod.Invoke(null, [meshStem, skeletonStem])?.ToString() ?? skeletonStem;
            var part = method.Invoke(null, [asset, classificationStem])!;
            string GetString(string name) => part.GetType().GetProperty(name)?.GetValue(part)?.ToString() ?? "";
            bool GetBool(string name) => part.GetType().GetProperty(name)?.GetValue(part) is true;
            Console.WriteLine(
                $"{Path.GetFileNameWithoutExtension(asset.MeshPath)} | skeleton={skeletonStem} model={classificationStem} | " +
                $"tail={GetString("Tail")} slot={GetString("Slot")} variant={GetString("Variant")} " +
                $"recognized={GetBool("IsRecognized")} additive={GetBool("IsAdditive")}");
        }
    }

    private static void DumpLayout(string input)
    {
        var data = File.ReadAllBytes(input);
        var layout = D3DMeshLayout.Build(data);
        var mesh = D3DMeshParser.ParseFile(input);

        Console.WriteLine($"file        : {Path.GetFileName(input)} ({data.Length} bytes)");
        Console.WriteLine($"name        : {layout.Name}");
        Console.WriteLine($"version     : {layout.Version}");
        Console.WriteLine($"submeshes   : {layout.SubmeshCount}");
        Console.WriteLine($"faces       : {layout.FacePointCount / 3}");
        Console.WriteLine($"vertices    : {layout.VertexCount}");
        foreach (var vertexBuffer in layout.VertexBuffers)
        {
            Console.WriteLine($"vertex data : #{vertexBuffer.Index} 0x{vertexBuffer.DataOffset:X} stride {vertexBuffer.VertexStride} len {vertexBuffer.DataLength}");
        }
        Console.WriteLine($"tail        : 0x{layout.TailOffset:X} len {layout.TailLength}");
        Console.WriteLine($"submeshBlkSz: field@0x{layout.SubmeshBlockSizeFieldOffset:X} value {layout.SubmeshBlockSize}");
        Console.WriteLine($"submeshTable: 0x{layout.SubmeshTableOffset:X} len {layout.SubmeshTableLength}");
        Console.WriteLine($"paletteBlock: 0x{layout.BonePaletteBlockOffset:X} len {layout.BonePaletteBlockLength} entrySize {layout.BonePaletteEntrySize} count {layout.OriginalBonePaletteCount}");
        Console.WriteLine($"texGroupBlk : 0x{layout.TextureGroupBlockOffset:X} len {layout.TextureGroupBlockLength}");
        Console.WriteLine($"uvScales    : 0x{layout.UvScalesOffset:X} len {layout.UvScalesLength}");
        Console.WriteLine($"faceData    : countField@0x{layout.FaceCountFieldOffset:X} data@0x{layout.FaceDataOffset:X} len {layout.FaceDataLength}");
        Console.WriteLine($"parsed      : {mesh.Submeshes.Count} submeshes, {mesh.VertexCount} verts, {mesh.FaceCount} tris");
        PrintBounds("bounds      ", mesh.GetBounds());
        foreach (var vertexBuffer in layout.VertexBuffers)
        {
            Console.WriteLine($"attrs       : vertex buffer #{vertexBuffer.Index}");
            PrintAttrs(vertexBuffer.Attributes);
        }
        Console.WriteLine(layout.TailOffset + layout.TailLength == data.Length
            ? "layout      : closes at EOF"
            : $"layout      : ends at 0x{layout.TailOffset + layout.TailLength:X}, file ends at 0x{data.Length:X}");
    }

    private static void DumpGlb(string input)
    {
        var model = GltfReader.Load(input);
        Console.WriteLine($"file       : {Path.GetFileName(input)}");
        Console.WriteLine($"primitives : {model.Primitives.Count}");
        Console.WriteLine($"vertices   : {model.Primitives.Sum(p => p.VertexCount)}");
        Console.WriteLine($"triangles  : {model.Primitives.Sum(p => p.Indices.Length / 3)}");
        Console.WriteLine($"joints     : {model.Joints.Count}");
        Console.WriteLine($"skeleton   : {model.Skeleton?.Bones.Count ?? 0}");
        PrintBounds("bounds     ", GetBounds(model));
        for (var i = 0; i < model.Primitives.Count; i++)
        {
            var primitive = model.Primitives[i];
            var textures = primitive.TextureSlots.Count == 0
                ? "none"
                : string.Join(", ", primitive.TextureSlots.Select(pair => pair.Key + "=" + pair.Value.Name));
            Console.WriteLine($"  prim {i}: verts={primitive.VertexCount} tris={primitive.Indices.Length / 3} textures={textures}");
            if (primitive.Color0 is { Length: > 0 } colors)
            {
                Console.WriteLine("    color0=" + DescribeColorStats(colors));
            }

            if (primitive.Joints0 is not null && primitive.Weights0 is not null)
            {
                var usedJoints = EnumerateWeightedJoints(primitive)
                    .Distinct()
                    .OrderBy(joint => joint)
                    .Take(24)
                    .Select(joint => DescribeGltfJoint(joint, model));
                Console.WriteLine("    joints=" + string.Join(", ", usedJoints));
            }
        }
    }

    private static void DumpMaterials(string input)
    {
        var mesh = D3DMeshParser.ParseFile(input);
        Console.WriteLine($"file       : {Path.GetFileName(input)}");
        Console.WriteLine($"submeshes  : {mesh.Submeshes.Count}");
        Console.WriteLine($"vertices   : {mesh.VertexCount}");
        Console.WriteLine($"faces      : {mesh.FaceCount}");
        for (var i = 0; i < mesh.Submeshes.Count; i++)
        {
            var submesh = mesh.Submeshes[i];
            Console.WriteLine($"  submesh {i}: {submesh.Name} ({submesh.Vertices.Count} verts, {submesh.Faces.Count} faces)");
            foreach (var pair in submesh.TextureNames.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    {pair.Key,-15} {pair.Value}");
            }
        }
    }

    private static void DumpUvRanges(string input)
    {
        var mesh = D3DMeshParser.ParseFile(input);
        Console.WriteLine($"file       : {Path.GetFileName(input)}");
        Console.WriteLine($"version    : {mesh.Version}");
        Console.WriteLine($"submeshes  : {mesh.Submeshes.Count}");
        for (var i = 0; i < mesh.Submeshes.Count; i++)
        {
            var submesh = mesh.Submeshes[i];
            Console.WriteLine($"  submesh {i}: {submesh.Name}");
            Console.WriteLine($"    uv1 {DescribeUvRange(submesh.Vertices, vertex => vertex.U, vertex => vertex.V)}");
            Console.WriteLine($"    uv2 {DescribeUvRange(submesh.Vertices, vertex => vertex.U2, vertex => vertex.V2)}");
            Console.WriteLine($"    uv3 {DescribeUvRange(submesh.Vertices, vertex => vertex.U3, vertex => vertex.V3)}");
            Console.WriteLine($"    uv4 {DescribeUvRange(submesh.Vertices, vertex => vertex.U4, vertex => vertex.V4)}");
            Console.WriteLine($"    uv5 {DescribeUvRange(submesh.Vertices, vertex => vertex.U5, vertex => vertex.V5)}");
            Console.WriteLine($"    uv6 {DescribeUvRange(submesh.Vertices, vertex => vertex.U6, vertex => vertex.V6)}");
        }
    }

    private static string DescribeUvRange(
        IReadOnlyList<VertexData> vertices,
        Func<VertexData, float> getU,
        Func<VertexData, float> getV)
    {
        if (vertices.Count == 0)
        {
            return "empty";
        }

        var minU = float.PositiveInfinity;
        var maxU = float.NegativeInfinity;
        var minV = float.PositiveInfinity;
        var maxV = float.NegativeInfinity;
        foreach (var vertex in vertices)
        {
            var u = getU(vertex);
            var v = getV(vertex);
            minU = MathF.Min(minU, u);
            maxU = MathF.Max(maxU, u);
            minV = MathF.Min(minV, v);
            maxV = MathF.Max(maxV, v);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "u={0:0.###}..{1:0.###}, v={2:0.###}..{3:0.###}",
            minU, maxU, minV, maxV);
    }

    private static void ExportTexturePng(string input, string output)
    {
        var texture = TextureLoader.Load(input);
        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using var bitmap = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < texture.Height; y++)
        {
            for (var x = 0; x < texture.Width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(texture.Pixels[y * texture.Width + x]));
            }
        }

        bitmap.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"texture    : {Path.GetFileName(input)} ({texture.Width}x{texture.Height})");
        Console.WriteLine($"exported   : {outputPath}");
    }

    private static string DescribeColorStats(IReadOnlyList<Vector4> colors)
    {
        var min = new Vector4(float.PositiveInfinity);
        var max = new Vector4(float.NegativeInfinity);
        var sum = Vector4.Zero;
        foreach (var color in colors)
        {
            min = Vector4.Min(min, color);
            max = Vector4.Max(max, color);
            sum += color;
        }

        var avg = sum / colors.Count;
        return string.Format(
            CultureInfo.InvariantCulture,
            "avg=({0:0.###},{1:0.###},{2:0.###},{3:0.###}) min=({4:0.###},{5:0.###},{6:0.###},{7:0.###}) max=({8:0.###},{9:0.###},{10:0.###},{11:0.###})",
            avg.X, avg.Y, avg.Z, avg.W,
            min.X, min.Y, min.Z, min.W,
            max.X, max.Y, max.Z, max.W);
    }

    private static IEnumerable<int> EnumerateWeightedJoints(GltfPrimitive primitive)
    {
        if (primitive.Joints0 is null || primitive.Weights0 is null)
        {
            yield break;
        }

        for (var vertex = 0; vertex < primitive.VertexCount; vertex++)
        {
            var jointOffset = vertex * 4;
            var weights = primitive.Weights0[vertex];
            if (weights.X > 0.000001f) yield return primitive.Joints0[jointOffset];
            if (weights.Y > 0.000001f) yield return primitive.Joints0[jointOffset + 1];
            if (weights.Z > 0.000001f) yield return primitive.Joints0[jointOffset + 2];
            if (weights.W > 0.000001f) yield return primitive.Joints0[jointOffset + 3];
        }
    }

    private static string DescribeGltfJoint(int jointIndex, GltfModel model)
    {
        if (jointIndex >= 0 &&
            jointIndex < model.Joints.Count &&
            !string.IsNullOrWhiteSpace(model.Joints[jointIndex].Name))
        {
            return $"{jointIndex}:{model.Joints[jointIndex].Name}";
        }

        return jointIndex.ToString(CultureInfo.InvariantCulture);
    }

    private static void DumpBonePalettes(string input, string? skeletonPath)
    {
        var data = File.ReadAllBytes(input);
        var mesh = D3DMeshParser.Parse(data);
        Dictionary<ulong, string>? namesByHash = null;
        Dictionary<ulong, Vector3>? positionsByHash = null;
        if (!string.IsNullOrWhiteSpace(skeletonPath) && File.Exists(skeletonPath))
        {
            var skeleton = SkeletonLoader.Load(skeletonPath, mesh.Version);
            namesByHash = skeleton.Bones
                .GroupBy(bone => bone.Hash)
                .ToDictionary(group => group.Key, group => group.First().Name);
            var positions = BuildSkeletonWorldPositions(skeleton);
            positionsByHash = skeleton.Bones
                .Select((bone, index) => (bone.Hash, Position: positions[index]))
                .GroupBy(item => item.Hash)
                .ToDictionary(group => group.Key, group => group.First().Position);
        }

        Console.WriteLine($"file       : {Path.GetFileName(input)}");
        Console.WriteLine($"palettes   : {mesh.BonePalettes.Count}");
        Console.WriteLine($"submeshes  : {mesh.Submeshes.Count}");
        for (var i = 0; i < mesh.Submeshes.Count; i++)
        {
            var submesh = mesh.Submeshes[i];
            Console.WriteLine($"  submesh {i}: palette={submesh.BonePaletteIndex} {submesh.Name}");
        }

        var influenceBounds = ComputePaletteInfluenceBounds(mesh);
        for (var i = 0; i < mesh.BonePalettes.Count; i++)
        {
            var palette = mesh.BonePalettes[i];
            Console.WriteLine($"  palette {i}: {palette.Length} bone(s)");
            if (positionsByHash is not null)
            {
                var palettePositions = palette
                    .Where(hash => positionsByHash.ContainsKey(hash))
                    .Select(hash => positionsByHash[hash])
                    .ToArray();
                if (palettePositions.Length > 0)
                {
                    PrintBounds("    bounds", BoundsOf(palettePositions));
                }
            }

            foreach (var hash in palette.Take(12))
            {
                var label = namesByHash is not null && namesByHash.TryGetValue(hash, out var name)
                    ? name
                    : $"0x{hash:X16}";
                var entry = TryGetBonePaletteEntry(mesh, i, hash);
                var influence = influenceBounds.TryGetValue((i, Array.IndexOf(palette, hash)), out var box)
                    ? $" influence=({F(box.MinX)}, {F(box.MinY)}, {F(box.MinZ)})..({F(box.MaxX)}, {F(box.MaxY)}, {F(box.MaxZ)})"
                    : "";
                if (entry is { HasBounds: true })
                {
                    Console.WriteLine($"    {label} center=({F(entry.Value.CenterX)}, {F(entry.Value.CenterY)}, {F(entry.Value.CenterZ)}) radius={F(entry.Value.Radius)}{influence}");
                }
                else
                {
                    Console.WriteLine($"    {label}{influence}");
                }
            }

            if (palette.Length > 12)
            {
                Console.WriteLine("    ...");
            }
        }
    }

    private static Dictionary<(int Palette, int LocalBone), CliBoneBox> ComputePaletteInfluenceBounds(MeshData mesh)
    {
        var result = new Dictionary<(int, int), CliBoneBox>();
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.BonePaletteIndex < 0 || submesh.BonePaletteIndex >= mesh.BonePalettes.Count)
            {
                continue;
            }

            var palette = mesh.BonePalettes[submesh.BonePaletteIndex];
            foreach (var vertex in submesh.Vertices)
            {
                AddInfluence(vertex.Bone0, vertex.Weight0, vertex, submesh.BonePaletteIndex, palette.Length, mesh.Version, result);
                AddInfluence(vertex.Bone1, vertex.Weight1, vertex, submesh.BonePaletteIndex, palette.Length, mesh.Version, result);
                AddInfluence(vertex.Bone2, vertex.Weight2, vertex, submesh.BonePaletteIndex, palette.Length, mesh.Version, result);
                AddInfluence(vertex.Bone3, vertex.Weight3, vertex, submesh.BonePaletteIndex, palette.Length, mesh.Version, result);
            }
        }

        return result;
    }

    private static void AddInfluence(
        int rawBone,
        float weight,
        VertexData vertex,
        int paletteIndex,
        int paletteLength,
        int meshVersion,
        Dictionary<(int, int), CliBoneBox> result)
    {
        if (weight <= 0.000001f)
        {
            return;
        }

        var local = BoneIndexConvention.ToPaletteIndex(rawBone, meshVersion);
        if (local < 0 || local >= paletteLength)
        {
            return;
        }

        var key = (paletteIndex, local);
        result[key] = result.TryGetValue(key, out var existing)
            ? existing.Include(vertex.X, vertex.Y, vertex.Z)
            : CliBoneBox.From(vertex.X, vertex.Y, vertex.Z);
    }

    private static BonePaletteEntryData? TryGetBonePaletteEntry(MeshData mesh, int paletteIndex, ulong hash)
    {
        if (paletteIndex < 0 || paletteIndex >= mesh.BonePaletteEntries.Count)
        {
            return null;
        }

        return mesh.BonePaletteEntries[paletteIndex].FirstOrDefault(entry => entry.Hash == hash);
    }

    // Full per-joint dump straight from the Toolkit entry (local + rest transforms, bone dir/length,
    // rotation adjustment, translation scales), optionally filtered by a name substring. This is the
    // data the game's procedural rigs (eye look-at, head tracking) consume, so a port that breaks one
    // of these fields is visible here even when the plain local pose looks fine.
    private static void DumpSkeletonRich(string input, string? nameFilter)
    {
        var entries = SkeletonRebuilder.ReadEntryDiagnostics(input);
        Console.WriteLine($"file       : {Path.GetFileName(input)}");
        Console.WriteLine($"joints     : {entries.Count}");
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(nameFilter) &&
                !entry.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine($"  [{entry.Index,3}] {entry.Name} parent={entry.ParentIndex}");
            Console.WriteLine($"        local pos=({F(entry.LocalPosition.X)}, {F(entry.LocalPosition.Y)}, {F(entry.LocalPosition.Z)}) quat=({F(entry.LocalRotation.X)}, {F(entry.LocalRotation.Y)}, {F(entry.LocalRotation.Z)}, {F(entry.LocalRotation.W)})");
            Console.WriteLine($"        rest  pos=({F(entry.RestTranslation.X)}, {F(entry.RestTranslation.Y)}, {F(entry.RestTranslation.Z)}) quat=({F(entry.RestRotation.X)}, {F(entry.RestRotation.Y)}, {F(entry.RestRotation.Z)}, {F(entry.RestRotation.W)})");
            Console.WriteLine($"        len={F(entry.BoneLength)} dir=({F(entry.BoneDir.X)}, {F(entry.BoneDir.Y)}, {F(entry.BoneDir.Z)}) rotAdj=({F(entry.BoneRotationAdjustment.X)}, {F(entry.BoneRotationAdjustment.Y)}, {F(entry.BoneRotationAdjustment.Z)}, {F(entry.BoneRotationAdjustment.W)})");
            Console.WriteLine($"        gts=({F(entry.GlobalTranslationScale.X)}, {F(entry.GlobalTranslationScale.Y)}, {F(entry.GlobalTranslationScale.Z)}) lts=({F(entry.LocalTranslationScale.X)}, {F(entry.LocalTranslationScale.Y)}, {F(entry.LocalTranslationScale.Z)}) ats=({F(entry.AnimTranslationScale.X)}, {F(entry.AnimTranslationScale.Y)}, {F(entry.AnimTranslationScale.Z)})");
        }
    }

    private static void DumpSkeleton(string input, string? meshPath)
    {
        SkeletonData? skeleton;
        var ext = Path.GetExtension(input);
        if (ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            skeleton = GltfReader.Load(input).Skeleton;
        }
        else
        {
            var version = 13;
            if (!string.IsNullOrWhiteSpace(meshPath) && File.Exists(meshPath))
            {
                var meshData = File.ReadAllBytes(meshPath);
                try
                {
                    version = D3DMeshLayout.Build(meshData).Version;
                }
                catch (NotSupportedException)
                {
                    version = D3DMeshParser.Parse(meshData).Version;
                }
            }

            skeleton = SkeletonLoader.Load(input, version);
        }

        if (skeleton is null || skeleton.Bones.Count == 0)
        {
            Console.WriteLine("skeleton   : none");
            return;
        }

        Console.WriteLine($"file       : {Path.GetFileName(input)}");
        Console.WriteLine($"bones      : {skeleton.Bones.Count}");
        Console.WriteLine($"rich data  : {skeleton.Bones.Count(bone => bone.HasRichSkeletonData)} bone(s)");
        Console.WriteLine($"late parent: {CountLateParentBones(skeleton)} bone(s)");
        PrintBounds("bounds     ", GetSkeletonBounds(skeleton));
        for (var i = 0; i < Math.Min(16, skeleton.Bones.Count); i++)
        {
            var bone = skeleton.Bones[i];
            var rich = bone.HasRichSkeletonData
                ? $" len={F(bone.BoneLength)} dir=({F(bone.BoneDir.X)}, {F(bone.BoneDir.Y)}, {F(bone.BoneDir.Z)}) rest=({F(bone.RestTranslation.X)}, {F(bone.RestTranslation.Y)}, {F(bone.RestTranslation.Z)})"
                : "";
            Console.WriteLine($"  {i,3}: parent={bone.ParentIndex,3} pos=({F(bone.X)}, {F(bone.Y)}, {F(bone.Z)}){rich} {bone.Name}");
        }
        if (skeleton.Bones.Count > 16)
        {
            Console.WriteLine("  ...");
        }
    }

    private static int CountLateParentBones(SkeletonData skeleton)
    {
        var count = 0;
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent >= i && parent < skeleton.Bones.Count)
            {
                count++;
            }
        }

        return count;
    }

    private static void DumpSkeletonSummary(string input, bool recursive)
    {
        if (!Directory.Exists(input))
        {
            throw new DirectoryNotFoundException(input);
        }

        var files = Directory.EnumerateFiles(
                input,
                "*.skl",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rows = new List<(string Path, SkeletonData Skeleton, int RichBones, int LateParents)>();
        var failures = new List<(string Path, string Error)>();
        foreach (var file in files)
        {
            try
            {
                var skeleton = SkeletonLoader.Load(file, 13);
                rows.Add((file, skeleton, skeleton.Bones.Count(bone => bone.HasRichSkeletonData), CountLateParentBones(skeleton)));
            }
            catch (Exception ex)
            {
                failures.Add((file, ex.Message));
            }
        }

        Console.WriteLine($"skeletons  : {rows.Count} readable, {failures.Count} failed");
        if (rows.Count > 0)
        {
            Console.WriteLine($"bones      : min={rows.Min(row => row.Skeleton.Bones.Count)} max={rows.Max(row => row.Skeleton.Bones.Count)} avg={rows.Average(row => row.Skeleton.Bones.Count):0.##}");
            Console.WriteLine($"rich files : {rows.Count(row => row.RichBones > 0)}");
            Console.WriteLine($"late parent: {rows.Count(row => row.LateParents > 0)} file(s)");
            foreach (var row in rows.OrderByDescending(row => row.Skeleton.Bones.Count).Take(8))
            {
                Console.WriteLine($"  {Path.GetFileName(row.Path),-48} bones={row.Skeleton.Bones.Count,3} rich={row.RichBones,3} lateParent={row.LateParents,2}");
            }
        }

        foreach (var row in rows.Where(row => row.LateParents > 0).Take(8))
        {
            Console.WriteLine($"  late-parent: {Path.GetFileName(row.Path)} ({row.LateParents})");
        }

        foreach (var failure in failures.Take(12))
        {
            Console.WriteLine($"  failed: {Path.GetFileName(failure.Path)} - {failure.Error}");
        }
    }

    private static void DumpTextureFormats(string input, bool recursive)
    {
        string[] files;
        if (File.Exists(input))
        {
            files = [Path.GetFullPath(input)];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.EnumerateFiles(
                    input,
                    "*.*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(path =>
                    Path.GetExtension(path).Equals(".d3dtx", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            throw new FileNotFoundException("Texture input not found.", input);
        }

        var rows = new List<(string Path, TextureFormatInfo Info)>();
        var failures = new List<(string Path, string Error)>();
        foreach (var file in files)
        {
            try
            {
                rows.Add((file, TextureLoader.InspectFormat(file)));
            }
            catch (Exception ex)
            {
                failures.Add((file, ex.Message));
            }
        }

        Console.WriteLine($"textures   : {rows.Count} readable, {failures.Count} failed");
        if (rows.Count == 1)
        {
            var row = rows[0];
            PrintTextureFormatRow(row.Path, row.Info);
        }
        else
        {
            foreach (var group in rows
                         .GroupBy(row => (row.Info.Container, row.Info.FormatName, row.Info.FormatValue, row.Info.GammaName))
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key.FormatName, StringComparer.OrdinalIgnoreCase))
            {
                var examples = string.Join(", ", group.Take(5).Select(row => Path.GetFileName(row.Path)));
                Console.WriteLine(
                    $"  {group.Key.FormatName,-12} 0x{group.Key.FormatValue:X2} gamma={group.Key.GammaName,-7} count={group.Count(),4} examples={examples}");
            }
        }

        foreach (var failure in failures.Take(12))
        {
            Console.WriteLine($"  failed: {Path.GetFileName(failure.Path)} - {failure.Error}");
        }
    }

    private static void PrintTextureFormatRow(string path, TextureFormatInfo info)
    {
        Console.WriteLine($"file       : {Path.GetFileName(path)}");
        Console.WriteLine($"container  : {info.Container}");
        Console.WriteLine($"format     : {info.FormatName} (0x{info.FormatValue:X})");
        Console.WriteLine($"gamma      : {info.GammaName}");
        Console.WriteLine($"size       : {info.Width}x{info.Height}");
        Console.WriteLine($"regions    : {info.RegionCount}");
        foreach (var region in info.Regions.Take(16))
        {
            Console.WriteLine(
                $"  face={region.FaceIndex} mip={region.MipIndex} count={region.MipCount} size={region.Width}x{region.Height} bytes={region.DataSize} pitch={region.Pitch} slice={region.SlicePitch}");
        }
    }

    private static void RewriteTexture(string templateTexture, string imagePath, string outputTexture, bool forceUncompressed = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputTexture)) ?? ".");
        var image = new GltfImage
        {
            Name = Path.GetFileNameWithoutExtension(outputTexture),
            Data = File.ReadAllBytes(imagePath),
            MimeType = MimeTypeFromExtension(imagePath),
        };
        D3dtxWriter.WriteFromImageBytes(File.ReadAllBytes(templateTexture), image, outputTexture, forceUncompressed);
        var info = TextureLoader.InspectFormat(outputTexture);
        Console.WriteLine($"rewritten  : {outputTexture}");
        PrintTextureFormatRow(outputTexture, info);
    }

    private static string MimeTypeFromExtension(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };

    private static void ExtractAsset(string meshPath, string outputPath, string? skeletonPath)
    {
        GameConfig.Current = InferGameConfig(meshPath);
        var fullMeshPath = Path.GetFullPath(meshPath);
        var resolvedSkeletonPath = ResolveExtractSkeletonPath(fullMeshPath, skeletonPath);
        var inputRoot = Path.GetDirectoryName(fullMeshPath) ?? ".";
        var format = Path.GetExtension(outputPath).Equals(".gltf", StringComparison.OrdinalIgnoreCase)
            ? UI.ExportFormat.GltfSeparate
            : UI.ExportFormat.Glb;

        var asset = UI.ModelAsset.FromPaths(fullMeshPath, resolvedSkeletonPath);
        UI.ExtractionService.ExtractAssetToPath(asset, inputRoot, outputPath, format);

        Console.WriteLine($"extracted   : {outputPath}");
        Console.WriteLine($"template    : {Path.GetFileName(meshPath)}");
        Console.WriteLine($"skeleton    : {(resolvedSkeletonPath is null ? "(none)" : Path.GetFileName(resolvedSkeletonPath))}");
        Console.WriteLine($"game        : {GameConfig.Current.DisplayName}");
        if (format == UI.ExportFormat.Glb)
        {
            DumpGlb(outputPath);
        }
    }

    private static string? ResolveExtractSkeletonPath(string meshPath, string? skeletonPath)
    {
        if (!string.IsNullOrWhiteSpace(skeletonPath))
        {
            return Path.GetFullPath(skeletonPath);
        }

        var sameStem = Path.ChangeExtension(meshPath, ".skl");
        if (File.Exists(sameStem))
        {
            return sameStem;
        }

        // Match the viewer's discovery: a part mesh (sk54_lee_body) shares one character skeleton
        // (sk54_lee.skl), found by stem prefix across the folder.
        var folder = Path.GetDirectoryName(meshPath);
        return folder is not null
            ? Formats.Skeleton.SkeletonResolver.FindForMesh(folder, meshPath)
            : null;
    }

    private static void ReinsertProp(string template, string glb, string output, bool useDiffuseAtlas, bool matchOriginalSize = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        var templateBytes = File.ReadAllBytes(template);
        var gameConfig = ApplySavedReimportSettings(InferGameConfig(template));
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(glb), gameConfig);
        if (matchOriginalSize)
        {
            var templateBounds = D3DMeshParser.Parse(templateBytes).GetBounds();
            GltfModelScaler.MatchBounds(model, templateBounds);
            Console.WriteLine($"match-size  : scaled import to template size ({templateBounds.MaxX - templateBounds.MinX:0.###} x {templateBounds.MaxY - templateBounds.MinY:0.###} x {templateBounds.MaxZ - templateBounds.MinZ:0.###})");
        }
        if (useDiffuseAtlas)
        {
            model = StrippedLineTextureRecovery.RestoreStrippedTextures(model, template);
        }
        if (useDiffuseAtlas &&
            (gameConfig.InvertHeadLineAlphaOnReimport || gameConfig.InvertBodyLineAlphaOnReimport || gameConfig.InvertHandLineAlphaOnReimport))
        {
            model = CharacterLineAtlasFix.InvertCharacterLineAlpha(model, gameConfig);
        }
        var atlas = ApplyDiffuseAtlasIfRequested(model, useDiffuseAtlas, template, gameConfig);
        model = atlas.Model;

        if (BttfMeshSupport.IsBackToTheFutureMesh(templateBytes))
        {
            ReinsertBttfProp(templateBytes, template, glb, output, gameConfig, model, atlas);
            return;
        }

        if (D3DMeshParser.Parse(templateBytes).Version == 25)
        {
            var v25Tex = ReinsertTextureService.WriteV25ReferencedTextures(model, template, output, forceUncompressed: false);
            var v25Layout = D3DMeshLayout.BuildV25(templateBytes);
            var v25SourceLayout = MeshReinserter.TryFindV25SourceMaterialLayout(model, template, glb);
            var v25Result = MeshReinserter.ReinsertV25Geometry(v25Layout, model, v25Tex.PrimitiveSlots, v25SourceLayout);
            File.WriteAllBytes(output, v25Result);

            var v25Reparsed = D3DMeshParser.Parse(v25Result);
            Console.WriteLine($"reinserted  : {output} (V25 static)");
            Console.WriteLine($"template    : {Path.GetFileName(template)}");
            Console.WriteLine($"input       : {Path.GetFileName(glb)}");
            Console.WriteLine($"mesh        : {v25Result.Length} bytes, {v25Reparsed.Submeshes.Count} submeshes, {v25Reparsed.VertexCount} verts, {v25Reparsed.FaceCount} tris");
            PrintBounds("bounds      ", v25Reparsed.GetBounds());
            Console.WriteLine($"textures    : {v25Tex.Written.Count}");
            foreach (var name in v25Tex.Written)
            {
                Console.WriteLine($"  {name}.d3dtx");
            }
            foreach (var missing in v25Tex.TemplateNotFound)
            {
                Console.WriteLine($"  WARNING: no original .d3dtx template for referenced texture '{missing}' (left untouched).");
            }

            var distinctTex = ReinsertTextureService.DistinctV25TextureCount(model);
            if (distinctTex > v25Layout.Materials.Count && !MeshReinserter.CanAddV25Materials(v25Layout))
            {
                Console.WriteLine($"  WARNING: the model has {distinctTex} distinct textures but the template has " +
                    $"{v25Layout.Materials.Count} material(s) and they can't be extended for this mesh; some textures " +
                    "will repeat. Use a template with at least as many submeshes/materials as the model has textures.");
            }
            return;
        }

        var textureOptions = BuildReinsertTextureOptions(useDiffuseAtlas);
        var textures = ReinsertTextureService.WriteAllReferencedTextures(model, template, output, gameConfig, textureOptions);

        var layout = D3DMeshLayout.Build(templateBytes);
        var result = MeshReinserter.ReinsertGeometry(layout, model, textures, gameConfig: gameConfig);
        File.WriteAllBytes(output, result);

        var check = D3DMeshLayout.Build(result);
        var reparsed = D3DMeshParser.Parse(result);

        Console.WriteLine($"reinserted  : {output}");
        Console.WriteLine($"template    : {Path.GetFileName(template)}");
        Console.WriteLine($"input       : {Path.GetFileName(glb)}");
        Console.WriteLine($"game        : {gameConfig.DisplayName}");
        Console.WriteLine($"mesh        : {result.Length} bytes, {reparsed.Submeshes.Count} submeshes, {reparsed.VertexCount} verts, {reparsed.FaceCount} tris");
        PrintBounds("bounds      ", reparsed.GetBounds());
        Console.WriteLine($"textures    : {textures.WrittenNames.Count}");
        foreach (var name in textures.WrittenNames)
        {
            Console.WriteLine($"  {name}.d3dtx");
        }
        PrintAtlasSummary(atlas);

        Console.WriteLine(check.TailOffset + check.TailLength == result.Length
            ? "layout      : closes at EOF"
            : "layout      : warning, does not close at EOF");
    }

    private static void ReinsertBttfProp(
        byte[] templateBytes,
        string template,
        string glb,
        string output,
        GameConfig gameConfig,
        GltfModel model,
        GltfDiffuseAtlasResult atlas)
    {
        var textureCount = BttfMeshSupport.WriteAlignedTextures(template, output, model, uncompressed: false);
        var result = BttfMeshSupport.ReinsertGeometry(templateBytes, model, model.Skeleton);
        var removedBakeRefs = 0;
        if (gameConfig.ClearInheritedBakeOnReimport && !BttfMeshSupport.ModelDeclaresBake(model))
        {
            (result, removedBakeRefs) = BttfMeshSupport.BreakInheritedBakeReference(result, template);
        }

        File.WriteAllBytes(output, result);

        // Skinned BTTF model: rebuild the .skl from the GLB skeleton next to the output (ERTM header from
        // the target's own .skl).
        var skeletonLine = RebuildBttfSkeletonNextToOutput(template, output, model);

        var reparsed = D3DMeshParser.Parse(result);

        Console.WriteLine($"reinserted  : {output}");
        Console.WriteLine($"template    : {Path.GetFileName(template)} (Back to the Future v1)");
        Console.WriteLine($"input       : {Path.GetFileName(glb)}");
        Console.WriteLine($"game        : {gameConfig.DisplayName}");
        Console.WriteLine($"mesh        : {result.Length} bytes, {reparsed.Submeshes.Count} submeshes, {reparsed.VertexCount} verts, {reparsed.FaceCount} tris");
        PrintBounds("bounds      ", reparsed.GetBounds());
        Console.WriteLine($"textures    : {textureCount} (written under the template's own slot names, part-aligned)");
        if (removedBakeRefs > 0)
        {
            Console.WriteLine($"bake        : removed {removedBakeRefs} inherited lightmap reference(s) (model has no bake)");
        }
        if (!string.IsNullOrEmpty(skeletonLine))
        {
            Console.WriteLine(skeletonLine);
        }
        PrintAtlasSummary(atlas);

        Console.WriteLine(BttfMeshSupport.VerifyClosesAtEof(result)
            ? "layout      : closes at EOF"
            : "layout      : warning, does not close at EOF");
    }

    // Rebuilds the .skl next to a reinserted skinned BTTF mesh from the GLB skeleton, reusing the target
    // .skl's ERTM header. The target .skl is the one sharing the template mesh's name.
    private static string RebuildBttfSkeletonNextToOutput(string templateMeshPath, string output, GltfModel model)
    {
        if (model.Skeleton is null || model.Skeleton.Bones.Count == 0)
        {
            return "";
        }

        var referenceSkl = Path.ChangeExtension(templateMeshPath, ".skl");
        if (!File.Exists(referenceSkl))
        {
            return "skeleton    : skipped (no reference .skl next to the template).";
        }

        try
        {
            var sklBytes = BttfSkeletonWriter.Build(File.ReadAllBytes(referenceSkl), model.Skeleton);
            var sklOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".", Path.GetFileName(referenceSkl));
            File.WriteAllBytes(sklOutput, sklBytes);
            return $"skeleton    : {Path.GetFileName(referenceSkl)} rebuilt from GLB ({model.Skeleton.Bones.Count} bones)";
        }
        catch (Exception ex)
        {
            return $"skeleton    : could not rebuild .skl ({ex.Message})";
        }
    }

    // Generates two side-by-side Back to the Future test outputs for the same prop so the inherited-bake
    // handling can be compared in-game: A = neutralize the bake (white texture shipped), B = remove the
    // bake reference from the mesh (no bake shipped). Both map only the GLB's diffuse.
    private static void ReinsertBttfTextureTests(string template, string glb, string outRoot, bool matchOriginalSize)
    {
        var templateBytes = File.ReadAllBytes(template);
        if (!BttfMeshSupport.IsBackToTheFutureMesh(templateBytes))
        {
            Console.WriteLine("Not a Back to the Future (v1/ERTM) mesh; this command is BTTF-only.");
            return;
        }

        var gameConfig = ApplySavedReimportSettings(InferGameConfig(template));
        var meshName = Path.GetFileName(template);

        var dirA = Path.Combine(outRoot, "A_neutralize_bake");
        var dirB = Path.Combine(outRoot, "B_remove_bake_reference");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // Variant A: neutralize the inherited bake with a white texture.
        var outA = Path.Combine(dirA, meshName);
        var modelA = PrepareBttfTestModel(glb, gameConfig, templateBytes, matchOriginalSize);
        var texA = BttfMeshSupport.WriteAlignedTextures(template, outA, modelA, uncompressed: false);
        var bytesA = BttfMeshSupport.ReinsertGeometry(templateBytes, modelA, modelA.Skeleton);
        File.WriteAllBytes(outA, bytesA);
        var neutralBakes = BttfMeshSupport.NeutralizeInheritedBake(template, outA);

        // Variant B: remove the bake reference from the mesh; ship no bake texture.
        var outB = Path.Combine(dirB, meshName);
        var modelB = PrepareBttfTestModel(glb, gameConfig, templateBytes, matchOriginalSize);
        var texB = BttfMeshSupport.WriteAlignedTextures(template, outB, modelB, uncompressed: false);
        var bytesB = BttfMeshSupport.ReinsertGeometry(templateBytes, modelB, modelB.Skeleton);
        var (brokenB, replaced) = BttfMeshSupport.BreakInheritedBakeReference(bytesB, template);
        File.WriteAllBytes(outB, brokenB);

        Console.WriteLine($"template    : {meshName} (Back to the Future v1)");
        Console.WriteLine($"input       : {Path.GetFileName(glb)}");
        Console.WriteLine();
        Console.WriteLine($"A (neutralize bake) : {dirA}");
        Console.WriteLine($"  textures          : {texA}");
        Console.WriteLine($"  white bakes       : {neutralBakes}");
        Console.WriteLine($"  layout            : {(BttfMeshSupport.VerifyClosesAtEof(bytesA) ? "closes at EOF" : "WARNING not closed")}");
        Console.WriteLine();
        Console.WriteLine($"B (remove bake ref) : {dirB}");
        Console.WriteLine($"  textures          : {texB}");
        Console.WriteLine($"  bake refs removed : {replaced} (no bake .d3dtx shipped)");
        Console.WriteLine($"  layout            : {(BttfMeshSupport.VerifyClosesAtEof(brokenB) ? "closes at EOF" : "WARNING not closed")}");
    }

    private static GltfModel PrepareBttfTestModel(string glb, GameConfig gameConfig, byte[] templateBytes, bool matchOriginalSize)
    {
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(glb), gameConfig);
        if (matchOriginalSize)
        {
            GltfModelScaler.MatchBounds(model, D3DMeshParser.Parse(templateBytes).GetBounds());
        }

        return model;
    }

    private static void ReinsertCharacter(string template, string skeletonPath, string glb, string output, bool useDiffuseAtlas)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        var templateBytes0 = File.ReadAllBytes(template);
        if (D3DMeshParser.Parse(templateBytes0).Version == 25)
        {
            ReinsertV25Character(template, templateBytes0, skeletonPath, glb, output);
            return;
        }

        var layout = D3DMeshLayout.Build(File.ReadAllBytes(template));
        var skeleton = SkeletonLoader.Load(skeletonPath, layout.Version);
        var gameConfig = ApplySavedReimportSettings(InferGameConfig(template));
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(glb), gameConfig);
        if (useDiffuseAtlas)
        {
            model = StrippedLineTextureRecovery.RestoreStrippedTextures(model, template);
        }
        if (useDiffuseAtlas &&
            (gameConfig.InvertHeadLineAlphaOnReimport || gameConfig.InvertBodyLineAlphaOnReimport || gameConfig.InvertHandLineAlphaOnReimport))
        {
            model = CharacterLineAtlasFix.InvertCharacterLineAlpha(model, gameConfig);
        }
        var atlas = ApplyDiffuseAtlasIfRequested(model, useDiffuseAtlas, template, gameConfig);
        model = atlas.Model;
        var textureOptions = BuildReinsertTextureOptions(useDiffuseAtlas);
        var textures = ReinsertTextureService.WriteAllReferencedTextures(model, template, output, gameConfig, textureOptions);
        var result = MeshReinserter.ReinsertGeometry(layout, model, textures, skeleton, gameConfig);
        File.WriteAllBytes(output, result);

        string skeletonLine;
        if (model.Skeleton is not null && model.Skeleton.Bones.Count > 0)
        {
            var skeletonOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".", Path.GetFileName(skeletonPath));
            var skeletonBytes = RebuildSkeletonBytesForGame(skeletonPath, model.Skeleton, gameConfig);
            File.WriteAllBytes(skeletonOutput, skeletonBytes);
            skeletonLine = $"{Path.GetFileName(skeletonOutput)} ({model.Skeleton.Bones.Count} edited/imported bones)";
        }
        else
        {
            skeletonLine = "target skeleton kept; GLB has no skin and was bound as static geometry";
        }

        var check = D3DMeshLayout.Build(result);
        var reparsed = D3DMeshParser.Parse(result);

        Console.WriteLine($"reinserted  : {output}");
        Console.WriteLine($"template    : {Path.GetFileName(template)}");
        Console.WriteLine($"skeleton    : {skeletonLine}");
        Console.WriteLine($"input       : {Path.GetFileName(glb)}");
        Console.WriteLine($"game        : {gameConfig.DisplayName}");
        Console.WriteLine($"mesh        : {result.Length} bytes, {reparsed.Submeshes.Count} submeshes, {reparsed.VertexCount} verts, {reparsed.FaceCount} tris");
        Console.WriteLine($"palettes    : {reparsed.BonePalettes.Count}");
        PrintBounds("bounds      ", reparsed.GetBounds());
        Console.WriteLine($"textures    : {textures.WrittenNames.Count}");
        foreach (var name in textures.WrittenNames)
        {
            Console.WriteLine($"  {name}.d3dtx");
        }
        PrintAtlasSummary(atlas);

        Console.WriteLine(check.TailOffset + check.TailLength == result.Length
            ? "layout      : closes at EOF"
            : "layout      : warning, does not close at EOF");
    }

    private static ReinsertTextureOptions BuildReinsertTextureOptions(bool useDiffuseAtlas)
        => useDiffuseAtlas
            ? new ReinsertTextureOptions
            {
                NameMode = ReinsertTextureNameMode.PreferGltfNames,
                IncludedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "diffuse",
                    "detail_diffuse",
                    "tex7",
                    "tex8",
                    "bump",
                    "normal",
                    // Lightmap (bake) and baked shadow are per-object and not atlased, but must still be
                    // written/preserved so atlasing a lightmapped mesh does not drop its lightmap section.
                    "bake",
                    "shadow",
                },
            }
            : ReinsertTextureOptions.Default;

    // Name the atlas after existing template textures (the body/head diffuse + its normal) instead of the
    // generic "diffuse_atlas", so the atlas and its normal companion reuse real texture names the game already
    // references. Never a lines/detail map (ResolveAtlasTextureNames enforces it).
    private static GltfDiffuseAtlasResult ApplyDiffuseAtlasIfRequested(GltfModel model, bool useDiffuseAtlas, string templateMeshPath, GameConfig gameConfig)
        => useDiffuseAtlas
            ? GltfDiffuseAtlasPacker.Pack(model, BuildAtlasOptions(templateMeshPath, gameConfig))
            : new GltfDiffuseAtlasResult(model, Applied: false, SourceTextureCount: 0, AtlasWidth: 0, AtlasHeight: 0, AtlasName: "", Warnings: []);

    private static GltfDiffuseAtlasOptions BuildAtlasOptions(string templateMeshPath, GameConfig gameConfig)
    {
        var names = ReinsertTextureService.ResolveAtlasTextureNames(templateMeshPath);
        var packSharedPartsTextures = true;
        return names is null
            ? new GltfDiffuseAtlasOptions(PackSharedPartsTextures: packSharedPartsTextures)
            : new GltfDiffuseAtlasOptions(AtlasName: names.Diffuse, NormalAtlasName: names.Normal, DetailAtlasName: names.Detail, PackSharedPartsTextures: packSharedPartsTextures);
    }

    private static void PrintAtlasSummary(GltfDiffuseAtlasResult atlas)
    {
        if (!atlas.Applied)
        {
            if (atlas.SourceTextureCount == 1)
            {
                Console.WriteLine("diffuseAtlas: skipped because the model already uses one diffuse texture");
            }

            return;
        }

        Console.WriteLine($"diffuseAtlas: packed {atlas.SourceTextureCount} texture region(s) into one {atlas.AtlasWidth}x{atlas.AtlasHeight} atlas");
        foreach (var warning in atlas.Warnings.Take(3))
        {
            Console.WriteLine("diffuseAtlas warning: " + warning);
        }
    }

    private sealed record TextureTestVariant(
        string FolderName,
        ReinsertTextureNameMode NameMode,
        IReadOnlySet<string>? IncludedSlots,
        string Description,
        IReadOnlySet<string>? ExcludedDiffuseNames = null,
        IReadOnlyDictionary<string, string>? DiffuseSlotNameOverrides = null,
        IReadOnlyList<DiffuseTextureRewrite>? DiffuseTextureRewrites = null,
        IReadOnlyList<SlotTextureRewrite>? SlotTextureRewrites = null,
        TextureImageTransform DetailTextureTransform = TextureImageTransform.None,
        TextureImageTransform DiffuseTextureTransform = TextureImageTransform.None,
        IReadOnlyDictionary<string, TextureImageTransform>? DetailTextureTransformsByDiffuseName = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? RemovedSlotsByDiffuseName = null,
        VertexColorMode VertexColorMode = VertexColorMode.Preserve);

    private sealed record DiffuseTextureRewrite(
        string SourceDiffuseName,
        string OutputTextureName,
        string TemplateTextureName,
        TextureImageTransform Transform = TextureImageTransform.None);

    private sealed record SlotTextureRewrite(
        string SourceDiffuseName,
        string Slot,
        string SourceTextureName,
        string OutputTextureName,
        string TemplateTextureName,
        TextureImageTransform Transform = TextureImageTransform.None);

    private enum TextureImageTransform
    {
        None,
        OpaqueAlpha,
        Brighten15,
        OpaqueAlphaBrighten15,
        InvertRgb,
        InvertAlpha,
        InvertRgbAndAlpha,
        AlphaFromLuminanceBlack,
        AlphaFromInvertedLuminanceBlack,
        ReduceGreen15,
        ReduceGreen30,
        WarmShift,
        SwapRedBlue,
        LinearToSrgb,
        GammaToLinear,
    }

    private enum VertexColorMode
    {
        Preserve,
        ForceWhite,
    }

    // The Walking Dead: Michonne (V25) character path: same user-facing flow as the other games
    // (template .d3dmesh + target .skl + rigged GLB -> skinned .d3dmesh + a .skl rebuilt from the GLB
    // skeleton). The mesh's blend weights/indices are re-encoded against the template's bone palettes,
    // and the .skl is rebuilt via the shared SkeletonRebuilder (validated byte-identical on Michonne).
    private static void ReinsertV25Character(string template, byte[] templateBytes, string skeletonPath, string glb, string output)
    {
        var gameConfig = ApplySavedReimportSettings(InferGameConfig(template));
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(glb), gameConfig);

        var v25Tex = ReinsertTextureService.WriteV25ReferencedTextures(model, template, output, forceUncompressed: false);
        var layout = D3DMeshLayout.BuildV25(templateBytes);
        var sourceLayout = MeshReinserter.TryFindV25SourceMaterialLayout(model, template, glb);
        var result = MeshReinserter.ReinsertV25Geometry(layout, model, v25Tex.PrimitiveSlots, sourceLayout);
        File.WriteAllBytes(output, result);

        string skeletonLine;
        if (model.Skeleton is not null && model.Skeleton.Bones.Count > 0 && File.Exists(skeletonPath))
        {
            var skeletonOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".", Path.GetFileName(skeletonPath));
            var skeletonBytes = RebuildSkeletonBytesForGame(skeletonPath, model.Skeleton, gameConfig);
            File.WriteAllBytes(skeletonOutput, skeletonBytes);
            skeletonLine = $"{Path.GetFileName(skeletonOutput)} ({model.Skeleton.Bones.Count} bones from GLB)";
        }
        else
        {
            skeletonLine = "no GLB skin; mesh reinserted without rewriting the skeleton";
        }

        var reparsed = D3DMeshParser.Parse(result);
        Console.WriteLine($"reinserted  : {output} (V25 character)");
        Console.WriteLine($"template    : {Path.GetFileName(template)}");
        Console.WriteLine($"skeleton    : {skeletonLine}");
        Console.WriteLine($"input       : {Path.GetFileName(glb)}");
        Console.WriteLine($"mesh        : {result.Length} bytes, {reparsed.Submeshes.Count} submeshes, {reparsed.VertexCount} verts, {reparsed.FaceCount} tris");
        Console.WriteLine($"palettes    : {reparsed.BonePalettes.Count}");
        PrintBounds("bounds      ", reparsed.GetBounds());
        Console.WriteLine($"textures    : {v25Tex.Written.Count}");
        foreach (var name in v25Tex.Written)
        {
            Console.WriteLine($"  {name}.d3dtx");
        }
    }

    private static void ReinsertCharacterTextureTests(string template, string skeletonPath, string glb, string outputFolder)
    {
        var templateBytes = File.ReadAllBytes(template);
        var templateLayout = D3DMeshLayout.Build(templateBytes);
        var skeleton = SkeletonLoader.Load(skeletonPath, templateLayout.Version);
        var gameConfig = InferGameConfig(template);
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(glb), gameConfig);
        var runFolder = Path.Combine(
            outputFolder,
            "texture_tests_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runFolder);

        var diffuseOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "diffuse" };
        var diffuseBump = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "diffuse", "bump" };
        var diffuseDetail = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "diffuse", "detail_diffuse" };
        var diffuseBumpDetail = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "diffuse", "bump", "detail_diffuse" };
        var variants = new[]
        {
            new TextureTestVariant(
                "01_game_default",
                ReinsertTextureNameMode.GameDefault,
                null,
                $"Game default policy for {gameConfig.DisplayName}."),
            new TextureTestVariant(
                "02_semantic_diffuse_bump_detail",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Uses Omid semantic texture names for diffuse, bump and detail_diffuse; skips gradient/extra slots."),
            new TextureTestVariant(
                "03_semantic_diffuse_bump",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBump,
                "Uses Omid semantic texture names for diffuse and bump only."),
            new TextureTestVariant(
                "04_semantic_diffuse_detail",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Uses Omid semantic texture names for diffuse and detail_diffuse only."),
            new TextureTestVariant(
                "05_semantic_diffuse_only",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Uses Omid semantic texture names for diffuse slots only."),
            new TextureTestVariant(
                "06_gltf_names_diffuse_only",
                ReinsertTextureNameMode.PreferGltfNames,
                diffuseOnly,
                "Baseline from the previous working result: GLB names with diffuse slots only."),
            new TextureTestVariant(
                "07_semantic_no_eye_helpers",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Semantic Omid names, but removes the GLB eye helper primitives map_1px_alpha and color_000.",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "map_1px_alpha", "color_000" }),
            new TextureTestVariant(
                "08_semantic_diffuse_only_no_eye_helpers",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Semantic Omid diffuse names only, with the GLB eye helper primitives removed.",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "map_1px_alpha", "color_000" }),
            new TextureTestVariant(
                "09_hair_gltf_name",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Same as semantic default, but points Cryer's hair diffuse slot at sk54_cryer_hair.",
                DiffuseSlotNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sk54_cryer_hair"] = "sk54_cryer_hair",
                }),
            new TextureTestVariant(
                "10_hands_gltf_name",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Same as semantic default, but points Cryer's hands diffuse slot at sk54_cryer_hands.",
                DiffuseSlotNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sk54_cryer_hands"] = "sk54_cryer_hands",
                }),
            new TextureTestVariant(
                "11_hair_hands_gltf_names",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Same as semantic default, but points Cryer's hair and hands diffuse slots at their GLB names.",
                DiffuseSlotNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sk54_cryer_hair"] = "sk54_cryer_hair",
                    ["sk54_cryer_hands"] = "sk54_cryer_hands",
                }),
            new TextureTestVariant(
                "12_hands_reuse_body_name_control",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Control: points Cryer's hands at Omid's body texture name to test whether only the shader symbol fixes darkness.",
                DiffuseSlotNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sk54_cryer_hands"] = "sk54_omidflashback_body",
                }),
            new TextureTestVariant(
                "13_hair_original_name_head_template",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Rebuilds Cryer's hair diffuse using Omid's head texture template, but keeps the original Omid alphahair slot name.",
                DiffuseTextureRewrites: new[]
                {
                    new DiffuseTextureRewrite("sk54_cryer_hair", "sk54_omidflashback_alphahair", "sk54_omidflashback_head"),
                }),
            new TextureTestVariant(
                "14_hair_gltf_name_head_template_hands_gltf_name",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Rebuilds Cryer's hair using Omid's head texture template and GLB hair name; also uses GLB hands name.",
                DiffuseSlotNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sk54_cryer_hands"] = "sk54_cryer_hands",
                },
                DiffuseTextureRewrites: new[]
                {
                    new DiffuseTextureRewrite("sk54_cryer_hair", "sk54_cryer_hair", "sk54_omidflashback_head"),
                }),
            new TextureTestVariant(
                "15_hair_bump_detail",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Adds Cryer's hair bump and head-line detail slots to the otherwise working semantic setup.",
                SlotTextureRewrites: HairSlotRewrites(includeGradient: false)),
            new TextureTestVariant(
                "16_hair_bump_detail_gradient",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Adds Cryer's hair bump, head-line detail and male gradient slots to the semantic setup.",
                SlotTextureRewrites: HairSlotRewrites(includeGradient: true)),
            new TextureTestVariant(
                "17_hair_gltf_name_bump_detail_gradient",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Uses the GLB hair diffuse name and adds Cryer's hair bump/detail/gradient slots.",
                DiffuseSlotNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sk54_cryer_hair"] = "sk54_cryer_hair",
                },
                SlotTextureRewrites: HairSlotRewrites(includeGradient: true)),
            new TextureTestVariant(
                "18_hair_opaque_alpha",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Rebuilds Cryer's hair diffuse with fully opaque alpha to test whether alpha blending is darkening it.",
                DiffuseTextureRewrites: new[]
                {
                    new DiffuseTextureRewrite("sk54_cryer_hair", "sk54_omidflashback_alphahair", "sk54_omidflashback_alphahair", TextureImageTransform.OpaqueAlpha),
                }),
            new TextureTestVariant(
                "19_hair_opaque_alpha_bump_detail_gradient",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Opaque-alpha hair diffuse plus Cryer's hair bump/detail/gradient slots.",
                DiffuseTextureRewrites: new[]
                {
                    new DiffuseTextureRewrite("sk54_cryer_hair", "sk54_omidflashback_alphahair", "sk54_omidflashback_alphahair", TextureImageTransform.OpaqueAlpha),
                },
                SlotTextureRewrites: HairSlotRewrites(includeGradient: true)),
            new TextureTestVariant(
                "20_hair_brighten15",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Brightens Cryer's hair diffuse by 15 percent, preserving alpha.",
                DiffuseTextureRewrites: new[]
                {
                    new DiffuseTextureRewrite("sk54_cryer_hair", "sk54_omidflashback_alphahair", "sk54_omidflashback_alphahair", TextureImageTransform.Brighten15),
                }),
            new TextureTestVariant(
                "21_hair_opaque_brighten15",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Brightens Cryer's hair by 15 percent and forces opaque alpha.",
                DiffuseTextureRewrites: new[]
                {
                    new DiffuseTextureRewrite("sk54_cryer_hair", "sk54_omidflashback_alphahair", "sk54_omidflashback_alphahair", TextureImageTransform.OpaqueAlphaBrighten15),
                }),
            new TextureTestVariant(
                "22_lines_disabled_control",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBump,
                "Control for TWAU->TWD S2 swaps: semantic diffuse+bump only, with all detail/lines disabled."),
            new TextureTestVariant(
                "23_lines_only_no_bump",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Control for TWAU->TWD S2 swaps: semantic diffuse+detail lines only, with bump disabled."),
            new TextureTestVariant(
                "24_lines_invert_rgb",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Inverts RGB in every detail_diffuse/lines texture while preserving alpha.",
                DetailTextureTransform: TextureImageTransform.InvertRgb),
            new TextureTestVariant(
                "25_lines_invert_alpha",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Inverts alpha in every detail_diffuse/lines texture while preserving RGB.",
                DetailTextureTransform: TextureImageTransform.InvertAlpha),
            new TextureTestVariant(
                "26_lines_invert_rgb_alpha",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Inverts both RGB and alpha in every detail_diffuse/lines texture.",
                DetailTextureTransform: TextureImageTransform.InvertRgbAndAlpha),
            new TextureTestVariant(
                "27_lines_alpha_from_luminance_black",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Converts detail/lines RGB luminance into black-line alpha: black background becomes transparent.",
                DetailTextureTransform: TextureImageTransform.AlphaFromLuminanceBlack),
            new TextureTestVariant(
                "28_lines_alpha_from_inverted_luminance_black",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Converts inverted detail/lines RGB luminance into black-line alpha: bright background becomes transparent.",
                DetailTextureTransform: TextureImageTransform.AlphaFromInvertedLuminanceBlack),
            new TextureTestVariant(
                "29_diffuse_reduce_green15",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Diffuse-only color test: reduces green by 15 percent in every diffuse texture.",
                DiffuseTextureTransform: TextureImageTransform.ReduceGreen15),
            new TextureTestVariant(
                "30_diffuse_reduce_green30",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Diffuse-only color test: reduces green by 30 percent in every diffuse texture.",
                DiffuseTextureTransform: TextureImageTransform.ReduceGreen30),
            new TextureTestVariant(
                "31_diffuse_warm_shift",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Diffuse-only color test: warms the diffuse by lifting red/blue slightly and lowering green.",
                DiffuseTextureTransform: TextureImageTransform.WarmShift),
            new TextureTestVariant(
                "32_diffuse_swap_red_blue",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Diffuse-only channel-order test: swaps red and blue in every diffuse texture.",
                DiffuseTextureTransform: TextureImageTransform.SwapRedBlue),
            new TextureTestVariant(
                "33_diffuse_linear_to_srgb",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Diffuse-only color-space test: treats source RGB as linear and converts it to sRGB.",
                DiffuseTextureTransform: TextureImageTransform.LinearToSrgb),
            new TextureTestVariant(
                "34_diffuse_gamma_to_linear",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseOnly,
                "Diffuse-only color-space test: converts source sRGB-like RGB toward linear.",
                DiffuseTextureTransform: TextureImageTransform.GammaToLinear),
            new TextureTestVariant(
                "35_body_lines_only_no_bump",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Keeps detail/lines only on CrookedMan body, with bump disabled. Isolates the body line texture from head and hands.",
                RemovedSlotsByDiffuseName: RemoveSlotsForDiffuse(
                    "detail_diffuse",
                    "sk_sharedparts_mouth",
                    "sk54_crookedman_hands",
                    "sk54_crookedman_head",
                    "sk54_crookedman_hair")),
            new TextureTestVariant(
                "36_head_hands_lines_body_disabled_no_bump",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Keeps head/hands detail lines but disables the body detail line slot, with bump disabled.",
                RemovedSlotsByDiffuseName: RemoveSlotsForDiffuse(
                    "detail_diffuse",
                    "sk54_crookedman_body")),
            new TextureTestVariant(
                "37_body_lines_only_invert_rgb",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Body-only detail test: inverts RGB only on the CrookedMan body lines texture.",
                DetailTextureTransform: TextureImageTransform.InvertRgb,
                RemovedSlotsByDiffuseName: RemoveSlotsForDiffuse(
                    "detail_diffuse",
                    "sk_sharedparts_mouth",
                    "sk54_crookedman_hands",
                    "sk54_crookedman_head",
                    "sk54_crookedman_hair")),
            new TextureTestVariant(
                "38_body_lines_only_invert_alpha",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Body-only detail test: inverts alpha only on the CrookedMan body lines texture.",
                DetailTextureTransform: TextureImageTransform.InvertAlpha,
                RemovedSlotsByDiffuseName: RemoveSlotsForDiffuse(
                    "detail_diffuse",
                    "sk_sharedparts_mouth",
                    "sk54_crookedman_hands",
                    "sk54_crookedman_head",
                    "sk54_crookedman_hair")),
            new TextureTestVariant(
                "39_body_lines_only_alpha_luma_black",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Body-only detail test: converts body line RGB luminance into black-line alpha.",
                DetailTextureTransform: TextureImageTransform.AlphaFromLuminanceBlack,
                RemovedSlotsByDiffuseName: RemoveSlotsForDiffuse(
                    "detail_diffuse",
                    "sk_sharedparts_mouth",
                    "sk54_crookedman_hands",
                    "sk54_crookedman_head",
                    "sk54_crookedman_hair")),
            new TextureTestVariant(
                "40_body_lines_only_alpha_inv_luma_black",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Body-only detail test: converts inverted body line RGB luminance into black-line alpha.",
                DetailTextureTransform: TextureImageTransform.AlphaFromInvertedLuminanceBlack,
                RemovedSlotsByDiffuseName: RemoveSlotsForDiffuse(
                    "detail_diffuse",
                    "sk_sharedparts_mouth",
                    "sk54_crookedman_hands",
                    "sk54_crookedman_head",
                    "sk54_crookedman_hair")),
            new TextureTestVariant(
                "41_all_lines_body_invert_alpha_no_bump",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseDetail,
                "Keeps head/hands lines normal, but gives CrookedMan body a separate alpha-inverted body lines texture; bump disabled.",
                SlotTextureRewrites: CrookedManBodyLineAlphaInvertRewrite()),
            new TextureTestVariant(
                "42_all_lines_body_invert_alpha_with_bump",
                ReinsertTextureNameMode.SemanticTemplateNames,
                diffuseBumpDetail,
                "Keeps head/hands lines normal, but gives CrookedMan body a separate alpha-inverted body lines texture; bump enabled.",
                SlotTextureRewrites: CrookedManBodyLineAlphaInvertRewrite()),
        };

        var report = new List<string>
        {
            $"template : {Path.GetFullPath(template)}",
            $"skeleton : {Path.GetFullPath(skeletonPath)}",
            $"input    : {Path.GetFullPath(glb)}",
            $"game     : {gameConfig.DisplayName}",
            $"run      : {Path.GetFullPath(runFolder)}",
            "",
        };

        foreach (var variant in variants)
        {
            var variantFolder = Path.Combine(runFolder, variant.FolderName);
            Directory.CreateDirectory(variantFolder);
            var output = Path.Combine(variantFolder, Path.GetFileName(template));
            var layout = D3DMeshLayout.Build(templateBytes);
            var filteredModel = variant.ExcludedDiffuseNames is null
                ? model
                : GltfModelPreprocessor.FilterPrimitivesByDiffuseName(model, variant.ExcludedDiffuseNames);
            var variantModel = ApplyModelTestTweaks(filteredModel, variant);
            var options = new ReinsertTextureOptions
            {
                NameMode = variant.NameMode,
                IncludedSlots = variant.IncludedSlots,
            };
            var textures = ReinsertTextureService.WriteAllReferencedTextures(variantModel, template, output, gameConfig, options);
            textures = ApplyTextureTestTweaks(variantModel, textures, template, output, variant);
            var result = MeshReinserter.ReinsertGeometry(layout, variantModel, textures, skeleton, gameConfig);
            File.WriteAllBytes(output, result);

            string skeletonLine;
            if (variantModel.Skeleton is not null && variantModel.Skeleton.Bones.Count > 0)
            {
                var skeletonOutput = Path.Combine(variantFolder, Path.GetFileName(skeletonPath));
                var skeletonBytes = RebuildSkeletonBytesForGame(skeletonPath, variantModel.Skeleton, gameConfig);
                File.WriteAllBytes(skeletonOutput, skeletonBytes);
                skeletonLine = Path.GetFileName(skeletonOutput);
            }
            else
            {
                skeletonLine = "none";
            }

            var check = D3DMeshLayout.Build(result);
            var reparsed = D3DMeshParser.Parse(result);
            var layoutStatus = check.TailOffset + check.TailLength == result.Length ? "closes at EOF" : "layout warning";

            report.Add($"[{variant.FolderName}]");
            report.Add(variant.Description);
            report.Add($"mesh      : {Path.GetFullPath(output)}");
            report.Add($"skeleton  : {skeletonLine}");
            report.Add($"layout    : {layoutStatus}");
            report.Add($"submeshes : {reparsed.Submeshes.Count}");
            report.Add($"textures  : {textures.WrittenNames.Count}");
            foreach (var name in textures.WrittenNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                report.Add($"  file    : {name}.d3dtx");
            }

            for (var i = 0; i < reparsed.Submeshes.Count; i++)
            {
                var submesh = reparsed.Submeshes[i];
                var slots = submesh.TextureNames.Count == 0
                    ? "none"
                    : string.Join(", ", submesh.TextureNames.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
                report.Add($"  submesh {i}: {submesh.Name} | {slots}");
            }

            report.Add("");

            Console.WriteLine($"{variant.FolderName}: {textures.WrittenNames.Count} texture(s), {layoutStatus}");
        }

        var reportPath = Path.Combine(runFolder, "texture-tests-report.txt");
        File.WriteAllLines(reportPath, report);
        Console.WriteLine($"tests      : {runFolder}");
        Console.WriteLine($"report     : {reportPath}");
    }

    private static SlotTextureRewrite[] HairSlotRewrites(bool includeGradient)
    {
        var rewrites = new List<SlotTextureRewrite>
        {
            new(
                "sk54_cryer_hair",
                "bump",
                "sk54_cryer_hair_nm",
                "sk54_cryer_hair_nm",
                "sk54_omidflashback_head_nm"),
            new(
                "sk54_cryer_hair",
                "detail_diffuse",
                "sk54_cryer_head_lines",
                "sk54_cryer_head_lines",
                "sk54_omidflashback_head_detail"),
        };

        if (includeGradient)
        {
            rewrites.Add(new(
                "sk54_cryer_hair",
                "gradient",
                "map_gradientmale",
                "map_gradientmale",
                "sk54_omidflashback_head_detail"));
        }

        return rewrites.ToArray();
    }

    private static SlotTextureRewrite[] CrookedManBodyLineAlphaInvertRewrite()
        =>
        [
            new(
                "sk54_crookedman_body",
                "detail_diffuse",
                "sk54_crookedman_body_lines",
                "sk54_omidflashback_body_detail_body_alpha_invert",
                "sk54_omidflashback_body_detail",
                TextureImageTransform.InvertAlpha),
        ];

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> RemoveSlotsForDiffuse(
        string slot,
        params string[] diffuseNames)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var diffuseName in diffuseNames)
        {
            result[diffuseName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeTextureSlotName(slot),
            };
        }

        return result;
    }

    private static GltfModel ApplyModelTestTweaks(GltfModel model, TextureTestVariant variant)
    {
        if (variant.VertexColorMode == VertexColorMode.Preserve)
        {
            return model;
        }

        return new GltfModel
        {
            Joints = model.Joints,
            Skeleton = model.Skeleton,
            Primitives = model.Primitives
                .Select(primitive => ApplyPrimitiveVertexColorMode(primitive, variant.VertexColorMode))
                .ToList(),
        };
    }

    private static GltfPrimitive ApplyPrimitiveVertexColorMode(GltfPrimitive primitive, VertexColorMode mode)
    {
        if (mode != VertexColorMode.ForceWhite)
        {
            return primitive;
        }

        var white = Enumerable.Repeat(new Vector4(1f, 1f, 1f, 1f), primitive.VertexCount).ToArray();
        return new GltfPrimitive
        {
            Positions = primitive.Positions,
            Normals = primitive.Normals,
            Uv0 = primitive.Uv0,
            Uv1 = primitive.Uv1,
            Uv2 = primitive.Uv2,
            Uv3 = primitive.Uv3,
            Color0 = white,
            Tangents = primitive.Tangents,
            Binormals = primitive.Binormals,
            Unknown1 = primitive.Unknown1,
            Joints0 = primitive.Joints0,
            Weights0 = primitive.Weights0,
            Indices = primitive.Indices,
            MaterialName = primitive.MaterialName,
            BonePaletteIndex = primitive.BonePaletteIndex,
            SourceMeshPath = primitive.SourceMeshPath,
            SourceSubmeshIndex = primitive.SourceSubmeshIndex,
            IsSkinned = primitive.IsSkinned,
            BaseColor = primitive.BaseColor,
            TextureSlots = primitive.TextureSlots,
            ReferencedTextures = primitive.ReferencedTextures,
        };
    }

    private static ReinsertedTextures ApplyTextureTestTweaks(
        GltfModel model,
        ReinsertedTextures textures,
        string template,
        string output,
        TextureTestVariant variant)
    {
        if (variant.DiffuseSlotNameOverrides is null &&
            variant.DiffuseTextureRewrites is null &&
            variant.SlotTextureRewrites is null &&
            variant.DetailTextureTransform == TextureImageTransform.None &&
            variant.DiffuseTextureTransform == TextureImageTransform.None &&
            variant.DetailTextureTransformsByDiffuseName is null &&
            variant.RemovedSlotsByDiffuseName is null)
        {
            return textures;
        }

        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".";
        var diffuseOverrides = variant.DiffuseSlotNameOverrides is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(variant.DiffuseSlotNameOverrides, StringComparer.OrdinalIgnoreCase);
        var writtenNames = textures.WrittenNames.ToList();

        if (variant.DiffuseTextureRewrites is not null)
        {
            foreach (var rewrite in variant.DiffuseTextureRewrites)
            {
                var image = FindDiffuseImage(model, rewrite.SourceDiffuseName);
                var templateTexture = FindTemplateTextureByName(template, rewrite.TemplateTextureName);
                if (image is null || templateTexture is null)
                {
                    continue;
                }

                var outputTexturePath = Path.Combine(outputFolder, rewrite.OutputTextureName + ".d3dtx");
                WriteTextureRewrite(templateTexture, image, outputTexturePath, rewrite.Transform);
                diffuseOverrides[rewrite.SourceDiffuseName] = rewrite.OutputTextureName;
                writtenNames.Add(rewrite.OutputTextureName);
            }
        }

        var primitiveSlots = new List<IReadOnlyDictionary<string, string>>(textures.PrimitiveSlots.Count);
        for (var i = 0; i < textures.PrimitiveSlots.Count; i++)
        {
            var slots = new Dictionary<string, string>(textures.PrimitiveSlots[i], StringComparer.OrdinalIgnoreCase);
            var sourceDiffuseName = i < model.Primitives.Count ? GetSourceDiffuseName(model.Primitives[i]) : null;
            if (i < model.Primitives.Count &&
                model.Primitives[i].TextureSlots.TryGetValue("diffuse", out var diffuse) &&
                diffuseOverrides.TryGetValue(diffuse.Name, out var replacementName))
            {
                if (slots.TryGetValue("diffuse", out var currentName) &&
                    !currentName.Equals(replacementName, StringComparison.OrdinalIgnoreCase))
                {
                    CopyTextureForSlotOverride(outputFolder, currentName, replacementName);
                    writtenNames.Add(replacementName);
                }

                slots["diffuse"] = replacementName;
            }

            RemoveVariantSlots(slots, sourceDiffuseName, variant.RemovedSlotsByDiffuseName);

            if (i < model.Primitives.Count && variant.DiffuseTextureTransform != TextureImageTransform.None)
            {
                ApplyDiffuseTextureTransform(model.Primitives[i], slots, outputFolder, variant.DiffuseTextureTransform);
            }

            if (i < model.Primitives.Count && variant.SlotTextureRewrites is not null)
            {
                ApplySlotTextureRewrites(model.Primitives[i], slots, template, outputFolder, variant.SlotTextureRewrites, writtenNames);
            }

            var detailTransform = ResolveDetailTextureTransform(variant, sourceDiffuseName);
            if (i < model.Primitives.Count && detailTransform != TextureImageTransform.None)
            {
                ApplyDetailTextureTransform(model.Primitives[i], slots, outputFolder, detailTransform);
            }

            primitiveSlots.Add(slots);
        }

        return new ReinsertedTextures(
            primitiveSlots,
            writtenNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? GetSourceDiffuseName(GltfPrimitive primitive)
        => primitive.TextureSlots.TryGetValue("diffuse", out var diffuse)
            ? diffuse.Name
            : null;

    private static void RemoveVariantSlots(
        Dictionary<string, string> slots,
        string? sourceDiffuseName,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? removedSlotsByDiffuseName)
    {
        if (sourceDiffuseName is null ||
            removedSlotsByDiffuseName is null ||
            !removedSlotsByDiffuseName.TryGetValue(sourceDiffuseName, out var removedSlots))
        {
            return;
        }

        foreach (var removedSlot in removedSlots)
        {
            slots.Remove(NormalizeTextureSlotName(removedSlot));
        }
    }

    private static TextureImageTransform ResolveDetailTextureTransform(TextureTestVariant variant, string? sourceDiffuseName)
    {
        if (sourceDiffuseName is not null &&
            variant.DetailTextureTransformsByDiffuseName is not null &&
            variant.DetailTextureTransformsByDiffuseName.TryGetValue(sourceDiffuseName, out var transform))
        {
            return transform;
        }

        return variant.DetailTextureTransform;
    }

    private static void ApplyDiffuseTextureTransform(
        GltfPrimitive primitive,
        Dictionary<string, string> slots,
        string outputFolder,
        TextureImageTransform transform)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuseImage) ||
            !slots.TryGetValue("diffuse", out var outputTextureName))
        {
            return;
        }

        var outputTexturePath = Path.Combine(outputFolder, outputTextureName + ".d3dtx");
        if (!File.Exists(outputTexturePath))
        {
            return;
        }

        WriteTextureRewrite(outputTexturePath, diffuseImage, outputTexturePath, transform);
    }

    private static void ApplyDetailTextureTransform(
        GltfPrimitive primitive,
        Dictionary<string, string> slots,
        string outputFolder,
        TextureImageTransform transform)
    {
        if (!primitive.TextureSlots.TryGetValue("detail_diffuse", out var detailImage) ||
            !slots.TryGetValue("detail_diffuse", out var outputTextureName))
        {
            return;
        }

        var outputTexturePath = Path.Combine(outputFolder, outputTextureName + ".d3dtx");
        if (!File.Exists(outputTexturePath))
        {
            return;
        }

        WriteTextureRewrite(outputTexturePath, detailImage, outputTexturePath, transform);
    }

    private static void ApplySlotTextureRewrites(
        GltfPrimitive primitive,
        Dictionary<string, string> slots,
        string template,
        string outputFolder,
        IReadOnlyList<SlotTextureRewrite> rewrites,
        List<string> writtenNames)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse))
        {
            return;
        }

        foreach (var rewrite in rewrites)
        {
            if (!diffuse.Name.Equals(rewrite.SourceDiffuseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var image = FindPrimitiveTextureImage(primitive, rewrite.Slot, rewrite.SourceTextureName);
            var templateTexture = FindTemplateTextureByName(template, rewrite.TemplateTextureName);
            if (image is null || templateTexture is null)
            {
                continue;
            }

            var outputTexturePath = Path.Combine(outputFolder, rewrite.OutputTextureName + ".d3dtx");
            WriteTextureRewrite(templateTexture, image, outputTexturePath, rewrite.Transform);
            slots[NormalizeTextureSlotName(rewrite.Slot)] = rewrite.OutputTextureName;
            writtenNames.Add(rewrite.OutputTextureName);
        }
    }

    private static GltfImage? FindPrimitiveTextureImage(GltfPrimitive primitive, string slot, string textureName)
    {
        var normalizedSlot = NormalizeTextureSlotName(slot);
        if (primitive.TextureSlots.TryGetValue(normalizedSlot, out var slotImage) &&
            (string.IsNullOrWhiteSpace(textureName) ||
             slotImage.Name.Equals(textureName, StringComparison.OrdinalIgnoreCase)))
        {
            return slotImage;
        }

        foreach (var image in primitive.TextureSlots.Values.Concat(primitive.ReferencedTextures.Values))
        {
            if (image.Name.Equals(textureName, StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }
        }

        return null;
    }

    private static void WriteTextureRewrite(
        string templateTexture,
        GltfImage image,
        string outputTexturePath,
        TextureImageTransform transform)
    {
        var outputImage = transform == TextureImageTransform.None
            ? image
            : TransformImage(image, transform);
        D3dtxWriter.WriteFromImageBytes(File.ReadAllBytes(templateTexture), outputImage, outputTexturePath);
    }

    private static GltfImage TransformImage(GltfImage image, TextureImageTransform transform)
    {
        using var input = new MemoryStream(image.Data);
        using var source = new Bitmap(input);
        using var output = new Bitmap(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);
                var alpha = (int)color.A;
                var red = (int)color.R;
                var green = (int)color.G;
                var blue = (int)color.B;
                if (transform is TextureImageTransform.OpaqueAlpha or TextureImageTransform.OpaqueAlphaBrighten15)
                {
                    alpha = 255;
                }

                if (transform is TextureImageTransform.Brighten15 or TextureImageTransform.OpaqueAlphaBrighten15)
                {
                    red = ClampColor(red * 1.15f);
                    green = ClampColor(green * 1.15f);
                    blue = ClampColor(blue * 1.15f);
                }

                if (transform is TextureImageTransform.InvertRgb or TextureImageTransform.InvertRgbAndAlpha)
                {
                    red = 255 - red;
                    green = 255 - green;
                    blue = 255 - blue;
                }

                if (transform is TextureImageTransform.InvertAlpha or TextureImageTransform.InvertRgbAndAlpha)
                {
                    alpha = 255 - alpha;
                }

                if (transform is TextureImageTransform.AlphaFromLuminanceBlack or TextureImageTransform.AlphaFromInvertedLuminanceBlack)
                {
                    var luminance = ClampColor(red * 0.299f + green * 0.587f + blue * 0.114f);
                    alpha = transform == TextureImageTransform.AlphaFromLuminanceBlack
                        ? luminance
                        : 255 - luminance;
                    red = 0;
                    green = 0;
                    blue = 0;
                }

                if (transform == TextureImageTransform.ReduceGreen15)
                {
                    green = ClampColor(green * 0.85f);
                }

                if (transform == TextureImageTransform.ReduceGreen30)
                {
                    green = ClampColor(green * 0.70f);
                }

                if (transform == TextureImageTransform.WarmShift)
                {
                    red = ClampColor(red * 1.08f);
                    green = ClampColor(green * 0.90f);
                    blue = ClampColor(blue * 1.05f);
                }

                if (transform == TextureImageTransform.SwapRedBlue)
                {
                    (red, blue) = (blue, red);
                }

                if (transform == TextureImageTransform.LinearToSrgb)
                {
                    red = LinearToSrgbByte(red);
                    green = LinearToSrgbByte(green);
                    blue = LinearToSrgbByte(blue);
                }

                if (transform == TextureImageTransform.GammaToLinear)
                {
                    red = GammaToLinearByte(red);
                    green = GammaToLinearByte(green);
                    blue = GammaToLinearByte(blue);
                }

                output.SetPixel(x, y, Color.FromArgb(alpha, red, green, blue));
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

    private static int ClampColor(float value)
        => (int)Math.Clamp(MathF.Round(value), 0, 255);

    private static int LinearToSrgbByte(int value)
    {
        var normalized = Math.Clamp(value / 255f, 0f, 1f);
        return ClampColor(MathF.Pow(normalized, 1f / 2.2f) * 255f);
    }

    private static int GammaToLinearByte(int value)
    {
        var normalized = Math.Clamp(value / 255f, 0f, 1f);
        return ClampColor(MathF.Pow(normalized, 2.2f) * 255f);
    }

    private static GltfImage? FindDiffuseImage(GltfModel model, string sourceDiffuseName)
    {
        foreach (var primitive in model.Primitives)
        {
            if (primitive.TextureSlots.TryGetValue("diffuse", out var image) &&
                image.Name.Equals(sourceDiffuseName, StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }
        }

        return null;
    }

    private static void CopyTextureForSlotOverride(string outputFolder, string currentName, string replacementName)
    {
        var source = Path.Combine(outputFolder, currentName + ".d3dtx");
        var target = Path.Combine(outputFolder, replacementName + ".d3dtx");
        if (File.Exists(target) || !File.Exists(source))
        {
            return;
        }

        D3dtxWriter.WriteRenamedCopy(File.ReadAllBytes(source), target);
    }

    private static string? FindTemplateTextureByName(string templateMeshPath, string textureName)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(templateMeshPath));
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        var stem = StripKnownTextureExtension(Path.GetFileName(textureName));
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
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                .Equals(stem, StringComparison.OrdinalIgnoreCase));
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

    private static string NormalizeTextureSlotName(string slot)
        => slot.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "bump" : slot;

    private static byte[] RebuildSkeletonBytesForGame(string skeletonPath, SkeletonData skeleton, GameConfig gameConfig)
        => SkeletonRebuilder.RebuildWithEdits(skeletonPath, skeleton, gameConfig);

    private static void PrintAttrs(VertexAttrLayout a)
    {
        PrintAttr("position", a.Position);
        PrintAttr("uv1", a.Uv1);
        PrintAttr("normal", a.Normals);
        PrintAttr("weights", a.Weights);
        PrintAttr("bones", a.Bones);
        PrintAttr("color", a.Colors);
        PrintAttr("unknown1", a.Unknown1);
        PrintAttr("binormal", a.Binormals);
        PrintAttr("tangent", a.Tangents);
        PrintAttr("uv2", a.Uv2);
        PrintAttr("uv3", a.Uv3);
        PrintAttr("uv4", a.Uv4);
        if (a.Uv5.Format != 0)
        {
            PrintAttr("uv5", a.Uv5);
        }
    }

    private static void PrintAttr(string name, AttrDescriptor attr)
        => Console.WriteLine($"  {name,-8} offset={attr.Offset,3} count={attr.Count,3} format={attr.Format,2}");

    // Runs the same combined-group reimport as the UI (part split, skeleton rebuild, companion
    // variant ports), so the full character-swap flow can be exercised and validated headlessly.
    private static void ReinsertCombined(string inputRoot, string groupName, string glb, string outputFolder, bool useDiffuseAtlas)
    {
        GameConfig.Current = ApplySavedReimportSettings(InferGameConfig(inputRoot));
        var assets = UI.ModelAsset.Discover(inputRoot);
        var groups = UI.ModelAssetGroup.Discover(assets, inputRoot);
        var group = groups.FirstOrDefault(candidate => candidate.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(candidate => candidate.Name.Contains(groupName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No combined group matches '{groupName}'. Available: {string.Join(", ", groups.Select(candidate => candidate.Name).Take(20))}");

        var model = GltfReader.Load(glb);
        var result = UI.MainForm.ReimportCombinedGroup(
            group,
            inputRoot,
            model,
            glb,
            outputFolder,
            useDiffuseAtlas,
            uncompressedTextures: false,
            normalizeFacialBonesOnReimport: AppPreferences.Load().NormalizeFacialBonesOnReimport);
        Console.WriteLine($"game        : {GameConfig.Current.DisplayName}");
        Console.WriteLine(result);
    }

    private static GameConfig InferGameConfig(string path)
    {
        var text = Path.GetFullPath(path);
        if (text.Contains("TWDS2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead: Season 2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead Season 2", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.WalkingDeadSeason2;
        }

        if (text.Contains("TWDM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead: Michonne", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead Michonne", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Michonne", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.WalkingDeadMichonne;
        }

        if (text.Contains("TWAU", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wolf Among Us", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.WolfAmongUs;
        }

        if (text.Contains("MCSM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Minecraft Story Mode", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Minecraft: Story Mode", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.MinecraftStoryMode;
        }

        if (IsGameOfThronesPath(text))
        {
            return GameConfig.GameOfThrones;
        }

        if (IsTalesFromTheBorderlandsOldPath(text))
        {
            return GameConfig.TalesFromTheBorderlandsOld;
        }

        if (IsTalesFromTheBorderlandsE3Path(text))
        {
            return GameConfig.TalesFromTheBorderlandsE3;
        }

        if (IsTalesFromTheBorderlands2014Path(text))
        {
            return GameConfig.TalesFromTheBorderlands2014;
        }

        if (text.Contains("BTTF", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Back to the Future", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BackToTheFuture", StringComparison.OrdinalIgnoreCase))
        {
            return InferBackToTheFutureConfig(text);
        }

        return GameConfig.Current;
    }

    private static bool IsTalesFromTheBorderlands2014Path(string text)
    {
        if (text.Contains("2021", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Contains("TFTB2014", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TftBL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Tales from the Borderlands", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Borderlands", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameOfThronesPath(string text)
        => text.Contains("Game of Thrones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("GameOfThrones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("Telltale Games Series", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("GOT _", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("GOT_", StringComparison.OrdinalIgnoreCase);

    private static bool IsTalesFromTheBorderlandsOldPath(string text)
        => !text.Contains("2021", StringComparison.OrdinalIgnoreCase) &&
           (text.Contains("Source Code Leaked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TFTBOLD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tales from the Borderlands (Old)", StringComparison.OrdinalIgnoreCase));

    private static bool IsTalesFromTheBorderlandsE3Path(string text)
        => !text.Contains("2021", StringComparison.OrdinalIgnoreCase) &&
           (text.Contains("TFTBE3", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("E3 Leak", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TFTB E3", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tales from the Borderlands E3", StringComparison.OrdinalIgnoreCase));

    private static GameConfig InferBackToTheFutureConfig(string text)
    {
        if (ContainsEpisodeMarker(text, 1, "101"))
        {
            return GameConfig.BackToTheFutureEpisode1;
        }
        if (ContainsEpisodeMarker(text, 2, "102"))
        {
            return GameConfig.BackToTheFutureEpisode2;
        }
        if (ContainsEpisodeMarker(text, 3, "103"))
        {
            return GameConfig.BackToTheFutureEpisode3;
        }
        if (ContainsEpisodeMarker(text, 4, "104"))
        {
            return GameConfig.BackToTheFutureEpisode4;
        }
        if (ContainsEpisodeMarker(text, 5, "105"))
        {
            return GameConfig.BackToTheFutureEpisode5;
        }

        return GameConfig.BackToTheFuture;
    }

    private static bool ContainsEpisodeMarker(string text, int episode, string archiveId)
        => text.Contains($"Ep{episode}", StringComparison.OrdinalIgnoreCase) ||
           text.Contains($"Episode {episode}", StringComparison.OrdinalIgnoreCase) ||
           text.Contains($"Episode{episode}", StringComparison.OrdinalIgnoreCase) ||
           text.Contains($"BTTF{archiveId}", StringComparison.OrdinalIgnoreCase) ||
           text.Contains($"BackToTheFuture{archiveId}", StringComparison.OrdinalIgnoreCase);

    private static (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) GetBounds(GltfModel model)
    {
        var positions = model.Primitives.SelectMany(primitive => primitive.Positions).ToArray();
        if (positions.Length == 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        return (
            positions.Min(v => v.X),
            positions.Min(v => v.Y),
            positions.Min(v => v.Z),
            positions.Max(v => v.X),
            positions.Max(v => v.Y),
            positions.Max(v => v.Z));
    }

    private static (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) GetSkeletonBounds(SkeletonData skeleton)
    {
        var world = BuildSkeletonWorldPositions(skeleton);
        if (world.Length == 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        return BoundsOf(world);
    }

    private static (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) BoundsOf(IReadOnlyList<Vector3> positions)
    {
        if (positions.Count == 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        return (
            positions.Min(v => v.X),
            positions.Min(v => v.Y),
            positions.Min(v => v.Z),
            positions.Max(v => v.X),
            positions.Max(v => v.Y),
            positions.Max(v => v.Z));
    }

    private static Vector3[] BuildSkeletonWorldPositions(SkeletonData skeleton)
    {
        var matrices = new Matrix4x4[skeleton.Bones.Count];
        var state = new byte[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            BuildSkeletonWorldMatrix(skeleton, i, matrices, state);
        }

        var result = new Vector3[skeleton.Bones.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Vector3.Transform(Vector3.Zero, matrices[i]);
        }

        return result;
    }

    private static Matrix4x4 BuildSkeletonWorldMatrix(SkeletonData skeleton, int index, Matrix4x4[] matrices, byte[] state)
    {
        if (state[index] == 2)
        {
            return matrices[index];
        }

        if (state[index] == 1)
        {
            return Matrix4x4.Identity;
        }

        state[index] = 1;
        var bone = skeleton.Bones[index];
        var rotation = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw);
        if (rotation.LengthSquared() < 0.000001f)
        {
            rotation = Quaternion.Identity;
        }
        else
        {
            rotation = Quaternion.Normalize(rotation);
        }

        var local = Matrix4x4.CreateFromQuaternion(rotation) *
                    Matrix4x4.CreateTranslation(bone.X, bone.Y, bone.Z);
        if (bone.ParentIndex >= 0 && bone.ParentIndex < skeleton.Bones.Count)
        {
            matrices[index] = local * BuildSkeletonWorldMatrix(skeleton, bone.ParentIndex, matrices, state);
        }
        else
        {
            matrices[index] = local;
        }

        state[index] = 2;
        return matrices[index];
    }

    private readonly record struct CliBoneBox(
        float MinX,
        float MinY,
        float MinZ,
        float MaxX,
        float MaxY,
        float MaxZ)
    {
        public static CliBoneBox From(float x, float y, float z) => new(x, y, z, x, y, z);

        public CliBoneBox Include(float x, float y, float z) => new(
            Math.Min(MinX, x),
            Math.Min(MinY, y),
            Math.Min(MinZ, z),
            Math.Max(MaxX, x),
            Math.Max(MaxY, y),
            Math.Max(MaxZ, z));
    }

    private static void PrintBounds(string label, (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) b)
    {
        var size = new Vector3(b.MaxX - b.MinX, b.MaxY - b.MinY, b.MaxZ - b.MinZ);
        Console.WriteLine(
            label + ": " +
            $"min=({F(b.MinX)}, {F(b.MinY)}, {F(b.MinZ)}) " +
            $"max=({F(b.MaxX)}, {F(b.MaxY)}, {F(b.MaxZ)}) " +
            $"size=({F(size.X)}, {F(size.Y)}, {F(size.Z)})");
    }

    private static string F(float value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static GameConfig ApplySavedReimportSettings(GameConfig gameConfig)
        => gameConfig.WithNormalizeFacialBonesOnReimport(AppPreferences.Load().NormalizeFacialBonesOnReimport);
}
