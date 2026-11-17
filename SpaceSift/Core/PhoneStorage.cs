using System.Diagnostics;

namespace SpaceSift.Core;

/// <summary>One storage of a phone or camera connected in "File transfer" (MTP) mode, e.g. "Internal shared storage".</summary>
public sealed record PhoneVolume(string Device, string Storage, string ParsingPath, long Total, long Free)
{
    /// <summary>Key used in the drive list ("phone:" + the shell path), so it never clashes with a drive letter.</summary>
    public string Key => PhoneStorage.Prefix + ParsingPath;
    public string DisplayRoot => $"{Device}\\{Storage}";
}

/// <summary>
/// Phones and cameras connected over USB in "File transfer" mode have no drive letter: Windows reaches them through
/// the shell (Windows Portable Devices). This reads their storages, sizes and files through Shell.Application.
/// Windows only offers browsing and copying for them, so SpaceSift treats phone storage as view-only.
/// </summary>
public static class PhoneStorage
{
    public const string Prefix = "phone:";
    private const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    public static bool IsPhoneKey(string? key) => key != null && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Every storage of every connected phone / camera. Shell calls need an STA thread, so this runs on one.</summary>
    public static List<PhoneVolume> List(TimeSpan timeout) => Sta(() =>
    {
        var result = new List<PhoneVolume>();
        dynamic shell = Shell();
        dynamic pc = shell.Namespace(17);           // This PC
        foreach (dynamic device in pc.Items())
        {
            string path = device.Path;
            // drives have a letter path ("C:\"); portable devices live under This PC's shell namespace
            if (!path.StartsWith(ThisPc, StringComparison.OrdinalIgnoreCase) || !(bool)device.IsFolder) continue;
            dynamic folder = device.GetFolder;
            foreach (dynamic storage in folder.Items())
            {
                if (!(bool)storage.IsFolder) continue;
                long total = Num(Prop(storage, "System.Capacity")) ?? ParseSize(folder.GetDetailsOf(storage, 2));
                long free = Num(Prop(storage, "System.FreeSpace")) ?? ParseSize(folder.GetDetailsOf(storage, 3));
                if (total <= 0) continue;           // not a storage (empty when the phone is locked)
                result.Add(new PhoneVolume((string)device.Name, (string)storage.Name, (string)storage.Path, total, free));
            }
        }
        return result;
    }, timeout, new List<PhoneVolume>());

    /// <summary>
    /// Builds the same scan result as a drive scan (folder sizes, files, dates). Uses the fast direct WPD read
    /// (one bulk request for the whole storage) and falls back to walking the shell folder by folder.
    /// </summary>
    public static ScanResult Scan(PhoneVolume volume, ScanProgress progress, CancellationToken ct)
    {
        try { return WpdScanner.Scan(volume, progress, ct); }
        catch (OperationCanceledException) { throw; }
        catch
        {
            progress.Items = 0; progress.Bytes = 0;
            return ScanViaShell(volume, progress, ct);
        }
    }

