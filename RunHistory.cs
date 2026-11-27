using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSync;

/// <summary>How something is doing, as the colour it is shown in.</summary>
public enum PairHealth
{
    /// <summary>Nothing to say yet: never run, or switched off.</summary>
    None,
    Ok,
    /// <summary>Worth a look, but not a fault: a drive that is not connected, a mirror waiting for approval.</summary>
    Warn,
    Bad,
}

/// <summary>What started a sync.</summary>
public enum RunTrigger
{
    /// <summary>"Sync now" in the window or the tray menu.</summary>
    Manual,
    Daily,
    Monthly,
    /// <summary>Signing in, or unlocking the screen.</summary>
    Logon,
    /// <summary>A scheduled task registered by a version that did not yet say which one it was.</summary>
    Scheduled,
    /// <summary>A file changed in a watched folder.</summary>
    FileChange,
}

/// <summary>Outcome of one destination in the most recent sync.</summary>
public sealed class DestinationRun
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public int ExitCode { get; set; }
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    public int FilesRecycled { get; set; }
    public int FilesBackedUp { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Short outcome shown in the UI: "OK", "FAILED", or the reason it was skipped.</summary>
    [JsonIgnore]
    public string ResultText => Skipped
        ? (string.IsNullOrWhiteSpace(Note) ? "Skipped" : "Skipped — " + Note)
        : Success ? "OK"
        : string.IsNullOrWhiteSpace(Note) ? "FAILED" : Note;
}

/// <summary>How one folder pair finished, the last time it ran.</summary>
public sealed class PairStatus
{
    public DateTime When { get; set; }
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public int FilesCopied { get; set; }
    public string Note { get; set; } = "";

    /// <summary>
    /// When the pair last ran right through without a fault. Kept apart from <see cref="When"/>, which
    /// moves on every attempt: a drive that has been unplugged for a fortnight has a recent attempt and
    /// a stale backup, and it is the backup's age that matters.
    /// </summary>
    public DateTime? LastSuccess { get; set; }

    /// <summary>When the pair was first seen running, so one that has never succeeded still has an age.</summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>The large-deletion guard stopped the last run, which can be reviewed and approved.</summary>
    public bool GuardStopped { get; set; }

    /// <summary>
    /// The last successful run. Entries written before <see cref="LastSuccess"/> existed have only the
    /// last attempt, which is the success when that attempt succeeded.
    /// </summary>
    [JsonIgnore]
    public DateTime? LastGood => LastSuccess ?? (Success && !Skipped ? When : null);

    /// <summary>
    /// The one-line form shown in the pairs grid, e.g. "OK · 2 hours ago". The point of this column
    /// is that a pair which has been quietly failing for a week is visible at a glance rather than
    /// only discoverable by reading logs.
    /// </summary>
    [JsonIgnore]
    public string Text =>
        $"{(GuardStopped ? "Stopped" : Skipped ? "Not connected" : Success ? "OK" : "Failed")} · {Humanise.Ago(When)}";

    [JsonIgnore]
    public PairHealth Health => GuardStopped || Skipped ? PairHealth.Warn : Success ? PairHealth.Ok : PairHealth.Bad;
}

/// <summary>
/// The last outcome of each folder pair, kept across runs so the grid can show which pairs are
/// healthy. Separate from <see cref="LastRun"/>, which only ever holds the most recent sync as a
/// whole and so cannot say when a particular pair last worked.
/// </summary>
public static class PairHistory
{
    private static string DefaultPath => Path.Combine(SyncConfig.AppDataDir, "pairstatus.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>
    /// Identifies a pair by its two folders rather than its name, so renaming a pair keeps its
    /// history and pointing it at different folders correctly starts afresh.
    /// </summary>
    public static string KeyFor(string left, string right) =>
        (Tidy(left) + "|" + Tidy(right)).ToLowerInvariant();

    private static string Tidy(string path) =>
        (path ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <param name="path">The file to read; the app's own when omitted. Path-taking so it can be tested.</param>
    public static Dictionary<string, PairStatus> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, PairStatus>();
            return JsonSerializer.Deserialize<Dictionary<string, PairStatus>>(File.ReadAllText(path))
                   ?? new Dictionary<string, PairStatus>();
        }
        catch { return new Dictionary<string, PairStatus>(); }
    }

