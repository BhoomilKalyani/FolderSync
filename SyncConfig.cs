using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSync;

/// <summary>One source-to-destination copy job.</summary>
public sealed class SyncTarget
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Overrides the global mode for this destination: true = mirror, false = additive,
    /// null = follow <see cref="SyncConfig.Mirror"/>. Lets you mirror to a cloud folder while
    /// a backup drive keeps everything (never deletes).
    /// </summary>
    public bool? Mirror { get; set; }

    /// <summary>The mode actually used, resolving the override against the global setting.</summary>
    public bool MirrorEffective(bool globalMirror) => Mirror ?? globalMirror;

    /// <summary>How the mode shows in the UI: "Mirror", "Additive", or "(default)".</summary>
    [JsonIgnore]
    public string ModeText => Mirror is null ? "(default)" : Mirror.Value ? "Mirror" : "Additive";
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
    public string Source { get; set; } = "";
    public List<SyncTarget> Targets { get; set; } = new();

    /// <summary>Mirror = destination becomes an exact copy (deletes extras). False = additive only.</summary>
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

    /// <summary>Check GitHub for a newer release when the app starts (at most once a day).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>"owner/repo" that publishes releases. Change only to test against another repo.</summary>
    public string UpdateRepo { get; set; } = DefaultUpdateRepo;

    public bool IncludePrereleaseUpdates { get; set; }

    [JsonIgnore]
    public const string DefaultUpdateRepo = "Bhoomil1991/FolderSync";

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
                    cfg.ExcludeFiles ??= new List<string>();
                    cfg.ExcludeFolders ??= new List<string>();
                    if (string.IsNullOrWhiteSpace(cfg.UpdateRepo)) cfg.UpdateRepo = DefaultUpdateRepo;
                    if (string.IsNullOrWhiteSpace(cfg.RecycleFolderName)) cfg.RecycleFolderName = "_FolderSyncTrash";
                    return cfg;
                }
            }
            catch
            {
                // Fall through to defaults if the file is missing or corrupt.
            }
        }

        var def = Default();
        def.Save();
        return def;
    }

    /// <summary>True on a first run: nothing has been configured yet.</summary>
    [JsonIgnore]
    public bool IsUnconfigured =>
        string.IsNullOrWhiteSpace(Source) || Targets is null || Targets.Count == 0;

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
