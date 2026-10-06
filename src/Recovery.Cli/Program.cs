using Recovery.Core;
using Recovery.Core.Carving;
using Recovery.Core.DiskUsage;
using Recovery.Core.Mft;
using Recovery.Core.RecycleBin;
using Recovery.Core.Repair;
using Recovery.Cli;

return args switch
{
    ["list", ..] => List(),
    ["restore", .. var rest] => Restore(rest),
    ["carve", .. var rest] => Carve(rest),
    ["check", .. var rest] => Check(rest),
    ["repair", .. var rest] => RepairCmd(rest),
    ["undelete", .. var rest] => Undelete(rest),
    ["fix-names", .. var rest] => FixNames(rest),
    ["rebuild-video", .. var rest] => RebuildVideo(rest),
    ["du", .. var rest] => DiskUsage(rest),
    ["selftest-mft"] => MftSelfTest.Run(),
    ["selftest-mp4"] => Mp4RebuildSelfTest.Run(),
    _ => Usage(),
};

static int RebuildVideo(string[] rest)
{
    string? broken = null, donor = null, dest = null;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--broken" when i + 1 < rest.Length: broken = rest[++i]; break;
            case "--donor" when i + 1 < rest.Length: donor = rest[++i]; break;
            case "--dest" when i + 1 < rest.Length: dest = rest[++i]; break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }
    if (broken is null || donor is null || dest is null)
        return Usage();
    if (!File.Exists(broken) || !File.Exists(donor))
    {
        Console.Error.WriteLine("error: --broken and --donor must both be existing files");
        return 1;
    }

    var result = Recovery.Core.Repair.Mp4Rebuilder.Rebuild(broken, donor, dest);
    Console.WriteLine(result.Success ? $"OK: {result.Detail}" : $"FAILED: {result.Detail}");
    return result.Success ? 0 : 1;
}

static int Usage()
{
    Console.WriteLine("""
        recover — photo & video recovery

        Recycle Bin (tier 1):
          recover list                          show recoverable photos/videos in the Recycle Bin
          recover restore --all --dest <dir>    restore everything listed
          recover restore --index 1,3,7 --dest <dir>
                                                restore selected items by their list index

        Deep scan / signature carving (tier 2):
          recover carve --drive E --dest <dir>  carve JPEG/PNG/MP4/MOV/HEIC/AVI out of the
                                                volume's FREE space (needs administrator)
          recover carve --drive E --dest <dir> --all-space
                                                scan the whole volume, not just free space
          recover carve --drive C --dest D:\out --all-space --min-size 500KB
                                                skip files under 500 KB (essential for whole-volume
                                                scans, or you get millions of tiny images)
          recover carve --drive E --dest <dir> --by-type
                                                sort carved files into JPG/PNG/MP4/… subfolders
          recover carve --drive E --dest <dir> --types jpg,png
                                                only search for the given types (jpeg/png/mp4/avi)
          recover carve --image file.img --dest <dir>
                                                carve from a raw disk-image file instead
          recover carve --drive E --dest <dir> --match-names
                                                also check deleted NTFS MFT records and rename
                                                carved files to their real name where one matches

        Undelete by original name (tier 3 — NTFS MFT, needs administrator):
          recover undelete --drive C                list deleted photos/videos with their real names
          recover undelete --drive C --dest <dir>   recover them under their original names
          recover undelete --drive C --dest <dir> --by-type
                                                sort recovered files into type subfolders
          recover undelete --drive C --name-starts-with alex
                                                only show/recover names starting with "alex"

        Disk usage — what is taking up space:
          recover du --path C:\                 folder sizes, type breakdown, biggest files
          recover du --path D:\ --type video --min-size 500MB --top 50
                                                list the 50 largest videos of 500 MB or more
                                                (types: video,image,audio,document,archive,program,other)

        Rebuild a video with no index (moov) using a same-source donor:
          recover rebuild-video --broken bad.mp4 --donor good.mp4 --dest fixed.mp4
                                                donor must be a healthy video from the SAME
                                                device/app (H.264/HEVC). Output is video-only.

        Check / repair damaged files:
          recover check --dir <folder> [--recurse]
                                                diagnose every media file in a folder
          recover check --bin                   diagnose the recoverable Recycle Bin media
          recover repair --dir <folder> --dest <out> [--recurse]
                                                write repaired copies of the fixable files
          recover fix-names --dir <folder> [--apply] [--recurse]
                                                rename files whose extension doesn't match their
                                                real content (e.g. an AVIF carved as .mp4)

        Restores COPY the data out of the bin; nothing is deleted from the bin.
        Carved output must go to a DIFFERENT drive than the one being scanned.
        Repairs never modify the damaged original.
        """);
    return 1;
}

