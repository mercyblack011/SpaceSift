using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpaceSift.Core;

/// <summary>
/// Fast phone scan through Windows Portable Devices (WPD), the API the Explorer phone view is built on.
/// One "bulk" request asks the phone for the name, folder, size and date of every object on a storage at once
/// (MTP GetObjectPropList), instead of one round trip per file through the shell. Read-only: the device is
/// opened with GENERIC_READ and nothing is written.
/// </summary>
public static class WpdScanner
{
    // ---------------------------------------------------------------- property keys and GUIDs (PortableDevice.h)
    private static readonly Guid ObjectProps = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
    private static readonly PropertyKey KeyId = new(ObjectProps, 2);
    private static readonly PropertyKey KeyParent = new(ObjectProps, 3);
    private static readonly PropertyKey KeyName = new(ObjectProps, 4);
    private static readonly PropertyKey KeyContentType = new(ObjectProps, 7);
    private static readonly PropertyKey KeySize = new(ObjectProps, 11);
    private static readonly PropertyKey KeyFileName = new(ObjectProps, 12);
    private static readonly PropertyKey KeyModified = new(ObjectProps, 19);
    private static readonly PropertyKey KeyDesiredAccess = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 9);
    private static readonly PropertyKey KeyClientName = new(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 2);
    private static readonly Guid FolderType = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    private static readonly Guid FunctionalType = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");
    private static readonly Guid FormatAll = new("C1F62EB2-4BB3-479C-9CFA-05B5F3A57B22");
    private static readonly Guid ClsidDevice = new("728A21C5-3D9E-48D7-9810-864848F0F404");
    private static readonly Guid ClsidValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    private static readonly Guid ClsidKeys = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");

    private sealed record Obj(string Id, string Parent, string Name, bool IsFolder, long Size, long Modified);

    /// <summary>
    /// What is known so far about one object. Phones answer a bulk request in passes (often one property for every
    /// object, then the next), so the pieces are merged by object id.
    /// </summary>
    private sealed class Partial
    {
        public string? Parent, Name, FileName;
        public Guid? Type;
        public long Size = -1, Modified;
        public Obj ToObj(string id) => new(id, Parent ?? "", !string.IsNullOrEmpty(FileName) ? FileName! : Name ?? id,
            Type == FolderType || Type == FunctionalType, Type == FolderType || Type == FunctionalType ? 0 : Math.Max(0, Size), Modified);
    }

    /// <summary>Copies whatever properties this answer carries into the object's record.</summary>
    private static void Merge(Dictionary<string, Partial> into, IPortableDeviceValues v, string? id = null, string? parent = null)
    {
        if (id == null && v.GetStringValue(KeyId, out id) != 0) return;
        if (!into.TryGetValue(id!, out var p)) into[id!] = p = new Partial();
        if (parent != null) p.Parent = parent;
        else if (v.GetStringValue(KeyParent, out var par) == 0) p.Parent = par;
        if (v.GetStringValue(KeyName, out var n) == 0) p.Name = n;
        if (v.GetStringValue(KeyFileName, out var fn) == 0) p.FileName = fn;
        if (v.GetGuidValue(KeyContentType, out var t) == 0) p.Type = t;
        if (v.GetUnsignedLargeIntegerValue(KeySize, out var s) == 0) p.Size = (long)s;
        if (v.GetValue(KeyModified, out var pv) == 0)
        {
            try
            {
                if (pv.vt == 7) p.Modified = DateTime.FromOADate(BitConverter.Int64BitsToDouble(pv.data)).ToUniversalTime().Ticks;   // VT_DATE
                else if (pv.vt == 64) p.Modified = DateTime.FromFileTimeUtc(pv.data).Ticks;                                         // VT_FILETIME
            }
            catch { }
            PropVariantClear(ref pv);
        }
    }

    /// <summary>"…\\?\usb#vid_…#{6ac27878-…}\SID-{…}" → the device path WPD opens ("\\?\usb#vid_…#{6ac27878-…}").</summary>
    public static string? DeviceIdFrom(string storageParsingPath)
    {
        int s = storageParsingPath.IndexOf(@"\\?\", StringComparison.Ordinal);
        if (s < 0) return null;
        int e = storageParsingPath.IndexOf(@"\SID-", s, StringComparison.OrdinalIgnoreCase);
        return e < 0 ? storageParsingPath[s..] : storageParsingPath[s..e];
    }

    public static ScanResult Scan(PhoneVolume volume, ScanProgress progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string deviceId = DeviceIdFrom(volume.ParsingPath) ?? throw new IOException("Not a portable device path.");
        progress.Phase = "Reading phone storage";

        var device = (IPortableDevice)Create(ClsidDevice);
        var client = (IPortableDeviceValues)Create(ClsidValues);
        client.SetStringValue(KeyClientName, "SpaceSift");
        client.SetUnsignedIntegerValue(KeyDesiredAccess, 0x80000000); // GENERIC_READ: never writes to the phone
        Check(device.Open(deviceId, client));
        try
        {
            Check(device.Content(out var content));
            Check(content.Properties(out var props));

            // the storage object ("s10001" …) whose name matches the shell's ("Internal shared storage")
            string storageId = FindStorage(content, props, volume.Storage)
                               ?? throw new IOException("The phone storage wasn't found. Unlock the phone and allow file access.");

            var found = new Dictionary<string, Partial>(StringComparer.Ordinal);
            bool bulkOk = false;
            try { bulkOk = ReadBulk(props, storageId, found, progress, ct); }
            catch (OperationCanceledException) { throw; }
            catch { bulkOk = false; }
            // a usable bulk answer names every object's folder; otherwise read folder by folder
            if (!bulkOk || found.Count == 0 || found.Values.Count(p => p.Parent == null) > found.Count / 10)
            {
                found.Clear();
                progress.Items = 0; progress.Bytes = 0;
                ReadByFolder(content, props, storageId, found, progress, ct);   // slower, works on every device
            }

            progress.Phase = "Building tree";
            return Build(volume, storageId, found.Select(kv => kv.Value.ToObj(kv.Key)).ToList(), sw.Elapsed);
        }
        finally { try { device.Close(); } catch { } }
    }

    // ---------------------------------------------------------------- reading

    private static string? FindStorage(IPortableDeviceContent content, IPortableDeviceProperties props, string name)
    {
        string? first = null;
        foreach (var id in Children(content, "DEVICE"))
        {
            var v = Get(props, id, KeyName, KeyContentType);
            if (v == null) continue;
            if (v.GetGuidValue(KeyContentType, out var type) == 0 && type != FunctionalType) continue;
            first ??= id;
            if (v.GetStringValue(KeyName, out var n) == 0 && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return first;
    }

    /// <summary>One request for every object under the storage, all levels deep. False if the phone can't do it.</summary>
    private static bool ReadBulk(IPortableDeviceProperties props, string storageId, Dictionary<string, Partial> into, ScanProgress progress, CancellationToken ct)
    {
        if (props is not IPortableDevicePropertiesBulk bulk) return false;
        // as few properties as possible: phones send one pass per property (the name doubles as the file name)
        var keys = Keys(KeyParent, KeyName, KeyContentType, KeySize, KeyModified);
        var cb = new BulkCallback(into, progress);
        var format = FormatAll;
        if (bulk.QueueGetValuesByObjectFormat(ref format, storageId, 0xFFFFFFFF, keys, cb, out var ctx) != 0) return false;
        if (bulk.Start(ref ctx) != 0) return false;
        while (!cb.Done.Wait(200))
            if (ct.IsCancellationRequested) { bulk.Cancel(ref ctx); ct.ThrowIfCancellationRequested(); }
        return cb.Status == 0 && into.Count > 0;
    }

    /// <summary>Fallback: list each folder and read each object's properties.</summary>
    private static void ReadByFolder(IPortableDeviceContent content, IPortableDeviceProperties props, string storageId, Dictionary<string, Partial> into, ScanProgress progress, CancellationToken ct)
    {
        var keys = Keys(KeyName, KeyFileName, KeyContentType, KeySize, KeyModified);
        var stack = new Stack<string>();
        stack.Push(storageId);
        while (stack.Count > 0)
        {
            string parent = stack.Pop();
            foreach (var id in Children(content, parent))
            {
                ct.ThrowIfCancellationRequested();
                if (props.GetValues(id, keys, out var v) != 0 || v == null) continue;
                Merge(into, v, id, parent);
                var p = into[id];
                if (p.Type == FolderType || p.Type == FunctionalType) stack.Push(id);
                Interlocked.Increment(ref progress.Items);
                if (p.Size > 0) Interlocked.Add(ref progress.Bytes, p.Size);
            }
        }
    }

    [ComVisible(true)]
    private sealed class BulkCallback(Dictionary<string, Partial> into, ScanProgress progress) : IPortableDevicePropertiesBulkCallback
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int Status;
        private long _answers;
        private const int Passes = 5;   // the phone sends one pass per requested property
        public int OnStart(ref Guid ctx) => 0;
        public int OnProgress(ref Guid ctx, IPortableDeviceValuesCollection results)
        {
            uint n = 0;
            results.GetCount(ref n);
            for (uint i = 0; i < n; i++)
            {
                if (results.GetAt(i, out var v) != 0 || v == null) continue;
                lock (into)
                {
                    int count = into.Count;
                    Merge(into, v);
                    if (into.Count > count) Interlocked.Increment(ref progress.Items);   // count each object once
                }
                if (v.GetUnsignedLargeIntegerValue(KeySize, out var s) == 0) Interlocked.Add(ref progress.Bytes, (long)s);
                _answers++;
            }
            // 0–100%: the first pass names every object (count still growing), then the remaining passes fill them in
            long objects = Math.Max(1, Interlocked.Read(ref progress.Items));
            progress.Fraction = _answers <= objects
                ? 0.2 * (1 - Math.Exp(-objects / 8000.0))
                : Math.Min(1, (double)_answers / (objects * Passes));
            return 0;
        }
        public int OnEnd(ref Guid ctx, int hr) { Status = hr; Done.Set(); return 0; }
    }

    // ---------------------------------------------------------------- tree

    private static ScanResult Build(PhoneVolume volume, string storageId, List<Obj> all, TimeSpan elapsed)
    {
        var builder = new ScanBuilder();
        int root = builder.Add(-1, volume.DisplayRoot, true, 0, 0, DateTime.UtcNow.Ticks, (uint)FileAttributes.Directory);
        var kids = new Dictionary<string, List<Obj>>(StringComparer.Ordinal);
        foreach (var o in all)
        {
            if (o.Id == storageId) continue;
            if (!kids.TryGetValue(o.Parent, out var l)) kids[o.Parent] = l = new List<Obj>();
            l.Add(o);
        }
        // parents before children
        var queue = new Queue<(string Id, int Index)>();
        queue.Enqueue((storageId, root));
        while (queue.Count > 0)
        {
            var (id, index) = queue.Dequeue();
            if (!kids.TryGetValue(id, out var list)) continue;
            foreach (var o in list)
            {
                int child = builder.Add(index, o.Name, o.IsFolder, o.Size, o.Size, o.Modified,
                    o.IsFolder ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal);
                if (o.IsFolder) queue.Enqueue((o.Id, child));
            }
        }
        var result = builder.Build(volume.DisplayRoot, 4096, elapsed, false);
        result.IsPhone = true;
        result.Key = volume.Key;
        result.PhoneParsingPath = volume.ParsingPath;
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string> Children(IPortableDeviceContent content, string parent)
    {
        if (content.EnumObjects(0, parent, null, out var e) != 0 || e == null) yield break;
        var buf = new IntPtr[100];
        while (true)
        {
            uint got = 0;
            int hr = e.Next((uint)buf.Length, buf, ref got);
            for (int i = 0; i < got; i++)
            {
                string? id = Marshal.PtrToStringUni(buf[i]);
                Marshal.FreeCoTaskMem(buf[i]);
                if (id != null) yield return id;
            }
            if (hr != 0 || got == 0) yield break;   // S_FALSE: no more
        }
    }

    private static IPortableDeviceValues? Get(IPortableDeviceProperties props, string id, params PropertyKey[] keys)
        => props.GetValues(id, Keys(keys), out var v) == 0 ? v : null;

    private static IPortableDeviceKeyCollection Keys(params PropertyKey[] keys)
    {
        var c = (IPortableDeviceKeyCollection)Create(ClsidKeys);
        foreach (var k in keys) { var key = k; c.Add(ref key); }
        return c;
    }

    private static object Create(Guid clsid) => Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    // ---------------------------------------------------------------- COM definitions (PortableDeviceApi.idl / PortableDeviceTypes.idl)

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid) { public Guid fmtid = fmtid; public uint pid = pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public long data; }

    [ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevice
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string pnpId, IPortableDeviceValues clientInfo);
        [PreserveSig] int SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
        [PreserveSig] int Content(out IPortableDeviceContent content);
        [PreserveSig] int Capabilities(out IntPtr capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceContent
    {
        [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentId, IPortableDeviceValues? filter, out IEnumPortableDeviceObjectIDs e);
        [PreserveSig] int Properties(out IPortableDeviceProperties props);
    }

    [ComImport, Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumPortableDeviceObjectIDs
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] ids, ref uint fetched);
    }

    [ComImport, Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceProperties
    {
        [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string id, out IPortableDeviceKeyCollection keys);
        [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out IPortableDeviceValues attrs);
        [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);
    }

    [ComImport, Guid("482B05C0-4056-44ED-9E0F-5E23B009DA93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevicePropertiesBulk
    {
        [PreserveSig] int QueueGetValuesByObjectList(IntPtr objectIds, IPortableDeviceKeyCollection keys, IPortableDevicePropertiesBulkCallback callback, out Guid context);
        [PreserveSig] int QueueGetValuesByObjectFormat(ref Guid format, [MarshalAs(UnmanagedType.LPWStr)] string parentId, uint depth,
            IPortableDeviceKeyCollection keys, IPortableDevicePropertiesBulkCallback callback, out Guid context);
        [PreserveSig] int QueueSetValuesByObjectList(IntPtr values, IPortableDevicePropertiesBulkCallback callback, out Guid context);
        [PreserveSig] int Start(ref Guid context);
        [PreserveSig] int Cancel(ref Guid context);
    }

    [ComImport, Guid("9DEACB80-11E8-40E3-A9F3-F557986A7845"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevicePropertiesBulkCallback
    {
        [PreserveSig] int OnStart(ref Guid context);
        [PreserveSig] int OnProgress(ref Guid context, IPortableDeviceValuesCollection results);
        [PreserveSig] int OnEnd(ref Guid context, int hr);
    }

    [ComImport, Guid("6E3F2D79-4E07-48C4-8208-D8C2E5AF4A99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValuesCollection
    {
        [PreserveSig] int GetCount(ref uint count);
        [PreserveSig] int GetAt(uint index, out IPortableDeviceValues values);
    }

    [ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceKeyCollection
    {
        [PreserveSig] int GetCount(ref uint count);
        [PreserveSig] int GetAt(uint index, ref PropertyKey key);
        [PreserveSig] int Add(ref PropertyKey key);
    }

    [ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(ref uint count);
        [PreserveSig] int GetAt(uint index, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
        [PreserveSig] int SetSignedIntegerValue(ref PropertyKey key, int value);
        [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int value);
        [PreserveSig] int SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
        [PreserveSig] int SetSignedLargeIntegerValue(ref PropertyKey key, long value);
        [PreserveSig] int GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
        [PreserveSig] int SetFloatValue(ref PropertyKey key, float value);
        [PreserveSig] int GetFloatValue(ref PropertyKey key, out float value);
        [PreserveSig] int SetErrorValue(ref PropertyKey key, int value);
        [PreserveSig] int GetErrorValue(ref PropertyKey key, out int value);
        [PreserveSig] int SetKeyValue(ref PropertyKey key, ref PropertyKey value);
        [PreserveSig] int GetKeyValue(ref PropertyKey key, out PropertyKey value);
        [PreserveSig] int SetBoolValue(ref PropertyKey key, int value);
        [PreserveSig] int GetBoolValue(ref PropertyKey key, out int value);
        [PreserveSig] int SetIUnknownValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIUnknownValue(ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetGuidValue(ref PropertyKey key, ref Guid value);
        [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
    }

    // convenience overloads taking the key by value (the interface wants it by reference)
    private static int SetStringValue(this IPortableDeviceValues v, PropertyKey k, string s) => v.SetStringValue(ref k, s);
    private static int SetUnsignedIntegerValue(this IPortableDeviceValues v, PropertyKey k, uint n) => v.SetUnsignedIntegerValue(ref k, n);
    private static int GetStringValue(this IPortableDeviceValues v, PropertyKey k, out string s) => v.GetStringValue(ref k, out s);
    private static int GetGuidValue(this IPortableDeviceValues v, PropertyKey k, out Guid g) => v.GetGuidValue(ref k, out g);
    private static int GetUnsignedLargeIntegerValue(this IPortableDeviceValues v, PropertyKey k, out ulong n) => v.GetUnsignedLargeIntegerValue(ref k, out n);
    private static int GetValue(this IPortableDeviceValues v, PropertyKey k, out PropVariant p) => v.GetValue(ref k, out p);
}
