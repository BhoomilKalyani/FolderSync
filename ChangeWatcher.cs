using System.Text.RegularExpressions;

namespace FolderSync;

/// <summary>
/// Decides when a burst of file changes has settled enough to sync.
///
/// <para>Saving a document is not one event: an editor writes a temporary file, renames it, touches the
/// folder. Syncing on the first event would copy a half-written file, so a sync waits until nothing has
/// changed for a while (<see cref="Quiet"/>). A folder that never goes quiet - a long download, an app
/// that autosaves every few seconds - would then never sync at all, so there is also a longest wait
/// (<see cref="MaxWait"/>), counted from the first change.</para>
///
/// Holds no clock and no timer, so it can be tested by handing it times.
/// </summary>
public sealed class ChangeDebouncer
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _first;
    private DateTime _last;

    public TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    public bool HasPending
    {
        get { lock (_gate) return _pending.Count > 0; }
    }

    /// <summary>Something changed for these pairs.</summary>
    public void Touch(IEnumerable<string> pairKeys, DateTime now)
    {
        lock (_gate)
        {
            if (_pending.Count == 0) _first = now;
            _last = now;
            foreach (var key in pairKeys) _pending.Add(key);
        }
    }

    /// <summary>
    /// The pairs to sync now, or none. Returns them all at once and forgets them: a change that arrives
    /// afterwards starts a new wait.
    /// </summary>
    public IReadOnlyCollection<string> TakeDue(DateTime now)
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return Array.Empty<string>();
            if (now - _last < Quiet && now - _first < MaxWait) return Array.Empty<string>();

            var due = _pending.ToList();
            _pending.Clear();
            return due;
        }
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
    }
}

