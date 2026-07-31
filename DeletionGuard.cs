namespace FolderSync;

/// <summary>
/// Decides whether a mirror is about to delete so much that it is more likely a mistake than an
/// intention.
///
/// <para>This is the one genuinely dangerous thing the app does. A mirror makes the receiving folder
/// an exact copy, so if the sending side is momentarily wrong - a drive letter that moved, a folder
/// renamed, a cloud client that hasn't finished starting and is showing an empty tree - the mirror
/// faithfully removes everything at the other end. The recycle folder means the files can be brought
/// back, but it can fill the disk and it is alarming to discover after the fact.</para>
///
/// <para>The decision is a pure function so it can be tested exhaustively without touching a
/// filesystem, which matters for something that exists to prevent data loss.</para>
/// </summary>
public static class DeletionGuard
{
    /// <summary>
    /// Whether to stop rather than delete. <paramref name="reason"/> is filled in when stopping, and
    /// is written to the log and shown to the user.
    /// </summary>
    /// <param name="filesToDelete">Files the mirror would remove from the receiving side.</param>
    /// <param name="filesAtDestination">Files currently at the receiving side.</param>
    /// <param name="minimumFiles">
    /// Never trip below this many files. Without it, clearing 2 files out of 3 reads as 67% and would
    /// block a perfectly ordinary sync of a small folder.
    /// </param>
    /// <param name="percent">Share of the receiving side that, once exceeded, looks like a mistake.</param>
    public static bool ShouldStop(
        int filesToDelete, int filesAtDestination, int minimumFiles, int percent, out string reason)
    {
        reason = "";

        if (filesToDelete <= 0) return false;
        if (filesToDelete < Math.Max(1, minimumFiles)) return false;
        if (filesAtDestination <= 0) return false;

        double share = 100.0 * filesToDelete / filesAtDestination;
        if (share < percent) return false;

        reason = $"this would delete {filesToDelete:N0} of the {filesAtDestination:N0} file(s) on the "
               + $"receiving side ({share:0}%), which is usually a sign that the sending folder is "
               + "wrong or not fully available yet";
        return true;
    }

    /// <summary>
    /// Files currently under <paramref name="folder"/>, ignoring the recycle folder so previously
    /// rescued files never count towards the total.
    ///
    /// Returns 0 when the folder cannot be read, which makes the guard stand down rather than block a
    /// sync over a permissions problem it cannot see past.
    /// </summary>
    public static int CountFiles(string folder, string recycleFolderName)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;

            int count = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (IsInside(file, folder, recycleFolderName)) continue;
                count++;
            }
            return count;
        }
        catch { return 0; }
    }

    /// <summary>True when a path sits under a folder named <paramref name="segment"/>.</summary>
    private static bool IsInside(string path, string root, string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        string relative;
        try { relative = Path.GetRelativePath(root, path); }
        catch { return false; }

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Equals(segment, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
