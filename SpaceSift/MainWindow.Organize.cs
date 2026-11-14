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
        if (FolderOrganizer.Blocked(dlg.FolderName) is string why)
        {
            AppDialog.Alert(this, "This folder can't be organized",
                $"{dlg.FolderName} is {why}. Moving its files would break Windows or your apps. Pick a folder of your own files, like Downloads, Desktop or a folder on another drive.",
                DialogKind.Warning);
            return;
        }
        _orgFolder = dlg.FolderName;
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
        TaskEnd($"Organized {result.Moved:N0} files in {folder}.");

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
        if (k == 0) ShellOps.Open(folder);
        await OrgRefresh();
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
