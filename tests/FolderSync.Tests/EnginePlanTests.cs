using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// What 1.3.0 added to the engine, run for real against robocopy and real folders: the list of what
/// changed, progress that counts, previous versions on one-directional pairs, running a single pair,
/// approving a deletion the guard stopped, and a preview that says exactly what a sync would do.
///
/// All of it comes from one list-only pass made before anything changes, so these tests are also what
/// holds that pass to the truth: each one compares what the engine <em>said</em> with what is on disk.
/// </summary>
public class EnginePlanTests : IDisposable
{
    private readonly string _root;
    private readonly string _left;
    private readonly string _right;

    public EnginePlanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-plan-" + Guid.NewGuid().ToString("N")[..8]);
        _left = Path.Combine(_root, "left");
        _right = Path.Combine(_root, "right");
        Directory.CreateDirectory(_left);
        Directory.CreateDirectory(_right);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string side, string relative, string content, DateTime? modified = null)
    {
        string path = Path.Combine(_root, side, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modified is { } when) File.SetLastWriteTime(path, when);
    }

    private SyncPair Pair(SyncDirection direction = SyncDirection.ToRight, bool mirror = false, bool keepVersions = false) =>
        new() { Name = "P", Left = _left, Right = _right, Direction = direction, Mirror = mirror, KeepVersions = keepVersions };

    private static SyncConfig Config(params SyncPair[] pairs) => new()
    {
        Pairs = pairs.ToList(),
        RecycleFolderName = "_Trash",
        RecycleDeletedFiles = true,
        ExcludeFiles = new List<string>(),
        ExcludeFolders = new List<string>(),
    };

    private string[] TrashFiles(string side, string name) =>
        Directory.Exists(Path.Combine(_root, side, "_Trash"))
            ? Directory.GetFiles(Path.Combine(_root, side, "_Trash"), name, SearchOption.AllDirectories)
            : Array.Empty<string>();

    // ---------- what changed ----------

    [Fact]
    public async Task A_run_lists_each_file_it_copied_replaced_and_removed()
    {
        Write("left", "new.txt", "brand new");
        Write("left", @"sub\changed.txt", "the new content");
        Write("right", @"sub\changed.txt", "old", DateTime.Now.AddDays(-2));
        Write("left", "same.txt", "same");
        File.Copy(Path.Combine(_left, "same.txt"), Path.Combine(_right, "same.txt"));
        Write("right", "gone.txt", "removed from the source");

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync();

        Assert.True(result.Success);
        var pair = Assert.Single(result.Targets);
        var changes = pair.Changes.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(3, changes.Count);                           // same.txt was not touched, so it is not listed
        Assert.Equal(ChangeKind.New, changes["new.txt"].Kind);
        Assert.Equal(ChangeKind.Updated, changes[@"sub\changed.txt"].Kind);
        Assert.Equal(ChangeKind.Deleted, changes["gone.txt"].Kind);
        Assert.True(changes["gone.txt"].Kept);                    // it went to the recycle folder
        Assert.False(changes[@"sub\changed.txt"].Kept);           // this pair does not keep versions
        Assert.All(pair.Changes, c => Assert.False(c.Failed));
        Assert.Equal(0, pair.ChangesOmitted);

        // Sizes are the real sizes, and the total is what was copied - not what was removed.
        Assert.Equal(new FileInfo(Path.Combine(_left, "new.txt")).Length, changes["new.txt"].Size);
        long copied = new FileInfo(Path.Combine(_left, "new.txt")).Length
                    + new FileInfo(Path.Combine(_left, "sub", "changed.txt")).Length;
        Assert.Equal(copied, pair.BytesCopied);

        // And the list matches the disk and robocopy's own count.
        Assert.Equal(2, pair.FilesCopied);
        Assert.Equal("the new content", File.ReadAllText(Path.Combine(_right, "sub", "changed.txt")));
        Assert.False(File.Exists(Path.Combine(_right, "gone.txt")));
    }

    [Fact]
    public async Task With_the_recycle_folder_off_a_deletion_is_listed_as_gone_for_good()
    {
        Write("left", "keep.txt", "K");
        Write("right", "keep.txt", "K");
        File.SetLastWriteTime(Path.Combine(_right, "keep.txt"), File.GetLastWriteTime(Path.Combine(_left, "keep.txt")));
        Write("right", "gone.txt", "removed from the source");
        var config = Config(Pair(mirror: true));
        config.RecycleDeletedFiles = false;

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        var deleted = Assert.Single(result.Targets[0].Changes);
        Assert.Equal(ChangeKind.Deleted, deleted.Kind);
        Assert.Equal("gone.txt", deleted.Path);
        Assert.False(deleted.Kept);                               // nothing to restore it from
        Assert.Equal("Deleted", deleted.KindText);
        Assert.False(File.Exists(Path.Combine(_right, "gone.txt")));
        Assert.False(Directory.Exists(Path.Combine(_right, "_Trash")));
    }

    [Fact]
    public async Task A_deleted_folder_is_listed_file_by_file()
    {
        // robocopy lists the folder and each file in it; the folder is moved aside as one item, and the
        // record names the files, which is what someone looking for a lost document needs.
        Write("left", "keep.txt", "K");
        Write("right", @"old\a.txt", "A");
        Write("right", @"old\deep\b.txt", "B");

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync();

        var pair = Assert.Single(result.Targets);
        var deleted = pair.Changes.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path).OrderBy(p => p).ToList();
        Assert.Equal(new[] { @"old\a.txt", @"old\deep\b.txt" }, deleted);
        Assert.Equal(1, pair.FilesRecycled);                      // one item moved: the folder
        Assert.False(Directory.Exists(Path.Combine(_right, "old")));
    }

    [Fact]
    public async Task A_run_that_changes_nothing_lists_nothing()
    {
        Write("left", "a.txt", "A");
        var config = Config(Pair(mirror: true));
        await new SyncEngine(config).RunAsync();

        var second = await new SyncEngine(config).RunAsync();

        Assert.True(second.Success);
        Assert.Empty(second.Targets[0].Changes);
        Assert.Equal(0, second.Targets[0].BytesCopied);
    }

    [Fact]
    public async Task Names_the_console_cannot_spell_are_listed_exactly()
    {
        // robocopy's console output is in the OEM code page and turns these into "?" - which is why
        // the list comes from a Unicode log file instead.
        const string name = "café 中文 – notes.txt";
        Write("left", name, "unicode");

        var result = await new SyncEngine(Config(Pair())).RunAsync();

        var change = Assert.Single(result.Targets[0].Changes);
        Assert.Equal(name, change.Path);
        Assert.True(File.Exists(Path.Combine(_right, name)));
    }

    [Fact]
    public async Task Changes_beyond_the_limit_are_counted_rather_than_listed()
    {
        for (int i = 0; i < 12; i++) Write("left", $"file{i:00}.txt", "x");

        var result = await new SyncEngine(Config(Pair())).RunAsync(new SyncOptions { ChangeLimit = 5 });

        var pair = Assert.Single(result.Targets);
        Assert.Equal(12, pair.FilesCopied);                       // every file was still copied
        Assert.Equal(5, pair.Changes.Count);
        Assert.Equal(7, pair.ChangesOmitted);
        Assert.Equal(12, pair.BytesCopied);                       // and every byte is still counted
    }

    [Fact]
    public async Task A_two_way_pair_says_which_side_received_each_file()
    {
        Write("left", "from-left.txt", "L");
        Write("right", "from-right.txt", "R");

        var result = await new SyncEngine(Config(Pair(SyncDirection.TwoWay))).RunAsync();

        var changes = result.Targets[0].Changes.ToDictionary(c => c.Path);
        Assert.False(changes["from-left.txt"].ToLeft);
        Assert.True(changes["from-right.txt"].ToLeft);
    }

    [Fact]
    public async Task A_file_that_could_not_be_copied_is_recorded_as_that_not_as_copied()
    {
        Write("left", "ok.txt", "fine");
        Write("left", "locked.txt", "the new content");
        Write("right", "locked.txt", "old", DateTime.Now.AddDays(-2));

        SyncResult result;
        // Held open with no sharing, so robocopy cannot replace it.
        using (new FileStream(Path.Combine(_right, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await new SyncEngine(Config(Pair())).RunAsync();
        }

        var pair = Assert.Single(result.Targets);
        Assert.True(pair.FilesFailed > 0);
        var changes = pair.Changes.ToDictionary(c => c.Path);
        Assert.True(changes["locked.txt"].Failed);
        Assert.False(changes["ok.txt"].Failed);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_right, "locked.txt")));

        // Only what arrived is counted.
        Assert.Equal(new FileInfo(Path.Combine(_left, "ok.txt")).Length, pair.BytesCopied);
    }

    // ---------- progress ----------

    [Fact]
    public async Task Progress_counts_up_to_the_number_of_files_and_never_goes_backwards()
    {
        for (int i = 0; i < 40; i++) Write("left", $"f{i:00}.txt", new string('x', 100));

        var seen = new List<SyncProgress>();
        var engine = new SyncEngine(Config(Pair()));
        engine.Progress += p => { lock (seen) seen.Add(p); };

        await engine.RunAsync();

        Assert.Contains(seen, p => p.Checking);                   // it says so while it works out what to do
        var copying = seen.Where(p => !p.Checking).ToList();
        Assert.NotEmpty(copying);
        Assert.All(copying, p => Assert.Equal(40, p.FilesTotal));
        Assert.All(copying, p => Assert.InRange(p.FilesDone, 0, 40));
        Assert.Equal(40, copying[^1].FilesDone);                  // ends on "40 of 40"
        Assert.Equal(1.0, copying[^1].Fraction);

        for (int i = 1; i < copying.Count; i++)
            Assert.True(copying[i].FilesDone >= copying[i - 1].FilesDone, "progress went backwards");

        Assert.Contains("40 of 40 files", copying[^1].Text);
        Assert.Contains("Pair 1 of 1", copying[^1].Text);
    }

    [Fact]
    public async Task Progress_says_so_when_there_is_nothing_to_copy()
    {
        var seen = new List<SyncProgress>();
        var engine = new SyncEngine(Config(Pair()));
        engine.Progress += p => { lock (seen) seen.Add(p); };

        await engine.RunAsync();

        Assert.Contains("nothing to copy", seen[^1].Text);
        Assert.Equal(1.0, seen[^1].Fraction);
    }

    // ---------- previous versions on a one-directional pair ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_one_way_pair_that_asks_for_it_keeps_the_version_a_changed_file_replaces(bool mirror)
    {
        Write("left", "report.txt", "the corrected version");
        Write("right", "report.txt", "the version that was there", DateTime.Now.AddDays(-2));
        Write("left", "new.txt", "nothing to keep for a new file");

        var config = Config(Pair(mirror: mirror, keepVersions: true));
        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.Equal("the corrected version", File.ReadAllText(Path.Combine(_right, "report.txt")));

        // The copy it replaced is recoverable, filed as a replacement rather than a deletion.
        var kept = Assert.Single(TrashFiles("right", "report.txt"));
        Assert.Equal("the version that was there", File.ReadAllText(kept));
        Assert.EndsWith(RecycleStore.ReplacedSuffix, Path.GetFileName(Path.GetDirectoryName(kept))!);
        Assert.Empty(TrashFiles("right", "new.txt"));

        var pair = Assert.Single(result.Targets);
        Assert.Equal(1, pair.FilesBackedUp);
        Assert.True(pair.Changes.Single(c => c.Path == "report.txt").Kept);
        Assert.False(pair.Changes.Single(c => c.Path == "new.txt").Kept);

        // The sending side is never written to by a one-directional pair, versions or not.
        Assert.False(Directory.Exists(Path.Combine(_left, "_Trash")));
    }

    [Fact]
    public async Task A_one_way_pair_keeps_no_versions_unless_asked()
    {
        Write("left", "report.txt", "new");
        Write("right", "report.txt", "old", DateTime.Now.AddDays(-2));

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.Targets[0].FilesBackedUp);
        Assert.False(Directory.Exists(Path.Combine(_right, "_Trash")));
    }

    [Fact]
    public async Task Versions_are_not_kept_when_the_recycle_folder_is_switched_off()
    {
        // The kept versions live in the recycle folder, so with recycling off there is nowhere to put
        // them - and the health check says so rather than the engine half-doing it.
        Write("left", "report.txt", "new");
        Write("right", "report.txt", "old", DateTime.Now.AddDays(-2));
        var config = Config(Pair(keepVersions: true));
        config.RecycleDeletedFiles = false;

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.Targets[0].FilesBackedUp);
        Assert.False(Directory.Exists(Path.Combine(_right, "_Trash")));
        Assert.False(config.KeepsVersionsFor(config.Pairs[0]));
    }

    [Fact]
    public async Task A_kept_version_can_be_put_back_from_the_recycle_folder()
    {
        Write("left", "report.txt", "a mistake");
        Write("right", "report.txt", "the good version", DateTime.Now.AddDays(-2));
        var config = Config(Pair(mirror: true, keepVersions: true));
        await new SyncEngine(config).RunAsync();

        var batch = Assert.Single(RecycleStore.GetBatches(config));
        Assert.Equal(RecycleReason.Replaced, batch.Reason);
        Assert.Equal(_left, batch.RestoreTo);                     // back to the side it was changed on

        var restore = RecycleStore.Restore(batch.Path, RecycleStore.GetFiles(batch), batch.RestoreTo, overwrite: true);

        Assert.True(restore.Success);
        Assert.Equal("the good version", File.ReadAllText(Path.Combine(_left, "report.txt")));
    }

    // ---------- one pair at a time ----------

    [Fact]
    public async Task Only_the_pairs_asked_for_are_run()
    {
        string otherLeft = Path.Combine(_root, "other-left"), otherRight = Path.Combine(_root, "other-right");
        Directory.CreateDirectory(otherLeft);
        Directory.CreateDirectory(otherRight);
        Write("left", "a.txt", "A");
        File.WriteAllText(Path.Combine(otherLeft, "b.txt"), "B");

        var first = Pair();
        var second = new SyncPair { Name = "Other", Left = otherLeft, Right = otherRight };
        var config = Config(first, second);

        var result = await new SyncEngine(config).RunAsync(new SyncOptions
        {
            OnlyPairs = new[] { PairHistory.KeyFor(otherLeft, otherRight) },
        });

        Assert.Equal("Other", Assert.Single(result.Targets).Name);
        Assert.True(File.Exists(Path.Combine(otherRight, "b.txt")));
        Assert.False(File.Exists(Path.Combine(_right, "a.txt")));      // the other pair was left alone
    }

    // ---------- approving a deletion the guard stopped ----------

    private void FillRight(int count)
    {
        for (int i = 0; i < count; i++) Write("right", $"file{i:00}.txt", "valuable");
    }

    [Fact]
    public async Task A_stopped_mirror_says_it_was_the_guard_and_how_much_was_at_stake()
    {
        FillRight(60);

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync();

        var pair = Assert.Single(result.Targets);
        Assert.False(pair.Success);
        Assert.True(pair.GuardStopped);
        Assert.Equal(60, pair.GuardFiles);
        Assert.Empty(pair.Changes);                               // nothing was done, so nothing is listed
        Assert.Equal(60, Directory.GetFiles(_right).Length);
    }

    [Fact]
    public async Task An_approved_deletion_goes_through_once_and_is_still_recoverable()
    {
        FillRight(60);
        var config = Config(Pair(mirror: true));
        string key = PairHistory.KeyFor(_left, _right);

        var result = await new SyncEngine(config).RunAsync(new SyncOptions
        {
            ApprovedDeletions = new Dictionary<string, int> { [key] = 60 },
        });

        var pair = Assert.Single(result.Targets);
        Assert.True(result.Success);
        Assert.False(pair.GuardStopped);
        Assert.Empty(Directory.GetFiles(_right));
        Assert.Equal(60, pair.Changes.Count(c => c.Kind == ChangeKind.Deleted && c.Kept));
        Assert.Equal(60, Directory.GetFiles(Path.Combine(_right, "_Trash"), "*", SearchOption.AllDirectories).Length);

        // The approval was for that run. The guard is still on for the next one.
        FillRight(60);
        var next = await new SyncEngine(config).RunAsync();
        Assert.True(next.Targets[0].GuardStopped);
        Assert.Equal(60, Directory.GetFiles(_right).Length);
    }

    [Fact]
    public async Task An_approval_does_not_cover_a_bigger_deletion_than_the_one_that_was_shown()
    {
        // Reviewed when 40 files were at stake; by the time it runs there are 60.
        FillRight(60);
        string key = PairHistory.KeyFor(_left, _right);

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync(new SyncOptions
        {
            ApprovedDeletions = new Dictionary<string, int> { [key] = 40 },
        });

        var pair = Assert.Single(result.Targets);
        Assert.False(pair.Success);
        Assert.True(pair.GuardStopped);
        Assert.Contains("approval was not used", pair.Note);
        Assert.Equal(60, Directory.GetFiles(_right).Length);
    }

    [Fact]
    public async Task An_approval_for_one_pair_does_not_open_the_guard_for_another()
    {
        FillRight(60);

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync(new SyncOptions
        {
            ApprovedDeletions = new Dictionary<string, int> { [PairHistory.KeyFor(@"C:\other", @"D:\other")] = 1000 },
        });

        Assert.True(result.Targets[0].GuardStopped);
        Assert.Equal(60, Directory.GetFiles(_right).Length);
    }

    // ---------- preview ----------

    [Fact]
    public async Task A_preview_lists_every_change_and_makes_none()
    {
        Write("left", "new.txt", "brand new");
        Write("left", "changed.txt", "newer");
        Write("right", "changed.txt", "old", DateTime.Now.AddDays(-2));
        Write("right", "gone.txt", "removed from the source");

        var result = await new SyncEngine(Config(Pair(mirror: true, keepVersions: true))).RunAsync(listOnly: true);

        Assert.True(result.Success);
        var plan = Assert.Single(result.Plans);
        Assert.False(plan.Blocked);
        Assert.Equal(2, plan.FilesToCopy);
        Assert.Equal(1, plan.FilesToDelete);
        Assert.False(plan.GuardWouldStop);

        var changes = plan.Changes.ToDictionary(c => c.Path);
        Assert.Equal(ChangeKind.New, changes["new.txt"].Kind);
        Assert.Equal(ChangeKind.Updated, changes["changed.txt"].Kind);
        Assert.True(changes["changed.txt"].Kept);                 // says the old version would be kept
        Assert.Equal(ChangeKind.Deleted, changes["gone.txt"].Kind);
        Assert.True(changes["gone.txt"].Kept);

        // Nothing moved.
        Assert.False(File.Exists(Path.Combine(_right, "new.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_right, "changed.txt")));
        Assert.True(File.Exists(Path.Combine(_right, "gone.txt")));
        Assert.False(Directory.Exists(Path.Combine(_right, "_Trash")));
    }

    [Fact]
    public async Task A_preview_warns_that_the_guard_would_stop_the_sync()
    {
        FillRight(60);

        var result = await new SyncEngine(Config(Pair(mirror: true))).RunAsync(listOnly: true);

        var plan = Assert.Single(result.Plans);
        Assert.True(plan.GuardWouldStop);
        Assert.Contains("would delete 60", plan.GuardReason);
        Assert.Equal(60, plan.FilesToDelete);
        Assert.Equal(60, plan.Changes.Count(c => c.Kind == ChangeKind.Deleted));
        Assert.Equal(60, Directory.GetFiles(_right).Length);      // and of course nothing was deleted
    }

    [Fact]
    public async Task A_preview_of_an_additive_pair_does_not_list_the_extras_it_would_leave_alone()
    {
        Write("left", "a.txt", "A");
        Write("right", "only-here.txt", "an additive pair never removes this");

        var result = await new SyncEngine(Config(Pair(mirror: false))).RunAsync(listOnly: true);

        var plan = Assert.Single(result.Plans);
        Assert.Equal(0, plan.FilesToDelete);
        Assert.Equal("a.txt", Assert.Single(plan.Changes).Path);
    }

    [Fact]
    public async Task A_preview_says_why_a_pair_cannot_run()
    {
        var missing = new SyncPair { Name = "Gone", Left = Path.Combine(_root, "does-not-exist"), Right = _right };

        var result = await new SyncEngine(Config(missing)).RunAsync(listOnly: true);

        var plan = Assert.Single(result.Plans);
        Assert.True(plan.Blocked);
        Assert.Contains("does not exist", plan.Note);
    }

    // ---------- a run that is stopped part-way ----------

    [Fact]
    public async Task A_cancelled_run_leaves_a_result_that_says_so()
    {
        Write("left", "a.txt", "A");
        var engine = new SyncEngine(Config(Pair()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(new SyncOptions(), cts.Token));

        Assert.NotNull(engine.Current);
        Assert.True(engine.Current!.Cancelled);
        Assert.False(engine.Current.Success);
        Assert.NotEqual(default, engine.Current.Finished);
    }
}
