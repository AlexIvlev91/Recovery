using System.Text;
using Recovery.Core;
using Recovery.Core.Carving;
using Recovery.Core.Mft;

namespace Recovery.Cli;

/// <summary>
/// Builds a tiny synthetic NTFS volume image (independently of the parser,
/// following the on-disk spec) and round-trips it through <see cref="MftReader"/>
/// to validate boot-sector parsing, the $MFT run list, fixups, $FILE_NAME
/// name selection, and resident + non-resident $DATA recovery — all without
/// needing a real volume or admin rights.
/// </summary>
internal static class MftSelfTest
{
    private const int Sector = 512;
    private const int Cluster = 512;   // 1 sector/cluster
    private const int RecordSize = 1024;
    private const int MftStartCluster = 4;
    private const int RecordCount = 8;  // 8 * 1024 = 8192 bytes = 16 clusters
    private const int Png2DataCluster = 24;

    public static int Run()
    {
        var residentJpeg = Encoding.ASCII.GetBytes("HELLOJPEGDATA123"); // 16 bytes
        var png2Pattern = new byte[2 * Cluster];
        for (int i = 0; i < png2Pattern.Length; i++)
            png2Pattern[i] = (byte)(i % 251);
        // A real PNG signature up front, so the data check sees picture2 as intact.
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        pngSignature.CopyTo(png2Pattern, 0);
        const int png2RealSize = 1000;

        byte[] image = new byte[64 * Cluster];
        WriteVbr(image);

        long mftBase = (long)MftStartCluster * Cluster;
        var mft = new byte[RecordCount * RecordSize];

        // record 0: $MFT itself (in use) — non-resident $DATA covering the MFT.
        BuildRecord(mft, 0, "$MFT", ns: 3, inUse: true, directory: false,
            resident: null, runLcn: MftStartCluster, runClusters: 16, realSize: RecordCount * RecordSize);
        // record 1: deleted JPEG in a subfolder "gruk" (record 6).
        BuildRecord(mft, 1, "photo1.jpg", ns: 1, inUse: false, directory: false, resident: residentJpeg, parent: 6);
        // record 2: deleted PNG at the volume root, non-resident data.
        BuildRecord(mft, 2, "picture2.png", ns: 1, inUse: false, directory: false,
            resident: null, runLcn: Png2DataCluster, runClusters: 2, realSize: png2RealSize);
        // record 3: in-use JPEG — must be ignored.
        BuildRecord(mft, 3, "keep.jpg", ns: 1, inUse: true, directory: false, resident: residentJpeg);
        // record 4: deleted directory — must be ignored as a file.
        BuildRecord(mft, 4, "folder1", ns: 1, inUse: false, directory: true, resident: null);
        // record 6: in-use directory "gruk" under root — used for path resolution.
        BuildRecord(mft, 6, "gruk", ns: 1, inUse: true, directory: true, resident: null);

        Array.Copy(mft, 0, image, mftBase, mft.Length);
        Array.Copy(png2Pattern, 0, image, (long)Png2DataCluster * Cluster, png2Pattern.Length);

        string tmp = Path.Combine(Path.GetTempPath(), $"mft-selftest-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(tmp, image);

        int failures = 0;
        try
        {
            using var src = new FileByteSource(tmp);
            var reader = new MftReader(src);
            var files = reader.EnumerateDeletedFiles(w => Console.Error.WriteLine("warn: " + w))
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

            failures += Check("finds deleted photo1.jpg", files.ContainsKey("photo1.jpg"));
            failures += Check("finds deleted picture2.png", files.ContainsKey("picture2.png"));
            failures += Check("ignores in-use keep.jpg", !files.ContainsKey("keep.jpg"));
            failures += Check("ignores deleted directory", !files.ContainsKey("folder1"));

            if (files.TryGetValue("photo1.jpg", out var jpg))
            {
                failures += Check("photo1 is resident", jpg.Resident);
                failures += Check("photo1 size correct", jpg.Size == residentJpeg.Length);
                failures += Check("photo1 data round-trips", reader.ReadData(jpg).SequenceEqual(residentJpeg));
                failures += Check("photo1 classified as photo", MediaExtensions.KindOf(jpg.Name) == MediaKind.Photo);
                failures += Check("photo1 parent folder resolved to 'gruk'", jpg.Directory == "gruk");
            }
            if (files.TryGetValue("picture2.png", out var pngDir))
                failures += Check("picture2 at root (empty dir path)", string.IsNullOrEmpty(pngDir.Directory));

            if (files.TryGetValue("picture2.png", out var png))
            {
                failures += Check("picture2 is non-resident", !png.Resident);
                failures += Check("picture2 size correct", png.Size == png2RealSize);
                failures += Check("picture2 data round-trips",
                    reader.ReadData(png).SequenceEqual(png2Pattern.Take(png2RealSize)));
                using var streamed = new MemoryStream();
                long unreadable = reader.CopyData(png, streamed);
                failures += Check("picture2 streams exactly its size", streamed.Length == png2RealSize && unreadable == 0);
            }

            // First-cluster signature check: picture2 starts with a PNG header (intact);
            // photo1's bytes aren't a JPEG (what reused clusters look like).
            var service = new MftUndeleteService();
            var entries = service.ScanSource(src, 'T');
            var verdicts = new Dictionary<string, MftUndeleteService.DataCheck>();
            service.CheckDataSource(src, entries, (e, c) => verdicts[e.OriginalName] = c);
            failures += Check("data check: picture2 looks intact",
                verdicts.GetValueOrDefault("picture2.png", MftUndeleteService.DataCheck.Unknown) == MftUndeleteService.DataCheck.LooksIntact);
            failures += Check("data check: photo1 flagged overwritten",
                verdicts.GetValueOrDefault("photo1.jpg", MftUndeleteService.DataCheck.Unknown) == MftUndeleteService.DataCheck.Overwritten);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EXCEPTION: " + ex);
            failures++;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }

        Console.WriteLine(failures == 0 ? "MFT self-test: ALL PASS" : $"MFT self-test: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    // ---- synthetic image construction (independent of the parser) ----------

    private static void WriteVbr(byte[] image)
    {
        image[0] = 0xEB; image[1] = 0x52; image[2] = 0x90; // jump
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image, 3);
        WriteU16(image, 0x0B, Sector);
        image[0x0D] = Cluster / Sector;      // sectors per cluster
        WriteI64(image, 0x30, MftStartCluster);
        image[0x40] = unchecked((byte)(sbyte)(RecordSize / Cluster)); // clusters per record (2)
        image[510] = 0x55; image[511] = 0xAA;
    }

    private static void BuildRecord(
        byte[] mft, int index, string name, int ns, bool inUse, bool directory,
        byte[]? resident, long runLcn = 0, long runClusters = 0, long realSize = 0, long parent = 5)
    {
        var r = new byte[RecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(r, 0);
        WriteU16(r, 0x04, 0x30);                 // update sequence array offset
        WriteU16(r, 0x06, RecordSize / Sector + 1); // USA count = sectors + 1 = 3
        WriteU16(r, 0x14, 0x38);                 // first attribute offset
        WriteU16(r, 0x16, (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        WriteU32(r, 0x1C, RecordSize);           // allocated size
        WriteU16(r, 0x30, 0x0001);               // USN (fixup entries left 0)

        int pos = 0x38;
        pos += WriteFileNameAttr(r, pos, name, ns, realSize > 0 ? realSize : resident?.Length ?? 0, parent);
        pos += resident is not null
            ? WriteResidentData(r, pos, resident)
            : WriteNonResidentData(r, pos, runLcn, runClusters, realSize);

        WriteU32(r, pos, 0xFFFFFFFF);            // end-of-attributes marker
        pos += 4;
        WriteU32(r, 0x18, (uint)((pos + 7) & ~7));// used size

        ApplyFixupForBuild(r);
        Array.Copy(r, 0, mft, (long)index * RecordSize, RecordSize);
    }

    private static int WriteFileNameAttr(byte[] r, int pos, string name, int ns, long realSize, long parent = 5)
    {
        int content = pos + 0x18;
        WriteI64(r, content + 0x00, parent);     // parent directory reference
        WriteI64(r, content + 0x30, realSize);
        WriteI64(r, content + 0x38, realSize);
        r[content + 0x40] = (byte)name.Length;
        r[content + 0x41] = (byte)ns;
        Encoding.Unicode.GetBytes(name).CopyTo(r, content + 0x42);
        int contentLen = 0x42 + name.Length * 2;
        int attrLen = (0x18 + contentLen + 7) & ~7;

        WriteU32(r, pos + 0x00, 0x30);           // type $FILE_NAME
        WriteU32(r, pos + 0x04, (uint)attrLen);
        r[pos + 0x08] = 0;                       // resident
        WriteU32(r, pos + 0x10, (uint)contentLen);
        WriteU16(r, pos + 0x14, 0x18);           // content offset
        return attrLen;
    }

    private static int WriteResidentData(byte[] r, int pos, byte[] data)
    {
        int contentOffset = 0x18;
        data.CopyTo(r, pos + contentOffset);
        int attrLen = (contentOffset + data.Length + 7) & ~7;

        WriteU32(r, pos + 0x00, 0x80);           // type $DATA
        WriteU32(r, pos + 0x04, (uint)attrLen);
        r[pos + 0x08] = 0;                       // resident
        WriteU32(r, pos + 0x10, (uint)data.Length);
        WriteU16(r, pos + 0x14, (ushort)contentOffset);
        return attrLen;
    }

    private static int WriteNonResidentData(byte[] r, int pos, long lcn, long clusters, long realSize)
    {
        int runOffset = 0x40;
        int rp = pos + runOffset;
        // one run: length = clusters, offset = lcn (both fit in 1 byte here)
        r[rp++] = 0x11;                          // 1 offset byte, 1 length byte
        r[rp++] = (byte)clusters;
        r[rp++] = (byte)lcn;
        r[rp++] = 0x00;                          // run list terminator
        int attrLen = ((rp - pos) + 7) & ~7;

        WriteU32(r, pos + 0x00, 0x80);           // type $DATA
        WriteU32(r, pos + 0x04, (uint)attrLen);
        r[pos + 0x08] = 1;                       // non-resident
        WriteI64(r, pos + 0x10, 0);              // start VCN
        WriteI64(r, pos + 0x18, clusters - 1);   // last VCN
        WriteU16(r, pos + 0x20, (ushort)runOffset);
        WriteI64(r, pos + 0x28, clusters * Cluster); // allocated size
        WriteI64(r, pos + 0x30, realSize);       // real size
        WriteI64(r, pos + 0x38, realSize);       // initialized size
        return attrLen;
    }

    private static void ApplyFixupForBuild(byte[] r)
    {
        const int usaOffset = 0x30;
        int sectors = RecordSize / Sector;
        ushort usn = 0x0001;
        for (int i = 1; i <= sectors; i++)
        {
            int sectorEnd = i * Sector - 2;
            // stash the real last-2-bytes into the USA, then stamp the USN.
            r[usaOffset + i * 2] = r[sectorEnd];
            r[usaOffset + i * 2 + 1] = r[sectorEnd + 1];
            r[sectorEnd] = (byte)(usn & 0xFF);
            r[sectorEnd + 1] = (byte)(usn >> 8);
        }
    }

    private static void WriteU16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, int o, uint v)
    { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static void WriteI64(byte[] b, int o, long v)
    { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }
}
