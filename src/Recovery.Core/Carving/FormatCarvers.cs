using System.Buffers.Binary;

namespace Recovery.Core.Carving;

/// <summary>
/// One recoverable file format. <see cref="MatchesHeader"/> is called with at
/// least 12 valid bytes at <paramref name="index"/>; when it matches,
/// <see cref="TryMeasure"/> determines the full file length by reading from
/// the source (returns null when the candidate turns out to be junk).
/// </summary>
public interface IFormatCarver
{
    /// <summary>Stable identifier used for type-selection filters, e.g. "JPEG", "MP4".</summary>
    string Name { get; }

    bool MatchesHeader(ReadOnlySpan<byte> buffer, int index);
    (long Length, string Extension)? TryMeasure(ByteSource source, long offset);
}

/// <summary>JPEG: FF D8 FF header, scan forward to the FF D9 end-of-image marker.</summary>
public sealed class JpegCarver : IFormatCarver
{
    public string Name => "JPEG";

    private const long MaxLength = 300L << 20;
    private const long MinLength = 1024;

    public bool MatchesHeader(ReadOnlySpan<byte> b, int i)
        => b[i] == 0xFF && b[i + 1] == 0xD8 && b[i + 2] == 0xFF && b[i + 3] >= 0xC0;

    public (long, string)? TryMeasure(ByteSource source, long offset)
    {
        var buf = new byte[1 << 20];
        long pos = offset + 2;
        byte prev = 0;

        while (pos - offset < MaxLength)
        {
            int got = source.ReadAt(pos, buf, buf.Length);
            if (got <= 0)
                return null;

            for (int i = 0; i < got; i++)
            {
                if (prev == 0xFF && buf[i] == 0xD9)
                {
                    long length = pos + i + 1 - offset;
                    return length >= MinLength ? (length, ".jpg") : null;
                }
                prev = buf[i];
            }
            pos += got;
        }
        return null;
    }
}

/// <summary>PNG: walk the chunk chain (len/type/data/crc) until IEND — exact length.</summary>
public sealed class PngCarver : IFormatCarver
{
    public string Name => "PNG";

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const long MaxLength = 100L << 20;

    public bool MatchesHeader(ReadOnlySpan<byte> b, int i)
        => b.Slice(i, 8).SequenceEqual(Signature);

    public (long, string)? TryMeasure(ByteSource source, long offset)
    {
        var header = new byte[8];
        long pos = offset + 8;

        while (pos - offset < MaxLength)
        {
            if (!source.TryReadExact(pos, header, 8))
                return null;

            uint len = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (len > int.MaxValue)
                return null;
            for (int i = 4; i < 8; i++)
                if (!char.IsAsciiLetter((char)header[i]))
                    return null;

            pos += 12 + len; // length + type + data + crc
            if (header[4] == 'I' && header[5] == 'E' && header[6] == 'N' && header[7] == 'D')
                return (pos - offset, ".png");
        }
        return null;
    }
}

/// <summary>
/// ISO base media (MP4/MOV/M4V/3GP/HEIC): starts with an ftyp box; the file is
/// the sum of consecutive valid top-level boxes. Requires a moov or mdat box
/// so stray ftyp fragments aren't carved.
/// </summary>
public sealed class IsoBmffCarver : IFormatCarver
{
    public string Name => "MP4/MOV/HEIC/AVIF";

    private const long MaxLength = 8L << 30;
    private const long MinLength = 256;

    public bool MatchesHeader(ReadOnlySpan<byte> b, int i)
    {
        uint size = BinaryPrimitives.ReadUInt32BigEndian(b[i..]);
        return b[i + 4] == 'f' && b[i + 5] == 't' && b[i + 6] == 'y' && b[i + 7] == 'p'
               && size >= 16 && size <= 4096 && size % 4 == 0;
    }

    public (long, string)? TryMeasure(ByteSource source, long offset)
    {
        var header = new byte[16];
        if (!source.TryReadExact(offset, header, 16))
            return null;

        string brand = System.Text.Encoding.ASCII.GetString(header, 8, 4);
        string ext = BrandToExtension(brand);
        // Still-image containers (AVIF/HEIC) are ftyp + meta + mdat and then end;
        // videos may legitimately have more top-level boxes (moov after mdat, etc.).
        bool isImage = ext is ".avif" or ".heic";

        long pos = offset;
        bool hasMedia = false;
        bool sawMdat = false;
        int boxes = 0;

        while (pos - offset < MaxLength)
        {
            if (!source.TryReadExact(pos, header, 16))
                break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (!IsBoxType(header.AsSpan(4, 4)))
                break;
            if (size == 1)
                size = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
            if (size < 8 || pos + size > source.Length)
                break;

            bool isMdat = header[4] == 'm' && header[5] == 'd' && header[6] == 'a' && header[7] == 't';
            if (isMdat || (header[4] == 'm' && header[5] == 'o' && header[6] == 'o' && header[7] == 'v'))
                hasMedia = true;
            sawMdat |= isMdat;

            pos += size;
            boxes++;

            // For a still image, the file ends at mdat — do not keep walking into
            // adjacent free space (which is what produced multi-GB bogus "videos").
            if (isImage && sawMdat)
                break;
        }

        long total = pos - offset;
        if (!hasMedia || boxes < 2 || total < MinLength)
            return null;
        // Sanity cap: a still image should never be huge; reject a runaway walk.
        if (isImage && total > (64L << 20))
            return null;
        return (total, ext);
    }

    private static bool IsBoxType(ReadOnlySpan<byte> t)
    {
        foreach (byte b in t)
            if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                  or >= (byte)'0' and <= (byte)'9' or (byte)' ' or 0xA9))
                return false;
        return true;
    }

    private static string BrandToExtension(string brand) => brand switch
    {
        "avif" or "avis" => ".avif",
        "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" => ".heic",
        "mif1" or "msf1" or "miaf" => ".heic", // generic image-in-ISO; HEIC is the common case
        "qt  " => ".mov",
        var b when b.StartsWith("3gp") => ".3gp",
        _ => ".mp4",
    };
}

/// <summary>RIFF/AVI: "RIFF" + little-endian size + "AVI " — length is size + 8.</summary>
public sealed class RiffAviCarver : IFormatCarver
{
    public string Name => "AVI";

    private const long MaxLength = 8L << 30;
    private const long MinLength = 1024;

    public bool MatchesHeader(ReadOnlySpan<byte> b, int i)
        => b[i] == 'R' && b[i + 1] == 'I' && b[i + 2] == 'F' && b[i + 3] == 'F'
           && b[i + 8] == 'A' && b[i + 9] == 'V' && b[i + 10] == 'I' && b[i + 11] == ' ';

    public (long, string)? TryMeasure(ByteSource source, long offset)
    {
        var header = new byte[12];
        if (!source.TryReadExact(offset, header, 12))
            return null;

        long length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8L;
        if (length is < MinLength or > MaxLength || offset + length > source.Length)
            return null;
        return (length, ".avi");
    }
}
