using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The guard that stops a mirror deleting more than looks intentional. Tested exhaustively because
/// its whole purpose is preventing data loss, and a pure decision function makes that cheap.
/// </summary>
public class DeletionGuardTests
{
    private const int Min = 25;
    private const int Percent = 25;

    [Fact]
    public void The_catastrophic_case_is_caught()
    {
        // The source folder was momentarily empty or wrong, so a mirror would clear the destination.
        Assert.True(DeletionGuard.ShouldStop(5000, 5000, Min, Percent, out string reason));

        // The message has to be specific enough to act on, not just "something looks wrong".
        Assert.Contains("5,000", reason);
        Assert.Contains("100%", reason);
    }

    [Fact]
    public void An_ordinary_tidy_up_is_allowed_through()
    {
        // 30 files out of 5,000 is 0.6% - normal housekeeping, must not block.
        Assert.False(DeletionGuard.ShouldStop(30, 5000, Min, Percent, out _));
    }

    [Fact]
    public void A_small_folder_is_not_blocked_by_the_percentage_alone()
    {
        // 2 of 3 files is 67%, which would trip the percentage, but deleting 2 files is not a
        // disaster and blocking it would be infuriating. This is what the minimum is for.
        Assert.False(DeletionGuard.ShouldStop(2, 3, Min, Percent, out _));
    }

    [Fact]
    public void The_minimum_and_the_percentage_must_both_be_exceeded()
    {
        // Over the minimum but a small share: allowed.
        Assert.False(DeletionGuard.ShouldStop(30, 10_000, Min, Percent, out _));

        // Over the share but under the minimum: allowed.
        Assert.False(DeletionGuard.ShouldStop(20, 40, Min, Percent, out _));

        // Both exceeded: stopped.
        Assert.True(DeletionGuard.ShouldStop(30, 100, Min, Percent, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Nothing_to_delete_is_never_a_problem(int toDelete)
    {
        Assert.False(DeletionGuard.ShouldStop(toDelete, 1000, Min, Percent, out _));
    }

    [Fact]
    public void An_empty_destination_cannot_trip_the_guard()
    {
        // Nothing there to lose, and it avoids dividing by zero.
        Assert.False(DeletionGuard.ShouldStop(10, 0, Min, Percent, out _));
    }

    [Fact]
    public void The_reason_is_empty_when_the_sync_is_allowed()
    {
        DeletionGuard.ShouldStop(1, 1000, Min, Percent, out string reason);
        Assert.Equal("", reason);
    }

    [Fact]
    public void A_zero_minimum_is_treated_as_one_rather_than_tripping_on_nothing()
    {
        // Guards against a config of 0 making every deletion suspicious.
        Assert.True(DeletionGuard.ShouldStop(1, 1, 0, Percent, out _));
        Assert.False(DeletionGuard.ShouldStop(0, 1, 0, Percent, out _));
    }

    [Fact]
    public void Turning_the_percentage_up_to_a_hundred_only_stops_a_complete_wipe()
    {
        Assert.False(DeletionGuard.ShouldStop(99, 100, Min, 100, out _));
        Assert.True(DeletionGuard.ShouldStop(100, 100, Min, 100, out _));
    }

    // ---------- counting the destination ----------

    [Fact]
    public void Files_already_rescued_do_not_count_towards_the_total()
    {
        // Otherwise each rescue inflates the destination count and the guard grows less sensitive
        // exactly as the recycle folder fills up.
        string root = NewFolder();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "_FolderSyncTrash", "20260731-120000"));

        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "_FolderSyncTrash", "20260731-120000", "old.txt"), "old");

        Assert.Equal(2, DeletionGuard.CountFiles(root, "_FolderSyncTrash"));
    }

    [Fact]
    public void A_folder_that_does_not_exist_counts_as_empty()
    {
        Assert.Equal(0, DeletionGuard.CountFiles(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid()), "_FolderSyncTrash"));
    }

    [Fact]
    public void One_unreadable_sub_folder_does_not_switch_the_guard_off()
    {
        // The count used to give up at the first unreadable sub-folder and report 0, and 0 makes the
        // guard stand down - so a single locked folder anywhere at the destination (a Documents folder
        // always has some) silently disabled it.
        string root = NewFolder();
        string locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(root, $"f{i}.txt"), "x");
        File.WriteAllText(Path.Combine(locked, "hidden.txt"), "x");

        using (DenyListing(locked))
        {
            int count = DeletionGuard.CountFiles(root, "_FolderSyncTrash");

            Assert.Equal(3, count);
            // And with that count, emptying the readable part is still caught.
            Assert.True(DeletionGuard.ShouldStop(3, count, 1, Percent, out _));
        }
    }

    [Fact]
    public void A_receiving_folder_that_cannot_be_read_at_all_is_uncountable_not_empty()
    {
        // "Empty" would let the deletion through; "could not count" makes the engine stop instead.
        string root = NewFolder();
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");

        using (DenyListing(root))
            Assert.Equal(DeletionGuard.Uncountable, DeletionGuard.CountFiles(root, "_FolderSyncTrash"));
    }

    [Fact]
    public void Hidden_files_count_towards_the_total()
    {
        string root = NewFolder();
        string hidden = Path.Combine(root, "h.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Equal(1, DeletionGuard.CountFiles(root, "_FolderSyncTrash"));
    }

    [Theory]
    [InlineData(0, 1)]       // 0% would stop every mirror over the minimum
    [InlineData(-10, 1)]
    [InlineData(150, 100)]   // over 100% could never fire
    public void A_percentage_outside_one_to_a_hundred_is_clamped(int configured, int effective)
    {
        // At the effective threshold exactly: stopped. Just under it: allowed.
        Assert.True(DeletionGuard.ShouldStop(effective, 100, 1, configured, out _));
        if (effective > 1)
            Assert.False(DeletionGuard.ShouldStop(effective - 1, 100, 1, configured, out _));
    }

    /// <summary>Denies the current user listing <paramref name="folder"/> until disposed.</summary>
    private static IDisposable DenyListing(string folder)
    {
        var info = new DirectoryInfo(folder);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);

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

    private static string NewFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "fs-guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
