using System.Diagnostics;
using System.Net;
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

    /// <summary>
    /// The installer's SHA-256 as GitHub reports it for the asset (its "digest"), lower-case hex, or null.
    /// GitHub works it out from the file it stores, so it catches a download that is damaged or altered
    /// on its way here.
    /// </summary>
    public string? SetupSha256 { get; init; }

    /// <summary>
    /// The "FolderSyncSetup-x.y.z.exe.sha256" file the release workflow publishes beside the installer,
    /// or null. Used as well as, or instead of, <see cref="SetupSha256"/>.
    /// </summary>
    public string? ChecksumUrl { get; init; }

    /// <summary>
    /// Whether the installer can be checked before it is run. One that cannot is never run by the app:
    /// the update window sends the user to the release page instead.
    /// </summary>
    public bool CanVerifySetup => SetupSha256 is not null || !string.IsNullOrEmpty(ChecksumUrl);
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
    /// Queries the latest release. Returns null only when GitHub actually answered that there is nothing
    /// newer (or no release yet). Anything else - offline, rate-limited, a server error - throws, so it
    /// is reported as "could not check" rather than as "you're up to date".
    ///
    /// The check is recorded only once GitHub has answered: recording it up front meant a failed check
    /// also switched off the automatic one for the next 24 hours.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(SyncConfig config, CancellationToken ct = default)
    {
        string repo = string.IsNullOrWhiteSpace(config.UpdateRepo) ? SyncConfig.DefaultUpdateRepo : config.UpdateRepo.Trim();
        string url = $"https://api.github.com/repos/{repo}/releases/latest";

        var info = await QueryLatestAsync(url, config.IncludePrereleaseUpdates, CurrentVersion, ct);
        RecordCheck();
        return info;
    }

    /// <summary>
    /// The release query itself, against any URL and version, so it can be tested against a local server
    /// without touching the real check-time record.
    /// </summary>
    public static async Task<UpdateInfo?> QueryLatestAsync(string url, bool includePrerelease, Version current,
                                                           CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, ct);

        // 404 is a real answer: the repository has no published release yet.
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            int code = (int)resp.StatusCode;
            string hint = code is 403 or 429 ? " - GitHub's limit on update checks was reached; try again later" : "";
            throw new HttpRequestException($"GitHub answered {code} {resp.ReasonPhrase}{hint}.", null, resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean() && !includePrerelease) return null;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var latest = ParseVersion(tag);
        if (latest is null || latest <= current) return null;

        string? setupUrl = null, setupSha256 = null, checksumUrl = null;
        long setupSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var all = assets.EnumerateArray()
                .Select(a => (
                    name: a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    url: a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null,
                    size: a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                    digest: a.TryGetProperty("digest", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null))
                .Where(a => a.url is not null)
                .ToList();

            // An installer is preferred over any other .exe that happens to be attached, rather than
            // trusting whichever one GitHub lists first.
            var exes = all.Where(a => a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = exes.FirstOrDefault(a => a.name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
            if (chosen.url is null) chosen = exes.FirstOrDefault();

            if (chosen.url is not null)
            {
                setupUrl = chosen.url;
                setupSize = chosen.size;
                setupSha256 = ParseDigest(chosen.digest);
                string sumName = chosen.name + ".sha256";
                checksumUrl = all.FirstOrDefault(a => a.name.Equals(sumName, StringComparison.OrdinalIgnoreCase)).url;
            }
        }

        return new UpdateInfo
        {
            Version = latest,
            TagName = tag,
            Notes = (root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "").Trim(),
            SetupUrl = setupUrl,
            SetupSize = setupSize,
            SetupSha256 = setupSha256,
            ChecksumUrl = checksumUrl,
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

    /// <summary>
    /// "sha256:4b2f…" (GitHub's asset digest) or a bare 64-character hex string, as lower-case hex.
    /// Null for anything else, including a digest made with some other algorithm.
    /// </summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        string s = digest.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) s = s["sha256:".Length..];
        return s.Length == 64 && s.All(Uri.IsHexDigit) ? s.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Reads a checksum file in the usual "hash *name" / "hash  name" form (or just the hash), as
    /// lower-case hex. Throws if it does not hold a SHA-256: a checksum that cannot be read is no check.
    /// </summary>
    public static string ParseChecksumFile(string text)
    {
        string first = (text ?? "").Trim().TrimStart('﻿').Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
                                           .FirstOrDefault() ?? "";
        return ParseDigest(first)
            ?? throw new InvalidDataException("The checksum published with the release could not be read.");
    }

    /// <summary>
    /// Every SHA-256 the release publishes for its installer. Asking for both and requiring each to
    /// match means neither source has to be trusted on its own.
    /// </summary>
    private static async Task<List<string>> ExpectedHashesAsync(UpdateInfo info, CancellationToken ct)
    {
        var hashes = new List<string>();
        if (info.SetupSha256 is { } digest) hashes.Add(digest);
        if (!string.IsNullOrEmpty(info.ChecksumUrl))
        {
            using var resp = await Http.GetAsync(info.ChecksumUrl, ct);
            resp.EnsureSuccessStatusCode();
            hashes.Add(ParseChecksumFile(await resp.Content.ReadAsStringAsync(ct)));
        }
        return hashes;
    }

    /// <summary>
    /// Downloads the installer to the temp folder, reporting 0-100 progress, and checks it against the
    /// SHA-256 the release publishes before handing it back to be run.
    ///
    /// <para>Setup runs with the user's rights and is not code-signed, so its size was the only check on
    /// what got executed: an installer damaged in transit, or swapped on the way here, ran all the same.
    /// It now has to match the release's checksum, or it is deleted and nothing is run. A release with
    /// no checksum at all is refused here too, rather than run unchecked.</para>
    /// </summary>
    public static async Task<string> DownloadSetupAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.SetupUrl))
            throw new InvalidOperationException("This release has no installer attached.");
        if (!info.CanVerifySetup)
            throw new InvalidOperationException(
                "This release does not publish a checksum for its installer, so it cannot be checked before it runs.");

        string dir = Path.Combine(Path.GetTempPath(), "FolderSyncUpdate");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"FolderSyncSetup-{info.Version}.exe");

        // Downloaded under a ".part" name and only renamed once complete: this file is about to be
        // executed, so a truncated installer must never sit under the name that gets run. Any failure -
        // including a connection that drops mid-transfer, which throws before the size check below
        // could ever run - deletes it.
        string part = file + ".part";
        try
        {
            var expectedHashes = await ExpectedHashesAsync(info, ct);
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);

            using var resp = await Http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            // This response's own length is the authority. The size cached when the update was found can
            // be stale - the release workflow replaces an installer in place - and preferring it rejected
            // complete downloads as incomplete.
            long? announced = resp.Content.Headers.ContentLength;
            long expected = announced ?? info.SetupSize;
            long written = 0;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    written += read;
                    if (expected > 0) progress?.Report((int)Math.Min(100, written * 100 / expected));
                }
            }

            if (expected > 0 && written != expected)
                throw new IOException(
                    $"The download is incomplete ({Humanise.Size(written)} of {Humanise.Size(expected)}). "
                    + "Nothing has been installed — check your connection and try again.");

            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (expectedHashes.Count == 0 || expectedHashes.Any(h => h != actual))
                throw new IOException(
                    "The downloaded installer does not match the checksum published with the release, so it "
                    + "was deleted and nothing has been installed. Try again later, or download it from the "
                    + "release page.");

            File.Move(part, file, overwrite: true);
            return file;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* a leftover in the temp folder is harmless */ }
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