    /// <summary>
    /// Merges a finished run into the stored history. Pairs that did not take part keep whatever they
    /// had, so running one pair does not erase the others' status - and so does a pair that was only
    /// interrupted, which says nothing about whether it works. Never throws.
    /// </summary>
    public static void Record(SyncResult result, string? path = null, DateTime? now = null)
    {
        path ??= DefaultPath;
        DateTime when = now ?? DateTime.Now;
        try
        {
            var all = Load(path);
            foreach (var target in result.Targets)
            {
                if (string.IsNullOrWhiteSpace(target.PairKey) || target.Cancelled) continue;

                all.TryGetValue(target.PairKey, out var before);
                bool good = target.Success && !target.Skipped;

                all[target.PairKey] = new PairStatus
                {
                    When = when,
                    Success = target.Success,
                    Skipped = target.Skipped,
                    FilesCopied = target.FilesCopied,
                    Note = target.Note,
                    LastSuccess = good ? when : before?.LastGood,
                    FirstSeen = before?.FirstSeen ?? before?.When ?? when,
                    GuardStopped = target.GuardStopped,
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Opts));
        }
        catch { /* reporting must never break a sync */ }
    }
}

/// <summary>
/// The last sync's result, persisted so the window can show a summary without re-reading logs.
/// </summary>
public sealed class LastRun
{
    public DateTime When { get; set; }
    public bool Success { get; set; }
    public List<DestinationRun> Destinations { get; set; } = new();

    private static string Path_ => Path.Combine(SyncConfig.AppDataDir, "lastrun.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static LastRun? Load()
    {
        try
        {
            return File.Exists(Path_) ? JsonSerializer.Deserialize<LastRun>(File.ReadAllText(Path_)) : null;
        }
        catch { return null; }
    }

    /// <summary>Records a finished sync. Never throws — reporting must not break a sync.</summary>
    public static void Save(SyncResult result)
    {
        try
        {
            var info = new LastRun
            {
                When = DateTime.Now,
                Success = result.Success,
                Destinations = result.Targets.Select(t => new DestinationRun
                {
                    Name = t.Name,
                    Destination = t.Destination,
                    Success = t.Success,
                    Skipped = t.Skipped,
                    ExitCode = t.ExitCode,
                    FilesCopied = t.FilesCopied,
                    FilesFailed = t.FilesFailed,
                    FilesRecycled = t.FilesRecycled,
                    FilesBackedUp = t.FilesBackedUp,
                    Note = t.Note,
                }).ToList(),
            };
            Directory.CreateDirectory(SyncConfig.AppDataDir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(info, Opts));
        }
        catch { /* not critical */ }
    }
}

/// <summary>What one pair did in one run.</summary>
public sealed class PairRun
{
    public string Name { get; set; } = "";
    public string PairKey { get; set; } = "";
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public bool Cancelled { get; set; }
    public bool GuardStopped { get; set; }
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    public int FilesRecycled { get; set; }
    public int FilesBackedUp { get; set; }
    public long BytesCopied { get; set; }
    public string Note { get; set; } = "";
    public List<FileChange> Changes { get; set; } = new();
    public int ChangesOmitted { get; set; }

    [JsonIgnore]
    public string ResultText =>
        Cancelled ? "Cancelled" : GuardStopped ? "Stopped" : Skipped ? "Skipped" : Success ? "OK" : "Failed";

    [JsonIgnore]
    public PairHealth Health =>
        Cancelled || GuardStopped || Skipped ? PairHealth.Warn : Success ? PairHealth.Ok : PairHealth.Bad;

