using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderSync;

/// <summary>Shared assets. The icon is embedded so the window and tray both use the real artwork.</summary>
public static class AppResources
{
    private static System.Drawing.Icon? _icon;
    private static ImageSource? _image;

    private static Stream? OpenIcon() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("FolderSync.ico");

    /// <summary>Icon for the WinForms tray component.</summary>
    public static System.Drawing.Icon TrayIcon => _icon ??= LoadDrawingIcon();

    private static System.Drawing.Icon LoadDrawingIcon()
    {
        try
        {
            using var stream = OpenIcon();
            if (stream is not null)
            {
                var full = new System.Drawing.Icon(stream);
                try { return new System.Drawing.Icon(full, 16, 16); }
                catch { return full; }
            }
        }
        catch { /* fall through */ }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Icon for WPF windows.</summary>
    public static ImageSource? WindowIcon
    {
        get
        {
            if (_image is not null) return _image;
            try
            {
                using var stream = OpenIcon();
                if (stream is null) return null;
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // Largest frame scales down cleanly for the title bar and Alt-Tab.
                _image = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            }
            catch { _image = null; }
            return _image;
        }
    }
}
