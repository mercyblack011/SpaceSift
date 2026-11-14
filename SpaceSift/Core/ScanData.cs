using System.Runtime.InteropServices;
using System.Text;

namespace SpaceSift.Core;

[Flags]
public enum EntryFlags : byte
{
    None = 0,
    Directory = 1,
    Deleted = 2,
}

/// <summary>
/// One file or folder. A plain struct stored in one big array, so 750,000 entries
/// cost about 60 bytes each (~45 MB) instead of a managed object per file.
/// Names live in one shared char[] pool.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Entry
{
    public int Parent;          // -1 for the root
    public int NameOffset;      // into ScanResult.Names
    public ushort NameLength;
    public ushort Ext;          // extension id (files only)
    public uint Attributes;     // FileAttributes
    public long Size;           // logical size (aggregated for folders)
    public long Allocated;      // size on disk (aggregated for folders)
    public long Modified;       // UTC ticks
    public int Files;           // recursive file count (1 for a file)
    public int Folders;         // recursive folder count
    public int ChildStart;      // into ScanResult.Children
    public int ChildCount;
    public EntryFlags Flags;
}

/// <summary>Collects raw entries from a scanner. Not thread safe; scanners lock around Add.</summary>
public sealed class ScanBuilder
{
    public const ushort NoExtension = 0;
    private Entry[] _entries = new Entry[1 << 16];
    private int _count;
    private char[] _names = new char[1 << 20];
    private int _namesLen;
    private readonly Dictionary<string, ushort> _extMap = new(StringComparer.Ordinal);
    private readonly List<string> _extNames = new() { "(no extension)" };

    public int Count => _count;

    public int Add(int parent, ReadOnlySpan<char> name, bool isDir, long size, long allocated, long modifiedUtcTicks, uint attributes)
    {
        if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        if (name.Length > ushort.MaxValue) name = name[..ushort.MaxValue];
        if (_namesLen + name.Length > _names.Length)
        {
            long newLen = Math.Max((long)_names.Length * 2, _namesLen + name.Length);
            Array.Resize(ref _names, (int)Math.Min(newLen, Array.MaxLength));
        }
        name.CopyTo(_names.AsSpan(_namesLen));

        ref Entry e = ref _entries[_count];
        e.Parent = parent;
        e.NameOffset = _namesLen;
        e.NameLength = (ushort)name.Length;
        e.Attributes = attributes;
        e.Modified = modifiedUtcTicks;
        if (isDir)
        {
            e.Flags = EntryFlags.Directory;
            e.Ext = NoExtension;
        }
        else
        {
            e.Flags = EntryFlags.None;
            e.Size = size;
            e.Allocated = allocated;
            e.Ext = GetExtId(name);
        }
        _namesLen += name.Length;
        return _count++;
    }

    /// <summary>Used by the MFT scanner, which fills sizes after the entry exists.</summary>
    public ref Entry Get(int index) => ref _entries[index];

    private ushort GetExtId(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1 || name.Length - dot > 16) return NoExtension;
        Span<char> buf = stackalloc char[16];
        int n = name[dot..].ToLowerInvariant(buf);
        string ext = new string(buf[..n]);
        if (_extMap.TryGetValue(ext, out ushort id)) return id;
        if (_extNames.Count >= ushort.MaxValue) return NoExtension;
        id = (ushort)_extNames.Count;
        _extNames.Add(ext);
        _extMap[ext] = id;
        return id;
    }

    public ScanResult Build(string rootPath, long clusterSize, TimeSpan elapsed, bool usedMft)
    {
        var r = new ScanResult(_entries, _count, _names, _extNames.ToArray(), rootPath, clusterSize, elapsed, usedMft);
        _entries = Array.Empty<Entry>();
        _names = Array.Empty<char>();
        r.FinishBuild();
        return r;
    }
}

/// <summary>The finished, aggregated scan.</summary>
public sealed class ScanResult
{
    public Entry[] Entries;
    public readonly int Count;
    public readonly char[] Names;
    public readonly string[] ExtNames;
    public int[] Children = Array.Empty<int>();
    public int[] Order = Array.Empty<int>();   // breadth-first order from the root (parents before children)
    public readonly string RootPath;
    public readonly long ClusterSize;
    public readonly TimeSpan Elapsed;
    public readonly bool UsedMft;

