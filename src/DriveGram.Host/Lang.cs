using System.Globalization;
using System.Text.Json;

namespace DriveGram.Host;

/// <summary>
/// UI language: Hebrew or English. "auto" follows the Windows display language (Hebrew when Windows
/// is in Hebrew, English otherwise); the user can override it from the tray menu.
/// Strings are written inline as L.T("עברית", "English") so both versions sit side by side.
/// </summary>
public static class L
{
    public static string Preference { get; private set; } = "auto";
    public static bool Hebrew { get; private set; }
    public static bool Rtl => Hebrew;

    static L() => Load();

    public static string T(string hebrew, string english) => Hebrew ? hebrew : english;

    static void Apply()
    {
        Hebrew = Preference switch
        {
            "he" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "he" or "iw",
        };
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(Settings.Path))
                Preference = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.Path))?.Language ?? "auto";
        }
        catch { Preference = "auto"; }
        Apply();
    }

    /// <summary>Use a language for this run only (not saved).</summary>
    public static void Override(string preference)
    {
        Preference = preference;
        Apply();
    }

    public static void Set(string preference)
    {
        Preference = preference is "he" or "en" ? preference : "auto";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Settings.Path)!);
        File.WriteAllText(Settings.Path, JsonSerializer.Serialize(new Settings { Language = Preference }));
        Apply();
    }

    sealed class Settings
    {
        public static string Path => System.IO.Path.Combine(AppState.Root, "settings.json");
        public string Language { get; set; } = "auto";
    }
}
