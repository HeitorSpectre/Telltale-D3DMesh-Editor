using System.Drawing.Imaging;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Texture;

namespace TelltaleD3DMeshEditor.Export;

// Exports only the raw diffuse/albedo as baseColor. Other Telltale material textures
// (detail/lines, bump/normal, bake, shadow, etc.) remain separate in images/textures + extras.
public static class BaseColorExporter
{
    public static Dictionary<string, (byte[] Png, float AvgAlpha)> BuildRawDiffuse(
        MeshData mesh,
        IReadOnlyDictionary<int, MaterialTextureSet> texturesBySubmesh)
    {
        var result = new Dictionary<string, (byte[] Png, float AvgAlpha)>(StringComparer.OrdinalIgnoreCase);

        for (var s = 0; s < mesh.Submeshes.Count; s++)
        {
            if (!texturesBySubmesh.TryGetValue(s, out var set) || set.Diffuse is null)
            {
                continue;
            }

            var submesh = mesh.Submeshes[s];
            var diffuseName = submesh.TextureNames.TryGetValue("diffuse", out var dn) && !string.IsNullOrWhiteSpace(dn)
                ? dn
                : submesh.Name;
            if (result.ContainsKey(diffuseName))
            {
                continue;
            }

            result[diffuseName] = (ToPng(set.Diffuse), set.Diffuse.AverageAlpha);
        }

        return result;
    }

    private static byte[] ToPng(TextureImage texture)
    {
        using var bitmap = texture.ToBitmap();
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
