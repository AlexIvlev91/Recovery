using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Recovery.Core.DiskUsage;

namespace Recovery.Gui;

/// <summary>Colors, fonts, icon glyphs and DPI scaling shared by every screen.</summary>
internal static class Theme
{
    // ---- palette -----------------------------------------------------------
    public static readonly Color Accent = Hex("#2563EB");
    public static readonly Color AccentHover = Hex("#1D4ED8");
    public static readonly Color AccentPressed = Hex("#1E40AF");
    public static readonly Color AccentSoft = Hex("#EFF4FF");
    public static readonly Color AccentSoftBorder = Hex("#BFD3FE");

    public static readonly Color Window = Hex("#F3F4F6");
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Hex("#F9FAFB");
    public static readonly Color Hover = Hex("#F3F4F6");
    public static readonly Color Border = Hex("#E5E7EB");
    public static readonly Color BorderStrong = Hex("#D1D5DB");

    public static readonly Color Text = Hex("#111827");
    public static readonly Color TextMuted = Hex("#6B7280");
    public static readonly Color TextSubtle = Hex("#9CA3AF");

    public static readonly Color Success = Hex("#15803D");
    public static readonly Color SuccessSoft = Hex("#DCFCE7");
    public static readonly Color Warning = Hex("#B45309");
    public static readonly Color WarningSoft = Hex("#FEF3C7");
    public static readonly Color WarningBorder = Hex("#FCD34D");
    public static readonly Color Danger = Hex("#B91C1C");
    public static readonly Color DangerHover = Hex("#991B1B");
    public static readonly Color DangerSoft = Hex("#FEE2E2");

    public static readonly Color Sidebar = Hex("#0F172A");
    public static readonly Color SidebarHover = Hex("#1E293B");
    public static readonly Color SidebarSelected = Hex("#1E293B");
    public static readonly Color SidebarText = Hex("#CBD5E1");
    public static readonly Color SidebarMuted = Hex("#64748B");

    public static readonly Color ThumbBack = Hex("#E5E7EB");

    /// <summary>
    /// Categorical colors for file types, in fixed order (never cycled) so a
    /// type keeps its color everywhere; "Other" is neutral gray. Order follows
    /// a CVD-validated categorical sequence for adjacent segments.
    /// </summary>
    public static Color CategoryColor(FileCategory c) => c switch
    {
        FileCategory.Video => Hex("#2A78D6"),
        FileCategory.Image => Hex("#EB6834"),
        FileCategory.Audio => Hex("#1BAF7A"),
        FileCategory.Document => Hex("#EDA100"),
        FileCategory.Archive => Hex("#E87BA4"),
        FileCategory.Program => Hex("#4A3AA7"),
        _ => Hex("#A3A3A3"),
    };

    // ---- type --------------------------------------------------------------
    public const string FontName = "Segoe UI";
    public static readonly Font Body = new(FontName, 9f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 9f);
    public static readonly Font Small = new(FontName, 8.25f);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8.25f);
    public static readonly Font Label = new("Segoe UI Semibold", 8f);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font Subtitle = new(FontName, 9.75f);
    public static readonly Font Section = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Big = new("Segoe UI Semibold", 20f);
    public static readonly Font Nav = new(FontName, 10f);
    public static readonly Font NavBold = new("Segoe UI Semibold", 10f);

    public static readonly string IconFontName = PickIconFont();
    private static readonly Dictionary<float, Font> IconFonts = [];

    public static Font Icon(float size)
    {
        if (!IconFonts.TryGetValue(size, out var f))
            IconFonts[size] = f = new Font(IconFontName, size, GraphicsUnit.Point);
        return f;
    }

    private static string PickIconFont()
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    // ---- DPI ---------------------------------------------------------------
    public static float DpiScale { get; private set; } = 1f;

    public static void InitDpi()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        DpiScale = Math.Max(1f, g.DpiX / 96f);
    }

    /// <summary>Scale a 96-DPI pixel measurement to the current DPI.</summary>
    public static int S(int px) => (int)Math.Round(px * DpiScale);

    public static Padding S(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));

    public static Size S(int w, int h) => new(S(w), S(h));

    // ---- drawing helpers ---------------------------------------------------
    public static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle bounds)
    {
        TextRenderer.DrawText(g, glyph, Icon(size), bounds, color,
            TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

/// <summary>Segoe Fluent Icons / MDL2 code points.</summary>
internal static class Glyph
{
    public const string RecycleBin = "";
    public const string Restore = "";
    public const string Search = "";
    public const string DiskUsage = "";
    public const string Drive = "";
    public const string Repair = "";
    public const string Shield = "";
    public const string Admin = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string Photo = "";
    public const string Video = "";
    public const string Audio = "";
    public const string Document = "";
    public const string Archive = "";
    public const string App = "";
    public const string Page = "";
    public const string Refresh = "";
    public const string Filter = "";
    public const string List = "";
    public const string Grid = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string Up = "";
    public const string Check = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Copy = "";
    public const string Open = "";
    public const string Download = "";
    public const string Health = "";
    public const string Sliders = "";
    public const string ChevronRight = "";
    public const string SelectAll = "";
    public const string ClearSelection = "";
    public const string Delete = "";
    public const string Video2 = "";

    public static string ForCategory(FileCategory c) => c switch
    {
        FileCategory.Video => Video,
        FileCategory.Image => Photo,
        FileCategory.Audio => Audio,
        FileCategory.Document => Document,
        FileCategory.Archive => Archive,
        FileCategory.Program => App,
        _ => Page,
    };
}
