using System.Text;

namespace TelltaleD3DMeshEditor.Core;

public static class ErrorLog
{
    public static string Write(Exception ex, string context)
    {
        var path = GetLogPath();
        var text = new StringBuilder()
            .AppendLine("============================================================")
            .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .AppendLine(context)
            .AppendLine(ex.ToString())
            .ToString();

        try
        {
            File.AppendAllText(path, text, Encoding.UTF8);
            return path;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TelltaleD3DMeshEditor",
                "TelltaleD3DMeshEditor.log");
            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            File.AppendAllText(fallback, text, Encoding.UTF8);
            return fallback;
        }
    }

    private static string GetLogPath()
    {
        var exeDir = AppContext.BaseDirectory;
        return Path.Combine(exeDir, "TelltaleD3DMeshEditor.log");
    }
}
