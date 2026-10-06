namespace Recovery.Core.RecycleBin;

public sealed class RestoreService
{
    public sealed record Result(RecycleBinItem Item, string? RestoredPath, string? Error)
    {
        public bool Success => RestoredPath is not null;
    }

    /// <summary>
    /// Copies items out of the bin into <paramref name="destinationDir"/> with
    /// their original names. When <paramref name="byType"/> is false, folder
    /// structure is preserved for items that were inside a deleted folder;
    /// when true, each file goes into a per-type subfolder (JPG, PNG, MP4…)
    /// instead. Never deletes the bin payload, so the operation is
    /// non-destructive and repeatable. <paramref name="onProgress"/> receives
    /// (files done, total files) after each file; cancelling stops between
    /// files and returns what was restored so far.
    /// </summary>
    public IReadOnlyList<Result> Restore(
        IEnumerable<RecycleBinItem> items, string destinationDir, bool byType = false,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        var list = items as IReadOnlyCollection<RecycleBinItem> ?? items.ToList();
        var results = new List<Result>(list.Count);

        foreach (var item in list)
        {
            if (ct.IsCancellationRequested)
                break;
            onProgress?.Invoke(results.Count, list.Count);
            try
            {
                string target = byType
                    ? Path.Combine(destinationDir, MediaExtensions.TypeFolder(item.OriginalName), item.OriginalName)
                    : item.RelativePathInDeletedFolder is { } rel
                        ? Path.Combine(destinationDir, SanitizeRelative(rel))
                        : Path.Combine(destinationDir, item.OriginalName);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                target = FileNaming.MakeUnique(target);
                File.Copy(item.BackingPath, target);
                results.Add(new Result(item, target, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                results.Add(new Result(item, null, ex.Message));
            }
        }

        onProgress?.Invoke(results.Count, list.Count);
        return results;
    }

    private static string SanitizeRelative(string relative)
    {
        // Keep restores inside the destination even if a $I path is malformed.
        var parts = relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p is not ("" or "." or ".."));
        return Path.Combine(parts.ToArray());
    }
}
