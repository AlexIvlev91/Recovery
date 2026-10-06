using System.Diagnostics;
using Recovery.Core;
using Recovery.Core.Carving;
using Recovery.Gui.Controls;

namespace Recovery.Gui.Pages;

/// <summary>
/// Tier 2: signature-carves photos/videos out of a drive's free space into a
/// staging folder, then lets you review them (grouped by type, filtered by
/// type/size/name) and keep only what you want — the rest is discarded.
/// Pause/continue and a safe stop (keeps what was found) are supported.
/// </summary>
internal sealed class DeepScanPage : PageBase
{
    private const int MaxShown = 4000;

    private static readonly (string Label, long Bytes)[] SizeChoices =
    [
        ("Any", 0), ("50 KB", 50L << 10), ("100 KB", 100L << 10), ("250 KB", 250L << 10), ("500 KB", 500L << 10),
        ("1 MB", 1L << 20), ("2 MB", 2L << 20), ("5 MB", 5L << 20), ("10 MB", 10L << 20), ("50 MB", 50L << 20),
        ("100 MB", 100L << 20), ("500 MB", 500L << 20), ("1 GB", 1L << 30), ("5 GB", 5L << 30),
    ];

    // settings
    private readonly ComboBox _drive;
    private readonly FlowLayoutPanel _formats;
    private readonly RadioButton _freeOnly, _wholeDrive;
    private readonly ComboBox _scanMin, _scanMax;
    private readonly CheckBox _matchNames, _sortByType;
    private readonly TextBox _dest;
    private readonly ModernButton _start, _pause, _stop;
    private readonly ThinProgressBar _progress;
    private readonly Label _progressMain, _progressDetail;
    private readonly Control[] _settingsInputs;

    // results
    private readonly FlowLayoutPanel _typeFilters;
    private readonly ComboBox _minSize, _maxSize;
    private readonly TextBox _search;
    private readonly GalleryListView _grid;
    private readonly ThumbnailLoader _thumbs;
    private readonly EmptyState _empty;
    private readonly FooterBar _footer;
    private readonly ModernButton _keep, _discard;
    private readonly Panel _filterRow;

    private CancellationTokenSource? _scanCts;
    private ManualResetEventSlim? _pauseGate;
    private readonly Stopwatch _activeClock = new();
    private string? _stagingDir;
    private string? _destDir;
    private List<CarvedFile> _staged = [];
    private List<CarvedFile> _matching = [];
    private readonly HashSet<string> _unchecked = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressCheck;
    private bool _scanning;
    private bool _keeping;
    private bool _discardOnClose;
    private Task? _scanTask;

