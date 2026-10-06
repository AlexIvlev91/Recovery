using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Recovery.Gui;

/// <summary>Decodes images for the preview pane: EXIF-rotated, downscaled, detached from their source.</summary>
internal static class ImageLoader
{
    private const int OrientationTag = 0x0112;

    /// <summary>
    /// Preview for a file on disk: GDI+ decode for ordinary photos up to
    /// <paramref name="maxDecodeBytes"/>, otherwise the Windows shell
    /// (video frames, HEIC, RAW via installed codecs). Null if neither works.
    /// </summary>
    public static Image? FromFile(string path, long maxDecodeBytes = 96L << 20)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= maxDecodeBytes && Recovery.Core.MediaExtensions.KindOf(path) == Recovery.Core.MediaKind.Photo &&
                FromBytes(File.ReadAllBytes(path)) is { } img)
                return img;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return ShellThumbnail.TryGet(path, Theme.S(1024));
    }

    /// <summary>Decode in-memory image bytes; null when GDI+ can't read them.</summary>
    public static Image? FromBytes(byte[] data, int maxDimension = 2048)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var src = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            ApplyOrientation(src);
            double scale = Math.Min(1.0, (double)maxDimension / Math.Max(src.Width, src.Height));
            int w = Math.Max(1, (int)(src.Width * scale));
            int h = Math.Max(1, (int)(src.Height * scale));
            // Copy into a standalone bitmap: GDI+ images stay tied to their stream otherwise.
            var copy = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(copy);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
            return copy;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null; // not decodable — e.g. overwritten data
        }
    }

    private static void ApplyOrientation(Image img)
    {
        if (!img.PropertyIdList.Contains(OrientationTag))
            return;
        var value = img.GetPropertyItem(OrientationTag)?.Value;
        if (value is not { Length: > 0 })
            return;
        var flip = value[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone)
            img.RotateFlip(flip);
    }
}
