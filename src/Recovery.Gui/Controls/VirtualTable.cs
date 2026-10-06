using System.ComponentModel;

namespace Recovery.Gui.Controls;

/// <summary>
/// Details-view list in virtual mode: handles hundreds of thousands of rows
/// without creating a ListViewItem per row. Columns are declared with a text
/// selector and an optional comparer (header click sorts, with the native
/// arrow). Optional checkboxes are backed by the caller's own state.
/// </summary>
internal sealed class VirtualTable<T> : ListView where T : class
{
    private sealed record Column(string Title, Func<T, string> Text, Comparison<T>? Compare, Action<DrawListViewSubItemEventArgs, T>? Draw);

    private readonly List<Column> _columns = [];
    private List<T> _rows = [];
    private int _sortColumn = -1;
    private SortOrder _sortOrder = SortOrder.None;
    private ListViewItem? _cacheItem;
    private int _cacheIndex = -1;
    private bool _ownerDrawColumns;
    private bool _restoringSelection;

    public VirtualTable()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Native.UseExplorerStyle(this);
        RetrieveVirtualItem += OnRetrieve;
        ColumnClick += (_, e) => SortBy(e.Column, _sortColumn == e.Column && _sortOrder == SortOrder.Descending ? SortOrder.Ascending : SortOrder.Descending);
        // Small-icon list gives rows a comfortable height; the image itself is blank.
        SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(24)) };
    }

    /// <summary>Called by the table to read/write a row's checkbox. Null = no checkboxes.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<T, bool>? IsChecked { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<T, bool>? SetChecked { get; set; }

    /// <summary>Rows for which the checkbox can't be ticked (e.g. unrecoverable).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<T, bool>? CanCheck { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<T, Color?>? RowColor { get; set; }

    /// <summary>Raised after the user ticks/unticks rows.</summary>
    public event EventHandler? CheckedChanged;

    /// <summary>Raised on double-click / Enter on a row.</summary>
    public event EventHandler<T>? RowActivated;

    public IReadOnlyList<T> Rows => _rows;

    public void EnableCheckboxes()
    {
        CheckBoxes = true;
        MouseDown += OnMouseDownCheck;
        KeyDown += OnKeyDownCheck;
    }

    public VirtualTable<T> AddColumn(string title, int width, Func<T, string> text, Comparison<T>? compare = null,
        HorizontalAlignment align = HorizontalAlignment.Left, Action<DrawListViewSubItemEventArgs, T>? draw = null)
    {
        _columns.Add(new Column(title, text, compare, draw));
        Columns.Add(title, Theme.S(width), align);
        if (draw is not null && !_ownerDrawColumns)
        {
            _ownerDrawColumns = true;
            OwnerDraw = true;
            DrawColumnHeader += (_, e) => e.DrawDefault = true;
            DrawItem += (_, e) => { }; // sub-items do the drawing
            DrawSubItem += OnDrawSubItem;
        }
        return this;
    }

    public void SetRows(IEnumerable<T> rows)
    {
        var selected = SelectedRow;
        // Re-selecting the same row after a refresh must not look like a new selection.
        _restoringSelection = true;
        try
        {
            SelectedIndices.Clear();
            _rows = rows as List<T> ?? rows.ToList();
            ApplySort();
            _cacheIndex = -1;
            VirtualListSize = _rows.Count;
            if (selected is not null)
            {
                int idx = _rows.IndexOf(selected);
                if (idx >= 0)
                {
                    SelectedIndices.Add(idx);
                    FocusedItem = Items[idx];
                }
            }
        }
        finally
        {
            _restoringSelection = false;
        }
        if (!ReferenceEquals(selected, SelectedRow))
            base.OnSelectedIndexChanged(EventArgs.Empty);
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        if (!_restoringSelection)
            base.OnSelectedIndexChanged(e);
    }

    /// <summary>Redraw after row data (not the row set) changed.</summary>
    public void RefreshRows()
    {
        _cacheIndex = -1;
        Invalidate();
    }

    public void SortBy(int column, SortOrder order)
    {
        if (column < 0 || column >= _columns.Count || _columns[column].Compare is null)
            return;
        var selected = SelectedRow;
        _sortColumn = column;
        _sortOrder = order;
        ApplySort();
        _cacheIndex = -1;
        Native.SetSortArrow(this, column, order);
        _restoringSelection = true;
        try
        {
            SelectedIndices.Clear();
            if (selected is not null && _rows.IndexOf(selected) is var idx and >= 0)
            {
                SelectedIndices.Add(idx);
                EnsureVisible(idx);
            }
        }
        finally
        {
            _restoringSelection = false;
        }
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_sortColumn >= 0)
            Native.SetSortArrow(this, _sortColumn, _sortOrder);
    }

    private void ApplySort()
    {
        if (_sortColumn < 0 || _columns[_sortColumn].Compare is not { } cmp)
            return;
        Comparison<T> c = _sortOrder == SortOrder.Ascending ? cmp : (a, b) => cmp(b, a);
        // List.Sort isn't stable; ties keep a deterministic order via index.
        var indexed = _rows.Select((r, i) => (r, i)).ToList();
        indexed.Sort((x, y) => c(x.r, y.r) is var d && d != 0 ? d : x.i.CompareTo(y.i));
        _rows = indexed.Select(x => x.r).ToList();
    }

    public T? SelectedRow => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]] : null;

    public IEnumerable<T> SelectedRows
    {
        get
        {
            foreach (int i in SelectedIndices)
            {
                if (i < _rows.Count)
                    yield return _rows[i];
            }
        }
    }

    private void OnRetrieve(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex == _cacheIndex && _cacheItem is not null)
        {
            e.Item = _cacheItem;
            return;
        }
        var row = _rows[e.ItemIndex];
        var item = new ListViewItem(_columns.Count > 0 ? _columns[0].Text(row) : "") { Tag = row };
        for (int i = 1; i < _columns.Count; i++)
            item.SubItems.Add(_columns[i].Text(row));
        if (RowColor?.Invoke(row) is { } color)
            item.ForeColor = color;
        if (CheckBoxes)
        {
            // Set the state image directly: on a fresh item "Checked = false" is a no-op that
            // leaves no checkbox at all. -1 hides the box for rows that can't be ticked.
            bool canCheck = CanCheck?.Invoke(row) ?? true;
            item.StateImageIndex = !canCheck ? -1 : (IsChecked?.Invoke(row) ?? false) ? 1 : 0;
        }
        _cacheItem = item;
        _cacheIndex = e.ItemIndex;
        e.Item = item;
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count || e.ColumnIndex >= _columns.Count ||
            _columns[e.ColumnIndex].Draw is not { } draw)
        {
            e.DrawDefault = true;
            return;
        }
        draw(e, _rows[e.ItemIndex]);
    }

    private void OnMouseDownCheck(object? sender, MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit.Item is null || hit.Location != ListViewHitTestLocations.StateImage)
            return;
        Toggle([hit.Item.Index]);
    }

    private void OnKeyDownCheck(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space || SelectedIndices.Count == 0)
            return;
        Toggle(SelectedIndices.Cast<int>().ToList());
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void Toggle(IReadOnlyList<int> indices)
    {
        if (SetChecked is null || IsChecked is null)
            return;
        var rows = indices.Where(i => i < _rows.Count).Select(i => _rows[i]).Where(r => CanCheck?.Invoke(r) ?? true).ToList();
        if (rows.Count == 0)
            return;
        bool value = !rows.All(IsChecked); // mixed selection → tick all
        foreach (var r in rows)
            SetChecked(r, value);
        RefreshRows();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnItemActivate(EventArgs e)
    {
        base.OnItemActivate(e);
        if (SelectedRow is { } row)
            RowActivated?.Invoke(this, row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SmallImageList?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Draws a proportional bar (value 0..1) inside a cell, with text to its right.</summary>
    public static void DrawBarCell(DrawListViewSubItemEventArgs e, double fraction, string text, Color barColor)
    {
        var g = e.Graphics!;
        bool selected = e.Item!.Selected;
        var bounds = e.Bounds;
        // Match the Explorer theme's own selection fill so drawn cells blend in.
        var list = (ListView)e.Item.ListView!;
        Color fill = !selected ? Theme.Surface : list.Focused ? Theme.Hex("#CCE8FF") : Theme.Hex("#D9D9D9");
        using (var back = new SolidBrush(fill))
            g.FillRectangle(back, bounds);

        int barW = Math.Max(Theme.S(40), bounds.Width - Theme.S(64));
        var track = new Rectangle(bounds.X + Theme.S(6), bounds.Y + (bounds.Height - Theme.S(8)) / 2, barW - Theme.S(12), Theme.S(8));
        using (var t = new SolidBrush(Theme.Hover))
            g.FillRectangle(t, track);
        int fillW = (int)Math.Round(track.Width * Math.Clamp(fraction, 0, 1));
        if (fillW > 0)
        {
            using var b = new SolidBrush(barColor);
            g.FillRectangle(b, track.X, track.Y, fillW, track.Height);
        }
        TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(bounds.X + barW, bounds.Y, bounds.Width - barW, bounds.Height),
            Theme.TextMuted, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}
