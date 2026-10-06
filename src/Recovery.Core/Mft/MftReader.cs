using System.Buffers.Binary;
using System.Text;
using Recovery.Core.Carving;

namespace Recovery.Core.Mft;

/// <summary>
/// A recoverable file recovered from a deleted NTFS MFT record: its original
/// name plus enough location info to read the data back off the volume.
/// </summary>
public sealed class MftFile
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required bool Resident { get; init; }
    public byte[]? ResidentData { get; init; }
    public IReadOnlyList<DataRun>? Runs { get; init; }
    public bool CompressedOrEncrypted { get; init; }

    /// <summary>
    /// Original parent folder path relative to the volume root (e.g. "gruk" or
    /// "Users\me\Pictures"), or null/"" for the root. Reconstructed by walking
    /// parent MFT references; "?" segments mark a parent that couldn't be read.
    /// </summary>
    public string? Directory { get; init; }
}

public readonly record struct DataRun(long Lcn, long Clusters, bool Sparse);

/// <summary>
/// Minimal read-only NTFS MFT parser. Reads the boot sector, walks the $MFT's
/// own run list, and enumerates FILE records to find deleted (not-in-use)
/// entries with their original names and $DATA location. Handles the common
/// case: base records with a resident or non-resident unnamed $DATA. It does
/// not follow $ATTRIBUTE_LIST (very large/fragmented files) and cannot
/// reconstruct compressed/encrypted data.
/// </summary>
public sealed class MftReader
{
    private readonly ByteSource _src;
    private readonly long _clusterSize;
    private readonly int _recordSize;
    private readonly int _bytesPerSector;
    private readonly List<(long VStart, long PhysStart, long Bytes)> _mftExtents = new();
    private readonly long _mftDataSize;

    public long RecordCount => _mftDataSize / _recordSize;
    public long ClusterSize => _clusterSize;

    public MftReader(ByteSource src)
    {
        _src = src;

        var vbr = new byte[512];
        if (!src.TryReadExact(0, vbr, 512) || vbr[3] != (byte)'N' || vbr[4] != (byte)'T')
            throw new InvalidDataException("Not an NTFS volume (no 'NTFS' OEM id in boot sector).");

        _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(vbr.AsSpan(0x0B));
        int sectorsPerCluster = vbr[0x0D] <= 0x80 ? vbr[0x0D] : 1 << (256 - vbr[0x0D]);
        _clusterSize = (long)_bytesPerSector * sectorsPerCluster;

        long mftStartCluster = BinaryPrimitives.ReadInt64LittleEndian(vbr.AsSpan(0x30));
        sbyte clustersPerRecord = (sbyte)vbr[0x40];
        _recordSize = clustersPerRecord >= 0
            ? (int)(clustersPerRecord * _clusterSize)
            : 1 << (-clustersPerRecord);

        if (_recordSize is < 256 or > (1 << 20) || _bytesPerSector == 0)
            throw new InvalidDataException("Unreasonable NTFS geometry.");

        // Bootstrap: read $MFT's own record (record 0) directly from its start
        // cluster to learn the full (possibly fragmented) MFT extent map.
        long mftOffset = mftStartCluster * _clusterSize;
        var record0 = new byte[_recordSize];
        if (!_src.TryReadExact(mftOffset, record0, _recordSize))
            throw new InvalidDataException("Cannot read $MFT record 0.");
        ApplyFixup(record0);

        var data = FindAttribute(record0, 0x80);
        if (data is null || data.Value.Resident)
            throw new InvalidDataException("$MFT has no non-resident $DATA.");

        _mftDataSize = data.Value.RealSize;
        long vpos = 0;
        foreach (var run in data.Value.Runs!)
        {
            if (!run.Sparse)
                _mftExtents.Add((vpos, run.Lcn * _clusterSize, run.Clusters * _clusterSize));
            vpos += run.Clusters * _clusterSize;
        }
        if (_mftExtents.Count == 0)
            throw new InvalidDataException("$MFT run list is empty.");
    }

