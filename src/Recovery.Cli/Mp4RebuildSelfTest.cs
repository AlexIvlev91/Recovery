using System.Buffers.Binary;
using System.Text;
using Recovery.Core.Repair;

namespace Recovery.Cli;

/// <summary>
/// Builds a minimal but structurally-valid donor MP4 and a moov-less "broken"
/// file (a bare ftyp+mdat holding length-prefixed H.264 NAL units), runs
/// <see cref="Mp4Rebuilder"/>, and verifies the reconstructed sample table —
/// frame count, keyframe detection, and the mdat chunk offset — without needing
/// a real video file.
/// </summary>
internal static class Mp4RebuildSelfTest
{
    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"mp4rebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string donorPath = Path.Combine(dir, "donor.mp4");
        string brokenPath = Path.Combine(dir, "broken.mp4");
        string outPath = Path.Combine(dir, "fixed.mp4");

        File.WriteAllBytes(donorPath, BuildDonor());
        File.WriteAllBytes(brokenPath, BuildBroken());

        int failures = 0;
        try
        {
            var result = Mp4Rebuilder.Rebuild(brokenPath, donorPath, outPath);
            failures += Check("rebuild reports success", result.Success, result.Detail);
            if (!result.Success)
            {
                Console.WriteLine($"MP4 rebuild self-test: FAILED ({result.Detail})");
                return 1;
            }

            byte[] outp = File.ReadAllBytes(outPath);
            failures += Check("output has moov", HasBox(outp, 0, outp.Length, "moov"));
            failures += Check("output has mdat", HasBox(outp, 0, outp.Length, "mdat"));

            int sampleCount = ReadStszCount(outp);
            failures += Check($"3 frames recovered (got {sampleCount})", sampleCount == 3);

            int[] keys = ReadStss(outp);
            failures += Check($"1 keyframe at frame 1 (got [{string.Join(",", keys)}])",
                keys.Length == 1 && keys[0] == 1);

            long stco = ReadStcoOffset(outp);
            var (ftypLen, moovLen) = TopBoxLens(outp);
            long expected = ftypLen + moovLen + 8;
            failures += Check($"mdat chunk offset correct (stco={stco}, expected {expected})", stco == expected);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EXCEPTION: " + ex);
            failures++;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }

