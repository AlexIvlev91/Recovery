using System.Collections.Concurrent;
using Recovery.Core;
using Recovery.Core.Mft;
using Recovery.Gui.Controls;
using Entry = Recovery.Core.Mft.MftUndeleteService.Entry;
using DataCheck = Recovery.Core.Mft.MftUndeleteService.DataCheck;

namespace Recovery.Gui.Pages;

/// <summary>
/// Tier 3: deleted photos/videos from the NTFS MFT, with their ORIGINAL names
/// and folders. After listing, each file's first cluster is checked so the
/// user can see which ones still hold their data before recovering.
/// </summary>
internal sealed class UndeletePage : PageBase
{
    private const long PreviewLimit = 64L << 20;

    private readonly ComboBox _drive;
    private readonly ModernButton _scan, _stop, _recover;
    private readonly TextBox _search;
    private readonly SegmentedControl _kind;
    private readonly ComboBox _minSize;
    private readonly CheckBox _hideOverwritten;
    private readonly VirtualTable<Entry> _table;
    private readonly EmptyState _empty;
    private readonly PreviewPanel _preview;
    private readonly FooterBar _footer;
    private readonly CheckBox _byType;
    private readonly System.Windows.Forms.Timer _drain = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 250 };

    private IReadOnlyList<Entry> _entries = [];
    private List<Entry> _shown = [];
    private readonly HashSet<Entry> _checked = [];
    private readonly Dictionary<Entry, DataCheck> _checks = [];
    private readonly ConcurrentQueue<(int Generation, Entry Entry, DataCheck Check)> _pendingChecks = new();
    private CancellationTokenSource? _cts;      // scan / recover
    private CancellationTokenSource? _checkCts; // background data check
    private int _checkGeneration;
    private Entry? _previewEntry;
    private readonly System.Windows.Forms.Timer _previewDebounce = new() { Interval = 120 };
    private bool _checking;
    private bool _working;
    private char _scannedDrive;

    private static readonly (string Label, long Bytes)[] SizeChoices =
    [
        ("Any size", 0), ("≥ 10 KB", 10L << 10), ("≥ 100 KB", 100L << 10), ("≥ 500 KB", 500L << 10),
        ("≥ 1 MB", 1L << 20), ("≥ 5 MB", 5L << 20), ("≥ 50 MB", 50L << 20), ("≥ 500 MB", 500L << 20),
    ];

    public UndeletePage()
        : base("undelete", "Undelete by name",
            "Finds deleted photos and videos in the NTFS file table, with their original names and folders. " +
            "Best right after deletion — before the space gets reused.",
            Glyph.Restore, needsAdmin: true)
    {
        // ---- scan bar -----------------------------------------------------------
        _drive = Ui.Combo(300);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
                    !string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    continue;
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removable" : "Local disk") : d.VolumeLabel;
                _drive.Items.Add(new DriveChoice(d.Name[0], $"{d.Name[0]}:  {label} · {ByteSize.Format(d.TotalSize)}"));
            }
            catch (IOException) { }
        }
        if (_drive.Items.Count > 0)
            _drive.SelectedIndex = 0;

        _scan = new ModernButton("Scan for deleted files", ButtonKind.Primary, Glyph.Search);
        _scan.Click += (_, _) => _ = ScanAsync();
        _stop = new ModernButton("Stop", ButtonKind.Secondary, Glyph.Stop) { Visible = false };
        _stop.Click += (_, _) => _cts?.Cancel();

        var scanCard = new Card { Dock = DockStyle.Top, Height = Theme.S(58), Padding = Theme.S(14, 12, 14, 10) };
        var scanRow = Ui.Row(Ui.FieldLabel("Drive"), _drive, _scan, _stop);
        scanRow.Dock = DockStyle.Left;
        scanCard.Controls.Add(scanRow);

        // ---- filters --------------------------------------------------------------
        _search = Ui.SearchBox("Search name or folder… e.g. Pictures", 240);
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        _kind = new SegmentedControl().Add("All").Add("Photos", Glyph.Photo).Add("Videos", Glyph.Video);
        _kind.SelectedIndexChanged += (_, _) => ApplyFilter();
        _minSize = Ui.Combo(110, SizeChoices.Select(c => c.Label).ToArray());
        _minSize.SelectedIndexChanged += (_, _) => ApplyFilter();
        _hideOverwritten = Ui.Check("Hide overwritten");
        _hideOverwritten.Tip("Hide files whose data has already been reused or zeroed — recovering them gives unusable files.");
        _hideOverwritten.CheckedChanged += (_, _) => ApplyFilter();
        var filterRow = Ui.Row(_search, _kind, Ui.FieldLabel("Size"), _minSize, _hideOverwritten);
        filterRow.Dock = DockStyle.Top;

        // ---- results ----------------------------------------------------------------
        _table = new VirtualTable<Entry> { Dock = DockStyle.Fill };
        _table.AddColumn("Name", 230, e => e.OriginalName, (a, b) => string.Compare(a.OriginalName, b.OriginalName, StringComparison.CurrentCultureIgnoreCase))
              .AddColumn("Original folder", 300, e => e.FolderPath, (a, b) => string.Compare(a.FolderPath, b.FolderPath, StringComparison.CurrentCultureIgnoreCase))
              .AddColumn("Size", 85, e => ByteSize.Format(e.SizeBytes), (a, b) => a.SizeBytes.CompareTo(b.SizeBytes), HorizontalAlignment.Right)
              .AddColumn("Data", 150, e => CheckLabel(e), (a, b) => CheckRank(b).CompareTo(CheckRank(a)))
              .AddColumn("Type", 60, e => Path.GetExtension(e.OriginalName).TrimStart('.').ToUpperInvariant(),
                  (a, b) => string.Compare(Path.GetExtension(a.OriginalName), Path.GetExtension(b.OriginalName), StringComparison.OrdinalIgnoreCase));
        _table.EnableCheckboxes();
        _table.IsChecked = e => _checked.Contains(e);
        _table.SetChecked = (e, v) => { if (v) _checked.Add(e); else _checked.Remove(e); };
        _table.CanCheck = e => e.DataAvailable;
        _table.RowColor = e => _checks.TryGetValue(e, out var c) && c is DataCheck.Overwritten or DataCheck.Zeroed or DataCheck.NotRecoverable or DataCheck.Unreadable
            ? Theme.TextSubtle : null;
        _table.CheckedChanged += (_, _) => UpdateSelectionUi();
        // Debounced: arrow-key scrolling or selecting many rows mustn't start a disk read per row.
        _table.SelectedIndexChanged += (_, _) => { _previewDebounce.Stop(); _previewDebounce.Start(); };
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); UpdatePreview(); };
        _table.ContextMenuStrip = BuildContextMenu();
        _table.SortBy(3, SortOrder.Descending); // intact first

        _empty = new EmptyState { Covers = _table, Dock = DockStyle.Fill };
        var tableCard = new Card { Dock = DockStyle.Fill, Padding = Theme.S(1, 6, 1, 1) };
        tableCard.Controls.Add(_table);
        tableCard.Controls.Add(_empty);

        _preview = new PreviewPanel { Dock = DockStyle.Right, Width = Theme.S(320) };

        // ---- footer ------------------------------------------------------------------
        _footer = new FooterBar();
        var selectAll = new ModernButton("Select all", ButtonKind.Ghost, Glyph.SelectAll);
        selectAll.Click += (_, _) => SetCheckedRows(_shown.Where(e => e.DataAvailable), true);
        var selectIntact = new ModernButton("Select intact", ButtonKind.Ghost, Glyph.Check)
            .Tip("Tick every shown file whose data still looks like the original.");
        selectIntact.Click += (_, _) => SetCheckedRows(_shown.Where(e => _checks.TryGetValue(e, out var c) && c == DataCheck.LooksIntact), true);
        var clear = new ModernButton("Clear", ButtonKind.Ghost, Glyph.ClearSelection);
        clear.Click += (_, _) => SetCheckedRows(_shown, false);
        _byType = Ui.Check("Sort into type folders");
        _recover = new ModernButton("Recover selected…", ButtonKind.Primary, Glyph.Download) { Margin = Padding.Empty };
        _recover.Click += (_, _) => _ = RecoverAsync();
        _footer.Actions.Controls.AddRange([selectAll, selectIntact, clear, _byType, _recover]);

        Body.Controls.Add(tableCard);
        Body.Controls.Add(Spacer(12, DockStyle.Right));
        Body.Controls.Add(_preview);
        Body.Controls.Add(Spacer(12, DockStyle.Bottom));
        Body.Controls.Add(_footer);
        Body.Controls.Add(Spacer(10));
        Body.Controls.Add(filterRow);
        Body.Controls.Add(Spacer(12));
        Body.Controls.Add(scanCard);

        _drain.Tick += (_, _) => DrainChecks();

        _empty.Show(Glyph.Restore, "Scan a drive to list deleted photos and videos",
            _drive.Items.Count == 0
                ? "No NTFS drive is available. This method needs an NTFS-formatted drive; use Deep scan for others (FAT32/exFAT cards)."
                : "Reading the file table takes a few seconds to a minute. Nothing is written to the drive.");
        UpdateSelectionUi();
    }

    private sealed record DriveChoice(char Letter, string Text)
    {
        public override string ToString() => Text;
    }

    public override bool ConfirmAppClose() =>
        !_working || Confirm("Work in progress", "Undelete is still scanning or recovering. Stop it and close?", defaultNo: true);

    public override void OnAppClosing()
    {
        _cts?.Cancel();
        _checkCts?.Cancel();
    }

    // ---- scanning -------------------------------------------------------------------

    private async Task ScanAsync()
    {
        if (_working || _drive.SelectedItem is not DriveChoice choice)
            return;
        if (!Elevation.IsAdministrator)
        {
            if (Confirm("Administrator required",
                    "Reading the drive's file table needs administrator rights.\n\nRestart the app as administrator now?"))
                RequestElevation();
            return;
        }

        char drive = choice.Letter;
        _cts?.Cancel();
        _checkCts?.Cancel();
        _checkGeneration++;
        _checking = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetWorking(true, "Reading the file table…");
        _footer.ShowProgress(0);
        _entries = [];
        _checks.Clear();
        _checked.Clear();
        _pendingChecks.Clear();
        ApplyFilter();

        try
        {
            var service = new MftUndeleteService();
            _entries = await Task.Run(() => service.Scan(drive, null, (done, total) => RunOnUi(() =>
            {
                _footer.Progress.Value = total == 0 ? 0 : (double)done / total;
                _footer.Status.Text = $"Reading the file table of {drive}: … {done * 100.0 / Math.Max(1, total):0}%";
            }), ct), ct);
            _scannedDrive = drive;
        }
        catch (OperationCanceledException)
        {
            SetWorking(false, "Scan stopped.");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            SetWorking(false, "Access denied — administrator rights are required.");
            return;
        }
        catch (Exception ex)
        {
            SetWorking(false, "Scan failed.");
            ShowError("Undelete scan failed", ex.Message);
            return;
        }

        SetWorking(false, null);
        ApplyFilter();
        StartDataCheck(drive);
    }

    /// <summary>
    /// Check the first cluster of every not-yet-checked file in the background;
    /// results stream into the table. Paused while recovering, resumed after.
    /// </summary>
    private void StartDataCheck(char drive)
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        int generation = ++_checkGeneration;
        var entries = _entries.Where(e => !_checks.ContainsKey(e)).ToList();
        if (entries.Count == 0)
            return;
        _checking = true;
        Busy = true;
        _drain.Start();
        // No token on Task.Run: a pre-cancelled token would skip the body and leave the page "busy".
        Task.Run(() =>
        {
            try
            {
                new MftUndeleteService().CheckData(drive, entries, (e, c) => _pendingChecks.Enqueue((generation, e, c)), cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
            }
            RunOnUi(() =>
            {
                if (generation != _checkGeneration)
                    return; // superseded by a newer scan/check
                _checking = false;
                Busy = _working;
                DrainChecks();
                _drain.Stop();
            });
        });
    }

    private void DrainChecks()
    {
        bool any = false, selectedChanged = false;
        var selected = _table.SelectedRow;
        while (_pendingChecks.TryDequeue(out var item))
        {
            if (item.Generation != _checkGeneration)
                continue; // result of an older, cancelled check
            _checks[item.Entry] = item.Check;
            any = true;
            selectedChanged |= ReferenceEquals(item.Entry, selected);
        }
        if (!any && _checking)
            return;
        if (_hideOverwritten.Checked)
            ApplyFilter();
        else
            _table.RefreshRows();
        UpdateSelectionUi();
        if (selectedChanged)
            UpdatePreviewDetails();
    }

    private void SetWorking(bool working, string? status)
    {
        _working = working;
        Busy = working || _checking;
        _scan.Enabled = !working;
        _drive.Enabled = !working;
        _stop.Visible = working;
        _recover.Enabled = !working && SelectedForRecovery().Count > 0;
        if (!working)
            _footer.HideProgress();
        if (status is not null)
            _footer.Status.Text = status;
        else
            UpdateSelectionUi();
    }

    // ---- filtering -------------------------------------------------------------------

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        long min = SizeChoices[Math.Max(0, _minSize.SelectedIndex)].Bytes;
        bool hideBad = _hideOverwritten.Checked;
        _shown = _entries.Where(e =>
                e.SizeBytes >= min &&
                (_kind.SelectedIndex == 0 || (_kind.SelectedIndex == 1 ? e.Kind == MediaKind.Photo : e.Kind == MediaKind.Video)) &&
                (q.Length == 0 || e.FullPath.Contains(q, StringComparison.OrdinalIgnoreCase)) &&
                (!hideBad || !_checks.TryGetValue(e, out var c) || c is not (DataCheck.Overwritten or DataCheck.Zeroed or DataCheck.NotRecoverable)))
            .ToList();
        _table.SetRows(_shown);

        if (_entries.Count == 0)
        {
            if (!_working && _scannedDrive != default)
                _empty.Show(Glyph.Restore, "No deleted photos or videos found",
                    "The file table has no deleted media entries left — they may have been reused already. " +
                    "Deep scan searches the free space itself and can still find files without names.",
                    ("Try Deep scan", ButtonKind.Primary, () => NavigateTo("carve")));
        }
        else if (_shown.Count == 0)
        {
            _empty.Show(Glyph.Filter, "Nothing matches your filters", "Clear the search box, pick \"All\" or lower the size limit.");
        }
        else
        {
            _empty.Visible = false;
        }
        UpdateSelectionUi();
    }

    private List<Entry> SelectedForRecovery() => _shown.Where(e => e.DataAvailable && _checked.Contains(e)).ToList();

    private void SetCheckedRows(IEnumerable<Entry> rows, bool value)
    {
        foreach (var e in rows)
        {
            if (value) _checked.Add(e); else _checked.Remove(e);
        }
        _table.RefreshRows();
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        var sel = SelectedForRecovery();
        _recover.Enabled = !_working && sel.Count > 0;
        _recover.Text = sel.Count == 0 ? "Recover selected…" : $"Recover {Plural(sel.Count, "file")} ({ByteSize.Format(sel.Sum(e => e.SizeBytes))})…";
        if (_working)
            return;
        if (_entries.Count == 0)
        {
            _footer.Status.Text = _scannedDrive == default ? "Pick a drive and scan." : "No deleted photos or videos found.";
            return;
        }
        int intact = _checks.Values.Count(c => c == DataCheck.LooksIntact);
        string status = _shown.Count == _entries.Count
            ? $"{Plural(_entries.Count, "deleted photo or video", "deleted photos and videos")}"
            : $"Showing {_shown.Count:N0} of {_entries.Count:N0}";
        status += _checking
            ? $" · checking data {_checks.Count * 100 / Math.Max(1, _entries.Count)}%… ({intact:N0} look intact so far)"
            : $" · {intact:N0} look intact";
        _footer.Status.Text = status;
    }

    private string CheckLabelFor(DataCheck? c, bool available) => c switch
    {
        DataCheck.LooksIntact => "✓ Looks intact",
        DataCheck.Overwritten => "✗ Overwritten",
        DataCheck.Zeroed => "✗ Wiped (zeros)",
        DataCheck.Unknown => "? Can't tell",
        DataCheck.Unreadable => "✗ Unreadable",
        DataCheck.NotRecoverable => "✗ Compressed/encrypted",
        _ => !available ? "✗ Compressed/encrypted" : _checking ? "Checking…" : "Not checked",
    };

    private string CheckLabel(Entry e) => CheckLabelFor(_checks.TryGetValue(e, out var c) ? c : null, e.DataAvailable);

    private int CheckRank(Entry e) => _checks.TryGetValue(e, out var c)
        ? c switch { DataCheck.LooksIntact => 5, DataCheck.Unknown => 4, DataCheck.Unreadable => 2, DataCheck.Overwritten => 1, _ => 0 }
        : 3;

    // ---- preview ------------------------------------------------------------------------

    private void UpdatePreview()
    {
        var current = _table.SelectedRow;
        if (current is not null && ReferenceEquals(current, _previewEntry))
        {
            UpdatePreviewDetails(); // same file: refresh the text, don't re-read the disk
            return;
        }
        _previewEntry = current;
        if (current is not { } e)
        {
            _preview.ShowEmpty(_entries.Count == 0 ? "" : "Select a file to check it.\nPhotos are previewed straight from the disk — nothing is written.");
            return;
        }
        UpdatePreviewDetails();
        if (!e.DataAvailable)
        {
            _preview.ShowEmpty("Compressed or encrypted on disk — this tool can't reconstruct it.");
            UpdatePreviewDetails();
        }
        else if (e.Kind != MediaKind.Photo)
        {
            _preview.LoadImage(() => null, failMessage: "Videos can't be previewed before recovery.\nRecover it, then open it to check.");
        }
        else if (e.SizeBytes > PreviewLimit)
        {
            _preview.LoadImage(() => null, failMessage: "Too large to preview in memory — recover it to check.");
        }
        else
        {
            var entry = e;
            _preview.LoadImage(() =>
            {
                var data = new MftUndeleteService().ReadForPreview(entry, PreviewLimit);
                return data is null ? null : ImageLoader.FromBytes(data);
            }, "Reading from disk…", "Couldn't decode this photo — its data was most likely overwritten.");
        }
    }

    private void UpdatePreviewDetails()
    {
        if (_table.SelectedRow is not { } e)
            return;
        _preview.ShowItem(e.OriginalName,
        [
            ("Size", ByteSize.Format(e.SizeBytes)),
            ("Folder", e.FolderPath),
            ("Data", CheckLabel(e)),
        ]);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Tick selected", null, (_, _) => SetCheckedRows(_table.SelectedRows.Where(r => r.DataAvailable), true));
        menu.Items.Add("Untick selected", null, (_, _) => SetCheckedRows(_table.SelectedRows, false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy original path", null, (_, _) => { if (_table.SelectedRow is { } e) Clipboard.SetText(e.FullPath); });
        menu.Opening += (_, ev) => ev.Cancel = _table.SelectedRow is null;
        return menu;
    }

    // ---- recover --------------------------------------------------------------------------

    private async Task RecoverAsync()
    {
        var chosen = SelectedForRecovery();
        if (_working || chosen.Count == 0)
            return;

        int bad = chosen.Count(e => _checks.GetValueOrDefault(e, DataCheck.Unknown) is DataCheck.Overwritten or DataCheck.Zeroed or DataCheck.Unreadable);
        if (bad > 0 && !Confirm("Some files look overwritten",
                $"{Plural(bad, "selected file")} {(bad == 1 ? "looks" : "look")} overwritten or wiped. " +
                "They'll be recovered, but probably won't open.\n\nContinue?"))
            return;

        char drive = _scannedDrive;
        string? dest = PickFolder($"Choose where to save {Plural(chosen.Count, "recovered file")} — ideally on ANOTHER drive than {drive}:",
            AppSettings.Get("undelete.dest", AppSettings.DefaultFolder("Undeleted")));
        if (dest is null || !ConfirmDestinationDrive(dest, drive))
            return;
        AppSettings.Set("undelete.dest", dest);

        _checkCts?.Cancel(); // pause the background data check — it competes for the disk
        _checkGeneration++;
        _checking = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        bool byType = _byType.Checked;
        SetWorking(true, $"Recovering {Plural(chosen.Count, "file")}…");
        _footer.ShowProgress(0);

        IReadOnlyList<MftUndeleteService.RecoverResult> results;
        try
        {
            results = await Task.Run(() => new MftUndeleteService().Recover(drive, chosen, dest, byType,
                (done, total) => RunOnUi(() =>
                {
                    _footer.Progress.Value = total == 0 ? 1 : (double)done / total;
                    _footer.Status.Text = $"Recovering {Math.Min(done + 1, total):N0} of {total:N0}…";
                }), ct));
        }
        catch (Exception ex)
        {
            SetWorking(false, "Recovery failed.");
            ShowError("Recovery failed", ex.Message);
            return;
        }

        int ok = results.Count(r => r.Success);
        int patched = results.Count(r => r.Success && r.Error is not null);
        var failed = results.Where(r => !r.Success).ToList();
        SetWorking(false, $"Recovered {ok:N0} of {chosen.Count:N0} to {dest}" +
                          (patched > 0 ? $" · {patched:N0} had unreadable parts (zero-filled)" : "") +
                          (ct.IsCancellationRequested ? " · stopped early" : "") + ".");
        if (failed.Count > 0)
            ShowError("Some files could not be recovered",
                string.Join("\n", failed.Take(10).Select(f => $"• {f.Entry.OriginalName}: {f.Error}")));
        if (ok > 0)
            ShowInExplorer(dest);
        StartDataCheck(drive); // resume checking whatever wasn't checked yet
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _checkCts?.Cancel();
            _drain.Dispose();
            _previewDebounce.Dispose();
            _searchDebounce.Dispose();
        }
        base.Dispose(disposing);
    }
}
