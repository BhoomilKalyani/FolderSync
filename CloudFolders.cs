using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace FolderSync;

/// <summary>
/// A cloud service whose desktop app already exposes its files as an ordinary folder on this PC.
/// </summary>
/// <param name="Provider">Display name, e.g. "Google Drive".</param>
/// <param name="Path">The local folder standing in for the cloud storage.</param>
public sealed record CloudFolder(string Provider, string Path);

/// <summary>
/// Finds cloud storage that is already mounted locally - Google Drive for Desktop, OneDrive, iCloud
/// Drive, Dropbox.
///
/// <para>This is the preferred way to reach those services, and the reason is worth recording: the
/// provider's own desktop app has already solved authentication, so Folder Sync just copies files
/// with robocopy. No OAuth client, no API quota, and nothing that can be retired out from under it -
/// unlike the API route, where Google is withdrawing rclone's shared client during 2026. It is also
/// far quicker, since there is no per-file network round trip.</para>
///
/// <para>The alternative - talking to a provider's HTTP API - was tried and removed. It needed an
/// OAuth client that cannot be shipped in a public app without Google's paid verification review,
/// and the shared client it fell back on is being withdrawn during 2026. This route has no such
/// dependency, so it cannot be discontinued.</para>
/// </summary>
public static class CloudFolders
{
    /// <summary>Every mounted cloud folder found on this PC, in a stable order.</summary>
    public static List<CloudFolder> Detect() =>
        Detect(Directory.Exists, ReadyDriveRoots, ReadFileOrNull, ReadWindowsSyncRoots);

    /// <summary>
    /// The detection itself, with every source of information passed in, so it can be tested against
    /// a made-up machine rather than whatever happens to be installed on the one running the tests.
    /// </summary>
    public static List<CloudFolder> Detect(
        Func<string, bool> exists,
        Func<IEnumerable<string>> driveRoots,
        Func<string, string?> readFile,
        Func<IEnumerable<(string Provider, string Path)>> syncRoots)
    {
        var found = new List<CloudFolder>();

        // First and most general: Windows keeps its own register of apps that sync a cloud account
        // into a folder, with the name to show and the folder itself. Reading that is what lets any
        // service the user installs later appear here without this code knowing about it - Dropbox,
        // Box, pCloud and so on all register the same way.
        foreach (var (provider, path) in syncRoots())
            AddIf(found, exists, provider, path);

        // Google Drive for Desktop is the notable exception: it mounts a drive letter holding
        // "My Drive" rather than registering. The letter is configurable, so check every ready drive.
        foreach (var root in driveRoots())
        {
            string candidate = System.IO.Path.Combine(root, "My Drive");
            if (exists(candidate)) Add(found, "Google Drive", candidate);
        }

        // Fallbacks for clients too old to register, or that never did. Anything already found above
        // wins, since the name Windows reports is better than a guess.
        AddIf(found, exists, "OneDrive", Env("OneDrive"));
        AddIf(found, exists, "OneDrive (work or school)", Env("OneDriveCommercial"));
        AddIf(found, exists, "OneDrive", Profile("OneDrive"));
        AddIf(found, exists, "iCloud Drive", Profile("iCloudDrive"));

        foreach (var path in DropboxPaths(readFile))
            AddIf(found, exists, "Dropbox", path);
        AddIf(found, exists, "Dropbox", Profile("Dropbox"));

        return found;
    }

    /// <summary>
    /// Cloud folders as registered with Windows: provider name and folder, from
    /// <c>…\Explorer\SyncRootManager</c>. Machine-wide entries are read before per-user ones.
    /// </summary>
    private static IEnumerable<(string Provider, string Path)> ReadWindowsSyncRoots()
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            RegistryKey? manager = null;
            try { manager = hive.OpenSubKey(keyPath); }
            catch { /* no access, or the key isn't there on this Windows build */ }
            if (manager is null) continue;

