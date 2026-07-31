using System.Reflection;

namespace FolderSync;

/// <summary>Shared assets. The icon is embedded so the window and tray both get the real artwork.</summary>
public static class AppResources
{
    private static Icon? _icon;

    public static Icon AppIcon => _icon ??= Load();

    private static Icon Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FolderSync.ico");
            if (stream is not null) return new Icon(stream);
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    /// <summary>A tray-sized copy (16x16) of the app icon.</summary>
    public static Icon TrayIcon
    {
        get
        {
            try { return new Icon(AppIcon, 16, 16); }
            catch { return AppIcon; }
        }
    }
}
