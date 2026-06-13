using System.Drawing.Imaging;
using TelltaleD3DMeshEditor.Formats.Texture;

namespace TelltaleD3DMeshEditor.Export;

// Exports a decoded texture (.d3dtx/.dds/.png) to PNG. Useful for extracting standalone textures
// without a model. Fully offline through TextureLoader.
public static class TextureExporter
{
    public static void ExportToPng(string inputPath, string outputPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
        var texture = TextureLoader.Load(inputPath);
        using var bitmap = texture.ToBitmap();
        bitmap.Save(outputPngPath, ImageFormat.Png);
    }
}
