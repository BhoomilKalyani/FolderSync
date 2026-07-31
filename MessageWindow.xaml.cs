using System.Windows;
using System.Windows.Media;

namespace FolderSync;

/// <summary>Which button the user pressed.</summary>
public enum DialogChoice { Primary, Secondary, Cancel }

public enum DialogTone { Info, Warning, Danger }

/// <summary>
/// A themed replacement for <c>MessageBox</c>. The Win32 message box ignores the app's theme and
/// looks a generation older than the rest of the UI, so all prompts use this instead.
/// </summary>
public partial class MessageWindow : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    private MessageWindow(string heading, string body, DialogTone tone,
                          string primary, string? secondary, string? cancel)
    {
        InitializeComponent();
        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        Title = heading;
        HeadingText.Text = heading;
        BodyText.Text = body;

        Accentbar.Background = (Brush)FindResource(tone switch
        {
            DialogTone.Warning => "Warn",
            DialogTone.Danger => "Bad",
            _ => "Accent",
        });

        PrimaryBtn.Content = primary;
        if (secondary is not null)
        {
            SecondaryBtn.Content = secondary;
            SecondaryBtn.Visibility = Visibility.Visible;
        }
        if (cancel is not null)
        {
            CancelBtn.Content = cancel;
            CancelBtn.Visibility = Visibility.Visible;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Finish(DialogChoice.Primary);
    private void Secondary_Click(object sender, RoutedEventArgs e) => Finish(DialogChoice.Secondary);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(DialogChoice.Cancel);

    private void Finish(DialogChoice choice)
    {
        _choice = choice;
        DialogResult = choice != DialogChoice.Cancel;
        Close();
    }

    private static DialogChoice Show(Window? owner, string heading, string body, DialogTone tone,
                                     string primary, string? secondary, string? cancel)
    {
        var dlg = new MessageWindow(heading, body, tone, primary, secondary, cancel);
        if (owner is not null && owner.IsLoaded) dlg.Owner = owner;
        dlg.ShowDialog();
        return dlg._choice;
    }

    // ---------- Convenience wrappers ----------

    public static void Info(Window? owner, string heading, string body) =>
        Show(owner, heading, body, DialogTone.Info, "OK", null, null);

    public static void Warn(Window? owner, string heading, string body) =>
        Show(owner, heading, body, DialogTone.Warning, "OK", null, null);

    /// <summary>Two-button question. Returns true when the primary (affirmative) button is pressed.</summary>
    public static bool Confirm(Window? owner, string heading, string body,
                               string primary = "Yes", string cancel = "Cancel",
                               DialogTone tone = DialogTone.Info) =>
        Show(owner, heading, body, tone, primary, null, cancel) == DialogChoice.Primary;

    /// <summary>Three-button question, for choices that are not simply yes/no.</summary>
    public static DialogChoice Choose(Window? owner, string heading, string body,
                                      string primary, string secondary, string cancel = "Cancel",
                                      DialogTone tone = DialogTone.Info) =>
        Show(owner, heading, body, tone, primary, secondary, cancel);
}
