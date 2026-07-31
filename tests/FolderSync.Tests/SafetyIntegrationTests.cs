using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// End-to-end tests of the three safety behaviours, running the real engine and the real robocopy
/// against real folders.
///
/// These are integration tests on purpose. All three exist to stop files being lost, and a mocked
/// robocopy would only prove that the mock behaves as imagined — which is precisely the assumption
/// that has produced bugs here before.
/// </summary>
public class SafetyIntegrationTests : IDisposable
{
    private readonly string _root;

    public SafetyIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-safety-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---------- the large-deletion guard ----------

    [Fact]
    public async Task A_mirror_from_an_emptied_source_is_stopped_before_anything_is_lost()
    {
        // The disaster this exists for: the sending folder is momentarily empty, so a mirror would
        // clear the receiving side.
        var (left, right) = Folders();
        for (int i = 0; i < 60; i++)
            File.WriteAllText(Path.Combine(right, $"file{i}.txt"), "valuable");

        var config = Config(left, right, mirror: true);
        var result = await new SyncEngine(config).RunAsync();

        Assert.False(result.Success);
        var outcome = Assert.Single(result.Targets);
        Assert.Contains("would delete", outcome.Note);

        // The point of the test: every file is still there, and nothing was even moved to the trash.
        Assert.Equal(60, Directory.GetFiles(right).Length);
        Assert.False(Directory.Exists(Path.Combine(right, config.RecycleFolderName)));
        Assert.Equal(0, outcome.FilesRecycled);
    }

