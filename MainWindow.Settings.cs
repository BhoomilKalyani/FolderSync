using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace FolderSync;

/// <summary>
/// The Settings and Notifications pages, and the things they switch on: watching folders for changes,
/// starting with Windows, and speaking up when a sync nobody was watching goes wrong.
///
/// <para>These settings used to exist only in config.json, reachable through "Open config file". Each
/// one here saves as it is changed, like the rest of the window.</para>
/// </summary>
public partial class MainWindow
{
    private readonly ChangeWatcher _watcher = new();
    private FileSystemWatcher? _historyWatcher;
    private DispatcherTimer? _clock;
    private int _clockTicks;

    /// <summary>Runs this window recorded itself, so the folder watcher can tell them from a scheduled run's.</summary>
    private readonly HashSet<string> _ownRuns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs already reacted to: a file can raise more than one event.</summary>
    private readonly HashSet<string> _seenRuns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while the controls are being filled in, so that does not count as the user changing them.</summary>
    private bool _fillingSettings;

    // ===================== Loading =====================

    private void LoadSettingsIntoUi()
    {
        _fillingSettings = true;
        try
        {
            WatchCheck.IsChecked = _config.SyncOnChange;
            Fill(WatchDelayBox, new[] { 15, 30, 60, 120, 300, 600 }, _config.SyncOnChangeDelaySeconds, DelayText);
            StartupCheck.IsChecked = StartupRegistration.IsEnabled();
            Fill(MinGapBox, new[] { 0, 5, 10, 15, 30, 60, 120 }, _config.MinMinutesBetweenAutoSyncs);

            RecycleCheck.IsChecked = _config.RecycleDeletedFiles;
            Fill(RecycleDaysBox, new[] { 7, 14, 30, 60, 90, 180, 365 }, _config.RecycleRetentionDays);
            GuardCheck.IsChecked = _config.GuardLargeDeletions;
            Fill(GuardPercentBox, new[] { 10, 25, 50, 75, 90 }, _config.LargeDeletionPercent);
            Fill(GuardMinFilesBox, new[] { 1, 5, 10, 25, 50, 100, 500 }, Math.Max(1, _config.LargeDeletionMinimumFiles));
            TwoWayKeepCheck.IsChecked = _config.BackupOverwrittenFiles;

            Fill(HistoryDaysBox, new[] { 30, 90, 180, 365, 730 }, _config.HistoryRetentionDays);
            Fill(LogDaysBox, new[] { 7, 14, 30, 60, 90 }, _config.LogRetentionDays);

            SelectTheme();
            UpdateCheck.IsChecked = _config.CheckForUpdates;

            NotifyFailureCheck.IsChecked = _config.NotifyOnFailure;
            OverdueCheck.IsChecked = _config.OverdueAfterDays > 0;
            Fill(OverdueDaysBox, new[] { 1, 2, 3, 5, 7, 14, 30 },
                 _config.OverdueAfterDays > 0 ? _config.OverdueAfterDays : SyncConfig.DefaultOverdueAfterDays);

            ShowSettingsState();
        }
        finally
        {
            _fillingSettings = false;
        }
    }

    /// <summary>
    /// Fills a drop-down with a short list of sensible values and selects the current one. A value that
    /// is not on the list - set by hand in config.json - is added rather than silently replaced.
    /// </summary>
    private static void Fill(ComboBox box, int[] presets, int current, Func<int, string>? text = null)
    {
        var values = presets.Contains(current) ? presets : presets.Append(current).OrderBy(v => v).ToArray();

        box.Items.Clear();
        foreach (int value in values)
        {
            var item = new ComboBoxItem { Content = text?.Invoke(value) ?? value.ToString(), Tag = value };
            box.Items.Add(item);
            if (value == current) box.SelectedItem = item;
        }
    }

