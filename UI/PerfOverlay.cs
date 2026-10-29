using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GpuSwitcher.Core;

namespace GpuSwitcher.UI;

/// <summary>FPS / GPU / CPU numbers and the spec sheet in a corner of the screen the game is on. Always dark, it sits on top of games.</summary>
sealed class PerfOverlay : Window
{
    static readonly Brush White = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    static readonly Brush Label = Frozen(0xB3, 0xFF, 0xFF, 0xFF);
    // GPU colors from the dark theme, whatever the app theme is
    static readonly ResourceDictionary DarkColors = new() { Source = new Uri("Themes/Dark.xaml", UriKind.Relative) };

    readonly Border _panel = new() { CornerRadius = new CornerRadius(8) };
    // drawn on top while dragging, so it doesn't change the size
    readonly Border _outline = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Grid _grid = new();
    readonly StackPanel _specs = new();
    readonly List<(TextBlock L, TextBlock V)> _rows = new();
    int _ticks;
    bool _showing;
    string _device = "";

    public bool Moving { get; private set; }

    public PerfOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = Top = -10000; // placed in pixels on the first tick
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular); // digits don't wiggle
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel();
        stack.Children.Add(_grid);
        stack.Children.Add(_specs);
        _panel.Child = stack;
        var root = new Grid();
        root.Children.Add(_panel);
        root.Children.Add(_outline);
        Content = root;
        SourceInitialized += (_, _) => Overlays.SetClickThrough(this, true);
        MouseLeftButtonDown += (_, e) =>
        {
            if (!Moving) return;
            if (e.ClickCount == 2) { EndMove(); return; }
            DragMove(); // returns when the mouse is let go
            Dropped();
        };
        MouseRightButtonUp += (_, _) => EndMove();
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    static Brush GpuColor(string key) => Look.Role(key) is var i and >= 0 ? (Brush)DarkColors[$"Gpu{i % 4}"] : White;

    double FontPx => Overlays.S.PerfSize switch { 0 => 11, 2 => 16, _ => 13 };

    public void Restyle()
    {
        var s = Overlays.S;
        // fully clear pixels let clicks through, so while dragging "none" gets an invisible tint to grab
        _panel.Background = s.PerfBackground switch { 0 => Moving ? Frozen(0x01, 0, 0, 0) : Brushes.Transparent, 2 => Frozen(0xE0, 0x16, 0x16, 0x16), _ => Frozen(0x80, 0, 0, 0) };
        _panel.Padding = new Thickness(FontPx * 0.8, FontPx * 0.5, FontPx * 0.9, FontPx * 0.55);
        // no background: a soft shadow keeps the text readable on bright scenes
        _panel.Effect = s.PerfBackground == 0 ? new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 1, Color = Colors.Black } : null;
        MinWidth = 0; // let it shrink to the new content, it only grows from here
        Render(grow: false); // ActualWidth is still the old size until the next layout pass
    }

    public void ShowOverlay()
    {
        if (_showing) return;
        _showing = true;
        _panel.Opacity = 0;
        Show();
        Tick();
        Anim.Fade(_panel, 1, 200);
    }

    public void HideOverlay()
    {
        if (!_showing) return;
        _showing = false;
        if (Moving) StopMoving();
        Anim.Fade(_panel, 0, 150, () => { if (!_showing) Hide(); });
    }

    public void Tick()
    {
        if (!_showing) return;
        if (_ticks++ % 3 == 0) Render(); // numbers every ~0.6 s, position every tick
        var s = Overlays.S;
        var (mon, device) = Overlays.MonitorOf(Overlays.TargetHwnd);
        if (s.PerfMonitor != null) // locked to one display (if it's still plugged in)
            foreach (var m in Overlays.Monitors())
                if (m.Device == s.PerfMonitor) { mon = m.Bounds; device = m.Device; }
        _device = device;
        if (Moving) return; // the user is placing it
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int mx = (int)(s.PerfOffsetX * dpi.DpiScaleX), my = (int)(s.PerfOffsetY * dpi.DpiScaleY);
        int x = s.PerfCorner.EndsWith("Right") ? mon.X + mon.W - w - mx : mon.X + mx;
        int y = s.PerfCorner.StartsWith("Bottom") ? mon.Y + mon.H - h - my : mon.Y + my;
        // keep it on screen if it got bigger than the room left
        x = Math.Clamp(x, mon.X, Math.Max(mon.X, mon.X + mon.W - w));
        y = Math.Clamp(y, mon.Y, Math.Max(mon.Y, mon.Y + mon.H - h));
        Overlays.Move(this, x, y);
    }

    // ------------------------------------------------------------------ placing it by hand

    public void BeginMove()
    {
        if (Moving) return;
        Moving = true;
        _outline.BorderBrush = (Brush)DarkColors["Gpu0"];
        _outline.Visibility = Visibility.Visible;
        _panel.Cursor = Cursors.SizeAll;
        Restyle();
        Overlays.SetClickThrough(this, false);
    }

    public void EndMove()
    {
        if (!Moving) return;
        StopMoving();
        Overlays.PerfMoved();
    }

    void StopMoving()
    {
        Moving = false;
        _outline.Visibility = Visibility.Collapsed;
        _panel.Cursor = null;
        Overlays.SetClickThrough(this, true);
    }

    /// <summary>Remember where it was dropped: the nearest corner of that screen plus the distance from it, so it grows away from the edge.</summary>
    void Dropped()
    {
        var r = Overlays.WindowRect(this);
        var (mon, device) = Overlays.MonitorOf(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        bool right = r.X + r.W / 2 > mon.X + mon.W / 2, bottom = r.Y + r.H / 2 > mon.Y + mon.H / 2;
        var s = Overlays.S;
        s.PerfCorner = (bottom ? "Bottom" : "Top") + (right ? "Right" : "Left");
        s.PerfOffsetX = Math.Round(Math.Max(0, right ? mon.X + mon.W - r.X - r.W : r.X - mon.X) / dpi.DpiScaleX);
        s.PerfOffsetY = Math.Round(Math.Max(0, bottom ? mon.Y + mon.H - r.Y - r.H : r.Y - mon.Y) / dpi.DpiScaleY);
        if (s.PerfMonitor != null) s.PerfMonitor = device; // dragged onto another locked screen
        AppSettings.Current.Save();
    }

    // ------------------------------------------------------------------ content

    record Row(string Label, Brush LabelBrush, string Value, Brush? ValueBrush = null);

    void Render(bool grow = true)
    {
        var s = Overlays.S;
        var on = s.PerfItems;
        var rows = new List<Row>();

        if (on.Contains("fps") || on.Contains("frametime") || on.Contains("low"))
        {
            var f = FrameCounter.For(Overlays.TargetPid);
            var parts = new List<string>();
            if (on.Contains("fps")) parts.Add(f == null ? "-" : $"{f.Fps:0}");
            if (on.Contains("frametime")) parts.Add(f == null ? "- ms" : $"{f.FrameMs:0.0} ms");
            if (on.Contains("low")) parts.Add(f == null ? "1% -" : $"1% {f.Low1Fps:0}");
            rows.Add(new Row(on.Contains("fps") ? "FPS" : "FRAMES", Label, string.Join("   ", parts)));
        }
        if (on.Contains("app") && Overlays.AppFor(Overlays.TargetPid) is { } app)
            rows.Add(new Row(Trim(app.DisplayName, 18), White, app.PrimaryGpuKey != null ? "on " + Look.Label(app.PrimaryGpuKey) : "", GpuColor(app.PrimaryGpuKey ?? "")));

        var snap = LiveSwitch.Monitor?.Latest;
        bool anyGpu = on.Overlaps(new[] { "gpu-usage", "gpu-temp", "gpu-clock", "gpu-power", "gpu-fan", "gpu-vram" });
        if (anyGpu)
            foreach (var g in Gpus.All.Where(g => s.PerfGpuKey == null || g.Key == s.PerfGpuKey))
            {
                if (g.IsDisabled) { rows.Add(new Row(Look.Label(g), GpuColor(g.Key), "off")); continue; }
                Sensors.Gpu.TryGetValue(g.Key, out var sen);
                var parts = new List<string>();
                if (on.Contains("gpu-usage")) parts.Add($"{sen?.Util ?? snap?.GpuUtil.GetValueOrDefault(g.Key) ?? 0,3:0}%");
                if (on.Contains("gpu-temp") && sen?.TempC is int t) parts.Add($"{t}°C");
                if (on.Contains("gpu-clock") && sen?.ClockMhz is int c) parts.Add($"{c,4} MHz");
                if (on.Contains("gpu-power") && sen?.PowerW is double p) parts.Add($"{p,3:0} W");
                if (on.Contains("gpu-fan") && sen?.FanPct is int fan) parts.Add($"fan {fan}%");
                if (on.Contains("gpu-vram")) parts.Add($"{Gb(snap?.GpuMem.GetValueOrDefault(g.Key) ?? 0)}/{Gb((long)g.VramBytes, 0)} GB");
                rows.Add(new Row(Look.Label(g), GpuColor(g.Key), string.Join("   ", parts)));
            }
        if (on.Contains("cpu")) rows.Add(new Row("CPU", Label, $"{Sensors.CpuPercent,3:0}%"));
        if (on.Contains("ram") && Sensors.RamTotal > 0) rows.Add(new Row("RAM", Label, $"{Gb((long)Sensors.RamUsed)}/{Gb((long)Sensors.RamTotal, 0)} GB"));
        if (on.Contains("clock")) rows.Add(new Row("TIME", Label, DateTime.Now.ToString("HH:mm")));

        SetRows(rows);
        SetSpecs(on);

        // only grow: shrinking every time a number gets shorter would make a right-side overlay jump around
        if (grow && IsLoaded) MinWidth = Math.Max(MinWidth, ActualWidth);
    }

    static string Gb(long bytes, int decimals = 1) => (bytes / (double)(1L << 30)).ToString(decimals == 0 ? "0" : "0.0");
    static string Trim(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    void SetRows(List<Row> rows)
    {
        double size = FontPx;
        while (_rows.Count < rows.Count)
        {
            var l = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, size, 1) };
            var v = new TextBlock { Margin = new Thickness(0, 1, 0, 1) };
            Grid.SetRow(l, _rows.Count); Grid.SetRow(v, _rows.Count); Grid.SetColumn(v, 1);
            _grid.RowDefinitions.Add(new RowDefinition());
            _grid.Children.Add(l); _grid.Children.Add(v);
            _rows.Add((l, v));
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            var (l, v) = _rows[i];
            bool used = i < rows.Count;
            l.Visibility = v.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
            if (!used) continue;
            l.Text = rows[i].Label; l.Foreground = rows[i].LabelBrush; l.FontSize = size; l.Margin = new Thickness(0, 1, size, 1);
            v.Text = rows[i].Value; v.Foreground = rows[i].ValueBrush ?? White; v.FontSize = size;
        }
    }

    void SetSpecs(HashSet<string> on)
    {
        var lines = new List<string>();
        var sp = SpecSheet.Current;
        if (on.Contains("spec-cpu") && sp != null) lines.Add(sp.Cpu);
        if (on.Contains("spec-gpu")) lines.Add(string.Join("  ·  ", Gpus.All.Select(g => $"{g.ShortName} {Math.Round(g.VramBytes / (double)(1UL << 30))} GB")));
        if (on.Contains("spec-ram") && sp != null) lines.Add(sp.Ram);
        if (on.Contains("spec-board") && sp != null) lines.Add(sp.Board);
        if (on.Contains("spec-os") && sp != null) lines.Add(sp.Os);
        if (on.Contains("spec-display") && _device.Length > 0) lines.Add(Overlays.DisplayMode(_device));

        double size = Math.Max(11, FontPx * 0.95); // small text over a busy game is hard to read, so barely smaller than the numbers
        _specs.Margin = new Thickness(0, lines.Count > 0 && _rows.Any(r => r.L.Visibility == Visibility.Visible) ? size * 0.6 : 0, 0, 0);
        while (_specs.Children.Count < lines.Count) _specs.Children.Add(new TextBlock { Foreground = Label, Margin = new Thickness(0, 1, 0, 1) });
        for (int i = 0; i < _specs.Children.Count; i++)
        {
            var t = (TextBlock)_specs.Children[i];
            t.Visibility = i < lines.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < lines.Count) { t.Text = lines[i]; t.FontSize = size; }
        }
    }
}
