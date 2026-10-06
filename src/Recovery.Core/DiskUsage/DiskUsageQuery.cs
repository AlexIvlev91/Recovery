namespace Recovery.Core.DiskUsage;

/// <summary>A file found by a query, with the folder that holds it.</summary>
public sealed record FileHit(DirNode Folder, FileEntry File)
{
    public string FullPath => Path.Join(Folder.FullPath, File.Name);
}

/// <summary>Totals for one file extension below a folder.</summary>
public sealed record ExtensionStat(string Extension, FileCategory Category, long Files, long Bytes);

/// <summary>Filter for <see cref="DiskUsageQuery.LargestFiles"/>. Null/default members don't filter.</summary>
public sealed record FileFilter
{
    public IReadOnlySet<FileCategory>? Categories { get; init; }
    public long MinSize { get; init; }
    public long MaxSize { get; init; } = long.MaxValue;
    /// <summary>Case-insensitive substring of the file name.</summary>
    public string? NameContains { get; init; }
    /// <summary>Exact extension as <see cref="DiskUsageQuery.ExtensionOf"/> reports it (".mp4"); "" = files without one.</summary>
    public string? Extension { get; init; }
    /// <summary>Only files last modified before this time (UTC) — e.g. "older than a year".</summary>
    public DateTime? ModifiedBeforeUtc { get; init; }

    public bool Matches(in FileEntry f)
    {
        if (f.Size < MinSize || f.Size > MaxSize)
            return false;
        if (Categories is not null && !Categories.Contains(f.Category))
            return false;
        if (ModifiedBeforeUtc is { } before && f.LastWriteUtcTicks >= before.Ticks)
            return false;
        if (Extension is { } ext && !string.Equals(DiskUsageQuery.ExtensionOf(f.Name), ext, StringComparison.OrdinalIgnoreCase))
            return false;
        if (NameContains is { Length: > 0 } text && !f.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}

public static class DiskUsageQuery
{
    /// <summary>
    /// The <paramref name="limit"/> largest files at any depth below
    /// <paramref name="folder"/> that pass <paramref name="filter"/>, largest
    /// first, plus the total count/bytes of everything that matched.
    /// </summary>
    public static (List<FileHit> Top, long MatchCount, long MatchBytes) LargestFiles(
        DirNode folder, FileFilter filter, int limit, CancellationToken ct = default)
    {
        var heap = new PriorityQueue<FileHit, long>(limit + 1);
        long count = 0, bytes = 0;
        int visited = 0;

        foreach (var dir in folder.SelfAndDescendants())
        {
            if ((++visited & 1023) == 0)
                ct.ThrowIfCancellationRequested();

            var files = dir.FileArray;
            for (int i = 0; i < files.Length; i++)
            {
                ref readonly var f = ref files[i];
                if (!filter.Matches(f))
                    continue;
                count++;
                bytes += f.Size;
                if (heap.Count < limit)
                {
                    heap.Enqueue(new FileHit(dir, f), f.Size);
                }
                else if (heap.TryPeek(out _, out long smallest) && f.Size > smallest)
                {
                    heap.EnqueueDequeue(new FileHit(dir, f), f.Size);
                }
            }
        }

        var top = new List<FileHit>(heap.Count);
        while (heap.TryDequeue(out var hit, out _))
            top.Add(hit);
        top.Reverse();
        return (top, count, bytes);
    }

    /// <summary>Per-extension totals below <paramref name="folder"/>, largest first.</summary>
    public static List<ExtensionStat> ExtensionBreakdown(DirNode folder, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (FileCategory Cat, long Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        foreach (var dir in folder.SelfAndDescendants())
        {
            if ((++visited & 1023) == 0)
                ct.ThrowIfCancellationRequested();
            foreach (var f in dir.FileArray)
            {
                string ext = ExtensionOf(f.Name);
                map.TryGetValue(ext, out var s);
                map[ext] = (f.Category, s.Files + 1, s.Bytes + f.Size);
            }
        }
        return map
            .Select(kv => new ExtensionStat(kv.Key, kv.Value.Cat, kv.Value.Files, kv.Value.Bytes))
            .OrderByDescending(s => s.Bytes)
            .ToList();
    }

    /// <summary>Lower-cased extension including the dot, or "" when the name has none.</summary>
    public static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 || name.Length - dot > 16 ? "" : name[dot..].ToLowerInvariant();
    }

    /// <summary>
    /// Folders below <paramref name="folder"/> (any depth) that are at least
    /// <paramref name="minSize"/>, largest first — "where did the space go".
    /// Only the deepest qualifying folders are useful, so a folder is skipped
    /// when a single child accounts for nearly all of it.
    /// </summary>
    public static List<DirNode> BiggestFolders(DirNode folder, int limit, CancellationToken ct = default)
    {
        var heap = new PriorityQueue<DirNode, long>(limit + 1);
        int visited = 0;
        foreach (var dir in folder.SelfAndDescendants())
        {
            if ((++visited & 1023) == 0)
                ct.ThrowIfCancellationRequested();
            if (dir == folder || dir.Size == 0)
                continue;
            // A folder whose biggest child holds ≥ 90% of it adds nothing beyond that child.
            if (dir.ChildList is { Count: > 0 } kids && kids[0].Size >= dir.Size * 0.9)
                continue;
            if (heap.Count < limit)
                heap.Enqueue(dir, dir.Size);
            else if (heap.TryPeek(out _, out long smallest) && dir.Size > smallest)
                heap.EnqueueDequeue(dir, dir.Size);
        }
        var list = new List<DirNode>(heap.Count);
        while (heap.TryDequeue(out var d, out _))
            list.Add(d);
        list.Reverse();
        return list;
    }
}
