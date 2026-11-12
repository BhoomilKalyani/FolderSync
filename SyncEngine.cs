using System.Diagnostics;
using System.IO;
using System.Text;

namespace FolderSync;

public sealed class TargetResult
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>Identifies which pair this was, so per-pair history survives a rename.</summary>
    public string PairKey { get; set; } = "";
    public int ExitCode { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = "";
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    /// <summary>Files moved aside to the recycle folder instead of being purged by mirror.</summary>
    public int FilesRecycled { get; set; }
    /// <summary>Previous versions kept when a newer copy from the other side replaced them.</summary>
    public int FilesBackedUp { get; set; }
    /// <summary>Short explanation when a destination was skipped or failed before copying.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// The pair did not run for a reason that is not a fault — currently only an unmounted drive.
    /// Skipped pairs deliberately do not make the whole run a failure, so an unplugged backup drive
    /// does not raise an alarm every day.
    /// </summary>
    public bool Skipped { get; set; }

    public string ResultText => Skipped ? "Skipped" : Success ? "OK" : "Failed";
}

public sealed class SyncResult
{
    public bool Success { get; set; }
    public List<TargetResult> Targets { get; set; } = new();
}

/// <summary>
/// Performs the actual folder mirroring by wrapping Windows' built-in robocopy,
/// which handles long paths, retries, multithreading and exact mirroring far more
/// robustly than hand-rolled copy logic.
///
/// Cloud storage is reached as an ordinary folder - Google Drive, OneDrive, iCloud and Dropbox all
/// mount one through their own desktop app - so it needs no separate code path here. See
/// <see cref="CloudFolders"/> for why that route is preferred over talking to a provider's API.
/// </summary>
public sealed class SyncEngine
{
    private readonly SyncConfig _config;

    /// <summary>Raised for each line of progress/log output (UI + file logging).</summary>
    public event Action<string>? Log;

    /// <summary>Raised with a short, human-readable status as each destination is processed.</summary>
    public event Action<string>? Progress;

    public SyncEngine(SyncConfig config) => _config = config;

    private void Emit(string line) => Log?.Invoke(line);

