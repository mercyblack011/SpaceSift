using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SpaceSift.Core;

public sealed class AppInfo : INotifyPropertyChanged
{
    private long _size;
    private bool _measured;

    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string RegistryKey { get; init; } = "";
    public bool Is32Bit { get; init; }
    public bool PerUser { get; init; }

    public string DisplayIcon { get; init; } = "";

    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon { get => _icon; set { _icon = value; On(nameof(Icon)); } }

    public long Size { get => _size; set { _size = value; _measured = true; On(nameof(Size)); On(nameof(SizeText)); } }

    private double _fraction;
    /// <summary>Size relative to the largest app in the list (drives the mini bar).</summary>
    public double Fraction { get => _fraction; set { _fraction = value; On(nameof(Fraction)); } }
    public string Subtitle => string.Join(" · ", new[] { Publisher, string.IsNullOrWhiteSpace(Version) ? "" : "v" + Version }.Where(s => s.Length > 0));
    public bool HasSize => _measured;
    public string SizeText => _measured && _size > 0 ? Fmt.Size(_size) : "–";
    public string DateText => InstallDate?.ToString("yyyy-MM-dd") ?? "";
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString);
    public bool HasFolder => !string.IsNullOrWhiteSpace(InstallLocation) && Directory.Exists(InstallLocation);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public override string ToString() => Name;
}

public sealed class LeftoverFolder
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string Location { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string SizeText => Fmt.Size(Size);
    public string DateText => Modified.ToString("yyyy-MM-dd");
    public override string ToString() => Name;
}

/// <summary>A file, folder, shortcut or registry key that belongs to an installed app.</summary>
public sealed class RelatedItem
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public bool IsRegistry { get; init; }
    public long Size { get; init; }
    public int Files { get; init; }
    public DateTime? LastUsed { get; init; }
    public string SizeText => IsRegistry ? "" : Fmt.Size(Size);
    public string FilesText => IsRegistry ? "" : IsFolder ? $"{Files:N0} files" : "1 file";
    public string DateText => LastUsed?.ToString("yyyy-MM-dd") ?? "";
    public string Glyph => IsRegistry ? "" : IsFolder ? "" : Kind == "Shortcut" ? "" : "";
    public bool CanDelete => !IsRegistry;
    public override string ToString() => Path;
}

public static class InstalledApps
{
    /// <summary>
    /// Finds everything on disk that belongs to an app: install folder, AppData / ProgramData
    /// folders named after the app (also inside its publisher's folder), Start Menu and Desktop
    /// shortcuts, and its uninstall registry key. Name matching is a best guess.
    /// </summary>
    public static List<RelatedItem> FindRelated(AppInfo app, CancellationToken ct)
    {
        var found = new Dictionary<string, (string kind, bool folder)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string kind, bool folder)
        {
            path = path.TrimEnd('\\');
            // never list a parent of the install folder (e.g. "Program Files (x86)\Microsoft" for Edge)
            if (app.HasFolder && app.InstallLocation.TrimEnd('\\').StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) return;
            // skip if this path is already covered by a parent we found
            foreach (var k in found.Keys)
                if (path.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var k in found.Keys.Where(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (found[k].kind == "Install folder") kind = "Install folder"; // the app's own folder, one level up
                found.Remove(k);
            }
            found.TryAdd(path, (kind, folder));
        }

        if (app.HasFolder && !IsSystemDir(app.InstallLocation)) Add(app.InstallLocation, "Install folder", true);

        var nameWords = Words(app.Name).Where(w => !IsGenericWord(w)).ToList();
        string nameNorm = Norm(app.Name);
        string pubNorm = Norm(PublisherShort(app.Publisher));
        string installLeaf = app.HasFolder ? Norm(Path.GetFileName(app.InstallLocation.TrimEnd('\\'))) : "";

        bool NameMatch(string folder)
        {
            string n = Norm(folder);
            if (n.Length < 3) return false;
            // never treat a whole publisher folder ("Microsoft", "Google") or a generic word as the app itself
            if (n != nameNorm && (IsGenericWord(n) || (pubNorm.Length >= 3 && (n == pubNorm || pubNorm.StartsWith(n))))) return false;
            if (n == nameNorm || (installLeaf.Length >= 3 && n == installLeaf)) return true;
            if (n.Length >= 4 && nameNorm.StartsWith(n)) return true;                  // "Termius" vs "Termius 9.37.0"
            return nameWords.Count > 0 && nameWords[0].Length >= 4 && n == nameWords[0]; // first significant word
        }
        bool PublisherMatch(string folder)
        {
            string n = Norm(folder);
            return pubNorm.Length >= 3 && (n == pubNorm || (n.Length >= 4 && pubNorm.StartsWith(n)));
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoots = new (string path, string kind)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "App data (Roaming)"),
            (local, "App data (Local)"),
            (Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"), "App data (LocalLow)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Shared data (ProgramData)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
            (Path.Combine(local, "Programs"), "Program folder"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"), "Documents"),
        };
        foreach (var (root, kind) in dataRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            foreach (var d in SafeDirs(root))
            {
                string leaf = Path.GetFileName(d);
                if (Ignore.Contains(leaf) && !NameMatch(leaf)) { if (!PublisherMatch(leaf)) continue; }
                if (NameMatch(leaf)) { Add(d, kind, true); continue; }
                if (PublisherMatch(leaf))
                    foreach (var sub in SafeDirs(d))                                   // e.g. Local\Google\Chrome
                        if (NameMatch(Path.GetFileName(sub))) Add(sub, kind, true);
            }
        }

        // shortcuts
        var linkRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (var root in linkRoots.Where(Directory.Exists))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var d in SafeDirs(root))
                if (NameMatch(Path.GetFileName(d)) || PublisherMatch(Path.GetFileName(d)) && SafeFiles(d, "*.lnk").Any(f => NameMatch(Path.GetFileNameWithoutExtension(f))))
                    Add(d, "Start Menu folder", true);
            foreach (var f in SafeFiles(root, "*.lnk"))
            {
                string n = Norm(Path.GetFileNameWithoutExtension(f));
                if (NameMatch(Path.GetFileNameWithoutExtension(f)) || (n.Length >= 4 && nameNorm.Contains(n)))
                    Add(f, "Shortcut", false);
            }
        }

