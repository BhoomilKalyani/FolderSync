using System.ComponentModel;
using System.Globalization;
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

/// <summary>
/// config.json exists but could not be opened, even after waiting - another program kept it locked, or
/// access is denied. Distinct from a damaged file: nothing is set aside or overwritten, and a scheduled
/// run fails rather than syncing with settings that are not the user's.
/// </summary>
public sealed class ConfigUnavailableException : IOException
{
    public ConfigUnavailableException(string path, Exception inner)
        : base($"The settings file could not be opened ({inner.Message}): {path}", inner) { }
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
    public int LargeDeletionPercent { get; set; } = DefaultLargeDeletionPercent;

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

    public const int DefaultLargeDeletionPercent = 25;

    /// <summary>
    /// Why these are the built-in defaults rather than the user's own settings, or null when config.json
    /// was read normally. Set when the file was damaged. A scheduled run treats it as a failure, so a
    /// broken config raises an alarm instead of quietly syncing nothing and reporting success.
    /// </summary>
    [JsonIgnore]
    public string? LoadProblem { get; private set; }

    /// <summary>
    /// The two-way "keep the previous version" safety net is actually in force. It needs both settings:
    /// the previous versions are filed in the recycle folder, so turning recycling off turns this off too.
    /// The engine and the health check both read this, so they cannot disagree about it.
    /// </summary>
    [JsonIgnore]
    public bool KeepsReplacedVersions => BackupOverwrittenFiles && RecycleDeletedFiles;

    /// <summary>Loads %LOCALAPPDATA%\FolderSync\config.json. See <see cref="LoadFrom"/>.</summary>
    /// <exception cref="ConfigUnavailableException">The file exists but stayed locked or unreadable.</exception>
    public static SyncConfig Load()
    {
        Directory.CreateDirectory(LogDir);
        return LoadFrom(ConfigPath, AppendEvent);
    }

    /// <summary>
    /// Loads the settings from <paramref name="configPath"/>, path-taking so it can be tested without
    /// touching the real configuration.
    ///
    /// <para>Only a file that genuinely fails to parse counts as damaged. A file that is merely busy -
    /// the window saving it at the moment a scheduled run starts, an antivirus scan, an editor - is
    /// retried, and if it stays busy this throws <see cref="ConfigUnavailableException"/> and leaves the
    /// file alone. Treating "busy" as "damaged" used to set a perfectly good config aside and carry on
    /// with no folder pairs at all.</para>
    /// </summary>
    public static SyncConfig LoadFrom(string configPath, Action<string>? log = null,
                                      int lockedAttempts = 15, int lockedDelayMs = 200)
    {
        log ??= _ => { };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);

        string? text = ReadConfigText(configPath, lockedAttempts, lockedDelayMs);
        if (text is null)
        {
            // First run: nothing saved yet.
            var fresh = Default();
            TrySave(fresh, configPath, log);
            return fresh;
        }

        if (!TryParse(text, out var cfg, out string error))
        {
            // Another process may have replaced the file since it was read - two scheduled runs that
            // start together, one of which has already rescued it. Look once more before deciding.
            string? again = ReadConfigText(configPath, lockedAttempts, lockedDelayMs);
            if (again is null || again == text || !TryParse(again, out cfg, out _))
                return RecoverFromDamage(configPath, error, log);
        }

