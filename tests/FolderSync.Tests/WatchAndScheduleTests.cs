using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The pieces 1.3.0 added around the engine: deciding when a burst of file changes has settled, reading
/// a scheduled task without depending on the language of Windows, the new settings, and the size column
/// of robocopy's list output.
/// </summary>
public class WatchAndScheduleTests : IDisposable
{
    private readonly string _root;
    private static readonly DateTime T0 = new(2026, 11, 26, 9, 0, 0);

    public WatchAndScheduleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-watch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---------- when changes have settled ----------

    private static ChangeDebouncer Debouncer() => new() { Quiet = TimeSpan.FromSeconds(60), MaxWait = TimeSpan.FromMinutes(10) };

    [Fact]
    public void Nothing_is_due_until_the_folder_has_been_quiet_for_the_delay()
    {
        var d = Debouncer();
        d.Touch(new[] { "a" }, T0);

        Assert.Empty(d.TakeDue(T0.AddSeconds(59)));
        Assert.Equal(new[] { "a" }, d.TakeDue(T0.AddSeconds(60)));
    }

    [Fact]
    public void Each_new_change_restarts_the_wait()
    {
        // Saving a document is several events over a few seconds; the sync must wait for the last one.
        var d = Debouncer();
        d.Touch(new[] { "a" }, T0);
        d.Touch(new[] { "a" }, T0.AddSeconds(50));

        Assert.Empty(d.TakeDue(T0.AddSeconds(100)));              // only 50 s since the last change
        Assert.Single(d.TakeDue(T0.AddSeconds(110)));
    }

    [Fact]
    public void A_folder_that_never_goes_quiet_still_syncs_after_the_longest_wait()
    {
        var d = Debouncer();
        for (int s = 0; s <= 600; s += 10) d.Touch(new[] { "a" }, T0.AddSeconds(s));      // a change every 10 s

        Assert.Single(d.TakeDue(T0.AddSeconds(600)));             // ten minutes after the first one
    }

