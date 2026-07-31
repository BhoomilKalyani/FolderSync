using System.Windows;

namespace FolderSync;

/// <summary>Read-only text viewer used for the health report and the sync preview.</summary>
public partial class TextWindow : Window
{
    private readonly string _text;

    public TextWindow(string title, string heading, string text)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = heading;
        Body.Text = text;
        _text = text;
        Icon = AppResources.WindowIcon;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_text); } catch { /* clipboard busy */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
