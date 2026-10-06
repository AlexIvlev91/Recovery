using Recovery.Core;
using Recovery.Core.RecycleBin;
using Recovery.Gui.Controls;

namespace Recovery.Gui.Pages;

/// <summary>
/// Tier 1: photos and videos still in the Recycle Bin — search, filter,
/// sort, preview, and restore copies (the bin itself is never modified).
/// </summary>
internal sealed class RecycleBinPage : PageBase
{
    private const int MaxShown = 5000;

    private readonly GalleryListView _list;
    private readonly ThumbnailLoader _thumbs;
    private readonly PreviewPanel _preview;
    private readonly EmptyState _empty;
    private readonly TextBox _search;
    private readonly SegmentedControl _kind;
    private readonly ComboBox _sort;
    private readonly SegmentedControl _view;
    private readonly InfoBar _skippedBar;
    private readonly FooterBar _footer;
    private readonly ModernButton _restore;
    private readonly ModernButton _refresh;
    private readonly CheckBox _byType;
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _previewDebounce = new() { Interval = 100 };

    private List<RecycleBinItem> _items = [];
    private List<RecycleBinItem> _shown = [];
    private readonly HashSet<RecycleBinItem> _checked = [];
    private bool _working;
    private bool _suppressCheck;

    public RecycleBinPage()
        : base("bin", "Recycle Bin",
            "Photos and videos still in the Recycle Bin. Restoring copies them out — nothing is removed from the bin.",
            Glyph.RecycleBin)
    {
        _refresh = new ModernButton("Rescan", ButtonKind.Secondary, Glyph.Refresh);
        _refresh.Click += (_, _) => _ = ScanAsync();
        HeaderActions.Controls.Add(_refresh);

        // ---- toolbar --------------------------------------------------------
        _search = Ui.SearchBox("Search name or original folder…", 260);
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };

        _kind = new SegmentedControl().Add("All").Add("Photos", Glyph.Photo).Add("Videos", Glyph.Video);
        _kind.SelectedIndexChanged += (_, _) => ApplyFilter();

        _sort = Ui.Combo(170, "Recently deleted", "Oldest deleted", "Largest first", "Smallest first", "Name (A–Z)", "Original folder");
        _sort.SelectedIndexChanged += (_, _) => ApplyFilter();

        _view = new SegmentedControl().Add("", Glyph.Grid, "Thumbnails").Add("", Glyph.List, "Details");
        _view.SelectedIndexChanged += (_, _) => SetView(_view.SelectedIndex == 0 ? View.LargeIcon : View.Details);

        var toolbar = new Panel { Dock = DockStyle.Top, Height = Theme.S(34), BackColor = Color.Transparent };
        var leftTools = Ui.Row(_search, _kind, Ui.FieldLabel("Sort"), _sort);
        leftTools.Dock = DockStyle.Left;
        var rightTools = Ui.Row(_view);
        rightTools.Dock = DockStyle.Right;
        toolbar.Controls.Add(leftTools);
        toolbar.Controls.Add(rightTools);

        _skippedBar = new InfoBar(InfoKind.Info,
            "Other users' Recycle Bins were skipped. Restart as administrator to include them.")
        { Dock = DockStyle.Top, Visible = false };
        _skippedBar.AddAction("Restart as administrator", (_, _) => RequestElevation()).Glyph = Glyph.Shield;