    /// <param name="listOnly">Preview mode — robocopy only lists what would change, makes no changes.</param>
    public async Task<SyncResult> RunAsync(CancellationToken ct = default, bool listOnly = false)
    {
        var result = new SyncResult { Success = true };
        string verb = listOnly ? "Preview" : "Sync";

        Emit($"===== {verb} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        if (listOnly) Emit("(PREVIEW — no files will be copied or deleted)");

        var pairs = _config.ActivePairs.ToList();
        Emit($"Pairs  : {pairs.Count} enabled");

        if (pairs.Count == 0)
        {
            Emit("Nothing to do: no folder pair is enabled.");
            return result;
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pair = pairs[i];
            Progress?.Invoke($"{(listOnly ? "Previewing" : "Syncing")} {i + 1} of {pairs.Count}: {pair.DisplayName}…");

            Emit("");
            // Plain ASCII: logs get emailed and opened in editors that may not assume UTF-8.
            Emit($"--- {pair.DisplayName}   [{AsciiDirection(pair)} | {pair.ModeText}]");
            Emit($"    left : {pair.Left}");
            Emit($"    right: {pair.Right}");

            var outcome = new TargetResult
            {
                Name = pair.DisplayName,
                Destination = pair.Direction == SyncDirection.ToLeft ? pair.Left : pair.Right,
                PairKey = PairHistory.KeyFor(pair.Left, pair.Right),
                Success = true,   // starts good; any failing pass below clears it
            };

            // An unmounted drive is not a fault - the pair simply waits until the drive is back.
            if (pair.MissingDrive() is { } offline)
            {
                string note = $"drive not connected: {offline}";
                Emit($"    SKIPPED: {note}");
                outcome.Skipped = true;
                outcome.Summary = note;
                outcome.Note = note;
                result.Targets.Add(outcome);      // deliberately does not clear result.Success
                continue;
            }

            string? problem = CheckPair(pair, listOnly);
            if (problem is not null)
            {
                Emit($"    FAILED: {problem}");
                outcome.Success = false;
                outcome.Summary = problem;
                outcome.Note = problem;
                result.Targets.Add(outcome);
                result.Success = false;
                continue;
            }

            foreach (var pass in pair.Passes())
            {
                ct.ThrowIfCancellationRequested();
                if (pair.Direction == SyncDirection.TwoWay)
                    Emit($"    pass: {Leaf(pass.From)} -> {Leaf(pass.To)}");

                if (!listOnly && pass.Mirror)
                {
                    // Everything a mirror would delete, found with a list-only pass so nothing has
                    // changed yet. Used first to decide whether the deletion looks like a mistake,
                    // and then to move the files aside.
                    var doomed = await FindDeletionsAsync(pass.From, pass.To, ct);

                    string? stop = doomed.Failure is { } listFailure
                        ? $"could not check what this mirror would delete ({listFailure})"
                        : StoppedByGuard(pass.To, doomed.Files, out string guardReason) ? guardReason : null;

                    if (stop is not null)
                    {
                        Emit($"    STOPPED: {stop}");
                        Emit("    Nothing was deleted or copied for this pair. Check the sending folder, "
                           + "then sync again. (Adjust or switch this off with GuardLargeDeletions in config.json.)");
                        Fail(outcome, result, stop);
                        break;      // leave this pair completely untouched
                    }

                    if (_config.RecycleDeletedFiles)
                    {
                        var (moved, stuck) = await RecycleAsync(doomed.All, pass.To, ct);
                        outcome.FilesRecycled += moved;
                        // The copy pass below does not purge while recycling is on, so anything that
                        // could not be moved aside simply stays put - but the pair is not in step.
                        if (stuck > 0)
                            Fail(outcome, result, $"{stuck} item(s) that the mirror would remove could not be "
                                                + "moved aside, so they were left in place");
                    }
                }

                // Two-way: the copy that loses to a newer version on the other side would otherwise
                // be overwritten and gone, so keep it alongside the deletions. If it cannot be kept,
                // the pass is not run: an overwrite is the one change here that nothing can undo.
                if (!listOnly && pass.SkipOlder && _config.KeepsReplacedVersions)
                {
                    var (kept, backupProblem) = await BackupOverwritesAsync(pass.From, pass.To, ct);
                    outcome.FilesBackedUp += kept;
                    if (backupProblem is not null)
                    {
                        Emit($"    STOPPED: {backupProblem}. This direction was not synced, so nothing was overwritten.");
                        Fail(outcome, result, backupProblem);
                        continue;
                    }
                }

                var tr = await RunRobocopyAsync(pass, ct, listOnly);
                outcome.FilesCopied += tr.FilesCopied;
                outcome.FilesFailed += tr.FilesFailed;
                outcome.ExitCode = Math.Max(outcome.ExitCode, tr.ExitCode);
                outcome.Summary = string.IsNullOrEmpty(outcome.Note) ? tr.Summary : outcome.Note;
                if (!tr.Success) outcome.Success = false;

                // Every pass that writes can leave batches behind - mirror deletions and two-way
                // previous versions alike - so every one of them applies the retention.
                if (!listOnly) PruneRecycleFolder(pass.To);
            }

            result.Targets.Add(outcome);
            if (!outcome.Success) result.Success = false;

            Emit($"--- {pair.DisplayName}: {outcome.ResultText.ToUpperInvariant()} "
               + $"(copied {outcome.FilesCopied}, failed {outcome.FilesFailed}"
               + (outcome.FilesRecycled > 0 ? $", recycled {outcome.FilesRecycled}" : "")
               + (outcome.FilesBackedUp > 0 ? $", previous versions kept {outcome.FilesBackedUp}" : "") + ")");
        }

        int skipped = result.Targets.Count(t => t.Skipped);
        if (skipped > 0)
            Emit($"\n{skipped} pair(s) skipped because a drive was not connected. This is not counted as an error.");

        Emit("");
        Emit($"===== {verb} {(result.Success ? "completed" : "completed WITH ERRORS")} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        return result;
    }

    private static void Fail(TargetResult outcome, SyncResult result, string why)
    {
        outcome.Success = false;
        outcome.Summary = why;
        outcome.Note = string.IsNullOrEmpty(outcome.Note) ? why : outcome.Note + "; " + why;
        result.Success = false;
    }

    private static string AsciiDirection(SyncPair pair) => pair.Direction switch
    {
        SyncDirection.ToRight => "left -> right",
        SyncDirection.ToLeft => "right -> left",
        _ => "two-way",
    };

    /// <summary>Returns why a pair cannot run, or null if it is good to go.</summary>
    private static string? CheckPair(SyncPair pair, bool listOnly)
    {
        // The same rule the pair dialog enforces: same/nested folders are unsafe, the side being
        // read from must exist, and the receiving side needs a reachable drive.
        if (pair.ValidateFolders() is { } invalid) return invalid;

        if (listOnly) return null;

        // The receiving side may legitimately not exist yet.
        foreach (var pass in pair.Passes())
        {
            try { Directory.CreateDirectory(pass.To); }
            catch (Exception ex) { return $"cannot create {pass.To}: {ex.Message}"; }
        }
        return null;
    }

    /// <summary>The last path segment, for readable two-way pass labels.</summary>
    private static string Leaf(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// The recycle folder inside <paramref name="folder"/>. Uses the validated name, so a hand-edited
    /// "RecycleFolderName": ".." can never point the recycle folder - and its pruning - outside the pair.
    /// </summary>
    private string RecycleRoot(string folder) => Path.Combine(folder, _config.SafeRecycleFolderName);

    /// <summary>
    /// Quotes a path for robocopy's command line. A trailing backslash has to be doubled: robocopy reads
    /// \" as a literal quote, so "E:\" swallowed the closing quote and the whole command failed (exit 16)
    /// - a pair whose folder was a drive root could never sync.
    /// </summary>
    public static string Quote(string path) =>
        "\"" + (path.EndsWith('\\') ? path + "\\" : path) + "\"";

    /// <summary>Builds the /XF (file patterns) and /XD (folders) exclusion arguments.</summary>
    private string BuildExclusionArgs(string src, string dst)
    {
        var sb = new StringBuilder();

        var files = (_config.ExcludeFiles ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (files.Count > 0)
            sb.Append(" /XF ").AppendJoin(' ', files.Select(Quote));

        var dirs = (_config.ExcludeFolders ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        // Always keep both recycle folders out of the copy. The receiving side's, or /MIR would purge the
        // backups; the sending side's, or a two-way pair copied each side's recycled files into the other.
        dirs.Add(RecycleRoot(dst));
        dirs.Add(RecycleRoot(src));
        sb.Append(" /XD ").AppendJoin(' ', dirs.Select(Quote));

        return sb.ToString();
    }

    private async Task<TargetResult> RunRobocopyAsync(SyncPass pass, CancellationToken ct, bool listOnly)
    {
        string src = pass.From, dst = pass.To;

        // /MIR  = mirror (copy + purge extras) ; /E = copy subdirs incl. empty, never deletes
        // /R:2 /W:2 = 2 retries, 2s wait      ; /MT:16 = 16 threads
        // /FFT  = relaxed (2s) timestamp compare, friendlier to cloud-sync drives
        // /XJ   = skip junctions (avoids loops) ; /NP = no per-file percent (cleaner log)
        // /NDL  = no dir listing                 ; /L = list only (preview, makes no changes)
        // /XO   = never overwrite a newer file at the destination. This is what makes a two-way
        //         pair resolve to "newer file wins" when run once in each direction.
        //
        // A mirror with recycling on does not purge here: everything it would delete has already been
        // moved aside, and anything that could not be (a locked file, a name the listing could not
        // represent) must stay rather than be deleted outright. Preview still uses /MIR so it lists
        // what the mirror will remove.
        bool purge = pass.Mirror && (listOnly || !_config.RecycleDeletedFiles);
        string copyMode = purge ? "/MIR" : "/E";
        string listFlag = listOnly ? " /L" : "";
        string skipOlder = pass.SkipOlder ? " /XO" : "";
        string args = $"{Quote(src)} {Quote(dst)} {copyMode} /R:2 /W:2 /MT:16 /FFT /XJ /NP /NDL{listFlag}{skipOlder}{BuildExclusionArgs(src, dst)}";

        var tr = new TargetResult { Destination = dst };
        var summary = new StringBuilder();

        int exit = await RunRobocopyProcessAsync(args, line =>
        {
            Emit("    " + line);
            if (line.Contains("Files :") || line.Contains("Dirs :") || line.Contains("Bytes :"))
                summary.Append(line.Trim()).Append("  ");
            if (RobocopyOutput.TryParseFilesSummary(line, out int copied, out int failed))
            {
                tr.FilesCopied = copied;
                tr.FilesFailed = failed;
            }
        }, ct);

        tr.ExitCode = exit;
        // robocopy: 0-7 = success (8+ = at least one failure). Bit 3 (8) and above are errors.
        tr.Success = exit < 8;
        tr.Summary = summary.ToString().Trim();
        return tr;
    }

    /// <summary>Everything a mirror would remove: all entries, plus how many of them are files.</summary>
    /// <param name="All">Files and folders, for moving aside. A folder brings its contents with it.</param>
    /// <param name="Files">
    /// File count only. robocopy lists an extra folder <em>and</em> each file inside it, so this is the
    /// true number of files at risk — the figure the guard needs, without walking the tree again.
    /// </param>
    /// <param name="Failure">Why the listing could not be made, or null. A mirror must not run blind.</param>
    private readonly record struct Deletions(List<string> All, int Files, string? Failure);

    /// <summary>
    /// Finds what a mirror would delete from the destination (robocopy's "*EXTRA" entries),
    /// using a list-only pass so nothing is changed yet.
    ///
    /// A failed listing used to be logged as a warning and treated as "nothing to delete", which let the
    /// mirror go ahead with no guard and nothing moved aside. It is now reported, and the pair stops.
    /// </summary>
    private async Task<Deletions> FindDeletionsAsync(string src, string dst, CancellationToken ct)
    {
        // /NJH /NJS strip the header/summary so only the item lines remain.
        // /NDL is essential: without it robocopy prints directory headers followed by bare file
        // names, and the full path needed below is missing for files (directories always show one).
        string args = $"{Quote(src)} {Quote(dst)} /MIR /L /R:0 /W:0 /FFT /XJ /NP /NDL /NJH /NJS{BuildExclusionArgs(src, dst)}";
        var all = new List<string>();
        int files = 0;

        try
        {
            int exit = await RunRobocopyListAsync(args, line =>
            {
                if (!RobocopyOutput.TryParseExtraPath(line, dst, out var path)) return;
                all.Add(path);
                if (RobocopyOutput.IsExtraFile(line)) files++;
            }, ct);

            // 16 is robocopy's "serious error, nothing done" - the listing is not to be trusted.
            if (exit >= 16) return new Deletions(all, files, $"robocopy exit {exit}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Deletions(new List<string>(), 0, ex.Message);
        }

        return new Deletions(all, files, null);
    }

    /// <summary>
    /// Whether the guard blocks this mirror. Counting the destination is only done when there is
    /// actually something to delete, so an ordinary sync pays nothing for the check.
    /// </summary>
    private bool StoppedByGuard(string dst, int filesToDelete, out string reason)
    {
        reason = "";
        if (!_config.GuardLargeDeletions || filesToDelete <= 0) return false;

        int atDestination = DeletionGuard.CountFiles(dst, _config.SafeRecycleFolderName);
        if (atDestination == DeletionGuard.Uncountable)
        {
            // Not knowing how much is there is no reason to assume deleting it is fine.
            reason = $"this would delete {filesToDelete:N0} file(s), but the receiving side could not be read "
                   + "to check whether that is a sensible share of it";
            return true;
        }

        return DeletionGuard.ShouldStop(
            filesToDelete, atDestination,
            _config.LargeDeletionMinimumFiles, _config.LargeDeletionPercent, out reason);
    }

    /// <summary>
    /// Copies each file that a newer version from the other side is about to overwrite into the
    /// recycle folder, so the losing version is recoverable.
    ///
    /// This is what makes a two-way pair safe. "Newer wins" otherwise means the older copy is
    /// destroyed with no record, and unlike a deletion there is nothing left to restore from.
    ///
    /// Copies rather than moves: the file has to stay where it is for robocopy to overwrite it.
    ///
    /// Returns how many were kept, and a problem when some could not be - in which case the caller does
    /// not run the pass, rather than overwrite a file whose previous version is not safe.
    /// </summary>
    private async Task<(int kept, string? problem)> BackupOverwritesAsync(string src, string dst, CancellationToken ct)
    {
        // Same flags as the real pass, so this sees exactly the files that pass will replace.
        string args = $"{Quote(src)} {Quote(dst)} /E /L /XO /R:0 /W:0 /FFT /XJ /NP /NDL /NJH /NJS{BuildExclusionArgs(src, dst)}";
        var overwritten = new List<string>();

        try
        {
            int exit = await RunRobocopyListAsync(args, line =>
            {
                // Any file about to be copied - "Newer" and "Changed" alike. Whether it really replaces
                // something is decided below, by whether the destination already has it.
                if (RobocopyOutput.TryParseOverwrittenSourcePath(line, src, out var sourcePath))
                    overwritten.Add(sourcePath);
            }, ct);
            if (exit >= 16) return (0, $"could not check which files would be overwritten (robocopy exit {exit})");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (0, $"could not check which files would be overwritten ({ex.Message})");
        }

        if (overwritten.Count == 0) return (0, null);

        // Deliberately a separate batch from the deletions, marked with a suffix. These files were
        // *replaced*, not deleted: putting the old copy back is a rollback that undoes the merge,
        // whereas restoring a deletion is a recovery. Filing them together would tell the Recycled
        // files page the wrong story about both.
        string trashRoot = Path.Combine(
            RecycleRoot(dst),
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + RecycleStore.ReplacedSuffix);
        int kept = 0, notKept = 0;

        foreach (var sourcePath in overwritten)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string relative = Path.GetRelativePath(src, sourcePath);
                string existing = Path.Combine(dst, relative);
                if (!File.Exists(existing)) continue;      // a new file: nothing there to overwrite

                string backup = Path.Combine(trashRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(existing, backup, overwrite: true);
                kept++;
            }
            catch (Exception ex)
            {
                notKept++;
                Emit($"    WARNING: could not keep the previous version of '{sourcePath}': {ex.Message}");
            }
        }

        if (kept > 0)
        {
            Emit($"    {kept} file(s) changed on both sides; the newer copy wins and the previous "
               + $"version was kept -> {trashRoot}");
            Emit("    (A two-way pair cannot tell a genuine conflict from an ordinary update without a "
               + "record of the previous run, so every replaced file is kept.)");
        }
        return notKept > 0
            ? (kept, $"the previous version of {notKept} file(s) could not be kept")
            : (kept, null);
    }

    /// <summary>
    /// Moves everything a mirror is about to purge into "&lt;destination&gt;\_FolderSyncTrash\&lt;timestamp&gt;",
    /// preserving relative paths, so an accidental source deletion stays recoverable.
    /// </summary>
    /// <returns>How many items were moved aside, and how many could not be (those stay in place).</returns>
    private async Task<(int moved, int stuck)> RecycleAsync(List<string> extras, string dst, CancellationToken ct)
    {
        if (extras.Count == 0) return (0, 0);
        await Task.CompletedTask;   // keeps the call site uniform with the other async steps

        string trashRoot = Path.Combine(RecycleRoot(dst),
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        int moved = 0, stuck = 0;

        // Shallowest first: moving a parent folder brings its children along, so deeper
        // entries simply no longer exist by the time we reach them.
        foreach (var path in extras.OrderBy(p => p.Length))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) continue;      // already moved with its parent

                string dest = Path.Combine(trashRoot, Path.GetRelativePath(dst, path));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                if (isDir) Directory.Move(path, dest);
                else File.Move(path, dest, overwrite: true);
                moved++;
            }
            catch (Exception ex)
            {
                stuck++;
                Emit($"    WARNING: could not move aside '{path}' - it was left in place: {ex.Message}");
            }
        }

        if (moved > 0)
            Emit($"    Recycled {moved} item(s) that mirror would have deleted -> {trashRoot}");
        return (moved, stuck);
    }

    /// <summary>
    /// Deletes recycled batches older than the configured retention.
    ///
    /// Only folders whose names are batch timestamps are ever touched, and their age is read from that
    /// name. Anything else in the folder - put there by hand, or the result of a misconfigured recycle
    /// folder name - is left alone: a clean-up must never be able to delete something it did not create.
    /// </summary>
    private void PruneRecycleFolder(string dst)
    {
        try
        {
            string root = RecycleRoot(dst);
            if (!Directory.Exists(root)) return;

            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _config.RecycleRetentionDays));
            foreach (var dir in Directory.EnumerateDirectories(root).ToList())
            {
                var made = RecycleStore.ParseBatchTimestamp(Path.GetFileName(dir));
                if (made == default) continue;              // not one of ours
                try { if (made < cutoff) Directory.Delete(dir, recursive: true); }
                catch { /* skip what we can't remove */ }
            }
            // Tidy up if the recycle folder is now empty.
            try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch { }
        }
        catch { /* pruning is best-effort */ }
    }

    /// <summary>
    /// Runs a list-only robocopy pass whose output is <em>acted on</em>, passing each line to
    /// <paramref name="onLine"/>; returns robocopy's exit code.
    ///
    /// <para>The output goes through a UTF-16 log file (/UNILOG) rather than the console pipe. Console
    /// output is in the OEM code page, which cannot represent every file name: "café.txt" came back
    /// with a replacement character, did not exist under that name, and so was skipped when moving
    /// files aside - after which the mirror deleted it for good. The log file is exact.</para>
    /// </summary>
    private static async Task<int> RunRobocopyListAsync(string args, Action<string> onLine, CancellationToken ct)
    {
        string log = Path.Combine(Path.GetTempPath(), $"foldersync-list-{Guid.NewGuid():N}.log");
        try
        {
            // With /UNILOG the console only reports where the log is, so its lines are not needed.
            int exit = await RunRobocopyProcessAsync($"{args} /UNILOG:{Quote(log)}", _ => { }, ct);

            if (File.Exists(log))
            {
                foreach (var raw in File.ReadLines(log, Encoding.Unicode))
                {
                    var line = raw.TrimEnd();
                    if (line.Length > 0) onLine(line);
                }
            }
            return exit;
        }
        finally
        {
            try { File.Delete(log); } catch { /* a stray temp file is harmless */ }
        }
    }

    /// <summary>Runs robocopy, streaming each output line to <paramref name="onLine"/>; returns its exit code.</summary>
    private static async Task<int> RunRobocopyProcessAsync(string args, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // robocopy writes the OEM code page to a pipe. These lines are only logged, never acted on
            // (see RunRobocopyListAsync for the ones that are), so OEM is right for readable logs.
            StandardOutputEncoding = ConsoleEncoding.Oem,
            StandardErrorEncoding = ConsoleEncoding.Oem,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read both streams to completion (more reliable than the event-based API, which can
        // drop the final summary lines), then await exit. Each stream is read on its own task.
        var stdoutTask = ReadStreamAsync(proc.StandardOutput, onLine);
        var stderrTask = ReadStreamAsync(proc.StandardError, l => onLine("ERR: " + l));

        // If cancelled, stop robocopy rather than leaving it running in the background.
        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { /* already gone */ } }))
        {
            try { await proc.WaitForExitAsync(ct); }
            finally { await Task.WhenAll(stdoutTask, stderrTask); }
        }

        return proc.ExitCode;
    }

    /// <summary>Reads a redirected stream line-by-line, passing each non-blank line to <paramref name="onLine"/>.</summary>
    private static async Task ReadStreamAsync(StreamReader reader, Action<string> onLine)
    {
        string? raw;
        while ((raw = await reader.ReadLineAsync()) is not null)
        {
            var line = raw.TrimEnd();
            if (line.Length > 0) onLine(line);
        }
    }
}
