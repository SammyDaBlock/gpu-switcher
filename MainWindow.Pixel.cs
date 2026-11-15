using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GpuSwitcher.Core;
using GpuSwitcher.UI;
using Forms = System.Windows.Forms;

namespace GpuSwitcher;

// The "Pump screen" tab (Atmos II 32x32 display). Drawing and the pump itself live in UI/PixelScreen.
public partial class MainWindow
{
    const int PixelTab = 5;
    readonly PickerOwner _pxMode = new() { Which = "px-mode" };
    readonly PickerOwner _pxRot = new() { Which = "px-rot" };
    readonly PickerOwner _pxMonitor = new() { Which = "px-monitor" };
    readonly WriteableBitmap _pxBmp = new(PixelDisplay.Size, PixelDisplay.Size, 96, 96, PixelFormats.Bgra32, null);
    bool _pxLoading;

    static readonly string[] PixelPalette = { "#FFFFFF", "#FF3D9A", "#9B5CFF", "#2F7BFF", "#00D5FF", "#2BE07A", "#FFD21F", "#FF8A1F", "#FF3030" };

    static PixelSettings Px => AppSettings.Current.Pixel;

    void InitPixel()
    {
        PixelPreview.Source = _pxBmp;
        PixelScreen.Frame += rgb => Dispatcher.BeginInvoke(() => DrawPixelPreview(rgb));
        PixelScreen.Changed += () => Dispatcher.BeginInvoke(RefreshPixelStatus);
        DrawPixelPreview(new byte[PixelDisplay.Size * PixelDisplay.Size * 3]);
    }

    /// <summary>Only draw frames for the preview while someone can see it.</summary>
    void UpdatePixelPreview() => PixelScreen.SetPreview(IsVisible && Tabs.SelectedIndex == PixelTab);

    void DrawPixelPreview(byte[] rgb)
    {
        int n = PixelDisplay.Size;
        var px = new byte[n * n * 4];
        for (int c = 0; c < n * n; c++)
        {
            if (!PixelDisplay.HasLed[c]) continue; // corners without LEDs stay see-through
            px[c * 4] = rgb[c * 3 + 2]; px[c * 4 + 1] = rgb[c * 3 + 1]; px[c * 4 + 2] = rgb[c * 3]; px[c * 4 + 3] = 255;
        }
        _pxBmp.WritePixels(new Int32Rect(0, 0, n, n), px, n * 4, 0);
    }

    void RefreshPixel()
    {
        _pxLoading = true;
        var p = Px;
        PixelOn.IsChecked = p.Enabled;
        PixelBrightness.Value = p.Brightness;
        PixelBrightnessText.Text = $"{p.Brightness}%";
        Picker(_pxRot, PixelRotationPicker, new[] { ("0", "0°"), ("90", "90°"), ("180", "180°"), ("270", "270°") }, p.Rotation.ToString());
        Picker(_pxMode, PixelModePicker, new[] { ("Clock", "Clock"), ("Printer", "3D printer"), ("Mirror", "Screen mirror"), ("Image", "GIF / image") }, p.Mode);
        var monitors = Overlays.Monitors().Select(m => (m.Device, $"{Overlays.MonitorName(m.Device)}{(m.Primary ? " (main)" : "")}")).ToArray();
        Picker(_pxMonitor, PixelMonitorPicker, monitors, p.MirrorMonitor ?? monitors.FirstOrDefault().Device);
        PixelClock24.IsChecked = p.Clock24h;
        PixelClockSeconds.IsChecked = p.ClockSeconds;
        PixelPrinterKey.Text = PrinterLink();
        PixelContrast.Value = p.MirrorContrast;
        PixelContrastText.Text = $"{p.MirrorContrast}%";
        PixelAreaText.Text = p.MirrorRegion == null ? "Middle of the screen" : "Your own square";
        PixelAreaReset.Visibility = p.MirrorRegion == null ? Visibility.Collapsed : Visibility.Visible;
        PixelImageText.Text = string.IsNullOrEmpty(p.ImagePath) ? "No file yet" : System.IO.Path.GetFileName(p.ImagePath);

        PixelClockPanel.Visibility = p.Mode == "Clock" ? Visibility.Visible : Visibility.Collapsed;
        PixelPrinterPanel.Visibility = p.Mode == "Printer" ? Visibility.Visible : Visibility.Collapsed;
        PixelMirrorPanel.Visibility = p.Mode == "Mirror" ? Visibility.Visible : Visibility.Collapsed;
        PixelImagePanel.Visibility = p.Mode == "Image" ? Visibility.Visible : Visibility.Collapsed;
        bool colored = p.Mode is "Clock" or "Printer";
        PixelColorLabel.Visibility = PixelColors.Visibility = colored ? Visibility.Visible : Visibility.Collapsed;
        BuildPixelColors();
        RefreshPixelStatus();
        _pxLoading = false;
    }

