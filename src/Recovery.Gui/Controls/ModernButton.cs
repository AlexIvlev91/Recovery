using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Recovery.Gui.Controls;

internal enum ButtonKind
{
    /// <summary>Filled accent — the one main action of a screen.</summary>
    Primary,
    /// <summary>White with a border — ordinary actions.</summary>
    Secondary,
    /// <summary>No chrome until hovered — toolbar/icon actions.</summary>
    Ghost,
    /// <summary>Filled red — destructive actions.</summary>
    Danger,
}

/// <summary>Flat rounded button with an optional icon glyph.</summary>
internal sealed class ModernButton : Button
{
    private bool _hover, _pressed;
    private ButtonKind _kind;
    private string? _glyph;

    public ModernButton(string text = "", ButtonKind kind = ButtonKind.Secondary, string? glyph = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = kind == ButtonKind.Primary || kind == ButtonKind.Danger ? Theme.BodyBold : Theme.Body;
        Text = text;
        _kind = kind;
        _glyph = glyph;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Theme.S(0, 0, 8, 0);
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph
    {
        get => _glyph;
        set { _glyph = value; PerformLayout(); Invalidate(); }
    }

    /// <summary>When set, the button is drawn as a square icon-only button and Text becomes its tooltip.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IconOnly { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = Theme.S(32);
        if (IconOnly)
            return new Size(h, h);
        int w = Theme.S(14) * 2;
        if (!string.IsNullOrEmpty(Text))
            w += TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        if (_glyph is not null)
            w += Theme.S(16) + (string.IsNullOrEmpty(Text) ? 0 : Theme.S(8));
        return new Size(w, h);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Parent?.PerformLayout();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(EffectiveParentBack());
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var (fill, border, fore) = Colors();
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundedRect(rect, Theme.S(6)))
        {
            if (fill != Color.Transparent)
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }
            if (border != Color.Transparent)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Accent, Theme.S(2)) { Alignment = PenAlignment.Inset };
            using var path = Theme.RoundedRect(new RectangleF(1, 1, Width - 3, Height - 3), Theme.S(6));
            g.DrawPath(pen, path);
        }

        int glyphW = _glyph is null ? 0 : Theme.S(16);
        int gap = _glyph is not null && !string.IsNullOrEmpty(Text) && !IconOnly ? Theme.S(8) : 0;
        int textW = IconOnly || string.IsNullOrEmpty(Text)
            ? 0
            : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        int x = (Width - (glyphW + gap + textW)) / 2;

        if (_glyph is not null)
        {
            Theme.DrawGlyph(g, _glyph, 10.5f, fore, new Rectangle(x, 0, glyphW, Height));
            x += glyphW + gap;
        }
        if (textW > 0)
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textW + 2, Height), fore,
                TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private (Color Fill, Color Border, Color Fore) Colors()
    {
        if (!Enabled)
        {
            return _kind switch
            {
                ButtonKind.Ghost => (Color.Transparent, Color.Transparent, Theme.TextSubtle),
                _ => (Theme.Hover, Theme.Border, Theme.TextSubtle),
            };
        }
        return _kind switch
        {
            ButtonKind.Primary => (_pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent, Color.Transparent, Color.White),
            ButtonKind.Danger => (_pressed ? Theme.DangerHover : _hover ? Theme.DangerHover : Theme.Danger, Color.Transparent, Color.White),
            ButtonKind.Ghost => (_pressed ? Theme.Border : _hover ? Theme.Hover : Color.Transparent, Color.Transparent, Theme.Text),
            _ => (_pressed ? Theme.Border : _hover ? Theme.Hover : Theme.Surface, Theme.BorderStrong, Theme.Text),
        };
    }

    private Color EffectiveParentBack()
    {
        for (Control? c = Parent; c is not null; c = c.Parent)
        {
            if (c.BackColor.A == 255)
                return c.BackColor;
        }
        return Theme.Window;
    }
}
