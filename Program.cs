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

        return RunGui();
    }

    /// <summary>
    /// Starts the WPF UI. The Application object is created here rather than from an App.xaml,
    /// because this method is the entry point (it also serves the headless/CLI modes above).
    /// </summary>
    private static int RunGui()
    {
        // One window per user session. A second copy meant two tray icons and two windows each saving
        // config.json over the other's changes. Held until the process exits.
        using var instance = new Mutex(initiallyOwned: true, @"Local\FolderSync.Window", out bool firstCopy);
        if (!firstCopy)
        {
            System.Windows.MessageBox.Show(
                "Folder Sync is already running. Look for its icon in the notification area, next to the clock.",
                "Folder Sync", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return 0;
        }

        // WinForms is still initialised: the tray icon and folder picker come from it.
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose,
        };

        // Without this a failure such as a bad data binding kills the window with no explanation
        // and nothing written down. Record it and say so, rather than vanishing.
        app.DispatcherUnhandledException += (_, e) =>
        {
            AppendEvent("[Crash] " + e.Exception);
            try
            {
                System.Windows.MessageBox.Show(
                    "Folder Sync hit an unexpected problem:\n\n" + e.Exception.Message
                    + "\n\nThe details have been written to events.log in the log folder.",
                    "Folder Sync", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch { /* if even the dialog fails, the log entry above is what matters */ }
            e.Handled = true;
        };
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("/FolderSync;component/Theme.xaml", UriKind.Relative),
        });
        ThemeManager.Apply(AppTheme.System);

        MainWindow window;
        try
        {
            window = new MainWindow();
        }
        catch (ConfigUnavailableException ex)
        {
            // Opening on defaults here would let the next save overwrite the user's real settings.
            AppendEvent("[Config] window not opened: " + ex.Message);
            System.Windows.MessageBox.Show(
                ex.Message + "\n\nAnother program may be using it. Close that program and start Folder Sync again.",
                "Folder Sync", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return 1;
        }
        app.MainWindow = window;
        return app.Run(window);
    }

    /// <summary>Silent run used by Task Scheduler. Writes a fresh log file per run and returns 0/1.</summary>
    /// <param name="auto">
    /// True for trigger-based runs (logon/unlock). These are skipped if a sync already ran within
    /// MinMinutesBetweenAutoSyncs, so unlocking the screen repeatedly doesn't re-sync every time.
    /// </param>
    private static int RunHeadless(bool auto = false)
    {
        SyncConfig config;
        try
        {
            config = SyncConfig.Load();
        }
        catch (ConfigUnavailableException ex)
        {
            // The settings exist but could not be opened. Running on defaults would sync nothing and
            // report success, so fail instead, and leave the file exactly as it is.
            return FailBeforeSync($"{ex.Message}\nNothing was synced, and the settings file was left untouched.");
        }

        if (config.LoadProblem is { } problem)
            return FailBeforeSync($"{problem}\nNothing was synced. Restore the kept copy, or set the folder "
                                + "pairs up again in the Folder Sync window.");

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

        // Written line by line as the sync goes, not all at once at the end: Task Scheduler stops a run
        // that passes its 2-hour limit, and a log held in memory until then was simply lost - exactly
        // the run whose log is most needed.
        var log = SyncLog.Open(logPath);
        var engine = new SyncEngine(config);
        engine.Log += log.WriteLine;

        SyncResult result;
        bool fatal = false;
        try
        {
            // One sync at a time across every copy of the app. Another run (the window, or a second
            // trigger firing at the same moment) is left to finish; this one has nothing to add.
            result = SyncLock.Run(() => engine.RunAsync().GetAwaiter().GetResult(), TimeSpan.Zero);
        }
        catch (SyncAlreadyRunningException)
        {
            log.Dispose();
            try { File.Delete(logPath); } catch { }
            AppendEvent("[Sync] skipped - another sync was already running.");
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine("FATAL: " + ex);
            result = new SyncResult { Success = false };
            fatal = true;
        }

        log.Dispose();                                // complete before it is attached to the email
        LastRun.Save(result);                         // so the window can show a summary
        PairHistory.Record(result);                   // so the grid shows each pair's last outcome
        SyncConfig.RecordSyncTime();                  // feeds the --auto minimum-gap throttle
        SyncConfig.CleanupOldLogs(7);                 // prune logs older than 7 days
        TrySendEmail(config, result, logPath);

        return (result.Success && !fatal) ? 0 : 1;
    }

    /// <summary>
    /// Records a scheduled run that could not start, so the failure shows in the log folder, the events
    /// log and the window's last-sync card rather than vanishing. Returns the failing exit code.
    /// </summary>
    private static int FailBeforeSync(string why)
    {
        AppendEvent("[Sync] not run: " + why.Replace('\n', ' '));
        try { File.WriteAllText(SyncConfig.NewSyncLogPath(), "FATAL: " + why + Environment.NewLine); } catch { }
        LastRun.Save(new SyncResult { Success = false });
        return 1;
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Appends a line to the small rolling events log (shared with the GUI).</summary>
    private static void AppendEvent(string line) => SyncConfig.AppendEvent(line);

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
