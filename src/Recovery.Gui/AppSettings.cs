using System.Text.Json;

namespace Recovery.Gui;

/// <summary>
/// Tiny persisted key/value store (%LOCALAPPDATA%\Recovery\settings.json) for
/// conveniences like the last destination folder and window size. Failures
/// are ignored — settings are never essential.
/// </summary>
internal static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recovery", "settings.json");

    private static Dictionary<string, string>? _values;

    private static Dictionary<string, string> Values
    {
        get
        {
            if (_values is not null)
                return _values;
            try
            {
                if (File.Exists(FilePath))
                    _values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            return _values ??= [];
        }
    }

    public static string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public static string Get(string key, string fallback) => Get(key) is { Length: > 0 } v ? v : fallback;

    public static void Set(string key, string? value)
    {
        if (value is null)
            Values.Remove(key);
        else
            Values[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string DefaultFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
}
