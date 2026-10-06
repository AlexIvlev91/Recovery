using System.Drawing.Drawing2D;
using Recovery.Core;

namespace Recovery.Gui.Controls;

/// <summary>One rectangle in a <see cref="TreemapView"/>.</summary>
internal sealed record TreemapItem(string Label, long Size, Color Color, object? Tag, string? Detail = null, bool Muted = false);

/// <summary>
/// Squarified treemap (Bruls, Huizing &amp; van Wijk): each item's area is
/// proportional to its size, laid out so rectangles stay close to square and
/// are easy to compare. Tiles are tinted by color, separated by a 2px surface
/// gap, labelled when big enough, and show a tooltip on hover.
/// </summary>
internal sealed class TreemapView : Control
{
    private readonly ToolTip _tip = new() { InitialDelay = 200, ReshowDelay = 50, AutoPopDelay = 20000 };
    private IReadOnlyList<TreemapItem> _items = [];
    private List<(RectangleF Rect, TreemapItem Item)> _layout = [];
    private TreemapItem? _hover;
    private long _total;

    public TreemapView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Font = Theme.Small;
    }

    /// <summary>Raised when a tile is clicked (left = open/drill down, right = context menu).</summary>
    public event EventHandler<(TreemapItem Item, MouseEventArgs Mouse)>? ItemClicked;

    public string EmptyText { get; set; } = "Nothing to show.";

    public void SetItems(IEnumerable<TreemapItem> items)
    {
        _items = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        _total = _items.Sum(i => i.Size);
        _hover = null;
        Relayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    private void Relayout()
    {
        _layout = [];
        var area = new RectangleF(0, 0, Width, Height);
        if (_total > 0 && area.Width > 4 && area.Height > 4)
        {
            // Very small items become slivers; fold everything past 300 or under
            // 0.05% of the total into one "smaller items" tile.
            var shown = new List<TreemapItem>();
            long rest = 0;
            int restCount = 0;
            foreach (var it in _items)
            {
                if (shown.Count < 300 && it.Size >= _total * 0.0005)
                    shown.Add(it);
                else
                {
                    rest += it.Size;
                    restCount++;
                }
            }
            if (rest > 0)
                shown.Add(new TreemapItem($"{restCount:N0} smaller items", rest, Theme.TextSubtle, null, Muted: true));

            double scale = area.Width * area.Height / _total;
            Squarify(shown.Select(i => (i, i.Size * scale)).ToList(), area, _layout);
        }
        Invalidate();
    }

    private static void Squarify(List<(TreemapItem Item, double Area)> items, RectangleF rect, List<(RectangleF, TreemapItem)> output)
    {
        int start = 0;
        while (start < items.Count)
        {
            float shortSide = Math.Min(rect.Width, rect.Height);
            if (shortSide <= 0)
                return;

            int end = start;
            double rowArea = 0;
            double worst = double.MaxValue;
            while (end < items.Count)
            {
                double candidateArea = rowArea + items[end].Area;
                double w = Worst(items, start, end + 1, candidateArea, shortSide);
                if (w > worst)
                    break;
                worst = w;
                rowArea = candidateArea;
                end++;
            }

            // Lay the row along the short side.
            bool horizontal = rect.Width >= rect.Height; // row is a vertical strip on the left
            float thickness = (float)(rowArea / shortSide);
            float offset = 0;
            for (int i = start; i < end; i++)
            {
                float len = (float)(items[i].Area / thickness);
                var r = horizontal
                    ? new RectangleF(rect.X, rect.Y + offset, thickness, len)
                    : new RectangleF(rect.X + offset, rect.Y, len, thickness);
                output.Add((r, items[i].Item));
                offset += len;
            }
            rect = horizontal
                ? new RectangleF(rect.X + thickness, rect.Y, rect.Width - thickness, rect.Height)
                : new RectangleF(rect.X, rect.Y + thickness, rect.Width, rect.Height - thickness);
            start = end;
        }
    }

    // Worst aspect ratio of a row laid along a side of length w.
    private static double Worst(List<(TreemapItem Item, double Area)> items, int from, int to, double sum, double w)
    {
        double max = 0, min = double.MaxValue;
        for (int i = from; i < to; i++)
        {
            max = Math.Max(max, items[i].Area);
            min = Math.Min(min, items[i].Area);
        }
        double s2 = sum * sum, w2 = w * w;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private TreemapItem? HitTest(Point p)
    {
        foreach (var (rect, item) in _layout)
        {
            if (rect.Contains(p))
                return item;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Cursor = hit?.Tag is not null ? Cursors.Hand : Cursors.Default;
            if (hit is not null)
            {
                double pct = _total == 0 ? 0 : hit.Size * 100.0 / _total;
                string text = $"{hit.Label}\n{ByteSize.Format(hit.Size)} · {pct:0.#}%" + (hit.Detail is null ? "" : $"\n{hit.Detail}");
                _tip.Show(text, this, e.X + Theme.S(14), e.Y + Theme.S(18));
            }
            else
            {
                _tip.Hide(this);
            }
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = null;
        _tip.Hide(this);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (HitTest(e.Location) is { } hit)
            ItemClicked?.Invoke(this, (hit, e));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_layout.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Theme.Body, ClientRectangle, Theme.TextMuted,
                TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float gap = Theme.S(2);
        foreach (var (rect, item) in _layout)
        {
            var r = RectangleF.FromLTRB(rect.Left + gap / 2, rect.Top + gap / 2, rect.Right - gap / 2, rect.Bottom - gap / 2);
            if (r.Width < 1 || r.Height < 1)
                continue;
            bool hover = ReferenceEquals(item, _hover);
            // Tinted fill keeps dark labels readable on every category color;
            // a solid strip on top carries the identity color.
            double tint = item.Muted ? 0.82 : hover ? 0.45 : 0.62;
            using (var path = Theme.RoundedRect(r, Math.Min(Theme.S(4), Math.Min(r.Width, r.Height) / 2)))
            {
                using var fill = new SolidBrush(Theme.Blend(item.Color, Color.White, tint));
                g.FillPath(fill, path);
                if (r.Height > Theme.S(10) && !item.Muted)
                {
                    var save = g.Clip;
                    g.SetClip(path);
                    using var strip = new SolidBrush(item.Color);
                    g.FillRectangle(strip, r.X, r.Y, r.Width, Math.Min(Theme.S(4), r.Height / 4));
                    g.Clip = save;
                }
                if (hover)
                {
                    using var pen = new Pen(Theme.Text, Theme.S(2)) { Alignment = PenAlignment.Inset };
                    g.DrawPath(pen, path);
                }
            }

            if (r.Width >= Theme.S(56) && r.Height >= Theme.S(30))
            {
                var textRect = Rectangle.Round(RectangleF.FromLTRB(r.Left + Theme.S(6), r.Top + Theme.S(7), r.Right - Theme.S(4), r.Bottom - Theme.S(2)));
                TextRenderer.DrawText(g, item.Label, Theme.SmallBold, textRect, Theme.Text,
                    TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (r.Height >= Theme.S(46))
                {
                    textRect.Y += Theme.S(16);
                    textRect.Height -= Theme.S(16);
                    TextRenderer.DrawText(g, ByteSize.Format(item.Size), Theme.Small, textRect, Theme.TextMuted,
                        TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }
}
