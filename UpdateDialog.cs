using System.Diagnostics;

namespace FolderSync;

/// <summary>
/// Shows an available update with its release notes and offers to download and install it.
/// Returns DialogResult.OK when the installer has been launched and the app should quit.
/// </summary>
public sealed class UpdateDialog : Form
{
    private readonly UpdateInfo _info;
    private readonly Button _updateButton;
    private readonly Button _laterButton;
    private readonly ProgressBar _progress;
    private readonly Label _statusLabel;
    private CancellationTokenSource? _cts;

    public UpdateDialog(UpdateInfo info)
    {
        _info = info;

        Text = "Update available";
        Icon = AppResources.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(460, 320);
        Font = new Font("Segoe UI", 9F);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        var header = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 54,
            Padding = new Padding(12, 10, 12, 0),
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Text = $"Folder Sync {info.Version} is available",
        };

        var sub = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 26,
            Padding = new Padding(14, 0, 12, 0),
            ForeColor = SystemColors.GrayText,
            Text = $"You have {Updater.CurrentVersion}."
                 + (info.SetupSize > 0 ? $"   Download size: {info.SetupSize / 1024.0 / 1024.0:0.#} MB" : ""),
        };

        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Margin = new Padding(12),
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "(no release notes)" : info.Notes,
        };
        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        notesHost.Controls.Add(notes);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(12, 6, 12, 10) };

        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, Visible = false };
        _statusLabel = new Label { Dock = DockStyle.Top, Height = 22, AutoSize = false, Text = "" };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        _laterButton = new Button { Text = "Later", AutoSize = true, DialogResult = DialogResult.Cancel };
        _updateButton = new Button { Text = "Update Now", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _updateButton.Click += async (_, _) => await DoUpdateAsync();
        var pageLink = new LinkLabel { Text = "View on GitHub", AutoSize = true, Margin = new Padding(16, 6, 0, 0) };
        pageLink.Click += (_, _) => OpenUrl(string.IsNullOrEmpty(info.ReleasePageUrl)
            ? "https://github.com/" + SyncConfig.DefaultUpdateRepo + "/releases"
            : info.ReleasePageUrl);
        buttons.Controls.Add(_laterButton);
        buttons.Controls.Add(_updateButton);
        buttons.Controls.Add(pageLink);

        bottom.Controls.Add(_statusLabel);
        bottom.Controls.Add(_progress);
        bottom.Controls.Add(buttons);

        Controls.Add(notesHost);
        Controls.Add(sub);
        Controls.Add(header);
        Controls.Add(bottom);

        AcceptButton = _updateButton;
        CancelButton = _laterButton;

        if (string.IsNullOrEmpty(info.SetupUrl))
        {
            _updateButton.Text = "Open download page";
            _statusLabel.Text = "This release has no installer attached — download it from GitHub.";
        }
    }

    private async Task DoUpdateAsync()
    {
        // No installer asset: just send the user to the release page.
        if (string.IsNullOrEmpty(_info.SetupUrl))
        {
            OpenUrl(string.IsNullOrEmpty(_info.ReleasePageUrl)
                ? "https://github.com/" + SyncConfig.DefaultUpdateRepo + "/releases"
                : _info.ReleasePageUrl);
            return;
        }

        _updateButton.Enabled = false;
        _laterButton.Enabled = false;
        _progress.Visible = true;
        _progress.Value = 0;
        _statusLabel.Text = "Downloading…";
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<int>(p => { if (p is >= 0 and <= 100) _progress.Value = p; });
            string setup = await Updater.DownloadSetupAsync(_info, progress, _cts.Token);

            _statusLabel.Text = "Starting installer — Folder Sync will close.";
            Updater.RunSetup(setup);
            DialogResult = DialogResult.OK;      // caller exits so files can be replaced
            Close();
        }
        catch (Exception ex)
        {
            _progress.Visible = false;
            _statusLabel.Text = "Download failed.";
            _updateButton.Enabled = true;
            _laterButton.Enabled = true;
            MessageBox.Show(this,
                $"Could not download the update:\n\n{ex.Message}\n\nYou can download it manually from GitHub.",
                "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* no browser available */ }
    }
}