    [JsonIgnore]
    public string SizeText => BytesCopied > 0 ? Humanise.Size(BytesCopied) : "";
}

/// <summary>
/// One sync, kept so that the history, the dashboard and the weekly summary have more to go on than the
/// latest run - which was all the app remembered before, apart from log files that expire in days.
/// </summary>
public sealed class RunRecord
{
    /// <summary>The record's file name without its extension; unique, and sorts by start time.</summary>
    public string Id { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RunTrigger Trigger { get; set; }

    public bool Success { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>Why a run that never reached the pairs failed, e.g. settings that could not be read.</summary>
    public string Note { get; set; } = "";

    /// <summary>The run's log, as a file name inside the log folder. It may have been pruned since.</summary>
    public string LogFile { get; set; } = "";

    public List<PairRun> Pairs { get; set; } = new();

    public static RunRecord From(SyncResult result, RunTrigger trigger, string? logPath)
    {
        DateTime started = result.Started == default ? DateTime.Now : result.Started;
        return new RunRecord
        {
            Started = started,
            Finished = result.Finished == default ? DateTime.Now : result.Finished,
            Trigger = trigger,
            Success = result.Success,
            Cancelled = result.Cancelled,
            LogFile = string.IsNullOrEmpty(logPath) ? "" : Path.GetFileName(logPath),
            Pairs = result.Targets.Select(t => new PairRun
            {
                Name = t.Name,
                PairKey = t.PairKey,
                Left = t.Left,
                Right = t.Right,
                Success = t.Success,
                Skipped = t.Skipped,
                Cancelled = t.Cancelled,
                GuardStopped = t.GuardStopped,
                FilesCopied = t.FilesCopied,
                FilesFailed = t.FilesFailed,
                FilesRecycled = t.FilesRecycled,
                FilesBackedUp = t.FilesBackedUp,
                BytesCopied = t.BytesCopied,
                Note = t.Note,
                Changes = t.Changes,
                ChangesOmitted = t.ChangesOmitted,
            }).ToList(),
        };
    }

    // ----- what the history list shows -----

    [JsonIgnore] public TimeSpan Duration => Finished > Started ? Finished - Started : TimeSpan.Zero;
    [JsonIgnore] public int FilesCopied => Pairs.Sum(p => p.FilesCopied);
    [JsonIgnore] public int FilesFailed => Pairs.Sum(p => p.FilesFailed);
    [JsonIgnore] public int FilesRecycled => Pairs.Sum(p => p.FilesRecycled);
    [JsonIgnore] public int FilesBackedUp => Pairs.Sum(p => p.FilesBackedUp);
    [JsonIgnore] public long BytesCopied => Pairs.Sum(p => p.BytesCopied);
    [JsonIgnore] public int PairsSkipped => Pairs.Count(p => p.Skipped);

    /// <summary>The run copied, removed or kept something - as opposed to finding everything already in step.</summary>
    [JsonIgnore]
    public bool ChangedSomething => FilesCopied > 0 || FilesRecycled > 0 || FilesBackedUp > 0
                                 || Pairs.Any(p => p.Changes.Count > 0);

    [JsonIgnore]
    public string WhenText => Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string TriggerText => TriggerName(Trigger);

    [JsonIgnore]
    public string DurationText => Humanise.Duration(Duration);

    [JsonIgnore]
    public string ResultText =>
        Cancelled ? "Cancelled"
        : !Success ? (Pairs.Any(p => p.GuardStopped) && Pairs.All(p => p.Success || p.Skipped || p.GuardStopped)
                        ? "Stopped for review"
                        : "Errors")
        : PairsSkipped > 0 ? "OK, some skipped"
        : "OK";

    [JsonIgnore]
    public PairHealth Health =>
        Cancelled ? PairHealth.Warn
        : !Success ? (ResultText == "Stopped for review" ? PairHealth.Warn : PairHealth.Bad)
        : PairsSkipped > 0 ? PairHealth.Warn
        : PairHealth.Ok;

    [JsonIgnore]
    public string SizeText => BytesCopied > 0 ? Humanise.Size(BytesCopied) : "";

    public static string TriggerName(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Manual => "Sync now",
        RunTrigger.Daily => "Daily schedule",
        RunTrigger.Monthly => "Monthly schedule",
        RunTrigger.Logon => "Sign-in or unlock",
        RunTrigger.FileChange => "File change",
        _ => "Schedule",
    };
}

/// <summary>
/// Run records on disk: one small JSON file per run in a "history" folder.
///
/// <para>One file each, rather than one growing file, because the window and a scheduled run are
/// separate processes that both write here. Neither ever rewrites something the other wrote, so there
/// is nothing to lock, and removing old history is deleting files.</para>
/// </summary>
public static class RunHistoryStore
{
    public const string FilePrefix = "run-";
    private const string Stamp = "yyyyMMdd-HHmmss-fff";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static string DefaultFolder => Path.Combine(SyncConfig.AppDataDir, "history");

    /// <summary>
    /// Writes the record and returns its file, or null if it could not be written. Sets
    /// <see cref="RunRecord.Id"/>. Never throws - history must not be able to break a sync.
    ///
    /// The file appears under its final name only once it is complete, so a reader that is watching the
    /// folder never sees half a record.
    /// </summary>
    public static string? Save(RunRecord record, string? folder = null)
    {
        folder ??= DefaultFolder;
        try
        {
            Directory.CreateDirectory(folder);
            string stem = FilePrefix + record.Started.ToString(Stamp, CultureInfo.InvariantCulture);

            for (int n = 1; n <= 100; n++)
            {
                string id = n == 1 ? stem : $"{stem}-{n}";
                string final = Path.Combine(folder, id + ".json");
                if (File.Exists(final)) continue;      // two runs that started in the same millisecond

                record.Id = id;
                string tmp = Path.Combine(folder, $"{id}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(record, Opts), new UTF8Encoding(false));
                    File.Move(tmp, final, overwrite: false);
                    return final;
                }
                catch (IOException) when (File.Exists(final))
                {
                    continue;                          // another process took this name a moment ago
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* a stray .tmp is harmless */ }
                }
            }
        }
        catch { /* not critical */ }
        return null;
    }

    /// <summary>
    /// Every stored run, newest first. A file that cannot be read - half-written by a crash, or edited
    /// by hand - is skipped rather than losing the rest.
    /// </summary>
    /// <param name="since">Skip runs that started before this, judged from the file name without opening it.</param>
    public static List<RunRecord> Load(string? folder = null, DateTime? since = null)
    {
        folder ??= DefaultFolder;
        var runs = new List<RunRecord>();
        try
        {
            if (!Directory.Exists(folder)) return runs;

            foreach (var file in Directory.EnumerateFiles(folder, FilePrefix + "*.json"))
            {
                // Only files named like a record are records, whatever else has been put in the folder.
                if (StartedFromName(file) is not { } started) continue;
                if (since is { } from && started < from) continue;

                var record = Read(file);
                if (record is not null) runs.Add(record);
            }
        }
        catch { /* return what was read */ }

        return runs.OrderByDescending(r => r.Started).ThenByDescending(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>One record by file path, or null when it is missing or unreadable.</summary>
    public static RunRecord? Read(string file)
    {
        try
        {
            var record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file));
            if (record is null) return null;

            record.Id = Path.GetFileNameWithoutExtension(file);
            record.Pairs ??= new List<PairRun>();
            foreach (var pair in record.Pairs) pair.Changes ??= new List<FileChange>();
            return record;
        }
        catch { return null; }
    }

    /// <summary>Deletes records older than <paramref name="days"/>. Only files named like a record are touched.</summary>
    public static int Prune(int days, string? folder = null, DateTime? now = null)
    {
        folder ??= DefaultFolder;
        int removed = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            DateTime cutoff = (now ?? DateTime.Now).AddDays(-Math.Max(1, days));

            foreach (var file in Directory.EnumerateFiles(folder, FilePrefix + "*.json").ToList())
            {
                if (StartedFromName(file) is not { } started || started >= cutoff) continue;
                try { File.Delete(file); removed++; } catch { /* skip what cannot be removed */ }
            }
        }
        catch { /* pruning is best-effort */ }
        return removed;
    }