            using (manager)
            {
                foreach (var entryName in SubKeyNames(manager))
                {
                    RegistryKey? entry = null;
                    try { entry = manager.OpenSubKey(entryName); }
                    catch { }
                    if (entry is null) continue;

                    using (entry)
                    {
                        string provider = ProviderNameOf(entry, entryName);
                        foreach (var path in SyncRootPaths(entry))
                            yield return (provider, path);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The name to show for a registered provider: its DisplayNameResource when that is real text,
    /// otherwise the leading part of the registry key ("OneDrive!&lt;sid&gt;!Personal" -> "OneDrive").
    /// A resource reference such as "@C:\...\app.dll,-123" is no use on screen, so it is discarded.
    /// </summary>
    private static string ProviderNameOf(RegistryKey entry, string entryName)
    {
        string? display = null;
        try { display = entry.GetValue("DisplayNameResource") as string; }
        catch { }

        if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@'))
            return display.Trim();

        int bang = entryName.IndexOf('!');
        return bang > 0 ? entryName[..bang] : entryName;
    }

    /// <summary>The folders under an entry's UserSyncRoots, one per signed-in user of that provider.</summary>
    private static IEnumerable<string> SyncRootPaths(RegistryKey entry)
    {
        RegistryKey? roots = null;
        try { roots = entry.OpenSubKey("UserSyncRoots"); }
        catch { }
        if (roots is null) yield break;

        using (roots)
        {
            foreach (var name in ValueNames(roots))
            {
                string? path = null;
                try { path = roots.GetValue(name) as string; }
                catch { }
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }
    }

    private static string[] SubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] ValueNames(RegistryKey key)
    {
        try { return key.GetValueNames(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Reads the folders listed in Dropbox's info.json, which looks like
    /// <c>{"personal":{"path":"C:\\Users\\me\\Dropbox"},"business":{"path":"..."}}</c>.
    /// </summary>
    public static List<string> ParseDropboxInfo(string? json)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return paths;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return paths;

            foreach (var account in doc.RootElement.EnumerateObject())
            {
                if (account.Value.ValueKind != JsonValueKind.Object) continue;
                if (account.Value.TryGetProperty("path", out var p)
                    && p.ValueKind == JsonValueKind.String
                    && p.GetString() is { Length: > 0 } path)
                    paths.Add(path);
            }
        }
        catch { /* a malformed file just means no Dropbox found this way */ }

        return paths;
    }

    private static IEnumerable<string> DropboxPaths(Func<string, string?> readFile)
    {
        foreach (var dir in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            string info = System.IO.Path.Combine(Environment.GetFolderPath(dir), "Dropbox", "info.json");
            foreach (var path in ParseDropboxInfo(readFile(info))) yield return path;
        }
    }

    private static void AddIf(List<CloudFolder> found, Func<string, bool> exists, string provider, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && exists(path)) Add(found, provider, path);
    }

    /// <summary>Adds unless the same folder was already found by another probe.</summary>
    private static void Add(List<CloudFolder> found, string provider, string path)
    {
        string tidy = path.TrimEnd('\\', '/');
        if (found.Any(f => string.Equals(f.Path, tidy, StringComparison.OrdinalIgnoreCase))) return;
        found.Add(new CloudFolder(provider, tidy));
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static string Profile(string child) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), child);

    private static IEnumerable<string> ReadyDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? root = null;
            try { if (drive.IsReady) root = drive.RootDirectory.FullName; }
            catch { /* a disconnected drive can throw just from being asked */ }
            if (root is not null) yield return root;
        }
    }

    private static string? ReadFileOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}

/// <summary>One detected cloud folder as shown on the Sync page.</summary>
public sealed class CloudFolderRow
{
    public string Provider { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Whether a pair already covers this folder, so the row can say so.</summary>
    public bool InUse { get; init; }

    public string StatusText => InUse ? "Already set up" : "Not set up yet";

    /// <summary>
    /// The button only appears for a service not yet set up, where it is the quick way in. Once a
    /// pair covers the folder the row is just a statement of fact, and a second pair to the same
    /// service is a rare enough thing to leave to "Add pair" below.
    /// </summary>
    public Visibility ButtonVisibility => InUse ? Visibility.Collapsed : Visibility.Visible;
}
