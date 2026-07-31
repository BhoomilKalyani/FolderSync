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

        string root = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int idx = line.IndexOf(root, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        string candidate = line[idx..].Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Ignore a bare mention of the root itself; we only want things inside it.
        if (candidate.Length <= root.Length) return false;

        path = candidate;
        return true;
    }
}