        Normalise(cfg!, configPath, log);
        return cfg!;
    }

    /// <summary>The file's text, or null when there is no file. Retries while another program has it locked.</summary>
    private static string? ReadConfigText(string path, int attempts, int delayMs)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // Share everything, including delete, so a reader never blocks a writer's atomic replace.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Math.Max(1, attempts)) throw new ConfigUnavailableException(path, ex);
                Thread.Sleep(Math.Max(0, delayMs));
            }
        }
    }

    private static bool TryParse(string text, out SyncConfig? cfg, out string error)
    {
        try
        {
            cfg = JsonSerializer.Deserialize<SyncConfig>(text);
            error = cfg is null ? "the file holds no settings" : "";
            return cfg is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            cfg = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Fills in anything an older version or a hand edit left out, and migrates old layouts. Settings are
    /// returned as they are otherwise - including a blank Source, which is a legitimate state.
    /// </summary>
    private static void Normalise(SyncConfig cfg, string configPath, Action<string> log)
    {
        cfg.Email ??= new EmailSettings();
        cfg.Targets ??= new List<SyncTarget>();
        cfg.Pairs ??= new List<SyncPair>();
        cfg.ExcludeFiles ??= new List<string>();
        cfg.ExcludeFolders ??= new List<string>();

        // A hand-edited "Left": null would otherwise surface as a crash in whichever screen reads it first.
        cfg.Pairs.RemoveAll(p => p is null);
        cfg.Targets.RemoveAll(t => t is null);
        foreach (var p in cfg.Pairs)
        {
            p.Name ??= "";
            p.Left ??= "";
            p.Right ??= "";
        }

        // Destinations saved before per-destination modes existed inherit the old global setting, so
        // an existing mirror setup keeps mirroring.
        foreach (var t in cfg.Targets) t.Mirror ??= cfg.Mirror;

        if (string.IsNullOrWhiteSpace(cfg.UpdateRepo)
            || cfg.UpdateRepo.Equals(LegacyUpdateRepo, StringComparison.OrdinalIgnoreCase))
            cfg.UpdateRepo = DefaultUpdateRepo;
        if (string.IsNullOrWhiteSpace(cfg.RecycleFolderName)) cfg.RecycleFolderName = "_FolderSyncTrash";

        // Outside 1-100 the guard either stops every mirror (0) or can never fire (over 100).
        if (cfg.LargeDeletionPercent is < 1 or > 100)
        {
            log($"[Config] LargeDeletionPercent {cfg.LargeDeletionPercent} is outside 1-100; "
              + $"using {DefaultLargeDeletionPercent}.");
            cfg.LargeDeletionPercent = DefaultLargeDeletionPercent;
        }

        bool changed = cfg.MigrateTargetsToPairs();
        changed |= cfg.DropUnusableCloudAccountPairs();
        // A failed save here is not damage: the migrated settings are in use, and saving is retried later.
        if (changed) TrySave(cfg, configPath, log);
    }

    /// <summary>
    /// The file could not be parsed. Keep a copy of it, then fall back to defaults. If no copy can be
    /// kept, nothing is written at all, because writing would destroy the only copy of the settings.
    /// </summary>
    private static SyncConfig RecoverFromDamage(string configPath, string error, Action<string> log)
    {
        var def = Default();
        string? kept = MoveConfigAside(configPath, DateTime.Now);

        if (kept is null)
        {
            def.LoadProblem = $"config.json could not be read ({error}) and could not be set aside, so it "
                            + "was left untouched. Default settings are in use and have not been saved.";
            log("[Config] " + def.LoadProblem);
            return def;
        }

        def.LoadProblem = $"config.json could not be read ({error}). It was kept as {kept}, and Folder "
                        + "Sync started from default settings.";
        log("[Config] " + def.LoadProblem);
        TrySave(def, configPath, log);
        return def;
    }

    /// <summary>
    /// Preserves a config file that could not be read as "config.bad-&lt;timestamp&gt;.json", so the
    /// defaults written next do not destroy it. Returns the kept path, or null when there was nothing to
    /// keep or no copy could be made.
    ///
    /// <para>Moves the file when it can, and copies it when it cannot (another program holding it open
    /// does not stop a copy). Never overwrites an earlier rescue: a second one in the same second - two
    /// scheduled runs starting together - gets its own "-2" name instead.</para>
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
            string stamp = when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            for (int n = 1; n <= 100; n++)
            {
                string kept = Path.Combine(folder, n == 1 ? $"config.bad-{stamp}.json" : $"config.bad-{stamp}-{n}.json");
                if (File.Exists(kept)) continue;

                try
                {
                    File.Move(configPath, kept, overwrite: false);
                    return kept;
                }
                catch (IOException) when (File.Exists(kept))
                {
                    continue;    // another process claimed this name a moment ago
                }
                catch
                {
                    // Could not be moved, usually because another program has it open. A copy keeps the
                    // contents just as well.
                    try
                    {
                        File.Copy(configPath, kept, overwrite: false);
                        return kept;
                    }
                    catch (IOException) when (File.Exists(kept)) { continue; }
                    catch { return null; }
                }
            }
            return null;
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

    public void Save() => SaveTo(ConfigPath);

    /// <summary>
    /// Writes the settings atomically: to a temporary file first, then swapped into place. Writing the
    /// file in place meant a crash, a full disk or a reader arriving mid-write could see half a file -
    /// which is exactly what then got treated as a damaged config.
    /// </summary>
    public void SaveTo(string configPath)
    {
        string full = Path.GetFullPath(configPath);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);

        // Unique per call, so two processes saving at once never share a temporary file.
        string tmp = Path.Combine(dir, $"{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, full, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(100);   // briefly held by a reader that does not share delete access
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* a stray .tmp is harmless */ }
        }
    }

    /// <summary>Saves, logging rather than throwing. For the load path, which must not fail over a save.</summary>
    private static bool TrySave(SyncConfig cfg, string configPath, Action<string> log)
    {
        try
        {
            cfg.SaveTo(configPath);
            return true;
        }
        catch (Exception ex)
        {
            log($"[Config] could not be saved ({ex.Message}). The settings are in use for now; "
              + "saving is tried again on the next change.");
            return false;
        }
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
