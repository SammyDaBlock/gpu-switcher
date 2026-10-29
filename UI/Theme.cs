using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GpuSwitcher.UI;

/// <summary>Follows Windows' app light/dark setting and does the Windows 11 window chrome (Mica, dark title bar, round corners).</summary>
public static class Theme
{
    public static bool IsDark { get; private set; } = true;
    /// <summary>Set by --screenshot to render a theme without changing Windows.</summary>
    public static bool? Forced { get; set; }
    public static event Action? Changed;

    /// <summary>Mica needs Windows 11 22H2.</summary>
    public static bool MicaSupported => Environment.OSVersion.Version.Build >= 22621;

    static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch { return true; }
    }

    /// <summary>Loads the right colors. Call at startup and whenever Windows might have switched.</summary>
    public static void Refresh(bool force = false)
    {
        bool dark = Forced ?? SystemIsDark();
        if (!force && dark == IsDark) return;
        IsDark = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var colors = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        if (dicts.Count > 0) dicts[0] = colors; else dicts.Add(colors);
        Changed?.Invoke();
    }

    /// <summary>WM_SETTINGCHANGE with "ImmersiveColorSet" = light/dark was switched.</summary>
    public static bool IsThemeMessage(int msg, IntPtr lParam) =>
        msg == 0x001A && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet";

    // ------------------------------------------------------------------ window chrome

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    /// <summary>Mica behind the whole window. Falls back to the solid Bg color on older Windows.</summary>
    public static void ApplyMica(Window w)
    {
        ApplyTitleBar(w);
        var hwnd = new WindowInteropHelper(w).Handle;
        if (!MicaSupported || hwnd == IntPtr.Zero) { w.SetResourceReference(Window.BackgroundProperty, "Bg"); return; }
        w.Background = Brushes.Transparent;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Transparent;
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        int mica = 2; // DWMSBT_MAINWINDOW
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));
        if (hr != 0)
        {
            Core.Log.Write($"Mica not available (0x{hr:X8}), using a solid background");
            w.SetResourceReference(Window.BackgroundProperty, "Bg");
        }
    }

    /// <summary>Windows 11 round corners (and its border/shadow) for borderless windows and menus.</summary>
    public static void RoundCorners(IntPtr hwnd, bool small = false)
    {
        if (hwnd == IntPtr.Zero) return;
        int pref = small ? 3 : 2; // DWMWCP_ROUNDSMALL : DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }
}
