using System.Globalization;

namespace FolderSync;

/// <summary>Why a file ended up in the recycle folder, which decides what restoring it means.</summary>
public enum RecycleReason
{
    /// <summary>A mirror was about to delete it because the sending side no longer has it.</summary>
    Deleted,

    /// <summary>
    /// A two-way pair was about to overwrite it with a newer copy from the other side. The file still
    /// exists — this is the version it used to have.
    /// </summary>
    Replaced,
}

/// <summary>One timestamped set of files that a sync moved aside instead of destroying.</summary>
public sealed class RecycleBatch
{
    public string DestinationName { get; init; } = "";
    public string DestinationPath { get; init; } = "";

    /// <summary>Where a restore should put these files back — the other side of the pair.</summary>
    public string RestoreTo { get; init; } = "";
    /// <summary>Full path of the batch folder, e.g. ...\_FolderSyncTrash\20260730-205223.</summary>
    public string Path { get; init; } = "";
    public DateTime When { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }

    public RecycleReason Reason { get; init; } = RecycleReason.Deleted;

    /// <summary>What happened to these files, for the list.</summary>
    public string ReasonText => Reason == RecycleReason.Replaced ? "Replaced by a newer copy" : "Deleted by mirror";

    /// <summary>
    /// What restoring would actually do. Spelled out because the two cases differ in kind: bringing
    /// back a deleted file recovers it, while bringing back a replaced one discards the newer version
    /// the sync just brought in.
    /// </summary>
    public string RestoreMeaning => Reason == RecycleReason.Replaced
        ? "These files still exist — this is the version they had before a newer copy replaced them. "
          + "Restoring rolls them back and the newer content is lost on the next sync."
        : "These files were removed because the other side no longer had them. Restoring puts them back.";

    public string WhenText => When == default
        ? System.IO.Path.GetFileName(Path)
        : When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string SizeText => Humanise.Size(TotalBytes);
}

/// <summary>A single recycled file, with the relative path it originally had.</summary>
public sealed class RecycledFile
{
    public string RelativePath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Length { get; init; }
    public DateTime Modified { get; init; }
    public string SizeText => Humanise.Size(Length);
}

public sealed class RestoreResult
{
    public int Restored { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Reads and restores the files that mirror mode moved into each destination's recycle folder.
/// Restoring puts a file back into the SOURCE, because it was its removal from the source that
/// caused the mirror to clear it from the destinations - so the next sync propagates it again.
/// </summary>
public static class RecycleStore
{
    /// <summary>
    /// Every recycled batch across all pairs, newest first. Only the receiving side of a mirror can
    /// have one, but both sides are checked so batches from an earlier configuration still show up.
    /// </summary>
    public static List<RecycleBatch> GetBatches(SyncConfig config)
    {
        var batches = new List<RecycleBatch>();

        var places = new List<(string name, string folder, string restoreTo)>();
        foreach (var pair in config.Pairs ?? new List<SyncPair>())
        {
            // Restoring puts files back on the opposite side, which is where they were deleted from.
            places.Add(($"{pair.DisplayName} (right)", pair.Right, pair.Left));
            places.Add(($"{pair.DisplayName} (left)", pair.Left, pair.Right));
        }

        foreach (var place in places)
        {
            // A blank or malformed path (a hand-edited config) is skipped rather than allowed to throw,
            // which would lose every other pair's batches along with it.
            if (string.IsNullOrWhiteSpace(place.folder)) continue;
            string root;
            try { root = Path.Combine(place.folder, config.SafeRecycleFolderName); }
            catch { continue; }
            if (!SafeDirectoryExists(root)) continue;

            // Materialised inside the try: enumeration is lazy, so a drive dropping out mid-listing
            // throws from the loop below, not from this call.
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                int count = 0;
                long bytes = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        count++;
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { /* report what we can */ }

                string folderName = Path.GetFileName(dir);
                batches.Add(new RecycleBatch
                {
                    DestinationName = place.name,
                    DestinationPath = place.folder,
                    RestoreTo = place.restoreTo,
                    Path = dir,
                    When = ParseBatchTimestamp(folderName),
                    Reason = ReasonOf(folderName),
                    FileCount = count,
                    TotalBytes = bytes,
                });
            }
        }

        return batches.OrderByDescending(b => b.When).ThenBy(b => b.DestinationName).ToList();
    }

    /// <summary>
    /// Marks a batch as holding replaced versions rather than deletions. A folder-name suffix rather
    /// than a side-file, so the distinction is visible in Explorer too.
    /// </summary>
    public const string ReplacedSuffix = "-replaced";

    /// <summary>Batch folders are named yyyyMMdd-HHmmss, optionally with a suffix.</summary>
    public static DateTime ParseBatchTimestamp(string folderName)
    {
        string stamp = StripSuffix(folderName);
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : default;
    }

    /// <summary>
    /// What a batch holds, from its folder name. Batches written before replaced versions existed have
    /// no suffix and are deletions, which is the correct reading of them.
    /// </summary>
    public static RecycleReason ReasonOf(string folderName) =>
        (folderName ?? "").EndsWith(ReplacedSuffix, StringComparison.OrdinalIgnoreCase)
            ? RecycleReason.Replaced
            : RecycleReason.Deleted;

    private static string StripSuffix(string folderName)
    {
        folderName ??= "";
        return folderName.EndsWith(ReplacedSuffix, StringComparison.OrdinalIgnoreCase)
            ? folderName[..^ReplacedSuffix.Length]
            : folderName;
    }

    /// <summary>Every file inside a batch, with paths relative to the batch root.</summary>
    public static List<RecycledFile> GetFiles(RecycleBatch batch) => GetFiles(batch.Path);

    public static List<RecycledFile> GetFiles(string batchPath)
    {
        var files = new List<RecycledFile>();
        if (!SafeDirectoryExists(batchPath)) return files;

        try
        {
            foreach (var f in Directory.EnumerateFiles(batchPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    files.Add(new RecycledFile
                    {
                        RelativePath = Path.GetRelativePath(batchPath, f),
                        FullPath = f,
                        Length = fi.Length,
                        Modified = fi.LastWriteTime,
                    });
                }
                catch { /* skip unreadable entries */ }
            }
        }
        catch { /* return what we have */ }

        return files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Copies the given recycled files back to <paramref name="restoreRoot"/> (normally the sync
    /// source), recreating their original folder structure. Existing files are skipped unless
    /// <paramref name="overwrite"/> is set. The recycled copies are left in place.
    /// </summary>
    public static RestoreResult Restore(string batchPath, IEnumerable<RecycledFile> files, string restoreRoot, bool overwrite = false)
    {
        var result = new RestoreResult();

        if (string.IsNullOrWhiteSpace(restoreRoot))
        {
            result.Errors.Add("No restore folder was given.");
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                string target = Path.Combine(restoreRoot, file.RelativePath);

                if (File.Exists(target) && !overwrite) { result.Skipped++; continue; }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.FullPath, target, overwrite);
                result.Restored++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{file.RelativePath}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>Permanently deletes a batch folder.</summary>
    public static (bool ok, string msg) DeleteBatch(string batchPath)
    {
        try
        {
            if (Directory.Exists(batchPath)) Directory.Delete(batchPath, recursive: true);
            return (true, "deleted");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path); }
        catch { return false; }
    }
}
