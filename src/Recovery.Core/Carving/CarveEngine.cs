namespace Recovery.Core.Carving;

public sealed record CarvedFile(string Path, long SourceOffset, long Length, string Extension, string? OriginalName = null);

public sealed record CarveProgress(long BytesScanned, long TotalBytes, int FilesFound);

/// <summary>
/// Signature-carving engine: scans byte ranges of a source for known media
/// file headers and extracts every hit into the destination directory.
/// Once a file is carved, scanning resumes past its end so signatures
/// embedded inside it (e.g. EXIF thumbnails) don't produce duplicates.
/// </summary>
public sealed class CarveEngine
{
    private const int BufferSize = 4 << 20;
    private const int HeaderProbe = 12; // max bytes MatchesHeader may touch

    /// <summary>All formats the engine knows how to carve, in scan-priority order.</summary>
    public static IReadOnlyList<IFormatCarver> AllFormats { get; } =
    [
        new PngCarver(),
        new JpegCarver(),
        new IsoBmffCarver(),
        new RiffAviCarver(),
    ];

    private readonly IFormatCarver[] _carvers;

    /// <summary>Carve every known format.</summary>
    public CarveEngine() : this(AllFormats) { }

    /// <summary>
    /// Carve only the given formats (e.g. from a type-selection filter). Order
    /// from <see cref="AllFormats"/> is preserved regardless of argument order.
    /// </summary>
    public CarveEngine(IEnumerable<IFormatCarver> formats)
    {
        var wanted = formats.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _carvers = AllFormats.Where(f => wanted.Contains(f.Name)).ToArray();
        if (_carvers.Length == 0)
            throw new ArgumentException("At least one format must be selected.", nameof(formats));
    }

    /// <param name="pauseGate">When provided, the scan blocks between chunks while
    /// the gate is reset — Set() resumes, Reset() pauses. Cancellation is honored
    /// while paused.</param>
    /// <param name="minFileSize">Files smaller than this are skipped (not written)
    /// — essential for whole-volume scans, where allocated space is full of tiny
    /// icons, thumbnails, and embedded images. 0 keeps everything.</param>
    /// <param name="maxFileSize">Files larger than this are skipped — set with
    /// minFileSize to carve only a size band (e.g. 50–150 MB videos).</param>
    public IReadOnlyList<CarvedFile> Carve(
        ByteSource source,
        IReadOnlyList<(long Start, long Length)> ranges,
        string destDir,
        Action<CarveProgress>? onProgress = null,
        CancellationToken ct = default,
        ManualResetEventSlim? pauseGate = null,
        long minFileSize = 0,
        long maxFileSize = long.MaxValue)
    {
        Directory.CreateDirectory(destDir);

        var found = new List<CarvedFile>();
        long total = ranges.Sum(r => r.Length);
        long scanned = 0;
        long lastReported = 0;
        var buffer = new byte[BufferSize];

        foreach (var (rangeStart, rangeLength) in ranges)
        {
            long rangeEnd = rangeStart + rangeLength;
            long pos = rangeStart;
            long skipUntil = 0;

            while (pos < rangeEnd)
            {
                ct.ThrowIfCancellationRequested();
                pauseGate?.Wait(ct);

                int want = (int)Math.Min(BufferSize, rangeEnd - pos);
                int got = source.ReadAt(pos, buffer, want);
                if (got <= 0)
                    break;

                // Positions within HeaderProbe of the end are re-scanned in the
                // next chunk so headers never straddle a buffer boundary.
                bool lastChunk = pos + got >= rangeEnd;
                int scanEnd = lastChunk ? Math.Max(0, got - HeaderProbe + 1) : got - HeaderProbe + 1;

                for (int i = 0; i < scanEnd; i++)
                {
                    long abs = pos + i;
                    if (abs < skipUntil)
                    {
                        long skipTo = Math.Min(skipUntil - pos, scanEnd);
                        i = (int)skipTo - 1;
                        continue;
                    }

                    foreach (var carver in _carvers)
                    {
                        if (!carver.MatchesHeader(buffer.AsSpan(0, got), i))
                            continue;

                        var measured = carver.TryMeasure(source, abs);
                        if (measured is not { } m)
                            continue;

                        // Skip past the matched region either way, but only write
                        // files inside the requested size band.
                        skipUntil = abs + m.Length;
                        if (m.Length < minFileSize || m.Length > maxFileSize)
                            break;

                        string path = Path.Combine(destDir, $"carved_{abs:x10}{m.Extension}");
                        WriteOut(source, abs, m.Length, path, ct);
                        found.Add(new CarvedFile(path, abs, m.Length, m.Extension));
                        break;
                    }
                }

                long advance = lastChunk ? got : Math.Max(scanEnd, 1);
                pos += advance;
                scanned += advance;

                if (onProgress is not null && (scanned - lastReported >= 64 << 20 || pos >= rangeEnd))
                {
                    lastReported = scanned;
                    onProgress(new CarveProgress(scanned, total, found.Count));
                }
            }
        }

        onProgress?.Invoke(new CarveProgress(total, total, found.Count));
        return found;
    }

    private static void WriteOut(ByteSource source, long offset, long length, string path, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        long done = 0;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            int chunk = (int)Math.Min(buf.Length, length - done);
            int got = source.ReadAt(offset + done, buf, chunk);
            if (got <= 0)
                break;
            output.Write(buf, 0, got);
            done += got;
        }
    }
}
