using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GpuSwitcher.Core;

namespace GpuSwitcher.UI;

/// <summary>Dims one monitor and lets you drag a square on it. Returns the square as fractions of the monitor, or null.</summary>
public sealed class RegionPicker : Window
{
    readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) };
    readonly Rectangle _box = new() { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), Visibility = Visibility.Collapsed };
    Point _start;
    bool _dragging;
    CoverRect? _result;

    RegionPicker(Overlays.PxRect monitor)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // place it on the monitor (pixels -> DIPs of the main display), then maximize to fill exactly that monitor
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
        Left = monitor.X / dpi.DpiScaleX + 10; Top = monitor.Y / dpi.DpiScaleY + 10;
        Width = 200; Height = 200;
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        var hint = new TextBlock
        {
            Text = "Drag a square over the part of the screen the pump should show.  Esc cancels.",
            Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(24),
        };
        _canvas.Children.Add(hint);
        _canvas.Children.Add(_box);
        Content = _canvas;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _dragging = true; CaptureMouse(); Update(_start); _box.Visibility = Visibility.Visible; };
        MouseMove += (_, e) => { if (_dragging) Update(e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            var r = Update(e.GetPosition(_canvas));
            if (r.Width >= 8) _result = new CoverRect { X = r.X / ActualWidth, Y = r.Y / ActualHeight, W = r.Width / ActualWidth, H = r.Height / ActualHeight };
            Close();
        };
    }

    /// <summary>Square from the drag start towards the mouse, kept on the screen.</summary>
    Rect Update(Point p)
    {
        double side = Math.Max(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        double x = p.X < _start.X ? _start.X - side : _start.X;
        double y = p.Y < _start.Y ? _start.Y - side : _start.Y;
        side = Math.Min(side, Math.Min(ActualWidth, ActualHeight));
        x = Math.Clamp(x, 0, ActualWidth - side); y = Math.Clamp(y, 0, ActualHeight - side);
        Canvas.SetLeft(_box, x); Canvas.SetTop(_box, y);
        _box.Width = side; _box.Height = side;
        return new Rect(x, y, side, side);
    }

    internal static CoverRect? Pick(Overlays.PxRect monitor)
    {
        var w = new RegionPicker(monitor);
        w.ShowDialog();
        return w._result;
    }
}
