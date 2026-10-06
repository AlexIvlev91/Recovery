using System.Buffers.Binary;

namespace Recovery.Core.Repair;

/// <summary>
/// Validation and repair of ISO-BMFF containers (MP4/MOV/M4V/3GP/HEIC).
/// Fixable without a donor file: a truncated mdat when the moov/meta index
/// is intact (the mdat size field is patched so the surviving media plays),
/// a truncated trailing box, and trailing garbage. A missing or truncated
/// moov/meta index is not repairable — that needs a donor-based rebuild.
/// </summary>
public static class Mp4Repair
{
    public static ValidationReport Validate(string path)
    {
        var d = Diagnose(path);
        return new ValidationReport(path, d.Health, d.Detail);
    }

    /// <summary>Writes a repaired copy to <paramref name="destPath"/>; false when not repairable.</summary>
    public static bool TryRepair(string path, string destPath)
    {
        var d = Diagnose(path);
        if (d.Health is not (FileHealth.Repairable or FileHealth.Partial))
            return false;

        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[1 << 20];
            long remaining = d.KeepBytes;
            while (remaining > 0)
            {
                int got = input.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (got <= 0)
                    break;
                output.Write(buf, 0, got);
                remaining -= got;
            }
        }

        if (d.PatchOffset is { } patchOffset)
        {
            using var patch = new FileStream(destPath, FileMode.Open, FileAccess.Write);
            patch.Seek(patchOffset, SeekOrigin.Begin);
            Span<byte> size = stackalloc byte[d.Patch64 ? 8 : 4];
            if (d.Patch64)
                BinaryPrimitives.WriteInt64BigEndian(size, d.PatchValue);
            else
                BinaryPrimitives.WriteUInt32BigEndian(size, (uint)d.PatchValue);
            patch.Write(size);
        }
        return true;
    }

    private sealed record Diagnosis(
        FileHealth Health, string Detail, long KeepBytes = 0,
        long? PatchOffset = null, long PatchValue = 0, bool Patch64 = false);

    private static Diagnosis Diagnose(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        var header = new byte[16];

        bool first = true, hasFtyp = false, hasIndex = false, hasMdat = false;
        bool isImage = false;
        long firstMdatEnd = -1;
        long pos = 0;
        long tailStart = -1; // where a truncated/garbage tail begins, -1 = clean
        string tailDetail = "";
        (long Offset, string Type, bool Size64)? truncated = null;

        while (pos + 8 <= length)
        {
            stream.Seek(pos, SeekOrigin.Begin);
            if (stream.Read(header, 0, 16) < 8)
                break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            bool size64 = false;

            if (first && type == "ftyp")
            {
                string brand = header.Length >= 12 ? System.Text.Encoding.ASCII.GetString(header, 8, 4) : "";
                isImage = brand is "avif" or "avis" or "heic" or "heix" or "mif1" or "msf1" or "miaf";
            }

            if (!type.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '©'))
            {
                tailStart = pos;
                tailDetail = $"garbage at offset 0x{pos:X}";
                break;
            }
            if (size == 1)
            {
                size64 = true;
                size = pos + 16 <= length ? BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8)) : long.MaxValue;
            }
            if (size == 0)
                size = length - pos;
            if (size < 8)
            {
                tailStart = pos;
                tailDetail = $"invalid box size at offset 0x{pos:X}";
                break;
            }

            if (first && type != "ftyp")
                return new Diagnosis(FileHealth.Broken, "does not start with an ftyp box");
            first = false;

            if (pos + size > length)
            {
                truncated = (pos, type, size64);
                tailDetail = $"truncated {type} box (declares {size} bytes, {length - pos} remain)";
                break;
            }

            hasFtyp |= type == "ftyp";
            hasIndex |= type is "moov" or "meta";
            hasMdat |= type == "mdat";
            if (type == "mdat" && firstMdatEnd < 0)
                firstMdatEnd = pos + size;
            pos += size;

            // A still image (AVIF/HEIC) ends at its mdat. Anything after it is
            // carve overrun — stop so we can trim it.
            if (isImage && firstMdatEnd >= 0)
                break;
        }

        if (!hasFtyp)
            return new Diagnosis(FileHealth.Broken, "no ftyp box — not an ISO media file");

        // Image with data past the end of its mdat: trim the overrun tail.
        if (isImage && hasIndex && firstMdatEnd > 0 && length - firstMdatEnd > 16)
            return new Diagnosis(FileHealth.Repairable,
                $"{length - firstMdatEnd} trailing byte(s) after the image — carve overrun, can be trimmed",
                KeepBytes: firstMdatEnd);
        if (!hasIndex)
            return new Diagnosis(FileHealth.Broken,
                "moov/meta index missing — unplayable; needs donor-based rebuild (not yet supported)");

        if (truncated is { } t)
        {
            if (t.Type == "mdat")
            {
                // Index survives before the cut: shrink mdat to the bytes present
                // so the container parses; players render the surviving media.
                // The tail of the video is gone, so this is Partial, not a clean fix.
                long available = length - t.Offset;
                return new Diagnosis(FileHealth.Partial,
                    tailDetail + " — PARTIAL video: the surviving start will play, the rest was overwritten",
                    KeepBytes: length,
                    PatchOffset: t.Size64 ? t.Offset + 8 : t.Offset,
                    PatchValue: available,
                    Patch64: t.Size64);
            }
            if (t.Type is "moov" or "meta")
                return new Diagnosis(FileHealth.Broken, tailDetail + " — index incomplete, needs donor-based rebuild");
            if (!hasMdat)
                return new Diagnosis(FileHealth.Broken, tailDetail + "; no media data before the cut");
            return new Diagnosis(FileHealth.Repairable, tailDetail + " — truncated box can be dropped",
                KeepBytes: t.Offset);
        }

        if (tailStart >= 0)
        {
            if (!hasMdat)
                return new Diagnosis(FileHealth.Broken, tailDetail + "; no media data");
            return new Diagnosis(FileHealth.Repairable, tailDetail + " — tail can be dropped", KeepBytes: tailStart);
        }

        if (!hasMdat)
            return new Diagnosis(FileHealth.Broken, "mdat (media data) missing");

        return new Diagnosis(FileHealth.Ok, "valid container structure");
    }
}
