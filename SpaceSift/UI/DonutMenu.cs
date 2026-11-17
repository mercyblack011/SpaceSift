// The format donut, a WPF port of Filee's DonutMenu (MIT, https://github.com/KnifeLemon/Filee):
// slices spring open from the centre one after another, the slice under the cursor pops outward, "More" flips
// the slices over to the next formats, and a picked format folds the donut into a progress ring with a ✓ at the end.
// Timings and easing follow Filee's (OpenDuration 0.30 s, OpenStagger 12 ms, PopOut 7 px, EaseOutBack c1 0.8,
// critically damped springs, collapse 0.28 s + 20 ms stagger, ring 36 px / 6 px, hold 0.9 s / 1.4 s, fade 0.25 s).

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceSift.Core;

namespace SpaceSift.UI;

/// <summary>One slice: a format, "More" or "Back".</summary>
public sealed record DonutSlice(string Label, string Sub, bool Enabled, object? Tag, bool IsNav = false);

public sealed class DonutMenu : FrameworkElement
{
    // ---- geometry (Filee: DefaultOuterRadius 150, GapPixels 3, PopOut 7)
    public const double Outer = 150, Inner = 64, Gap = 3, PopOut = 7;
    private const double RingRadius = 36, RingStroke = 6;

    // ---- timing
    private const double OpenDuration = 0.30, OpenStagger = 0.012, MaxStagger = 0.072;
    private const double CloseDuration = 0.18;
    private const double SliceTurn = 0.12, FlipStagger = 0.08;
    private const double CollapseSeconds = 0.28, CollapseStagger = 0.02;
    private const double HoldSuccess = 0.9, HoldFailure = 1.4, FadeSeconds = 0.25;

    private List<DonutSlice> _slices = new();
    private List<DonutSlice>? _nextSlices;     // during a flip: what the slices turn into
    private Spring[] _hover = Array.Empty<Spring>();
    private int _active = -1;
    private bool _centerHot;

    private enum Phase { Opening, Open, Flipping, Closing, Collapsing, Progress, Done, Fading, Gone }
    private Phase _phase = Phase.Gone;
    private double _clock;                      // seconds in the current phase
    private TimeSpan _last;
    private bool _ticking;

    private double _progress;                   // 0..1 shown on the ring
    private double _shown;                      // eased value drawn
    private Spring _ring, _badge;
    private bool _ok;
    private string _centerTitle = "", _centerSub = "", _result = "";

    public Point Center { get; set; }

    /// <summary>Dim what is behind the ring (when it floats over the window). Off when it sits inside a page.</summary>
    public bool Dim { get; set; } = true;

    /// <summary>A format slice was clicked.</summary>
    public event Action<DonutSlice>? Picked;
    /// <summary>"More" / "Back" was clicked; the host answers with <see cref="FlipTo"/>.</summary>
    public event Action<DonutSlice>? Navigate;
    /// <summary>The donut finished closing (cancelled, or its result faded away).</summary>
    public event Action? Closed;

    public DonutMenu()
    {
        Focusable = true;
        IsHitTestVisible = true;
    }

    // ================================================================ public API

    public void Open(Point center, IEnumerable<DonutSlice> slices, string title, string sub)
    {
        Center = center;
        _slices = slices.ToList();
        _hover = new Spring[_slices.Count];
        _active = -1;
        _centerTitle = title; _centerSub = sub;
        Go(Phase.Opening);
        Focus();
    }

    /// <summary>Flips every slice over (staggered) to show a new set of slices.</summary>
    public void FlipTo(IEnumerable<DonutSlice> slices)
    {
        _nextSlices = slices.ToList();
        Go(Phase.Flipping);
    }

    /// <summary>Folds the slices into the centre and shows a progress ring.</summary>
    public void BeginProgress(string title)
    {
        _centerTitle = title;
        _progress = 0; _shown = 0;
        _ring = default; _badge = default;
        Go(Phase.Collapsing);
    }

    public void SetProgress(double f) => _progress = Math.Clamp(f, 0, 1);

    /// <summary>Completes the ring with a ✓ (or ! on failure), holds, then fades away.</summary>
    public void Finish(bool ok, string result)
    {
        _ok = ok; _result = result;
        _progress = 1;
        if (_phase is Phase.Collapsing or Phase.Progress) Go(Phase.Done);
    }

    public void Cancel()
    {
        if (_phase is Phase.Opening or Phase.Open or Phase.Flipping) Go(Phase.Closing);
    }

    public bool IsBusy => _phase is Phase.Collapsing or Phase.Progress or Phase.Done or Phase.Fading;

