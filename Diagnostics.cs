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
        CheckSource(sb, config);
        CheckDestinations(sb, config);
        CheckSchedules(sb);
        CheckLastRun(sb, config);
        CheckEmail(sb, config);
        CheckLogs(sb);

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

    private static void CheckSource(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("SOURCE");
        if (string.IsNullOrWhiteSpace(config.Source))
        {
            Line(sb, "FAIL", "No source folder is configured.");
            return;
        }

        if (!Directory.Exists(config.Source))
        {
            Line(sb, "FAIL", $"Source folder does not exist: {config.Source}");
            return;
        }

        Line(sb, "OK", $"{config.Source}");
        Line(sb, "OK", $"Mode: {(config.Mirror ? "Mirror (deletions propagate)" : "Additive (never deletes)")}"
                     + (config.Mirror
                        ? config.RecycleDeletedFiles
                            ? $"; deleted files kept {config.RecycleRetentionDays} days in '{config.RecycleFolderName}'"
                            : "; deletions are PERMANENT (recycle turned off)"
                        : ""));

        if (config.Mirror && !config.RecycleDeletedFiles)
            Line(sb, "WARN", "Mirror without the recycle safety net - a file deleted from the source is "
                            + "removed from every destination with no backup.");
    }

    private static void CheckDestinations(StringBuilder sb, SyncConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("DESTINATIONS");

        var enabled = config.Targets.Where(t => t.Enabled).ToList();
        if (enabled.Count == 0)
        {
            Line(sb, "FAIL", "No destinations are enabled - a sync would do nothing.");
            return;
        }

        long sourceSize = TryGetFolderSize(config.Source);

        foreach (var t in config.Targets)
        {
            string label = $"{t.Name} -> {t.Destination}";
            if (!t.Enabled) { Line(sb, "WARN", $"{label}  (disabled)"); continue; }

            if (string.IsNullOrWhiteSpace(t.Destination)) { Line(sb, "FAIL", $"{t.Name}: no path set."); continue; }
            if (PathUtil.SameOrNested(config.Source, t.Destination))
            {
                Line(sb, "FAIL", $"{label}  is the source or nested with it - would be skipped as unsafe.");
                continue;
            }
            if (!PathUtil.DriveAvailable(t.Destination))
            {
                Line(sb, "FAIL", $"{label}  drive not available (is the cloud client running?).");
                continue;
            }

            Line(sb, "OK", label + (Directory.Exists(t.Destination) ? "" : "  (will be created)"));

            // Free space, when we can work out both numbers.
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(t.Destination));
                if (root is not null && !root.StartsWith(@"\\"))
                {
                    var di = new DriveInfo(root);
                    string free = FormatSize(di.AvailableFreeSpace);
                    if (sourceSize > 0 && di.AvailableFreeSpace < sourceSize)
                        Line(sb, "WARN", $"       free space {free} is less than the source ({FormatSize(sourceSize)}).");
                    else
                        Line(sb, "OK", $"       free space {free}.");
                }
            }
            catch { /* free space is informational only */ }
        }

        var patterns = (config.ExcludeFiles ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var folders = (config.ExcludeFolders ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        Line(sb, "OK", $"Excluded files: {(patterns.Count > 0 ? string.Join(" ", patterns) : "(none)")}");
        Line(sb, "OK", $"Excluded folders: {(folders.Count > 0 ? string.Join(" ", folders) : "(none)")}");
    }

    private static void CheckSchedules(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("SCHEDULES");

        var tasks = new[]
        {
            (ScheduleManager.DailyTask, "Daily"),
            (ScheduleManager.MonthlyTask, "Monthly"),
            (ScheduleManager.LogonTask, "Logon / unlock"),
        };

        bool any = false;
        foreach (var (task, label) in tasks)
        {
            var state = ScheduleManager.GetState(task);
            if (state == ScheduleManager.TaskState.NotInstalled)
            {
                Line(sb, "OK", $"{label}: not installed.");
                continue;
            }

            any = true;
            string target = ScheduleManager.GetTaskTarget(task) ?? "";
            string exe = target.Replace("\"", "").Replace(" --sync", "").Replace(" --auto", "").Trim();

            if (exe.Length > 0 && !File.Exists(exe))
                Line(sb, "FAIL", $"{label}: {state}, but its program file is missing - it never runs. "
                                + $"Click Enable again to repair.  ({exe})");
            else if (state == ScheduleManager.TaskState.Disabled)
                Line(sb, "WARN", $"{label}: installed but disabled.");
            else
                Line(sb, "OK", $"{label}: enabled -> {exe}");
        }

        if (!any)
            Line(sb, "WARN", "No automatic schedule is installed - syncs only happen when you click Sync Now.");
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
        string when = $"{last.When:yyyy-MM-dd HH:mm:ss} ({FormatAge(age)} ago)";
        Line(sb, last.Success ? "OK" : "FAIL", $"{when} - {(last.Success ? "succeeded" : "completed with errors")}");

        foreach (var d in last.Destinations)
            Line(sb, d.Success ? "OK" : "FAIL",
                 $"       {d.Name}: {d.FilesCopied} copied, {d.FilesFailed} failed"
                 + (d.FilesRecycled > 0 ? $", {d.FilesRecycled} recycled" : "")
                 + (string.IsNullOrEmpty(d.Note) ? "" : $" - {d.Note}"));

        if (age > TimeSpan.FromDays(7))
            Line(sb, "WARN", "The last sync was over a week ago - check the schedules above.");

        Line(sb, "OK", $"Logon/unlock throttle: at most one sync every {config.MinMinutesBetweenAutoSyncs} min.");
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

    private static void CheckLogs(StringBuilder sb)
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
            Line(sb, "OK", $"{SyncConfig.LogDir} is writable ({count} log file(s), pruned after 7 days).");
        }
        catch (Exception ex)
        {
            Line(sb, "FAIL", $"Cannot write to {SyncConfig.LogDir}: {ex.Message}");
        }
    }

    private static long TryGetFolderSize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    private static string FormatAge(TimeSpan age) => age.TotalDays >= 1
        ? $"{age.TotalDays:0.#} day(s)"
        : age.TotalHours >= 1 ? $"{age.TotalHours:0.#} hour(s)" : $"{age.TotalMinutes:0} min";
}
