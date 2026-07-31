using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Version comparison decides whether users are offered an update at all, so the tag parsing
/// needs to cope with however a release happens to be tagged.
/// </summary>
public class UpdaterTests
{
    [Theory]
    [InlineData("v1.0.0", 1, 0, 0)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("V2.3.4", 2, 3, 4)]
    [InlineData("1.2", 1, 2, 0)]          // missing build
    [InlineData("3", 3, 0, 0)]            // major only
    [InlineData("v1.0.1-beta", 1, 0, 1)]  // pre-release suffix ignored
    [InlineData("  v1.4.2  ", 1, 4, 2)]   // stray whitespace
    [InlineData("v1.2.3.4", 1, 2, 3)]     // extra component ignored
    public void ParseVersion_reads_usual_tag_shapes(string tag, int major, int minor, int build)
    {
        var v = Updater.ParseVersion(tag);

        Assert.NotNull(v);
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("junk")]
    [InlineData("is-6_7_3")]   // Inno Setup style tag: no leading digits
    [InlineData("v")]
    [InlineData("release")]
    public void ParseVersion_returns_null_when_there_is_no_version(string tag)
    {
        Assert.Null(Updater.ParseVersion(tag));
    }

    [Fact]
    public void Newer_tags_compare_greater_than_older_ones()
    {
        Assert.True(Updater.ParseVersion("v1.1.0") > Updater.ParseVersion("v1.0.9"));
        Assert.True(Updater.ParseVersion("v2.0.0") > Updater.ParseVersion("v1.99.99"));
        Assert.True(Updater.ParseVersion("v1.0.1") > Updater.ParseVersion("v1.0.0"));
        Assert.False(Updater.ParseVersion("v1.0.0") > Updater.ParseVersion("v1.0.0"));
    }

    [Fact]
    public void A_zero_dot_version_is_not_treated_as_newer_than_one_point_zero()
    {
        // Guards the mistake of reading "0.87.1" as newer than "1.0.0".
        Assert.True(Updater.ParseVersion("v0.87.1") < Updater.ParseVersion("v1.0.0"));
    }

    [Fact]
    public void CurrentVersion_is_populated()
    {
        Assert.True(Updater.CurrentVersion > new Version(0, 0, 0));
    }
}
