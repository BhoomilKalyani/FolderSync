using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace FolderSync;

public partial class MainWindow : Window
{
    private readonly SyncConfig _config;
    private readonly ObservableCollection<SyncTarget> _targets;
    private readonly WinForms.NotifyIcon _tray;
    private readonly object _logLock = new();
    private bool _loaded;

    private CancellationTokenSource? _cts;
    private UpdateInfo? _pendingUpdate;
    private List<RecycleBatch> _batches = new();

    /// <summary>Options for the per-destination Mode column.</summary>
    private const string DeveloperName = "Bhoomil Kalyani";
    private const string DeveloperEmail = "bhoomil.kalyani999@gmail.com";

    private sealed record ModeOption(string Label, bool? Value);

    private static readonly ModeOption[] ModeOptions =
    {
        new("(default)", null),
        new("Mirror", true),
        new("Additive", false),
    };

    private readonly (string task, string label)[] _scheduleTasks =
    {
        (ScheduleManager.DailyTask, "Daily"),
        (ScheduleManager.MonthlyTask, "Monthly"),
        (ScheduleManager.LogonTask, "Logon / unlock"),
    };

    public MainWindow()
    {
        InitializeComponent();

        _config = SyncConfig.Load();
        _targets = new ObservableCollection<SyncTarget>(_config.Targets);
        _targets.CollectionChanged += (_, _) => { SyncTargetsToConfig(); SaveConfig(); };

        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        VersionText.Text = $"Version {Updater.CurrentVersion}";
        AboutVersion.Text = $"Folder Sync {Updater.CurrentVersion}";
        CopyrightText.Text = $"Copyright © {DateTime.Now.Year} {DeveloperName}. All rights reserved.";
        AboutPaths.Text = $"Settings:  {SyncConfig.ConfigPath}\nLogs:      {SyncConfig.LogDir}\nProgram:   {ScheduleManager.ExePath}";

        ModeColumn.ItemsSource = ModeOptions;
        DestGrid.ItemsSource = _targets;
        LoadConfigIntoUi();
        BuildScheduleRows();
        RefreshLastRunSummary();

        _tray = CreateTray();

        _loaded = true;

        if (_config.IsUnconfigured)
            StatusText.Text = "Set a source folder and add at least one destination to begin.";

        Loaded += async (_, _) =>
        {
            WarnAboutStaleTaskTargets();
            if (_config.CheckForUpdates && Updater.IsCheckDue())
                await CheckForUpdatesAsync(manual: false);
        };
    }

    // ===================== Config <-> UI =====================

    private void LoadConfigIntoUi()
    {
        SourceBox.Text = _config.Source;
        MirrorCheck.IsChecked = _config.Mirror;
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
        if (em.HasPassword) EmailStatus.Text = "Password saved.";

        UnlockNote.Text = "“Logon / unlock” syncs when you sign in and each time you unlock the screen, "
                        + $"at most once every {_config.MinMinutesBetweenAutoSyncs} minutes. "
                        + "On a laptop that sleeps rather than shutting down, this is the trigger that keeps things current.";
    }

    private void UpdateRecycleNote() =>
        RecycleNote.Text = MirrorCheck.IsChecked == true
            ? _config.RecycleDeletedFiles
                ? $"Deleted files are kept for {_config.RecycleRetentionDays} days in “{_config.RecycleFolderName}” — see Recycled files."
                : "Deletions are permanent (recycle is turned off in config.json)."
            : "Additive: files are only added or updated, never deleted.";

    private void SyncTargetsToConfig()
    {
        _config.Targets.Clear();
        _config.Targets.AddRange(_targets);
    }

    private void SaveConfig()
    {
        if (!_loaded) return;

        _config.Source = SourceBox.Text.Trim();
        _config.Mirror = MirrorCheck.IsChecked == true;
        _config.ExcludeFiles = SplitList(SkipFilesBox.Text);
        _config.ExcludeFolders = SplitList(SkipFoldersBox.Text);
        SyncTargetsToConfig();

        try { _config.Save(); }
        catch (Exception ex) { AppendEvent("[Config] save failed: " + ex.Message); }
    }

    private static List<string> SplitList(string text) => text
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private void SourceBox_LostFocus(object sender, RoutedEventArgs e) => SaveConfig();
    private void Skip_LostFocus(object sender, RoutedEventArgs e) => SaveConfig();

