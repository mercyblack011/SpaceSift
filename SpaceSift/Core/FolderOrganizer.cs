using System.Text.RegularExpressions;

namespace SpaceSift.Core;

/// <summary>One group of files that will go into one folder, e.g. "Audio (5 GB)".</summary>
public sealed class OrganizeGroup
{
    public required string Name { get; init; }      // "Audio"
    public required string Glyph { get; init; }
    public required string Hint { get; init; }      // "mp3, wav, flac"
    public List<string> Files { get; } = new();
    public long Size { get; set; }
    public string ExistingFolder { get; set; } = ""; // a folder from an earlier run to merge into
    public string FolderName => $"{Name} ({Fmt.Size(Size + ExistingSize)})";
    public long ExistingSize { get; set; }
    public string SizeText => Fmt.Size(Size);
    public string CountText => Files.Count == 1 ? "1 file" : $"{Files.Count:N0} files";
    public string Example => string.Join(", ", Files.Take(3).Select(Path.GetFileName)) + (Files.Count > 3 ? ", …" : "");
}

/// <summary>A record of moves so an organize can be undone.</summary>
public sealed record MoveRecord(string From, string To);

public sealed record OrganizeResult(int Moved, long Bytes, List<string> Failed, List<MoveRecord> Moves, List<string> CreatedFolders, Dictionary<string, string> Renamed);

/// <summary>
/// Sorts the loose files in a folder into one sub-folder per kind of file,
/// named in plain words with the size: "Pictures (1.2 GB)", "Audio (5 GB)".
/// Only files directly in the folder are moved; existing sub-folders are left alone.
/// </summary>
public static class FolderOrganizer
{
    private sealed record Kind(string Name, string Glyph, string Exts);

    // order = display order; names are what a person would call them
    private static readonly Kind[] Kinds =
    {
        new("Pictures", "", "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 nef arw dng svg ico avif jfif"),
        new("Videos", "", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob"),
        new("Audio", "", "mp3 wav flac aac ogg wma m4a opus aiff mid midi amr"),
        new("Documents", "", "doc docx odt rtf txt md pages wpd"),
        new("PDF files", "", "pdf"),
        new("Spreadsheets", "", "xls xlsx xlsm ods csv numbers"),
        new("Presentations", "", "ppt pptx odp key"),
        new("E-books", "", "epub mobi azw azw3 djvu"),
        new("Archives", "", "zip rar 7z tar gz bz2 xz tgz zst cab"),
        new("Disk images", "", "iso img vhd vhdx wim esd dmg"),
        new("Installers and apps", "", "exe msi msix msixbundle appx appxbundle apk"),
        new("Code", "", "cs js ts jsx tsx py java c cpp h hpp go rs rb php html htm css scss json xml yml yaml sql sh ps1 bat cmd kt swift dart ipynb"),
        new("Design files", "", "psd ai xd fig sketch indd blend fbx obj stl dwg dxf rvt"),
        new("Fonts", "", "ttf otf woff woff2 fon"),
        new("Torrents", "", "torrent"),
        new("Shortcuts", "", "lnk url"),
    };

    private static readonly Dictionary<string, Kind> ByExt = BuildMap();
    private static Dictionary<string, Kind> BuildMap()
    {
        var d = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Kinds) foreach (var e in k.Exts.Split(' ')) d["." + e] = k;
        return d;
    }
    private static readonly Kind OtherKind = new("Other files", "", "");

    /// <summary>Matches folders this tool made earlier, e.g. "Audio (5.2 GB)".</summary>
    private static readonly Regex OurFolder = new(@"^(?<name>.+?) \((?<size>[\d.,]+ (B|KB|MB|GB|TB))\)$", RegexOptions.CultureInvariant);

    /// <summary>Folders that must never be reorganized (would break Windows or apps).</summary>
    public static string? Blocked(string folder)
    {
        string f = Path.GetFullPath(folder).TrimEnd('\\');
        if (Path.GetPathRoot(f)?.TrimEnd('\\') == f) return "a whole drive";
        foreach (var sf in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                   Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string p = Environment.GetFolderPath(sf).TrimEnd('\\');
            if (p.Length > 0 && (f.Equals(p, StringComparison.OrdinalIgnoreCase) || f.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)))
                return "a Windows or program folder";
        }
        if (f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "your user folder";
        return null;
    }

