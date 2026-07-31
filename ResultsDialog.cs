namespace FolderSync;

/// <summary>A simple modal window that shows multi-line text (used for the sync Preview).</summary>
public sealed class ResultsDialog : Form
{
    public ResultsDialog(string title, string text)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(480, 320);
        Font = new Font("Segoe UI", 9F);

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            Text = text,
        };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "Copy to clipboard", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(text); } catch { /* clipboard busy */ } };
        bottom.Controls.Add(close);
        bottom.Controls.Add(copy);

        Controls.Add(box);
        Controls.Add(bottom);
        AcceptButton = close;
        CancelButton = close;
    }
}
