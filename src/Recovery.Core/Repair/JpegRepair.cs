using System.Buffers.Binary;

namespace Recovery.Core.Repair;

/// <summary>
/// Structural JPEG validation and repair. Walks the marker/segment chain and
/// the entropy-coded scan data. Fixable damage: leading garbage before the
/// SOI marker, and a missing end-of-image marker (truncated file — decoders
/// then render the intact upper part of the image).
/// </summary>
public static class JpegRepair
{
    public static ValidationReport Validate(byte[] data, string path)
    {
        var (health, detail, _, _) = Analyze(data);
        return new ValidationReport(path, health, detail);
    }

    /// <summary>Returns repaired bytes, or null when the file is fine or unfixable.</summary>
    public static byte[]? TryRepair(byte[] data)
    {
        var (health, _, soi, end) = Analyze(data);
        if (health is not (FileHealth.Repairable or FileHealth.Partial))
            return null;

        if (end > 0) // valid EOI found; damage was leading/trailing garbage
            return data[soi..end];

        // truncated: keep everything from SOI and close the image
        var repaired = new byte[data.Length - soi + 2];
        Array.Copy(data, soi, repaired, 0, data.Length - soi);
        repaired[^2] = 0xFF;
        repaired[^1] = 0xD9;
        return repaired;
    }

    private static (FileHealth Health, string Detail, int Soi, int End) Analyze(byte[] data)
    {
        int soi = FindSoi(data);
        if (soi < 0)
            return (FileHealth.Broken, "no JPEG header (SOI) found", 0, 0);

        var issues = new List<string>();
        if (soi > 0)
            issues.Add($"{soi} byte(s) of garbage before header");

        int pos = soi + 2;
        bool sawSos = false;

        while (true)
        {
            if (pos + 2 > data.Length)
                return Truncated(sawSos, issues, soi, "file ends inside segment headers");

            if (data[pos] != 0xFF)
                return (FileHealth.Broken, $"corrupt marker stream at offset 0x{pos:X}", soi, 0);

            byte marker = data[pos + 1];

            if (marker == 0xFF) // fill byte before a marker — legal padding
            {
                pos += 1;
                continue;
            }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                pos += 2;
                continue;
            }
            if (marker == 0xD9)
                return Finish(pos + 2, data.Length, issues, soi);

            if (pos + 4 > data.Length)
                return Truncated(sawSos, issues, soi, "file ends inside segment headers");

            int segLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
            if (segLen < 2 || pos + 2 + segLen > data.Length)
                return Truncated(sawSos, issues, soi, $"truncated segment 0x{marker:X2} at offset 0x{pos:X}");

            if (marker == 0xDA) // SOS: entropy-coded data follows until next real marker
            {
                sawSos = true;
                int scan = pos + 2 + segLen;
                while (true)
                {
                    if (scan + 1 >= data.Length)
                        return Truncated(sawSos, issues, soi, "scan data ends without end-of-image marker");
                    if (data[scan] == 0xFF && data[scan + 1] is not 0x00 and not (>= 0xD0 and <= 0xD7))
                        break;
                    scan++;
                }
                pos = scan;
                continue;
            }

            pos += 2 + segLen;
        }
    }

    private static (FileHealth, string, int, int) Finish(int end, int length, List<string> issues, int soi)
    {
        if (length - end > 16)
            issues.Add($"{length - end} trailing byte(s) after end-of-image");
        return issues.Count == 0
            ? (FileHealth.Ok, "valid JPEG", soi, end)
            : (FileHealth.Repairable, string.Join("; ", issues), soi, end);
    }

    private static (FileHealth, string, int, int) Truncated(bool sawSos, List<string> issues, int soi, string what)
    {
        issues.Add(what);
        // With scan data we can salvage the top of the image, but the file is
        // incomplete — report Partial, not a clean repair. Without any scan
        // data there is nothing to salvage.
        if (sawSos)
            issues.Add("PARTIAL image — only the top of the picture survived; the rest was overwritten");
        return sawSos
            ? (FileHealth.Partial, string.Join("; ", issues), soi, 0)
            : (FileHealth.Broken, string.Join("; ", issues), soi, 0);
    }

    private static int FindSoi(byte[] data)
    {
        for (int i = 0; i + 2 < data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
                return i;
        return -1;
    }
}