        Console.WriteLine(failures == 0 ? "MP4 rebuild self-test: ALL PASS" : $"MP4 rebuild self-test: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}" + (ok || detail is null ? "" : $"  ({detail})"));
        return ok ? 0 : 1;
    }

    // ---- build the moov-less broken file -----------------------------------

    private static byte[] BuildBroken()
    {
        // Three access units: [SPS+PPS+IDR] (keyframe), [P-slice], [P-slice].
        using var mdat = new MemoryStream();
        WriteNal(mdat, 7, 20);  // SPS  (non-VCL)
        WriteNal(mdat, 8, 6);   // PPS  (non-VCL)
        WriteNal(mdat, 5, 400); // IDR  (VCL, keyframe)  -> ends AU 1 when next VCL arrives
        WriteNal(mdat, 1, 300); // P    (VCL) -> AU 2
        WriteNal(mdat, 1, 250); // P    (VCL) -> AU 3
        byte[] mdatContent = mdat.ToArray();

        using var file = new MemoryStream();
        file.Write(Ftyp());
        file.Write(Box("mdat", mdatContent));
        return file.ToArray();
    }

    private static void WriteNal(Stream s, int h264Type, int payload)
    {
        long len = 1 + payload;                 // 1-byte NAL header + payload
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)len);
        s.Write(prefix);
        s.WriteByte((byte)(h264Type & 0x1F));   // nal_ref_idc=0, type in low 5 bits
        s.Write(new byte[payload]);
    }

    // ---- build a minimal valid donor ---------------------------------------

    private static byte[] BuildDonor()
    {
        byte[] avcC = Box("avcC", [1, 0x42, 0x00, 0x1E, 0xFF, 0xE1, 0x00, 0x00]); // lengthSizeMinusOne=3 -> nalLen 4
        byte[] avc1 = Concat(BE32(0), Ascii("avc1"), new byte[78], avcC);         // entry: [size placeholder][format][visual header][avcC]
        FixEntrySize(avc1);
        byte[] stsdContent = Concat(new byte[4], BE32(1), avc1);                  // ver/flags + entry_count=1 + entry
        byte[] stsd = Box("stsd", stsdContent);

        // stts: 1 entry, 5 samples @ delta 20 (600/30 fps). Rebuilder reads the delta.
        byte[] stts = Box("stts", Concat(new byte[4], BE32(1), BE32(5), BE32(20)));

        byte[] stbl = Box("stbl", stsd, stts);
        byte[] vmhd = Box("vmhd", new byte[12]);
        byte[] dref = Box("dref", Concat(new byte[4], BE32(0)));
        byte[] dinf = Box("dinf", dref);
        byte[] minf = Box("minf", vmhd, dinf, stbl);

        byte[] mdhd = Box("mdhd", Mdhd(timescale: 600, duration: 100));
        byte[] hdlr = Hdlr();
        byte[] mdia = Box("mdia", mdhd, hdlr, minf);

        byte[] tkhd = Box("tkhd", Tkhd(duration: 166));
        byte[] trak = Box("trak", tkhd, mdia);

        byte[] mvhd = Box("mvhd", Mvhd(timescale: 1000, duration: 166));
        byte[] moov = Box("moov", mvhd, trak);

        return Concat(Ftyp(), moov, Box("mdat", new byte[32]));
    }

    private static byte[] Mdhd(uint timescale, uint duration)
    {
        var c = new byte[24]; // ver/flags + creation + modif + timescale + duration + lang + pre
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(12), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), duration);
        return c;
    }

    private static byte[] Mvhd(uint timescale, uint duration)
    {
        var c = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(12), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), duration);
        return c;
    }

    private static byte[] Tkhd(uint duration)
    {
        var c = new byte[84]; // ver/flags+creation+modif+trackId+reserved+duration+...
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(20), duration);
        return c;
    }

    private static byte[] Hdlr()
    {
        // ver/flags(4) + pre_defined(4) + handler_type(4)='vide' + reserved(12) + name(1)
        var c = new byte[25];
        Ascii("vide").CopyTo(c, 8);
        return Box("hdlr", c);
    }

    private static void FixEntrySize(byte[] entry) => BinaryPrimitives.WriteInt32BigEndian(entry, entry.Length);

    // ---- tiny box helpers ---------------------------------------------------

    private static byte[] Box(string type, params byte[][] children)
    {
        int len = 8 + children.Sum(c => c.Length);
        var b = new byte[len];
        BinaryPrimitives.WriteInt32BigEndian(b, len);
        Ascii(type).CopyTo(b, 4);
        int p = 8;
        foreach (var c in children) { c.CopyTo(b, p); p += c.Length; }
        return b;
    }

    private static byte[] Ftyp() => Box("ftyp", Concat(Ascii("isom"), BE32(0), Ascii("isommp42")));
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] BE32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(outp, o); o += p.Length; }
        return outp;
    }

    // ---- output verification ------------------------------------------------

    private static bool HasBox(byte[] d, int s, int e, string type) => FindBox(d, s, e, type) is not null;

    private static (int cs, int ce)? FindBox(byte[] d, int s, int e, string type)
    {
        int p = s;
        while (p + 8 <= e)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            string t = Encoding.ASCII.GetString(d, p + 4, 4);
            if (size < 8 || p + size > e) return null;
            if (t == type) return (p + 8, (int)(p + size));
            p += (int)size;
        }
        return null;
    }

    private static (int cs, int ce)? Descend(byte[] d, params string[] path)
    {
        int s = 0, e = d.Length;
        (int cs, int ce)? box = null;
        foreach (var type in path)
        {
            box = FindBox(d, s, e, type);
            if (box is null) return null;
            s = box.Value.cs;
            e = box.Value.ce;
        }
        return box;
    }

    private static int ReadStszCount(byte[] d)
    {
        var stsz = Descend(d, "moov", "trak", "mdia", "minf", "stbl", "stsz");
        return stsz is null ? -1 : (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stsz.Value.cs + 8));
    }

    private static int[] ReadStss(byte[] d)
    {
        var stss = Descend(d, "moov", "trak", "mdia", "minf", "stbl", "stss");
        if (stss is null) return [];
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stss.Value.cs + 4));
        var keys = new int[count];
        for (int i = 0; i < count; i++)
            keys[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stss.Value.cs + 8 + i * 4));
        return keys;
    }

    private static long ReadStcoOffset(byte[] d)
    {
        var stco = Descend(d, "moov", "trak", "mdia", "minf", "stbl", "stco");
        return stco is null ? -1 : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(stco.Value.cs + 8));
    }

    private static (long ftyp, long moov) TopBoxLens(byte[] d)
    {
        long ftyp = 0, moov = 0;
        int p = 0;
        while (p + 8 <= d.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            string t = Encoding.ASCII.GetString(d, p + 4, 4);
            if (size < 8) break;
            if (t == "ftyp") ftyp = size;
            if (t == "moov") moov = size;
            p += (int)size;
        }
        return (ftyp, moov);
    }
}