    /// <summary>
    /// Enumerate deleted, non-directory files that have a name and data.
    /// <paramref name="onProgress"/> receives (records read, total records)
    /// roughly every 16k records; cancellation is checked between batches.
    /// </summary>
    public IEnumerable<MftFile> EnumerateDeletedFiles(
        Action<string>? onWarning = null, Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        const int batchRecords = 256;
        long total = RecordCount;
        var buffer = new byte[batchRecords * _recordSize];
        var single = new byte[_recordSize];

        for (long start = 0; start < total; start += batchRecords)
        {
            ct.ThrowIfCancellationRequested();
            if (onProgress is not null && start % (batchRecords * 64) == 0)
                onProgress(start, total);
            int count = (int)Math.Min(batchRecords, total - start);
            int bytes = count * _recordSize;

            if (ReadVirtual(start * _recordSize, buffer, bytes))
            {
                for (int i = 0; i < count; i++)
                {
                    var file = TryParse(buffer, i * _recordSize, start + i, onWarning);
                    if (file is not null)
                        yield return file;
                }
            }
            else
            {
                // A batch read failed (bad/locked region on a live volume) — do
                // NOT abort the whole scan; drop to per-record reads and skip
                // only the records that truly can't be read.
                onWarning?.Invoke($"records {start}–{start + count - 1}: batch unreadable, retrying individually");
                for (int i = 0; i < count; i++)
                {
                    if (!ReadVirtual((start + i) * _recordSize, single, _recordSize))
                        continue;
                    var file = TryParse(single, 0, start + i, onWarning);
                    if (file is not null)
                        yield return file;
                }
            }
        }
        onProgress?.Invoke(total, total);
    }

