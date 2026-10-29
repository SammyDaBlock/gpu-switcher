using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GpuSwitcher.Core;

namespace GpuSwitcher.UI;

/// <summary>
/// The performance overlay and the money covers: topmost, click-through windows drawn over games. Nothing is injected
/// into the game. They work over borderless / windowed games; exclusive fullscreen bypasses the desktop, so nothing
/// can draw over it. Global hotkeys work from inside games.
/// </summary>
public static class Overlays
{
    public const string PerfKeys = "Ctrl+Shift+F10", RevealKeys = "Ctrl+Shift+F11", EditKeys = "Ctrl+Shift+F12";
    const int HkPerf = 1, HkReveal = 2, HkEdit = 3;

    /// <summary>Something to tell the user (tray balloon).</summary>
    public static event Action<string>? Notify;
    /// <summary>Overlay settings changed from a hotkey or the cover editor, so the Overlays tab can refresh.</summary>
    public static event Action? Changed;

    static PerfOverlay? _perf;
    static CoverOverlay? _covers;
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    static HwndSource? _hotkeyWindow;
    static List<RunningApp> _apps = new();
    static readonly int OwnPid = Environment.ProcessId;
    static readonly Dictionary<int, string> ExeCache = new();

    /// <summary>The last window in front that isn't one of ours (usually the game).</summary>
    public static IntPtr TargetHwnd { get; private set; }
    public static int TargetPid { get; private set; }
    public static string TargetExe { get; private set; } = "";

    public static OverlaySettings S => AppSettings.Current.Overlay;

    public static void Start()
    {
        RegisterHotkeys();
        Timer.Tick += (_, _) => Tick();
        Apply();
    }

    /// <summary>Called from the monitor thread with every snapshot (to know which GPU the game is on).</summary>
    public static void OnApps(List<RunningApp> apps) => _apps = apps;
    public static RunningApp? AppFor(int pid) => _apps.FirstOrDefault(a => a.Pids.Contains(pid));

    /// <summary>Bring the windows in line with the settings. Call after changing any overlay setting.</summary>
    public static void Apply()
    {
        if (S.PerfEnabled)
        {
            _perf ??= new PerfOverlay();
            _perf.Restyle();
            _perf.ShowOverlay();
            Sensors.Start();
            if (S.PerfItems.Overlaps(new[] { "fps", "frametime", "low" })) FrameCounter.Start(); else FrameCounter.Stop();
        }
        else
        {
            _perf?.HideOverlay();
            Sensors.Stop();
            FrameCounter.Stop();
        }
        if (S.Covers.Count > 0 || _covers?.Editing == true) _covers ??= new CoverOverlay();
        _covers?.Restyle();

        bool needed = S.PerfEnabled || S.Covers.Count > 0 || _covers?.Editing == true;
        if (needed && !Timer.IsEnabled) Timer.Start();
        if (!needed) Timer.Stop();
        Tick();
    }

    public static void Shutdown()
    {
        Timer.Stop();
        FrameCounter.Stop();
        Sensors.Stop();
        if (_hotkeyWindow != null) foreach (var id in new[] { HkPerf, HkReveal, HkEdit }) UnregisterHotKey(_hotkeyWindow.Handle, id);
    }

