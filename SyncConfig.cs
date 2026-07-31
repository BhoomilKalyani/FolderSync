using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSync;

/// <summary>
/// One source-to-destination copy job.
///
/// Raises change notifications so the grid can update dependent cells (the Mode text follows the
/// Mirror tick) on its own. Without that the UI had to call DataGrid.Items.Refresh(), which throws
/// if the grid happens to be in an edit transaction.
/// </summary>
public sealed class SyncTarget : INotifyPropertyChanged
{
    private string _name = "";
    private string _destination = "";
    private bool _enabled = true;
    private bool? _mirror;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Notify(nameof(Name)); } }
    }

    public string Destination
    {
        get => _destination;
        set { if (_destination != value) { _destination = value; Notify(nameof(Destination)); } }
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Notify(nameof(Enabled)); } }
    }

    /// <summary>
    /// How this destination is synced: true = mirror (an exact copy, so deletions carry over),
    /// false = additive (only ever adds and updates). Each destination decides for itself, so you
    /// can mirror to a cloud folder while a backup drive keeps everything.
    ///
    /// Nullable only for backwards compatibility: configs written before per-destination modes
    /// existed have no value here, and <see cref="SyncConfig.Load"/> fills it in on load.
    /// </summary>
    public bool? Mirror
    {
        get => _mirror;
        set
        {
            if (_mirror == value) return;
            _mirror = value;
            Notify(nameof(Mirror));
            Notify(nameof(ModeText));   // the read-only Mode column follows this
        }
    }

    /// <summary>The mode actually used; falls back to <paramref name="fallback"/> if unset.</summary>
    public bool MirrorEffective(bool fallback) => Mirror ?? fallback;

    /// <summary>How the mode reads in the UI.</summary>
    [JsonIgnore]
    public string ModeText => Mirror is null ? "(not set)" : Mirror.Value ? "Mirror" : "Additive";
}

/// <summary>When a scheduled sync should send a notification email.</summary>
public enum EmailMode { Always, OnError, OnSuccess }

/// <summary>
/// SMTP email-notification settings. The password is stored DPAPI-encrypted
/// (per-Windows-user) — never in plaintext in the JSON file.
/// </summary>
public sealed class EmailSettings
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = "";   // full email address used to sign in
    public string From { get; set; } = "";        // defaults to Username if blank
    public string To { get; set; } = "";          // one or more, comma/semicolon separated

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmailMode Mode { get; set; } = EmailMode.Always;

    public bool AttachLog { get; set; } = true;

    /// <summary>DPAPI-encrypted password as Base64. Use Set/GetPassword to access.</summary>
    public string ProtectedPassword { get; set; } = "";

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(ProtectedPassword);

    public void SetPassword(string plain)
    {
        if (string.IsNullOrEmpty(plain)) { ProtectedPassword = ""; return; }
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        ProtectedPassword = Convert.ToBase64String(enc);
    }

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword)) return "";
        try
        {
            byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }

    public string EffectiveFrom => string.IsNullOrWhiteSpace(From) ? Username : From;

    public bool ShouldSend(bool success) => Mode switch
    {
        EmailMode.OnError => !success,
        EmailMode.OnSuccess => success,
        _ => true,
    };
}

/// <summary>
/// Persisted configuration, stored as JSON under %LOCALAPPDATA%\FolderSync\config.json.
/// Created with sensible defaults on first run.
/// </summary>
public sealed class SyncConfig
{
    /// <summary>The folder pairs to sync. This replaced the old single Source + Targets model.</summary>
    public List<SyncPair> Pairs { get; set; } = new();

    /// <summary>Legacy single source folder; only read when migrating an old config into Pairs.</summary>
    public string Source { get; set; } = "";

    /// <summary>Legacy destination list; only read when migrating an old config into Pairs.</summary>
    public List<SyncTarget> Targets { get; set; } = new();

