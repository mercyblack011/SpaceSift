using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using SpaceSift.Core;
using SpaceSift.UI;

namespace SpaceSift;

/// <summary>Clean Up tab (junk finder) and Apps tab (uninstall + leftover folders).</summary>
public partial class MainWindow
{
    private List<JunkItem> _junk = new();
    private CancellationTokenSource? _junkCts;
    private List<AppInfo> _apps = new();
    private CancellationTokenSource? _appsCts;
    private string _appSort = "Size";
    private bool _appDesc = true;

    private void StartCleanupPages()
    {
        _ = ScanJunk();
        _ = LoadApps();
    }

    // =================================================================== Clean Up

    private async Task ScanJunk()
    {
        _junkCts?.Cancel();
        var cts = _junkCts = new CancellationTokenSource();
        string drive = SelectedDrive;
        foreach (var j in _junk) j.PropertyChanged -= Junk_PropertyChanged;
        _junk = JunkScanner.CreateItems(drive); // only junk that lives on the selected partition
        foreach (var j in _junk) j.PropertyChanged += Junk_PropertyChanged;
        JunkList.ItemsSource = _junk;
        Motion.FadeIn(JunkList, 320, 10);
        JunkScanButton.IsEnabled = false;
        JunkCleanButton.IsEnabled = false;
        JunkSummary.Text = $"Looking for files you don't need on {drive.TrimEnd('\\')}…";
        try
        {
            await Task.WhenAll(_junk.Select(async item =>
            {
                item.Status = "Measuring…";
                await Task.Run(() => JunkScanner.Measure(item, cts.Token), cts.Token);
                item.Status = "";
            }));
        }
        catch (OperationCanceledException) { return; }
        finally { if (cts == _junkCts) JunkScanButton.IsEnabled = true; }
        if (cts.IsCancellationRequested) return;
        UpdateJunkSummary();
    }

