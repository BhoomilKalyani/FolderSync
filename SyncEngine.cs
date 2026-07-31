using System.Diagnostics;
using System.IO;
using System.Text;

namespace FolderSync;

public sealed class TargetResult
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public int ExitCode { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = "";
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    /// <summary>Files moved aside to the recycle folder instead of being purged by mirror.</summary>
    public int FilesRecycled { get; set; }
    /// <summary>Short explanation when a destination was skipped or failed before copying.</summary>
    public string Note { get; set; } = "";
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
                Success = true,   // starts good; any failing pass below clears it
            };

            string? problem = CheckPair(pair, listOnly);
            if (problem is not null)
            {
                Emit($"    SKIPPED: {problem}");
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

                // Mirror safety net: move whatever the purge would delete aside first.
                if (!listOnly && pass.Mirror && _config.RecycleDeletedFiles)
                    outcome.FilesRecycled += await RecycleExtrasAsync(pass.From, pass.To, pass.Mirror, ct);

                var tr = await RunRobocopyAsync(pass, ct, listOnly);
                outcome.FilesCopied += tr.FilesCopied;
                outcome.FilesFailed += tr.FilesFailed;
                outcome.ExitCode = Math.Max(outcome.ExitCode, tr.ExitCode);
                outcome.Summary = tr.Summary;
                if (!tr.Success) outcome.Success = false;

                if (!listOnly && pass.Mirror && _config.RecycleDeletedFiles)
                    PruneRecycleFolder(pass.To);
            }

            result.Targets.Add(outcome);
            if (!outcome.Success) result.Success = false;

            Emit($"--- {pair.DisplayName}: {(outcome.Success ? "OK" : "FAILED")} "
               + $"(copied {outcome.FilesCopied}, failed {outcome.FilesFailed}"
               + (outcome.FilesRecycled > 0 ? $", recycled {outcome.FilesRecycled}" : "") + ")");
        }

        Emit("");
        Emit($"===== {verb} {(result.Success ? "completed" : "completed WITH ERRORS")} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        return result;
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

    private string RecycleRoot(string dst) => Path.Combine(dst, _config.RecycleFolderName);

    /// <summary>Builds the /XF (file patterns) and /XD (folders) exclusion arguments.</summary>
    private string BuildExclusionArgs(string dst)
    {
        var sb = new StringBuilder();

        var files = (_config.ExcludeFiles ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (files.Count > 0)
            sb.Append(" /XF ").AppendJoin(' ', files.Select(f => $"\"{f}\""));

        var dirs = (_config.ExcludeFolders ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        // Always keep the recycle folder out of the mirror, otherwise /MIR would purge the backups.
        dirs.Add(RecycleRoot(dst));
        sb.Append(" /XD ").AppendJoin(' ', dirs.Select(d => $"\"{d}\""));

        return sb.ToString();
    }

    private async Task<TargetResult> RunRobocopyAsync(SyncPass pass, CancellationToken ct, bool listOnly)
    {
        string src = pass.From, dst = pass.To;

        // /MIR  = mirror (copy + purge extras) ; for additive we use /E (copy subdirs incl. empty)
        // /R:2 /W:2 = 2 retries, 2s wait      ; /MT:16 = 16 threads
        // /FFT  = relaxed (2s) timestamp compare, friendlier to cloud-sync drives
        // /XJ   = skip junctions (avoids loops) ; /NP = no per-file percent (cleaner log)
        // /NDL  = no dir listing                 ; /L = list only (preview, makes no changes)
        // /XO   = never overwrite a newer file at the destination. This is what makes a two-way
        //         pair resolve to "newer file wins" when run once in each direction.
        string copyMode = pass.Mirror ? "/MIR" : "/E";
        string listFlag = listOnly ? " /L" : "";
        string skipOlder = pass.SkipOlder ? " /XO" : "";
        string args = $"\"{src}\" \"{dst}\" {copyMode} /R:2 /W:2 /MT:16 /FFT /XJ /NP /NDL{listFlag}{skipOlder}{BuildExclusionArgs(dst)}";

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

    /// <summary>
    /// Finds what a mirror would delete from the destination (robocopy's "*EXTRA" entries),
    /// using a list-only pass so nothing is changed yet.
    /// </summary>
    private async Task<List<string>> ListExtrasAsync(string src, string dst, CancellationToken ct)
    {
        // /NJH /NJS strip the header/summary so only the item lines remain.
        // /NDL is essential: without it robocopy prints directory headers followed by bare file
        // names, and the full path needed below is missing for files (directories always show one).
        string args = $"\"{src}\" \"{dst}\" /MIR /L /R:0 /W:0 /FFT /XJ /NP /NDL /NJH /NJS{BuildExclusionArgs(dst)}";
        var extras = new List<string>();

        await RunRobocopyProcessAsync(args, line =>
        {
            if (RobocopyOutput.TryParseExtraPath(line, dst, out var path)) extras.Add(path);
        }, ct);

        return extras;
    }

    /// <summary>
    /// Moves everything a mirror is about to purge into "&lt;destination&gt;\_FolderSyncTrash\&lt;timestamp&gt;",
    /// preserving relative paths, so an accidental source deletion stays recoverable.
    /// </summary>
    private async Task<int> RecycleExtrasAsync(string src, string dst, bool mirror, CancellationToken ct)
    {
        List<string> extras;
        try { extras = await ListExtrasAsync(src, dst, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit($"    WARNING: could not check for deletions: {ex.Message}"); return 0; }

        if (extras.Count == 0) return 0;

        string trashRoot = Path.Combine(RecycleRoot(dst), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        int moved = 0;

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
                Emit($"    WARNING: could not move aside '{path}': {ex.Message}");
            }
        }

        if (moved > 0)
            Emit($"    Recycled {moved} item(s) that mirror would have deleted -> {trashRoot}");
        return moved;
    }

    /// <summary>Deletes recycled batches older than the configured retention.</summary>
    private void PruneRecycleFolder(string dst)
    {
        try
        {
            string root = RecycleRoot(dst);
            if (!Directory.Exists(root)) return;

            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _config.RecycleRetentionDays));
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try { if (Directory.GetCreationTime(dir) < cutoff) Directory.Delete(dir, recursive: true); }
                catch { /* skip what we can't remove */ }
            }
            // Tidy up if the recycle folder is now empty.
            try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch { }
        }
        catch { /* pruning is best-effort */ }
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
