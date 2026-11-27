using System.Diagnostics;
using System.Text;
using WinForms = System.Windows.Forms;

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
            return RunHeadless(auto, ScheduleManager.TriggerFromArguments(args));

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
            if (AppInstance.IsSeparate) return 0;      // the tasks belong to the installed copy

            string current = ScheduleManager.ExePath;
            foreach (var (task, label) in ScheduleManager.AllTasks())
            {
                var info = ScheduleManager.Query(task);
                if (!info.Installed) continue;

                bool moved = info.Exe is not null && !info.Exe.Equals(current, StringComparison.OrdinalIgnoreCase);

                // A task from before 1.3.0 does not say which schedule it is, so its runs show in the
                // history as just "Schedule". Registering it again fixes that - but only when the task
                // was read in full, because a calendar task is re-registered from the time that was read.
                bool readInFull = info.Exe is not null && (task == ScheduleManager.LogonTask || info.Time is not null);
                bool unlabelled = info.LacksTrigger && readInFull;

                if (!moved && !unlabelled) continue;

                var (ok, msg) = ScheduleManager.Repair(task);
                AppendEvent(moved
                    ? $"[Setup] repointed {label} schedule to this install: {(ok ? "OK" : "FAILED")} {msg}"
                    : $"[Setup] refreshed {label} schedule so its runs are labelled in the history: {(ok ? "OK" : "FAILED")} {msg}");
            }

            StartupRegistration.Repair();
            return 0;
        }

        if (Has(args, "--remove-schedules"))
        {
            if (AppInstance.IsSeparate) return 0;

            foreach (var task in new[] { ScheduleManager.DailyTask, ScheduleManager.MonthlyTask, ScheduleManager.LogonTask })
            {
                if (ScheduleManager.Exists(task)) ScheduleManager.Remove(task);
            }
            ScheduleManager.RemoveLegacyStartupShortcut();
            StartupRegistration.Remove();
            AppendEvent("[Setup] scheduled tasks and the start-at-sign-in entry removed (uninstall).");
            return 0;
        }

        return RunGui(startInTray: Has(args, StartupRegistration.TraySwitch));
    }

    /// <summary>
    /// Starts the WPF UI. The Application object is created here rather than from an App.xaml,
    /// because this method is the entry point (it also serves the headless/CLI modes above).
    /// </summary>
    /// <param name="startInTray">Started by Windows at sign-in: no window, only the tray icon.</param>
    private static int RunGui(bool startInTray)
    {
        // One window per user session. A second copy meant two tray icons and two windows each saving
        // config.json over the other's changes. Held until the process exits.
        using var instance = new Mutex(initiallyOwned: true, AppInstance.WindowMutexName, out bool firstCopy);
        if (!firstCopy)
        {
            // Started in the tray while already running: there is nothing to show and nothing to say.
            if (startInTray) return 0;

            // The copy that is already running may have no window on screen - it lives in the tray -
            // so ask it to show itself rather than telling the user to go and find it.
            if (SignalRunningCopy()) return 0;

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
            window = new MainWindow(startInTray);
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

        // In the tray there is no window to show: the message loop runs with only the tray icon, and
        // the window appears when the icon is opened.
        return startInTray ? app.Run() : app.Run(window);
    }

    /// <summary>Asks the copy that is already running to bring its window up. False if it is not listening.</summary>
    private static bool SignalRunningCopy()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(AppInstance.ShowEventName, out var signal)) return false;
            using (signal) signal.Set();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Silent run used by Task Scheduler. Writes a fresh log file per run and returns 0/1.</summary>
    /// <param name="auto">
    /// True for trigger-based runs (logon/unlock). These are skipped if a sync already ran within
    /// MinMinutesBetweenAutoSyncs, so unlocking the screen repeatedly doesn't re-sync every time.
    /// </param>
    /// <param name="trigger">Which schedule started the run, for the history.</param>
    private static int RunHeadless(bool auto, RunTrigger trigger)
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
            return FailBeforeSync($"{ex.Message}\nNothing was synced, and the settings file was left untouched.",
                                  trigger, config: null);
        }

        if (config.LoadProblem is { } problem)
            return FailBeforeSync($"{problem}\nNothing was synced. Restore the kept copy, or set the folder "
                                + "pairs up again in the Folder Sync window.", trigger, config);

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
        string? fatal = null;
        try
        {
            // One sync at a time across every copy of the app. Another run (the window, or a second
            // trigger firing at the same moment) is left to finish; this one has nothing to add.
            result = SyncLock.Run(() => engine.RunAsync().GetAwaiter().GetResult(), TimeSpan.Zero, AppInstance.SyncLockName);
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
            // Whatever the pairs had done before it stopped is still worth recording.
            result = engine.Current ?? new SyncResult();
            result.Success = false;
            if (result.Finished == default) result.Finished = DateTime.Now;
            fatal = ex.Message;
        }

        log.Dispose();                                // complete before it is attached to the email
        LastRun.Save(result);                         // so the window can show a summary
        PairHistory.Record(result);                   // so the grid shows each pair's last outcome
        SyncConfig.RecordSyncTime();                  // feeds the --auto minimum-gap throttle
        SyncConfig.CleanupOldLogs(config.LogRetentionDays);

        // Written last: an open window watches for this file and reads the others when it appears.
        var record = RunRecord.From(result, trigger, logPath);
        if (fatal is not null) record.Note = "The sync stopped unexpectedly: " + fatal;
        RunHistoryStore.Save(record);
        RunHistoryStore.Prune(config.HistoryRetentionDays);

        TrySendEmail(config, result, logPath);
        if (WeeklyDigest.SendIfDue(config, DateTime.Now) is { } digest) AppendEvent(digest);

        NotifyIfNobodyIsWatching(config, record);

        return (result.Success && fatal is null) ? 0 : 1;
    }

    /// <summary>
    /// Records a scheduled run that could not start, so the failure shows in the log folder, the events
    /// log, the history and the window's status rather than vanishing. Returns the failing exit code.
    /// </summary>
    private static int FailBeforeSync(string why, RunTrigger trigger, SyncConfig? config)
    {
        AppendEvent("[Sync] not run: " + why.Replace('\n', ' '));

        string logPath = SyncConfig.NewSyncLogPath();
        try { File.WriteAllText(logPath, "FATAL: " + why + Environment.NewLine); } catch { }
        LastRun.Save(new SyncResult { Success = false });

        var record = new RunRecord
        {
            Started = DateTime.Now,
            Finished = DateTime.Now,
            Trigger = trigger,
            Success = false,
            Note = why.Replace('\n', ' '),
            LogFile = Path.GetFileName(logPath),
        };
        RunHistoryStore.Save(record);

        // With no readable settings there is nothing to say notifications are off, and a sync that
        // cannot start at all is exactly what they are for.
        NotifyIfNobodyIsWatching(config ?? new SyncConfig(), record);
        return 1;
    }

    /// <summary>
    /// Shows a notification for a run that failed, or for a pair that has gone too long without a
    /// successful sync - but only when no window is running. An open window (it may be in the tray) shows
    /// them itself, from an icon that stays put, so they remain in the notification list; this process
    /// is about to exit and can only show one briefly.
    /// </summary>
    private static void NotifyIfNobodyIsWatching(SyncConfig config, RunRecord record)
    {
        try
        {
            if (WindowIsRunning()) return;

            DateTime now = DateTime.Now;
            var memory = AlertMemory.Load();
            var alerts = new List<Alert>();

            if (Alerts.ForRun(record, config) is { } failure && Alerts.ShouldShow(failure, memory, now))
                alerts.Add(failure);
            if (Alerts.Overdue(config.Pairs ?? new List<SyncPair>(), PairHistory.Load(), config, now, memory) is { } overdue)
                alerts.Add(overdue);

            if (alerts.Count == 0) return;
            memory.Save();

            var first = alerts[0];
            string text = alerts.Count == 1 ? first.Text : first.Text + "\n" + alerts[1].Text;
            HeadlessNotifier.Show(new Alert(first.Title, text, alerts.Any(a => a.IsError)));
            AppendEvent("[Notify] " + first.Title + ": " + text.Replace('\n', ' '));
        }
        catch (Exception ex)
        {
            AppendEvent("[Notify] could not show a notification: " + ex.Message);
        }
    }

    private static bool WindowIsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(AppInstance.WindowMutexName, out var window)) return false;
            window.Dispose();
            return true;
        }
        catch { return false; }
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

