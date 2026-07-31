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
    public void Two_rescues_in_the_same_second_do_not_throw()
    {
        string config = Path.Combine(_dir, "config.json");
        var when = new DateTime(2026, 7, 31, 14, 30, 45);

        File.WriteAllText(config, "first");
        Assert.NotNull(SyncConfig.MoveConfigAside(config, when));

        File.WriteAllText(config, "second");
        string? second = SyncConfig.MoveConfigAside(config, when);

        // Same timestamp, so the same name: overwriting is acceptable, failing to start is not.
        Assert.NotNull(second);
        Assert.Equal("second", File.ReadAllText(second!));
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