        var result = new List<RelatedItem>();
        foreach (var (path, (kind, folder)) in found)
        {
            ct.ThrowIfCancellationRequested();
            if (folder)
            {
                var (size, files, newest) = Measure(path, ct);
                result.Add(new RelatedItem { Kind = kind, Path = path, IsFolder = true, Size = size, Files = files, LastUsed = newest == DateTime.MinValue ? null : newest.ToLocalTime() });
            }
            else
            {
                var fi = new FileInfo(path);
                result.Add(new RelatedItem { Kind = kind, Path = path, Size = fi.Exists ? fi.Length : 0, Files = 1, LastUsed = fi.Exists ? fi.LastWriteTime : null });
            }
        }
        result.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (!string.IsNullOrEmpty(app.RegistryKey))
            result.Add(new RelatedItem { Kind = "Registry (uninstall entry)", Path = app.RegistryKey, IsRegistry = true });
        return result;
    }

    private static readonly HashSet<string> GenericWords = new() { "microsoft", "windows", "visual", "desktop", "client", "service", "driver", "setup", "player", "media", "manager", "system", "data", "with", "hotspot", "remove", "only", "professional", "plus", "office", "google", "online" };
    private static bool IsGenericWord(string w) => GenericWords.Contains(w);

    private static string PublisherShort(string p)
    {
        // "Google LLC" -> "Google", "Brave Software Inc" -> "BraveSoftware"
        var words = Regex.Split(p, @"[^A-Za-z0-9]+").Where(w => w.Length > 0 && !StopWords.Contains(w.ToLowerInvariant())).ToList();
        if (words.Count == 0) return "";
        string first = words[0];
        return first.Length >= 3 ? first : string.Concat(words.Take(2));
    }

    private static IEnumerable<string> SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); } }
    private static IEnumerable<string> SafeFiles(string d, string pattern) { try { return Directory.GetFiles(d, pattern); } catch { return Array.Empty<string>(); } }

    private static (long size, int files, DateTime newestUtc) Measure(string dir, CancellationToken ct)
    {
        long total = 0; int files = 0; DateTime newest = DateTime.MinValue;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                total += f.Length; files++;
                if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (total, files, newest);
    }

    public static List<AppInfo> Load()
    {
        var apps = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryHive hive, RegistryView view, bool is32, bool perUser)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var un = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) return;
                foreach (var sub in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(sub);
                    if (k == null) continue;
                    string? name = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null) continue;
                    string rel = k.GetValue("ReleaseType") as string ?? "";
                    if (rel.Contains("Update", StringComparison.OrdinalIgnoreCase) || rel.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;

                    string uninstall = k.GetValue("UninstallString") as string ?? "";
                    string loc = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                    if (string.IsNullOrEmpty(loc)) loc = GuessFolder(k.GetValue("DisplayIcon") as string, uninstall);
                    DateTime? date = null;
                    if (k.GetValue("InstallDate") is string ds && DateTime.TryParseExact(ds, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;

                    var app = new AppInfo
                    {
                        Name = name.Trim(),
                        Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                        Version = k.GetValue("DisplayVersion") as string ?? "",
                        InstallDate = date,
                        InstallLocation = loc,
                        UninstallString = uninstall,
                        DisplayIcon = k.GetValue("DisplayIcon") as string ?? "",
                        RegistryKey = k.Name,
                        Is32Bit = is32,
                        PerUser = perUser,
                    };
                    if (k.GetValue("EstimatedSize") is int kb && kb > 0) app.Size = kb * 1024L;
                    string key = app.Name + "|" + app.Version;
                    if (!apps.ContainsKey(key)) apps[key] = app;
                }
            }
            catch { }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, false, false);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, true, false);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, false, true);
        return apps.Values.ToList();
    }

    private static string GuessFolder(string? icon, string uninstall)
    {
        foreach (var s in new[] { icon, uninstall })
        {
            string? exe = ExtractExe(s ?? "");
            if (exe == null || exe.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;
            string? dir = Path.GetDirectoryName(exe);
            if (dir != null && Directory.Exists(dir) && !IsSystemDir(dir)) return dir;
        }
        return "";
    }

    private static bool IsSystemDir(string dir)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] roots =
        {
            win, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
        };
        string d = dir.TrimEnd('\\');
        return roots.Any(r => d.Equals(r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            || d.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)
            || d.Contains("\\Package Cache\\", StringComparison.OrdinalIgnoreCase)
            || d.Contains("\\Installer", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractExe(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        int comma = s.IndexOf(',');            // DisplayIcon "path.exe,0"
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return s[..(exe + 4)];
        return comma > 0 ? s[..comma] : s;
    }

    /// <summary>
    /// Finds the best icon for an app: its DisplayIcon ("path,index"), else the uninstaller /
    /// an .exe in its folder, else the generic program icon. Returns a frozen image.
    /// </summary>
    public static System.Windows.Media.ImageSource? LoadIcon(AppInfo app)
    {
        var candidates = new List<(string path, int index)>();
        string di = Environment.ExpandEnvironmentVariables(app.DisplayIcon.Trim());
        if (di.Length > 0)
        {
            int index = 0;
            string path = di;
            int comma = di.LastIndexOf(',');
            if (comma > 0 && int.TryParse(di[(comma + 1)..].Trim(), out int ix)) { path = di[..comma]; index = ix; }
            candidates.Add((path.Trim().Trim('"'), index));
        }
        if (app.HasFolder)
        {
            try
            {
                // prefer an exe named like the app, then any exe that isn't an uninstaller
                var exes = Directory.GetFiles(app.InstallLocation, "*.exe");
                string first = Words(app.Name).FirstOrDefault() ?? "";
                var pick = exes.FirstOrDefault(e => first.Length > 0 && Path.GetFileNameWithoutExtension(e).Contains(first, StringComparison.OrdinalIgnoreCase))
                        ?? exes.FirstOrDefault(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                                                 && !Path.GetFileName(e).Contains("uninstall", StringComparison.OrdinalIgnoreCase));
                if (pick != null) candidates.Add((pick, 0));
            }
            catch { }
        }
        string? un = ExtractExe(app.UninstallString);
        if (un != null && !un.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) candidates.Add((un, 0));

        foreach (var (path, index) in candidates)
        {
            if (path.Length == 0 || !File.Exists(path)) continue;
            var img = NativeMethods.ExtractIcon(path, index);
            if (img != null) return img;
        }
        return NativeMethods.GenericProgramIcon();
    }

    /// <summary>Folder sizes for apps that don't report one. Runs on a background thread.</summary>
    public static void MeasureMissing(IEnumerable<AppInfo> apps, Action<AppInfo, long> report, CancellationToken ct)
    {
        foreach (var a in apps)
        {
            ct.ThrowIfCancellationRequested();
            if (a.HasSize || !a.HasFolder || IsSystemDir(a.InstallLocation)) continue;
            report(a, FolderSize(a.InstallLocation, ct));
        }
    }

    public static long FolderSize(string dir, CancellationToken ct)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                total += f.Length;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return total;
    }

    private static (long size, DateTime newestUtc) FolderSizeAndNewest(string dir, CancellationToken ct)
    {
        long total = 0;
        DateTime newest = DateTime.MinValue;
        try
        {
            var di = new DirectoryInfo(dir);
            newest = di.LastWriteTimeUtc;
            foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                total += f.Length;
                if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (total, newest);
    }

    /// <summary>Starts the app's own uninstaller and waits for it to finish.</summary>
    public static async Task<bool> UninstallAsync(AppInfo app)
    {
        string cmd = app.UninstallString.Trim();
        string file, args;
        var msi = Regex.Match(cmd, @"msiexec(\.exe)?\s+(/[IX])\s*(\{[0-9A-Fa-f\-]+\})", RegexOptions.IgnoreCase);
        if (msi.Success)
        {
            file = "msiexec.exe";
            args = "/X" + msi.Groups[3].Value;   // /I would repair; /X uninstalls
        }
        else if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            file = cmd[1..end];
            args = cmd[(end + 1)..].Trim();
        }
        else
        {
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) { file = cmd[..(exe + 4)]; args = cmd[(exe + 4)..].Trim(); }
            else { file = cmd; args = ""; }
        }
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        if (p == null) return false;
        await p.WaitForExitAsync();
        return true;
    }

    // ---------------------------------------------------------------- leftover folders

    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Microsoft Shared", "Windows Defender", "Windows NT", "Windows Mail", "Windows Media Player",
        "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell",
        "Internet Explorer", "ModifiableWindowsApps", "Reference Assemblies", "MSBuild", "dotnet", "Microsoft.NET", "Packages", "Package Cache",
        "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "PeerDistRepub", "Publishers", "Microsoft Help", "Uninstall Information",
        "Application Data", "History", "Temporary Internet Files", "VirtualStore", "PlaceholderTileLogoFolder", "Desktop", "Documents",
        "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Realtek", "Mozilla", "Google", "regid.1991-06.com.microsoft", "USOShared", "USOPrivate",
        "ssh", "Adobe", "Apple", "IsolatedStorage", "CrashDumps", "SquirrelTemp", "pip", "npm-cache", "npm", "Package Manager", "GroupPolicy",
        "Windows Kits", "Microsoft SDKs", "Microsoft Visual Studio", "Microsoft SQL Server", "IIS", "IIS Express", "Default", "Public",
        "SoftwareDistribution", "regid", "MicrosoftEdge", "Speech", "Templates", "Start Menu", "Media", "SystemTemp", "fontconfig",
        "SpaceSift.build", "Claude", "AnthropicClaude", "Claude-Data", "Claude-3p", "Docker", "Ssh",
    };

    /// <summary>
    /// Folders in Program Files / ProgramData / AppData whose name matches no installed app.
    /// A heuristic: everything here should be checked by the user before deleting.
    /// </summary>
    public static List<LeftoverFolder> FindLeftovers(List<AppInfo> apps, CancellationToken ct, long minSize = 5L << 20)
    {
        var tokens = new HashSet<string>();
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
        {
            foreach (var s in new[] { a.Name, a.Publisher })
                foreach (var w in Words(s)) tokens.Add(w);
            tokens.Add(Norm(a.Name));
            tokens.Add(Norm(a.Publisher));
            if (!string.IsNullOrEmpty(a.InstallLocation))
            {
                string loc = a.InstallLocation.TrimEnd('\\');
                locations.Add(loc);
                for (string? p = Path.GetDirectoryName(loc); p != null; p = Path.GetDirectoryName(p)) locations.Add(p);
                tokens.Add(Norm(Path.GetFileName(loc)));
            }
        }
        tokens.Remove("");

        var roots = new (string path, string label)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData\\Roaming"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppData\\Local"),
        };

        var result = new List<LeftoverFolder>();
        foreach (var (root, label) in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(d);
                if (name.StartsWith('.') || name.StartsWith('$') || Ignore.Contains(name)) continue;
                if (name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (locations.Contains(d.TrimEnd('\\'))) continue;
                try { if ((File.GetAttributes(d) & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue; } catch { continue; }
                if (MatchesAnyApp(name, tokens)) continue;

                var (size, newest) = FolderSizeAndNewest(d, ct);
                if (size < minSize) continue;
                // anything touched in the last 30 days is clearly still in use by something
                if (newest > DateTime.UtcNow.AddDays(-30)) continue;
                result.Add(new LeftoverFolder { Path = d, Name = name, Location = label, Size = size, Modified = newest.ToLocalTime() });
            }
        }
        result.Sort((a, b) => b.Size.CompareTo(a.Size));
        return result;
    }

    private static bool MatchesAnyApp(string folder, HashSet<string> tokens)
    {
        string n = Norm(folder);
        if (n.Length < 3) return true; // too short to judge: leave it alone
        if (tokens.Contains(n)) return true;
        foreach (var w in Words(folder)) if (tokens.Contains(w)) return true;
        foreach (var t in tokens)
            if (t.Length >= 4 && (n.Contains(t) || (n.Length >= 4 && t.Contains(n)))) return true;
        return false;
    }

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly HashSet<string> StopWords = new() { "the", "inc", "llc", "ltd", "corp", "corporation", "software", "and", "for", "version", "x64", "x86", "64bit", "32bit", "bit", "edition", "update", "tools", "app", "studio", "runtime", "pro", "free" };

    private static IEnumerable<string> Words(string s)
    {
        foreach (var w in Regex.Split(s, @"[^A-Za-z0-9]+"))
        {
            string l = w.ToLowerInvariant();
            if (l.Length >= 4 && !StopWords.Contains(l) && !l.All(char.IsDigit)) yield return l;
        }
    }
}
