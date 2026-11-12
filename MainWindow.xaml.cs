using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinForms = System.Windows.Forms;

namespace FolderSync;

public partial class MainWindow : Window
{
    private readonly SyncConfig _config;
    private readonly ObservableCollection<SyncPair> _pairs;
    private readonly WinForms.NotifyIcon _tray;
    private readonly object _logLock = new();
    private bool _loaded;

    private CancellationTokenSource? _cts;
    private UpdateInfo? _pendingUpdate;
    private List<RecycleBatch> _batches = new();

    /// <summary>Options for the per-destination Mode column.</summary>
    private const string DeveloperName = "Bhoomil Kalyani";
    private const string DeveloperEmail = "bhoomil.kalyani999@gmail.com";

    // The Mode picker's choices live in MainWindow.xaml as the "ModeOptionList" resource.

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

    public MainWindow()
    {
        InitializeComponent();

        _config = SyncConfig.Load();
        _pairs = new ObservableCollection<SyncPair>(_config.Pairs);
        _pairs.CollectionChanged += (_, _) => { SyncPairsToConfig(); SaveConfig(); UpdateRecycleNote(); };

        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        VersionText.Text = $"Version {Updater.CurrentVersion}";
        AboutVersion.Text = $"Folder Sync {Updater.CurrentVersion}";
        CopyrightText.Text = $"Copyright © {DateTime.Now.Year} {DeveloperName}. All rights reserved.";
        AboutPaths.Text = $"Settings:  {SyncConfig.ConfigPath}\nLogs:      {SyncConfig.LogDir}\nProgram:   {ScheduleManager.ExePath}";

        PairGrid.ItemsSource = _pairs;
        LoadConfigIntoUi();
        RefreshMountedCloud();
        RefreshPairStatuses();
        BuildScheduleRows();
        RefreshLastRunSummary();

        _tray = CreateTray();

        _loaded = true;

        if (_config.IsUnconfigured)
            StatusText.Text = "Set a source folder and add at least one destination to begin.";

        Loaded += async (_, _) =>
        {
            // Started here, not in the constructor: the constructor runs before WPF's message loop has
            // a synchronisation context, so the code after each background await resumed on a worker
            // thread and failed touching the controls ("a different thread owns it").
            RefreshScheduleStates();
            _ = LoadScheduleTimesFromTasksAsync();

            if (_config.LoadProblem is { } problem)
                MessageWindow.Warn(this, "Settings could not be read",
                    problem + "\n\nScheduled syncs will report a failure until the folder pairs are set up "
                    + "again. The settings folder is listed on the About page.");

            await WarnAboutStaleTaskTargetsAsync();
            if (_config.CheckForUpdates && Updater.IsCheckDue())
                await CheckForUpdatesAsync(manual: false);
        };
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
        EmailModeBox.SelectedIndex = em.Mode switch { FolderSync.EmailMode.OnError => 1, FolderSync.EmailMode.OnSuccess => 2, _ => 0 };
        UpdatePasswordHint();

        UnlockNote.Text = "“Logon / unlock” syncs when you sign in and each time you unlock the screen, "
                        + $"at most once every {_config.MinMinutesBetweenAutoSyncs} minutes. "
                        + "On a laptop that sleeps rather than shutting down, this is the trigger that keeps things current.";
    }

    private void UpdateRecycleNote()
    {
        var deleting = _pairs.Where(p => p.Enabled && p.DeletesExtras).ToList();
        RecycleNote.Text = deleting.Count == 0
            ? "No enabled pair deletes anything right now."
            : _config.RecycleDeletedFiles
                ? $"{deleting.Count} pair(s) mirror, so deletions carry over — removed files are kept "
                  + $"for {_config.RecycleRetentionDays} days in “{_config.RecycleFolderName}” (see Recycled files)."
                : $"{deleting.Count} pair(s) mirror and deletions are PERMANENT (recycle is off in config.json).";
    }

    /// <summary>
    /// Copies the grid's pairs into the config as a new list. Never clear-and-refill in place: the health
    /// check, the recycled-files scan and a sync all read the config's list on a background thread, and
    /// changing that list under them throws "Collection was modified". A fresh list leaves any reader
    /// with the one it started on.
    /// </summary>
    private void SyncPairsToConfig() => _config.Pairs = _pairs.ToList();

