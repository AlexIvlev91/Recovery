namespace Recovery.Core.RecycleBin;

/// <summary>
/// A single recoverable file found in a Recycle Bin.
/// </summary>
public sealed class RecycleBinItem
{
    /// <summary>Full path the file had before deletion.</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Path of the $R payload file currently holding the data.</summary>
    public required string BackingPath { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime DeletedUtc { get; init; }

    public required MediaKind Kind { get; init; }

    /// <summary>
    /// Set when the file was inside a deleted folder: path relative to that
    /// folder's root, so the folder structure can be recreated on restore.
    /// </summary>
    public string? RelativePathInDeletedFolder { get; init; }

    public string OriginalName => Path.GetFileName(OriginalPath);
}
