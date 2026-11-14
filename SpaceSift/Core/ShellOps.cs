using System.Diagnostics;
using System.Windows;

namespace SpaceSift.Core;

public static class ShellOps
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Open", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static void ShowInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Show in Explorer", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static void CopyPath(IEnumerable<string> paths)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { /* clipboard busy */ }
    }

    public static void Properties(IntPtr owner, string path) => NativeMethods.ShowProperties(owner, path);

    /// <summary>Deletes permanently. Returns the list of errors.</summary>
    public static List<string> DeletePermanently(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                        TryClearReadOnly(f);
                    Directory.Delete(p, true);
                }
                else if (File.Exists(p))
                {
                    TryClearReadOnly(p);
                    File.Delete(p);
                }
            }
            catch (Exception ex) { errors.Add($"{p}: {ex.Message}"); }
        }
        return errors;
    }

    private static void TryClearReadOnly(string f)
    {
        try
        {
            var a = File.GetAttributes(f);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}
