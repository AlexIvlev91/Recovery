using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Recovery.Gui.Controls;

/// <summary>
/// A pill-shaped toggle (multi-select filter chip). Checked chips are tinted
/// with the accent and show a check mark; an optional color dot identifies a
/// category.
/// </summary>
internal sealed class Chip : CheckBox
{
    private bool _hover;

    public Chip(string text, bool isChecked = true, Color? dot = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        Checked = isChecked;
        Dot = dot;
        Font = Theme.Body;
        AutoSize = false;
        Cursor = Cursors.Hand;
        Margin = Theme.S(0, 0, 6, 6);
        Size = GetPreferredSize(Size.Empty);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Dot { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int w = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        w += Theme.S(12) * 2 + Theme.S(18);
        if (Dot is not null)
            w += Theme.S(14);
        return new Size(w, Theme.S(28));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill = Checked ? Theme.AccentSoft : _hover ? Theme.Hover : Theme.Surface;
        Color border = Checked ? Theme.AccentSoftBorder : Theme.BorderStrong;
        Color fore = !Enabled ? Theme.TextSubtle : Checked ? Theme.AccentPressed : Theme.Text;

        using (var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        int x = Theme.S(10);
        var glyphRect = new Rectangle(x, 0, Theme.S(14), Height);
        if (Checked)
            Theme.DrawGlyph(g, Glyph.Check, 8.5f, fore, glyphRect);
        else
            DrawEmptyBox(g, glyphRect);
        x += Theme.S(20);

        if (Dot is { } dot)
        {
            int d = Theme.S(8);
            using var b = new SolidBrush(Enabled ? dot : Theme.TextSubtle);
            g.FillEllipse(b, x, (Height - d) / 2f, d, d);
            x += Theme.S(14);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore,
            TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Accent, Theme.S(2)) { Alignment = PenAlignment.Inset };
            using var path = Theme.RoundedRect(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f);
            g.DrawPath(pen, path);
        }
    }

    private void DrawEmptyBox(Graphics g, Rectangle r)
    {
        int s = Theme.S(11);
        var box = new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s);
        using var path = Theme.RoundedRect(box, Theme.S(2));
        using var pen = new Pen(Enabled ? Theme.TextMuted : Theme.TextSubtle);
        g.DrawPath(pen, path);
    }

    internal static Color ParentBack(Control c)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent)
        {
            if (p.BackColor.A == 255)
                return p.BackColor;
        }
        return Theme.Window;
    }
}

/// <summary>
/// Single-choice segmented control ("All | Photos | Videos"), optionally with
/// icon-only segments. Raises <see cref="SelectedIndexChanged"/>.
/// </summary>
internal sealed class SegmentedControl : Control
{
    private readonly List<(string Text, string? Glyph, string? Tip)> _items = [];
    private readonly ToolTip _tip = new();
    private int _selected;
    private int _hover = -1;
    private int[] _widths = [];

    public SegmentedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = Theme.S(0, 0, 8, 0);
    }

    public event EventHandler? SelectedIndexChanged;

    public SegmentedControl Add(string text, string? glyph = null, string? tip = null)
    {
        _items.Add((text, glyph, tip));
        Relayout();
        return this;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected)
                return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Change a segment's label (e.g. to update a count) without re-raising selection.</summary>
    public void SetText(int index, string text)
    {
        _items[index] = (text, _items[index].Glyph, _items[index].Tip);
        Relayout();
    }

    private void Relayout()
    {
        _widths = new int[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            var (text, glyph, _) = _items[i];
            int w = Theme.S(12) * 2;
            if (!string.IsNullOrEmpty(text))
                w += TextRenderer.MeasureText(text, Theme.BodyBold, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (glyph is not null)
                w += Theme.S(16) + (string.IsNullOrEmpty(text) ? 0 : Theme.S(6));
            _widths[i] = Math.Max(w, Theme.S(36));
        }
        Size = new Size(_widths.Sum() + Theme.S(6), Theme.S(32));
        Invalidate();
    }

    private Rectangle SegmentRect(int index)
    {
        int x = Theme.S(3);
        for (int i = 0; i < index; i++)
            x += _widths[i];
        return new Rectangle(x, Theme.S(3), _widths[index], Height - Theme.S(6));
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (SegmentRect(i).Contains(p))
                return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            _tip.SetToolTip(this, h >= 0 ? _items[h].Tip ?? "" : "");
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        int h = HitTest(e.Location);
        if (h >= 0)
            SelectedIndex = h;
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _selected - 1);
        if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(_items.Count - 1, _selected + 1);
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Chip.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(7)))
        using (var back = new SolidBrush(Theme.Hover))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(back, path);
            g.DrawPath(pen, path);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            var r = SegmentRect(i);
            bool sel = i == _selected;
            if (sel || i == _hover)
            {
                using var path = Theme.RoundedRect(r, Theme.S(5));
                using var brush = new SolidBrush(sel ? Theme.Surface : Theme.Blend(Theme.Hover, Theme.Border, 0.5));
                g.FillPath(brush, path);
                if (sel)
                {
                    using var pen = new Pen(Theme.Border);
                    g.DrawPath(pen, path);
                }
            }

            var (text, glyph, _) = _items[i];
            Color fore = !Enabled ? Theme.TextSubtle : sel ? Theme.Text : Theme.TextMuted;
            int contentW = (glyph is null ? 0 : Theme.S(16)) +
                           (glyph is not null && !string.IsNullOrEmpty(text) ? Theme.S(6) : 0) +
                           (string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(g, text, sel ? Theme.BodyBold : Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
            int x = r.X + (r.Width - contentW) / 2;
            if (glyph is not null)
            {
                Theme.DrawGlyph(g, glyph, 10f, fore, new Rectangle(x, r.Y, Theme.S(16), r.Height));
                x += Theme.S(16) + (string.IsNullOrEmpty(text) ? 0 : Theme.S(6));
            }
            if (!string.IsNullOrEmpty(text))
            {
                TextRenderer.DrawText(g, text, sel ? Theme.BodyBold : Font, new Rectangle(x, r.Y, r.Right - x, r.Height), fore,
                    TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            if (sel && Focused && ShowFocusCues)
            {
                using var pen = new Pen(Theme.Accent, Theme.S(2)) { Alignment = PenAlignment.Inset };
                using var path = Theme.RoundedRect(r, Theme.S(5));
                g.DrawPath(pen, path);
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
