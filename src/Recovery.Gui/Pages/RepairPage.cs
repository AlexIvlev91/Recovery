using Recovery.Core;
using Recovery.Core.RecycleBin;
using Recovery.Core.Repair;
using Recovery.Gui.Controls;

namespace Recovery.Gui.Pages;

/// <summary>
/// Diagnoses media files (a folder or the Recycle Bin) by content and writes
/// repaired copies of the fixable ones — originals are never modified. Also
/// hosts the "fix file extensions" and donor-based video rebuild tools.
/// </summary>
internal sealed class RepairPage : PageBase
{
    private sealed record Row(string Name, string Path, string Folder, ValidationReport Report);

    private static readonly FileHealth?[] HealthFilters = [null, FileHealth.Repairable, FileHealth.Partial, FileHealth.Broken, FileHealth.Unsupported, FileHealth.Ok];

    private readonly SegmentedControl _source;
    private readonly TextBox _dir;
    private readonly ModernButton _browse, _check, _stop, _repair, _fixNames, _rebuild;
    private readonly CheckBox _recurse;
    private readonly SegmentedControl _health;
    private readonly VirtualTable<Row> _table;
    private readonly EmptyState _empty;
    private readonly FooterBar _footer;

    private List<Row> _rows = [];
    private CancellationTokenSource? _cts;
    private bool _working;

