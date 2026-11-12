using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Protecting the configuration file and keeping the logs bounded.
///
/// Both are driven through path-taking overloads against a temporary folder, so — as with the rest of
/// the suite — nothing here reads or writes the real %LOCALAPPDATA%\FolderSync.
/// </summary>
public class ConfigSafetyTests : IDisposable
{
    private readonly string _dir;

    public ConfigSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fs-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------- a damaged config is kept, not destroyed ----------

    [Fact]
    public void An_unreadable_config_is_set_aside_with_its_contents_intact()
    {
        // Loading used to replace a corrupt file with defaults on the spot, destroying every folder
        // pair, the email settings and the schedule preferences with nothing said and no copy kept.
        string config = Path.Combine(_dir, "config.json");
        const string damaged = "{ \"Pairs\": [ { \"Left\": \"C:\\\\Docs\"  <-- truncated";
        File.WriteAllText(config, damaged);

        string? kept = SyncConfig.MoveConfigAside(config, new DateTime(2026, 7, 31, 14, 30, 45));

        Assert.NotNull(kept);
        Assert.Equal("config.bad-20260731-143045.json", Path.GetFileName(kept));

        // The original is out of the way, so writing defaults cannot overwrite it...
        Assert.False(File.Exists(config));
        // ...and every byte of it survived for inspection or repair by hand.
        Assert.Equal(damaged, File.ReadAllText(kept!));
    }

    [Fact]
    public void Nothing_happens_when_there_is_no_config_to_rescue()
    {
        // First run: there is no file, which is not a problem to report.
        Assert.Null(SyncConfig.MoveConfigAside(Path.Combine(_dir, "config.json"), DateTime.Now));
    }

    [Fact]
    public void Two_rescues_in_the_same_second_keep_both_files()
    {
        // Daily and Monthly both default to 18:00, so two scheduled runs can rescue in the same second.
        // The second rescue used to overwrite the first, which could replace the user's real (damaged)
        // settings with the defaults the first run had just written.
        string config = Path.Combine(_dir, "config.json");
        var when = new DateTime(2026, 7, 31, 14, 30, 45);

        File.WriteAllText(config, "first");
        string? first = SyncConfig.MoveConfigAside(config, when);

        File.WriteAllText(config, "second");
        string? second = SyncConfig.MoveConfigAside(config, when);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal("first", File.ReadAllText(first!));
        Assert.Equal("second", File.ReadAllText(second!));
    }

    [Fact]
    public void A_damaged_config_that_another_program_holds_open_is_copied_aside_instead()
    {
        // Held open without delete sharing, the file cannot be moved. That used to leave no copy at all,
        // and the defaults were then written straight over it.
        string config = Path.Combine(_dir, "config.json");
        File.WriteAllText(config, "{ damaged");

        using (new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            string? kept = SyncConfig.MoveConfigAside(config, DateTime.Now);
            Assert.NotNull(kept);
            Assert.Equal("{ damaged", File.ReadAllText(kept!));
        }
    }

    // ---------- loading: only real damage counts as damage ----------

    [Fact]
    public void A_valid_config_loads_with_its_pairs()
    {
        string config = WriteConfig(PairJson);

        var cfg = Load(config, out var log);

        Assert.Null(cfg.LoadProblem);
        Assert.Equal(@"C:\Docs", Assert.Single(cfg.Pairs).Left);
        Assert.Empty(BadCopies());
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_config_that_is_briefly_locked_is_waited_for_not_thrown_away()
    {
        // The window saving at the moment a scheduled run starts. This used to count as "damaged": the
        // good file was renamed to config.bad-* and the run carried on with no pairs, reporting success.
        string config = WriteConfig(PairJson);
        var holder = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(400); holder.Dispose(); });

        var cfg = Load(config, out _, attempts: 30, delayMs: 50);
        await release;

