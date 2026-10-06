using Recovery.Core.Carving;

namespace Recovery.Core.Mft;

/// <summary>
/// Recovers deleted photos/videos from an NTFS volume's MFT, keeping their
/// original file names. Data is read from the clusters the record still points
/// at — successful only where those clusters have not been reused yet.
/// </summary>
public sealed class MftUndeleteService
{
    public sealed record Entry(string OriginalName, long SizeBytes, MediaKind Kind, MftFile File, char Drive)
    {
        public bool DataAvailable => !File.CompressedOrEncrypted;

        /// <summary>Best-effort original full path, e.g. "C:\gruk\photo.jpg".</summary>
        public string FullPath =>
            string.IsNullOrEmpty(File.Directory)
                ? $"{Drive}:\\{OriginalName}"
                : $"{Drive}:\\{File.Directory}\\{OriginalName}";

        /// <summary>Original parent folder, e.g. "C:\gruk".</summary>
        public string FolderPath =>
            string.IsNullOrEmpty(File.Directory) ? $"{Drive}:\\" : $"{Drive}:\\{File.Directory}";
    }

    public sealed record RecoverResult(Entry Entry, string? OutputPath, string? Error)
    {
        public bool Success => OutputPath is not null;
    }

    /// <summary>What a deleted file's first cluster says about its data.</summary>
    public enum DataCheck
    {
        /// <summary>Header matches the file type — the data very likely survived.</summary>
        LooksIntact,
        /// <summary>Header belongs to something else — the clusters were reused.</summary>
        Overwritten,
        /// <summary>All zeros — typically TRIMmed on an SSD; nothing to recover.</summary>
        Zeroed,
        /// <summary>No reliable signature for this type; recover and check.</summary>
        Unknown,
        /// <summary>The clusters couldn't be read.</summary>
        Unreadable,
        /// <summary>Compressed/encrypted data that can't be reconstructed.</summary>
        NotRecoverable,
    }

    /// <summary>
    /// Scan a volume (e.g. 'C') for deleted media files. Requires administrator
    /// rights to open the raw volume. <paramref name="onProgress"/> receives
    /// (MFT records read, total records).
    /// </summary>
    public IReadOnlyList<Entry> Scan(
        char driveLetter, Action<string>? onWarning = null,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        using var src = new RawVolumeByteSource(driveLetter);
        return ScanSource(src, char.ToUpperInvariant(driveLetter), onWarning, onProgress, ct);
    }

    /// <summary>Scan any byte source (raw volume or NTFS image). Used for testing.</summary>
    public IReadOnlyList<Entry> ScanSource(
        ByteSource src, char drive = '?', Action<string>? onWarning = null,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        var reader = new MftReader(src);
        var entries = new List<Entry>();

        foreach (var file in reader.EnumerateDeletedFiles(onWarning, onProgress, ct))
        {
            if (file.Size <= 0)
                continue;
            entries.Add(new Entry(file.Name, file.Size, MediaExtensions.KindOf(file.Name), file, drive));
        }

        return entries.OrderByDescending(e => e.SizeBytes).ToList();
    }

    /// <summary>
    /// Reads the first cluster of each entry and compares it to the file type's
    /// signature, reporting a <see cref="DataCheck"/> per entry as it goes.
    /// Cheap (one small read per file) and the best early signal of whether a
    /// recovery will produce the real file or reused/zeroed clusters.
    /// </summary>
    public void CheckData(
        char driveLetter, IReadOnlyList<Entry> entries, Action<Entry, DataCheck> onResult, CancellationToken ct = default)
    {
        using var src = new RawVolumeByteSource(driveLetter);
        CheckDataSource(src, entries, onResult, ct);
    }

    /// <summary><see cref="CheckData"/> against any byte source. Used for testing.</summary>
    public void CheckDataSource(
        ByteSource src, IReadOnlyList<Entry> entries, Action<Entry, DataCheck> onResult, CancellationToken ct = default)
    {
        var reader = new MftReader(src);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            onResult(entry, Check(reader, entry));
        }
    }

    private static DataCheck Check(MftReader reader, Entry entry)
    {
        if (!entry.DataAvailable)
            return DataCheck.NotRecoverable;
        byte[] head;
        try
        {
            head = reader.ReadHead(entry.File, MediaSignature.HeadLength);
        }
        catch (IOException)
        {
            return DataCheck.Unreadable;
        }
        if (head.Length == 0)
            return DataCheck.Unreadable; // no data runs to read from
        if (MediaSignature.IsZeroed(head))
            return DataCheck.Zeroed;
        return MediaSignature.Matches(Path.GetExtension(entry.OriginalName), head) switch
        {
            true => DataCheck.LooksIntact,
            false => DataCheck.Overwritten,
            null => DataCheck.Unknown,
        };
    }

    /// <summary>
    /// Reads one entry's data into memory for previewing, or null when it is
    /// larger than <paramref name="maxBytes"/>. Nothing is written to disk, so
    /// previewing can't overwrite other deleted data on the same volume.
    /// </summary>
    public byte[]? ReadForPreview(Entry entry, long maxBytes)
    {
        if (!entry.DataAvailable || entry.SizeBytes > maxBytes)
            return null;
        using var src = new RawVolumeByteSource(entry.Drive);
        return new MftReader(src).ReadData(entry.File);
    }

    /// <summary>
    /// Recover the given entries to <paramref name="destDir"/> under their
    /// original names (optionally grouped into per-type subfolders). The
    /// volume is reopened to read the data, which is streamed so files of any
    /// size work. <paramref name="onProgress"/> receives (files done, total).
    /// </summary>
    public IReadOnlyList<RecoverResult> Recover(
        char driveLetter, IEnumerable<Entry> entries, string destDir, bool byType = false,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var list = entries as IReadOnlyCollection<Entry> ?? entries.ToList();
        using var src = new RawVolumeByteSource(driveLetter);
        var reader = new MftReader(src);
        var results = new List<RecoverResult>(list.Count);
        Directory.CreateDirectory(destDir);

        foreach (var entry in list)
        {
            if (ct.IsCancellationRequested)
                break;
            onProgress?.Invoke(results.Count, list.Count);

            if (!entry.DataAvailable)
            {
                results.Add(new RecoverResult(entry, null, "compressed/encrypted data — cannot reconstruct"));
                continue;
            }

            string? target = null;
            try
            {
                string folder = byType ? Path.Combine(destDir, MediaExtensions.TypeFolder(entry.OriginalName)) : destDir;
                Directory.CreateDirectory(folder);
                target = FileNaming.MakeUnique(Path.Combine(folder, FileNaming.SanitizeName(entry.OriginalName)));
                long unreadable;
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    unreadable = reader.CopyData(entry.File, output, ct);
                results.Add(new RecoverResult(entry, target,
                    unreadable > 0 ? $"{ByteSize.Format(unreadable)} could not be read and was zero-filled" : null));
            }
            catch (OperationCanceledException)
            {
                TryDelete(target); // don't leave a half-written file behind
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(target);
                results.Add(new RecoverResult(entry, null, ex.Message));
            }
        }

        onProgress?.Invoke(results.Count, list.Count);
        return results;
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
            return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
