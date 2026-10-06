using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Recovery.Core.Carving;

/// <summary>Random-access byte source: a raw volume or a disk-image file.</summary>
public abstract class ByteSource : IDisposable
{
    public abstract long Length { get; }

    /// <summary>Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>; returns bytes read.</summary>
    public abstract int ReadAt(long offset, byte[] buffer, int count);

    public bool TryReadExact(long offset, byte[] buffer, int count)
        => offset >= 0 && offset + count <= Length && ReadAt(offset, buffer, count) == count;

    public abstract void Dispose();
}

/// <summary>A plain file (disk image, VHD-as-raw, or any file to carve from).</summary>
public sealed class FileByteSource : ByteSource
{
    private readonly FileStream _stream;

    public FileByteSource(string path)
        => _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public override long Length => _stream.Length;

    public override int ReadAt(long offset, byte[] buffer, int count)
    {
        if (offset >= Length)
            return 0;
        count = (int)Math.Min(count, Length - offset);
        _stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < count)
        {
            int n = _stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public override void Dispose() => _stream.Dispose();
}

/// <summary>
/// Raw volume reader over \\.\X:. Volume handles require sector-aligned offsets
/// and lengths, so all reads go through an aligned scratch buffer. Requires
/// administrator rights to open.
/// </summary>
public sealed class RawVolumeByteSource : ByteSource
{
    private const int Align = 4096; // multiple of every real sector size

    private readonly FileStream _stream;
    private byte[] _scratch = [];

    public char DriveLetter { get; }

    public RawVolumeByteSource(char driveLetter)
    {
        DriveLetter = char.ToUpperInvariant(driveLetter);
        _stream = new FileStream($@"\\.\{DriveLetter}:", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Length = GetVolumeLength(_stream.SafeFileHandle, DriveLetter);
    }

    public override long Length { get; }

    public override int ReadAt(long offset, byte[] buffer, int count)
    {
        if (offset >= Length)
            return 0;
        count = (int)Math.Min(count, Length - offset);

        long alignedOffset = offset & ~(long)(Align - 1);
        int skip = (int)(offset - alignedOffset);
        int alignedCount = (skip + count + Align - 1) & ~(Align - 1);

        if (_scratch.Length < alignedCount)
            _scratch = new byte[alignedCount];

        _stream.Seek(alignedOffset, SeekOrigin.Begin);
        int got = 0;
        while (got < alignedCount)
        {
            int n = _stream.Read(_scratch, got, alignedCount - got);
            if (n == 0) break;
            got += n;
        }

        int available = Math.Max(0, Math.Min(count, got - skip));
        Array.Copy(_scratch, skip, buffer, 0, available);
        return available;
    }

    public override void Dispose() => _stream.Dispose();

    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        out long lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName, out long lpFreeBytesAvailable,
        out long lpTotalNumberOfBytes, out long lpTotalNumberOfFreeBytes);

    private static long GetVolumeLength(SafeFileHandle handle, char drive)
    {
        if (DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long len, sizeof(long), out _, IntPtr.Zero))
            return len;
        if (GetDiskFreeSpaceEx($"{drive}:\\", out _, out long total, out _))
            return total;
        throw new IOException($"Cannot determine size of volume {drive}:");
    }
}