    private void Junk_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(JunkItem.IsChecked) or nameof(JunkItem.Size))) return;
        if (Dispatcher.CheckAccess()) UpdateJunkSummary();
        else Dispatcher.BeginInvoke(UpdateJunkSummary);
    }

    private void UpdateJunkSummary()
    {
        long found = _junk.Sum(j => j.Size);
        long selected = _junk.Where(j => j.IsChecked).Sum(j => j.Size);
        long safe = _junk.Where(j => j.Safety == Safety.Safe).Sum(j => j.Size);
        bool measuring = _junk.Any(j => j.Status == "Measuring…");
        // numbers count to their new value instead of jumping
        JunkHeadSuffix.Text = measuring ? "found so far…" : "of junk found";
        Motion.Count(JunkHeadline, found, v => Fmt.Size((long)v), 700);
        string d = SelectedDrive.TrimEnd('\\');
        bool system = string.Equals(SelectedDrive, JunkScanner.SystemDrive, StringComparison.OrdinalIgnoreCase);
        JunkSummary.Text = measuring
            ? $"Checking {d} for temp files, caches, the Recycle Bin and more."
            : _junk.Count(j => j.Size > 0) == 0
                ? system ? $"{d} is clean. Nothing to remove right now."
                         : $"{d} is clean. Windows keeps its temp files and caches on {JunkScanner.SystemDrive.TrimEnd('\\')}, so on {d} only its Recycle Bin and setup leftovers are checked."
                : $"{_junk.Count(j => j.Size > 0)} places on {d} hold files you don't need. Safe items are already ticked.";
        Motion.CountBytes(StatSafe, safe, 700);
        Motion.CountBytes(StatSelected, selected, 450);
        if (_driveFree > 0)
        {
            Motion.CountBytes(StatAfter, _driveFree + selected, 450);
            StatAfterSub.Text = $"now {Fmt.Size(_driveFree)}  (+{Fmt.Size(selected)})";
        }
        JunkCleanText.Text = selected > 0 ? $"Clean {Fmt.Size(selected)}" : "Clean selected";
        JunkCleanButton.IsEnabled = selected > 0 && JunkScanButton.IsEnabled;
    }

    private async void JunkScan_Click(object sender, RoutedEventArgs e) => await ScanJunk();

    private void JunkOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not JunkItem item) return;
        if (item.IsRecycleBin) { ShellOps.Open("shell:RecycleBinFolder"); return; }
        string? target = item.Folders.FirstOrDefault(Directory.Exists) ?? item.Files.FirstOrDefault(File.Exists);
        if (target != null) ShellOps.Open(target);
    }

    private async void JunkClean_Click(object sender, RoutedEventArgs e)
    {
        var items = _junk.Where(j => j.IsChecked && j.Size > 0).ToList();
        if (items.Count == 0) return;
        long total = items.Sum(j => j.Size);
        bool review = items.Any(i => i.Safety == Safety.Review);
        var lines = items.Select(i => new DialogLine(i.Name + (i.Safety == Safety.Review ? "  · check first" : ""), i.SizeText, i.Glyph)).ToList();
        string msg = "These files are deleted permanently (not moved to the Recycle Bin). Files that are in use are skipped."
                     + (review ? " Some items are marked \"check first\", so make sure you don't need them." : "");
        if (!AppDialog.Confirm(this, $"Clean {Fmt.Size(total)} of junk?", msg, $"Clean {Fmt.Size(total)}",
                review ? DialogKind.Warning : DialogKind.Question, lines))
            return;

        JunkCleanButton.IsEnabled = false;
        JunkScanButton.IsEnabled = false;
        long freedTotal = 0;
        var ct = CancellationToken.None;
        var results = new List<DialogLine>();
        TaskBegin("Cleaning");
        for (int n = 0; n < items.Count; n++)
        {
            var item = items[n];
            item.Status = "Cleaning…";
            TaskProgress((double)n / items.Count, $"Cleaning {item.Name}…");
            long freed = await Task.Run(() => JunkScanner.Clean(item, ct));
            freedTotal += freed;
            await Task.Run(() => JunkScanner.Measure(item, ct));
            item.Status = item.Size > 0
                ? $"Freed {Fmt.Size(freed)}. {item.SizeText} left (in use or needs admin)."
                : $"Freed {Fmt.Size(freed)}.";
            results.Add(new DialogLine(item.Name, item.Size > 0 ? $"{Fmt.Size(freed)} · {item.SizeText} in use" : Fmt.Size(freed), item.Glyph));
            item.IsChecked = false;
            UpdateSummary(SelectedDrive); // gauge + numbers move live
            // a fully cleaned card shows its result briefly, then folds away
            if (item.Size == 0 && JunkList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement card)
                _ = Task.Delay(1200).ContinueWith(_ => Dispatcher.Invoke(() => Motion.Collapse(card)));
        }
        JunkScanButton.IsEnabled = true;
        UpdateJunkSummary();
        UpdateSummary(SelectedDrive);
        TaskEnd($"Clean up finished: {Fmt.Size(freedTotal)} freed on {SelectedDrive.TrimEnd('\\')}.");
        long left = items.Sum(i => i.Size);
        AppDialog.Result(this, "Clean up complete", Fmt.Size(freedTotal), "freed",
            left > 0
                ? $"{Fmt.Size(left)} was skipped because it's in use or needs administrator rights. Close open apps (or use Fast mode, which runs as admin) and clean again to get it."
                : $"{SelectedDrive.TrimEnd('\\')} now has {Fmt.Size(_driveFree)} free.",
            results);
    }

    // =================================================================== Apps

    private async Task LoadApps()
    {
        _appsCts?.Cancel();
        var cts = _appsCts = new CancellationTokenSource();
        AppsSummary.Text = "Loading installed programs…";
        _apps = await Task.Run(InstalledApps.Load);
        ShowApps();
        // icons: extracted off the UI thread, frozen, then assigned on the UI thread
        var forIcons = _apps.ToList();
        var iconThread = new Thread(() => // shell icon APIs want an STA thread
        {
            foreach (var app in forIcons)
            {
                if (cts.IsCancellationRequested) return;
                System.Windows.Media.ImageSource? icon = null;
                try { icon = InstalledApps.LoadIcon(app); } catch { }
                if (icon != null) Dispatcher.BeginInvoke(() => app.Icon = icon);
            }
        }) { IsBackground = true };
        iconThread.SetApartmentState(ApartmentState.STA);
        iconThread.Start();
        // measure folders for apps that don't report a size
        try
        {
            await Task.Run(() => InstalledApps.MeasureMissing(_apps.ToList(),
                (app, size) => Dispatcher.BeginInvoke(() => app.Size = size), cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        ShowApps();
    }

    private void AppSort_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        _appSort = key;
        _appDesc = key is "Size" or "Date";
        if (AppList != null && _apps.Count > 0) ShowApps();
    }

    private void ShowApps()
    {
        long maxSize = _apps.Count > 0 ? Math.Max(1, _apps.Max(a => a.Size)) : 1;
        foreach (var a in _apps) a.Fraction = (double)a.Size / maxSize;
        string q = AppSearch.Text.Trim();
        // always every installed program, whichever partition is picked
        IEnumerable<AppInfo> list = _apps;
        if (q.Length > 0)
            list = list.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase));
        Func<AppInfo, object> key = _appSort switch
        {
            "Name" => a => a.Name,
            "Publisher" => a => a.Publisher,
            "Date" => a => a.InstallDate ?? DateTime.MinValue,
            "Version" => a => a.Version,
            "Folder" => a => a.InstallLocation,
            _ => a => a.Size,
        };
        var sorted = (_appDesc ? list.OrderByDescending(key) : list.OrderBy(key)).ToList();
        var selected = AppList.SelectedItem;
        AppList.ItemsSource = sorted;
        if (selected != null && sorted.Contains(selected)) AppList.SelectedItem = selected;
        AppsSummary.Text = $"{sorted.Count:N0} programs installed · {Fmt.Size(sorted.Sum(a => a.Size))}";
    }

    private void AppSearch_Changed(object sender, TextChangedEventArgs e) => ShowApps();

    private void AppHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;
        if (_appSort == key) _appDesc = !_appDesc;
        else { _appSort = key; _appDesc = key is "Size" or "Date"; }
        ShowApps();
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var a = AppList.SelectedItem as AppInfo;
        UninstallButton.IsEnabled = a?.CanUninstall == true;
        AppFolderButton.IsEnabled = a?.HasFolder == true;
        if (a != null) _ = ShowRelated(a);
    }

    // ---------------- files connected to the selected app

    private CancellationTokenSource? _relatedCts;

    private async Task ShowRelated(AppInfo app)
    {
        _relatedCts?.Cancel();
        var cts = _relatedCts = new CancellationTokenSource();
        AppBottomTabs.SelectedItem = TabAppFiles;
        RelatedIcon.Source = app.Icon;
        RelatedTitle.Text = app.Name;
        RelatedSummary.Text = "Looking for this app's files…";
        RelatedList.ItemsSource = null;
        List<RelatedItem> items;
        try { items = await Task.Run(() => InstalledApps.FindRelated(app, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        RelatedList.ItemsSource = items;
        var disk = items.Where(i => !i.IsRegistry).ToList();
        RelatedSummary.Text = disk.Count == 0
            ? "No folders found on disk (it may be installed somewhere unusual)."
            : $"{disk.Count} location(s) on disk, {Fmt.Size(disk.Sum(i => i.Size))} in total. Found by name, so check before deleting.";
    }

    private List<RelatedItem> RelatedSelection() => RelatedList.SelectedItems.OfType<RelatedItem>().ToList();

    private void RelatedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = RelatedSelection();
        bool single = sel.Count == 1 && !sel[0].IsRegistry;
        RelatedOpenButton.IsEnabled = single;
        RelatedExplorerButton.IsEnabled = single;
        RelatedDeleteButton.IsEnabled = sel.Count > 0 && sel.All(i => i.CanDelete);
    }

    private void RelatedList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RelatedList.SelectedItem is RelatedItem { IsRegistry: false } r) ShellOps.Open(r.Path);
    }

    private void RelatedOpen_Click(object sender, RoutedEventArgs e)
    {
        if (RelatedList.SelectedItem is RelatedItem { IsRegistry: false } r) ShellOps.Open(r.Path);
    }

    private void RelatedExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (RelatedList.SelectedItem is RelatedItem { IsRegistry: false } r) ShellOps.ShowInExplorer(r.Path);
    }

    private async void RelatedDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = RelatedSelection().Where(i => i.CanDelete).ToList();
        if (sel.Count == 0 || AppList.SelectedItem is not AppInfo app) return;
        bool stillInstalled = _apps.Contains(app);
        long total = sel.Sum(i => i.Size);
        var lines = sel.Select(i => new DialogLine(i.Path, i.SizeText, "")).ToList();
        string warn = stillInstalled && sel.Any(i => i.Kind == "Install folder")
            ? "Careful: this deletes the program files of an app that is still installed. Use Uninstall instead unless you know what you are doing."
            : stillInstalled ? "The app is still installed. Deleting its data may reset its settings or sign you out." : "";
        if (!AppDialog.Confirm(this, $"Move {sel.Count} item(s), {Fmt.Size(total)}, to the Recycle Bin?",
                (warn.Length > 0 ? warn + " " : "") + "You can restore them from the Recycle Bin.", "Move to Recycle Bin",
                warn.Length > 0 ? DialogKind.Warning : DialogKind.Question, lines))
            return;
        NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, sel.Select(i => i.Path));
        int removed = sel.Count(i => !ShellOps.Exists(i.Path));
        StatusText.Text = $"Moved {removed} of {sel.Count} item(s) to the Recycle Bin.";
        if (removed > 0) ShowToast($"{removed} item(s) moved to the Recycle Bin");
        UpdateSummary(SelectedDrive);
        await ShowRelated(app);
    }

    private void AppList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem is AppInfo a && a.HasFolder) ShellOps.Open(a.InstallLocation);
    }

    private void AppFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is AppInfo a && a.HasFolder) ShellOps.Open(a.InstallLocation);
    }

    private async void AppsRefresh_Click(object sender, RoutedEventArgs e) => await LoadApps();

    private void StoreApps_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch { }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppInfo app || !app.CanUninstall) return;

        // find connected folders first so the dialog can list them
        UninstallButton.IsEnabled = false;
        TaskBegin($"Uninstall {app.Name}", $"Finding folders connected to {app.Name}…");
        TaskCreep(0.1, 1.5);
        List<RelatedItem> related;
        try { related = await Task.Run(() => InstalledApps.FindRelated(app, CancellationToken.None)); }
        catch { related = new List<RelatedItem>(); }
        StopCreep();
        TaskProgress(0.1, "Choose how to uninstall…");

        var dlg = new UninstallDialog(app, related) { Owner = this };
        if (dlg.ShowDialog() != true) { UninstallButton.IsEnabled = true; TaskEnd("Uninstall cancelled.", false); return; }
        bool removeConnected = dlg.RemoveConnected;
        var toRemove = removeConnected ? dlg.SelectedItems : new List<RelatedItem>();

        // step 1: the program's own uninstaller (10% → 70%; its progress is unknown, so the bar eases toward 70%)
        TaskProgress(0.12, $"Running the {app.Name} uninstaller. Follow its window if it asks anything…");
        TaskCreep(0.7, 25);
        try
        {
            await InstalledApps.UninstallAsync(app);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            UninstallButton.IsEnabled = true; TaskEnd("Uninstall cancelled.", false); return;
        }
        catch (Exception ex)
        {
            TaskEnd("Could not start the uninstaller.", false);
            AppDialog.Alert(this, "Could not start the uninstaller", $"{ex.Message}\n\nTry Windows Settings › Apps instead.", DialogKind.Warning);
            UninstallButton.IsEnabled = true;
            return;
        }
        StopCreep();

        // step 2: check it is really gone (70% → 80%)
        TaskProgress(0.72, "Checking the program was removed…");
        await LoadApps();
        bool stillThere = _apps.Any(a => a.RegistryKey == app.RegistryKey);
        TaskProgress(0.8);

        if (stillThere)
        {
            TaskEnd($"{app.Name} is still installed.", false);
            AppDialog.Alert(this, $"{app.Name} is still installed",
                "The uninstaller was cancelled or failed" + (removeConnected ? ", so its folders were not touched." : "."), DialogKind.Warning);
        }
        else if (removeConnected)
        {
            // step 3: connected folders to the Recycle Bin (80% → 100%)
            var existing = toRemove.Where(i => ShellOps.Exists(i.Path)).ToList();
            TaskProgress(0.85, $"Moving {existing.Count} connected folder(s) to the Recycle Bin…");
            long freed = existing.Sum(i => i.Size);
            if (existing.Count > 0)
                NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, existing.Select(i => i.Path));
            var failed = existing.Where(i => ShellOps.Exists(i.Path)).ToList();
            freed -= failed.Sum(i => i.Size);
            TaskEnd($"{app.Name} was uninstalled and {existing.Count - failed.Count} connected folder(s), {Fmt.Size(freed)}, moved to the Recycle Bin.");
            if (failed.Count == 0)
                AppDialog.Result(this, $"{app.Name} uninstalled", Fmt.Size(freed), "of connected folders removed",
                    "The folders are in the Recycle Bin, so you can still restore them.",
                    existing.Select(i => new DialogLine(i.Path, i.SizeText, "")).ToList());
            else
            {
                int k = AppDialog.Show(this, DialogKind.Warning, $"{app.Name} uninstalled, some folders are left",
                    "These are in use or need administrator rights. Force delete closes the programs using them and removes them.",
                    new[] { "Keep them", "Force delete" }, failed.Select(f => new DialogLine(f.Path, f.SizeText, "")).ToList(), 0, 1);
                if (k == 1) RunForceDelete(failed.Select(f => f.Path).ToList(), p => failed.FirstOrDefault(f => f.Path == p)?.Size ?? 0);
            }
        }
        // "program only": still offer the install folder if the uninstaller left it behind
        else if (app.HasFolder && Directory.Exists(app.InstallLocation))
        {
            TaskProgress(0.9, "Checking for a leftover folder…");
            long left = await Task.Run(() => InstalledApps.FolderSize(app.InstallLocation, CancellationToken.None));
            TaskEnd($"{app.Name} was uninstalled.");
            if (AppDialog.Confirm(this, $"{app.Name} left its folder behind",
                    "The uninstaller finished but this folder is still there. Move it to the Recycle Bin?", "Move to Recycle Bin",
                    DialogKind.Question, new[] { new DialogLine(app.InstallLocation, Fmt.Size(left), "") }, cancel: "Keep it"))
            {
                NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, new[] { app.InstallLocation });
                if (Directory.Exists(app.InstallLocation))
                    AppDialog.Alert(this, "Some leftover files are still there", "They are in use or need administrator rights. Right-click them in Disk Map to force delete.", DialogKind.Warning);
                else ShowToast($"{app.Name} and its leftover folder ({Fmt.Size(left)}) were removed.");
            }
        }
        else
        {
            TaskEnd($"{app.Name} was uninstalled.");
            AppDialog.Result(this, $"{app.Name} uninstalled", app.Size > 0 ? app.SizeText : "Done", app.Size > 0 ? "freed" : "",
                "The program was removed. Its settings folders were kept in case you reinstall it.");
        }
        UpdateSummary(SelectedDrive);
        UninstallButton.IsEnabled = AppList.SelectedItem is AppInfo { CanUninstall: true };
    }

    // ---------------- leftovers

    private async void LeftoverScan_Click(object sender, RoutedEventArgs e)
    {
        LeftoverScanButton.IsEnabled = false;
        AppBottomTabs.SelectedItem = TabLeftovers;
        LeftoverSummary.Text = "Looking for folders that belong to no installed program…";
        try
        {
            var apps = _apps.ToList();
            string drive = SelectedDrive;
            var found = await Task.Run(() => InstalledApps.FindLeftovers(apps, CancellationToken.None, drive));
            LeftoverList.ItemsSource = found;
            LeftoverSummary.Text = found.Count == 0
                ? "No obvious leftover folders found."
                : $"{found.Count} folders, {Fmt.Size(found.Sum(f => f.Size))}, match no installed program. Some may still be in use (games, tools, data you keep), so check each one before deleting.";
        }
        finally { LeftoverScanButton.IsEnabled = true; }
    }

    private void LeftoverList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = LeftoverList.SelectedItems.Count;
        LeftoverOpenButton.IsEnabled = n == 1;
        LeftoverDeleteButton.IsEnabled = n > 0;
    }

    private void LeftoverOpen_Click(object sender, RoutedEventArgs e)
    {
        if (LeftoverList.SelectedItem is LeftoverFolder f) ShellOps.Open(f.Path);
    }

    private void LeftoverDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = LeftoverList.SelectedItems.OfType<LeftoverFolder>().ToList();
        if (sel.Count == 0) return;
        long total = sel.Sum(f => f.Size);
        if (!AppDialog.Confirm(this, $"Move {sel.Count} folder(s), {Fmt.Size(total)}, to the Recycle Bin?",
                "You can restore them from the Recycle Bin if something stops working.", "Move to Recycle Bin", DialogKind.Warning,
                sel.Select(f => new DialogLine(f.Path, Fmt.Size(f.Size), "")).ToList()))
            return;
        NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, sel.Select(f => f.Path));
        var remaining = ((List<LeftoverFolder>)LeftoverList.ItemsSource).Where(f => Directory.Exists(f.Path)).ToList();
        ShowToast($"{sel.Count(f => !Directory.Exists(f.Path))} leftover folder(s) moved to the Recycle Bin");
        int removed = sel.Count(f => !Directory.Exists(f.Path));
        LeftoverList.ItemsSource = remaining;
        LeftoverSummary.Text = $"Moved {removed} of {sel.Count} folder(s) to the Recycle Bin.";
        UpdateSummary(SelectedDrive);
    }
}
