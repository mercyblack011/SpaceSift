using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SpaceSift.Core;

namespace SpaceSift.UI;

/// <summary>A visible row of the folder tree. Created only for rows that are on screen in the flattened list.</summary>
public sealed class TreeRow : INotifyPropertyChanged
{
    private readonly ScanResult _scan;
    private readonly SizeView _view;
    private bool _expanded;

    public TreeRow(ScanResult scan, SizeView view, int index, int depth, bool hasChildren)
    {
        _scan = scan; _view = view; Index = index; Depth = depth; HasChildren = hasChildren;
    }

    public int Index { get; }
    public int Depth { get; }
    public bool HasChildren { get; }

    public bool IsExpanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); PropertyChanged?.Invoke(this, new(nameof(Glyph))); } }
    }

    private ref Entry E => ref _scan.Entries[Index];

    public string Name => _scan.GetName(Index);
    public bool IsDir => _scan.IsDir(Index);
    public string Glyph => !HasChildren ? "" : IsExpanded ? "" : "";
    public string Icon => IsDir ? "" : "";
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public bool IsDimmed => (E.Attributes & (uint)(FileAttributes.Hidden | FileAttributes.System)) != 0;

    public double PercentOfParent
    {
        get
        {
            int p = E.Parent;
            if (p < 0) return 1;
            long ps = _view.Size(p);
            return ps <= 0 ? 0 : (double)_view.Size(Index) / ps;
        }
    }
    public double BarWidth => Math.Max(0, Math.Min(1, PercentOfParent)) * 60;
    public string PercentText => Fmt.Pct(PercentOfParent);
    public long SizeValue => _view.Size(Index);
    public string Size => Fmt.Size(_view.Size(Index));
    public string Allocated => Fmt.Size(_view.Allocated(Index));
    public string Items => IsDir ? (_view.Files(Index) + E.Folders).ToString("N0") : "";
    public string Files => IsDir ? _view.Files(Index).ToString("N0") : "";
    public string Folders => IsDir ? E.Folders.ToString("N0") : "";
    public string Modified => Fmt.Date(E.Modified);
    public string Attributes => Fmt.Attr(E.Attributes);

    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name; // screen readers / UI Automation
}

public sealed class ExtRow
{
    private static readonly ConcurrentDictionary<string, string> TypeNames = new();

    public ExtRow(ExtStat s, double total, Brush brush) { Stat = s; Percent = total <= 0 ? 0 : s.Size / total; Swatch = brush; }

    public ExtStat Stat { get; }
    public Brush Swatch { get; }
    public double Percent { get; }
    public ushort Id => Stat.Id;
    public string Extension => Stat.Ext;
    public string FileType => Stat.Id == ScanBuilder.NoExtension ? "File" :
        TypeNames.GetOrAdd(Stat.Ext, e => { try { return NativeMethods.GetTypeName(e); } catch { return ""; } });
    public string PercentText => Fmt.Pct(Percent);
    public string Size => Fmt.Size(Stat.Size);
    public string Allocated => Fmt.Size(Stat.Allocated);
    public string Files => Stat.Files.ToString("N0");
    public override string ToString() => Extension;
}

public sealed class FileRow
{
    private readonly ScanResult _scan;
    public FileRow(ScanResult scan, int index) { _scan = scan; Index = index; }
    public int Index { get; }
    private ref Entry E => ref _scan.Entries[Index];
    public string Name => _scan.GetName(Index);
    public string Folder => _scan.GetPath(E.Parent);
    public string Size => Fmt.Size(E.Size);
    public string Allocated => Fmt.Size(E.Allocated);
    public string Extension => _scan.ExtName(Index);
    /// <summary>Windows' icon for this file type (cached per extension, so 500k rows stay cheap).</summary>
    public System.Windows.Media.ImageSource? Icon => NativeMethods.FileTypeIcon(Extension);
    public string Modified => Fmt.Date(E.Modified);
    public string Attributes => Fmt.Attr(E.Attributes);
    public bool IsDimmed => (E.Attributes & (uint)(FileAttributes.Hidden | FileAttributes.System)) != 0;

    /// <summary>System files and anything under Windows / Program Files can't be deleted from the list.</summary>
    public bool IsProtected
    {
        get
        {
            if ((E.Attributes & (uint)FileAttributes.System) != 0) return true;
            string n = Name.ToLowerInvariant();
            if (n is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" or "dumpstack.log.tmp") return true;
            string folder = Folder;
            return folder.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);
        }
    }
    public Visibility DeleteVisibility => IsProtected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ProtectedVisibility => IsProtected ? Visibility.Visible : Visibility.Collapsed;

    public override bool Equals(object? obj) => obj is FileRow f && f.Index == Index && ReferenceEquals(f._scan, _scan);
    public override int GetHashCode() => Index;
    public override string ToString() => Name;
}

/// <summary>
/// A read-only list over an int[] of file indices. Row objects are made on demand
/// for the rows WPF virtualisation actually shows, so 750k files cost one int each.
/// </summary>
public sealed class VirtualFileList : IList, INotifyCollectionChanged
{
    private readonly ScanResult _scan;
    private readonly int[] _items;
    private readonly int[] _posOf;
    private readonly Dictionary<int, FileRow> _cache = new();

    public VirtualFileList(ScanResult scan, int[] items)
    {
        _scan = scan; _items = items;
        _posOf = new int[scan.Count];
        Array.Fill(_posOf, -1);
        for (int k = 0; k < items.Length; k++) _posOf[items[k]] = k;
    }

    public object? this[int index]
    {
        get
        {
            int i = _items[index];
            if (_cache.TryGetValue(i, out var r)) return r;
            if (_cache.Count > 4000) _cache.Clear();
            r = new FileRow(_scan, i);
            _cache[i] = r;
            return r;
        }
        set => throw new NotSupportedException();
    }

    public int Count => _items.Length;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value) => value is FileRow f && f.Index < _posOf.Length ? _posOf[f.Index] : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) { for (int k = 0; k < Count; k++) array.SetValue(this[k], index + k); }
    public IEnumerator GetEnumerator() { for (int k = 0; k < Count; k++) yield return this[k]; }
    public event NotifyCollectionChangedEventHandler? CollectionChanged { add { } remove { } }
}
