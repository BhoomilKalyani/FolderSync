using System.Text.Json;

namespace FolderSync;

/// <summary>How one pair is doing right now, in words: what the Home page shows and what an alert says.</summary>
/// <param name="Overdue">It has gone longer without a successful sync than the user said to tolerate.</param>
public sealed record PairState(PairHealth Health, string Headline, string Detail, bool Overdue);

/// <summary>
/// Reads a pair's stored status as a person would want it put: is it fine, does it need a look, has it
/// failed - and how long since it last actually worked.
///
/// <para>One place, used by the Home page, the notifications and the weekly email, so the three cannot
/// describe the same pair differently.</para>
/// </summary>
public static class PairAttention
{
    public static PairState Describe(SyncPair pair, PairStatus? status, int overdueDays, DateTime now)
    {
        if (!pair.Enabled)
            return new PairState(PairHealth.None, "Switched off", "This pair is left out when syncing.", false);

        if (status is null)
            return new PairState(PairHealth.None, "Not synced yet", "It runs with the next sync.", false);

        DateTime? good = status.LastGood;
        string lastGood = good is { } g ? $"Last synced {Humanise.Ago(g, now)}." : "It has never synced successfully.";

        // A pair that has never worked is as old as its first attempt.
        DateTime since = good ?? status.FirstSeen ?? status.When;
        bool overdue = overdueDays > 0 && (now - since).TotalDays >= overdueDays;

        if (status.GuardStopped)
            return new PairState(PairHealth.Warn, "Stopped by the deletion guard",
                                 $"{Sentence(status.Note)} {lastGood}", overdue);

        if (status.Skipped)
            return new PairState(PairHealth.Warn, "Drive not connected", lastGood, overdue);

        if (!status.Success)
            return new PairState(PairHealth.Bad, $"Failed {Humanise.Ago(status.When, now)}",
                                 $"{Sentence(status.Note)} {lastGood}".Trim(), overdue);

        if (overdue)
            return new PairState(PairHealth.Warn, $"Last synced {Humanise.Ago(status.When, now)}",
                                 "Nothing has synced it since. Check the schedule.", true);

        return new PairState(PairHealth.Ok, $"Synced {Humanise.Ago(status.When, now)}",
                             status.FilesCopied > 0
                                 ? $"{Humanise.Count(status.FilesCopied, "file")} copied."
                                 : "Nothing needed copying.",
                             false);
    }

    /// <summary>The one line at the top of the Home page, from every pair's state.</summary>
    public static (PairHealth health, string headline) Overall(IReadOnlyList<SyncPair> pairs, IReadOnlyList<PairState> states)
    {
        if (pairs.Count == 0) return (PairHealth.None, "No folder pairs yet");

        var enabled = states.Where((_, i) => pairs[i].Enabled).ToList();
        if (enabled.Count == 0) return (PairHealth.Warn, "Every folder pair is switched off");

        int bad = enabled.Count(s => s.Health == PairHealth.Bad);
        int warn = enabled.Count(s => s.Health == PairHealth.Warn);
        int ok = enabled.Count(s => s.Health == PairHealth.Ok);
        string of = enabled.Count == 1 ? "The folder pair" : $"of {enabled.Count} folder pairs";

        if (bad > 0)
            return (PairHealth.Bad, enabled.Count == 1 ? "The folder pair failed to sync" : $"{bad} {of} failed to sync");
        if (warn > 0)
            return (PairHealth.Warn, enabled.Count == 1 ? "The folder pair needs attention"
                                                        : $"{warn} {of} {(warn == 1 ? "needs" : "need")} attention");
        if (ok == 0)
            return (PairHealth.None, "Nothing has been synced yet");
        if (ok < enabled.Count)
            return (PairHealth.Ok, $"{ok} {of} in sync; the rest have not run yet");

        return (PairHealth.Ok, enabled.Count == 1 ? "The folder pair is in sync" : $"All {enabled.Count} folder pairs are in sync");
    }

    /// <summary>A note as a sentence: capital first letter, one full stop.</summary>
    public static string Sentence(string? note)
    {
        string text = (note ?? "").Trim().TrimEnd('.');
        if (text.Length == 0) return "";
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }
}

/// <summary>A notification to show.</summary>
public sealed record Alert(string Title, string Text, bool IsError);

/// <summary>What has already been said, so the same thing is not said again every time a sync runs.</summary>
public sealed class AlertMemory
{
    /// <summary>Pair key to when its "overdue" warning was last shown.</summary>
    public Dictionary<string, DateTime> OverdueShown { get; set; } = new();

    public string LastFailure { get; set; } = "";
    public DateTime LastFailureShown { get; set; }

    private static string DefaultPath => Path.Combine(SyncConfig.AppDataDir, "alerts.json");

    public static AlertMemory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AlertMemory();
            var memory = JsonSerializer.Deserialize<AlertMemory>(File.ReadAllText(path)) ?? new AlertMemory();
            memory.OverdueShown ??= new Dictionary<string, DateTime>();
            memory.LastFailure ??= "";
            return memory;
        }
        catch { return new AlertMemory(); }
    }

    /// <summary>Never throws: forgetting what was shown only means saying it once more.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch { /* not critical */ }
    }
}