    /// <summary>The slow but universal way: the shell, one folder and one file at a time.</summary>
    public static ScanResult ScanViaShell(PhoneVolume volume, ScanProgress progress, CancellationToken ct) => Sta(() =>
    {
        var sw = Stopwatch.StartNew();
        progress.Phase = "Reading phone storage";
        var builder = new ScanBuilder();
        dynamic shell = Shell();
        dynamic rootFolder = OpenStorage(shell, volume.ParsingPath) ?? throw new IOException("The phone storage can't be opened. Unlock the phone and allow file access, then try again.");
        int root = builder.Add(-1, volume.DisplayRoot, true, 0, 0, DateTime.UtcNow.Ticks, (uint)FileAttributes.Directory);

        var stack = new Stack<(dynamic Folder, int Index)>();
        stack.Push((rootFolder, root));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, index) = stack.Pop();
            dynamic items;
            try { items = folder.Items(); } catch { continue; }
            foreach (dynamic it in items)
            {
                ct.ThrowIfCancellationRequested();
                string name;
                try { name = it.Name; } catch { continue; }
                long modified = 0;
                try { modified = ((DateTime)it.ModifyDate).ToUniversalTime().Ticks; } catch { }
                bool isDir = false;
                try { isDir = (bool)it.IsFolder; } catch { }
                if (isDir)
                {
                    int child = builder.Add(index, name, true, 0, 0, modified, (uint)FileAttributes.Directory);
                    try { stack.Push((it.GetFolder, child)); } catch { }
                }
                else
                {
                    long size = Num(Prop(it, "System.Size")) ?? 0;
                    builder.Add(index, name, false, size, size, modified, (uint)FileAttributes.Normal);
                    Interlocked.Add(ref progress.Bytes, size);
                }
                Interlocked.Increment(ref progress.Items);
            }
        }
        progress.Phase = "Building tree";
        var result = builder.Build(volume.DisplayRoot, 4096, sw.Elapsed, false);
        result.IsPhone = true;
        result.Key = volume.Key;
        result.PhoneParsingPath = volume.ParsingPath;
        return result;
    }, Timeout.InfiniteTimeSpan, null!) ?? throw new IOException("The phone storage couldn't be read.");

    /// <summary>Shows a folder (or a file's folder) of the phone in File Explorer, found by its names under the storage.</summary>
    public static void ShowInExplorer(string storageParsingPath, IReadOnlyList<string> names, bool lastIsFile)
    {
        var folderNames = lastIsFile ? names.Take(names.Count - 1).ToList() : names.ToList();
        string target = Sta(() =>
        {
            dynamic shell = Shell();
            dynamic? folder = OpenStorage(shell, storageParsingPath);
            string path = storageParsingPath;
            if (folder == null) return path;
            foreach (var n in folderNames)
            {
                dynamic? next = null;
                foreach (dynamic it in folder.Items()) if ((string)it.Name == n && (bool)it.IsFolder) { next = it; break; }
                if (next == null) break;
                path = next.Path;
                folder = next.GetFolder;
            }
            return path;
        }, TimeSpan.FromSeconds(30), storageParsingPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A phone storage can't be opened straight from its shell path: Windows needs it reached through
    /// This PC → device → storage, so walk there and match the paths.
    /// </summary>
    private static dynamic? OpenStorage(dynamic shell, string storageParsingPath)
    {
        dynamic pc = shell.Namespace(17);
        foreach (dynamic device in pc.Items())
        {
            string devicePath = device.Path;
            if (!storageParsingPath.StartsWith(devicePath + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (dynamic storage in device.GetFolder.Items())
                if (string.Equals((string)storage.Path, storageParsingPath, StringComparison.OrdinalIgnoreCase))
                    return storage.GetFolder;
        }
        return null;
    }

    private static dynamic Shell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application is not available."))!;

    private static object? Prop(dynamic item, string name)
    {
        try { return item.ExtendedProperty(name); } catch { return null; }
    }

    private static long? Num(object? v)
    {
        try { return v == null || v is DBNull ? null : Convert.ToInt64(v); } catch { return null; }
    }

    /// <summary>"105 GB" → bytes (the shell's own text, used when a property isn't available).</summary>
    private static long ParseSize(object? text)
    {
        var s = (text as string ?? "").Replace('\u200e', ' ').Replace('\u200f', ' ').Trim();
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double n)) return 0;
        long mul = parts[1].ToUpperInvariant() switch { "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 1 };
        return (long)(n * mul);
    }

    /// <summary>Shell automation objects must be used on an STA thread.</summary>
    private static T Sta<T>(Func<T> f, TimeSpan timeout, T fallback)
    {
        T result = fallback;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (timeout == Timeout.InfiniteTimeSpan) t.Join();
        else if (!t.Join(timeout)) return fallback;
        if (error is OperationCanceledException) throw error;
        if (error != null && fallback == null) throw error;
        return result;
    }
}
