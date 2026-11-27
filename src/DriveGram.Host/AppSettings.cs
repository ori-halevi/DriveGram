using System.Text.Json;

namespace DriveGram.Host;

/// <summary>
/// The settings that belong to the program itself, not to one drive: the language and the look
/// (%LOCALAPPDATA%\DriveGram\settings.json). One file read and written in one place, so saving the
/// language can never drop the theme (or a field a newer version added).
///
/// The installer writes this file once, on a first install, with what the user chose there; an upgrade
/// leaves it alone, so a choice made later in the app survives.
/// </summary>
public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "he" or "en".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>"auto" (follow Windows' light/dark), "light", "dark" or "rose".</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>Fields a newer version added: kept verbatim, so an older one never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static string Path => System.IO.Path.Combine(AppState.Root, "settings.json");

    static readonly object Gate = new();
    // written as "Language"/"Theme" (what every version so far wrote and reads), but read whatever the
    // case: a file written by hand, or by a version that chose another style, still works
    static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        lock (Gate)
        {
            return Of(Path) ?? Of(Path + ".bak") ?? new AppSettings();
        }
    }

    static AppSettings? Of(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Read);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings: {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Change one field and save, keeping everything else in the file (also fields this version
    /// does not know). Atomic: a power cut leaves the old file or the new one.</summary>
    public static void Update(Action<AppSettings> change)
    {
        lock (Gate)
        {
            var s = Load();
            change(s);
            try
            {
                Directory.CreateDirectory(AppState.Root);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, Write));
                // the copy it replaces is kept: a file damaged by a power cut is not the end of the choices
                if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak", ignoreMetadataErrors: true);
                else File.Move(tmp, Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"settings: could not be saved ({ex.Message})");
            }
        }
    }
}
