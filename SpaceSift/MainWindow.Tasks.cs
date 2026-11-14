using System.Windows;
using System.Windows.Threading;
using SpaceSift.Core;
using SpaceSift.UI;

namespace SpaceSift;

/// <summary>Header progress for long jobs, partition switching, and force delete.</summary>
public partial class MainWindow
{
    // =================================================================== header progress (0–100%)

    private double _taskShown;
    private DispatcherTimer? _creep;
    private int _taskToken;

    /// <summary>Shows the header progress bar at 0% with a task name.</summary>
    private void TaskBegin(string name, string? status = null)
    {
        _taskToken++;
        StopCreep();
        _taskShown = 0;
        ProgressTask.Text = name;
        Progress.IsIndeterminate = false;
        Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        Progress.Value = 0;
        ProgressPct.Text = "0%";
        Motion.FadeTo(ProgressRow, 1, 200);
        if (status != null) StatusText.Text = status;
    }

    /// <summary>Moves the bar forward (never back) to <paramref name="fraction"/>.</summary>
    private void TaskProgress(double fraction, string? status = null)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction > _taskShown)
        {
            _taskShown = fraction;
            Motion.Glide(Progress, fraction, 260);
            ProgressPct.Text = $"{Math.Floor(fraction * 100):0}%";
        }
        if (status != null) StatusText.Text = status;
    }

    /// <summary>
    /// For steps with no measurable progress (waiting for an uninstaller): the bar eases
    /// toward <paramref name="to"/> and slows down as it gets close, so it never stalls or overshoots.
    /// </summary>
    private void TaskCreep(double to, double seconds)
    {
        StopCreep();
        double from = _taskShown;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _creep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _creep.Tick += (_, _) => TaskProgress(from + (to - from) * (1 - Math.Exp(-sw.Elapsed.TotalSeconds / seconds)));
        _creep.Start();
    }

    private void StopCreep() { _creep?.Stop(); _creep = null; }

    /// <summary>Fills to 100%, holds briefly, then fades the bar away.</summary>
    private async void TaskEnd(string? status = null, bool success = true)
    {
        StopCreep();
        int token = _taskToken;
        if (success) TaskProgress(1);
        if (status != null) StatusText.Text = status;
        await Task.Delay(1100);
        if (token != _taskToken) return; // a new task started meanwhile
        Motion.FadeTo(ProgressRow, 0, 600);
    }

    // =================================================================== partitions

    private readonly Dictionary<string, ScanResult> _scansByDrive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The partition picked in the sidebar ("C:\"). Every page shows results for this drive only.</summary>
    private string SelectedDrive => ((DriveItem?)DriveBox.SelectedItem)?.Root ?? JunkScanner.SystemDrive;

    private string DriveName(string root)
    {
        foreach (DriveItem d in DriveBox.Items)
            if (string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase)) return d.Label;
        return root.TrimEnd('\\');
    }

    private void DriveBox_Changed()
    {
        string drive = SelectedDrive;
        UpdateSummary(drive);
        // Disk Map / Files: show this drive's last scan, or the "scan this drive" card
        _scansByDrive.TryGetValue(drive, out var cached);
        if (!ReferenceEquals(cached, _scan))
        {
            if (cached != null) LoadResult(cached, fromCache: true);
            else ClearResult();
        }
        UpdateEmptyStates(false);
        UpdatePageTitle();
        StatusText.Text = _scan != null ? $"Showing {DriveName(drive)}." : $"Showing {DriveName(drive)}. Press Scan drive to map it.";
        _ = ScanJunk();
        LeftoverList.ItemsSource = null;
        LeftoverSummary.Text = $"Find folders on {drive.TrimEnd('\\')} that belong to no installed program.";
    }

    private void ClearResult()
    {
        _scan = null;
        _colors = null; _catView = null; _view = null;
        Treemap.SetData(null, null, null, -1);
        _rows.Clear();
        ExtList.ItemsSource = null;
        ExtChart.SetData(new List<ExtStat>(), null);
        FileList.ItemsSource = null;
        FileCount.Text = "";
        Breadcrumb.ItemsSource = null;
        SumScan.Text = "";
    }

    private static bool OnDrive(string? path, string drive) =>
        path != null && string.Equals(Path.GetPathRoot(path), drive, StringComparison.OrdinalIgnoreCase);

    // =================================================================== force delete

    private async void RunForceDelete(List<string> paths, Func<string, long> sizeOf)
    {
        paths = paths.Where(ShellOps.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (paths.Any(p => p.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) || Path.GetPathRoot(p) == p))
        {
            AppDialog.Alert(this, "Can't force delete this",
                "Force delete is blocked for drive roots and the Windows folder, because removing them would stop Windows from starting.",
                DialogKind.Warning);
            return;
        }

        TaskBegin("Force delete", "Checking which programs are using these files…");
        List<LockingProcess> lockers;
        try
        {
            var files = await Task.Run(() => ForceDelete.ExpandFiles(paths));
            TaskProgress(0.15);
            lockers = await Task.Run(() => ForceDelete.FindLockers(files));
            TaskProgress(0.3);
        }
        catch (Exception ex) { TaskEnd("Force delete failed: " + ex.Message, false); return; }

        long total = paths.Sum(sizeOf);
        var lines = new List<DialogLine>();
        foreach (var lp in lockers)
            lines.Add(new DialogLine(lp.Name + (lp.Critical ? "  (Windows, can't close)" : ""), $"PID {lp.Id}", "\uE7EF"));
        lines.AddRange(paths.Take(20).Select(p => new DialogLine(p, Fmt.Size(sizeOf(p)), Directory.Exists(p) ? "\uE8B7" : "\uE7C3")));
        if (paths.Count > 20) lines.Add(new DialogLine($"… and {paths.Count - 20} more"));

        string msg = lockers.Count == 0
            ? "No program is using these files right now. They will be deleted permanently (not to the Recycle Bin)."
            : $"{lockers.Count} program(s) are using these files. SpaceSift will close them (unsaved work in them is lost), then delete the files permanently.";
        if (!AppDialog.Confirm(this, $"Force delete {paths.Count:N0} item(s), {Fmt.Size(total)}?", msg + " This cannot be undone.",
                lockers.Count == 0 ? "Delete permanently" : "Close programs and delete", DialogKind.Danger, lines, danger: true))
        {
            TaskEnd("Force delete cancelled.", false);
            return;
        }

        var closeErrors = new List<string>();
        if (lockers.Count > 0)
        {
            TaskProgress(0.35, $"Closing {string.Join(", ", lockers.Select(l => l.Name).Distinct().Take(4))}…");
            closeErrors = await Task.Run(() => ForceDelete.CloseProcesses(lockers));
        }
        TaskProgress(0.5, "Deleting…");
        var progress = new Progress<double>(f => TaskProgress(0.5 + f * 0.5));
        var outcome = await Task.Run(() => ForceDelete.Delete(paths, sizeOf, progress));

        // keep the map and lists in step with the disk
        if (_scan != null)
        {
            bool changed = false;
            foreach (var p in outcome.Deleted)
            {
                int idx = _scan.FindPath(p);
                if (idx > 0) { _scan.MarkDeleted(idx); changed = true; }
            }
            if (changed) RecomputeAll(rebuildExtPanel: true);
        }
        UpdateSummary(SelectedDrive);
        TaskEnd($"Force delete: {outcome.Deleted.Count} of {paths.Count} item(s) removed, {Fmt.Size(outcome.Freed)} freed.");

        if (outcome.Failed.Count == 0 && outcome.ScheduledForReboot.Count == 0)
        {
            AppDialog.Result(this, "Deleted", Fmt.Size(outcome.Freed), "freed",
                lockers.Count > 0 ? $"Closed {lockers.Count(l => !l.Critical)} program(s) and deleted {outcome.Deleted.Count:N0} item(s)." : $"Deleted {outcome.Deleted.Count:N0} item(s).");
            return;
        }
        var report = new List<DialogLine>();
        report.AddRange(outcome.ScheduledForReboot.Select(p => new DialogLine(p, "at restart", "\uE777")));
        report.AddRange(outcome.Failed.Select(p => new DialogLine(p, "locked", "\uE72E")));
        report.AddRange(closeErrors.Select(e => new DialogLine(e, "", "\uE7BA")));
        string tail = outcome.ScheduledForReboot.Count > 0
            ? "Items marked \"at restart\" will be removed the next time Windows starts."
            : NativeMethods.IsAdministrator()
                ? "Windows itself is holding these files."
                : "Some files need administrator rights. Restart SpaceSift as administrator (Fast mode does this) and try again; it can then also remove files at the next restart.";
        AppDialog.Alert(this, outcome.Deleted.Count > 0 ? $"Freed {Fmt.Size(outcome.Freed)}, some items are left" : "These items could not be deleted",
            tail, DialogKind.Warning, report);
    }

    /// <summary>Header "Force delete" button: pick any files or a folder anywhere.</summary>
    private void ForceDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        int k = AppDialog.Show(this, DialogKind.Danger, "Force delete",
            "Deletes a file or folder permanently, even when a program is using it. SpaceSift shows which programs hold it open, closes them, then deletes it. " +
            "You can also right-click anything in Disk Map or Files.",
            new[] { "Cancel", "Choose a folder…", "Choose files…" }, defaultButton: 2);
        if (k == 2)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose files to force delete", Multiselect = true, CheckFileExists = true };
            if (dlg.ShowDialog(this) == true)
                RunForceDelete(dlg.FileNames.ToList(), p => { try { return new FileInfo(p).Length; } catch { return 0; } });
        }
        else if (k == 1)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to force delete" };
            if (dlg.ShowDialog(this) == true)
                RunForceDelete(new List<string> { dlg.FolderName }, p => InstalledApps.FolderSize(p, CancellationToken.None));
        }
    }
}