    internal ScanResult(Entry[] entries, int count, char[] names, string[] extNames, string rootPath, long clusterSize, TimeSpan elapsed, bool usedMft)
    {
        Entries = entries; Count = count; Names = names; ExtNames = extNames;
        RootPath = rootPath; ClusterSize = clusterSize; Elapsed = elapsed; UsedMft = usedMft;
    }

    public const int Root = 0;

    public bool IsDir(int i) => (Entries[i].Flags & EntryFlags.Directory) != 0;
    public bool IsDeleted(int i) => (Entries[i].Flags & EntryFlags.Deleted) != 0;
    public string GetName(int i) => i == Root ? RootPath : new string(Names, Entries[i].NameOffset, Entries[i].NameLength);
    public ReadOnlySpan<char> NameSpan(int i) => Names.AsSpan(Entries[i].NameOffset, Entries[i].NameLength);
    public string ExtName(int i) => IsDir(i) ? "" : ExtNames[Entries[i].Ext];

    public string GetPath(int i)
    {
        if (i == Root) return RootPath;
        Span<int> chain = stackalloc int[512];
        int n = 0;
        for (int p = i; p > Root && n < chain.Length; p = Entries[p].Parent) chain[n++] = p;
        var sb = new StringBuilder(RootPath.TrimEnd('\\'));
        for (int k = n - 1; k >= 0; k--) sb.Append('\\').Append(NameSpan(chain[k]));
        if (n == 0) sb.Append('\\');
        return sb.ToString();
    }

    public ReadOnlySpan<int> GetChildren(int i) => Children.AsSpan(Entries[i].ChildStart, Entries[i].ChildCount);

    /// <summary>Index of a full path inside this scan, or -1.</summary>
    public int FindPath(string path)
    {
        string root = RootPath.TrimEnd('\\');
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return -1;
        int cur = Root;
        foreach (var part in path.Substring(root.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int next = -1;
            foreach (int c in GetChildren(cur))
                if (!IsDeleted(c) && NameSpan(c).Equals(part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next < 0) return -1;
            cur = next;
        }
        return cur;
    }

    /// <summary>Builds child lists, aggregates folder totals bottom-up and sorts children by size.</summary>
    internal void FinishBuild()
    {
        var e = Entries;
        int n = Count;

        // Counting sort of entries by parent -> contiguous child lists.
        var counts = new int[n + 1];
        for (int i = 1; i < n; i++)
        {
            int p = e[i].Parent;
            if ((uint)p < (uint)n) counts[p]++;
        }
        int acc = 0;
        for (int i = 0; i < n; i++) { e[i].ChildStart = acc; acc += counts[i]; e[i].ChildCount = 0; }
        var children = new int[acc];
        for (int i = 1; i < n; i++)
        {
            int p = e[i].Parent;
            if ((uint)p < (uint)n) children[e[p].ChildStart + e[p].ChildCount++] = i;
        }
        Children = children;

        // Breadth-first order from root; also guards against cycles / orphans (MFT oddities).
        var order = new int[n];
        var seen = new bool[n];
        int head = 0, tail = 0;
        order[tail++] = Root; seen[Root] = true;
        while (head < tail)
        {
            int cur = order[head++];
            int cs = e[cur].ChildStart, cc = e[cur].ChildCount;
            for (int k = 0; k < cc; k++)
            {
                int c = children[cs + k];
                if (seen[c]) continue;
                seen[c] = true;
                order[tail++] = c;
            }
        }
        Array.Resize(ref order, tail);
        Order = order;
        for (int i = 0; i < n; i++) if (!seen[i]) e[i].Flags |= EntryFlags.Deleted; // unreachable

        // Aggregate bottom-up.
        for (int k = tail - 1; k >= 0; k--)
        {
            int i = order[k];
            if ((e[i].Flags & EntryFlags.Directory) != 0)
            {
                long newest = e[i].Modified;
                long size = 0, alloc = 0; int files = 0, folders = 0;
                int cs = e[i].ChildStart, cc = e[i].ChildCount;
                for (int c = 0; c < cc; c++)
                {
                    ref Entry ch = ref e[children[cs + c]];
                    if ((ch.Flags & EntryFlags.Deleted) != 0) continue;
                    size += ch.Size; alloc += ch.Allocated; files += ch.Files; folders += ch.Folders;
                    if ((ch.Flags & EntryFlags.Directory) != 0) folders++;
                    if (ch.Modified > newest) newest = ch.Modified;
                }
                e[i].Size = size; e[i].Allocated = alloc; e[i].Files = files; e[i].Folders = folders;
                e[i].Modified = newest;
            }
            else
            {
                e[i].Files = 1; e[i].Folders = 0;
            }
        }

        // Sort each child list by size, largest first.
        var cmp = Comparer<int>.Create((a, b) => e[b].Size.CompareTo(e[a].Size));
        for (int i = 0; i < n; i++)
            if (e[i].ChildCount > 1) Array.Sort(children, e[i].ChildStart, e[i].ChildCount, cmp);
    }

    /// <summary>Marks an entry (and everything below it) deleted and removes its totals from all ancestors.</summary>
    public void MarkDeleted(int i)
    {
        if (i == Root || IsDeleted(i)) return;
        ref Entry d = ref Entries[i];
        long size = d.Size, alloc = d.Allocated; int files = d.Files, folders = d.Folders + (IsDir(i) ? 1 : 0);
        for (int p = d.Parent; p >= 0; p = Entries[p].Parent)
        {
            Entries[p].Size -= size; Entries[p].Allocated -= alloc;
            Entries[p].Files -= files; Entries[p].Folders -= folders;
        }
        var stack = new Stack<int>();
        stack.Push(i);
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            Entries[c].Flags |= EntryFlags.Deleted;
            foreach (int ch in GetChildren(c)) stack.Push(ch);
        }
    }
}

/// <summary>
/// Sizes after applying a file filter (category and/or extension). Folder totals are
/// recomputed in one O(n) pass using the breadth-first order.
/// </summary>
public sealed class SizeView
{
    public readonly ScanResult Scan;
    public readonly bool IsFiltered;
    private readonly long[]? _size;
    private readonly long[]? _alloc;
    private readonly int[]? _files;