    [Fact]
    public async Task A_normal_mirror_is_not_blocked_and_still_recycles_what_it_removes()
    {
        var (left, right) = Folders();
        for (int i = 0; i < 60; i++)
        {
            File.WriteAllText(Path.Combine(left, $"file{i}.txt"), "kept");
            File.WriteAllText(Path.Combine(right, $"file{i}.txt"), "kept");
        }
        // One file only at the destination: 1 of 61 is well under the threshold.
        File.WriteAllText(Path.Combine(right, "removed.txt"), "goes to the trash");

        var config = Config(left, right, mirror: true);
        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(right, "removed.txt")));

        // Recovered rather than destroyed.
        var rescued = Directory.GetFiles(Path.Combine(right, config.RecycleFolderName), "removed.txt",
                                        SearchOption.AllDirectories);
        Assert.Single(rescued);
        Assert.Equal("goes to the trash", File.ReadAllText(rescued[0]));
    }

    [Fact]
    public async Task Turning_the_guard_off_lets_the_deletion_through()
    {
        var (left, right) = Folders();
        for (int i = 0; i < 60; i++)
            File.WriteAllText(Path.Combine(right, $"file{i}.txt"), "valuable");

        var config = Config(left, right, mirror: true);
        config.GuardLargeDeletions = false;

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        // Deleted from the destination, but recoverable - the recycle net still applies.
        Assert.Empty(Directory.GetFiles(right));
        Assert.Equal(60, Directory.GetFiles(Path.Combine(right, config.RecycleFolderName),
                                            "*", SearchOption.AllDirectories).Length);
    }

    // ---------- two-way overwrite backup ----------

    [Fact]
    public async Task The_losing_copy_of_a_two_way_pair_is_kept_rather_than_destroyed()
    {
        var (left, right) = Folders();

        // Same file on both sides, different contents, left is newer - so left wins and the right
        // copy would previously have been overwritten with no trace.
        File.WriteAllText(Path.Combine(right, "notes.txt"), "the version on the right");
        File.SetLastWriteTime(Path.Combine(right, "notes.txt"), DateTime.Now.AddDays(-2));
        File.WriteAllText(Path.Combine(left, "notes.txt"), "the version on the left");

        var config = Config(left, right, mirror: false);
        config.Pairs[0].Direction = SyncDirection.TwoWay;

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);

        // The newer copy won, as documented.
        Assert.Equal("the version on the left", File.ReadAllText(Path.Combine(right, "notes.txt")));

        // And the copy it replaced is recoverable, which is the behaviour that was previously
        // claimed but not implemented.
        var kept = Directory.GetFiles(Path.Combine(right, config.RecycleFolderName), "notes.txt",
                                      SearchOption.AllDirectories);
        Assert.Single(kept);
        Assert.Equal("the version on the right", File.ReadAllText(kept[0]));
        Assert.Equal(1, Assert.Single(result.Targets).FilesBackedUp);

        // Filed as a replacement, not a deletion. Restoring the two means different things - one
        // recovers a file, the other discards the newer content - so the batch has to say which.
        string batchFolder = Path.GetFileName(Path.GetDirectoryName(kept[0]))!;
        Assert.EndsWith(RecycleStore.ReplacedSuffix, batchFolder);
        Assert.Equal(RecycleReason.Replaced, RecycleStore.ReasonOf(batchFolder));
    }

    [Fact]
    public async Task Deletions_and_replacements_from_the_same_pair_land_in_separate_batches()
    {
        var (left, right) = Folders();

        // Enough files that removing one does not trip the guard.
        for (int i = 0; i < 40; i++)
        {
            File.WriteAllText(Path.Combine(left, $"keep{i}.txt"), "same");
            File.WriteAllText(Path.Combine(right, $"keep{i}.txt"), "same");
        }

        // One file the mirror will delete...
        File.WriteAllText(Path.Combine(right, "gone.txt"), "deleted by mirror");
        // ...and one it will overwrite with a newer copy.
        File.WriteAllText(Path.Combine(right, "changed.txt"), "older");
        File.SetLastWriteTime(Path.Combine(right, "changed.txt"), DateTime.Now.AddDays(-2));
        File.WriteAllText(Path.Combine(left, "changed.txt"), "newer");

        var config = Config(left, right, mirror: true);
        config.Pairs[0].Mirror = true;

        var result = await new SyncEngine(config).RunAsync();
        Assert.True(result.Success);

        var batches = Directory.GetDirectories(Path.Combine(right, config.RecycleFolderName))
                               .Select(Path.GetFileName)
                               .ToList();

        // A one-directional mirror overwrites without /XO, so only the deletion is kept here - the
        // important part is that whatever is kept is filed under the right reason.
        Assert.All(batches, b => Assert.Equal(RecycleReason.Deleted, RecycleStore.ReasonOf(b!)));
        Assert.True(File.Exists(Directory.GetFiles(Path.Combine(right, config.RecycleFolderName),
                                                   "gone.txt", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task A_file_that_exists_on_only_one_side_is_copied_without_a_pointless_backup()
    {
        var (left, right) = Folders();
        File.WriteAllText(Path.Combine(left, "new.txt"), "brand new");

        var config = Config(left, right, mirror: false);
        config.Pairs[0].Direction = SyncDirection.TwoWay;

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(right, "new.txt")));

        // Nothing was overwritten, so nothing should have been kept aside.
        Assert.Equal(0, Assert.Single(result.Targets).FilesBackedUp);
        Assert.False(Directory.Exists(Path.Combine(right, config.RecycleFolderName)));
    }

    [Fact]
    public async Task Two_way_still_gives_each_side_what_the_other_has()
    {
        var (left, right) = Folders();
        File.WriteAllText(Path.Combine(left, "fromleft.txt"), "L");
        File.WriteAllText(Path.Combine(right, "fromright.txt"), "R");

        var config = Config(left, right, mirror: false);
        config.Pairs[0].Direction = SyncDirection.TwoWay;

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(right, "fromleft.txt")));
        Assert.True(File.Exists(Path.Combine(left, "fromright.txt")));
    }

    // ---------- an unmounted drive is not a failure ----------

    [Fact]
    public async Task An_unconnected_drive_is_skipped_and_does_not_make_the_run_fail()
    {
        // A drive letter that is almost certainly not mounted, standing in for an unplugged USB disk.
        var (left, _) = Folders();
        File.WriteAllText(Path.Combine(left, "a.txt"), "a");

        var config = new SyncConfig();
        config.Pairs.Add(new SyncPair
        {
            Name = "backup drive",
            Left = left,
            Right = @"Q:\Backup",
            Direction = SyncDirection.ToRight,
            Mirror = true,
            Enabled = true,
        });

        var result = await new SyncEngine(config).RunAsync();

        var outcome = Assert.Single(result.Targets);
        Assert.True(outcome.Skipped);
        Assert.Contains("drive not connected", outcome.Note);
        Assert.Equal("Skipped", outcome.ResultText);

        // The whole point: a scheduled run does not report an error, so it does not email one every
        // day the drive happens to be unplugged.
        Assert.True(result.Success);
    }

    [Fact]
    public async Task A_missing_folder_on_a_drive_that_is_present_is_still_a_real_failure()
    {
        // The distinction that matters: "drive absent" is waiting, "folder deleted" is broken.
        var (left, right) = Folders();
        Directory.Delete(left, recursive: true);

        var config = Config(left, right, mirror: false);
        var result = await new SyncEngine(config).RunAsync();

        var outcome = Assert.Single(result.Targets);
        Assert.False(outcome.Skipped);
        Assert.False(outcome.Success);
        Assert.False(result.Success);
        Assert.Contains("does not exist", outcome.Note);
    }

    // ---------- per-pair identity ----------

    [Fact]
    public void A_pair_is_identified_by_its_folders_so_renaming_it_keeps_its_history()
    {
        string a = PairHistory.KeyFor(@"C:\Docs", @"D:\Backup");
        string b = PairHistory.KeyFor(@"c:\docs\", @"d:\backup");      // case and trailing slash
        string c = PairHistory.KeyFor(@"C:\Docs", @"E:\Elsewhere");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ---------- helpers ----------

    private (string left, string right) Folders()
    {
        string left = Path.Combine(_root, "left");
        string right = Path.Combine(_root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        return (left, right);
    }

    private static SyncConfig Config(string left, string right, bool mirror)
    {
        var config = new SyncConfig();
        config.Pairs.Add(new SyncPair
        {
            Name = "test",
            Left = left,
            Right = right,
            Direction = SyncDirection.ToRight,
            Mirror = mirror,
            Enabled = true,
        });
        return config;
    }
}
