using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceSift.Core;
using SpaceSift.UI;

namespace SpaceSift;

/// <summary>Convert page and the format donut (Filee's conversion engine).</summary>
public partial class MainWindow
{
    /// <summary>One line in "Recent conversions".</summary>
    public sealed class ConvertRecentItem
    {
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public required bool Ok { get; init; }
        public string? Output { get; init; }
        public string Glyph => Ok ? "" : "";
        public Brush BadgeBrush => ThemeManager.Brush(Ok ? "Accent" : "Danger", Brushes.Gold);
        public Visibility OpenVisibility => Output != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private const int SlicesPerPage = 7;
    private readonly ObservableCollection<ConvertRecentItem> _convertRecent = new();
    private List<ConvertTarget> _donutTargets = new();
    private List<string> _donutFiles = new();
    private int _donutPage;
    private bool _donutWired;

    private void WireDonut()
    {
        if (_donutWired) return;
        _donutWired = true;
        ConvertRecent.ItemsSource = _convertRecent;
        Donut.Picked += Donut_Picked;
        Donut.Navigate += _ => { _donutPage = (_donutPage + 1) % PageCount(); Donut.FlipTo(PageSlices(_donutPage)); };
        Donut.Closed += () =>
        {
            Donut.Visibility = Visibility.Collapsed;
            ConvertDropContent.IsHitTestVisible = true;
            Motion.FadeTo(ConvertDropContent, 1, 260);   // the card's text and button come back
        };
        _ = Converter.InitAsync(); // load the engines in the background so the first donut opens fast
    }

    // ================================================================ opening the donut

