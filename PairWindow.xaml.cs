using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace FolderSync;

/// <summary>Creates or edits one folder pair. DialogResult is true when the user saved.</summary>
public partial class PairWindow : Window
{
    private readonly SyncPair _pair;
    private readonly bool _isNew;

    public PairWindow(SyncPair pair, bool isNew)
    {
        InitializeComponent();
        _pair = pair;
        _isNew = isNew;

        Title = isNew ? "Add folder pair" : "Edit folder pair";
        Heading.Text = Title;
        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);

        NameBox.Text = pair.Name;
        LeftBox.Text = pair.Left;
        RightBox.Text = pair.Right;
        DirectionBox.SelectedIndex = pair.Direction switch
        {
            SyncDirection.ToRight => 0,
            SyncDirection.ToLeft => 1,
            _ => 2,
        };
        MirrorCheck.IsChecked = pair.Mirror;
        UpdateHelp();
    }

    private SyncDirection SelectedDirection => DirectionBox.SelectedIndex switch
    {
        0 => SyncDirection.ToRight,
        1 => SyncDirection.ToLeft,
        _ => SyncDirection.TwoWay,
    };

    private void Direction_Changed(object sender, SelectionChangedEventArgs e) => UpdateHelp();
    private void Mirror_Changed(object sender, RoutedEventArgs e) => UpdateHelp();
    private void Path_Changed(object sender, TextChangedEventArgs e) => UpdateHelp();

    private void UpdateHelp()
    {
        if (DirectionHelp is null) return;   // still initialising

        bool twoWay = SelectedDirection == SyncDirection.TwoWay;

        DirectionHelp.Text = SelectedDirection switch
        {
            SyncDirection.ToRight => "Changes in the left folder are copied to the right folder. The left folder is never modified.",
            SyncDirection.ToLeft => "Changes in the right folder are copied to the left folder. The right folder is never modified.",
            _ => "Both folders receive whatever is newer on the other side, so they end up holding everything. "
               + "If the same file changed on both sides, the newer one wins.",
        };

        // Mirror cannot apply to two-way: without a record of the previous run there is no way to
        // tell a deletion on one side from a new file on the other, so deleting would be guesswork.
        MirrorCheck.IsEnabled = !twoWay;
        MirrorHelp.Text = twoWay
            ? "Two-way never deletes: a file removed from one side is restored from the other on the next sync."
            : MirrorCheck.IsChecked == true
                ? "Files missing from the source will be removed from the receiving folder — recoverable from Recycled files."
                : "Additive: files are only added or updated, never deleted.";

        // Red for something that blocks saving, muted grey for a heads-up.
        string? error = Problem();
        string? note = error is null ? Candidate()?.FolderNote() : null;
        ErrorText.Text = error ?? note ?? "";
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource(error is not null ? "Bad" : "FgMuted");
    }

    /// <summary>The pair as currently typed, or null while a folder is still blank.</summary>
    private SyncPair? Candidate()
    {
        string left = LeftBox.Text.Trim();
        string right = RightBox.Text.Trim();
        if (left.Length == 0 || right.Length == 0) return null;

        return new SyncPair
        {
            Left = left,
            Right = right,
            Direction = SelectedDirection,
            Mirror = MirrorCheck.IsChecked == true,
        };
    }

    /// <summary>
    /// Validation shown live and enforced on save. Delegates to SyncPair so the dialog applies
    /// exactly the same rule the engine will — checked for both folders, not just the left one.
    /// </summary>
    private string? Problem() => Candidate()?.ValidateFolders();

    private void BrowseLeft_Click(object sender, RoutedEventArgs e)
    {
        var picked = Pick("Select the left folder", LeftBox.Text);
        if (picked is not null) LeftBox.Text = picked;
    }

    private void BrowseRight_Click(object sender, RoutedEventArgs e)
    {
        var picked = Pick("Select the right folder", RightBox.Text);
        if (picked is not null) RightBox.Text = picked;
    }

    private static string? Pick(string description, string? startAt)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt)) dlg.SelectedPath = startAt;
        return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string left = LeftBox.Text.Trim();
        string right = RightBox.Text.Trim();

        if (left.Length == 0 || right.Length == 0)
        {
            MessageWindow.Warn(this, "Folders required", "Please choose both a left and a right folder.");
            return;
        }
        if (Problem() is { } problem)
        {
            MessageWindow.Warn(this, "Cannot save this pair", problem);
            return;
        }

        _pair.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : SyncPair.SuggestName(left, right);
        _pair.Left = left;
        _pair.Right = right;
        _pair.Direction = SelectedDirection;
        _pair.Mirror = SelectedDirection != SyncDirection.TwoWay && MirrorCheck.IsChecked == true;
        if (_isNew) _pair.Enabled = true;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
