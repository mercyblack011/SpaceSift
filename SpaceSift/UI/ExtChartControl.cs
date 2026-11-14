using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceSift.Core;

namespace SpaceSift.UI;

/// <summary>Pie or donut chart of the largest extensions plus an "Other" slice, with a legend.</summary>
public sealed class ExtChartControl : FrameworkElement
{
    private const int MaxSlices = 10;
    private List<ExtStat> _stats = new();
    private ExtColors? _colors;
    private readonly List<(double start, double sweep, int ext)> _slices = new();
    private Point _center;
    private double _radius, _hole;

    public bool Donut { get; set; } = true;
    public int SelectedExt { get; set; } = -1;
    public event Action<int>? SliceClicked;

    private static readonly Brush OtherBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC4)));
    private static readonly Pen SlicePen = Freeze(new Pen(Brushes.White, 1.5));
    private static readonly Pen SelPen = Freeze(new Pen(Brushes.Black, 2.5));
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>0..1: how much of the circle is drawn. Animated so slices sweep in clockwise.</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(ExtChartControl), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    public void SetData(List<ExtStat> stats, ExtColors? colors)
    {
        _stats = stats; _colors = colors; PlayReveal();
    }

    /// <summary>Sweeps the chart in from 12 o'clock.</summary>
    public void PlayReveal()
    {
        if (!Motion.Enabled) { BeginAnimation(RevealProperty, null); Reveal = 1; return; }
        BeginAnimation(RevealProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(750))
        { EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _slices.Clear();
        if (_colors == null || _stats.Count == 0) return;
        double total = 0;
        foreach (var s in _stats) total += s.Size;
        if (total <= 0) return;

        double legendW = Math.Min(190, ActualWidth * 0.45);
        double size = Math.Min(ActualWidth - legendW - 16, ActualHeight - 16);
        if (size < 40) { legendW = 0; size = Math.Min(ActualWidth, ActualHeight) - 16; }
        if (size < 20) return;
        _radius = size / 2;
        _hole = Donut ? _radius * 0.55 : 0;
        _center = new Point(8 + _radius, ActualHeight / 2);

        double angle = -90;
        var top = _stats.Take(MaxSlices).ToList();
        double otherSize = total - top.Sum(s => (double)s.Size);
        var entries = top.Select(s => (s.Ext, (double)s.Size, (int)s.Id, (Brush)_colors.Brushes[s.Id])).ToList();
        if (otherSize > 0) entries.Add(("Other", otherSize, -1, OtherBrush));

        double reveal = Math.Clamp(Reveal, 0, 1);
        foreach (var (_, sz, id, brush) in entries)
        {
            double sweep = sz / total * 360 * reveal;
            if (sweep > 0.05)
            {
                dc.DrawGeometry(brush, new Pen(ThemeManager.Brush("Surface", Brushes.White), 2), Slice(angle, sweep));
                _slices.Add((angle, sweep, id));
            }
            angle += sweep;
        }
        foreach (var (start, sweep, id) in _slices)
            if (id >= 0 && id == SelectedExt) dc.DrawGeometry(null, new Pen(ThemeManager.Brush("Text", Brushes.Black), 2.5), Slice(start, sweep));

        var tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Donut)
        {
            var t = new FormattedText(Fmt.Size((long)total), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 15, ThemeManager.Brush("Text", Brushes.Black), dpi);
            dc.DrawText(t, new Point(_center.X - t.Width / 2, _center.Y - t.Height / 2));
        }

        if (legendW > 0)
        {
            double x = _center.X + _radius + 16, y = Math.Max(4, _center.Y - entries.Count * 9);
            foreach (var (name, sz, _, brush) in entries)
            {
                dc.DrawRectangle(brush, null, new Rect(x, y + 3, 10, 10));
                var t = new FormattedText($"{name}  {sz / total * 100:0.0}%", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 11.5, ThemeManager.Brush("Text", Brushes.Black), dpi)
                { MaxTextWidth = Math.Max(10, legendW - 16), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(t, new Point(x + 15, y));
                y += 18;
            }
        }
    }

    private Geometry Slice(double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99)
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new EllipseGeometry(_center, _radius, _radius));
            if (_hole > 0) g.Children.Add(new EllipseGeometry(_center, _hole, _hole));
            return g;
        }
        Point P(double deg, double r) => new(_center.X + r * Math.Cos(deg * Math.PI / 180), _center.Y + r * Math.Sin(deg * Math.PI / 180));
        bool large = sweepDeg > 180;
        var sg = new StreamGeometry();
        using (var c = sg.Open())
        {
            if (_hole > 0)
            {
                c.BeginFigure(P(startDeg, _hole), true, true);
                c.LineTo(P(startDeg, _radius), true, false);
                c.ArcTo(P(startDeg + sweepDeg, _radius), new Size(_radius, _radius), 0, large, SweepDirection.Clockwise, true, false);
                c.LineTo(P(startDeg + sweepDeg, _hole), true, false);
                c.ArcTo(P(startDeg, _hole), new Size(_hole, _hole), 0, large, SweepDirection.Counterclockwise, true, false);
            }
            else
            {
                c.BeginFigure(_center, true, true);
                c.LineTo(P(startDeg, _radius), true, false);
                c.ArcTo(P(startDeg + sweepDeg, _radius), new Size(_radius, _radius), 0, large, SweepDirection.Clockwise, true, false);
            }
        }
        sg.Freeze();
        return sg;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        double dx = p.X - _center.X, dy = p.Y - _center.Y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d > _radius || d < _hole) return;
        double a = Math.Atan2(dy, dx) * 180 / Math.PI;
        foreach (var (start, sweep, id) in _slices)
        {
            double rel = ((a - start) % 360 + 360) % 360;
            if (rel <= sweep) { if (id >= 0) SliceClicked?.Invoke(id); return; }
        }
    }
}
