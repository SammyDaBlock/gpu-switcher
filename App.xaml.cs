using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GpuSwitcher.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GpuSwitcher;

public partial class App : Application
{
    const string InstanceMutex = @"Local\GpuSwitcher.Single";
    const string ShowEvent = @"Local\GpuSwitcher.Show";

    Mutex? _single;
    GpuMonitor? _monitor;
    ForceMode? _force;
    MainWindow? _window;
    Forms.NotifyIcon? _tray;
    bool _toldAboutTray;
    int _ticks;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        if (args.Length > 0 && RunCli(args)) { Shutdown(); return; }

        _single = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            // already running: bring that one up instead
            try { EventWaitHandle.OpenExisting(ShowEvent).Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { Log.Write("UI error: " + ex.Exception); ex.Handled = true; };

        // light/dark: follow Windows (screenshots can force one)
        if (args.Length >= 4 && args[0] == "--screenshot") UI.Theme.Forced = args[3] != "light";
        UI.Theme.Refresh(force: true);

        // anything a previous run (or crash) left disabled comes back first
        Gpus.Refresh();
        int recovered = DeviceGuard.RecoverLeftovers();
        Gpus.Refresh();
        ApplyDefaults();

        _monitor = new GpuMonitor();
        LiveSwitch.Monitor = _monitor;
        _force = new ForceMode();
        _force.Notify += msg => Balloon(msg);
        GpuReturn.Notify += msg => Balloon(msg);
        _force.Start();
        Task.Run(Preferences.SyncAllAppIds);

        _window = new MainWindow { Icon = WindowIcon() };
        _monitor.Updated += snap =>
        {
            var apps = Apps.Build(snap);
            Compat.DetectRunning(apps);
            try
            {
                AutoAssign.CarryOverVersioned(apps);
                AutoAssign.OnApps(apps);
            }
            catch (Exception ex) { Log.Write("auto-assign: " + ex.Message); }
            try { GpuReturn.OnApps(apps); } catch (Exception ex) { Log.Write("gpu return: " + ex.Message); }
            _window.PostData(apps, snap);
            UpdateTrayText(snap);

            if (_ticks % 200 == 199) // ~every 5 min
                try { foreach (var note in StartupManager.Reconcile()) Balloon(note); } catch (Exception ex) { Log.Write("startup apps: " + ex.Message); }

            if (++_ticks % 20 == 1) // ~every 30 s
                foreach (var exe in LiveSwitch.NewSoftwareFallbacks())
                    Balloon($"{Apps.FriendlyName(exe)} is running without GPU acceleration. If its GPU process restarted a lot, close and reopen it to get it back. If hardware acceleration is off in its settings, that's why.");
        };
        _monitor.Start();

        CreateTray();
        ListenForShow();

        bool tray = args.Contains("--tray");
        if (!tray) _window.Show();
        if (recovered > 0) Balloon($"Re-enabled {recovered} GPU(s) that were left off last time.");

        // startup apps: take back any that re-enabled themselves, then (at logon) launch them ourselves
        _ = Task.Run(async () =>
        {
            try
            {
                Autostart.RefreshIfOutdated();
                foreach (var note in StartupManager.Reconcile()) Balloon(note);
                if (tray) await StartupManager.LaunchAtLogon();
            }
            catch (Exception ex) { Log.Write("startup apps: " + ex.Message); }
        });

        if (args.Length >= 2 && args[0] == "--screenshot") _ = Screenshot(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
    }

    void ApplyDefaults()
    {
        var s = AppSettings.Current;
        // Games: the monitors' GPU (desktops plug monitors into the gaming card). Laptops have the screen on the
        // built-in graphics, so if another GPU has clearly more VRAM (or the monitors' GPU barely has any), use that one.
        var display = Gpus.DisplayGpu;
        var biggest = Gpus.All.OrderByDescending(g => g.VramBytes).FirstOrDefault();
        var games = display != null && biggest != null && (display.VramBytes < 2UL << 30 || biggest.VramBytes >= display.VramBytes * 3 / 2)
            ? biggest : display ?? biggest;
        var apps = Gpus.All.FirstOrDefault(g => g.Key != games?.Key);
        bool changed = false;
        if (s.AutoAppsGpuKey == null && apps != null) { s.AutoAppsGpuKey = apps.Key; changed = true; }
        if (s.AutoGamesGpuKey == null && games != null) { s.AutoGamesGpuKey = games.Key; changed = true; }
        if (changed) s.Save();
    }

    // ------------------------------------------------------------------ tray

    void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "GPU Switcher", Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open GPU Switcher", null, (_, _) => ShowWindow());
        menu.Items.Add("Put everything on its GPU", null, async (_, _) => { ShowWindow(); await _window!.FixAll(); });
        foreach (var g in Gpus.All.Where(g => !g.IsDisplayGpu))
            menu.Items.Add($"Move everything off {g.ShortName}…", null, (_, _) => { ShowWindow(); _window!.SelectTab(0); });
        menu.Items.Add("Re-enable all GPUs", null, async (_, _) =>
        {
            var n = await Task.Run(DeviceGuard.EnableAllGpus);
            Balloon(n == 0 ? "All GPUs are already on." : $"Re-enabled {n} GPU(s).");
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        UI.TrayMenuRenderer.Style(menu);
        _tray.ContextMenuStrip = menu;
    }

    void UpdateTrayText(GpuSnapshot snap)
    {
        if (_tray == null) return;
        var text = "GPU Switcher\n" + string.Join("\n", Gpus.All.Select(g => g.IsDisabled ? $"{g.ShortName}: off" : $"{g.ShortName}: {snap.GpuUtil.GetValueOrDefault(g.Key):0}%"));
        Dispatcher.BeginInvoke(() => _tray.Text = text.Length > 127 ? text[..127] : text);
    }

    void Balloon(string msg)
    {
        Log.Write(msg);
        Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(5000, "GPU Switcher", msg, Forms.ToolTipIcon.None));
    }

    public void HiddenToTray()
    {
        if (_toldAboutTray) return;
        _toldAboutTray = true;
        Balloon("Still running in the tray. Right-click the icon to exit.");
    }

    void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void ListenForShow()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        var t = new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true };
        t.Start();
    }

    public static bool Exiting { get; private set; }

    async void ExitApp()
    {
        Exiting = true;
        if (DeviceGuard.IsBusy)
        {
            Balloon("Waiting for the GPU to come back on before exiting…");
            while (DeviceGuard.IsBusy) await Task.Delay(200);
        }
        _monitor?.Dispose();
        _force?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    // ------------------------------------------------------------------ icon (drawn, so no asset files)

    static Drawing.Bitmap IconBitmap(int size)
    {
        var bmp = new Drawing.Bitmap(size, size);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = size / 32f;
        using var body = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x5E, 0xC8, 0xFF));
        using var dark = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0F, 0x11, 0x15));
        using var path = new Drawing.Drawing2D.GraphicsPath();
        var r = new Drawing.RectangleF(2 * s, 7 * s, 28 * s, 18 * s);
        float rad = 4 * s;
        path.AddArc(r.X, r.Y, rad, rad, 180, 90);
        path.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
        path.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
        path.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
        path.CloseFigure();
        g.FillPath(body, path);
        g.FillEllipse(dark, 6 * s, 10 * s, 12 * s, 12 * s);   // fan
        g.FillEllipse(body, 10 * s, 14 * s, 4 * s, 4 * s);
        g.FillRectangle(dark, 21 * s, 11 * s, 5 * s, 3 * s);  // switch arrows
        g.FillRectangle(dark, 21 * s, 18 * s, 5 * s, 3 * s);
        g.FillRectangle(body, 6 * s, 25 * s, 3 * s, 3 * s);   // pcie pins
        g.FillRectangle(body, 11 * s, 25 * s, 12 * s, 3 * s);
        return bmp;
    }

    static Drawing.Icon TrayIcon()
    {
        using var bmp = IconBitmap(32);
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    static ImageSource WindowIcon()
    {
        using var bmp = IconBitmap(64);
        var h = bmp.GetHbitmap();
        var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    // ------------------------------------------------------------------ test hooks

    /// <summary>--screenshot &lt;png&gt; [tab] [dark|light]. tab -1 = tray menu, -2 = a dialog (both captured from the screen).
    /// For tabs, &lt;png&gt; is the rendered window and &lt;png&gt;.screen.png what's really on screen (with Mica).</summary>
    async Task Screenshot(string path, int tab)
    {
        if (tab == -1)
        {
            var menu = _tray!.ContextMenuStrip!;
            var wa = Forms.Screen.PrimaryScreen!.WorkingArea;
            menu.Show(wa.Right - 400, wa.Bottom - 300);
            await Task.Delay(1200);            var r = menu.Bounds;
            r.Inflate(16, 16);
            CaptureScreen(r, path);
            menu.Close();
            ExitApp();
            return;
        }
        if (tab == -2)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var dlg = Windows.OfType<Window>().FirstOrDefault(w => w is not GpuSwitcher.MainWindow && w.IsVisible);
                if (dlg != null) { CaptureScreen(ScreenRect(dlg, 16), path); dlg.Close(); }
            };
            timer.Start();
            var second = Gpus.All.FirstOrDefault(g => !g.IsDisplayGpu)?.ShortName ?? "the second GPU";
            UI.Dialog.Confirm($"Turn {second} off for 5 seconds?",
                $"Using it right now: Discord, Brave.\n\nApps that can handle it move to {Gpus.DisplayGpu?.ShortName}. Others may crash or restart. Your monitors stay on.", "Turn it off", "Cancel");
            ExitApp();
            return;
        }

        _window!.SelectTab(tab);
        await Task.Delay(5000);
        // Mica only shows on the active window; a process started in the background isn't allowed to take focus,
        // a synthetic Alt press lifts that lock
        keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0);
        SetForegroundWindow(new WindowInteropHelper(_window).Handle);
        await Task.Delay(800);
        CaptureScreen(ScreenRect(_window, 0), Path.ChangeExtension(path, ".screen.png"));
        var root = (FrameworkElement)_window.Content;
        var dpi = VisualTreeHelper.GetDpi(_window);
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        ExitApp();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);

    /// <summary>A window's bounds on screen in pixels, plus some margin for the shadow.</summary>
    static Drawing.Rectangle ScreenRect(Window w, int margin)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var content = (FrameworkElement)w.Content;
        var tl = content.PointToScreen(new Point(0, 0));
        var r = new Drawing.Rectangle((int)tl.X, (int)tl.Y, (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY));
        r.Inflate(margin, margin);
        return r;
    }

    static void CaptureScreen(Drawing.Rectangle r, string path)
    {
        using var bmp = new Drawing.Bitmap(r.Width, r.Height);
        using (var g = Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Drawing.Point.Empty, r.Size);
        bmp.Save(path, Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Headless modes: the watchdog, plus test hooks. Returns true if handled.</summary>
    static bool RunCli(string[] args)
    {
        switch (args[0])
        {
            case "--watchdog":
                DeviceGuard.RunWatchdog(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]));
                return true;

            case "--autostart":
                if (args.Length > 1 && args[1] == "off") Core.Autostart.Disable(); else Core.Autostart.Enable();
                return true;

            // --scan <out>: dry run of "assign all installed apps", writes nothing to the registry
            case "--scan":
            {
                Gpus.Refresh();
                var s = AppSettings.Current;
                s.AutoAppsGpuKey ??= Gpus.All.FirstOrDefault(g => !g.IsDisplayGpu)?.Key;
                s.AutoGamesGpuKey ??= Gpus.DisplayGpu?.Key;
                var found = AutoAssign.ScanInstalled();
                File.WriteAllLines(args[1], found.Select(c => $"{(c.IsGame ? "GAME" : "app "),-5} {Gpus.ByKey(c.GpuKey)?.ShortName,-9} {c.Name,-40} {c.Exe}"));
                return true;
            }

            // --startup-list <out>: what the Startup tab would show
            case "--startup-list":
                File.WriteAllLines(args[1], StartupManager.List().Select(i =>
                    $"{(StartupManager.IsManaged(i) ? "MANAGED" : i.EnabledInWindows ? "windows" : "off"),-8} {i.Name,-32} exe={i.ExePath} target={i.TargetExe}"));
                return true;

            // --startup-roundtrip <value name> <out>: take over, launch like at logon, give back
            case "--startup-roundtrip":
            {
                Gpus.Refresh();
                var sb = new StringBuilder();
                var item = StartupManager.List().First(i => i.ValueName == args[1]);
                sb.AppendLine($"before: windows={item.EnabledInWindows}");
                StartupManager.TakeOver(item);
                sb.AppendLine($"taken: windows={StartupManager.List().First(i => i.Id == item.Id).EnabledInWindows} managed={StartupManager.IsManaged(item)}");
                foreach (var r in Task.Run(StartupManager.LaunchAll).GetAwaiter().GetResult()) sb.AppendLine("launch: " + r);
                StartupManager.GiveBack(item);
                sb.AppendLine($"given back: windows={StartupManager.List().First(i => i.Id == item.Id).EnabledInWindows} managed={StartupManager.IsManaged(item)}");
                File.WriteAllText(args[2], sb.ToString());
                return true;
            }

            // --launch-test <exe> <out>: start something the way restarts/startup apps do, report pid
            case "--launch-test":
                File.WriteAllText(args[2], Native.LaunchUnelevated(args[1], null, null).ToString());
                return true;

            // --test-names <out>: how other PCs' GPU names get shortened
            case "--test-names":
            {
                string[][] pcs =
                {
                    new[] { "NVIDIA GeForce RTX 5070", "NVIDIA GeForce RTX 3060" },
                    new[] { "AMD Radeon RX 7900 XTX", "AMD Radeon(TM) Graphics" },
                    new[] { "NVIDIA GeForce RTX 4070 Laptop GPU", "Intel(R) UHD Graphics 770" },
                    new[] { "Intel(R) Arc(TM) A770 Graphics", "NVIDIA GeForce GTX 1660 SUPER", "NVIDIA Quadro RTX 4000" },
                    new[] { "NVIDIA GeForce RTX 3060", "NVIDIA GeForce RTX 3060" },
                };
                var sb = new StringBuilder();
                uint sub = 1;
                foreach (var pc in pcs)
                {
                    var fake = pc.Select(n => new GpuAdapter { Name = n, VendorId = 0x10DE, DeviceId = 1, SubSysId = sub++ }).ToList();
                    sb.AppendLine(string.Join("   |   ", fake.Select(g => $"{g.Name} -> \"{g.ShortName}\" / \"{UI.Look.Label(g, fake)}\"")));
                }
                File.WriteAllText(args[1], sb.ToString());
                return true;
            }

            // --export-icon <out.ico>: writes the drawn icon as a multi-size .ico (used for the exe and the installer)
            case "--export-icon":
            {
                int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
                var pngs = sizes.Select(sz =>
                {
                    using var bmp = IconBitmap(sz);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }).ToList();
                using var fs = File.Create(args[1]);
                using var w = new BinaryWriter(fs);
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); // ICONDIR
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++) // ICONDIRENTRY, PNG-compressed images
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
                return true;
            }

            // emergency: hand every startup app back to Windows
            case "--restore-startup":
                StartupManager.GiveBackAll();
                return true;

            case "--recover":
                Gpus.Refresh();
                DeviceGuard.EnableAllGpus();
                return true;

            case "--list":
            {
                Gpus.Refresh();
                var mon = new GpuMonitor();
                mon.Sample(); Thread.Sleep(1500);
                var snap = mon.Sample();
                var sb = new StringBuilder();
                foreach (var g in Gpus.All)
                    sb.AppendLine($"GPU {g.ShortName} key={g.Key} display={g.IsDisplayGpu} disabled={g.IsDisabled} luids={string.Join(",", g.Luids.Select(l => l.ToString("x")))} util={snap.GpuUtil.GetValueOrDefault(g.Key):0.0}% mem={snap.GpuMem.GetValueOrDefault(g.Key) >> 20}MB");
                foreach (var a in Apps.Build(snap).OrderBy(a => a.PrimaryGpuKey).ThenByDescending(a => a.TotalUtil))
                    sb.AppendLine($"{a.DisplayName,-32} {a.ExeName,-36} on={Gpus.ByKey(a.PrimaryGpuKey)?.ShortName,-9} util={a.TotalUtil,5:0.0}% mem={a.Mem.Values.Sum() >> 20}MB pref={Gpus.ByKey(Preferences.GetGpuKey(a.ExePath))?.ShortName ?? "Auto"} live={Compat.For(a.ExePath).Kind}");
                File.WriteAllText(args[1], sb.ToString());
                return true;
            }

            // --test-disable <gpu key> <seconds> <out>: exercise DeviceGuard end to end
            case "--test-disable":
            {
                Gpus.Refresh();
                var gpu = Gpus.ByKey(args[1])!;
                var sb = new StringBuilder();
                try
                {
                    Task.Run(() => DeviceGuard.RunWithGpuDisabled(gpu, 30, "test", async ct =>
                    {
                        sb.AppendLine($"disabled={Native.IsDeviceDisabled(gpu.PnpId!)} listed={Gpus.ByKey(args[1])?.IsDisabled}");
                        await Task.Delay(int.Parse(args[2]) * 1000, ct);
                    })).GetAwaiter().GetResult();
                }
                catch (Exception ex) { sb.AppendLine("error: " + ex.Message); }
                sb.AppendLine($"after: disabled={Native.IsDeviceDisabled(gpu.PnpId!)}");
                File.WriteAllText(args[3], sb.ToString());
                return true;
            }

            // --test-chromium <exe path> <gpu key> <out>
            case "--test-chromium":
            {
                Gpus.Refresh();
                LiveSwitch.Monitor = new GpuMonitor();
                string r;
                try { r = Task.Run(() => LiveSwitch.MoveChromium(args[1], args[2])).GetAwaiter().GetResult(); }
                catch (Exception ex) { r = "error: " + ex.Message; }
                File.WriteAllText(args[3], r);
                return true;
            }

            // --test-force <game exe name> <out>: run one force-mode cycle as if the game just started
            case "--test-force":
            {
                Gpus.Refresh();
                LiveSwitch.Monitor = new GpuMonitor();
                var fm = new ForceMode();
                var sb = new StringBuilder();
                fm.Notify += m => sb.AppendLine(m);
                var hide = Gpus.All.First(g => !g.IsDisplayGpu);
                var rule = new ForceRule { GameExe = args[1], Triggers = new() { args[1] }, DisableGpuKey = hide.Key, Label = "test", MaxWaitSeconds = 40 };
                Task.Run(() => fm.Run(rule, args[1])).GetAwaiter().GetResult();
                sb.AppendLine($"after: disabled={Native.IsDeviceDisabled(hide.PnpId!)}");
                File.WriteAllText(args[2], sb.ToString());
                return true;
            }
        }
        return false;
    }
}
