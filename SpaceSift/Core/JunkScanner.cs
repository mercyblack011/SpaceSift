using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SpaceSift.Core;

public enum Safety { Safe, Review }

/// <summary>One cleanup suggestion: a group of folders whose contents can be removed.</summary>
public sealed class JunkItem : INotifyPropertyChanged
{
    private bool _checked;
    private long _size;
    private int _files;
    private string _status = "";

    public required string Name { get; init; }
    public required string Description { get; init; }
    public Safety Safety { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool IsRecycleBin { get; init; }
    /// <summary>Folders whose contents are deleted (the folder itself is kept).</summary>
    public List<string> Folders { get; init; } = new();
    /// <summary>Individual files to delete.</summary>
    public List<string> Files { get; init; } = new();
    /// <summary>Only delete files older than this many days (0 = all).</summary>
    public int OlderThanDays { get; init; }
    /// <summary>Only these extensions (empty = all).</summary>
    public string[] Extensions { get; init; } = Array.Empty<string>();

    public bool IsChecked { get => _checked; set { _checked = value; On(nameof(IsChecked)); } }
    public long Size { get => _size; set { _size = value; On(nameof(Size)); On(nameof(SizeText)); On(nameof(IsEmpty)); } }
    public int FileCount { get => _files; set { _files = value; On(nameof(FileCount)); On(nameof(CountText)); } }
    public string Status { get => _status; set { _status = value; On(nameof(Status)); } }

    public string SizeText => Fmt.Size(_size);
    public string CountText => IsRecycleBin ? $"{_files:N0} items" : $"{_files:N0} files";
    public bool IsEmpty => _size <= 0;
    public string SafetyText => Safety == Safety.Safe ? "Safe to clean" : "Check first";
    public string Glyph { get; set; } = "";
    public System.Windows.Visibility OpenVisibility =>
        IsRecycleBin || Folders.Any(Directory.Exists) || Files.Any(File.Exists) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string AdminText => NeedsAdmin && !NativeMethods.IsAdministrator() ? "Needs admin for full clean" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class JunkScanner
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static List<JunkItem> CreateItems()
    {
        string local = Env(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Env(Environment.SpecialFolder.ApplicationData);
        string user = Env(Environment.SpecialFolder.UserProfile);
        string win = Env(Environment.SpecialFolder.Windows);
        string progData = Env(Environment.SpecialFolder.CommonApplicationData);
        string sysDrive = Path.GetPathRoot(win) ?? "C:\\";
        string downloads = Path.Combine(user, "Downloads");

        var list = new List<JunkItem>
        {
            new() { Name = "Recycle Bin", IsRecycleBin = true, Safety = Safety.Safe,
                    Description = "Files you already deleted. Emptying it frees the space for real." },
            new() { Name = "Your temporary files", Safety = Safety.Safe,
                    Description = "Scratch files apps leave behind in your Temp folder. Files in use are skipped.",
                    Folders = { Path.GetTempPath() } },
            new() { Name = "Windows temporary files", Safety = Safety.Safe, NeedsAdmin = true,
                    Description = "Windows' own Temp folder.",
                    Folders = { Path.Combine(win, "Temp") } },
            new() { Name = "Windows Update downloads", Safety = Safety.Safe, NeedsAdmin = true,
                    Description = "Update packages that are already installed. Windows downloads them again if needed.",
                    Folders = { Path.Combine(win, "SoftwareDistribution", "Download") } },
            new() { Name = "Delivery Optimization cache", Safety = Safety.Safe, NeedsAdmin = true,
                    Description = "Update pieces Windows shares with other PCs.",
                    Folders = { Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") } },
            new() { Name = "Crash dumps and error reports", Safety = Safety.Safe, NeedsAdmin = true,
                    Description = "Memory dumps and reports from crashed apps. Only useful for debugging.",
                    Folders = { Path.Combine(local, "CrashDumps"), Path.Combine(win, "Minidump"), Path.Combine(win, "LiveKernelReports"),
                                Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"),
                                Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue") },
                    Files = { Path.Combine(win, "MEMORY.DMP") } },
            new() { Name = "Thumbnail cache", Safety = Safety.Safe,
                    Description = "Picture previews Explorer rebuilds automatically.",
                    Folders = { Path.Combine(local, @"Microsoft\Windows\Explorer") }, Extensions = new[] { ".db" } },
            new() { Name = "Browser caches (Chrome, Edge, Brave, Firefox)", Safety = Safety.Safe,
                    Description = "Cached web pages and images. Logins, passwords and history are NOT touched. Close the browser first.",
                    Folders = BrowserCaches(local) },
            new() { Name = "Developer caches (npm, pip, yarn, NuGet, Gradle)", Safety = Safety.Review,
                    Description = "Downloaded packages for programming tools. Safe, but projects will download them again on the next build.",
                    Folders = { Path.Combine(local, "npm-cache"), Path.Combine(roaming, "npm-cache"), Path.Combine(local, @"pip\Cache"),
                                Path.Combine(local, @"Yarn\Cache"), Path.Combine(user, @".nuget\packages"), Path.Combine(user, @".gradle\caches") } },
            new() { Name = "Old installers in Downloads (older than 30 days)", Safety = Safety.Review, OlderThanDays = 30,
                    Description = "Setup files (.exe, .msi, .iso, .zip, .rar, .7z) you downloaded more than a month ago. Check you don't need them.",
                    Folders = { downloads }, Extensions = new[] { ".exe", ".msi", ".iso", ".zip", ".rar", ".7z", ".msix", ".img" } },
            new() { Name = "Previous Windows installation", Safety = Safety.Review, NeedsAdmin = true,
                    Description = "Windows.old and upgrade leftovers ($Windows.~BT, $Windows.~WS). Removing them means you can't roll back the last Windows upgrade.",
                    Folders = { Path.Combine(sysDrive, "Windows.old"), Path.Combine(sysDrive, "$Windows.~BT"), Path.Combine(sysDrive, "$Windows.~WS") } },
        };
        string[] glyphs = { "", "", "", "", "", "", "", "", "", "", "" };
        for (int k = 0; k < list.Count && k < glyphs.Length; k++) list[k].Glyph = glyphs[k];
        foreach (var i in list) i.IsChecked = i.Safety == Safety.Safe;
        return list;
    }

    private static List<string> BrowserCaches(string local)
    {
        var result = new List<string>();
        foreach (var root in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data" })
        {
            string ud = Path.Combine(local, root);
            if (!Directory.Exists(ud)) continue;
            foreach (var profile in SafeDirs(ud))
            {
                string n = Path.GetFileName(profile);
                if (n != "Default" && !n.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
                    result.Add(Path.Combine(profile, c));
            }
        }
        string ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff)) foreach (var p in SafeDirs(ff)) result.Add(Path.Combine(p, "cache2"));
        return result;
    }

    private static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); }
    }

    private static readonly EnumerationOptions Recurse = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private static bool Matches(JunkItem item, FileInfo f)
    {
        if (item.Extensions.Length > 0 && !item.Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return false;
        if (item.OlderThanDays > 0 && f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-item.OlderThanDays)) return false;
        return true;
    }

    /// <summary>Measures how much each item would free.</summary>
    public static void Measure(JunkItem item, CancellationToken ct)
    {
        long size = 0; int count = 0;
        if (item.IsRecycleBin)
        {
            var (s, c) = RunSta(() =>
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                return SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0L, 0L);
            }, TimeSpan.FromSeconds(30), (0L, 0L));
            size = s; count = (int)c;
        }
        else
        {
            bool topOnly = item.Extensions.Length > 0 && item.OlderThanDays > 0; // Downloads: top level only
            foreach (var folder in item.Folders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    var opts = topOnly ? new EnumerationOptions { IgnoreInaccessible = true } : Recurse;
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Matches(item, f)) continue;
                        size += f.Length; count++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            foreach (var file in item.Files)
                try { var fi = new FileInfo(file); if (fi.Exists) { size += fi.Length; count++; } } catch { }
        }
        item.Size = size; item.FileCount = count;
    }