    /// <summary>When a record's run started, read from its file name; null when the name is not a record's.</summary>
    public static DateTime? StartedFromName(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) return null;

        string rest = name[FilePrefix.Length..];
        if (rest.Length < Stamp.Length) return null;

        return DateTime.TryParseExact(rest[..Stamp.Length], Stamp, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var started)
            ? started
            : null;
    }
}

/// <summary>One calendar day of syncing, for the strip of squares on the Home page.</summary>
public sealed class DaySummary
{
    public DateTime Date { get; init; }
    public int Runs { get; init; }
    public int RunsWithErrors { get; init; }
    public int FilesCopied { get; init; }
    public long BytesCopied { get; init; }
    public PairHealth Health { get; init; }

    public string Tooltip
    {
        get
        {
            string day = Date.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            if (Runs == 0) return $"{day} — no sync";

            return $"{day} — {Humanise.Count(Runs, "sync", "syncs")}"
                 + (RunsWithErrors > 0 ? $", {RunsWithErrors} with errors" : "")
                 + $", {Humanise.Count(FilesCopied, "file")} copied"
                 + (BytesCopied > 0 ? $" ({Humanise.Size(BytesCopied)})" : "");
        }
    }
}

/// <summary>Totals and day-by-day summaries worked out from the stored runs.</summary>
public static class HistorySummary
{
    /// <summary>
    /// The last <paramref name="days"/> days, oldest first, ending today. A day is red when its last run
    /// failed, amber when a run failed or skipped something but the day ended on a good one, green when
    /// every run was clean, and blank when nothing ran.
    /// </summary>
    public static List<DaySummary> Days(IEnumerable<RunRecord> runs, DateTime today, int days = 30)
    {
        var byDay = runs
            .Where(r => !r.Cancelled)
            .GroupBy(r => r.Started.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Started).ToList());