static int Undelete(string[] rest)
{
    char? drive = null;
    string? dest = null;
    bool byType = false, force = false;
    string? nameStartsWith = null;

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--drive" when i + 1 < rest.Length:
                string d = rest[++i].TrimEnd(':', '\\');
                if (d.Length != 1 || !char.IsAsciiLetter(d[0])) { Console.Error.WriteLine($"error: bad drive '{rest[i]}'"); return 1; }
                drive = char.ToUpperInvariant(d[0]);
                break;
            case "--dest" when i + 1 < rest.Length: dest = rest[++i]; break;
            case "--by-type": byType = true; break;
            case "--name-starts-with" when i + 1 < rest.Length: nameStartsWith = rest[++i]; break;
            case "--force": force = true; break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }
    if (drive is null)
        return Usage();

    var service = new MftUndeleteService();
    IReadOnlyList<MftUndeleteService.Entry> entries;
    try
    {
        Console.WriteLine($"Reading the MFT of {drive}: … (this can take a moment on large volumes)");
        entries = service.Scan(drive.Value, w => Console.Error.WriteLine("warn: " + w));
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("error: access denied — run from an elevated (administrator) prompt.");
        return 1;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    if (nameStartsWith is not null)
        entries = entries.Where(e => e.OriginalName.StartsWith(nameStartsWith, StringComparison.OrdinalIgnoreCase) ||
                                     e.FullPath.StartsWith(nameStartsWith, StringComparison.OrdinalIgnoreCase)).ToList();

    if (entries.Count == 0)
    {
        Console.WriteLine(nameStartsWith is null
            ? "No recoverable deleted photos/videos found in the MFT."
            : $"No deleted photos/videos found whose name starts with '{nameStartsWith}'.");
        return 0;
    }

    // One small read per file tells whether its clusters still hold the file.
    var checks = new Dictionary<MftUndeleteService.Entry, MftUndeleteService.DataCheck>();
    try
    {
        service.CheckData(drive.Value, entries, (e, c) => checks[e] = c);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"warn: data check skipped: {ex.Message}");
    }

    Console.WriteLine($"{entries.Count} deleted media file(s) with recoverable names " +
        $"({checks.Values.Count(c => c == MftUndeleteService.DataCheck.LooksIntact)} look intact):");
    Console.WriteLine();
    int shown = 0;
    foreach (var e in entries)
    {
        string state = checks.TryGetValue(e, out var c) ? c switch
        {
            MftUndeleteService.DataCheck.LooksIntact => "intact",
            MftUndeleteService.DataCheck.Overwritten => "overwritten",
            MftUndeleteService.DataCheck.Zeroed => "zeroed",
            MftUndeleteService.DataCheck.Unreadable => "unreadable",
            MftUndeleteService.DataCheck.NotRecoverable => "compressed",
            _ => "?",
        } : "?";
        Console.WriteLine($"  {FormatSize(e.SizeBytes),10}  {e.Kind.ToString().ToLowerInvariant(),-5}  {state,-11}  {e.FullPath}");
        if (++shown >= 200 && dest is null) { Console.WriteLine($"  … and {entries.Count - shown} more"); break; }
    }

    if (dest is null)
    {
        Console.WriteLine();
        Console.WriteLine("Recover them with: recover undelete --drive " + drive + " --dest <dir>");
        return 0;
    }

    if (FileNaming.IsOnDrive(dest, drive.Value) && !force)
    {
        Console.Error.WriteLine($"error: --dest is on {drive}:, the drive being recovered from. Writing there can " +
            "overwrite the deleted data still to be read. Use another drive, or pass --force.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"Recovering {entries.Count} file(s) to {Path.GetFullPath(dest)} …");
    var results = service.Recover(drive.Value, entries, dest, byType);
    int ok = results.Count(r => r.Success);
    foreach (var r in results.Where(r => !r.Success).Take(20))
        Console.WriteLine($"  skipped {r.Entry.OriginalName}: {r.Error}");
    Console.WriteLine($"{ok}/{results.Count} file(s) recovered under their original names.");
    return 0;
}

static int FixNames(string[] rest)
{
    string? dir = null;
    bool apply = false, recurse = false;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--dir" when i + 1 < rest.Length: dir = rest[++i]; break;
            case "--apply": apply = true; break;
            case "--recurse": recurse = true; break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }
    if (dir is null || !Directory.Exists(dir))
    {
        Console.Error.WriteLine($"error: folder not found: {dir}");
        return 1;
    }

    var files = Directory.EnumerateFiles(dir, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    int mismatches = 0, renamed = 0;
    foreach (var file in files)
    {
        string? real = RepairService.DetectRealExtension(file);
        if (real is null)
            continue;
        string current = Path.GetExtension(file);
        // .jpg/.jpeg and .heic/.heif are equivalent — don't churn those.
        if (RepairService.ExtensionMatches(current, real))
            continue;

        mismatches++;
        string target = Path.ChangeExtension(file, real);
        for (int i = 1; File.Exists(target); i++)
            target = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)} ({i}){real}");

        if (apply)
        {
            try { File.Move(file, target); renamed++; Console.WriteLine($"renamed  {Path.GetFileName(file)}  ->  {Path.GetFileName(target)}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"FAILED   {Path.GetFileName(file)}: {ex.Message}"); }
        }
        else
        {
            Console.WriteLine($"{current,-6} -> {real,-6}  {Path.GetFileName(file)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(apply
        ? $"{renamed} file(s) renamed to their real type."
        : $"{mismatches} file(s) have the wrong extension. Re-run with --apply to rename them.");
    return 0;
}

static bool TryParseSize(string text, out long bytes) => ByteSize.TryParse(text, out bytes);

static IReadOnlyList<CarvedFile> SortByType(IReadOnlyList<CarvedFile> results, string dest)
{
    var moved = new List<CarvedFile>(results.Count);
    foreach (var f in results)
    {
        string folder = Path.Combine(dest, MediaExtensions.TypeFolder(f.Extension));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(f.Path));
        for (int i = 1; File.Exists(target); i++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(f.Path)} ({i}){f.Extension}");
        try
        {
            File.Move(f.Path, target);
            moved.Add(f with { Path = target });
        }
        catch (IOException)
        {
            moved.Add(f); // leave in place if it can't be moved
        }
    }
    return moved;
}

static IEnumerable<string> MediaFilesIn(string dir, bool recurse)
    => Directory.EnumerateFiles(dir, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
        .Where(MediaExtensions.IsMedia);

static int Check(string[] rest)
{
    string? dir = null;
    bool bin = false, recurse = false;

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--dir" when i + 1 < rest.Length: dir = rest[++i]; break;
            case "--bin": bin = true; break;
            case "--recurse": recurse = true; break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }
    if (bin == (dir is not null))
        return Usage();

    var service = new RepairService();
    var reports = new List<(string Display, ValidationReport Report)>();

    if (bin)
    {
        foreach (var item in ScanWithWarnings())
            reports.Add((item.OriginalName, service.Check(item.BackingPath)));
    }
    else
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"error: folder not found: {dir}");
            return 1;
        }
        foreach (var file in MediaFilesIn(dir!, recurse))
            reports.Add((Path.GetFileName(file), service.Check(file)));
    }

    foreach (var (display, report) in reports.Where(r => r.Report.Health != FileHealth.Ok))
        Console.WriteLine($"  {report.Health,-11}  {display}  —  {report.Detail}");

    Console.WriteLine();
    Console.WriteLine($"{reports.Count} file(s) checked: " +
        $"{reports.Count(r => r.Report.Health == FileHealth.Ok)} ok, " +
        $"{reports.Count(r => r.Report.Health == FileHealth.Repairable)} repairable, " +
        $"{reports.Count(r => r.Report.Health == FileHealth.Partial)} partial, " +
        $"{reports.Count(r => r.Report.Health == FileHealth.Broken)} broken, " +
        $"{reports.Count(r => r.Report.Health == FileHealth.Unsupported)} unsupported.");
    if (reports.Any(r => r.Report.Health is FileHealth.Repairable or FileHealth.Partial))
        Console.WriteLine("Write best-effort copies with: recover repair --dir <folder> --dest <out>");
    if (reports.Any(r => r.Report.Health == FileHealth.Partial))
        Console.WriteLine("  ('partial' = opens but part of the image/video is permanently gone — top/start only)");
    return 0;
}