    [Fact]
    public void Due_pairs_are_handed_over_once_and_every_pair_that_changed_is_included()
    {
        var d = Debouncer();
        d.Touch(new[] { "a", "b" }, T0);
        d.Touch(new[] { "B", "c" }, T0.AddSeconds(1));            // the same pair, differently cased

        var due = d.TakeDue(T0.AddSeconds(61)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(3, due.Count);
        Assert.False(d.HasPending);
        Assert.Empty(d.TakeDue(T0.AddSeconds(200)));              // not handed over twice
    }

    [Fact]
    public void A_change_after_a_sync_was_handed_over_starts_a_new_wait()
    {
        var d = Debouncer();
        d.Touch(new[] { "a" }, T0);
        d.TakeDue(T0.AddSeconds(60));

        d.Touch(new[] { "a" }, T0.AddSeconds(61));

        Assert.Empty(d.TakeDue(T0.AddSeconds(100)));
        Assert.Single(d.TakeDue(T0.AddSeconds(121)));
    }

    // ---------- the watcher itself, on a real folder ----------

    private SyncConfig WatchConfig(SyncDirection direction, out string left, out string right)
    {
        left = Path.Combine(_root, "left");
        right = Path.Combine(_root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        return new SyncConfig
        {
            SyncOnChange = true,
            SyncOnChangeDelaySeconds = 5,
            RecycleFolderName = "_Trash",
            ExcludeFiles = new List<string> { "~$*", "*.tmp" },
            Pairs = { new SyncPair { Name = "P", Left = left, Right = right, Direction = direction } },
        };
    }

    /// <summary>A clock the test moves by hand, so "five quiet seconds" does not take five seconds.</summary>
    private sealed class FakeClock
    {
        private long _ticks = T0.Ticks;
        public DateTime Now => new(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static async Task<bool> Eventually(Func<bool> condition, int milliseconds = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task A_file_dropped_in_the_sending_folder_makes_its_pair_due_once_things_settle()
    {
        var config = WatchConfig(SyncDirection.ToRight, out string left, out string right);
        var clock = new FakeClock();
        var due = new List<string>();
        using var watcher = new ChangeWatcher(() => clock.Now);
        watcher.Due += keys => { lock (due) due.AddRange(keys); };
        watcher.Configure(config);

        Assert.Equal((1, 1), watcher.Counts);
        Assert.Equal("Watching 1 folder for changes.", watcher.StatusText);

        File.WriteAllText(Path.Combine(left, "new.txt"), "hello");
        Assert.True(await Eventually(() => watcher.HasPendingChanges), "the change was never noticed");
        await Task.Delay(1300);                                   // at least one tick of the watcher's timer
        lock (due) Assert.Empty(due);                             // not yet: the folder has not been quiet

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(await Eventually(() => { lock (due) return due.Count > 0; }), "the pair never became due");
        lock (due) Assert.Equal(PairHistory.KeyFor(left, right), Assert.Single(due));
    }

    [Fact]
    public async Task Changes_on_the_receiving_side_of_a_one_way_pair_do_not_start_a_sync()
    {
        // Otherwise the sync's own copying would set off the next sync, for ever.
        var config = WatchConfig(SyncDirection.ToRight, out _, out string right);
        var clock = new FakeClock();
        int raised = 0;
        using var watcher = new ChangeWatcher(() => clock.Now);
        watcher.Due += _ => Interlocked.Increment(ref raised);
        watcher.Configure(config);

        File.WriteAllText(Path.Combine(right, "copied-by-the-sync.txt"), "x");
        await Task.Delay(800);
        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(1800);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Skipped_names_and_the_recycle_folder_do_not_start_a_sync()
    {
        var config = WatchConfig(SyncDirection.ToRight, out string left, out _);
        var clock = new FakeClock();
        int raised = 0;
        using var watcher = new ChangeWatcher(() => clock.Now);
        watcher.Due += _ => Interlocked.Increment(ref raised);
        watcher.Configure(config);

        File.WriteAllText(Path.Combine(left, "~$report.docx"), "an Office lock file");
        File.WriteAllText(Path.Combine(left, "download.tmp"), "half a download");
        Directory.CreateDirectory(Path.Combine(left, "_Trash", "20261126-090000"));
        File.WriteAllText(Path.Combine(left, "_Trash", "20261126-090000", "old.txt"), "housekeeping");
        await Task.Delay(800);
        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(1800);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_two_way_pairs_own_writes_are_ignored_while_it_syncs_but_real_changes_count_afterwards()
    {
        var config = WatchConfig(SyncDirection.TwoWay, out string left, out string right);
        var clock = new FakeClock();
        int raised = 0;
        using var watcher = new ChangeWatcher(() => clock.Now);
        watcher.Due += _ => Interlocked.Increment(ref raised);
        watcher.Configure(config);
        Assert.Equal((2, 2), watcher.Counts);                     // a two-way pair reads from both sides

        watcher.SyncStarted();
        File.WriteAllText(Path.Combine(right, "written-by-the-sync.txt"), "x");
        await Task.Delay(800);
        watcher.SyncFinished();
        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(1800);
        Assert.Equal(0, raised);

        File.WriteAllText(Path.Combine(left, "written-by-a-person.txt"), "y");
        Assert.True(await Eventually(() => watcher.HasPendingChanges), "a real change after the sync was not noticed");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await Eventually(() => Volatile.Read(ref raised) > 0), "a real change after the sync was missed");
    }

    [Fact]
    public async Task A_sync_that_could_not_start_is_tried_again()
    {
        var config = WatchConfig(SyncDirection.ToRight, out string left, out string right);
        var clock = new FakeClock();
        int raised = 0;
        using var watcher = new ChangeWatcher(() => clock.Now);
        watcher.Due += _ => Interlocked.Increment(ref raised);
        watcher.Configure(config);

        watcher.Requeue(new[] { PairHistory.KeyFor(left, right) });   // "busy just now - ask me again"
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(await Eventually(() => Volatile.Read(ref raised) == 1));
    }

    [Fact]
    public void Watching_is_off_unless_switched_on_and_a_missing_folder_is_reported_not_fatal()
    {
        var config = WatchConfig(SyncDirection.ToRight, out _, out _);
        config.SyncOnChange = false;
        using var watcher = new ChangeWatcher();

        watcher.Configure(config);
        Assert.False(watcher.Enabled);
        Assert.Equal("Off", watcher.StatusText);
        Assert.Equal((0, 0), watcher.Counts);

        // Switched on, but the sending folder is on a drive that is not there.
        config.SyncOnChange = true;
        config.Pairs[0].Left = Path.Combine(_root, "not-connected");
        watcher.Configure(config);

        Assert.Equal((1, 0), watcher.Counts);
        Assert.StartsWith("Watching 0 of 1 folder;", watcher.StatusText);
    }

    [Fact]
    public void Pairs_that_share_a_sending_folder_share_one_watch()
    {
        // Three mirrors of the same Documents folder need one watcher, and all three become due together.
        string source = Path.Combine(_root, "docs");
        Directory.CreateDirectory(source);
        var config = new SyncConfig { SyncOnChange = true };
        foreach (var name in new[] { "a", "b", "c" })
            config.Pairs.Add(new SyncPair { Left = source, Right = Path.Combine(_root, name) });
        config.Pairs.Add(new SyncPair { Left = source, Right = Path.Combine(_root, "off"), Enabled = false });

        using var watcher = new ChangeWatcher();
        watcher.Configure(config);

        Assert.Equal((1, 1), watcher.Counts);
    }

    [Theory]
    [InlineData("~$*", "~$report.docx", true)]
    [InlineData("~$*", "report.docx", false)]
    [InlineData("*.tmp", "DOWNLOAD.TMP", true)]              // Windows names are not case-sensitive
    [InlineData("*.tmp", "tmp.txt", false)]
    [InlineData("Thumbs.db", "Thumbs.db", true)]
    [InlineData("Thumbs.db", "XThumbs.db", false)]           // the whole name, not part of it
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file10.txt", false)]
    [InlineData("a.b", "aXb", false)]                        // a dot is a dot
    public void A_skip_pattern_matches_whole_names_the_way_robocopy_does(string pattern, string name, bool matches)
    {
        Assert.Equal(matches, ChangeWatcher.WildcardToRegex(pattern).IsMatch(name));
    }

    // ---------- reading a scheduled task ----------

    // schtasks /Query /XML as printed on this PC for a task created by 1.2.x. Windows leaves out every
    // setting that has its default value - so an enabled task has no <Enabled> under <Settings> at all.
    private const string DailyXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Folder Sync - daily sync</Description>
            <URI>\FolderSync - Daily</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1-2-3-1001</UserId>
              <LogonType>InteractiveToken</LogonType>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
            </IdleSettings>
          </Settings>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-07-30T18:30:00</StartBoundary>
              <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
              </ScheduleByDay>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>C:\Users\Me\AppData\Local\Programs\FolderSync\FolderSync.exe</Command>
              <Arguments>--sync</Arguments>
              <WorkingDirectory>C:\Users\Me\AppData\Local\Programs\FolderSync</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void An_enabled_task_is_read_with_its_program_arguments_and_time()
    {
        var info = ScheduleManager.ParseTaskXml(DailyXml);

        Assert.Equal(ScheduleManager.TaskState.Enabled, info.State);
        Assert.Equal(@"C:\Users\Me\AppData\Local\Programs\FolderSync\FolderSync.exe", info.Exe);
        Assert.Equal("--sync", info.Arguments);
        Assert.Equal(new TimeOnly(18, 30), info.Time);
        Assert.Null(info.Day);
        Assert.True(info.LacksTrigger);                           // registered before the switch existed
    }

    [Fact]
    public void A_disabled_task_is_recognised_by_the_setting_not_by_a_triggers_own_enabled_flag()
    {
        string disabled = DailyXml.Replace("<Settings>", "<Settings>\n    <Enabled>false</Enabled>");
        // The opposite trap: the task is enabled, but one of its triggers carries <Enabled>false</Enabled>.
        string triggerOff = DailyXml.Replace("<CalendarTrigger>", "<CalendarTrigger>\n      <Enabled>false</Enabled>");

        Assert.Equal(ScheduleManager.TaskState.Disabled, ScheduleManager.ParseTaskXml(disabled).State);
        Assert.Equal(ScheduleManager.TaskState.Enabled, ScheduleManager.ParseTaskXml(triggerOff).State);
    }

    [Fact]
    public void A_monthly_task_gives_its_day_and_a_new_style_task_names_its_trigger()
    {
        string monthly = DailyXml
            .Replace("<ScheduleByDay>\n        <DaysInterval>1</DaysInterval>\n      </ScheduleByDay>",
                     "<ScheduleByMonth><DaysOfMonth><Day>15</Day></DaysOfMonth><Months><January /></Months></ScheduleByMonth>")
            .Replace("<Arguments>--sync</Arguments>", "<Arguments>--sync --trigger monthly</Arguments>");

        var info = ScheduleManager.ParseTaskXml(monthly);

        Assert.Equal(15, info.Day);
        Assert.Equal("--sync --trigger monthly", info.Arguments);
        Assert.False(info.LacksTrigger);
    }

    [Fact]
    public void A_program_path_with_an_ampersand_or_quotes_is_read_as_the_path()
    {
        // The path is XML-escaped in the task, and some tools quote it.
        string odd = DailyXml.Replace(@"<Command>C:\Users\Me\AppData\Local\Programs\FolderSync\FolderSync.exe</Command>",
                                      "<Command>\"C:\\Users\\Tom &amp; Jo\\FolderSync.exe\"</Command>");

        Assert.Equal(@"C:\Users\Tom & Jo\FolderSync.exe", ScheduleManager.ParseTaskXml(odd).Exe);
    }

    [Fact]
    public void Text_that_is_not_task_xml_is_an_installed_task_with_nothing_known_about_it()
    {
        // The query succeeded, so the task exists; claiming its program is missing would be a false alarm.
        var info = ScheduleManager.ParseTaskXml("ERROR: something unexpected");

        Assert.Equal(ScheduleManager.TaskState.Enabled, info.State);
        Assert.Null(info.Exe);
        Assert.Null(info.Time);
    }

    [Theory]
    [InlineData(new[] { "--sync", "--trigger", "daily" }, RunTrigger.Daily)]
    [InlineData(new[] { "--sync", "--trigger", "MONTHLY" }, RunTrigger.Monthly)]
    [InlineData(new[] { "--sync", "--auto", "--trigger", "logon" }, RunTrigger.Logon)]
    [InlineData(new[] { "--sync", "--auto" }, RunTrigger.Logon)]            // a task from before 1.3.0
    [InlineData(new[] { "--sync" }, RunTrigger.Scheduled)]                  // likewise: daily or monthly, not known
    [InlineData(new[] { "--sync", "--trigger", "weekly" }, RunTrigger.Scheduled)]
    [InlineData(new[] { "--sync", "--trigger" }, RunTrigger.Scheduled)]     // the switch with nothing after it
    public void A_scheduled_run_knows_what_started_it_from_its_command_line(string[] args, RunTrigger expected)
    {
        Assert.Equal(expected, ScheduleManager.TriggerFromArguments(args));
    }

    [Fact]
    public void Each_task_is_registered_with_the_arguments_that_name_it()
    {
        Assert.Equal(RunTrigger.Daily, ScheduleManager.TriggerFromArguments(ScheduleManager.ArgumentsFor(ScheduleManager.DailyTask).Split(' ')));
        Assert.Equal(RunTrigger.Monthly, ScheduleManager.TriggerFromArguments(ScheduleManager.ArgumentsFor(ScheduleManager.MonthlyTask).Split(' ')));
        Assert.Equal(RunTrigger.Logon, ScheduleManager.TriggerFromArguments(ScheduleManager.ArgumentsFor(ScheduleManager.LogonTask).Split(' ')));

        // Only the sign-in/unlock task is throttled, and every one of them still syncs.
        Assert.Contains("--auto", ScheduleManager.ArgumentsFor(ScheduleManager.LogonTask));
        Assert.DoesNotContain("--auto", ScheduleManager.ArgumentsFor(ScheduleManager.DailyTask));
        Assert.All(ScheduleManager.AllTasks(), t => Assert.StartsWith("--sync", ScheduleManager.ArgumentsFor(t.task)));
    }

    private static ScheduleManager.TaskInfo Scheduled(ScheduleManager.TaskState state, int hour, int minute, int? day = null) =>
        new(state, "x.exe", "--sync", new TimeOnly(hour, minute), day);

    [Fact]
    public void The_next_run_is_today_if_the_time_is_still_to_come_and_tomorrow_if_it_has_passed()
    {
        var daily = Scheduled(ScheduleManager.TaskState.Enabled, 18, 0);
        var none = ScheduleManager.TaskInfo.Missing;

        Assert.Equal(new DateTime(2026, 11, 26, 18, 0, 0), ScheduleManager.NextRun(daily, none, new DateTime(2026, 11, 26, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 27, 18, 0, 0), ScheduleManager.NextRun(daily, none, new DateTime(2026, 11, 26, 18, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 27, 18, 0, 0), ScheduleManager.NextRun(daily, none, new DateTime(2026, 11, 26, 23, 59, 0)));
    }

    [Fact]
    public void A_monthly_run_rolls_into_next_month_and_the_sooner_of_the_two_schedules_wins()
    {
        var monthly = Scheduled(ScheduleManager.TaskState.Enabled, 7, 30, day: 15);
        var daily = Scheduled(ScheduleManager.TaskState.Enabled, 18, 0);
        var none = ScheduleManager.TaskInfo.Missing;

        Assert.Equal(new DateTime(2026, 12, 15, 7, 30, 0), ScheduleManager.NextRun(none, monthly, new DateTime(2026, 11, 26, 9, 0, 0)));
        Assert.Equal(new DateTime(2027, 1, 15, 7, 30, 0), ScheduleManager.NextRun(none, monthly, new DateTime(2026, 12, 20, 9, 0, 0)));

        Assert.Equal(new DateTime(2026, 11, 26, 18, 0, 0), ScheduleManager.NextRun(daily, monthly, new DateTime(2026, 11, 26, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 12, 15, 7, 30, 0), ScheduleManager.NextRun(Scheduled(ScheduleManager.TaskState.Enabled, 18, 0), monthly, new DateTime(2026, 12, 14, 19, 0, 0)));
    }

    [Fact]
    public void A_schedule_that_is_paused_or_not_installed_has_no_next_run()
    {
        var paused = Scheduled(ScheduleManager.TaskState.Disabled, 18, 0);

        Assert.Null(ScheduleManager.NextRun(paused, ScheduleManager.TaskInfo.Missing, DateTime.Now));
        Assert.Null(ScheduleManager.NextRun(ScheduleManager.TaskInfo.Missing, Scheduled(ScheduleManager.TaskState.Disabled, 7, 0, 1), DateTime.Now));
    }

    // ---------- starting with Windows ----------

    [Fact]
    public void The_start_at_sign_in_command_quotes_the_path_and_starts_in_the_tray()
    {
        Assert.Equal("\"C:\\Program Files\\Folder Sync\\FolderSync.exe\" --tray",
                     StartupRegistration.CommandFor(@"C:\Program Files\Folder Sync\FolderSync.exe"));
    }

    // ---------- the new settings ----------

    [Fact]
    public void The_new_settings_have_sensible_defaults_and_cost_an_existing_user_nothing()
    {
        var config = new SyncConfig();

        Assert.False(config.SyncOnChange);                        // watching is opt-in
        Assert.Equal(60, config.SyncOnChangeDelaySeconds);
        Assert.True(config.NotifyOnFailure);
        Assert.Equal(7, config.OverdueAfterDays);
        Assert.Equal(90, config.HistoryRetentionDays);
        Assert.Equal(30, config.LogRetentionDays);
        Assert.Equal(AppTheme.System, config.ThemeChoice);
        Assert.False(new SyncPair().KeepVersions);                // and so is keeping versions on a one-way pair
    }

    [Fact]
    public void Settings_that_would_make_no_sense_fall_back_to_their_defaults_on_load()
    {
        string path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
            { "HistoryRetentionDays": 0, "LogRetentionDays": -5, "SyncOnChangeDelaySeconds": 0,
              "OverdueAfterDays": -1, "Theme": "purple" }
            """);

        var config = SyncConfig.LoadFrom(path);

        Assert.Null(config.LoadProblem);
        Assert.Equal(90, config.HistoryRetentionDays);            // 0 would delete the run just recorded
        Assert.Equal(30, config.LogRetentionDays);
        Assert.Equal(60, config.SyncOnChangeDelaySeconds);        // 0 would sync a file still being written
        Assert.Equal(7, config.OverdueAfterDays);
        Assert.Equal("System", config.Theme);
    }

    [Fact]
    public void Zero_days_is_a_valid_way_to_switch_the_overdue_warning_off()
    {
        string path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{ "OverdueAfterDays": 0, "Theme": "dark" }""");

        var config = SyncConfig.LoadFrom(path);

        Assert.Equal(0, config.OverdueAfterDays);
        Assert.Equal(AppTheme.Dark, config.ThemeChoice);
        Assert.Equal("Dark", config.Theme);
    }

    [Fact]
    public void The_new_settings_and_a_pairs_keep_versions_choice_survive_a_save_and_load()
    {
        string path = Path.Combine(_root, "config.json");
        var config = new SyncConfig
        {
            SyncOnChange = true, SyncOnChangeDelaySeconds = 120, NotifyOnFailure = false, OverdueAfterDays = 14,
            HistoryRetentionDays = 365, LogRetentionDays = 7, Theme = "Light",
            Email = new EmailSettings { Mode = EmailMode.Weekly },
            Pairs = { new SyncPair { Left = @"C:\a", Right = @"D:\b", Mirror = true, KeepVersions = true } },
        };

        config.SaveTo(path);
        var read = SyncConfig.LoadFrom(path);

        Assert.True(read.SyncOnChange);
        Assert.Equal(120, read.SyncOnChangeDelaySeconds);
        Assert.False(read.NotifyOnFailure);
        Assert.Equal(14, read.OverdueAfterDays);
        Assert.Equal(365, read.HistoryRetentionDays);
        Assert.Equal(7, read.LogRetentionDays);
        Assert.Equal(AppTheme.Light, read.ThemeChoice);
        Assert.Equal(EmailMode.Weekly, read.Email.Mode);
        Assert.True(Assert.Single(read.Pairs).KeepVersions);
    }

    [Fact]
    public void A_config_from_1_2_reads_unchanged_with_versions_off_for_its_pairs()
    {
        string path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
            { "Pairs": [ { "Name": "Docs", "Left": "C:\\Docs", "Right": "D:\\Backup", "Direction": 0, "Mirror": true, "Enabled": true } ],
              "RecycleRetentionDays": 30, "GuardLargeDeletions": true, "Email": { "Enabled": false, "Mode": "OnError" } }
            """);

        var config = SyncConfig.LoadFrom(path);

        var pair = Assert.Single(config.Pairs);
        Assert.True(pair.Mirror);
        Assert.False(pair.KeepVersions);
        Assert.False(config.SyncOnChange);
        Assert.Equal(EmailMode.OnError, config.Email.Mode);
    }

    [Theory]
    [InlineData(SyncDirection.ToRight, true, true, true, true)]       // one-way, asked for
    [InlineData(SyncDirection.ToRight, false, true, true, false)]     // one-way, not asked for
    [InlineData(SyncDirection.ToLeft, true, false, true, true)]       // the two-way setting does not affect one-way
    [InlineData(SyncDirection.TwoWay, false, true, true, true)]       // two-way follows the global setting
    [InlineData(SyncDirection.TwoWay, true, false, true, false)]      // ...and not the pair's own
    [InlineData(SyncDirection.ToRight, true, true, false, false)]     // nowhere to keep them without the recycle folder
    [InlineData(SyncDirection.TwoWay, false, true, false, false)]
    public void Whether_a_pair_keeps_replaced_versions(SyncDirection direction, bool pairAsks, bool twoWaySetting,
                                                       bool recycle, bool expected)
    {
        var pair = new SyncPair { Left = "a", Right = "b", Direction = direction, KeepVersions = pairAsks };
        var config = new SyncConfig { BackupOverwrittenFiles = twoWaySetting, RecycleDeletedFiles = recycle };

        Assert.Equal(expected, config.KeepsVersionsFor(pair));
    }

    [Fact]
    public void A_pair_describes_its_direction_and_mode_for_the_list()
    {
        var mirror = new SyncPair { Direction = SyncDirection.ToRight, Mirror = true };
        Assert.Equal("→", mirror.Arrow);
        Assert.Equal("Mirror", mirror.ModeDetail);

        mirror.KeepVersions = true;
        Assert.Equal("Mirror · keeps versions", mirror.ModeDetail);

        Assert.Equal("←", new SyncPair { Direction = SyncDirection.ToLeft }.Arrow);
        Assert.Equal("Additive", new SyncPair { Direction = SyncDirection.ToLeft }.ModeDetail);

        var twoWay = new SyncPair { Direction = SyncDirection.TwoWay, KeepVersions = true };
        Assert.Equal("↔", twoWay.Arrow);
        Assert.Equal("Two-way merge", twoWay.ModeDetail);          // its versions follow the global setting
    }

    [Fact]
    public void Changing_a_pair_tells_the_list_to_redraw_what_depends_on_it()
    {
        var pair = new SyncPair();
        var raised = new List<string?>();
        pair.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pair.KeepVersions = true;
        Assert.Contains(nameof(SyncPair.ModeDetail), raised);

        raised.Clear();
        pair.Direction = SyncDirection.TwoWay;
        Assert.Contains(nameof(SyncPair.Arrow), raised);

        raised.Clear();
        pair.LastRunHealth = PairHealth.Bad;
        Assert.Contains(nameof(SyncPair.LastRunHealth), raised);
    }

    // ---------- a second copy of the app's data ----------

    [Fact]
    public void Without_the_override_the_app_uses_its_usual_data_folder_and_lock_names()
    {
        // The tests run without FOLDERSYNC_DATA_DIR, so this is the ordinary case.
        Assert.False(AppInstance.IsSeparate);
        Assert.Equal("", AppInstance.Suffix);
        Assert.Equal(@"Local\FolderSync.Window", AppInstance.WindowMutexName);
        Assert.Equal(SyncLock.DefaultName, AppInstance.SyncLockName);
        Assert.Equal("FolderSync", AppInstance.StartupValueName);
        Assert.EndsWith(@"\FolderSync", AppInstance.DataDir);
    }

    // ---------- sizes in robocopy's list ----------

    [Theory]
    [InlineData("\t    New File  \t\t 3145728\tC:\\src\\big.bin", 3145728)]
    [InlineData("\t    Newer     \t\t      12\tC:\\src\\sub\\changed.txt", 12)]
    [InlineData("\t    New File  \t\t       0\tC:\\src\\empty.txt", 0)]
    [InlineData("\t  Neue Datei  \t\t    4096\tC:\\src\\notiz.txt", 4096)]          // the label is never read
    [InlineData("\t    New File  \t\t   3.0 m\tC:\\src\\big.bin", -1)]              // without /BYTES: not a number
    public void A_file_to_copy_is_read_with_its_size_in_bytes(string line, long size)
    {
        Assert.True(RobocopyOutput.TryParseCopyPath(line, @"C:\src", out string path, out long actual));
        Assert.StartsWith(@"C:\src\", path);
        Assert.Equal(size, actual);
    }

    [Fact]
    public void A_name_with_spaces_or_digits_is_not_mistaken_for_the_size()
    {
        // The path is found first, by its root; only what comes before it is the size.
        Assert.True(RobocopyOutput.TryParseCopyPath("\t    New File  \t\t      12\tC:\\src\\2026 budget 99.xlsx", @"C:\src", out string path, out long size));

        Assert.Equal(@"C:\src\2026 budget 99.xlsx", path);
        Assert.Equal(12, size);
    }

    [Fact]
    public void An_extra_is_read_with_its_size_and_a_folder_has_none()
    {
        Assert.True(RobocopyOutput.TryParseExtraPath("\t  *EXTRA File \t\t       3\tC:\\dst\\gone\\inner.txt", @"C:\dst", out string file, out bool isDir, out long size));
        Assert.Equal(@"C:\dst\gone\inner.txt", file);
        Assert.False(isDir);
        Assert.Equal(3, size);

        // Verified on Windows 11: robocopy sizes a folder as -1, in the same column as the label.
        Assert.True(RobocopyOutput.TryParseExtraPath("\t*EXTRA Dir        -1\tC:\\dst\\gone\\", @"C:\dst", out string folder, out isDir, out size));
        Assert.Equal(@"C:\dst\gone", folder);
        Assert.True(isDir);
        Assert.Equal(-1, size);
    }

    [Theory]
    [InlineData("\t    New File  \t\t   3.0 m\tC:\\src\\big.bin", true)]
    [InlineData("\t    Newer     \t\t      12\tC:\\src\\caf? ?.txt", true)]             // a name the console could not spell
    [InlineData("\t  Neue Datei  \t\t    4096\tC:\\src\\notiz.txt", true)]
    [InlineData("\t  *EXTRA File \t\t       3\tC:\\dst\\extra.txt", false)]             // listed, not copied
    [InlineData("\t    New Dir          2\tC:\\src\\sub\\", false)]                     // a folder
    [InlineData("2026/11/12 13:52:22 ERROR 5 (0x00000005) Copying File C:\\src\\a.txt", false)]
    [InlineData("   Files :         5         4         1         0         0         1", false)]
    [InlineData("  Started : November 26, 2026 10:59:44 PM", false)]
    [InlineData("", false)]
    public void Only_lines_that_report_a_copied_file_count_towards_progress(string line, bool counts)
    {
        Assert.Equal(counts, RobocopyOutput.IsCopiedFileLine(line, @"C:\dst"));
    }

    // ---------- small formatting ----------

    [Theory]
    [InlineData(0, "0 pairs")]
    [InlineData(1, "1 pair")]
    [InlineData(3, "3 pairs")]
    public void Counts_are_written_as_words_not_with_a_bracketed_s(int count, string expected)
    {
        Assert.Equal(expected, Humanise.Count(count, "pair"));
    }

    [Fact]
    public void A_count_can_be_given_an_irregular_plural_and_large_ones_are_grouped()
    {
        Assert.Equal("2 batches", Humanise.Count(2, "batch", "batches"));
        Assert.Equal("1 batch", Humanise.Count(1, "batch", "batches"));
        Assert.Equal($"{1204:N0} files", Humanise.Count(1204, "file"));
    }

    [Theory]
    [InlineData(0.4, "under 1 s")]
    [InlineData(42, "42 s")]
    [InlineData(125, "2 min 05 s")]
    [InlineData(3600 + 12 * 60 + 9, "1 h 12 min")]
    public void A_duration_is_short_and_readable(double seconds, string expected)
    {
        Assert.Equal(expected, Humanise.Duration(TimeSpan.FromSeconds(seconds)));
    }
}
