using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GpuSwitcher.UI;

/// <summary>Draws the tray menu like a Windows 11 menu, in the app's light/dark colors.</summary>
public sealed class TrayMenuRenderer : Forms.ToolStripRenderer
{
    sealed record Palette(Drawing.Color Bg, Drawing.Color Border, Drawing.Color Text, Drawing.Color Disabled, Drawing.Color Hover, Drawing.Color Line);

    static readonly Palette Dark = new(Hex("#2C2C2C"), Hex("#454545"), Hex("#FFFFFF"), Hex("#787878"), Hex("#3D3D3D"), Hex("#3F3F3F"));
    static readonly Palette Light = new(Hex("#F9F9F9"), Hex("#D6D6D6"), Hex("#1B1B1B"), Hex("#8A8A8A"), Hex("#EBEBEB"), Hex("#E3E3E3"));
    static Palette P => Theme.IsDark ? Dark : Light;

    static Drawing.Color Hex(string h) => Drawing.ColorTranslator.FromHtml(h);

    /// <summary>Sets up a menu to use this renderer: font, spacing, round corners.</summary>
    public static void Style(Forms.ContextMenuStrip menu)
    {
        menu.Renderer = new TrayMenuRenderer();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Font = new Drawing.Font("Segoe UI Variable Text", 9.75f);
        // Windows 11 rounds the menu and gives it a soft shadow; WinForms' own square shadow would show behind it
        menu.DropShadowEnabled = !Theme.MicaSupported;
        menu.HandleCreated += (_, _) => Theme.RoundCorners(menu.Handle);
        menu.Opening += (_, _) =>
        {
            Theme.Refresh(); // follow a light/dark switch made while the window was closed
            int s(int px) => menu.LogicalToDeviceUnits(px);
            menu.Padding = new Forms.Padding(s(4), s(4), s(4), s(4));
            foreach (Forms.ToolStripItem i in menu.Items)
                if (i is Forms.ToolStripMenuItem) { i.Padding = new Forms.Padding(s(10), s(6), s(24), s(6)); i.Margin = Forms.Padding.Empty; }
            menu.BackColor = P.Bg;
        };
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var b = new Drawing.SolidBrush(P.Bg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        if (Theme.MicaSupported) return; // Windows 11 draws the rounded border itself
        using var pen = new Drawing.Pen(P.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Drawing.RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        float d = e.Item.Owner?.LogicalToDeviceUnits(8) ?? 8; // corner diameter
        using var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var b = new Drawing.SolidBrush(P.Hover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? P.Text : P.Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Drawing.Pen(P.Line);
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }
}
