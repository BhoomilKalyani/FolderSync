using System.Windows;
using System.Windows.Controls;

namespace FolderSync;

/// <summary>
/// The History page: every stored run, what started it and what it did - and, for the selected run,
/// each pair's result and the files it copied, replaced or removed.
/// </summary>
public partial class MainWindow
{
    private List<RunRecord> _history = new();
    private int _historyLoad;

    private void HistoryRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadHistoryAsync();

    private void HistoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded) ShowHistory((HistoryGrid.SelectedItem as RunRecord)?.Id);
    }

    /// <summary>
    /// Reads the stored runs off the UI thread - there can be a couple of thousand small files - and
    /// shows them. Loads can overlap (opening the page, Refresh, a sync finishing), so only the newest
    /// one's result is shown. Never throws: callers discard the task.
    /// </summary>
    private async Task LoadHistoryAsync()
    {
        int load = ++_historyLoad;
        HistoryRefreshButton.IsEnabled = false;
        try
        {
            string? selected = (HistoryGrid.SelectedItem as RunRecord)?.Id;
            if (_history.Count == 0) HistoryStatus.Text = "Reading the history…";

            var runs = await Task.Run(() => RunHistoryStore.Load());
            if (load != _historyLoad) return;

            _history = runs;
            ShowHistory(selected);
        }
        catch (Exception ex)
        {
            if (load != _historyLoad) return;
            AppendEvent("[History] could not be read: " + ex);
            HistoryStatus.Text = "Could not read the history: " + ex.Message;
        }
        finally
        {
            if (load == _historyLoad) HistoryRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>The runs the list is showing: all of them, or only those with something to report.</summary>
    private List<RunRecord> ShownRuns() =>
        HistoryOnlyChanges.IsChecked == true
            ? _history.Where(r => r.ChangedSomething || r.Health != PairHealth.Ok).ToList()
            : _history;

    private void ShowHistory(string? reselect)
    {
        var shown = ShownRuns();
        HistoryGrid.ItemsSource = shown;
        if (reselect is not null)
            HistoryGrid.SelectedItem = shown.FirstOrDefault(r => r.Id == reselect);

        string kept = $"History is kept for {Humanise.Count(_config.HistoryRetentionDays, "day")}.";
        HistoryStatus.Text = _history.Count == 0
            ? "No sync has been recorded yet. Every sync from now on appears here."
            : shown.Count == _history.Count
                ? $"{Humanise.Count(_history.Count, "run")}. {kept}"
                : $"Showing {shown.Count:N0} of {Humanise.Count(_history.Count, "run")}. {kept}";

        HistoryExportButton.IsEnabled = shown.Count > 0;
        if (HistoryGrid.SelectedItem is not RunRecord) ShowRun(null);
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowRun(HistoryGrid.SelectedItem as RunRecord);

    /// <summary>The details card: each pair's result, and the list of what changed.</summary>
    private void ShowRun(RunRecord? run)
    {
        if (run is null)
        {
            HistoryDetailTitle.Text = "Run details";
            HistoryDetailNote.Text = _history.Count == 0 ? "" : "Select a run above to see what it did.";
            HistoryPairGrid.Visibility = Visibility.Collapsed;
            HistoryChangeTitle.Visibility = Visibility.Collapsed;
            HistoryChangeGrid.Visibility = Visibility.Collapsed;
            HistoryChangeNote.Text = "";
            HistoryOpenLogButton.IsEnabled = false;
            HistoryOpenLogButton.ToolTip = null;
            return;
        }

        HistoryDetailTitle.Text = $"{run.WhenText} — {run.TriggerText}";
        HistoryDetailNote.Text = $"{run.ResultText} · took {run.DurationText}"
                               + (string.IsNullOrWhiteSpace(run.Note) ? "" : $" · {run.Note}");

        HistoryPairGrid.ItemsSource = run.Pairs;
        HistoryPairGrid.Visibility = run.Pairs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = run.Pairs
            .SelectMany(p => p.Changes.Select(c => ChangeRow.From(c, p.Name, p.Left, p.Right, planned: false)))
            .ToList();
        HistoryChangeGrid.ItemsSource = rows;
        HistoryChangeGrid.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryChangeTitle.Visibility = HistoryChangeGrid.Visibility;

        int omitted = run.Pairs.Sum(p => p.ChangesOmitted);
        HistoryChangeNote.Text =
            rows.Count == 0 ? (run.Pairs.Count == 0 ? "" : "Nothing was copied or removed in this run.")
            : omitted > 0 ? $"…and {omitted:N0} more that were counted but not listed. The log has every one."
            : "";

        bool hasLog = !string.IsNullOrEmpty(run.LogFile) && File.Exists(Path.Combine(SyncConfig.LogDir, run.LogFile));
        HistoryOpenLogButton.IsEnabled = hasLog;
        HistoryOpenLogButton.ToolTip = hasLog
            ? null
            : $"This run's log has been removed. Logs are kept for {Humanise.Count(_config.LogRetentionDays, "day")}.";
    }

    private void HistoryOpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not RunRecord run || string.IsNullOrEmpty(run.LogFile)) return;

        string path = Path.Combine(SyncConfig.LogDir, run.LogFile);
        try
        {
            new TextWindow("Log", $"Log of the sync on {run.WhenText}", File.ReadAllText(path)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageWindow.Warn(this, "Could not open the log", $"{path}\n\n{ex.Message}");
        }
    }

    private void HistoryExport_Click(object sender, RoutedEventArgs e)
    {
        var runs = ShownRuns();
        if (runs.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the run history",
            FileName = $"FolderSync-history-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            HistoryCsv.Save(dialog.FileName, runs);
            HistoryStatus.Text = $"Exported {Humanise.Count(runs.Count, "run")} to {dialog.FileName}";
            AppendEvent($"[History] exported {runs.Count} run(s) to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageWindow.Warn(this, "Could not export", $"{dialog.FileName}\n\n{ex.Message}");
        }
    }
}
