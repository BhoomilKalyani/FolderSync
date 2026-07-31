using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Runs the real engine (and therefore real robocopy) against temp folders. These catch things no
/// unit test can see — the first version of the pair loop reported every pair as FAILED even though
/// the copy had worked, because the result's Success flag was never set to true.
/// </summary>
public class SyncEngineTests : IDisposable
{
    private readonly string _root;
    private readonly string _left;
    private readonly string _right;

    public SyncEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderSyncEngineTests", Guid.NewGuid().ToString("N"));
        _left = Path.Combine(_root, "left");
        _right = Path.Combine(_root, "right");
        Directory.CreateDirectory(_left);
        Directory.CreateDirectory(_right);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, string content)
    {
        string p = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static SyncConfig Config(params SyncPair[] pairs) => new()
    {
        Pairs = pairs.ToList(),
        RecycleFolderName = "_Trash",
        RecycleDeletedFiles = true,
        ExcludeFiles = new List<string>(),
        ExcludeFolders = new List<string>(),
    };

    private static async Task<SyncResult> Run(SyncConfig config, bool listOnly = false) =>
        await new SyncEngine(config).RunAsync(CancellationToken.None, listOnly);

    [Fact]
    public async Task A_successful_one_directional_sync_reports_success()
    {
        Write(@"left\a.txt", "A");
        Write(@"left\sub\b.txt", "B");
        var config = Config(new SyncPair { Name = "P", Left = _left, Right = _right, Direction = SyncDirection.ToRight });

        var result = await Run(config);

        Assert.True(result.Success);                                  // the bug: this was false
        var pair = Assert.Single(result.Targets);
        Assert.True(pair.Success);
        Assert.Equal(2, pair.FilesCopied);
        Assert.Equal(0, pair.FilesFailed);
        Assert.True(File.Exists(Path.Combine(_right, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_right, "sub", "b.txt")));
    }

    [Fact]
    public async Task Syncing_again_with_nothing_to_do_still_reports_success()
    {
        // robocopy exits 0 with zero files copied; that must not read as a failure.
        Write(@"left\a.txt", "A");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.ToRight });
        await Run(config);

        var second = await Run(config);

        Assert.True(second.Success);
        Assert.Equal(0, second.Targets[0].FilesCopied);
    }

    [Fact]
    public async Task ToLeft_copies_the_other_way()
    {
        Write(@"right\only-right.txt", "R");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.ToLeft });

        var result = await Run(config);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_left, "only-right.txt")));
    }

    [Fact]
    public async Task TwoWay_merges_both_sides_and_the_newer_file_wins()
    {
        Write(@"left\only-left.txt", "L");
        Write(@"right\only-right.txt", "R");
        Write(@"right\both.txt", "old");
        // Make the left copy clearly newer than the right one.
        Write(@"left\both.txt", "new");
        File.SetLastWriteTimeUtc(Path.Combine(_right, "both.txt"), DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(Path.Combine(_left, "both.txt"), DateTime.UtcNow);

        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.TwoWay });
        var result = await Run(config);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_right, "only-left.txt")));
        Assert.True(File.Exists(Path.Combine(_left, "only-right.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_left, "both.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_right, "both.txt")));
    }

    [Fact]
    public async Task TwoWay_restores_a_file_deleted_from_one_side_instead_of_deleting_it()
    {
        Write(@"left\keep.txt", "K");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.TwoWay });
        await Run(config);
        File.Delete(Path.Combine(_left, "keep.txt"));

        await Run(config);

        Assert.True(File.Exists(Path.Combine(_left, "keep.txt")));    // came back from the right
        Assert.False(Directory.Exists(Path.Combine(_right, "_Trash")));
    }

    [Fact]
    public async Task Mirror_moves_what_it_would_delete_into_the_recycle_folder()
    {
        Write(@"left\keep.txt", "K");
        Write(@"right\stale.txt", "S");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.ToRight, Mirror = true });

        var result = await Run(config);

        Assert.True(result.Success);
        Assert.Equal(1, result.Targets[0].FilesRecycled);
        Assert.False(File.Exists(Path.Combine(_right, "stale.txt")));
        Assert.True(Directory.Exists(Path.Combine(_right, "_Trash")));
        Assert.Contains("stale.txt",
            Directory.EnumerateFiles(Path.Combine(_right, "_Trash"), "*", SearchOption.AllDirectories)
                     .Select(Path.GetFileName));
    }

    [Fact]
    public async Task An_invalid_pair_is_skipped_and_reported_as_failed()
    {
        // Right nested inside left: a mirror here could delete the source.
        var nested = Path.Combine(_left, "inner");
        Directory.CreateDirectory(nested);
        var config = Config(new SyncPair { Name = "Bad", Left = _left, Right = nested, Direction = SyncDirection.ToRight });

        var result = await Run(config);

        Assert.False(result.Success);
        Assert.False(result.Targets[0].Success);
        Assert.Contains("inside", result.Targets[0].Note);
    }

    [Fact]
    public async Task Preview_reports_success_without_changing_anything()
    {
        Write(@"left\a.txt", "A");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.ToRight });

        var result = await Run(config, listOnly: true);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(_right, "a.txt")));   // nothing actually copied
    }

    [Fact]
    public async Task A_disabled_pair_is_ignored()
    {
        Write(@"left\a.txt", "A");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.ToRight, Enabled = false });

        var result = await Run(config);

        Assert.True(result.Success);
        Assert.Empty(result.Targets);
        Assert.False(File.Exists(Path.Combine(_right, "a.txt")));
    }

    [Fact]
    public async Task Excluded_names_are_not_copied()
    {
        Write(@"left\keep.txt", "K");
        Write(@"left\Thumbs.db", "junk");
        var config = Config(new SyncPair { Left = _left, Right = _right, Direction = SyncDirection.ToRight });
        config.ExcludeFiles = new List<string> { "Thumbs.db" };

        await Run(config);

        Assert.True(File.Exists(Path.Combine(_right, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(_right, "Thumbs.db")));
    }
}
