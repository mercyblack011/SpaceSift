using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceSift.Core;

/// <summary>
/// Phase 2 scanner: reads the NTFS Master File Table straight from the volume.
/// Needs administrator rights. Gives exact allocated sizes (compressed / sparse aware).
/// </summary>
public static class MftScanner
{
    public static bool IsNtfs(string root)
    {
        try { return string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private struct Rec
    {
        public int Parent;          // parent FRN, -1 unknown
        public int NameOffset;
        public ushort NameLength;
        public byte NameRank;       // 0 none, 1 DOS, 2 POSIX/Win32+DOS, 3 Win32
        public bool InUse, IsDir;
        public uint Attributes;
        public long Modified;
        public long Size, Allocated;
    }

    public static ScanResult Scan(string root, ScanProgress progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string drive = root.Substring(0, 2); // "C:"
        using SafeFileHandle h = NativeMethods.CreateFileW(@"\\.\" + drive, NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new UnauthorizedAccessException($"Cannot open volume {drive} (error {Marshal.GetLastWin32Error()}).");

        // ---- boot sector ----
        var boot = new byte[4096];
        ReadExact(h, boot.AsSpan(0, 512), 0);
        if (BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(3)) != 0x202020205346544E) // "NTFS    "
            throw new NotSupportedException("Not an NTFS volume.");
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int sectorsPerCluster = boot[0x0D];
        long clusterSize = (long)bytesPerSector * sectorsPerCluster;
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        sbyte cpr = (sbyte)boot[0x40];
        int recordSize = cpr > 0 ? (int)(cpr * clusterSize) : 1 << -cpr;

        // ---- $MFT record 0 -> data runs of the MFT itself ----
        var rec0 = new byte[Math.Max(recordSize, bytesPerSector)];
        ReadExact(h, rec0.AsSpan(0, AlignUp(recordSize, bytesPerSector)), mftLcn * clusterSize);
        if (!ApplyFixup(rec0.AsSpan(0, recordSize), bytesPerSector)) throw new InvalidDataException("Corrupt $MFT record.");
        var (runs, mftDataSize) = GetMftRuns(rec0.AsSpan(0, recordSize));
        long totalRecords = mftDataSize / recordSize;
        if (totalRecords > int.MaxValue) throw new NotSupportedException("MFT too large.");

        var recs = new Rec[totalRecords];
        for (int i = 0; i < recs.Length; i++) recs[i].Parent = -1;
        var names = new char[1 << 22];
        int namesLen = 0;

        progress.Phase = "Reading MFT";
        const int chunkBytes = 4 * 1024 * 1024;
        var buf = GC.AllocateUninitializedArray<byte>(chunkBytes);
        long frn = 0;

        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * clusterSize;
            long offset = 0;
            while (offset < runBytes && frn < totalRecords)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(chunkBytes, runBytes - offset);
                toRead -= toRead % recordSize;
                if (toRead <= 0) break;
                if (lcn < 0) { frn += toRead / recordSize; offset += toRead; continue; } // sparse run
                ReadExact(h, buf.AsSpan(0, toRead), lcn * clusterSize + offset);
                for (int p = 0; p + recordSize <= toRead && frn < totalRecords; p += recordSize, frn++)
                    ParseRecord(buf.AsSpan(p, recordSize), bytesPerSector, (int)frn, recs, ref names, ref namesLen);
                offset += toRead;
                progress.Items = frn;
                progress.Fraction = (double)frn / totalRecords;
            }
        }

        // ---- build entries ----
        progress.Phase = "Building tree";
        progress.Fraction = -1;
        const int RootFrn = 5;
        var builder = new ScanBuilder();
        var map = new int[recs.Length];
        Array.Fill(map, -1);
        map[RootFrn] = builder.Add(-1, drive, true, 0, 0, recs[RootFrn].Modified, (uint)FileAttributes.Directory);

        // Add entries; parents may come later, so resolve parent indices in a second pass.
        var pendingParent = new List<(int entry, int parentFrn)>(recs.Length);
        for (int i = 0; i < recs.Length; i++)
        {
            ref Rec r = ref recs[i];
            if (!r.InUse || r.NameRank == 0 || i == RootFrn) continue;
            if (r.Parent < 0 || r.Parent >= recs.Length) continue;
            uint attrs = r.Attributes;
            if (r.IsDir) attrs |= (uint)FileAttributes.Directory;
            int idx = builder.Add(-1, names.AsSpan(r.NameOffset, r.NameLength), r.IsDir, r.Size, r.Allocated, r.Modified, attrs);
            map[i] = idx;
            pendingParent.Add((idx, r.Parent));
        }
        foreach (var (entry, parentFrn) in pendingParent)
        {
            int p = map[parentFrn];
            builder.Get(entry).Parent = p >= 0 && p != entry ? p : -2; // -2 = orphan, dropped by FinishBuild
        }

        string rootPath = drive + "\\";
        progress.Items = builder.Count;
        return builder.Build(rootPath, clusterSize, sw.Elapsed, usedMft: true);
    }

