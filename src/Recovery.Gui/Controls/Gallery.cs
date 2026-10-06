using Recovery.Core;

namespace Recovery.Gui.Controls;

/// <summary>
/// ListView for thumbnail grids: double-buffered, Explorer-styled, and raises
/// <see cref="Scrolled"/> so thumbnails for newly visible tiles load first.
/// </summary>
internal sealed class GalleryListView : ListView
{
    private const int WM_HSCROLL = 0x0114, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100;

    public GalleryListView()
    {
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
        ShowItemToolTips = true;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        HideSelection = false;
        Native.UseExplorerStyle(this);
    }

    public event EventHandler? Scrolled;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WM_VSCROLL or WM_HSCROLL or WM_MOUSEWHEEL or WM_KEYDOWN)
            Scrolled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Scrolled?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Loads shell thumbnails for a <see cref="GalleryListView"/> on a background
/// thread, visible tiles first. Items are matched by <see cref="ListViewItem.Name"/>
/// (set to the file path by <see cref="Bind"/>). Thumbnails are cached by path
/// across re-filters; <see cref="Reset"/> drops the cache when the file set changes.
/// </summary>
internal sealed class ThumbnailLoader : IDisposable
{
    public const string PhotoKey = "~photo";
    public const string VideoKey = "~video";
    public const string OtherKey = "~other";

    private readonly ListView _list;
    private readonly int _size;
    private readonly int _budget;
    private readonly object _gate = new();
    private readonly Stack<string> _priority = new();
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    // UI-thread only: thumbnails already in the ImageList, and the current items by path.
    private readonly HashSet<string> _cached = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ListViewItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 120 };
    private List<string> _queue = [];
    private int _next;
    private int _made;
    private bool _running;
    private bool _disposed;

    public ThumbnailLoader(ListView list, int size, int budget = 1500)
    {
        _list = list;
        _size = size;
        _budget = budget;
        Images = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        AddPlaceholders();
        list.LargeImageList = Images;

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            PrioritizeVisible();
        };
        if (list is GalleryListView g)
            g.Scrolled += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        // A list that was hidden (e.g. behind an empty-state message) has no handle yet;
        // start loading once it can actually show thumbnails.
        list.HandleCreated += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        list.VisibleChanged += (_, _) => { if (list.Visible) { _debounce.Stop(); _debounce.Start(); } };
    }

    public ImageList Images { get; }

    public static string PlaceholderFor(string path) => MediaExtensions.KindOf(path) switch
    {
        MediaKind.Video => VideoKey,
        MediaKind.Photo => PhotoKey,
        _ => OtherKey,
    };

    /// <summary>
    /// Call after (re)filling the list, with items in display order. Items get
    /// a cached thumbnail immediately when one exists, otherwise a placeholder.
    /// </summary>
    public void Bind(IEnumerable<(ListViewItem Item, string Path)> entries)
    {
        var queue = new List<string>();
        var byPath = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, path) in entries)
        {
            item.Name = path;
            item.ImageKey = _cached.Contains(path) ? path : PlaceholderFor(path);
            byPath[path] = item;
            queue.Add(path);
        }
        _byPath = byPath;
        lock (_gate)
        {
            _queue = queue;
            _next = 0;
            _priority.Clear();
        }
        PrioritizeVisible();
    }

    /// <summary>Forget all cached thumbnails (the underlying files changed).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queue = [];
            _next = 0;
            _priority.Clear();
            _done.Clear();
            _made = 0;
        }
        _cached.Clear();
        _byPath.Clear();
        Images.Images.Clear();
        AddPlaceholders();
    }

    private void AddPlaceholders()
    {
        Images.Images.Add(PhotoKey, MakePlaceholder(Glyph.Photo));
        Images.Images.Add(VideoKey, MakePlaceholder(Glyph.Video));
        Images.Images.Add(OtherKey, MakePlaceholder(Glyph.Page));
    }

    private void PrioritizeVisible()
    {
        if (_disposed || !_list.IsHandleCreated || _list.Items.Count == 0)
            return;
        var visible = VisibleItems();
        lock (_gate)
        {
            _priority.Clear();
            for (int i = visible.Count - 1; i >= 0; i--)
            {
                if (!_done.Contains(visible[i]))
                    _priority.Push(visible[i]);
            }
        }
        EnsureWorker();
    }

    /// <summary>Paths of the items currently on screen (items are in display order, so binary search for the first).</summary>
    private List<string> VisibleItems()
    {
        var result = new List<string>();
        int count = _list.Items.Count;
        int height = _list.ClientSize.Height;
        int lo = 0, hi = count - 1, first = count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_list.Items[mid].Bounds.Bottom >= 0)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        for (int i = Math.Max(0, first - 8); i < count; i++)
        {
            var item = _list.Items[i];
            var b = item.Bounds;
            if (b.Top > height + _size) // one extra row of look-ahead
                break;
            if (b.Bottom >= -_size && !string.IsNullOrEmpty(item.Name))
                result.Add(item.Name);
        }
        return result;
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            if (_running || _disposed)
                return;
            _running = true;
        }
        Task.Run(Work);
    }

    private string? NextPath()
    {
        while (_priority.Count > 0)
        {
            string p = _priority.Pop();
            if (_done.Add(p))
                return p;
        }
        while (_next < _queue.Count)
        {
            string p = _queue[_next++];
            if (_done.Add(p))
                return p;
        }
        return null;
    }

    private void Work()
    {
        while (true)
        {
            string? path;
            lock (_gate)
            {
                // No handle → results couldn't be posted; stop and resume on HandleCreated.
                path = _disposed || _made >= _budget || !_list.IsHandleCreated ? null : NextPath();
                if (path is null)
                {
                    _running = false;
                    return;
                }
            }

            Bitmap? tile = null;
            try
            {
                tile = ShellThumbnail.TryGetTile(path, _size);
            }
            catch
            {
                // undecodable — the placeholder stays
            }
            if (tile is null)
                continue;
            Interlocked.Increment(ref _made);

            if (!Post(() => Apply(path, tile)))
            {
                tile.Dispose();
                lock (_gate)
                {
                    _done.Remove(path); // retry when the list is back
                    Interlocked.Decrement(ref _made);
                    _running = false;
                }
                return;
            }
        }
    }

    private void Apply(string path, Bitmap tile)
    {
        try
        {
            if (_disposed)
                return;
            if (_cached.Add(path))
                Images.Images.Add(path, tile); // ImageList copies the bitmap
            if (_byPath.TryGetValue(path, out var item) && item.ListView == _list)
                item.ImageKey = path;
        }
        finally
        {
            tile.Dispose();
        }
    }

    private bool Post(Action action)
    {
        if (_disposed || _list.IsDisposed || !_list.IsHandleCreated)
            return false;
        try
        {
            _list.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private Bitmap MakePlaceholder(string glyph)
    {
        var bmp = new Bitmap(_size, _size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Theme.ThumbBack);
        Theme.DrawGlyph(g, glyph, _size / 5f / Theme.DpiScale, Theme.TextSubtle, new Rectangle(0, 0, _size, _size));
        return bmp;
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _debounce.Dispose();
        Images.Dispose();
    }
}
