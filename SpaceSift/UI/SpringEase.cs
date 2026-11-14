using System.Windows;
using System.Windows.Media.Animation;

namespace SpaceSift.UI;

/// <summary>
/// Spring physics as an easing curve, using Apple's designer-friendly parameters
/// (the same model Motion / Framer Motion and SwiftUI use):
///   Damping  — 1.0 = critically damped (no overshoot), &lt; 1 = bouncy (0.8 = Apple's "momentum" feel)
///   Response — how quickly it gets there, in seconds (0.3–0.4 for most UI)
/// The animation's Duration should be at least <see cref="SettleTime"/> so the spring can finish;
/// <see cref="Spring.Animation"/> sets that for you.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty = DependencyProperty.Register(
        nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(1.0));
    public static readonly DependencyProperty ResponseProperty = DependencyProperty.Register(
        nameof(Response), typeof(double), typeof(SpringEase), new PropertyMetadata(0.35));

    public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
    public double Response { get => (double)GetValue(ResponseProperty); set => SetValue(ResponseProperty, value); }

    public SpringEase() { EasingMode = EasingMode.EaseIn; } // EaseIn = use EaseInCore as-is

    /// <summary>Seconds until the spring is visually at rest (within 0.1%).</summary>
    public static double SettleTime(double damping, double response)
    {
        double w0 = 2 * Math.PI / Math.Max(0.05, response);
        double zeta = Math.Clamp(damping, 0.05, 1.0);
        return Math.Min(2.5, -Math.Log(0.001) / (zeta * w0));
    }

    /// <summary>Spring position (0→1) at <paramref name="seconds"/>.</summary>
    public static double Position(double seconds, double damping, double response)
    {
        double w0 = 2 * Math.PI / Math.Max(0.05, response);
        double zeta = Math.Clamp(damping, 0.05, 1.0);
        double t = seconds;
        if (zeta >= 0.999)
            return 1 - (1 + w0 * t) * Math.Exp(-w0 * t);               // critically damped
        double wd = w0 * Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * w0 * t) * (Math.Cos(wd * t) + zeta * w0 / wd * Math.Sin(wd * t)); // under-damped
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1) return 1;
        double total = SettleTime(Damping, Response);
        return Position(normalizedTime * total, Damping, Response);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}

/// <summary>Helpers that build spring-driven animations with the right duration.</summary>
public static class Spring
{
    /// <summary>Default UI spring: critically damped, no overshoot (Apple: damping 1.0, response 0.35).</summary>
    public static readonly SpringEase Smooth = Make(1.0, 0.35);
    /// <summary>Momentum spring with a little life (Apple: damping 0.8, response 0.35).</summary>
    public static readonly SpringEase Bouncy = Make(0.8, 0.35);

    private static SpringEase Make(double d, double r) { var e = new SpringEase { Damping = d, Response = r }; e.Freeze(); return e; }

    public static Duration DurationFor(SpringEase e) => TimeSpan.FromSeconds(SpringEase.SettleTime(e.Damping, e.Response));

    public static DoubleAnimation Animation(double? from, double to, SpringEase? ease = null)
    {
        ease ??= Smooth;
        var a = new DoubleAnimation { To = to, Duration = DurationFor(ease), EasingFunction = ease };
        if (from.HasValue) a.From = from;
        return a;
    }
}
