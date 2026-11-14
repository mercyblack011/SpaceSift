using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpaceSift.UI;

/// <summary>
/// Small motion toolkit (native take on the patterns Animate UI ships for the web):
/// counting numbers, fade/slide entrances, collapses and text cross-fades.
/// All durations are short (200–900 ms) and ease-out so the app still feels fast.
/// </summary>
public static class Motion
{
    /// <summary>False when Windows "Animation effects" is off: motion becomes instant (reduced-motion).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Out = Spring.Smooth;   // critically damped spring (damping 1.0, response 0.35)
    public static readonly IEasingFunction InOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });

    private static T Freeze<T>(T e) where T : Freezable { e.Freeze(); return e; }

    // ------------------------------------------------------------------ counting numbers

    private sealed class CountState { public Func<double, string> Format = v => v.ToString("0"); }
    private static readonly ConditionalWeakTable<TextBlock, CountState> States = new();

    public static readonly DependencyProperty NumberProperty = DependencyProperty.RegisterAttached(
        "Number", typeof(double), typeof(Motion), new PropertyMetadata(double.NaN, OnNumberChanged));

    private static void OnNumberChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb && States.TryGetValue(tb, out var s) && e.NewValue is double v && !double.IsNaN(v))
            tb.Text = s.Format(v);
    }

    /// <summary>Counts a TextBlock from its last value to <paramref name="to"/>, formatting every frame.</summary>
    public static void Count(TextBlock tb, double to, Func<double, string> format, int ms = 900)
    {
        var state = States.GetOrCreateValue(tb);
        if (!Enabled) { state.Format = format; tb.BeginAnimation(NumberProperty, null); tb.SetValue(NumberProperty, to); tb.Text = format(to); return; }
        state.Format = format;
        double from = (double)tb.GetValue(NumberProperty);
        if (double.IsNaN(from)) from = 0;
        if (Math.Abs(from - to) < 0.5) { tb.BeginAnimation(NumberProperty, null); tb.SetValue(NumberProperty, to); tb.Text = format(to); return; }
        tb.BeginAnimation(NumberProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    public static void CountBytes(TextBlock tb, long bytes, int ms = 900) => Count(tb, bytes, v => Core.Fmt.Size((long)v), ms);

    // ------------------------------------------------------------------ entrances / exits

    /// <summary>Fades in and glides up from <paramref name="dy"/> px.</summary>
    public static void FadeIn(UIElement el, int ms = 320, double dy = 8, int delayMs = 0)
    {
        if (!Enabled) return;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, BeginTime = begin });
        if (dy != 0)
        {
            if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen) el.RenderTransform = tt = new TranslateTransform();
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms + 80)) { EasingFunction = Out, BeginTime = begin });
        }
    }

    /// <summary>Soft opacity dip and return, used when text content changes.</summary>
    public static void Blink(UIElement el, int ms = 260)
    { if (Enabled) el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out }); }

    /// <summary>Folds an element away vertically (scale + fade), then collapses it.</summary>
    public static Task Collapse(FrameworkElement el, int ms = 420)
    {
        var tcs = new TaskCompletionSource();
        if (!Enabled) { el.Visibility = Visibility.Collapsed; tcs.SetResult(); return tcs.Task; }
        var st = new ScaleTransform(1, 1);
        el.LayoutTransform = st;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = InOut };
        anim.Completed += (_, _) => { el.Visibility = Visibility.Collapsed; tcs.TrySetResult(); };
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms * 0.7)));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        return tcs.Task;
    }

    /// <summary>Animates opacity to a target (for enabling / disabling areas).</summary>
    public static void FadeTo(UIElement el, double to, int ms = 220)
    {
        if (!Enabled) { el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = to; return; }
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    /// <summary>Glides a progress bar to its new value instead of jumping.</summary>
    public static void Glide(System.Windows.Controls.Primitives.RangeBase bar, double to, int ms = 220)
    {
        if (!Enabled) { bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); bar.Value = to; return; }
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    /// <summary>Cross-fades a TextBlock every time its Text changes.</summary>
    public static void FadeOnTextChange(TextBlock tb)
    {
        var dpd = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        dpd?.AddValueChanged(tb, (_, _) => Blink(tb));
    }
}
