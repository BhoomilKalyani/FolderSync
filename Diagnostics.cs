using System.Text;

namespace FolderSync;

/// <summary>
/// Checks everything that can silently stop this app from syncing: a missing source, an
/// unavailable destination, a scheduled task whose program file has moved, no disk space,
/// half-configured email, and so on. Each finding is prefixed OK / WARN / FAIL.
/// </summary>
public static class Diagnostics
{
    public static string Run(SyncConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Folder Sync health check - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Machine: {Environment.MachineName}    User: {Environment.UserName}");
        sb.AppendLine(new string('-', 78));

        CheckApp(sb);
        CheckPairs(sb, config);
        CheckSchedules(sb, config);
        CheckLastRun(sb, config);
        CheckNotifications(sb, config);
        CheckEmail(sb, config);
        CheckLogs(sb, config);

        sb.AppendLine(new string('-', 78));
        sb.AppendLine("OK = fine   WARN = worth a look   FAIL = will not work until fixed");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string status, string text) =>
        sb.AppendLine($"[{status,-4}] {text}");

    private static void CheckApp(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("APPLICATION");
        string exe = ScheduleManager.ExePath;
        Line(sb, "OK", $"Running from: {exe}");

        if (ScheduleManager.IsBuildOutputPath(exe))
            Line(sb, "WARN", "This is a build output folder (bin\\Debug or bin\\Release). Schedules created "
                            + "from here break when the project is rebuilt - run the installed copy instead.");
    }

