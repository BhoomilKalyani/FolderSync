using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace FolderSync;

/// <summary>What a sync did to one file.</summary>
public enum ChangeKind
{
    /// <summary>Copied to a side that did not have it.</summary>
    New,
    /// <summary>Copied over a version the receiving side already had.</summary>
    Updated,
    /// <summary>Removed from the receiving side of a mirror because the sending side no longer has it.</summary>
    Deleted,
}

/// <summary>One file a sync copied, replaced or removed - or, in a preview, would.</summary>
public sealed class FileChange
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChangeKind Kind { get; set; }

    /// <summary>Path relative to the pair's two folders.</summary>
    public string Path { get; set; } = "";

    /// <summary>Bytes, or -1 when not known.</summary>
    public long Size { get; set; } = -1;

    /// <summary>True when the left folder received the change. A two-way pair has changes both ways.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ToLeft { get; set; }

    /// <summary>What was there before - the replaced version, or the deleted file - is in the recycle folder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Kept { get; set; }

    /// <summary>robocopy could not copy it; the receiving side still has whatever it had.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Failed { get; set; }

    /// <summary>What happened, for a finished run.</summary>
    [JsonIgnore]
    public string KindText => Failed ? "Could not be copied" : Kind switch
    {
        ChangeKind.New => "New",
        ChangeKind.Updated => Kept ? "Updated (previous version kept)" : "Updated",
        _ => Kept ? "Deleted (kept in the recycle folder)" : "Deleted",
    };

    /// <summary>What would happen, for a preview.</summary>
    [JsonIgnore]
    public string PlanText => Kind switch
    {
        ChangeKind.New => "Copy (new)",
        ChangeKind.Updated => Kept ? "Replace (previous version kept)" : "Replace",
        _ => Kept ? "Delete (kept in the recycle folder)" : "Delete permanently",
    };

    [JsonIgnore]
    public string SizeText => Size < 0 ? "" : Humanise.Size(Size);
}

public sealed class TargetResult
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>The pair's two folders, so a stored result can say which side a change went to.</summary>
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";

    /// <summary>Identifies which pair this was, so per-pair history survives a rename.</summary>
    public string PairKey { get; set; } = "";
    public int ExitCode { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = "";
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    /// <summary>Files moved aside to the recycle folder instead of being purged by mirror.</summary>
    public int FilesRecycled { get; set; }
    /// <summary>Previous versions kept when a newer copy replaced them.</summary>
    public int FilesBackedUp { get; set; }
    /// <summary>Short explanation when a destination was skipped or failed before copying.</summary>
    public string Note { get; set; } = "";

    /// <summary>Bytes of the files that were copied.</summary>
    public long BytesCopied { get; set; }

    /// <summary>The files that were copied, replaced or removed, up to <see cref="SyncOptions.ChangeLimit"/>.</summary>
    public List<FileChange> Changes { get; set; } = new();

    /// <summary>Changes beyond the limit: counted, not listed.</summary>
    public int ChangesOmitted { get; set; }

    /// <summary>
    /// The large-deletion guard stopped this pair. Unlike any other failure this one can be reviewed and
    /// approved, so it is recorded apart from <see cref="Note"/>, which only explains it.
    /// </summary>
    public bool GuardStopped { get; set; }

    /// <summary>How many files the stopped mirror would have removed.</summary>
    public int GuardFiles { get; set; }

    /// <summary>The run was cancelled while this pair was being processed, so it says nothing about the pair's health.</summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// The pair did not run for a reason that is not a fault — currently only an unmounted drive.
    /// Skipped pairs deliberately do not make the whole run a failure, so an unplugged backup drive
    /// does not raise an alarm every day.
    /// </summary>
    public bool Skipped { get; set; }

    public string ResultText => Skipped ? "Skipped" : Success ? "OK" : "Failed";
}

/// <summary>What a preview found for one pair: every change a sync would make, and whether it would run at all.</summary>
public sealed class PairPlan
{
    public string Name { get; init; } = "";
    public string PairKey { get; init; } = "";
    public string Left { get; init; } = "";
    public string Right { get; init; } = "";

    /// <summary>The pair cannot run (or was skipped); <see cref="Note"/> says why.</summary>
    public bool Blocked { get; set; }
    public string Note { get; set; } = "";

    public List<FileChange> Changes { get; } = new();
    public int ChangesOmitted { get; set; }

    public int FilesToCopy { get; set; }
    public long BytesToCopy { get; set; }
    public int FilesToDelete { get; set; }
    public long BytesToDelete { get; set; }

