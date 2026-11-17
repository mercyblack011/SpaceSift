using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpaceSift.Core;
using SpaceSift.UI;
using ThemeMode = SpaceSift.Core.ThemeMode; // .NET 10 WPF has its own System.Windows.ThemeMode

namespace SpaceSift;

public partial class MainWindow : Window
{
    /// <summary>An entry of the drive list: a drive letter ("C:\") or a phone storage ("phone:…").</summary>
    private sealed record DriveItem(string Label, string Root, PhoneVolume? Phone = null) { public override string ToString() => Label; }
    private sealed record CategoryItem(string Label, FileCategory Value) { public override string ToString() => Label; }
    private sealed record Crumb(string Name, int Index, string Sep);

    private ScanResult? _scan;
    private long _driveFree;
    private ExtColors? _colors;
    private SizeView? _catView;   // category filter only (treemap)
    private SizeView? _view;      // category + extension filter (tree, file list)
    private FileCategory _category = FileCategory.All;
    private int _extFilter = -1;
    private bool _suppressExtEvents;

    private ObservableCollection<TreeRow> _rows = new();
    private readonly HashSet<int> _expanded = new();
    private string _treeSort = "Size";
    private bool _treeDesc = true;

    private string _fileSort = "Size";
    private bool _fileDesc = true;
    private bool _fileListDirty = true;
    private CancellationTokenSource? _fileCts;
    private readonly DispatcherTimer _fileDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private CancellationTokenSource? _scanCts;
    private int _lastTip = -1;

    private static readonly (string Label, long Min)[] SizeFilters =
    {
        ("Any size", 0), ("> 1 MB", 1L << 20), ("> 10 MB", 10L << 20), ("> 100 MB", 100L << 20), ("> 1 GB", 1L << 30),
    };
    private static readonly (string Label, int Days, bool Older)[] DateFilters =
    {
        ("Any time", 0, false), ("Last 7 days", 7, false), ("Last 30 days", 30, false),
        ("Older than 6 months", 182, true), ("Older than 1 year", 365, true), ("Older than 2 years", 730, true),
    };