    /// <summary>Deletes the item's files. Locked files are skipped. Returns bytes freed.</summary>
    public static long Clean(JunkItem item, CancellationToken ct)
    {
        if (item.IsRecycleBin)
        {
            long before = item.Size;
            // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
            return RunSta(() => SHEmptyRecycleBinW(IntPtr.Zero, null, 0x7) == 0, TimeSpan.FromMinutes(10), false) ? before : 0;
        }
        long freed = 0;
        bool topOnly = item.Extensions.Length > 0 && item.OlderThanDays > 0;
        bool filesOnly = item.Extensions.Length > 0 || item.OlderThanDays > 0;
        foreach (var folder in item.Folders)
        {
            if (!Directory.Exists(folder)) continue;
            var di = new DirectoryInfo(folder);
            var opts = topOnly ? new EnumerationOptions { IgnoreInaccessible = true } : Recurse;
            List<FileInfo> files;
            try { files = di.EnumerateFiles("*", opts).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(item, f)) continue;
                try
                {
                    long len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len;
                }
                catch { /* in use or no permission: skip */ }
            }
            if (filesOnly) continue;
            // remove now-empty sub folders (deepest first); keep the folder itself
            try
            {
                foreach (var d in di.EnumerateDirectories("*", Recurse).OrderByDescending(x => x.FullName.Length))
                    try { d.Delete(false); } catch { }
            }
            catch { }
        }
        foreach (var file in item.Files)
            try { var fi = new FileInfo(file); if (fi.Exists) { long l = fi.Length; fi.Delete(); freed += l; } } catch { }
        return freed;
    }

    /// <summary>Shell recycle-bin APIs must run on an STA thread; thread-pool (MTA) calls can hang.</summary>
    private static T RunSta<T>(Func<T> f, TimeSpan timeout, T fallback)
    {
        T result = fallback;
        var t = new Thread(() => { try { result = f(); } catch { } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t.Join(timeout) ? result : fallback;
    }

    [StructLayout(LayoutKind.Sequential)] // 64-bit layout: 4-byte padding after cbSize
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);
}
