namespace Recovery.Gui.Controls;

/// <summary>
/// Right-hand preview card: large image, file name, key/value details and
/// action buttons. Images load off the UI thread; a newer request always
/// wins over a slower older one.
/// </summary>
internal sealed class PreviewPanel : Card
{
    private readonly PictureBox _picture;
    private readonly Label _title;
    private readonly TableLayoutPanel _details;
    private string _message = "";
    private int _version;

    public PreviewPanel()
    {
        Padding = Theme.S(12, 12, 12, 12);

        _picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.ThumbBack,
            Margin = Theme.S(0, 0, 0, 10),
        };
        _picture.Paint += (_, e) =>
        {
            if (_picture.Image is not null || _message.Length == 0)
                return;
            var r = _picture.ClientRectangle;
            r.Inflate(-Theme.S(16), -Theme.S(16));
            TextRenderer.DrawText(e.Graphics, _message, Theme.Body, r, Theme.TextMuted,
                TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        };

        _title = new Label
        {
            UseMnemonic = false,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = Theme.Section,
            ForeColor = Theme.Text,
            Margin = Theme.S(0, 0, 0, 6),
            AutoEllipsis = true,
        };

        _details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Theme.S(0, 0, 0, 10),
        };
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty,
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_picture, 0, 0);
        layout.Controls.Add(_title, 0, 1);
        layout.Controls.Add(_details, 0, 2);
        layout.Controls.Add(Actions, 0, 3);
        Controls.Add(layout);

        ShowEmpty("Select a file to preview it.");
    }

    public FlowLayoutPanel Actions { get; }

    public void ShowEmpty(string message)
    {
        _version++;
        SetImage(null);
        _message = message;
        _title.Text = "";
        _details.Controls.ClearAndDispose();
        Actions.Visible = false;
        _picture.Invalidate();
    }

    public void ShowItem(string title, IEnumerable<(string Key, string Value)> details)
    {
        _title.Text = title;
        _details.SuspendLayout();
        _details.Controls.ClearAndDispose();
        _details.RowStyles.Clear();
        int row = 0;
        foreach (var (key, value) in details)
        {
            _details.Controls.Add(new Label
        {
            UseMnemonic = false,
                Text = key,
                AutoSize = true,
                Font = Theme.Small,
                ForeColor = Theme.TextMuted,
                Margin = Theme.S(0, 2, 12, 2),
            }, 0, row);
            _details.Controls.Add(new Label
        {
            UseMnemonic = false,
                Text = value,
                AutoSize = true,
                Font = Theme.Small,
                ForeColor = Theme.Text,
                Margin = Theme.S(0, 2, 0, 2),
                MaximumSize = new Size(Math.Max(Theme.S(120), Width - Theme.S(120)), 0),
            }, 1, row);
            row++;
        }
        _details.ResumeLayout();
        Actions.Visible = Actions.Controls.Count > 0;
    }

    /// <summary>Load the preview image in the background; null result shows <paramref name="failMessage"/>.</summary>
    public void LoadImage(Func<Image?> load, string loadingMessage = "Loading preview…", string failMessage = "No preview available for this file.")
    {
        int version = ++_version;
        SetImage(null);
        _message = loadingMessage;
        _picture.Invalidate();

        Task.Run(() =>
        {
            if (version != Volatile.Read(ref _version))
                return; // superseded before it started — don't touch the disk
            Image? img = null;
            string? error = null;
            try
            {
                img = load();
            }
            catch (Exception ex)
            {
                error = ex is UnauthorizedAccessException ? "Administrator rights are needed to read this file." : null;
            }
            if (IsDisposed || !IsHandleCreated)
            {
                img?.Dispose();
                return;
            }
            try
            {
                BeginInvoke(() =>
                {
                    if (version != _version || IsDisposed)
                    {
                        img?.Dispose();
                        return;
                    }
                    _message = error ?? failMessage;
                    SetImage(img);
                    _picture.Invalidate();
                });
            }
            catch (InvalidOperationException)
            {
                img?.Dispose();
            }
        });
    }

    private void SetImage(Image? img)
    {
        var old = _picture.Image;
        _picture.Image = img;
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SetImage(null);
        base.Dispose(disposing);
    }
}
