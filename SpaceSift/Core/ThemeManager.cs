using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpaceSift.Core;

public enum ThemeMode { System, Light, Dark }

/// <summary>Swaps the Dark / Light brush dictionaries, remembers the choice and tints the title bar.</summary>
public static class ThemeManager
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceSift", "theme.txt");

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;

    public static void Init()
    {
        try
        {
            if (File.Exists(SettingsFile) && Enum.TryParse(File.ReadAllText(SettingsFile).Trim(), true, out ThemeMode m)) Mode = m;
        }
        catch { }
        Apply(Mode, save: false);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System, save: false));
        };
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Toggle() => Apply(IsDark ? ThemeMode.Light : ThemeMode.Dark, save: true);

    public static void Apply(ThemeMode mode, bool save)
    {
        Mode = mode;
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        var dicts = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri($"/SpaceSift;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        // slot 0 is always the colour theme; slot 1 the control styles
        if (dicts.Count > 0) dicts[0] = theme; else dicts.Add(theme);
        foreach (Window w in Application.Current.Windows) TintTitleBar(w);
        if (save)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllText(SettingsFile, mode.ToString()); } catch { }
        }
        Changed?.Invoke();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>
    /// Glass needs Windows 11 22H2+ (system backdrop API) and the user's "Transparency effects"
    /// setting on. Otherwise we fall back to solid surfaces (reduced-transparency behaviour).
    /// </summary>
    public static bool GlassSupported
    {
        get
        {
            if (Environment.OSVersion.Version.Build < 22621) return false;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("EnableTransparency") is not int t || t != 0;
            }
            catch { return true; }
        }
    }

    /// <summary>
    /// Dark/light title bar, plus the Windows Acrylic material behind the whole window (Win11).
    /// WPF draws the semi-transparent "Backdrop" tint on top, so content stays legible.
    /// </summary>
    public static void TintTitleBar(Window w)
    {
        if (w.AllowsTransparency) return; // custom popups draw their own frame
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, 4);

        bool glass = false;
        if (GlassSupported)
        {
            int acrylic = 3; // DWMWA_SYSTEMBACKDROP_TYPE = 38 → DWMSBT_TRANSIENTWINDOW (Acrylic)
            if (DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4) == 0)
            {
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                if (HwndSource.FromHwnd(hwnd) is HwndSource src && src.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = Colors.Transparent;
                int defaultCaption = unchecked((int)0xFFFFFFFF); // let the caption show the material
                DwmSetWindowAttribute(hwnd, 35, ref defaultCaption, 4);
                glass = true;
            }
        }
        if (!glass && Application.Current.TryFindResource("C.Bg") is Color c)
        {
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref colorRef, 4); // solid caption that matches the app
        }
        // translucent tint over the material, or the solid colour when there is no glass
        w.SetResourceReference(Window.BackgroundProperty, glass ? "Backdrop" : "Bg");
        IsGlass = glass;
    }

    public static bool IsGlass { get; private set; }

    /// <summary>Theme-aware brush lookup for code-drawn controls.</summary>
    public static Brush Brush(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;
}
