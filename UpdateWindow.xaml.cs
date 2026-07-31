using System.Diagnostics;
using System.Windows;

namespace FolderSync;

/// <summary>
/// Shows an available release and installs it. DialogResult is true once the installer has been
/// launched, which tells the caller to quit so the files can be replaced.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        Heading.Text = $"Folder Sync {info.Version} is available";
        SubHeading.Text = $"You have {Updater.CurrentVersion}."
                        + (info.SetupSize > 0 ? $"   Download: {info.SetupSize / 1024.0 / 1024.0:0.#} MB" : "");
        Notes.Text = string.IsNullOrWhiteSpace(info.Notes) ? "(no release notes)" : info.Notes;

        if (string.IsNullOrEmpty(info.SetupUrl))
        {
            UpdateButton.Content = "Open download page";
            StatusText.Text = "This release has no installer attached — download it from GitHub.";
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_info.SetupUrl)) { OpenReleasePage(); return; }

        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Downloading…";
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<int>(p => { if (p is >= 0 and <= 100) Progress.Value = p; });
            string setup = await Updater.DownloadSetupAsync(_info, progress, _cts.Token);

            StatusText.Text = "Starting installer — Folder Sync will close.";
            Updater.RunSetup(setup);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = "Download failed.";
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            MessageWindow.Warn(this, "Update failed",
                $"Could not download the update:\n\n{ex.Message}\n\nYou can download it manually from GitHub.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void ViewOnline_Click(object sender, RoutedEventArgs e) => OpenReleasePage();

    private void OpenReleasePage()
    {
        string url = string.IsNullOrEmpty(_info.ReleasePageUrl)
            ? $"https://github.com/{SyncConfig.DefaultUpdateRepo}/releases"
            : _info.ReleasePageUrl;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* no browser */ }
    }
}
