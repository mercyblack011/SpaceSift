// File conversion for SpaceSift, powered by Filee's engine (MIT, https://github.com/KnifeLemon/Filee).
// SpaceSift only drives it: picks the formats a file can become, runs a job and reports progress.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines;
using Filee.Engines.Infrastructure;

namespace SpaceSift.Core;

/// <summary>A format a file can be converted to, as shown on the donut.</summary>
/// <param name="NeedsPackage">An optional engine (e.g. "ffmpeg") that must be downloaded first, or null.</param>
public sealed record ConvertTarget(string Id, string Label, FormatCategory Category, FormatGrade Grade, string? NeedsPackage);

/// <summary>What happened to one file.</summary>
public sealed record ConvertFileResult(string Source, bool Ok, IReadOnlyList<string> Outputs, string? Error);

public static class Converter
{
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static ConverterCatalog? _catalog;
    private static JobQueue? _queue;
    private static RoutePlanner? _planner;
    private static readonly Lazy<Texts> _texts = new(() => new Texts());

    /// <summary>Where Filee's engine keeps its state and downloaded engines (FFmpeg, LibreOffice …).</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceSift", "convert");

    public static ILocalizer Localizer => _texts.Value;

    /// <summary>Loads every engine once (a second or two the first time).</summary>
    public static async Task InitAsync()
    {
        if (_queue != null) return;
        await InitLock.WaitAsync();
        try
        {
            if (_queue != null) return;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(DataDirectory);
                EngineEnvironment.DownloadRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceSift", "engines");
                var converters = EngineRegistry.CreateAll(new EngineEnvironment(DataDirectory));
                var catalog = new ConverterCatalog(converters);
                catalog.CheckInBackgroundAsync().Wait();
                _catalog = catalog;
                _planner = catalog.CreatePlanner();
                _queue = new JobQueue(catalog, EngineRegistry.FindPdfMerger(converters), combiner: EngineRegistry.FindFileCombiner(converters),
                    tiffMerger: EngineRegistry.FindTiffMerger(converters), mediaJoiner: EngineRegistry.FindMediaJoiner(converters));
            });
        }
        finally { InitLock.Release(); }
    }

    /// <summary>The format of a file, or null when Filee doesn't know it.</summary>
    public static FileFormat? FormatOf(string path) => FormatRegistry.Detect(path);

    /// <summary>
    /// Formats every one of <paramref name="files"/> can become, most common first. Formats that only work after an
    /// optional engine download are included with <see cref="ConvertTarget.NeedsPackage"/> set.
    /// </summary>
    public static List<ConvertTarget> TargetsFor(IReadOnlyList<string> files)
    {
        if (_catalog == null || _planner == null || files.Count == 0) return new();
        var sourceFormats = files.Select(FormatOf).ToList();
        if (sourceFormats.Any(s => s == null)) return new();
        var sources = sourceFormats.Select(s => s!.Id).Distinct().ToList();
        var kinds = sourceFormats.Select(s => s!).DistinctBy(s => s.Id).ToList();

        // planners that pretend one optional engine is installed, to say which download a format needs
        var installer = new EngineInstaller(EngineInstaller.CreateHttpClient(TimeSpan.FromSeconds(30)));
        var optional = EngineDownloads.Packages
            .Where(p => EngineDownloads.CanDownload(p) && !installer.IsInstalled(p))
            .Select(p => (p.Id, Planner: _catalog.CreatePlanner(p.ConverterIds)))
            .ToList();

        var result = new List<ConvertTarget>();
        foreach (var f in FormatRegistry.Known)
        {
            if (!f.Writable || sources.Contains(f.Id)) continue;
            FormatGrade grade;
            try { grade = FormatFields.GradeOf(f); }
            catch (KeyNotFoundException) { continue; } // pseudo-formats like "folder" aren't conversion targets
            // only formats that make sense for every picked file (a PNG gets pictures and PDF, not Word or HWP)
            if (!kinds.All(k => IsRelevant(k, f, grade))) continue;
            if (sources.All(s => Plan(_planner, s!, f.Id)))
            {
                result.Add(new ConvertTarget(f.Id, Label(f), f.Category, grade, null));
                continue;
            }
            foreach (var (id, planner) in optional)
                if (sources.All(s => Plan(planner, s!, f.Id)))
                {
                    result.Add(new ConvertTarget(f.Id, Label(f), f.Category, grade, id));
                    break;
                }
        }
        var first = FormatOf(files[0])!.Category;
        // front of the donut: common formats of the same kind (photo → JPG, WebP …), then PDF, then the rest
        int Rank(ConvertTarget t)
        {
            bool same = t.Category == first;
            if (same && t.Grade == FormatGrade.Everyday) return 0;
            if (t.Id == "pdf") return 1;
            if (same && t.Grade == FormatGrade.General) return 2;
            if (t.Grade == FormatGrade.Everyday) return 3;
            if (t.Grade == FormatGrade.General) return 4;
            return 5;
        }
        return result
            .OrderBy(t => t.NeedsPackage != null)               // ready to use first
            .ThenBy(Rank)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ relevance rules
    //
    // Filee can route almost anything to almost anything (a photo → Word, via OCR). The ring only offers what people
    // expect for that kind of file: its own family plus the obvious cross-overs, never rare "specialist" formats.

    private static readonly Dictionary<FormatCategory, (FormatCategory[] Families, string[] Extra)> Rules = new()
    {
        [FormatCategory.Image]        = (new[] { FormatCategory.Image, FormatCategory.Vector }, new[] { "pdf" }),
        [FormatCategory.Vector]       = (new[] { FormatCategory.Vector, FormatCategory.Image }, new[] { "pdf" }),
        [FormatCategory.Pdf]          = (new[] { FormatCategory.Image, FormatCategory.Document, FormatCategory.Text, FormatCategory.Presentation }, Array.Empty<string>()),
        [FormatCategory.Document]     = (new[] { FormatCategory.Document, FormatCategory.Text, FormatCategory.Ebook }, new[] { "pdf" }),
        [FormatCategory.Text]         = (new[] { FormatCategory.Text, FormatCategory.Document, FormatCategory.Ebook }, new[] { "pdf" }),
        [FormatCategory.Hwp]          = (new[] { FormatCategory.Hwp, FormatCategory.Document, FormatCategory.Text }, new[] { "pdf" }),
        [FormatCategory.Spreadsheet]  = (new[] { FormatCategory.Spreadsheet }, new[] { "pdf", "csv" }),
        [FormatCategory.Presentation] = (new[] { FormatCategory.Presentation, FormatCategory.Image }, new[] { "pdf" }),
        [FormatCategory.Video]        = (new[] { FormatCategory.Video, FormatCategory.Audio }, new[] { "gif" }),
        [FormatCategory.Audio]        = (new[] { FormatCategory.Audio }, Array.Empty<string>()),
        [FormatCategory.Ebook]        = (new[] { FormatCategory.Ebook, FormatCategory.Document, FormatCategory.Text }, new[] { "pdf" }),
        [FormatCategory.Archive]      = (new[] { FormatCategory.Archive }, Array.Empty<string>()),
        [FormatCategory.Cad]          = (new[] { FormatCategory.Cad, FormatCategory.Vector }, new[] { "pdf", "png" }),
        [FormatCategory.Font]         = (new[] { FormatCategory.Font }, Array.Empty<string>()),
        [FormatCategory.Subtitle]     = (new[] { FormatCategory.Subtitle, FormatCategory.Text }, Array.Empty<string>()),
        [FormatCategory.Data]         = (new[] { FormatCategory.Data, FormatCategory.Spreadsheet }, Array.Empty<string>()),
        [FormatCategory.Model3D]      = (new[] { FormatCategory.Model3D }, Array.Empty<string>()),
        [FormatCategory.Geo]          = (new[] { FormatCategory.Geo, FormatCategory.Data }, Array.Empty<string>()),
    };

    /// <summary>
    /// Never offered as a target: templates and macro variants (DOTX, DOCM …), niche containers and drawing documents
    /// that people don't convert *to* in everyday use.
    /// </summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "docm", "dotm", "dotx", "ott", "xlsm", "xltx", "ots", "pptm", "potm", "potx", "otp",
        "odg", "emf", "svgz", "tga", "opml", "kepub", "fb2", "cbz", "azw3",
        "m4b", "m4r", "mka", "weba", "m2ts", "ts", "ogv", "mpg",
    };

    /// <summary>True when <paramref name="target"/> is a sensible result for a file of <paramref name="source"/>.</summary>
    public static bool IsRelevant(FileFormat source, FileFormat target, FormatGrade grade)
    {
        if (grade is FormatGrade.Specialist or FormatGrade.Legacy) return false;          // PSD, EXR, DICOM, … stay hidden
        if (Hidden.Contains(target.Id)) return false;
        if (target.Category == FormatCategory.Hwp && source.Category != FormatCategory.Hwp) return false; // Korean office files only for HWP
        if (!Rules.TryGetValue(source.Category, out var rule)) return target.Category == source.Category;
        return rule.Families.Contains(target.Category) || rule.Extra.Contains(target.Id);
    }

    private static bool Plan(RoutePlanner planner, string from, string to)
    {
        try { return planner.Plan(from, to) != null; } catch { return false; }
    }

    /// <summary>Short name for a slice: "JPG", "PDF", "Word (DOCX)".</summary>
    private static string Label(FileFormat f) => f.Id.Length <= 5 ? f.Id.ToUpperInvariant() : f.DisplayName;

    /// <summary>Converts the files to <paramref name="targetId"/>; results are saved next to the originals.</summary>
    public static async Task<List<ConvertFileResult>> ConvertAsync(IReadOnlyList<string> files, string targetId,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await InitAsync();
        var preset = new Preset { TargetFormat = targetId };
        var job = new ConversionJob(files, preset, targetId.ToUpperInvariant());
        void Updated(object? sender, ConversionJob j) { if (j == job) progress?.Report(job.Progress); }
        _queue!.JobUpdated += Updated;
        try
        {
            using var _ = ct.Register(job.Cancel);
            await _queue.RunAsync(job);
        }
        finally { _queue.JobUpdated -= Updated; }
        progress?.Report(1);
        return job.Files.Select(f => new ConvertFileResult(f.SourcePath, f.State is FileState.Done or FileState.Skipped,
            f.Outputs.ToList(), f.State is FileState.Done ? null : Error(f))).ToList();
    }

    private static string Error(FileResult f)
    {
        if (f.State == FileState.Skipped) return "A file with that name already exists, so it was skipped.";
        if (f.State == FileState.Cancelled) return "Cancelled.";
        string msg = f.ErrorKey == null ? "Conversion failed." : Localizer[f.ErrorKey];
        return string.IsNullOrWhiteSpace(f.ErrorDetail) ? msg : $"{msg} ({f.ErrorDetail})";
    }

    // ------------------------------------------------------------------ optional engines (FFmpeg, LibreOffice …)

    public static EnginePackage? Package(string id) => EngineDownloads.Packages.FirstOrDefault(p => p.Id == id);

    public static long DownloadSize(EnginePackage p) => EngineDownloads.DownloadSize(p);

    /// <summary>Downloads and installs an optional engine, then reloads the engines so it is used.</summary>
    public static async Task InstallAsync(EnginePackage package, IProgress<double>? progress, CancellationToken ct)
    {
        var installer = new EngineInstaller(EngineInstaller.CreateHttpClient(Timeout.InfiniteTimeSpan));
        await installer.InstallAsync(package, new Progress<EngineInstallProgress>(p => progress?.Report(p.Fraction)), ct);
        _catalog?.Refresh();
        if (_catalog != null) _planner = _catalog.CreatePlanner();
    }

    /// <summary>Filee's English texts (error messages), from its translation file embedded in SpaceSift.</summary>
    private sealed class Texts : ILocalizer
    {
        private readonly Dictionary<string, string> _texts;

        public Texts()
        {
            using var stream = typeof(Converter).Assembly.GetManifestResourceStream("SpaceSift.filee.en.json");
            if (stream == null) { _texts = new(); return; }
            using var json = JsonDocument.Parse(stream);
            _texts = json.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }

        public string Language => "en";
        public string this[string key] => _texts.GetValueOrDefault(key, key);
        public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, this[key], args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
