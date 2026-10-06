using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace Recovery.Gui.Controls;

/// <summary>White rounded panel with a hairline border — groups related content.</summary>
internal class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
        Padding = Theme.S(12, 12, 12, 12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Chip.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(8));
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(Theme.Border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// Slim accent progress bar. <see cref="Indeterminate"/> animates a sliding
/// segment for work whose length isn't known.
/// </summary>
internal sealed class ThinProgressBar : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private double _value;
    private bool _indeterminate;
    private int _phase;

    public ThinProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.S(6);
        _timer.Tick += (_, _) => { _phase = (_phase + 2) % 200; Invalidate(); };
    }

    /// <summary>0..1</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            _indeterminate = value;
            _timer.Enabled = value && Visible;
            Invalidate();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        _timer.Enabled = _indeterminate && Visible;
        base.OnVisibleChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Chip.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = Height / 2f;
        using (var track = Theme.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), r))
        using (var b = new SolidBrush(Theme.Border))
            g.FillPath(b, track);

        RectangleF fill;
        if (_indeterminate)
        {
            float seg = Width * 0.3f;
            float x = (float)(_phase / 200.0 * (Width + seg)) - seg;
            fill = RectangleF.FromLTRB(Math.Max(0, x), 0, Math.Min(Width - 1, x + seg), Height - 1);
        }
        else
        {
            fill = new RectangleF(0, 0, (float)((Width - 1) * _value), Height - 1);
        }
        if (fill.Width < 1)
            return;
        using var path = Theme.RoundedRect(fill, r);
        using var brush = new SolidBrush(Theme.Accent);
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}

internal enum InfoKind { Info, Warning, Success, Error }

/// <summary>Inline message strip with an icon, text and an optional action button.</summary>
internal sealed class InfoBar : Panel
{
    private readonly Label _text;
    private readonly FlowLayoutPanel _actions;
    private InfoKind _kind;

    public InfoBar(InfoKind kind, string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Padding = Theme.S(40, 8, 10, 8);
        Height = Theme.S(46);
        Margin = Theme.S(0, 0, 0, 12);

        _text = new Label
        {
            UseMnemonic = false,
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Body,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        _actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = Theme.S(0, 0, 0, 0),
        };
        Controls.Add(_text);
        Controls.Add(_actions);
        Kind = kind;
        Text = text;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InfoKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            BackColor = value switch
            {
                InfoKind.Warning => Theme.WarningSoft,
                InfoKind.Success => Theme.SuccessSoft,
                InfoKind.Error => Theme.DangerSoft,
                _ => Theme.AccentSoft,
            };
            _text.ForeColor = Theme.Text;
            Invalidate();
        }
    }

    [AllowNull]
    public override string Text
    {
        get => _text?.Text ?? "";
        set { if (_text is not null) _text.Text = value; }
    }

    public ModernButton AddAction(string text, EventHandler onClick, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new ModernButton(text, kind) { Margin = Theme.S(6, 0, 0, 0) };
        b.Click += onClick;
        _actions.Controls.Add(b);
        return b;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Chip.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color border = _kind switch
        {
            InfoKind.Warning => Theme.WarningBorder,
            InfoKind.Success => Theme.Blend(Theme.Success, Color.White, 0.6),
            InfoKind.Error => Theme.Blend(Theme.Danger, Color.White, 0.6),
            _ => Theme.AccentSoftBorder,
        };
        Color icon = _kind switch
        {
            InfoKind.Warning => Theme.Warning,
            InfoKind.Success => Theme.Success,
            InfoKind.Error => Theme.Danger,
            _ => Theme.Accent,
        };
        using (var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(8)))
        using (var brush = new SolidBrush(BackColor))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        string glyph = _kind switch
        {
            InfoKind.Warning => Glyph.Warning,
            InfoKind.Success => Glyph.Check,
            InfoKind.Error => Glyph.Warning,
            _ => Glyph.Info,
        };
        Theme.DrawGlyph(g, glyph, 12f, icon, new Rectangle(Theme.S(10), 0, Theme.S(22), Height));
    }
}

/// <summary>Centered icon + title + message (+ optional button) shown when a list is empty.</summary>
internal sealed class EmptyState : Panel
{
    private readonly Label _glyph, _title, _message;
    private readonly FlowLayoutPanel _actions;
    private readonly TableLayoutPanel _stack;