    private void MirrorCheck_Click(object sender, RoutedEventArgs e)
    {
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

        if (Nav.SelectedIndex == 1) RefreshScheduleStates();
        if (Nav.SelectedIndex == 3) LoadRecycled();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark);
        ThemeToggle.Content = ThemeManager.IsDark ? "Switch to light mode" : "Switch to dark mode";
    }

    // ===================== Source / destinations =====================

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Select the folder to sync from", SourceBox.Text);
        if (picked is null) return;
        SourceBox.Text = picked;
        SaveConfig();
    }

    private void AddDestination_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("Select a folder to sync to", null);
        if (picked is null) return;

        if (_targets.Any(t => string.Equals(t.Destination, picked, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That destination is already in the list.", "Folder Sync");
            return;
        }

        if (PathUtil.SameOrNested(_config.Source, picked))
        {
            MessageBox.Show(this,
                "That folder is the source — or nested inside/around it.\n\n"
                + "With Mirror on, syncing there could delete your files. Pick a different destination.",
                "Unsafe destination", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _targets.Add(new SyncTarget { Name = new DirectoryInfo(picked).Name, Destination = picked, Enabled = true });
    }

    private void RemoveDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DestGrid.SelectedItem is not SyncTarget target) return;

        var answer = MessageBox.Show(this,
            $"Stop syncing to:\n\n{target.Name} — {target.Destination}\n\nThe folder's files are not deleted.",
            "Remove destination", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) _targets.Remove(target);
    }

    private static string? PickFolder(string description, string? startAt)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt)) dlg.SelectedPath = startAt;
        return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
    }

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

        DestGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveConfig();

        if (string.IsNullOrWhiteSpace(_config.Source) || !Directory.Exists(_config.Source))
        {
            MessageBox.Show(this,
                $"The source folder does not exist:\n\n{(string.IsNullOrWhiteSpace(_config.Source) ? "(empty)" : _config.Source)}\n\n"
                + "Please correct it and try again.",
                "Invalid source folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Source folder not found.";
            SourceBox.Focus();
            SourceBox.SelectAll();
            return;
        }

        if (!_config.Targets.Any(t => t.Enabled))
        {
            MessageBox.Show(this,
                "No destinations are enabled, so there is nothing to sync to.\n\n"
                + "Use “Add destination…” to choose a folder, then try again.",
                "No destinations", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Add at least one destination.";
            return;
        }

        var offline = _config.Targets.Where(t => t.Enabled && !PathUtil.DriveAvailable(t.Destination)).ToList();
        if (offline.Count > 0)
        {
            string list = string.Join("\n", offline.Select(t => $"  • {t.Name} — {t.Destination}"));
            var answer = MessageBox.Show(this,
                $"These destinations are not available and will be skipped:\n\n{list}\n\nContinue anyway?",
                "Destination not available", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { StatusText.Text = "Cancelled."; return; }
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = listOnly ? "Previewing…" : "Syncing…";

        var log = new StringBuilder();
        var engine = new SyncEngine(_config);
        engine.Log += line => { lock (_logLock) log.AppendLine(line); };
        engine.Progress += p => Dispatcher.Invoke(() => StatusText.Text = p);

        SyncResult? result = null;
        try
        {
            result = await Task.Run(() => engine.RunAsync(_cts.Token, listOnly));
        }
        catch (OperationCanceledException)
        {
            lock (_logLock) log.AppendLine("CANCELLED by user.");
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            lock (_logLock) log.AppendLine("FATAL: " + ex);
            StatusText.Text = "Failed.";
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }

        if (listOnly)
        {
            TryWrite(SyncConfig.NewPreviewLogPath(), log.ToString());
            SyncConfig.CleanupOldLogs(7);
            if (result is not null) StatusText.Text = "Preview ready (also saved to the log folder).";
            new TextWindow("Preview", "Changes that would be made — nothing has been altered", log.ToString())
            { Owner = this }.ShowDialog();
            return;
        }

        TryWrite(SyncConfig.NewSyncLogPath(), log.ToString());
        SyncConfig.RecordSyncTime();
        SyncConfig.CleanupOldLogs(7);

        if (result is null) return;

        LastRun.Save(result);
        RefreshLastRunSummary();

        StatusText.Text = result.Success
            ? $"Done — {DateTime.Now:HH:mm:ss}."
            : $"Completed with errors — {DateTime.Now:HH:mm:ss}.";

        int recycled = result.Targets.Sum(t => t.FilesRecycled);
        _tray.ShowBalloonTip(5000, "Folder Sync",
            (result.Success ? "Sync completed successfully." : "Sync completed with errors — see the log.")
            + (recycled > 0 ? $" {recycled} deleted item(s) moved to “{_config.RecycleFolderName}”." : ""),
            result.Success ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);
    }

    private void SetBusy(bool busy)
    {
        SyncButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy;
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

        var age = DateTime.Now - last.When;
        string ago = age.TotalDays >= 1 ? $"{age.TotalDays:0.#} day(s) ago"
                   : age.TotalHours >= 1 ? $"{age.TotalHours:0.#} hour(s) ago"
                   : $"{age.TotalMinutes:0} min ago";

        LastRunText.Text = $"{(last.Success ? "Succeeded" : "Completed with errors")} — {last.When:yyyy-MM-dd HH:mm:ss} ({ago})";
        LastRunText.Foreground = (System.Windows.Media.Brush)FindResource(last.Success ? "Ok" : "Bad");
        LastRunGrid.ItemsSource = last.Destinations;
        LastRunGrid.Visibility = last.Destinations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Health_Click(object sender, RoutedEventArgs e)
    {
        DestGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveConfig();
        string report;
        try { report = Diagnostics.Run(_config); }
        catch (Exception ex) { report = "Health check failed: " + ex; }
        new TextWindow("Health check", "Folder Sync health check", report) { Owner = this }.ShowDialog();
    }

    // ===================== Schedule =====================

    private void BuildScheduleRows()
    {
        ScheduleRows.Children.Clear();
        ScheduleRows.Children.Add(new TextBlock
        {
            Text = "Triggers",
            Style = (Style)FindResource("CardTitle"),
        });

        foreach (var (task, label) in _scheduleTasks)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var enable = new Button { Content = "Enable", Tag = task };
            var disable = new Button { Content = "Disable", Tag = task };
            var remove = new Button { Content = "Remove", Tag = task };
            enable.Click += (_, _) => ApplySchedule(() => EnableSchedule(task), $"{label}: enable");
            disable.Click += (_, _) => ApplySchedule(() => ScheduleManager.SetEnabled(task, false), $"{label}: disable");
            remove.Click += (_, _) => ApplySchedule(() => ScheduleManager.Remove(task), $"{label}: remove");
            buttons.Children.Add(enable);
            buttons.Children.Add(disable);
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);

            var state = new TextBlock
            {
                Margin = new Thickness(12, 0, 0, 0),
                Style = (Style)FindResource("Subtle"),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = task,
            };
            Grid.SetColumn(state, 2);
            row.Children.Add(state);

            ScheduleRows.Children.Add(row);
        }

        RefreshScheduleStates();
    }

    private (bool ok, string msg) EnableSchedule(string task)
    {
        if (!ConfirmExePathForScheduling()) return (false, "cancelled");

        var time = ParseTime(TimeBox.Text);
        if (task == ScheduleManager.DailyTask) return ScheduleManager.RegisterDaily(time);
        if (task == ScheduleManager.MonthlyTask)
        {
            int day = int.TryParse(MonthDayBox.Text, out int d) ? Math.Clamp(d, 1, 28) : 1;
            return ScheduleManager.RegisterMonthly(time, day);
        }
        return ScheduleManager.RegisterLogon();
    }

    private static TimeOnly ParseTime(string text) =>
        TimeOnly.TryParse(text, CultureInfo.CurrentCulture, out var t) ? t : new TimeOnly(18, 0);

    private void ApplySchedule(Func<(bool ok, string msg)> action, string what)
    {
        try
        {
            var (ok, msg) = action();
            AppendEvent($"[Schedule] {what}: {(ok ? "OK" : "FAILED")} {msg}");
            if (!ok && !msg.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                MessageBox.Show(this, msg, "Schedule", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Schedule", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshScheduleStates();
    }

    /// <summary>Greys out buttons that don't apply and shows each trigger's state.</summary>
    private void RefreshScheduleStates()
    {
        foreach (var child in ScheduleRows.Children)
        {
            if (child is not Grid row) continue;

            var buttons = row.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<Button>().ToList();
            var state = row.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag is string);
            string? task = buttons?.FirstOrDefault()?.Tag as string;
            if (buttons is null || task is null) continue;

            var st = ScheduleManager.GetState(task);
            buttons[0].IsEnabled = st != ScheduleManager.TaskState.Enabled;
            buttons[1].IsEnabled = st == ScheduleManager.TaskState.Enabled;
            buttons[2].IsEnabled = st != ScheduleManager.TaskState.NotInstalled;

            if (state is not null)
                state.Text = st switch
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

        return MessageBox.Show(this,
            "This copy is running from a build output folder:\n\n"
            + ScheduleManager.ExePath
            + "\n\nA schedule created now would stop working as soon as the project is rebuilt. "
            + "Run the installed copy instead.\n\nCreate it anyway?",
            "Running from a build folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void WarnAboutStaleTaskTargets()
    {
        var stale = new List<(string task, string label, string exe)>();
        foreach (var (task, label) in _scheduleTasks)
        {
            if (!ScheduleManager.Exists(task)) continue;
            var exe = ScheduleManager.GetTaskExe(task);
            if (!string.IsNullOrEmpty(exe) && !File.Exists(exe)) stale.Add((task, label, exe));
        }
        if (stale.Count == 0) return;

        AppendEvent("[Schedule] stale target(s): " + string.Join(" | ", stale.Select(s => $"{s.label} -> {s.exe}")));

        var answer = MessageBox.Show(this,
            "These schedules point at a program file that no longer exists, so they never run:\n\n"
            + string.Join("\n", stale.Select(s => $"  • {s.label} → {s.exe}"))
            + "\n\nRepair them to use this copy?\n\n" + ScheduleManager.ExePath
            + "\n\n(Times and days are kept.)",
            "Schedules need repair", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var s in stale)
        {
            var (ok, msg) = ScheduleManager.Repair(s.task);
            AppendEvent($"[Schedule] repair {s.label}: {(ok ? "OK" : "FAILED")} {msg}");
        }
        RefreshScheduleStates();
        MessageBox.Show(this, "Schedules repaired.", "Folder Sync", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===================== Email =====================

    private void EmailEnable_Click(object sender, RoutedEventArgs e)
    {
        EmailFields.Visibility = EmailEnable.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SaveEmail();
    }

    private void Email_LostFocus(object sender, RoutedEventArgs e) => SaveEmail();
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
        }

        try { _config.Save(); }
        catch (Exception ex) { AppendEvent("[Email] config save failed: " + ex.Message); }
    }

    private void AppPasswordHelp_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Gmail needs a 16-character App Password, not your normal password.\n\n"
            + "1. Turn on 2-Step Verification\n"
            + "2. Create an App Password for “Mail”\n"
            + "3. Paste the 16-character code here (spaces are fine)\n\n"
            + "Each Google account has its own App Password and you can revoke it at any time.\n\n"
            + "Open the App Passwords page now?",
            "How to get an App Password", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) OpenPath("https://myaccount.google.com/apppasswords");
    }

    private void SendTest_Click(object sender, RoutedEventArgs e)
    {
        SaveEmail();
        var em = _config.Email;
        if (!em.HasPassword) { EmailStatus.Text = "Enter the app password first."; return; }

        EmailStatus.Text = "Sending…";
        try
        {
            Emailer.Send(em, "[Folder Sync] Test email",
                $"This is a test from Folder Sync on {Environment.MachineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n\n"
                + "If you received this, notifications are configured correctly.");
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
            MessageBox.Show(this, msg, "Test email failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===================== Recycled files =====================

    private void RefreshRecycled_Click(object sender, RoutedEventArgs e) => LoadRecycled();

    private void LoadRecycled()
    {
        SaveConfig();
        _batches = RecycleStore.GetBatches(_config);
        BatchGrid.ItemsSource = _batches;
        RecycledFileGrid.ItemsSource = null;
        RecycledFilesTitle.Text = "Files";
        RestoreStatus.Text = _batches.Count == 0
            ? "Nothing has been recycled. Files appear here when a mirror sync would otherwise delete them."
            : $"{_batches.Count} batch(es) found.";
        UpdateRestoreButtons();
    }

    private void BatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not RecycleBatch batch)
        {
            RecycledFileGrid.ItemsSource = null;
            UpdateRestoreButtons();
            return;
        }

        var files = RecycleStore.GetFiles(batch);
        RecycledFileGrid.ItemsSource = files;
        RecycledFilesTitle.Text = $"Files in {batch.WhenText} ({batch.DestinationName}) — {files.Count}";
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

        if (string.IsNullOrWhiteSpace(_config.Source) || !Directory.Exists(_config.Source))
        {
            MessageBox.Show(this, "The source folder is not set or doesn't exist, so there is nowhere to restore to.",
                "Cannot restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Restore {files.Count} file(s) into the source folder?\n\n{_config.Source}\n\n"
            + "The next sync will then copy them to every destination again.\n\n"
            + "Files that already exist are skipped — choose No to overwrite them instead.",
            "Restore files", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        bool overwrite = answer == MessageBoxResult.No;
        var result = RecycleStore.Restore(batch.Path, files, _config.Source, overwrite);

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

        var answer = MessageBox.Show(this,
            $"Permanently delete this batch?\n\n{batch.Path}\n\n{batch.FileCount} file(s), {batch.SizeText}.\n\nThis cannot be undone.",
            "Delete batch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var (ok, msg) = RecycleStore.DeleteBatch(batch.Path);
        RestoreStatus.Text = ok ? "Batch deleted." : "Could not delete: " + msg;
        LoadRecycled();
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
        catch (Exception ex) { MessageBox.Show($"Could not open:\n{path}\n\n{ex.Message}"); }
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); } catch { /* logging must not break a sync */ }
    }

    /// <summary>Appends to the rolling events log (schedule, email and update activity).</summary>
    private void AppendEvent(string line)
    {
        try
        {
            Directory.CreateDirectory(SyncConfig.LogDir);
            lock (_logLock)
                File.AppendAllText(Path.Combine(SyncConfig.LogDir, "events.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* not critical */ }
    }
}
