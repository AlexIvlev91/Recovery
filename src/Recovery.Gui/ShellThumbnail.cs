using System.Runtime.InteropServices;

namespace Recovery.Gui;

/// <summary>
/// Thumbnails via the Windows shell (IShellItemImageFactory) — the same
/// pipeline Explorer uses, so it covers video frames, HEIC, and RAW formats
/// through whatever codecs are installed, not just what GDI+ can decode.
/// </summary>
internal static class ShellThumbnail
{
    public static Bitmap? TryGet(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;

        try
        {
            var hr = factory.GetImage(new SIZE { cx = size, cy = size },
                SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                return null;
            try
            {
                return Image.FromHbitmap(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Square letterboxed tile for a grid: shell thumbnail first, GDI+ decode
    /// as fallback; null when neither can render the file.
    /// </summary>
    public static Bitmap? TryGetTile(string path, int tileSize)
    {
        using (var shell = TryGet(path, tileSize))
        {
            if (shell is not null)
                return Letterbox(shell, tileSize);
        }
        try
        {
            using var img = Image.FromFile(path);
            return Letterbox(img, tileSize);
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap Letterbox(Image src, int tileSize)
    {
        var bmp = new Bitmap(tileSize, tileSize);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Theme.ThumbBack);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        double scale = Math.Min((double)tileSize / src.Width, (double)tileSize / src.Height);
        int w = Math.Max(1, (int)(src.Width * scale));
        int h = Math.Max(1, (int)(src.Height * scale));
        g.DrawImage(src, (tileSize - w) / 2, (tileSize - h) / 2, w, h);
        return bmp;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
