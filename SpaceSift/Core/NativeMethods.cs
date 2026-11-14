using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SpaceSift.Core;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;

    public static long GetClusterSize(string root)
    {
        if (GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _)) return (long)spc * bps;
        return 4096;
    }

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---- Shell: recycle bin delete ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Sends files/folders to the Recycle Bin. Returns true when nothing was aborted.</summary>
    public static bool RecycleBin(IntPtr owner, IEnumerable<string> paths)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
        };
        int rc = SHFileOperationW(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    // ---- Shell: properties dialog ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    public static void ShowProperties(IntPtr owner, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
        };
        ShellExecuteExW(ref info);
    }

    // ---- Shell: file type names ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    // ---- icons ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private static System.Windows.Media.ImageSource? FromHIcon(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DestroyIcon(h); }
    }

    /// <summary>Extracts the 32x32 icon at index (negative = resource id) from an exe, dll or ico file.</summary>
    public static System.Windows.Media.ImageSource? ExtractIcon(string file, int index)
    {
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconExW(file, index, large, null, 1) == 0 || large[0] == IntPtr.Zero)
            {
                if (index == 0) return null;
                if (ExtractIconExW(file, 0, large, null, 1) == 0) return null; // bad index: try the first icon
            }
            return FromHIcon(large[0]);
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> TypeIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The small Windows icon for a file type (e.g. ".pdf"), cached per extension.</summary>
    public static System.Windows.Media.ImageSource? FileTypeIcon(string ext)
    {
        if (TypeIcons.TryGetValue(ext, out var cached)) return cached;
        var info = new SHFILEINFO();
        const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
        string probe = "file" + (string.IsNullOrEmpty(ext) || ext.StartsWith('(') ? "" : ext);
        SHGetFileInfoW(probe, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
        var img = FromHIcon(info.hIcon);
        TypeIcons[ext] = img;
        return img;
    }

    private static System.Windows.Media.ImageSource? _generic;
    public static System.Windows.Media.ImageSource? GenericProgramIcon()
    {
        if (_generic != null) return _generic;
        var info = new SHFILEINFO();
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
        SHGetFileInfoW("program.exe", 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        return _generic = FromHIcon(info.hIcon);
    }

    public static string GetTypeName(string ext)
    {
        var info = new SHFILEINFO();
        const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
        SHGetFileInfoW("file" + ext, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        return info.szTypeName ?? "";
    }
}
