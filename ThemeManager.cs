using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FolderSync;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Swaps the palette brushes defined in Theme.xaml and keeps the Windows title bar in step, so
/// dark mode doesn't leave a bright caption bar stuck above a dark window.
/// </summary>
public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool IsDark { get; private set; }

    /// <summary>Reads the user's Windows preference (light unless they've chosen dark).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(AppTheme theme)
    {
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };

        var r = Application.Current?.Resources;
        if (r is null) return;

        if (IsDark)
        {
            Set(r, "WindowBg", "#1F1F1F");
            Set(r, "SidebarBg", "#181818");
            Set(r, "CardBg", "#2B2B2B");
            Set(r, "CardBorder", "#3A3A3A");
            Set(r, "ControlBg", "#333333");
            Set(r, "ControlBorder", "#4A4A4A");
            Set(r, "Fg", "#F0F0F0");
            Set(r, "FgMuted", "#A8ADB4");
            Set(r, "HoverBg", "#FFFFFF14");
            Set(r, "Accent", "#4CA0E0");
            Set(r, "AccentHover", "#63AFE6");
            Set(r, "AccentPressed", "#3A8AC8");
            Set(r, "Ok", "#6CCB5F");
            Set(r, "Bad", "#FF6B5E");
            Set(r, "Warn", "#E8B339");
        }
        else
        {
            Set(r, "WindowBg", "#F3F3F3");
            Set(r, "SidebarBg", "#EAEAEA");
            Set(r, "CardBg", "#FFFFFF");
            Set(r, "CardBorder", "#E1E1E1");
            Set(r, "ControlBg", "#FFFFFF");
            Set(r, "ControlBorder", "#D0D0D0");
            Set(r, "Fg", "#1B1B1B");
            Set(r, "FgMuted", "#5F6368");
            Set(r, "HoverBg", "#00000010");
            Set(r, "Accent", "#0F6CBD");
            Set(r, "AccentHover", "#1A7FD4");
            Set(r, "AccentPressed", "#0C5A9E");
            Set(r, "Ok", "#0F7B0F");
            Set(r, "Bad", "#C42B1C");
            Set(r, "Warn", "#9D5D00");
        }

        foreach (Window w in Application.Current.Windows) ApplyWindowChrome(w);
    }

    private static void Set(ResourceDictionary r, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        if (r[key] is SolidColorBrush existing && !existing.IsFrozen)
            existing.Color = color;
        else
            r[key] = new SolidColorBrush(color);
    }

    /// <summary>Gives the window a dark caption bar and Windows 11 rounded corners.</summary>
    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;   // not shown yet; called again on SourceInitialized

            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            int corner = CornerRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch { /* older Windows - the window just keeps its default chrome */ }
    }
}
