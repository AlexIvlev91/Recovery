using System.Buffers.Binary;
using System.Text;

namespace Recovery.Core.Repair;

/// <summary>
/// Reconstructs a playable MP4/MOV whose <c>moov</c> index is missing, using a
/// healthy "donor" recorded by the same device/app (same codec configuration).
/// The donor supplies the track layout and codec setup (<c>stsd</c>/avcC/hvcC);
/// the broken file's <c>mdat</c> is re-scanned for H.264/HEVC frame boundaries
/// to rebuild the sample tables. Produces a VIDEO-ONLY file — interleaved audio
/// is not demuxed. Best-effort; success depends on the donor truly matching.
/// </summary>
public static class Mp4Rebuilder
{
    public sealed record Result(bool Success, string? OutputPath, string Detail);

    public static Result Rebuild(string brokenPath, string donorPath, string outPath)
    {
        byte[] donor, broken;
        try
        {
            donor = File.ReadAllBytes(donorPath);
            broken = File.ReadAllBytes(brokenPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, null, $"cannot read input: {ex.Message}");
        }

        // --- locate donor moov and its video track ---------------------------
        var moov = Find(donor, 0, donor.Length, "moov");
        if (moov is null)
            return new Result(false, null, "donor has no moov (pick a healthy video from the same source)");

        var vtrak = FindVideoTrak(donor, moov.Value.cs, moov.Value.ce);
        if (vtrak is null)
            return new Result(false, null, "donor has no video track");

        var (tcs, tce) = vtrak.Value;
        var mdia = Find(donor, tcs, tce, "mdia") ?? default;
        var mdhd = FindBox(donor, mdia.cs, mdia.ce, "mdhd");
        var hdlr = FindBox(donor, mdia.cs, mdia.ce, "hdlr");
        var minf = Find(donor, mdia.cs, mdia.ce, "minf") ?? default;
        var stbl = Find(donor, minf.cs, minf.ce, "stbl") ?? default;
        var stsd = FindBox(donor, stbl.cs, stbl.ce, "stsd");
        var tkhd = FindBox(donor, tcs, tce, "tkhd");
        var mvhd = FindBox(donor, moov.Value.cs, moov.Value.ce, "mvhd");
        var vmhd = FindBox(donor, minf.cs, minf.ce, "vmhd");
        var dinf = FindBox(donor, minf.cs, minf.ce, "dinf");

        if (stsd is null || mdhd is null || hdlr is null || tkhd is null || mvhd is null)
            return new Result(false, null, "donor moov is incomplete (missing stsd/mdhd/tkhd/mvhd)");

        uint mediaTimescale = ReadMdhdTimescale(mdhd);
        uint movieTimescale = ReadMvhdTimescale(mvhd);
        long frameDuration = ReadFirstSttsDelta(donor, stbl.cs, stbl.ce, mediaTimescale);

        string codec = ReadStsdFormat(stsd);
        int nalLen = ReadNalLengthSize(stsd, codec);
        bool hevc = codec is "hvc1" or "hev1" or "hvc2" or "hev2";
        if (!hevc && codec is not ("avc1" or "avc3"))
            return new Result(false, null, $"donor codec '{codec}' is not H.264/HEVC — only those can be rebuilt");

        // --- scan the broken file's mdat into samples ------------------------
        var mdat = Find(broken, 0, broken.Length, "mdat");
        if (mdat is null)
            return new Result(false, null, "broken file has no mdat — no media data to rebuild from");

        int mdStart = mdat.Value.cs;
        int mdEnd = mdat.Value.ce;
        var samples = ScanSamples(broken, mdStart, mdEnd, nalLen, hevc, out int consumedEnd);
        if (samples.Count < 2)
            return new Result(false, null,
                $"mdat does not parse as {(hevc ? "HEVC" : "H.264")} frames (likely interleaved audio or a different codec)");

        // --- assemble the new moov ------------------------------------------
        long dataBytes = consumedEnd - mdStart;
        long durationMedia = (long)samples.Count * frameDuration;
        long durationMovie = movieTimescale != 0 && mediaTimescale != 0
            ? durationMedia * movieTimescale / mediaTimescale
            : durationMedia;

        byte[] stts = BuildStts(samples.Count, frameDuration);
        byte[] stsc = BuildStsc(samples.Count);
        byte[] stsz = BuildStsz(samples);
        byte[] stco = BuildStco(0); // offset patched once total moov size is known
        byte[] stss = BuildStss(samples);

        byte[] newStbl = Box("stbl", stsd, stts, stsc, stsz, stco, stss);
        var minfChildren = new List<byte[]>();
        if (vmhd is not null) minfChildren.Add(vmhd);
        if (dinf is not null) minfChildren.Add(dinf);
        minfChildren.Add(newStbl);
        byte[] newMinf = Box("minf", minfChildren.ToArray());

        byte[] newMdhd = (byte[])mdhd.Clone();
        PatchDuration(newMdhd, durationMedia, mdhdDurationOffset: true);
        byte[] newMdia = Box("mdia", newMdhd, hdlr, newMinf);

        byte[] newTkhd = (byte[])tkhd.Clone();
        PatchTkhdDuration(newTkhd, durationMovie);
        byte[] newTrak = Box("trak", newTkhd, newMdia);

        byte[] newMvhd = (byte[])mvhd.Clone();
        PatchMvhdDuration(newMvhd, durationMovie);
        byte[] newMoov = Box("moov", newMvhd, newTrak);

        byte[] ftyp = FindBox(donor, 0, donor.Length, "ftyp")
                      ?? FindBox(broken, 0, broken.Length, "ftyp")
                      ?? DefaultFtyp();

        // mdat data begins right after ftyp + moov + the 8-byte mdat header.
        long mdatDataOffset = ftyp.Length + newMoov.Length + 8;
        PatchStcoOffset(newMoov, mdatDataOffset);

        // --- write output ----------------------------------------------------
        try
        {
            using var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            outStream.Write(ftyp);
            outStream.Write(newMoov);
            WriteMdatHeader(outStream, dataBytes);
            outStream.Write(broken, mdStart, (int)dataBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, null, $"cannot write output: {ex.Message}");
        }

        int keyframes = samples.Count(s => s.KeyFrame);
        return new Result(true, outPath,
            $"rebuilt {codec} video: {samples.Count} frames ({keyframes} keyframes), " +
            $"{FormatSize(dataBytes)} of media, {(double)durationMedia / Math.Max(1, mediaTimescale):0.#}s. " +
            "Audio is not included; verify playback.");
    }

