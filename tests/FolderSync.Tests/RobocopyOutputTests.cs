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

    // Captured verbatim from robocopy on Windows 11. Note the tabs, the padded class names, and that
    // an extra directory is listed alongside every file inside it.
    private const string ExtraDirLine = "\t*EXTRA Dir        -1\tC:\\dest\\Docs\\extradir\\";
    private const string ExtraFileLine = "\t  *EXTRA File \t\t     17\tC:\\dest\\Docs\\extradir\\a.txt";
    private const string NewerLine = "\t    Newer     \t\t      16\tC:\\src\\Docs\\shared.txt";
    private const string NewFileLine = "\t    New File  \t\t       8\tC:\\src\\Docs\\brandnew.txt";

    [Fact]
    public void An_extra_directory_is_told_apart_from_an_extra_file()
    {
        // The deletion guard counts files only. robocopy lists the folder *and* its contents, so
        // counting every *EXTRA line would double-count and make the guard trip too easily.
        Assert.True(RobocopyOutput.IsExtraFile(ExtraFileLine));
        Assert.False(RobocopyOutput.IsExtraFile(ExtraDirLine));

        // Both are still things to move aside, so both must parse as paths.
        Assert.True(RobocopyOutput.TryParseExtraPath(ExtraDirLine, Dst, out string dir));
        Assert.Equal(@"C:\dest\Docs\extradir", dir);          // trailing slash removed
        Assert.True(RobocopyOutput.TryParseExtraPath(ExtraFileLine, Dst, out string file));
        Assert.Equal(@"C:\dest\Docs\extradir\a.txt", file);
    }

    [Fact]
    public void Only_a_file_that_already_exists_at_the_destination_counts_as_an_overwrite()
    {
        const string src = @"C:\src\Docs";

        // "Newer": the destination holds an older copy, which is about to be replaced and lost.
        Assert.True(RobocopyOutput.TryParseOverwrittenSourcePath(NewerLine, src, out string path));
        Assert.Equal(@"C:\src\Docs\shared.txt", path);

        // "New File": nothing there to overwrite, so backing it up would be meaningless.
        Assert.False(RobocopyOutput.TryParseOverwrittenSourcePath(NewFileLine, src, out _));

        // An extra belongs to the destination and is a deletion, not an overwrite.
        Assert.False(RobocopyOutput.TryParseOverwrittenSourcePath(ExtraFileLine, src, out _));
    }

    [Fact]
    public void A_path_is_found_even_when_the_size_column_uses_two_tokens()
    {
        // robocopy prints large sizes as "1.7 m", which is why the path is located by searching for
        // the root rather than by splitting on whitespace.
        const string line = "\t    Newer     \t\t     1.7 m\tC:\\src\\Docs\\big.iso";

        Assert.True(RobocopyOutput.TryParseOverwrittenSourcePath(line, @"C:\src\Docs", out string path));
        Assert.Equal(@"C:\src\Docs\big.iso", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t    Newer     \t\t      16\tC:\\somewhere\\else\\shared.txt")]   // different root
    public void A_line_that_is_not_an_overwrite_here_is_rejected(string line)
    {
        Assert.False(RobocopyOutput.TryParseOverwrittenSourcePath(line, @"C:\src\Docs", out _));
    }

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
