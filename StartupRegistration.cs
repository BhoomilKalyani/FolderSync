using Microsoft.Win32;

namespace FolderSync;

/// <summary>
/// "Start Folder Sync when I sign in": a per-user entry under the registry's Run key that starts the
/// app in the tray, with no window.
///
/// <para>The scheduled tasks do not need this - they start a headless copy that syncs and exits. It is
/// for the things only a running app can do: watching folders for changes, and showing notifications
/// that stay in the notification list.</para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Starts the app hidden, with only its tray icon.</summary>
    public const string TraySwitch = "--tray";

    /// <summary>The command Windows runs at sign-in. The path is quoted: install paths contain spaces.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" {TraySwitch}";

    /// <summary>The registered command, or null when the app does not start at sign-in.</summary>
    public static string? CurrentCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppInstance.StartupValueName) as string is { Length: > 0 } command ? command : null;
        }
        catch { return null; }
    }

    public static bool IsEnabled() => CurrentCommand() is not null;

    public static (bool ok, string msg) Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return (false, "The registry key could not be opened.");

            if (enabled) key.SetValue(AppInstance.StartupValueName, CommandFor(ScheduleManager.ExePath), RegistryValueKind.String);
            else key.DeleteValue(AppInstance.StartupValueName, throwOnMissingValue: false);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Points an existing entry at the copy running now. Run by Setup, in case the program has moved;
    /// does nothing when the app is not set to start at sign-in.
    /// </summary>
    public static void Repair()
    {
        string? current = CurrentCommand();
        if (current is null) return;

        string wanted = CommandFor(ScheduleManager.ExePath);
        if (!current.Equals(wanted, StringComparison.OrdinalIgnoreCase)) Set(true);
    }

    /// <summary>Removes the entry. Run by the uninstaller so nothing is left pointing at a deleted program.</summary>
    public static void Remove() => Set(false);
}
