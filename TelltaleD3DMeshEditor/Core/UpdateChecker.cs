using System.Net.Http;
using System.Text.Json;

namespace TelltaleD3DMeshEditor.Core;

// Information about a release that is newer than the running build.
public sealed record UpdateInfo(string Version, string Title, string Changelog, string ReleaseUrl, string? DownloadUrl);

// Checks the project's GitHub Releases for a newer version. The check is read-only and never installs
// anything on its own: it only reports what is available so the UI can show the changelog and let the
// user decide. Designed to fail quietly (offline, rate-limited, etc.) so it never disrupts the tool.
public static class UpdateChecker
{
    // Bump this on every release so the checker can tell when a newer one is published.
    public const string CurrentVersion = "1.10";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/HeitorSpectre/Telltale-D3DMesh-Editor/releases/latest";

    public const string ReleasesPage =
        "https://github.com/HeitorSpectre/Telltale-D3DMesh-Editor/releases";

    // Returns the latest release when it is newer than CurrentVersion; null when up to date, offline,
    // or anything goes wrong. Never throws.
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var latest = await FetchLatestReleaseAsync(cancellationToken);
        return latest is not null && IsNewerVersion(latest.Version, CurrentVersion) ? latest : null;
    }

    // Fetches the latest published release as-is, WITHOUT comparing versions. Used by the debug-only
    // "simulate update" action so the update dialog can be exercised even when we are already up to date.
    // Still fails quietly (returns null) and never throws.
    public static async Task<UpdateInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub requires a User-Agent; the Accept header opts into the stable v3 JSON.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TelltaleD3DMeshEditor-UpdateChecker");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            await using var stream = await http.GetStreamAsync(LatestReleaseApi, cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var title = GetString(root, "name");
            var changelog = GetString(root, "body");
            var releaseUrl = GetString(root, "html_url");

            var assetUrls = new List<string>();
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var url = GetString(asset, "browser_download_url");
                    if (!string.IsNullOrEmpty(url))
                    {
                        assetUrls.Add(url);
                    }
                }
            }

            var downloadUrl = assetUrls
                .FirstOrDefault(IsSupportedUpdateArchive)
                ?? assetUrls.FirstOrDefault();

            return new UpdateInfo(
                tag.Trim(),
                string.IsNullOrWhiteSpace(title) ? tag.Trim() : title,
                changelog,
                string.IsNullOrEmpty(releaseUrl) ? ReleasesPage : releaseUrl,
                downloadUrl);
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool IsSupportedUpdateArchive(string url)
    {
        var extension = Path.GetExtension(new Uri(url).LocalPath);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rar", StringComparison.OrdinalIgnoreCase);
    }

    // True when the release tag parses to a higher version than the running one. Tolerates a leading
    // "v" and single-number tags (e.g. "2" -> "2.0").
    public static bool IsNewerVersion(string latestTag, string current) =>
        System.Version.TryParse(Normalize(latestTag), out var latest) &&
        System.Version.TryParse(Normalize(current), out var running) &&
        latest > running;

    private static string Normalize(string tag)
    {
        var trimmed = tag.Trim().TrimStart('v', 'V');
        return trimmed.Contains('.') ? trimmed : trimmed + ".0";
    }
}