/// <summary>
/// Shows one notification from a process that has no window: a scheduled run that is about to exit.
/// The tray icon exists only for as long as the notification is on screen.
/// </summary>
internal static class HeadlessNotifier
{
    public static void Show(Alert alert, int seconds = 12)
    {
        try
        {
            using var icon = new WinForms.NotifyIcon
            {
                Icon = AppResources.TrayIcon,
                Text = "Folder Sync",
                Visible = true,
            };

            bool done = false;
            icon.BalloonTipClicked += (_, _) => { OpenWindow(); done = true; };
            icon.BalloonTipClosed += (_, _) => done = true;
            icon.ShowBalloonTip(seconds * 1000, alert.Title, alert.Text,
                                alert.IsError ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

            // The notification's events arrive as window messages, so this thread has to keep reading
            // them until it is dismissed, clicked, or has been up long enough.
            var until = Stopwatch.StartNew();
            while (!done && until.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                WinForms.Application.DoEvents();
                Thread.Sleep(50);
            }
            icon.Visible = false;
        }
        catch { /* a notification that cannot be shown must not turn a finished sync into a failure */ }
    }

    private static void OpenWindow()
    {
        try { Process.Start(new ProcessStartInfo { FileName = ScheduleManager.ExePath, UseShellExecute = false }); }
        catch { /* nothing more to do from here */ }
    }
}