    // ================================================================ animation clock

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    /// <summary>Screen readers (and UI tests) read the state, centre and slices from the automation properties.</summary>
    private void Announce()
    {
        System.Windows.Automation.AutomationProperties.SetName(this, "Format ring");
        System.Windows.Automation.AutomationProperties.SetItemStatus(this,
            FormattableString.Invariant($"{_phase};{Center.X:0};{Center.Y:0};{_slices.Count}"));
        System.Windows.Automation.AutomationProperties.SetHelpText(this, string.Join(", ", _slices.Select(s => s.Label)));
    }

    private void Go(Phase p)
    {
        _phase = p;
        _clock = 0;
        Announce();
        if (!Motion.Enabled)
        {
            // reduced motion: jump straight to the end state of each phase
            if (p == Phase.Opening) _phase = Phase.Open;
            if (p == Phase.Flipping) { Swap(); _phase = Phase.Open; }
            if (p == Phase.Collapsing) _phase = Phase.Progress;
            Announce();
        }
        if (!_ticking) { _ticking = true; _last = TimeSpan.Zero; CompositionTarget.Rendering += Tick; }
        InvalidateVisual();
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        if (dt <= 0) return;
        _clock += dt;

        for (int i = 0; i < _hover.Length; i++) _hover[i].Step(i == _active && _slices[i].Enabled ? 1 : 0, dt);

        switch (_phase)
        {
            case Phase.Opening:
                if (_clock >= OpenDuration + Math.Min(MaxStagger, OpenStagger * _slices.Count)) { _phase = Phase.Open; Announce(); }
                break;
            case Phase.Flipping:
                // each slice swaps to its new face at its own half-turn (see OnRender); then the set is replaced
                if (_clock >= SliceTurn + FlipStagger) { Swap(); _phase = Phase.Open; Announce(); }
                break;
            case Phase.Closing:
                if (_clock >= CloseDuration) { Stop(); Closed?.Invoke(); return; }
                break;
            case Phase.Collapsing:
                _ring.Step(_clock > CollapseSeconds * 0.6 ? 1 : 0, dt);
                if (_clock >= CollapseSeconds + CollapseStagger * _slices.Count) { _phase = Phase.Progress; _clock = 0; Announce(); }
                break;
            case Phase.Progress:
                _ring.Step(1, dt);
                break;
            case Phase.Done:
                _ring.Step(1, dt);
                if (_shown > 0.995) _badge.Step(1, dt, 0.55);
                if (_clock >= (_ok ? HoldSuccess : HoldFailure) + 0.3) { _phase = Phase.Fading; _clock = 0; Announce(); }
                break;
            case Phase.Fading:
                if (_clock >= FadeSeconds) { Stop(); Closed?.Invoke(); return; }
                break;
        }
        // the ring eases toward the real progress instead of jumping
        _shown += (_progress - _shown) * Math.Min(1, dt * 9);
        if (_phase == Phase.Done && _progress >= 1) _shown = Math.Min(1, _shown + dt * 2.5);
        InvalidateVisual();
    }

    private void Swap()
    {
        if (_nextSlices == null) return;
        _slices = _nextSlices; _nextSlices = null;
        _hover = new Spring[_slices.Count];
        _active = -1;
    }

    private void Stop()
    {
        _phase = Phase.Gone;
        Announce();
        if (_ticking) { CompositionTarget.Rendering -= Tick; _ticking = false; }
        InvalidateVisual();
    }

    // ================================================================ drawing

    protected override void OnRender(DrawingContext dc)
    {
        // full-size layer: clicks outside the donut close it; a soft dim separates the ring from the page behind
        double dim = !Dim ? 0 : _phase switch
        {
            Phase.Gone => 0,
            Phase.Opening => Math.Clamp(_clock / 0.2, 0, 1),
            Phase.Closing => 1 - Math.Clamp(_clock / CloseDuration, 0, 1),
            Phase.Fading => 1 - Math.Clamp(_clock / FadeSeconds, 0, 1),
            _ => 1,
        };
        var scrim = ThemeManager.Brush("Scrim", Brushes.Black) as SolidColorBrush;
        var dimBrush = scrim == null ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb((byte)(scrim.Color.A * 0.55 * dim), scrim.Color.R, scrim.Color.G, scrim.Color.B));
        dc.DrawRectangle(dimBrush, null, new Rect(RenderSize));
        if (_phase == Phase.Gone) return;

