using Recovery.Core.Mft;

namespace Recovery.Core.Carving;

/// <summary>
/// Carving has no filename info of its own — the directory entry that maps a
/// name to those clusters is what's missing. This cross-references each
/// carved hit's offset against deleted-but-still-present NTFS MFT records
/// (same source the "undelete" tier reads) and renames the carved file to its
/// probable original name wherever a deleted record's data run covers it.
/// Best-effort: files whose MFT record is also gone keep their generic name.
/// </summary>
public static class CarveNameMatcher
{
    /// <summary>
    /// Renames files in <paramref name="carved"/> to their probable original
    /// name where a deleted MFT entry's data run covers the carved offset.
    /// <paramref name="source"/> must still be open (called before disposal).
    /// </summary>
    public static IReadOnlyList<CarvedFile> ApplyOriginalNames(
        IReadOnlyList<CarvedFile> carved, ByteSource source, Action<string>? onWarning = null)
    {
        if (carved.Count == 0)
            return carved;

        MftReader reader;
        try
        {
            reader = new MftReader(source);
        }
        catch (InvalidDataException ex)
        {
            onWarning?.Invoke($"Original-name matching skipped: {ex.Message}");
            return carved;
        }

        var ranges = BuildRanges(reader, reader.EnumerateDeletedFiles(onWarning));
        if (ranges.Count == 0)
            return carved;

        var updated = new List<CarvedFile>(carved.Count);
        foreach (var cf in carved)
        {
            string? name = FindName(ranges, cf.SourceOffset);
            if (name is null)
            {
                updated.Add(cf);
                continue;
            }

            string dir = Path.GetDirectoryName(cf.Path)!;
            string target = FileNaming.MakeUnique(Path.Combine(dir, FileNaming.SanitizeName(name)));
            try
            {
                File.Move(cf.Path, target);
                updated.Add(cf with { Path = target, OriginalName = name });
            }
            catch (IOException)
            {
                updated.Add(cf); // rename failed (e.g. name collision race) — keep generic name
            }
        }
        return updated;
    }

    private static List<(long Start, long End, string Name)> BuildRanges(MftReader reader, IEnumerable<MftFile> deletedFiles)
    {
        long clusterSize = reader.ClusterSize;
        var ranges = new List<(long Start, long End, string Name)>();
        foreach (var file in deletedFiles)
        {
            if (file.Resident || file.Runs is null)
                continue; // resident data lives inside the MFT record, not at a volume offset carving can hit
            foreach (var run in file.Runs)
            {
                if (run.Sparse)
                    continue;
                long start = run.Lcn * clusterSize;
                ranges.Add((start, start + run.Clusters * clusterSize, file.Name));
            }
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        return ranges;
    }

    /// <summary>Binary search for the (non-overlapping) range covering <paramref name="offset"/>.</summary>
    private static string? FindName(List<(long Start, long End, string Name)> ranges, long offset)
    {
        int lo = 0, hi = ranges.Count - 1, hit = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid].Start <= offset) { hit = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (hit < 0)
            return null;
        var r = ranges[hit];
        return offset < r.End ? r.Name : null;
    }
}
