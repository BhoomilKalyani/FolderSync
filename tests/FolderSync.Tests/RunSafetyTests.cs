using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// One sync at a time, a log that survives a run being stopped, and a recycle folder that stays inside
/// its pair. Each test uses its own lock name and temporary folder, so nothing here can collide with a
/// real copy of the app.
/// </summary>
public class RunSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-run-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _lockName = @"Local\FolderSync.Test." + Guid.NewGuid().ToString("N");

    public RunSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------- one sync at a time ----------

    [Fact]
    public void A_second_sync_does_not_start_while_the_first_is_running()
    {
        // The Daily and Monthly tasks both default to 18:00, and "Sync now" could start on top of a
        // scheduled run: two passes listing, moving aside and copying the same folders at once.
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new Thread(() => SyncLock.Run(() => { holding.Set(); release.Wait(); return 0; }, TimeSpan.Zero, _lockName));
        first.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        bool secondRan = false;
        Assert.Throws<SyncAlreadyRunningException>(
            () => SyncLock.Run(() => secondRan = true, TimeSpan.Zero, _lockName));
        Assert.False(secondRan);

        release.Set();
        first.Join();
    }

    [Fact]
    public void The_next_sync_runs_once_the_previous_one_has_finished()
    {
        Assert.Equal(1, SyncLock.Run(() => 1, TimeSpan.Zero, _lockName));
        Assert.Equal(2, SyncLock.Run(() => 2, TimeSpan.Zero, _lockName));
    }

    [Fact]
    public void A_sync_that_throws_still_releases_the_lock()
    {
        Assert.Throws<InvalidOperationException>(
            () => SyncLock.Run<int>(() => throw new InvalidOperationException(), TimeSpan.Zero, _lockName));

        Assert.Equal(3, SyncLock.Run(() => 3, TimeSpan.Zero, _lockName));
    }

    [Fact]
    public void A_sync_that_died_holding_the_lock_does_not_block_the_next_one()
    {
        // A thread that exits while owning a mutex abandons it - the same as a process being killed.
        var dead = new Thread(() =>
        {
            var m = new Mutex(false, _lockName);
            m.WaitOne();
            // exits without releasing
        });
        dead.Start();
        dead.Join();

        Assert.Equal(4, SyncLock.Run(() => 4, TimeSpan.FromSeconds(2), _lockName));
    }

    // ---------- the log is on disk as it goes ----------

    [Fact]
    public void Each_line_is_on_disk_before_the_run_ends()
    {
        // The scheduled run kept its whole log in memory and wrote it at the end, so a run stopped by
        // Task Scheduler's time limit left no log at all.
        string path = Path.Combine(_dir, "sync.log");
        var log = SyncLog.Open(path);
        log.WriteLine("===== Sync started");
        log.WriteLine("--- Docs");

        // Read while it is still open, as anyone looking at a stuck run would.
        string soFar;
        using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            soFar = reader.ReadToEnd();
        log.Dispose();

        Assert.Contains("Sync started", soFar);
        Assert.Contains("--- Docs", soFar);
    }

    [Fact]
    public async Task Lines_from_several_threads_are_all_kept_whole()
    {
        // robocopy's output and error streams are read on separate threads, and both write to the log.
        string path = Path.Combine(_dir, "sync.log");
        using (var log = SyncLog.Open(path))
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 500; i++) log.WriteLine($"thread {t} line {i}");
            })));
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2000, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^thread \d line \d+$", l));
    }

    [Fact]
    public void A_log_that_cannot_be_created_does_not_stop_the_sync()
    {
        var log = SyncLog.Open(Path.Combine(_dir, "no-such-folder", "sync.log"));
        log.WriteLine("still fine");
        log.Dispose();
    }

    // ---------- the recycle folder stays inside its pair ----------

    [Theory]
    [InlineData("_FolderSyncTrash", true)]
    [InlineData("Recycled files", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData(@"..\elsewhere", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData("a/b", false)]
    [InlineData("trash.", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Only_a_plain_folder_name_is_accepted_for_the_recycle_folder(string? name, bool valid)
    {
        Assert.Equal(valid, SyncConfig.IsValidRecycleFolderName(name));
    }

    [Fact]
    public void An_invalid_recycle_folder_name_falls_back_to_the_default()
    {
        Assert.Equal(SyncConfig.DefaultRecycleFolderName, new SyncConfig { RecycleFolderName = ".." }.SafeRecycleFolderName);
        Assert.Equal("Kept", new SyncConfig { RecycleFolderName = "Kept" }.SafeRecycleFolderName);
    }

    [Fact]
    public void Loading_a_config_with_an_invalid_recycle_folder_name_resets_it_and_says_so()
    {
        string config = Path.Combine(_dir, "config.json");
        File.WriteAllText(config, "{ \"RecycleFolderName\": \"..\" }");
        var log = new List<string>();

        var cfg = SyncConfig.LoadFrom(config, log.Add, 3, 10);

        Assert.Equal(SyncConfig.DefaultRecycleFolderName, cfg.RecycleFolderName);
        Assert.Contains(log, l => l.Contains("RecycleFolderName"));
    }
}
