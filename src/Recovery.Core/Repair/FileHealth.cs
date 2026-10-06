namespace Recovery.Core.Repair;

public enum FileHealth
{
    /// <summary>Structure is valid and the full image/media data is present.</summary>
    Ok,
    /// <summary>Damaged wrapper, but our repairers can produce a fully working file.</summary>
    Repairable,
    /// <summary>
    /// Opens/plays, but part of the content is permanently missing (recovery
    /// captured only the first clusters). Best-effort output shows the portion
    /// that survived; the rest cannot be recovered from this file.
    /// </summary>
    Partial,
    /// <summary>Damaged beyond what we can fix automatically.</summary>
    Broken,
    /// <summary>Format we don't know how to inspect.</summary>
    Unsupported,
}

public sealed record ValidationReport(string Path, FileHealth Health, string Detail);

public sealed record RepairResult(string Source, FileHealth Before, bool Repaired, string? OutputPath, string Detail);