static int RepairCmd(string[] rest)
{
    string? dir = null, dest = null;
    bool recurse = false;

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--dir" when i + 1 < rest.Length: dir = rest[++i]; break;
            case "--dest" when i + 1 < rest.Length: dest = rest[++i]; break;
            case "--recurse": recurse = true; break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }
    if (dir is null || dest is null)
        return Usage();
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"error: folder not found: {dir}");
        return 1;
    }

    var service = new RepairService();
    int repaired = 0, unfixable = 0, ok = 0;

    foreach (var file in MediaFilesIn(dir, recurse))
    {
        var result = service.Repair(file, dest);
        switch (result)
        {
            case { Repaired: true }:
                repaired++;
                Console.WriteLine($"repaired  {Path.GetFileName(file)}  ->  {result.OutputPath}");
                Console.WriteLine($"          {result.Detail}");
                break;
            case { Before: FileHealth.Ok or FileHealth.Unsupported }:
                ok++;
                break;
            default:
                unfixable++;
                Console.WriteLine($"SKIPPED   {Path.GetFileName(file)}: {result.Detail}");
                break;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{repaired} repaired, {unfixable} not fixable, {ok} needed no repair.");
    return 0;
}

static int Carve(string[] rest)
{
    char? drive = null;
    string? image = null, dest = null;
    bool allSpace = false, force = false, byType = false, matchNames = false;
    long minSize = 0, maxSize = long.MaxValue;
    List<Recovery.Core.Carving.IFormatCarver>? formats = null;

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--types" when i + 1 < rest.Length:
            {
                var wanted = rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                formats = new List<Recovery.Core.Carving.IFormatCarver>();
                foreach (var w in wanted)
                {
                    var match = CarveEngine.AllFormats.FirstOrDefault(f =>
                        f.Name.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Split('/').Any(part => part.Equals(w, StringComparison.OrdinalIgnoreCase)) ||
                        (w.Equals("jpg", StringComparison.OrdinalIgnoreCase) && f.Name == "JPEG"));
                    if (match is null)
                    {
                        Console.Error.WriteLine($"error: unknown type '{w}'. Known: {string.Join(", ", CarveEngine.AllFormats.Select(f => f.Name))}");
                        return 1;
                    }
                    formats.Add(match);
                }
                break;
            }
            case "--drive" when i + 1 < rest.Length:
                string d = rest[++i].TrimEnd(':', '\\');
                if (d.Length != 1 || !char.IsAsciiLetter(d[0]))
                {
                    Console.Error.WriteLine($"error: bad drive '{rest[i]}'");
                    return 1;
                }
                drive = char.ToUpperInvariant(d[0]);
                break;
            case "--image" when i + 1 < rest.Length:
                image = rest[++i];
                break;
            case "--dest" when i + 1 < rest.Length:
                dest = rest[++i];
                break;
            case "--all-space":
                allSpace = true;
                break;
            case "--min-size" when i + 1 < rest.Length:
                if (!TryParseSize(rest[++i], out minSize)) { Console.Error.WriteLine($"error: bad --min-size '{rest[i]}' (use e.g. 500KB, 1MB)"); return 1; }
                break;
            case "--max-size" when i + 1 < rest.Length:
                if (!TryParseSize(rest[++i], out maxSize)) { Console.Error.WriteLine($"error: bad --max-size '{rest[i]}' (use e.g. 150MB, 2GB)"); return 1; }
                break;
            case "--by-type":
                byType = true;
                break;
            case "--force":
                force = true;
                break;
            case "--match-names":
                matchNames = true;
                break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }

    if (dest is null || (drive is null) == (image is null))
        return Usage();

    ByteSource source;
    IReadOnlyList<(long, long)> ranges;

    try
    {
        if (image is not null)
        {
            source = new FileByteSource(image);
            ranges = [(0L, source.Length)];
        }
        else
        {
            string destRoot = Path.GetPathRoot(Path.GetFullPath(dest)) ?? "";
            if (destRoot.StartsWith($"{drive}:", StringComparison.OrdinalIgnoreCase) && !force)
            {
                Console.Error.WriteLine(
                    $"error: destination is on the drive being scanned ({drive}:) — writing there can " +
                    "overwrite the very data you're recovering. Use another drive, or --force to override.");
                return 1;
            }

            source = new RawVolumeByteSource(drive!.Value);
            if (allSpace)
            {
                ranges = [(0L, source.Length)];
            }
            else
            {
                var free = FreeSpaceMap.TryGetFreeRanges(drive.Value);
                if (free is null)
                {
                    Console.Error.WriteLine("warn: free-space bitmap unavailable (non-NTFS?) — scanning the whole volume.");
                    ranges = [(0L, source.Length)];
                }
                else
                {
                    ranges = free;
                }
            }
        }
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("error: access denied opening the volume — run from an elevated (administrator) prompt.");
        return 1;
    }
    catch (Exception ex) when (ex is IOException or FileNotFoundException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    using (source)
    {
        long totalBytes = ranges.Sum(r => r.Item2);
        Console.WriteLine($"Scanning {FormatSize(totalBytes)}{(image is not null ? $" of {image}" : $" of {(allSpace ? "volume" : "free space on")} {drive}:")} …");

        var engine = formats is null ? new CarveEngine() : new CarveEngine(formats);
        var results = engine.Carve(source, ranges, dest, p =>
        {
            int pct = p.TotalBytes > 0 ? (int)(p.BytesScanned * 100 / p.TotalBytes) : 100;
            Console.Write($"\r  {pct,3}%  scanned {FormatSize(p.BytesScanned)}, {p.FilesFound} file(s) carved   ");
        }, minFileSize: minSize, maxFileSize: maxSize);
        Console.WriteLine();

        if (matchNames)
        {
            Console.WriteLine("Matching carved files against deleted MFT records for original names …");
            results = CarveNameMatcher.ApplyOriginalNames(results, source, w => Console.Error.WriteLine("warn: " + w));
        }

        if (byType)
            results = SortByType(results, dest);

        int named = results.Count(r => r.OriginalName is not null);
        Console.WriteLine($"Done: {results.Count} file(s) carved to {Path.GetFullPath(dest)}" +
            (byType ? ", sorted into type subfolders." : ".") +
            (matchNames ? $" {named} recovered under their original name." : ""));
        foreach (var group in results.GroupBy(r => r.Extension).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key,-6} {group.Count(),6}   ({FormatSize(group.Sum(f => f.Length))})");
        return 0;
    }
}

static IReadOnlyList<RecycleBinItem> ScanWithWarnings()
{
    var items = new RecycleBinScanner().Scan(w =>
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Error.WriteLine("warn: " + w);
        Console.ForegroundColor = prev;
    });
    return items;
}

