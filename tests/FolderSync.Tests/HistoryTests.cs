using System.Text.Json;
using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The run history: what is written after each sync, how it is read back, and what the History page,
/// the dashboard's 30-day strip and the CSV export make of it. Everything here works on a temporary
/// folder - nothing reads or writes the real history under %LOCALAPPDATA%.
/// </summary>
public class HistoryTests : IDisposable
{
    private readonly string _folder;

    public HistoryTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "fs-history-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static RunRecord Run(DateTime started, bool success = true, RunTrigger trigger = RunTrigger.Daily,
                                 int copied = 0, long bytes = 0, bool skipped = false, bool guard = false,
                                 string note = "", string name = "Docs")
    {
        return new RunRecord
        {
            Started = started,
            Finished = started.AddSeconds(42),
            Trigger = trigger,
            Success = success,
            Pairs =
            {
                new PairRun
                {
                    Name = name, PairKey = "k", Left = @"C:\Docs", Right = @"D:\Backup",
                    Success = success || skipped, Skipped = skipped, GuardStopped = guard,
                    FilesCopied = copied, BytesCopied = bytes, Note = note,
                },
            },
        };
    }

    // ---------- the store ----------

    [Fact]
    public void A_saved_run_reads_back_with_everything_it_recorded()
    {
        var record = Run(new DateTime(2026, 11, 26, 18, 0, 5), copied: 3, bytes: 4096, trigger: RunTrigger.Logon);
        record.LogFile = "sync-20261126-180005-000.log";
        record.Pairs[0].Changes.Add(new FileChange { Kind = ChangeKind.Updated, Path = @"sub\a.txt", Size = 0, Kept = true });
        record.Pairs[0].Changes.Add(new FileChange { Kind = ChangeKind.Deleted, Path = "gone.txt", Size = 12, ToLeft = true });
        record.Pairs[0].ChangesOmitted = 7;

        string? file = RunHistoryStore.Save(record, _folder);

        Assert.NotNull(file);
        var read = Assert.Single(RunHistoryStore.Load(_folder));
        Assert.Equal(record.Id, read.Id);
        Assert.Equal(record.Started, read.Started);
        Assert.Equal(RunTrigger.Logon, read.Trigger);
        Assert.Equal("sync-20261126-180005-000.log", read.LogFile);
        Assert.Equal(TimeSpan.FromSeconds(42), read.Duration);

        var pair = Assert.Single(read.Pairs);
        Assert.Equal(3, pair.FilesCopied);
        Assert.Equal(4096, pair.BytesCopied);
        Assert.Equal(7, pair.ChangesOmitted);

        // A size of zero is a real size (an empty file), not "unknown" - it has to survive the round trip.
        Assert.Equal(0, pair.Changes[0].Size);
        Assert.True(pair.Changes[0].Kept);
        Assert.Equal(ChangeKind.Deleted, pair.Changes[1].Kind);
        Assert.True(pair.Changes[1].ToLeft);
    }

    [Fact]
    public void The_trigger_and_the_kind_of_change_are_stored_as_words()
    {
        // As numbers, adding a new trigger in the middle of the enum would silently relabel old runs.
        var record = Run(new DateTime(2026, 11, 26, 9, 0, 0), trigger: RunTrigger.FileChange);
        record.Pairs[0].Changes.Add(new FileChange { Kind = ChangeKind.Deleted, Path = "x" });

        string json = File.ReadAllText(RunHistoryStore.Save(record, _folder)!);

        Assert.Contains("\"Trigger\":\"FileChange\"", json);
        Assert.Contains("\"Kind\":\"Deleted\"", json);
    }

    [Fact]
    public void Runs_come_back_newest_first()
    {
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 24, 8, 0, 0)), _folder);
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 26, 8, 0, 0)), _folder);
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 25, 8, 0, 0)), _folder);

        var days = RunHistoryStore.Load(_folder).Select(r => r.Started.Day).ToList();

        Assert.Equal(new[] { 26, 25, 24 }, days);
    }

    [Fact]
    public void Two_runs_that_start_in_the_same_millisecond_are_both_kept()
    {
        // The window and a scheduled run are separate processes; neither may overwrite the other.
        var when = new DateTime(2026, 11, 26, 18, 0, 0, 123);
        var first = Run(when, name: "First");
        var second = Run(when, name: "Second");

        RunHistoryStore.Save(first, _folder);
        RunHistoryStore.Save(second, _folder);

        Assert.NotEqual(first.Id, second.Id);
        var names = RunHistoryStore.Load(_folder).Select(r => r.Pairs[0].Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "First", "Second" }, names);
    }

    [Fact]
    public void An_unreadable_record_is_skipped_and_the_rest_are_still_listed()
    {
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 26, 8, 0, 0)), _folder);
        File.WriteAllText(Path.Combine(_folder, "run-20261125-080000-000.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a record at all");

        var runs = RunHistoryStore.Load(_folder);

        Assert.Single(runs);
    }

    [Fact]
    public void A_missing_history_folder_is_an_empty_history()
    {
        Assert.Empty(RunHistoryStore.Load(Path.Combine(_folder, "nope")));
        Assert.Equal(0, RunHistoryStore.Prune(30, Path.Combine(_folder, "nope")));
    }

    [Fact]
    public void Loading_since_a_date_leaves_out_what_started_before_it()
    {
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 10, 8, 0, 0)), _folder);
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 26, 8, 0, 0)), _folder);

        var runs = RunHistoryStore.Load(_folder, since: new DateTime(2026, 11, 20));

        Assert.Equal(26, Assert.Single(runs).Started.Day);
    }

    [Fact]
    public void Pruning_removes_only_records_older_than_the_retention()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0);
        RunHistoryStore.Save(Run(now.AddDays(-100)), _folder);
        RunHistoryStore.Save(Run(now.AddDays(-89)), _folder);
        RunHistoryStore.Save(Run(now.AddHours(-1)), _folder);
        // Not ours: the name does not look like a record, however old the file is.
        string stranger = Path.Combine(_folder, "run-notes.json");
        File.WriteAllText(stranger, "{}");
        File.SetLastWriteTime(stranger, now.AddYears(-1));

        int removed = RunHistoryStore.Prune(90, _folder, now);

        Assert.Equal(1, removed);
        Assert.Equal(2, RunHistoryStore.Load(_folder).Count);     // the stranger is not read as a run either
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void A_record_is_never_visible_half_written()
    {
        // The window watches this folder. The file must appear under its final name only when complete,
        // so nothing but the finished record may be left behind.
        RunHistoryStore.Save(Run(new DateTime(2026, 11, 26, 8, 0, 0)), _folder);

        var files = Directory.GetFiles(_folder).Select(Path.GetFileName).ToList();

        Assert.Equal("run-20261126-080000-000.json", Assert.Single(files));
    }

    [Theory]
    [InlineData("run-20261126-180005-123.json", true)]
    [InlineData("run-20261126-180005-123-2.json", true)]     // the second run of that millisecond
    [InlineData("run-notes.json", false)]
    [InlineData("sync-20261126-180005-123.log", false)]
    public void A_records_start_time_is_read_from_its_name(string file, bool isRecord)
    {
        var started = RunHistoryStore.StartedFromName(Path.Combine(_folder, file));

        Assert.Equal(isRecord, started is not null);
        if (isRecord) Assert.Equal(new DateTime(2026, 11, 26, 18, 0, 5, 123), started);
    }

    // ---------- a record built from a finished sync ----------

    [Fact]
    public void A_record_carries_what_the_engine_reported()
    {
        var result = new SyncResult
        {
            Success = false,
            Started = new DateTime(2026, 11, 26, 18, 0, 0),
            Finished = new DateTime(2026, 11, 26, 18, 2, 5),
            Targets =
            {
                new TargetResult
                {
                    Name = "Docs", PairKey = "a|b", Left = "A", Right = "B", Success = true,
                    FilesCopied = 5, FilesRecycled = 2, FilesBackedUp = 1, BytesCopied = 2048,
                    Changes = { new FileChange { Kind = ChangeKind.New, Path = "n.txt", Size = 3 } },
                },
                new TargetResult { Name = "USB", Skipped = true, Success = true, Note = "drive not connected: Q:\\" },
                new TargetResult { Name = "Cloud", Success = false, GuardStopped = true, GuardFiles = 60, Note = "this would delete 60" },
            },
        };

        var record = RunRecord.From(result, RunTrigger.Daily, @"C:\logs\sync-1.log");

        Assert.Equal("sync-1.log", record.LogFile);                // the name only: the folder can move
        Assert.Equal(TimeSpan.FromSeconds(125), record.Duration);
        Assert.Equal("2 min 05 s", record.DurationText);
        Assert.Equal("Daily schedule", record.TriggerText);
        Assert.Equal(5, record.FilesCopied);
        Assert.Equal(2, record.FilesRecycled);
        Assert.Equal(1, record.FilesBackedUp);
        Assert.Equal(2048, record.BytesCopied);
        Assert.Equal(1, record.PairsSkipped);
        Assert.True(record.ChangedSomething);

        Assert.Equal("OK", record.Pairs[0].ResultText);
        Assert.Equal("Skipped", record.Pairs[1].ResultText);
        Assert.Equal("Stopped", record.Pairs[2].ResultText);
    }

    [Theory]
    [InlineData(true, false, false, false, "OK", PairHealth.Ok)]
    [InlineData(true, true, false, false, "OK, some skipped", PairHealth.Warn)]
    [InlineData(false, false, false, false, "Errors", PairHealth.Bad)]
    [InlineData(false, false, true, false, "Stopped for review", PairHealth.Warn)]
    [InlineData(false, false, false, true, "Cancelled", PairHealth.Warn)]
    public void A_run_is_summed_up_in_a_word_and_a_colour(bool success, bool skipped, bool guard, bool cancelled,
                                                           string text, PairHealth health)
    {
        var run = Run(DateTime.Now, success: success, skipped: skipped, guard: guard);
        if (guard) run.Pairs[0].Success = false;
        run.Cancelled = cancelled;

        Assert.Equal(text, run.ResultText);
        Assert.Equal(health, run.Health);
    }

    [Fact]
    public void A_stopped_mirror_beside_a_real_failure_is_reported_as_errors()
    {
        var run = Run(DateTime.Now, success: false, guard: true);
        run.Pairs[0].Success = false;
        run.Pairs.Add(new PairRun { Name = "Broken", Success = false, Note = "cannot create folder" });

        Assert.Equal("Errors", run.ResultText);
        Assert.Equal(PairHealth.Bad, run.Health);
    }

    [Fact]
    public void A_run_that_found_everything_in_step_did_not_change_anything()
    {
        Assert.False(Run(DateTime.Now).ChangedSomething);
        Assert.True(Run(DateTime.Now, copied: 1).ChangedSomething);
    }

    // ---------- each pair's status ----------

    [Fact]
    public void A_pairs_last_success_survives_later_runs_that_do_not_succeed()
    {
        string path = Path.Combine(_folder, "pairstatus.json");
        var good = new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = true, FilesCopied = 4 } } };
        var skipped = new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = true, Skipped = true, Note = "drive not connected" } } };
        var failed = new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = false, Note = "boom" } } };

        var monday = new DateTime(2026, 11, 23, 18, 0, 0);
        PairHistory.Record(good, path, monday);
        PairHistory.Record(skipped, path, monday.AddDays(1));
        PairHistory.Record(failed, path, monday.AddDays(2));

        var status = PairHistory.Load(path)["k"];
        Assert.False(status.Success);
        Assert.Equal(monday.AddDays(2), status.When);             // the last attempt
        Assert.Equal(monday, status.LastSuccess);                 // the last time it actually worked
        Assert.Equal(monday, status.FirstSeen);
    }

    [Fact]
    public void A_pair_that_was_only_interrupted_keeps_the_status_it_had()
    {
        string path = Path.Combine(_folder, "pairstatus.json");
        var monday = new DateTime(2026, 11, 23, 18, 0, 0);
        PairHistory.Record(new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = true } } }, path, monday);

        var cancelled = new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = false, Cancelled = true, Note = "cancelled" } } };
        PairHistory.Record(cancelled, path, monday.AddDays(1));

        var status = PairHistory.Load(path)["k"];
        Assert.True(status.Success);
        Assert.Equal(monday, status.When);
    }

    [Fact]
    public void A_status_written_by_an_earlier_version_still_knows_when_it_last_succeeded()
    {
        // 1.2.x stored only the last attempt. When that attempt succeeded, it is the last success.
        string path = Path.Combine(_folder, "pairstatus.json");
        File.WriteAllText(path, """
            { "ok":   { "When": "2026-11-20T18:00:00", "Success": true,  "Skipped": false, "FilesCopied": 3, "Note": "" },
              "away": { "When": "2026-11-20T18:00:00", "Success": true,  "Skipped": true,  "FilesCopied": 0, "Note": "drive not connected" },
              "bad":  { "When": "2026-11-20T18:00:00", "Success": false, "Skipped": false, "FilesCopied": 0, "Note": "boom" } }
            """);

        var statuses = PairHistory.Load(path);

        Assert.Equal(new DateTime(2026, 11, 20, 18, 0, 0), statuses["ok"].LastGood);
        Assert.Null(statuses["away"].LastGood);
        Assert.Null(statuses["bad"].LastGood);
    }

    [Fact]
    public void A_stopped_mirror_shows_as_stopped_in_the_pairs_list()
    {
        string path = Path.Combine(_folder, "pairstatus.json");
        var stopped = new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = false, GuardStopped = true, Note = "this would delete 60" } } };

        PairHistory.Record(stopped, path, DateTime.Now);

        var status = PairHistory.Load(path)["k"];
        Assert.True(status.GuardStopped);
        Assert.StartsWith("Stopped · ", status.Text);
        Assert.Equal(PairHealth.Warn, status.Health);
    }

    [Fact]
    public void The_computed_parts_of_a_status_are_not_written_to_the_file()
    {
        string path = Path.Combine(_folder, "pairstatus.json");
        PairHistory.Record(new SyncResult { Targets = { new TargetResult { PairKey = "k", Success = true } } }, path, DateTime.Now);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var names = doc.RootElement.GetProperty("k").EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Text", names);
        Assert.DoesNotContain("Health", names);
        Assert.DoesNotContain("LastGood", names);
    }

    // ---------- the 30-day strip ----------

    [Fact]
    public void The_strip_has_one_square_for_each_day_ending_today()
    {
        var today = new DateTime(2026, 11, 26, 15, 0, 0);

        var days = HistorySummary.Days(Array.Empty<RunRecord>(), today, 30);

        Assert.Equal(30, days.Count);
        Assert.Equal(today.Date, days[^1].Date);
        Assert.Equal(today.Date.AddDays(-29), days[0].Date);
        Assert.All(days, d => Assert.Equal(PairHealth.None, d.Health));
        Assert.Contains("no sync", days[^1].Tooltip);
    }

    [Fact]
    public void A_day_is_red_when_it_ended_on_a_failure_and_amber_when_it_recovered()
    {
        var today = new DateTime(2026, 11, 26, 20, 0, 0);
        var runs = new[]
        {
            // Two days ago: all clean.
            Run(today.AddDays(-2).Date.AddHours(9), copied: 2, bytes: 100),
            Run(today.AddDays(-2).Date.AddHours(18), copied: 3, bytes: 200),
            // Yesterday: failed in the morning, fine by the evening.
            Run(today.AddDays(-1).Date.AddHours(9), success: false),
            Run(today.AddDays(-1).Date.AddHours(18)),
            // Today: the last run failed.
            Run(today.Date.AddHours(9)),
            Run(today.Date.AddHours(18), success: false),
        };

        var days = HistorySummary.Days(runs, today, 3);

        Assert.Equal(PairHealth.Ok, days[0].Health);
        Assert.Equal(2, days[0].Runs);
        Assert.Equal(5, days[0].FilesCopied);
        Assert.Equal(300, days[0].BytesCopied);

        Assert.Equal(PairHealth.Warn, days[1].Health);
        Assert.Equal(1, days[1].RunsWithErrors);

        Assert.Equal(PairHealth.Bad, days[2].Health);
        Assert.Contains("2 syncs", days[2].Tooltip);
        Assert.Contains("1 with errors", days[2].Tooltip);
    }

    [Fact]
    public void A_skipped_drive_makes_a_day_amber_not_red()
    {
        var today = new DateTime(2026, 11, 26, 20, 0, 0);

        var days = HistorySummary.Days(new[] { Run(today.Date.AddHours(9), skipped: true) }, today, 1);

        Assert.Equal(PairHealth.Warn, days[0].Health);
        Assert.Equal(0, days[0].RunsWithErrors);
    }

    [Fact]
    public void A_cancelled_run_does_not_colour_its_day()
    {
        var today = new DateTime(2026, 11, 26, 20, 0, 0);
        var cancelled = Run(today.Date.AddHours(9), success: false);
        cancelled.Cancelled = true;

        var days = HistorySummary.Days(new[] { cancelled }, today, 1);

        Assert.Equal(PairHealth.None, days[0].Health);
    }

    [Fact]
    public void The_totals_line_reads_naturally()
    {
        var today = new DateTime(2026, 11, 26, 20, 0, 0);
        var runs = new[]
        {
            Run(today.Date.AddHours(9), copied: 1204, bytes: 3L * 1024 * 1024 * 1024),
            Run(today.Date.AddHours(10), success: false),
        };

        string text = HistorySummary.TotalsText(HistorySummary.Days(runs, today, 30));

        Assert.Equal($"2 syncs · 1 with errors · {1204:N0} files copied (3 GB)", text);
        Assert.Contains("No sync has been recorded", HistorySummary.TotalsText(HistorySummary.Days(Array.Empty<RunRecord>(), today, 30)));
    }

    // ---------- CSV export ----------

    [Fact]
    public void The_csv_has_a_header_and_one_row_per_pair_per_run_oldest_first()
    {
        var later = Run(new DateTime(2026, 11, 26, 18, 0, 0), copied: 3, bytes: 4096, trigger: RunTrigger.Manual);
        later.Pairs.Add(new PairRun { Name = "USB", Left = @"C:\Docs", Right = @"Q:\Backup", Success = true, Skipped = true, Note = "drive not connected: Q:\\Backup" });
        var earlier = Run(new DateTime(2026, 11, 25, 18, 0, 0), copied: 1);

        var lines = HistoryCsv.Build(new[] { later, earlier }).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Started,Finished,Seconds,Started by,Run result,Pair,", lines[0]);
        Assert.StartsWith("2026-11-25 18:00:00,2026-11-25 18:00:42,42,Daily schedule,OK,Docs,", lines[1]);
        Assert.Contains(",Sync now,", lines[2]);
        Assert.EndsWith(@",OK,3,0,0,0,4096,", lines[2]);
        Assert.Contains(@",USB,C:\Docs,Q:\Backup,Skipped,0,0,0,0,0,drive not connected: Q:\Backup", lines[3]);
    }

    [Fact]
    public void A_run_that_never_reached_the_pairs_still_gets_a_row()
    {
        var failed = new RunRecord
        {
            Started = new DateTime(2026, 11, 26, 18, 0, 0),
            Finished = new DateTime(2026, 11, 26, 18, 0, 0),
            Trigger = RunTrigger.Logon,
            Success = false,
            Note = "config.json could not be read",
        };

        var lines = HistoryCsv.Build(new[] { failed }).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.EndsWith(",config.json could not be read", lines[1]);
        Assert.Contains(",Sign-in or unlock,Errors,", lines[1]);
    }

    [Fact]
    public async Task The_export_of_a_real_sync_matches_what_the_sync_did()
    {
        // The whole chain the Export button uses: a real sync, recorded, stored, read back, written out.
        string left = Path.Combine(_folder, "left"), right = Path.Combine(_folder, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(left, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(left, "b.txt"), "beta!");
        var config = new SyncConfig
        {
            Pairs = { new SyncPair { Name = "Docs, \"quoted\"", Left = left, Right = right } },
            ExcludeFiles = new List<string>(),
        };
        var result = await new SyncEngine(config).RunAsync();
        string store = Path.Combine(_folder, "store");
        RunHistoryStore.Save(RunRecord.From(result, RunTrigger.FileChange, @"C:\logs\sync.log"), store);

        string csv = Path.Combine(_folder, "export.csv");
        HistoryCsv.Save(csv, RunHistoryStore.Load(store));

        // A byte-order mark first, so Excel reads non-Latin names correctly.
        byte[] bytes = File.ReadAllBytes(csv);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var lines = File.ReadAllLines(csv);
        Assert.Equal(2, lines.Length);                            // the header, and one pair of one run
        Assert.Contains(",File change,OK,\"Docs, \"\"quoted\"\"\",", lines[1]);
        Assert.Contains($",{left},{right},OK,2,0,0,0,10,", lines[1]);      // 2 files, 10 bytes
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Cells_are_quoted_only_when_they_need_to_be(string? cell, string expected)
    {
        Assert.Equal(expected, HistoryCsv.Escape(cell));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void A_cell_a_spreadsheet_would_run_as_a_formula_is_made_plain_text(string cell, string expected)
    {
        // A folder pair can be named anything. Opened in Excel, "=..." would otherwise be evaluated.
        Assert.Equal(expected, HistoryCsv.Escape(cell));
    }

    [Fact]
    public void A_pair_named_like_a_formula_cannot_reach_the_spreadsheet_as_one()
    {
        var run = Run(new DateTime(2026, 11, 26, 18, 0, 0), name: "=cmd|' /C calc'!A0");

        string csv = HistoryCsv.Build(new[] { run });

        Assert.Contains(",'=cmd|' /C calc'!A0,", csv);
        Assert.DoesNotContain(",=cmd", csv);
    }
}