        var result = new List<DaySummary>(days);
        for (int back = days - 1; back >= 0; back--)
        {
            DateTime date = today.Date.AddDays(-back);
            if (!byDay.TryGetValue(date, out var ofDay) || ofDay.Count == 0)
            {
                result.Add(new DaySummary { Date = date, Health = PairHealth.None });
                continue;
            }

            int withErrors = ofDay.Count(r => r.Health == PairHealth.Bad);
            var last = ofDay[^1];
            PairHealth health =
                last.Health == PairHealth.Bad ? PairHealth.Bad
                : ofDay.Any(r => r.Health != PairHealth.Ok) ? PairHealth.Warn
                : PairHealth.Ok;

            result.Add(new DaySummary
            {
                Date = date,
                Runs = ofDay.Count,
                RunsWithErrors = withErrors,
                FilesCopied = ofDay.Sum(r => r.FilesCopied),
                BytesCopied = ofDay.Sum(r => r.BytesCopied),
                Health = health,
            });
        }
        return result;
    }

    /// <summary>"142 syncs · 3 with errors · 1,204 files copied (3.4 GB)".</summary>
    public static string TotalsText(IEnumerable<DaySummary> days)
    {
        var list = days.ToList();
        int runs = list.Sum(d => d.Runs);
        if (runs == 0) return "No sync has been recorded in this period yet.";

        int errors = list.Sum(d => d.RunsWithErrors);
        int files = list.Sum(d => d.FilesCopied);
        long bytes = list.Sum(d => d.BytesCopied);

        return $"{Humanise.Count(runs, "sync", "syncs")} · "
             + (errors == 0 ? "none with errors" : $"{errors:N0} with errors")
             + $" · {Humanise.Count(files, "file")} copied"
             + (bytes > 0 ? $" ({Humanise.Size(bytes)})" : "");
    }
}

/// <summary>The run history as a spreadsheet: one row for each pair of each run.</summary>
public static class HistoryCsv
{
    private static readonly string[] Header =
    {
        "Started", "Finished", "Seconds", "Started by", "Run result", "Pair", "Left folder", "Right folder",
        "Pair result", "Files copied", "Files failed", "Deletions kept", "Previous versions kept",
        "Bytes copied", "Note",
    };

    public static string Build(IEnumerable<RunRecord> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header));

        foreach (var run in runs.OrderBy(r => r.Started))
        {
            string started = run.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string finished = run.Finished.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string seconds = Math.Round(run.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            if (run.Pairs.Count == 0)
            {
                // A run that never reached the pairs still happened, and its note says why.
                Row(sb, started, finished, seconds, run.TriggerText, run.ResultText,
                    "", "", "", "", "0", "0", "0", "0", "0", run.Note);
                continue;
            }

            foreach (var pair in run.Pairs)
                Row(sb, started, finished, seconds, run.TriggerText, run.ResultText,
                    pair.Name, pair.Left, pair.Right, pair.ResultText,
                    Number(pair.FilesCopied), Number(pair.FilesFailed), Number(pair.FilesRecycled),
                    Number(pair.FilesBackedUp), pair.BytesCopied.ToString(CultureInfo.InvariantCulture), pair.Note);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the export to a file. With a byte-order mark, so a spreadsheet reads accented and
    /// non-Latin names as what they are rather than guessing at the encoding.
    /// </summary>
    public static void Save(string path, IEnumerable<RunRecord> runs) =>
        File.WriteAllText(path, Build(runs), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Row(StringBuilder sb, params string[] cells) =>
        sb.AppendLine(string.Join(",", cells.Select(Escape)));

    /// <summary>
    /// Quotes a cell when it has a comma, a quote or a line break, doubling any quotes inside it.
    ///
    /// A cell that a spreadsheet would read as a formula - one starting with =, +, - or @ - gets a
    /// leading apostrophe, so a folder pair named "=cmd|..." is shown as text rather than run. Numbers
    /// written by this class never start with those, so they stay numbers.
    /// </summary>
    public static string Escape(string? cell)
    {
        string text = cell ?? "";
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            text = "'" + text;

        bool quote = text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!quote) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
