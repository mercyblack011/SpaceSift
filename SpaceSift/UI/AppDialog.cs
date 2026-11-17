using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace SpaceSift.UI;

/// <summary>Erase = Force delete (the Font Awesome eraser on the badge).</summary>
public enum DialogKind { Info, Success, Warning, Danger, Question, Erase }

/// <summary>One line in a dialog's detail list: text on the left, optional value on the right.</summary>
public sealed record DialogLine(string Text, string Value = "", string Glyph = "");

/// <summary>
/// The app's own popup. It dims the main window, and a glass card springs in over it.
/// Every message, confirmation and result in SpaceSift goes through here instead of MessageBox.
/// </summary>
public sealed class AppDialog : Window
{
    private int _result = -1;
    private readonly Border _card;
    private readonly Border _scrim;

    private AppDialog(Window? owner, DialogKind kind, string title, string message, string? bigValue, string? bigCaption,
                      IReadOnlyList<DialogLine>? lines, string[] buttons, int defaultButton, int dangerButton)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = owner == null;
        ResizeMode = ResizeMode.NoResize;
        Title = title;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 12.5;
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;

        CoverOwner(this, owner);

        // ---------- card content
        var stack = new StackPanel();

        var accent = kind switch
        {
            DialogKind.Success => "Accent",
            DialogKind.Danger => "Accent", // theme yellow, not red: the app's highlight colour for important moments
            DialogKind.Warning => "Accent",
            _ => "Mark",
        };
        string glyph = kind switch
        {
            DialogKind.Success => "",  // check
            DialogKind.Danger => "",   // trash bin, in the theme's yellow
            DialogKind.Warning => "",  // warning
            DialogKind.Question => "", // help
            _ => "",                   // info
        };
        var badge = new Grid { Width = 52, Height = 52, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        var ring = new Ellipse { StrokeThickness = 2 };
        ring.SetResourceReference(Shape.StrokeProperty, accent);
        var fill = new Ellipse { Opacity = 0.14 };
        fill.SetResourceReference(Shape.FillProperty, accent);
        var icon = new TextBlock
        {
            Text = glyph, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.Resources["IconFont"], RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, accent);
        // every popup: solid yellow circle with a black icon (trash bin, check, warning, question, info)
        fill.SetResourceReference(Shape.FillProperty, "Accent");
        fill.Opacity = 1;
        ring.StrokeThickness = 0;
        icon.Foreground = Brushes.Black;
        // Force delete shows the eraser shape instead of a font glyph
        FrameworkElement badgeIcon = icon;
        if (kind == DialogKind.Erase)
            badgeIcon = new System.Windows.Shapes.Path
            {
                Data = (Geometry)Application.Current.Resources["EraserIcon"], Fill = Brushes.Black, Stretch = Stretch.Uniform,
                Width = 24, Height = 21, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
        badge.Children.Add(fill); badge.Children.Add(ring); badge.Children.Add(badgeIcon);
        stack.Children.Add(badge);

        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"],
        });

