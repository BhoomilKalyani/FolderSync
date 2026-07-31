using System.Text.Json;

namespace FolderSync;

/// <summary>Outcome of one destination in the most recent sync.</summary>
public sealed class DestinationRun
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public int FilesCopied { get; set; }
    public int FilesFailed { get; set; }
    public int FilesRecycled { get; set; }
    public string Note { get; set; } = "";
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
                    ExitCode = t.ExitCode,
                    FilesCopied = t.FilesCopied,
                    FilesFailed = t.FilesFailed,
                    FilesRecycled = t.FilesRecycled,
                    Note = t.Note,
                }).ToList(),
            };
            Directory.CreateDirectory(SyncConfig.AppDataDir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(info, Opts));
        }
        catch { /* not critical */ }
    }
}
