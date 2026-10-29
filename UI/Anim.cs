using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GpuSwitcher.UI;

/// <summary>Small motion helpers. Hover/press fades live in App.xaml; these are the ones started from code.
/// They're skipped when Windows' "Animation effects" setting is off.</summary>
public static class Anim
{
    public static bool On => SystemParameters.ClientAreaAnimation;
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>Fade in while sliding up a bit (tab content, banners, dialogs, the window).</summary>
    public static void FadeUp(UIElement el, double offset = 10, int ms = 220)
    {
        if (!On) return;
        var move = el.RenderTransform as TranslateTransform;
        if (move == null || move.IsFrozen) el.RenderTransform = move = new TranslateTransform();
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(ms)) { EasingFunction = Out });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, Ms(ms + 60)) { EasingFunction = Out });
    }

    /// <summary>Fade (and optionally scale) to a target opacity, then run done.</summary>
    public static void Fade(UIElement el, double to, int ms = 180, Action? done = null, double? scaleTo = null)
    {
        if (!On)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            done?.Invoke();
            return;
        }
        var a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = Out };
        if (done != null) a.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, a);
        if (scaleTo is double s)
        {
            if (el.RenderTransform is not ScaleTransform sc || sc.IsFrozen)
            {
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                el.RenderTransform = sc = new ScaleTransform();
            }
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s, Ms(ms)) { EasingFunction = Out });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s, Ms(ms)) { EasingFunction = Out });
        }
    }

    /// <summary>Shows a collapsed element with a fade-up, or hides it. No-op if it's already in that state.</summary>
    public static void Show(UIElement el, bool show)
    {
        var want = show ? Visibility.Visible : Visibility.Collapsed;
        if (el.Visibility == want) return;
        el.Visibility = want;
        if (show) FadeUp(el, 6, 200);
    }

    // ------------------------------------------------------------------ smooth meters

    /// <summary>Bind this instead of Value on a ProgressBar/Slider: the bar glides to new values instead of jumping.</summary>
    public static readonly DependencyProperty SmoothValueProperty = DependencyProperty.RegisterAttached(
        "SmoothValue", typeof(double), typeof(Anim), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is not RangeBase r) return;
            if (!On) { r.BeginAnimation(RangeBase.ValueProperty, null); r.Value = (double)e.NewValue; return; }
            r.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation((double)e.NewValue, Ms(700)) { EasingFunction = Out });
        }));

    public static double GetSmoothValue(DependencyObject d) => (double)d.GetValue(SmoothValueProperty);
    public static void SetSmoothValue(DependencyObject d, double v) => d.SetValue(SmoothValueProperty, v);
}
