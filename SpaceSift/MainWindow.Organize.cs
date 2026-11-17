using System.Windows;
using SpaceSift.Core;
using SpaceSift.UI;

namespace SpaceSift;

/// <summary>Organize page: sorts a folder's loose files into "Audio (5 GB)"-style folders.</summary>
public partial class MainWindow
{
    private string? _orgFolder;
    private List<OrganizeGroup> _orgPlan = new();
    private OrganizeResult? _orgLast;
    private string? _orgLastFolder;

    private void OrgChoose_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to organize" };
        if (_orgFolder != null) dlg.InitialDirectory = _orgFolder;
        else dlg.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (dlg.ShowDialog(this) != true) return;
        OpenOrganizer(dlg.FolderName);
    }

    /// <summary>Opens the Organize page on a folder (from the folder picker or a right-click in Disk Map / Files).</summary>
    private void OpenOrganizer(string folder)
    {
        if (FolderOrganizer.Blocked(folder) is string why)
        {
            AppDialog.Alert(this, "This folder can't be organized",
                $"{folder} is {why}. Moving its files would break Windows or your apps. Pick a folder of your own files, like Downloads, Desktop or a folder on another drive.",
                DialogKind.Warning);
            return;
        }
        _orgFolder = folder;
        if (Tabs.SelectedItem != TabOrganize) Tabs.SelectedItem = TabOrganize;
        _ = OrgRefresh();
    }

    private async Task OrgRefresh()
    {
        if (_orgFolder == null) return;
        string folder = _orgFolder;
        OrgPath.Text = folder;
        OrgSummary.Text = "Looking at the files…";
        try { _orgPlan = await Task.Run(() => FolderOrganizer.Plan(folder)); }
        catch (Exception ex) { OrgSummary.Text = "Can't read this folder: " + ex.Message; _orgPlan = new(); }
        if (folder != _orgFolder) return;

        if (OrgEmpty.Visibility == Visibility.Visible)
        {
            _ = Motion.Collapse(OrgEmpty, 380);
            OrgHero.Visibility = OrgListCard.Visibility = Visibility.Visible;
            Motion.FadeIn(OrgHero, 420, 10, 120);
        }
        OrgList.ItemsSource = _orgPlan;
        Motion.FadeIn(OrgList, 320, 10);
        int files = _orgPlan.Sum(g => g.Files.Count);
        long size = _orgPlan.Sum(g => g.Size);
        Motion.Count(OrgHeadline, files, v => $"{v:N0} {((int)v == 1 ? "file" : "files")}", 600);
        OrgHeadSuffix.Text = files == 0 ? "loose here" : "to sort";
        OrgSummary.Text = files == 0
            ? "Nothing to sort: every file here is already in a folder."
            : $"{Fmt.Size(size)} will go into {_orgPlan.Count} folder(s), shown below with the names they will get. Sub-folders you already have stay as they are.";
        OrgButton.IsEnabled = files > 0;
        OrgButtonText.Text = files > 0 ? $"Organize {files:N0} files" : "Organize";
        OrgUndoButton.Visibility = _orgLast != null && _orgLastFolder == folder ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OrgRun_Click(object sender, RoutedEventArgs e)
    {
        if (_orgFolder == null || _orgPlan.Count == 0) return;
        string folder = _orgFolder;
        int files = _orgPlan.Sum(g => g.Files.Count);
        var lines = _orgPlan.Select(g => new DialogLine(g.FolderName, g.CountText, g.Glyph)).ToList();
        if (!AppDialog.Confirm(this, $"Organize {files:N0} files?",
                $"The files in {folder} will be moved into these folders. Nothing is deleted, and you can undo it.",
                "Organize", DialogKind.Question, lines))
            return;

        OrgButton.IsEnabled = false;
        TaskBegin("Organizing", $"Sorting {files:N0} files in {folder}…");
        var plan = _orgPlan;
        var progress = new Progress<double>(f => TaskProgress(f * 0.97));
        OrganizeResult result;
        try { result = await Task.Run(() => FolderOrganizer.Organize(folder, plan, progress)); }
        catch (Exception ex)
        {
            TaskEnd("Organize failed: " + ex.Message, false);
            AppDialog.Alert(this, "Organize failed", ex.Message, DialogKind.Warning);
            await OrgRefresh();
            return;
        }
        _orgLast = result; _orgLastFolder = folder;

        // look for files with identical content inside the folders we just filled
        var sortedDirs = result.Moves.Select(m => Path.GetDirectoryName(m.To)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<List<string>> dupes;
        try
        {
            TaskProgress(0.97, "Checking for duplicate files…");
            var dupProgress = new Progress<double>(f => TaskProgress(0.97 + f * 0.03, "Checking for duplicate files…"));
            dupes = await Task.Run(() => FolderOrganizer.FindDuplicates(
                sortedDirs.SelectMany(d => { try { return Directory.EnumerateFiles(d).ToList(); } catch { return new List<string>(); } }), dupProgress));
        }
        catch { dupes = new(); }
        TaskEnd($"Organized {result.Moved:N0} files in {folder}." + (dupes.Count > 0 ? $" Found {dupes.Sum(g => g.Count - 1):N0} duplicate file(s)." : ""));

        // show the folders with their final names
        var made = Directory.EnumerateDirectories(folder)
            .Where(d => result.Moves.Any(m => m.To.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)))
            .Select(d => new DialogLine(Path.GetFileName(d), $"{result.Moves.Count(m => m.To.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)):N0} files", ""))
            .ToList();
        made.AddRange(result.Failed.Take(20).Select(f => new DialogLine(Path.GetFileName(f), "in use, not moved", "")));
        int k = AppDialog.Show(this, DialogKind.Success, "Folder organized", result.Failed.Count == 0
                ? "Every file is now in a folder for its kind. Press Undo on the Organize page to put them back."
                : $"{result.Failed.Count} file(s) were in use and stayed where they were. Close the programs using them and organize again.",
            new[] { "Open folder", "Done" }, made, defaultButton: 1,
            bigValue: result.Moved.ToString("N0"), bigCaption: result.Moved == 1 ? "file sorted" : "files sorted");
        if (dupes.Count > 0) await AskDeleteDuplicates(folder, dupes);
        if (k == 0) ShellOps.Open(folder);
        await OrgRefresh();
    }

    /// <summary>Lists every set of identical files and offers to send the extra copies to the Recycle Bin.</summary>
    private async Task AskDeleteDuplicates(string folder, List<List<string>> dupes)
    {
        int extra = dupes.Sum(g => g.Count - 1);
        long waste = dupes.Sum(g => { try { return new FileInfo(g[0]).Length * (g.Count - 1); } catch { return 0L; } });
        string Rel(string p) => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? p.Substring(folder.Length).TrimStart('\\') : p;
        var lines = new List<DialogLine>();
        foreach (var g in dupes.Take(40))
        {
            string size = Fmt.Size(new FileInfo(g[0]).Length);
            lines.Add(new DialogLine(Rel(g[0]), "keep", ""));
            foreach (var d in g.Skip(1)) lines.Add(new DialogLine("    " + Rel(d), $"duplicate · {size}", ""));
        }
        if (dupes.Count > 40) lines.Add(new DialogLine($"… and {dupes.Count - 40} more sets"));

        int k = AppDialog.Show(this, DialogKind.Warning,
            $"Found {extra:N0} duplicate file{(extra == 1 ? "" : "s")}",
            "These files have exactly the same content as another file (checked byte for byte, not just by name). " +
            "In each set, the file marked \"keep\" stays and the copies below it can be deleted. Deleted copies go to the Recycle Bin.",
            new[] { "Keep all", "Delete duplicates" }, lines, defaultButton: 0, dangerButton: 1,
            bigValue: Fmt.Size(waste), bigCaption: "wasted on copies");
        if (k != 1) { ShowToast("Duplicates kept."); return; }

        var toDelete = dupes.SelectMany(g => g.Skip(1)).ToList();
        TaskBegin("Deleting duplicates");
        TaskCreep(0.9, 2);
        await Task.Yield();
        NativeMethods.RecycleBin(new System.Windows.Interop.WindowInteropHelper(this).Handle, toDelete);
        var left = toDelete.Where(File.Exists).ToList();
        long freed = waste - left.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        TaskEnd($"Moved {toDelete.Count - left.Count:N0} duplicate file(s), {Fmt.Size(freed)}, to the Recycle Bin.");

        // the folder names show their size, so bring them up to date
        await Task.Run(() => FolderOrganizer.RefreshFolderNames(folder, _orgLast));
        UpdateSummary(SelectedDrive);
        if (left.Count == 0)
            AppDialog.Result(this, "Duplicates removed", Fmt.Size(freed), "freed",
                $"{toDelete.Count:N0} extra copies were moved to the Recycle Bin. One copy of each file was kept.");
        else
            AppDialog.Alert(this, "Some duplicates could not be deleted",
                "They are in use. Close the programs using them and try again, or right-click them in Files and choose Force delete.",
                DialogKind.Warning, AppDialog.Paths(left));
    }

    private async void OrgUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_orgLast == null || _orgLastFolder == null) return;
        if (!AppDialog.Confirm(this, "Undo the last organize?",
                $"{_orgLast.Moved:N0} files go back to {_orgLastFolder}, and the folders SpaceSift made are removed if they are empty.",
                "Undo", DialogKind.Question))
            return;
        var last = _orgLast;
        TaskBegin("Undo organize");
        TaskCreep(0.9, 2);
        int back = await Task.Run(() => FolderOrganizer.Undo(last));
        TaskEnd($"Put {back:N0} files back.");
        _orgLast = null;
        ShowToast($"Undo: {back:N0} files moved back");
        await OrgRefresh();
    }

    private void OrgOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_orgFolder != null) ShellOps.Open(_orgFolder);
    }
}
