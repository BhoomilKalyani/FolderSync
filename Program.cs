using System.Text;

namespace FolderSync;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool headless = args.Any(a =>
            a.Equals("--sync", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/sync", StringComparison.OrdinalIgnoreCase));

        // --auto marks a trigger-based run (logon/unlock), which honours the minimum-gap throttle.
        bool auto = Has(args, "--auto");

        if (headless)
            return RunHeadless(auto);

        // Setup/uninstall hooks: register or clean up the scheduled tasks without showing a window.
        if (Has(args, "--register-logon"))
        {
            var (ok, msg) = ScheduleManager.RegisterLogon();
            AppendEvent($"[Setup] register logon/unlock: {(ok ? "OK" : "FAILED")} {msg}");
            return ok ? 0 : 1;
        }

        // Run by Setup after every install: re-point any schedule that still refers to a
        // different copy of the app (e.g. an older install location), keeping its time/day.
        if (Has(args, "--repair-schedules"))
        {
            string current = ScheduleManager.ExePath;
            foreach (var (task, label) in ScheduleManager.AllTasks())
            {
                if (!ScheduleManager.Exists(task)) continue;
                var exe = ScheduleManager.GetTaskExe(task);
                if (exe is null || exe.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;

                var (ok, msg) = ScheduleManager.Repair(task);
                AppendEvent($"[Setup] repointed {label} schedule to this install: {(ok ? "OK" : "FAILED")} {msg}");
            }
            return 0;
        }

        if (Has(args, "--remove-schedules"))
        {
            foreach (var task in new[] { ScheduleManager.DailyTask, ScheduleManager.MonthlyTask, ScheduleManager.LogonTask })
            {
                if (ScheduleManager.Exists(task)) ScheduleManager.Remove(task);
            }
            ScheduleManager.RemoveLegacyStartupShortcut();
            AppendEvent("[Setup] scheduled tasks removed (uninstall).");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>Silent run used by Task Scheduler. Writes a fresh log file per run and returns 0/1.</summary>
    /// <param name="auto">
    /// True for trigger-based runs (logon/unlock). These are skipped if a sync already ran within
    /// MinMinutesBetweenAutoSyncs, so unlocking the screen repeatedly doesn't re-sync every time.
    /// </param>
    private static int RunHeadless(bool auto = false)
    {
        var config = SyncConfig.Load();

        if (auto && config.MinMinutesBetweenAutoSyncs > 0)
        {
            var last = SyncConfig.GetLastSyncTime();
            if (last is not null)
            {
                var age = DateTime.Now - last.Value;
                if (age < TimeSpan.FromMinutes(config.MinMinutesBetweenAutoSyncs))
                {
                    AppendEvent($"[Auto] skipped - last sync was {age.TotalMinutes:F0} min ago " +
                                $"(minimum gap {config.MinMinutesBetweenAutoSyncs} min).");
                    return 0;
                }
            }
        }

        var logPath = SyncConfig.NewSyncLogPath();   // unique file per sync, never appended to an old one
        var sb = new StringBuilder();

        var engine = new SyncEngine(config);
        engine.Log += line => sb.AppendLine(line);

        SyncResult result;
        bool fatal = false;
        try
        {
            result = engine.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine("FATAL: " + ex);
            result = new SyncResult { Success = false };
            fatal = true;
        }

        try { File.WriteAllText(logPath, sb.ToString()); } catch { }
        LastRun.Save(result);                         // so the window can show a summary
        SyncConfig.RecordSyncTime();                  // feeds the --auto minimum-gap throttle
        SyncConfig.CleanupOldLogs(7);                 // prune logs older than 7 days
        TrySendEmail(config, result, logPath);

        return (result.Success && !fatal) ? 0 : 1;
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Appends a line to the small rolling events log (shared with the GUI).</summary>
    private static void AppendEvent(string line)
    {
        try
        {
            Directory.CreateDirectory(SyncConfig.LogDir);
            File.AppendAllText(Path.Combine(SyncConfig.LogDir, "events.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* not critical */ }
    }

    /// <summary>Sends the notification email if enabled and the mode condition is met. Never throws.</summary>
    private static void TrySendEmail(SyncConfig config, SyncResult result, string logPath)
    {
        var email = config.Email;
        if (email is null || !email.Enabled) return;
        if (!email.ShouldSend(result.Success)) return;

        try
        {
            var (subject, body) = Emailer.BuildSummary(result, config, logPath);
            Emailer.Send(email, subject, body, logPath);
            TryAppend(logPath, $"[Email] notification sent to {email.To}.");
        }
        catch (Exception ex)
        {
            TryAppend(logPath, $"[Email] FAILED to send: {ex.Message}");
        }
    }

    private static void TryAppend(string path, string line)
    {
        try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
    }
}
