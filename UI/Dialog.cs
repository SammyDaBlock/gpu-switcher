using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GpuSwitcher.UI;

/// <summary>Plain dark yes/no and info dialogs. No typing to confirm.</summary>
public static class Dialog
{
    public static bool Confirm(string title, string message, string yes = "Yes", string no = "No") => Show(title, message, yes, no);

    public static void Info(string title, string message) => Show(title, message, "OK", null);

    /// <summary>Searchable list of apps to add to startup. Returns the picked exe, "browse", or null.</summary>
    public static string? PickApp(string title, IReadOnlyList<Core.StartupManager.AddCandidate> apps)
    {
        var app = Application.Current;
        var win = NewWindow(title);
        win.SizeToContent = SizeToContent.Manual;
        win.Width = 480; win.Height = 560; win.MaxWidth = double.PositiveInfinity;
        string? result = null;

        var outer = new DockPanel();
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        var head = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)app.FindResource("DisplayFont"), Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);

        var buttons = new DockPanel();
        var browse = new Button { Content = "Browse for a program…" };
        browse.Click += (_, _) => { result = "browse"; win.Close(); };
        buttons.Children.Add(browse);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => win.Close();
        var add = new Button { Content = "Add", MinWidth = 96, IsDefault = true, IsEnabled = false, Style = (Style)app.FindResource("AccentButton") };
        right.Children.Add(cancel);
        right.Children.Add(add);
        buttons.Children.Add(right);
        outer.Children.Add(Footer(buttons));
        outer.Children.Add(root);

        var list = new ListBox();
        root.Children.Add(list);
        void Fill()
        {
            var q = search.Text.Trim();
            list.Items.Clear();
            foreach (var c in apps.Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new DockPanel { Tag = c.Exe, ToolTip = c.Exe };
                var icon = new Image { Source = Look.IconFor(c.Exe), Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0) };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                row.Children.Add(icon);
                var badge = new TextBlock { Text = c.Running ? "running" : "", Foreground = Look.Ok, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
                row.Children.Add(new TextBlock { Text = c.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Items.Add(row);
            }
        }
        Fill();
        search.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem != null;
        void Pick() { if (list.SelectedItem is FrameworkElement f) { result = (string)f.Tag; win.Close(); } }
        add.Click += (_, _) => Pick();
        list.MouseDoubleClick += (_, _) => Pick();

        win.Content = outer;
        win.Loaded += (_, _) => search.Focus();
        win.ShowDialog();
        return result;
    }

    static Window NewWindow(string title)
    {
        var app = Application.Current;
        var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.IsActive)
                    ?? app.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        var win = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (Brush)app.FindResource("Panel"),
            Foreground = (Brush)app.FindResource("Text"),
            FontFamily = (FontFamily)app.FindResource("UiFont"),
            // Windows 11 draws its own rounded border + shadow; a WPF border would get its corners cut off
            BorderBrush = (Brush)app.FindResource("LineStrong"),
            BorderThickness = new Thickness(Theme.MicaSupported ? 0 : 1),
            ShowInTaskbar = owner == null,
            Topmost = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            MaxWidth = 540,
            MinWidth = 340,
        };
        TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
        win.SourceInitialized += (_, _) => Theme.RoundCorners(new System.Windows.Interop.WindowInteropHelper(win).Handle);
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
        return win;
    }

    /// <summary>Button strip along the bottom, on a slightly different background (like Windows 11 dialogs).</summary>
    static Border Footer(UIElement buttons)
    {
        var app = Application.Current;
        var f = new Border
        {
            Background = (Brush)app.FindResource("DialogFooter"),
            BorderBrush = (Brush)app.FindResource("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 16, 24, 16),
            Child = buttons,
        };
        DockPanel.SetDock(f, Dock.Bottom);
        return f;
    }

    static bool Show(string title, string message, string yes, string? no)
    {
        var app = Application.Current;
        var win = NewWindow(title);
        bool result = false;
        var outer = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (no != null)
        {
            var bNo = new Button { Content = no, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            bNo.Click += (_, _) => win.Close();
            buttons.Children.Add(bNo);
        }
        var bYes = new Button { Content = yes, MinWidth = 96, IsDefault = true, Style = (Style)app.FindResource("AccentButton") };
        bYes.Click += (_, _) => { result = true; win.Close(); };
        buttons.Children.Add(bYes);
        outer.Children.Add(Footer(buttons));

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)app.FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Foreground = (Brush)app.FindResource("Sub") });
        outer.Children.Add(root);
        win.Content = outer;
        win.ShowDialog();
        return result;
    }
}
