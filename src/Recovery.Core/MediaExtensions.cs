namespace Recovery.Core;

public static class MediaExtensions
{
    public static readonly IReadOnlySet<string> Photo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".jfif",
        // camera RAW formats
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".dng", ".orf", ".rw2", ".raf", ".srw",
    };

    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".webm",
        ".mts", ".m2ts", ".ts", ".3gp", ".3g2", ".mpg", ".mpeg", ".flv",
    };

    public static bool IsMedia(string path)
    {
        var ext = Path.GetExtension(path);
        return Photo.Contains(ext) || Video.Contains(ext);
    }

    public static MediaKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (Photo.Contains(ext)) return MediaKind.Photo;
        if (Video.Contains(ext)) return MediaKind.Video;
        return MediaKind.Other;
    }

    /// <summary>
    /// Subfolder name for grouping files by type, e.g. ".jpg" -> "JPG".
    /// Files with no extension go into "OTHER".
    /// </summary>
    public static string TypeFolder(string extensionOrPath)
    {
        string ext = extensionOrPath.StartsWith('.')
            ? extensionOrPath
            : Path.GetExtension(extensionOrPath);
        ext = ext.TrimStart('.').ToUpperInvariant();
        return ext.Length == 0 ? "OTHER" : ext;
    }
}

public enum MediaKind
{
    Photo,
    Video,
    Other,
}
