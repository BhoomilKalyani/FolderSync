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
                        + (info.SetupSize > 0 ? $"   Download: {Humanise.Size(info.SetupSize)}" : "");
        string notes = Updater.NotesAsPlainText(info.Notes);
        Notes.Text = notes.Length == 0 ? "(no release notes)" : notes;

        if (string.IsNullOrEmpty(info.SetupUrl))
        {
            UpdateButton.Content = "Open download page";
            StatusText.Text = "This release has no installer attached — download it from GitHub.";
        }
        else if (!info.CanVerifySetup)
        {
            // Never run an installer that cannot be checked; let the user decide on the release page.
            UpdateButton.Content = "Open download page";
            StatusText.Text = "This release does not publish a checksum, so it cannot be installed from here — download it from GitHub.";
        }
    }

    private bool CanInstallHere => !string.IsNullOrEmpty(_info.SetupUrl) && _info.CanVerifySetup;

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInstallHere) { OpenReleasePage(); return; }

        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Downloading and checking…";
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
