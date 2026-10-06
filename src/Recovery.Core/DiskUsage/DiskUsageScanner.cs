using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace Recovery.Core.DiskUsage;

public sealed record DiskUsageProgress(long Files, long Folders, long Bytes, string CurrentFolder, TimeSpan Elapsed);

/// <summary>Result of a disk-usage scan.</summary>
public sealed class DiskUsageResult
{
    internal DiskUsageResult(DirNode root, long unreadableFolders, long onlineOnlyFiles, long onlineOnlyBytes, TimeSpan elapsed)
    {
        Root = root;
        UnreadableFolders = unreadableFolders;
        OnlineOnlyFiles = onlineOnlyFiles;
        OnlineOnlyBytes = onlineOnlyBytes;
        Elapsed = elapsed;
    }

    public DirNode Root { get; }

    /// <summary>Folders that couldn't be listed (access denied etc.). Run elevated to see more.</summary>
    public long UnreadableFolders { get; }

    /// <summary>Cloud placeholders (OneDrive "online-only") — they take no local space and aren't counted.</summary>
    public long OnlineOnlyFiles { get; }

    public long OnlineOnlyBytes { get; }

    public TimeSpan Elapsed { get; }
}

/// <summary>
/// Builds a folder tree with recursive sizes for a drive or folder, the way
/// WinDirStat/TreeSize do: every file's logical size is attributed to all of
/// its parent folders, broken down by <see cref="FileCategory"/>.
///
/// Folders are listed in parallel by a pool of workers (I/O latency dominates,
/// so this is several times faster than a single-threaded walk on SSDs).
/// Junctions/symlinks are not followed (no loops, no double counting) and
/// OneDrive online-only placeholders are excluded because they occupy no
/// local space.
/// </summary>
public sealed class DiskUsageScanner
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS: cloud placeholders.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes OnlineOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,          // include hidden & system files — they use space too
        IgnoreInaccessible = false,    // we want to know which folders were unreadable
        RecurseSubdirectories = false, // the worker pool does the recursion
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, long LastWriteTicks, FileAttributes Attributes);

    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 12);

    public DiskUsageResult Scan(string rootPath, Action<DiskUsageProgress>? onProgress = null, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Folder not found: {full}");
        // Keep "C:\" as-is but drop the trailing separator from "D:\Photos\".
        if (full.Length > 3)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var root = new DirNode(full, null);
        var state = new ScanState(onProgress, ct);
        var stack = new ConcurrentStack<(DirNode Node, string Path)>();
        stack.Push((root, full));
        state.Pending = 1;

        using var signal = new SemaphoreSlim(1);
        var workers = new Task[Parallelism];
        for (int i = 0; i < workers.Length; i++)
            workers[i] = Task.Factory.StartNew(() => Work(stack, signal, state), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Task.WaitAll(workers, CancellationToken.None);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // fall through to the cancellation check below
        }
        ct.ThrowIfCancellationRequested();

        Aggregate(root);
        state.Report(full, force: true);
        return new DiskUsageResult(root, state.Unreadable, state.OnlineOnlyFiles, state.OnlineOnlyBytes, state.Clock.Elapsed);
    }

    private sealed class ScanState(Action<DiskUsageProgress>? onProgress, CancellationToken ct)
    {
        public readonly CancellationToken Ct = ct;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long Pending;
        public long Files, Folders, Bytes, Unreadable, OnlineOnlyFiles, OnlineOnlyBytes;
        private long _nextReportMs;

        public void Report(string folder, bool force = false)
        {
            if (onProgress is null)
                return;
            long now = Clock.ElapsedMilliseconds;
            long due = Interlocked.Read(ref _nextReportMs);
            if (!force && (now < due || Interlocked.CompareExchange(ref _nextReportMs, now + 100, due) != due))
                return;
            onProgress(new DiskUsageProgress(
                Interlocked.Read(ref Files), Interlocked.Read(ref Folders), Interlocked.Read(ref Bytes), folder, Clock.Elapsed));
        }
    }

    private void Work(ConcurrentStack<(DirNode Node, string Path)> stack, SemaphoreSlim signal, ScanState state)
    {
        var files = new List<FileEntry>(256);
        while (true)
        {
            try
            {
                signal.Wait(state.Ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (Interlocked.Read(ref state.Pending) == 0)
            {
                signal.Release(); // wake the next worker so it can exit too
                return;
            }
            if (!stack.TryPop(out var item))
                continue;

            List<(DirNode, string)>? children;
            try
            {
                children = ListFolder(item.Node, item.Path, files, state);
            }
            catch (OperationCanceledException)
            {
                return; // the other workers see the token and exit too
            }
            catch (Exception)
            {
                // Never let one odd folder kill a worker: the pending count would
                // never reach zero and the scan would hang.
                item.Node.Unreadable = true;
                Interlocked.Increment(ref state.Unreadable);
                children = null;
            }

            if (children is { Count: > 0 })
            {
                Interlocked.Add(ref state.Pending, children.Count);
                stack.PushRange(children.ToArray());
                signal.Release(children.Count);
            }
            if (Interlocked.Decrement(ref state.Pending) == 0)
                signal.Release(); // all done — start the exit chain
        }
    }

    private static List<(DirNode, string)>? ListFolder(DirNode node, string path, List<FileEntry> files, ScanState state)
    {
        List<(DirNode, string)>? subdirs = null;
        files.Clear();
        try
        {
            var entries = new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length,
                    e.LastWriteTimeUtc.UtcTicks, e.Attributes),
                Options);

            foreach (var e in entries)
            {
                if ((files.Count & 4095) == 4095)
                    state.Ct.ThrowIfCancellationRequested();
                if (e.IsDirectory)
                {
                    // Junctions and directory symlinks point elsewhere (often back up
                    // the tree, e.g. "Application Data") — following them would loop
                    // or count the same bytes twice.
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var child = new DirNode(e.Name, node);
                    (node.ChildList ??= []).Add(child);
                    (subdirs ??= []).Add((child, Path.Join(path, e.Name)));
                    continue;
                }

                long size = e.Length;
                if ((e.Attributes & OnlineOnly) != 0)
                {
                    Interlocked.Increment(ref state.OnlineOnlyFiles);
                    Interlocked.Add(ref state.OnlineOnlyBytes, size);
                    size = 0;
                }
                files.Add(new FileEntry(e.Name, size, e.LastWriteTicks, FileCategories.Of(e.Name, e.Length)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Unreadable = true;
            Interlocked.Increment(ref state.Unreadable);
        }

        node.FileArray = files.Count == 0 ? [] : files.ToArray();
        Interlocked.Add(ref state.Files, files.Count);
        Interlocked.Increment(ref state.Folders);
        long bytes = 0;
        foreach (var f in files)
            bytes += f.Size;
        Interlocked.Add(ref state.Bytes, bytes);
        state.Report(path);
        return subdirs;
    }

    /// <summary>Post-order pass: recursive totals, then sort children and files largest-first.</summary>
    private static void Aggregate(DirNode root)
    {
        // Iterative so pathologically deep trees can't overflow the stack.
        var order = new List<DirNode>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            if (n.ChildList is { } kids)
            {
                foreach (var k in kids)
                    stack.Push(k);
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
            Recompute(order[i]);
    }

    /// <summary>Rebuild a node's totals from its files and (already computed) children.</summary>
    internal static void Recompute(DirNode n)
    {
        Array.Clear(n.CategoryBytes);
        Array.Clear(n.CategoryFiles);
        long own = 0, newest = 0;
        foreach (var f in n.FileArray)
        {
            own += f.Size;
            n.CategoryBytes[(int)f.Category] += f.Size;
            n.CategoryFiles[(int)f.Category]++;
            if (f.LastWriteUtcTicks > newest)
                newest = f.LastWriteUtcTicks;
        }
        if (n.FileArray.Length > 1)
            Array.Sort(n.FileArray, static (a, b) => b.Size.CompareTo(a.Size));

        long size = own, files = n.FileArray.Length, folders = 0;
        bool incomplete = n.Unreadable;
        if (n.ChildList is { } kids)
        {
            foreach (var k in kids)
            {
                size += k.Size;
                files += k.FileCount;
                folders += 1 + k.FolderCount;
                incomplete |= k.Incomplete;
                if (k.LastWriteUtcTicks > newest)
                    newest = k.LastWriteUtcTicks;
                for (int c = 0; c < n.CategoryBytes.Length; c++)
                {
                    n.CategoryBytes[c] += k.CategoryBytes[c];
                    n.CategoryFiles[c] += k.CategoryFiles[c];
                }
            }
            kids.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        }

        n.OwnFilesSize = own;
        n.Size = size;
        n.FileCount = files;
        n.FolderCount = folders;
        n.Incomplete = incomplete;
        n.LastWriteUtcTicks = newest;
    }

    /// <summary>
    /// Remove a file from the tree (after it was deleted on disk) and update
    /// every ancestor's totals.
    /// </summary>
    public static void RemoveFile(DirNode folder, FileEntry file)
    {
        int idx = Array.IndexOf(folder.FileArray, file);
        if (idx < 0)
            return;
        var copy = new List<FileEntry>(folder.FileArray);
        copy.RemoveAt(idx);
        folder.FileArray = copy.ToArray();
        RecomputeUpwards(folder);
    }

    /// <summary>Remove a folder (deleted on disk) from the tree and update its ancestors.</summary>
    public static void RemoveFolder(DirNode folder)
    {
        if (folder.Parent is not { } parent)
            throw new InvalidOperationException("Cannot remove the scan root.");
        parent.ChildList?.Remove(folder);
        RecomputeUpwards(parent);
    }

    private static void RecomputeUpwards(DirNode from)
    {
        for (DirNode? n = from; n is not null; n = n.Parent)
            Recompute(n);
    }
}