    /// <summary>
    /// Legacy setting from before each destination chose its own mode. It is now only used to fill
    /// in destinations saved by an older version (see <see cref="Load"/>), and as the fallback if a
    /// destination somehow has no mode. New destinations default to additive, which cannot delete.
    /// </summary>
    public bool Mirror { get; set; } = true;

    public EmailSettings Email { get; set; } = new();

    /// <summary>
    /// Minimum gap between trigger-based syncs (logon/unlock). Stops a sync running every
    /// time you unlock the screen. Time-based Daily/Monthly runs ignore this.
    /// </summary>
    public int MinMinutesBetweenAutoSyncs { get; set; } = 15;

    /// <summary>
    /// Mirror safety net: before a mirror purges files from a destination, move them into a
    /// timestamped folder inside that destination instead of deleting them outright.
    /// </summary>
    public bool RecycleDeletedFiles { get; set; } = true;

    /// <summary>How long to keep the moved-aside copies before pruning them.</summary>
    public int RecycleRetentionDays { get; set; } = 30;

    /// <summary>Folder (inside each destination) that holds moved-aside files. Never mirrored.</summary>
    public string RecycleFolderName { get; set; } = "_FolderSyncTrash";

    /// <summary>
    /// Stop a mirror that would delete an unexpectedly large share of the receiving side, instead of
    /// going ahead. See <see cref="DeletionGuard"/> for why this matters.
    /// </summary>
    public bool GuardLargeDeletions { get; set; } = true;

    /// <summary>Share of the receiving side that, once exceeded, is treated as suspicious.</summary>
    public int LargeDeletionPercent { get; set; } = 25;

    /// <summary>Never treat fewer files than this as suspicious, whatever the percentage.</summary>
    public int LargeDeletionMinimumFiles { get; set; } = 25;

    /// <summary>
    /// In a two-way pair, keep the copy that loses to a newer version on the other side, rather than
    /// letting it be overwritten and lost. Uses the same recycle folder as deletions.
    /// </summary>
    public bool BackupOverwrittenFiles { get; set; } = true;

    /// <summary>Check GitHub for a newer release when the app starts (at most once a day).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>"owner/repo" that publishes releases. Change only to test against another repo.</summary>
    public string UpdateRepo { get; set; } = DefaultUpdateRepo;

    public bool IncludePrereleaseUpdates { get; set; }

    [JsonIgnore]
    public const string DefaultUpdateRepo = "BhoomilKalyani/FolderSync";

    /// <summary>Repository name used before the account was renamed; migrated away from on load.</summary>
    private const string LegacyUpdateRepo = "Bhoomil1991/FolderSync";

    /// <summary>File name patterns to skip, e.g. "~$*", "Thumbs.db" (robocopy /XF).</summary>
    public List<string> ExcludeFiles { get; set; } = new() { "~$*", "Thumbs.db", "desktop.ini", ".DS_Store" };

    /// <summary>Folder names/paths to skip (robocopy /XD).</summary>
    public List<string> ExcludeFolders { get; set; } = new();

