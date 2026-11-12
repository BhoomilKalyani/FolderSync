using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The update check and the installer download, run against a throwaway HTTP server on the loopback
/// address. A real socket rather than a mocked handler, because the failures that matter - a connection
/// that drops mid-transfer, a server that never says how long the body is - live in the transport.
///
/// Nothing here touches GitHub, or the real "last checked" record under %LOCALAPPDATA%.
/// </summary>
public class UpdaterNetworkTests : IDisposable
{
    // A version nobody will ever ship, so the download lands on a file name of its own.
    private readonly Version _version = new(0, 0, Random.Shared.Next(10_000, 60_000));

    private string SetupPath => Path.Combine(Path.GetTempPath(), "FolderSyncUpdate", $"FolderSyncSetup-{_version}.exe");

    public void Dispose()
    {
        foreach (var f in new[] { SetupPath, SetupPath + ".part" })
            try { File.Delete(f); } catch { }
    }

    // ---------- the release check ----------

    [Fact]
    public async Task A_rate_limited_check_is_an_error_not_up_to_date()
    {
        // Every non-2xx answer used to come back as null, which reads as "You're up to date".
        await using var server = new OneShotServer(Status(403, "rate limit exceeded"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Updater.QueryLatestAsync(server.Url, false, new Version(1, 0, 0)));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task A_server_error_is_an_error_not_up_to_date()
    {
        await using var server = new OneShotServer(Status(502, "bad gateway"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Updater.QueryLatestAsync(server.Url, false, new Version(1, 0, 0)));
    }

    [Fact]
    public async Task No_release_yet_is_a_real_answer_meaning_nothing_to_do()
    {
        await using var server = new OneShotServer(Status(404, "{\"message\":\"Not Found\"}"));

        Assert.Null(await Updater.QueryLatestAsync(server.Url, false, new Version(1, 0, 0)));
    }

    [Fact]
    public async Task A_newer_release_is_offered_with_its_installer()
    {
        const string json = "{\"tag_name\":\"v9.8.7\",\"draft\":false,\"prerelease\":false,\"body\":\"notes\","
                          + "\"html_url\":\"https://example.invalid/r\",\"assets\":["
                          + "{\"name\":\"other.exe\",\"browser_download_url\":\"https://example.invalid/o\",\"size\":5},"
                          + "{\"name\":\"FolderSyncSetup-9.8.7.exe\",\"browser_download_url\":\"https://example.invalid/s\",\"size\":1234}]}";
        await using var server = new OneShotServer(Status(200, json));

        var info = await Updater.QueryLatestAsync(server.Url, false, new Version(1, 0, 0));

        Assert.NotNull(info);
        Assert.Equal(new Version(9, 8, 7), info!.Version);
        Assert.Equal("https://example.invalid/s", info.SetupUrl);
        Assert.Equal(1234, info.SetupSize);
    }

    // ---------- the download ----------

    [Fact]
    public async Task A_complete_download_is_accepted_even_if_the_size_seen_earlier_is_stale()
    {
        // The installer was replaced in place after the update was found, so the cached size no longer
        // matches. The response's own Content-Length is the authority; the stale figure used to make
        // every attempt fail as "incomplete".
        byte[] body = Bytes(5000);
        await using var server = new OneShotServer(async s =>
        {
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(body);
        });

        string file = await Updater.DownloadSetupAsync(Info(server.Url, cachedSize: 4000), null);

        Assert.Equal(SetupPath, file);
        Assert.Equal(body, File.ReadAllBytes(file));
        Assert.False(File.Exists(file + ".part"));
    }

    [Fact]
    public async Task A_connection_that_drops_mid_transfer_leaves_no_installer_behind()
    {
        // The stream throws before the size check is reached, so the old clean-up never ran and a
        // truncated installer stayed on disk under the name that gets executed.
        byte[] body = Bytes(5000);
        await using var server = new OneShotServer(async s =>
        {
            await Write(s, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(body.AsMemory(0, 1500));
            // ...and the connection closes, 3,500 bytes short.
        });

        await Assert.ThrowsAnyAsync<Exception>(
            () => Updater.DownloadSetupAsync(Info(server.Url, cachedSize: body.Length), null));

        Assert.False(File.Exists(SetupPath));
        Assert.False(File.Exists(SetupPath + ".part"));
    }

    [Fact]
    public async Task A_short_download_with_no_announced_length_is_rejected_and_removed()
    {
        // No Content-Length: the size from the release listing is all there is to check against.
        await using var server = new OneShotServer(async s =>
        {
            await Write(s, "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(Bytes(1500));
        });

        var ex = await Assert.ThrowsAsync<IOException>(
            () => Updater.DownloadSetupAsync(Info(server.Url, cachedSize: 5000), null));

        Assert.Contains("incomplete", ex.Message);
        Assert.False(File.Exists(SetupPath));
        Assert.False(File.Exists(SetupPath + ".part"));
    }

    // ---------- helpers ----------

    private UpdateInfo Info(string url, long cachedSize) =>
        new() { Version = _version, TagName = "v" + _version, SetupUrl = url, SetupSize = cachedSize };

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }

    private static Func<NetworkStream, Task> Status(int code, string body) => async s =>
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        await Write(s, $"HTTP/1.1 {code} Status\r\nContent-Type: application/json\r\n"
                     + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(payload);
    };

    private static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    /// <summary>Accepts one connection on loopback, reads the request headers, then runs a scripted reply.</summary>
    private sealed class OneShotServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;

        public OneShotServer(Func<NetworkStream, Task> reply)
        {
            _listener.Start();
            _serving = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                await ReadHeaders(stream);
                await reply(stream);
                await stream.FlushAsync();
            });
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/latest";

        private static async Task ReadHeaders(NetworkStream s)
        {
            var seen = new List<byte>();
            var one = new byte[1];
            while (await s.ReadAsync(one) == 1)
            {
                seen.Add(one[0]);
                int n = seen.Count;
                if (n >= 4 && seen[n - 4] == '\r' && seen[n - 3] == '\n' && seen[n - 2] == '\r' && seen[n - 1] == '\n')
                    return;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* the test has its answer */ }
            _listener.Stop();
        }
    }
}
