using System.Windows;

namespace FolderSync;

/// <summary>One line in a list of changes: a file, what happens to it, and where.</summary>
public sealed class ChangeRow
{
    public string Pair { get; init; } = "";
    public string What { get; init; } = "";
    public string File { get; init; } = "";

    /// <summary>Bytes, or -1 when not known.</summary>
    public long Size { get; init; } = -1;
    public string SizeText => Size < 0 ? "" : Humanise.Size(Size);

    /// <summary>The folder that receives the change.</summary>
    public string Into { get; init; } = "";

    /// <summary>A change as a sync would make it (a preview) or as it made it (the history).</summary>
    public static ChangeRow From(FileChange change, string pair, string left, string right, bool planned) => new()
    {
        Pair = pair,
        What = planned ? change.PlanText : change.KindText,
        File = change.Path,
        Size = change.Size,
        Into = change.ToLeft ? left : right,
    };
}

/// <summary>
/// A table of file changes. Used for the preview ("this is what a sync would do") and for reviewing a
/// mirror the deletion guard stopped ("this is what it would delete - approve it?").
///
/// The preview used to be the raw robocopy log in a text box, which had every fact in it and answered
/// no question at a glance. The log is still one click away.
/// </summary>
public partial class ChangesWindow : Window
{
    private readonly string? _log;

    /// <summary>True when the window was closed with its approve button.</summary>
    public bool Approved { get; private set; }

    /// <param name="warning">Shown in a box above the table, or null.</param>
    /// <param name="logText">The full log behind the table; null hides the link to it.</param>
    /// <param name="approveLabel">Text for a red button that approves what the table shows; null for a plain viewer.</param>
    /// <param name="emptyText">Shown in place of the table when there are no rows.</param>
    /// <param name="footnote">Shown beside the buttons, e.g. how many more changes were not listed.</param>
    public ChangesWindow(string title, string heading, string summary, IReadOnlyList<ChangeRow> rows,
                         string? warning = null, string? logText = null, string? approveLabel = null,
                         string emptyText = "Nothing to show.", string? footnote = null)
    {
        InitializeComponent();
        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        Title = title;
        Heading.Text = heading;
        Summary.Text = summary;
        _log = logText;

        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningBox.Visibility = Visibility.Visible;
        }

        ChangeGrid.ItemsSource = rows;
        if (rows.Count == 0)
        {
            ChangeGrid.Visibility = Visibility.Collapsed;
            EmptyText.Text = emptyText;
            EmptyText.Visibility = Visibility.Visible;
        }

        // With one pair the column says the same thing on every row.
        if (rows.Select(r => r.Pair).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1)
            ChangeGrid.Columns[0].Visibility = Visibility.Collapsed;

        CountText.Text = footnote ?? "";
        LogButton.Visibility = string.IsNullOrEmpty(logText) ? Visibility.Collapsed : Visibility.Visible;

        if (approveLabel is not null)
        {
            ApproveButton.Content = approveLabel;
            ApproveButton.Visibility = Visibility.Visible;
            CloseButton.Content = "Cancel";
        }
        else
        {
            CloseButton.Style = (Style)FindResource("PrimaryButton");
            CloseButton.Margin = new Thickness(0);
        }
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_log is null) return;
        new TextWindow("Log", "The full log behind this list", _log) { Owner = this }.ShowDialog();
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        Approved = true;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
