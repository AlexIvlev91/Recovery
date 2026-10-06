using System.Drawing.Drawing2D;
using Recovery.Gui.Controls;
using Recovery.Gui.Pages;

namespace Recovery.Gui;

/// <summary>
/// App shell: a sidebar to switch between tools and a host for the active
/// page. Pages are created on first visit and kept alive, so a scan keeps
/// running (with a dot in the sidebar) while you look at something else.
/// </summary>
public sealed class MainForm : Form
{
    private sealed record PageEntry(string Key, string Title, string Glyph, Func<PageBase> Create)
    {
        public NavItem Nav { get; } = new(Title, Glyph);
    }

    private readonly Panel _host;
    private readonly List<PageEntry> _entries;
    private readonly Dictionary<string, PageBase> _pages = [];
    private PageBase? _current;
    private bool _closingForRestart;

    public MainForm(string? startPage = null)
    {
        Text = "Recovery — photo & video recovery";
        Font = Theme.Body;
        BackColor = Theme.Window;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = Theme.S(1040, 680);
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        Size = new Size(Math.Min(Theme.S(1340), work.Width - 40), Math.Min(Theme.S(860), work.Height - 40));
        Icon = MakeAppIcon();
        KeyPreview = true;
        RestoreWindowBounds();

        _entries =
        [
            new("bin", "Recycle Bin", Glyph.RecycleBin, () => new RecycleBinPage()),
            new("undelete", "Undelete by name", Glyph.Restore, () => new UndeletePage()),
            new("carve", "Deep scan", Glyph.Search, () => new DeepScanPage()),
            new("usage", "Disk usage", Glyph.DiskUsage, () => new DiskUsagePage()),
            new("repair", "Check & repair", Glyph.Repair, () => new RepairPage()),
        ];

        _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
        var sidebar = BuildSidebar();
        Controls.Add(_host);
        Controls.Add(sidebar);

        Shown += (_, _) => Navigate(_entries.Any(e => e.Key == startPage) ? startPage! : "bin");
        FormClosing += OnFormClosing;
    }

    // ---- sidebar ---------------------------------------------------------------------------

