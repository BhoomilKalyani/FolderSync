using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// When the app speaks up: a notification for a sync that failed with nobody watching, a warning when a
/// pair has gone days without a successful sync, the status line on the Home page, and the weekly
/// summary email. All of it is decided by plain functions, tested here without a window or a mail server.
/// </summary>
public class AlertsTests : IDisposable
{
    private readonly string _folder;
    private static readonly DateTime Now = new(2026, 11, 26, 18, 0, 0);

    public AlertsTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "fs-alerts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static SyncPair Pair(string name = "USB backup", bool enabled = true) =>
        new() { Name = name, Left = @"C:\Docs", Right = $@"Q:\{name}", Enabled = enabled };

    private static string Key(SyncPair pair) => PairHistory.KeyFor(pair.Left, pair.Right);

    private static RunRecord Run(RunTrigger trigger, params PairRun[] pairs) => new()
    {
        Started = Now.AddMinutes(-1),
        Finished = Now,
        Trigger = trigger,
        Success = pairs.All(p => p.Success || p.Skipped),
        Pairs = pairs.ToList(),
    };

    // ---------- how a pair is described ----------

    [Fact]
    public void A_pair_that_just_synced_is_fine()
    {
        var state = PairAttention.Describe(Pair(), new PairStatus { When = Now.AddMinutes(-10), Success = true, FilesCopied = 3 }, 7, Now);

        Assert.Equal(PairHealth.Ok, state.Health);
        Assert.Equal("Synced 10 minutes ago", state.Headline);
        Assert.Equal("3 files copied.", state.Detail);
        Assert.False(state.Overdue);
    }

    [Fact]
    public void A_pair_whose_drive_is_away_says_when_it_last_really_synced()
    {
        // The last attempt was an hour ago, but the last backup was nine days ago - and that is the age
        // that matters.
        var status = new PairStatus
        {
            When = Now.AddHours(-1), Success = true, Skipped = true,
            LastSuccess = Now.AddDays(-9), Note = "drive not connected: Q:\\",
        };

        var state = PairAttention.Describe(Pair(), status, 7, Now);

        Assert.Equal(PairHealth.Warn, state.Health);
        Assert.Equal("Drive not connected", state.Headline);
        Assert.Equal("Last synced 9 days ago.", state.Detail);
        Assert.True(state.Overdue);
    }

    [Fact]
    public void A_failed_pair_gives_its_reason_as_a_sentence()
    {
        var status = new PairStatus { When = Now.AddHours(-2), Success = false, Note = "cannot create Q:\\x: access denied", LastSuccess = Now.AddDays(-1) };

        var state = PairAttention.Describe(Pair(), status, 7, Now);

        Assert.Equal(PairHealth.Bad, state.Health);
        Assert.Equal("Failed 2 hours ago", state.Headline);
        Assert.Equal("Cannot create Q:\\x: access denied. Last synced 1 day ago.", state.Detail);
        Assert.False(state.Overdue);
    }

    [Fact]
    public void A_pair_that_has_never_worked_is_overdue_from_its_first_attempt()
    {
        var status = new PairStatus { When = Now.AddHours(-1), Success = false, Note = "boom", FirstSeen = Now.AddDays(-8) };

        var state = PairAttention.Describe(Pair(), status, 7, Now);

        Assert.True(state.Overdue);
        Assert.Contains("never synced successfully", state.Detail);
    }

    [Fact]
    public void A_pair_that_succeeded_long_ago_and_has_not_run_since_is_overdue()
    {
        // Nothing failed. Nothing ran - a schedule that was removed, say.
        var state = PairAttention.Describe(Pair(), new PairStatus { When = Now.AddDays(-10), Success = true }, 7, Now);

        Assert.Equal(PairHealth.Warn, state.Health);
        Assert.Equal("Last synced 10 days ago", state.Headline);
        Assert.True(state.Overdue);
    }

    [Theory]
    [InlineData(0, false)]       // the warning is switched off
    [InlineData(7, false)]       // six days is inside the limit
    [InlineData(6, true)]
    public void How_long_is_too_long_is_the_users_choice(int overdueAfterDays, bool overdue)
    {
        var status = new PairStatus { When = Now.AddDays(-6), Success = true };

        Assert.Equal(overdue, PairAttention.Describe(Pair(), status, overdueAfterDays, Now).Overdue);
    }

