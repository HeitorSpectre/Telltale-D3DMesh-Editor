using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;

namespace TelltaleD3DMeshEditor.Export;

// GLB writer: writes the complete asset (mesh + embedded baseColor PNG textures + skeleton
// with skin/inverseBind) into one self-contained .glb file. Ideal for opening directly in Blender.
public static class GltfWriter
{
    public static void WriteCompleteAssetGlb(
        MeshData mesh,
        SkeletonData? skeleton,
        IReadOnlyDictionary<string, (byte[] Png, float AvgAlpha)> baseColorPngByName,
        string path,
        IReadOnlyDictionary<string, byte[]>? auxiliaryPngByName = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var built = AssetGltfBuilder.Build(mesh, skeleton, baseColorPngByName, auxiliaryPngByName);

        var bin = built.Bin;
        var bufferViews = (List<Dictionary<string, object>>)built.Gltf["bufferViews"];
        var images = new List<Dictionary<string, object>>();
        foreach (var (name, png) in built.Images)
        {
            GltfCommon.AddEmbeddedPngImage(bin, bufferViews, images, name, png);
        }

        if (images.Count > 0)
        {
            built.Gltf["images"] = images;
        }
        built.Gltf["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = bin.Count } };

        GltfCommon.WriteGlbContainer(built.Gltf, bin.ToArray(), path);
    }
}
