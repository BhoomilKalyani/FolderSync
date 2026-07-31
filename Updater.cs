using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace FolderSync;

/// <summary>A newer release found on GitHub.</summary>
public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string TagName { get; init; } = "";
    public string Notes { get; init; } = "";
    /// <summary>Direct link to the setup .exe asset, if the release has one.</summary>
    public string? SetupUrl { get; init; }
    public string ReleasePageUrl { get; init; } = "";
    public long SetupSize { get; init; }
}

/// <summary>
/// Checks GitHub Releases for a newer version and can download the installer.
/// The repository is public, so no token or login is needed.
/// </summary>
public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FolderSync", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>The running app's version, taken from the assembly.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    private static string LastCheckPath => Path.Combine(SyncConfig.AppDataDir, "lastupdatecheck.txt");

    public static DateTime? LastCheck
    {
        get
        {
            try
            {
                return File.Exists(LastCheckPath)
                    && DateTime.TryParse(File.ReadAllText(LastCheckPath).Trim(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt : null;
            }
            catch { return null; }
        }
    }

    private static void RecordCheck()
    {
        try
        {
            Directory.CreateDirectory(SyncConfig.AppDataDir);
            File.WriteAllText(LastCheckPath, DateTime.Now.ToString("o"));
        }
        catch { /* not critical */ }
    }

    /// <summary>True if we have not checked within the given number of hours.</summary>
    public static bool IsCheckDue(int hours = 24)
    {
        var last = LastCheck;
        return last is null || (DateTime.Now - last.Value) >= TimeSpan.FromHours(hours);
    }

    /// <summary>
    /// Queries the latest release. Returns null when already up to date, when the repo has no
    /// releases yet, or when the check fails (offline) — callers treat null as "nothing to do".
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(SyncConfig config, CancellationToken ct = default)
    {
        RecordCheck();

        string repo = string.IsNullOrWhiteSpace(config.UpdateRepo) ? SyncConfig.DefaultUpdateRepo : config.UpdateRepo.Trim();
        string url = $"https://api.github.com/repos/{repo}/releases/latest";

        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;      // 404 = no releases published yet

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean() && !config.IncludePrereleaseUpdates) return null;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var latest = ParseVersion(tag);
        if (latest is null || latest <= CurrentVersion) return null;

        string? setupUrl = null;
        long setupSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                setupUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                setupSize = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                break;
            }
        }

        return new UpdateInfo
        {
            Version = latest,
            TagName = tag,
            Notes = (root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "").Trim(),
            SetupUrl = setupUrl,
            SetupSize = setupSize,
            ReleasePageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
        };
    }

    /// <summary>Turns a tag such as "v1.2.3" or "1.2" into a Version, or null if unparseable.</summary>
    public static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');

        // Keep only leading digits and dots so tags like "1.2.3-beta" still parse.
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].Trim('.');
        if (s.Length == 0) return null;

        var parts = s.Split('.');
        if (!int.TryParse(parts[0], out int major)) return null;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int mi) ? mi : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out int bu) ? bu : 0;
        return new Version(major, minor, build);
    }

    /// <summary>Downloads the installer to the temp folder, reporting 0-100 progress.</summary>
    public static async Task<string> DownloadSetupAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.SetupUrl))
            throw new InvalidOperationException("This release has no installer attached.");

        string dir = Path.Combine(Path.GetTempPath(), "FolderSyncUpdate");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"FolderSyncSetup-{info.Version}.exe");

        using var resp = await Http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? info.SetupSize;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }
        return file;
    }

    /// <summary>
    /// Launches the downloaded installer and asks the caller to quit so files can be replaced.
    /// /SILENT shows only a progress window; /CLOSEAPPLICATIONS lets Setup close this app if needed.
    /// </summary>
    public static void RunSetup(string setupPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART",
            UseShellExecute = true,
        });
    }
}
