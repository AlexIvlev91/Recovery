using System.Drawing.Drawing2D;
using Recovery.Core;
using Recovery.Core.DiskUsage;
using Recovery.Gui.Controls;

namespace Recovery.Gui.Pages;

/// <summary>
/// Disk space analyzer: scans a drive or folder, then shows where the space
/// went — a folder tree with size bars, a breakdown by file type, a treemap,
/// and filterable lists of the largest files and file types. Files and
/// folders can be opened, revealed, or moved to the Recycle Bin.
/// </summary>
internal sealed class DiskUsagePage : PageBase
{
    private const int MaxTreeChildren = 400;
    private const int TopFiles = 1000;

    private static readonly (string Label, long Bytes)[] MinSizes =
    [
        ("Any size", 0), ("≥ 1 MB", 1L << 20), ("≥ 10 MB", 10L << 20), ("≥ 100 MB", 100L << 20),
        ("≥ 500 MB", 500L << 20), ("≥ 1 GB", 1L << 30), ("≥ 5 GB", 5L << 30),
    ];

    private static readonly (string Label, int Days)[] Ages =
    [
        ("Any date", 0), ("Untouched 1 month+", 30), ("Untouched 6 months+", 182), ("Untouched 1 year+", 365),
        ("Untouched 2 years+", 730), ("Untouched 5 years+", 1826),
    ];

    // scan bar
    private readonly ComboBox _target;
    private readonly ModernButton _scanDrive, _scanFolder, _stop;
    private readonly ThinProgressBar _scanProgress;
    private readonly Label _scanStatus;

    // landing + results
    private readonly Card _landing;
    private readonly FlowLayoutPanel _driveList;
    private readonly Panel _results;
    private readonly InfoBar _incompleteBar;

    // tree
    private readonly TreeView _tree;
    private readonly ImageList _treeImages;
    private readonly Dictionary<DirNode, TreeNode> _nodes = [];
    private static readonly object LoadingTag = new();
    private bool _suppressTreeSelect;

    // summary
    private readonly FlowLayoutPanel _crumbs;
    private readonly Label _bigSize, _summaryDetail;
    private readonly UsageBar _categoryBar;
    private readonly FlowLayoutPanel _legend;
    private readonly ModernButton _up;
    private readonly Card _summaryCard;
    private readonly Panel _crumbRow;
    private readonly FlowLayoutPanel _sizeRow;
    private readonly Label _summaryNote;

    // views
    private readonly SegmentedControl _tabs;
    private readonly Panel _fileFilters;
    private readonly ComboBox _fCategory, _fMinSize, _fAge;
    private readonly TextBox _fSearch;
    private readonly Chip _extChip;
    private readonly Panel _viewHost;
    private readonly TreemapView _map;
    private readonly VirtualTable<DirNode> _folders;
    private readonly VirtualTable<FileHit> _files;
    private readonly VirtualTable<ExtensionStat> _types;
    private readonly Label _viewNote;
    private readonly System.Windows.Forms.Timer _queryDebounce = new() { Interval = 200 };

    // footer
    private readonly FooterBar _footer;
    private readonly ModernButton _open, _reveal, _delete;

    private DiskUsageResult? _result;
    private DirNode? _current;
    private object? _selection; // DirNode or FileHit chosen in the active view
    private string? _extFilter;
    private char? _scanDriveLetter;
    private long _driveTotal, _driveFree;
    private CancellationTokenSource? _scanCts, _queryCts;
    private bool _scanning;

    public DiskUsagePage()
        : base("usage", "Disk usage",
            "See which folders and files take up the most space. Filter by type, size and age to find what to clean up.",
            Glyph.DiskUsage)
    {
        // ---- scan bar -------------------------------------------------------------------
        _target = Ui.Combo(330);
        _scanDrive = new ModernButton("Scan drive", ButtonKind.Primary, Glyph.Drive);
        _scanDrive.Click += (_, _) => { if (_target.SelectedItem is DriveChoice d) _ = ScanAsync(d.Root, d.Letter); };
        _scanFolder = new ModernButton("Scan a folder…", ButtonKind.Secondary, Glyph.FolderOpen);
        _scanFolder.Click += (_, _) =>
        {
            if (PickFolder("Choose a folder to analyze", AppSettings.Get("usage.folder")) is { } folder)
            {
                AppSettings.Set("usage.folder", folder);
                _ = ScanAsync(folder, null);
            }
        };
        _stop = new ModernButton("Stop", ButtonKind.Secondary, Glyph.Stop) { Visible = false };
        _stop.Click += (_, _) => _scanCts?.Cancel();
        _scanProgress = new ThinProgressBar { Dock = DockStyle.Bottom, Visible = false };
        _scanStatus = new Label
        {
            UseMnemonic = false,
            Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextMuted, Font = Theme.Small, BackColor = Color.Transparent, Padding = Theme.S(8, 0, 0, 0),
        };
        var scanRow = Ui.Row(Ui.FieldLabel("Drive"), _target, _scanDrive, _scanFolder, _stop);
        scanRow.Dock = DockStyle.Left;
        var scanCard = new Card { Dock = DockStyle.Top, Height = Theme.S(60), Padding = Theme.S(14, 12, 14, 8) };
        scanCard.Controls.Add(_scanStatus);
        scanCard.Controls.Add(scanRow);
        scanCard.Controls.Add(_scanProgress);

        // ---- landing: drives overview --------------------------------------------------------
        _driveList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            BackColor = Color.Transparent,
        };
        _landing = new Card { Dock = DockStyle.Fill, Padding = Theme.S(20, 16, 20, 16) };
        _landing.Controls.Add(_driveList);
        _landing.Controls.Add(new Label
        {
            UseMnemonic = false,
            Text = "Your drives", Dock = DockStyle.Top, Height = Theme.S(30), Font = Theme.Section, ForeColor = Theme.Text,
        });

