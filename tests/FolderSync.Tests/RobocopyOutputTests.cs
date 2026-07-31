using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Guards the robocopy output parsing. A missing /NDL once meant "*EXTRA File" lines carried only
/// a bare file name, so root-level files were permanently deleted instead of recycled - these
/// tests pin down both the working format and the one that must be rejected.
/// </summary>
public class RobocopyOutputTests
{
    private const string Dst = @"C:\dest\Docs";

    [Fact]
    public void FilesSummary_reads_copied_and_failed_columns()
    {
        // Total, Copied, Skipped, Mismatch, FAILED, Extras
        const string line = "       Files :      2201       462      1739         0         3         1";

        Assert.True(RobocopyOutput.TryParseFilesSummary(line, out int copied, out int failed));
        Assert.Equal(462, copied);
        Assert.Equal(3, failed);
    }

    [Theory]
    [InlineData("        Files : *.*")]                                  // options header, not the summary
    [InlineData("   Exc Files : Thumbs.db")]                             // exclusion header
    [InlineData("        Dirs :       344       344       289 0 0 0")]    // different row
    [InlineData("       Files :      1  2  3")]                          // too few columns
    [InlineData("")]
    [InlineData("   ")]
    public void FilesSummary_rejects_everything_that_is_not_the_summary_row(string line)
    {
        Assert.False(RobocopyOutput.TryParseFilesSummary(line, out _, out _));
    }

    [Fact]
    public void ExtraPath_reads_a_root_level_file()
    {
        // This is the case the /NDL bug used to miss.
        string line = "\t    *EXTRA File \t\t       3\t" + Dst + @"\root.txt";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path));
        Assert.Equal(Dst + @"\root.txt", path);
    }

    [Fact]
    public void ExtraPath_reads_a_directory_and_drops_the_trailing_separator()
    {
        string line = "\t    *EXTRA Dir \t\t      -1\t" + Dst + @"\Travel\";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path));
        Assert.Equal(Dst + @"\Travel", path);
    }

    [Fact]
    public void ExtraPath_handles_sizes_that_contain_a_space()
    {
        // "1.7 m" would break any parser that split the line on whitespace.
        string line = "\t    *EXTRA File \t\t   1.7 m\t" + Dst + @"\big file.pdf";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path));
        Assert.Equal(Dst + @"\big file.pdf", path);
    }

    [Fact]
    public void ExtraPath_is_case_insensitive_about_the_destination_root()
    {
        string line = @"    *EXTRA File        5    c:\DEST\docs\a.txt";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path));
        Assert.EndsWith(@"a.txt", path);
    }

    [Theory]
    [InlineData("\t    New File \t\t       3\tC:\\dest\\Docs\\root.txt")]   // not an EXTRA line
    [InlineData("\t    *EXTRA Dir \t\t      -1\tC:\\dest\\Docs\\")]         // the root itself
    [InlineData("\t    *EXTRA File \t\t       3\tD:\\somewhere\\else.txt")] // different root
    [InlineData("")]
    public void ExtraPath_rejects_lines_that_are_not_deletable_items(string line)
    {
        Assert.False(RobocopyOutput.TryParseExtraPath(line, Dst, out _));
    }
}