    void RefreshPixelStatus()
    {
        var p = Px;
        PixelStatus.Text = !p.Enabled ? "Off: Cooler Master's software has the screen. The preview shows what it would look like."
            : PixelScreen.PumpConnected ? "Connected to the pump." : "Looking for the pump...";
        PixelPrinterStatus.Text = PixelScreen.PrinterStatus;
    }

    void BuildPixelColors()
    {
        PixelColors.Children.Clear();
        var current = Px.Color;
        foreach (var hex in PixelPalette.Append(PixelPalette.Contains(current, StringComparer.OrdinalIgnoreCase) ? null : current))
        {
            if (hex == null) continue;
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool sel = hex.Equals(current, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 28, Height = 28 };
            dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Stroke = (Brush)FindResource("LineStrong"), StrokeThickness = 1, Margin = new Thickness(sel ? 4 : 2) });
            if (sel) dot.Children.Add(new Ellipse { Stroke = (Brush)FindResource("Primary"), StrokeThickness = 2 });
            var b = new Button { Content = dot, Tag = hex, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2), ToolTip = hex };
            b.Click += (_, _) => SetPixelColor(hex);
            PixelColors.Children.Add(b);
        }
        var custom = new Button { Content = "Custom...", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 6), VerticalAlignment = VerticalAlignment.Center };
        custom.Click += (_, _) =>
        {
            var c = (Color)ColorConverter.ConvertFromString(Px.Color);
            using var dlg = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) };
            if (dlg.ShowDialog() == Forms.DialogResult.OK) SetPixelColor($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        };
        PixelColors.Children.Add(custom);
    }

    void SetPixelColor(string hex)
    {
        Px.Color = hex;
        SavePixel();
        BuildPixelColors();
    }

    void SavePixel()
    {
        AppSettings.Current.Save();
        PixelScreen.Apply();
    }

    void PixelPick(string which, string? key)
    {
        var p = Px;
        switch (which)
        {
            case "px-mode": p.Mode = key ?? "Clock"; break;
            case "px-rot": p.Rotation = int.Parse(key ?? "0"); break;
            case "px-monitor": p.MirrorMonitor = key; p.MirrorRegion = null; break;
        }
        SavePixel();
        if (which != "px-rot") RefreshPixel();
    }

    void PixelOn_Click(object sender, RoutedEventArgs e)
    {
        bool on = PixelOn.IsChecked == true;
        PixelScreen.SetEnabled(on);
        RefreshPixelStatus();
        Status(on ? "Taking the pump screen over from Cooler Master..." : "Giving the pump screen back to Cooler Master.");
    }

    void PixelBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_pxLoading || !IsLoaded) return;
        Px.Brightness = (int)Math.Round(PixelBrightness.Value);
        PixelBrightnessText.Text = $"{Px.Brightness}%";
        SavePixel();
    }

    void PixelContrast_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_pxLoading || !IsLoaded) return;
        Px.MirrorContrast = (int)Math.Round(PixelContrast.Value);
        PixelContrastText.Text = $"{Px.MirrorContrast}%";
        SavePixel();
    }

    void PixelClock_Click(object sender, RoutedEventArgs e)
    {
        Px.Clock24h = PixelClock24.IsChecked == true;
        Px.ClockSeconds = PixelClockSeconds.IsChecked == true;
        SavePixel();
    }

    void PixelPrinterKey_LostFocus(object sender, RoutedEventArgs e)
    {
        // http://host:port/tv?key=abc -> ws://host:port/tvws + abc (a bare key keeps the saved address)
        var text = PixelPrinterKey.Text.Trim().Trim('"');
        string url = Px.PrinterUrl, key = text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
        {
            url = $"{(u.Scheme == "https" ? "wss" : "ws")}://{u.Authority}/tvws";
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            key = q["key"] ?? "";
        }
        if (url == Px.PrinterUrl && key == Px.PrinterKey) return;
        Px.PrinterUrl = url; Px.PrinterKey = key;
        SavePixel();
        PixelPrinterKey.Text = PrinterLink();
    }

    static string PrinterLink() => Px.PrinterUrl == "" ? Px.PrinterKey
        : $"{Px.PrinterUrl.Replace("wss://", "https://").Replace("ws://", "http://").Replace("/tvws", "/tv")}?key={Px.PrinterKey}";

    void PixelPickArea_Click(object sender, RoutedEventArgs e)
    {
        var monitors = Overlays.Monitors();
        var mon = monitors.FirstOrDefault(m => m.Device == Px.MirrorMonitor);
        if (mon.Device == null) mon = monitors.FirstOrDefault();
        var r = RegionPicker.Pick(mon.Bounds);
        if (r == null) return;
        Px.MirrorMonitor = mon.Device;
        Px.MirrorRegion = r;
        SavePixel();
        RefreshPixel();
    }

    void PixelAreaReset_Click(object sender, RoutedEventArgs e)
    {
        Px.MirrorRegion = null;
        SavePixel();
        RefreshPixel();
    }

    void PixelChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images and GIFs|*.gif;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        Px.ImagePath = dlg.FileName;
        SavePixel();
        RefreshPixel();
    }
}