/// <summary>
/// Decides when to interrupt someone. Two things are worth it: a sync that ran unattended and went
/// wrong, and a pair that has quietly gone without a successful sync for days.
///
/// <para>Both exist because the only signal used to be an email - and with email off, a scheduled run
/// that failed every night said nothing until somebody happened to open the window.</para>
/// </summary>
public static class Alerts
{
    /// <summary>A balloon cannot show more than this; anything longer is cut by Windows mid-word.</summary>
    private const int MaxText = 240;

    /// <summary>How long the same failure stays quiet after it has been shown once.</summary>
    public static readonly TimeSpan RepeatFailureAfter = TimeSpan.FromHours(6);

    /// <summary>How long a pair's overdue warning stays quiet after it has been shown once.</summary>
    public static readonly TimeSpan RepeatOverdueAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// The alert for a finished run, or null when it needs none: it succeeded, it was started by hand
    /// (whoever clicked is already looking at the result), or notifications are off.
    /// </summary>
    public static Alert? ForRun(RunRecord run, SyncConfig config)
    {
        if (!config.NotifyOnFailure) return null;
        if (run.Trigger == RunTrigger.Manual || run.Cancelled) return null;
        if (run.Success) return null;

        var failing = run.Pairs.Where(p => !p.Success && !p.Skipped && !p.Cancelled).ToList();
        if (failing.Count == 0)
        {
            return new Alert("Folder Sync could not sync",
                             Clip(string.IsNullOrWhiteSpace(run.Note) ? "Open Folder Sync for details." : run.Note),
                             IsError: true);
        }

        bool allGuard = failing.All(p => p.GuardStopped);
        var first = failing[0];
        string more = failing.Count > 1 ? $" (and {failing.Count - 1} more)" : "";
        string detail = $"{first.Name}: {PairAttention.Sentence(first.Note)}{more}";

        return allGuard
            ? new Alert("Folder Sync stopped a mirror",
                        Clip($"{detail} Nothing was deleted. Open Folder Sync to review it."), IsError: false)
            : new Alert("Folder Sync finished with errors",
                        Clip($"{detail} Open Folder Sync for details."), IsError: true);
    }

    /// <summary>
    /// True when a failure alert should be shown: it differs from the last one, or enough time has
    /// passed. A pair that fails on every unlock would otherwise raise the same alert twenty times a day.
    /// Records the alert in <paramref name="memory"/> when it returns true.
    /// </summary>
    public static bool ShouldShow(Alert alert, AlertMemory memory, DateTime now)
    {
        string key = alert.Title + "|" + alert.Text;
        if (key == memory.LastFailure && now - memory.LastFailureShown < RepeatFailureAfter) return false;

        memory.LastFailure = key;
        memory.LastFailureShown = now;
        return true;
    }

    /// <summary>
    /// One alert covering every enabled pair that is overdue and has not been mentioned in the last day,
    /// or null. Records what it mentions in <paramref name="memory"/>.
    /// </summary>
    public static Alert? Overdue(IEnumerable<SyncPair> pairs, IReadOnlyDictionary<string, PairStatus> statuses,
                                 SyncConfig config, DateTime now, AlertMemory memory)
    {
        if (config.OverdueAfterDays <= 0) return null;

        var due = new List<(SyncPair pair, PairStatus status, string key)>();
        foreach (var pair in pairs)
        {
            if (!pair.Enabled) continue;
            string key = PairHistory.KeyFor(pair.Left, pair.Right);
            if (!statuses.TryGetValue(key, out var status)) continue;      // never run: nothing is overdue yet
            if (!PairAttention.Describe(pair, status, config.OverdueAfterDays, now).Overdue) continue;
            if (memory.OverdueShown.TryGetValue(key, out var shown) && now - shown < RepeatOverdueAfter) continue;
            due.Add((pair, status, key));
        }
        if (due.Count == 0) return null;

        foreach (var item in due) memory.OverdueShown[item.key] = now;

        var (firstPair, firstStatus, _) = due[0];
        DateTime since = firstStatus.LastGood ?? firstStatus.FirstSeen ?? firstStatus.When;
        string age = Humanise.Age(now - since);
        string why = firstStatus.Skipped ? " (drive not connected)"
                   : firstStatus.GuardStopped ? " (stopped by the deletion guard)"
                   : !firstStatus.Success ? " (the last sync failed)"
                   : "";
        string more = due.Count > 1 ? $" {due.Count - 1} more {(due.Count == 2 ? "pair is" : "pairs are")} overdue too." : "";

        return new Alert("Folder Sync: a backup is overdue",
                         Clip($"{firstPair.DisplayName} has not synced successfully for {age}{why}.{more}"),
                         IsError: false);
    }

    private static string Clip(string text) =>
        text.Length <= MaxText ? text : text[..(MaxText - 1)].TrimEnd() + "…";
}
