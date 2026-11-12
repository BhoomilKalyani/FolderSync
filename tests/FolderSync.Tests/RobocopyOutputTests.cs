using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Guards the robocopy output parsing. A missing /NDL once meant "*EXTRA File" lines carried only
/// a bare file name, so root-level files were permanently deleted instead of recycled - these
/// tests pin down both the working format and the one that must be rejected.
///
/// None of the parsers may depend on robocopy's English words, which are translated on non-English
/// Windows. Many tests below therefore run each real line again with its label swapped for another
/// language's, and expect the same answer.
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
    private const string MismatchLine = "\t*MISMATCH         -1\tC:\\src\\Docs\\clash\\";
    private const string ErrorLine = "2026/11/12 13:52:22 ERROR 5 (0x00000005) Accessing Destination Directory C:\\dest\\Docs\\sub\\";

    /// <summary>The same line as robocopy prints it on German Windows, or in some language not yet seen.</summary>
    private static IEnumerable<string> InOtherLanguages(string line) => new[]
    {
        line,
        line.Replace("*EXTRA File", "*EXTRA Datei").Replace("*EXTRA Dir", "*EXTRA Verz.")
            .Replace("New File", "Neue Datei").Replace("Newer", "Neuer").Replace("ERROR", "FEHLER")
            .Replace("Accessing Destination Directory", "Zugriff auf Zielverzeichnis"),
        line.Replace("*EXTRA File", "*Лишний файл").Replace("*EXTRA Dir", "*Лишняя папка").Replace("Newer", "Новее")
            .Replace("New File", "Новый файл").Replace("ERROR", "ОШИБКА")
            .Replace("Accessing Destination Directory", "Доступ к папке назначения"),
    };

    // ---------- what a mirror would delete ----------

    [Fact]
    public void An_extra_directory_is_told_apart_from_an_extra_file_in_any_language()
    {
        // The deletion guard counts files only. robocopy lists the folder *and* its contents, so
        // counting every extra line would double-count and make the guard trip too easily. Folders are
        // told apart by their trailing "\", not by the "Dir"/"File" label.
        foreach (var line in InOtherLanguages(ExtraDirLine))
        {
            Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string dir, out bool isDir));
            Assert.Equal(@"C:\dest\Docs\extradir", dir);          // trailing slash removed
            Assert.True(isDir);
        }
        foreach (var line in InOtherLanguages(ExtraFileLine))
        {
            Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string file, out bool isDir));
            Assert.Equal(@"C:\dest\Docs\extradir\a.txt", file);
            Assert.False(isDir);
        }
    }

    [Fact]
    public void Only_lines_naming_the_destination_are_deletions_in_any_language()
    {
        // Everything robocopy copies, or reports a clash for, is named by its source path.
        foreach (var source in new[] { NewerLine, NewFileLine, MismatchLine })
            foreach (var line in InOtherLanguages(source))
                Assert.False(RobocopyOutput.TryParseExtraPath(line, Dst, out _, out _), line);
    }

    [Fact]
    public void An_error_about_a_destination_folder_is_not_a_deletion_in_any_language()
    {
        // It names a destination folder and ends in "\" - exactly like an extra folder - but robocopy is
        // reporting that it could not open it. Moving it aside would hide files that are still wanted.
        foreach (var line in InOtherLanguages(ErrorLine))
            Assert.False(RobocopyOutput.TryParseExtraPath(line, Dst, out _, out _), line);
    }

    [Fact]
    public void A_file_whose_name_looks_like_an_error_code_is_still_found()
    {
        // Only the text before the path is checked for an error code.
        const string line = "\t  *EXTRA File \t\t      3\tC:\\dest\\Docs\\(0x00000005) report.txt";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path, out _));
        Assert.Equal(@"C:\dest\Docs\(0x00000005) report.txt", path);
    }

    [Fact]
    public void A_drive_root_works_as_the_root()
    {
        Assert.True(RobocopyOutput.TryParseExtraPath("\t  *EXTRA File \t\t 5\tE:\\a.txt", @"E:\", out string path, out _));
        Assert.Equal(@"E:\a.txt", path);
    }

    [Fact]
    public void ExtraPath_reads_a_root_level_file()
    {
        // This is the case the /NDL bug used to miss.
        string line = "\t    *EXTRA File \t\t       3\t" + Dst + @"\root.txt";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path, out _));
        Assert.Equal(Dst + @"\root.txt", path);
    }

    [Fact]
    public void ExtraPath_handles_sizes_that_contain_a_space()
    {
        // "1.7 m" would break any parser that split the line on whitespace.
        string line = "\t    *EXTRA File \t\t   1.7 m\t" + Dst + @"\big file.pdf";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path, out bool isDir));
        Assert.Equal(Dst + @"\big file.pdf", path);
        Assert.False(isDir);
    }

    [Fact]
    public void ExtraPath_is_case_insensitive_about_the_destination_root()
    {
        string line = @"    *EXTRA File        5    c:\DEST\docs\a.txt";

        Assert.True(RobocopyOutput.TryParseExtraPath(line, Dst, out string path, out _));
        Assert.EndsWith(@"a.txt", path);
    }

    [Theory]
    [InlineData("\t    *EXTRA Dir \t\t      -1\tC:\\dest\\Docs\\")]         // the root itself
    [InlineData("\t    *EXTRA File \t\t       3\tD:\\somewhere\\else.txt")] // different root
    [InlineData("\t  *EXTRA File \t\t     17\tC:\\dest\\Docs2\\a.txt")]    // a sibling that starts the same
    [InlineData("")]
    [InlineData("Access is denied.")]                                      // the line after an error
    public void ExtraPath_rejects_lines_that_are_not_deletable_items(string line)
    {
        Assert.False(RobocopyOutput.TryParseExtraPath(line, Dst, out _, out _));
    }

    // ---------- what a two-way pass would overwrite ----------

    [Fact]
    public void Any_listed_source_file_is_a_candidate_overwrite_in_any_language()
    {
        // Whether it really replaces something is decided by the engine checking that the destination
        // already has the file - so the label, translated or not, is never read.
        const string src = @"C:\src\Docs";
        const string changed = "\t    Changed   \t\t      27\tC:\\src\\Docs\\same-time.txt";

        foreach (var line in InOtherLanguages(NewerLine))
        {
            Assert.True(RobocopyOutput.TryParseOverwrittenSourcePath(line, src, out string path));
            Assert.Equal(@"C:\src\Docs\shared.txt", path);
        }
        Assert.True(RobocopyOutput.TryParseOverwrittenSourcePath(changed, src, out string c));
        Assert.Equal(@"C:\src\Docs\same-time.txt", c);
    }

    [Fact]
    public void The_destinations_own_lines_are_never_overwrites()
    {
        const string src = @"C:\src\Docs";
        foreach (var line in InOtherLanguages(ExtraFileLine).Concat(InOtherLanguages(ErrorLine)))
            Assert.False(RobocopyOutput.TryParseOverwrittenSourcePath(line, src, out _), line);
    }

    [Fact]
    public void An_error_about_a_source_file_is_not_an_overwrite()
    {
        const string line = "2026/11/12 13:52:22 ERROR 32 (0x00000020) Copying File C:\\src\\Docs\\locked.txt";
        Assert.False(RobocopyOutput.TryParseOverwrittenSourcePath(line, @"C:\src\Docs", out _));
    }

    [Theory]
    [InlineData("\t    New Dir          2\tC:\\src\\Docs\\folder\\")]    // a directory
    [InlineData("\t*MISMATCH         -1\tC:\\src\\Docs\\clash\\")]       // a directory clash
    [InlineData("\t    Newer     \t\t      16\tC:\\src\\Docs2\\other.txt")] // a sibling that starts the same
    [InlineData("\t    Newer     \t\t      16\tC:\\somewhere\\else\\shared.txt")]   // different root
    [InlineData("")]
    [InlineData("   ")]
    public void A_line_that_is_not_an_overwrite_here_is_rejected(string line)
    {
        Assert.False(RobocopyOutput.TryParseOverwrittenSourcePath(line, @"C:\src\Docs", out _));
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
    [InlineData(@"C:\Docs", "\"C:\\Docs\"")]
    [InlineData(@"E:\", "\"E:\\\\\"")]                    // the trailing \ doubled so it cannot escape the quote
    [InlineData(@"C:\Docs\", "\"C:\\Docs\\\\\"")]
    public void Paths_are_quoted_so_a_trailing_backslash_cannot_escape_the_quote(string path, string expected)
    {
        Assert.Equal(expected, SyncEngine.Quote(path));
    }

    // ---------- the totals table ----------

    // The end of a real run on Windows 11, then the same table as German Windows prints it.
    private static readonly string[] EnglishTotals =
    {
        "\t    Newer     \t\t       5\tC:\\src\\Docs\\newer.txt",
        "",
        "------------------------------------------------------------------------------",
        "",
        "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
        "    Dirs :         4         0         2         2         0         2",
        "   Files :      2201       462      1739         0         3         1",
        "   Bytes :   1.234 g   12.50 m         0         0         0        28",
        "   Times :   0:00:05   0:00:01                       0:00:00   0:00:00",
        "   Ended : November 12, 2026 1:51:32 PM",
    };

    private static readonly string[] GermanTotals =
    {
        "\t    Neuer     \t\t       5\tC:\\src\\Docs\\newer.txt",
        "",
        "------------------------------------------------------------------------------",
        "",
        "            Insgesamt   KopiertÜbersprungenKeine Übereinstimmung   FEHLER    Extras",
        "Verzeich.:         4         0         2         2         0         2",
        "  Dateien:      2201       462      1739         0         3         1",
        "    Bytes:   1.234 g   12.50 m         0         0         0        28",
        "   Zeiten:   0:00:05   0:00:01                       0:00:00   0:00:00",
        "    Beendet: Donnerstag, 12. November 2026 13:51:32",
    };

    [Fact]
    public void The_files_row_is_read_by_its_place_in_the_table_in_any_language()
    {
        // Matching "Files :" meant every report on a non-English PC said nothing was copied.
        foreach (var table in new[] { EnglishTotals, GermanTotals })
        {
            var totals = new RobocopyTotals();
            foreach (var line in table) totals.Feed(line);

            Assert.True(totals.FoundFiles);
            Assert.Equal(462, totals.FilesCopied);
            Assert.Equal(3, totals.FilesFailed);
        }
    }

    [Fact]
    public void The_summary_keeps_the_dirs_files_and_bytes_rows()
    {
        var totals = new RobocopyTotals();
        foreach (var line in EnglishTotals) totals.Feed(line);

        Assert.StartsWith("Dirs :", totals.Summary);
        Assert.Contains("Files :      2201", totals.Summary);
        Assert.Contains("Bytes :   1.234 g", totals.Summary);
        Assert.DoesNotContain("Times", totals.Summary);
    }

    [Fact]
    public void Small_byte_counts_do_not_shift_the_rows()
    {
        // Bytes are printed as whole numbers when small, so that row parses too - it must stay third.
        var totals = new RobocopyTotals();
        foreach (var line in new[]
                 {
                     "    Dirs :         1         0         1         0         0         0",
                     "   Files :         2         2         0         0         0         0",
                     "   Bytes :        28        28         0         0         0         0",
                 })
            totals.Feed(line);

        Assert.Equal(2, totals.FilesCopied);
    }

    [Theory]
    [InlineData("        Files : *.*")]                                  // options header, not the summary
    [InlineData("   Exc Files : Thumbs.db")]                             // exclusion header
    [InlineData("   Times :   0:00:05   0:00:01   0:00:00   0:00:00")]   // not whole numbers
    [InlineData("       Files :      1  2  3")]                          // too few columns
    [InlineData("\t    New File  \t\t       3\tC:\\a 1 2 3 4 5 6")]      // an item line
    [InlineData("")]
    [InlineData("   ")]
    public void A_line_that_is_not_a_totals_row_is_rejected(string line)
    {
        Assert.False(RobocopyOutput.TryParseTotalsRow(line, out _));
    }

    [Fact]
    public void A_run_with_no_totals_table_reports_none()
    {
        var totals = new RobocopyTotals();
        totals.Feed(NewerLine);

        Assert.False(totals.FoundFiles);
        Assert.Equal(0, totals.FilesCopied);
        Assert.Equal("", totals.Summary);
    }
}
