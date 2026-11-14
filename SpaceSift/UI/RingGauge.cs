using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpaceSift.UI;

/// <summary>
/// Circular gauge with a gradient arc and rounded caps. Value (0..1) animates smoothly;
/// the arc turns warm (amber → rose) when the disk is nearly full.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Solid arc colour (the mockup's yellow). When null, a cool/warm gradient is used.</summary>
    public Brush? ArcBrush { get => (Brush?)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>Animates to a new value with an ease-out curve.</summary>
    public void AnimateTo(double v)
    {
        var anim = new DoubleAnimation(Math.Clamp(v, 0, 1), TimeSpan.FromMilliseconds(1100))
        { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(ValueProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        var trackPen = new Pen(TrackBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, c, r, r);

        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.001) return;

        // cool gradient normally; warm gradient when the drive is over 90% full
        bool hot = v >= 0.9;
        var grad = new LinearGradientBrush(
            hot ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0x8B, 0x6C, 0xFF),
            hot ? Color.FromRgb(0xF4, 0x3F, 0x5E) : Color.FromRgb(0x22, 0xD3, 0xEE), 45);
        var pen = new Pen(ArcBrush ?? (Brush)grad, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a0 = -90, a1 = -90 + 360 * v;
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(a0), false, false);
            ctx.ArcTo(P(a1), new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
