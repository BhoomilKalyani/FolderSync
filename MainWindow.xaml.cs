using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace FolderSync;

/// <summary>
/// The main window. Split across files by page: this one holds what is shared - start-up, navigation,
/// the folder pairs, running a sync, schedules, email, recycled files, updates and the tray icon.
/// See MainWindow.Home.cs, MainWindow.History.cs and MainWindow.Settings.cs for the rest.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SyncConfig _config;
    private readonly ObservableCollection<SyncPair> _pairs;
    private readonly WinForms.NotifyIcon _tray;
    private readonly object _logLock = new();
    private bool _loaded;
    private bool _exiting;

    private CancellationTokenSource? _cts;
    private UpdateInfo? _pendingUpdate;
    private List<RecycleBatch> _batches = new();

    private const string DeveloperName = "Bhoomil Kalyani";
    private const string DeveloperEmail = "bhoomil.kalyani999@gmail.com";

    // The pages, in the order they appear in the sidebar.
    private const int NavHome = 0, NavPairs = 1, NavSchedule = 2, NavHistory = 3,
                      NavRecycled = 4, NavNotifications = 5, NavSettings = 6, NavAbout = 7;

    private readonly (string task, string label)[] _scheduleTasks =
    {
        (ScheduleManager.DailyTask, "Daily"),
        (ScheduleManager.MonthlyTask, "Monthly"),
        (ScheduleManager.LogonTask, "Logon / unlock"),
    };

    /// <summary>One trigger row, so its buttons and state text can be refreshed later.</summary>
    private sealed record SchedRow(string Task, Button Enable, Button Disable, Button Remove, TextBlock State);

    private readonly List<SchedRow> _rows = new();

    // Pickers built in BuildScheduleRows, held so EnableSchedule can read them.
    private ComboBox _dailyHour = null!, _dailyMinute = null!;
    private ComboBox _monthlyHour = null!, _monthlyMinute = null!, _monthlyDay = null!;

    /// <param name="startHidden">Started by Windows at sign-in: the window is not shown until it is asked for.</param>
    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        _config = SyncConfig.Load();
        ThemeManager.Apply(_config.ThemeChoice);

        _pairs = new ObservableCollection<SyncPair>(_config.Pairs);
        foreach (var pair in _pairs) pair.PropertyChanged += Pair_PropertyChanged;
        _pairs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (SyncPair gone in e.OldItems) gone.PropertyChanged -= Pair_PropertyChanged;
            if (e.NewItems is not null) foreach (SyncPair added in e.NewItems) added.PropertyChanged += Pair_PropertyChanged;
            SyncPairsToConfig();
            SaveConfig();
            PairsChanged();
        };

        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        VersionText.Text = $"Version {Updater.CurrentVersion}";
        AboutVersion.Text = $"Folder Sync {Updater.CurrentVersion}";
        CopyrightText.Text = $"Copyright © {DateTime.Now.Year} {DeveloperName}. All rights reserved.";
        AboutPaths.Text = $"Settings:  {SyncConfig.ConfigPath}\nLogs:      {SyncConfig.LogDir}\n"
                        + $"History:   {RunHistoryStore.DefaultFolder}\nProgram:   {ScheduleManager.ExePath}";

        PairGrid.ItemsSource = _pairs;
        LoadConfigIntoUi();
        LoadSettingsIntoUi();
        RefreshMountedCloud(firstTime: true);
        RefreshPairStatuses();
        BuildScheduleRows();
        UpdateThemeToggle();

        _tray = CreateTray();
        _watcher.Due += keys => Dispatcher.BeginInvoke(new Action(() => OnWatchedFoldersChanged(keys)));
        _watcher.StatusChanged += () => Dispatcher.BeginInvoke(new Action(ShowWatchStatus));
        ListenForShowRequests();

        _loaded = true;
        RefreshHome();

        if (_config.IsUnconfigured)
            Status = "Add a folder pair to begin.";

        // Started once the message loop is running, whether or not the window is ever shown. Not in the
        // constructor itself: it runs before WPF has a synchronisation context, so the code after each
        // background await would resume on a worker thread and fail touching the controls.
        Dispatcher.BeginInvoke(new Action(() => _ = StartBackgroundWorkAsync()), DispatcherPriority.ApplicationIdle);

        Loaded += async (_, _) => await FirstShownAsync();
    }

    /// <summary>
    /// Everything that has to be going whether the window is on screen or sitting in the tray: reading
    /// the schedules, watching folders, noticing scheduled runs, and the update check.
    /// </summary>
    private async Task StartBackgroundWorkAsync()
    {
        try
        {
            WatchHistoryFolder();
            ApplyWatchSettings();
            StartClock();

            await RefreshSchedulesAsync();
            await RefreshHomeAsync();
            CheckForOverduePairs();

            if (_config.CheckForUpdates && Updater.IsCheckDue())
                await CheckForUpdatesAsync(manual: false);
        }
        catch (Exception ex)
        {
            AppendEvent("[Startup] " + ex.Message);
        }
    }

    /// <summary>The first time the window is actually shown: the things that need someone to read them.</summary>
    private async Task FirstShownAsync()
    {
        if (_config.LoadProblem is { } problem)
            MessageWindow.Warn(this, "Settings could not be read",
                problem + "\n\nScheduled syncs will report a failure until the folder pairs are set up "
                + "again. The settings folder is listed on the About page.");

        await _schedulesRead.Task;
        await WarnAboutStaleTaskTargetsAsync();
    }

    // ===================== Config <-> UI =====================

    private void LoadConfigIntoUi()
    {
        SkipFilesBox.Text = string.Join("; ", _config.ExcludeFiles);
        SkipFoldersBox.Text = string.Join("; ", _config.ExcludeFolders);
        UpdateRecycleNote();

        var em = _config.Email;
        EmailEnable.IsChecked = em.Enabled;
        EmailFields.Visibility = em.Enabled ? Visibility.Visible : Visibility.Collapsed;
        EmailFrom.Text = em.From;
        EmailTo.Text = em.To;
        EmailHost.Text = em.SmtpHost;
        EmailPort.Text = em.SmtpPort.ToString(CultureInfo.InvariantCulture);
        EmailModeBox.SelectedIndex = em.Mode switch
        {
            FolderSync.EmailMode.OnError => 1,
            FolderSync.EmailMode.OnSuccess => 2,
            FolderSync.EmailMode.Weekly => 3,
            _ => 0,
        };
        UpdatePasswordHint();
        UpdateEmailModeHint();
        UpdateUnlockNote();
    }

    private void UpdateUnlockNote()
    {
        UnlockNote.Text = "“Logon / unlock” syncs when you sign in and each time you unlock the screen, "
                        + (_config.MinMinutesBetweenAutoSyncs > 0
                            ? $"at most once every {Humanise.Count(_config.MinMinutesBetweenAutoSyncs, "minute")}. "
                            : "with no limit on how often. ")
                        + "On a laptop that sleeps rather than shutting down, this is the trigger that keeps things current.";
    }

    private void UpdateRecycleNote()
    {
        var deleting = _pairs.Where(p => p.Enabled && p.DeletesExtras).ToList();
        string mirrors = deleting.Count == 1 ? "1 pair mirrors" : $"{deleting.Count} pairs mirror";

        string note = deleting.Count == 0
            ? "No enabled pair deletes anything right now."
            : _config.RecycleDeletedFiles
                ? $"{mirrors}, so deletions carry over — removed files are kept for "
                  + $"{Humanise.Count(_config.RecycleRetentionDays, "day")} and can be put back from Recycled files."
                : $"{mirrors}, and deletions are PERMANENT: the recycle folder is switched off in Settings.";

        RecycleNote.Text = note + " Double-click a pair to edit it.";
    }

    /// <summary>
    /// Copies the grid's pairs into the config as a new list. Never clear-and-refill in place: the health
    /// check, the recycled-files scan and a sync all read the config's list on a background thread, and
    /// changing that list under them throws "Collection was modified". A fresh list leaves any reader
    /// with the one it started on.
    /// </summary>
    private void SyncPairsToConfig() => _config.Pairs = _pairs.ToList();

    /// <summary>
    /// Fills in each pair's "Last sync" column from the stored per-pair history, so a pair that has
    /// been quietly failing is obvious without opening a log.
    /// </summary>
    private void RefreshPairStatuses()
    {
        var history = PairHistory.Load();
        foreach (var pair in _pairs)
        {
            if (history.TryGetValue(KeyOf(pair), out var status))
            {
                pair.LastRunText = status.Text;
                pair.LastRunHealth = status.Health;
            }
            else
            {
                pair.LastRunText = "Never run";
                pair.LastRunHealth = PairHealth.None;
            }
        }
    }

    private static string KeyOf(SyncPair pair) => PairHistory.KeyFor(pair.Left, pair.Right);

    private void SaveConfig()
    {
        if (!_loaded) return;

        _config.ExcludeFiles = SplitList(SkipFilesBox.Text);
        _config.ExcludeFolders = SplitList(SkipFoldersBox.Text);
        SyncPairsToConfig();

        try { _config.Save(); }
        catch (Exception ex) { AppendEvent("[Config] save failed: " + ex.Message); }
    }

    private static List<string> SplitList(string text) => text
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private void Skip_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        ApplyWatchSettings();      // the skip list is also what the watcher ignores
    }

    /// <summary>
    /// A pair was switched on or off in the list. Followed from the pair itself rather than from a click
    /// on the tick-box, so it is saved however the tick-box was changed - the keyboard and a screen
    /// reader change it without a click.
    /// </summary>
    private void Pair_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loaded || e.PropertyName != nameof(SyncPair.Enabled)) return;
        SaveConfig();
        PairsChanged();
    }

    /// <summary>Something about the pairs changed: everything that describes them has to follow.</summary>
    private void PairsChanged()
    {
        if (!_loaded) return;
        UpdateRecycleNote();
        RefreshMountedCloud();
        RefreshPairStatuses();
        RefreshHome();
        ApplyWatchSettings();
    }

    // ===================== Navigation / theme =====================

    private FrameworkElement[] Pages => new FrameworkElement[]
    {
        PageHome, PageSync, PageSchedule, PageHistory, PageRecycled, PageEmail, PageSettings, PageAbout,
    };

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageAbout is null) return;   // during construction

        var pages = Pages;
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ScrollToTop();

        if (!_loaded) return;
        switch (Nav.SelectedIndex)
        {
            case NavHome: _ = RefreshHomeAsync(); break;
            case NavPairs: RefreshMountedCloud(); break;   // a provider may have been installed since
            case NavSchedule: RefreshScheduleStates(); ShowWatchStatus(); break;
            case NavHistory: _ = LoadHistoryAsync(); break;
            case NavRecycled: LoadRecycled(); break;
            case NavSettings: LoadSettingsIntoUi(); break;
        }
    }

    private void GoTo(int page) => Nav.SelectedIndex = page;

    private void GoToPairs_Click(object sender, RoutedEventArgs e) => GoTo(NavPairs);
    private void GoToHistory_Click(object sender, RoutedEventArgs e) => GoTo(NavHistory);
    private void GoToSettings_Click(object sender, RoutedEventArgs e) => GoTo(NavSettings);

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        // The link flips between the two; "Same as Windows" is chosen on the Settings page.
        _config.Theme = ThemeManager.IsDark ? "Light" : "Dark";
        ApplyTheme();
        SaveConfig();
    }

    private void ApplyTheme()
    {
        ThemeManager.Apply(_config.ThemeChoice);
        UpdateThemeToggle();
        if (_loaded) SelectTheme();
    }

    /// <summary>The link names the mode it switches to, from the start rather than only after a click.</summary>
    private void UpdateThemeToggle() =>
        ThemeToggle.Content = ThemeManager.IsDark ? "Switch to light mode" : "Switch to dark mode";

    // ===================== Folder pairs =====================

    private void PairGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Edit and Remove act on the selected pair, so they are greyed out until there is one, rather
        // than being clickable and answering "select a pair first".
        bool selected = PairGrid.SelectedItem is SyncPair;
        EditPairButton.IsEnabled = selected;
        RemovePairButton.IsEnabled = selected;
    }

    private void AddPair_Click(object sender, RoutedEventArgs e)
    {
        // Additive by default: a new pair cannot delete anything until Mirror is ticked.
        var pair = new SyncPair { Direction = SyncDirection.ToRight, Mirror = false, Enabled = true };
        AddPair(pair);
    }

    private void AddPair(SyncPair pair)
    {
        var dlg = new PairWindow(pair, isNew: true, _config) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (_pairs.Any(p => SamePair(p, pair)))
        {
            MessageWindow.Info(this, "Already added", "A pair with those two folders already exists.");
            return;
        }
        _pairs.Add(pair);
        PairGrid.SelectedItem = pair;
    }

    private void EditPair_Click(object sender, RoutedEventArgs e) => EditSelectedPair();

    private void PairGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        EditSelectedPair();

    private void EditSelectedPair()
    {
        if (PairGrid.SelectedItem is not SyncPair pair) return;

        var dlg = new PairWindow(pair, isNew: false, _config) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // The pair object edits in place and raises change notifications, so the grid follows.
        SaveConfig();
        PairsChanged();
    }

    private void RemovePair_Click(object sender, RoutedEventArgs e)
    {
        if (PairGrid.SelectedItem is not SyncPair pair) return;

        if (MessageWindow.Confirm(this, "Remove pair",
                $"Stop syncing this pair?\n\n{pair.DisplayName}\n{pair.Left}\n{pair.Right}\n\n"
                + "Neither folder's files are deleted.",
                primary: "Remove"))
            _pairs.Remove(pair);
    }

    private static bool SamePair(SyncPair a, SyncPair b) =>
        string.Equals(a.Left, b.Left, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Right, b.Right, StringComparison.OrdinalIgnoreCase);

    private void EmailDeveloper_Click(object sender, RoutedEventArgs e) =>
        OpenPath($"mailto:{DeveloperEmail}?subject=Folder%20Sync%20{Updater.CurrentVersion}");

    private void ViewLicence_Click(object sender, RoutedEventArgs e)
    {
        // Ship-alongside licence if present, otherwise the canonical copy on GitHub.
        string local = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (File.Exists(local))
            new TextWindow("Licence", "MIT Licence", File.ReadAllText(local)) { Owner = this }.ShowDialog();
        else
            OpenPath($"https://github.com/{SyncConfig.DefaultUpdateRepo}/blob/main/LICENSE");
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenPath(SyncConfig.ConfigPath);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenPath(SyncConfig.LogDir);
    private void OpenProject_Click(object sender, RoutedEventArgs e) =>
        OpenPath($"https://github.com/{SyncConfig.DefaultUpdateRepo}");

    // ===================== Sync / preview =====================

    /// <summary>The most changes a preview lists. Beyond this the table is no longer something to read.</summary>
    private const int PreviewLimit = 20000;

    private async void Sync_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(listOnly: false);
    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(listOnly: true);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Status = "Cancelling…";
    }

    /// <summary>
    /// Runs a sync or a preview from the window.
    /// </summary>
    /// <param name="trigger">
    /// What started it. A run started by a file change has nobody waiting for it, so it asks no
    /// questions and shows no dialogs: anything that would have needed one is skipped or logged.
    /// </param>
    /// <param name="onlyPairs">Only these pairs, or null for every enabled one.</param>
    /// <param name="approved">Deletions that were reviewed and approved for this run (see <see cref="SyncOptions"/>).</param>
    /// <returns>The result, or null when nothing ran.</returns>
    private async Task<SyncResult?> RunSyncAsync(bool listOnly, RunTrigger trigger = RunTrigger.Manual,
                                                IReadOnlyCollection<string>? onlyPairs = null,
                                                IReadOnlyDictionary<string, int>? approved = null,
                                                bool showPreview = true)
    {
        bool quiet = trigger != RunTrigger.Manual;

        if (_cts is not null)      // already running
        {
            if (quiet && onlyPairs is not null) _watcher.Requeue(onlyPairs);
            return null;
        }

        SaveConfig();

        var active = _config.ActivePairs
            .Where(p => onlyPairs is null || onlyPairs.Contains(KeyOf(p), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (active.Count == 0)
        {
            if (quiet) return null;
            MessageWindow.Warn(this, "Nothing to sync",
                "No folder pair is enabled, so there is nothing to do.\n\n"
                + "Use “Add pair…” on the Folder pairs page to set up two folders, then try again.");
            Status = "Add at least one folder pair.";
            return null;
        }

        // An unplugged drive is not a problem to be warned about - the pair just waits for it - so it
        // is separated out from genuine faults rather than being mixed into an alarming list.
        var offline = active.Where(p => p.MissingDrive() is not null).ToList();
        var runnable = active.Except(offline).ToList();

        // Uses the pair's own rule, so this warning always matches what the engine will actually do.
        var broken = runnable.Select(p => (pair: p, problem: p.ValidateFolders()))
                             .Where(x => x.problem is not null)
                             .ToList();

        if (!quiet && broken.Count > 0)
        {
            string list = string.Join("\n", broken.Select(x => $"  • {x.pair.DisplayName} — {x.problem}"));

            if (broken.Count == runnable.Count)
            {
                MessageWindow.Warn(this, "Nothing can be synced",
                    $"Every pair that could run has a problem:\n\n{list}");
                Status = "Fix the folder pairs and try again.";
                return null;
            }

            if (!MessageWindow.Confirm(this, "Some pairs cannot run",
                    $"These pairs will be skipped:\n\n{list}\n\nContinue with the rest?",
                    primary: "Sync anyway", tone: DialogTone.Warning))
            {
                Status = "Cancelled.";
                return null;
            }
        }
        else if (!quiet && offline.Count == active.Count)
        {
            MessageWindow.Info(this, "Nothing to sync yet",
                "Every enabled pair uses a drive that is not connected:\n\n"
                + string.Join("\n", offline.Select(p => $"  • {p.DisplayName} — {p.MissingDrive()}"))
                + "\n\nConnect the drive and sync again. This is not an error.");
            Status = "Waiting for a drive to be connected.";
            return null;
        }

        string statusBefore = StatusText.Text;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        Status = listOnly ? "Previewing…" : "Syncing…";

        var log = new StringBuilder();
        var engine = new SyncEngine(_config);
        engine.Log += line => { lock (_logLock) log.AppendLine(line); };
        engine.Progress += p => Dispatcher.BeginInvoke(new Action(() => ShowProgress(p)));

        var options = new SyncOptions
        {
            ListOnly = listOnly,
            OnlyPairs = onlyPairs,
            ApprovedDeletions = approved,
            ChangeLimit = listOnly ? PreviewLimit : new SyncOptions().ChangeLimit,
        };

        SyncResult? result = null;
        bool busyElsewhere = false, cancelled = false;
        string? fatal = null;
        var token = _cts.Token;
        if (!listOnly) _watcher.SyncStarted();
        try
        {
            // A real sync takes the cross-process lock, so it cannot overlap a scheduled run over the
            // same folders. Preview changes nothing and needs no lock.
            result = listOnly
                ? await Task.Run(() => engine.RunAsync(options, token))
                : await Task.Run(() => SyncLock.Run(
                      () => engine.RunAsync(options, token).GetAwaiter().GetResult(), TimeSpan.Zero,
                      AppInstance.SyncLockName));
        }
        catch (SyncAlreadyRunningException)
        {
            busyElsewhere = true;      // nothing ran, so there is no log or result to record
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            lock (_logLock) log.AppendLine("CANCELLED by user.");
            result = engine.Current;   // what had been done before it was stopped
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
            lock (_logLock) log.AppendLine("FATAL: " + ex);
            result = engine.Current;
            if (result is not null) result.Success = false;
        }
        finally
        {
            if (!listOnly) _watcher.SyncFinished();
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }

        if (busyElsewhere)
        {
            Status = "A scheduled sync is running right now — try again when it has finished.";
            if (quiet && onlyPairs is not null) _watcher.Requeue(onlyPairs);
            return null;
        }

        if (listOnly)
        {
            string logText;
            lock (_logLock) logText = log.ToString();
            TryWrite(SyncConfig.NewPreviewLogPath(), logText);
            SyncConfig.CleanupOldLogs(_config.LogRetentionDays);

            if (cancelled || fatal is not null || result is null)
            {
                Status = cancelled ? "Cancelled." : "The preview failed — see the log folder.";
                return null;
            }

            // A preview made on the way to something else (reviewing a stopped mirror) leaves the status
            // line saying what it said: that line is still about the last sync.
            Status = showPreview ? "Preview ready (also saved to the log folder)." : statusBefore;
            if (showPreview) ShowPreview(result, logText);
            return result;
        }

        string logPath = SyncConfig.NewSyncLogPath();
        lock (_logLock) TryWrite(logPath, log.ToString());
        SyncConfig.RecordSyncTime();
        SyncConfig.CleanupOldLogs(_config.LogRetentionDays);

        if (result is null)
        {
            Status = "Failed.";
            return null;
        }
        if (result.Finished == default) result.Finished = DateTime.Now;

        // A run that was stopped part-way is recorded in the history, but it does not replace the
        // summary of the last complete sync.
        if (!cancelled) LastRun.Save(result);
        PairHistory.Record(result);

        var record = RunRecord.From(result, trigger, logPath);
        if (fatal is not null) record.Note = "The sync stopped unexpectedly: " + fatal;
        RunHistoryStore.Save(record);
        _ownRuns.Add(record.Id);       // so the folder watcher does not treat it as someone else's run
        RunHistoryStore.Prune(_config.HistoryRetentionDays);

        RefreshPairStatuses();
        _ = RefreshHomeAsync();
        if (Nav.SelectedIndex == NavHistory) _ = LoadHistoryAsync();

        int skipped = result.Targets.Count(t => t.Skipped);
        string skippedNote = skipped > 0 ? $" {Humanise.Count(skipped, "pair")} skipped (drive not connected)." : "";
        int copied = result.Targets.Sum(t => t.FilesCopied);
        string copiedNote = copied > 0 ? $" {Humanise.Count(copied, "file")} copied." : " Nothing needed copying.";
        string lead = trigger == RunTrigger.FileChange ? "Synced after a file change" : "Done";

        Status = cancelled ? $"Cancelled — {DateTime.Now:HH:mm:ss}."
               : fatal is not null ? $"Failed — {DateTime.Now:HH:mm:ss}. The details are in the log."
               : result.Success ? $"{lead} — {DateTime.Now:HH:mm:ss}.{copiedNote}{skippedNote}"
               : $"Completed with errors — {DateTime.Now:HH:mm:ss}.{skippedNote}";

        if (quiet)
        {
            // Nobody asked for this run, so it only speaks up if it went wrong.
            ShowAlertFor(record);

            // The weekly summary is sent by whichever unattended run comes first after the week is up.
            // Someone who only syncs on file changes has no scheduled run to do it, so these count too.
            var config = _config;
            _ = Task.Run(() =>
            {
                if (WeeklyDigest.SendIfDue(config, DateTime.Now) is { } line) AppendEvent(line);
            });
            return result;
        }

        if (cancelled) return result;

        int recycled = result.Targets.Sum(t => t.FilesRecycled);
        _tray.ShowBalloonTip(5000, "Folder Sync",
            (result.Success ? "Sync completed successfully." : "Sync completed with errors — see the log.")
            + (recycled > 0 ? $" {Humanise.Count(recycled, "deleted item")} kept in the recycle folder." : ""),
            result.Success ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);

        // A mirror the guard stopped is the one failure that can be put right from here.
        var stopped = result.Targets.FirstOrDefault(t => t.GuardStopped);
        if (stopped is not null && approved is null)
            await OfferReviewAsync(stopped);

        return result;
    }

    /// <summary>The status line under the sync buttons.</summary>
    private string Status
    {
        set => StatusText.Text = value;
    }

    private void SetBusy(bool busy)
    {
        SyncButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy;
        HealthButton.IsEnabled = !busy && !_healthRunning;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = true;      // until the first count arrives
        Progress.Value = 0;
    }

    /// <summary>
    /// Shows where a run has got to: which pair, and how many of its files are done. While a pair is
    /// still being checked nothing can be counted, so the bar only moves; once the count is known it
    /// fills as files are copied.
    /// </summary>
    private void ShowProgress(SyncProgress p)
    {
        if (_cts is null) return;      // a late report from a run that has already finished

        Status = p.Text;
        if (p.Fraction is { } fraction)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = fraction * 100;
        }
        else
        {
            Progress.IsIndeterminate = true;
        }
    }

    /// <summary>The preview as a table: every file, what would happen to it, and where.</summary>
    private void ShowPreview(SyncResult result, string logText)
    {
        var rows = new List<ChangeRow>();
        var warnings = new List<string>();
        int toCopy = 0, toDelete = 0, omitted = 0;
        long bytes = 0;

        foreach (var plan in result.Plans)
        {
            if (plan.Blocked)
            {
                warnings.Add($"{plan.Name} would not run: {plan.Note}.");
                continue;
            }
            if (plan.GuardWouldStop)
                warnings.Add($"{plan.Name} would be stopped by the large-deletion guard: {plan.GuardReason}. "
                           + "Nothing would be copied or deleted for that pair until the deletion is reviewed and approved.");

            toCopy += plan.FilesToCopy;
            toDelete += plan.FilesToDelete;
            bytes += plan.BytesToCopy;
            omitted += plan.ChangesOmitted;
            rows.AddRange(plan.Changes.Select(c => ChangeRow.From(c, plan.Name, plan.Left, plan.Right, planned: true)));
        }

        string summary = $"{Humanise.Count(result.Plans.Count, "pair")} checked · "
                       + $"{Humanise.Count(toCopy, "file")} to copy"
                       + (bytes > 0 ? $" ({Humanise.Size(bytes)})" : "")
                       + $" · {Humanise.Count(toDelete, "file")} to remove";

        new ChangesWindow(
            "Preview", "Preview — nothing has been changed", summary, rows,
            warning: warnings.Count > 0 ? string.Join("\n", warnings) : null,
            logText: logText,
            emptyText: warnings.Count > 0
                ? "No changes to list for the pairs that can run."
                : "Everything is already in step. A sync would not copy or remove anything.",
            footnote: omitted > 0 ? $"  and {omitted:N0} more that are not listed here" : null)
        { Owner = DialogOwner }.ShowDialog();
    }

    /// <summary>
    /// The owner for a dialog that can be opened from the tray menu or a notification: this window, if it
    /// has ever been on screen. WPF refuses to use a window that has never been shown as an owner, and
    /// that is exactly the state the app is in when Windows starts it straight into the tray.
    /// </summary>
    private Window? DialogOwner => IsLoaded ? this : null;

    private bool _healthRunning;

    private async void Health_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null || _healthRunning) return;   // a sync or another check is running
        SaveConfig();

        // The report launches several schtasks processes and walks every folder pair, which is
        // long enough to freeze the window if it runs on the UI thread.
        _healthRunning = true;
        HealthButton.IsEnabled = false;

        // The status line belongs to the sync: put back whatever it said ("Completed with errors…")
        // rather than replacing it with "Ready.". Only if nothing else has written to it meanwhile.
        const string checking = "Checking…";
        string before = StatusText.Text;
        Status = checking;

        string report;
        try { report = await Task.Run(() => Diagnostics.Run(_config)); }
        catch (Exception ex) { report = "Health check failed: " + ex; }
        finally
        {
            _healthRunning = false;
            HealthButton.IsEnabled = _cts is null;
            if (StatusText.Text == checking) Status = before;
        }

        new TextWindow("Health check", "Folder Sync health check", report) { Owner = this }.ShowDialog();
    }

    // ===================== Schedule =====================

    /// <summary>What Task Scheduler holds for each of the three tasks, as last read.</summary>
    private Dictionary<string, ScheduleManager.TaskInfo> _tasks = new();

    /// <summary>Completes when the schedules have been read for the first time.</summary>
    private readonly TaskCompletionSource _schedulesRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _scheduleTimesShown;

    private void BuildScheduleRows()
    {
        ScheduleRows.Children.Clear();
        _rows.Clear();
        ScheduleRows.Children.Add(new TextBlock
        {
            Text = "Triggers",
            Style = (Style)FindResource("CardTitle"),
        });

        // Each trigger carries only the settings it actually needs, so there is nothing to fill in
        // for logon/unlock and no free-text field anywhere that could hold an impossible time.
        _dailyHour = MakeNumberPicker(0, 23, 18);
        _dailyMinute = MakeNumberPicker(0, 55, 0, step: 5);
        ScheduleRows.Children.Add(MakeScheduleRow("Daily", ScheduleManager.DailyTask,
            TimeControls("at", _dailyHour, _dailyMinute)));

        _monthlyHour = MakeNumberPicker(0, 23, 18);
        _monthlyMinute = MakeNumberPicker(0, 55, 0, step: 5);
        _monthlyDay = MakeNumberPicker(1, 28, 1);
        var monthly = TimeControls("at", _monthlyHour, _monthlyMinute);
        monthly.Children.Add(new TextBlock { Text = "on day", Margin = new Thickness(12, 0, 8, 0) });
        monthly.Children.Add(_monthlyDay);
        ScheduleRows.Children.Add(MakeScheduleRow("Monthly", ScheduleManager.MonthlyTask, monthly));

        ScheduleRows.Children.Add(MakeScheduleRow("Logon / unlock", ScheduleManager.LogonTask, null));
        // States are filled in once the message loop is running; see StartBackgroundWorkAsync.

        if (AppInstance.IsSeparate)
        {
            ScheduleSeparateNote.Text = ScheduleManager.SeparateCopyMessage;
            ScheduleSeparateNote.Visibility = Visibility.Visible;
        }
    }

    /// <summary>"at [hh] : [mm]" — pickers rather than a text box, so only valid times exist.</summary>
    private StackPanel TimeControls(string lead, ComboBox hour, ComboBox minute)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = lead, Margin = new Thickness(0, 0, 8, 0) });
        panel.Children.Add(hour);
        panel.Children.Add(new TextBlock { Text = ":", Margin = new Thickness(4, 0, 4, 0) });
        panel.Children.Add(minute);
        return panel;
    }

    private ComboBox MakeNumberPicker(int from, int to, int selected, int step = 1)
    {
        var combo = new ComboBox
        {
            Width = 62,
            Style = (Style)FindResource("ThemedComboBox"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        };
        for (int v = from; v <= to; v += step) combo.Items.Add(v.ToString("00", CultureInfo.InvariantCulture));
        combo.SelectedItem = selected.ToString("00", CultureInfo.InvariantCulture);
        if (combo.SelectedItem is null && combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private Grid MakeScheduleRow(string label, string task, StackPanel? extras)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Disabled until the task's state has been read (in the background), so nothing can be clicked
        // against a state that is not known yet.
        var enable = new Button { Content = "Enable", IsEnabled = false };
        var disable = new Button { Content = "Disable", IsEnabled = false };
        var remove = new Button { Content = "Remove", IsEnabled = false };
        enable.Click += (_, _) => ApplySchedule(() => EnableSchedule(task), $"{label}: enable");
        disable.Click += (_, _) => ApplySchedule(() => ScheduleManager.SetEnabled(task, false), $"{label}: disable");
        remove.Click += (_, _) => ApplySchedule(() => ScheduleManager.Remove(task), $"{label}: remove");
        buttons.Children.Add(enable);
        buttons.Children.Add(disable);
        buttons.Children.Add(remove);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        if (extras is not null)
        {
            extras.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(extras, 2);
            row.Children.Add(extras);
        }

        var state = new TextBlock
        {
            Text = "checking…",
            Margin = new Thickness(14, 0, 0, 0),
            Style = (Style)FindResource("Subtle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 3);
        row.Children.Add(state);

        _rows.Add(new SchedRow(task, enable, disable, remove, state));
        return row;
    }

    private (bool ok, string msg) EnableSchedule(string task)
    {
        if (!ConfirmExePathForScheduling()) return (false, "cancelled");

        if (task == ScheduleManager.DailyTask)
            return ScheduleManager.RegisterDaily(PickedTime(_dailyHour, _dailyMinute));

        if (task == ScheduleManager.MonthlyTask)
            return ScheduleManager.RegisterMonthly(PickedTime(_monthlyHour, _monthlyMinute), Picked(_monthlyDay, 1));

        return ScheduleManager.RegisterLogon();
    }

    private static TimeOnly PickedTime(ComboBox hour, ComboBox minute) =>
        new(Picked(hour, 18), Picked(minute, 0));

    private static int Picked(ComboBox combo, int fallback) =>
        combo.SelectedItem is string s && int.TryParse(s, out int v) ? v : fallback;

    private static void SelectTime(ComboBox hour, ComboBox minute, TimeOnly time)
    {
        Select(hour, time.Hour);
        // Minutes are in 5-minute steps; snap to the nearest available entry.
        Select(minute, time.Minute - (time.Minute % 5));
    }

    private static void Select(ComboBox combo, int value)
    {
        string text = value.ToString("00", CultureInfo.InvariantCulture);
        if (combo.Items.Contains(text)) combo.SelectedItem = text;
    }

    private void ApplySchedule(Func<(bool ok, string msg)> action, string what)
    {
        try
        {
            var (ok, msg) = action();
            AppendEvent($"[Schedule] {what}: {(ok ? "OK" : "FAILED")} {msg}");
            if (!ok && !msg.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                MessageWindow.Warn(this, "Schedule not changed", msg);
        }
        catch (Exception ex)
        {
            MessageWindow.Warn(this, "Schedule not changed", ex.Message);
        }
        RefreshScheduleStates();
    }

    private void RefreshScheduleStates() => _ = RefreshSchedulesAsync();

    /// <summary>
    /// Reads the three tasks and shows what was found: each trigger's state, the time it is registered
    /// for, and - on the Home page - when the next scheduled sync is. Each task is one schtasks process,
    /// so they are read off the UI thread. Never throws - callers discard the task.
    /// </summary>
    private async Task RefreshSchedulesAsync()
    {
        var rows = _rows.ToList();
        Dictionary<string, ScheduleManager.TaskInfo> tasks;
        try
        {
            tasks = await Task.Run(() => rows.ToDictionary(r => r.Task, r => ScheduleManager.Query(r.Task)));
        }
        catch (Exception ex)
        {
            AppendEvent("[Schedule] could not read the schedules: " + ex.Message);
            foreach (var row in rows) row.State.Text = "could not be read";
            _schedulesRead.TrySetResult();
            return;
        }

        _tasks = tasks;
        foreach (var row in rows)
        {
            var st = tasks[row.Task].State;
            // A copy running on its own data folder may look, but must not change the real tasks.
            bool canChange = !AppInstance.IsSeparate;
            row.Enable.IsEnabled = canChange && st != ScheduleManager.TaskState.Enabled;
            row.Disable.IsEnabled = canChange && st == ScheduleManager.TaskState.Enabled;
            row.Remove.IsEnabled = canChange && st != ScheduleManager.TaskState.NotInstalled;
            row.State.Text = st switch
            {
                ScheduleManager.TaskState.Enabled => "enabled",
                ScheduleManager.TaskState.Disabled => "installed but disabled",
                _ => "not installed",
            };
        }

        // Show the time a registered task actually runs at, rather than a stale default - once, so a
        // time the user is in the middle of choosing is not overwritten by a later refresh.
        if (!_scheduleTimesShown)
        {
            _scheduleTimesShown = true;
            var daily = tasks[ScheduleManager.DailyTask];
            var monthly = tasks[ScheduleManager.MonthlyTask];
            if (daily.Time is { } dt) SelectTime(_dailyHour, _dailyMinute, dt);
            if (monthly.Time is { } mt) SelectTime(_monthlyHour, _monthlyMinute, mt);
            if (monthly.Day is { } day) Select(_monthlyDay, day);
        }

        _schedulesRead.TrySetResult();
        RefreshHome();
    }

    private bool ConfirmExePathForScheduling()
    {
        if (!ScheduleManager.IsBuildOutputPath()) return true;

        return MessageWindow.Confirm(this, "Running from a build folder",
            "This copy is running from a build output folder:\n\n"
            + ScheduleManager.ExePath
            + "\n\nA schedule created now would stop working as soon as the project is rebuilt. "
            + "Run the installed copy instead.",
            primary: "Create anyway", tone: DialogTone.Warning);
    }

    private Task WarnAboutStaleTaskTargetsAsync()
    {
        if (AppInstance.IsSeparate) return Task.CompletedTask;      // not this copy's tasks to repair

        var stale = _scheduleTasks
            .Select(t => (t.task, t.label, info: _tasks.TryGetValue(t.task, out var info) ? info : ScheduleManager.TaskInfo.Missing))
            .Where(t => t.info.Installed && !string.IsNullOrEmpty(t.info.Exe) && !File.Exists(t.info.Exe))
            .Select(t => (t.task, t.label, exe: t.info.Exe!))
            .ToList();
        if (stale.Count == 0) return Task.CompletedTask;

        AppendEvent("[Schedule] stale target(s): " + string.Join(" | ", stale.Select(s => $"{s.label} -> {s.exe}")));

        if (!MessageWindow.Confirm(this, "Schedules need repair",
                "These schedules point at a program file that no longer exists, so they never run:\n\n"
                + string.Join("\n", stale.Select(s => $"  • {s.label} → {s.exe}"))
                + "\n\nRepair them to use this copy?\n\n" + ScheduleManager.ExePath
                + "\n\nTimes and days are kept as they are.",
                primary: "Repair", tone: DialogTone.Warning))
            return Task.CompletedTask;

        foreach (var s in stale)
        {
            var (ok, msg) = ScheduleManager.Repair(s.task);
            AppendEvent($"[Schedule] repair {s.label}: {(ok ? "OK" : "FAILED")} {msg}");
        }
        RefreshScheduleStates();
        MessageWindow.Info(this, "Schedules repaired", "They now point at this copy of Folder Sync.");
        return Task.CompletedTask;
    }

    // ===================== Email =====================

    private void EmailEnable_Click(object sender, RoutedEventArgs e)
    {
        EmailFields.Visibility = EmailEnable.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SaveEmail();
    }

    private void Email_LostFocus(object sender, RoutedEventArgs e) => SaveEmail();

    private void EmailPassword_Changed(object sender, RoutedEventArgs e) => UpdatePasswordHint();

    /// <summary>
    /// Shows whether a password is already stored, since a PasswordBox looks identical whether it
    /// is empty or holding a secret we deliberately never read back out.
    /// </summary>
    private void UpdatePasswordHint()
    {
        if (EmailPassword.Password.Length > 0)
        {
            EmailPasswordHint.Text = "";
            return;
        }
        EmailPasswordHint.Text = _config.Email.HasPassword
            ? "•••••••••••••••  saved — type to replace"
            : "paste the 16-character App Password";
    }

    private void EmailMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        SaveEmail();
        if (_loaded) UpdateEmailModeHint();
    }

    private void UpdateEmailModeHint()
    {
        EmailModeHint.Text = EmailModeBox.SelectedIndex == 3
            ? "One email a week with how many syncs ran, any that failed, and which pairs have not synced. "
              + "It is sent by the first scheduled sync after the week is up; the first one arrives a week from now."
            : "Sent after each scheduled sync, with that run's log attached. Syncs you start by hand do not send email.";
    }

    private void SaveEmail()
    {
        if (!_loaded) return;

        var em = _config.Email;
        em.Enabled = EmailEnable.IsChecked == true;
        em.From = EmailFrom.Text.Trim();
        em.Username = em.From;                       // Gmail signs in with the address
        em.To = EmailTo.Text.Trim();
        em.SmtpHost = EmailHost.Text.Trim();
        em.SmtpPort = int.TryParse(EmailPort.Text, out int port) ? port : 587;
        em.Mode = EmailModeBox.SelectedIndex switch
        {
            1 => FolderSync.EmailMode.OnError,
            2 => FolderSync.EmailMode.OnSuccess,
            3 => FolderSync.EmailMode.Weekly,
            _ => FolderSync.EmailMode.Always,
        };

        if (EmailPassword.Password.Length > 0)
        {
            // Google shows App Passwords with spaces; strip them so a paste just works.
            em.SetPassword(EmailPassword.Password.Replace(" ", ""));
            EmailPassword.Clear();
            EmailStatus.Text = "Password saved.";
            UpdatePasswordHint();
        }

        try { _config.Save(); }
        catch (Exception ex) { AppendEvent("[Email] config save failed: " + ex.Message); }
    }

    private void AppPasswordHelp_Click(object sender, RoutedEventArgs e)
    {
        if (MessageWindow.Confirm(this, "How to get an App Password",
                "Gmail needs a 16-character App Password, not your normal password.\n\n"
                + "1.  Turn on 2-Step Verification\n"
                + "2.  Create an App Password for “Mail”\n"
                + "3.  Paste the 16-character code here — spaces are fine\n\n"
                + "Each Google account has its own App Password, and you can revoke it at any time.",
                primary: "Open the page"))
            OpenPath("https://myaccount.google.com/apppasswords");
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        SaveEmail();
        var em = _config.Email;
        if (!em.HasPassword) { EmailStatus.Text = "Enter the app password first."; return; }

        // Connecting, signing in and sending can take many seconds (or a long timeout when the server is
        // unreachable), which froze the whole window while it ran on the UI thread.
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        EmailStatus.Text = "Sending…";
        string subject = "[Folder Sync] Test email";
        string body = $"This is a test from Folder Sync on {Environment.MachineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n\n"
                    + "If you received this, notifications are configured correctly.";
        try
        {
            await Task.Run(() => Emailer.Send(em, subject, body));
            EmailStatus.Text = "Test email sent.";
            AppendEvent($"[Email] test sent to {em.To}.");
        }
        catch (Exception ex)
        {
            EmailStatus.Text = "Failed.";
            AppendEvent("[Email] test FAILED: " + ex.Message);

            string msg = ex.Message;
            if (msg.Contains("Application-specific password", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("InvalidSecondFactor", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("5.7.9", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Username and Password not accepted", StringComparison.OrdinalIgnoreCase))
            {
                msg = "Google rejected the sign-in because this isn't an App Password.\n\n"
                    + "Create one at myaccount.google.com/apppasswords (app = Mail) and paste the "
                    + "16-character code into the App password box.\n\nOriginal error:\n" + ex.Message;
            }
            MessageWindow.Warn(this, "Test email failed", msg);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    // ===================== Recycled files =====================

    private void RefreshRecycled_Click(object sender, RoutedEventArgs e) => _ = LoadRecycledAsync();

    private void LoadRecycled() => _ = LoadRecycledAsync();

    /// <summary>Numbers each scan, so only the newest one's result is ever shown.</summary>
    private int _recycleScan;

    /// <summary>
    /// Lists the recycled batches. The scan walks every file of every batch on both sides of every
    /// pair to total up counts and sizes, which is slow enough to stall the window after a month of
    /// retained batches — so it runs off the UI thread.
    ///
    /// <para>Scans can overlap (Refresh, opening the page, deleting a batch), and they finish in any
    /// order, so a result is applied only if no newer scan has started since: otherwise an older scan
    /// that still listed a just-deleted batch could finish last and bring it back. The old list is
    /// cleared while scanning, so nothing stale can be restored or deleted in the meantime.</para>
    ///
    /// <para>Never throws: callers discard the returned task, so an exception escaping here would be
    /// lost without a trace and leave the page stuck on "Looking…".</para>
    /// </summary>
    private async Task LoadRecycledAsync()
    {
        int scan = ++_recycleScan;
        RefreshRecycledButton.IsEnabled = false;

        try
        {
            SaveConfig();

            BatchGrid.ItemsSource = null;          // clears the selection, which resets the file list
            RecycledFileGrid.ItemsSource = null;
            RecycledFilesTitle.Text = "Files";
            RestoreMeaningText.Text = "";
            RestoreStatus.Text = "Looking…";
            UpdateRestoreButtons();

            var config = _config;
            var batches = await Task.Run(() => RecycleStore.GetBatches(config));
            if (scan != _recycleScan) return;       // a newer scan has started; its result is the one to show

            _batches = batches;
            BatchGrid.ItemsSource = _batches;
            RestoreStatus.Text = _batches.Count == 0
                ? "Nothing has been recycled. Files appear here when a sync would otherwise delete or replace them."
                : $"{Humanise.Count(_batches.Count, "batch", "batches")} found.";
            UpdateRestoreButtons();
        }
        catch (Exception ex)
        {
            if (scan != _recycleScan) return;
            AppendEvent("[Recycle] could not list recycled files: " + ex);
            RestoreStatus.Text = "Could not list the recycled files: " + ex.Message;
        }
        finally
        {
            if (scan == _recycleScan) RefreshRecycledButton.IsEnabled = true;
        }
    }

    private void BatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not RecycleBatch batch)
        {
            RecycledFileGrid.ItemsSource = null;
            RecycledFilesTitle.Text = "Files";
            RestoreMeaningText.Text = "";
            UpdateRestoreButtons();
            return;
        }

        var files = RecycleStore.GetFiles(batch);
        RecycledFileGrid.ItemsSource = files;
        RecycledFilesTitle.Text = $"Files in {batch.WhenText} ({batch.DestinationName}) — {files.Count}";

        RestoreMeaningText.Text = batch.RestoreMeaning;
        RestoreMeaningText.Foreground = (System.Windows.Media.Brush)FindResource(
            batch.Reason == RecycleReason.Replaced ? "Warn" : "FgMuted");

        UpdateRestoreButtons();
    }

    private void UpdateRestoreButtons()
    {
        bool hasBatch = BatchGrid.SelectedItem is RecycleBatch;
        RestoreAllButton.IsEnabled = hasBatch;
        DeleteBatchButton.IsEnabled = hasBatch;
        RestoreSelectedButton.IsEnabled = hasBatch;
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var files = RecycledFileGrid.SelectedItems.OfType<RecycledFile>().ToList();
        if (files.Count == 0)
        {
            RestoreStatus.Text = "Select one or more files first, or use “Restore all in batch”.";
            return;
        }
        DoRestore(files);
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not RecycleBatch batch) return;
        DoRestore(RecycleStore.GetFiles(batch));
    }

    private void DoRestore(List<RecycledFile> files)
    {
        if (BatchGrid.SelectedItem is not RecycleBatch batch) return;

        string restoreTo = batch.RestoreTo;
        if (string.IsNullOrWhiteSpace(restoreTo) || !Directory.Exists(restoreTo))
        {
            MessageWindow.Warn(this, "Cannot restore",
                $"The folder these files came from is missing, so there is nowhere to restore to:\n\n{restoreTo}");
            return;
        }

        // Explicit button labels beat Yes/No here: "No" meaning "overwrite" would be a trap.
        // A replaced file still exists on both sides; putting the old copy back is a rollback, not a
        // recovery, and the next sync will carry that rollback across. Say so before doing it.
        string caution = batch.Reason == RecycleReason.Replaced
            ? "\n\nThese are previous versions of files that still exist. Restoring rolls them back, "
              + "and the newer content will be replaced on the next sync."
            : "\n\nThe next sync will copy them back across the pair.";

        var choice = MessageWindow.Choose(this, "Restore files",
            $"Restore {Humanise.Count(files.Count, "file")} into:\n\n{restoreTo}{caution}",
            primary: "Skip existing", secondary: "Overwrite existing");
        if (choice == DialogChoice.Cancel) return;

        bool overwrite = choice == DialogChoice.Secondary;
        var result = RecycleStore.Restore(batch.Path, files, restoreTo, overwrite);

        RestoreStatus.Text = $"Restored {result.Restored}, skipped {result.Skipped}"
                           + (result.Errors.Count > 0 ? $", {Humanise.Count(result.Errors.Count, "error")}." : ".");
        AppendEvent($"[Recycle] restored {result.Restored}, skipped {result.Skipped}, errors {result.Errors.Count}");

        if (result.Errors.Count > 0)
            new TextWindow("Restore", "Some files could not be restored", string.Join(Environment.NewLine, result.Errors))
            { Owner = this }.ShowDialog();
    }

    private void OpenBatchFolder_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is RecycleBatch batch) OpenPath(batch.Path);
    }

    private void DeleteBatch_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not RecycleBatch batch) return;

        if (!MessageWindow.Confirm(this, "Delete batch",
                $"Permanently delete this batch?\n\n{batch.Path}\n\n{Humanise.Count(batch.FileCount, "file")}, {batch.SizeText}.\n\n"
                + "This cannot be undone.",
                primary: "Delete", tone: DialogTone.Danger))
            return;

        var (ok, msg) = RecycleStore.DeleteBatch(batch.Path);
        RestoreStatus.Text = ok ? "Batch deleted." : "Could not delete: " + msg;
        LoadRecycled();
    }

    // ===================== Cloud storage mounted as a folder =====================

    private List<CloudFolder> _mountedCloud = new();
    private bool _cloudExpanded;

    /// <summary>
    /// Shows the cloud services whose desktop app already exposes them as a folder, so syncing to
    /// Google Drive or OneDrive is a couple of clicks with no sign-in at all. This is the only cloud
    /// route the app has: see <see cref="CloudFolders"/> for why talking to a provider's API was
    /// tried and dropped.
    /// </summary>
    /// <param name="firstTime">Decide whether the card starts open: it does until there is a pair.</param>
    private void RefreshMountedCloud(bool firstTime = false)
    {
        try { _mountedCloud = CloudFolders.Detect(); }
        catch (Exception ex) { _mountedCloud = new List<CloudFolder>(); AppendEvent("[Cloud] detect failed: " + ex.Message); }

        var rows = _mountedCloud
            .Select(f => new CloudFolderRow
            {
                Provider = f.Provider,
                Path = f.Path,
                InUse = _pairs.Any(p => Covers(p, f.Path)),
            })
            .ToList();
        MountedCloudList.ItemsSource = rows;

        // Stays visible even with nothing found: the note below the list explains that installing a
        // cloud app is all it takes for one to show up.
        MountedCloudCard.Visibility = Visibility.Visible;

        int unused = rows.Count(r => !r.InUse);
        CloudSummary.Text = rows.Count == 0
            ? "None found. Install a cloud service's desktop app and its folder appears here."
            : $"{Humanise.Count(rows.Count, "cloud folder")} found · "
              + (unused == 0 ? "all set up" : $"{unused} not set up yet");

        // Open on a fresh install, where this card is the quickest way to a first pair; folded once
        // pairs exist, where it would otherwise push them down the page every time.
        if (firstTime) _cloudExpanded = _pairs.Count == 0;
        ShowCloudDetails();
    }

    private void CloudToggle_Click(object sender, RoutedEventArgs e)
    {
        _cloudExpanded = !_cloudExpanded;
        ShowCloudDetails();
    }

    private void ShowCloudDetails()
    {
        CloudDetails.Visibility = _cloudExpanded ? Visibility.Visible : Visibility.Collapsed;
        CloudToggle.Content = _cloudExpanded ? "Hide" : "Show";
    }

    /// <summary>True when either side of a pair is at or inside <paramref name="folder"/>.</summary>
    private static bool Covers(SyncPair pair, string folder) =>
        Inside(pair.Left, folder) || Inside(pair.Right, folder);

    private static bool Inside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string a = path.TrimEnd('\\', '/');
        string b = folder.TrimEnd('\\', '/');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts a normal local pair with the cloud folder as the receiving side.</summary>
    private void SyncToMountedCloud_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string folder) return;

        AddPair(new SyncPair
        {
            Right = folder,
            Direction = SyncDirection.ToRight,
            Mirror = false,      // additive until the user chooses otherwise
            Enabled = true,
        });
    }

    // ===================== Updates =====================

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private void UpdateBanner_Click(object sender, RoutedEventArgs e) => ShowUpdateWindow();

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual) UpdateStatus.Text = "Checking…";
        try
        {
            var info = await Updater.CheckAsync(_config);
            if (info is null)
            {
                if (manual) UpdateStatus.Text = $"You're up to date — {Updater.CurrentVersion} is the newest version.";
                return;
            }

            _pendingUpdate = info;
            UpdateBanner.Content = $"Update to {info.Version}";
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateStatus.Text = $"Version {info.Version} is available.";
            AppendEvent($"[Update] version {info.Version} available (running {Updater.CurrentVersion}).");

            if (manual) ShowUpdateWindow();
            else _tray.ShowBalloonTip(6000, "Folder Sync update available",
                     $"Version {info.Version} is ready to install.", WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppendEvent("[Update] check failed: " + ex.Message);
            if (manual) UpdateStatus.Text = "Could not check for updates: " + ex.Message;
        }
    }

    private void ShowUpdateWindow()
    {
        if (_pendingUpdate is null) return;
        var dlg = new UpdateWindow(_pendingUpdate) { Owner = DialogOwner };
        if (dlg.ShowDialog() == true) ExitApp();
    }

    // ===================== Tray =====================

    private WinForms.NotifyIcon CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sync now", null, async (_, _) => await RunSyncAsync(listOnly: false));
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        var tray = new WinForms.NotifyIcon
        {
            Icon = AppResources.TrayIcon,
            Text = "Folder Sync",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        // A notification is an invitation to look: clicking it opens the window on the Home page.
        tray.BalloonTipClicked += (_, _) => { ShowFromTray(); GoTo(NavHome); };
        return tray;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // Texts such as "10 minutes ago" may be stale after a long time in the tray.
        RefreshPairStatuses();
        _ = RefreshHomeAsync();
    }

    /// <summary>Really exits, as opposed to closing the window into the tray.</summary>
    private void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// A second copy of the app was started. It cannot open a second window, so it asks this one to
    /// show itself - which matters once the app can be running with no window at all.
    /// </summary>
    private void ListenForShowRequests()
    {
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, AppInstance.ShowEventName);
        }
        catch (Exception ex)
        {
            AppendEvent("[Startup] could not listen for a second copy: " + ex.Message);
            return;
        }

        var signal = _showSignal;
        var thread = new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                {
                    if (_exiting) return;
                    Dispatcher.BeginInvoke(new Action(ShowFromTray));
                }
            }
            catch { /* the handle was closed: the app is exiting */ }
        })
        { IsBackground = true, Name = "Show requests" };
        thread.Start();
    }

    private EventWaitHandle? _showSignal;
    private bool _trayHintShown;

    /// <summary>
    /// Whether closing the window should leave the app running in the tray: when it is watching folders,
    /// or when it has been set to start in the tray at sign-in - both say "keep this running".
    /// </summary>
    private bool StaysInTray => _config.SyncOnChange || StartupRegistration.IsEnabled();

    protected override void OnStateChanged(EventArgs e)
    {
        // Minimising sends the window to the tray rather than the taskbar.
        if (WindowState == WindowState.Minimized) Hide();
        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Watching for changes only works while the app is running, so with that on, closing the window
        // puts it in the tray instead of ending the watch without a word - and likewise when the app has
        // been asked to live in the tray from sign-in. Exit on the tray icon still exits. (When Windows
        // or Setup shuts the app down, WPF closes the window regardless of this.)
        if (!_exiting && StaysInTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(6000, "Folder Sync is still running",
                    "It stays in the tray, next to the clock. Right-click the icon and choose Exit to stop it.",
                    WinForms.ToolTipIcon.Info);
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _exiting = true;
        _clock?.Stop();
        _watcher.Dispose();
        _historyWatcher?.Dispose();
        try { _showSignal?.Set(); _showSignal?.Dispose(); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnClosed(e);
    }

    // ===================== Helpers =====================

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageWindow.Warn(Application.Current?.MainWindow, "Could not open", $"{path}\n\n{ex.Message}");
        }
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); } catch { /* logging must not break a sync */ }
    }

    /// <summary>
    /// Appends to the rolling events log (schedule, email and update activity). The lock keeps two
    /// writes from this window from interleaving; SyncConfig owns the file format and the trimming.
    /// </summary>
    private void AppendEvent(string line)
    {
        lock (_logLock) SyncConfig.AppendEvent(line);
    }
}