    /// <summary>A real sync would stop this pair untouched unless the deletion is approved.</summary>
    public bool GuardWouldStop { get; set; }
    public string GuardReason { get; set; } = "";
}

public sealed class SyncResult
{
    public bool Success { get; set; }
    public List<TargetResult> Targets { get; set; } = new();

    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }

    /// <summary>The run was stopped part-way; <see cref="Targets"/> holds what had been done by then.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Filled in by a preview: one entry per pair, in the same order as <see cref="Targets"/>.</summary>
    public List<PairPlan> Plans { get; set; } = new();
}

/// <summary>How a run differs from "sync every enabled pair".</summary>
public sealed class SyncOptions
{
    /// <summary>Preview: work out what would change and change nothing.</summary>
    public bool ListOnly { get; init; }

    /// <summary>Only these pairs (by <see cref="PairHistory.KeyFor"/>); null means every enabled pair.</summary>
    public IReadOnlyCollection<string>? OnlyPairs { get; init; }

    /// <summary>
    /// Pairs whose deletions someone has looked at and approved, with the number of files they approved
    /// removing. The large-deletion guard lets that pair through for this run only - and only if it is
    /// still removing no more than was approved, so an approval cannot be used for a bigger deletion
    /// than the one that was shown.
    /// </summary>
    public IReadOnlyDictionary<string, int>? ApprovedDeletions { get; init; }

    /// <summary>The most changes to list per pair. The rest are counted but not named.</summary>
    public int ChangeLimit { get; init; } = 1000;
}

/// <summary>Where a run has got to, for a progress bar and a status line.</summary>
/// <param name="Pair">1-based position of the pair being processed.</param>
/// <param name="Checking">Working out what has changed; the amount of copying is not known yet.</param>
/// <param name="Fraction">Overall progress from 0 to 1, or null while it cannot be known.</param>
public sealed record SyncProgress(
    int Pair, int PairCount, string PairName, bool ListOnly, bool Checking,
    int FilesDone, int FilesTotal, long BytesTotal, double? Fraction)
{
    public string Text
    {
        get
        {
            string lead = $"{(ListOnly ? "Previewing pair" : "Pair")} {Pair} of {PairCount} · {PairName}";
            if (ListOnly || Checking) return lead + " — checking what has changed…";
            if (FilesTotal <= 0) return lead + " — nothing to copy";
            return $"{lead} — {FilesDone:N0} of {FilesTotal:N0} files"
                 + (BytesTotal > 0 ? $" ({Humanise.Size(BytesTotal)})" : "");
        }
    }
}

/// <summary>
/// Performs the actual folder mirroring by wrapping Windows' built-in robocopy,
/// which handles long paths, retries, multithreading and exact mirroring far more
/// robustly than hand-rolled copy logic.
///
/// Cloud storage is reached as an ordinary folder - Google Drive, OneDrive, iCloud and Dropbox all
/// mount one through their own desktop app - so it needs no separate code path here. See
/// <see cref="CloudFolders"/> for why that route is preferred over talking to a provider's API.
///
/// <para>Every pass starts with a <em>plan</em>: one list-only robocopy run that names each file the
/// pass would copy and, for a mirror, each one it would remove. Everything that has to be decided
/// before anything changes is decided from that one list - whether a deletion is large enough to be a
/// mistake, what to move aside, which files are about to be replaced - and so are the things that are
/// only reported: how much there is to do, and what changed.</para>
/// </summary>
public sealed class SyncEngine
{
    private readonly SyncConfig _config;

    /// <summary>Raised for each line of progress/log output (UI + file logging).</summary>
    public event Action<string>? Log;

    /// <summary>Raised as each pair is checked and as its files are copied.</summary>
    public event Action<SyncProgress>? Progress;

    /// <summary>
    /// The run in progress, or the last one. A cancelled run throws, so this is how its caller reads what
    /// had been done before it stopped.
    /// </summary>
    public SyncResult? Current { get; private set; }

    public SyncEngine(SyncConfig config) => _config = config;

    private void Emit(string line) => Log?.Invoke(line);

    /// <param name="listOnly">Preview mode — works out what would change, makes no changes.</param>
    public Task<SyncResult> RunAsync(CancellationToken ct = default, bool listOnly = false) =>
        RunAsync(new SyncOptions { ListOnly = listOnly }, ct);