static int List()
{
    var items = ScanWithWarnings();
    if (items.Count == 0)
    {
        Console.WriteLine("No recoverable photos or videos found in the Recycle Bin.");
        return 0;
    }

    Console.WriteLine($"{items.Count} recoverable media file(s):");
    Console.WriteLine();
    Console.WriteLine($"{"#",4}  {"kind",-5}  {"size",10}  {"deleted (local)",-19}  original path");
    Console.WriteLine(new string('-', 100));

    for (int i = 0; i < items.Count; i++)
    {
        var it = items[i];
        Console.WriteLine(
            $"{i + 1,4}  {it.Kind.ToString().ToLowerInvariant(),-5}  {FormatSize(it.SizeBytes),10}  " +
            $"{it.DeletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {it.OriginalPath}");
    }

    Console.WriteLine();
    Console.WriteLine("Restore with: recover restore --all --dest <dir>   or   --index 1,2,... --dest <dir>");
    return 0;
}

static int Restore(string[] rest)
{
    bool all = false;
    string? dest = null;
    var indexes = new List<int>();

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--all":
                all = true;
                break;
            case "--dest" when i + 1 < rest.Length:
                dest = rest[++i];
                break;
            case "--index" when i + 1 < rest.Length:
                foreach (var part in rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!int.TryParse(part, out int n) || n < 1)
                    {
                        Console.Error.WriteLine($"error: bad index '{part}'");
                        return 1;
                    }
                    indexes.Add(n);
                }
                break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }

    if (dest is null || (!all && indexes.Count == 0))
        return Usage();

    var items = ScanWithWarnings();
    if (items.Count == 0)
    {
        Console.WriteLine("Nothing to restore.");
        return 0;
    }

    IReadOnlyList<RecycleBinItem> selected;
    if (all)
    {
        selected = items;
    }
    else
    {
        var bad = indexes.Where(n => n > items.Count).ToList();
        if (bad.Count > 0)
        {
            Console.Error.WriteLine($"error: index out of range: {string.Join(", ", bad)} (list has {items.Count} items)");
            return 1;
        }
        selected = indexes.Distinct().Select(n => items[n - 1]).ToList();
    }

    var results = new RestoreService().Restore(selected, dest);

    int ok = 0;
    foreach (var r in results)
    {
        if (r.Success)
        {
            ok++;
            Console.WriteLine($"restored  {r.Item.OriginalName}  ->  {r.RestoredPath}");
        }
        else
        {
            Console.Error.WriteLine($"FAILED    {r.Item.OriginalName}: {r.Error}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{ok}/{results.Count} file(s) restored to {Path.GetFullPath(dest)}.");
    return ok == results.Count ? 0 : 2;
}

static string FormatSize(long bytes) => ByteSize.Format(bytes);

static int DiskUsage(string[] rest)
{
    string? path = null;
    int top = 25;
    long minSize = 0;
    HashSet<FileCategory>? types = null;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--path" when i + 1 < rest.Length: path = rest[++i]; break;
            case "--top" when i + 1 < rest.Length:
                if (!int.TryParse(rest[++i], out top) || top <= 0) { Console.Error.WriteLine($"error: bad --top '{rest[i]}'"); return 1; }
                break;
            case "--min-size" when i + 1 < rest.Length:
                if (!TryParseSize(rest[++i], out minSize)) { Console.Error.WriteLine($"error: bad --min-size '{rest[i]}' (use e.g. 100MB)"); return 1; }
                break;
            case "--type" when i + 1 < rest.Length:
                types = [];
                foreach (var t in rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<FileCategory>(t.TrimEnd('s'), ignoreCase: true, out var cat))
                    {
                        Console.Error.WriteLine($"error: unknown type '{t}' (video, image, audio, document, archive, program, other)");
                        return 1;
                    }
                    types.Add(cat);
                }
                break;
            default:
                Console.Error.WriteLine($"error: unknown argument '{rest[i]}'");
                return Usage();
        }
    }
    if (path is null)
        return Usage();
    if (path.Length == 2 && path[1] == ':')
        path += "\\"; // "C:" means the current directory on C:, not the drive root

    Console.WriteLine($"Scanning {Path.GetFullPath(path)} …");
    DiskUsageResult result;
    try
    {
        result = new DiskUsageScanner().Scan(path, p =>
            Console.Write($"\r  {p.Files:N0} files, {p.Folders:N0} folders, {FormatSize(p.Bytes)}   "));
    }
    catch (DirectoryNotFoundException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
    Console.WriteLine();

    var root = result.Root;
    Console.WriteLine();
    Console.WriteLine($"{root.FullPath}: {FormatSize(root.Size)} in {root.FileCount:N0} files, {root.FolderCount:N0} folders " +
        $"(scanned in {result.Elapsed.TotalSeconds:0.0}s)");
    if (result.UnreadableFolders > 0)
        Console.WriteLine($"  {result.UnreadableFolders:N0} folder(s) could not be read — run elevated for a complete picture.");
    if (result.OnlineOnlyFiles > 0)
        Console.WriteLine($"  {result.OnlineOnlyFiles:N0} online-only cloud file(s) ({FormatSize(result.OnlineOnlyBytes)}) not counted — they use no local space.");

    Console.WriteLine();
    Console.WriteLine("By type:");
    foreach (var cat in FileCategories.All.OrderByDescending(c => root.BytesOf(c)))
    {
        if (root.FilesOf(cat) == 0)
            continue;
        Console.WriteLine($"  {FileCategories.Label(cat),-24} {FormatSize(root.BytesOf(cat)),10}  {Percent(root.BytesOf(cat), root.Size),6}  {root.FilesOf(cat),10:N0} files");
    }

    Console.WriteLine();
    Console.WriteLine("Top-level folders:");
    foreach (var child in root.Children.Take(top))
        Console.WriteLine($"  {FormatSize(child.Size),10}  {Percent(child.Size, root.Size),6}  {child.Name}{(child.Incomplete ? "  (incomplete)" : "")}");
    if (root.OwnFilesSize > 0)
        Console.WriteLine($"  {FormatSize(root.OwnFilesSize),10}  {Percent(root.OwnFilesSize, root.Size),6}  [files directly in {root.Name}]");

    Console.WriteLine();
    Console.WriteLine("Biggest folders (any depth):");
    foreach (var dir in DiskUsageQuery.BiggestFolders(root, Math.Min(top, 15)))
        Console.WriteLine($"  {FormatSize(dir.Size),10}  {dir.FullPath}");

    var filter = new FileFilter { Categories = types, MinSize = minSize };
    var (files, matchCount, matchBytes) = DiskUsageQuery.LargestFiles(root, filter, top);
    Console.WriteLine();
    Console.WriteLine($"Largest files{(types is null ? "" : $" ({string.Join(", ", types.Select(FileCategories.Label))})")}" +
        $"{(minSize > 0 ? $" ≥ {FormatSize(minSize)}" : "")} — {matchCount:N0} match, {FormatSize(matchBytes)} total:");
    foreach (var hit in files)
        Console.WriteLine($"  {FormatSize(hit.File.Size),10}  {hit.File.LastWriteUtc.ToLocalTime():yyyy-MM-dd}  {hit.FullPath}");
    return 0;

    static string Percent(long part, long whole) => whole == 0 ? "" : $"{part * 100.0 / whole:0.0}%";
}
