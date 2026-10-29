using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GpuSwitcher.Core;

namespace GpuSwitcher.UI;

/// <summary>
/// Opaque blocks over the spots where a game shows your money, so you can grind and get surprised at the end.
/// Covers belong to one game (exe) and only show while it's in front. Normally click-through; in edit mode
/// it covers the whole game window: drag to draw, drag a cover to move it, its corner to resize, × or right-click to delete.
/// </summary>
sealed class CoverOverlay : Window
{
    readonly Canvas _canvas = new();
    readonly Border _toolbar;
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    IntPtr _game;
    string _exe = "";
    Overlays.PxRect _client, _placed;
    bool _shown;
    string _layoutSig = "";

    public bool Editing { get; private set; }

    public CoverOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Left = Top = -10000;
        Width = Height = 1;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _toolbar = BuildToolbar();
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_toolbar);
        Content = root;

        SourceInitialized += (_, _) => Overlays.SetClickThrough(this, true);
        _canvas.MouseLeftButtonDown += Canvas_Down;
        _canvas.MouseMove += Canvas_Move;
        _canvas.MouseLeftButtonUp += Canvas_Up;
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) EndEdit(); };
    }

    List<CoverRect>? Covers => AppSettings.Current.Overlay.Covers.TryGetValue(_exe, out var l) ? l : null;

    static Brush CoverBrush => AppSettings.Current.Overlay.CoverColor switch
    {
        "Gray" => new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        "Blue" => new SolidColorBrush(Color.FromRgb(0x1E, 0x5F, 0x8C)),
        "Pink" => new SolidColorBrush(Color.FromRgb(0xC2, 0x4E, 0x8E)),
        _ => new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)),
    };

    public void Restyle() => _layoutSig = ""; // rebuilt on the next tick

    // ------------------------------------------------------------------ following the game

    /// <summary>fg = the window in front right now (zero if it's one of ours).</summary>
    public void Tick(IntPtr fg, string fgExe)
    {
        var s = AppSettings.Current.Overlay;
        if (!Editing)
        {
            if (fg != IntPtr.Zero && fg != _game && s.Covers.ContainsKey(fgExe)) { _game = fg; _exe = fgExe; _layoutSig = ""; }
            bool want = !s.CoversRevealed && fg != IntPtr.Zero && fg == _game && Covers is { Count: > 0 };
            var rect = want ? Overlays.ClientRect(_game) : null;
            if (rect == null) { SetShown(false); return; }
            _client = rect.Value;
        }
        else
        {
            var rect = Overlays.ClientRect(_game);
            if (rect == null) { EndEdit(); return; } // game closed or minimized
            _client = rect.Value;
        }
        Layout();
        SetShown(true);
    }

    void SetShown(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        if (show)
        {
            _canvas.Opacity = 0;
            Show();
            Anim.Fade(_canvas, 1, 160, scaleTo: 1);
        }
        else if (AppSettings.Current.Overlay.CoversRevealed && IsVisible)
        {
            // the reveal: covers shrink away
            Anim.Fade(_canvas, 0, 450, () => { if (!_shown) Hide(); }, scaleTo: 0.85);
        }
        else Hide(); // alt-tab: gone right away, no fade over the next window
    }

    /// <summary>Window = the game's client area while editing, otherwise just the box around the covers (less to redraw).</summary>
    void Layout()
    {
        var covers = Covers ?? new List<CoverRect>();
        var c = _client;
        Overlays.PxRect box = c;
        if (!Editing && covers.Count > 0)
        {
            int x1 = covers.Min(r => c.X + (int)(r.X * c.W)), y1 = covers.Min(r => c.Y + (int)(r.Y * c.H));
            int x2 = covers.Max(r => c.X + (int)Math.Ceiling((r.X + r.W) * c.W)), y2 = covers.Max(r => c.Y + (int)Math.Ceiling((r.Y + r.H) * c.H));
            box = new Overlays.PxRect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        }
        var s = AppSettings.Current.Overlay;
        var sig = $"{box}|{c}|{Editing}|{s.CoverColor}|{s.CoverLabel}|{string.Join(";", covers.Select(r => $"{r.X:0.####},{r.Y:0.####},{r.W:0.####},{r.H:0.####}"))}";
        if (box != _placed) { Overlays.Place(this, box.X, box.Y, box.W, box.H); _placed = box; }
        if (sig == _layoutSig && !_dragging) return;
        _layoutSig = sig;

        var dpi = VisualTreeHelper.GetDpi(this);
        _canvas.Children.Clear();
        foreach (var r in covers)
        {
            var el = CoverElement(r);
            Canvas.SetLeft(el, (c.X + r.X * c.W - box.X) / dpi.DpiScaleX);
            Canvas.SetTop(el, (c.Y + r.Y * c.H - box.Y) / dpi.DpiScaleY);
            el.Width = Math.Max(4, r.W * c.W / dpi.DpiScaleX);
            el.Height = Math.Max(4, r.H * c.H / dpi.DpiScaleY);
            _canvas.Children.Add(el);
        }
    }

    FrameworkElement CoverElement(CoverRect r)
    {
        var g = new Grid { Tag = r, Cursor = Editing ? Cursors.SizeAll : null };
        g.Children.Add(new Border
        {
            Background = CoverBrush, CornerRadius = new CornerRadius(6),
            BorderBrush = Editing ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) : null,
            BorderThickness = new Thickness(Editing ? 1.5 : 0),
        });
        if (AppSettings.Current.Overlay.CoverLabel)
            g.Children.Add(new Viewbox
            {
                Margin = new Thickness(6, 3, 6, 3), StretchDirection = StretchDirection.DownOnly,
                Child = new TextBlock { Text = "???", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) },
            });
        if (!Editing) return g;

        var handle = new Border
        {
            Name = "handle", Width = 14, Height = 14, Cursor = Cursors.SizeNWSE, Background = Brushes.White,
            CornerRadius = new CornerRadius(3, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        };
        g.Children.Add(handle);
        var close = new Button
        {
            Content = "✕", Style = (Style)Application.Current.FindResource("SmallButton"), MinHeight = 0, Padding = new Thickness(5, 0, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 3, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)), Foreground = Brushes.White, ToolTip = "Delete this cover",
        };
        close.Click += (_, _) => Remove(r);
        g.Children.Add(close);
        g.MouseRightButtonUp += (_, e) => { Remove(r); e.Handled = true; };
        return g;
    }

    void Remove(CoverRect r)
    {
        Covers?.Remove(r);
        _layoutSig = "";
        Layout();
    }

    // ------------------------------------------------------------------ edit mode

    public void BeginEdit(IntPtr game, string exe)
    {
        _game = game;
        _exe = exe;
        var all = AppSettings.Current.Overlay.Covers;
        if (!all.ContainsKey(exe)) all[exe] = new List<CoverRect>();
        Editing = true;
        _layoutSig = "";
        var rect = Overlays.ClientRect(game);
        if (rect == null) { Editing = false; return; }
        _client = rect.Value;
        _title.Text = $"Money covers for {Path.GetFileNameWithoutExtension(exe)}";
        _canvas.Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        _canvas.Cursor = Cursors.Cross;
        _toolbar.Visibility = Visibility.Visible;
        Layout();
        SetShown(true);
        Overlays.SetClickThrough(this, false);
        Activate();
        Focus();
        Anim.FadeUp(_toolbar, 8, 220);
    }

    public void EndEdit()
    {
        if (!Editing) return;
        Editing = false;
        _dragging = false;
        _canvas.Background = null;
        _canvas.Cursor = null;
        _toolbar.Visibility = Visibility.Collapsed;
        var all = AppSettings.Current.Overlay.Covers;
        if (all.TryGetValue(_exe, out var l) && l.Count == 0) all.Remove(_exe);
        Overlays.SetClickThrough(this, true);
        _layoutSig = "";
        Overlays.Focus(_game); // back into the game
        Overlays.CoversEdited();
    }

    Border BuildToolbar()
    {
        var app = Application.Current;
        var done = new Button { Content = "Done", Style = (Style)app.FindResource("AccentButton"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        done.Click += (_, _) => EndEdit();
        var clear = new Button { Content = "Clear all", Margin = new Thickness(16, 0, 0, 0) };
        clear.Click += (_, _) => { Covers?.Clear(); _layoutSig = ""; Layout(); };
        var hint = new TextBlock
        {
            Text = "Drag to draw a cover. Drag a cover to move it, its corner to resize, ✕ or right-click to delete. Enter or Esc when done.",
            Foreground = (Brush)app.FindResource("Sub"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center,
        };

        var dock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(clear);
        buttons.Children.Add(done);
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(hint);
        dock.Children.Add(_title);
        var bar = new Border
        {
            Child = dock, Visibility = Visibility.Collapsed, Padding = new Thickness(18, 12, 14, 12), CornerRadius = new CornerRadius(8),
            Background = (Brush)app.FindResource("Panel"), BorderBrush = (Brush)app.FindResource("LineStrong"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4 },
        };
        TextElement.SetForeground(bar, (Brush)app.FindResource("Text"));
        return bar;
    }

    // drag state: drawing a new cover, moving one, or resizing one
    bool _dragging;
    enum DragKind { Draw, Move, Resize }
    DragKind _kind;
    CoverRect? _drag;
    Point _start;
    CoverRect _orig = new();

    void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        if (!Editing || e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
        var p = Frac(e.GetPosition(_canvas));
        var hit = e.OriginalSource as FrameworkElement;
        var cover = hit == null ? null : FindCover(hit);
        if (cover != null)
        {
            _drag = cover;
            _kind = hit!.Name == "handle" ? DragKind.Resize : DragKind.Move;
        }
        else
        {
            _drag = new CoverRect { X = p.X, Y = p.Y };
            Covers?.Add(_drag);
            _kind = DragKind.Draw;
        }
        _start = p;
        _orig = new CoverRect { X = _drag.X, Y = _drag.Y, W = _drag.W, H = _drag.H };
        _dragging = true;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    void Canvas_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging || _drag == null) return;
        var p = Frac(e.GetPosition(_canvas));
        double dx = p.X - _start.X, dy = p.Y - _start.Y;
        switch (_kind)
        {
            case DragKind.Draw:
                _drag.X = Math.Min(_start.X, p.X); _drag.Y = Math.Min(_start.Y, p.Y);
                _drag.W = Math.Abs(dx); _drag.H = Math.Abs(dy);
                break;
            case DragKind.Move:
                _drag.X = Math.Clamp(_orig.X + dx, 0, 1 - _orig.W); _drag.Y = Math.Clamp(_orig.Y + dy, 0, 1 - _orig.H);
                break;
            case DragKind.Resize:
                _drag.W = Math.Clamp(_orig.W + dx, 0.005, 1 - _orig.X); _drag.H = Math.Clamp(_orig.H + dy, 0.005, 1 - _orig.Y);
                break;
        }
        Layout();
    }

    void Canvas_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();
        // a click without a drag doesn't leave a tiny cover behind
        if (_kind == DragKind.Draw && _drag != null && (_drag.W * _client.W < 6 || _drag.H * _client.H < 6)) Covers?.Remove(_drag);
        _drag = null;
        _layoutSig = "";
        Layout();
    }

    /// <summary>Canvas position (DIPs) as a fraction of the game's client area. While editing the canvas is exactly that area.</summary>
    Point Frac(Point dip)
    {
        double w = Math.Max(1, _canvas.ActualWidth), h = Math.Max(1, _canvas.ActualHeight);
        return new Point(Math.Clamp(dip.X / w, 0, 1), Math.Clamp(dip.Y / h, 0, 1));
    }

    static CoverRect? FindCover(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is FrameworkElement { Tag: CoverRect r }) return r;
        return null;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }
}
