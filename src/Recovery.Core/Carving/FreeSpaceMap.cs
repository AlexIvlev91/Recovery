using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Recovery.Core.Carving;

/// <summary>
/// Builds the list of unallocated byte ranges of an NTFS volume via
/// FSCTL_GET_VOLUME_BITMAP, so carving can skip live files entirely.
/// </summary>
public static class FreeSpaceMap
{
    /// <summary>
    /// Returns free (unallocated) byte ranges of the volume, or null when the
    /// bitmap is unavailable (non-NTFS volume, access denied, …).
    /// </summary>
    public static IReadOnlyList<(long Start, long Length)>? TryGetFreeRanges(char driveLetter)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);

        if (!GetDiskFreeSpace($"{driveLetter}:\\", out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            return null;
        long clusterSize = (long)sectorsPerCluster * bytesPerSector;

        using var handle = CreateFile($@"\\.\{driveLetter}:", 0x80000000 /*GENERIC_READ*/,
            0x3 /*share rw*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var ranges = new List<(long Start, long Length)>();
        long runStartCluster = -1;
        long runLength = 0;

        byte[] outBuf = new byte[1 << 20];
        long startLcn = 0;

        while (true)
        {
            bool ok = DeviceIoControl(handle, FSCTL_GET_VOLUME_BITMAP,
                ref startLcn, sizeof(long), outBuf, outBuf.Length, out int returned, IntPtr.Zero);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error != ERROR_MORE_DATA)
                return null;
            if (returned < 17)
                return null;

            long bitmapStart = BitConverter.ToInt64(outBuf, 0);
            long clustersInBitmap = BitConverter.ToInt64(outBuf, 8);
            long availBits = Math.Min(clustersInBitmap, (long)(returned - 16) * 8);

            for (long c = 0; c < availBits; c++)
            {
                bool allocated = (outBuf[16 + (int)(c >> 3)] & (1 << (int)(c & 7))) != 0;
                long cluster = bitmapStart + c;

                if (!allocated)
                {
                    if (runStartCluster < 0) { runStartCluster = cluster; runLength = 0; }
                    runLength++;
                }
                else if (runStartCluster >= 0)
                {
                    ranges.Add((runStartCluster * clusterSize, runLength * clusterSize));
                    runStartCluster = -1;
                }
            }

            if (ok)
                break;
            startLcn = bitmapStart + availBits;
        }

        if (runStartCluster >= 0)
            ranges.Add((runStartCluster * clusterSize, runLength * clusterSize));

        return ranges;
    }

    private const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref long lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(
        string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
}
