using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace FolderSync;

public sealed class MainForm : Form
{
    private readonly SyncConfig _config;
    private readonly BindingList<SyncTarget> _targets;
    private bool _loaded;

    private readonly TextBox _sourceBox;
    private readonly CheckBox _mirrorCheck;
    private readonly DataGridView _destGrid;
    private readonly TextBox _excludeFilesBox;
    private readonly TextBox _excludeFoldersBox;
    private readonly DataGridView _lastRunGrid;
    private readonly Label _lastRunLabel;
    private readonly Button _syncButton;
    private readonly Button _previewButton;
    private readonly Button _cancelButton;
    private readonly ProgressBar _progress;
    private readonly object _logLock = new();
    private readonly Label _statusLabel;
    private readonly NotifyIcon _tray;
    private readonly LinkLabel _updateLink;
    private UpdateInfo? _pendingUpdate;
    private readonly CheckBox _schedEnable;
    private readonly DateTimePicker _timePicker;
    private readonly NumericUpDown _monthDay;

    private sealed record SchedRow(string TaskName, Button Enable, Button Disable, Button Remove);
    private readonly List<SchedRow> _scheduleRows = new();

    private TableLayoutPanel _emailDetails = null!;

    private readonly CheckBox _emailEnable;
    private readonly TextBox _emailFrom;
    private readonly TextBox _emailPass;
    private readonly TextBox _emailTo;
    private readonly TextBox _emailHost;
    private readonly NumericUpDown _emailPort;
    private readonly ComboBox _emailMode;
    private readonly Label _emailStatus;
    private readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 200, ReshowDelay = 100 };

    private const string AppPasswordHelpText =
        "Gmail needs a 16-character App Password (NOT your normal password).\r\n\r\n" +
        "1. Turn ON 2-Step Verification:\r\n     myaccount.google.com/signinoptions/twosv\r\n" +
        "2. Create an App Password (app = Mail):\r\n     myaccount.google.com/apppasswords\r\n" +
        "3. Paste the 16-character code here (spaces are fine).\r\n\r\n" +
        "Each Google account has its own App Password; you can revoke it anytime.\r\n" +
        "Click this icon to open the App Passwords page.";

    private CancellationTokenSource? _cts;

    public MainForm()
    {
        _config = SyncConfig.Load();
        // BindingList wraps the existing list, so add/remove/edit flow straight back into _config.Targets.
        _targets = new BindingList<SyncTarget>(_config.Targets);

        Text = $"Folder Sync {Updater.CurrentVersion}";
        Icon = AppResources.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(760, 880);
        MinimumSize = new Size(720, 400);

        // Form-level AutoScroll: when the content stack is taller than the window, a
        // vertical scrollbar appears. (AutoScroll on the Form is more reliable than on a child panel.)
        AutoScroll = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // config
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // sync row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // last sync summary
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // schedule
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // email
        Controls.Add(root);

        // ===== Configuration (editable) =====
        var info = new GroupBox { Text = "Configuration", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var infoLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 7 };
        infoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        infoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        infoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Explicit auto-height rows: without these a TableLayoutPanel can under-measure and clip
        // its last row (the bug that hid the email panel's bottom row).
        for (int i = 0; i < 7; i++) infoLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Row 0: Source
        infoLayout.Controls.Add(new Label { Text = "Source:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 0, 0);
        _sourceBox = new TextBox { Text = _config.Source, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 6, 0) };
        _sourceBox.Leave += (_, _) => SaveConfig();
        infoLayout.Controls.Add(_sourceBox, 1, 0);
        var browseSrc = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        browseSrc.Click += (_, _) => BrowseSource();
        infoLayout.Controls.Add(browseSrc, 2, 0);

        // Row 1: Mirror toggle
        _mirrorCheck = new CheckBox
        {
            Text = "Mirror — make destinations an exact copy (deletes files in destinations that are gone from source)",
            Checked = _config.Mirror,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        _mirrorCheck.CheckedChanged += (_, _) => SaveConfig();
        infoLayout.Controls.Add(_mirrorCheck, 1, 1);
        infoLayout.SetColumnSpan(_mirrorCheck, 2);

        // Row 2: Destinations label
        infoLayout.Controls.Add(new Label { Text = "Destinations:", AutoSize = true, Margin = new Padding(0, 10, 0, 2) }, 0, 2);

        // Row 3: Destinations grid
        _destGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Height = 130,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window,
            Margin = new Padding(0, 0, 0, 4),
            DataSource = _targets,
        };
        _destGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(SyncTarget.Enabled), HeaderText = "On", Width = 40 });
        _destGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SyncTarget.Name), HeaderText = "Name", Width = 150 });
        _destGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SyncTarget.Destination), HeaderText = "Destination folder", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        // Commit checkbox edits immediately so they save without losing focus.
        _destGrid.CurrentCellDirtyStateChanged += (_, _) => { if (_destGrid.IsCurrentCellDirty) _destGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _destGrid.CellValueChanged += (_, _) => SaveConfig();
        _targets.ListChanged += (_, _) => SaveConfig();
        infoLayout.Controls.Add(_destGrid, 1, 3);
        infoLayout.SetColumnSpan(_destGrid, 2);

        // Row 4: Add / Remove / open config
        var destBtns = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        AddButton(destBtns, "Add Destination…", (_, _) => AddDestination());
        AddButton(destBtns, "Remove Selected", (_, _) => RemoveSelectedDestination());
        var editLink = new LinkLabel { Text = "Open config file", AutoSize = true, Margin = new Padding(14, 6, 0, 0) };
        editLink.Click += (_, _) => OpenInEditor(SyncConfig.ConfigPath);
        destBtns.Controls.Add(editLink);
        infoLayout.Controls.Add(destBtns, 1, 4);
        infoLayout.SetColumnSpan(destBtns, 2);

        // Row 5: excluded file patterns
        infoLayout.Controls.Add(new Label { Text = "Skip files:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 6, 0) }, 0, 5);
        _excludeFilesBox = new TextBox
        {
            Text = string.Join("; ", _config.ExcludeFiles ?? new List<string>()),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 7, 0, 0),
            PlaceholderText = "name patterns, e.g.  ~$*; Thumbs.db; *.tmp",
        };
        _excludeFilesBox.Leave += (_, _) => SaveConfig();
        _tip.SetToolTip(_excludeFilesBox, "File name patterns to skip, separated by ';' or ','.\r\nWildcards allowed (robocopy /XF).");
        infoLayout.Controls.Add(_excludeFilesBox, 1, 5);
        infoLayout.SetColumnSpan(_excludeFilesBox, 2);

        // Row 6: excluded folders
        infoLayout.Controls.Add(new Label { Text = "Skip folders:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 6, 0) }, 0, 6);
        _excludeFoldersBox = new TextBox
        {
            Text = string.Join("; ", _config.ExcludeFolders ?? new List<string>()),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 7, 0, 0),
            PlaceholderText = "folder names, e.g.  node_modules; Temp",
        };
        _excludeFoldersBox.Leave += (_, _) => SaveConfig();
        _tip.SetToolTip(_excludeFoldersBox, "Folder names or full paths to skip, separated by ';' or ','. (robocopy /XD)");
        infoLayout.Controls.Add(_excludeFoldersBox, 1, 6);
        infoLayout.SetColumnSpan(_excludeFoldersBox, 2);

        info.Controls.Add(infoLayout);
        root.Controls.Add(info, 0, 0);

        // ===== Sync row =====
        var syncRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 0) };
        _syncButton = new Button { Text = "Sync Now", Width = 130, Height = 40, Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        _syncButton.Click += async (_, _) => await DoSyncAsync();
        _previewButton = new Button { Text = "Preview", Width = 90, Height = 40, Margin = new Padding(8, 3, 0, 3) };
        _previewButton.Click += async (_, _) => await DoSyncAsync(listOnly: true);
        _statusLabel = new Label { Text = "Ready.", AutoSize = true, Margin = new Padding(14, 12, 0, 0) };
        _cancelButton = new Button { Text = "Cancel", Width = 80, Height = 40, Margin = new Padding(8, 3, 0, 3), Visible = false };
        _cancelButton.Click += (_, _) => { _cts?.Cancel(); _statusLabel.Text = "Cancelling…"; };
        var healthButton = new Button { Text = "Check Health", Width = 110, Height = 40, Margin = new Padding(8, 3, 0, 3) };
        healthButton.Click += (_, _) => ShowHealthCheck();
        var openLogs = new LinkLabel { Text = "Open log folder", AutoSize = true, Margin = new Padding(14, 14, 0, 0) };
        openLogs.Click += (_, _) => OpenInEditor(SyncConfig.LogDir);
        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 180, Height = 18, Margin = new Padding(14, 12, 0, 0), Visible = false };
        _updateLink = new LinkLabel
        {
            Text = "Update available",
            AutoSize = true,
            Margin = new Padding(14, 14, 0, 0),
            Visible = false,
            LinkColor = Color.FromArgb(180, 60, 0),
        };
        _updateLink.Click += (_, _) => ShowUpdateDialog();
        syncRow.Controls.Add(_syncButton);
        syncRow.Controls.Add(_previewButton);
        syncRow.Controls.Add(_cancelButton);
        syncRow.Controls.Add(healthButton);
        syncRow.Controls.Add(_statusLabel);
        syncRow.Controls.Add(_progress);
        syncRow.Controls.Add(openLogs);
        syncRow.Controls.Add(_updateLink);
        root.Controls.Add(syncRow, 0, 1);

        // ===== Last sync summary =====
        var lastGroup = new GroupBox { Text = "Last sync", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 6, 10, 12) };
        var lastStack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        lastStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lastStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lastStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lastRunLabel = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 4), Text = "No sync recorded yet." };
        lastStack.Controls.Add(_lastRunLabel, 0, 0);

        _lastRunGrid = new DataGridView
        {
            Dock = DockStyle.Top,
            Height = 96,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window,
            Visible = false,
        };
        _lastRunGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Destination", Width = 150 });
        _lastRunGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", Width = 90 });
        _lastRunGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Copied", Width = 70 });
        _lastRunGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Failed", Width = 60 });
        _lastRunGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Recycled", Width = 75 });
        _lastRunGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        lastStack.Controls.Add(_lastRunGrid, 0, 1);

        lastGroup.Controls.Add(lastStack);
        root.Controls.Add(lastGroup, 0, 2);

        // ===== Schedule group =====
        var sched = new GroupBox { Text = "Automatic scheduling (Windows Task Scheduler)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 14) };
        var schedOuter = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, AutoSize = true, WrapContents = false };

        bool anyInstalled = ScheduleManager.Exists(ScheduleManager.DailyTask)
                         || ScheduleManager.Exists(ScheduleManager.MonthlyTask)
                         || ScheduleManager.Exists(ScheduleManager.LogonTask);
        _schedEnable = new CheckBox { Text = "Enable automatic scheduling (show options)", Checked = anyInstalled, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        schedOuter.Controls.Add(_schedEnable);

        var schedDetails = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = anyInstalled };

        var row1 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        row1.Controls.Add(new Label { Text = "Run at:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        _timePicker = new DateTimePicker { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 130, Value = DateTime.Today.AddHours(18) };
        row1.Controls.Add(_timePicker);
        row1.Controls.Add(new Label { Text = "Monthly day:", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        _monthDay = new NumericUpDown { Minimum = 1, Maximum = 28, Value = 1, Width = 50 };
        row1.Controls.Add(_monthDay);
        schedDetails.Controls.Add(row1);

        // One row per schedule: Enable (create/replace) · Disable (pause) · Remove (delete).
        // Buttons grey out to reflect the current state, so no separate status text is needed.
        schedDetails.Controls.Add(MakeScheduleRow("Daily",
            () => ScheduleManager.RegisterDaily(TimeOnly.FromDateTime(_timePicker.Value)),
            ScheduleManager.DailyTask));
        schedDetails.Controls.Add(MakeScheduleRow("Monthly",
            () => ScheduleManager.RegisterMonthly(TimeOnly.FromDateTime(_timePicker.Value), (int)_monthDay.Value),
            ScheduleManager.MonthlyTask));
        schedDetails.Controls.Add(MakeScheduleRow("Logon / unlock",
            ScheduleManager.RegisterLogon,
            ScheduleManager.LogonTask));

        var schedNote = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
            Text = $"“Logon / unlock” syncs when you sign in and each time you unlock the screen "
                 + $"(at most once every {_config.MinMinutesBetweenAutoSyncs} minutes).",
        };
        schedDetails.Controls.Add(schedNote);

        schedOuter.Controls.Add(schedDetails);
        _schedEnable.CheckedChanged += (_, _) => schedDetails.Visible = _schedEnable.Checked;

        sched.Controls.Add(schedOuter);
        root.Controls.Add(sched, 0, 3);

        // ===== Email notifications =====
        var em = _config.Email;
        var emailGroup = new GroupBox { Text = "Email notification after a scheduled sync", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 16) };
        // Outer 1-column layout: checkbox on top, collapsible detail grid below.
        var emailOuter = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        emailOuter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        emailOuter.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        emailOuter.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _emailEnable = new CheckBox { Text = "Enable email notifications (sent after each scheduled run)", Checked = em.Enabled, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        emailOuter.Controls.Add(_emailEnable, 0, 0);

        var emailDetails = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 4, Visible = em.Enabled };
        _emailDetails = emailDetails;
        emailDetails.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        emailDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        emailDetails.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        emailDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        // Explicit auto-height rows — without these the last row gets no height and is clipped.
        for (int i = 0; i < 4; i++) emailDetails.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // From/login + Password
        emailDetails.Controls.Add(new Label { Text = "Gmail address:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 0, 0);
        _emailFrom = new TextBox { Text = em.From, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 10, 0) };
        _emailFrom.Leave += (_, _) => SaveEmailConfig();
        emailDetails.Controls.Add(_emailFrom, 1, 0);

        // "App password:" label with a clickable ⓘ hint that explains how to generate one.
        var pwLabelPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };
        pwLabelPanel.Controls.Add(new Label { Text = "App password:", AutoSize = true, Margin = new Padding(0, 2, 2, 0) });
        var pwHint = new LinkLabel { Text = "ⓘ", AutoSize = true, Margin = new Padding(0, 2, 0, 0), Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        pwHint.Click += (_, _) => ShowAppPasswordHelp();
        _tip.SetToolTip(pwHint, AppPasswordHelpText);
        pwLabelPanel.Controls.Add(pwHint);
        emailDetails.Controls.Add(pwLabelPanel, 2, 0);

        _emailPass = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(0, 5, 0, 0), PlaceholderText = em.HasPassword ? "•••••••• (saved — type to change)" : "16-char Google App Password" };
        _emailPass.Leave += (_, _) => SaveEmailConfig();
        _tip.SetToolTip(_emailPass, AppPasswordHelpText);
        emailDetails.Controls.Add(_emailPass, 3, 0);

        // To
        emailDetails.Controls.Add(new Label { Text = "Send to:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 0, 1);
        _emailTo = new TextBox { Text = em.To, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 0), PlaceholderText = "recipient@example.com (comma-separate for several)" };
        _emailTo.Leave += (_, _) => SaveEmailConfig();
        emailDetails.Controls.Add(_emailTo, 1, 1);
        emailDetails.SetColumnSpan(_emailTo, 3);

        // SMTP host + port
        emailDetails.Controls.Add(new Label { Text = "SMTP host:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 0, 2);
        _emailHost = new TextBox { Text = em.SmtpHost, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 10, 0) };
        _emailHost.Leave += (_, _) => SaveEmailConfig();
        emailDetails.Controls.Add(_emailHost, 1, 2);
        emailDetails.Controls.Add(new Label { Text = "Port:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 2, 2);
        _emailPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = em.SmtpPort, Width = 80, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 0, 0) };
        _emailPort.ValueChanged += (_, _) => SaveEmailConfig();
        emailDetails.Controls.Add(_emailPort, 3, 2);

        // When + test button
        emailDetails.Controls.Add(new Label { Text = "When:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 0) }, 0, 3);
        _emailMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 200, Margin = new Padding(0, 5, 0, 0) };
        _emailMode.Items.AddRange(new object[] { "Always", "Only on errors", "Only on success" });
        _emailMode.SelectedIndex = em.Mode switch { EmailMode.OnError => 1, EmailMode.OnSuccess => 2, _ => 0 };
        _emailMode.SelectedIndexChanged += (_, _) => SaveEmailConfig();
        emailDetails.Controls.Add(_emailMode, 1, 3);
        var testRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        AddButton(testRow, "Send Test Email", (_, _) => SendTestEmail());
        _emailStatus = new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        testRow.Controls.Add(_emailStatus);
        emailDetails.Controls.Add(testRow, 2, 3);
        emailDetails.SetColumnSpan(testRow, 2);

        emailOuter.Controls.Add(emailDetails, 0, 1);

        // Show the fields only when the checkbox is ticked.
        _emailEnable.CheckedChanged += (_, _) =>
        {
            emailDetails.Visible = _emailEnable.Checked;
            SaveEmailConfig();
            if (_emailEnable.Checked) BeginInvoke(new Action(FixEmailPanelHeight));
        };

        emailGroup.Controls.Add(emailOuter);
        root.Controls.Add(emailGroup, 0, 4);

        _tray = CreateTrayIcon();

        _loaded = true;
        RefreshScheduleStates();
        RefreshLastRunSummary();

        // First run after a fresh install: nothing is configured yet, so say what to do.
        if (_config.IsUnconfigured)
            _statusLabel.Text = "Set a Source folder and add at least one destination to begin.";

        // Keep the window within the screen's working area (small/scaled displays); AutoScroll
        // then provides a scrollbar for any overflow.
        Load += (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height > wa.Height) { Height = wa.Height; Top = wa.Top; }
            if (Width > wa.Width) { Width = wa.Width; Left = wa.Left; }
        };

        // WinForms bug: a TableLayoutPanel with AutoSize + Percent columns lays out its rows
        // correctly but under-reports its own preferred height, clipping the last row. Force the
        // panel's minimum height to the true sum of its row heights once it has been laid out.
        Shown += (_, _) => BeginInvoke(new Action(async () =>
        {
            FixEmailPanelHeight();
            WarnAboutStaleTaskTargets();

            // Quiet background check, at most once a day.
            if (_config.CheckForUpdates && Updater.IsCheckDue())
                await CheckForUpdatesAsync(manual: false);
        }));

        // Minimizing hides the window to the tray instead of the taskbar.
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) { Hide(); _tray.Visible = true; }
        };
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sync Now", null, async (_, _) => await DoSyncAsync());
        menu.Items.Add("Check for Updates…", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        var tray = new NotifyIcon
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
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Warns if a registered task launches an exe that no longer exists — the schedule looks
    /// "enabled" but silently never runs. Happens when a trigger was created from a build folder.
    /// </summary>
    private void WarnAboutStaleTaskTargets()
    {
        var stale = new List<(string task, string label, string exe)>();
        foreach (var (task, label) in new[]
                 {
                     (ScheduleManager.DailyTask, "Daily"),
                     (ScheduleManager.MonthlyTask, "Monthly"),
                     (ScheduleManager.LogonTask, "Logon / unlock"),
                 })
        {
            if (!ScheduleManager.Exists(task)) continue;
            var target = ScheduleManager.GetTaskTarget(task);
            if (target is null) continue;

            // "Task To Run" includes the arguments; take just the exe part.
            var exe = target.Replace("\"", "").Replace(" --sync", "").Replace(" --auto", "").Trim();
            if (exe.Length > 0 && !File.Exists(exe)) stale.Add((task, label, exe));
        }

        if (stale.Count == 0) return;

        AppendLog("[Schedule] stale target(s): " + string.Join(" | ", stale.Select(s => $"{s.label} -> {s.exe}")));

        var answer = MessageBox.Show(this,
            "These schedules point at a program file that no longer exists, so they never run:\n\n"
            + string.Join("\n", stale.Select(s => $"  • {s.label} → {s.exe}"))
            + "\n\nRepair them now to use this copy?\n\n"
            + ScheduleManager.ExePath
            + "\n\n(Times and days are kept as they are.)",
            "Schedules need repair", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        if (!ConfirmExePathForScheduling()) return;

        foreach (var s in stale)
        {
            var (ok, msg) = ScheduleManager.Repair(s.task);
            AppendLog($"[Schedule] repair {s.label}: {(ok ? "OK" : "FAILED")} {msg}");
        }
        RefreshScheduleStates();
        MessageBox.Show(this, "Schedules repaired.", "Folder Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void FixEmailPanelHeight()
    {
        int needed = _emailDetails.GetRowHeights().Sum();
        if (needed > 0 && _emailDetails.Height < needed)
        {
            _emailDetails.MinimumSize = new Size(0, needed);
            PerformLayout();
        }
    }


    // ===== Configuration editing =====

    private void BrowseSource()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the source folder to sync from", UseDescriptionForTitle = true };
        if (Directory.Exists(_sourceBox.Text)) dlg.SelectedPath = _sourceBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _sourceBox.Text = dlg.SelectedPath;
            SaveConfig();
        }
    }

    private void AddDestination()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a destination folder to sync to", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string path = dlg.SelectedPath;
        if (_targets.Any(t => string.Equals(t.Destination, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That destination is already in the list.", "Folder Sync");
            return;
        }
        if (PathUtil.SameOrNested(_config.Source, path))
        {
            MessageBox.Show(this,
                "That folder is the source — or nested inside/around it.\n\nWith Mirror on, syncing there could delete your files. Pick a different destination.",
                "Folder Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _targets.Add(new SyncTarget { Name = new DirectoryInfo(path).Name, Destination = path, Enabled = true });
        // ListChanged handler saves automatically.
    }

    private void RemoveSelectedDestination()
    {
        if (_destGrid.CurrentRow?.DataBoundItem is not SyncTarget target) return;
        if (MessageBox.Show(this, $"Remove destination:\n{target.Name} — {target.Destination}?\n\n(This only stops syncing to it; the folder's files are not deleted.)",
                "Folder Sync", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _targets.Remove(target);
        }
    }

    private void SaveConfig()
    {
        if (!_loaded) return;
        _config.Source = _sourceBox.Text.Trim();
        _config.Mirror = _mirrorCheck.Checked;
        _config.ExcludeFiles = SplitList(_excludeFilesBox.Text);
        _config.ExcludeFolders = SplitList(_excludeFoldersBox.Text);
        // _config.Targets is the same list backing _targets, so it is already current.
        try { _config.Save(); }
        catch (Exception ex) { AppendLog("[Config] save failed: " + ex.Message); }
    }

    /// <summary>Splits a ';' or ',' separated list from a text box into trimmed entries.</summary>
    private static List<string> SplitList(string text) => text
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>Fills the "Last sync" group from the persisted run history.</summary>
    private void RefreshLastRunSummary()
    {
        var last = LastRun.Load();
        if (last is null)
        {
            _lastRunLabel.Text = "No sync recorded yet.";
            _lastRunGrid.Visible = false;
            return;
        }

        var age = DateTime.Now - last.When;
        string ago = age.TotalDays >= 1 ? $"{age.TotalDays:0.#} day(s) ago"
                   : age.TotalHours >= 1 ? $"{age.TotalHours:0.#} hour(s) ago"
                   : $"{age.TotalMinutes:0} min ago";
        _lastRunLabel.Text = $"{(last.Success ? "✓ Succeeded" : "✗ Completed with errors")}  —  "
                           + $"{last.When:yyyy-MM-dd HH:mm:ss}  ({ago})";
        _lastRunLabel.ForeColor = last.Success ? Color.FromArgb(0, 110, 0) : Color.FromArgb(170, 0, 0);

        _lastRunGrid.Rows.Clear();
        foreach (var d in last.Destinations)
        {
            string result = d.Success ? "OK" : (string.IsNullOrEmpty(d.Note) ? "FAILED" : d.Note);
            int row = _lastRunGrid.Rows.Add(d.Name, result, d.FilesCopied, d.FilesFailed, d.FilesRecycled, d.Destination);
            if (!d.Success) _lastRunGrid.Rows[row].DefaultCellStyle.ForeColor = Color.FromArgb(170, 0, 0);
        }
        _lastRunGrid.Visible = last.Destinations.Count > 0;
    }

    /// <summary>
    /// Looks for a newer release. A manual check always reports the outcome; the automatic
    /// startup check stays quiet unless there is something to install.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var info = await Updater.CheckAsync(_config);
            if (info is null)
            {
                if (manual)
                    MessageBox.Show(this,
                        $"You're up to date.\n\nFolder Sync {Updater.CurrentVersion} is the newest version available.",
                        "No updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _pendingUpdate = info;
            _updateLink.Text = $"Update to {info.Version}";
            _updateLink.Visible = true;
            AppendLog($"[Update] version {info.Version} available (running {Updater.CurrentVersion}).");

            if (manual)
            {
                ShowUpdateDialog();
            }
            else
            {
                _tray.ShowBalloonTip(6000, "Folder Sync update available",
                    $"Version {info.Version} is ready to install.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            AppendLog("[Update] check failed: " + ex.Message);
            if (manual)
                MessageBox.Show(this,
                    $"Could not check for updates:\n\n{ex.Message}",
                    "Update check failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowUpdateDialog()
    {
        if (_pendingUpdate is null) return;
        using var dlg = new UpdateDialog(_pendingUpdate);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            // The installer is running; quit so it can replace the program files.
            _tray.Visible = false;
            Application.Exit();
        }
    }

    private void ShowHealthCheck()
    {
        _destGrid.EndEdit();
        SaveConfig();
        string report;
        try { report = Diagnostics.Run(_config); }
        catch (Exception ex) { report = "Health check failed: " + ex; }
        using var dlg = new ResultsDialog("Folder Sync — health check", report);
        dlg.ShowDialog(this);
    }

    // ===== Email settings =====

    private void SaveEmailConfig()
    {
        if (!_loaded) return;
        var em = _config.Email;
        em.Enabled = _emailEnable.Checked;
        em.From = _emailFrom.Text.Trim();
        em.Username = em.From;          // for Gmail the login is the same address
        em.To = _emailTo.Text.Trim();
        em.SmtpHost = _emailHost.Text.Trim();
        em.SmtpPort = (int)_emailPort.Value;
        em.Mode = _emailMode.SelectedIndex switch { 1 => EmailMode.OnError, 2 => EmailMode.OnSuccess, _ => EmailMode.Always };
        // Only overwrite the stored password if the user actually typed a new one.
        if (_emailPass.Text.Length > 0)
        {
            // Google shows App Passwords with spaces; strip them so the pasted code works.
            em.SetPassword(_emailPass.Text.Replace(" ", ""));
            _emailPass.Clear();
            _emailPass.PlaceholderText = "•••••••• (saved — type to change)";
        }
        try { _config.Save(); }
        catch (Exception ex) { AppendLog("[Email] config save failed: " + ex.Message); }
    }

    private void ShowAppPasswordHelp()
    {
        var answer = MessageBox.Show(this,
            AppPasswordHelpText + "\r\n\r\nOpen the Google App Passwords page now?",
            "How to get a Gmail App Password",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes)
            OpenInEditor("https://myaccount.google.com/apppasswords");
    }

    private void SendTestEmail()
    {
        SaveEmailConfig();
        var em = _config.Email;
        if (!em.HasPassword) { _emailStatus.Text = "Enter the app password first."; return; }

        _emailStatus.Text = "Sending…";
        try
        {
            Emailer.Send(em, "[Folder Sync] Test email",
                $"This is a test from Folder Sync on {Environment.MachineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n\nIf you received this, notifications are configured correctly.");
            _emailStatus.Text = "Test email sent ✓";
            AppendLog($"[Email] test sent to {em.To}.");
        }
        catch (Exception ex)
        {
            _emailStatus.Text = "Failed.";
            AppendLog("[Email] test FAILED: " + ex.Message);

            string msg = ex.Message;
            bool needsAppPassword = msg.Contains("Application-specific password", StringComparison.OrdinalIgnoreCase)
                                 || msg.Contains("InvalidSecondFactor", StringComparison.OrdinalIgnoreCase)
                                 || msg.Contains("5.7.9", StringComparison.OrdinalIgnoreCase)
                                 || msg.Contains("Username and Password not accepted", StringComparison.OrdinalIgnoreCase);
            if (needsAppPassword)
            {
                msg = "Google rejected the sign-in because this isn't an App Password.\r\n\r\n"
                    + "Gmail does not allow your normal account password for apps. You need a 16-character "
                    + "App Password:\r\n\r\n"
                    + "1. Turn ON 2-Step Verification:  myaccount.google.com/signinoptions/twosv\r\n"
                    + "2. Create an App Password:  myaccount.google.com/apppasswords\r\n"
                    + "   (choose app = Mail, then copy the 16-character code)\r\n"
                    + "3. Paste that code into the App password box here and click Send Test Email again.\r\n\r\n"
                    + "Tip: enter it with or without spaces — both work.\r\n\r\n"
                    + "Original error:\r\n" + ex.Message;
            }
            MessageBox.Show(this, msg, "Test email failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ===== Helpers =====

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0), Height = 28 };
        b.Click += onClick;
        return b;
    }

    private static void AddButton(Control parent, string text, EventHandler onClick)
        => parent.Controls.Add(MakeButton(text, onClick));

    /// <summary>Builds a labelled row with Enable / Disable / Remove buttons for one schedule.</summary>
    private FlowLayoutPanel MakeScheduleRow(string label, Func<(bool ok, string msg)> enableAction, string taskName)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 100, Margin = new Padding(0, 6, 8, 0) });
        var enable = MakeButton("Enable", (_, _) =>
        {
            if (!ConfirmExePathForScheduling()) return;
            Apply(enableAction, $"{label}: enable");
        });
        var disable = MakeButton("Disable", (_, _) => Apply(() => ScheduleManager.SetEnabled(taskName, false), $"{label}: disable"));
        var remove = MakeButton("Remove", (_, _) => Apply(() => ScheduleManager.Remove(taskName), $"{label}: remove"));
        row.Controls.Add(enable);
        row.Controls.Add(disable);
        row.Controls.Add(remove);
        _scheduleRows.Add(new SchedRow(taskName, enable, disable, remove));
        return row;
    }

    /// <summary>Greys out buttons that don't apply to each task's current state.</summary>
    private void RefreshScheduleStates()
    {
        foreach (var r in _scheduleRows)
        {
            var st = ScheduleManager.GetState(r.TaskName);
            r.Enable.Enabled = st != ScheduleManager.TaskState.Enabled;        // greyed when already enabled
            r.Disable.Enabled = st == ScheduleManager.TaskState.Enabled;       // only an enabled task can be paused
            r.Remove.Enabled = st != ScheduleManager.TaskState.NotInstalled;   // nothing to remove if not installed
        }
    }

    /// <summary>
    /// A scheduled task stores the full path of the exe that registered it. If that is a
    /// bin\Debug / bin\Release folder (i.e. the app was launched from Visual Studio), the trigger
    /// silently breaks the next time the project is rebuilt or cleaned — so warn before doing it.
    /// </summary>
    private bool ConfirmExePathForScheduling()
    {
        if (!ScheduleManager.IsBuildOutputPath()) return true;

        var answer = MessageBox.Show(this,
            "This copy of Folder Sync is running from a build output folder:\n\n"
            + $"{ScheduleManager.ExePath}\n\n"
            + "A schedule created now would point at that temporary path and stop working as soon "
            + "as the project is rebuilt or cleaned.\n\n"
            + "Run the installed copy instead (e.g. the Desktop shortcut), then enable the schedule there.\n\n"
            + "Create the schedule anyway?",
            "Running from a build folder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        return answer == DialogResult.Yes;
    }

    private void Apply(Func<(bool ok, string msg)> action, string what)
    {
        try
        {
            var (ok, msg) = action();
            AppendLog($"[Schedule] {what}: {(ok ? "OK" : "FAILED")} {msg}");
        }
        catch (Exception ex)
        {
            AppendLog($"[Schedule] {what}: ERROR {ex.Message}");
        }
        RefreshScheduleStates();
    }

    private async Task DoSyncAsync(bool listOnly = false)
    {
        if (_cts is not null) return; // already running

        // Make sure any in-progress grid edit is committed before syncing.
        _destGrid.EndEdit();
        SaveConfig();

        // Validate the source folder before doing anything.
        if (string.IsNullOrWhiteSpace(_config.Source) || !Directory.Exists(_config.Source))
        {
            string shown = string.IsNullOrWhiteSpace(_config.Source) ? "(empty)" : _config.Source;
            MessageBox.Show(this,
                $"The source folder does not exist:\n\n{shown}\n\nPlease correct the Source path and try again.",
                "Invalid source folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusLabel.Text = "Source folder not found — please fix the Source path.";
            _sourceBox.Focus();
            _sourceBox.SelectAll();
            return;
        }

        if (!_config.Targets.Any(t => t.Enabled))
        {
            MessageBox.Show(this,
                "No destinations are enabled, so there is nothing to sync to.\n\n"
                + "Use \"Add Destination…\" to choose a folder (for example your Google Drive or "
                + "iCloud Drive folder), then try again.",
                "No destinations", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusLabel.Text = "Add at least one destination to sync to.";
            return;
        }

        // Warn about destinations whose drive isn't available (e.g. Google Drive 'G:' not running).
        var offline = _config.Targets
            .Where(t => t.Enabled && !PathUtil.DriveAvailable(t.Destination))
            .ToList();
        if (offline.Count > 0)
        {
            string list = string.Join("\n", offline.Select(t => $"  • {t.Name} — {t.Destination}"));
            var answer = MessageBox.Show(this,
                $"These destinations are not available right now and will be skipped:\n\n{list}\n\nContinue anyway?",
                "Destination not available", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) { _statusLabel.Text = "Cancelled — destination not available."; return; }
        }

        _cts = new CancellationTokenSource();
        SetSyncingUi(true);
        _statusLabel.Text = listOnly ? "Previewing…" : "Syncing…";

        var sb = new StringBuilder();
        var engine = new SyncEngine(_config);
        engine.Log += line => { lock (_logLock) sb.AppendLine(line); };
        engine.Progress += p => BeginInvoke(() => _statusLabel.Text = p);

        SyncResult? result = null;
        try
        {
            result = await Task.Run(() => engine.RunAsync(_cts.Token, listOnly));
        }
        catch (OperationCanceledException)
        {
            lock (_logLock) sb.AppendLine("CANCELLED by user.");
            _statusLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            lock (_logLock) sb.AppendLine("FATAL: " + ex.Message);
            _statusLabel.Text = "Failed.";
        }
        finally
        {
            SetSyncingUi(false);
            _cts.Dispose();
            _cts = null;
        }

        if (listOnly)
        {
            // Preview: save to a preview log file (so there's a record) and show it in a dialog.
            try { File.WriteAllText(SyncConfig.NewPreviewLogPath(), sb.ToString()); } catch { }
            SyncConfig.CleanupOldLogs(7);
            if (result is not null)
                _statusLabel.Text = "Preview ready (saved to log folder).";
            using var dlg = new ResultsDialog("Preview — changes that WOULD be made", sb.ToString());
            dlg.ShowDialog(this);
            return;
        }

        // Real sync: write a fresh log file, prune old ones, and update status + toast.
        try { File.WriteAllText(SyncConfig.NewSyncLogPath(), sb.ToString()); } catch { }
        SyncConfig.RecordSyncTime();    // feeds the logon/unlock minimum-gap throttle
        SyncConfig.CleanupOldLogs(7);

        if (result is not null)
        {
            LastRun.Save(result);
            RefreshLastRunSummary();

            _statusLabel.Text = result.Success
                ? $"Done — last sync {DateTime.Now:HH:mm:ss}."
                : $"Completed with errors — {DateTime.Now:HH:mm:ss}.";

            int recycled = result.Targets.Sum(t => t.FilesRecycled);
            string extra = recycled > 0 ? $" {recycled} deleted file(s) moved to '{_config.RecycleFolderName}'." : "";
            _tray.ShowBalloonTip(5000, "Folder Sync",
                (result.Success ? "Sync completed successfully." : "Sync completed with errors — see the log.") + extra,
                result.Success ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
    }

    /// <summary>Toggles the buttons/progress bar between idle and running states.</summary>
    private void SetSyncingUi(bool running)
    {
        _syncButton.Enabled = !running;
        _previewButton.Enabled = !running;
        _cancelButton.Visible = running;
        _progress.Visible = running;
    }

    /// <summary>Appends a one-off event (schedule/email actions) to a small rolling events log.</summary>
    private void AppendLog(string line)
    {
        try
        {
            Directory.CreateDirectory(SyncConfig.LogDir);
            var path = Path.Combine(SyncConfig.LogDir, "events.log");
            lock (_logLock) { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}"); }
        }
        catch { /* never let logging break the UI */ }
    }

    private static void OpenInEditor(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open:\n{path}\n\n{ex.Message}");
        }
    }
}
