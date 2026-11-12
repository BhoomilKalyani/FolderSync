using System.Text;

namespace FolderSync;

/// <summary>
/// A sync log written to disk as each line arrives, so whatever happened before a run was stopped is
/// already saved. Thread-safe: robocopy's output and error streams are read on separate threads and both
/// write here. Never throws - logging must not be able to break a sync.
/// </summary>
public sealed class SyncLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    private SyncLog(StreamWriter? writer) => _writer = writer;

    /// <summary>Creates the file. If it cannot be created, lines are dropped rather than failing the sync.</summary>
    public static SyncLog Open(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new SyncLog(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { return new SyncLog(null); }
    }

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            try { _writer?.WriteLine(line); }
            catch { /* disk full or the like: keep syncing */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