    public MainWindow()
    {
        InitializeComponent();
        TreeList.ItemsSource = _rows;

        LoadLetterDrives(selectRoot: JunkScanner.SystemDrive);

        foreach (FileCategory c in Enum.GetValues<FileCategory>())
            CategoryBox.Items.Add(new CategoryItem(c == FileCategory.All ? "All files" : c.ToString(), c));
        CategoryBox.SelectedIndex = 0;
        foreach (var s in SizeFilters) SizeFilter.Items.Add(s.Label);
        SizeFilter.SelectedIndex = 0;
        foreach (var d in DateFilters) DateFilter.Items.Add(d.Label);
        DateFilter.SelectedIndex = 0;

        FastMode.IsChecked = true; // fast (MFT) scanning is the default; it asks for admin once when needed
        AboutVersion.Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · 64-bit · {(NativeMethods.IsAdministrator() ? "running as administrator" : "running as standard user")}";

        Treemap.ItemClicked += i => { RevealInTree(i); };
        Treemap.ItemDoubleClicked += Treemap_DoubleClicked;
        Treemap.HoverChanged += Treemap_Hover;
        Treemap.ContextMenuOpening += Treemap_ContextMenuOpening;
        ExtChart.SliceClicked += id => SelectExtension(id);
        _fileDebounce.Tick += (_, _) => { _fileDebounce.Stop(); RebuildFileList(); };

        Loaded += OnLoaded;
        NavList.SelectedIndex = 0;
        ThemeManager.Changed += OnThemeChanged;
        OnThemeChanged();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) { ThemeButton_Click(this, new RoutedEventArgs()); e.Handled = true; }
            // mockup: "/" jumps to file search
            else if ((e.Key == Key.OemQuestion || e.Key == Key.Divide) && Keyboard.FocusedElement is not TextBox)
            {
                Tabs.SelectedItem = TabFiles;
                Dispatcher.BeginInvoke(() => SearchBox.Focus(), DispatcherPriority.Input);
                e.Handled = true;
            }
        };
        Motion.FadeOnTextChange(StatusText);
        Motion.FadeOnTextChange(PageSub);
        UpdateSummary(((DriveItem?)DriveBox.SelectedItem)?.Root);
        DriveBox.SelectionChanged += (_, _) => { if (IsLoaded && !_rebuildingDrives) DriveBox_Changed(); };
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateEmptyStates(false);
        WireDonut();
        UpdateConvertList();
        EnableElevatedFileDrop();
        WatchDevices();
        // SpaceSift.exe --convert a.png b.jpg : open the Convert page with the format donut for those files
        {
            var a = Environment.GetCommandLineArgs();
            int c = Array.IndexOf(a, "--convert");
            if (c >= 0 && c + 1 < a.Length)
            {
                Tabs.SelectedItem = TabConvert;
                var files = a.Skip(c + 1).TakeWhile(x => !x.StartsWith("--")).ToList();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                OpenDonut(files);
            }
        }
        // relaunched elevated: SpaceSift.exe --scan C: --mft
        var args = Environment.GetCommandLineArgs();
        int k = Array.IndexOf(args, "--scan");
        if (k >= 0 && k + 1 < args.Length)
        {
            string root = NormalizeRoot(args[k + 1]);
            bool mft = args.Contains("--mft");
            FastMode.IsChecked = mft;
            for (int i = 0; i < DriveBox.Items.Count; i++)
                if (string.Equals(((DriveItem)DriveBox.Items[i]).Root, root, StringComparison.OrdinalIgnoreCase)) DriveBox.SelectedIndex = i;
            Tabs.SelectedItem = TabTree;
            await StartScan(root, mft);
        }
        StartCleanupPages();
    }

    private static string NormalizeRoot(string r)
    {
        r = r.Trim('"');
        if (r.Length == 2 && r[1] == ':') r += "\\";
        return r;
    }

    // =================================================================== scanning

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is DriveItem d) await StartScan(d.Root, FastMode.IsChecked == true);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task StartScan(string root, bool mft)
    {
        // a phone storage ("phone:…") is read through the Windows shell; fast mode is for NTFS drives only
        var phone = PhoneStorage.IsPhoneKey(root) ? _phones.FirstOrDefault(p => p.Key == root) : null;
        if (PhoneStorage.IsPhoneKey(root) && phone == null)
        {
            AppDialog.Alert(this, "The phone isn't connected anymore", "Connect it again in \"File transfer\" mode and unlock it, then press Refresh.", DialogKind.Warning);
            return;
        }
        if (phone != null) mft = false;
        bool isDriveRoot = phone == null && Path.GetPathRoot(root)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true;
        if (mft && (!isDriveRoot || !MftScanner.IsNtfs(root)))
        {
            StatusText.Text = "Fast mode needs an NTFS drive root; using normal scan.";
            mft = false;
        }
        if (mft && !NativeMethods.IsAdministrator())
        {
            int answer = AppDialog.Show(this, DialogKind.Question, "Scan in fast mode?",
                "Fast mode reads the drive's file table directly, so a full drive is mapped in seconds. " +
                "Windows only allows that for administrators, so SpaceSift restarts once with administrator rights.",
                new[] { "Cancel", "Normal scan", "Restart as admin" }, defaultButton: 2);
            if (answer <= 0) return;
            if (answer == 2)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--scan {root.TrimEnd('\\')} --mft")
                    { UseShellExecute = true, Verb = "runas" });
                    Application.Current.Shutdown();
                    return;
                }
                catch (Win32Exception)
                {
                    StatusText.Text = "Administrator rights declined; using normal scan.";
                }
            }
            mft = false;
        }

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var progress = new ScanProgress();
        SetBusy(true);
        TaskBegin($"Scanning {(phone != null ? phone.Device : root.TrimEnd('\\'))}");
        // real percentage: MFT records read, or bytes found vs. the drive's used space
        long usedBytes = phone != null ? phone.Total - phone.Free : 0;
        if (isDriveRoot && NativeMethods.GetDiskFreeSpaceExW(root, out _, out ulong tot, out ulong totFree)) usedBytes = (long)(tot - totFree);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var sw = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            long bytes = Interlocked.Read(ref progress.Bytes), items = Interlocked.Read(ref progress.Items);
            double f;
            if (progress.Phase == "Building tree") f = _taskShown + (0.99 - _taskShown) * 0.12;   // last few %: building the tree
            else if (progress.Fraction >= 0) f = progress.Fraction * 0.9;                          // MFT: records read
            else if (usedBytes > 0) f = Math.Min(0.9, (double)bytes / usedBytes * 0.9);             // folder scan of a drive
            else f = 0.9 * (1 - Math.Exp(-items / 120000.0));                                        // single folder: estimate
            TaskProgress(f, $"{progress.Phase}… {items:N0} items · {Fmt.Size(bytes)} · {sw.Elapsed.TotalSeconds:0.0}s");
        };
        timer.Start();

        ScanResult? result = null;
        try
        {
            if (mft)
            {
                try { result = await Task.Run(() => MftScanner.Scan(root, progress, ct)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    StatusText.Text = "MFT read failed (" + ex.Message + "); using normal scan.";
                    progress = new ScanProgress();
                    result = null;
                }
            }
            result ??= phone != null
                ? await Task.Run(() => PhoneStorage.Scan(phone, progress, ct))
                : await Task.Run(() => DirectoryScanner.Scan(root, progress, ct));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            timer.Stop();
            SetBusy(false);
        }

        if (result != null)
        {
            LoadResult(result);
            TaskEnd();
        }
        else TaskEnd(null, success: false);
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        DriveBox.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        UpdateEmptyStates(busy);
    }

    /// <summary>Disk Map / Files show a friendly "scan your drive" card until there is data.</summary>
    private void UpdateEmptyStates(bool busy)
    {
        bool empty = _scan == null;
        foreach (var (panel, title, button, idle) in new[]
        {
            (MapEmpty, MapEmptyTitle, MapEmptyButton, $"Map {ShortDrive}"),
            (FilesEmpty, FilesEmptyTitle, FilesEmptyButton, $"See every file on {ShortDrive}"),
        })
        {
            title.Text = busy ? $"Scanning {ShortDrive}…" : idle;
            button.IsEnabled = !busy;
            // hide the empty panels behind the card so the page reads as one clean message
            if (panel.Parent is Panel host)
                foreach (UIElement sibling in host.Children)
                    if (!ReferenceEquals(sibling, panel))
                    {
                        bool show = !empty;
                        if (show && sibling.Visibility != Visibility.Visible) { sibling.Visibility = Visibility.Visible; Motion.FadeIn(sibling, 420, 10, 120); }
                        else if (!show) sibling.Visibility = Visibility.Hidden;
                    }
            if (empty)
            {
                bool wasHidden = panel.Visibility != Visibility.Visible;
                panel.LayoutTransform = Transform.Identity;   // undo a previous fold-away
                panel.BeginAnimation(OpacityProperty, null);
                panel.Opacity = 1;
                panel.Visibility = Visibility.Visible;
                if (wasHidden) Motion.FadeIn(panel, 320, 8);
                continue;
            }
            if (panel.Visibility == Visibility.Visible)
                _ = Motion.Collapse(panel, 380); // scan finished: the card folds away to reveal the results
        }
    }

    /// <summary>Short confirmation shown in the status line.</summary>
    public void ShowToast(string message) => StatusText.Text = message;

    private void LoadResult(ScanResult r, bool fromCache = false)
    {
        string drive = r.Key ?? Path.GetPathRoot(r.RootPath) ?? r.RootPath;
        _scansByDrive[drive] = r;
        // a folder scanned on another partition switches the whole app to that partition
        if (!string.Equals(drive, SelectedDrive, StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < DriveBox.Items.Count; i++)
                if (string.Equals(((DriveItem)DriveBox.Items[i]).Root, drive, StringComparison.OrdinalIgnoreCase))
                {
                    _scan = r; // so DriveBox_Changed keeps it
                    DriveBox.SelectedIndex = i;
                    break;
                }
        }
        _scan = r;
        _colors = new ExtColors(r);
        _expanded.Clear();
        _expanded.Add(ScanResult.Root);
        _extFilter = -1;
        ClearExtButton.IsEnabled = false;
        Treemap.SetData(null, null, null, -1);
        Treemap.SetRoot(ScanResult.Root);
        UpdateSummary(r.RootPath);
        ref Entry root = ref r.Entries[ScanResult.Root];
        StatusText.Text = $"Scan complete in {r.Elapsed.TotalSeconds:0.00} seconds ({(r.UsedMft ? "MFT" : "folder scan")}) · {root.Files:N0} files · {root.Folders:N0} folders";
        SumScan.Text = $"Scanned: {Fmt.Size(root.Size)} in {r.RootPath}";
        RecomputeAll(rebuildExtPanel: true);
        UpdateEmptyStates(false);
        UpdatePageTitle();
    }

    private void UpdateSummary(string? root)
    {
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            ulong avail, total, totalFree;
            var phone = PhoneStorage.IsPhoneKey(root) ? _phones.FirstOrDefault(p => p.Key == root) : null;
            if (phone != null)
            {
                // a phone storage reports its size through the shell
                total = (ulong)phone.Total; avail = totalFree = (ulong)Math.Max(0, phone.Free);
            }
            else
            {
                string? drive = Path.GetPathRoot(root);
                if (drive == null || !NativeMethods.GetDiskFreeSpaceExW(drive, out avail, out total, out totalFree)) return;
            }
            ulong used = total - totalFree, reserved = totalFree - avail;
            Motion.CountBytes(SumTotal, (long)total);
            Motion.CountBytes(SumUsed, (long)used);
            SumUsedPct.Text = $"({(double)used / total * 100:0.0}%)";
            Motion.CountBytes(SumFree, (long)avail);
            SumFreePct.Text = $"({(double)avail / total * 100:0.0}%)";
            Motion.CountBytes(SumReserved, (long)reserved);
            _driveFree = (long)avail;
            double frac = (double)used / total;
            Gauge.AnimateTo(frac);
            Motion.Count(GaugePct, frac * 100, v => $"{v:0}%", 1100);
            if (_junk.Count > 0) UpdateJunkSummary();
        }
        catch { }
    }

    // =================================================================== filters

    private Func<ushort, bool>? CategoryFilter() => _scan == null ? null : Categories.Filter(_scan, _category);

    private void RecomputeAll(bool rebuildExtPanel)
    {
        if (_scan == null) return;
        var catF = CategoryFilter();
        _catView = SizeView.Create(_scan, catF);
        if (_extFilter >= 0)
        {
            ushort x = (ushort)_extFilter;
            _view = SizeView.Create(_scan, id => id == x);
        }
        else _view = _catView;

        if (rebuildExtPanel) RebuildExtPanel(catF);

        int tmRoot = Treemap.RootIndex;
        if (tmRoot < 0 || _scan.IsDeleted(tmRoot)) tmRoot = ScanResult.Root;
        Treemap.SetData(_scan, _catView, _colors, tmRoot);
        Treemap.SetHighlight(_extFilter);
        UpdateBreadcrumb();
        RebuildTree();
        _fileListDirty = true;
        if (Tabs.SelectedItem == TabFiles) RebuildFileList();
    }

    private void RebuildExtPanel(Func<ushort, bool>? catF)
    {
        if (_scan == null || _colors == null) return;
        var stats = ExtStats.Compute(_scan, catF);
        double total = stats.Sum(s => (double)s.Size);
        _suppressExtEvents = true;
        ExtList.ItemsSource = stats.Select(s => new ExtRow(s, total, _colors.Brushes[s.Id])).ToList();
        ExtList.SelectedItem = null;
        _suppressExtEvents = false;
        ExtChart.SelectedExt = _extFilter;
        ExtChart.SetData(stats, _colors);
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is not CategoryItem c) return;
        _category = c.Value;
        _extFilter = -1;
        ClearExtButton.IsEnabled = false;
        RecomputeAll(rebuildExtPanel: true);
    }

    private void ExtList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressExtEvents || ExtList.SelectedItem is not ExtRow r) return;
        SelectExtension(r.Id);
    }

    private void SelectExtension(int id)
    {
        if (_scan == null) return;
        _extFilter = id;
        ClearExtButton.IsEnabled = true;
        _suppressExtEvents = true;
        if (ExtList.ItemsSource is List<ExtRow> rows)
        {
            var row = rows.FirstOrDefault(x => x.Id == id);
            ExtList.SelectedItem = row;
            if (row != null) ExtList.ScrollIntoView(row);
        }
        _suppressExtEvents = false;
        ExtChart.SelectedExt = id;
        ExtChart.InvalidateVisual();
        RecomputeAll(rebuildExtPanel: false);
        StatusText.Text = $"Filtered to {_scan.ExtNames[id]} files. Press \"Clear filter\" to show everything.";
    }

    private void ClearExt_Click(object sender, RoutedEventArgs e)
    {
        _extFilter = -1;
        ClearExtButton.IsEnabled = false;
        _suppressExtEvents = true;
        ExtList.SelectedItem = null;
        _suppressExtEvents = false;
        ExtChart.SelectedExt = -1;
        ExtChart.InvalidateVisual();
        RecomputeAll(rebuildExtPanel: false);
    }

    private void ExtView_Changed(object sender, RoutedEventArgs e)
    {
        if (ExtChart == null || ExtList == null) return;
        bool chart = ViewPie.IsChecked == true || ViewDonut.IsChecked == true;
        ExtList.Visibility = chart ? Visibility.Collapsed : Visibility.Visible;
        ExtChart.Visibility = chart ? Visibility.Visible : Visibility.Collapsed;
        ExtChart.Donut = ViewDonut.IsChecked == true;
        if (chart) ExtChart.PlayReveal(); else Motion.FadeIn(ExtList, 260, 6);
        ExtChart.InvalidateVisual();
    }

    // =================================================================== tree view

    private Comparison<int> TreeComparer()
    {
        var s = _scan!; var v = _view!; var e = s.Entries;
        Comparison<int> c = _treeSort switch
        {
            "Name" => (a, b) => s.NameSpan(a).CompareTo(s.NameSpan(b), StringComparison.OrdinalIgnoreCase),
            "Allocated" => (a, b) => v.Allocated(a).CompareTo(v.Allocated(b)),
            "Items" => (a, b) => (v.Files(a) + e[a].Folders).CompareTo(v.Files(b) + e[b].Folders),
            "Files" => (a, b) => v.Files(a).CompareTo(v.Files(b)),
            "Folders" => (a, b) => e[a].Folders.CompareTo(e[b].Folders),
            "Modified" => (a, b) => e[a].Modified.CompareTo(e[b].Modified),
            "Attributes" => (a, b) => Fmt.Attr(e[a].Attributes).CompareTo(Fmt.Attr(e[b].Attributes)),
            _ => (a, b) => v.Size(a).CompareTo(v.Size(b)),
        };
        return _treeDesc ? (a, b) => c(b, a) : c;
    }

    private List<int> VisibleChildren(int i, Comparison<int> cmp)
    {
        var list = new List<int>();
        foreach (int c in _scan!.GetChildren(i)) if (_view!.Visible(c)) list.Add(c);
        list.Sort(cmp);
        return list;
    }

    private bool HasVisibleChild(int i)
    {
        if (!_scan!.IsDir(i)) return false;
        foreach (int c in _scan.GetChildren(i)) if (_view!.Visible(c)) return true;
        return false;
    }

    private TreeRow MakeRow(int i, int depth)
        => new(_scan!, _view!, i, depth, HasVisibleChild(i)) { IsExpanded = _expanded.Contains(i) && _scan!.IsDir(i) };

    private void AppendRows(List<TreeRow> into, int i, int depth, Comparison<int> cmp)
    {
        var row = MakeRow(i, depth);
        into.Add(row);
        if (row.IsExpanded && row.HasChildren)
            foreach (int c in VisibleChildren(i, cmp)) AppendRows(into, c, depth + 1, cmp);
    }

    private void RebuildTree(int selectIndex = -1)
    {
        if (_scan == null || _view == null) return;
        if (selectIndex < 0 && TreeList.SelectedItem is TreeRow sel) selectIndex = sel.Index;
        var list = new List<TreeRow>();
        AppendRows(list, ScanResult.Root, 0, TreeComparer());
        _rows = new ObservableCollection<TreeRow>(list);
        TreeList.ItemsSource = _rows;
        if (selectIndex >= 0)
        {
            var row = _rows.FirstOrDefault(r => r.Index == selectIndex);
            if (row != null) { TreeList.SelectedItem = row; TreeList.ScrollIntoView(row); }
        }
    }

    private void Toggle(TreeRow row)
    {
        if (!row.HasChildren) return;
        if (row.IsExpanded) Collapse(row); else Expand(row);
    }

    private void Expand(TreeRow row)
    {
        if (row.IsExpanded || !row.HasChildren) return;
        _expanded.Add(row.Index);
        row.IsExpanded = true;
        var add = new List<TreeRow>();
        var cmp = TreeComparer();
        foreach (int c in VisibleChildren(row.Index, cmp)) AppendRows(add, c, row.Depth + 1, cmp);
        int pos = _rows.IndexOf(row);
        if (add.Count > 400) { RebuildTree(row.Index); return; }
        for (int k = 0; k < add.Count; k++) _rows.Insert(pos + 1 + k, add[k]);
    }

    private void Collapse(TreeRow row)
    {
        if (!row.IsExpanded) return;
        _expanded.Remove(row.Index);
        row.IsExpanded = false;
        int pos = _rows.IndexOf(row);
        int end = pos + 1;
        while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
        if (end - pos - 1 > 400) { RebuildTree(row.Index); return; }
        for (int k = end - 1; k > pos; k--) _rows.RemoveAt(k);
    }

    private void Expander_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TreeRow row) { Toggle(row); e.Handled = true; }
    }

    private void TreeList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeList.SelectedItem is not TreeRow row) return;
        if (row.IsDir) Toggle(row);
        else if (_scan!.IsPhone) ShowPhoneItem(row.Index);
        else ShellOps.Open(_scan.GetPath(row.Index));
    }

    /// <summary>Phone storage: shows the item's folder in File Explorer (Windows opens phone files from there).</summary>
    private void ShowPhoneItem(int index)
    {
        if (_scan?.PhoneParsingPath is not { } storage) return;
        var names = new List<string>();
        for (int p = index; p > ScanResult.Root; p = _scan.Entries[p].Parent) names.Insert(0, _scan.GetName(p));
        try { PhoneStorage.ShowInExplorer(storage, names, lastIsFile: !_scan.IsDir(index)); }
        catch (Exception ex) { AppDialog.Alert(this, "Couldn't open the phone folder", ex.Message + "\n\nUnlock the phone and allow file access, then try again.", DialogKind.Warning); }
    }

    private void TreeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TreeList.SelectedItem is not TreeRow row || _scan == null) return;
        switch (e.Key)
        {
            case Key.Right: Expand(row); e.Handled = true; break;
            case Key.Left:
                if (row.IsExpanded) Collapse(row);
                else
                {
                    var parent = _rows.FirstOrDefault(r => r.Index == _scan.Entries[row.Index].Parent);
                    if (parent != null) { TreeList.SelectedItem = parent; TreeList.ScrollIntoView(parent); }
                }
                e.Handled = true; break;
            case Key.Enter: Toggle(row); e.Handled = true; break;
            case Key.Delete:
                Delete(TreeSelection(), permanent: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true; break;
        }
    }

    private void TreeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TreeList.SelectedItem is TreeRow row) Treemap.SetSelected(row.Index);
    }

    private void TreeHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;
        if (key == "Percent") key = "Size";
        if (_treeSort == key) _treeDesc = !_treeDesc;
        else { _treeSort = key; _treeDesc = key != "Name"; }
        RebuildTree();
    }

    private List<int> TreeSelection() => TreeList.SelectedItems.OfType<TreeRow>().Select(r => r.Index).ToList();

    /// <summary>Expands the path to an item, selects it in the tree and scrolls to it.</summary>
    private void RevealInTree(int index)
    {
        if (_scan == null) return;
        bool changed = false;
        for (int p = _scan.Entries[index].Parent; p >= 0; p = _scan.Entries[p].Parent)
            changed |= _expanded.Add(p);
        if (changed) RebuildTree(index);
        else
        {
            var row = _rows.FirstOrDefault(r => r.Index == index);
            if (row != null) { TreeList.SelectedItem = row; TreeList.ScrollIntoView(row); }
        }
        if (Tabs.SelectedItem != TabTree) Tabs.SelectedItem = TabTree;
    }

    // =================================================================== treemap

    private void Treemap_DoubleClicked(int index)
    {
        if (_scan == null) return;
        int target = _scan.IsDir(index) ? index : _scan.Entries[index].Parent;
        if (target < 0 || target == Treemap.RootIndex) return;
        Treemap.SetRoot(target);
        UpdateBreadcrumb();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_scan == null || Treemap.RootIndex <= ScanResult.Root) return;
        Treemap.SetRoot(_scan.Entries[Treemap.RootIndex].Parent);
        UpdateBreadcrumb();
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int idx) { Treemap.SetRoot(idx); UpdateBreadcrumb(); }
    }

    private void UpdateBreadcrumb()
    {
        if (_scan == null) { Breadcrumb.ItemsSource = null; return; }
        var chain = new List<int>();
        for (int p = Treemap.RootIndex; p >= 0; p = _scan.Entries[p].Parent) chain.Add(p);
        chain.Reverse();
        Breadcrumb.ItemsSource = chain.Select((i, k) =>
            new Crumb(i == ScanResult.Root ? _scan.RootPath.TrimEnd('\\') : _scan.GetName(i), i, k == 0 ? "" : "\\")).ToList();
        UpButton.IsEnabled = Treemap.RootIndex > ScanResult.Root;
    }

    private void Treemap_Hover(int index, Point p)
    {
        if (index < 0 || _scan == null || _catView == null) { TipPopup.IsOpen = false; _lastTip = -1; return; }
        if (index != _lastTip)
        {
            _lastTip = index;
            TipPath.Text = _scan.GetPath(index);
            TipSize.Text = _scan.IsDir(index)
                ? $"{Fmt.Size(_catView.Size(index))} · {_catView.Files(index):N0} files"
                : $"{Fmt.Size(_scan.Entries[index].Size)} · {_scan.ExtName(index)} · modified {Fmt.Date(_scan.Entries[index].Modified)}";
        }
        double x = p.X + 16, y = p.Y + 18;
        if (x > Treemap.ActualWidth - 320) x = Math.Max(0, p.X - 330);
        if (y > Treemap.ActualHeight - 50) y = p.Y - 50;
        TipPopup.HorizontalOffset = x;
        TipPopup.VerticalOffset = y;
        TipPopup.IsOpen = true;
    }

    private void Treemap_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        TipPopup.IsOpen = false;
        int hit = Treemap.HitTest(Mouse.GetPosition(Treemap));
        if (hit < 0 || !BuildMenu(Treemap.ContextMenu, new List<int> { hit }, fromTreemap: true)) e.Handled = true;
    }

    // =================================================================== file view

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        if (NavList.SelectedIndex != Tabs.SelectedIndex) NavList.SelectedIndex = Tabs.SelectedIndex;
        UpdatePageTitle();
        PlayPageTransition();
        if (Tabs.SelectedItem == TabFiles && _fileListDirty) RebuildFileList();
    }

    /// <summary>Every page says which partition it is showing.</summary>
    private void UpdatePageTitle()
    {
        string d = DriveName(SelectedDrive);
        (PageTitle.Text, PageSub.Text) = Tabs.SelectedIndex switch
        {
            0 => ("Clean Up", $"Junk on {d}, cleaned safely in one click"),
            1 => ("Apps", "Every program installed on this PC, and everything it leaves behind"),
            2 => ("Disk Map", _scan == null ? $"See exactly what fills {d}" : $"What fills {_scan.RootPath}"),
            3 => ("Files", _scan == null ? $"Every file on {d}" : $"Every file in {_scan.RootPath}, searchable and sortable"),
            4 => ("Organize", "Sort a messy folder into tidy folders by file type"),
            5 => ("Convert", "Change a file into another format, offline"),
            _ => ("About", "SpaceSift Disk Studio"),
        };
    }

    // =================================================================== navigation, theme, motion

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex >= 0 && Tabs.SelectedIndex != NavList.SelectedIndex) Tabs.SelectedIndex = NavList.SelectedIndex;
    }

    /// <summary>New page fades in and glides up 14px.</summary>
    private void PlayPageTransition()
    {
        if (!Motion.Enabled || Tabs.Template?.FindName("PART_SelectedContentHost", Tabs) is not FrameworkElement host) return;
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        // the template's transform is frozen (shared); give the host its own animatable one
        if (host.RenderTransform is not TranslateTransform tt || tt.IsFrozen) host.RenderTransform = tt = new TranslateTransform();
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        // spin the sun/moon while the palette swaps
        if (ThemeGlyph.RenderTransform is RotateTransform rt)
            rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
        ThemeManager.Toggle();
    }

    private bool _syncingTheme;
    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingTheme || (sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse(tag, out ThemeMode mode)) return;
        ThemeManager.Apply(mode, save: true);
    }

    private void OnThemeChanged()
    {
        ThemeGlyph.Text = ThemeManager.IsDark ? "" : "";   // show what you switch TO
        ThemeText.Text = ThemeManager.IsDark ? "Light mode" : "Dark mode";
        _syncingTheme = true;
        (ThemeManager.Mode switch { Core.ThemeMode.Light => ThemeLight, Core.ThemeMode.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        _syncingTheme = false;
        Treemap.Invalidate();
        ExtChart.InvalidateVisual();
        // soft crossfade so the palette swap doesn't flash
        if (Content is UIElement root)
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(380)));
    }

    private void ExternalLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url) ShellOps.Open(url);
    }

    private void FileFilter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded) return;
        _fileDebounce.Stop();
        _fileDebounce.Start();
    }

    private void FileHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;
        if (_fileSort == key) _fileDesc = !_fileDesc;
        else { _fileSort = key; _fileDesc = key is "Size" or "Allocated" or "Modified"; }
        RebuildFileList();
    }

    private async void RebuildFileList()
    {
        if (_scan == null) return;
        _fileListDirty = false;
        _fileCts?.Cancel();
        var cts = _fileCts = new CancellationTokenSource();
        var scan = _scan;
        var catF = CategoryFilter();
        int ext = _extFilter;
        string search = SearchBox.Text.Trim();
        long minSize = SizeFilters[Math.Max(0, SizeFilter.SelectedIndex)].Min;
        var df = DateFilters[Math.Max(0, DateFilter.SelectedIndex)];
        string sortKey = _fileSort; bool desc = _fileDesc;
        FileCount.Text = "Filtering…";

        int[] items;
        try
        {
            items = await Task.Run(() => FilterFiles(scan, catF, ext, search, minSize, df.Days, df.Older, sortKey, desc, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        if (scan != _scan) { _fileListDirty = true; return; }

        FileList.ItemsSource = new VirtualFileList(scan, items);
        long total = 0;
        foreach (int i in items) total += scan.Entries[i].Size;
        FileCount.Text = $"{items.Length:N0} files · {Fmt.Size(total)}";
    }

    private static int[] FilterFiles(ScanResult s, Func<ushort, bool>? catF, int ext, string search, long minSize,
                                     int days, bool older, string sortKey, bool desc, CancellationToken ct)
    {
        var e = s.Entries;
        var allowed = new bool[s.ExtNames.Length];
        for (int x = 0; x < allowed.Length; x++) allowed[x] = ext >= 0 ? x == ext : catF == null || catF((ushort)x);
        Regex? rx = null;
        if (search.IndexOfAny(new[] { '*', '?' }) >= 0)
            rx = new Regex("^" + Regex.Escape(search).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        long cutoff = days > 0 ? DateTime.UtcNow.AddDays(-days).Ticks : 0;

        var list = new List<int>(Math.Min(s.Count, 1 << 20));
        for (int i = 0; i < s.Count; i++)
        {
            if ((e[i].Flags & (EntryFlags.Directory | EntryFlags.Deleted)) != 0) continue;
            if (!allowed[e[i].Ext] || e[i].Size < minSize) continue;
            if (days > 0 && (older ? e[i].Modified >= cutoff : e[i].Modified < cutoff)) continue;
            if (search.Length > 0)
            {
                var name = s.NameSpan(i);
                if (rx != null ? !rx.IsMatch(name) : name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            list.Add(i);
        }
        var arr = list.ToArray();
        ct.ThrowIfCancellationRequested();

        Comparison<int> cmp;
        switch (sortKey)
        {
            case "Name": cmp = (a, b) => s.NameSpan(a).CompareTo(s.NameSpan(b), StringComparison.OrdinalIgnoreCase); break;
            case "Allocated": cmp = (a, b) => e[a].Allocated.CompareTo(e[b].Allocated); break;
            case "Ext": cmp = (a, b) => string.CompareOrdinal(s.ExtNames[e[a].Ext], s.ExtNames[e[b].Ext]); break;
            case "Modified": cmp = (a, b) => e[a].Modified.CompareTo(e[b].Modified); break;
            case "Attributes": cmp = (a, b) => e[a].Attributes.CompareTo(e[b].Attributes); break;
            case "Folder":
                var paths = new Dictionary<int, string>();
                string P(int i) { int p = e[i].Parent; if (!paths.TryGetValue(p, out var v)) paths[p] = v = s.GetPath(p); return v; }
                foreach (int i in arr) P(i);
                cmp = (a, b) => string.Compare(paths[e[a].Parent], paths[e[b].Parent], StringComparison.OrdinalIgnoreCase);
                break;
            default: cmp = (a, b) => e[a].Size.CompareTo(e[b].Size); break;
        }
        if (desc) { var c0 = cmp; cmp = (a, b) => c0(b, a); }
        Array.Sort(arr, cmp);
        return arr;
    }

    private List<int> FileSelection() => FileList.SelectedItems.OfType<FileRow>().Select(r => r.Index).ToList();

    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow r || _scan == null) return;
        if (_scan.IsPhone) ShowPhoneItem(r.Index); else ShellOps.Open(_scan.GetPath(r.Index));
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Delete(FileSelection(), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
    }

    // =================================================================== context menu & delete

    private void TreeList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!BuildMenu(TreeList.ContextMenu, TreeSelection(), fromTreemap: false)) e.Handled = true;
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!BuildMenu(FileList.ContextMenu, FileSelection(), fromTreemap: false)) e.Handled = true;
    }

    private static MenuItem Item(string header, string glyph, Action click, bool enabled = true)
    {
        // "eraser" = the Font Awesome eraser shape (Force delete); anything else is a Segoe Fluent Icons glyph
        object icon = glyph == "eraser"
            ? new System.Windows.Shapes.Path
            {
                Data = (Geometry)Application.Current.Resources["EraserIcon"], Stretch = Stretch.Uniform, Width = 14, Height = 12,
                Fill = (Brush)Application.Current.FindResource("Text"),
            }
            : new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"] };
        var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = icon };
        mi.Click += (_, _) => click();
        return mi;
    }

    private bool BuildMenu(ContextMenu menu, List<int> sel, bool fromTreemap)
    {
        menu.Items.Clear();
        if (_scan == null || sel.Count == 0) return false;
        var s = _scan;
        int first = sel[0];
        string firstPath = s.GetPath(first);
        bool single = sel.Count == 1;
        long size = TopLevel(sel).Sum(i => s.Entries[i].Size);

        if (s.IsPhone)
        {
            // phone storage is view-only: Windows offers browsing and copying for it, not normal deletion
            menu.Items.Add(Item("Show in Explorer", "", () => ShowPhoneItem(first), single));
            menu.Items.Add(Item(single ? "Copy path" : $"Copy {sel.Count} paths", "", () => ShellOps.CopyPath(sel.Select(s.GetPath))));
            if (single && s.IsDir(first))
            {
                menu.Items.Add(new Separator());
                if (fromTreemap) menu.Items.Add(Item("Find in tree", "", () => RevealInTree(first)));
                menu.Items.Add(Item("Zoom treemap here", "", () => { Treemap.SetRoot(first); UpdateBreadcrumb(); }));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Phone storage is view-only", "", () => { }, false));
            return true;
        }

        menu.Items.Add(Item("Open", "", () => ShellOps.Open(firstPath), single));
        menu.Items.Add(Item("Show in Explorer", "", () => ShellOps.ShowInExplorer(firstPath)));
        menu.Items.Add(Item(single ? "Copy path" : $"Copy {sel.Count} paths", "", () => ShellOps.CopyPath(sel.Select(s.GetPath))));
        menu.Items.Add(Item("Properties", "", () => ShellOps.Properties(new WindowInteropHelper(this).Handle, firstPath), single));
        if (single && s.IsDir(first))
        {
            menu.Items.Add(new Separator());
            if (fromTreemap) menu.Items.Add(Item("Find in tree", "", () => RevealInTree(first)));
            menu.Items.Add(Item("Zoom treemap here", "", () => { Treemap.SetRoot(first); UpdateBreadcrumb(); }));
            menu.Items.Add(Item("Organize this folder…", "", () => OpenOrganizer(firstPath), first != ScanResult.Root));
        }
        if (sel.All(i => !s.IsDir(i)))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(single ? "Convert…" : $"Convert {sel.Count} files…", "",
                () => ConvertFromMenu(sel.Select(s.GetPath).ToList())));
        }
        menu.Items.Add(new Separator());
        bool canDelete = !sel.Contains(ScanResult.Root);
        menu.Items.Add(Item($"Delete to Recycle Bin ({Fmt.Size(size)})", "", () => Delete(sel, false), canDelete));
        menu.Items.Add(Item("Delete permanently…", "", () => Delete(sel, true), canDelete));
        menu.Items.Add(Item("Force delete (close apps using it)…", "eraser",
            () => RunForceDelete(TopLevel(sel).Where(i => i != ScanResult.Root).Select(s.GetPath).ToList(),
                                 p => { int k = s.FindPath(p); return k >= 0 ? s.Entries[k].Size : 0; }), canDelete));
        return true;
    }

    /// <summary>Drops items whose parent folder is also selected.</summary>
    private List<int> TopLevel(List<int> sel)
    {
        var set = new HashSet<int>(sel);
        var result = new List<int>();
        foreach (int i in set)
        {
            bool nested = false;
            for (int p = _scan!.Entries[i].Parent; p >= 0 && !nested; p = _scan.Entries[p].Parent) nested = set.Contains(p);
            if (!nested) result.Add(i);
        }
        return result;
    }

    private void Delete(List<int> sel, bool permanent)
    {
        if (_scan == null || sel.Count == 0) return;
        var s = _scan;
        if (s.IsPhone)
        {
            AppDialog.Alert(this, "Phone storage is view-only",
                "Windows doesn't let apps delete files on a phone connected in File transfer mode the normal way. Right-click → Show in Explorer opens the folder, where you can delete them.",
                DialogKind.Info);
            return;
        }
        var items = TopLevel(sel).Where(i => i != ScanResult.Root).ToList();
        if (items.Count == 0) return;
        long total = items.Sum(i => s.Entries[i].Size);
        var paths = items.Select(s.GetPath).ToList();

        var lines = items.Take(30).Select(i => new DialogLine(s.GetPath(i), Fmt.Size(s.Entries[i].Size), s.IsDir(i) ? "" : "")).ToList();
        if (items.Count > 30) lines.Add(new DialogLine($"… and {items.Count - 30:N0} more"));
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool risky = paths.Any(p => p.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)
                                 || p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase));
        string warn = risky ? " Careful: this includes Windows or Program Files content, and deleting it can break Windows or installed apps." : "";
        string msg = permanent
            ? "The files are removed for good. This cannot be undone." + warn
            : "You can restore them from the Recycle Bin." + warn;
        if (!AppDialog.Confirm(this,
                permanent ? $"Permanently delete {items.Count:N0} item(s), {Fmt.Size(total)}?" : $"Move {items.Count:N0} item(s), {Fmt.Size(total)}, to the Recycle Bin?",
                msg, permanent ? "Delete permanently" : "Move to Recycle Bin",
                permanent || risky ? DialogKind.Danger : DialogKind.Question, lines, danger: permanent || risky))
            return;

        Mouse.OverrideCursor = Cursors.Wait;
        List<string> errors;
        try
        {
            if (permanent) errors = ShellOps.DeletePermanently(paths);
            else
            {
                errors = new List<string>();
                if (!NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, paths)) errors.Add("Some items were not moved to the Recycle Bin.");
            }
        }
        finally { Mouse.OverrideCursor = null; }

        int removed = 0; long freed = 0;
        for (int k = 0; k < items.Count; k++)
        {
            if (ShellOps.Exists(paths[k])) continue;
            freed += s.Entries[items[k]].Size;
            s.MarkDeleted(items[k]);
            removed++;
        }
        if (removed > 0) RecomputeAll(rebuildExtPanel: true);
        UpdateSummary(s.RootPath);
        StatusText.Text = $"Deleted {removed:N0} of {items.Count:N0} item(s), {Fmt.Size(freed)} {(permanent ? "freed" : "moved to the Recycle Bin")}.";
        if (errors.Count > 0)
        {
            int k = AppDialog.Show(this, DialogKind.Warning, "Some items could not be deleted",
                "They are in use or need administrator rights. Force delete closes the programs using them and tries again.",
                new[] { "Close", "Force delete" }, errors.Take(30).Select(e => new DialogLine(e, "", "")).ToList(), defaultButton: 0, dangerButton: 1);
            var left = paths.Where(ShellOps.Exists).ToList();
            if (k == 1 && left.Count > 0) RunForceDelete(left, p => { int x = s.FindPath(p); return x >= 0 ? s.Entries[x].Size : 0; });
        }
        else if (removed > 0 && removed == items.Count)
            ShowToast($"{removed:N0} item(s) deleted · {Fmt.Size(freed)} {(permanent ? "freed" : "moved to the Recycle Bin")}");
    }
}