    static void Tick()
    {
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out int pid);
        bool ours = pid == OwnPid;
        if (fg != IntPtr.Zero && !ours)
        {
            TargetHwnd = fg;
            TargetPid = pid;
            TargetExe = ExeOf(pid);
        }
        _perf?.Tick();
        // covers only show while their game is really in front, not while our own window is
        _covers?.Tick(ours ? IntPtr.Zero : fg, ours ? "" : TargetExe);
    }

    static string ExeOf(int pid)
    {
        if (ExeCache.TryGetValue(pid, out var exe)) return exe;
        if (ExeCache.Count > 200) ExeCache.Clear();
        return ExeCache[pid] = Path.GetFileName(Native.GetProcessPath(pid) ?? "");
    }

    // ------------------------------------------------------------------ hotkeys

    static void RegisterHotkeys()
    {
        // message-only window, it just receives WM_HOTKEY
        _hotkeyWindow = new HwndSource(new HwndSourceParameters("GpuSwitcherHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _hotkeyWindow.AddHook(HotkeyHook);
        const uint ctrlShift = 0x0002 | 0x0004 | 0x4000; // MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT
        var failed = new List<string>();
        if (!RegisterHotKey(_hotkeyWindow.Handle, HkPerf, ctrlShift, 0x79)) failed.Add(PerfKeys);
        if (!RegisterHotKey(_hotkeyWindow.Handle, HkReveal, ctrlShift, 0x7A)) failed.Add(RevealKeys);
        if (!RegisterHotKey(_hotkeyWindow.Handle, HkEdit, ctrlShift, 0x7B)) failed.Add(EditKeys);
        if (failed.Count > 0) Log.Write($"overlay hotkeys already used by another app: {string.Join(", ", failed)}");
    }

    static IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero; // WM_HOTKEY
        handled = true;
        switch ((int)wParam)
        {
            case HkPerf: TogglePerf(); break;
            case HkReveal: ToggleReveal(); break;
            case HkEdit: ToggleEdit(); break;
        }
        return IntPtr.Zero;
    }

    public static void TogglePerf()
    {
        S.PerfEnabled = !S.PerfEnabled;
        AppSettings.Current.Save();
        Apply();
        Changed?.Invoke();
    }

    public static void ToggleReveal()
    {
        S.CoversRevealed = !S.CoversRevealed;
        AppSettings.Current.Save();
        Apply();
        Changed?.Invoke();
    }

    public static bool PerfMoving => _perf?.Moving == true;

    /// <summary>Drag mode for the performance overlay (turns it on if it's off).</summary>
    public static void TogglePerfMove()
    {
        if (_perf?.Moving == true) { _perf.EndMove(); return; }
        if (!S.PerfEnabled) { S.PerfEnabled = true; AppSettings.Current.Save(); Apply(); }
        _perf!.BeginMove();
        Changed?.Invoke();
    }

    internal static void PerfMoved()
    {
        AppSettings.Current.Save();
        Apply();
        Changed?.Invoke();
    }

    static void ToggleEdit()
    {
        if (_covers?.Editing == true) { _covers.EndEdit(); return; }
        Tick();
        GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
        if (pid == OwnPid || TargetHwnd == IntPtr.Zero || string.IsNullOrEmpty(TargetExe))
        {
            Notify?.Invoke($"Click into the game first, then press {EditKeys} to place money covers.");
            return;
        }
        _covers ??= new CoverOverlay();
        _covers.BeginEdit(TargetHwnd, TargetExe);
        Apply();
    }

    internal static void CoversEdited()
    {
        AppSettings.Current.Save();
        Apply();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Win32 shared by the overlay windows

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOACTIVATE = 0x10, SWP_NOSIZE = 0x1;

    /// <summary>Click-through windows let the mouse fall through to the game and never take focus.</summary>
    internal static void SetClickThrough(Window w, bool on)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;
        ex = on ? ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Move/size in screen pixels and stay above the game (games sometimes push themselves up).</summary>
    internal static void Place(Window w, int x, int y, int width, int height)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
    }

    internal static void Move(Window w, int x, int y)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
    }

    internal record struct PxRect(int X, int Y, int W, int H);

    /// <summary>The monitor a window is on, in pixels, plus its device name.</summary>
    internal static (PxRect Bounds, string Device) MonitorOf(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, hwnd == IntPtr.Zero ? 1u : 2u); // PRIMARY : NEAREST
        var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(mon, ref mi)) return (new PxRect(0, 0, 1920, 1080), "");
        var r = mi.Monitor;
        return (new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), mi.Device);
    }

    /// <summary>Every display, in pixels, main one first.</summary>
    internal static List<(PxRect Bounds, string Device, bool Primary)> Monitors()
    {
        var list = new List<(PxRect, string, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (mon, _, _, _) =>
        {
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                var r = mi.Monitor;
                list.Add((new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), mi.Device, (mi.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Item3).ThenBy(m => m.Item2).ToList();
    }

    /// <summary>"\\.\DISPLAY2" -> "Display 2".</summary>
    internal static string MonitorName(string device) => device.Replace(@"\\.\DISPLAY", "Display ");

    /// <summary>A window's outer rectangle in screen pixels.</summary>
    internal static PxRect WindowRect(Window w)
    {
        GetWindowRect(new WindowInteropHelper(w).Handle, out var r);
        return new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>"2560×1440 @ 165 Hz" for a monitor.</summary>
    internal static string DisplayMode(string device)
    {
        var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(device, -1, ref dm) ? $"{dm.PelsWidth}×{dm.PelsHeight} @ {dm.DisplayFrequency} Hz" : "";
    }

    /// <summary>A window's client area in screen pixels. Null if it's gone or minimized.</summary>
    internal static PxRect? ClientRect(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd) || !GetClientRect(hwnd, out var r)) return null;
        var p = new PointI();
        if (!ClientToScreen(hwnd, ref p)) return null;
        return r.Right <= 0 || r.Bottom <= 0 ? null : new PxRect(p.X, p.Y, r.Right, r.Bottom);
    }

    internal static void Focus(IntPtr hwnd) => SetForegroundWindow(hwnd);

    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public RectI Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfoEx mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")] static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);
    delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RectI r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RectI r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref PointI p);
}