        // ---- gallery ----------------------------------------------------------
        _list = new GalleryListView { Dock = DockStyle.Fill, CheckBoxes = true, View = View.LargeIcon };
        _list.Columns.Add("Name", Theme.S(210));
        _list.Columns.Add("Original folder", Theme.S(250));
        _list.Columns.Add("Size", Theme.S(80), HorizontalAlignment.Right);
        _list.Columns.Add("Deleted", Theme.S(130));
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(24)) }; // comfortable row height in Details view
        _list.ColumnClick += (_, e) =>
        {
            _sort.SelectedIndex = e.Column switch { 0 => 4, 1 => 5, 2 => _sort.SelectedIndex == 2 ? 3 : 2, _ => _sort.SelectedIndex == 0 ? 1 : 0 };
        };
        _list.ItemChecked += (_, e) =>
        {
            if (_suppressCheck || e.Item.Tag is not RecycleBinItem it)
                return;
            if (e.Item.Checked) _checked.Add(it); else _checked.Remove(it);
            UpdateSelectionUi();
        };
        // Debounced: Ctrl+A fires one event per item and must not decode thousands of previews.
        _list.ItemSelectionChanged += (_, _) => { _previewDebounce.Stop(); _previewDebounce.Start(); };
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); UpdatePreview(); };
        _list.ItemActivate += (_, _) => { if (SelectedItem is { } it) OpenFile(it.BackingPath); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem i in _list.Items) i.Selected = true;
                e.Handled = true;
            }
        };
        _list.ContextMenuStrip = BuildContextMenu();
        _thumbs = new ThumbnailLoader(_list, Theme.S(112));

        _empty = new EmptyState { Covers = _list, Dock = DockStyle.Fill, Visible = false };
        var listCard = new Card { Dock = DockStyle.Fill, Padding = Theme.S(1, 8, 1, 1) };
        listCard.Controls.Add(_list);
        listCard.Controls.Add(_empty);

        _preview = new PreviewPanel { Dock = DockStyle.Right, Width = Theme.S(340) };
        var open = new ModernButton("Open", ButtonKind.Secondary, Glyph.Open);
        open.Click += (_, _) => { if (SelectedItem is { } it) OpenFile(it.BackingPath); };
        var restoreOne = new ModernButton("Restore this…", ButtonKind.Secondary, Glyph.Restore);
        restoreOne.Click += (_, _) => { if (SelectedItem is { } it) _ = RestoreAsync([it]); };
        _preview.Actions.Controls.Add(open);
        _preview.Actions.Controls.Add(restoreOne);

        // ---- footer -------------------------------------------------------------
        _footer = new FooterBar();
        var selectAll = new ModernButton("Select all", ButtonKind.Ghost, Glyph.SelectAll);
        selectAll.Click += (_, _) => SetChecked(_shown, true);
        var selectNone = new ModernButton("Clear", ButtonKind.Ghost, Glyph.ClearSelection);
        selectNone.Click += (_, _) => SetChecked(_shown, false);
        _byType = Ui.Check("Sort into type folders");
        _byType.Tip("Restore into JPG / PNG / MP4 … subfolders instead of the original folder layout.");
        _restore = new ModernButton("Restore selected…", ButtonKind.Primary, Glyph.Restore) { Margin = Padding.Empty };
        _restore.Click += (_, _) => _ = RestoreAsync(SelectedForRestore());
        _footer.Actions.Controls.AddRange([selectAll, selectNone, _byType, _restore]);

        Body.Controls.Add(listCard);
        Body.Controls.Add(Spacer(12, DockStyle.Right));
        Body.Controls.Add(_preview);
        Body.Controls.Add(Spacer(12, DockStyle.Bottom));
        Body.Controls.Add(_footer);
        Body.Controls.Add(Spacer(12));
        Body.Controls.Add(_skippedBar);
        Body.Controls.Add(Spacer(12));
        Body.Controls.Add(toolbar);

        UpdateSelectionUi();
    }

    protected override void OnActivated(bool firstTime)
    {
        if (firstTime)
            _ = ScanAsync();
    }

    private RecycleBinItem? SelectedItem =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as RecycleBinItem : null;

    // ---- scanning -------------------------------------------------------------

    private async Task ScanAsync()
    {
        if (_working)
            return;
        _working = true;
        Busy = true;
        _refresh.Enabled = false;
        _restore.Enabled = false;
        _footer.Status.Text = "Scanning the Recycle Bin on every drive…";
        _footer.ShowProgress(null);

        var warnings = new List<string>();
        try
        {
            _items = (await Task.Run(() => new RecycleBinScanner().Scan(w => { lock (warnings) warnings.Add(w); }))).ToList();
        }
        catch (Exception ex)
        {
            _items = [];
            ShowError("Scan failed", ex.Message);
        }
        finally
        {
            _working = false;
            Busy = false;
            _refresh.Enabled = true;
            _footer.HideProgress();
        }

        var ticked = _checked.Select(i => i.BackingPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checked.Clear();
        _checked.UnionWith(_items.Where(i => ticked.Contains(i.BackingPath)));
        _thumbs.Reset();
        _skippedBar.Visible = warnings.Count > 0 && !Elevation.IsAdministrator;
        ApplyFilter();
    }

    // ---- filtering & display --------------------------------------------------

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        IEnumerable<RecycleBinItem> query = _items;
        if (_kind.SelectedIndex == 1) query = query.Where(i => i.Kind == MediaKind.Photo);
        if (_kind.SelectedIndex == 2) query = query.Where(i => i.Kind == MediaKind.Video);
        if (q.Length > 0)
            query = query.Where(i => i.OriginalPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        query = _sort.SelectedIndex switch
        {
            1 => query.OrderBy(i => i.DeletedUtc),
            2 => query.OrderByDescending(i => i.SizeBytes),
            3 => query.OrderBy(i => i.SizeBytes),
            4 => query.OrderBy(i => i.OriginalName, StringComparer.CurrentCultureIgnoreCase),
            5 => query.OrderBy(i => Path.GetDirectoryName(i.OriginalPath), StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.OriginalName),
            _ => query.OrderByDescending(i => i.DeletedUtc),
        };
        _shown = query.ToList();
        Populate();
    }

    private void Populate()
    {
        _suppressCheck = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        var bound = new List<(ListViewItem, string)>();
        foreach (var it in _shown.Take(MaxShown))
        {
            var lvi = new ListViewItem(it.OriginalName)
            {
                Tag = it,
                Checked = _checked.Contains(it),
                ToolTipText = $"{it.OriginalPath}\n{ByteSize.Format(it.SizeBytes)} · deleted {it.DeletedUtc.ToLocalTime():g}",
            };
            lvi.SubItems.Add(Path.GetDirectoryName(it.OriginalPath) ?? "");
            lvi.SubItems.Add(ByteSize.Format(it.SizeBytes));
            lvi.SubItems.Add(it.DeletedUtc.ToLocalTime().ToString("g"));
            _list.Items.Add(lvi);
            bound.Add((lvi, it.BackingPath));
        }
        _list.EndUpdate();
        _suppressCheck = false;
        _thumbs.Bind(bound);

        if (_items.Count == 0 && !_working)
        {
            _empty.Show(Glyph.RecycleBin, "No deleted photos or videos in the Recycle Bin",
                "Files removed from the bin (or deleted with Shift+Delete) may still be on the disk. " +
                "Try Undelete by name first — it keeps original names — then Deep scan.",
                ("Undelete by name", ButtonKind.Primary, () => NavigateTo("undelete")),
                ("Deep scan", ButtonKind.Secondary, () => NavigateTo("carve")));
        }
        else if (_shown.Count == 0 && !_working)
        {
            _empty.Show(Glyph.Filter, "Nothing matches your filters", "Clear the search box or pick \"All\".",
                ("Clear filters", ButtonKind.Secondary, () => { _search.Text = ""; _kind.SelectedIndex = 0; ApplyFilter(); }));
        }
        else
        {
            _empty.Visible = false;
        }

        _preview.Visible = _items.Count > 0;
        UpdatePreview();
        UpdateSelectionUi();
    }

    private void SetView(View view)
    {
        _list.View = view;
        _thumbs.Bind(_list.Items.Cast<ListViewItem>().Select(i => (i, ((RecycleBinItem)i.Tag!).BackingPath)).ToList());
    }

    private void SetChecked(IEnumerable<RecycleBinItem> items, bool value)
    {
        foreach (var it in items)
        {
            if (value) _checked.Add(it); else _checked.Remove(it);
        }
        _suppressCheck = true;
        _list.BeginUpdate();
        foreach (ListViewItem lvi in _list.Items)
            lvi.Checked = _checked.Contains((RecycleBinItem)lvi.Tag!);
        _list.EndUpdate();
        _suppressCheck = false;
        UpdateSelectionUi();
    }

    /// <summary>Ticked items that pass the current filters — what you see is what gets restored.</summary>
    private List<RecycleBinItem> SelectedForRestore() => _shown.Where(_checked.Contains).ToList();

    private void UpdateSelectionUi()
    {
        if (_working)
            return;
        var sel = SelectedForRestore();
        long bytes = sel.Sum(i => i.SizeBytes);
        _restore.Text = sel.Count == 0 ? "Restore selected…" : $"Restore {Plural(sel.Count, "file")} ({ByteSize.Format(bytes)})…";
        _restore.Enabled = sel.Count > 0;

        long total = _items.Sum(i => i.SizeBytes);
        string status = _items.Count == 0
            ? "The Recycle Bin has no photos or videos."
            : _shown.Count == _items.Count
                ? $"{Plural(_items.Count, "photo or video", "photos and videos")} · {ByteSize.Format(total)}"
                : $"Showing {_shown.Count:N0} of {_items.Count:N0} · {ByteSize.Format(_shown.Sum(i => i.SizeBytes))}";
        if (_shown.Count > MaxShown)
            status += $" — first {MaxShown:N0} displayed, narrow with search";
        int hiddenChecked = _checked.Count - sel.Count;
        if (hiddenChecked > 0)
            status += $" · {hiddenChecked:N0} ticked but hidden by filters";
        _footer.Status.Text = status;
    }

    private void UpdatePreview()
    {
        if (SelectedItem is not { } it)
        {
            _preview.ShowEmpty(_items.Count == 0 ? "" : "Select a photo or video to preview it.\nDouble-click opens it.");
            return;
        }
        _preview.ShowItem(it.OriginalName,
        [
            ("Type", $"{(it.Kind == MediaKind.Video ? "Video" : "Photo")} · {Path.GetExtension(it.OriginalName).TrimStart('.').ToUpperInvariant()}"),
            ("Size", ByteSize.Format(it.SizeBytes)),
            ("Deleted", it.DeletedUtc.ToLocalTime().ToString("f")),
            ("From", Path.GetDirectoryName(it.OriginalPath) ?? ""),
        ]);
        string path = it.BackingPath;
        _preview.LoadImage(() => ImageLoader.FromFile(path));
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => { if (SelectedItem is { } it) OpenFile(it.BackingPath); });
        menu.Items.Add("Restore selected…", null, (_, _) =>
            _ = RestoreAsync(_list.SelectedItems.Cast<ListViewItem>().Select(i => (RecycleBinItem)i.Tag!).ToList()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Tick selected", null, (_, _) => SetChecked(SelectedItems(), true));
        menu.Items.Add("Untick selected", null, (_, _) => SetChecked(SelectedItems(), false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy original path", null, (_, _) => { if (SelectedItem is { } it) Clipboard.SetText(it.OriginalPath); });
        menu.Opening += (_, e) => e.Cancel = _list.SelectedItems.Count == 0;
        return menu;

        List<RecycleBinItem> SelectedItems() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (RecycleBinItem)i.Tag!).ToList();
    }

    // ---- restore ------------------------------------------------------------------

    private async Task RestoreAsync(IReadOnlyList<RecycleBinItem> items)
    {
        if (_working)
            return;
        if (items.Count == 0)
        {
            ShowInfo("Nothing selected", "Tick the photos and videos you want to restore first.");
            return;
        }
        string? dest = PickFolder($"Choose where to restore {Plural(items.Count, "file")}",
            AppSettings.Get("bin.dest", AppSettings.DefaultFolder("Restored")));
        if (dest is null)
            return;
        AppSettings.Set("bin.dest", dest);

        _working = true;
        Busy = true;
        _restore.Enabled = _refresh.Enabled = false;
        _footer.ShowProgress(0);
        _footer.Status.Text = $"Restoring {Plural(items.Count, "file")}…";
        bool byType = _byType.Checked;

        IReadOnlyList<RestoreService.Result> results;
        try
        {
            results = await Task.Run(() => new RestoreService().Restore(items, dest, byType,
                (done, total) => RunOnUi(() =>
                {
                    _footer.Progress.Value = total == 0 ? 1 : (double)done / total;
                    _footer.Status.Text = $"Restoring {done + 1:N0} of {total:N0}…";
                })));
        }
        catch (Exception ex)
        {
            ShowError("Restore failed", ex.Message);
            return;
        }
        finally
        {
            _working = false;
            Busy = false;
            _refresh.Enabled = true;
            _footer.HideProgress();
            UpdateSelectionUi();
        }

        int ok = results.Count(r => r.Success);
        var failures = results.Where(r => !r.Success).ToList();
        _footer.Status.Text = $"Restored {ok:N0} of {results.Count:N0} to {dest}.";
        if (failures.Count > 0)
        {
            string detail = string.Join("\n", failures.Take(10).Select(f => $"• {f.Item.OriginalName}: {f.Error}"));
            ShowError("Some files could not be restored", $"{Plural(failures.Count, "file")} failed:\n\n{detail}");
        }
        if (ok > 0)
            ShowInExplorer(dest);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbs.Dispose();
            _searchDebounce.Dispose();
            _previewDebounce.Dispose();
        }
        base.Dispose(disposing);
    }
}