    /// <summary>Groups the loose files in <paramref name="folder"/> (nothing is moved).</summary>
    public static List<OrganizeGroup> Plan(string folder)
    {
        var groups = new Dictionary<string, OrganizeGroup>();
        OrganizeGroup Group(Kind k) => groups.TryGetValue(k.Name, out var g) ? g
            : groups[k.Name] = new OrganizeGroup { Name = k.Name, Glyph = k.Glyph, Hint = k.Exts.Length == 0 ? "everything else" : string.Join(", ", k.Exts.Split(' ').Take(5)) };

        foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
        {
            if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue; // desktop.ini, thumbs.db …
            var kind = ByExt.TryGetValue(f.Extension, out var k) ? k : OtherKind;
            var g = Group(kind);
            g.Files.Add(f.FullName);
            g.Size += f.Length;
        }
        // folders from an earlier run: new files merge into them and the size in the name is updated
        foreach (var d in new DirectoryInfo(folder).EnumerateDirectories())
        {
            var m = OurFolder.Match(d.Name);
            if (!m.Success || !groups.TryGetValue(m.Groups["name"].Value, out var g)) continue;
            g.ExistingFolder = d.FullName;
            g.ExistingSize = FolderSize(d.FullName);
        }
        var order = Kinds.Append(OtherKind).Select(k => k.Name).ToList();
        return groups.Values.OrderBy(g => order.IndexOf(g.Name)).ToList();
    }

    /// <summary>Moves every file into its group folder, then names each folder with its final size.</summary>
    public static OrganizeResult Organize(string folder, IEnumerable<OrganizeGroup> groups, IProgress<double>? progress = null)
    {
        var list = groups.Where(g => g.Files.Count > 0).ToList();
        int total = list.Sum(g => g.Files.Count), done = 0, moved = 0;
        long bytes = 0;
        var failed = new List<string>(); var moves = new List<MoveRecord>(); var created = new List<string>();
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in list)
        {
            // work in a temporary name first, then rename once the real size is known
            string work = g.ExistingFolder.Length > 0 ? g.ExistingFolder : Unique(Path.Combine(folder, g.Name));
            if (!Directory.Exists(work)) { Directory.CreateDirectory(work); created.Add(work); }
            foreach (var src in g.Files)
            {
                try
                {
                    long len = new FileInfo(src).Length;
                    string dst = Unique(Path.Combine(work, Path.GetFileName(src)));
                    File.Move(src, dst);
                    moves.Add(new MoveRecord(src, dst));
                    moved++; bytes += len;
                }
                catch { failed.Add(src); } // in use or no permission
                progress?.Report(++done / (double)Math.Max(1, total));
            }
            // final name with the folder's size: "Audio (5 GB)"
            long size = FolderSize(work);
            string final = Path.Combine(folder, $"{g.Name} ({Fmt.Size(size)})");
            if (!string.Equals(final, work, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(final) || File.Exists(final)) final = Unique(final);
                try
                {
                    Directory.Move(work, final);
                    renamed[final] = work;
                    for (int i = 0; i < moves.Count; i++)
                        if (moves[i].To.StartsWith(work + "\\", StringComparison.OrdinalIgnoreCase))
                            moves[i] = moves[i] with { To = final + moves[i].To.Substring(work.Length) };
                    int c = created.IndexOf(work);
                    if (c >= 0) created[c] = final;
                }
                catch { /* keep the plain name if the rename fails */ }
            }
        }
        return new OrganizeResult(moved, bytes, failed, moves, created, renamed);
    }

    /// <summary>Puts every file back where it was and removes the folders the organize created (if empty).</summary>
    public static int Undo(OrganizeResult r)
    {
        int back = 0;
        foreach (var m in Enumerable.Reverse(r.Moves))
        {
            try
            {
                if (!File.Exists(m.To) || File.Exists(m.From)) continue;
                File.Move(m.To, m.From);
                back++;
            }
            catch { }
        }
        foreach (var d in r.CreatedFolders)
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        // merged folders from an earlier run get their old name back
        foreach (var (now, before) in r.Renamed)
            try { if (Directory.Exists(now) && !Directory.Exists(before) && !r.CreatedFolders.Contains(now)) Directory.Move(now, before); } catch { }
        return back;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        // a folder name like "Audio" has no real extension
        if (Directory.Exists(path) || ext.Contains(' ')) { name = Path.GetFileName(path); ext = ""; }
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public static long FolderSize(string dir)
    {
        long s = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                s += f.Length;
        }
        catch { }
        return s;
    }
}
