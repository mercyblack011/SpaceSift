using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using SpaceSift.Core;

namespace SpaceSift;

/// <summary>
/// SpaceSift always runs as administrator. Windows blocks normal (OLE) drag &amp; drop from Explorer, which runs as the
/// normal user, into an elevated window. The classic WM_DROPFILES drop still works once the window allows that
/// message, so files dropped anywhere on the window go to the Convert page.
/// </summary>
public partial class MainWindow
{
    private const int WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049, MSGFLT_ALLOW = 1;

    private void EnableElevatedFileDrop()
    {
        if (!NativeMethods.IsAdministrator()) return;   // not elevated: WPF's own drag & drop works
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        RevokeDragDrop(hwnd);                            // the OLE target can't receive from Explorer anyway
        foreach (var msg in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
            ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero);
        DragAcceptFiles(hwnd, true);
        HwndSource.FromHwnd(hwnd)?.AddHook(DropHook);
    }

    private IntPtr DropHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DROPFILES) return IntPtr.Zero;
        handled = true;
        var files = new List<string>();
        Point? at = null;
        try
        {
            uint n = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < n; i++)
            {
                uint len = DragQueryFile(wParam, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                DragQueryFile(wParam, i, sb, len + 1);
                files.Add(sb.ToString());
            }
            if (DragQueryPoint(wParam, out var p))
            {
                // client pixels → this window's DIPs → the Convert card
                var dip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.Transform(new Point(p.X, p.Y)) ?? new Point(p.X, p.Y);
                if (Tabs.SelectedItem == TabConvert && Donut.Parent is FrameworkElement card && card.IsVisible)
                {
                    var local = ((UIElement)Content).TranslatePoint(dip, card);
                    if (local.X >= 0 && local.Y >= 0 && local.X <= card.ActualWidth && local.Y <= card.ActualHeight) at = local;
                }
            }
        }
        finally { DragFinish(wParam); }

        // folders: their files (top level), like the normal drop
        var all = files.SelectMany(f => Directory.Exists(f) ? Directory.EnumerateFiles(f) : new[] { f }).ToList();
        if (all.Count > 0) Dispatcher.BeginInvoke(() => OpenDonut(all, at));
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out POINT pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr changeInfo);
}
