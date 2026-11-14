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
        _junk = JunkScanner.CreateItems();
        foreach (var j in _junk) j.PropertyChanged += Junk_PropertyChanged;
        JunkList.ItemsSource = _junk;
        JunkScanButton.IsEnabled = false;
        JunkCleanButton.IsEnabled = false;
        JunkSummary.Text = "Looking for files you don't need…";
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
        finally { JunkScanButton.IsEnabled = true; }
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
        JunkSummary.Text = measuring
            ? "Checking temp files, caches, the Recycle Bin and more."
            : $"{_junk.Count(j => j.Size > 0)} places hold files you don't need. Safe items are already ticked.";
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
        string list = string.Join("\n", items.Select(i => $"• {i.Name}  ({i.SizeText})"));
        bool review = items.Any(i => i.Safety == Safety.Review);
        string msg = $"Delete {Fmt.Size(total)} of junk files?\n\n{list}\n\nThese files are deleted permanently (not moved to the Recycle Bin). Files in use are skipped."
                     + (review ? "\n\nSome selected items are marked \"Check first\". Make sure you don't need them." : "");
        if (MessageBox.Show(this, msg, "Clean up", MessageBoxButton.YesNo, review ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        JunkCleanButton.IsEnabled = false;
        JunkScanButton.IsEnabled = false;
        long freedTotal = 0;
        var ct = CancellationToken.None;
        foreach (var item in items)
        {
            item.Status = "Cleaning…";
            long freed = await Task.Run(() => JunkScanner.Clean(item, ct));
            freedTotal += freed;
            await Task.Run(() => JunkScanner.Measure(item, ct));
            item.Status = item.Size > 0
                ? $"Freed {Fmt.Size(freed)}. {item.SizeText} left (in use or needs admin)."
                : $"Freed {Fmt.Size(freed)}.";
            item.IsChecked = false;
            UpdateSummary(((DriveItem?)DriveBox.SelectedItem)?.Root ?? Path.GetPathRoot(Environment.SystemDirectory)); // gauge + numbers move live
            // a fully cleaned card shows its result briefly, then folds away
            if (item.Size == 0 && JunkList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement card)
                _ = Task.Delay(1200).ContinueWith(_ => Dispatcher.Invoke(() => Motion.Collapse(card)));
        }
        JunkScanButton.IsEnabled = true;
        UpdateJunkSummary();
        UpdateSummary(((DriveItem?)DriveBox.SelectedItem)?.Root ?? Path.GetPathRoot(Environment.SystemDirectory));
        StatusText.Text = $"Clean up finished: {Fmt.Size(freedTotal)} freed.";
        MessageBox.Show(this, $"Done. {Fmt.Size(freedTotal)} freed.", "Clean up", MessageBoxButton.OK, MessageBoxImage.Information);
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
        AppsSummary.Text = $"{sorted.Count:N0} programs · {Fmt.Size(sorted.Sum(a => a.Size))}";
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
        string list = string.Join("\n", sel.Select(i => $"• {i.Path}  ({i.SizeText})"));
        string warn = stillInstalled && sel.Any(i => i.Kind == "Install folder")
            ? "\n\nWARNING: this deletes the program files of an app that is still installed. Use Uninstall instead unless you know what you are doing."
            : stillInstalled ? "\n\nThe app is still installed. Deleting its data may reset its settings or sign you out." : "";
        if (MessageBox.Show(this, $"Move {sel.Count} item(s), {Fmt.Size(total)}, to the Recycle Bin?\n\n{list}{warn}",
                "Remove app files", MessageBoxButton.YesNo, warn.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, sel.Select(i => i.Path));
        int removed = sel.Count(i => !ShellOps.Exists(i.Path));
        StatusText.Text = $"Moved {removed} of {sel.Count} item(s) to the Recycle Bin.";
        if (removed > 0) ShowToast($"{removed} item(s) moved to the Recycle Bin");
        UpdateSummary(Path.GetPathRoot(Environment.SystemDirectory));
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
        StatusText.Text = $"Finding folders connected to {app.Name}…";
        List<RelatedItem> related;
        try { related = await Task.Run(() => InstalledApps.FindRelated(app, CancellationToken.None)); }
        catch { related = new List<RelatedItem>(); }
        StatusText.Text = "";

        var dlg = new UninstallDialog(app, related) { Owner = this };
        if (dlg.ShowDialog() != true) { UninstallButton.IsEnabled = true; return; }
        bool removeConnected = dlg.RemoveConnected;
        var toRemove = removeConnected ? dlg.SelectedItems : new List<RelatedItem>();
        StatusText.Text = $"Waiting for the {app.Name} uninstaller to finish…";
        try
        {
            await InstalledApps.UninstallAsync(app);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "Uninstall cancelled."; UninstallButton.IsEnabled = true; return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start the uninstaller:\n{ex.Message}\n\nTry Windows Settings › Apps instead.", "Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
            UninstallButton.IsEnabled = true;
            return;
        }

        await LoadApps();
        bool stillThere = _apps.Any(a => a.RegistryKey == app.RegistryKey);
        StatusText.Text = stillThere ? $"{app.Name} is still installed." : $"{app.Name} was uninstalled.";
        if (!stillThere && !removeConnected) ShowToast($"{app.Name} uninstalled");

        if (removeConnected)
        {
            if (stillThere)
            {
                MessageBox.Show(this, $"{app.Name} is still installed (the uninstaller was cancelled or failed), so its folders were not touched.",
                    "Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var existing = toRemove.Where(i => ShellOps.Exists(i.Path)).ToList();
                long freed = existing.Sum(i => i.Size);
                if (existing.Count > 0)
                    NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, existing.Select(i => i.Path));
                var failed = existing.Where(i => ShellOps.Exists(i.Path)).ToList();
                freed -= failed.Sum(i => i.Size);
                StatusText.Text = $"{app.Name} was uninstalled and {existing.Count - failed.Count} connected folder(s), {Fmt.Size(freed)}, moved to the Recycle Bin.";
                ShowToast($"{app.Name} uninstalled · {Fmt.Size(freed)} of connected folders to the Recycle Bin");
                if (failed.Count > 0)
                    MessageBox.Show(this, "These could not be removed (in use or need admin):\n\n" + string.Join("\n", failed.Select(f => f.Path)),
                        "Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        // "program only": still offer the install folder if the uninstaller left it behind
        else if (!stillThere && app.HasFolder)
        {
            long left = await Task.Run(() => InstalledApps.FolderSize(app.InstallLocation, CancellationToken.None));
            if (MessageBox.Show(this,
                    $"{app.Name} left its folder behind:\n\n{app.InstallLocation}\n{Fmt.Size(left)}\n\nMove this folder to the Recycle Bin?",
                    "Leftover folder", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, new[] { app.InstallLocation });
                StatusText.Text = Directory.Exists(app.InstallLocation)
                    ? "Some leftover files could not be removed (in use or need admin)."
                    : $"{app.Name} and its leftover folder ({Fmt.Size(left)}) were removed.";
            }
        }
        UpdateSummary(Path.GetPathRoot(Environment.SystemDirectory));
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
            var found = await Task.Run(() => InstalledApps.FindLeftovers(apps, CancellationToken.None));
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
        string list = string.Join("\n", sel.Take(10).Select(f => f.Path)) + (sel.Count > 10 ? $"\n… and {sel.Count - 10} more" : "");
        if (MessageBox.Show(this, $"Move {sel.Count} folder(s), {Fmt.Size(total)}, to the Recycle Bin?\n\n{list}\n\nYou can restore them from the Recycle Bin if something stops working.",
                "Remove leftover folders", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        NativeMethods.RecycleBin(new WindowInteropHelper(this).Handle, sel.Select(f => f.Path));
        var remaining = ((List<LeftoverFolder>)LeftoverList.ItemsSource).Where(f => Directory.Exists(f.Path)).ToList();
        ShowToast($"{sel.Count(f => !Directory.Exists(f.Path))} leftover folder(s) moved to the Recycle Bin");
        int removed = sel.Count(f => !Directory.Exists(f.Path));
        LeftoverList.ItemsSource = remaining;
        LeftoverSummary.Text = $"Moved {removed} of {sel.Count} folder(s) to the Recycle Bin.";
        UpdateSummary(Path.GetPathRoot(Environment.SystemDirectory));
    }
}