        var surface = ThemeManager.Brush("Elevated", Brushes.White);
        var edge = new Pen(ThemeManager.Brush("LineStrong", Brushes.Gray), 1);
        var accent = ThemeManager.Brush("Accent", Brushes.Gold);
        var text = ThemeManager.Brush("Text", Brushes.Black);
        var muted = ThemeManager.Brush("Muted", Brushes.Gray);
        int n = Math.Max(1, _slices.Count);
        double span = 2 * Math.PI / n;

        double fade = _phase == Phase.Fading ? 1 - _clock / FadeSeconds : 1;
        dc.PushOpacity(Math.Clamp(fade, 0, 1));

        // ---------- slices
        if (_phase is Phase.Opening or Phase.Open or Phase.Flipping or Phase.Closing or Phase.Collapsing)
        {
            for (int i = 0; i < _slices.Count; i++)
            {
                double scale = 1, alpha = 1, turn = 1;
                bool showNext = false;
                switch (_phase)
                {
                    case Phase.Opening:
                    {
                        double t = Math.Clamp((_clock - Math.Min(i * OpenStagger, MaxStagger)) / OpenDuration, 0, 1);
                        scale = EaseOutBack(t); alpha = Math.Min(1, t * 2.5);
                        break;
                    }
                    case Phase.Closing:
                    {
                        double t = Math.Clamp(_clock / CloseDuration, 0, 1);
                        scale = 1 - EaseInCubic(t) * 0.6; alpha = 1 - t;
                        break;
                    }
                    case Phase.Flipping:
                    {
                        // each slice turns over in SliceTurn, later the farther it is from the top (Filee's flip)
                        double delay = FlipStagger * Math.Abs(Math.IEEERemainder(i * span, 2 * Math.PI)) / Math.PI;
                        double t = Math.Clamp((_clock - delay) / SliceTurn, 0, 1);
                        turn = Math.Abs(Math.Cos(Math.PI * t));
                        showNext = t >= 0.5;
                        break;
                    }
                    case Phase.Collapsing:
                    {
                        double t = Math.Clamp((_clock - i * CollapseStagger) / CollapseSeconds, 0, 1);
                        double c = EaseInCubic(t);
                        scale = 1 - c; alpha = 1 - c;
                        break;
                    }
                }
                var slice = showNext && _nextSlices != null && i < _nextSlices.Count ? _nextSlices[i] : _slices[i];
                if (showNext && _nextSlices != null && i >= _nextSlices.Count) continue;
                double h = i < _hover.Length ? _hover[i].Value : 0;
                DrawSlice(dc, slice, i, n, span, scale, alpha, turn, h, surface, edge, accent, text, muted);
            }
        }

        // ---------- centre plate
        if (_phase is Phase.Opening or Phase.Open or Phase.Flipping or Phase.Closing)
        {
            double t = _phase == Phase.Opening ? EaseOutBack(Math.Clamp(_clock / OpenDuration, 0, 1)) : _phase == Phase.Closing ? 1 - Math.Clamp(_clock / CloseDuration, 0, 1) : 1;
            double r = (Inner - 8) * t;
            dc.DrawEllipse(_centerHot ? ThemeManager.Brush("Hover", surface) : surface, edge, Center, r, r);
            if (t > 0.6)
            {
                if (_centerHot) DrawCentered(dc, "", Center.Y - 2, 18, text, icon: true);
                else
                {
                    DrawCentered(dc, _centerTitle, Center.Y - 9, 12.5, text, bold: true, maxWidth: r * 1.7);
                    DrawCentered(dc, _centerSub, Center.Y + 9, 10.5, muted, maxWidth: r * 1.7);
                }
            }
        }

        // ---------- progress ring
        if (_phase is Phase.Collapsing or Phase.Progress or Phase.Done or Phase.Fading)
        {
            double s = Math.Max(0, _ring.Value);
            if (_phase == Phase.Fading) s *= 1 - 0.15 * (_clock / FadeSeconds);
            double r = RingRadius * s;
            if (r > 1)
            {
                dc.DrawEllipse(surface, null, Center, r + RingStroke + 6, r + RingStroke + 6);
                dc.DrawEllipse(null, new Pen(ThemeManager.Brush("Track", Brushes.LightGray), RingStroke), Center, r, r);
                double sweep = Math.Clamp(_shown, 0, 1);
                var ringBrush = _phase is Phase.Done or Phase.Fading && !_ok ? ThemeManager.Brush("Danger", Brushes.IndianRed) : accent;
                if (sweep > 0.001) dc.DrawGeometry(null, new Pen(ringBrush, RingStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(r, sweep));
                double b = Math.Max(0, _badge.Value);
                if (_phase is Phase.Done or Phase.Fading && b > 0.01)
                {
                    // ✓ / ! badge pops in the middle: solid yellow circle, black glyph (like the app's popups)
                    double br = (r - RingStroke) * b;
                    dc.DrawEllipse(_ok ? accent : ThemeManager.Brush("Danger", Brushes.IndianRed), null, Center, br, br);
                    DrawCentered(dc, _ok ? "" : "", Center.Y, 20 * b, Brushes.Black, icon: true);
                }
                else DrawCentered(dc, $"{Math.Floor(sweep * 100):0}%", Center.Y, 13, text, bold: true);
                // caption under the ring
                string caption = _phase is Phase.Done or Phase.Fading ? _result : _centerTitle;
                DrawCaption(dc, caption, Center.Y + r + RingStroke + 22, surface, edge, text);
            }
        }
        dc.Pop();
    }