        TextBlock? big = null;
        if (!string.IsNullOrEmpty(bigValue))
        {
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            big = new TextBlock
            {
                Text = bigValue, FontSize = 44, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom,
                FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"],
            };
            big.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            row.Children.Add(big);
            if (!string.IsNullOrEmpty(bigCaption))
            {
                var cap = new TextBlock { Text = bigCaption, FontSize = 15, Margin = new Thickness(10, 0, 0, 9), VerticalAlignment = VerticalAlignment.Bottom };
                cap.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                row.Children.Add(cap);
            }
            stack.Children.Add(row);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 19 };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(msg);
        }

        if (lines is { Count: > 0 })
        {
            var list = new StackPanel();
            foreach (var l in lines)
            {
                var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                if (!string.IsNullOrEmpty(l.Glyph))
                {
                    var g = new TextBlock { Text = l.Glyph, Width = 24, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], VerticalAlignment = VerticalAlignment.Center };
                    g.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    DockPanel.SetDock(g, Dock.Left);
                    dp.Children.Add(g);
                }
                if (!string.IsNullOrEmpty(l.Value))
                {
                    var v = new TextBlock { Text = l.Value, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(v, Dock.Right);
                    dp.Children.Add(v);
                }
                dp.Children.Add(new TextBlock { Text = l.Text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = l.Text, VerticalAlignment = VerticalAlignment.Center });
                list.Children.Add(dp);
            }
            var box = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(1),
                Child = new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            box.SetResourceReference(Border.BackgroundProperty, "Surface2");
            box.SetResourceReference(Border.BorderBrushProperty, "Line");
            stack.Children.Add(box);
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int k = i;
            bool primary = i == defaultButton;
            var b = new Button
            {
                Content = new TextBlock { Text = buttons[i], FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
                Padding = new Thickness(18, 8, 18, 8), MinWidth = 92,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = primary,
            };
            if (i == dangerButton) b.SetResourceReference(StyleProperty, "DangerBtn");
            else if (primary) b.SetResourceReference(StyleProperty, "Primary");
            b.Click += (_, _) => Close(k);
            bar.Children.Add(b);
            if (primary) Loaded += (_, _) => b.Focus();
        }
        stack.Children.Add(bar);

        _card = new Border
        {
            Width = 500, CornerRadius = new CornerRadius(22), Padding = new Thickness(28, 26, 28, 22), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = stack, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            Effect = new DropShadowEffect { BlurRadius = 48, ShadowDepth = 14, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "Elevated");
        _card.SetResourceReference(Border.BorderBrushProperty, "GlassEdge");

        _scrim = new Border();
        _scrim.SetResourceReference(Border.BackgroundProperty, "Scrim");
        _scrim.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _scrim) Nudge(); };

        var root = new Grid();
        root.Children.Add(_scrim);
        root.Children.Add(_card);
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(-1); e.Handled = true; } };
        Loaded += (_, _) => PlayIn(badgeIcon, big);
    }

    // ------------------------------------------------------------------ motion

    private void PlayIn(FrameworkElement icon, TextBlock? big)
    {
        if (!Motion.Enabled) return;
        _scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var spring = new SpringEase { Damping = 0.78, Response = 0.38 };
        var st = (ScaleTransform)_card.RenderTransform;
        var grow = new DoubleAnimation(0.9, 1, TimeSpan.FromSeconds(SpringEase.SettleTime(0.78, 0.38))) { EasingFunction = spring };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        // the badge icon pops a beat later
        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(SpringEase.SettleTime(0.55, 0.4)))
        { EasingFunction = new SpringEase { Damping = 0.55, Response = 0.4 }, BeginTime = TimeSpan.FromMilliseconds(120) };
        var ist = (ScaleTransform)icon.RenderTransform;
        ist.ScaleX = ist.ScaleY = 0.3;
        ist.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ist.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        if (big != null) Motion.FadeIn(big, 420, 10, 160);
    }

    /// <summary>Clicking outside a modal card gives a small shake instead of closing it.</summary>
    private void Nudge()
    {
        if (!Motion.Enabled) return;
        var tt = new TranslateTransform();
        var tg = new TransformGroup();
        tg.Children.Add(_card.RenderTransform);
        tg.Children.Add(tt);
        var keep = _card.RenderTransform;
        _card.RenderTransform = tg;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        foreach (var (t, v) in new[] { (0, 0.0), (60, -9.0), (130, 8.0), (200, -5.0), (270, 3.0), (360, 0.0) })
            a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        a.Completed += (_, _) => _card.RenderTransform = keep;
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    private bool _closing;
    private void Close(int result)
    {
        if (_closing) return;
        _closing = true;
        _result = result;
        if (!Motion.Enabled) { Close(); return; }
        var d = TimeSpan.FromMilliseconds(150);
        var st = (_card.RenderTransform as ScaleTransform) ?? new ScaleTransform();
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, d));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, d));
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) => Close();
        _scrim.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Places a frameless popup over the owner's client area so the whole app dims behind it.</summary>
    public static void CoverOwner(Window w, Window? owner)
    {
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            var src = PresentationSource.FromVisual(owner);
            var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var p = m.Transform(owner.PointToScreen(new Point(0, 0)));
            w.Left = p.X; w.Top = p.Y;
            if (owner.Content is FrameworkElement fe) { w.Width = fe.ActualWidth; w.Height = fe.ActualHeight; }
            else { w.Width = owner.ActualWidth; w.Height = owner.ActualHeight; }
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Width = 720; w.Height = 720;
        }
    }

    // ------------------------------------------------------------------ API

    /// <summary>Shows the popup and returns the index of the button pressed, or -1 for Esc.</summary>
    public static int Show(Window? owner, DialogKind kind, string title, string message, string[] buttons,
                           IReadOnlyList<DialogLine>? lines = null, int defaultButton = 0, int dangerButton = -1,
                           string? bigValue = null, string? bigCaption = null)
    {
        owner ??= Application.Current?.MainWindow;
        var d = new AppDialog(owner, kind, title, message, bigValue, bigCaption, lines, buttons, defaultButton, dangerButton);
        d.ShowDialog();
        return d._result;
    }

    public static void Alert(Window? owner, string title, string message, DialogKind kind = DialogKind.Info, IReadOnlyList<DialogLine>? lines = null)
        => Show(owner, kind, title, message, new[] { "OK" }, lines);

    /// <summary>Two buttons: Cancel (left) and the action (right, default). True when the action is chosen.</summary>
    public static bool Confirm(Window? owner, string title, string message, string action, DialogKind kind = DialogKind.Question,
                               IReadOnlyList<DialogLine>? lines = null, bool danger = false, string cancel = "Cancel")
        => Show(owner, kind, title, message, new[] { cancel, action }, lines, defaultButton: danger ? 0 : 1, dangerButton: danger ? 1 : -1) == 1;

    /// <summary>A success card with a large number, e.g. "4.2 GB freed".</summary>
    public static void Result(Window? owner, string title, string bigValue, string bigCaption, string message, IReadOnlyList<DialogLine>? lines = null)
        => Show(owner, DialogKind.Success, title, message, new[] { "Done" }, lines, bigValue: bigValue, bigCaption: bigCaption);

    public static IReadOnlyList<DialogLine> Paths(IEnumerable<string> paths, int max = 40)
    {
        var all = paths.ToList();
        var lines = all.Take(max).Select(p => new DialogLine(p)).ToList();
        if (all.Count > max) lines.Add(new DialogLine($"… and {all.Count - max:N0} more"));
        return lines;
    }
}
