using System.Windows;
using Shape = System.Windows.Shapes.Shape;

namespace FolderSync;

/// <summary>One folder pair as the Home page shows it.</summary>
public sealed class PairCard
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Mode { get; init; } = "";

    /// <summary>"C:\Docs  →  G:\My Drive\Docs".</summary>
    public string Route { get; init; } = "";
    public PairHealth Health { get; init; }
    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>Free space on the receiving drive, or empty when it is not known.</summary>
    public string Space { get; init; } = "";

    /// <summary>Only a mirror the deletion guard stopped has something to review.</summary>
    public Visibility ReviewVisibility { get; init; } = Visibility.Collapsed;
}

/// <summary>
/// The Home page: one line saying how things stand, a card per pair, and the last 30 days at a glance -
/// plus the one thing that can be put right from here, a mirror the deletion guard stopped.
/// </summary>
public partial class MainWindow
{
    /// <summary>The last 30 days of runs, newest first. Read off the UI thread; see <see cref="RefreshHomeAsync"/>.</summary>
    private List<RunRecord> _recentRuns = new();

    /// <summary>Pair key to "61.7 GB free on S:".</summary>
    private Dictionary<string, string> _freeSpace = new(StringComparer.OrdinalIgnoreCase);

    private int _homeLoad;

    /// <summary>
    /// Redraws the Home page from what is already in memory plus the small per-pair status file. Cheap
    /// enough to call whenever anything it shows might have changed.
    /// </summary>
    private void RefreshHome()
    {
        if (!_loaded) return;

        DateTime now = DateTime.Now;
        var statuses = PairHistory.Load();
        var pairs = _pairs.ToList();
        var states = pairs
            .Select(p => PairAttention.Describe(p, statuses.GetValueOrDefault(KeyOf(p)), _config.OverdueAfterDays, now))
            .ToList();

        var (health, headline) = PairAttention.Overall(pairs, states);
        HomeHeadline.Text = headline;
        // By reference, not by value, so the dot follows a change of theme.
        HomeDot.SetResourceReference(Shape.FillProperty, health switch
        {
            PairHealth.Ok => "Ok",
            PairHealth.Warn => "Warn",
            PairHealth.Bad => "Bad",
            _ => "ControlBorder",
        });
        HomeSubline.Text = Subline(now);

        HomeEmptyCard.Visibility = pairs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomePairs.ItemsSource = pairs.Select((pair, i) =>
        {
            string key = KeyOf(pair);
            bool stopped = pair.Enabled && statuses.TryGetValue(key, out var status) && status.GuardStopped;
            return new PairCard
            {
                Key = key,
                Name = pair.DisplayName,
                Mode = pair.ModeDetail,
                Route = $"{pair.Left}  {pair.Arrow}  {pair.Right}",
                Health = states[i].Health,
                Headline = states[i].Headline + (states[i].Detail.Length > 0 ? " ·" : ""),
                Detail = states[i].Detail,
                Space = pair.Enabled && _freeSpace.TryGetValue(key, out var free) ? "· " + free : "",
                ReviewVisibility = stopped ? Visibility.Visible : Visibility.Collapsed,
            };
        }).ToList();

        var days = HistorySummary.Days(_recentRuns, now, 30);
        HomeDays.ItemsSource = days;
        HomeMonthText.Text = HistorySummary.TotalsText(days);
    }

    /// <summary>
    /// Reads what the Home page needs from disk - the last 30 days of runs, and the free space on each
    /// receiving drive - off the UI thread, then redraws. Never throws: callers discard the task.
    /// </summary>
    private async Task RefreshHomeAsync()
    {
        int load = ++_homeLoad;
        var pairs = _pairs.ToList();
        try
        {
            DateTime since = DateTime.Now.Date.AddDays(-30);
            var (runs, space) = await Task.Run(() => (RunHistoryStore.Load(since: since), FreeSpaceFor(pairs)));
            if (load != _homeLoad) return;      // a newer load has started; its result is the one to show

            _recentRuns = runs;
            _freeSpace = space;
        }
        catch (Exception ex)
        {
            AppendEvent("[Home] could not read the history: " + ex.Message);
        }
        RefreshHome();
    }

    /// <summary>"Last sync 10 minutes ago (daily schedule) · Next: today at 18:00, at each sign-in or unlock."</summary>
    private string Subline(DateTime now)
    {
        string last;
        if (_recentRuns.FirstOrDefault(r => !r.Cancelled) is { } run)
            last = $"Last sync {Humanise.Ago(run.Finished)} ({run.TriggerText.ToLowerInvariant()})";
        else if (LastRun.Load() is { } before)      // synced by an earlier version, which kept no history
            last = $"Last sync {Humanise.Ago(before.When)}";
        else
            last = "No sync yet";

        return $"{last} · {NextSyncText(now)}.";
    }

    private string NextSyncText(DateTime now)
    {
        var none = ScheduleManager.TaskInfo.Missing;
        var daily = _tasks.GetValueOrDefault(ScheduleManager.DailyTask, none);
        var monthly = _tasks.GetValueOrDefault(ScheduleManager.MonthlyTask, none);
        var logon = _tasks.GetValueOrDefault(ScheduleManager.LogonTask, none);

        var when = new List<string>();
        if (ScheduleManager.NextRun(daily, monthly, now) is { } next) when.Add(DayAndTime(next, now));
        if (logon.State == ScheduleManager.TaskState.Enabled) when.Add("at each sign-in or unlock");
        if (_config.SyncOnChange) when.Add("when files change");

        if (when.Count > 0) return "Next: " + string.Join(", ", when);
        return _schedulesRead.Task.IsCompleted ? "No automatic sync is set up" : "Checking the schedule…";
    }