    // ---- mdat sample scanning ----------------------------------------------

    public readonly record struct Sample(int Offset, int Length, bool KeyFrame);

    private static List<Sample> ScanSamples(byte[] data, int start, int end, int nalLen, bool hevc, out int consumedEnd)
    {
        var samples = new List<Sample>();
        int pos = start;
        int sampleStart = start;
        int sampleLen = 0;
        bool sampleHasVcl = false;
        bool sampleKey = false;
        int consumed = start;

        void Flush()
        {
            if (sampleLen > 0 && sampleHasVcl)
            {
                samples.Add(new Sample(sampleStart, sampleLen, sampleKey));
                consumed = sampleStart + sampleLen;
            }
        }

        while (pos + nalLen <= end)
        {
            long len = ReadUIntBE(data, pos, nalLen);
            if (len <= 0 || pos + nalLen + len > end)
                break;
            int hdr = pos + nalLen;
            int nalType = hevc ? (data[hdr] >> 1) & 0x3F : data[hdr] & 0x1F;
            bool isVcl = hevc ? nalType <= 31 && nalType is not (32 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40)
                              : nalType is >= 1 and <= 5;
            bool isKey = hevc ? nalType is >= 16 and <= 23 : nalType == 5;

            if (isVcl && sampleHasVcl)
            {
                Flush();
                sampleStart = pos;
                sampleLen = 0;
                sampleHasVcl = false;
                sampleKey = false;
            }

            sampleLen += nalLen + (int)len;
            if (isVcl) sampleHasVcl = true;
            if (isKey) sampleKey = true;
            pos += nalLen + (int)len;
        }
        Flush();
        consumedEnd = consumed;
        return samples;
    }

    // ---- box building -------------------------------------------------------

    private static byte[] Box(string type, params byte[][] children)
    {
        int len = 8 + children.Sum(c => c.Length);
        var b = new byte[len];
        BinaryPrimitives.WriteInt32BigEndian(b, len);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        int p = 8;
        foreach (var c in children) { c.CopyTo(b, p); p += c.Length; }
        return b;
    }

