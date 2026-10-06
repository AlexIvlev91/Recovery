namespace Recovery.Core.DiskUsage;

/// <summary>One file in a disk-usage tree. Kept as a compact struct — a full drive has millions.</summary>
public readonly record struct FileEntry(string Name, long Size, long LastWriteUtcTicks, FileCategory Category)
{
    public DateTime LastWriteUtc => new(LastWriteUtcTicks, DateTimeKind.Utc);
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
}

/// <summary>
/// A folder in a disk-usage tree with recursive totals. After a scan the
/// children are sorted largest-first and files largest-first.
/// </summary>
public sealed class DirNode
{
    private static readonly List<DirNode> NoChildren = [];

    internal List<DirNode>? ChildList;
    internal FileEntry[] FileArray = [];
    internal readonly long[] CategoryBytes = new long[FileCategories.Count];
    internal readonly long[] CategoryFiles = new long[FileCategories.Count];

    internal DirNode(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>Folder name; for the scan root, the full root path.</summary>
    public string Name { get; }

    public DirNode? Parent { get; }

    public IReadOnlyList<DirNode> Children => ChildList ?? NoChildren;

    /// <summary>Files directly in this folder.</summary>
    public IReadOnlyList<FileEntry> Files => FileArray;

    /// <summary>Total bytes of all files at any depth below this folder.</summary>
    public long Size { get; internal set; }

    /// <summary>Total bytes of the files directly in this folder.</summary>
    public long OwnFilesSize { get; internal set; }

    /// <summary>Number of files at any depth below this folder.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Number of subfolders at any depth below this folder.</summary>
    public long FolderCount { get; internal set; }

    /// <summary>Newest file modification time at any depth (UTC ticks; 0 when empty).</summary>
    public long LastWriteUtcTicks { get; internal set; }

    /// <summary>The folder (or something inside it) could not be listed — totals are a lower bound.</summary>
    public bool Incomplete { get; internal set; }

    /// <summary>This folder itself could not be listed (access denied, in use…).</summary>
    public bool Unreadable { get; internal set; }

    public long BytesOf(FileCategory c) => CategoryBytes[(int)c];

    public long FilesOf(FileCategory c) => CategoryFiles[(int)c];

    /// <summary>The category holding the most bytes below this folder.</summary>
    public FileCategory DominantCategory
    {
        get
        {
            int best = (int)FileCategory.Other;
            for (int i = 0; i < CategoryBytes.Length; i++)
            {
                if (CategoryBytes[i] > CategoryBytes[best])
                    best = i;
            }
            return (FileCategory)best;
        }
    }

    public bool IsRoot => Parent is null;

    public string FullPath
    {
        get
        {
            if (Parent is null)
                return Name;
            var parts = new Stack<string>();
            DirNode? n = this;
            for (; n!.Parent is not null; n = n.Parent)
                parts.Push(n.Name);
            return Path.Combine(n.Name, string.Join(Path.DirectorySeparatorChar, parts));
        }
    }

    /// <summary>Share of the parent's size, 0..1 (1 for the root).</summary>
    public double ShareOfParent => Parent is null ? 1 : Parent.Size == 0 ? 0 : (double)Size / Parent.Size;

    public IEnumerable<DirNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            yield return p;
    }

    /// <summary>This folder and every folder below it, parents before children.</summary>
    public IEnumerable<DirNode> SelfAndDescendants()
    {
        var stack = new Stack<DirNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.ChildList is { } kids)
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push(kids[i]);
            }
        }
    }

    public bool IsAncestorOf(DirNode other)
    {
        for (var p = other.Parent; p is not null; p = p.Parent)
        {
            if (p == this)
                return true;
        }
        return false;
    }

    public override string ToString() => $"{Name} ({ByteSize.Format(Size)})";
}
