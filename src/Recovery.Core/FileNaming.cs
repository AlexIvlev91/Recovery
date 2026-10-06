namespace Recovery.Core;

/// <summary>Output-file naming helpers shared by every restore/recover path.</summary>
public static class FileNaming
{
    /// <summary>
    /// Returns <paramref name="path"/> if nothing exists there, otherwise the
    /// first free "name (1).ext", "name (2).ext", … in the same folder.
    /// </summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>Replaces characters that are illegal in a Windows file name.</summary>
    public static string SanitizeName(string name, string fallback = "recovered")
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    /// <summary>
    /// True when <paramref name="path"/> lives on volume <paramref name="driveLetter"/>.
    /// Used to warn before writing recovered data onto the drive it is being
    /// recovered from (which can overwrite the very clusters still to be read).
    /// </summary>
    public static bool IsOnDrive(string path, char driveLetter)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            return root.Length >= 2 && root[1] == ':' && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(driveLetter);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