    private static string DayAndTime(DateTime when, DateTime now)
    {
        string time = when.ToString("HH:mm");
        if (when.Date == now.Date) return $"today at {time}";
        if (when.Date == now.Date.AddDays(1)) return $"tomorrow at {time}";
        return $"on {when:d MMM} at {time}";
    }

    /// <summary>Free space on the drive each pair writes to. Skips network shares and drives that are not there.</summary>
    private static Dictionary<string, string> FreeSpaceFor(IEnumerable<SyncPair> pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            try
            {
                string receiving = pair.Direction == SyncDirection.ToLeft ? pair.Left : pair.Right;
                string? root = Path.GetPathRoot(Path.GetFullPath(receiving));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) continue;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) continue;
                result[KeyOf(pair)] = $"{Humanise.Size(drive.AvailableFreeSpace)} free on {root.TrimEnd('\\')}";
            }
            catch { /* not knowing is fine: the line is simply left out */ }
        }
        return result;
    }

    // ===================== A mirror the deletion guard stopped =====================

    private async void ReviewPair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) await ReviewPairAsync(key);
    }

    /// <summary>After a sync started by hand: the guard stopped a pair, so offer to look at why.</summary>
    private async Task OfferReviewAsync(TargetResult stopped)
    {
        if (!MessageWindow.Confirm(this, $"{stopped.Name} was stopped",
                $"The large-deletion guard stopped this pair: {stopped.Note}.\n\n"
                + "Nothing was deleted or copied for it. You can look at exactly what it would delete, "
                + "and approve it if that is what you meant.",
                primary: "Review…", cancel: "Not now", tone: DialogTone.Warning))
            return;

        await ReviewPairAsync(stopped.PairKey);
    }

    /// <summary>
    /// Shows what a stopped mirror would delete and, if that is approved, runs that one pair with the
    /// guard let through for exactly that many files.
    ///
    /// <para>The list is made afresh rather than taken from the run that was stopped, which may be hours
    /// old. The approval carries the number of files that was shown: if the sync then finds more to
    /// delete than that, the guard stops it again.</para>
    /// </summary>
    private async Task ReviewPairAsync(string key)
    {
        var pair = _pairs.FirstOrDefault(p => KeyOf(p).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (pair is null) return;

        var preview = await RunSyncAsync(listOnly: true, onlyPairs: new[] { key }, showPreview: false);
        var plan = preview?.Plans.FirstOrDefault();
        if (plan is null) return;

        if (plan.Blocked)
        {
            MessageWindow.Warn(this, "This pair cannot run right now", $"{pair.DisplayName}: {plan.Note}.");
            return;
        }

        if (!plan.GuardWouldStop)
        {
            // Whatever tripped the guard has changed - the sending folder is back, most likely.
            if (MessageWindow.Confirm(this, "Nothing to approve any more",
                    $"{pair.DisplayName} would no longer be stopped. A sync would now copy "
                    + $"{Humanise.Count(plan.FilesToCopy, "file")} and remove {Humanise.Count(plan.FilesToDelete, "file")}.\n\n"
                    + "Sync this pair now?",
                    primary: "Sync now", cancel: "Not now"))
                await RunSyncAsync(listOnly: false, onlyPairs: new[] { key });
            return;
        }

        bool toLeft = pair.Direction == SyncDirection.ToLeft;
        string receiving = toLeft ? pair.Left : pair.Right;
        string sending = toLeft ? pair.Right : pair.Left;
        string recycleName = _config.SafeRecycleFolderName;
        int atSource = await Task.Run(() => DeletionGuard.CountFiles(sending, recycleName));

        var deletions = plan.Changes
            .Where(c => c.Kind == ChangeKind.Deleted)
            .Select(c => ChangeRow.From(c, plan.Name, plan.Left, plan.Right, planned: true))
            .ToList();
        int unlisted = plan.FilesToDelete - deletions.Count;

        string fate = _config.RecycleDeletedFiles
            ? $"They would be moved to the recycle folder and kept for {Humanise.Count(_config.RecycleRetentionDays, "day")}, so they can be put back."
            : "They would be deleted for good: the recycle folder is switched off.";
        string sourceNow = atSource == DeletionGuard.Uncountable
            ? "could not be read"
            : $"holds {Humanise.Count(atSource, "file")} right now";

        var dialog = new ChangesWindow(
            "Review deletion",
            $"{pair.DisplayName} would remove {Humanise.Count(plan.FilesToDelete, "file")}",
            $"From {receiving}"
            + (plan.BytesToDelete > 0 ? $" ({Humanise.Size(plan.BytesToDelete)})" : "") + $". {fate}",
            deletions,
            warning: $"The guard stopped this pair because {plan.GuardReason}.\n"
                   + $"The sending folder, {sending}, {sourceNow}. If that looks wrong — an empty or "
                   + "half-loaded folder — cancel, and put the folder right instead.",
            approveLabel: $"Remove {Humanise.Count(plan.FilesToDelete, "file")} and sync",
            footnote: unlisted > 0 ? $"and {unlisted:N0} more that are not listed here" : null)
        { Owner = DialogOwner };

        if (dialog.ShowDialog() != true || !dialog.Approved) return;

        // A sync started by a file change may have begun while the list was being read. The approval is
        // for what was shown, so it is not carried over to whatever the folders look like afterwards.
        if (_cts is not null)
        {
            MessageWindow.Info(this, "A sync is running",
                "Another sync started while you were looking at this list, so nothing was removed. "
                + "Review the pair again when it has finished.");
            return;
        }

        AppendEvent($"[Guard] removing {plan.FilesToDelete} file(s) approved for \"{pair.DisplayName}\".");
        await RunSyncAsync(listOnly: false, onlyPairs: new[] { key },
            approved: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [key] = plan.FilesToDelete });
    }
}