/// <summary>
/// Watches the sending folder of each enabled pair and says when a sync is due, so a file dropped into
/// a folder reaches the other side in about a minute instead of at the next unlock or scheduled time.
///
/// <para>Only the side a pair reads from is watched. For a one-directional pair a sync never writes
/// there, so the sync's own copying cannot set off another sync. A two-way pair reads from both sides
/// and writes to both, so changes on its folders are ignored while a sync from this app is running and
/// for a moment afterwards.</para>
///
/// It only works while the app is running - which is why the app can start in the tray at sign-in.
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
    /// <summary>How long after a sync the folders it wrote to are still ignored.</summary>
    private static readonly TimeSpan SettleAfterSync = TimeSpan.FromSeconds(3);

    /// <summary>How often a folder that could not be watched (a drive that is not connected) is tried again.</summary>
    private const int RetryEveryTicks = 30;

    private sealed class Watch
    {
        public string Root = "";
        public List<string> PairKeys = new();
        /// <summary>A sync writes into this folder (a two-way pair), so its own changes must not count.</summary>
        public bool WrittenBySync;
        public FileSystemWatcher? Watcher;
    }

    private readonly object _gate = new();
    private readonly ChangeDebouncer _debouncer = new();
    private readonly Func<DateTime> _clock;
    private readonly System.Threading.Timer _timer;
    private List<Watch> _watches = new();
    private List<Regex> _ignoredNames = new();
    private string _recycleSegment = "";
    private bool _enabled;
    private bool _syncing;
    private DateTime _ignoreWritesUntil;
    private int _ticks;
    private bool _disposed;

    /// <summary>
    /// The pairs (by <see cref="PairHistory.KeyFor"/>) whose folders changed and have settled. Raised on
    /// a timer thread. If the sync cannot start, hand them back with <see cref="Requeue"/>.
    /// </summary>
    public event Action<IReadOnlyCollection<string>>? Due;

    /// <summary>Raised when the number of folders being watched changes.</summary>
    public event Action? StatusChanged;

    public ChangeWatcher(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Folders that should be watched, and how many of them are.</summary>
    public (int wanted, int watching) Counts
    {
        get { lock (_gate) return (_watches.Count, _watches.Count(w => w.Watcher is not null)); }
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>A change has been seen and is waiting for the folder to go quiet.</summary>
    public bool HasPendingChanges => _debouncer.HasPending;

    public string StatusText
    {
        get
        {
            if (!Enabled) return "Off";
            var (wanted, watching) = Counts;
            if (wanted == 0) return "On, but no enabled pair has a folder to watch.";
            if (watching == wanted) return $"Watching {Humanise.Count(watching, "folder")} for changes.";
            return $"Watching {watching} of {Humanise.Count(wanted, "folder")}; "
                 + $"{wanted - watching} cannot be reached right now and will be picked up when it can.";
        }
    }

    /// <summary>
    /// Starts, stops or re-targets the watching to match the settings. Safe to call whenever the pairs or
    /// the settings change.
    /// </summary>
    public void Configure(SyncConfig config)
    {
        List<Watch> old;
        lock (_gate)
        {
            if (_disposed) return;
            old = _watches;
            _watches = new List<Watch>();
            _enabled = config.SyncOnChange;
            _debouncer.Quiet = TimeSpan.FromSeconds(Math.Clamp(config.SyncOnChangeDelaySeconds, 5, 3600));
            _debouncer.Clear();

            _recycleSegment = config.SafeRecycleFolderName;
            _ignoredNames = (config.ExcludeFiles ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => WildcardToRegex(p.Trim()))
                .ToList();

            if (_enabled) _watches = BuildWatches(config);
        }

        foreach (var watch in old) Stop(watch);

        if (_enabled)
        {
            foreach (var watch in _watches) TryStart(watch);
            _timer.Change(1000, 1000);
        }
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        StatusChanged?.Invoke();
    }

    /// <summary>One watch per distinct folder, each knowing every pair that reads from it.</summary>
    private static List<Watch> BuildWatches(SyncConfig config)
    {
        var byRoot = new Dictionary<string, Watch>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.ActivePairs)
        {
            if (pair.Validate() is not null) continue;      // would not sync anyway
            string key = PairHistory.KeyFor(pair.Left, pair.Right);
            bool twoWay = pair.Direction == SyncDirection.TwoWay;

            foreach (var pass in pair.Passes())
            {
                string root = pass.From.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;      // "E:" means "E:\"
                if (!byRoot.TryGetValue(root, out var watch))
                    byRoot[root] = watch = new Watch { Root = root };
                if (!watch.PairKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) watch.PairKeys.Add(key);
                watch.WrittenBySync |= twoWay;
            }
        }
        return byRoot.Values.ToList();
    }

    private void TryStart(Watch watch)
    {
        try
        {
            if (!Directory.Exists(watch.Root)) return;

            var fsw = new FileSystemWatcher(watch.Root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            fsw.Changed += (_, e) => OnChange(watch, e.FullPath);
            fsw.Created += (_, e) => OnChange(watch, e.FullPath);
            fsw.Deleted += (_, e) => OnChange(watch, e.FullPath);
            fsw.Renamed += (_, e) => OnChange(watch, e.FullPath);
            // The buffer overflowed, or the folder went away. Something changed, even if not what: sync,
            // and start watching afresh.
            fsw.Error += (_, _) =>
            {
                OnChange(watch, null);
                Stop(watch);
                StatusChanged?.Invoke();
            };
            fsw.EnableRaisingEvents = true;

            lock (_gate)
            {
                if (_disposed || !_watches.Contains(watch)) { fsw.Dispose(); return; }
                watch.Watcher = fsw;
            }
        }
        catch { /* not reachable right now; retried from the timer */ }
    }

    private void Stop(Watch watch)
    {
        FileSystemWatcher? fsw;
        lock (_gate)
        {
            fsw = watch.Watcher;
            watch.Watcher = null;
        }
        try { fsw?.Dispose(); } catch { }
    }

    private void OnChange(Watch watch, string? path)
    {
        if (path is not null && IsNoise(path)) return;

        DateTime now = _clock();
        lock (_gate)
        {
            if (!_enabled) return;
            // A sync from this app is writing here: these are its own changes, not the user's.
            if (watch.WrittenBySync && (_syncing || now < _ignoreWritesUntil)) return;
        }
        _debouncer.Touch(watch.PairKeys, now);
    }

    /// <summary>
    /// Changes that should not start a sync: anything in the recycle folder (the sync's own
    /// housekeeping), and names the user has said to skip, which a sync would not copy anyway.
    /// </summary>
    public bool IsNoise(string path)
    {
        string segment;
        List<Regex> ignored;
        lock (_gate)
        {
            segment = _recycleSegment;
            ignored = _ignoredNames;
        }

        if (segment.Length > 0)
        {
            foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (part.Equals(segment, StringComparison.OrdinalIgnoreCase)) return true;
        }

        string name = Path.GetFileName(path);
        foreach (var pattern in ignored)
            if (pattern.IsMatch(name)) return true;

        return false;
    }

    /// <summary>A skip-list pattern ("~$*", "*.tmp", "Thumbs.db") as a whole-name, case-insensitive match.</summary>
    public static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Tell the watcher a sync from this app has started, so its own writes are not counted.</summary>
    public void SyncStarted()
    {
        lock (_gate) _syncing = true;
    }

    public void SyncFinished()
    {
        lock (_gate)
        {
            _syncing = false;
            _ignoreWritesUntil = _clock() + SettleAfterSync;
        }
    }

    /// <summary>The sync for these pairs could not start; try again after the usual quiet time.</summary>
    public void Requeue(IEnumerable<string> pairKeys) => _debouncer.Touch(pairKeys, _clock());

    private void Tick()
    {
        try
        {
            IReadOnlyCollection<string> due = Array.Empty<string>();
            List<Watch> down = new();
            lock (_gate)
            {
                if (_disposed || !_enabled) return;
                if (!_syncing) due = _debouncer.TakeDue(_clock());
                if (++_ticks % RetryEveryTicks == 0) down = _watches.Where(w => w.Watcher is null).ToList();
            }

            if (down.Count > 0)
            {
                foreach (var watch in down) TryStart(watch);
                if (down.Any(w => w.Watcher is not null)) StatusChanged?.Invoke();
            }

            if (due.Count > 0) Due?.Invoke(due);
        }
        catch { /* a timer callback must never throw */ }
    }

    public void Dispose()
    {
        List<Watch> watches;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watches = _watches;
            _watches = new List<Watch>();
        }
        _timer.Dispose();
        foreach (var watch in watches) Stop(watch);
    }
}