    public async Task<SyncResult> RunAsync(SyncOptions options, CancellationToken ct = default)
    {
        var result = new SyncResult { Success = true, Started = DateTime.Now };
        Current = result;
        try
        {
            await RunPairsAsync(options, result, ct);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Success = false;
            throw;
        }
        finally
        {
            result.Finished = DateTime.Now;
        }
        return result;
    }

    private async Task RunPairsAsync(SyncOptions options, SyncResult result, CancellationToken ct)
    {
        bool listOnly = options.ListOnly;
        string verb = listOnly ? "Preview" : "Sync";

        Emit($"===== {verb} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        if (listOnly) Emit("(PREVIEW — no files will be copied or deleted)");

        var pairs = _config.ActivePairs
            .Where(p => options.OnlyPairs is null
                     || options.OnlyPairs.Contains(PairHistory.KeyFor(p.Left, p.Right), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Emit($"Pairs  : {pairs.Count} enabled");

        if (pairs.Count == 0)
        {
            Emit("Nothing to do: no folder pair is enabled.");
            return;
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pair = pairs[i];
            var at = new Position(i + 1, pairs.Count, pair.DisplayName, listOnly);
            Report(at, checking: true);

            Emit("");
            // Plain ASCII: logs get emailed and opened in editors that may not assume UTF-8.
            Emit($"--- {pair.DisplayName}   [{AsciiDirection(pair)} | {pair.ModeText}]");
            Emit($"    left : {pair.Left}");
            Emit($"    right: {pair.Right}");

            var outcome = new TargetResult
            {
                Name = pair.DisplayName,
                Destination = pair.Direction == SyncDirection.ToLeft ? pair.Left : pair.Right,
                Left = pair.Left,
                Right = pair.Right,
                PairKey = PairHistory.KeyFor(pair.Left, pair.Right),
                Success = true,   // starts good; any failing pass below clears it
            };
            var preview = new PairPlan
            {
                Name = outcome.Name,
                PairKey = outcome.PairKey,
                Left = pair.Left,
                Right = pair.Right,
            };
            if (listOnly) result.Plans.Add(preview);

            // An unmounted drive is not a fault - the pair simply waits until the drive is back.
            if (pair.MissingDrive() is { } offline)
            {
                string note = $"drive not connected: {offline}";
                Emit($"    SKIPPED: {note}");
                outcome.Skipped = true;
                outcome.Summary = note;
                outcome.Note = note;
                preview.Blocked = true;
                preview.Note = note;
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
                preview.Blocked = true;
                preview.Note = problem;
                result.Targets.Add(outcome);
                result.Success = false;
                continue;
            }

            try
            {
                var passes = pair.Passes();
                for (int p = 0; p < passes.Count; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    await RunPassAsync(pair, passes[p], at with { Pass = p, PassCount = passes.Count },
                                       outcome, preview, result, options, ct);
                    if (outcome.GuardStopped || _stopPair) break;      // leave this pair completely untouched
                }
            }
            catch (OperationCanceledException)
            {
                // Record what this pair had done before it was stopped, so the run's history is honest.
                outcome.Cancelled = true;
                outcome.Success = false;
                outcome.Note = string.IsNullOrEmpty(outcome.Note) ? "cancelled" : outcome.Note + "; cancelled";
                result.Targets.Add(outcome);
                throw;
            }
            finally
            {
                _stopPair = false;
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
    }

    /// <summary>Set when a pass decides the rest of its pair must not run (a mirror whose deletions could not be listed).</summary>
    private bool _stopPair;

    /// <summary>Where the run is, for progress reports.</summary>
    private readonly record struct Position(int Pair, int PairCount, string Name, bool ListOnly, int Pass = 0, int PassCount = 1);

    private void Report(Position at, bool checking, int done = 0, int total = 0, long bytes = 0)
    {
        if (Progress is null) return;

        double? fraction = null;
        if (!checking && !at.ListOnly)
        {
            double pass = total > 0 ? Math.Min(1.0, (double)done / total) : 1.0;
            fraction = (at.Pair - 1 + (at.Pass + pass) / Math.Max(1, at.PassCount)) / Math.Max(1, at.PairCount);
        }

        Progress.Invoke(new SyncProgress(at.Pair, at.PairCount, at.Name, at.ListOnly, checking,
                                         Math.Min(done, Math.Max(total, 0)), total, bytes, fraction));
    }

    /// <summary>
    /// One direction of one pair: plan it, make the decisions that have to come before any change, then
    /// copy. In a preview it stops after the plan.
    /// </summary>
    private async Task RunPassAsync(SyncPair pair, SyncPass pass, Position at, TargetResult outcome,
                                    PairPlan preview, SyncResult result, SyncOptions options, CancellationToken ct)
    {
        bool listOnly = options.ListOnly;
        bool toLeft = string.Equals(pass.To, pair.Left, StringComparison.OrdinalIgnoreCase);
        if (pair.Direction == SyncDirection.TwoWay)
            Emit($"    pass: {Leaf(pass.From)} -> {Leaf(pass.To)}");

        // The version a copy replaces would otherwise be overwritten and gone, so it is kept alongside
        // the deletions: always for a two-way pair (unless switched off), and for a one-directional
        // pair that asks for it.
        bool keepVersions = KeepsVersions(pair, pass);

        Report(at, checking: true);
        var plan = await PlanAsync(pass, options.ChangeLimit, keepAllReplacing: keepVersions && !listOnly, ct,
                                   listOnly ? line => Emit("    " + line) : null);

        if (listOnly)
        {
            DescribePlan(pass, plan, toLeft, keepVersions, outcome, preview, result, options.ChangeLimit);
            return;
        }

        if (pass.Mirror)
        {
            // Everything the mirror would delete was found by the plan, so nothing has changed yet.
            // Used first to decide whether the deletion looks like a mistake, and then to move the
            // files aside.
            string? stop = null;
            bool guard = false;
            if (plan.Failure is { } listFailure)
                stop = $"could not check what this mirror would delete ({listFailure})";
            else if (StoppedByGuard(pass.To, plan.ExtraFiles, out string guardReason))
            {
                stop = guardReason;
                guard = true;

                if (options.ApprovedDeletions is { } approvals
                    && approvals.TryGetValue(outcome.PairKey, out int approved))
                {
                    if (plan.ExtraFiles <= approved)
                    {
                        Emit($"    APPROVED: the large-deletion guard would have stopped this pair ({guardReason}). "
                           + $"Removing {plan.ExtraFiles:N0} file(s) was reviewed and approved for this run.");
                        stop = null;
                    }
                    else
                    {
                        stop = $"{guardReason}. Removing {approved:N0} file(s) was approved, but it is now "
                             + $"{plan.ExtraFiles:N0}, so the approval was not used";
                    }
                }
            }

            if (stop is not null)
            {
                Emit($"    STOPPED: {stop}");
                Emit("    Nothing was deleted or copied for this pair. Check the sending folder, then sync again"
                   + (guard ? " - or review the deletion in the Folder Sync window and approve it." : "."));
                if (guard)
                {
                    outcome.GuardStopped = true;
                    outcome.GuardFiles = plan.ExtraFiles;
                }
                Fail(outcome, result, stop);
                _stopPair = true;      // leave this pair completely untouched
                return;
            }

            if (_config.RecycleDeletedFiles)
            {
                var (moved, stuck) = await RecycleAsync(plan.Extras.Select(e => e.Path).ToList(), pass.To, ct);
                outcome.FilesRecycled += moved;
                // The copy pass below does not purge while recycling is on, so anything that
                // could not be moved aside simply stays put - but the pair is not in step.
                if (stuck > 0)
                    Fail(outcome, result, $"{stuck} item(s) that the mirror would remove could not be "
                                        + "moved aside, so they were left in place");
            }
        }

        // If the replaced version cannot be kept, the pass is not run: an overwrite is the one change
        // here that nothing can undo.
        if (keepVersions)
        {
            int kept = 0;
            string? backupProblem;
            if (plan.Failure is { } failure)
                backupProblem = $"could not check which files would be overwritten ({failure})";
            else
                (kept, backupProblem) = BackupOverwrites(plan.Copies, pass, twoWay: pass.SkipOlder, ct);

            outcome.FilesBackedUp += kept;
            if (backupProblem is not null)
            {
                Emit($"    STOPPED: {backupProblem}. This direction was not synced, so nothing was overwritten.");
                Fail(outcome, result, backupProblem);
                return;
            }
        }
        else if (plan.Failure is { } unlisted)
        {
            // Nothing here depends on the list for safety: an additive pass deletes nothing and keeps
            // no versions. It only means there is no progress count or change list for this pass.
            Emit($"    NOTE: could not list the changes first ({unlisted}); copying anyway.");
        }

        int total = plan.CopyCount;
        int done = 0;
        long lastReport = 0;
        Report(at, checking: false, 0, total, plan.CopyBytes);

        var tr = await RunRobocopyAsync(pass, ct, () =>
        {
            int now = Interlocked.Increment(ref done);
            // Thousands of small files a second would flood the window with updates.
            long tick = Environment.TickCount64;
            if (tick - Interlocked.Read(ref lastReport) < 120) return;
            Interlocked.Exchange(ref lastReport, tick);
            Report(at, checking: false, now, total, plan.CopyBytes);
        });
        Report(at, checking: false, total, total, plan.CopyBytes);

        outcome.FilesCopied += tr.FilesCopied;
        outcome.FilesFailed += tr.FilesFailed;
        outcome.ExitCode = Math.Max(outcome.ExitCode, tr.ExitCode);
        outcome.Summary = string.IsNullOrEmpty(outcome.Note) ? tr.Summary : outcome.Note;
        if (!tr.Success) outcome.Success = false;

        RecordChanges(pass, plan, toLeft, keepVersions, anyFailed: tr.FilesFailed > 0 || !tr.Success,
                      outcome, options.ChangeLimit);

        // Every pass that writes can leave batches behind - mirror deletions and previous versions
        // alike - so every one of them applies the retention.
        PruneRecycleFolder(pass.To);
    }

    /// <summary>
    /// Whether the version a copy replaces is kept in the recycle folder. A two-way pass follows the
    /// global setting; a one-directional pair chooses for itself. Both need the recycle folder, because
    /// that is where the kept versions go.
    /// </summary>
    private bool KeepsVersions(SyncPair pair, SyncPass pass) =>
        _config.RecycleDeletedFiles && (pass.SkipOlder ? _config.BackupOverwrittenFiles : pair.KeepVersions);

    /// <summary>A preview: turns the plan into the list of changes a sync would make, and changes nothing.</summary>
    private void DescribePlan(SyncPass pass, PassPlan plan, bool toLeft, bool keepVersions,
                              TargetResult outcome, PairPlan preview, SyncResult result, int limit)
    {
        if (plan.Failure is { } failure)
        {
            string why = $"could not list what would change ({failure})";
            Emit($"    FAILED: {why}");
            Fail(outcome, result, why);
            preview.Blocked = true;
            preview.Note = why;
            return;
        }

        foreach (var copy in plan.Copies)
        {
            if (preview.Changes.Count >= limit) { preview.ChangesOmitted++; continue; }
            preview.Changes.Add(new FileChange
            {
                Kind = copy.Replaces ? ChangeKind.Updated : ChangeKind.New,
                Path = copy.Relative,
                Size = copy.Size,
                ToLeft = toLeft,
                Kept = copy.Replaces && keepVersions,
            });
        }
        preview.ChangesOmitted += plan.CopiesOmitted;

        if (pass.Mirror)
        {
            foreach (var extra in plan.Extras)
            {
                if (extra.IsDirectory) continue;      // its files are listed one by one
                if (preview.Changes.Count >= limit) { preview.ChangesOmitted++; continue; }
                preview.Changes.Add(new FileChange
                {
                    Kind = ChangeKind.Deleted,
                    Path = RelativeTo(pass.To, extra.Path),
                    Size = extra.Size,
                    ToLeft = toLeft,
                    Kept = _config.RecycleDeletedFiles,
                });
            }

            preview.FilesToDelete += plan.ExtraFiles;
            preview.BytesToDelete += plan.ExtraBytes;

            if (StoppedByGuard(pass.To, plan.ExtraFiles, out string guardReason))
            {
                preview.GuardWouldStop = true;
                preview.GuardReason = guardReason;
                Emit($"    A sync would STOP here: {guardReason}.");
            }
        }

        preview.FilesToCopy += plan.CopyCount;
        preview.BytesToCopy += plan.CopyBytes;
        outcome.FilesCopied += plan.CopyCount;

        Emit($"    Would copy {plan.CopyCount:N0} file(s) ({Humanise.Size(plan.CopyBytes)})"
           + (pass.Mirror ? $", and remove {plan.ExtraFiles:N0} file(s)" : "") + ".");
    }

    /// <summary>
    /// Turns what the plan listed into the record of what the pass did. When robocopy reports failures
    /// each planned copy is checked against the receiving side, so a file that could not be copied is
    /// recorded as that rather than as copied.
    /// </summary>
    private void RecordChanges(SyncPass pass, PassPlan plan, bool toLeft, bool keepVersions, bool anyFailed,
                               TargetResult outcome, int limit)
    {
        long bytes = plan.CopyBytes;

        foreach (var copy in plan.Copies)
        {
            bool failed = anyFailed && !Landed(copy.SourcePath, System.IO.Path.Combine(pass.To, copy.Relative));
            if (failed && copy.Size > 0) bytes -= copy.Size;

            if (outcome.Changes.Count >= limit) { outcome.ChangesOmitted++; continue; }
            outcome.Changes.Add(new FileChange
            {
                Kind = copy.Replaces ? ChangeKind.Updated : ChangeKind.New,
                Path = copy.Relative,
                Size = copy.Size,
                ToLeft = toLeft,
                Kept = copy.Replaces && keepVersions && !failed,
                Failed = failed,
            });
        }
        outcome.ChangesOmitted += plan.CopiesOmitted;
        outcome.BytesCopied += Math.Max(0, bytes);

        if (!pass.Mirror) return;

        foreach (var extra in plan.Extras)
        {
            if (extra.IsDirectory) continue;                   // its files are listed one by one
            if (SafeFileExists(extra.Path)) continue;          // could not be removed: it is still there

            if (outcome.Changes.Count >= limit) { outcome.ChangesOmitted++; continue; }
            outcome.Changes.Add(new FileChange
            {
                Kind = ChangeKind.Deleted,
                Path = RelativeTo(pass.To, extra.Path),
                Size = extra.Size,
                ToLeft = toLeft,
                Kept = _config.RecycleDeletedFiles,
            });
        }
    }

    /// <summary>
    /// Whether a copy arrived: the receiving file has the sender's size and, within the two seconds
    /// robocopy's /FFT allows, its time. Only consulted when robocopy says something failed.
    /// </summary>
    private static bool Landed(string source, string destination)
    {
        try
        {
            var from = new FileInfo(source);
            var to = new FileInfo(destination);
            if (!to.Exists) return false;
            if (!from.Exists) return true;      // gone from the source since; nothing to compare against
            return from.Length == to.Length
                && Math.Abs((from.LastWriteTimeUtc - to.LastWriteTimeUtc).TotalSeconds) <= 2.5;
        }
        catch { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static string RelativeTo(string root, string path)
    {
        try { return System.IO.Path.GetRelativePath(root, path); }
        catch { return path; }
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
        System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>
    /// The recycle folder inside <paramref name="folder"/>. Uses the validated name, so a hand-edited
    /// "RecycleFolderName": ".." can never point the recycle folder - and its pruning - outside the pair.
    /// </summary>
    private string RecycleRoot(string folder) => System.IO.Path.Combine(folder, _config.SafeRecycleFolderName);

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

    private async Task<TargetResult> RunRobocopyAsync(SyncPass pass, CancellationToken ct, Action? onFileCopied = null)
    {
        string src = pass.From, dst = pass.To;

        // /MIR  = mirror (copy + purge extras) ; /E = copy subdirs incl. empty, never deletes
        // /R:2 /W:2 = 2 retries, 2s wait      ; /MT:16 = 16 threads
        // /FFT  = relaxed (2s) timestamp compare, friendlier to cloud-sync drives
        // /XJ   = skip junctions (avoids loops) ; /NP = no per-file percent (cleaner log)
        // /NDL  = no dir listing
        // /XO   = never overwrite a newer file at the destination. This is what makes a two-way
        //         pair resolve to "newer file wins" when run once in each direction.
        //
        // A mirror with recycling on does not purge here: everything it would delete has already been
        // moved aside, and anything that could not be (a locked file, a name the listing could not
        // represent) must stay rather than be deleted outright.
        bool purge = pass.Mirror && !_config.RecycleDeletedFiles;
        string copyMode = purge ? "/MIR" : "/E";
        string skipOlder = pass.SkipOlder ? " /XO" : "";
        string args = $"{Quote(src)} {Quote(dst)} {copyMode} /R:2 /W:2 /MT:16 /FFT /XJ /NP /NDL{skipOlder}{BuildExclusionArgs(src, dst)}";

        var tr = new TargetResult { Destination = dst };
        // Read by row order, not by the "Files :" label, which is translated on non-English Windows.
        var totals = new RobocopyTotals();

        int exit = await RunRobocopyProcessAsync(args, line =>
        {
            Emit("    " + line);
            lock (totals) totals.Feed(line);
            if (onFileCopied is not null && RobocopyOutput.IsCopiedFileLine(line, dst)) onFileCopied();
        }, ct);

        tr.ExitCode = exit;
        // robocopy: 0-7 = success (8+ = at least one failure). Bit 3 (8) and above are errors.
        tr.Success = exit < 8;
        tr.FilesCopied = totals.FilesCopied;
        tr.FilesFailed = totals.FilesFailed;
        tr.Summary = totals.Summary;
        return tr;
    }

    /// <summary>A file the pass will copy.</summary>
    /// <param name="Relative">Its path inside the pair's folders.</param>
    /// <param name="Replaces">The receiving side already has a file of that name, which this overwrites.</param>
    private readonly record struct PlannedCopy(string SourcePath, string Relative, long Size, bool Replaces);

    /// <summary>Something a mirror would remove from the receiving side.</summary>
    private readonly record struct PlannedExtra(string Path, bool IsDirectory, long Size);

    /// <summary>What one pass would do, from a list-only run made before anything changes.</summary>
    private sealed class PassPlan
    {
        /// <summary>
        /// Files to copy. Complete when versions are being kept (every replaced file is needed, to copy
        /// its old version aside); otherwise only as many as will be listed, since the rest are only
        /// counted - a first sync of a large folder would otherwise hold every path in memory.
        /// </summary>
        public List<PlannedCopy> Copies { get; } = new();
        public int CopiesOmitted { get; set; }
        public int CopyCount { get; set; }
        public long CopyBytes { get; set; }

        /// <summary>
        /// Everything a mirror would remove: files and folders, for moving aside. A folder brings its
        /// contents with it. Empty for a pass that does not mirror.
        /// </summary>
        public List<PlannedExtra> Extras { get; } = new();

        /// <summary>
        /// File count only. robocopy lists an extra folder <em>and</em> each file inside it, so this is
        /// the true number of files at risk — the figure the guard needs, without walking the tree again.
        /// </summary>
        public int ExtraFiles { get; set; }
        public long ExtraBytes { get; set; }

        /// <summary>Why the listing could not be made, or null. A mirror must not run blind.</summary>
        public string? Failure { get; set; }
    }

    /// <summary>
    /// Lists what a pass would do, using a list-only robocopy run with the same selection rules as the
    /// real one, so nothing is changed yet.
    ///
    /// <para>What a mirror would delete (robocopy's "*EXTRA" entries) is recognised by pointing inside
    /// the destination rather than by the label, which is translated on non-English Windows - where
    /// this used to find nothing at all, so the guard never fired and nothing was moved aside. Files to
    /// be copied are the lines that point inside the source; any of them whose name already exists at
    /// the destination replaces something.</para>
    ///
    /// A failed listing used to be logged as a warning and treated as "nothing to delete", which let the
    /// mirror go ahead with no guard and nothing moved aside. It is now reported, and the pair stops.
    /// </summary>
    private async Task<PassPlan> PlanAsync(SyncPass pass, int listLimit, bool keepAllReplacing,
                                           CancellationToken ct, Action<string>? raw)
    {
        string src = pass.From, dst = pass.To;

        // /NJH /NJS strip the header/summary so only the item lines remain.
        // /NDL is essential: without it robocopy prints directory headers followed by bare file
        // names, and the full path needed below is missing for files (directories always show one).
        // /BYTES prints each size as a plain number of bytes rather than "1.7 m".
        // /XO as in the real pass, so a two-way pass lists exactly the files that pass will replace.
        string mode = pass.Mirror ? "/MIR" : "/E";
        string skipOlder = pass.SkipOlder ? " /XO" : "";
        string args = $"{Quote(src)} {Quote(dst)} {mode} /L{skipOlder} /R:0 /W:0 /FFT /XJ /NP /NDL /NJH /NJS /BYTES{BuildExclusionArgs(src, dst)}";

        var plan = new PassPlan();
        var destination = new FolderIndex();

        try
        {
            int exit = await RunRobocopyListAsync(args, line =>
            {
                raw?.Invoke(line);

                if (RobocopyOutput.TryParseExtraPath(line, dst, out var extra, out bool isDirectory, out long extraSize))
                {
                    // A pass that does not mirror lists the extras too, and leaves them alone.
                    if (!pass.Mirror) return;
                    plan.Extras.Add(new PlannedExtra(extra, isDirectory, extraSize));
                    if (!isDirectory)
                    {
                        plan.ExtraFiles++;
                        if (extraSize > 0) plan.ExtraBytes += extraSize;
                    }
                    return;
                }

                if (!RobocopyOutput.TryParseCopyPath(line, src, out var sourcePath, out long size)) return;

                string relative = RelativeTo(src, sourcePath);
                bool replaces = destination.HasFile(System.IO.Path.Combine(dst, relative));

                plan.CopyCount++;
                if (size > 0) plan.CopyBytes += size;

                if ((replaces && keepAllReplacing) || plan.Copies.Count < listLimit)
                    plan.Copies.Add(new PlannedCopy(sourcePath, relative, size, replaces));
                else
                    plan.CopiesOmitted++;
            }, ct);

            // 16 is robocopy's "serious error, nothing done" - the listing is not to be trusted.
            if (exit >= 16) plan.Failure = $"robocopy exit {exit}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PassPlan { Failure = ex.Message };
        }

        return plan;
    }

    /// <summary>
    /// Answers "does the receiving side already have this file?" for a run of paths that arrive folder
    /// by folder, which is how robocopy lists them. Each folder is read once and remembered until the
    /// next one starts, so a thousand new files in one folder cost one directory listing rather than a
    /// thousand lookups - which matters on a network share.
    /// </summary>
    private sealed class FolderIndex
    {
        private string? _folder;
        private HashSet<string>? _names;

        public bool HasFile(string path)
        {
            try
            {
                string? folder = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder)) return File.Exists(path);

                if (!string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
                {
                    _folder = folder;
                    _names = Directory.Exists(folder)
                        ? new HashSet<string>(
                              Directory.EnumerateFiles(folder).Select(f => System.IO.Path.GetFileName(f)),
                              StringComparer.OrdinalIgnoreCase)
                        : null;
                }
                return _names is not null && _names.Contains(System.IO.Path.GetFileName(path));
            }
            catch
            {
                _folder = null;
                _names = null;
                return SafeFileExists(path);
            }
        }
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
    /// Copies each file that the pass is about to overwrite into the recycle folder, so the version it
    /// replaces is recoverable.
    ///
    /// This is what makes a two-way pair safe, and a one-directional pair that asks for it. A changed
    /// file otherwise destroys the copy it replaces with no record, and unlike a deletion there is
    /// nothing left to restore from.
    ///
    /// Copies rather than moves: the file has to stay where it is for robocopy to overwrite it.
    ///
    /// Returns how many were kept, and a problem when some could not be - in which case the caller does
    /// not run the pass, rather than overwrite a file whose previous version is not safe.
    /// </summary>
    private (int kept, string? problem) BackupOverwrites(List<PlannedCopy> copies, SyncPass pass, bool twoWay,
                                                         CancellationToken ct)
    {
        string dst = pass.To;
        var overwritten = copies.Where(c => c.Replaces).ToList();
        if (overwritten.Count == 0) return (0, null);

        // Deliberately a separate batch from the deletions, marked with a suffix. These files were
        // *replaced*, not deleted: putting the old copy back is a rollback that undoes the change,
        // whereas restoring a deletion is a recovery. Filing them together would tell the Recycled
        // files page the wrong story about both.
        string trashRoot = System.IO.Path.Combine(
            RecycleRoot(dst),
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + RecycleStore.ReplacedSuffix);
        int kept = 0, notKept = 0;

        foreach (var copy in overwritten)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string existing = System.IO.Path.Combine(dst, copy.Relative);
                if (!File.Exists(existing)) continue;      // gone since the plan: nothing there to overwrite

                string backup = System.IO.Path.Combine(trashRoot, copy.Relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                File.Copy(existing, backup, overwrite: true);
                kept++;
            }
            catch (Exception ex)
            {
                notKept++;
                Emit($"    WARNING: could not keep the previous version of '{copy.SourcePath}': {ex.Message}");
            }
        }

        if (kept > 0 && twoWay)
        {
            Emit($"    {kept} file(s) changed on both sides; the newer copy wins and the previous "
               + $"version was kept -> {trashRoot}");
            Emit("    (A two-way pair cannot tell a genuine conflict from an ordinary update without a "
               + "record of the previous run, so every replaced file is kept.)");
        }
        else if (kept > 0)
        {
            Emit($"    {kept} file(s) are being replaced by a changed copy; the previous version was kept -> {trashRoot}");
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

        string trashRoot = System.IO.Path.Combine(RecycleRoot(dst),
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

                string dest = System.IO.Path.Combine(trashRoot, System.IO.Path.GetRelativePath(dst, path));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);

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
                var made = RecycleStore.ParseBatchTimestamp(System.IO.Path.GetFileName(dir));
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
        string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"foldersync-list-{Guid.NewGuid():N}.log");
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
            // robocopy writes the OEM code page to a pipe. These lines are only logged and counted, never
            // acted on (see RunRobocopyListAsync for the ones that are), so OEM is right for readable logs.
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
