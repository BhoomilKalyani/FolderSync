using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderSync;

/// <summary>
/// Parsers for robocopy's console output. Kept separate from <see cref="SyncEngine"/> so the
/// fiddly bits (which have already caused real bugs) can be unit tested without running robocopy.
///
/// <para>None of them reads robocopy's words. Its labels ("*EXTRA File", "Newer", "Files :", "ERROR")
/// are translated on non-English Windows, so parsers keyed on the English text found nothing there:
/// no deletions to move aside or to guard against, and zero files copied in every report. They rely
/// instead on what is the same in every language - which folder a path is in, a trailing "\" for a
/// folder, the order of the totals table, and the "(0x00000005)" form of an error code.</para>
/// </summary>
public static class RobocopyOutput
{
    /// <summary>
    /// One row of the totals table robocopy prints at the end of a run: a label, a colon, then six
    /// whole-number columns - Total, Copied, Skipped, Mismatch, FAILED, Extras.
    /// <c>"   Files :  2201  462  1739  0  0  0"</c>.
    ///
    /// The label is not checked, because it is translated ("Dateien :", "Fichiers :"). Which row is
    /// which comes from their order, which never changes - see <see cref="RobocopyTotals"/>. Returns
    /// false for the options header (<c>"Files : *.*"</c>), the Times row, and item lines, which start
    /// with a tab and carry a path.
    /// </summary>
    public static bool TryParseTotalsRow(string line, out int[] columns)
    {
        columns = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(line)) return false;

        int colon = line.IndexOf(':');
        if (colon <= 0) return false;
        string label = line[..colon];
        if (label.Contains('\t') || label.Contains('\\') || label.Trim().Length == 0) return false;

        var parts = line[(colon + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 6) return false;

        var numbers = new int[6];
        for (int i = 0; i < 6; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;   // e.g. "*.*", or "0:00:05" in the Times row

        columns = numbers;
        return true;
    }

    /// <summary>
    /// Pulls the full path out of a line about something a mirror would delete from
    /// <paramref name="destinationRoot"/>, and says whether it is a folder.
    ///
    /// <para>Found by where the path points, not by the "*EXTRA" label. In a listing from source to
    /// destination robocopy names every item by its source path - new, newer, older, changed, even
    /// "*MISMATCH" (verified on Windows 11) - except the extras, which exist only at the destination and
    /// so can only be named there. The pair rules keep the two folders from being nested, so a path
    /// inside the destination is an extra, in any language.</para>
    ///
    /// <para>A trailing separator marks a folder; it is removed so files and folders are handled the
    /// same way. Error lines are refused even though they can carry a destination path:
    /// <c>"2026/11/12 13:52:22 ERROR 5 (0x00000005) Accessing Destination Directory C:\dst\sub\"</c> is a
    /// folder robocopy could not open, not one to delete.</para>
    ///
    /// The path is located by searching for the destination root rather than splitting on
    /// whitespace, because robocopy's size column can read "1.7 m" (two tokens).
    /// </summary>
    public static bool TryParseExtraPath(string line, string destinationRoot, out string path, out bool isDirectory)
    {
        path = "";
        isDirectory = false;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(destinationRoot)) return false;
        if (!TryFindPathUnder(line, destinationRoot, out string raw, out int rootLength)) return false;

        string candidate = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Ignore a bare mention of the root itself; we only want things inside it.
        if (candidate.Length <= rootLength) return false;

        path = candidate;
        isDirectory = candidate.Length != raw.Length;
        return true;
    }