    public EmptyState()
    {
        BackColor = Theme.Surface;
        _glyph = new Label { UseMnemonic = false, AutoSize = true, Font = Theme.Icon(28f), ForeColor = Theme.TextSubtle, Anchor = AnchorStyles.None, Margin = Theme.S(0, 0, 0, 10) };
        _title = new Label { UseMnemonic = false, AutoSize = true, Font = Theme.Section, ForeColor = Theme.Text, Anchor = AnchorStyles.None, Margin = Theme.S(0, 0, 0, 4) };
        _message = new Label
        {
            UseMnemonic = false,
            AutoSize = true, Font = Theme.Body, ForeColor = Theme.TextMuted, Anchor = AnchorStyles.None,
            MaximumSize = new Size(Theme.S(460), 0), TextAlign = ContentAlignment.MiddleCenter, Margin = Theme.S(0, 0, 0, 14),
        };
        _actions = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        _stack = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
        _stack.Controls.Add(_glyph);
        _stack.Controls.Add(_title);
        _stack.Controls.Add(_message);
        _stack.Controls.Add(_actions);
        Controls.Add(_stack);
        Resize += (_, _) => Center();
        _stack.SizeChanged += (_, _) => Center();
    }

    public void Show(string glyph, string title, string message, params (string Text, ButtonKind Kind, Action OnClick)[] actions)
    {
        _glyph.Text = glyph;
        _title.Text = title;
        _message.Text = message;
        _actions.Controls.ClearAndDispose();
        foreach (var (text, kind, onClick) in actions)
        {
            var b = new ModernButton(text, kind) { Margin = Theme.S(4, 0, 4, 0) };
            b.Click += (_, _) => onClick();
            _actions.Controls.Add(b);
        }
        _actions.Visible = actions.Length > 0;
        Visible = true;
        if (Covers is not null)
            Covers.Visible = false;
        BringToFront();
        Center();
    }

    /// <summary>The list this message stands in for — hidden while the message shows.</summary>
    public Control? Covers { get; set; }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Covers is not null)
            Covers.Visible = !Visible;
    }

    private void Center()
    {
        _stack.Location = new Point(Math.Max(0, (Width - _stack.Width) / 2), Math.Max(0, (Height - _stack.Height) / 2 - Theme.S(20)));
    }
}

/// <summary>Sidebar navigation entry: icon, label, selection bar and a "working" dot.</summary>
internal sealed class NavItem : Control
{
    private bool _hover, _selected, _busy;

    public NavItem(string text, string glyph)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text;
        GlyphText = glyph;
        Height = Theme.S(40);
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = Theme.Nav;
    }

    public string GlyphText { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    /// <summary>Shows a pulsing dot — the page is running a scan in the background.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        set { _busy = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
            OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(Theme.S(10), Theme.S(2), Width - Theme.S(20), Height - Theme.S(4));
        if (_selected || _hover)
        {
            using var path = Theme.RoundedRect(r, Theme.S(6));
            using var b = new SolidBrush(_selected ? Theme.SidebarSelected : Theme.SidebarHover);
            g.FillPath(b, path);
        }
        if (_selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            using var bar = Theme.RoundedRect(new RectangleF(r.X, r.Y + Theme.S(9), Theme.S(3), r.Height - Theme.S(18)), Theme.S(2));
            g.FillPath(accent, bar);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Accent, Theme.S(1));
            using var path = Theme.RoundedRect(r, Theme.S(6));
            g.DrawPath(pen, path);
        }

        Color fore = _selected ? Color.White : Theme.SidebarText;
        Theme.DrawGlyph(g, GlyphText, 12f, _selected ? Theme.Blend(Theme.Accent, Color.White, 0.35) : fore,
            new Rectangle(r.X + Theme.S(10), r.Y, Theme.S(22), r.Height));
        TextRenderer.DrawText(g, Text, _selected ? Theme.NavBold : Font,
            new Rectangle(r.X + Theme.S(42), r.Y, r.Width - Theme.S(60), r.Height), fore,
            TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (_busy)
        {
            int d = Theme.S(8);
            using var dot = new SolidBrush(Theme.Hex("#60A5FA"));
            g.FillEllipse(dot, r.Right - Theme.S(18), r.Y + (r.Height - d) / 2f, d, d);
        }
    }
}

/// <summary>
/// Horizontal stacked bar of segments with a 2px surface gap between them;
/// hovering a segment shows its label/value in a tooltip.
/// </summary>
internal sealed class UsageBar : Control
{
    private readonly ToolTip _tip = new() { InitialDelay = 150, ReshowDelay = 50 };
    private IReadOnlyList<(string Label, long Value, Color Color)> _segments = [];
    private long _total;
    private int _hover = -1;

