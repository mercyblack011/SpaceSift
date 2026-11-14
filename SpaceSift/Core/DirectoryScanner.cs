using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace SpaceSift.Core;

public sealed class ScanProgress
{
    public long Items;
    public long Bytes;
    public double Fraction = -1; // -1 = indeterminate
    public string Phase = "";
}

/// <summary>
/// Phase 1 scanner: parallel directory enumeration using FileSystemEnumerable
/// (no FileSystemInfo objects). Skips reparse points and inaccessible folders.
/// </summary>
public static class DirectoryScanner
{
    private readonly struct Raw
    {
        public readonly string Name;
        public readonly long Length;
        public readonly long Modified;
        public readonly uint Attributes;
        public readonly bool IsDir;
        public Raw(string name, long length, long modified, uint attrs, bool isDir)
        { Name = name; Length = length; Modified = modified; Attributes = attrs; IsDir = isDir; }
    }

    public static ScanResult Scan(string root, ScanProgress progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = Path.GetFullPath(root);
        if (!root.EndsWith('\\')) root += "\\";
        long cluster = NativeMethods.GetClusterSize(root);

        var builder = new ScanBuilder();
        var gate = new object();
        int rootIndex;
        lock (gate)
            rootIndex = builder.Add(-1, root.TrimEnd('\\'), true, 0, 0, Directory.GetLastWriteTimeUtc(root).Ticks, (uint)FileAttributes.Directory);

        var queue = new ConcurrentQueue<(string Path, int Index)>();
        queue.Enqueue((root, rootIndex));
        int pending = 1;
        progress.Phase = "Scanning folders";

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,          // include hidden + system files
            BufferSize = 64 * 1024,
        };

        void Worker()
        {
            var local = new List<Raw>(256);
            var spin = new SpinWait();
            while (true)
            {
                if (ct.IsCancellationRequested) return;
                if (!queue.TryDequeue(out var job))
                {
                    if (Volatile.Read(ref pending) == 0) return;
                    spin.SpinOnce(sleep1Threshold: 20);
                    continue;
                }
                spin.Reset();
                local.Clear();
                try
                {
                    var en = new FileSystemEnumerable<Raw>(job.Path,
                        (ref FileSystemEntry fe) => new Raw(
                            fe.FileName.ToString(),
                            fe.IsDirectory ? 0 : fe.Length,
                            fe.LastWriteTimeUtc.UtcTicks,
                            (uint)fe.Attributes,
                            fe.IsDirectory),
                        options);
                    foreach (var r in en) local.Add(r);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // inaccessible: keep folder with zero size
                }

                long bytes = 0;
                lock (gate)
                {
                    foreach (var r in local)
                    {
                        long alloc = r.IsDir ? 0 : RoundUp(r.Length, cluster, r.Attributes);
                        int idx = builder.Add(job.Index, r.Name, r.IsDir, r.Length, alloc, r.Modified, r.Attributes);
                        bytes += r.Length;
                        bool reparse = (r.Attributes & (uint)FileAttributes.ReparsePoint) != 0;
                        if (r.IsDir && !reparse)
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue((job.Path + r.Name + "\\", idx));
                        }
                    }
                }
                Interlocked.Add(ref progress.Items, local.Count);
                Interlocked.Add(ref progress.Bytes, bytes);
                Interlocked.Decrement(ref pending);
            }
        }

        int workers = Math.Clamp(Environment.ProcessorCount, 2, 16);
        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++) tasks[w] = Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning);
        Task.WaitAll(tasks);
        ct.ThrowIfCancellationRequested();

        progress.Phase = "Building tree";
        return builder.Build(root, cluster, sw.Elapsed, usedMft: false);
    }

    /// <summary>Estimated size on disk. Compressed/sparse files are an estimate in this mode; MFT mode is exact.</summary>
    private static long RoundUp(long length, long cluster, uint attrs)
    {
        if (length <= 0) return 0;
        // very small files usually live inside the MFT record on NTFS
        if (length < 700 && cluster >= 4096) return 0;
        return (length + cluster - 1) / cluster * cluster;
    }
}
