using System.Globalization;

namespace Recovery.Core;

/// <summary>Human-readable byte counts ("1.5 GB") and parsing of "500KB"-style sizes.</summary>
public static class ByteSize
{
    public const long KB = 1L << 10;
    public const long MB = 1L << 20;
    public const long GB = 1L << 30;
    public const long TB = 1L << 40;

    public static string Format(long bytes) => bytes switch
    {
        >= TB => $"{bytes / (double)TB:0.##} TB",
        >= GB => $"{bytes / (double)GB:0.##} GB",
        >= MB => $"{bytes / (double)MB:0.##} MB",
        >= KB => $"{bytes / (double)KB:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Parses "500KB", "1.5 GB", "2tb", "4096" (bytes). Units are binary (1 KB = 1024 B).</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        string v = text.Trim().ToUpperInvariant();
        long mult = 1;
        foreach (var (suffix, m) in new[] { ("TB", TB), ("GB", GB), ("MB", MB), ("KB", KB), ("T", TB), ("G", GB), ("M", MB), ("K", KB), ("B", 1L) })
        {
            if (v.EndsWith(suffix, StringComparison.Ordinal))
            {
                mult = m;
                v = v[..^suffix.Length].TrimEnd();
                break;
            }
        }
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n < 0 || double.IsInfinity(n))
            return false;
        double result = n * mult;
        if (result > long.MaxValue)
            return false;
        bytes = (long)result;
        return true;
    }
}
