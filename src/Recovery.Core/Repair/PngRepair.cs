using System.Buffers.Binary;

namespace Recovery.Core.Repair;

/// <summary>
/// Structural PNG validation and repair. Walks the chunk chain verifying each
/// chunk's CRC. Fixable damage: bad CRCs (recomputed), truncated/garbage tail
/// (dropped at the last complete chunk), and a missing IEND (appended).
/// </summary>
public static class PngRepair
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ValidationReport Validate(byte[] data, string path)
    {
        var (health, detail, _) = Analyze(data);
        return new ValidationReport(path, health, detail);
    }

    public static byte[]? TryRepair(byte[] data)
    {
        var (health, _, chunks) = Analyze(data);
        if (health is not (FileHealth.Repairable or FileHealth.Partial))
            return null;

        using var output = new MemoryStream();
        output.Write(Signature);

        bool wroteIend = false;
        Span<byte> lenBytes = stackalloc byte[4];
        foreach (var (offset, length, type) in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)length);
            output.Write(lenBytes);
            output.Write(data, offset + 4, 4 + length); // type + data
            uint crc = Crc32(data.AsSpan(offset + 4, 4 + length));
            BinaryPrimitives.WriteUInt32BigEndian(lenBytes, crc);
            output.Write(lenBytes);
            if (type == "IEND")
            {
                wroteIend = true;
                break;
            }
        }

        if (!wroteIend)
            output.Write([0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82]);

        return output.ToArray();
    }

    private static (FileHealth Health, string Detail, List<(int Offset, int Length, string Type)> Chunks) Analyze(byte[] data)
    {
        var chunks = new List<(int, int, string)>();

        int sig = data.AsSpan().IndexOf(Signature);
        if (sig < 0)
            return (FileHealth.Broken, "not a PNG (no signature)", chunks);

        var issues = new List<string>();
        if (sig > 0)
            issues.Add($"{sig} byte(s) of garbage before signature");

        int pos = sig + 8;
        bool hasIhdr = false, hasIdat = false, hasIend = false;

        while (pos + 8 <= data.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);

            if (len > int.MaxValue - 12 || !type.All(char.IsAsciiLetter))
            {
                issues.Add($"garbage at offset 0x{pos:X}");
                break;
            }
            if (pos + 12 + (int)len > data.Length)
            {
                // Salvage whatever bytes of a truncated IDAT are present —
                // decoders render the intact upper part of the image.
                int avail = Math.Min(data.Length - pos - 8, (int)len);
                if (type == "IDAT" && avail > 0)
                {
                    issues.Add($"truncated IDAT chunk (salvaged {avail} of {len} bytes)");
                    chunks.Add((pos, avail, type));
                    hasIdat = true;
                }
                else
                {
                    issues.Add($"truncated {type} chunk at offset 0x{pos:X}");
                }
                break;
            }

            uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8 + (int)len));
            if (stored != Crc32(data.AsSpan(pos + 4, 4 + (int)len)))
                issues.Add($"bad CRC in {type} chunk");

            if (chunks.Count == 0 && type != "IHDR")
                return (FileHealth.Broken, "first chunk is not IHDR", chunks);

            chunks.Add((pos, (int)len, type));
            hasIhdr |= type == "IHDR";
            hasIdat |= type == "IDAT";
            pos += 12 + (int)len;

            if (type == "IEND")
            {
                hasIend = true;
                if (data.Length - pos > 0)
                    issues.Add($"{data.Length - pos} trailing byte(s) after IEND");
                break;
            }
        }

        if (!hasIend && issues.Count == 0)
            issues.Add("missing IEND chunk");

        if (!hasIhdr || !hasIdat)
            return (FileHealth.Broken, $"no complete image data ({string.Join("; ", issues)})", chunks);

        // Structure can be valid while the pixel stream is truncated (recovery
        // grabbed only the first clusters). Inflate IDAT and compare to the size
        // the IHDR dimensions require, so a partial image isn't reported as OK.
        double coverage = PixelCoverage(data, chunks);
        if (coverage is >= 0 and < 0.98)
        {
            string pct = coverage < 0.01 ? "under 1%" : $"~{coverage * 100:0}%";
            issues.Add($"PARTIAL image — only {pct} of the picture is present " +
                       "(top of the image; the rest was overwritten and cannot be recovered)");
            return (FileHealth.Partial, string.Join("; ", issues), chunks);
        }

        // coverage is >= 0.99 (complete) or -1 (couldn't verify) here.
        return issues.Count == 0
            ? (FileHealth.Ok, "valid PNG", chunks)
            : (FileHealth.Repairable, string.Join("; ", issues), chunks);
    }

    /// <summary>
    /// Fraction of the image's raw pixel data that actually decompresses from
    /// IDAT (1.0 = complete). Returns -1 when it can't be determined (interlaced
    /// image, unknown color type, or inflate error before any output).
    /// </summary>
    private static double PixelCoverage(byte[] data, List<(int Offset, int Length, string Type)> chunks)
    {
        var ihdr = chunks.FirstOrDefault(c => c.Type == "IHDR");
        if (ihdr.Type != "IHDR" || ihdr.Length < 13)
            return -1;

        int p = ihdr.Offset + 8;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 4));
        int bitDepth = data[p + 8];
        int colorType = data[p + 9];
        int interlace = data[p + 12];
        if (width == 0 || height == 0 || interlace != 0)
            return -1;

        int channels = colorType switch
        {
            0 => 1, // grayscale
            2 => 3, // truecolor
            3 => 1, // palette
            4 => 2, // grayscale + alpha
            6 => 4, // truecolor + alpha
            _ => -1,
        };
        if (channels < 0)
            return -1;

        long bitsPerPixel = (long)bitDepth * channels;
        long bytesPerRow = (width * bitsPerPixel + 7) / 8 + 1; // +1 filter byte per row
        long expected = bytesPerRow * height;
        if (expected <= 0)
            return -1;

        // Concatenate IDAT payloads and inflate (zlib-wrapped).
        using var idat = new MemoryStream();
        foreach (var (offset, length, type) in chunks)
            if (type == "IDAT")
                idat.Write(data, offset + 8, length);
        idat.Position = 0;

        long produced = 0;
        try
        {
            using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
            var buf = new byte[64 << 10];
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0)
                produced += n;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Truncated deflate stream: whatever we produced before the error is
            // the recovered portion.
        }

        return Math.Min(1.0, produced / (double)expected);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
