using System.Text.Json;

namespace FolderSync;

/// <summary>Outcome of one destination in the most recent sync.</summary>
public sealed class DestinationRun
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public int ExitCode { get; set; }
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    public int FilesRecycled { get; set; }
    public int FilesBackedUp { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Short outcome shown in the UI: "OK", "FAILED", or the reason it was skipped.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResultText => Skipped
        ? (string.IsNullOrWhiteSpace(Note) ? "Skipped" : "Skipped — " + Note)
        : Success ? "OK"
        : string.IsNullOrWhiteSpace(Note) ? "FAILED" : Note;
}

/// <summary>How one folder pair finished, the last time it ran.</summary>
public sealed class PairStatus
{
    public DateTime When { get; set; }
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public int FilesCopied { get; set; }
    public string Note { get; set; } = "";

    /// <summary>
    /// The one-line form shown in the pairs grid, e.g. "OK · 2 hours ago". The point of this column
    /// is that a pair which has been quietly failing for a week is visible at a glance rather than
    /// only discoverable by reading logs.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Text => $"{(Skipped ? "Not connected" : Success ? "OK" : "Failed")} · {Humanise.Ago(When)}";
}

/// <summary>
/// The last outcome of each folder pair, kept across runs so the grid can show which pairs are
/// healthy. Separate from <see cref="LastRun"/>, which only ever holds the most recent sync as a
/// whole and so cannot say when a particular pair last worked.
/// </summary>
public static class PairHistory
{
    private static string Path_ => Path.Combine(SyncConfig.AppDataDir, "pairstatus.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>
    /// Identifies a pair by its two folders rather than its name, so renaming a pair keeps its
    /// history and pointing it at different folders correctly starts afresh.
    /// </summary>
    public static string KeyFor(string left, string right) =>
        (Tidy(left) + "|" + Tidy(right)).ToLowerInvariant();

    private static string Tidy(string path) =>
        (path ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static Dictionary<string, PairStatus> Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new Dictionary<string, PairStatus>();
            return JsonSerializer.Deserialize<Dictionary<string, PairStatus>>(File.ReadAllText(Path_))
                   ?? new Dictionary<string, PairStatus>();
        }
        catch { return new Dictionary<string, PairStatus>(); }
    }

    /// <summary>
    /// Merges a finished run into the stored history. Pairs that did not take part keep whatever they
    /// had, so running one pair does not erase the others' status. Never throws.
    /// </summary>
    public static void Record(SyncResult result)
    {
        try
        {
            var all = Load();
            foreach (var target in result.Targets)
            {
                if (string.IsNullOrWhiteSpace(target.PairKey)) continue;
                all[target.PairKey] = new PairStatus
                {
                    When = DateTime.Now,
                    Success = target.Success,
                    Skipped = target.Skipped,
                    FilesCopied = target.FilesCopied,
                    Note = target.Note,
                };
            }

            Directory.CreateDirectory(SyncConfig.AppDataDir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(all, Opts));
        }
        catch { /* reporting must never break a sync */ }
    }
}

/// <summary>
/// The last sync's result, persisted so the window can show a summary without re-reading logs.
/// </summary>
public sealed class LastRun
{
    public DateTime When { get; set; }
    public bool Success { get; set; }
    public List<DestinationRun> Destinations { get; set; } = new();

    private static string Path_ => Path.Combine(SyncConfig.AppDataDir, "lastrun.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static LastRun? Load()
    {
        try
        {
            return File.Exists(Path_) ? JsonSerializer.Deserialize<LastRun>(File.ReadAllText(Path_)) : null;
        }
        catch { return null; }
    }

    /// <summary>Records a finished sync. Never throws — reporting must not break a sync.</summary>
    public static void Save(SyncResult result)
    {
        try
        {
            var info = new LastRun
            {
                When = DateTime.Now,
                Success = result.Success,
                Destinations = result.Targets.Select(t => new DestinationRun
                {
                    Name = t.Name,
                    Destination = t.Destination,
                    Success = t.Success,
                    Skipped = t.Skipped,
                    ExitCode = t.ExitCode,
                    FilesCopied = t.FilesCopied,
                    FilesFailed = t.FilesFailed,
                    FilesRecycled = t.FilesRecycled,
                    FilesBackedUp = t.FilesBackedUp,
                    Note = t.Note,
                }).ToList(),
            };
            Directory.CreateDirectory(SyncConfig.AppDataDir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(info, Opts));
        }
        catch { /* not critical */ }
    }
}