    private static int AlignUp(int v, int a) => (v + a - 1) / a * a;

    private static void ReadExact(SafeFileHandle h, Span<byte> span, long offset)
    {
        int done = 0;
        while (done < span.Length)
        {
            int n = RandomAccess.Read(h, span[done..], offset + done);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of volume.");
            done += n;
        }
    }

    private static bool ApplyFixup(Span<byte> rec, int sectorSize)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != 0x454C4946) return false; // "FILE"
        int usaOfs = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
        if (usaOfs + usaCount * 2 > rec.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(rec[usaOfs..]);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * sectorSize - 2;
            if (end + 2 > rec.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec[end..]) != usn) return false;
            rec[end] = rec[usaOfs + i * 2];
            rec[end + 1] = rec[usaOfs + i * 2 + 1];
        }
        return true;
    }

    private static (List<(long lcn, long clusters)> runs, long size) GetMftRuns(Span<byte> rec)
    {
        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        while (off + 16 <= rec.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec[off..]);
            if (type == 0xFFFFFFFF) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(off + 4)..]);
            if (len <= 0) break;
            if (type == 0x80 && rec[off + 8] != 0 && rec[off + 9] == 0)
            {
                var a = rec.Slice(off, len);
                long realSize = BinaryPrimitives.ReadInt64LittleEndian(a[0x30..]);
                int runOfs = BinaryPrimitives.ReadUInt16LittleEndian(a[0x20..]);
                return (DecodeRuns(a[runOfs..]), realSize);
            }
            off += len;
        }
        throw new InvalidDataException("$MFT has no data attribute.");
    }

    private static List<(long, long)> DecodeRuns(ReadOnlySpan<byte> r)
    {
        var list = new List<(long, long)>();
        long lcn = 0;
        int p = 0;
        while (p < r.Length && r[p] != 0)
        {
            int lenBytes = r[p] & 0x0F, ofsBytes = r[p] >> 4;
            p++;
            long length = 0;
            for (int i = 0; i < lenBytes; i++) length |= (long)r[p + i] << (8 * i);
            p += lenBytes;
            if (ofsBytes == 0) { list.Add((-1, length)); continue; } // sparse
            long delta = 0;
            for (int i = 0; i < ofsBytes; i++) delta |= (long)r[p + i] << (8 * i);
            if ((r[p + ofsBytes - 1] & 0x80) != 0) delta |= -1L << (8 * ofsBytes); // sign extend
            p += ofsBytes;
            lcn += delta;
            list.Add((lcn, length));
        }
        return list;
    }

    private static void ParseRecord(Span<byte> rec, int sectorSize, int frn, Rec[] recs, ref char[] names, ref int namesLen)
    {
        if (!ApplyFixup(rec, sectorSize)) return;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
        if ((flags & 1) == 0) return; // not in use
        long baseRef = BinaryPrimitives.ReadInt64LittleEndian(rec[0x20..]) & 0xFFFFFFFFFFFF;
        int target = baseRef != 0 ? (int)baseRef : frn;
        if ((uint)target >= (uint)recs.Length) return;
        ref Rec r = ref recs[target];
        if (baseRef == 0)
        {
            r.InUse = true;
            r.IsDir = (flags & 2) != 0;
        }

        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        while (off + 16 <= rec.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec[off..]);
            if (type == 0xFFFFFFFF) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(off + 4)..]);
            if (len < 16 || off + len > rec.Length) break;
            var a = rec.Slice(off, len);
            bool nonRes = a[8] != 0;
            int nameLen = a[9];

            switch (type)
            {
                case 0x10 when !nonRes: // $STANDARD_INFORMATION
                {
                    int vo = BinaryPrimitives.ReadUInt16LittleEndian(a[0x14..]);
                    if (vo + 0x24 <= len)
                    {
                        long ft = BinaryPrimitives.ReadInt64LittleEndian(a[(vo + 8)..]);
                        r.Modified = FileTimeToTicks(ft);
                        r.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(a[(vo + 0x20)..]);
                    }
                    break;
                }
                case 0x30 when !nonRes: // $FILE_NAME
                {
                    int vo = BinaryPrimitives.ReadUInt16LittleEndian(a[0x14..]);
                    if (vo + 0x42 > len) break;
                    var v = a[vo..];
                    int fnLen = v[0x40];
                    byte ns = v[0x41];
                    byte rank = ns switch { 2 => (byte)1, 0 => (byte)2, _ => (byte)3 }; // DOS < POSIX < Win32 / Win32&DOS
                    if (rank <= r.NameRank || 0x42 + fnLen * 2 > v.Length) break;
                    long parent = BinaryPrimitives.ReadInt64LittleEndian(v) & 0xFFFFFFFFFFFF;
                    if (namesLen + fnLen > names.Length) Array.Resize(ref names, Math.Max(names.Length * 2, namesLen + fnLen));
                    var nameChars = MemoryMarshal.Cast<byte, char>(v.Slice(0x42, fnLen * 2));
                    nameChars.CopyTo(names.AsSpan(namesLen));
                    r.NameOffset = namesLen; r.NameLength = (ushort)fnLen; r.NameRank = rank;
                    namesLen += fnLen;
                    r.Parent = parent < recs.Length ? (int)parent : -1;
                    if (r.Modified == 0) r.Modified = FileTimeToTicks(BinaryPrimitives.ReadInt64LittleEndian(v[0x10..]));
                    break;
                }
                case 0x80: // $DATA: unnamed = main stream (size + disk use); named streams only add disk use
                {
                    bool mainStream = nameLen == 0;
                    if (!nonRes)
                    {
                        if (mainStream) r.Size += BinaryPrimitives.ReadUInt32LittleEndian(a[0x10..]); // resident: lives in the MFT record
                    }
                    else if (BinaryPrimitives.ReadInt64LittleEndian(a[0x10..]) == 0) // first extent only (start VCN 0)
                    {
                        ushort aflags = BinaryPrimitives.ReadUInt16LittleEndian(a[0x0C..]);
                        long alloc = BinaryPrimitives.ReadInt64LittleEndian(a[0x28..]);
                        long real = BinaryPrimitives.ReadInt64LittleEndian(a[0x30..]);
                        if ((aflags & 0x8001) != 0 && len >= 0x48) alloc = BinaryPrimitives.ReadInt64LittleEndian(a[0x40..]); // compressed/sparse
                        if (mainStream) r.Size += real;
                        r.Allocated += alloc;
                    }
                    break;
                }
            }
            off += len;
        }
    }

    private static long FileTimeToTicks(long ft)
    {
        if (ft <= 0) return 0;
        try { return DateTime.FromFileTimeUtc(ft).Ticks; } catch { return 0; }
    }
}
