using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpaceSift.Core;

/// <summary>A running program that holds one of the files open.</summary>
public sealed record LockingProcess(int Id, string Name, string Path, bool Critical);

/// <summary>
/// Force delete: finds the programs that keep files open (Windows Restart Manager),
/// closes them, then deletes the files permanently. Files that stay locked can be
/// scheduled for removal at the next restart (administrator only).
/// </summary>
public static class ForceDelete
{
    // processes Windows cannot run without; never closed
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm",
        "fontdrvhost", "Registry", "Memory Compression", "spoolsv", "MsMpEng", "SecurityHealthService", "sihost", "ctfmon",
    };

    /// <summary>All files under the given paths (folders are expanded), capped so Restart Manager stays fast.</summary>
    public static List<string> ExpandFiles(IEnumerable<string> paths, int cap = 20000)
    {
        var list = new List<string>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var p in paths)
        {
            if (File.Exists(p)) list.Add(p);
            else if (Directory.Exists(p))
                try { foreach (var f in Directory.EnumerateFiles(p, "*", opts)) { list.Add(f); if (list.Count >= cap) return list; } } catch { }
            if (list.Count >= cap) break;
        }
        return list;
    }

    /// <summary>Programs that have any of the files open.</summary>
    public static List<LockingProcess> FindLockers(IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var found = new Dictionary<int, LockingProcess>();
        int self = Environment.ProcessId;
        // Restart Manager handles a few hundred files per call comfortably
        for (int start = 0; start < files.Count; start += 500)
        {
            ct.ThrowIfCancellationRequested();
            var batch = files.Skip(start).Take(500).ToArray();
            if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0) break;
            try
            {
                if (RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null) != 0) continue;
                uint needed = 0, count = 0, reasons = 0;
                int r = RmGetList(session, out needed, ref count, null, ref reasons);
                if (r != ERROR_MORE_DATA || needed == 0) continue;
                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) continue;
                for (int i = 0; i < count; i++)
                {
                    int pid = infos[i].Process.dwProcessId;
                    if (pid == self || found.ContainsKey(pid)) continue;
                    string name = infos[i].strAppName, path = "";
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        name = string.IsNullOrWhiteSpace(name) ? proc.ProcessName : name;
                        try { path = proc.MainModule?.FileName ?? ""; } catch { }
                        bool critical = CriticalNames.Contains(proc.ProcessName) || infos[i].ApplicationType == RM_APP_TYPE.RmCritical;
                        found[pid] = new LockingProcess(pid, name, path, critical);
                    }
                    catch { /* already exited */ }
                }
            }
            finally { RmEndSession(session); }
        }
        return found.Values.OrderBy(p => p.Name).ToList();
    }

    /// <summary>Closes the programs: politely first (close window), then forcefully.</summary>
    public static List<string> CloseProcesses(IEnumerable<LockingProcess> procs)
    {
        var errors = new List<string>();
        foreach (var lp in procs)
        {
            if (lp.Critical) { errors.Add($"{lp.Name} is part of Windows and was not closed."); continue; }
            try
            {
                using var p = Process.GetProcessById(lp.Id);
                bool explorer = p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
                if (!explorer && p.CloseMainWindow() && p.WaitForExit(2500)) continue;
                p.Kill(entireProcessTree: false);
                p.WaitForExit(4000);
                if (explorer) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); // bring the taskbar back
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) { errors.Add($"{lp.Name}: {ex.Message}"); }
        }
        return errors;
    }

    public sealed record Outcome(long Freed, List<string> Deleted, List<string> Failed, List<string> ScheduledForReboot);

    /// <summary>Deletes permanently, retrying briefly while closed programs release their handles.</summary>
    public static Outcome Delete(IReadOnlyList<string> paths, Func<string, long> sizeOf, IProgress<double>? progress = null)
    {
        var deleted = new List<string>(); var failed = new List<string>(); var reboot = new List<string>();
        long freed = 0;
        for (int k = 0; k < paths.Count; k++)
        {
            string p = paths[k];
            long size = sizeOf(p);
            for (int attempt = 0; attempt < 4 && ShellOps.Exists(p); attempt++)
            {
                ShellOps.DeletePermanently(new[] { p });
                if (ShellOps.Exists(p)) Thread.Sleep(400);
            }
            if (!ShellOps.Exists(p)) { deleted.Add(p); freed += size; }
            else if (NativeMethods.IsAdministrator() && ScheduleOnReboot(p)) reboot.Add(p);
            else failed.Add(p);
            progress?.Report((k + 1.0) / paths.Count);
        }
        return new Outcome(freed, deleted, failed, reboot);
    }

    /// <summary>MoveFileEx(DELAY_UNTIL_REBOOT): Windows removes it before anything can lock it again.</summary>
    private static bool ScheduleOnReboot(string path)
    {
        bool ok = true;
        if (Directory.Exists(path))
        {
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var f in Directory.EnumerateFiles(path, "*", opts)) ok &= MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                foreach (var d in Directory.EnumerateDirectories(path, "*", opts).OrderByDescending(x => x.Length))
                    ok &= MoveFileEx(d, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch { ok = false; }
        }
        return MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT) && ok;
    }

    // ------------------------------------------------------------- Restart Manager

    private const int ERROR_MORE_DATA = 234;
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    private enum RM_APP_TYPE { RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2, RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000 }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
}