    [JsonIgnore]
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSync");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");

    [JsonIgnore]
    public static string LogDir => Path.Combine(AppDataDir, "logs");

    [JsonIgnore]
    private static string LastAutoSyncPath => Path.Combine(AppDataDir, "lastsync.txt");

    /// <summary>When a sync last completed, or null if unknown. Used to throttle unlock triggers.</summary>
    public static DateTime? GetLastSyncTime()
    {
        try
        {
            if (!File.Exists(LastAutoSyncPath)) return null;
            var text = File.ReadAllText(LastAutoSyncPath).Trim();
            return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
        }
        catch { return null; }
    }

    public static void RecordSyncTime()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(LastAutoSyncPath, DateTime.Now.ToString("o"));
        }
        catch { /* not critical */ }
    }

    /// <summary>A fresh, uniquely-named log file for one sync run (never reused).</summary>
    public static string NewSyncLogPath() => NewLogPath("sync");

    /// <summary>A fresh, uniquely-named log file for one preview (dry-run).</summary>
    public static string NewPreviewLogPath() => NewLogPath("preview");

    private static string NewLogPath(string prefix)
    {
        Directory.CreateDirectory(LogDir);
        return Path.Combine(LogDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
    }

    [JsonIgnore]
    public static string EventLogPath => Path.Combine(LogDir, "events.log");

    /// <summary>
    /// Appends one timestamped line to the rolling events log, trimming it when it gets large.
    ///
    /// Both the window and the headless run write here, and nothing used to remove anything: only
    /// sync-*.log and preview-*.log were pruned, so this file grew for the lifetime of the install.
    /// Never throws — logging must not be able to break a sync.
    /// </summary>
    public static void AppendEvent(string line)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(EventLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            TrimLog(EventLogPath);
        }
        catch { /* not critical */ }
    }

    /// <summary>
    /// Keeps only the most recent lines once a log passes a size worth caring about. Path-taking so it
    /// can be tested against a temporary file. Never throws — leaving a log long is better than
    /// losing it, and far better than breaking whatever was being logged.
    /// </summary>
    public static void TrimLog(string path, int keepLines = 500, long maxBytes = 512 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= maxBytes) return;

            var kept = File.ReadAllLines(path).TakeLast(keepLines).ToList();
            kept.Insert(0, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [Log] trimmed to the last {keepLines} entries.");
            File.WriteAllLines(path, kept);
        }
        catch { /* leaving it long is better than losing it */ }
    }

    /// <summary>Deletes sync/preview log files older than the given number of days.</summary>
    public static void CleanupOldLogs(int days = 7)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var pattern in new[] { "sync-*.log", "preview-*.log" })
            {
                foreach (var f in Directory.EnumerateFiles(LogDir, pattern))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                    catch { /* skip files we can't delete */ }
                }
            }
        }
        catch { /* logs dir missing etc. — nothing to clean */ }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static SyncConfig Load()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogDir);

        if (File.Exists(ConfigPath))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(ConfigPath));
                if (cfg is not null)
                {
                    // Return the saved settings as they are - including a blank Source, which is a
                    // legitimate state. (Treating "blank" as "invalid" here would wipe the user's
                    // own config and make an empty source impossible to save.)
                    cfg.Email ??= new EmailSettings();
                    cfg.Targets ??= new List<SyncTarget>();
                    cfg.Pairs ??= new List<SyncPair>();
                    // Destinations saved before per-destination modes existed inherit the old
                    // global setting, so an existing mirror setup keeps mirroring.
                    foreach (var t in cfg.Targets) t.Mirror ??= cfg.Mirror;
                    bool changed = cfg.MigrateTargetsToPairs();
                    changed |= cfg.DropUnusableCloudAccountPairs();
                    if (changed) cfg.Save();
                    cfg.ExcludeFiles ??= new List<string>();
                    cfg.ExcludeFolders ??= new List<string>();
                    if (string.IsNullOrWhiteSpace(cfg.UpdateRepo)
                        || cfg.UpdateRepo.Equals(LegacyUpdateRepo, StringComparison.OrdinalIgnoreCase))
                        cfg.UpdateRepo = DefaultUpdateRepo;
                    if (string.IsNullOrWhiteSpace(cfg.RecycleFolderName)) cfg.RecycleFolderName = "_FolderSyncTrash";
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                // A damaged config used to be replaced by defaults on the spot, silently destroying
                // every folder pair, the email settings and the schedule preferences. Keep the file so
                // it can be inspected or repaired by hand, and leave a note saying what happened.
                PreserveUnreadableConfig(ex);
            }
        }

        var def = Default();
        def.Save();
        return def;
    }

    /// <summary>Sets the damaged file aside and records why, then lets the caller fall back to defaults.</summary>
    private static void PreserveUnreadableConfig(Exception cause)
    {
        string? kept = MoveConfigAside(ConfigPath, DateTime.Now);
        AppendEvent($"[Config] could not be read ({cause.Message}). "
                  + (kept is null ? "It could not be set aside." : $"Kept as {kept}.")
                  + " Started from defaults.");
    }

    /// <summary>
    /// Renames a config file that could not be read to "config.bad-&lt;timestamp&gt;.json", so the
    /// defaults written next do not destroy it. Returns the new path, or null if it could not be moved.
    ///
    /// Takes the path rather than using <see cref="ConfigPath"/> so it can be tested without touching
    /// the real configuration. Never throws: this runs on the way to a fallback that has to succeed.
    /// </summary>
    public static string? MoveConfigAside(string configPath, DateTime when)
    {
        try
        {
            if (!File.Exists(configPath)) return null;

            string folder = Path.GetDirectoryName(configPath) ?? "";
            string kept = Path.Combine(folder, $"config.bad-{when:yyyyMMdd-HHmmss}.json");
            File.Move(configPath, kept, overwrite: true);
            return kept;
        }
        catch { return null; }
    }

    /// <summary>
    /// Removes pairs that point at a cloud account through the old "account:folder" syntax.
    ///
    /// A short-lived version reached cloud storage through a provider's API, which needed an OAuth
    /// client that Google is withdrawing. Cloud storage is now reached as an ordinary folder through
    /// the provider's own desktop app, so these pairs can no longer run - and leaving them in place
    /// would make every sync report a failure. Returns true when something was removed.
    /// </summary>
    public bool DropUnusableCloudAccountPairs()
    {
        int before = Pairs.Count;
        Pairs.RemoveAll(p => LooksLikeCloudAccountPath(p.Left) || LooksLikeCloudAccountPath(p.Right));
        return Pairs.Count != before;
    }

    /// <summary>
    /// True for "gdrive:Documents" but not for "C:\Docs" or "D:". The name has to be at least two
    /// characters, which is what keeps a drive letter from matching.
    /// </summary>
    public static bool LooksLikeCloudAccountPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        int colon = path.IndexOf(':');
        if (colon < 2) return false;

        foreach (char c in path[..colon])
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return false;

        return true;
    }

    /// <summary>
    /// Turns an old single-source config into folder pairs — one "left to right" pair per
    /// destination, keeping each destination's mirror setting, so behaviour is unchanged.
    /// Returns true when something was migrated (and the caller should save).
    /// </summary>
    public bool MigrateTargetsToPairs()
    {
        if (Pairs.Count > 0) return false;                       // already on the new model
        if (Targets.Count == 0 || string.IsNullOrWhiteSpace(Source)) return false;

        foreach (var t in Targets)
        {
            Pairs.Add(new SyncPair
            {
                Name = t.Name,
                Left = Source,
                Right = t.Destination,
                Direction = SyncDirection.ToRight,
                Mirror = t.MirrorEffective(Mirror),
                Enabled = t.Enabled,
            });
        }

        // Cleared so the old fields can't drift out of step with the pairs that replaced them.
        Targets.Clear();
        Source = "";
        return true;
    }

    /// <summary>True on a first run: nothing has been configured yet.</summary>
    [JsonIgnore]
    public bool IsUnconfigured => Pairs is null || Pairs.Count == 0;

    /// <summary>The pairs that a sync will actually process.</summary>
    [JsonIgnore]
    public IEnumerable<SyncPair> ActivePairs => (Pairs ?? new List<SyncPair>()).Where(p => p.Enabled);

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>
    /// Settings for a brand-new install: deliberately empty, so each user picks their own
    /// source and destinations. (An update never reaches here - the existing config.json in
    /// %LOCALAPPDATA% is left untouched by Setup and is loaded above.)
    /// </summary>
    private static SyncConfig Default() => new()
    {
        Source = "",
        Mirror = true,
        Targets = new List<SyncTarget>(),
    };
}