    /// <summary>
    /// Opens the format ring for <paramref name="files"/> inside the Convert page's card: where the files were dropped
    /// (<paramref name="at"/>, in the card's coordinates) or in the middle of the card.
    /// </summary>
    private async void OpenDonut(IReadOnlyList<string> files, Point? at = null)
    {
        WireDonut();
        // the ring lives on the Convert page: go there first (right-click → Convert… comes from Files / Disk Map)
        if (Tabs.SelectedItem != TabConvert)
        {
            Tabs.SelectedItem = TabConvert;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        files = files.Where(File.Exists).ToList();
        if (files.Count == 0 || Donut.IsBusy) return;
        var unknown = files.Where(f => Converter.FormatOf(f) == null).ToList();
        if (unknown.Count == files.Count)
        {
            AppDialog.Alert(this, "Can't convert this file type",
                $"SpaceSift doesn't know how to convert {Path.GetExtension(files[0])} files.", DialogKind.Info, AppDialog.Paths(unknown));
            return;
        }
        files = files.Except(unknown).ToList();

        StatusText.Text = "Loading the converters…";
        Mouse.OverrideCursor = Cursors.AppStarting;
        List<ConvertTarget> targets;
        try
        {
            await Converter.InitAsync();
            var list = files.ToList();
            targets = await Task.Run(() => Converter.TargetsFor(list));
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Alert(this, "The converter couldn't start", ex.Message, DialogKind.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        StatusText.Text = "";

        if (targets.Count == 0)
        {
            AppDialog.Alert(this, "No formats available",
                files.Count > 1 ? "These files have no format they can all be converted to. Try them one type at a time." :
                                  $"{Path.GetFileName(files[0])} can't be converted to anything yet.", DialogKind.Info);
            return;
        }

        _donutFiles = files.ToList();
        _donutTargets = targets;
        _donutPage = 0;
        // keep the whole ring (and the caption under it) inside the card
        var card = (FrameworkElement)Donut.Parent;
        double r = DonutMenu.Outer + DonutMenu.PopOut + 12;
        var size = new Size(card.ActualWidth, card.ActualHeight);
        var want = at ?? new Point(size.Width / 2, size.Height / 2);
        var c = new Point(Math.Clamp(want.X, r, Math.Max(r, size.Width - r)), Math.Clamp(want.Y, r, Math.Max(r, size.Height - r - 30)));
        ConvertDropContent.IsHitTestVisible = false;
        Motion.FadeTo(ConvertDropContent, 0, 180);   // the card's text steps aside for the ring
        Donut.Visibility = Visibility.Visible;
        string title = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files";
        string sub = files.Count == 1 ? (Converter.FormatOf(files[0])?.DisplayName ?? "") : "convert to…";
        Donut.Open(c, PageSlices(0), title, sub);
    }

    private int PageCount() => Math.Max(1, (int)Math.Ceiling(_donutTargets.Count / (double)SlicesPerPage));

    /// <summary>A page of formats, plus "More" / "Back" when there are several pages (Filee's two-sided donut).</summary>
    private List<DonutSlice> PageSlices(int page)
    {
        var slices = _donutTargets.Skip(page * SlicesPerPage).Take(SlicesPerPage)
            .Select(t => new DonutSlice(t.Label, t.NeedsPackage == null ? "" : "↓ " + PackageName(t.NeedsPackage), t.NeedsPackage == null, t))
            .ToList();
        int pages = PageCount();
        if (pages > 1)
            slices.Add(new DonutSlice(page == pages - 1 ? "Back" : "More", $"{page + 1} / {pages}", true, null, IsNav: true));
        return slices;
    }

    private static string PackageName(string id) => id switch
    {
        "ffmpeg" => "FFmpeg",
        "libreoffice" => "LibreOffice",
        "ghostscript" => "Ghostscript",
        "pandoc" => "Pandoc",
        "calibre" => "calibre",
        "tesseract" => "Tesseract",
        "weasyprint" => "WeasyPrint",
        _ => id,
    };

    // ================================================================ converting

    private async void Donut_Picked(DonutSlice slice)
    {
        if (slice.Tag is not ConvertTarget target) return;
        if (target.NeedsPackage != null)
        {
            Donut.Cancel();
            await OfferEngine(target);
            return;
        }

        var files = _donutFiles.ToList();
        Donut.BeginProgress($"Converting to {target.Label}…");
        TaskBegin($"Converting to {target.Label}");
        var progress = new Progress<double>(f => { Donut.SetProgress(f); TaskProgress(f); });
        List<ConvertFileResult> results;
        try { results = await Converter.ConvertAsync(files, target.Id, progress); }
        catch (Exception ex) { results = files.Select(f => new ConvertFileResult(f, false, Array.Empty<string>(), ex.Message)).ToList(); }

        int ok = results.Count(r => r.Ok);
        string caption = ok == results.Count
            ? (results.Count == 1 && results[0].Outputs.Count > 0 ? $"Saved {Path.GetFileName(results[0].Outputs[0])}" : $"{ok} files converted to {target.Label}")
            : ok == 0 ? "Couldn't convert" : $"{ok} of {results.Count} converted";
        Donut.Finish(ok > 0, caption);
        TaskEnd(caption, ok > 0);

        foreach (var r in results)
            _convertRecent.Insert(0, new ConvertRecentItem
            {
                Ok = r.Ok,
                Title = $"{Path.GetFileName(r.Source)}  →  {target.Label}",
                Detail = r.Ok ? (r.Outputs.Count > 0 ? string.Join(", ", r.Outputs) : "Done") : (r.Error ?? "Failed"),
                Output = r.Outputs.FirstOrDefault(File.Exists),
            });
        while (_convertRecent.Count > 50) _convertRecent.RemoveAt(_convertRecent.Count - 1);
        UpdateConvertList();

        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count > 0)
        {
            await Task.Delay(1500); // let the ring show its "!" first
            AppDialog.Alert(this, ok == 0 ? "The file couldn't be converted" : "Some files couldn't be converted", "",
                DialogKind.Warning, failed.Select(f => new DialogLine(Path.GetFileName(f.Source), f.Error ?? "", "")).ToList());
        }
    }

    /// <summary>A format needs an optional engine (e.g. FFmpeg for video): offer the download, then convert.</summary>
    private async Task OfferEngine(ConvertTarget target)
    {
        var package = Converter.Package(target.NeedsPackage!);
        if (package == null) return;
        string name = PackageName(package.Id);
        if (!AppDialog.Confirm(this, $"Download {name}?",
                $"Converting to {target.Label} needs {name}, a free open-source tool. SpaceSift downloads it once " +
                $"({Fmt.Size(Converter.DownloadSize(package))}) from Filee's pinned, checksum-verified copy and keeps it in its own folder.",
                $"Download {name}", DialogKind.Question))
            return;
        TaskBegin($"Downloading {name}");
        try
        {
            await Converter.InstallAsync(package, new Progress<double>(f => TaskProgress(f * 0.98, $"Downloading {name}… {f * 100:0}%")), CancellationToken.None);
            TaskEnd($"{name} is ready.");
        }
        catch (Exception ex)
        {
            TaskEnd($"{name} download failed.", false);
            AppDialog.Alert(this, $"{name} couldn't be downloaded", ex.Message, DialogKind.Warning);
            return;
        }
        // reopen the ring for the same files, now with the format available
        OpenDonut(_donutFiles.ToList());
    }

    private void UpdateConvertList()
    {
        ConvertEmpty.Visibility = _convertRecent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertClearButton.Visibility = _convertRecent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================================================================ Convert page

    private void ConvertChoose_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose files to convert", Multiselect = true, CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        OpenDonut(dlg.FileNames);   // middle of the card
    }

    private void ConvertDrop_DragEnter(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (files && ConvertDropGlow.Opacity < 1)
        {
            Motion.FadeTo(ConvertDropGlow, 1, 160);
            if (ConvertBadge.RenderTransform is ScaleTransform st)
            {
                var a = Spring.Animation(null, 1.12, Spring.Bouncy);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, a); st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
        }
    }

    private void ConvertDrop_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void ResetDropZone()
    {
        Motion.FadeTo(ConvertDropGlow, 0, 200);
        if (ConvertBadge.RenderTransform is ScaleTransform st)
        {
            var a = Spring.Animation(null, 1);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a); st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    private void ConvertDrop_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // folders: their files (top level)
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p) : new[] { p }).ToList();
        OpenDonut(files, e.GetPosition((IInputElement)Donut.Parent));   // where the files were dropped
    }

    private void ConvertOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConvertRecentItem { Output: { } o }) ShellOps.Open(o);
    }

    private void ConvertShow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConvertRecentItem { Output: { } o }) ShellOps.ShowInExplorer(o);
    }

    private void ConvertClear_Click(object sender, RoutedEventArgs e)
    {
        _convertRecent.Clear();
        UpdateConvertList();
    }

    /// <summary>Right-click → Convert… in Files / Disk Map: switches to the Convert page and opens the ring there.</summary>
    private void ConvertFromMenu(List<string> paths) => OpenDonut(paths);
}
