namespace Recovery.Core.RecycleBin;

/// <summary>
/// Scans $Recycle.Bin folders on local fixed/removable drives for recoverable
/// photo and video files. Deleted files are stored as $R&lt;id&gt;&lt;ext&gt; payloads
/// with a matching $I&lt;id&gt;&lt;ext&gt; metadata file holding the original path.
/// Deleted folders are stored as $R directories whose contents keep their
/// original names; media inside them is reported with a relative path.
/// </summary>
public sealed class RecycleBinScanner
{
    /// <summary>Scan every accessible Recycle Bin on all ready drives.</summary>
    public IReadOnlyList<RecycleBinItem> Scan(Action<string>? onWarning = null)
    {
        var items = new List<RecycleBinItem>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;

            string binRoot = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (!Directory.Exists(binRoot))
                continue;

            IEnumerable<string> sidDirs;
            try
            {
                sidDirs = Directory.EnumerateDirectories(binRoot);
            }
            catch (UnauthorizedAccessException)
            {
                onWarning?.Invoke($"No access to {binRoot} (run elevated to scan all users' bins).");
                continue;
            }

            foreach (var sidDir in sidDirs)
                ScanSidFolder(sidDir, items, onWarning);
        }

        return items
            .OrderByDescending(i => i.DeletedUtc)
            .ToList();
    }

    private static void ScanSidFolder(string sidDir, List<RecycleBinItem> items, Action<string>? onWarning)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(sidDir);
        }
        catch (UnauthorizedAccessException)
        {
            onWarning?.Invoke($"No access to {sidDir} (other user's bin; run elevated).");
            return;
        }

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (!name.StartsWith("$I", StringComparison.OrdinalIgnoreCase))
                continue;

            var meta = DollarIFile.TryParse(entry);
            if (meta is null)
            {
                onWarning?.Invoke($"Could not parse metadata file {entry}.");
                continue;
            }

            string backing = Path.Combine(sidDir, "$R" + name[2..]);

            if (File.Exists(backing))
            {
                if (!MediaExtensions.IsMedia(meta.OriginalPath))
                    continue;

                items.Add(new RecycleBinItem
                {
                    OriginalPath = meta.OriginalPath,
                    BackingPath = backing,
                    SizeBytes = SafeLength(backing) ?? meta.SizeBytes,
                    DeletedUtc = meta.DeletedUtc,
                    Kind = MediaExtensions.KindOf(meta.OriginalPath),
                });
            }
            else if (Directory.Exists(backing))
            {
                AddMediaInDeletedFolder(backing, meta, items, onWarning);
            }
            // else: orphaned $I with no payload — nothing recoverable.
        }
    }

    private static void AddMediaInDeletedFolder(
        string backingDir, DollarIFile.Metadata meta, List<RecycleBinItem> items, Action<string>? onWarning)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(backingDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onWarning?.Invoke($"Could not enumerate deleted folder {backingDir}: {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            if (!MediaExtensions.IsMedia(file))
                continue;

            string relative = Path.GetRelativePath(backingDir, file);
            items.Add(new RecycleBinItem
            {
                OriginalPath = Path.Combine(meta.OriginalPath, relative),
                BackingPath = file,
                SizeBytes = SafeLength(file) ?? 0,
                DeletedUtc = meta.DeletedUtc,
                Kind = MediaExtensions.KindOf(file),
                RelativePathInDeletedFolder = relative,
            });
        }
    }

    private static long? SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