    private static int Chosen(ComboBox box, int fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag is int value ? value : fallback;

    private static string DelayText(int seconds) =>
        seconds < 60 ? Humanise.Count(seconds, "second")
        : seconds % 60 == 0 ? Humanise.Count(seconds / 60, "minute")
        : $"{seconds} seconds";

    private void SelectTheme()
    {
        bool was = _fillingSettings;
        _fillingSettings = true;
        ThemeBox.SelectedIndex = _config.ThemeChoice switch { AppTheme.Light => 1, AppTheme.Dark => 2, _ => 0 };
        _fillingSettings = was;
    }

    /// <summary>Greys out what depends on a switch that is off, and shows what the switches currently mean.</summary>
    private void ShowSettingsState()
    {
        WatchDelayBox.IsEnabled = WatchCheck.IsChecked == true;

        bool recycling = RecycleCheck.IsChecked == true;
        RecycleDaysBox.IsEnabled = recycling;
        RecycleOffWarning.Visibility = recycling ? Visibility.Collapsed : Visibility.Visible;
        TwoWayKeepCheck.IsEnabled = recycling;      // kept versions live in the recycle folder

        bool guarding = GuardCheck.IsChecked == true;
        GuardPercentBox.IsEnabled = guarding;
        GuardMinFilesBox.IsEnabled = guarding;

        OverdueDaysBox.IsEnabled = OverdueCheck.IsChecked == true;
        ShowWatchStatus();
    }

    // ===================== Saving =====================

    private void Setting_Click(object sender, RoutedEventArgs e) => SaveSettings();
    private void Setting_Changed(object sender, SelectionChangedEventArgs e) => SaveSettings();
    private void NotifySetting_Click(object sender, RoutedEventArgs e) => SaveSettings();
    private void NotifySetting_Changed(object sender, SelectionChangedEventArgs e) => SaveSettings();

    // The tick-boxes are handled on Checked/Unchecked rather than Click: a screen reader (or anything else
    // driving the window through UI Automation) changes a tick-box without clicking it, and a setting
    // that only saved on a click would then look changed and not be.

    private void WatchSetting_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _fillingSettings) return;
        SaveSettings();
        ApplyWatchSettings();
    }

    /// <summary>Puts a tick-box back without that counting as the user changing it.</summary>
    private void Revert(CheckBox box, bool value)
    {
        bool was = _fillingSettings;
        _fillingSettings = true;
        box.IsChecked = value;
        _fillingSettings = was;
    }

    private void WatchSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _fillingSettings) return;
        SaveSettings();
        ApplyWatchSettings();
    }

    private void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _fillingSettings) return;
        if (RecycleCheck.IsChecked == true) { SaveSettings(); return; }

        // Turning this off is the one setting that makes the app destructive, so it asks first - but
        // only once this event has returned. Whatever changed the tick-box is still in the middle of
        // doing so; a question opened from in here would leave a screen reader waiting on an answer it
        // has no way to give.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (RecycleCheck.IsChecked == true) return;      // ticked again in the meantime

            if (!MessageWindow.Confirm(this, "Turn the recycle folder off?",
                    "A mirror will then delete files for good when they are removed from the sending folder, "
                    + "and no pair will be able to keep the previous version of a changed file.\n\n"
                    + "Files already in the recycle folder stay there until they expire.",
                    primary: "Turn it off", tone: DialogTone.Danger))
                Revert(RecycleCheck, true);

            SaveSettings();
        }));
    }

    private void Guard_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _fillingSettings) return;
        if (GuardCheck.IsChecked == true) { SaveSettings(); return; }

        // Asked after the event has returned, for the same reason as above.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (GuardCheck.IsChecked == true) return;

            if (!MessageWindow.Confirm(this, "Turn the deletion guard off?",
                    "A mirror will then remove whatever the sending folder no longer has, however much that is — "
                    + "including everything, if the sending folder is empty or not available for a moment.",
                    primary: "Turn it off", tone: DialogTone.Warning))
                Revert(GuardCheck, true);

            SaveSettings();
        }));
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _fillingSettings) return;

        bool wanted = StartupCheck.IsChecked == true;
        var (ok, message) = StartupRegistration.Set(wanted);
        AppendEvent($"[Startup] start at sign-in {(wanted ? "on" : "off")}: {(ok ? "OK" : "FAILED " + message)}");
        if (!ok)
        {
            Revert(StartupCheck, StartupRegistration.IsEnabled());
            Dispatcher.BeginInvoke(new Action(() => MessageWindow.Warn(this, "Could not change this", message)));
        }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _fillingSettings) return;
        _config.Theme = ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        ApplyTheme();
        SaveConfig();
    }

    /// <summary>Copies every control on the two pages into the settings and saves them.</summary>
    private void SaveSettings()
    {
        if (!_loaded || _fillingSettings) return;

        _config.SyncOnChange = WatchCheck.IsChecked == true;
        _config.SyncOnChangeDelaySeconds = Chosen(WatchDelayBox, _config.SyncOnChangeDelaySeconds);
        _config.MinMinutesBetweenAutoSyncs = Chosen(MinGapBox, _config.MinMinutesBetweenAutoSyncs);

        _config.RecycleDeletedFiles = RecycleCheck.IsChecked == true;
        _config.RecycleRetentionDays = Chosen(RecycleDaysBox, _config.RecycleRetentionDays);
        _config.GuardLargeDeletions = GuardCheck.IsChecked == true;
        _config.LargeDeletionPercent = Chosen(GuardPercentBox, _config.LargeDeletionPercent);
        _config.LargeDeletionMinimumFiles = Chosen(GuardMinFilesBox, _config.LargeDeletionMinimumFiles);
        _config.BackupOverwrittenFiles = TwoWayKeepCheck.IsChecked == true;

        _config.HistoryRetentionDays = Chosen(HistoryDaysBox, _config.HistoryRetentionDays);
        _config.LogRetentionDays = Chosen(LogDaysBox, _config.LogRetentionDays);
        _config.CheckForUpdates = UpdateCheck.IsChecked == true;

        _config.NotifyOnFailure = NotifyFailureCheck.IsChecked == true;
        _config.OverdueAfterDays = OverdueCheck.IsChecked == true
            ? Chosen(OverdueDaysBox, SyncConfig.DefaultOverdueAfterDays)
            : 0;

        SaveConfig();

        // Other pages describe these settings in words.
        ShowSettingsState();
        UpdateRecycleNote();
        UpdateUnlockNote();
        RefreshHome();
    }

    // ===================== Sync when files change =====================

    /// <summary>Starts, stops or re-targets the folder watching to match the settings and the pairs.</summary>
    private void ApplyWatchSettings()
    {
        if (!_loaded) return;
        try { _watcher.Configure(_config); }
        catch (Exception ex) { AppendEvent("[Watch] could not be set up: " + ex.Message); }
        ShowWatchStatus();
        RefreshHome();      // "Next: … when files change"
    }

    private void ShowWatchStatus()
    {
        if (!_loaded) return;
        string status = _watcher.StatusText;
        WatchStatusText.Text = _watcher.Enabled ? status : "";
        WatchStatusText.Visibility = _watcher.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ScheduleWatchNote.Text = _watcher.Enabled
            ? $"On. {status} A sync starts {DelayText(_config.SyncOnChangeDelaySeconds)} after the last change, "
              + "for as long as Folder Sync is running."
            : "Off. Folder Sync can also sync shortly after a file changes in a folder a pair copies from. "
              + "Turn it on in Settings.";
    }

    /// <summary>Files changed in a watched folder and have settled: sync the pairs that read from it.</summary>
    private void OnWatchedFoldersChanged(IReadOnlyCollection<string> pairKeys)
    {
        if (_exiting || !_config.SyncOnChange) return;
        _ = RunSyncAsync(listOnly: false, RunTrigger.FileChange, onlyPairs: pairKeys.ToList());
    }

    // ===================== Noticing a scheduled run =====================

    /// <summary>
    /// Watches the history folder, so the window follows a sync run by a scheduled task: the pair
    /// statuses and the Home page update, and a failure raises a notification from this window's own
    /// tray icon - which, unlike the one a scheduled run can show, stays in the notification list.
    /// </summary>
    private void WatchHistoryFolder()
    {
        try
        {
            Directory.CreateDirectory(RunHistoryStore.DefaultFolder);
            _historyWatcher = new FileSystemWatcher(RunHistoryStore.DefaultFolder, RunHistoryStore.FilePrefix + "*.json")
            {
                NotifyFilter = NotifyFilters.FileName,
            };
            // A record is written under a temporary name and renamed when complete, so it arrives as a
            // rename; "created" is handled too in case that ever changes.
            _historyWatcher.Created += (_, e) => Dispatcher.BeginInvoke(new Action(() => OnRunRecorded(e.FullPath)));
            _historyWatcher.Renamed += (_, e) => Dispatcher.BeginInvoke(new Action(() => OnRunRecorded(e.FullPath)));
            _historyWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            AppendEvent("[History] the folder could not be watched: " + ex.Message);
        }
    }

    private void OnRunRecorded(string file)
    {
        if (_exiting) return;
        string id = Path.GetFileNameWithoutExtension(file);
        if (_ownRuns.Contains(id) || !_seenRuns.Add(id)) return;

        // A run by another process - a scheduled task - has just finished.
        RefreshPairStatuses();
        _ = RefreshHomeAsync();
        if (Nav.SelectedIndex == NavHistory) _ = LoadHistoryAsync();

        if (RunHistoryStore.Read(file) is { } record) ShowAlertFor(record);
        CheckForOverduePairs();
    }

    // ===================== Notifications =====================

    /// <summary>Raises a notification for a run nobody was watching, if it needs one and it is not a repeat.</summary>
    private void ShowAlertFor(RunRecord record)
    {
        try
        {
            if (Alerts.ForRun(record, _config) is not { } alert) return;

            var memory = AlertMemory.Load();
            if (!Alerts.ShouldShow(alert, memory, DateTime.Now)) return;
            memory.Save();
            ShowAlert(alert);
        }
        catch (Exception ex)
        {
            AppendEvent("[Notify] could not show a notification: " + ex.Message);
        }
    }

    /// <summary>Warns about pairs that have gone too long without a successful sync - at most once a day each.</summary>
    private void CheckForOverduePairs()
    {
        try
        {
            var memory = AlertMemory.Load();
            if (Alerts.Overdue(_pairs.ToList(), PairHistory.Load(), _config, DateTime.Now, memory) is not { } alert) return;
            memory.Save();
            ShowAlert(alert);
        }
        catch (Exception ex)
        {
            AppendEvent("[Notify] could not check for overdue pairs: " + ex.Message);
        }
    }

    private void ShowAlert(Alert alert)
    {
        _tray.ShowBalloonTip(10000, alert.Title, alert.Text,
                             alert.IsError ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        AppendEvent($"[Notify] {alert.Title}: {alert.Text.Replace('\n', ' ')}");
    }

    /// <summary>
    /// Once a minute while the app runs: keeps "10 minutes ago" true, and every half hour looks for
    /// pairs that have become overdue while nothing was syncing.
    /// </summary>
    private void StartClock()
    {
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) =>
        {
            if (_exiting) return;
            if (IsVisible && _cts is null)
            {
                RefreshPairStatuses();
                RefreshHome();
            }
            if (++_clockTicks % 30 == 0) CheckForOverduePairs();
        };
        _clock.Start();
    }
}
