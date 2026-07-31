using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The two things that report a sync to a human: the notification email, and how the recycle folder
/// describes what it is holding.
///
/// Both had defects that were invisible in normal use — an email quietly reporting the wrong source
/// for every send since 1.1.0, and replaced versions filed as if they had been deleted. These tests
/// exist so neither can come back unnoticed.
/// </summary>
public class ReportingTests
{
    // ---------- the emailed summary ----------

    private static SyncResult Run(params TargetResult[] targets) =>
        new() { Success = targets.All(t => t.Skipped || t.Success), Targets = targets.ToList() };

    [Fact]
    public void The_summary_never_reports_the_dead_pre_pairs_fields()
    {
        // Source and Mirror are the pre-1.1.0 single-source fields. Migrating to pairs blanks Source
        // and stops using Mirror, so an email built from them showed a blank source and "Additive"
        // no matter what the pairs actually did.
        var config = new SyncConfig { Source = "", Mirror = false };
        config.Pairs.Add(new SyncPair { Name = "Docs", Left = @"C:\Docs", Right = @"D:\Backup", Mirror = true });

        var (_, body) = Emailer.BuildSummary(
            Run(new TargetResult { Name = "Docs", Destination = @"D:\Backup", Success = true, FilesCopied = 12 }),
            config, @"C:\logs\sync.log");

        Assert.DoesNotContain("Source:", body);
        Assert.DoesNotContain("Additive", body);

        // What it says instead is drawn from the run itself.
        Assert.Contains("Docs -> D:\\Backup", body);
        Assert.Contains("copied 12", body);
    }

    [Fact]
    public void A_skipped_pair_is_never_reported_as_a_success()
    {
        // Skipping deliberately leaves Success true so the run stays green, which meant a pair that
        // never ran was printed as "[OK ]" - the email claiming a backup happened when none did.
        var skipped = new TargetResult
        {
            Name = "USB backup",
            Destination = @"Q:\Backup",
            Success = true,
            Skipped = true,
            Note = "drive not connected: Q:\\Backup",
        };

        var (subject, body) = Emailer.BuildSummary(Run(skipped), new SyncConfig(), "log.txt");

        Assert.Contains("[SKIP] USB backup", body);
        Assert.DoesNotContain("[OK  ] USB backup", body);
        Assert.Contains("drive not connected", body);

        // The subject says so too, rather than a bare "OK".
        Assert.Contains("some skipped", subject);
        Assert.Contains("not treated as an error", body);
    }

    [Fact]
    public void Every_counter_reaches_the_email()
    {
        var target = new TargetResult
        {
            Name = "Docs",
            Destination = @"D:\Backup",
            Success = true,
            FilesCopied = 7,
            FilesFailed = 1,
            FilesRecycled = 3,
            FilesBackedUp = 2,
        };

        var (_, body) = Emailer.BuildSummary(Run(target), new SyncConfig(), "log.txt");

        Assert.Contains("Files copied      : 7", body);
        Assert.Contains("Files failed      : 1", body);
        Assert.Contains("Deletions kept    : 3", body);
        Assert.Contains("Previous versions : 2", body);
        Assert.Contains("_FolderSyncTrash", body);      // where to find them
    }

    [Fact]
    public void A_failure_reports_its_reason_and_marks_the_subject()
    {
        var failed = new TargetResult
        {
            Name = "Photos",
            Destination = @"E:\Photos",
            Success = false,
            Note = "Right folder does not exist: E:\\Photos",
        };

        var (subject, body) = Emailer.BuildSummary(Run(failed), new SyncConfig(), "log.txt");

        Assert.Contains("ERRORS", subject);
        Assert.Contains("[FAIL] Photos", body);
        Assert.Contains("does not exist", body);
    }

    [Fact]
    public void Counters_that_are_zero_are_left_out_rather_than_adding_noise()
    {
        var clean = new TargetResult { Name = "Docs", Destination = @"D:\Backup", Success = true, FilesCopied = 4 };

        var (_, body) = Emailer.BuildSummary(Run(clean), new SyncConfig(), "log.txt");

        Assert.DoesNotContain("Deletions kept", body);
        Assert.DoesNotContain("Previous versions", body);
    }

    // ---------- how a recycle batch describes itself ----------

    [Theory]
    [InlineData("20260731-120000", RecycleReason.Deleted)]
    [InlineData("20260731-120000-replaced", RecycleReason.Replaced)]
    [InlineData("20260731-120000-REPLACED", RecycleReason.Replaced)]   // case-insensitive
    public void A_batch_folder_name_says_what_it_holds(string folderName, RecycleReason expected)
    {
        Assert.Equal(expected, RecycleStore.ReasonOf(folderName));
    }

    [Fact]
    public void Batches_written_before_this_distinction_existed_still_read_as_deletions()
    {
        // Old batches have no suffix, and deletions is the correct reading of them.
        Assert.Equal(RecycleReason.Deleted, RecycleStore.ReasonOf("20260101-093000"));
        Assert.Equal(RecycleReason.Deleted, RecycleStore.ReasonOf(""));
    }

    [Fact]
    public void The_timestamp_is_still_read_when_a_suffix_is_present()
    {
        // Otherwise a replaced batch would sort to the bottom of the list with a blank date.
        var expected = new DateTime(2026, 7, 31, 12, 30, 45);

        Assert.Equal(expected, RecycleStore.ParseBatchTimestamp("20260731-123045"));
        Assert.Equal(expected, RecycleStore.ParseBatchTimestamp("20260731-123045" + RecycleStore.ReplacedSuffix));
    }

    [Fact]
    public void Restoring_a_replaced_file_is_described_as_a_rollback_not_a_recovery()
    {
        // The distinction the UI has to convey: one brings a file back, the other throws away the
        // newer content the sync just delivered.
        var deleted = new RecycleBatch { Reason = RecycleReason.Deleted };
        var replaced = new RecycleBatch { Reason = RecycleReason.Replaced };

        Assert.Equal("Deleted by mirror", deleted.ReasonText);
        Assert.Contains("Restoring puts them back", deleted.RestoreMeaning);

        Assert.Equal("Replaced by a newer copy", replaced.ReasonText);
        Assert.Contains("still exist", replaced.RestoreMeaning);
        Assert.Contains("rolls them back", replaced.RestoreMeaning);
    }
}
