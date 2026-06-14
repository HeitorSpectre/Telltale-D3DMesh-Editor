using System.Reflection;

namespace TelltaleD3DMeshEditor.Core;

public static class EmbeddedIconResources
{
    public const string D3DMesh = "TelltaleD3DMeshEditor.Resources.Icons.D3DMesh.ico";
    public const string Skeleton = "TelltaleD3DMeshEditor.Resources.Icons.Skeleton.ico";
    public const string Issue = "TelltaleD3DMeshEditor.Resources.Icons.Issue.ico";

    public static Icon LoadIcon(string resourceName)
    {
        using var stream = Open(resourceName);
        return stream is null
            ? Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application
            : new Icon(stream);
    }

    public static string? ExtractToLocalCache(string resourceName, string fileName)
    {
        using var stream = Open(resourceName);
        if (stream is null)
        {
            return null;
        }

        var iconDir = Path.Combine(AppPreferences.AppDataFolder, "Icons");
        Directory.CreateDirectory(iconDir);

        var outputPath = Path.Combine(iconDir, fileName);
        using var output = File.Create(outputPath);
        stream.CopyTo(output);
        return outputPath;
    }

    private static Stream? Open(string resourceName)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
}
