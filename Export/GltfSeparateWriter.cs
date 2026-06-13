using System.Text.Json;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Export;

// Separate GLTF writer: writes <name>.gltf (JSON) and <name>.bin (geometry buffer) side by side,
// plus textures as .png files inside a "textures/" subfolder. Useful for inspection/editing.
public static class GltfSeparateWriter
{
    public static void WriteCompleteAssetGltf(
        MeshData mesh,
        SkeletonData? skeleton,
        IReadOnlyDictionary<string, (byte[] Png, float AvgAlpha)> baseColorPngByName,
        string path,
        IReadOnlyDictionary<string, byte[]>? auxiliaryPngByName = null)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var baseName = Path.GetFileNameWithoutExtension(path);

        var built = AssetGltfBuilder.Build(mesh, skeleton, baseColorPngByName, auxiliaryPngByName);

        // Geometry buffer as an external .bin file.
        var binFileName = baseName + ".bin";
        File.WriteAllBytes(Path.Combine(folder, binFileName), built.Bin.ToArray());
        built.Gltf["buffers"] = new[]
        {
            new Dictionary<string, object>
            {
                ["uri"] = Uri.EscapeDataString(binFileName),
                ["byteLength"] = built.Bin.Count,
            },
        };

        // Each image becomes a .png in the "textures/" subfolder; the list index matches
        // gltf["images"]. The .gltf references them through relative "textures/<file>.png" URIs.
        if (built.Images.Count > 0)
        {
            const string texturesDir = "textures";
            Directory.CreateDirectory(Path.Combine(folder, texturesDir));
            var images = new List<Dictionary<string, object>>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, png) in built.Images)
            {
                var fileName = UniquePngName(name, baseName, usedNames);
                File.WriteAllBytes(Path.Combine(folder, texturesDir, fileName), png);
                images.Add(new Dictionary<string, object>
                {
                    ["uri"] = $"{texturesDir}/{Uri.EscapeDataString(fileName)}",
                    ["mimeType"] = "image/png",
                    ["name"] = name,
                });
            }
            built.Gltf["images"] = images;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(built.Gltf, GltfCommon.JsonOptions);
        File.WriteAllBytes(path, json);
    }

    private static string UniquePngName(string textureName, string baseName, HashSet<string> used)
    {
        var stem = Sanitize(textureName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = baseName + "_tex";
        }

        var candidate = stem + ".png";
        var counter = 1;
        while (!used.Add(candidate))
        {
            candidate = $"{stem}_{counter++}.png";
        }

        return candidate;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
