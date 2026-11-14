using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceSift.Core;

namespace SpaceSift.UI;

/// <summary>
/// Squarified treemap drawn with DrawingVisuals. Rectangles are batched into one
/// StreamGeometry per extension colour, so even 500k+ files take a handful of draw calls.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private readonly struct Laid
    {
        public readonly Rect R; public readonly int Index; public readonly bool IsDir;
        public Laid(Rect r, int i, bool d) { R = r; Index = i; IsDir = d; }
    }

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _main = new();
    private readonly DrawingVisual _overlay = new();
    private readonly List<Laid> _laid = new(100_000);

    private ScanResult? _scan;
    private SizeView? _view;
    private ExtColors? _colors;
    private int _root = -1;
    private int _highlightExt = -1;
    private int _selected = -1;
    private int _hover = -1;
    private bool _dirty = true;

    private static readonly Brush FolderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x47)));
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD5, 0xD7, 0xDB)));
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF3)));
    private static readonly Pen EdgePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 0.5));
    private static readonly Pen SelPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x00)), 3));
    private static readonly Pen SelPenInner = Freeze(new Pen(Brushes.Black, 1));
    private static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 1.5));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public event Action<int>? ItemClicked;
    public event Action<int>? ItemDoubleClicked;
    public event Action<int>? ItemRightClicked;
    public event Action<int, Point>? HoverChanged;

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _main, _overlay };
        RenderOptions.SetEdgeMode(_main, EdgeMode.Aliased);
        ClipToBounds = true;
        Focusable = true;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    public int RootIndex => _root;
    public int SelectedIndex => _selected;

    public void SetData(ScanResult? scan, SizeView? view, ExtColors? colors, int root)
    {
        bool fresh = !ReferenceEquals(scan, _scan) && scan != null;
        _scan = scan; _view = view; _colors = colors; _root = root;
        _hover = -1;
        Invalidate();
        if (fresh) PlayZoom(0.96);
    }

    public void SetRoot(int root)
    {
        if (root == _root) return;
        // zooming into a sub-folder grows in; going back up settles down from slightly larger
        bool deeper = false;
        if (_scan != null && _root >= 0)
            for (int p = root; p >= 0; p = _scan.Entries[p].Parent) if (p == _root) { deeper = true; break; }
        _root = root;
        Invalidate();
        PlayZoom(deeper ? 0.93 : 1.05);
    }

    private void PlayZoom(double fromScale)
    {
        if (!Motion.Enabled) return;
        var ease = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        RenderTransformOrigin = new Point(0.5, 0.5);
        if (RenderTransform is not ScaleTransform st || st.IsFrozen) RenderTransform = st = new ScaleTransform();
        var dur = TimeSpan.FromMilliseconds(380);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(fromScale, 1, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(fromScale, 1, dur) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }

    public void SetHighlight(int ext) { _highlightExt = ext; Invalidate(); }

    public void SetSelected(int index) { _selected = index; DrawOverlay(); }

    public void Invalidate() { _dirty = true; InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Invalidate();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // a transparent background so the whole area receives mouse input
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_dirty) { _dirty = false; Rebuild(); }
    }

    // ----------------------------------------------------------------- layout

    // ----------------------------------------------------------------- cushion rendering
    //
    // Each folder and file adds a parabolic "ridge" to a height field (van Wijk & van de Wetering,
    // "Cushion Treemaps"). Lighting that surface gives every file a soft pillow shape and makes the
    // folder nesting readable at a glance. Pixels are written straight into a bitmap, so even
    // hundreds of thousands of files render in a few milliseconds.

    private struct Surface { public double X1, X2, Y1, Y2; }

    // gentle cushions: parents add only a little slope, so big blocks stay bright and readable
    private const double RidgeHeight = 0.32, RidgeFalloff = 0.72;
    private static readonly double Lx, Ly, Lz;
    static TreemapControl()
    {
        // light from the top-left, mostly from the front
        double lx = -1, ly = -1.3, lz = 7, n = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        Lx = lx / n; Ly = ly / n; Lz = lz / n;
    }

    private int[] _px = Array.Empty<int>();
    private int _pw, _ph;
    private double _sx = 1, _sy = 1;
    private WriteableBitmap? _bmp;
    private uint _folderColor, _headerColor, _dimColor;
    private readonly List<(Rect r, string text, bool header, uint bg)> _labels = new();
    private readonly Typeface _labelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static uint Pack(Color c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    private static Color ColorOf(string key, Color fallback) => ThemeManager.Brush(key, new SolidColorBrush(fallback)) is SolidColorBrush b ? b.Color : fallback;

    private void Rebuild()
    {
        _laid.Clear();
        _labels.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        using var dc = _main.RenderOpen();
        dc.DrawRectangle(ThemeManager.Brush("Surface2", EmptyBrush), null, bounds);
        if (_scan == null || _view == null || _colors == null || _root < 0 || bounds.Width < 2 || bounds.Height < 2)
        {
            DrawOverlay();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _sx = dpi.DpiScaleX; _sy = dpi.DpiScaleY;
        _pw = Math.Max(1, (int)Math.Round(bounds.Width * _sx));
        _ph = Math.Max(1, (int)Math.Round(bounds.Height * _sy));
        if (_px.Length != _pw * _ph) _px = new int[_pw * _ph];

        // map palette (WizTree-like: dark folders with lighter title bars, files lit as cushions)
        var folder = ColorOf("MapFolder", Color.FromRgb(0x2A, 0x2C, 0x30));
        folder.A = 255;
        _folderColor = Pack(Blend(folder, Color.FromRgb(0x2A, 0x2C, 0x30), 0.5));
        _headerColor = Pack(Color.FromRgb(0x4A, 0x4D, 0x54));
        _dimColor = Pack(Color.FromRgb(0x5A, 0x5C, 0x61));
        Array.Fill(_px, unchecked((int)_folderColor));

        var full = new Rect(0, 0, _pw, _ph);
        var surf = new Surface();
        if (_scan.IsDir(_root)) LayoutDir(_root, full, surf, 0);
        else ShadeFile(_root, full, surf, 0);

        if (_bmp == null || _bmp.PixelWidth != _pw || _bmp.PixelHeight != _ph)
            _bmp = new WriteableBitmap(_pw, _ph, 96 * _sx, 96 * _sy, PixelFormats.Bgr32, null);
        _bmp.WritePixels(new Int32Rect(0, 0, _pw, _ph), _px, _pw * 4, 0);
        dc.DrawImage(_bmp, bounds);
        DrawLabels(dc, dpi.PixelsPerDip);
        DrawOverlay();
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static void AddRidge(ref double s1, ref double s2, double a, double b, double h)
    {
        double w = b - a;
        if (w <= 0) return;
        s1 += 4 * h * (b + a) / w;
        s2 -= 4 * h / w;
    }

    private Rect ToDip(Rect px) => new(px.X / _sx, px.Y / _sy, px.Width / _sx, px.Height / _sy);

    private void FillFlat(Rect r, uint color)
    {
        int x0 = Math.Max(0, (int)(r.Left + 0.5)), x1 = Math.Min(_pw, (int)(r.Right + 0.5));
        int y0 = Math.Max(0, (int)(r.Top + 0.5)), y1 = Math.Min(_ph, (int)(r.Bottom + 0.5));
        int c = unchecked((int)color);
        for (int y = y0; y < y1; y++) Array.Fill(_px, c, y * _pw + x0, Math.Max(0, x1 - x0));
    }

    private void ShadeFile(int index, Rect r, Surface s, int depth)
    {
        double h = RidgeHeight * Math.Pow(RidgeFalloff, depth);
        AddRidge(ref s.X1, ref s.X2, r.Left, r.Right, h);
        AddRidge(ref s.Y1, ref s.Y2, r.Top, r.Bottom, h);

        int ext = _scan!.Entries[index].Ext;
        bool dim = _highlightExt >= 0 && ext != _highlightExt;
        Color c = dim ? Color.FromRgb(0x5A, 0x5C, 0x61) : _colors!.Colors[ext];

        int x0 = Math.Max(0, (int)(r.Left + 0.5)), x1 = Math.Min(_pw, (int)(r.Right + 0.5));
        int y0 = Math.Max(0, (int)(r.Top + 0.5)), y1 = Math.Min(_ph, (int)(r.Bottom + 0.5));
        if (x1 <= x0) x1 = Math.Min(_pw, x0 + 1);
        if (y1 <= y0) y1 = Math.Min(_ph, y0 + 1);
        const double ambient = 0.38, diffuse = 0.78;
        for (int y = y0; y < y1; y++)
        {
            double ny = -(2 * s.Y2 * (y + 0.5) + s.Y1);
            int row = y * _pw;
            for (int x = x0; x < x1; x++)
            {
                double nx = -(2 * s.X2 * (x + 0.5) + s.X1);
                double cos = (nx * Lx + ny * Ly + Lz) / Math.Sqrt(nx * nx + ny * ny + 1);
                double i = ambient + diffuse * Math.Max(0, cos);
                int rr = (int)(c.R * i), gg = (int)(c.G * i), bb = (int)(c.B * i);
                if (rr > 255) rr = 255; if (gg > 255) gg = 255; if (bb > 255) bb = 255;
                _px[row + x] = unchecked((int)(0xFF000000u | ((uint)rr << 16) | ((uint)gg << 8) | (uint)bb));
            }
        }
        var dip = ToDip(r);
        _laid.Add(new Laid(dip, index, false));
        // big files get a name tag, like folders do
        if (dip.Width >= 70 && dip.Height >= 18 && _labels.Count < 2500)
            _labels.Add((dip, $"{_scan.GetName(index)} ({Fmt.Size(_scan.Entries[index].Size)})", false, Pack(Blend(c, Colors.White, 0.15))));
    }

    private void LayoutDir(int dir, Rect r, Surface s, int depth)
    {
        double h = RidgeHeight * Math.Pow(RidgeFalloff, depth);
        AddRidge(ref s.X1, ref s.X2, r.Left, r.Right, h);
        AddRidge(ref s.Y1, ref s.Y2, r.Top, r.Bottom, h);
        var dip = ToDip(r);
        _laid.Add(new Laid(dip, dir, true));
        FillFlat(r, _folderColor);

        // title bar "Name\ (size)" when there is room, like WizTree
        double headerH = Math.Round(15 * _sy);
        bool header = dip.Width >= 44 && dip.Height >= 30;
        double pad = r.Width > 6 * _sx && r.Height > 6 * _sy ? Math.Max(1, Math.Round(1 * _sx)) : 0;
        Rect inner;
        if (header)
        {
            var bar = new Rect(r.X, r.Y, r.Width, headerH);
            FillFlat(bar, _headerColor);
            string name = dir == ScanResult.Root ? _scan!.RootPath.TrimEnd('\\') + "\\" : _scan!.GetName(dir) + "\\";
            if (_labels.Count < 2500) _labels.Add((ToDip(bar), $"{name} ({Fmt.Size(_view!.Size(dir))})", true, _headerColor));
            inner = new Rect(r.X + pad, r.Y + headerH, Math.Max(0, r.Width - 2 * pad), Math.Max(0, r.Height - headerH - pad));
        }
        else inner = new Rect(r.X + pad, r.Y + pad, Math.Max(0, r.Width - 2 * pad), Math.Max(0, r.Height - 2 * pad));
        if (inner.Width < 1 || inner.Height < 1) return;

        var view = _view!;
        var kids = _scan.GetChildren(dir);
        var items = new List<(int idx, double size)>(kids.Length);
        double total = 0;
        foreach (int k in kids)
        {
            if (!view.Visible(k)) continue;
            long sz = view.Size(k);
            if (sz <= 0) continue;
            items.Add((k, sz));
            total += sz;
        }
        if (items.Count == 0 || total <= 0) return;
        if (view.IsFiltered) items.Sort((a, b) => b.size.CompareTo(a.size));

        // scale so that the folder's own size maps to the inner area
        double dirSize = Math.Max(total, view.Size(dir));
        double scale = inner.Width * inner.Height / dirSize;
        Squarify(items, scale, inner, s, depth);
    }

    private void DrawLabels(DrawingContext dc, double pixelsPerDip)
    {
        foreach (var (r, text, header, bg) in _labels)
        {
            // dark text on light cushions, light text on dark title bars
            double lum = (((bg >> 16) & 0xFF) * 0.299 + ((bg >> 8) & 0xFF) * 0.587 + (bg & 0xFF) * 0.114) / 255;
            var fg = header || lum < 0.55 ? Brushes.White : Brushes.Black;
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                       _labelFace, header ? 11 : 11, fg, pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, r.Width - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
            if (header) ft.SetFontWeight(FontWeights.Normal);
            dc.PushClip(new RectangleGeometry(r));
            dc.DrawText(ft, new Point(r.X + 3, r.Y + (header ? Math.Max(0, (r.Height - ft.Height) / 2) : 2)));
            dc.Pop();
        }
    }

    private void Squarify(List<(int idx, double size)> items, double scale, Rect r, Surface surf, int depth)
    {
        int i = 0, n = items.Count;
        while (i < n && r.Width >= 1 && r.Height >= 1)
        {
            double side = Math.Min(r.Width, r.Height);
            double first = items[i].size * scale;
            if (first < 1.0) break; // everything left is smaller than a pixel
            double rowSum = 0, worst = double.MaxValue;
            int j = i;
            while (j < n)
            {
                double a = items[j].size * scale;
                double sum = rowSum + a;
                double w = Worst(first, a, sum, side);
                if (j > i && w > worst) break;
                worst = w; rowSum = sum; j++;
            }
            double thick = rowSum / side;
            bool vertical = r.Width >= r.Height; // strip on the left
            double pos = vertical ? r.Y : r.X;
            for (int k = i; k < j; k++)
            {
                double len = items[k].size * scale / thick;
                Rect cell = vertical ? new Rect(r.X, pos, thick, len) : new Rect(pos, r.Y, len, thick);
                pos += len;
                if (cell.Width < 0.5 || cell.Height < 0.5) continue;
                int idx = items[k].idx;
                if (_scan!.IsDir(idx)) LayoutDir(idx, cell, surf, depth + 1);
                else ShadeFile(idx, cell, surf, depth + 1);
            }
            r = vertical
                ? new Rect(r.X + thick, r.Y, Math.Max(0, r.Width - thick), r.Height)
                : new Rect(r.X, r.Y + thick, r.Width, Math.Max(0, r.Height - thick));
            i = j;
        }
    }

    private static double Worst(double max, double min, double sum, double side)
    {
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    // ----------------------------------------------------------------- overlay & input

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (TryGetRect(_hover, out var h)) dc.DrawRectangle(null, HoverPen, h);
        if (TryGetRect(_selected, out var s))
        {
            dc.DrawRectangle(null, new Pen(ThemeManager.Brush("Accent", Brushes.Gold), 3), s);
            dc.DrawRectangle(null, SelPenInner, new Rect(s.X - 1.5, s.Y - 1.5, s.Width + 3, s.Height + 3));
        }
    }

    private bool TryGetRect(int index, out Rect r)
    {
        r = Rect.Empty;
        if (index < 0) return false;
        for (int k = 0; k < _laid.Count; k++)
            if (_laid[k].Index == index) { r = _laid[k].R; return true; }
        return false;
    }

    public int HitTest(Point p)
    {
        for (int k = _laid.Count - 1; k >= 0; k--)
            if (_laid[k].R.Contains(p)) return _laid[k].Index;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        int hit = HitTest(p);
        if (hit != _hover) { _hover = hit; DrawOverlay(); }
        HoverChanged?.Invoke(hit, p);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        DrawOverlay();
        HoverChanged?.Invoke(-1, default);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        if (e.ClickCount == 2) ItemDoubleClicked?.Invoke(hit);
        else { SetSelected(hit); ItemClicked?.Invoke(hit); }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        SetSelected(hit);
        ItemRightClicked?.Invoke(hit);
    }
}