    private Panel BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.S(236), BackColor = Theme.Sidebar, Padding = Theme.S(0, 18, 0, 14) };

        var brand = new Panel { Dock = DockStyle.Top, Height = Theme.S(64), BackColor = Theme.Sidebar };
        brand.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var logo = new Rectangle(Theme.S(20), Theme.S(4), Theme.S(36), Theme.S(36));
            using (var path = Theme.RoundedRect(logo, Theme.S(9)))
            using (var b = new LinearGradientBrush(logo, Theme.Hex("#3B82F6"), Theme.Hex("#1D4ED8"), 45f))
                g.FillPath(b, path);
            Theme.DrawGlyph(g, Glyph.Restore, 13f, Color.White, logo);
            using var titleFont = new Font("Segoe UI Semibold", 12.5f);
            TextRenderer.DrawText(g, "Recovery", titleFont, new Point(Theme.S(66), Theme.S(2)), Color.White, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "Photo & video rescue", Theme.Small, new Point(Theme.S(67), Theme.S(25)), Theme.SidebarMuted, TextFormatFlags.NoPrefix);
        };

        var nav = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar };
        // Docked Top in reverse order: last added sits at the top.
        var items = new List<Control>
        {
            SectionCaption("Recover"),
            _entries[0].Nav, _entries[1].Nav, _entries[2].Nav,
            SectionCaption("Analyze & fix"),
            _entries[3].Nav, _entries[4].Nav,
        };
        for (int i = items.Count - 1; i >= 0; i--)
            nav.Controls.Add(items[i]);
        foreach (var e in _entries)
            e.Nav.Click += (_, _) => Navigate(e.Key);

        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(BuildAdminStatus());
        sidebar.Controls.Add(brand);
        return sidebar;
    }

    private static Label SectionCaption(string text) => new()
    {
        UseMnemonic = false,
        Text = text.ToUpperInvariant(),
        Dock = DockStyle.Top,
        Height = Theme.S(34),
        Font = Theme.Label,
        ForeColor = Theme.SidebarMuted,
        TextAlign = ContentAlignment.BottomLeft,
        Padding = Theme.S(22, 0, 0, 6),
        BackColor = Theme.Sidebar,
    };

    private Control BuildAdminStatus()
    {
        bool admin = Elevation.IsAdministrator;
        var panel = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(admin ? 52 : 92), BackColor = Theme.Sidebar, Padding = Theme.S(16, 6, 16, 0) };
        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using (var pen = new Pen(Theme.SidebarHover))
                g.DrawLine(pen, Theme.S(16), 0, panel.Width - Theme.S(16), 0);
            Color c = admin ? Theme.Hex("#4ADE80") : Theme.Hex("#FBBF24");
            Theme.DrawGlyph(g, Glyph.Shield, 11f, c, new Rectangle(Theme.S(18), Theme.S(12), Theme.S(20), Theme.S(24)));
            TextRenderer.DrawText(g, admin ? "Administrator" : "Standard mode", Theme.BodyBold, new Point(Theme.S(44), Theme.S(10)), Color.White, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, admin ? "Full disk access" : "Bin & disk usage only", Theme.Small, new Point(Theme.S(44), Theme.S(28)), Theme.SidebarMuted, TextFormatFlags.NoPrefix);
        };
        if (!admin)
        {
            var restart = new ModernButton("Restart as admin", ButtonKind.Primary, Glyph.Shield)
            {
                AutoSize = false,
                Size = new Size(Theme.S(204), Theme.S(30)),
                Location = new Point(Theme.S(16), Theme.S(52)),
            };
            restart.Tip("Undelete and Deep scan read the disk directly, which needs administrator rights.");
            restart.Click += (_, _) => RestartElevated(_current?.Key ?? "bin");
            panel.Controls.Add(restart);
        }
        return panel;
    }

    // ---- navigation ----------------------------------------------------------------------------

    private void Navigate(string key)
    {
        var entry = _entries.First(e => e.Key == key);
        if (!_pages.TryGetValue(key, out var page))
        {
            page = entry.Create();
            page.BusyChanged += (_, _) => entry.Nav.Busy = page.Busy;
            page.NavigateRequested += (_, target) => Navigate(target);
            page.RestartElevatedRequested += (_, target) => RestartElevated(target);
            page.Visible = false;
            _host.Controls.Add(page);
            _pages[key] = page;
        }

        if (_current != page)
        {
            _host.SuspendLayout();
            page.Visible = true;
            page.BringToFront();
            if (_current is not null)
                _current.Visible = false;
            _host.ResumeLayout();
            _current = page;
        }
        foreach (var e in _entries)
            e.Nav.Selected = e.Key == key;
        Text = $"{entry.Title} — Recovery";
        // Keep focus in the sidebar so a page's search box doesn't grab it (and hide its placeholder).
        ActiveControl = entry.Nav;
        page.Activate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+1 … Ctrl+5 switch tools.
        if ((keyData & Keys.Control) != 0)
        {
            int n = (keyData & Keys.KeyCode) - Keys.D1;
            if (n >= 0 && n < _entries.Count && (keyData & (Keys.Alt | Keys.Shift)) == 0)
            {
                Navigate(_entries[n].Key);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RestartElevated(string page)
    {
        // Ask first, launch second, clean up last: if UAC is declined nothing was stopped or discarded.
        if (!_pages.Values.All(p => p.ConfirmAppClose()))
            return;
        if (!Elevation.TryRestartElevated(page))
            return;
        _closingForRestart = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_closingForRestart && !_pages.Values.All(p => p.ConfirmAppClose()))
        {
            e.Cancel = true;
            return;
        }
        foreach (var page in _pages.Values)
            page.OnAppClosing();
        SaveWindowBounds();
    }

    // ---- window state ----------------------------------------------------------------------------

    private void RestoreWindowBounds()
    {
        var parts = AppSettings.Get("window")?.Split(',');
        if (parts is not { Length: 5 } || !parts.Take(4).All(p => int.TryParse(p, out _)))
            return;
        var r = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
        if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
            return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (parts[4] == "max")
            WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        AppSettings.Set("window", $"{r.X},{r.Y},{r.Width},{r.Height},{(WindowState == FormWindowState.Maximized ? "max" : "normal")}");
    }

    private static Icon MakeAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = Theme.RoundedRect(new RectangleF(1, 1, 30, 30), 7);
            using var b = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Theme.Hex("#3B82F6"), Theme.Hex("#1D4ED8"), 45f);
            g.FillPath(b, path);
            using var font = new Font(Theme.IconFontName, 14f, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, Glyph.Restore, font, new Rectangle(0, 0, 32, 32), Color.White,
                TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