    private static byte[] BuildStts(int count, long delta)
    {
        var content = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8), (uint)count);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(12), (uint)delta);
        return Box("stts", content);
    }

    private static byte[] BuildStsc(int count)
    {
        var content = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8), 1);       // first chunk
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(12), (uint)count); // samples per chunk
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(16), 1);      // sample description index
        return Box("stsc", content);
    }

    private static byte[] BuildStsz(List<Sample> samples)
    {
        var content = new byte[12 + samples.Count * 4];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8), (uint)samples.Count);
        for (int i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(12 + i * 4), (uint)samples[i].Length);
        return Box("stsz", content);
    }

    private static byte[] BuildStco(long offset)
    {
        var content = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8), (uint)offset);
        return Box("stco", content);
    }

    private static byte[] BuildStss(List<Sample> samples)
    {
        var keys = new List<int>();
        for (int i = 0; i < samples.Count; i++)
            if (samples[i].KeyFrame)
                keys.Add(i + 1); // sample numbers are 1-based
        if (keys.Count == 0)
            keys.Add(1); // assume first frame is a sync sample if none detected
        var content = new byte[8 + keys.Count * 4];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4), (uint)keys.Count);
        for (int i = 0; i < keys.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8 + i * 4), (uint)keys[i]);
        return Box("stss", content);
    }

    private static void PatchStcoOffset(byte[] moov, long offset)
    {
        // The moov has exactly one stco we built (single chunk). idx points at the
        // 'stco' type; content follows (+4), then ver/flags(4) + entryCount(4),
        // so the single 32-bit chunk offset sits at idx + 4 + 8.
        int idx = IndexOf(moov, "stco");
        if (idx < 0) return;
        BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(idx + 12), (uint)offset);
    }

    private static void WriteMdatHeader(FileStream s, long dataBytes)
    {
        long total = dataBytes + 8;
        Span<byte> h = stackalloc byte[8];
        if (total <= uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(h, (uint)total);
            Encoding.ASCII.GetBytes("mdat").CopyTo(h[4..]);
            s.Write(h);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(h, 1);
            Encoding.ASCII.GetBytes("mdat").CopyTo(h[4..]);
            s.Write(h);
            Span<byte> big = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(big, total + 8);
            s.Write(big);
        }
    }

    // ---- donor parsing helpers ---------------------------------------------

    private static (int cs, int ce)? Find(byte[] d, int s, int e, string type)
    {
        foreach (var (t, start, cs, size) in Boxes(d, s, e))
            if (t == type)
                return (cs, start + size);
        return null;
    }

    private static byte[]? FindBox(byte[] d, int s, int e, string type)
    {
        foreach (var (t, start, _, size) in Boxes(d, s, e))
            if (t == type)
                return d[start..(start + size)];
        return null;
    }

    private static (int cs, int ce)? FindVideoTrak(byte[] d, int moovCs, int moovCe)
    {
        foreach (var (t, start, cs, size) in Boxes(d, moovCs, moovCe))
        {
            if (t != "trak") continue;
            int trakCe = start + size;
            var mdia = Find(d, cs, trakCe, "mdia");
            if (mdia is null) continue;
            var hdlr = FindBox(d, mdia.Value.cs, mdia.Value.ce, "hdlr");
            if (hdlr is null || hdlr.Length < 16) continue;
            string handler = Encoding.ASCII.GetString(hdlr, 16, 4); // hdlr: 8 hdr + 8 (ver/flags+pre_defined) then handler_type
            if (handler == "vide")
                return (cs, trakCe);
        }
        return null;
    }

    private static IEnumerable<(string type, int start, int contentStart, int size)> Boxes(byte[] d, int s, int e)
    {
        int p = s;
        while (p + 8 <= e)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            string type = Encoding.ASCII.GetString(d, p + 4, 4);
            int hdr = 8;
            if (size == 1)
            {
                if (p + 16 > e) yield break;
                size = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p + 8));
                hdr = 16;
            }
            if (size == 0) size = e - p;
            if (size < hdr || p + size > e) yield break;
            yield return (type, p, p + hdr, (int)size);
            p += (int)size;
        }
    }

    private static uint ReadMdhdTimescale(byte[] mdhd)
    {
        int off = mdhd[8] == 1 ? 8 + 4 + 8 + 8 : 8 + 4 + 4 + 4; // header + ver/flags + creation + modif
        return off + 4 <= mdhd.Length ? BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(off)) : 1000;
    }

    private static uint ReadMvhdTimescale(byte[] mvhd)
    {
        int off = mvhd[8] == 1 ? 8 + 4 + 8 + 8 : 8 + 4 + 4 + 4;
        return off + 4 <= mvhd.Length ? BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(off)) : 1000;
    }

    private static long ReadFirstSttsDelta(byte[] d, int stblCs, int stblCe, uint timescale)
    {
        var stts = FindBox(d, stblCs, stblCe, "stts");
        if (stts is not null && stts.Length >= 24)
        {
            uint entries = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(12));
            if (entries >= 1)
            {
                uint delta = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(20));
                if (delta > 0) return delta;
            }
        }
        return Math.Max(1, timescale / 30); // fall back to ~30 fps
    }

    private static string ReadStsdFormat(byte[] stsd)
        // stsd: 8 hdr + 4 ver/flags + 4 entry_count + 4 entry_size + 4 format
        => stsd.Length >= 24 ? Encoding.ASCII.GetString(stsd, 20, 4) : "????";

    private static int ReadNalLengthSize(byte[] stsd, string codec)
    {
        string cfg = codec is "hvc1" or "hev1" or "hvc2" or "hev2" ? "hvcC" : "avcC";
        int idx = IndexOf(stsd, cfg);
        if (idx < 0) return 4;
        int content = idx + 4; // after the 4-byte type
        if (cfg == "avcC")
            return content + 4 < stsd.Length ? (stsd[content + 4] & 0x3) + 1 : 4;
        // hvcC: lengthSizeMinusOne is in byte at content offset 21 (lower 2 bits)
        return content + 21 < stsd.Length ? (stsd[content + 21] & 0x3) + 1 : 4;
    }

    private static void PatchDuration(byte[] box, long duration, bool mdhdDurationOffset)
    {
        // mdhd: ver0 → duration at content offset 16; ver1 → 24 (64-bit).
        if (box[8] == 1)
        {
            int off = 8 + 4 + 8 + 8 + 4; // hdr+ver/flags+creation64+modif64+timescale
            if (off + 8 <= box.Length) BinaryPrimitives.WriteInt64BigEndian(box.AsSpan(off), duration);
        }
        else
        {
            int off = 8 + 4 + 4 + 4 + 4; // hdr+ver/flags+creation+modif+timescale
            if (off + 4 <= box.Length) BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(off), (uint)duration);
        }
    }

    private static void PatchTkhdDuration(byte[] tkhd, long duration)
    {
        if (tkhd[8] == 1)
        {
            int off = 8 + 4 + 8 + 8 + 4 + 4; // hdr+ver/flags+creation64+modif64+trackId+reserved
            if (off + 8 <= tkhd.Length) BinaryPrimitives.WriteInt64BigEndian(tkhd.AsSpan(off), duration);
        }
        else
        {
            int off = 8 + 4 + 4 + 4 + 4 + 4;
            if (off + 4 <= tkhd.Length) BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(off), (uint)duration);
        }
    }

    private static void PatchMvhdDuration(byte[] mvhd, long duration)
    {
        if (mvhd[8] == 1)
        {
            int off = 8 + 4 + 8 + 8 + 4; // hdr+ver/flags+creation64+modif64+timescale
            if (off + 8 <= mvhd.Length) BinaryPrimitives.WriteInt64BigEndian(mvhd.AsSpan(off), duration);
        }
        else
        {
            int off = 8 + 4 + 4 + 4 + 4;
            if (off + 4 <= mvhd.Length) BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(off), (uint)duration);
        }
    }

    private static int IndexOf(byte[] haystack, string type)
    {
        byte[] needle = Encoding.ASCII.GetBytes(type);
        for (int i = 0; i <= haystack.Length - 4; i++)
            if (haystack[i] == needle[0] && haystack[i + 1] == needle[1] &&
                haystack[i + 2] == needle[2] && haystack[i + 3] == needle[3])
                return i;
        return -1;
    }

    private static long ReadUIntBE(byte[] d, int off, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | d[off + i];
        return v;
    }

    private static byte[] DefaultFtyp()
    {
        var content = new byte[16];
        Encoding.ASCII.GetBytes("isom").CopyTo(content, 0);
        Encoding.ASCII.GetBytes("isommp42").CopyTo(content, 8);
        return Box("ftyp", content);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        _ => $"{bytes / (double)(1L << 10):0.#} KB",
    };
}
