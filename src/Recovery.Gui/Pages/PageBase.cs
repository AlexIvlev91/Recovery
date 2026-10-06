using System.ComponentModel;
using System.Diagnostics;
using Recovery.Gui.Controls;

namespace Recovery.Gui.Pages;

/// <summary>
/// A screen hosted in the main window: header (title, subtitle, actions),
/// an optional "needs administrator" bar, and a body. Pages stay alive when
/// you switch away, so long scans keep running in the background.
/// </summary>
internal abstract class PageBase : UserControl
{
    private bool _busy;
    private bool _activated;

    protected PageBase(string key, string title, string subtitle, string glyph, bool needsAdmin = false)
    {
        Key = key;
        Title = title;
        GlyphText = glyph;
        Dock = DockStyle.Fill;
        BackColor = Theme.Window;
        Padding = Theme.S(28, 20, 28, 20);
        Font = Theme.Body;

        var titleLabel = new Label { UseMnemonic = false, Text = title, AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = Padding.Empty };
        var subtitleLabel = new Label
        {
            UseMnemonic = false,
            Text = subtitle, AutoSize = true, Font = Theme.Subtitle, ForeColor = Theme.TextMuted,
            Margin = Theme.S(1, 2, 0, 0), MaximumSize = new Size(Theme.S(760), 0),
        };
        var titles = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty,
        };
        titles.Controls.Add(titleLabel);
        titles.Controls.Add(subtitleLabel);

        HeaderActions = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = Padding.Empty,
            Padding = Theme.S(0, 6, 0, 0),
        };

        // Two columns: titles take the free width, actions hug the right edge;
        // the row height follows the (possibly wrapped) subtitle.
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent, Padding = Theme.S(0, 0, 0, 16), Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(titles, 0, 0);
        header.Controls.Add(HeaderActions, 1, 0);

        Body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        Controls.Add(Body);

        if (needsAdmin && !Elevation.IsAdministrator)
        {
            var bar = new InfoBar(InfoKind.Warning,
                "This tool reads the disk directly, which needs administrator rights. Restart the app as administrator to use it.")
            { Dock = DockStyle.Top };
            bar.AddAction("Restart as administrator", (_, _) => RestartElevatedRequested?.Invoke(this, Key), ButtonKind.Primary)
                .Glyph = Glyph.Shield;
            Controls.Add(Spacer(12));
            Controls.Add(bar);
        }
        Controls.Add(header);
    }

    public string Key { get; }

    public string Title { get; }

    public string GlyphText { get; }

    public Panel Body { get; }

    protected FlowLayoutPanel HeaderActions { get; }

    /// <summary>True while a scan/restore runs — shown as a dot in the sidebar.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        protected set
        {
            if (_busy == value)
                return;
            _busy = value;
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? BusyChanged;

    /// <summary>The page asks the app to relaunch elevated and reopen it.</summary>
    public event EventHandler<string>? RestartElevatedRequested;

    /// <summary>The page asks the app to switch to another page (by key).</summary>
    public event EventHandler<string>? NavigateRequested;

    protected void NavigateTo(string key) => NavigateRequested?.Invoke(this, key);

    protected void RequestElevation() => RestartElevatedRequested?.Invoke(this, Key);

    /// <summary>Called each time the page is shown.</summary>
    public void Activate()
    {
        bool first = !_activated;
        _activated = true;
        OnActivated(first);
    }

    protected virtual void OnActivated(bool firstTime) { }

    /// <summary>
    /// Asked before the app closes (or restarts elevated); return false to keep
    /// it open. Must not change anything — another page may still say no.
    /// </summary>
    public virtual bool ConfirmAppClose() => true;

    /// <summary>The close is certain: stop work and clean up as agreed in <see cref="ConfirmAppClose"/>.</summary>
    public virtual void OnAppClosing() { }

    // ---- helpers for derived pages ------------------------------------------

    /// <summary>Marshal a UI update from a worker thread; a no-op once the page is gone.</summary>
    protected void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected static Panel Spacer(int height, DockStyle dock = DockStyle.Top) =>
        new() { Height = Theme.S(height), Width = Theme.S(height), Dock = dock, BackColor = Color.Transparent };

    protected static string Plural(long n, string singular, string? plural = null) =>
        $"{n:N0} {(n == 1 ? singular : plural ?? singular + "s")}";

    protected void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected void ShowInfo(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected bool Confirm(string title, string message, bool defaultNo = false) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            defaultNo ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) == DialogResult.Yes;

    protected string? PickFolder(string description, string? initial = null)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrEmpty(initial))
            dlg.InitialDirectory = Directory.Exists(initial) ? initial : Path.GetDirectoryName(initial) ?? "";
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    protected void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowError("Could not open file", ex.Message);
        }
    }

    protected static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>
    /// Warn before writing recovered data to the drive it's being recovered
    /// from. Returns true when it's safe or the user accepts the risk.
    /// </summary>
    protected bool ConfirmDestinationDrive(string dest, char sourceDrive)
    {
        if (!Recovery.Core.FileNaming.IsOnDrive(dest, sourceDrive))
            return true;
        return MessageBox.Show(this,
            $"The destination is on {char.ToUpperInvariant(sourceDrive)}:, the same drive the files are being recovered from.\n\n" +
            "Writing there can overwrite the deleted data that hasn't been read yet. Choose a folder on another " +
            "drive (a USB stick or external disk is ideal).\n\nContinue anyway?",
            "Destination on the source drive", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}

/// <summary>Bottom bar of a page: status text (+ progress) on the left, actions on the right.</summary>
internal sealed class FooterBar : Card
{
    public FooterBar()
    {
        Dock = DockStyle.Bottom;
        Height = Theme.S(60);
        Padding = Theme.S(16, 10, 12, 10);

        Status = new Label
        {
            UseMnemonic = false,
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
        };
        Progress = new ThinProgressBar { Dock = DockStyle.Bottom, Visible = false };
        Actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = Theme.S(8, 4, 0, 0),
        };
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        left.Controls.Add(Status);
        left.Controls.Add(Progress);
        Controls.Add(left);
        Controls.Add(Actions);
    }

    public Label Status { get; }

    public ThinProgressBar Progress { get; }

    public FlowLayoutPanel Actions { get; }

    public void ShowProgress(double? fraction)
    {
        Progress.Visible = true;
        Progress.Indeterminate = fraction is null;
        if (fraction is { } f)
            Progress.Value = f;
    }

    public void HideProgress()
    {
        Progress.Indeterminate = false;
        Progress.Visible = false;
    }
}