    /// <summary>
    /// Pulls the source path out of a line for a file that is about to be copied: a candidate for
    /// overwriting the destination's copy, which would then be lost.
    ///
    /// <para>Deliberately does not depend on the label. Matching "Newer" alone missed "Changed" - same
    /// timestamp, different size - so those files were overwritten with no copy kept, and the labels are
    /// translated on non-English Windows anyway. Whether anything is really overwritten is decided by the
    /// caller, which checks that the destination already has the file. Directory lines are dropped, and
    /// so are the destination's extras, which never lie inside the source.</para>
    ///
    /// Verified against robocopy: <c>"\t    Newer     \t\t      16\tC:\src\shared.txt"</c> and
    /// <c>"\t    Changed   \t\t      27\tC:\src\same-time.txt"</c>.
    /// </summary>
    public static bool TryParseOverwrittenSourcePath(string line, string sourceRoot, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(sourceRoot)) return false;

        // Located by searching for the root rather than splitting on whitespace, because the size
        // column can read "1.7 m" - two tokens.
        if (!TryFindPathUnder(line, sourceRoot, out string raw, out int rootLength)) return false;

        // A trailing separator marks a directory, which has no contents of its own to overwrite.
        if (raw.EndsWith(Path.DirectorySeparatorChar) || raw.EndsWith(Path.AltDirectorySeparatorChar)) return false;
        if (raw.Length <= rootLength) return false;

        path = raw;
        return true;
    }

    /// <summary>robocopy's error code, "(0x00000005)": printed in the same form in every language.</summary>
    private static readonly Regex ErrorCode = new(@"\(0x[0-9A-Fa-f]{8}\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds the path in a robocopy line that lies inside <paramref name="root"/>, returning it untrimmed
    /// of separators (a trailing "\" marks a directory) and the length of the root as matched.
    ///
    /// Matches the root followed by a separator, not the bare root, so "C:\Docs" does not match a line
    /// about "C:\Docs2\file" - a sibling folder whose name happens to start the same way. An error line
    /// never matches. Only the text before the path is checked for the error code, so a file that
    /// happens to be called "(0x00000005).txt" is still found.
    /// </summary>
    private static bool TryFindPathUnder(string line, string root, out string path, out int rootLength)
    {
        path = "";
        string trimmed = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        rootLength = trimmed.Length;
        if (trimmed.Length == 0) return false;

        int idx = line.IndexOf(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        if (ErrorCode.IsMatch(line[..idx])) return false;

        path = line[idx..].Trim();
        return true;
    }
}

/// <summary>
/// Reads the totals table at the end of a robocopy run, whatever language it is printed in.
///
/// <para>The rows always come in the same order - Dirs, Files, Bytes, Times - so the second row of six
/// whole numbers is Files, whether it is labelled "Files :", "Dateien :" or "Fichiers :". Matching the
/// English label meant every report on a non-English PC said nothing was copied.</para>
///
/// Feed it every line; it keeps the Dirs, Files and Bytes rows as the one-line summary.
/// </summary>
public sealed class RobocopyTotals
{
    private readonly StringBuilder _summary = new();
    private int _rowsSeen;          // rows of six whole numbers so far: 1 = Dirs, 2 = Files
    private int _summaryLines = -1; // lines kept since the Dirs row; -1 until it is seen

    public bool FoundFiles { get; private set; }
    public int FilesCopied { get; private set; }
    public int FilesFailed { get; private set; }

    /// <summary>The Dirs, Files and Bytes rows, joined - the same text the old English match kept.</summary>
    public string Summary => _summary.ToString().Trim();

    public void Feed(string line)
    {
        if (_summaryLines is >= 0 and < 3)
        {
            _summary.Append(line.Trim()).Append("  ");
            _summaryLines++;
        }

        if (!RobocopyOutput.TryParseTotalsRow(line, out var columns)) return;

        _rowsSeen++;
        if (_rowsSeen == 1 && _summaryLines < 0)
        {
            // The Dirs row starts the summary; it and the next two lines (Files, Bytes) are kept.
            _summary.Append(line.Trim()).Append("  ");
            _summaryLines = 1;
        }
        else if (_rowsSeen == 2)
        {
            FoundFiles = true;
            FilesCopied = columns[1];
            FilesFailed = columns[4];
        }
    }
}
