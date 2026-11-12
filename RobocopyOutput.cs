namespace FolderSync;

/// <summary>
/// Parsers for robocopy's console output. Kept separate from <see cref="SyncEngine"/> so the
/// fiddly bits (which have already caused real bugs) can be unit tested without running robocopy.
/// </summary>
public static class RobocopyOutput
{
    /// <summary>
    /// Reads the "Files :" row of robocopy's summary table:
    /// <c>"   Files :  2201  462  1739  0  0  0"</c> -> copied 462, failed 0.
    /// The columns are Total, Copied, Skipped, Mismatch, FAILED, Extras.
    /// Returns false for the options header (<c>"Files : *.*"</c>), which has no numeric columns.
    /// </summary>
    public static bool TryParseFilesSummary(string line, out int copied, out int failed)
    {
        copied = 0;
        failed = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (!line.TrimStart().StartsWith("Files", StringComparison.OrdinalIgnoreCase)) return false;

        int colon = line.IndexOf(':');
        if (colon < 0) return false;

        var parts = line[(colon + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 6) return false;

        var numbers = new int[6];
        for (int i = 0; i < 6; i++)
            if (!int.TryParse(parts[i], out numbers[i])) return false;   // e.g. "*.*"

        copied = numbers[1];
        failed = numbers[4];
        return true;
    }

    /// <summary>
    /// Pulls the full path out of an "*EXTRA File" / "*EXTRA Dir" line — the items a mirror
    /// would delete from <paramref name="destinationRoot"/>.
    ///
    /// The path is located by searching for the destination root rather than splitting on
    /// whitespace, because robocopy's size column can read "1.7 m" (two tokens). Any trailing
    /// directory separator is removed so files and folders are handled the same way.
    /// </summary>
    public static bool TryParseExtraPath(string line, string destinationRoot, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(destinationRoot)) return false;
        if (!line.Contains("*EXTRA", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryFindPathUnder(line, destinationRoot, out string raw, out int rootLength)) return false;

        string candidate = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Ignore a bare mention of the root itself; we only want things inside it.
        if (candidate.Length <= rootLength) return false;

        path = candidate;
        return true;
    }

    /// <summary>
    /// Finds the path in a robocopy line that lies inside <paramref name="root"/>, returning it untrimmed
    /// of separators (a trailing "\" marks a directory) and the length of the root as matched.
    ///
    /// Matches the root followed by a separator, not the bare root, so "C:\Docs" does not match a line
    /// about "C:\Docs2\file" - a sibling folder whose name happens to start the same way.
    /// </summary>
    private static bool TryFindPathUnder(string line, string root, out string path, out int rootLength)
    {
        path = "";
        string trimmed = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        rootLength = trimmed.Length;
        if (trimmed.Length == 0) return false;

        int idx = line.IndexOf(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        path = line[idx..].Trim();
        return true;
    }

    /// <summary>
    /// True for an "*EXTRA File" line, as opposed to "*EXTRA Dir".
    ///
    /// Worth separating because robocopy lists an extra directory <em>and</em> every file inside it,
    /// so counting only the file lines gives the true number of files a mirror would delete without
    /// having to walk the tree again. Verified against robocopy on Windows 11:
    /// <code>
    /// \t*EXTRA Dir        -1\tC:\dst\extradir\
    /// \t  *EXTRA File \t\t     17\tC:\dst\extradir\a.txt
    /// </code>
    /// </summary>
    public static bool IsExtraFile(string line) =>
        line.Contains("*EXTRA File", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the source path out of a line for a file that is about to be copied: a candidate for
    /// overwriting the destination's copy, which would then be lost.
    ///
    /// <para>Deliberately does not depend on the label. Matching "Newer" alone missed "Changed" - same
    /// timestamp, different size - so those files were overwritten with no copy kept, and the labels are
    /// translated on non-English Windows anyway. Whether anything is really overwritten is decided by the
    /// caller, which checks that the destination already has the file. "New File" lines are dropped here
    /// as a cheap first filter, as are directory lines and the destination's own "*EXTRA" lines.</para>
    ///
    /// Verified against robocopy: <c>"\t    Newer     \t\t      16\tC:\src\shared.txt"</c> and
    /// <c>"\t    Changed   \t\t      27\tC:\src\same-time.txt"</c>.
    /// </summary>
    public static bool TryParseOverwrittenSourcePath(string line, string sourceRoot, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(sourceRoot)) return false;
        if (line.Contains("New File", StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Contains("*EXTRA", StringComparison.OrdinalIgnoreCase)) return false;

        // Located by searching for the root rather than splitting on whitespace, because the size
        // column can read "1.7 m" - two tokens.
        if (!TryFindPathUnder(line, sourceRoot, out string raw, out int rootLength)) return false;

        // A trailing separator marks a directory, which has no contents of its own to overwrite.
        if (raw.EndsWith(Path.DirectorySeparatorChar) || raw.EndsWith(Path.AltDirectorySeparatorChar)) return false;
        if (raw.Length <= rootLength) return false;

        path = raw;
        return true;
    }
}
