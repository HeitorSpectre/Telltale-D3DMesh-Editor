using System.Diagnostics;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace TelltaleD3DMeshEditor.Core;

public static class SelfUpdater
{
    private const string ExeName = "TelltaleD3DMeshEditor.exe";

    public static async Task DownloadExtractAndRestartAsync(
        UpdateInfo update,
        IProgress<(int Done, int Total, string Label)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            throw new InvalidOperationException("This release has no downloadable update asset.");
        }

        var workRoot = Path.Combine(Path.GetTempPath(), "TelltaleD3DMeshEditor_Update_" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(workRoot, GetSafeArchiveName(update.DownloadUrl));
        var extractRoot = Path.Combine(workRoot, "extracted");

        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(extractRoot);

        progress?.Report((0, 1000, "Downloading update... 0%"));
        await DownloadAsync(update.DownloadUrl, archivePath, progress, cancellationToken);

        progress?.Report((0, 1000, "Extracting update..."));
        ExtractArchive(archivePath, extractRoot);
        progress?.Report((1000, 1000, "Extracting update... 100%"));

        var updateRoot = FindUpdateRoot(extractRoot);
        var targetRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentExe = Environment.ProcessPath ?? Application.ExecutablePath;
        var processId = Environment.ProcessId;

        var scriptPath = WriteApplyScript(workRoot, updateRoot, targetRoot, currentExe, processId);
        StartApplyScript(scriptPath);
    }

    private static async Task DownloadAsync(
        string url,
        string outputPath,
        IProgress<(int Done, int Total, string Label)>? progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TelltaleD3DMeshEditor-SelfUpdater");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(outputPath);

        var buffer = new byte[1024 * 128];
        long readTotal = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (totalBytes is > 0)
            {
                var done = (int)Math.Clamp(readTotal * 1000 / totalBytes.Value, 0, 1000);
                progress?.Report((done, 1000, $"Downloading update... {done / 10}%"));
            }
        }

        progress?.Report((1000, 1000, "Downloading update... 100%"));
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        var extension = Path.GetExtension(archivePath);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                entry.WriteToDirectory(destination, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }

            return;
        }

        throw new InvalidOperationException($"Unsupported update archive format: {extension}");
    }

    private static string FindUpdateRoot(string extractRoot)
    {
        var exePath = Directory
            .EnumerateFiles(extractRoot, ExeName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();

        if (exePath is null)
        {
            throw new InvalidOperationException($"The update archive does not contain {ExeName}.");
        }

        return Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("Could not identify the extracted update folder.");
    }

    private static string WriteApplyScript(
        string workRoot,
        string updateRoot,
        string targetRoot,
        string currentExe,
        int processId)
    {
        var scriptPath = Path.Combine(workRoot, "apply-update.ps1");
        var robocopyLog = Path.Combine(workRoot, "robocopy.log");
        var script = $$"""
        $ErrorActionPreference = 'Stop'
        $processId = {{processId}}
        $source = {{ToPowerShellString(updateRoot)}}
        $target = {{ToPowerShellString(targetRoot)}}
        $exe = {{ToPowerShellString(currentExe)}}
        $log = {{ToPowerShellString(robocopyLog)}}

        try {
            Wait-Process -Id $processId -Timeout 60 -ErrorAction SilentlyContinue
        } catch {
        }

        Start-Sleep -Milliseconds 500
        robocopy $source $target /E /R:20 /W:1 /NFL /NDL /NJH /NJS /NP /LOG:$log
        $code = $LASTEXITCODE
        if ($code -gt 7) {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show("The update could not be applied. Robocopy exit code: $code`n`nLog: $log", "Telltale D3DMesh Editor Update", "OK", "Error") | Out-Null
            exit $code
        }

        Start-Process -FilePath $exe -WorkingDirectory $target
        """;

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private static void StartApplyScript(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static string GetSafeArchiveName(string url)
    {
        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        return string.IsNullOrWhiteSpace(fileName) ? "update.zip" : fileName;
    }

    private static string ToPowerShellString(string value)
        => "'" + value.Replace("'", "''") + "'";

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
