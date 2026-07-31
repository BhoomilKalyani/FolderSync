using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Exercises the recycle browser and restore against real folders in a temp directory, since the
/// whole point of the feature is getting files back off disk.
/// </summary>
public class RecycleStoreTests : IDisposable
{
    private readonly string _root;

    public RecycleStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private string File_(string relative, string content = "x")
    {
        string p = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Theory]
    [InlineData("20260730-205223", 2026, 7, 30, 20, 52, 23)]
    [InlineData("20250101-000000", 2025, 1, 1, 0, 0, 0)]
    public void Batch_folder_names_parse_as_timestamps(string name, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), RecycleStore.ParseBatchTimestamp(name));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("20261399-999999")]
    public void Unparseable_batch_names_give_the_default_date(string name)
    {
        Assert.Equal(default, RecycleStore.ParseBatchTimestamp(name));
    }

    [Fact]
    public void GetBatches_finds_batches_across_destinations_newest_first()
    {
        var config = new SyncConfig
        {
            RecycleFolderName = "_Trash",
            Targets = new List<SyncTarget>
            {
                new() { Name = "Cloud",  Destination = Dir("cloud") },
                new() { Name = "Backup", Destination = Dir("backup") },
                new() { Name = "Absent", Destination = Path.Combine(_root, "does-not-exist") },
            },
        };

        Dir("cloud", "_Trash", "20260101-100000");
        File_(@"cloud\_Trash\20260101-100000\a.txt", "aaa");
        File_(@"cloud\_Trash\20260101-100000\sub\b.txt", "bb");
        Dir("backup", "_Trash", "20260202-110000");
        File_(@"backup\_Trash\20260202-110000\c.txt", "c");

        var batches = RecycleStore.GetBatches(config);

        Assert.Equal(2, batches.Count);
        Assert.Equal("Backup", batches[0].DestinationName);   // newest first
        Assert.Equal("Cloud", batches[1].DestinationName);
        Assert.Equal(2, batches[1].FileCount);                // counts nested files
        Assert.Equal(5, batches[1].TotalBytes);               // "aaa" + "bb"
    }

    [Fact]
    public void GetBatches_returns_nothing_when_no_recycle_folder_exists()
    {
        var config = new SyncConfig
        {
            RecycleFolderName = "_Trash",
            Targets = new List<SyncTarget> { new() { Name = "Cloud", Destination = Dir("cloud") } },
        };

        Assert.Empty(RecycleStore.GetBatches(config));
    }

    [Fact]
    public void GetFiles_lists_nested_files_with_relative_paths()
    {
        string batch = Dir("batch");
        File_(@"batch\root.txt");
        File_(@"batch\keep\a.txt");
        File_(@"batch\keep\deep\b.txt");

        var files = RecycleStore.GetFiles(batch);

        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.RelativePath == "root.txt");
        Assert.Contains(files, f => f.RelativePath == Path.Combine("keep", "a.txt"));
        Assert.Contains(files, f => f.RelativePath == Path.Combine("keep", "deep", "b.txt"));
    }

    [Fact]
    public void Restore_puts_files_back_with_their_folder_structure()
    {
        string batch = Dir("batch");
        File_(@"batch\root.txt", "R");
        File_(@"batch\keep\deep\b.txt", "B");
        string source = Dir("source");

        var result = RecycleStore.Restore(batch, RecycleStore.GetFiles(batch), source);

        Assert.True(result.Success);
        Assert.Equal(2, result.Restored);
        Assert.Equal("R", File.ReadAllText(Path.Combine(source, "root.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(source, "keep", "deep", "b.txt")));
        // The recycled copies stay put, so a failed restore can be retried.
        Assert.True(File.Exists(Path.Combine(batch, "root.txt")));
    }

    [Fact]
    public void Restore_skips_files_that_already_exist_unless_overwrite_is_set()
    {
        string batch = Dir("batch");
        File_(@"batch\root.txt", "recycled");
        string source = Dir("source");
        File.WriteAllText(Path.Combine(source, "root.txt"), "current");

        var skipped = RecycleStore.Restore(batch, RecycleStore.GetFiles(batch), source);

        Assert.Equal(0, skipped.Restored);
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal("current", File.ReadAllText(Path.Combine(source, "root.txt")));

        var overwritten = RecycleStore.Restore(batch, RecycleStore.GetFiles(batch), source, overwrite: true);

        Assert.Equal(1, overwritten.Restored);
        Assert.Equal("recycled", File.ReadAllText(Path.Combine(source, "root.txt")));
    }

    [Fact]
    public void Restore_reports_an_error_when_no_destination_is_given()
    {
        string batch = Dir("batch");
        File_(@"batch\root.txt");

        var result = RecycleStore.Restore(batch, RecycleStore.GetFiles(batch), "");

        Assert.False(result.Success);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void DeleteBatch_removes_the_folder_and_is_safe_to_repeat()
    {
        string batch = Dir("batch");
        File_(@"batch\root.txt");

        Assert.True(RecycleStore.DeleteBatch(batch).ok);
        Assert.False(Directory.Exists(batch));
        Assert.True(RecycleStore.DeleteBatch(batch).ok);   // already gone
    }
}
