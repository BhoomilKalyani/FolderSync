using System.Globalization;
using System.Text;

namespace FolderSync;

/// <summary>
/// The weekly summary email: one message a week saying what ran, what failed and which pairs are
/// stale, instead of one message after every scheduled sync.
///
/// <para>A sync that runs at every unlock can send twenty near-identical "OK" emails a day, which
/// trains the reader to stop opening them - and then the one that matters is missed too.</para>
/// </summary>
public static class WeeklyDigest
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    /// <summary>After a failed attempt, how long to wait before trying to send again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    private static string DefaultStatePath => Path.Combine(SyncConfig.AppDataDir, "lastdigest.txt");

    /// <summary>When the last summary went out, and when sending was last tried.</summary>
    public readonly record struct State(DateTime? Sent, DateTime? Tried);

    public static State ReadState(string? path = null)
    {
        path ??= DefaultStatePath;
        try
        {
            if (!File.Exists(path)) return default;
            var lines = File.ReadAllLines(path);
            return new State(Parse(lines.ElementAtOrDefault(0)), Parse(lines.ElementAtOrDefault(1)));
        }
        catch { return default; }

        static DateTime? Parse(string? text) =>
            DateTime.TryParse(text?.Trim(), null, DateTimeStyles.RoundtripKind, out var when) ? when : null;
    }

    public static void WriteState(State state, string? path = null)
    {
        path ??= DefaultStatePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllLines(path, new[]
            {
                state.Sent?.ToString("o") ?? "",
                state.Tried?.ToString("o") ?? "",
            });
        }
        catch { /* not critical: at worst a summary goes out a little early or late */ }
    }

    /// <summary>
    /// Whether a summary should be sent now: a week since the last one, and not within a few hours of a
    /// failed attempt. With no record at all there is nothing to summarise yet - the caller starts the
    /// clock instead, so the first summary arrives a week after weekly mode is switched on.
    /// </summary>
    public static bool IsDue(State state, DateTime now)
    {
        if (state.Sent is not { } sent) return false;
        if (now - sent < Interval) return false;
        if (state.Tried is { } tried && tried > sent && now - tried < RetryAfter) return false;
        return true;
    }

    /// <summary>
    /// Sends the summary if weekly mode is on and one is due. Returns a line for the events log, or null
    /// when there was nothing to do. Never throws.
    /// </summary>
    /// <param name="send">Sends the message; the real mailer when omitted. Replaceable so it can be tested.</param>
    public static string? SendIfDue(SyncConfig config, DateTime now,
                                    Action<EmailSettings, string, string>? send = null,
                                    string? statePath = null, string? historyFolder = null, string? statusPath = null)
    {
        try
        {
            var email = config.Email;
            if (email is null || !email.Enabled || email.Mode != EmailMode.Weekly) return null;

            var state = ReadState(statePath);
            if (state.Sent is null)
            {
                WriteState(new State(now, null), statePath);      // starts the week; nothing to report yet
                return null;
            }
            if (!IsDue(state, now)) return null;

            DateTime from = state.Sent.Value;
            var runs = RunHistoryStore.Load(historyFolder, since: from).Where(r => r.Started >= from).ToList();
            var (subject, body) = Build(runs, config.Pairs ?? new List<SyncPair>(), PairHistory.Load(statusPath),
                                        from, now, Environment.MachineName, config.OverdueAfterDays);
            try
            {
                (send ?? ((s, subj, text) => Emailer.Send(s, subj, text)))(email, subject, body);
                WriteState(new State(now, now), statePath);
                return $"[Email] weekly summary sent to {email.To} ({runs.Count} run(s)).";
            }
            catch (Exception ex)
            {
                WriteState(new State(state.Sent, now), statePath);
                return $"[Email] weekly summary FAILED to send: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            return $"[Email] weekly summary could not be prepared: {ex.Message}";
        }
    }

    /// <summary>The subject and body, from the runs of the period and each pair's current state.</summary>
    public static (string subject, string body) Build(
        IReadOnlyList<RunRecord> runs, IReadOnlyList<SyncPair> pairs,
        IReadOnlyDictionary<string, PairStatus> statuses,
        DateTime from, DateTime to, string machine, int overdueDays)
    {
        var counted = runs.Where(r => !r.Cancelled).OrderBy(r => r.Started).ToList();
        var failed = counted.Where(r => r.Health == PairHealth.Bad).ToList();
        int files = counted.Sum(r => r.FilesCopied);
        long bytes = counted.Sum(r => r.BytesCopied);
        int recycled = counted.Sum(r => r.FilesRecycled);
        int kept = counted.Sum(r => r.FilesBackedUp);

        var states = pairs.Select(p =>
        {
            statuses.TryGetValue(PairHistory.KeyFor(p.Left, p.Right), out var status);
            return (pair: p, status, state: PairAttention.Describe(p, status, overdueDays, to));
        }).ToList();
        int needLook = states.Count(s => s.pair.Enabled && s.state.Health is PairHealth.Warn or PairHealth.Bad);

        string verdict = failed.Count > 0 ? $"{failed.Count} with errors"
                       : needLook > 0 ? $"{Humanise.Count(needLook, "pair")} to check"
                       : "all OK";
        string subject = $"[Folder Sync] Weekly summary — {Humanise.Count(counted.Count, "sync", "syncs")}, {verdict}";

        var sb = new StringBuilder();
        sb.AppendLine($"Folder Sync weekly summary for {machine}");
        sb.AppendLine($"{from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        sb.AppendLine($"Syncs run         : {counted.Count}{ByTrigger(counted)}");
        sb.AppendLine($"With errors       : {failed.Count}");
        sb.AppendLine($"Files copied      : {files:N0}{(bytes > 0 ? $"  ({Humanise.Size(bytes)})" : "")}");
        if (recycled > 0) sb.AppendLine($"Deletions kept    : {recycled:N0}");
        if (kept > 0) sb.AppendLine($"Previous versions : {kept:N0}");
        sb.AppendLine();

        sb.AppendLine("Folder pairs:");
        if (states.Count == 0) sb.AppendLine("  (none set up)");
        foreach (var (pair, status, state) in states)
        {
            string tag = !pair.Enabled ? "OFF "
                       : state.Health switch { PairHealth.Ok => "OK  ", PairHealth.Bad => "FAIL", PairHealth.Warn => "WARN", _ => "--  " };
            sb.AppendLine($"  [{tag}] {pair.DisplayName} — {state.Headline}");

            string? lastGood = status?.LastGood is { } g ? $"last synced {g:yyyy-MM-dd HH:mm}" : null;
            if (pair.Enabled && state.Health is PairHealth.Warn or PairHealth.Bad)
            {
                sb.AppendLine($"         {state.Detail}");
                if (lastGood is not null) sb.AppendLine($"         ({lastGood})");
                if (state.Overdue) sb.AppendLine($"         No successful sync for {overdueDays} days or more.");
            }
        }
        sb.AppendLine();

        if (failed.Count > 0)
        {
            sb.AppendLine("Runs with errors:");
            foreach (var run in failed.Take(20))
            {
                var bad = run.Pairs.Where(p => !p.Success && !p.Skipped).ToList();
                string what = bad.Count == 0
                    ? (string.IsNullOrWhiteSpace(run.Note) ? "no detail" : run.Note)
                    : string.Join("; ", bad.Select(p => $"{p.Name}: {(string.IsNullOrWhiteSpace(p.Note) ? "failed" : p.Note)}"));
                sb.AppendLine($"  {run.Started:yyyy-MM-dd HH:mm}  {run.TriggerText} — {what}");
            }
            if (failed.Count > 20) sb.AppendLine($"  … and {failed.Count - 20} more.");
            sb.AppendLine();
        }

        sb.AppendLine("Every run is listed on the History page in Folder Sync.");
        return (subject, sb.ToString());
    }

    /// <summary>"  (5 daily schedule, 6 sign-in or unlock, 1 sync now)", or empty when nothing ran.</summary>
    private static string ByTrigger(IReadOnlyList<RunRecord> runs)
    {
        if (runs.Count == 0) return "";
        var parts = runs.GroupBy(r => r.Trigger)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Count()} {RunRecord.TriggerName(g.Key).ToLowerInvariant()}");
        return "  (" + string.Join(", ", parts) + ")";
    }
}