    private SizeView(ScanResult scan, long[]? size, long[]? alloc, int[]? files)
    {
        Scan = scan; _size = size; _alloc = alloc; _files = files; IsFiltered = size != null;
    }

    public static SizeView Unfiltered(ScanResult scan) => new(scan, null, null, null);

    public static SizeView Create(ScanResult scan, Func<ushort, bool>? extFilter)
    {
        if (extFilter == null) return Unfiltered(scan);
        var e = scan.Entries;
        int n = scan.Count;
        var size = new long[n]; var alloc = new long[n]; var files = new int[n];
        var order = scan.Order;
        // cache filter result per extension id
        var allowed = new bool[scan.ExtNames.Length];
        for (int x = 0; x < allowed.Length; x++) allowed[x] = extFilter((ushort)x);

        for (int k = order.Length - 1; k >= 0; k--)
        {
            int i = order[k];
            if ((e[i].Flags & EntryFlags.Deleted) != 0) continue;
            if ((e[i].Flags & EntryFlags.Directory) == 0)
            {
                if (allowed[e[i].Ext]) { size[i] = e[i].Size; alloc[i] = e[i].Allocated; files[i] = 1; }
            }
            int p = e[i].Parent;
            if (p >= 0) { size[p] += size[i]; alloc[p] += alloc[i]; files[p] += files[i]; }
        }
        return new SizeView(scan, size, alloc, files);
    }

    public long Size(int i) => _size?[i] ?? Scan.Entries[i].Size;
    public long Allocated(int i) => _alloc?[i] ?? Scan.Entries[i].Allocated;
    public int Files(int i) => _files?[i] ?? Scan.Entries[i].Files;

    /// <summary>True when the entry should be shown under this view.</summary>
    public bool Visible(int i)
    {
        if (Scan.IsDeleted(i)) return false;
        if (!IsFiltered) return true;
        return _files![i] > 0;
    }
}

public sealed record ExtStat(ushort Id, string Ext, long Size, long Allocated, int Files);

public static class ExtStats
{
    public static List<ExtStat> Compute(ScanResult scan, Func<ushort, bool>? filter)
    {
        int m = scan.ExtNames.Length;
        var size = new long[m]; var alloc = new long[m]; var files = new int[m];
        var e = scan.Entries;
        for (int i = 0; i < scan.Count; i++)
        {
            if ((e[i].Flags & (EntryFlags.Directory | EntryFlags.Deleted)) != 0) continue;
            ushort x = e[i].Ext;
            size[x] += e[i].Size; alloc[x] += e[i].Allocated; files[x]++;
        }
        var list = new List<ExtStat>();
        for (int x = 0; x < m; x++)
            if (files[x] > 0 && (filter == null || filter((ushort)x)))
                list.Add(new ExtStat((ushort)x, scan.ExtNames[x], size[x], alloc[x], files[x]));
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list;
    }
}