    private static void CheckPairs(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("FOLDER PAIRS");

        var pairs = config.Pairs ?? new List<SyncPair>();
        if (pairs.Count == 0)
        {
            Line(sb, "FAIL", "No folder pairs are configured - a sync would do nothing.");
            return;
        }
        if (!pairs.Any(p => p.Enabled))
            Line(sb, "FAIL", "Every pair is switched off - a sync would do nothing.");

        foreach (var p in pairs)
        {
            string label = $"{p.DisplayName}  [{p.DirectionText} · {p.ModeText}]";
            if (!p.Enabled) { Line(sb, "WARN", $"{label}  (disabled)"); continue; }

            if (p.Validate() is { } invalid)
            {
                Line(sb, "FAIL", $"{label}  {invalid} - would be skipped.");
                continue;
            }

            Line(sb, "OK", label);

            // Each side: reachable? exists? and how much room is left.
            foreach (var (side, path) in new[] { ("left ", p.Left), ("right", p.Right) })
            {
                if (!PathUtil.DriveAvailable(path))
                {
                    // WARN, not FAIL: the engine skips a pair whose drive is absent rather than
                    // failing it, and the health check must not contradict that.
                    Line(sb, "WARN", $"       {side}: drive not connected - this pair will be skipped, "
                                   + $"which is not treated as an error.  {path}");
                    continue;
                }
                if (!Directory.Exists(path))
                {
                    // Only a problem when we need to read from it.
                    bool reads = (side.Trim() == "left" && p.Direction != SyncDirection.ToLeft)
                              || (side.Trim() == "right" && p.Direction != SyncDirection.ToRight);
                    Line(sb, reads ? "FAIL" : "WARN", $"       {side}: does not exist{(reads ? "" : " (will be created)")} {path}");
                    continue;
                }
                Line(sb, "OK", $"       {side}: {path}{FreeSpaceNote(path)}");
            }

            if (p.DeletesExtras && !config.RecycleDeletedFiles)
                Line(sb, "WARN", "       mirrors with the recycle safety net turned off - deletions are permanent.");
        }

        // The guard is the difference between a wrong source folder costing nothing and costing the
        // whole destination, so its state belongs in a health report.
        if (pairs.Any(p => p.Enabled && p.DeletesExtras))
        {
            // Worded to match DeletionGuard.ShouldStop exactly: the share is "or more", the minimum is
            // floored at 1, and a receiving side that cannot be counted stops the mirror too.
            if (config.GuardLargeDeletions)
                Line(sb, "OK", $"Large-deletion guard: on - a mirror stops if it would remove "
                             + $"{Math.Clamp(config.LargeDeletionPercent, 1, 100)}% or more of the receiving side "
                             + $"(and at least {Math.Max(1, config.LargeDeletionMinimumFiles)} files), or if the "
                             + "receiving side cannot be read to check.");
            else
                Line(sb, "WARN", "Large-deletion guard: OFF - a mirror will delete whatever the sending "
                               + "side no longer has, however much that is.");
        }

        if (pairs.Any(p => p.Enabled && p.Direction == SyncDirection.TwoWay))
        {
            if (config.KeepsReplacedVersions)
                Line(sb, "OK", "Two-way overwrites: the replaced version is kept and can be rolled back.");
            else if (config.BackupOverwrittenFiles)
                Line(sb, "WARN", "Two-way overwrites: the replaced version is NOT kept, because "
                               + "RecycleDeletedFiles is off and the kept versions are stored in the recycle folder.");
            else
                Line(sb, "WARN", "Two-way overwrites: the replaced version is NOT kept - the older copy is lost.");
        }

        // A one-directional pair chooses for itself whether to keep the version a changed file replaces.
        var asking = pairs.Where(p => p.Enabled && p.Direction != SyncDirection.TwoWay && p.KeepVersions).ToList();
        if (asking.Count > 0)
        {
            string names = string.Join(", ", asking.Select(p => p.DisplayName));
            if (config.RecycleDeletedFiles)
                Line(sb, "OK", $"Previous versions: kept for {config.RecycleRetentionDays} days when a changed file is replaced ({names}).");
            else
                Line(sb, "WARN", $"Previous versions: asked for by {names}, but NOT kept, because the recycle "
                               + "folder is off and that is where they are stored.");
        }

        var patterns = (config.ExcludeFiles ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var folders = (config.ExcludeFolders ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        Line(sb, "OK", $"Excluded files: {(patterns.Count > 0 ? string.Join(" ", patterns) : "(none)")}");
        Line(sb, "OK", $"Excluded folders: {(folders.Count > 0 ? string.Join(" ", folders) : "(none)")}");
    }

    private static void CheckSchedules(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("SCHEDULES");

        bool any = false;
        foreach (var (task, label) in ScheduleManager.AllTasks())
        {
            // One query per task, read by ScheduleManager's own parser rather than a second copy of it.
            var info = ScheduleManager.Query(task);
            if (!info.Installed)
            {
                Line(sb, "OK", $"{label}: not installed.");
                continue;
            }

            any = true;
            string exe = info.Exe ?? "";

            if (exe.Length > 0 && !File.Exists(exe))
                Line(sb, "FAIL", $"{label}: {info.State}, but its program file is missing - it never runs. "
                                + $"Click Enable again to repair.  ({exe})");
            else if (info.State == ScheduleManager.TaskState.Disabled)
                Line(sb, "WARN", $"{label}: installed but disabled.");
            else
                Line(sb, "OK", $"{label}: enabled -> {exe}");
        }

        // Watching for changes is the one trigger that needs the app itself to be running.
        bool startsWithWindows = StartupRegistration.IsEnabled();
        if (config.SyncOnChange)
        {
            any = true;
            Line(sb, "OK", $"Sync when files change: on, {config.SyncOnChangeDelaySeconds} s after the last change.");
            if (!startsWithWindows)
                Line(sb, "WARN", "       It only works while Folder Sync is running, and Folder Sync is not set to "
                               + "start when you sign in (Settings).");
        }
        else
        {
            Line(sb, "OK", "Sync when files change: off.");
        }
        Line(sb, "OK", $"Start with Windows (in the tray): {(startsWithWindows ? "yes" : "no")}.");

        if (!any)
            Line(sb, "WARN", "No automatic sync is set up - syncs only happen when you click Sync now.");
    }

    private static void CheckLastRun(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("LAST SYNC");

        var last = LastRun.Load();
        if (last is null)
        {
            Line(sb, "WARN", "No sync has been recorded yet.");
            return;
        }

        var age = DateTime.Now - last.When;
        string when = $"{last.When:yyyy-MM-dd HH:mm:ss} ({Humanise.Ago(last.When)})";
        Line(sb, last.Success ? "OK" : "FAIL", $"{when} - {(last.Success ? "succeeded" : "completed with errors")}");

        foreach (var d in last.Destinations)
        {
            // A skipped destination leaves Success true so the run stays green, so testing Success
            // alone would print OK for a pair that never ran.
            string status = d.Skipped ? "SKIP" : d.Success ? "OK" : "FAIL";

            Line(sb, status, d.Skipped
                ? $"       {d.Name}: skipped - {(string.IsNullOrEmpty(d.Note) ? "no detail" : d.Note)}"
                : $"       {d.Name}: {d.FilesCopied} copied, {d.FilesFailed} failed"
                  + (d.FilesRecycled > 0 ? $", {d.FilesRecycled} deletions kept" : "")
                  + (d.FilesBackedUp > 0 ? $", {d.FilesBackedUp} previous versions kept" : "")
                  + (string.IsNullOrEmpty(d.Note) ? "" : $" - {d.Note}"));
        }

        if (age > TimeSpan.FromDays(7))
            Line(sb, "WARN", "The last sync was over a week ago - check the schedules above.");

        // The last run can be recent and a pair still stale: an unplugged drive is skipped every time.
        var statuses = PairHistory.Load();
        foreach (var pair in (config.Pairs ?? new List<SyncPair>()).Where(p => p.Enabled))
        {
            if (!statuses.TryGetValue(PairHistory.KeyFor(pair.Left, pair.Right), out var status)) continue;
            var state = PairAttention.Describe(pair, status, config.OverdueAfterDays, DateTime.Now);
            if (state.Overdue)
                Line(sb, "WARN", $"       {pair.DisplayName}: no successful sync for {config.OverdueAfterDays} days or more "
                               + $"- {state.Headline.ToLowerInvariant()}.");
        }

        Line(sb, "OK", $"Logon/unlock throttle: at most one sync every {config.MinMinutesBetweenAutoSyncs} min.");

        int runs = RunHistoryStore.Load(since: DateTime.Now.AddDays(-30)).Count;
        Line(sb, "OK", $"History: {runs} run(s) in the last 30 days, kept for {config.HistoryRetentionDays} days.");
    }

    private static void CheckNotifications(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("WINDOWS NOTIFICATIONS");

        Line(sb, config.NotifyOnFailure ? "OK" : "WARN",
             config.NotifyOnFailure
                 ? "A sync that fails or is stopped with nobody watching raises a notification."
                 : "Off - a scheduled sync that fails says nothing unless email is set up.");
        Line(sb, "OK", config.OverdueAfterDays > 0
            ? $"A pair with no successful sync for {config.OverdueAfterDays} days raises a warning."
            : "No warning for pairs that have not synced for a long time (switched off).");
    }

    private static void CheckEmail(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("EMAIL NOTIFICATION");

        var e = config.Email;
        if (e is null || !e.Enabled) { Line(sb, "OK", "Disabled."); return; }

        if (string.IsNullOrWhiteSpace(e.EffectiveFrom)) Line(sb, "FAIL", "Enabled but no sender address.");
        else Line(sb, "OK", $"From: {e.EffectiveFrom}");

        if (string.IsNullOrWhiteSpace(e.To)) Line(sb, "FAIL", "Enabled but no recipient address.");
        else Line(sb, "OK", $"To: {e.To}");

        if (!e.HasPassword) Line(sb, "FAIL", "No app password saved - sending will fail.");
        else Line(sb, "OK", "App password is saved (encrypted).");

        Line(sb, "OK", $"SMTP {e.SmtpHost}:{e.SmtpPort}, send {e.Mode}.");
        Line(sb, "OK", "Use 'Send Test Email' to confirm the credentials actually work.");
    }

    private static void CheckLogs(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("LOGS");
        try
        {
            Directory.CreateDirectory(SyncConfig.LogDir);
            string probe = Path.Combine(SyncConfig.LogDir, ".writetest");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            int count = Directory.EnumerateFiles(SyncConfig.LogDir, "*.log").Count();
            Line(sb, "OK", $"{SyncConfig.LogDir} is writable ({count} log file(s), pruned after {config.LogRetentionDays} days).");
        }
        catch (Exception ex)
        {
            Line(sb, "FAIL", $"Cannot write to {SyncConfig.LogDir}: {ex.Message}");
        }
    }

    /// <summary>" — 120 GB free" for a local drive, or empty when it can't be determined.</summary>
    private static string FreeSpaceNote(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null || root.StartsWith(@"\\")) return "";
            return $"   ({Humanise.Size(new DriveInfo(root).AvailableFreeSpace)} free)";
        }
        catch { return ""; }
    }
}
