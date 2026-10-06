namespace Recovery.Core;

/// <summary>
/// Magic-byte checks per media extension. Used to judge whether a deleted
/// file's clusters still hold its own data (header matches its extension) or
/// have been reused/zeroed since deletion.
/// </summary>
public static class MediaSignature
{
    /// <summary>Bytes of a file's head needed for every check here.</summary>
    public const int HeadLength = 32;

    /// <summary>
    /// True/false when <paramref name="data"/> does/doesn't start like a file
    /// of type <paramref name="extension"/>; null when there's no reliable
    /// signature for that extension.
    /// </summary>
    public static bool? Matches(string extension, ReadOnlySpan<byte> data)
    {
        // Zero-pad short heads so every pattern below can index freely: a type
        // with a signature then simply fails to match, one without stays null.
        Span<byte> head = stackalloc byte[HeadLength];
        data[..Math.Min(data.Length, HeadLength)].CopyTo(head);

        switch (extension.ToLowerInvariant())
        {
            case ".jpg" or ".jpeg" or ".jfif":
                return head is [0xFF, 0xD8, 0xFF, ..];
            case ".png":
                return head is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..];
            case ".gif":
                return head is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..];
            case ".bmp":
                return head is [(byte)'B', (byte)'M', ..];
            case ".webp":
                return IsRiff(head, "WEBP");
            case ".avi":
                return IsRiff(head, "AVI ");
            case ".tif" or ".tiff" or ".cr2" or ".nef" or ".nrw" or ".arw" or ".dng" or ".srw":
                return IsTiff(head);
            case ".orf":
                return head is [(byte)'I', (byte)'I', (byte)'R', (byte)'O' or (byte)'S', ..] or [(byte)'M', (byte)'M', (byte)'O', (byte)'R', ..] || IsTiff(head);
            case ".rw2":
                return head is [(byte)'I', (byte)'I', (byte)'U', 0x00, ..] || IsTiff(head);
            case ".raf":
                return Ascii(head, 0, "FUJIFILM");
            case ".mp4" or ".m4v" or ".mov" or ".3gp" or ".3g2" or ".heic" or ".heif" or ".avif" or ".cr3":
                return IsBmffBox(head);
            case ".mkv" or ".webm":
                return head is [0x1A, 0x45, 0xDF, 0xA3, ..];
            case ".wmv":
                return head is [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, ..];
            case ".flv":
                return Ascii(head, 0, "FLV");
            case ".mpg" or ".mpeg":
                return head is [0x00, 0x00, 0x01, 0xBA or 0xB3, ..];
            case ".ts" or ".mts" or ".m2ts":
                return head[0] == 0x47 || head[4] == 0x47;
            default:
                return null;
        }
    }

    /// <summary>True when every byte is zero — typical of TRIMmed SSD blocks.</summary>
    public static bool IsZeroed(ReadOnlySpan<byte> head) => head.Length > 0 && head.IndexOfAnyExcept((byte)0) < 0;

    private static bool IsRiff(ReadOnlySpan<byte> head, string form) => Ascii(head, 0, "RIFF") && Ascii(head, 8, form);

    private static bool IsTiff(ReadOnlySpan<byte> head) =>
        head is [(byte)'I', (byte)'I', 0x2A, 0x00, ..] or [(byte)'M', (byte)'M', 0x00, 0x2A, ..];

    // ISO-BMFF files normally open with ftyp, but older QuickTime files can
    // open straight into moov/mdat/wide/free/skip.
    private static bool IsBmffBox(ReadOnlySpan<byte> head) =>
        Ascii(head, 4, "ftyp") || Ascii(head, 4, "moov") || Ascii(head, 4, "mdat") ||
        Ascii(head, 4, "wide") || Ascii(head, 4, "free") || Ascii(head, 4, "skip") || Ascii(head, 4, "pnot");

    private static bool Ascii(ReadOnlySpan<byte> data, int offset, string text)
    {
        if (data.Length < offset + text.Length)
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (data[offset + i] != (byte)text[i])
                return false;
        }
        return true;
    }
}