    public RepairPage()
        : base("repair", "Check & repair",
            "Recovered files are often damaged. Check them by their content, then save repaired copies — originals are never changed.",
            Glyph.Repair)
    {
        _fixNames = new ModernButton("Fix file extensions…", ButtonKind.Secondary, Glyph.Page)
            .Tip("Renames files whose extension doesn't match their real content (e.g. an AVIF photo carved as .mp4).");
        _fixNames.Click += (_, _) => _ = FixFileTypesAsync();
        _rebuild = new ModernButton("Rebuild video…", ButtonKind.Secondary, Glyph.Video)
            .Tip("Rebuilds a video whose index (moov) is missing, using a healthy 'donor' video from the same camera/app.");
        _rebuild.Click += (_, _) => _ = RebuildVideoAsync();
        _rebuild.Margin = Padding.Empty;
        HeaderActions.Controls.AddRange([_fixNames, _rebuild]);

        // ---- source bar --------------------------------------------------------------------
        _source = new SegmentedControl().Add("Folder", Glyph.Folder).Add("Recycle Bin", Glyph.RecycleBin);
        _dir = new TextBox { Width = Theme.S(320), Margin = Theme.S(0, 5, 6, 0), Text = AppSettings.Get("repair.dir", ""), PlaceholderText = "Folder with recovered files…" };
        _browse = new ModernButton("", ButtonKind.Secondary, Glyph.FolderOpen) { IconOnly = true }.Tip("Browse…");
        _browse.Click += (_, _) => { if (PickFolder("Choose the folder of files to check", _dir.Text) is { } p) _dir.Text = p; };
        _recurse = Ui.Check("Include subfolders", true);
        _source.SelectedIndexChanged += (_, _) => _dir.Enabled = _browse.Enabled = _recurse.Enabled = _source.SelectedIndex == 0;
        _check = new ModernButton("Check files", ButtonKind.Primary, Glyph.Health);
        _check.Click += (_, _) => _ = CheckAsync();
        _stop = new ModernButton("Stop", ButtonKind.Secondary, Glyph.Stop) { Visible = false };
        _stop.Click += (_, _) => _cts?.Cancel();

        var sourceCard = new Card { Dock = DockStyle.Top, Height = Theme.S(58), Padding = Theme.S(14, 12, 14, 10) };
        var sourceRow = Ui.Row(_source, _dir, _browse, _recurse, _check, _stop);
        sourceRow.Dock = DockStyle.Left;
        sourceCard.Controls.Add(sourceRow);

        // ---- results ------------------------------------------------------------------------
        _health = new SegmentedControl();
        foreach (var h in HealthFilters)
            _health.Add(HealthFilterLabel(h, 0));
        _health.SelectedIndexChanged += (_, _) => ApplyFilter();
        var healthRow = Ui.Row(_health);
        healthRow.Dock = DockStyle.Top;

        _table = new VirtualTable<Row> { Dock = DockStyle.Fill };
        _table.AddColumn("File", 240, r => r.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase))
              .AddColumn("Health", 110, r => HealthLabel(r.Report.Health), (a, b) => SortRank(a.Report.Health).CompareTo(SortRank(b.Report.Health)))
              .AddColumn("Details", 420, r => r.Report.Detail)
              .AddColumn("Folder", 260, r => r.Folder, (a, b) => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase));
        _table.RowColor = r => HealthColor(r.Report.Health);
        _table.SortBy(1, SortOrder.Ascending);
        _table.RowActivated += (_, r) => OpenFile(r.Path);
        _table.ContextMenuStrip = BuildContextMenu();

        _empty = new EmptyState { Covers = _table, Dock = DockStyle.Fill };
        var tableCard = new Card { Dock = DockStyle.Fill, Padding = Theme.S(1, 6, 1, 1) };
        tableCard.Controls.Add(_table);
        tableCard.Controls.Add(_empty);

        // ---- footer ---------------------------------------------------------------------------
        _footer = new FooterBar();
        _repair = new ModernButton("Repair fixable files…", ButtonKind.Primary, Glyph.Repair) { Enabled = false, Margin = Padding.Empty };
        _repair.Click += (_, _) => _ = RepairAsync(_rows.Where(r => r.Report.Health is FileHealth.Repairable or FileHealth.Partial).ToList());
        _footer.Actions.Controls.Add(_repair);
        _footer.Status.Text = "Pick a folder of recovered files (or the Recycle Bin) and press Check files.";

        Body.Controls.Add(tableCard);
        Body.Controls.Add(Spacer(12, DockStyle.Bottom));
        Body.Controls.Add(_footer);
        Body.Controls.Add(Spacer(10));
        Body.Controls.Add(healthRow);
        Body.Controls.Add(Spacer(12));
        Body.Controls.Add(sourceCard);

        _empty.Show(Glyph.Health, "Check recovered photos and videos",
            "Each file is inspected by its content: OK, repairable (fully fixable), partial (opens, but part is missing), " +
            "broken, or not actually media. Nothing is changed until you choose to save repaired copies.");
    }

    public override void OnAppClosing() => _cts?.Cancel();

    private static string HealthLabel(FileHealth h) => h switch
    {
        FileHealth.Ok => "✓ OK",
        FileHealth.Repairable => "Repairable",
        FileHealth.Partial => "Partial",
        FileHealth.Broken => "✗ Broken",
        _ => "Not recognized",
    };

    private static string HealthFilterLabel(FileHealth? h, int count) => (h switch
    {
        null => "All",
        FileHealth.Ok => "OK",
        FileHealth.Unsupported => "Not recognized",
        { } x => x.ToString(),
    }) + (count > 0 ? $"  {count:N0}" : "");

    private static Color? HealthColor(FileHealth h) => h switch
    {
        FileHealth.Ok => Theme.Success,
        FileHealth.Repairable => Theme.Accent,
        FileHealth.Partial => Theme.Warning,
        FileHealth.Broken => Theme.Danger,
        _ => Theme.TextMuted,
    };

    private static int SortRank(FileHealth h) => h switch
    {
        FileHealth.Repairable => 0,
        FileHealth.Partial => 1,
        FileHealth.Broken => 2,
        FileHealth.Unsupported => 3,
        _ => 4,
    };

    // ---- checking -------------------------------------------------------------------------------

    private async Task CheckAsync()
    {
        if (_working)
            return;
        bool fromBin = _source.SelectedIndex == 1;
        string dir = _dir.Text.Trim();
        bool recurse = _recurse.Checked;
        if (!fromBin && !Directory.Exists(dir))
        {
            ShowInfo("Choose a folder", "Pick an existing folder of recovered files first.");
            return;
        }
        if (!fromBin)
            AppSettings.Set("repair.dir", dir);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetWorking(true);
        _rows = [];
        ApplyFilter();
        _footer.Status.Text = "Finding files…";
        _footer.ShowProgress(null);

        try
        {
            _rows = await Task.Run(() =>
            {
                var targets = new List<(string Name, string Path, string Folder)>();
                if (fromBin)
                {
                    foreach (var item in new RecycleBinScanner().Scan())
                        targets.Add((item.OriginalName, item.BackingPath, Path.GetDirectoryName(item.OriginalPath) ?? ""));
                }
                else
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = 0 };
                    foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (MediaExtensions.IsMedia(file))
                            targets.Add((Path.GetFileName(file), file, Path.GetDirectoryName(file) ?? ""));
                    }
                }

                var service = new RepairService();
                var rows = new List<Row>(targets.Count);
                long lastUi = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < targets.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = targets[i];
                    ValidationReport report;
                    try
                    {
                        report = service.Check(t.Path);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        report = new ValidationReport(t.Path, FileHealth.Broken, $"check failed: {ex.Message}");
                    }
                    rows.Add(new Row(t.Name, t.Path, t.Folder, report));
                    if (clock.ElapsedMilliseconds - lastUi > 150)
                    {
                        lastUi = clock.ElapsedMilliseconds;
                        int done = i + 1, total = targets.Count;
                        RunOnUi(() =>
                        {
                            _footer.ShowProgress((double)done / total);
                            _footer.Status.Text = $"Checking {done:N0} of {total:N0}…";
                        });
                    }
                }
                return rows;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            _footer.Status.Text = "Check stopped.";
        }
        catch (Exception ex)
        {
            _footer.Status.Text = "Check failed.";
            ShowError("Check failed", ex.Message);
        }
        finally
        {
            SetWorking(false);
        }

        ApplyFilter();
        if (!ct.IsCancellationRequested)
            UpdateStatus();
    }

    private void SetWorking(bool working)
    {
        _working = working;
        Busy = working;
        _check.Enabled = _source.Enabled = !working;
        _dir.Enabled = _browse.Enabled = _recurse.Enabled = !working && _source.SelectedIndex == 0;
        _fixNames.Enabled = _rebuild.Enabled = !working;
        _stop.Visible = working;
        int fixable = _rows.Count(r => r.Report.Health is FileHealth.Repairable or FileHealth.Partial);
        _repair.Enabled = !working && fixable > 0;
        _repair.Text = fixable == 0 ? "Repair fixable files…" : $"Repair {Plural(fixable, "fixable file")}…";
        if (!working)
            _footer.HideProgress();
    }

    private void ApplyFilter()
    {
        for (int i = 0; i < HealthFilters.Length; i++)
        {
            var h = HealthFilters[i];
            _health.SetText(i, HealthFilterLabel(h, h is null ? _rows.Count : _rows.Count(r => r.Report.Health == h)));
        }
        var filter = HealthFilters[_health.SelectedIndex];
        var shown = filter is null ? _rows : _rows.Where(r => r.Report.Health == filter).ToList();
        _table.SetRows(shown);
        if (_rows.Count > 0)
        {
            if (shown.Count == 0)
                _empty.Show(Glyph.Filter, "No files in this group", "Pick another health filter above.");
            else
                _empty.Visible = false;
        }
        else if (!_working && _footer.Status.Text.StartsWith("Checking", StringComparison.Ordinal))
        {
            _empty.Show(Glyph.Health, "No photos or videos found", "The folder has no media files with a known extension.");
        }
    }

    private void UpdateStatus()
    {
        if (_rows.Count == 0)
        {
            _footer.Status.Text = "No photos or videos found to check.";
            _empty.Show(Glyph.Health, "No photos or videos found", "The chosen source has no media files with a known extension.");
            return;
        }
        int Count(FileHealth h) => _rows.Count(r => r.Report.Health == h);
        _footer.Status.Text = $"{Plural(_rows.Count, "file")} checked — {Count(FileHealth.Ok):N0} OK, {Count(FileHealth.Repairable):N0} repairable, " +
                              $"{Count(FileHealth.Partial):N0} partial, {Count(FileHealth.Broken):N0} broken, {Count(FileHealth.Unsupported):N0} not recognized.";
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => { if (_table.SelectedRow is { } r) OpenFile(r.Path); });
        menu.Items.Add("Show in folder", null, (_, _) => { if (_table.SelectedRow is { } r) ShowInExplorer(r.Path); });
        var repairOne = menu.Items.Add("Repair selected…", null, (_, _) =>
            _ = RepairAsync(_table.SelectedRows.Where(r => r.Report.Health is FileHealth.Repairable or FileHealth.Partial).ToList()));
        menu.Items.Add("Copy path", null, (_, _) => { if (_table.SelectedRow is { } r) Clipboard.SetText(r.Path); });
        menu.Opening += (_, e) =>
        {
            e.Cancel = _table.SelectedRow is null;
            repairOne.Enabled = !_working && _table.SelectedRows.Any(r => r.Report.Health is FileHealth.Repairable or FileHealth.Partial);
        };
        return menu;
    }

    // ---- repair ---------------------------------------------------------------------------------------

    private async Task RepairAsync(IReadOnlyList<Row> fixable)
    {
        if (_working || fixable.Count == 0)
            return;
        string? dest = PickFolder("Choose where to save the repaired copies", AppSettings.Get("repair.dest", AppSettings.DefaultFolder("Repaired")));
        if (dest is null)
            return;
        AppSettings.Set("repair.dest", dest);

        SetWorking(true);
        _footer.ShowProgress(0);
        List<RepairResult> results;
        try
        {
            results = await Task.Run(() =>
            {
                var service = new RepairService();
                var list = new List<RepairResult>();
                for (int i = 0; i < fixable.Count; i++)
                {
                    try
                    {
                        list.Add(service.Repair(fixable[i].Path, dest));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        list.Add(new RepairResult(fixable[i].Path, fixable[i].Report.Health, false, null, ex.Message));
                    }
                    int done = i + 1;
                    RunOnUi(() =>
                    {
                        _footer.Progress.Value = (double)done / fixable.Count;
                        _footer.Status.Text = $"Repairing {done:N0} of {fixable.Count:N0}…";
                    });
                }
                return list;
            });
        }
        finally
        {
            SetWorking(false);
        }

        int ok = results.Count(r => r.Repaired);
        _footer.Status.Text = $"Repaired {ok:N0} of {results.Count:N0} — saved to {dest}.";
        if (ok < results.Count)
            ShowError("Some files couldn't be repaired",
                string.Join("\n", results.Where(r => !r.Repaired).Take(10).Select(r => $"• {Path.GetFileName(r.Source)}: {r.Detail}")));
        if (ok > 0)
            ShowInExplorer(dest);
    }

    // ---- tools ------------------------------------------------------------------------------------------

    private async Task FixFileTypesAsync()
    {
        if (_working)
            return;
        string? folder = PickFolder("Pick a folder of recovered files to rename to their real type (e.g. AVIF carved as .mp4)",
            _dir.Text.Length > 0 ? _dir.Text : null);
        if (folder is null)
            return;

        SetWorking(true);
        _footer.ShowProgress(null);
        _footer.Status.Text = "Reading file headers…";
        List<(string Path, string Real)> mismatches;
        try
        {
            mismatches = await Task.Run(() =>
            {
                var list = new List<(string, string)>();
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(folder, "*", options))
                {
                    string? real = RepairService.DetectRealExtension(file);
                    if (real is null)
                        continue;
                    if (RepairService.ExtensionMatches(Path.GetExtension(file), real))
                        continue;
                    list.Add((file, real));
                }
                return list;
            });
        }
        finally
        {
            SetWorking(false);
        }

        if (mismatches.Count == 0)
        {
            _footer.Status.Text = "Every recognized file already has the right extension.";
            ShowInfo("Nothing to fix", "All recognized files already have the correct extension.");
            return;
        }

        string byType = string.Join("\n", mismatches.GroupBy(m => m.Real).OrderByDescending(g => g.Count())
            .Select(g => $"  {Plural(g.Count(), "file")} → {g.Key}"));
        if (!Confirm("Fix file extensions", $"{Plural(mismatches.Count, "file")} have the wrong extension:\n\n{byType}\n\nRename them to their real type?"))
            return;

        int renamed = 0;
        foreach (var (path, real) in mismatches)
        {
            try
            {
                File.Move(path, FileNaming.MakeUnique(Path.ChangeExtension(path, real)));
                renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // locked or vanished — skip
            }
        }
        _footer.Status.Text = $"Renamed {Plural(renamed, "file")} to their real type in {folder}.";
        ShowInExplorer(folder);
    }

    private async Task RebuildVideoAsync()
    {
        if (_working)
            return;
        ShowInfo("Rebuild video with a donor",
            "Rebuilds a video whose index (moov) is missing, using a DONOR — a healthy video recorded by the same " +
            "device/app with the same settings.\n\nYou'll pick:\n  1) the broken video\n  2) the donor video\n  3) where to save the result\n\n" +
            "The output is video-only (no audio) and best-effort — check that it plays.");

        string? broken = PickVideo("Select the BROKEN video (missing index)");
        if (broken is null) return;
        string? donor = PickVideo("Select the DONOR video (healthy, same device/app)");
        if (donor is null) return;

        using var save = new SaveFileDialog
        {
            Title = "Save the rebuilt video as",
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = Path.GetFileNameWithoutExtension(broken) + "_rebuilt.mp4",
            InitialDirectory = Path.GetDirectoryName(broken) ?? "",
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        string output = save.FileName;

        SetWorking(true);
        _footer.ShowProgress(null);
        _footer.Status.Text = "Rebuilding video…";
        Mp4Rebuilder.Result? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() => Mp4Rebuilder.Rebuild(broken, donor, output));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            SetWorking(false);
        }

        if (result is null)
        {
            _footer.Status.Text = "Rebuild failed.";
            ShowError("Rebuild failed", error ?? "Unknown error.");
            return;
        }
        _footer.Status.Text = result.Success ? $"Video rebuilt: {output}" : "Rebuild failed.";
        MessageBox.Show(this, result.Detail, result.Success ? "Rebuilt — check that it plays" : "Could not rebuild",
            MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (result.Success)
            ShowInExplorer(output);
    }

    private string? PickVideo(string title)
    {
        using var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Video files (*.mp4;*.mov;*.m4v;*.3gp)|*.mp4;*.mov;*.m4v;*.3gp|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cts?.Cancel();
        base.Dispose(disposing);
    }
}