        // ---- tree ----------------------------------------------------------------------------
        _treeImages = BuildBarImages();
        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            ImageList = _treeImages,
            HideSelection = false,
            ShowLines = false,
            FullRowSelect = true,
            Font = Theme.Body,
            ForeColor = Theme.Text,
            ItemHeight = Theme.S(26),
        };
        Native.UseExplorerStyle(_tree);
        _tree.BeforeExpand += (_, e) => { if (e.Node is { } n) EnsurePopulated(n); };
        _tree.AfterSelect += (_, e) =>
        {
            if (!_suppressTreeSelect && e.Node?.Tag is DirNode d)
                SetCurrent(d);
        };
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.Node.Tag is DirNode d)
            {
                _tree.SelectedNode = e.Node;
                ShowContextMenu(d, _tree, e.Location);
            }
        };
        var treeCard = new Card { Dock = DockStyle.Left, Width = Theme.S(330), Padding = Theme.S(1, 8, 1, 1) };
        treeCard.Controls.Add(_tree);
        var splitter = new Splitter { Dock = DockStyle.Left, Width = Theme.S(12), BackColor = Theme.Window, MinSize = Theme.S(220) };

        // ---- summary --------------------------------------------------------------------------
        _crumbs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Padding = Theme.S(0, 4, 0, 0) };
        _up = new ModernButton("Up", ButtonKind.Ghost, Glyph.Up);
        _up.Click += (_, _) => { if (_current?.Parent is { } p) Navigate(p); };
        var openHere = new ModernButton("Open in Explorer", ButtonKind.Ghost, Glyph.FolderOpen) { Margin = Padding.Empty };
        openHere.Click += (_, _) => { if (_current is not null) ShowInExplorer(_current.FullPath); };
        var crumbActions = Ui.Row(_up, openHere);
        crumbActions.Dock = DockStyle.Right;
        _crumbRow = new Panel { Dock = DockStyle.Top, Height = Theme.S(32), BackColor = Color.Transparent };
        _crumbRow.Controls.Add(_crumbs);
        _crumbRow.Controls.Add(crumbActions);

        _bigSize = new Label { UseMnemonic = false, AutoSize = true, Font = Theme.Big, ForeColor = Theme.Text, Margin = Theme.S(0, 0, 12, 0), BackColor = Color.Transparent };
        _summaryDetail = new Label { UseMnemonic = false, AutoSize = true, Font = Theme.Body, ForeColor = Theme.TextMuted, Margin = Theme.S(0, 12, 0, 0), BackColor = Color.Transparent };
        _sizeRow = Ui.Row(_bigSize, _summaryDetail);
        _sizeRow.Dock = DockStyle.Top;
        _sizeRow.Padding = Theme.S(0, 2, 0, 4);
        _summaryNote = new Label
        {
            UseMnemonic = false, Dock = DockStyle.Top, AutoSize = false, Font = Theme.Small, ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent, Padding = Theme.S(0, 0, 0, 8),
        };

        _categoryBar = new UsageBar { Dock = DockStyle.Top, Height = Theme.S(14) };
        _legend = new FlowLayoutPanel { Dock = DockStyle.Top, BackColor = Color.Transparent, Padding = Theme.S(0, 8, 0, 0) };

        // Sized by hand (LayoutSummary): docked AutoSize panels don't grow when the legend wraps.
        _summaryCard = new Card { Dock = DockStyle.Top, Height = Theme.S(140), Padding = Theme.S(16, 10, 12, 12) };
        _summaryCard.Controls.Add(_legend);
        _summaryCard.Controls.Add(_categoryBar);
        _summaryCard.Controls.Add(_summaryNote);
        _summaryCard.Controls.Add(_sizeRow);
        _summaryCard.Controls.Add(_crumbRow);
        _summaryCard.Resize += (_, _) => LayoutSummary();
        var summary = _summaryCard;

        // ---- views -------------------------------------------------------------------------------
        _tabs = new SegmentedControl()
            .Add("Map", Glyph.Grid, "Treemap — area shows size")
            .Add("Folders", Glyph.Folder, "Subfolders by size")
            .Add("Largest files", Glyph.Page, "Biggest files anywhere below this folder")
            .Add("File types", Glyph.Filter, "Space used per file extension");
        _tabs.SelectedIndexChanged += (_, _) => RefreshView();

        _fCategory = Ui.Combo(150, ["All types", .. FileCategories.All.Select(FileCategories.Label)]);
        _fMinSize = Ui.Combo(100, MinSizes.Select(m => m.Label).ToArray());
        _fMinSize.SelectedIndex = 2; // ≥ 10 MB: the useful default for hunting space
        _fAge = Ui.Combo(150, Ages.Select(a => a.Label).ToArray());
        _fSearch = Ui.SearchBox("Name contains…", 130);
        _extChip = new Chip("", true) { Visible = false };
        _extChip.Tip("Click to remove the extension filter");
        _extChip.Click += (_, _) => { _extFilter = null; _extChip.Visible = false; ScheduleQuery(); };
        foreach (var c in new Control[] { _fCategory, _fMinSize, _fAge })
            ((ComboBox)c).SelectedIndexChanged += (_, _) => ScheduleQuery();
        _fSearch.TextChanged += (_, _) => ScheduleQuery();
        _queryDebounce.Tick += (_, _) => { _queryDebounce.Stop(); RefreshView(); };
        var filterRow = Ui.Row(Ui.FieldLabel("Show"), _fCategory, _fMinSize, _fAge, _fSearch, _extChip);
        filterRow.Dock = DockStyle.Left;
        _fileFilters = new Panel { Dock = DockStyle.Top, Height = Theme.S(42), BackColor = Color.Transparent, Padding = Theme.S(0, 8, 0, 0), Visible = false };
        _fileFilters.Controls.Add(filterRow);

        var tabRow = new Panel { Dock = DockStyle.Top, Height = Theme.S(34), BackColor = Color.Transparent };
        var tabsWrap = Ui.Row(_tabs);
        tabsWrap.Dock = DockStyle.Left;
        tabRow.Controls.Add(tabsWrap);

        _map = new TreemapView { Dock = DockStyle.Fill, EmptyText = "This folder is empty." };
        _map.ItemClicked += (_, a) => OnMapClick(a.Item, a.Mouse);

        _folders = new VirtualTable<DirNode> { Dock = DockStyle.Fill };
        _folders.AddColumn("Folder", 260, d => d.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase))
                .AddColumn("Size", 90, d => ByteSize.Format(d.Size), (a, b) => a.Size.CompareTo(b.Size), HorizontalAlignment.Right)
                .AddColumn("Share", 170, d => "", (a, b) => a.Size.CompareTo(b.Size),
                    draw: (e, d) => VirtualTable<DirNode>.DrawBarCell(e, d.ShareOfParent, $"{d.ShareOfParent * 100:0.0}%", Theme.CategoryColor(d.DominantCategory)))
                .AddColumn("Files", 80, d => d.FileCount.ToString("N0"), (a, b) => a.FileCount.CompareTo(b.FileCount), HorizontalAlignment.Right)
                .AddColumn("Mostly", 120, d => d.Size == 0 ? "" : FileCategories.ShortLabel(d.DominantCategory), (a, b) => a.DominantCategory.CompareTo(b.DominantCategory))
                .AddColumn("Last modified", 120, d => d.LastWriteUtcTicks == 0 ? "" : new DateTime(d.LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("d"),
                    (a, b) => a.LastWriteUtcTicks.CompareTo(b.LastWriteUtcTicks));
        _folders.RowColor = d => d.Incomplete ? Theme.Warning : null;
        _folders.SortBy(1, SortOrder.Descending);
        _folders.RowActivated += (_, d) => Navigate(d);
        _folders.SelectedIndexChanged += (_, _) => SetSelection(_folders.SelectedRow);
        _folders.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && _folders.SelectedRow is { } d) ShowContextMenu(d, _folders, e.Location); };

        _files = new VirtualTable<FileHit> { Dock = DockStyle.Fill };
        _files.AddColumn("File", 260, h => h.File.Name, (a, b) => string.Compare(a.File.Name, b.File.Name, StringComparison.CurrentCultureIgnoreCase))
              .AddColumn("Size", 90, h => ByteSize.Format(h.File.Size), (a, b) => a.File.Size.CompareTo(b.File.Size), HorizontalAlignment.Right)
              .AddColumn("Type", 110, h => FileCategories.ShortLabel(h.File.Category), (a, b) => a.File.Category.CompareTo(b.File.Category))
              .AddColumn("Modified", 110, h => h.File.LastWriteUtc.ToLocalTime().ToString("d"), (a, b) => a.File.LastWriteUtcTicks.CompareTo(b.File.LastWriteUtcTicks))
              .AddColumn("Folder", 380, h => RelativeFolder(h.Folder), (a, b) => string.Compare(RelativeFolder(a.Folder), RelativeFolder(b.Folder), StringComparison.CurrentCultureIgnoreCase));
        _files.SortBy(1, SortOrder.Descending);
        _files.RowActivated += (_, h) => OpenFile(h.FullPath);
        _files.SelectedIndexChanged += (_, _) => SetSelection(_files.SelectedRow);
        _files.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && _files.SelectedRow is { } h) ShowContextMenu(h, _files, e.Location); };

        _types = new VirtualTable<ExtensionStat> { Dock = DockStyle.Fill };
        _types.AddColumn("Extension", 120, s => s.Extension.Length == 0 ? "(none)" : s.Extension, (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase))
              .AddColumn("Type", 150, s => FileCategories.Label(s.Category), (a, b) => a.Category.CompareTo(b.Category))
              .AddColumn("Files", 90, s => s.Files.ToString("N0"), (a, b) => a.Files.CompareTo(b.Files), HorizontalAlignment.Right)
              .AddColumn("Size", 90, s => ByteSize.Format(s.Bytes), (a, b) => a.Bytes.CompareTo(b.Bytes), HorizontalAlignment.Right)
              .AddColumn("Share", 200, s => "", (a, b) => a.Bytes.CompareTo(b.Bytes),
                  draw: (e, s) => VirtualTable<ExtensionStat>.DrawBarCell(e, _current is { Size: > 0 } c ? (double)s.Bytes / c.Size : 0,
                      _current is { Size: > 0 } c2 ? $"{s.Bytes * 100.0 / c2.Size:0.0}%" : "", Theme.CategoryColor(s.Category)));
        _types.SortBy(3, SortOrder.Descending);
        _types.RowActivated += (_, s) => ShowFilesOfExtension(s.Extension);

        _viewNote = new Label
        {
            UseMnemonic = false,
            Dock = DockStyle.Bottom, Height = Theme.S(26), ForeColor = Theme.TextMuted, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft, Padding = Theme.S(10, 0, 0, 0), BackColor = Theme.Surface,
        };
        _viewHost = new Card { Dock = DockStyle.Fill, Padding = Theme.S(1, 6, 1, 1) };
        _viewHost.Controls.Add(_map);
        _viewHost.Controls.Add(_folders);
        _viewHost.Controls.Add(_files);
        _viewHost.Controls.Add(_types);
        _viewHost.Controls.Add(_viewNote);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        right.Controls.Add(_viewHost);
        right.Controls.Add(Spacer(8));
        right.Controls.Add(_fileFilters);
        right.Controls.Add(tabRow);
        right.Controls.Add(Spacer(12));
        right.Controls.Add(summary);

        _incompleteBar = new InfoBar(InfoKind.Info, "") { Dock = DockStyle.Top, Visible = false };
        _incompleteBar.AddAction("Restart as administrator", (_, _) => RequestElevation()).Glyph = Glyph.Shield;

        _results = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Visible = false };
        _results.Controls.Add(right);
        _results.Controls.Add(splitter);
        _results.Controls.Add(treeCard);
        _results.Controls.Add(_incompleteBar);

        // ---- footer -------------------------------------------------------------------------------
        _footer = new FooterBar();
        _open = new ModernButton("Open", ButtonKind.Secondary, Glyph.Open);
        _open.Click += (_, _) => OpenSelection();
        _reveal = new ModernButton("Show in Explorer", ButtonKind.Secondary, Glyph.FolderOpen);
        _reveal.Click += (_, _) => { if (SelectionPath() is { } p) ShowInExplorer(p); };
        _delete = new ModernButton("Move to Recycle Bin…", ButtonKind.Danger, Glyph.Delete) { Margin = Padding.Empty };
        _delete.Click += (_, _) => DeleteSelection();
        _footer.Actions.Controls.AddRange([_open, _reveal, _delete]);

        Body.Controls.Add(_results);
        Body.Controls.Add(_landing);
        Body.Controls.Add(Spacer(12, DockStyle.Bottom));
        Body.Controls.Add(_footer);
        Body.Controls.Add(Spacer(12));
        Body.Controls.Add(scanCard);

        LoadDrives();
        UpdateFooter();
    }

    private sealed record DriveChoice(char Letter, string Root, string Text, long Total, long Free)
    {
        public override string ToString() => Text;
    }

    protected override void OnActivated(bool firstTime)
    {
        if (!firstTime && _result is null && !_scanning)
            LoadDrives(); // free space may have changed
    }

    public override void OnAppClosing()
    {
        _scanCts?.Cancel();
        _queryCts?.Cancel();
    }

    // ---- drives ---------------------------------------------------------------------------------

    private void LoadDrives()
    {
        var drives = new List<DriveChoice>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                    continue;
                string label = string.IsNullOrEmpty(d.VolumeLabel)
                    ? d.DriveType switch { DriveType.Removable => "Removable", DriveType.Network => "Network", _ => "Local disk" }
                    : d.VolumeLabel;
                drives.Add(new DriveChoice(d.Name[0], d.RootDirectory.FullName,
                    $"{d.Name[0]}:  {label} · {ByteSize.Format(d.TotalFreeSpace)} free of {ByteSize.Format(d.TotalSize)}",
                    d.TotalSize, d.TotalFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var previous = (_target.SelectedItem as DriveChoice)?.Letter;
        _target.Items.Clear();
        foreach (var d in drives)
            _target.Items.Add(d);
        int idx = drives.FindIndex(d => d.Letter == previous);
        if (_target.Items.Count > 0)
            _target.SelectedIndex = Math.Max(0, idx);
        _scanDrive.Enabled = _target.Items.Count > 0;

        _driveList.SuspendLayout();
        _driveList.Controls.ClearAndDispose();
        foreach (var d in drives)
            _driveList.Controls.Add(BuildDriveRow(d));
        _driveList.ResumeLayout();
    }

    private Control BuildDriveRow(DriveChoice d)
    {
        long used = d.Total - d.Free;
        var row = new Panel { Width = Theme.S(720), Height = Theme.S(64), BackColor = Color.Transparent, Margin = Theme.S(0, 0, 0, 6) };
        var icon = new Label { UseMnemonic = false, Text = Glyph.Drive, Font = Theme.Icon(18f), ForeColor = Theme.TextMuted, Location = new Point(0, Theme.S(10)), AutoSize = true };
        var name = new Label { UseMnemonic = false, Text = d.Text.Split(" · ")[0], Font = Theme.BodyBold, AutoSize = true, Location = new Point(Theme.S(44), Theme.S(6)) };
        var detail = new Label
        {
            UseMnemonic = false,
            Text = $"{ByteSize.Format(used)} used · {ByteSize.Format(d.Free)} free of {ByteSize.Format(d.Total)}",
            Font = Theme.Small, ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(Theme.S(44), Theme.S(42)),
        };
        double frac = d.Total == 0 ? 0 : (double)used / d.Total;
        var bar = new UsageBar { Location = new Point(Theme.S(44), Theme.S(28)), Width = Theme.S(480), Height = Theme.S(10) };
        bar.SetSegments([("Used", used, frac > 0.9 ? Theme.Danger : Theme.Accent)], d.Total);
        var scan = new ModernButton("Scan", ButtonKind.Secondary, Glyph.Search) { Location = new Point(Theme.S(560), Theme.S(14)) };
        scan.Click += (_, _) =>
        {
            _target.SelectedItem = d;
            _ = ScanAsync(d.Root, d.Letter);
        };
        row.Controls.AddRange([icon, name, bar, detail, scan]);
        return row;
    }

    // ---- scanning ----------------------------------------------------------------------------------

    private async Task ScanAsync(string path, char? driveLetter)
    {
        if (_scanning)
            return;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _scanning = true;
        Busy = true;
        _scanDrive.Enabled = _scanFolder.Enabled = _target.Enabled = false;
        _stop.Visible = true;
        _scanProgress.Visible = true;

        long expected = 0;
        if (driveLetter is { } letter && _target.Items.OfType<DriveChoice>().FirstOrDefault(c => c.Letter == letter) is { } dc)
            expected = dc.Total - dc.Free;
        _scanProgress.Indeterminate = expected == 0;
        _scanStatus.Text = $"Scanning {path} …";

        DiskUsageResult? result = null;
        try
        {
            result = await Task.Run(() => new DiskUsageScanner().Scan(path, p => RunOnUi(() =>
            {
                if (!_scanning)
                    return;
                if (expected > 0)
                    _scanProgress.Value = Math.Min(0.99, (double)p.Bytes / expected);
                _scanStatus.Text = $"{p.Files:N0} files · {p.Folders:N0} folders · {ByteSize.Format(p.Bytes)} — {p.CurrentFolder}";
            }), ct), ct);
        }
        catch (OperationCanceledException)
        {
            _scanStatus.Text = "Scan stopped.";
        }
        catch (Exception ex)
        {
            _scanStatus.Text = "Scan failed.";
            ShowError("Scan failed", ex.Message);
        }
        finally
        {
            _scanning = false;
            Busy = false;
            _scanDrive.Enabled = _target.Items.Count > 0;
            _scanFolder.Enabled = _target.Enabled = true;
            _stop.Visible = false;
            _scanProgress.Visible = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }

        if (result is null || IsDisposed)
            return;
        _result = result;
        _scanDriveLetter = driveLetter;
        if (driveLetter is { } l2)
        {
            try
            {
                var di = new DriveInfo(l2.ToString());
                _driveTotal = di.TotalSize;
                _driveFree = di.TotalFreeSpace;
            }
            catch (IOException)
            {
                _driveTotal = _driveFree = 0;
            }
        }
        else
        {
            _driveTotal = _driveFree = 0;
        }

        var root = result.Root;
        _scanStatus.Text = $"Scanned {root.FullPath} in {(result.Elapsed.TotalSeconds < 1 ? "under a second" : $"{result.Elapsed.TotalSeconds:0.0} s")} — " +
                           $"{root.FileCount:N0} files, {root.FolderCount:N0} folders, {ByteSize.Format(root.Size)}" +
                           (result.OnlineOnlyFiles > 0 ? $" · {result.OnlineOnlyFiles:N0} online-only cloud files not counted" : "");
        _incompleteBar.Text = $"{Plural(result.UnreadableFolders, "folder")} couldn't be read, so some totals are too low" +
                              (Elevation.IsAdministrator ? "." : ". Restart as administrator for a complete picture.");
        _incompleteBar.Visible = result.UnreadableFolders > 0;

        BuildTree(root);
        _landing.Visible = false;
        _results.Visible = true;
        Navigate(root);
    }

    // ---- tree --------------------------------------------------------------------------------------

    private ImageList BuildBarImages()
    {
        var size = new Size(Theme.S(30), Theme.S(16));
        var list = new ImageList { ImageSize = size, ColorDepth = ColorDepth.Depth32Bit };
        for (int i = 0; i <= 20; i++)
        {
            var bmp = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var track = new RectangleF(1, size.Height / 2f - Theme.S(4), size.Width - Theme.S(6), Theme.S(8));
                using (var p = Theme.RoundedRect(track, Theme.S(2)))
                using (var b = new SolidBrush(Theme.Border))
                    g.FillPath(b, p);
                if (i > 0)
                {
                    var fill = new RectangleF(track.X, track.Y, Math.Max(Theme.S(3), track.Width * i / 20f), track.Height);
                    using var p = Theme.RoundedRect(fill, Theme.S(2));
                    using var b = new SolidBrush(Theme.Accent);
                    g.FillPath(b, p);
                }
            }
            list.Images.Add(bmp);
        }
        return list;
    }

    private static int BarIndex(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 20);

    private static string NodeText(DirNode d) => $"{d.Name}   {ByteSize.Format(d.Size)}";

    private void BuildTree(DirNode root)
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        _nodes.Clear();
        var rootNode = MakeNode(root);
        _tree.Nodes.Add(rootNode);
        EnsurePopulated(rootNode);
        rootNode.Expand();
        _tree.EndUpdate();
    }

    private TreeNode MakeNode(DirNode d)
    {
        int img = BarIndex(d.ShareOfParent);
        var node = new TreeNode(NodeText(d)) { Tag = d, ImageIndex = img, SelectedImageIndex = img };
        if (d.Incomplete)
            node.ForeColor = Theme.Warning;
        node.ToolTipText = d.Incomplete ? "Some folders inside couldn't be read — the real size may be larger." : "";
        if (d.Children.Count > 0)
            node.Nodes.Add(new TreeNode("Loading…") { Tag = LoadingTag });
        _nodes[d] = node;
        return node;
    }

    private void EnsurePopulated(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Tag != LoadingTag || node.Tag is not DirNode d)
            return;
        _tree.BeginUpdate();
        node.Nodes.Clear();
        foreach (var child in d.Children.Take(MaxTreeChildren))
            node.Nodes.Add(MakeNode(child));
        if (d.Children.Count > MaxTreeChildren)
        {
            var rest = d.Children.Skip(MaxTreeChildren).ToList();
            node.Nodes.Add(new TreeNode($"… {rest.Count:N0} smaller folders   {ByteSize.Format(rest.Sum(r => r.Size))}")
            {
                ForeColor = Theme.TextMuted, ImageIndex = 0, SelectedImageIndex = 0,
            });
        }
        _tree.EndUpdate();
    }

    /// <summary>Select a folder in the tree (expanding its ancestors) and show it.</summary>
    private void Navigate(DirNode d)
    {
        var node = EnsureNode(d);
        _suppressTreeSelect = true;
        _tree.SelectedNode = node;
        node?.EnsureVisible();
        _suppressTreeSelect = false;
        SetCurrent(d);
    }

    private TreeNode? EnsureNode(DirNode d)
    {
        if (_nodes.TryGetValue(d, out var existing))
            return existing;
        if (d.Parent is null || EnsureNode(d.Parent) is not { } parentNode)
            return null;
        EnsurePopulated(parentNode);
        if (_nodes.TryGetValue(d, out var node))
            return node;
        // Past the per-folder cap: add it explicitly so it can be selected.
        node = MakeNode(d);
        parentNode.Nodes.Add(node);
        return node;
    }

    // ---- current folder ------------------------------------------------------------------------------

    private void SetCurrent(DirNode d)
    {
        _current = d;
        _selection = null;
        UpdateSummary();
        RefreshView();
        UpdateFooter();
    }

    private void UpdateSummary()
    {
        if (_current is not { } d || _result is null)
            return;

        // breadcrumb: root › … › current
        _crumbs.SuspendLayout();
        _crumbs.Controls.ClearAndDispose();
        var chain = d.Ancestors().Reverse().Append(d).ToList();
        if (chain.Count > 6)
            chain = [chain[0], .. chain.Skip(chain.Count - 5)];
        DirNode? prev = null;
        foreach (var n in chain)
        {
            if (prev is not null)
            {
                bool gap = n.Parent != prev;
                _crumbs.Controls.Add(new Label
        {
            UseMnemonic = false,
                    Text = gap ? "› … ›" : "›", AutoSize = true, ForeColor = Theme.TextSubtle, Margin = Theme.S(2, 4, 2, 0),
                });
            }
            if (n == d)
            {
                _crumbs.Controls.Add(new Label { UseMnemonic = false, Text = n.Name, AutoSize = true, Font = Theme.BodyBold, ForeColor = Theme.Text, Margin = Theme.S(0, 4, 0, 0) });
            }
            else
            {
                var target = n;
                var link = new LinkLabel
                {
                    Text = n.Name, AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentPressed,
                    LinkBehavior = LinkBehavior.HoverUnderline, Margin = Theme.S(0, 4, 0, 0),
                };
                link.LinkClicked += (_, _) => Navigate(target);
                _crumbs.Controls.Add(link);
            }
            prev = n;
        }
        _crumbs.ResumeLayout();
        _up.Enabled = d.Parent is not null;

        _bigSize.Text = ByteSize.Format(d.Size);
        string detail = $"{d.FileCount:N0} files · {d.FolderCount:N0} folders";
        if (d.Parent is not null && _result.Root.Size > 0)
            detail += $" · {d.Size * 100.0 / _result.Root.Size:0.#}% of {_result.Root.Name}";
        _summaryDetail.Text = detail;

        var notes = new List<string>();
        if (d.IsRoot && _driveTotal > 0)
        {
            long used = _driveTotal - _driveFree;
            long unaccounted = used - d.Size;
            string note = $"Drive: {ByteSize.Format(used)} used, {ByteSize.Format(_driveFree)} free of {ByteSize.Format(_driveTotal)}.";
            if (unaccounted > used / 50)
                note += $" {ByteSize.Format(unaccounted)} isn't in any file the scan could read — system files, restore points, " +
                        "the page/hibernation file or protected folders.";
            notes.Add(note);
        }
        if (d.Incomplete)
            notes.Add("Some folders inside couldn't be read, so this total may be too low.");
        _summaryNote.Text = string.Join(" ", notes);
        _summaryNote.Visible = notes.Count > 0;

        var segments = FileCategories.All
            .Select(c => (Label: FileCategories.Label(c), Value: d.BytesOf(c), Color: Theme.CategoryColor(c)))
            .ToList();
        _categoryBar.SetSegments(segments, d.Size);

        _legend.SuspendLayout();
        _legend.Controls.ClearAndDispose();
        foreach (var c in FileCategories.All)
        {
            long bytes = d.BytesOf(c);
            if (bytes <= 0)
                continue;
            var cat = c;
            var item = new LegendItem(Theme.CategoryColor(c),
                $"{FileCategories.ShortLabel(c)}  {ByteSize.Format(bytes)}",
                d.Size == 0 ? "" : $" {bytes * 100.0 / d.Size:0.#}%");
            item.Tip($"{FileCategories.Label(c)}: {d.FilesOf(c):N0} files, {ByteSize.Format(bytes)}\nClick to list the largest ones.");
            item.Click += (_, _) => ShowFilesOfCategory(cat);
            _legend.Controls.Add(item);
        }
        _legend.ResumeLayout();
        LayoutSummary();
    }

    /// <summary>Size the summary card to its content (the legend and note wrap on narrow windows).</summary>
    private void LayoutSummary()
    {
        int w = _summaryCard.ClientSize.Width - _summaryCard.Padding.Horizontal;
        if (w <= 0)
            return;
        int noteH = 0;
        if (_summaryNote.Visible && _summaryNote.Text.Length > 0)
        {
            noteH = TextRenderer.MeasureText(_summaryNote.Text, _summaryNote.Font, new Size(w, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + _summaryNote.Padding.Vertical;
            _summaryNote.Height = noteH;
        }
        _legend.Height = _legend.GetPreferredSize(new Size(w, 0)).Height;
        _summaryCard.Height = _summaryCard.Padding.Vertical + _crumbRow.Height + _sizeRow.GetPreferredSize(Size.Empty).Height +
                              noteH + _categoryBar.Height + _legend.Height;
    }

    private string RelativeFolder(DirNode folder)
    {
        if (_current is null || folder == _current)
            return ".";
        var parts = new Stack<string>();
        for (var n = folder; n is not null && n != _current; n = n.Parent)
            parts.Push(n.Name);
        return string.Join("\\", parts);
    }

    // ---- views -------------------------------------------------------------------------------------------

    private void ScheduleQuery()
    {
        _queryDebounce.Stop();
        _queryDebounce.Start();
    }

    private void RefreshView()
    {
        int tab = _tabs.SelectedIndex;
        _map.Visible = tab == 0;
        _folders.Visible = tab == 1;
        _files.Visible = tab == 2;
        _types.Visible = tab == 3;
        _fileFilters.Visible = tab == 2;
        _viewNote.Text = "";
        if (_current is not { } d)
            return;

        _queryCts?.Cancel();
        switch (tab)
        {
            case 0:
                var tiles = d.Children.Select(c => new TreemapItem(c.Name, c.Size, Theme.CategoryColor(c.DominantCategory), c,
                        $"Folder · {c.FileCount:N0} files · mostly {FileCategories.ShortLabel(c.DominantCategory).ToLowerInvariant()}"))
                    .Concat(d.Files.Take(200).Select(f => new TreemapItem(f.Name, f.Size, Theme.CategoryColor(f.Category), new FileHit(d, f),
                        $"File · {FileCategories.ShortLabel(f.Category)} · modified {f.LastWriteUtc.ToLocalTime():d}")));
                _map.SetItems(tiles);
                _viewNote.Text = "Area shows size; color shows the main file type (see legend above). Click a folder to open it, right-click for actions.";
                break;
            case 1:
                _folders.SetRows(d.Children.ToList());
                _viewNote.Text = d.OwnFilesSize > 0
                    ? $"{Plural(d.Children.Count, "subfolder")} · plus {Plural(d.Files.Count, "file")} ({ByteSize.Format(d.OwnFilesSize)}) directly in this folder. Double-click to open a folder."
                    : $"{Plural(d.Children.Count, "subfolder")}. Double-click to open a folder.";
                break;
            case 2:
                RunFileQuery(d);
                break;
            case 3:
                RunTypeQuery(d);
                break;
        }
    }

    private FileFilter CurrentFilter()
    {
        int cat = _fCategory.SelectedIndex;
        int days = Ages[Math.Max(0, _fAge.SelectedIndex)].Days;
        return new FileFilter
        {
            Categories = cat <= 0 ? null : new HashSet<FileCategory> { FileCategories.All[cat - 1] },
            MinSize = MinSizes[Math.Max(0, _fMinSize.SelectedIndex)].Bytes,
            ModifiedBeforeUtc = days == 0 ? null : DateTime.UtcNow.AddDays(-days),
            NameContains = _fSearch.Text.Trim(),
            Extension = _extFilter,
        };
    }

    private void RunFileQuery(DirNode d)
    {
        var filter = CurrentFilter();
        var cts = _queryCts = new CancellationTokenSource();
        _viewNote.Text = "Searching…";
        Task.Run(() => DiskUsageQuery.LargestFiles(d, filter, TopFiles, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                RunOnUi(() =>
                {
                    if (cts.IsCancellationRequested || _current != d)
                        return;
                    var (top, count, bytes) = t.Result;
                    _files.SetRows(top);
                    _viewNote.Text = count == 0
                        ? "No files match these filters."
                        : $"{count:N0} matching files · {ByteSize.Format(bytes)}" +
                          (count > top.Count ? $" — showing the largest {top.Count:N0}" : "") +
                          ". Double-click to open.";
                });
            }
        }, TaskScheduler.Default);
    }

    private void RunTypeQuery(DirNode d)
    {
        var cts = _queryCts = new CancellationTokenSource();
        _viewNote.Text = "Counting…";
        Task.Run(() => DiskUsageQuery.ExtensionBreakdown(d, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                RunOnUi(() =>
                {
                    if (cts.IsCancellationRequested || _current != d)
                        return;
                    _types.SetRows(t.Result);
                    _viewNote.Text = $"{Plural(t.Result.Count, "file type")}. Double-click a type to list its largest files.";
                });
            }
        }, TaskScheduler.Default);
    }

    private void ShowFilesOfCategory(FileCategory c)
    {
        _extFilter = null;
        _extChip.Visible = false;
        _fCategory.SelectedIndex = (int)c + 1;
        _fMinSize.SelectedIndex = 0;
        _tabs.SelectedIndex = 2;
        RefreshView();
    }

    private void ShowFilesOfExtension(string ext)
    {
        _extFilter = ext; // "" = files without an extension
        _extChip.Text = $"{(ext.Length == 0 ? "no extension" : ext)}  ✕";
        _extChip.Checked = true;
        _extChip.Visible = true;
        _fCategory.SelectedIndex = 0;
        _fMinSize.SelectedIndex = 0;
        _tabs.SelectedIndex = 2;
        RefreshView();
    }

    private void OnMapClick(TreemapItem item, MouseEventArgs mouse)
    {
        switch (item.Tag)
        {
            case DirNode dir when mouse.Button == MouseButtons.Left:
                Navigate(dir);
                break;
            case DirNode dir:
                SetSelection(dir);
                ShowContextMenu(dir, _map, mouse.Location);
                break;
            case FileHit hit:
                SetSelection(hit);
                if (mouse.Button == MouseButtons.Right)
                    ShowContextMenu(hit, _map, mouse.Location);
                break;
        }
    }

    // ---- selection & actions ----------------------------------------------------------------------------

    private void SetSelection(object? item)
    {
        _selection = item;
        UpdateFooter();
    }

    private string? SelectionPath() => _selection switch
    {
        FileHit h => h.FullPath,
        DirNode d => d.FullPath,
        _ => _current?.FullPath,
    };

    private void UpdateFooter()
    {
        bool has = _result is not null && _current is not null;
        _open.Enabled = _reveal.Enabled = has;
        _delete.Enabled = has && _selection is FileHit or DirNode { IsRoot: false };
        if (!has)
        {
            _footer.Status.Text = _scanning ? "Scanning…" : "Pick a drive or folder to see what's using the space.";
            return;
        }
        _footer.Status.Text = _selection switch
        {
            FileHit h => $"{h.File.Name} · {ByteSize.Format(h.File.Size)} · {FileCategories.ShortLabel(h.File.Category)} · modified {h.File.LastWriteUtc.ToLocalTime():g}",
            DirNode d => $"{d.Name} · {ByteSize.Format(d.Size)} · {d.FileCount:N0} files",
            _ => $"{_current!.FullPath} · {ByteSize.Format(_current.Size)}",
        };
    }

    private void OpenSelection()
    {
        switch (_selection)
        {
            case FileHit h:
                OpenFile(h.FullPath);
                break;
            case DirNode d:
                Navigate(d);
                break;
            default:
                if (_current is not null)
                    ShowInExplorer(_current.FullPath);
                break;
        }
    }

    private void ShowContextMenu(object item, Control owner, Point location)
    {
        SetSelection(item);
        var menu = new ContextMenuStrip();
        if (item is DirNode d)
        {
            menu.Items.Add("Open folder here", null, (_, _) => Navigate(d));
            menu.Items.Add("Show in Explorer", null, (_, _) => ShowInExplorer(d.FullPath));
            menu.Items.Add("List largest files in it", null, (_, _) => { Navigate(d); _tabs.SelectedIndex = 2; });
            menu.Items.Add("Copy path", null, (_, _) => Clipboard.SetText(d.FullPath));
            if (!d.IsRoot)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Move to Recycle Bin…", null, (_, _) => DeleteSelection());
            }
        }
        else if (item is FileHit h)
        {
            menu.Items.Add("Open", null, (_, _) => OpenFile(h.FullPath));
            menu.Items.Add("Show in Explorer", null, (_, _) => ShowInExplorer(h.FullPath));
            menu.Items.Add("Go to its folder", null, (_, _) => Navigate(h.Folder));
            menu.Items.Add("Copy path", null, (_, _) => Clipboard.SetText(h.FullPath));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Move to Recycle Bin…", null, (_, _) => DeleteSelection());
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(owner, location);
    }

    /// <summary>
    /// Moves the selected file/folder to the Recycle Bin after confirmation.
    /// Only offered where a Recycle Bin exists, so nothing is ever deleted permanently.
    /// </summary>
    private void DeleteSelection()
    {
        string? path;
        long size;
        switch (_selection)
        {
            case FileHit h: path = h.FullPath; size = h.File.Size; break;
            case DirNode { IsRoot: false } d: path = d.FullPath; size = d.Size; break;
            default: return;
        }

        string root = Path.GetPathRoot(path) ?? "";
        DriveType type;
        try { type = new DriveInfo(root).DriveType; }
        catch (ArgumentException) { type = DriveType.Unknown; }
        if (type != DriveType.Fixed)
        {
            ShowInfo("No Recycle Bin on this drive",
                "This drive has no Recycle Bin, so deleting would be permanent. To be safe, this app only moves files to the " +
                "Recycle Bin — delete it from Explorer if you're sure.");
            return;
        }

        bool system = IsSystemPath(path);
        string what = _selection is DirNode dn ? $"the folder \"{dn.Name}\" ({ByteSize.Format(size)}, {dn.FileCount:N0} files)" : $"\"{Path.GetFileName(path)}\" ({ByteSize.Format(size)})";
        string message = $"Move {what} to the Recycle Bin?\n\n{path}" +
                         (system ? "\n\n⚠ This is inside a Windows or program folder. Removing it can break Windows or installed apps." : "");
        if (MessageBox.Show(this, message, "Move to Recycle Bin", MessageBoxButtons.YesNo,
                system ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        // If Windows can't recycle it (too big for the bin, bin disabled) it asks
        // before deleting permanently; declining leaves the file where it is.
        switch (Native.MoveToRecycleBin(this, path, out string? error))
        {
            case Native.RecycleResult.Cancelled:
                _footer.Status.Text = $"Not moved: {path}";
                return;
            case Native.RecycleResult.Failed:
                ShowError("Couldn't move to the Recycle Bin", error ?? "Unknown error.");
                return;
        }

        // Update totals in place — no rescan needed.
        switch (_selection)
        {
            case FileHit h:
                DiskUsageScanner.RemoveFile(h.Folder, h.File);
                RefreshAncestorNodes(h.Folder);
                break;
            case DirNode d:
                var parent = d.Parent!;
                DiskUsageScanner.RemoveFolder(d);
                if (_nodes.Remove(d, out var node))
                    node.Remove();
                foreach (var gone in d.SelfAndDescendants())
                    _nodes.Remove(gone);
                RefreshAncestorNodes(parent);
                if (_current is not null && (_current == d || d.IsAncestorOf(_current)))
                {
                    Navigate(parent);
                    return;
                }
                break;
        }
        _footer.Status.Text = $"Moved to the Recycle Bin: {path} ({ByteSize.Format(size)} freed once the bin is emptied).";
        var keepStatus = _footer.Status.Text;
        SetCurrent(_current!);
        _footer.Status.Text = keepStatus;
    }

    private void RefreshAncestorNodes(DirNode from)
    {
        for (DirNode? n = from; n is not null; n = n.Parent)
        {
            if (!_nodes.TryGetValue(n, out var node))
                continue;
            node.Text = NodeText(n);
            int img = BarIndex(n.ShareOfParent);
            node.ImageIndex = node.SelectedImageIndex = img;
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag is DirNode c)
                    child.ImageIndex = child.SelectedImageIndex = BarIndex(c.ShareOfParent);
            }
        }
    }

    private static bool IsSystemPath(string path)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        return roots.Any(r => r.Length > 0 && (path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                                                path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanCts?.Cancel();
            _queryCts?.Cancel();
            _queryDebounce.Dispose();
            _treeImages.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Clickable legend entry: color dot, label, muted percentage.</summary>
    private sealed class LegendItem : Control
    {
        private readonly Color _color;
        private readonly string _suffix;
        private bool _hover;

        public LegendItem(Color color, string text, string suffix)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _color = color;
            _suffix = suffix;
            Text = text;
            Font = Theme.Small;
            Cursor = Cursors.Hand;
            Margin = Theme.S(0, 0, 14, 0);
            int w = Theme.S(14) + TextRenderer.MeasureText(text, Theme.SmallBold).Width + TextRenderer.MeasureText(suffix, Theme.Small).Width;
            Size = new Size(w, Theme.S(20));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Chip.ParentBack(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.S(9);
            using (var b = new SolidBrush(_color))
                g.FillEllipse(b, 0, (Height - d) / 2f, d, d);
            var font = _hover ? new Font(Theme.SmallBold, FontStyle.Underline) : Theme.SmallBold;
            int x = Theme.S(14);
            var tf = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, Text, font, new Rectangle(x, 0, Width - x, Height), Theme.Text, tf);
            x += TextRenderer.MeasureText(g, Text, font, Size.Empty, tf).Width;
            TextRenderer.DrawText(g, _suffix, Theme.Small, new Rectangle(x, 0, Width - x, Height), Theme.TextMuted, tf);
            if (_hover)
                font.Dispose();
        }
    }
}