    /// <summary>
    /// Fills in each pair's "Last run" column from the stored per-pair history, so a pair that has
    /// been quietly failing is obvious without opening a log.
    /// </summary>
    private void RefreshPairStatuses()
    {
        var history = PairHistory.Load();
        foreach (var pair in _pairs)
        {
            pair.LastRunText = history.TryGetValue(PairHistory.KeyFor(pair.Left, pair.Right), out var status)
                ? status.Text
                : "Never run";
        }
    }

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

    private void Skip_LostFocus(object sender, RoutedEventArgs e) => SaveConfig();

    /// <summary>A pair was switched on or off.</summary>
    private void EnabledCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        SaveConfig();
        UpdateRecycleNote();
    }

    private void DestGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(SaveConfig));

    // ===================== Navigation / theme =====================

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSync is null) return;   // during construction

        var pages = new[] { PageSync, PageSchedule, PageEmail, PageRecycled, PageAbout };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;

        switch (Nav.SelectedIndex)
        {
            case 0: RefreshMountedCloud(); break;   // a provider may have been installed since
            case 1: RefreshScheduleStates(); break;
            case 3: LoadRecycled(); break;
        }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark);
        ThemeToggle.Content = ThemeManager.IsDark ? "Switch to light mode" : "Switch to dark mode";
    }

    // ===================== Folder pairs =====================

    private void AddPair_Click(object sender, RoutedEventArgs e)
    {
        // Additive by default: a new pair cannot delete anything until Mirror is ticked.
        var pair = new SyncPair { Direction = SyncDirection.ToRight, Mirror = false, Enabled = true };
        var dlg = new PairWindow(pair, isNew: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (_pairs.Any(p => SamePair(p, pair)))
        {
            MessageWindow.Info(this, "Already added", "A pair with those two folders already exists.");
            return;
        }
        _pairs.Add(pair);
        PairGrid.SelectedItem = pair;
        RefreshMountedCloud();   // the new pair may cover a detected cloud folder
    }

    private void EditPair_Click(object sender, RoutedEventArgs e) => EditSelectedPair();

    private void PairGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        EditSelectedPair();

    private void EditSelectedPair()
    {
        if (PairGrid.SelectedItem is not SyncPair pair)
        {
            MessageWindow.Info(this, "No pair selected", "Select a pair in the list first.");
            return;
        }

        var dlg = new PairWindow(pair, isNew: false) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // The pair object edits in place and raises change notifications, so the grid follows.
        SaveConfig();
        UpdateRecycleNote();
        RefreshMountedCloud();
    }

    private void RemovePair_Click(object sender, RoutedEventArgs e)
    {
        if (PairGrid.SelectedItem is not SyncPair pair)
        {
            MessageWindow.Info(this, "No pair selected", "Select a pair in the list first.");
            return;
        }

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

    private async void Sync_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(listOnly: false);
    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(listOnly: true);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling…";
    }

    private async Task RunSyncAsync(bool listOnly)
    {
        if (_cts is not null) return;   // already running

        SaveConfig();

        var active = _config.ActivePairs.ToList();
        if (active.Count == 0)
        {
            MessageWindow.Warn(this, "Nothing to sync",
                "No folder pair is enabled, so there is nothing to do.\n\n"
                + "Use “Add pair…” to set up two folders, then try again.");
            Status = "Add at least one folder pair.";
            return;
        }

        // An unplugged drive is not a problem to be warned about - the pair just waits for it - so it
        // is separated out from genuine faults rather than being mixed into an alarming list.
        var offline = active.Where(p => p.MissingDrive() is not null).ToList();
        var runnable = active.Except(offline).ToList();

        // Uses the pair's own rule, so this warning always matches what the engine will actually do.
        var broken = runnable.Select(p => (pair: p, problem: p.ValidateFolders()))
                             .Where(x => x.problem is not null)
                             .ToList();

        if (broken.Count > 0)
        {
            string list = string.Join("\n", broken.Select(x => $"  • {x.pair.DisplayName} — {x.problem}"));

            if (broken.Count == runnable.Count)
            {
                MessageWindow.Warn(this, "Nothing can be synced",
                    $"Every pair that could run has a problem:\n\n{list}");
                Status = "Fix the folder pairs and try again.";
                return;
            }

            if (!MessageWindow.Confirm(this, "Some pairs cannot run",
                    $"These pairs will be skipped:\n\n{list}\n\nContinue with the rest?",
                    primary: "Sync anyway", tone: DialogTone.Warning))
            {
                Status = "Cancelled.";
                return;
            }
        }
        else if (offline.Count == active.Count)
        {
            MessageWindow.Info(this, "Nothing to sync yet",
                "Every enabled pair uses a drive that is not connected:\n\n"
                + string.Join("\n", offline.Select(p => $"  • {p.DisplayName} — {p.MissingDrive()}"))
                + "\n\nConnect the drive and sync again. This is not an error.");
            Status = "Waiting for a drive to be connected.";
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Status = listOnly ? "Previewing…" : "Syncing…";

        var log = new StringBuilder();
        var engine = new SyncEngine(_config);
        engine.Log += line => { lock (_logLock) log.AppendLine(line); };
        engine.Progress += p => Dispatcher.Invoke(() => Status = p);

        SyncResult? result = null;
        bool busyElsewhere = false;
        var token = _cts.Token;
        try
        {
            // A real sync takes the cross-process lock, so it cannot overlap a scheduled run over the
            // same folders. Preview changes nothing and needs no lock.
            result = listOnly
                ? await Task.Run(() => engine.RunAsync(token, listOnly: true))
                : await Task.Run(() => SyncLock.Run(
                      () => engine.RunAsync(token).GetAwaiter().GetResult(), TimeSpan.Zero));
        }
        catch (SyncAlreadyRunningException)
        {
            busyElsewhere = true;      // nothing ran, so there is no log or result to record
        }
        catch (OperationCanceledException)
        {
            lock (_logLock) log.AppendLine("CANCELLED by user.");
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            lock (_logLock) log.AppendLine("FATAL: " + ex);
            Status = "Failed.";
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }

        if (busyElsewhere)
        {
            Status = "A scheduled sync is running right now — try again when it has finished.";
            return;
        }

        if (listOnly)
        {
            TryWrite(SyncConfig.NewPreviewLogPath(), log.ToString());
            SyncConfig.CleanupOldLogs(7);
            if (result is not null) Status = "Preview ready (also saved to the log folder).";
            new TextWindow("Preview", "Changes that would be made — nothing has been altered", log.ToString())
            { Owner = this }.ShowDialog();
            return;
        }

        TryWrite(SyncConfig.NewSyncLogPath(), log.ToString());
        SyncConfig.RecordSyncTime();
        SyncConfig.CleanupOldLogs(7);

        if (result is null) return;

        LastRun.Save(result);
        PairHistory.Record(result);
        RefreshLastRunSummary();
        RefreshPairStatuses();

        int skipped = result.Targets.Count(t => t.Skipped);
        string skippedNote = skipped > 0 ? $" {skipped} skipped (drive not connected)." : "";

        Status = result.Success
            ? $"Done — {DateTime.Now:HH:mm:ss}.{skippedNote}"
            : $"Completed with errors — {DateTime.Now:HH:mm:ss}.{skippedNote}";

        int recycled = result.Targets.Sum(t => t.FilesRecycled);
        _tray.ShowBalloonTip(5000, "Folder Sync",
            (result.Success ? "Sync completed successfully." : "Sync completed with errors — see the log.")
            + (recycled > 0 ? $" {recycled} deleted item(s) moved to “{_config.RecycleFolderName}”." : ""),
            result.Success ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);
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
    }

    private void RefreshLastRunSummary()
    {
        var last = LastRun.Load();
        if (last is null)
        {
            LastRunText.Text = "No sync recorded yet.";
            LastRunGrid.Visibility = Visibility.Collapsed;
            return;
        }

        // The shared formatter, so this card, the pairs grid and the health check describe a run alike.
        LastRunText.Text = $"{(last.Success ? "Succeeded" : "Completed with errors")} — "
                         + $"{last.When:yyyy-MM-dd HH:mm:ss} ({Humanise.Ago(last.When)})";
        LastRunText.Foreground = (System.Windows.Media.Brush)FindResource(last.Success ? "Ok" : "Bad");
        LastRunGrid.ItemsSource = last.Destinations;
        LastRunGrid.Visibility = last.Destinations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _healthRunning;

    private async void Health_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null || _healthRunning) return;   // a sync or another check is running
        SaveConfig();

        // The report launches half a dozen schtasks processes and walks every folder pair, which is
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
        // States are filled in once the window has loaded; see the Loaded handler.
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

    /// <summary>
    /// Shows the time a registered task actually runs at, rather than a stale default. Queried off the
    /// UI thread; never throws, since the caller discards the task.
    /// </summary>
    private async Task LoadScheduleTimesFromTasksAsync()
    {
        try
        {
            var (daily, monthly) = await Task.Run(() => (
                ScheduleManager.GetCalendarSchedule(ScheduleManager.DailyTask),
                ScheduleManager.GetCalendarSchedule(ScheduleManager.MonthlyTask)));

            if (daily.time is { } dt) SelectTime(_dailyHour, _dailyMinute, dt);
            if (monthly.time is { } mt) SelectTime(_monthlyHour, _monthlyMinute, mt);
            if (monthly.day is { } day) Select(_monthlyDay, day);
        }
        catch (Exception ex)
        {
            AppendEvent("[Schedule] could not read the schedule times: " + ex.Message);
        }
    }

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

    /// <summary>
    /// Greys out buttons that don't apply and shows each trigger's state. Each state is a schtasks
    /// process, so they are queried off the UI thread: at startup these queries (with the schedule times
    /// and the stale-target check) launched up to eleven processes one after another and held the window.
    /// Never throws - callers discard the task.
    /// </summary>
    private void RefreshScheduleStates() => _ = RefreshScheduleStatesAsync();

    private async Task RefreshScheduleStatesAsync()
    {
        var rows = _rows.ToList();
        List<ScheduleManager.TaskState> states;
        try
        {
            states = await Task.Run(() => rows.Select(r => ScheduleManager.GetState(r.Task)).ToList());
        }
        catch (Exception ex)
        {
            AppendEvent("[Schedule] could not read the schedules: " + ex.Message);
            foreach (var row in rows) row.State.Text = "could not be read";
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var st = states[i];
            row.Enable.IsEnabled = st != ScheduleManager.TaskState.Enabled;
            row.Disable.IsEnabled = st == ScheduleManager.TaskState.Enabled;
            row.Remove.IsEnabled = st != ScheduleManager.TaskState.NotInstalled;
            row.State.Text = st switch
            {
                ScheduleManager.TaskState.Enabled => "enabled",
                ScheduleManager.TaskState.Disabled => "installed but disabled",
                _ => "not installed",
            };
        }
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

    private async Task WarnAboutStaleTaskTargetsAsync()
    {
        var tasks = _scheduleTasks;
        List<(string task, string label, string exe)> stale;
        try
        {
            // Six schtasks processes at most: off the UI thread, so the window is usable meanwhile.
            stale = await Task.Run(() =>
            {
                var found = new List<(string task, string label, string exe)>();
                foreach (var (task, label) in tasks)
                {
                    if (!ScheduleManager.Exists(task)) continue;
                    var exe = ScheduleManager.GetTaskExe(task);
                    if (!string.IsNullOrEmpty(exe) && !File.Exists(exe)) found.Add((task, label, exe));
                }
                return found;
            });
        }
        catch (Exception ex)
        {
            AppendEvent("[Schedule] could not check the schedules' targets: " + ex.Message);
            return;
        }
        if (stale.Count == 0) return;

        AppendEvent("[Schedule] stale target(s): " + string.Join(" | ", stale.Select(s => $"{s.label} -> {s.exe}")));

        if (!MessageWindow.Confirm(this, "Schedules need repair",
                "These schedules point at a program file that no longer exists, so they never run:\n\n"
                + string.Join("\n", stale.Select(s => $"  • {s.label} → {s.exe}"))
                + "\n\nRepair them to use this copy?\n\n" + ScheduleManager.ExePath
                + "\n\nTimes and days are kept as they are.",
                primary: "Repair", tone: DialogTone.Warning))
            return;

        foreach (var s in stale)
        {
            var (ok, msg) = ScheduleManager.Repair(s.task);
            AppendEvent($"[Schedule] repair {s.label}: {(ok ? "OK" : "FAILED")} {msg}");
        }
        RefreshScheduleStates();
        MessageWindow.Info(this, "Schedules repaired", "They now point at this copy of Folder Sync.");
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
    private void EmailMode_Changed(object sender, SelectionChangedEventArgs e) => SaveEmail();

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
        em.Mode = EmailModeBox.SelectedIndex switch { 1 => FolderSync.EmailMode.OnError, 2 => FolderSync.EmailMode.OnSuccess, _ => FolderSync.EmailMode.Always };

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
                : $"{_batches.Count} batch(es) found.";
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
            $"Restore {files.Count} file(s) into:\n\n{restoreTo}{caution}",
            primary: "Skip existing", secondary: "Overwrite existing");
        if (choice == DialogChoice.Cancel) return;

        bool overwrite = choice == DialogChoice.Secondary;
        var result = RecycleStore.Restore(batch.Path, files, restoreTo, overwrite);

        RestoreStatus.Text = $"Restored {result.Restored}, skipped {result.Skipped}"
                           + (result.Errors.Count > 0 ? $", {result.Errors.Count} error(s)." : ".");
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
                $"Permanently delete this batch?\n\n{batch.Path}\n\n{batch.FileCount} file(s), {batch.SizeText}.\n\n"
                + "This cannot be undone.",
                primary: "Delete", tone: DialogTone.Danger))
            return;

        var (ok, msg) = RecycleStore.DeleteBatch(batch.Path);
        RestoreStatus.Text = ok ? "Batch deleted." : "Could not delete: " + msg;
        LoadRecycled();
    }

    // ===================== Cloud storage mounted as a folder =====================

    private List<CloudFolder> _mountedCloud = new();

    /// <summary>
    /// Shows the cloud services whose desktop app already exposes them as a folder, so syncing to
    /// Google Drive or OneDrive is a couple of clicks with no sign-in at all. This is the only cloud
    /// route the app has: see <see cref="CloudFolders"/> for why talking to a provider's API was
    /// tried and dropped.
    /// </summary>
    private void RefreshMountedCloud()
    {
        try { _mountedCloud = CloudFolders.Detect(); }
        catch (Exception ex) { _mountedCloud = new List<CloudFolder>(); AppendEvent("[Cloud] detect failed: " + ex.Message); }

        MountedCloudList.ItemsSource = _mountedCloud
            .Select(f => new CloudFolderRow
            {
                Provider = f.Provider,
                Path = f.Path,
                InUse = _pairs.Any(p => Covers(p, f.Path)),
            })
            .ToList();

        // Stays visible even with nothing found: the note below the list explains that installing a
        // cloud app is all it takes for one to show up.
        MountedCloudCard.Visibility = Visibility.Visible;
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

        var pair = new SyncPair
        {
            Right = folder,
            Direction = SyncDirection.ToRight,
            Mirror = false,      // additive until the user chooses otherwise
            Enabled = true,
        };

        var dlg = new PairWindow(pair, isNew: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (_pairs.Any(p => SamePair(p, pair)))
        {
            MessageWindow.Info(this, "Already added", "A pair with those two folders already exists.");
            return;
        }

        _pairs.Add(pair);
        PairGrid.SelectedItem = pair;
        RefreshMountedCloud();
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
        var dlg = new UpdateWindow(_pendingUpdate) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _tray.Visible = false;
            Application.Current.Shutdown();
        }
    }

    // ===================== Tray =====================

    private WinForms.NotifyIcon CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sync now", null, async (_, _) => await RunSyncAsync(listOnly: false));
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _tray!.Visible = false; Application.Current.Shutdown(); });

        var tray = new WinForms.NotifyIcon
        {
            Icon = AppResources.TrayIcon,
            Text = "Folder Sync",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        return tray;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        // Minimising sends the window to the tray rather than the taskbar.
        if (WindowState == WindowState.Minimized) Hide();
        base.OnStateChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
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
