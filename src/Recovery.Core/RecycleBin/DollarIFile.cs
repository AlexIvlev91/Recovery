namespace Recovery.Core.RecycleBin;

/// <summary>
/// Parser for the $I metadata files the shell writes next to each $R payload
/// in $Recycle.Bin. Layout:
///   offset 0  int64  version (1 = Vista..8.1, 2 = Win10+)
///   offset 8  int64  original file size in bytes
///   offset 16 int64  deletion time as FILETIME
///   v1: offset 24  fixed 260 UTF-16 chars, null-terminated original path
///   v2: offset 24  int32 path length in chars (incl. null), then UTF-16 path
/// </summary>
public static class DollarIFile
{
    public sealed record Metadata(string OriginalPath, long SizeBytes, DateTime DeletedUtc);

    public static Metadata? TryParse(string dollarIPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(dollarIPath);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (data.Length < 24)
            return null;

        long version = BitConverter.ToInt64(data, 0);
        long size = BitConverter.ToInt64(data, 8);
        long fileTime = BitConverter.ToInt64(data, 16);

        DateTime deletedUtc;
        try
        {
            deletedUtc = DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        string? path = version switch
        {
            1 => ReadFixedPath(data),
            2 => ReadLengthPrefixedPath(data),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(path))
            return null;

        return new Metadata(path, size, deletedUtc);
    }

    private static string? ReadFixedPath(byte[] data)
    {
        const int offset = 24;
        const int maxChars = 260;
        if (data.Length < offset + maxChars * 2)
            return null;

        string raw = System.Text.Encoding.Unicode.GetString(data, offset, maxChars * 2);
        int nul = raw.IndexOf('\0');
        return nul >= 0 ? raw[..nul] : raw;
    }

    private static string? ReadLengthPrefixedPath(byte[] data)
    {
        const int offset = 28;
        if (data.Length < offset)
            return null;

        int chars = BitConverter.ToInt32(data, 24);
        if (chars <= 0 || chars > 32768 || data.Length < offset + chars * 2)
            return null;

        string raw = System.Text.Encoding.Unicode.GetString(data, offset, chars * 2);
        return raw.TrimEnd('\0');
    }
}