    private void DrawSlice(DrawingContext dc, DonutSlice slice, int i, int n, double span, double scale, double alpha, double turn,
                           double hover, Brush surface, Pen edge, Brush accent, Brush text, Brush muted)
    {
        if (scale <= 0.01 || alpha <= 0.01 || turn <= 0.01) return;
        double mid = i * span;
        double pop = PopOut * hover;
        // slice drawn pointing up (centred on 12 o'clock), then rotated into place
        double r0 = Inner * scale, r1 = (Outer + pop) * scale;
        var geo = Wedge(r0 + pop * scale, r1, span, Gap);
        dc.PushOpacity(alpha * (slice.Enabled ? 1 : 0.55));
        dc.PushTransform(new RotateTransform(mid * 180 / Math.PI, Center.X, Center.Y));
        dc.PushTransform(new ScaleTransform(turn, 1, Center.X, Center.Y));   // the flip turns each slice around its middle
        Brush fill = surface;
        if (hover > 0.01 && slice.Enabled) fill = Blend(surface, accent, hover);
        dc.DrawGeometry(fill, edge, geo);
        dc.Pop(); dc.Pop();

        // label stays upright, at the middle of the ring
        double lr = (r0 + r1) / 2 + pop * 0.3;
        var p = new Point(Center.X + lr * Math.Sin(mid), Center.Y - lr * Math.Cos(mid));
        var labelBrush = hover > 0.5 && slice.Enabled ? Brushes.Black : text;
        double maxW = Math.Min(92, 2 * lr * Math.Sin(span / 2) - 10) * turn;
        if (maxW > 8)
        {
            dc.PushTransform(new ScaleTransform(turn, 1, p.X, p.Y));
            double w = maxW / Math.Max(turn, 0.01);
            DrawAt(dc, slice.Label, p.X, p.Y - (slice.Sub.Length > 0 ? 7 : 0), slice.IsNav ? 12.5 : 14, labelBrush, bold: true, maxWidth: w);
            if (slice.Sub.Length > 0)
                DrawAt(dc, slice.Sub, p.X, p.Y + 10, 10, hover > 0.5 && slice.Enabled ? Brushes.Black : muted, maxWidth: w);
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>A ring slice centred on 12 o'clock with a constant pixel gap between neighbours.</summary>
    private Geometry Wedge(double r0, double r1, double span, double gap)
    {
        double a1 = span / 2 - Math.Asin(Math.Min(1, gap / 2 / Math.Max(r1, 1)));
        double a0 = span / 2 - Math.Asin(Math.Min(1, gap / 2 / Math.Max(r0, 1)));
        if (span >= 2 * Math.PI - 0.001) { a1 = Math.PI - 0.001; a0 = Math.PI - 0.001; }
        Point P(double r, double a) => new(Center.X + r * Math.Sin(a), Center.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(r1, -a1), true, true);
            c.ArcTo(P(r1, a1), new Size(r1, r1), 0, a1 * 2 > Math.PI, SweepDirection.Clockwise, true, true);
            c.LineTo(P(r0, a0), true, true);
            c.ArcTo(P(r0, -a0), new Size(r0, r0), 0, a0 * 2 > Math.PI, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private Geometry Arc(double r, double sweep)
    {
        if (sweep >= 0.9999) return new EllipseGeometry(Center, r, r);
        double a = sweep * 2 * Math.PI;
        Point P(double ang) => new(Center.X + r * Math.Sin(ang), Center.Y - r * Math.Cos(ang));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(0), false, false);
            c.ArcTo(P(a), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Text centred on the donut's middle line (x = Center.X).</summary>
    private void DrawCentered(DrawingContext dc, string s, double y, double size, Brush brush, bool bold = false, bool icon = false, double maxWidth = 0)
        => DrawAt(dc, s, Center.X, y, size, brush, bold, icon, maxWidth);

    /// <summary>One line of text centred on (x, y), trimmed with "…" to <paramref name="maxWidth"/>.</summary>
    private void DrawAt(DrawingContext dc, string s, double x, double y, double size, Brush brush, bool bold = false, bool icon = false, double maxWidth = 0)
    {
        if (string.IsNullOrEmpty(s) || size < 1) return;
        var family = (FontFamily)Application.Current.Resources[icon ? "IconFont" : "UiFont"];
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (maxWidth > 0 && ft.Width > maxWidth) ft.MaxTextWidth = maxWidth;
        double w = Math.Min(ft.Width, maxWidth > 0 ? maxWidth : ft.Width);
        dc.DrawText(ft, new Point(x - w / 2, y - ft.Height / 2));
    }

    private void DrawCaption(DrawingContext dc, string s, double y, Brush surface, Pen edge, Brush text)
    {
        if (string.IsNullOrEmpty(s)) return;
        var family = (FontFamily)Application.Current.Resources["UiFont"];
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12.5, text, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = 360, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        double w = Math.Min(ft.Width, 360);
        var box = new Rect(Center.X - w / 2 - 14, y - 4, w + 28, ft.Height + 12);
        dc.DrawRoundedRectangle(surface, edge, box, 12, 12);
        dc.DrawText(ft, new Point(Center.X - 180, y + 2));
    }

    private static Brush Blend(Brush a, Brush b, double t)
    {
        if (a is not SolidColorBrush sa || b is not SolidColorBrush sb) return t > 0.5 ? b : a;
        Color ca = sa.Color, cb = sb.Color;
        byte L(byte x, byte y) => (byte)(x + (y - x) * t);
        return new SolidColorBrush(Color.FromArgb(L(ca.A, cb.A), L(ca.R, cb.R), L(ca.G, cb.G), L(ca.B, cb.B)));
    }

    // Filee's easing
    private static double EaseOutBack(double t)
    {
        const double c1 = 0.8, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
    private static double EaseInCubic(double t) => t * t * t;

    /// <summary>Critically damped spring (Filee: CriticalDamping 36), stepped at most 1/120 s at a time.</summary>
    private struct Spring
    {
        public double Value, Velocity;
        public void Step(double target, double dt, double damping = 1.0)
        {
            const double c = 36, k = c * c / 4;
            while (dt > 0)
            {
                double h = Math.Min(dt, 1 / 120.0);
                double acc = -k * (Value - target) - c * damping * Velocity;
                Velocity += acc * h; Value += Velocity * h;
                dt -= h;
            }
        }
    }

    // ================================================================ input

    private (int index, bool center) HitTest(Point p)
    {
        double dx = p.X - Center.X, dy = p.Y - Center.Y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < Inner - 8) return (-1, true);
        if (d < Inner || d > Outer + PopOut || _slices.Count == 0) return (-1, false);
        double ang = Math.Atan2(dx, -dy); if (ang < 0) ang += 2 * Math.PI;
        double span = 2 * Math.PI / _slices.Count;
        return ((int)Math.Floor((ang + span / 2) / span) % _slices.Count, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_phase is not (Phase.Open or Phase.Opening)) return;
        var (i, c) = HitTest(e.GetPosition(this));
        if (i != _active || c != _centerHot)
        {
            _active = i; _centerHot = c;
            Cursor = i >= 0 && _slices[i].Enabled || c ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_phase is not (Phase.Open or Phase.Opening)) return;
        var (i, c) = HitTest(e.GetPosition(this));
        if (i >= 0)
        {
            var s = _slices[i];
            if (s.IsNav) Navigate?.Invoke(s);
            else Picked?.Invoke(s);   // a greyed slice (needs a download) is picked too: the host offers the download
        }
        else Cancel();               // the centre or outside: close
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { base.OnMouseRightButtonUp(e); Cancel(); e.Handled = true; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Tab && _phase == Phase.Open)
        {
            // Tab flips to the next formats, like Filee
            var nav = _slices.FirstOrDefault(s => s.IsNav);
            if (nav != null) Navigate?.Invoke(nav);
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_phase != Phase.Open) return;
        var nav = _slices.FirstOrDefault(s => s.IsNav);   // the wheel flips too, like Filee
        if (nav != null) Navigate?.Invoke(nav);
        e.Handled = true;
    }
}
