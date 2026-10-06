namespace Recovery.Core.DiskUsage;

/// <summary>Broad file kinds used to break disk usage down. Order is the display order.</summary>
public enum FileCategory : byte
{
    Video,
    Image,
    Audio,
    Document,
    Archive,
    Program,
    Other,
}

public static class FileCategories
{
    public static readonly IReadOnlyList<FileCategory> All = Enum.GetValues<FileCategory>();

    public static int Count => All.Count;

    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    public static FileCategory Of(string fileName) => Of(fileName, long.MaxValue);

    /// <summary>
    /// Category by extension; <paramref name="size"/> disambiguates ".ts",
    /// which is both an MPEG transport stream and a TypeScript source file —
    /// generated code can reach a few MB, real recordings are far larger.
    /// </summary>
    public static FileCategory Of(string fileName, long size)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1 || fileName.Length - dot > 16)
            return FileCategory.Other;
        if (!ByExtension.TryGetValue(fileName[dot..], out var c))
            return FileCategory.Other;
        if (c == FileCategory.Video && string.Compare(fileName, dot, ".ts", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
            fileName.Length - dot == 3 && (size < 8L * 1024 * 1024 || fileName.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)))
            return FileCategory.Other;
        return c;
    }

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Video => "Videos",
        FileCategory.Image => "Images",
        FileCategory.Audio => "Audio",
        FileCategory.Document => "Documents",
        FileCategory.Archive => "Archives & disk images",
        FileCategory.Program => "Apps & system files",
        _ => "Other",
    };

    public static string ShortLabel(FileCategory c) => c switch
    {
        FileCategory.Archive => "Archives",
        FileCategory.Program => "Apps & system",
        _ => Label(c),
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts)
        {
            foreach (var e in exts)
                map[e] = c;
        }

        foreach (var e in MediaExtensions.Video)
            map[e] = FileCategory.Video;
        Add(FileCategory.Video, ".vob", ".ogv", ".divx", ".rm", ".rmvb", ".asf", ".f4v", ".m2v", ".mxf", ".dav");

        foreach (var e in MediaExtensions.Photo)
            map[e] = FileCategory.Image;
        Add(FileCategory.Image, ".svg", ".ico", ".psd", ".xcf", ".ai", ".eps", ".jxl", ".jp2", ".tga", ".dds", ".exr", ".hdr", ".pef", ".x3f", ".kdc");

        Add(FileCategory.Audio, ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff",
            ".alac", ".ape", ".mid", ".midi", ".amr", ".m4b", ".dsf", ".dff", ".mka");

        Add(FileCategory.Document, ".pdf", ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".xlsb", ".ppt", ".pptx",
            ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".csv", ".tsv", ".epub", ".mobi", ".azw3", ".djvu", ".xps",
            ".oxps", ".pages", ".numbers", ".key", ".one", ".tex", ".vsdx", ".pub", ".msg", ".eml", ".pst", ".ost");

        Add(FileCategory.Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz", ".lzma",
            ".cab", ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2", ".dmg", ".wim", ".esd", ".swm", ".arc");

        Add(FileCategory.Program, ".exe", ".dll", ".sys", ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle",
            ".ocx", ".drv", ".cpl", ".mui", ".efi", ".com", ".scr", ".jar", ".apk", ".so", ".lib", ".node", ".pak",
            ".pdb", ".winmd", ".cat", ".pyd", ".etl", ".evtx", ".edb", ".blf", ".regtrans-ms");
        return map;
    }
}
