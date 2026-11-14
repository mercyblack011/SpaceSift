using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

    private void Rebuild()
    {
        _laid.Clear();
        using (var dc = _main.RenderOpen())
        {
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(ThemeManager.Brush("Surface2", EmptyBrush), null, bounds);
            if (_scan == null || _view == null || _colors == null || _root < 0 || bounds.Width < 2 || bounds.Height < 2)
            {
                DrawOverlay();
                return;
            }

            int extCount = _scan.ExtNames.Length;
            var geoms = new Dictionary<int, (StreamGeometry g, StreamGeometryContext c)>();
            var folderGeom = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var folderCtx = folderGeom.Open())
            {
                StreamGeometryContext Ctx(int key)
                {
                    if (!geoms.TryGetValue(key, out var t))
                    {
                        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
                        t = (g, g.Open());
                        geoms[key] = t;
                    }
                    return t.c;
                }

                if (_scan.IsDir(_root)) LayoutDir(_root, bounds, folderCtx, Ctx, 0);
                else AddRect(Ctx(FileKey(_root)), bounds, _root, false);

                foreach (var t in geoms.Values) t.c.Close();
            }
            folderGeom.Freeze();
            dc.DrawGeometry(ThemeManager.Brush("MapFolder", FolderBrush), null, folderGeom);
            foreach (var (key, t) in geoms)
            {
                t.g.Freeze();
                Brush b = key == -1 ? ThemeManager.Brush("MapDim", DimBrush) : _colors.Brushes[key];
                dc.DrawGeometry(b, EdgePen, t.g);
            }
            _ = extCount;
        }
        DrawOverlay();
    }

    private int FileKey(int i)
    {
        int ext = _scan!.Entries[i].Ext;
        if (_highlightExt >= 0 && ext != _highlightExt) return -1;
        return ext;
    }

    private void AddRect(StreamGeometryContext c, Rect r, int index, bool isDir)
    {
        c.BeginFigure(r.TopLeft, true, true);
        c.PolyLineTo(new[] { r.TopRight, r.BottomRight, r.BottomLeft }, false, false);
        _laid.Add(new Laid(r, index, isDir));
    }

    private void LayoutDir(int dir, Rect r, StreamGeometryContext folderCtx, Func<int, StreamGeometryContext> ctx, int depth)
    {
        AddRect(folderCtx, r, dir, true);
        // inset so nested folders get a visible frame
        double pad = r.Width > 12 && r.Height > 12 ? 1.5 : 0;
        var inner = new Rect(r.X + pad, r.Y + pad, Math.Max(0, r.Width - 2 * pad), Math.Max(0, r.Height - 2 * pad));
        if (inner.Width < 1 || inner.Height < 1) return;

        var view = _view!;
        var kids = _scan!.GetChildren(dir);
        var items = new List<(int idx, double size)>(kids.Length);
        double total = 0;
        foreach (int k in kids)
        {
            if (!view.Visible(k)) continue;
            long s = view.Size(k);
            if (s <= 0) continue;
            items.Add((k, s));
            total += s;
        }
        if (items.Count == 0 || total <= 0) return;
        if (view.IsFiltered) items.Sort((a, b) => b.size.CompareTo(a.size));

        // scale so that the folder's own size maps to the inner area
        double dirSize = Math.Max(total, view.Size(dir));
        double scale = inner.Width * inner.Height / dirSize;
        Squarify(items, scale, inner, folderCtx, ctx, depth);
    }

    private void Squarify(List<(int idx, double size)> items, double scale, Rect r,
                          StreamGeometryContext folderCtx, Func<int, StreamGeometryContext> ctx, int depth)
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
                if (_scan!.IsDir(idx)) LayoutDir(idx, cell, folderCtx, ctx, depth + 1);
                else AddRect(ctx(FileKey(idx)), cell, idx, false);
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
