using System.Windows.Media;

namespace SpaceSift.Core;

public enum FileCategory { All, Applications, Documents, Images, Videos, Audio, Archives, System, Other }

public static class Categories
{
    private static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase);

    static Categories()
    {
        void Add(FileCategory c, string list) { foreach (var e in list.Split(' ')) Map["." + e] = c; }
        Add(FileCategory.Applications, "exe msi msix appx appxbundle bat cmd ps1 com jar apk vbs scr");
        Add(FileCategory.Documents, "doc docx xls xlsx ppt pptx pdf txt rtf odt ods odp csv md epub xml json html htm log one pub");
        Add(FileCategory.Images, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw svg ico psd ai dng");
        Add(FileCategory.Videos, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob");
        Add(FileCategory.Audio, "mp3 wav flac aac ogg wma m4a opus aiff mid midi");
        Add(FileCategory.Archives, "zip rar 7z tar gz bz2 xz iso cab tgz zst img vhd vhdx wim esd");
        Add(FileCategory.System, "dll sys drv cat mui ocx cpl etl evtx dat bin pak tmp ini inf manifest pdb lib so pf db regtrans-ms blf mum edb pri msp");
    }

    public static FileCategory Of(string ext) => Map.TryGetValue(ext, out var c) ? c : FileCategory.Other;

    public static Func<ushort, bool>? Filter(ScanResult scan, FileCategory cat)
    {
        if (cat == FileCategory.All) return null;
        var names = scan.ExtNames;
        var ok = new bool[names.Length];
        for (int i = 0; i < names.Length; i++) ok[i] = Of(names[i]) == cat;
        return x => ok[x];
    }
}

/// <summary>One colour per extension, shared by the extension list, chart and treemap.</summary>
public sealed class ExtColors
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4E,0x79,0xA7), Color.FromRgb(0xF2,0x8E,0x2B), Color.FromRgb(0xE1,0x57,0x59), Color.FromRgb(0x76,0xB7,0xB2),
        Color.FromRgb(0x59,0xA1,0x4F), Color.FromRgb(0xED,0xC9,0x48), Color.FromRgb(0xB0,0x7A,0xA1), Color.FromRgb(0xFF,0x9D,0xA7),
        Color.FromRgb(0x9C,0x75,0x5F), Color.FromRgb(0x86,0xBC,0xB6), Color.FromRgb(0x8C,0xD1,0x7D), Color.FromRgb(0xD3,0x72,0x95),
        Color.FromRgb(0x49,0x98,0x94), Color.FromRgb(0xF1,0xCE,0x63), Color.FromRgb(0xA0,0xCB,0xE8), Color.FromRgb(0xFA,0xBF,0xD2),
        Color.FromRgb(0x79,0x70,0x6E), Color.FromRgb(0xD4,0xA6,0xC8), Color.FromRgb(0xB6,0x99,0x2D), Color.FromRgb(0x5B,0x8F,0xD8),
    };

    public readonly Color[] Colors;
    public readonly SolidColorBrush[] Brushes;

    public ExtColors(ScanResult scan)
    {
        var all = ExtStats.Compute(scan, null);
        int m = scan.ExtNames.Length;
        Colors = new Color[m];
        for (int x = 0; x < m; x++) Colors[x] = HashColor(scan.ExtNames[x]);
        for (int k = 0; k < all.Count && k < Palette.Length; k++) Colors[all[k].Id] = Palette[k];
        Colors[ScanBuilder.NoExtension] = Color.FromRgb(0x9A, 0x9A, 0x9A);
        Brushes = new SolidColorBrush[m];
        for (int x = 0; x < m; x++) { Brushes[x] = new SolidColorBrush(Colors[x]); Brushes[x].Freeze(); }
    }

    private static Color HashColor(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        double hue = h % 360;
        return FromHsv(hue, 0.45, 0.80);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

public static class Fmt
{
    public static string Size(long b)
    {
        if (b < 1024) return b + " B";
        string[] u = { "KB", "MB", "GB", "TB", "PB" };
        double v = b; int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0") + " " + u[i];
    }

    public static string Pct(double p) => (p * 100).ToString("0.0") + " %";

    public static string Date(long ticks) => ticks <= 0 ? "" : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string Attr(uint a)
    {
        Span<char> s = stackalloc char[3];
        int n = 0;
        if ((a & (uint)FileAttributes.ReadOnly) != 0) s[n++] = 'R';
        if ((a & (uint)FileAttributes.Hidden) != 0) s[n++] = 'H';
        if ((a & (uint)FileAttributes.System) != 0) s[n++] = 'S';
        return new string(s[..n]);
    }
}
