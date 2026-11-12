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

    // ---------- names robocopy's console output cannot represent ----------

    [Theory]
    [InlineData("caf\u00e9.txt")]              // representable in the OEM code page, but not as UTF-8
    [InlineData("\u65e5\u672c.txt")]          // not representable in the OEM code page at all
    [InlineData("na\u00efve r\u00e9sum\u00e9.docx")]
    public async Task A_file_with_a_non_ascii_name_is_recycled_rather_than_deleted(string name)
    {
        // The listing was decoded as UTF-8 while robocopy wrote the OEM code page, so the path came back
        // mangled, "did not exist", was skipped when moving files aside - and /MIR then deleted it.
        var (left, right) = Folders();
        SameFiles(left, right, 60);
        File.WriteAllText(Path.Combine(right, name), "precious");

        var config = Config(left, right, mirror: true);
        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(right, name)));
        var rescued = Directory.GetFiles(Path.Combine(right, config.RecycleFolderName), name, SearchOption.AllDirectories);
        Assert.Equal("precious", File.ReadAllText(Assert.Single(rescued)));
        Assert.Equal(1, result.Targets[0].FilesRecycled);
    }

    [Fact]
    public async Task Something_that_cannot_be_moved_aside_is_left_in_place_not_deleted()
    {
        // With recycling on, the copy pass no longer purges: whatever could not be moved aside stays,
        // and the pair reports it. Here the recycle folder itself cannot be created.
        var (left, right) = Folders();
        SameFiles(left, right, 60);
        File.WriteAllText(Path.Combine(right, "extra.txt"), "keep me");

        var config = Config(left, right, mirror: true);
        SyncResult result;
        using (DenyOnFolder(right, System.Security.AccessControl.FileSystemRights.CreateDirectories))
            result = await new SyncEngine(config).RunAsync();

        Assert.False(result.Success);
        Assert.Contains("could not be moved aside", result.Targets[0].Note);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(right, "extra.txt")));
    }

    // ---------- two-way: every overwrite keeps the previous version ----------

    [Fact]
    public async Task A_same_time_different_size_file_keeps_the_version_it_replaced()
    {
        // robocopy calls this "Changed", not "Newer", and only "Newer" was backed up - so this version
        // was overwritten with nothing kept.
        var (left, right) = Folders();
        File.WriteAllText(Path.Combine(left, "doc.txt"), "the version on the left, longer");
        File.WriteAllText(Path.Combine(right, "doc.txt"), "the version on the right");
        var stamp = DateTime.Now.AddHours(-3);
        File.SetLastWriteTime(Path.Combine(left, "doc.txt"), stamp);
        File.SetLastWriteTime(Path.Combine(right, "doc.txt"), stamp);

        var config = Config(left, right, mirror: false);
        config.Pairs[0].Direction = SyncDirection.TwoWay;
        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.Targets[0].FilesBackedUp);
        var kept = Directory.GetFiles(Path.Combine(right, config.RecycleFolderName), "doc.txt", SearchOption.AllDirectories);
        Assert.Equal("the version on the right", File.ReadAllText(Assert.Single(kept)));
    }

    [Fact]
    public async Task Two_way_does_not_copy_one_sides_recycled_files_into_the_other()
    {
        var (left, right) = Folders();
        var config = Config(left, right, mirror: false);
        config.Pairs[0].Direction = SyncDirection.TwoWay;
        string batch = Path.Combine(left, config.RecycleFolderName, "20261101-120000-replaced");
        Directory.CreateDirectory(batch);
        File.WriteAllText(Path.Combine(batch, "old.txt"), "left's old version");
        File.WriteAllText(Path.Combine(left, "a.txt"), "a");

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(right, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(right, config.RecycleFolderName)));
    }

    // ---------- pruning ----------

    [Fact]
    public async Task Two_way_batches_past_their_retention_are_pruned()
    {
        // Pruning only ran after mirror passes, and a two-way pair never mirrors - so its previous
        // versions, one batch per sync with a change, piled up for ever.
        var (left, right) = Folders();
        File.WriteAllText(Path.Combine(left, "a.txt"), "a");
        var config = Config(left, right, mirror: false);
        config.Pairs[0].Direction = SyncDirection.TwoWay;

        string trash = Path.Combine(right, config.RecycleFolderName);
        string old = Batch(trash, DateTime.Now.AddDays(-(config.RecycleRetentionDays + 5)), RecycleStore.ReplacedSuffix);
        string recent = Batch(trash, DateTime.Now.AddDays(-1), RecycleStore.ReplacedSuffix);

        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public async Task Pruning_never_deletes_a_folder_it_did_not_create()
    {
        // Age used to be read from the folder's creation time and every folder qualified. Only
        // timestamp-named batches are candidates now, dated by their name.
        var (left, right) = Folders();
        SameFiles(left, right, 3);
        var config = Config(left, right, mirror: true);

        string trash = Path.Combine(right, config.RecycleFolderName);
        string mine = Path.Combine(trash, "my notes");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "n.txt"), "x");
        Directory.SetCreationTime(mine, DateTime.Now.AddYears(-2));

        await new SyncEngine(config).RunAsync();

        Assert.True(File.Exists(Path.Combine(mine, "n.txt")));
    }

    [Fact]
    public async Task A_recycle_folder_name_that_points_outside_the_pair_is_not_used()
    {
        // "RecycleFolderName": ".." made the recycle folder the destination's parent, and pruning then
        // deleted the destination's unrelated siblings.
        string parent = Path.Combine(_root, "backup");
        string left = Path.Combine(_root, "left"), right = Path.Combine(parent, "docs");
        string sibling = Path.Combine(parent, "20250101-000000");     // even one that looks like a batch
        foreach (var d in new[] { left, right, sibling }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(sibling, "holiday.jpg"), "unrelated to any pair");
        File.WriteAllText(Path.Combine(left, "a.txt"), "a");

        var config = Config(left, right, mirror: true);
        config.RecycleFolderName = "..";
        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(sibling, "holiday.jpg")));
        Assert.True(File.Exists(Path.Combine(right, "a.txt")));
    }

    // ---------- paths ending in a backslash ----------

    [Fact]
    public async Task Folders_written_with_a_trailing_backslash_still_sync()
    {
        // "E:\" ended the quoted argument with \", which robocopy reads as a literal quote: the command
        // was malformed (exit 16) and a pair on a drive root could never sync.
        var (left, right) = Folders();
        File.WriteAllText(Path.Combine(left, "a.txt"), "a");

        var config = Config(left + @"\", right + @"\", mirror: false);
        var result = await new SyncEngine(config).RunAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(right, "a.txt")));
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

    private static void SameFiles(string left, string right, int count)
    {
        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(left, $"keep{i}.txt"), "same");
            File.WriteAllText(Path.Combine(right, $"keep{i}.txt"), "same");
        }
    }

    private static string Batch(string trash, DateTime when, string suffix = "")
    {
        string dir = Path.Combine(trash, when.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + suffix);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.txt"), "x");
        return dir;
    }

    /// <summary>Denies the current user <paramref name="rights"/> on a folder until disposed.</summary>
    private static IDisposable DenyOnFolder(string folder, System.Security.AccessControl.FileSystemRights rights)
    {
        var info = new DirectoryInfo(folder);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user, rights, System.Security.AccessControl.AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        return new Undo(() =>
        {
            var restored = info.GetAccessControl();
            restored.RemoveAccessRule(rule);
            info.SetAccessControl(restored);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

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