    public UsageBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.S(14);
    }

    /// <summary>Segments in display order; <paramref name="total"/> ≥ their sum (the rest is drawn as track).</summary>
    public void SetSegments(IReadOnlyList<(string Label, long Value, Color Color)> segments, long total)
    {
        _segments = segments;
        _total = Math.Max(total, segments.Sum(s => s.Value));
        Invalidate();
    }

    private List<(RectangleF Rect, int Index)> SegmentRects()
    {
        var list = new List<(RectangleF, int)>();
        if (_total <= 0)
            return list;
        float x = 0, w = Width;
        for (int i = 0; i < _segments.Count; i++)
        {
            float sw = (float)(_segments[i].Value / (double)_total * w);
            if (sw <= 0)
                continue;
            list.Add((new RectangleF(x, 0, sw, Height), i));
            x += sw;
        }
        return list;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = -1;
        foreach (var (rect, index) in SegmentRects())
        {
            if (e.X >= rect.Left && e.X < rect.Right)
                hit = index;
        }
        if (hit != _hover)
        {
            _hover = hit;
            if (hit >= 0)
            {
                var s = _segments[hit];
                double pct = _total == 0 ? 0 : s.Value * 100.0 / _total;
                _tip.Show($"{s.Label}: {Recovery.Core.ByteSize.Format(s.Value)} ({pct:0.#}%)", this, e.X + Theme.S(12), Height + Theme.S(6));
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
        _hover = -1;
        _tip.Hide(this);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Chip.ParentBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = Math.Min(Theme.S(4), Height / 2f);

        // Track, then clip everything to the rounded outline so the ends are rounded.
        using var outline = Theme.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), r);
        using (var track = new SolidBrush(Theme.Border))
            g.FillPath(track, outline);
        g.SetClip(outline);

        float gap = Theme.S(2);
        foreach (var (rect, index) in SegmentRects())
        {
            var s = _segments[index];
            var fillRect = new RectangleF(rect.X, rect.Y, Math.Max(1, rect.Width - gap), rect.Height);
            Color c = _hover >= 0 && _hover != index ? Theme.Blend(s.Color, Color.White, 0.45) : s.Color;
            using var brush = new SolidBrush(c);
            g.FillRectangle(brush, fillRect);
            // surface gap between adjacent segments
            using var gapBrush = new SolidBrush(back);
            g.FillRectangle(gapBrush, fillRect.Right, 0, gap, Height);
        }
        g.ResetClip();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Small factory helpers so page code stays declarative.</summary>
internal static class Ui
{
    private static readonly ToolTip SharedTip = new() { AutoPopDelay = 15000 };

    public static T Tip<T>(this T control, string text) where T : Control
    {
        SharedTip.SetToolTip(control, text);
        return control;
    }

    public static Label Text(string text, Font? font = null, Color? color = null, Padding? margin = null) => new()
    {
        UseMnemonic = false,
        Text = text,
        AutoSize = true,
        Font = font ?? Theme.Body,
        ForeColor = color ?? Theme.Text,
        BackColor = Color.Transparent,
        Margin = margin ?? Theme.S(0, 0, 0, 0),
    };

    /// <summary>Label vertically centered in a 32px toolbar row.</summary>
    public static Label FieldLabel(string text) => new()
    {
        UseMnemonic = false,
        Text = text,
        AutoSize = true,
        Font = Theme.Body,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        Margin = Theme.S(0, 8, 6, 0),
    };

    /// <summary>Uppercase caption above a group of settings.</summary>
    public static Label Caption(string text) => new()
    {
        UseMnemonic = false,
        Text = text.ToUpperInvariant(),
        AutoSize = true,
        Font = Theme.Label,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        Margin = Theme.S(0, 12, 0, 6),
    };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        row.Controls.AddRange(controls);
        return row;
    }

    public static ComboBox Combo(int width, params string[] items)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Theme.S(width),
            Font = Theme.Body,
            Margin = Theme.S(0, 4, 8, 0),
            FlatStyle = FlatStyle.System,
        };
        c.Items.AddRange(items);
        if (items.Length > 0)
            c.SelectedIndex = 0;
        return c;
    }

    public static TextBox SearchBox(string placeholder, int width) => new()
    {
        PlaceholderText = placeholder,
        Width = Theme.S(width),
        Font = Theme.Body,
        Margin = Theme.S(0, 5, 8, 0),
    };

    public static CheckBox Check(string text, bool isChecked = false) => new()
    {
        Text = text,
        Checked = isChecked,
        AutoSize = true,
        Font = Theme.Body,
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        Margin = Theme.S(0, 7, 12, 0),
    };

    /// <summary>Remove and dispose every child — <c>Controls.Clear()</c> alone leaks their window handles.</summary>
    public static void ClearAndDispose(this Control.ControlCollection controls)
    {
        for (int i = controls.Count - 1; i >= 0; i--)
        {
            var c = controls[i];
            controls.RemoveAt(i);
            c.Dispose();
        }
    }

    /// <summary>Thin horizontal rule.</summary>
    public static Control Divider() => new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Theme.Border };
}