        Assert.Null(cfg.LoadProblem);
        Assert.Single(cfg.Pairs);
        Assert.Empty(BadCopies());
        Assert.Equal(PairJson, File.ReadAllText(config));
    }

    [Fact]
    public void A_config_that_stays_locked_fails_the_load_and_is_left_alone()
    {
        string config = WriteConfig(PairJson);

        using (new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<ConfigUnavailableException>(() => Load(config, out _, attempts: 3, delayMs: 10));

        // Neither renamed nor replaced.
        Assert.Equal(PairJson, File.ReadAllText(config));
        Assert.Empty(BadCopies());
    }

    [Fact]
    public void A_damaged_config_is_kept_and_the_load_reports_why()
    {
        string config = WriteConfig("{ \"Pairs\": [ { \"Left\": ");

        var cfg = Load(config, out var log);

        Assert.Empty(cfg.Pairs);
        Assert.NotNull(cfg.LoadProblem);              // makes a scheduled run fail instead of "succeeding"
        Assert.Contains("config.bad-", cfg.LoadProblem);
        Assert.Equal("{ \"Pairs\": [ { \"Left\": ", File.ReadAllText(Assert.Single(BadCopies())));
        Assert.Contains(log, l => l.Contains("could not be read"));
    }

    [Fact]
    public void A_damaged_config_that_cannot_be_kept_is_not_overwritten()
    {
        // Nowhere to keep a copy (the folder refuses new files), so writing defaults would destroy the
        // only copy of the settings. They must be used in memory only.
        string config = WriteConfig("{ damaged");
        var denied = DenyCreatingFiles(_dir);
        try
        {
            var cfg = Load(config, out _);

            Assert.NotNull(cfg.LoadProblem);
            Assert.Contains("left untouched", cfg.LoadProblem);
        }
        finally { denied.Dispose(); }

        Assert.Equal("{ damaged", File.ReadAllText(config));
    }

    [Fact]
    public void A_migration_that_cannot_be_saved_is_not_mistaken_for_damage()
    {
        // An old single-source config migrates to pairs and is saved. When that save failed (a read-only
        // file), the same catch-all treated the good file as unreadable and set it aside.
        const string legacy = "{ \"Source\": \"C:\\\\Docs\", \"Targets\": [ { \"Name\": \"Backup\", \"Destination\": \"E:\\\\Docs\" } ] }";
        string config = WriteConfig(legacy);
        File.SetAttributes(config, FileAttributes.ReadOnly);
        try
        {
            var cfg = Load(config, out var log);

            Assert.Null(cfg.LoadProblem);
            var pair = Assert.Single(cfg.Pairs);       // migrated, and in use for this run
            Assert.Equal(@"E:\Docs", pair.Right);
            Assert.Empty(BadCopies());
            Assert.Contains(log, l => l.Contains("could not be saved"));
        }
        finally { File.SetAttributes(config, FileAttributes.Normal); }

        Assert.Equal(legacy, File.ReadAllText(config));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(-3)]
    public void A_deletion_percentage_outside_one_to_a_hundred_is_reset(int percent)
    {
        string config = WriteConfig($"{{ \"LargeDeletionPercent\": {percent} }}");

        var cfg = Load(config, out var log);

        Assert.Equal(SyncConfig.DefaultLargeDeletionPercent, cfg.LargeDeletionPercent);
        Assert.Contains(log, l => l.Contains("LargeDeletionPercent"));
    }

    [Fact]
    public void A_pair_with_a_null_folder_loads_as_blank_rather_than_null()
    {
        string config = WriteConfig("{ \"Pairs\": [ { \"Left\": null, \"Right\": \"E:\\\\x\" } ] }");

        var pair = Assert.Single(Load(config, out _).Pairs);

        Assert.Equal("", pair.Left);
    }

    [Fact]
    public void The_replaced_version_safety_net_needs_recycling_switched_on()
    {
        // The engine needs both settings; the health check used to look at only one, and so reported
        // replaced versions as kept while they were being lost.
        Assert.True(new SyncConfig { BackupOverwrittenFiles = true, RecycleDeletedFiles = true }.KeepsReplacedVersions);
        Assert.False(new SyncConfig { BackupOverwrittenFiles = true, RecycleDeletedFiles = false }.KeepsReplacedVersions);
        Assert.False(new SyncConfig { BackupOverwrittenFiles = false, RecycleDeletedFiles = true }.KeepsReplacedVersions);
    }

    // ---------- saving is atomic ----------

    [Fact]
    public async Task A_reader_never_sees_a_half_written_config()
    {
        // Saving used to write the file in place, so a reader could catch it part-written - which is
        // exactly what then got treated as a damaged config.
        string config = Path.Combine(_dir, "config.json");
        var cfg = new SyncConfig();
        for (int i = 0; i < 300; i++)
            cfg.Pairs.Add(new SyncPair { Name = $"pair {i}", Left = $@"C:\left\{i}", Right = $@"E:\right\{i}" });
        cfg.SaveTo(config);

        using var stop = new CancellationTokenSource();
        int saves = 0;
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // File.ReadAllText below does not share delete access, so a save can be refused while it
                // reads; that is retried inside SaveTo and, if still refused, simply skipped here.
                try { cfg.SaveTo(config); saves++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });

        int torn = 0;
        for (int i = 0; i < 300; i++)
        {
            string text;
            try { text = File.ReadAllText(config); }
            catch (IOException) { continue; }        // momentarily busy is fine; half-written is not
            try { System.Text.Json.JsonSerializer.Deserialize<SyncConfig>(text); }
            catch (System.Text.Json.JsonException) { torn++; }
        }
        stop.Cancel();
        await writer;

        Assert.True(saves > 0);
        Assert.Equal(0, torn);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ---------- helpers ----------

    private const string PairJson = "{ \"Pairs\": [ { \"Name\": \"Docs\", \"Left\": \"C:\\\\Docs\", \"Right\": \"E:\\\\Docs\" } ] }";

    private string WriteConfig(string json)
    {
        string config = Path.Combine(_dir, "config.json");
        File.WriteAllText(config, json);
        return config;
    }

    private static SyncConfig Load(string config, out List<string> log, int attempts = 5, int delayMs = 20)
    {
        var lines = new List<string>();
        log = lines;
        return SyncConfig.LoadFrom(config, lines.Add, attempts, delayMs);
    }

    private string[] BadCopies() => Directory.GetFiles(_dir, "config.bad-*");

    /// <summary>Stops the current user creating files in <paramref name="folder"/> until disposed.</summary>
    private static IDisposable DenyCreatingFiles(string folder)
    {
        var info = new DirectoryInfo(folder);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user, System.Security.AccessControl.FileSystemRights.CreateFiles,
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

    // ---------- logs stay bounded ----------

    [Fact]
    public void A_large_event_log_is_trimmed_to_its_most_recent_entries()
    {
        // events.log was never pruned - only sync-*.log and preview-*.log were - so it grew for the
        // lifetime of the installation.
        string log = Path.Combine(_dir, "events.log");
        File.WriteAllLines(log, Enumerable.Range(1, 5000).Select(i => $"line {i}"));

        SyncConfig.TrimLog(log, keepLines: 100, maxBytes: 1024);

        var lines = File.ReadAllLines(log);
        Assert.Equal(101, lines.Length);                 // 100 kept, plus a note saying it was trimmed
        Assert.Contains("trimmed to the last 100", lines[0]);

        // The newest entries are the ones worth keeping.
        Assert.Equal("line 5000", lines[^1]);
        Assert.Equal("line 4901", lines[1]);
    }

    [Fact]
    public void A_log_under_the_limit_is_left_exactly_as_it_was()
    {
        string log = Path.Combine(_dir, "events.log");
        File.WriteAllLines(log, new[] { "one", "two", "three" });
        string before = File.ReadAllText(log);

        SyncConfig.TrimLog(log, keepLines: 100, maxBytes: 1024 * 1024);

        Assert.Equal(before, File.ReadAllText(log));
    }

    [Fact]
    public void Trimming_a_log_that_is_not_there_is_harmless()
    {
        SyncConfig.TrimLog(Path.Combine(_dir, "nope.log"));
    }
}