    private MftFile? TryParse(byte[] buf, int offset, long recordNumber, Action<string>? onWarning)
    {
        try
        {
            return ParseRecord(buf, offset);
        }
        catch (Exception ex)
        {
            onWarning?.Invoke($"record {recordNumber}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read a recovered file's bytes (truncated to its real size) into memory.
    /// Use <see cref="CopyData"/> for large files — arrays cap out near 2 GB.
    /// </summary>
    public byte[] ReadData(MftFile file)
    {
        if (file.Resident)
            return file.ResidentData ?? [];
        if (file.Size > Array.MaxLength)
            throw new IOException($"{file.Name} is too large to read into memory; stream it with CopyData.");

        using var ms = new MemoryStream((int)file.Size);
        CopyData(file, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Streams a recovered file's bytes (exactly <see cref="MftFile.Size"/>
    /// of them) into <paramref name="output"/>, reading contiguous clusters in
    /// large chunks. Unreadable clusters and a run list that ends early are
    /// zero-filled so later data stays at its correct offset. Returns the
    /// number of bytes that could not be read (0 = clean copy).
    /// </summary>
    public long CopyData(MftFile file, Stream output, CancellationToken ct = default)
    {
        if (file.Resident)
        {
            output.Write(file.ResidentData ?? []);
            return 0;
        }

        int chunk = (int)Math.Max(_clusterSize, (1 << 20) / _clusterSize * _clusterSize);
        var buf = new byte[chunk];
        long written = 0, unreadable = 0;

        foreach (var run in file.Runs!)
        {
            long runBytes = run.Clusters * _clusterSize;
            for (long inRun = 0; inRun < runBytes && written < file.Size;)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(chunk, Math.Min(runBytes - inRun, file.Size - written));
                int readLen = (int)((want + _clusterSize - 1) / _clusterSize * _clusterSize);

                if (run.Sparse)
                    Array.Clear(buf, 0, want);
                else if (!_src.TryReadExact(run.Lcn * _clusterSize + inRun, buf, readLen))
                    unreadable += ReadClusterByCluster(run.Lcn * _clusterSize + inRun, buf, readLen);

                output.Write(buf, 0, want);
                written += want;
                inRun += want;
            }
        }

        if (written < file.Size)
        {
            // Run list shorter than the recorded size — pad so the file keeps its size.
            Array.Clear(buf);
            unreadable += file.Size - written;
            while (written < file.Size)
            {
                int n = (int)Math.Min(buf.Length, file.Size - written);
                output.Write(buf, 0, n);
                written += n;
            }
        }
        return unreadable;
    }

    /// <summary>
    /// The first bytes of a recovered file (at most <paramref name="maxBytes"/>,
    /// read from its first cluster) — enough to check its signature without
    /// reading the whole file. Throws <see cref="IOException"/> when unreadable.
    /// </summary>
    public byte[] ReadHead(MftFile file, int maxBytes)
    {
        int len = (int)Math.Min(maxBytes, file.Size);
        if (file.Resident)
        {
            var data = file.ResidentData ?? [];
            return data.AsSpan(0, Math.Min(len, data.Length)).ToArray();
        }
        if (file.Runs is not { Count: > 0 } runs)
            return [];
        if (runs[0].Sparse)
            return new byte[len];

        var cluster = new byte[_clusterSize];
        if (!_src.TryReadExact(runs[0].Lcn * _clusterSize, cluster, (int)_clusterSize))
            throw new IOException("first cluster unreadable");
        return cluster.AsSpan(0, Math.Min(len, cluster.Length)).ToArray();
    }

    /// <summary>Fallback after a failed bulk read: salvage what reads, zero the rest.</summary>
    private long ReadClusterByCluster(long offset, byte[] buf, int length)
    {
        long failed = 0;
        var cluster = new byte[_clusterSize];
        for (int pos = 0; pos < length; pos += (int)_clusterSize)
        {
            if (_src.TryReadExact(offset + pos, cluster, (int)_clusterSize))
            {
                Array.Copy(cluster, 0, buf, pos, _clusterSize);
            }
            else
            {
                Array.Clear(buf, pos, (int)_clusterSize);
                failed += _clusterSize;
            }
        }
        return failed;
    }

    // ---- record / attribute parsing ---------------------------------------

    private MftFile? ParseRecord(byte[] buf, int offset)
    {
        if (buf[offset] != (byte)'F' || buf[offset + 1] != (byte)'I' ||
            buf[offset + 2] != (byte)'L' || buf[offset + 3] != (byte)'E')
            return null;

        // Fixup works on a copy of the single record so batch reads aren't disturbed.
        var record = new byte[_recordSize];
        Array.Copy(buf, offset, record, 0, _recordSize);
        ApplyFixup(record);

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x16));
        bool inUse = (flags & 0x0001) != 0;
        bool directory = (flags & 0x0002) != 0;
        if (inUse || directory)
            return null; // we only want deleted files

        var (name, parentRef) = ReadBestFileName(record);
        if (name is null || !MediaExtensions.IsMedia(name))
            return null;

        var data = FindAttribute(record, 0x80);
        if (data is null)
            return null;

        string dir = ResolveDirectory(parentRef, 0);

        if (data.Value.CompressedOrEncrypted)
            return new MftFile { Name = name, Directory = dir, Size = data.Value.RealSize, Resident = false, CompressedOrEncrypted = true };

        return data.Value.Resident
            ? new MftFile { Name = name, Directory = dir, Size = data.Value.ResidentData!.Length, Resident = true, ResidentData = data.Value.ResidentData }
            : new MftFile { Name = name, Directory = dir, Size = data.Value.RealSize, Resident = false, Runs = data.Value.Runs };
    }

    // Root directory is MFT record 5; ".." chains terminate there.
    private const long RootRecord = 5;
    private readonly Dictionary<long, string> _dirCache = new();

    /// <summary>
    /// Reconstruct a parent folder's path (relative to volume root) by walking
    /// parent MFT references. Results are cached; unreadable parents yield "?".
    /// </summary>
    private string ResolveDirectory(long parentRef, int depth)
    {
        long rec = parentRef & 0xFFFFFFFFFFFF; // low 48 bits = record number
        if (rec == RootRecord || rec == 0)
            return "";
        if (depth > 64)
            return "?";
        if (_dirCache.TryGetValue(rec, out var cached))
            return cached;

        _dirCache[rec] = "?"; // guard against cycles while recursing
        string result = "?";

        var single = new byte[_recordSize];
        if (ReadVirtual(rec * _recordSize, single, _recordSize) &&
            single[0] == 'F' && single[1] == 'I' && single[2] == 'L' && single[3] == 'E')
        {
            ApplyFixup(single);
            var (name, grandParent) = ReadBestFileName(single);
            if (name is not null)
            {
                string parentPath = ResolveDirectory(grandParent, depth + 1);
                result = parentPath.Length == 0 ? name : parentPath + "\\" + name;
            }
        }

        _dirCache[rec] = result;
        return result;
    }

    private (string? Name, long ParentRef) ReadBestFileName(byte[] record)
    {
        string? best = null;
        long bestParent = 0;
        int bestScore = -1;
        int pos = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));

        while (pos + 8 <= _recordSize)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
            if (type == 0xFFFFFFFF)
                break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
            if (len <= 0 || pos + len > _recordSize)
                break;

            if (type == 0x30 && record[pos + 8] == 0) // resident $FILE_NAME
            {
                int contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 0x14));
                int c = pos + contentOffset;
                if (c + 0x42 <= _recordSize)
                {
                    int nameLen = record[c + 0x40];
                    int ns = record[c + 0x41];
                    int nameBytes = nameLen * 2;
                    if (c + 0x42 + nameBytes <= _recordSize)
                    {
                        // Prefer Win32 (1) / Win32+DOS (3) over POSIX (0) over bare DOS 8.3 (2).
                        int score = ns switch { 3 => 3, 1 => 2, 0 => 1, _ => 0 };
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = Encoding.Unicode.GetString(record, c + 0x42, nameBytes);
                            bestParent = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(c + 0x00));
                        }
                    }
                }
            }
            pos += len;
        }
        return (best, bestParent);
    }

    private readonly record struct AttrData(
        bool Resident, byte[]? ResidentData, long RealSize,
        IReadOnlyList<DataRun>? Runs, bool CompressedOrEncrypted);

    /// <summary>Finds the first unnamed attribute of the given type (e.g. 0x80 $DATA).</summary>
    private AttrData? FindAttribute(byte[] record, uint wantType)
    {
        int pos = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));

        while (pos + 8 <= _recordSize)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
            if (type == 0xFFFFFFFF)
                break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
            if (len <= 0 || pos + len > _recordSize)
                break;

            byte nameLen = record[pos + 9];
            if (type == wantType && nameLen == 0)
            {
                bool nonResident = record[pos + 8] != 0;
                if (!nonResident)
                {
                    int contentLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 0x10));
                    int contentOff = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 0x14));
                    if (contentOff < 0 || pos + contentOff + contentLen > _recordSize || contentLen < 0)
                        return null;
                    var bytes = new byte[contentLen];
                    Array.Copy(record, pos + contentOff, bytes, 0, contentLen);
                    return new AttrData(true, bytes, contentLen, null, false);
                }
                else
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(pos + 0x10));
                    if (startVcn != 0)
                    {
                        pos += len; // continuation via $ATTRIBUTE_LIST — not followed
                        continue;
                    }
                    ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 0x0C));
                    bool compressedOrEncrypted = (attrFlags & 0x0001) != 0 || (attrFlags & 0x4000) != 0;
                    long realSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(pos + 0x30));
                    int runOff = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 0x20));
                    if (compressedOrEncrypted)
                        return new AttrData(false, null, realSize, null, true);
                    var runs = ParseDataRuns(record, pos + runOff, pos + len);
                    return new AttrData(false, null, realSize, runs, false);
                }
            }
            pos += len;
        }
        return null;
    }

    private static List<DataRun> ParseDataRuns(byte[] record, int start, int end)
    {
        var runs = new List<DataRun>();
        int pos = start;
        long lcn = 0;

        while (pos < end && record[pos] != 0)
        {
            int header = record[pos++];
            int lenBytes = header & 0x0F;
            int offBytes = (header >> 4) & 0x0F;
            if (lenBytes == 0 || pos + lenBytes + offBytes > end)
                break;

            long length = ReadLE(record, pos, lenBytes, signed: false);
            pos += lenBytes;

            if (offBytes == 0)
            {
                runs.Add(new DataRun(0, length, Sparse: true));
            }
            else
            {
                lcn += ReadLE(record, pos, offBytes, signed: true);
                pos += offBytes;
                runs.Add(new DataRun(lcn, length, Sparse: false));
            }
        }
        return runs;
    }

    private static long ReadLE(byte[] data, int offset, int count, bool signed)
    {
        long value = 0;
        for (int i = 0; i < count; i++)
            value |= (long)data[offset + i] << (8 * i);
        if (signed && count > 0 && (data[offset + count - 1] & 0x80) != 0)
            value |= -1L << (8 * count); // sign-extend
        return value;
    }

    // ---- fixups & virtual MFT stream --------------------------------------

    private void ApplyFixup(byte[] record)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x04));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x06));
        if (usaCount == 0 || usaOffset + usaCount * 2 > record.Length)
            return;

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * _bytesPerSector - 2;
            if (sectorEnd + 2 > record.Length)
                break;
            // The last two bytes of each sector should hold the USN; restore them.
            ushort fixup = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset + i * 2));
            record[sectorEnd] = (byte)(fixup & 0xFF);
            record[sectorEnd + 1] = (byte)(fixup >> 8);
        }
        _ = usn;
    }

    private bool ReadVirtual(long vOffset, byte[] buffer, int length)
    {
        int done = 0;
        while (done < length)
        {
            long v = vOffset + done;
            var ext = _mftExtents.FirstOrDefault(e => v >= e.VStart && v < e.VStart + e.Bytes);
            if (ext.Bytes == 0)
                return false;
            long into = v - ext.VStart;
            int chunk = (int)Math.Min(length - done, ext.Bytes - into);
            var tmp = new byte[chunk];
            if (!_src.TryReadExact(ext.PhysStart + into, tmp, chunk))
                return false;
            Array.Copy(tmp, 0, buffer, done, chunk);
            done += chunk;
        }
        return true;
    }
}