    [Fact]
    public void A_stopped_mirror_is_a_warning_with_its_reason()
    {
        var status = new PairStatus { When = Now.AddMinutes(-5), Success = false, GuardStopped = true, Note = "this would delete 60 of the 60 file(s)" };

        var state = PairAttention.Describe(Pair(), status, 7, Now);

        Assert.Equal(PairHealth.Warn, state.Health);
        Assert.Equal("Stopped by the deletion guard", state.Headline);
        Assert.StartsWith("This would delete 60 of the 60 file(s).", state.Detail);
    }

    [Fact]
    public void A_pair_that_is_off_or_has_never_run_has_nothing_to_report()
    {
        Assert.Equal("Switched off", PairAttention.Describe(Pair(enabled: false), new PairStatus { When = Now, Success = false }, 7, Now).Headline);
        Assert.Equal(PairHealth.None, PairAttention.Describe(Pair(enabled: false), null, 7, Now).Health);

        var fresh = PairAttention.Describe(Pair(), null, 7, Now);
        Assert.Equal("Not synced yet", fresh.Headline);
        Assert.False(fresh.Overdue);
    }

    [Theory]
    [InlineData("boom", "Boom.")]
    [InlineData("Already a sentence.", "Already a sentence.")]
    [InlineData("  spaced  ", "Spaced.")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_note_is_turned_into_a_sentence(string? note, string expected)
    {
        Assert.Equal(expected, PairAttention.Sentence(note));
    }

    // ---------- the line at the top of the Home page ----------

    private static (PairHealth health, string headline) Overall(params (bool enabled, PairHealth health)[] pairs)
    {
        var list = pairs.Select((p, i) => Pair($"P{i}", p.enabled)).ToList();
        var states = pairs.Select(p => new PairState(p.health, "", "", false)).ToList();
        return PairAttention.Overall(list, states);
    }

    [Fact]
    public void The_headline_counts_what_is_wrong_and_the_worst_colour_wins()
    {
        Assert.Equal((PairHealth.None, "No folder pairs yet"), Overall());
        Assert.Equal((PairHealth.Ok, "All 3 folder pairs are in sync"),
                     Overall((true, PairHealth.Ok), (true, PairHealth.Ok), (true, PairHealth.Ok)));
        Assert.Equal((PairHealth.Ok, "The folder pair is in sync"), Overall((true, PairHealth.Ok)));
        Assert.Equal((PairHealth.Warn, "1 of 3 folder pairs needs attention"),
                     Overall((true, PairHealth.Ok), (true, PairHealth.Warn), (true, PairHealth.Ok)));
        Assert.Equal((PairHealth.Warn, "2 of 3 folder pairs need attention"),
                     Overall((true, PairHealth.Warn), (true, PairHealth.Warn), (true, PairHealth.Ok)));
        Assert.Equal((PairHealth.Bad, "1 of 2 folder pairs failed to sync"),
                     Overall((true, PairHealth.Bad), (true, PairHealth.Warn)));
        Assert.Equal((PairHealth.Bad, "The folder pair failed to sync"), Overall((true, PairHealth.Bad)));
    }

    [Fact]
    public void A_pair_that_is_switched_off_does_not_count_for_or_against()
    {
        Assert.Equal((PairHealth.Ok, "All 2 folder pairs are in sync"),
                     Overall((true, PairHealth.Ok), (false, PairHealth.None), (true, PairHealth.Ok)));
        Assert.Equal((PairHealth.Warn, "Every folder pair is switched off"), Overall((false, PairHealth.None)));
        Assert.Equal((PairHealth.None, "Nothing has been synced yet"), Overall((true, PairHealth.None)));
        Assert.Equal((PairHealth.Ok, "1 of 2 folder pairs in sync; the rest have not run yet"),
                     Overall((true, PairHealth.Ok), (true, PairHealth.None)));
    }

    // ---------- a run that failed with nobody watching ----------

    [Fact]
    public void A_scheduled_run_that_failed_raises_an_alert_naming_the_pair_and_the_reason()
    {
        var run = Run(RunTrigger.Daily,
                      new PairRun { Name = "Docs", Success = true },
                      new PairRun { Name = "Cloud", Success = false, Note = "cannot create G:\\My Drive\\x" });

        var alert = Alerts.ForRun(run, new SyncConfig());

        Assert.NotNull(alert);
        Assert.True(alert!.IsError);
        Assert.Equal("Folder Sync finished with errors", alert.Title);
        Assert.StartsWith("Cloud: Cannot create G:\\My Drive\\x.", alert.Text);
    }

    [Fact]
    public void A_stopped_mirror_raises_a_calmer_alert_that_says_nothing_was_deleted()
    {
        var run = Run(RunTrigger.Logon, new PairRun { Name = "Cloud", Success = false, GuardStopped = true, Note = "this would delete 60 of the 60 file(s)" });

        var alert = Alerts.ForRun(run, new SyncConfig());

        Assert.False(alert!.IsError);
        Assert.Equal("Folder Sync stopped a mirror", alert.Title);
        Assert.Contains("Nothing was deleted", alert.Text);
        Assert.Contains("review", alert.Text);
    }

    [Fact]
    public void Several_failures_are_one_alert()
    {
        var run = Run(RunTrigger.Daily,
                      new PairRun { Name = "A", Success = false, Note = "x" },
                      new PairRun { Name = "B", Success = false, Note = "y" },
                      new PairRun { Name = "C", Success = false, Note = "z" });

        Assert.Contains("A: X. (and 2 more)", Alerts.ForRun(run, new SyncConfig())!.Text);
    }

    [Fact]
    public void No_alert_for_a_run_someone_started_by_hand_or_cancelled()
    {
        var failed = new PairRun { Name = "A", Success = false, Note = "x" };

        Assert.Null(Alerts.ForRun(Run(RunTrigger.Manual, failed), new SyncConfig()));

        var cancelled = Run(RunTrigger.Daily, failed);
        cancelled.Cancelled = true;
        Assert.Null(Alerts.ForRun(cancelled, new SyncConfig()));
    }

    [Fact]
    public void No_alert_for_a_run_that_worked_or_only_skipped_an_unplugged_drive()
    {
        Assert.Null(Alerts.ForRun(Run(RunTrigger.Daily, new PairRun { Name = "A", Success = true }), new SyncConfig()));
        Assert.Null(Alerts.ForRun(Run(RunTrigger.Daily, new PairRun { Name = "USB", Success = true, Skipped = true }), new SyncConfig()));
    }

    [Fact]
    public void No_alert_when_notifications_are_switched_off()
    {
        var run = Run(RunTrigger.Daily, new PairRun { Name = "A", Success = false, Note = "x" });

        Assert.Null(Alerts.ForRun(run, new SyncConfig { NotifyOnFailure = false }));
    }

    [Fact]
    public void A_run_that_could_not_even_start_raises_an_alert_with_its_reason()
    {
        var run = new RunRecord { Trigger = RunTrigger.Daily, Success = false, Note = "config.json could not be read" };

        var alert = Alerts.ForRun(run, new SyncConfig());

        Assert.Equal("Folder Sync could not sync", alert!.Title);
        Assert.Equal("config.json could not be read", alert.Text);
    }

    [Fact]
    public void A_very_long_reason_is_cut_to_what_a_notification_can_show()
    {
        var run = Run(RunTrigger.Daily, new PairRun { Name = "A", Success = false, Note = new string('x', 600) });

        var alert = Alerts.ForRun(run, new SyncConfig());

        Assert.True(alert!.Text.Length <= 240);
        Assert.EndsWith("…", alert.Text);
    }

    [Fact]
    public void The_same_failure_is_not_announced_again_until_hours_have_passed()
    {
        // A pair that fails at every unlock would otherwise raise the same alert twenty times a day.
        var alert = new Alert("Folder Sync finished with errors", "Cloud: X.", true);
        var memory = new AlertMemory();

        Assert.True(Alerts.ShouldShow(alert, memory, Now));
        Assert.False(Alerts.ShouldShow(alert, memory, Now.AddMinutes(15)));
        Assert.False(Alerts.ShouldShow(alert, memory, Now.AddHours(5)));
        Assert.True(Alerts.ShouldShow(alert, memory, Now.AddHours(7)));

        // A different failure is news straight away.
        Assert.True(Alerts.ShouldShow(new Alert(alert.Title, "Other: Y.", true), memory, Now.AddHours(7).AddMinutes(1)));
    }

    // ---------- a pair that has gone too long ----------

    [Fact]
    public void An_overdue_pair_is_announced_once_a_day()
    {
        var pair = Pair();
        var statuses = new Dictionary<string, PairStatus>
        {
            [Key(pair)] = new() { When = Now.AddHours(-1), Success = true, Skipped = true, LastSuccess = Now.AddDays(-9) },
        };
        var memory = new AlertMemory();
        var config = new SyncConfig();

        var first = Alerts.Overdue(new[] { pair }, statuses, config, Now, memory);
        Assert.NotNull(first);
        Assert.Equal("Folder Sync: a backup is overdue", first!.Title);
        Assert.Equal("USB backup has not synced successfully for 9 days (drive not connected).", first.Text);

        Assert.Null(Alerts.Overdue(new[] { pair }, statuses, config, Now.AddHours(3), memory));
        Assert.NotNull(Alerts.Overdue(new[] { pair }, statuses, config, Now.AddHours(25), memory));
    }

    [Fact]
    public void Several_overdue_pairs_are_one_alert()
    {
        var a = Pair("A");
        var b = Pair("B");
        var c = Pair("C");
        var old = new PairStatus { When = Now.AddDays(-10), Success = true };
        var statuses = new Dictionary<string, PairStatus> { [Key(a)] = old, [Key(b)] = old, [Key(c)] = old };

        var alert = Alerts.Overdue(new[] { a, b, c }, statuses, new SyncConfig(), Now, new AlertMemory());

        Assert.StartsWith("A has not synced successfully for 10 days.", alert!.Text);
        Assert.EndsWith("2 more pairs are overdue too.", alert.Text);
    }

    [Fact]
    public void Nothing_is_overdue_when_the_warning_is_off_or_the_pair_is_off_or_has_never_run()
    {
        var pair = Pair();
        var old = new Dictionary<string, PairStatus> { [Key(pair)] = new() { When = Now.AddDays(-30), Success = true } };

        Assert.Null(Alerts.Overdue(new[] { pair }, old, new SyncConfig { OverdueAfterDays = 0 }, Now, new AlertMemory()));

        var off = Pair(enabled: false);
        Assert.Null(Alerts.Overdue(new[] { off }, old, new SyncConfig(), Now, new AlertMemory()));

        Assert.Null(Alerts.Overdue(new[] { pair }, new Dictionary<string, PairStatus>(), new SyncConfig(), Now, new AlertMemory()));
    }

    [Fact]
    public void What_has_been_said_is_remembered_across_runs()
    {
        // Each scheduled run is a new process, so the memory has to be on disk.
        string path = Path.Combine(_folder, "alerts.json");
        var memory = new AlertMemory { LastFailure = "t|x", LastFailureShown = Now };
        memory.OverdueShown["k"] = Now;

        memory.Save(path);
        var read = AlertMemory.Load(path);

        Assert.Equal("t|x", read.LastFailure);
        Assert.Equal(Now, read.LastFailureShown);
        Assert.Equal(Now, read.OverdueShown["k"]);

        // A missing or damaged file is simply "nothing said yet".
        Assert.Empty(AlertMemory.Load(Path.Combine(_folder, "nope.json")).OverdueShown);
        File.WriteAllText(path, "{ not json");
        Assert.Equal("", AlertMemory.Load(path).LastFailure);
    }

    // ---------- the weekly summary ----------

    [Fact]
    public void A_summary_is_due_a_week_after_the_last_one()
    {
        var sent = new WeeklyDigest.State(Now, Now);

        Assert.False(WeeklyDigest.IsDue(sent, Now.AddDays(6).AddHours(23)));
        Assert.True(WeeklyDigest.IsDue(sent, Now.AddDays(7)));
        Assert.False(WeeklyDigest.IsDue(default, Now));           // never started: nothing to summarise yet
    }

    [Fact]
    public void After_a_failed_attempt_sending_is_not_retried_at_once()
    {
        var failed = new WeeklyDigest.State(Sent: Now.AddDays(-8), Tried: Now);

        Assert.False(WeeklyDigest.IsDue(failed, Now.AddHours(1)));
        Assert.True(WeeklyDigest.IsDue(failed, Now.AddHours(7)));
    }

    [Fact]
    public void The_summary_reports_the_week_and_each_pairs_state()
    {
        var docs = Pair("Docs");
        var usb = Pair("USB backup");
        var off = Pair("Old", enabled: false);
        var statuses = new Dictionary<string, PairStatus>
        {
            [Key(docs)] = new() { When = Now.AddHours(-1), Success = true, LastSuccess = Now.AddHours(-1) },
            [Key(usb)] = new() { When = Now.AddHours(-1), Success = true, Skipped = true, LastSuccess = Now.AddDays(-9) },
        };
        var runs = new[]
        {
            new RunRecord
            {
                Started = Now.AddDays(-2), Finished = Now.AddDays(-2).AddSeconds(30), Trigger = RunTrigger.Daily, Success = true,
                Pairs = { new PairRun { Name = "Docs", Success = true, FilesCopied = 200, BytesCopied = 1024 * 1024, FilesRecycled = 4, FilesBackedUp = 3 } },
            },
            new RunRecord
            {
                Started = Now.AddDays(-1), Finished = Now.AddDays(-1).AddSeconds(5), Trigger = RunTrigger.Logon, Success = false,
                Pairs = { new PairRun { Name = "Docs", Success = false, Note = "disk full" } },
            },
            new RunRecord
            {
                Started = Now.AddHours(-1), Finished = Now.AddHours(-1).AddSeconds(5), Trigger = RunTrigger.Logon, Success = true,
                Pairs = { new PairRun { Name = "Docs", Success = true, FilesCopied = 12 } },
            },
        };

        var (subject, body) = WeeklyDigest.Build(runs, new[] { docs, usb, off }, statuses, Now.AddDays(-7), Now, "MYPC", 7);

        Assert.Equal("[Folder Sync] Weekly summary — 3 syncs, 1 with errors", subject);
        Assert.Contains("Folder Sync weekly summary for MYPC", body);
        Assert.Contains("2026-11-19 18:00 to 2026-11-26 18:00", body);
        Assert.Contains("Syncs run         : 3  (2 sign-in or unlock, 1 daily schedule)", body);
        Assert.Contains("With errors       : 1", body);
        Assert.Contains("Files copied      : 212  (1 MB)", body);
        Assert.Contains("Deletions kept    : 4", body);
        Assert.Contains("Previous versions : 3", body);

        Assert.Contains("[OK  ] Docs — Synced 1 hour ago", body);
        Assert.Contains("[WARN] USB backup — Drive not connected", body);
        Assert.Contains("(last synced 2026-11-17 18:00)", body);
        Assert.Contains("No successful sync for 7 days or more.", body);
        Assert.Contains("[OFF ] Old — Switched off", body);

        Assert.Contains("Runs with errors:", body);
        Assert.Contains("2026-11-25 18:00  Sign-in or unlock — Docs: disk full", body);
    }

    [Fact]
    public void A_quiet_week_says_so_plainly()
    {
        var (subject, body) = WeeklyDigest.Build(Array.Empty<RunRecord>(), Array.Empty<SyncPair>(),
                                                 new Dictionary<string, PairStatus>(), Now.AddDays(-7), Now, "MYPC", 7);

        Assert.Equal("[Folder Sync] Weekly summary — 0 syncs, all OK", subject);
        Assert.Contains("Syncs run         : 0", body);
        Assert.Contains("(none set up)", body);
        Assert.DoesNotContain("Runs with errors", body);
    }

    [Fact]
    public void A_week_with_no_errors_but_a_stale_pair_says_there_is_something_to_check()
    {
        var usb = Pair();
        var statuses = new Dictionary<string, PairStatus>
        {
            [Key(usb)] = new() { When = Now.AddHours(-1), Success = true, Skipped = true, LastSuccess = Now.AddDays(-9) },
        };

        var (subject, _) = WeeklyDigest.Build(Array.Empty<RunRecord>(), new[] { usb }, statuses, Now.AddDays(-7), Now, "MYPC", 7);

        Assert.EndsWith("1 pair to check", subject);
    }

    private SyncConfig WeeklyConfig() => new()
    {
        Email = new EmailSettings { Enabled = true, Mode = EmailMode.Weekly, To = "me@example.invalid", From = "me@example.invalid" },
    };

    [Fact]
    public void Switching_weekly_mode_on_starts_the_week_rather_than_sending_at_once()
    {
        string state = Path.Combine(_folder, "lastdigest.txt");
        int sent = 0;

        string? line = WeeklyDigest.SendIfDue(WeeklyConfig(), Now, (_, _, _) => sent++, state, _folder, Path.Combine(_folder, "ps.json"));

        Assert.Null(line);
        Assert.Equal(0, sent);
        Assert.Equal(Now, WeeklyDigest.ReadState(state).Sent);
    }

    [Fact]
    public void A_week_later_the_summary_is_sent_once_and_covers_the_runs_since()
    {
        string state = Path.Combine(_folder, "lastdigest.txt");
        string history = Path.Combine(_folder, "history");
        WeeklyDigest.WriteState(new WeeklyDigest.State(Now.AddDays(-7), null), state);
        RunHistoryStore.Save(new RunRecord { Started = Now.AddDays(-20), Finished = Now.AddDays(-20), Success = true }, history);   // before the week
        RunHistoryStore.Save(new RunRecord { Started = Now.AddDays(-3), Finished = Now.AddDays(-3), Success = true }, history);
        RunHistoryStore.Save(new RunRecord { Started = Now.AddDays(-1), Finished = Now.AddDays(-1), Success = true }, history);

        var sent = new List<(string subject, string body)>();
        string? line = WeeklyDigest.SendIfDue(WeeklyConfig(), Now, (_, subject, body) => sent.Add((subject, body)),
                                              state, history, Path.Combine(_folder, "ps.json"));

        Assert.Contains("weekly summary sent to me@example.invalid (2 run(s))", line);
        Assert.Contains("2 syncs", Assert.Single(sent).subject);
        Assert.Equal(Now, WeeklyDigest.ReadState(state).Sent);

        // The next run, a minute later, sends nothing.
        Assert.Null(WeeklyDigest.SendIfDue(WeeklyConfig(), Now.AddMinutes(1), (_, s, b) => sent.Add((s, b)),
                                           state, history, Path.Combine(_folder, "ps.json")));
        Assert.Single(sent);
    }

    [Fact]
    public void A_summary_that_could_not_be_sent_is_tried_again_later_not_lost()
    {
        string state = Path.Combine(_folder, "lastdigest.txt");
        WeeklyDigest.WriteState(new WeeklyDigest.State(Now.AddDays(-7), null), state);
        int attempts = 0;
        Action<EmailSettings, string, string> failing = (_, _, _) => { attempts++; throw new IOException("no network"); };

        string? line = WeeklyDigest.SendIfDue(WeeklyConfig(), Now, failing, state, _folder, Path.Combine(_folder, "ps.json"));

        Assert.Contains("FAILED to send: no network", line);
        Assert.Equal(Now.AddDays(-7), WeeklyDigest.ReadState(state).Sent);      // still owed

        // Not hammered on every unlock...
        Assert.Null(WeeklyDigest.SendIfDue(WeeklyConfig(), Now.AddMinutes(20), failing, state, _folder, Path.Combine(_folder, "ps.json")));
        Assert.Equal(1, attempts);

        // ...but tried again once the retry time has passed.
        WeeklyDigest.SendIfDue(WeeklyConfig(), Now.AddHours(7), failing, state, _folder, Path.Combine(_folder, "ps.json"));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(EmailMode.Always)]
    [InlineData(EmailMode.OnError)]
    [InlineData(EmailMode.OnSuccess)]
    public void No_summary_is_sent_in_the_per_run_modes(EmailMode mode)
    {
        string state = Path.Combine(_folder, "lastdigest.txt");
        WeeklyDigest.WriteState(new WeeklyDigest.State(Now.AddDays(-30), null), state);
        var config = WeeklyConfig();
        config.Email.Mode = mode;
        int sent = 0;

        Assert.Null(WeeklyDigest.SendIfDue(config, Now, (_, _, _) => sent++, state, _folder, Path.Combine(_folder, "ps.json")));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void No_summary_is_sent_when_email_is_off()
    {
        string state = Path.Combine(_folder, "lastdigest.txt");
        WeeklyDigest.WriteState(new WeeklyDigest.State(Now.AddDays(-30), null), state);
        var config = WeeklyConfig();
        config.Email.Enabled = false;
        int sent = 0;

        Assert.Null(WeeklyDigest.SendIfDue(config, Now, (_, _, _) => sent++, state, _folder, Path.Combine(_folder, "ps.json")));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void Weekly_mode_sends_nothing_after_an_individual_run()
    {
        var weekly = new EmailSettings { Mode = EmailMode.Weekly };

        Assert.False(weekly.ShouldSend(success: true));
        Assert.False(weekly.ShouldSend(success: false));
    }
}
