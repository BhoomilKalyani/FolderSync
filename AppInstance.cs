using System.Security.Cryptography;
using System.Text;

namespace FolderSync;

/// <summary>
/// Which copy of the app's data this process works on.
///
/// <para>Normally there is one: <c>%LOCALAPPDATA%\FolderSync</c>. Setting the
/// <c>FOLDERSYNC_DATA_DIR</c> environment variable points the app at another folder for its settings,
/// logs and history, and gives it its own single-window and one-sync-at-a-time locks. That lets a
/// development or test copy run next to the installed one without reading its settings, writing into
/// its history, or blocking its scheduled syncs.</para>
///
/// The scheduled tasks are the one thing that cannot be separated - Windows has one set per user - so
/// a copy with its own data folder leaves them alone (see <see cref="IsSeparate"/>).
/// </summary>
public static class AppInstance
{
    public const string DataDirVariable = "FOLDERSYNC_DATA_DIR";

    /// <summary>The folder named by the environment variable, or null when it is not set.</summary>
    public static string? DataDirOverride { get; } = ReadOverride();

    private static string? ReadOverride()
    {
        try
        {
            string? value = Environment.GetEnvironmentVariable(DataDirVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
        }
        catch { return null; }
    }

    /// <summary>True for a copy running on its own data folder rather than the user's real one.</summary>
    public static bool IsSeparate => DataDirOverride is not null;

    public static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSync");

    /// <summary>Empty for the normal copy; ".1a2b3c4d" for one with its own data folder.</summary>
    public static string Suffix { get; } = DataDirOverride is null ? "" : "." + ShortHash(DataDirOverride);

    /// <summary>Held by the window, so a second copy can tell one is already open.</summary>
    public static string WindowMutexName => @"Local\FolderSync.Window" + Suffix;

    /// <summary>Signalled by a second copy to ask the first to show its window.</summary>
    public static string ShowEventName => @"Local\FolderSync.Show" + Suffix;

    /// <summary>Held for the length of a sync, by whichever process is running it.</summary>
    public static string SyncLockName => SyncLock.DefaultName + Suffix;

    /// <summary>The name of the "start when I sign in" entry in the registry.</summary>
    public static string StartupValueName => "FolderSync" + Suffix;

    private static string ShortHash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
