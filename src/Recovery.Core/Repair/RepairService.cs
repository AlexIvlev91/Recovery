using System.Buffers.Binary;

namespace Recovery.Core.Repair;

/// <summary>
/// Diagnoses media files by content (not extension) and repairs what the
/// format repairers can fix. Repairs always write a new file into the
/// destination directory — the damaged original is never touched.
/// </summary>
public sealed class RepairService
{
    private const long MaxInMemory = 512L << 20;

    public ValidationReport Check(string path)
    {
        byte[] head;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            head = new byte[16];
            int got = stream.Read(head, 0, 16);
            if (got < 12)
                return new ValidationReport(path, FileHealth.Broken, "file too small to be media");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValidationReport(path, FileHealth.Broken, $"unreadable: {ex.Message}");
        }

        return ResolveKind(head, path) switch
        {
            Kind.Jpeg => WithBytes(path, JpegRepair.Validate),
            Kind.Png => WithBytes(path, PngRepair.Validate),
            Kind.Bmff => Mp4Repair.Validate(path),
            Kind.Avi => ValidateAvi(path),
            _ => new ValidationReport(path, FileHealth.Unsupported, "unrecognized format"),
        };
    }

    public RepairResult Repair(string path, string destDir)
    {
        var report = Check(path);
        if (report.Health is not (FileHealth.Repairable or FileHealth.Partial))
            return new RepairResult(path, report.Health, false, null, report.Detail);

        Directory.CreateDirectory(destDir);
        string name = Path.GetFileNameWithoutExtension(path) + "_repaired" + Path.GetExtension(path);
        string target = FileNaming.MakeUnique(Path.Combine(destDir, name));

        var head = new byte[16];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            _ = stream.Read(head, 0, 16);

        bool repaired;
        var kind = ResolveKind(head, path);
        switch (kind)
        {
            case Kind.Jpeg:
            case Kind.Png:
            {
                byte[] data = File.ReadAllBytes(path);
                byte[]? fixedData = kind == Kind.Jpeg ? JpegRepair.TryRepair(data) : PngRepair.TryRepair(data);
                repaired = fixedData is not null;
                if (repaired)
                    File.WriteAllBytes(target, fixedData!);
                break;
            }
            case Kind.Bmff:
                repaired = Mp4Repair.TryRepair(path, target);
                break;
            default:
                repaired = false;
                break;
        }

        if (!repaired)
        {
            if (File.Exists(target))
                File.Delete(target);
            return new RepairResult(path, report.Health, false, null, report.Detail);
        }

        var after = Check(target);
        return new RepairResult(path, report.Health, true, target,
            $"was: {report.Detail} — now: {after.Detail}");
    }

    private static Kind ResolveKind(ReadOnlySpan<byte> head, string path)
    {
        var kind = Sniff(head);
        return kind == Kind.Unknown ? DeepSniff(path) : kind;
    }

    private enum Kind { Jpeg, Png, Bmff, Avi, Unknown }

    private static Kind Sniff(ReadOnlySpan<byte> head) => head switch
    {
        [0xFF, 0xD8, 0xFF, ..] => Kind.Jpeg,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Kind.Png,
        [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..] => Kind.Bmff,
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'A', (byte)'V', (byte)'I', (byte)' ', ..] => Kind.Avi,
        _ => Kind.Unknown,
    };

    /// <summary>
    /// The file's true extension based on its content (magic bytes / ISO-BMFF
    /// brand), or null when the content isn't a recognized media format. Used to
    /// fix mislabeled recovered files (e.g. an AVIF image carved as ".mp4").
    /// </summary>
    public static string? DetectRealExtension(string path)
    {
        byte[] head = new byte[32];
        int got;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            got = stream.Read(head, 0, head.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (got < 12)
            return null;

        return Sniff(head) switch
        {
            Kind.Jpeg => ".jpg",
            Kind.Png => ".png",
            Kind.Avi => ".avi",
            Kind.Bmff => BmffExtension(System.Text.Encoding.ASCII.GetString(head, 8, 4)),
            _ => null,
        };
    }

    /// <summary>
    /// True when a file's current extension is an accepted spelling of the type
    /// its content was detected as (".jpeg" for ".jpg", ".m4v" for ".mp4"…), so
    /// "fix file types" leaves it alone.
    /// </summary>
    public static bool ExtensionMatches(string currentExtension, string realExtension)
    {
        string cur = currentExtension.ToLowerInvariant();
        return cur == realExtension || (cur, realExtension) switch
        {
            (".jpeg" or ".jfif" or ".jpe", ".jpg") => true,
            (".heif" or ".hif", ".heic") => true,
            (".m4v", ".mp4") or (".mp4", ".m4v") => true,
            _ => false,
        };
    }

    private static string BmffExtension(string brand) => brand switch
    {
        "avif" or "avis" => ".avif",
        "heic" or "heix" or "heim" or "heis" or "mif1" or "msf1" or "miaf" => ".heic",
        "qt  " => ".mov",
        "M4A " => ".m4a",
        "M4V " or "M4VH" or "M4VP" => ".m4v",
        "crx " => ".cr3", // Canon RAW is ISO-BMFF too — must not be "fixed" to .mp4
        var b when b.StartsWith("3gp") => ".3gp",
        var b when b.StartsWith("3g2") => ".3g2",
        _ => ".mp4",
    };

    /// <summary>
    /// Recovered files often carry leading garbage; when the head bytes match
    /// nothing, look for a JPEG or PNG signature within the first 1 MB.
    /// </summary>
    private static Kind DeepSniff(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[(int)Math.Min(stream.Length, 1 << 20)];
        int got = stream.Read(buf, 0, buf.Length);
        return DeepSniffBuffer(buf.AsSpan(0, got));
    }

    private static Kind DeepSniffBuffer(ReadOnlySpan<byte> data)
    {
        int jpeg = data.IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
        int png = data.IndexOf((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        if (jpeg < 0 && png < 0)
            return Kind.Unknown;
        if (png < 0 || (jpeg >= 0 && jpeg < png))
            return Kind.Jpeg;
        return Kind.Png;
    }

    private static ValidationReport WithBytes(string path, Func<byte[], string, ValidationReport> validate)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxInMemory)
            return new ValidationReport(path, FileHealth.Unsupported, "file too large to inspect in memory");
        return validate(File.ReadAllBytes(path), path);
    }

    private static ValidationReport ValidateAvi(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[12];
        if (stream.Read(header, 0, 12) < 12)
            return new ValidationReport(path, FileHealth.Broken, "too small");
        long declared = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8L;
        long actual = stream.Length;
        if (actual < declared)
            return new ValidationReport(path, FileHealth.Broken,
                $"truncated ({actual} of {declared} declared bytes) — may partially play; no auto-repair for videos yet");
        return new ValidationReport(path, FileHealth.Ok, "valid RIFF structure");
    }
}
