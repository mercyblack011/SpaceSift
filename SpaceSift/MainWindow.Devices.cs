using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpaceSift.Core;
using SpaceSift.UI;

namespace SpaceSift;

/// <summary>The drive list: fixed and USB drives, phones in "File transfer" mode, live updates and the Refresh button.</summary>
public partial class MainWindow
{
    private bool _rebuildingDrives;
    private List<PhoneVolume> _phones = new();
    private DispatcherTimer? _deviceDebounce;

    /// <summary>The selected phone storage, or null when a drive letter is selected.</summary>
    private PhoneVolume? SelectedPhone => (DriveBox.SelectedItem as DriveItem)?.Phone;

    /// <summary>Short name for messages: "C:" for a drive, "OPPO F19 Pro" for a phone.</summary>
    private string ShortDrive => SelectedPhone?.Device ?? SelectedDrive.TrimEnd('\\');

    /// <summary>Lists the drive letters (internal, USB / pendrive, SD card, network) plus the phones found last time.</summary>
    private void LoadLetterDrives(string? selectRoot = null)
    {
        selectRoot ??= (DriveBox.SelectedItem as DriveItem)?.Root ?? JunkScanner.SystemDrive;
        var items = new List<DriveItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType == DriveType.CDRom && d.TotalSize == 0) continue;
                string kind = d.DriveType switch
                {
                    DriveType.Removable => "USB drive",
                    DriveType.Network => "Network drive",
                    DriveType.CDRom => "Disc",
                    _ => "Local Disk",
                };
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? kind
                    : d.DriveType == DriveType.Removable ? $"{d.VolumeLabel} (USB)" : d.VolumeLabel;
                items.Add(new DriveItem($"[{d.Name.TrimEnd('\\')}] {label}", d.Name));
            }
            catch { /* the drive was removed meanwhile */ }
        }
        foreach (var p in _phones)
            items.Add(new DriveItem($"\U0001F4F1 {p.Device} · {p.Storage}", p.Key, p));
        FillDriveBox(items, selectRoot);
    }

    private void FillDriveBox(List<DriveItem> items, string selectRoot)
    {
        string? before = (DriveBox.SelectedItem as DriveItem)?.Root;
        _rebuildingDrives = true;
        try
        {
            DriveBox.Items.Clear();
            foreach (var i in items) DriveBox.Items.Add(i);
            int pick = items.FindIndex(i => string.Equals(i.Root, selectRoot, StringComparison.OrdinalIgnoreCase));
            if (pick < 0) pick = items.FindIndex(i => string.Equals(i.Root, JunkScanner.SystemDrive, StringComparison.OrdinalIgnoreCase));
            DriveBox.SelectedIndex = Math.Max(0, pick);
        }
        finally { _rebuildingDrives = false; }
        // the selected drive went away (USB unplugged): switch every page to the new selection
        string? after = (DriveBox.SelectedItem as DriveItem)?.Root;
        if (IsLoaded && before != null && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) DriveBox_Changed();
        else if (IsLoaded) UpdateSummary(SelectedDrive);
    }

    /// <summary>Finds connected phones (a second or so through the shell) and adds them to the list.</summary>
    private async Task LoadPhonesAsync()
    {
        var phones = await Task.Run(() => PhoneStorage.List(TimeSpan.FromSeconds(20)));
        bool changed = phones.Count != _phones.Count || phones.Where((p, i) => p.Key != _phones[i].Key).Any();
        var added = phones.Where(p => _phones.All(o => o.Key != p.Key)).ToList();
        _phones = phones;
        if (changed) LoadLetterDrives();
        foreach (var p in added) ShowToast($"{p.Device} connected · {p.Storage}, {Fmt.Size(p.Free)} free of {Fmt.Size(p.Total)}");
    }

    // ================================================================ live updates (USB plugged in / removed)

    private const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVNODES_CHANGED = 0x0007;

    private void WatchDevices()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(DeviceHook);
        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _deviceDebounce.Tick += async (_, _) =>
        {
            _deviceDebounce!.Stop();
            var known = DriveBox.Items.OfType<DriveItem>().Where(d => d.Phone == null).Select(d => d.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            LoadLetterDrives();
            foreach (var d in DriveBox.Items.OfType<DriveItem>().Where(d => d.Phone == null && !known.Contains(d.Root)))
                ShowToast($"{d.Label} connected");
            await LoadPhonesAsync();
        };
        _ = LoadPhonesAsync();
    }

    private IntPtr DeviceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
        {
            // Windows sends several of these per device: wait until it settles, then list again once
            _deviceDebounce?.Stop();
            _deviceDebounce?.Start();
        }
        return IntPtr.Zero;
    }

    // ================================================================ Refresh button

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (RefreshGlyph.RenderTransform is System.Windows.Media.RotateTransform rt && Motion.Enabled)
            rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        RefreshButton.IsEnabled = false;
        try
        {
            ShowToast("Refreshing…");
            LoadLetterDrives();
            await LoadPhonesAsync();
            UpdateSummary(SelectedDrive);
            _ = ScanJunk();
            _ = LoadApps();
            if (_orgFolder != null) _ = OrgRefresh();
            // Disk Map / Files: scan the drive again so the map shows what is on it now
            if ((Tabs.SelectedItem == TabTree || Tabs.SelectedItem == TabFiles) && _scan != null && ScanButton.IsEnabled)
            {
                if (DriveBox.SelectedItem is DriveItem d) await StartScan(d.Root, FastMode.IsChecked == true);
            }
            else ShowToast("Refreshed.");
        }
        finally { RefreshButton.IsEnabled = true; }
    }
}
