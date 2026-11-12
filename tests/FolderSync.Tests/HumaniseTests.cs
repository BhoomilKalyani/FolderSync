using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The shared readable-text helpers. These replaced three separate relative-time formatters that had
/// drifted apart, so the same sync could be described differently on different screens.
/// </summary>
public class HumaniseTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(255054573568, "237.5 GB")]
    public void Sizes_read_the_way_people_expect(long bytes, string expected)
    {
        Assert.Equal(expected, Humanise.Size(bytes));
    }

    [Fact]
    public void A_negative_size_produces_nothing_rather_than_a_nonsense_figure()
    {
        // Some providers report -1 for "unknown".
        Assert.Equal("", Humanise.Size(-1));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(86400, "1 day")]
    [InlineData(259200, "3 days")]
    public void Ages_use_whole_units_and_correct_plurals(int seconds, string expected)
    {
        // Whole units on purpose: "1.5 day(s)" was both ugly and more precise than the figure deserved.
        Assert.Equal(expected, Humanise.Age(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Ago_appends_the_suffix()
    {
        Assert.Equal("2 hours ago", Humanise.Ago(DateTime.Now.AddHours(-2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(59)]
    public void Something_under_a_minute_old_is_just_now_without_a_suffix(int secondsAgo)
    {
        // Was "just now ago" - shown in every pair's Last run cell straight after a sync.
        Assert.Equal("just now", Humanise.Ago(DateTime.Now.AddSeconds(-secondsAgo)));
    }

    [Fact]
    public void A_pair_that_has_just_run_reads_naturally()
    {
        Assert.Equal("OK · just now", new PairStatus { When = DateTime.Now, Success = true }.Text);
    }

    [Fact]
    public void A_timestamp_in_the_future_does_not_produce_a_negative_age()
    {
        // Happens after a clock change, or when a config is copied from another machine.
        Assert.Equal("just now", Humanise.Ago(DateTime.Now.AddHours(2)));
    }

    [Fact]
    public void The_pair_status_line_uses_the_shared_formatter()
    {
        var status = new PairStatus { When = DateTime.Now.AddDays(-3), Success = true };
        Assert.Equal("OK · 3 days ago", status.Text);

        var offline = new PairStatus { When = DateTime.Now.AddHours(-1), Skipped = true };
        Assert.Equal("Not connected · 1 hour ago", offline.Text);

        var broken = new PairStatus { When = DateTime.Now.AddMinutes(-5), Success = false };
        Assert.Equal("Failed · 5 minutes ago", broken.Text);
    }
}