    public DeepScanPage()
        : base("carve", "Deep scan",
            "Searches a drive's free space for the raw content of deleted photos and videos — finds files even when " +
            "their names are gone. Slow but thorough; works on FAT32/exFAT cards too.",
            Glyph.Search, needsAdmin: true)
    {
        // ---- settings column ----------------------------------------------------------
        _drive = Ui.Combo(262);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removable" : "Local disk") : d.VolumeLabel;
                _drive.Items.Add(new DriveChoice(d.Name[0], $"{d.Name[0]}:  {label} · {d.DriveFormat} · {ByteSize.Format(d.TotalSize)}"));
            }
            catch (IOException) { }
        }
        if (_drive.Items.Count > 0)
            _drive.SelectedIndex = _drive.Items.Count > 1 ? 1 : 0; // the system drive is rarely the one to carve
        _drive.Margin = Theme.S(0, 0, 0, 0);

        _formats = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(Theme.S(266), 0), Margin = Padding.Empty, BackColor = Color.Transparent };
        foreach (var fmt in CarveEngine.AllFormats)
            _formats.Controls.Add(new Chip(fmt.Name.Replace("/", " · "), true) { Tag = fmt.Name });

        _freeOnly = new RadioButton { Text = "Free space only (recommended)", Checked = true, AutoSize = true, Margin = Theme.S(0, 0, 0, 2), BackColor = Color.Transparent };
        _wholeDrive = new RadioButton { Text = "Entire drive (also finds live files)", AutoSize = true, Margin = Theme.S(0, 0, 0, 0), BackColor = Color.Transparent };

        _scanMin = Ui.Combo(110, SizeChoices.Select(c => c.Label).ToArray());
        _scanMax = Ui.Combo(110, ["No limit", .. SizeChoices.Skip(1).Select(c => c.Label)]);
        _scanMin.Margin = _scanMax.Margin = Theme.S(0, 0, 6, 0);
        var sizeRow = Ui.Row(_scanMin, Ui.Text("to", color: Theme.TextMuted, margin: Theme.S(0, 4, 6, 0)), _scanMax);

        _matchNames = Ui.Check("Recover original names (NTFS)", true);
        _matchNames.Tip("Cross-checks found files against deleted NTFS records and restores their real names where possible.");
        _sortByType = Ui.Check("Sort kept files into type folders", true);
        _matchNames.Margin = _sortByType.Margin = Theme.S(0, 0, 0, 4);

        _dest = new TextBox { Width = Theme.S(222), Text = AppSettings.Get("carve.dest", AppSettings.DefaultFolder("Recovered")), Margin = Theme.S(0, 1, 6, 0) };
        var browse = new ModernButton("", ButtonKind.Secondary, Glyph.FolderOpen) { IconOnly = true, Margin = Padding.Empty }.Tip("Browse…");
        browse.Click += (_, _) =>
        {
            if (PickFolder("Where should kept files go? Pick a folder on a DIFFERENT drive than the one you scan.", _dest.Text) is { } p)
                _dest.Text = p;
        };

        _start = new ModernButton("Start scan", ButtonKind.Primary, Glyph.Play) { AutoSize = false, Width = Theme.S(262), Height = Theme.S(36), Margin = Theme.S(0, 16, 0, 8) };
        _start.Click += (_, _) => _ = StartAsync();
        _pause = new ModernButton("Pause", ButtonKind.Secondary, Glyph.Pause) { Enabled = false, AutoSize = false, Width = Theme.S(127) };
        _pause.Click += (_, _) => TogglePause();
        _stop = new ModernButton("Stop", ButtonKind.Secondary, Glyph.Stop) { Enabled = false, AutoSize = false, Width = Theme.S(127), Margin = Padding.Empty };
        _stop.Tip("Stops the scan and keeps everything found so far for review.");
        _stop.Click += (_, _) =>
        {
            _pauseGate?.Set(); // a paused scan must wake up to see the cancel
            _scanCts?.Cancel();
        };

        _progress = new ThinProgressBar { Width = Theme.S(262), Margin = Theme.S(0, 16, 0, 8) };
        _progressMain = Ui.Text("", Theme.BodyBold);
        _progressDetail = Ui.Text("", Theme.Small, Theme.TextMuted);
        _progressDetail.MaximumSize = new Size(Theme.S(262), 0);

        var tip = Ui.Text("Tip: works best on HDDs, SD cards and USB sticks. On SSDs, TRIM usually wipes deleted data within seconds. " +
                          "Large fragmented videos may come back incomplete.", Theme.Small, Theme.TextMuted, Theme.S(0, 18, 0, 0));
        tip.MaximumSize = new Size(Theme.S(262), 0);

        var settings = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
        };
        settings.Controls.AddRange(
        [
            Caption("Drive to scan", top: 0), _drive,
            Caption("Look for"), _formats,
            Caption("Search area"), _freeOnly, _wholeDrive,
            Caption("Only keep files sized"), sizeRow,
            Caption("Options"), _matchNames, _sortByType,
            Caption("Save kept files to"), Ui.Row(_dest, browse),
            _start, Ui.Row(_pause, _stop),
            _progress, _progressMain, _progressDetail, tip,
        ]);
        _pause.Margin = Theme.S(0, 0, 8, 0);
        _progress.Visible = false;

        var settingsCard = new Card { Dock = DockStyle.Left, Width = Theme.S(316), Padding = Theme.S(16, 14, 4, 14) };
        settingsCard.Controls.Add(settings);
        _settingsInputs = [_drive, _formats, _freeOnly, _wholeDrive, _scanMin, _scanMax, _matchNames, _dest, browse];

        // ---- results ---------------------------------------------------------------------
        _typeFilters = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Left, BackColor = Color.Transparent, Padding = Theme.S(0, 3, 0, 0) };
        _minSize = Ui.Combo(90, SizeChoices.Select(c => c.Label).ToArray());
        _maxSize = Ui.Combo(90, ["No limit", .. SizeChoices.Skip(1).Select(c => c.Label)]);
        _minSize.SelectedIndexChanged += (_, _) => RefreshGrid();
        _maxSize.SelectedIndexChanged += (_, _) => RefreshGrid();
        _search = Ui.SearchBox("Search names…", 150);
        _search.TextChanged += (_, _) => RefreshGrid();
        var sizeFilter = Ui.Row(Ui.FieldLabel("Size"), _minSize, Ui.FieldLabel("to"), _maxSize, _search);
        sizeFilter.Dock = DockStyle.Right;
        _filterRow = new Panel { Dock = DockStyle.Top, Height = Theme.S(36), BackColor = Color.Transparent, Visible = false };
        _filterRow.Controls.Add(_typeFilters);
        _filterRow.Controls.Add(sizeFilter);

        _grid = new GalleryListView { Dock = DockStyle.Fill, CheckBoxes = true, View = View.LargeIcon, ShowGroups = true };
        _grid.ItemChecked += (_, e) =>
        {
            if (_suppressCheck || e.Item.Tag is not CarvedFile f)
                return;
            if (e.Item.Checked) _unchecked.Remove(f.Path); else _unchecked.Add(f.Path);
            UpdateFooter();
        };
        _grid.ItemActivate += (_, _) => { if (SelectedFile is { } f) OpenFile(f.Path); };
        _grid.ContextMenuStrip = BuildContextMenu();
        _thumbs = new ThumbnailLoader(_grid, Theme.S(112));

        _empty = new EmptyState { Covers = _grid, Dock = DockStyle.Fill };
        var gridCard = new Card { Dock = DockStyle.Fill, Padding = Theme.S(1, 8, 1, 1) };
        gridCard.Controls.Add(_grid);
        gridCard.Controls.Add(_empty);

        _footer = new FooterBar();
        var selectAll = new ModernButton("Select all", ButtonKind.Ghost, Glyph.SelectAll);
        selectAll.Click += (_, _) => SetChecked(_matching, true);
        var clear = new ModernButton("Clear", ButtonKind.Ghost, Glyph.ClearSelection);
        clear.Click += (_, _) => SetChecked(_matching, false);
        _discard = new ModernButton("Discard all", ButtonKind.Secondary, Glyph.Delete) { Enabled = false };
        _discard.Click += (_, _) => DiscardAll(confirm: true);
        _keep = new ModernButton("Keep selected…", ButtonKind.Primary, Glyph.Download) { Enabled = false, Margin = Padding.Empty };
        _keep.Click += (_, _) => _ = KeepSelectedAsync();
        _footer.Actions.Controls.AddRange([selectAll, clear, _discard, _keep]);

        var results = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        results.Controls.Add(gridCard);
        results.Controls.Add(Spacer(12, DockStyle.Bottom));
        results.Controls.Add(_footer);
        results.Controls.Add(Spacer(8));
        results.Controls.Add(_filterRow);

        Body.Controls.Add(results);
        Body.Controls.Add(Spacer(16, DockStyle.Left));
        Body.Controls.Add(settingsCard);

        ShowIdleEmptyState();
        UpdateFooter();
    }

    private sealed record DriveChoice(char Letter, string Text)
    {
        public override string ToString() => Text;
    }

    private static Label Caption(string text, int top = 14)
    {
        var l = Ui.Caption(text);
        l.Margin = Theme.S(0, top, 0, 6);
        return l;
    }

    private CarvedFile? SelectedFile => _grid.SelectedItems.Count > 0 ? _grid.SelectedItems[0].Tag as CarvedFile : null;

    private void ShowIdleEmptyState() =>
        _empty.Show(Glyph.Search, "Found files will appear here for review",
            "Pick a drive and start the scan. Everything found is staged first — you choose what to keep, the rest is discarded.");

    // ---- scanning ------------------------------------------------------------------------

    private async Task StartAsync()
    {
        if (_scanning || _keeping || _drive.SelectedItem is not DriveChoice choice)
            return;
        if (!Elevation.IsAdministrator)
        {
            if (Confirm("Administrator required", "Deep scan reads the raw drive, which needs administrator rights.\n\nRestart the app as administrator now?"))
                RequestElevation();
            return;
        }

        char drive = choice.Letter;
        string dest = _dest.Text.Trim();
        if (dest.Length == 0)
        {
            ShowInfo("Choose a folder", "Pick where kept files should be saved.");
            return;
        }
        if (!ConfirmDestinationDrive(dest, drive))
            return;

        var selected = _formats.Controls.OfType<Chip>().Where(c => c.Checked).Select(c => (string)c.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            ShowInfo("No file types", "Pick at least one file type to look for.");
            return;
        }
        var formats = CarveEngine.AllFormats.Where(f => selected.Contains(f.Name)).ToList();

        long minSize = SizeChoices[Math.Max(0, _scanMin.SelectedIndex)].Bytes;
        long maxSize = _scanMax.SelectedIndex <= 0 ? long.MaxValue : SizeChoices[_scanMax.SelectedIndex].Bytes;
        if (maxSize < minSize)
        {
            ShowInfo("Size range", "The upper size limit must be larger than the lower one.");
            return;
        }
        bool freeOnly = _freeOnly.Checked;
        if (!freeOnly && minSize == 0)
        {
            // A whole-volume scan with no floor floods the disk with millions of icons and cached thumbnails.
            var answer = MessageBox.Show(this,
                "Scanning the entire drive with no minimum size can produce millions of tiny images (icons, thumbnails, " +
                "cache files) and fill the destination.\n\nOnly keep files of 500 KB and larger?\n\n" +
                "Yes = 500 KB minimum (recommended)   No = keep every size",
                "Large scan", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
            {
                minSize = 500L << 10;
                _scanMin.SelectedIndex = Array.FindIndex(SizeChoices, c => c.Bytes == minSize);
            }
        }

        if (_staged.Count > 0 && !Confirm("Start a new scan?",
                $"{Plural(_staged.Count, "file")} from the previous scan haven't been kept. Starting a new scan discards them.\n\nContinue?", defaultNo: true))
            return;
        DiscardAll(confirm: false);
        AppSettings.Set("carve.dest", dest);

        _destDir = dest;
        _stagingDir = Path.Combine(dest, $".carve-staging-{DateTime.Now:yyyyMMdd-HHmmss}");
        string staging = _stagingDir;
        bool matchNames = _matchNames.Checked;
        _scanCts = new CancellationTokenSource();
        _pauseGate = new ManualResetEventSlim(true);
        var ct = _scanCts.Token;
        var gate = _pauseGate;

        SetScanning(true);
        _activeClock.Restart();
        _progressMain.Text = "Starting…";
        _progressDetail.Text = "";
        _empty.Show(Glyph.Search, "Scanning…", "Found files will be shown here when the scan finishes or you press Stop.");

        bool cancelled = false;
        try
        {
            var scanTask = Task.Run(() =>
            {
                using var source = new RawVolumeByteSource(drive);
                IReadOnlyList<(long, long)> ranges;
                if (freeOnly && FreeSpaceMap.TryGetFreeRanges(drive) is { } free)
                {
                    ranges = free;
                }
                else
                {
                    if (freeOnly)
                        RunOnUi(() => _progressDetail.Text = "Free-space map unavailable — scanning the whole drive.");
                    ranges = [(0L, source.Length)];
                }

                long lastUi = 0;
                var carved = new CarveEngine(formats).Carve(source, ranges, staging, p =>
                {
                    long now = _activeClock.ElapsedMilliseconds;
                    if (now - lastUi < 200 && p.BytesScanned < p.TotalBytes)
                        return;
                    lastUi = now;
                    RunOnUi(() => ShowProgress(p));
                }, ct, gate, minSize, maxSize);

                if (matchNames && carved.Count > 0 && !ct.IsCancellationRequested)
                {
                    RunOnUi(() =>
                    {
                        _progress.Indeterminate = true;
                        _progressMain.Text = "Matching original names…";
                        _progressDetail.Text = "Cross-checking found files against deleted NTFS records.";
                    });
                    carved = CarveNameMatcher.ApplyOriginalNames(carved, source);
                }
                return carved;
            }, ct);
            _scanTask = scanTask;
            var found = await scanTask;
            _staged = found.ToList();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _staged = ListStaged();
        }
        catch (UnauthorizedAccessException)
        {
            if (IsDisposed) return;
            SetScanning(false);
            _progressMain.Text = "Access denied";
            _progressDetail.Text = "Administrator rights are required to read the drive.";
            ShowIdleEmptyState();
            return;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetScanning(false);
            _progressMain.Text = "Scan failed";
            _progressDetail.Text = ex.Message;
            ShowError("Deep scan failed", ex.Message);
            ShowIdleEmptyState();
            return;
        }

        if (IsDisposed)
            return;
        SetScanning(false);
        _activeClock.Stop();
        _progressMain.Text = (cancelled ? "Stopped" : "Finished") + $" — {Plural(_staged.Count, "file")} found";
        _progressDetail.Text = $"Took {FormatDuration(_activeClock.Elapsed)}.";

        _unchecked.Clear();
        _thumbs.Reset();
        BuildTypeFilters();
        RefreshGrid();
        if (_staged.Count == 0)
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _stagingDir = null;
            _empty.Show(Glyph.Search, cancelled ? "Stopped before anything was found" : "No recoverable photos or videos found",
                "If the files were on an SSD, TRIM has most likely erased them. On other drives, try \"Entire drive\" " +
                "or include more file types.");
        }
    }

    private void ShowProgress(CarveProgress p)
    {
        double frac = p.TotalBytes > 0 ? (double)p.BytesScanned / p.TotalBytes : 1;
        _progress.Indeterminate = false;
        _progress.Value = frac;
        _progressMain.Text = $"{frac * 100:0.0}% · {Plural(p.FilesFound, "file")} found";
        double secs = _activeClock.Elapsed.TotalSeconds;
        double speed = secs > 0.5 ? p.BytesScanned / secs : 0;
        string eta = speed > 0 && p.BytesScanned < p.TotalBytes
            ? $" · about {FormatDuration(TimeSpan.FromSeconds((p.TotalBytes - p.BytesScanned) / speed))} left"
            : "";
        _progressDetail.Text = $"{ByteSize.Format(p.BytesScanned)} of {ByteSize.Format(p.TotalBytes)}" +
                               (speed > 0 ? $" · {ByteSize.Format((long)speed)}/s" : "") + eta;
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(1, (int)t.TotalSeconds)} s";

    private void SetScanning(bool scanning)
    {
        _scanning = scanning;
        Busy = scanning;
        foreach (var c in _settingsInputs)
            c.Enabled = !scanning;
        _start.Enabled = !scanning;
        _pause.Enabled = _stop.Enabled = scanning;
        _pause.Text = "Pause";
        _pause.Glyph = Glyph.Pause;
        _progress.Visible = true;
        if (!scanning)
        {
            _progress.Indeterminate = false;
            _progress.Value = 1;
            _scanCts?.Dispose();
            _scanCts = null;
            _pauseGate?.Dispose();
            _pauseGate = null;
        }
        UpdateFooter();
    }

    private void TogglePause()
    {
        if (_pauseGate is null)
            return;
        if (_pauseGate.IsSet)
        {
            _pauseGate.Reset();
            _activeClock.Stop();
            _pause.Text = "Continue";
            _pause.Glyph = Glyph.Play;
            _progressDetail.Text = "Paused — Continue to resume, or Stop to review what's been found.";
        }
        else
        {
            _pauseGate.Set();
            _activeClock.Start();
            _pause.Text = "Pause";
            _pause.Glyph = Glyph.Pause;
        }
    }

    private List<CarvedFile> ListStaged()
    {
        if (_stagingDir is null || !Directory.Exists(_stagingDir))
            return [];
        return Directory.EnumerateFiles(_stagingDir)
            .Select(f => new CarvedFile(f, 0, new FileInfo(f).Length, Path.GetExtension(f)))
            .ToList();
    }

    // ---- review ----------------------------------------------------------------------------

    private void BuildTypeFilters()
    {
        _typeFilters.Controls.ClearAndDispose();
        foreach (var group in _staged.GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(f => f.Length)))
        {
            var chip = new Chip($"{group.Key.TrimStart('.').ToUpperInvariant()}  {group.Count():N0}") { Tag = group.Key };
            chip.CheckedChanged += (_, _) => RefreshGrid();
            _typeFilters.Controls.Add(chip);
        }
        _minSize.SelectedIndex = 0;
        _maxSize.SelectedIndex = 0;
        _filterRow.Visible = _staged.Count > 0;
    }

    private void RefreshGrid()
    {
        var allowed = _typeFilters.Controls.OfType<Chip>().Where(c => c.Checked).Select(c => (string)c.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long min = SizeChoices[Math.Max(0, _minSize.SelectedIndex)].Bytes;
        long max = _maxSize.SelectedIndex <= 0 ? long.MaxValue : SizeChoices[_maxSize.SelectedIndex].Bytes;
        string q = _search.Text.Trim();

        _matching = _staged
            .Where(f => allowed.Contains(f.Extension) && f.Length >= min && f.Length <= max &&
                        (q.Length == 0 || Path.GetFileName(f.Path).Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Length)
            .ToList();

        _suppressCheck = true;
        _grid.BeginUpdate();
        _grid.Items.Clear();
        _grid.Groups.Clear();
        var bound = new List<(ListViewItem, string)>();
        int added = 0;
        foreach (var group in _matching.GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(f => f.Length)))
        {
            var lvg = new ListViewGroup($"{group.Key.TrimStart('.').ToUpperInvariant()} — {Plural(group.Count(), "file")}, {ByteSize.Format(group.Sum(f => f.Length))}");
            _grid.Groups.Add(lvg);
            foreach (var f in group)
            {
                if (added++ >= MaxShown)
                    break;
                var lvi = new ListViewItem($"{Path.GetFileName(f.Path)}  ({ByteSize.Format(f.Length)})", lvg)
                {
                    Tag = f,
                    Checked = !_unchecked.Contains(f.Path),
                    ToolTipText = $"{Path.GetFileName(f.Path)} · {ByteSize.Format(f.Length)}\nDouble-click to open and check it.",
                };
                _grid.Items.Add(lvi);
                bound.Add((lvi, f.Path));
            }
        }
        _grid.EndUpdate();
        _suppressCheck = false;
        _thumbs.Bind(bound);

        if (_staged.Count > 0)
        {
            if (_matching.Count == 0)
                _empty.Show(Glyph.Filter, "Nothing matches your filters", "Tick more types or widen the size range.");
            else
                _empty.Visible = false;
        }
        UpdateFooter();
    }

    private void SetChecked(IEnumerable<CarvedFile> files, bool value)
    {
        foreach (var f in files)
        {
            if (value) _unchecked.Remove(f.Path); else _unchecked.Add(f.Path);
        }
        _suppressCheck = true;
        _grid.BeginUpdate();
        foreach (ListViewItem item in _grid.Items)
            item.Checked = !_unchecked.Contains(((CarvedFile)item.Tag!).Path);
        _grid.EndUpdate();
        _suppressCheck = false;
        UpdateFooter();
    }

    /// <summary>Files to keep: those passing the current filters and still ticked.</summary>
    private List<CarvedFile> KeepList() => _matching.Where(f => !_unchecked.Contains(f.Path)).ToList();

    private void UpdateFooter()
    {
        var keep = KeepList();
        bool any = _staged.Count > 0 && !_scanning;
        _keep.Enabled = any && keep.Count > 0;
        _discard.Enabled = any;
        _keep.Text = keep.Count == 0 ? "Keep selected…" : $"Keep {Plural(keep.Count, "file")} ({ByteSize.Format(keep.Sum(f => f.Length))})…";
        if (_scanning)
        {
            _footer.Status.Text = "Scanning… results appear here when the scan ends (Stop keeps what's found so far).";
            return;
        }
        if (_staged.Count == 0)
        {
            _footer.Status.Text = "No staged files.";
            return;
        }
        string status = $"{Plural(_staged.Count, "file")} staged · {ByteSize.Format(_staged.Sum(f => f.Length))}";
        if (_matching.Count != _staged.Count)
            status += $" · {_matching.Count:N0} match filters";
        if (_matching.Count > MaxShown)
            status += $" (first {MaxShown:N0} shown)";
        _footer.Status.Text = status;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => { if (SelectedFile is { } f) OpenFile(f.Path); });
        menu.Items.Add("Show in folder", null, (_, _) => { if (SelectedFile is { } f) ShowInExplorer(f.Path); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Tick selected", null, (_, _) => SetChecked(SelectedFiles(), true));
        menu.Items.Add("Untick selected", null, (_, _) => SetChecked(SelectedFiles(), false));
        menu.Opening += (_, e) => e.Cancel = _grid.SelectedItems.Count == 0;
        return menu;

        List<CarvedFile> SelectedFiles() => _grid.SelectedItems.Cast<ListViewItem>().Select(i => (CarvedFile)i.Tag!).ToList();
    }

    // ---- keep / discard -------------------------------------------------------------------

    private async Task KeepSelectedAsync()
    {
        if (_destDir is null || _stagingDir is null)
            return;
        var keep = KeepList();
        int dropped = _staged.Count - keep.Count;
        if (!Confirm("Keep selected",
                $"Move {Plural(keep.Count, "file")} ({ByteSize.Format(keep.Sum(f => f.Length))}) into {_destDir}" +
                (dropped > 0 ? $"\nand discard the other {Plural(dropped, "staged file")}?" : "?")))
            return;

        string dest = _destDir;
        bool byType = _sortByType.Checked;
        // Nothing may touch the staging folder while files move out of it.
        _keeping = true;
        _keep.Enabled = _discard.Enabled = _start.Enabled = false;
        Busy = true;
        _footer.ShowProgress(0);

        var errors = await Task.Run(() =>
        {
            var errs = new List<string>();
            for (int i = 0; i < keep.Count; i++)
            {
                var f = keep[i];
                try
                {
                    string folder = byType ? Path.Combine(dest, MediaExtensions.TypeFolder(f.Extension)) : dest;
                    Directory.CreateDirectory(folder);
                    File.Move(f.Path, FileNaming.MakeUnique(Path.Combine(folder, Path.GetFileName(f.Path))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errs.Add($"• {Path.GetFileName(f.Path)}: {ex.Message}");
                }
                if (i % 25 == 0)
                {
                    int done = i;
                    RunOnUi(() => _footer.Progress.Value = (double)done / keep.Count);
                }
            }
            return errs;
        });

        _keeping = false;
        _start.Enabled = true;
        Busy = false;
        _footer.HideProgress();
        int moved = keep.Count - errors.Count;
        if (errors.Count == 0)
            DiscardAll(confirm: false);
        else
        {
            // Keep the review consistent: drop what was moved, leave the failures staged.
            var movedPaths = keep.Select(f => f.Path).Where(p => !File.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _staged = _staged.Where(f => !movedPaths.Contains(f.Path)).ToList();
            BuildTypeFilters();
            RefreshGrid();
        }
        _footer.Status.Text = $"Kept {Plural(moved, "file")} in {dest}" + (dropped > 0 && errors.Count == 0 ? $", discarded {dropped:N0}." : ".");
        _progressMain.Text = $"Kept {Plural(moved, "file")}";
        if (errors.Count > 0)
            ShowError("Some files could not be kept", string.Join("\n", errors.Take(10)));
        else if (moved > 0)
            ShowInExplorer(dest);
    }

    private void DiscardAll(bool confirm)
    {
        if (_stagingDir is not null && Directory.Exists(_stagingDir))
        {
            if (confirm && !Confirm("Discard all", $"Delete all {Plural(_staged.Count, "staged file")} without keeping any?", defaultNo: true))
                return;
            try
            {
                Directory.Delete(_stagingDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a leftover staging folder is harmless
            }
        }

        _stagingDir = null;
        _staged = [];
        _matching = [];
        _unchecked.Clear();
        _grid.Items.Clear();
        _grid.Groups.Clear();
        _thumbs.Reset();
        _typeFilters.Controls.ClearAndDispose();
        _filterRow.Visible = false;
        ShowIdleEmptyState();
        UpdateFooter();
    }

    public override bool ConfirmAppClose()
    {
        _discardOnClose = false;
        if (_keeping)
        {
            ShowInfo("Still keeping files", "Found files are being moved to the destination. Close the app when that finishes.");
            return false;
        }
        if (_scanning)
        {
            if (!Confirm("Scan in progress", "A deep scan is still running. Stop it and discard what it has found so far?", defaultNo: true))
                return false;
            _discardOnClose = true;
            return true;
        }
        if (_stagingDir is null || !Directory.Exists(_stagingDir) || _staged.Count == 0)
            return true;
        var answer = MessageBox.Show(this,
            $"{Plural(_staged.Count, "found file")} haven't been kept.\n\nYes — discard them\nNo — leave them in {_stagingDir}\nCancel — go back",
            "Staged files remain", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel)
            return false;
        _discardOnClose = answer == DialogResult.Yes;
        return true;
    }

    public override void OnAppClosing()
    {
        _pauseGate?.Set();
        _scanCts?.Cancel();
        if (!_discardOnClose || _stagingDir is null)
            return;
        // A cancelled scan stops within a chunk; let it close its output file first.
        try { _scanTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        try
        {
            if (Directory.Exists(_stagingDir))
                Directory.Delete(_stagingDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanCts?.Cancel();
            _thumbs.Dispose();
        }
        base.Dispose(disposing);
    }
}
