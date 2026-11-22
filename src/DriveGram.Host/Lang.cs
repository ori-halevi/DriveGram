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

    /// <summary>A count in words: N(1, "קובץ אחד", "{0} קבצים", "1 file", "{0} files").
    /// "{0}" or "{0:N0}" stands for the number; nothing else is a placeholder, so a drive or file name
    /// with braces inside the text is safe.</summary>
    public static string N(long n, string heOne, string heMany, string enOne, string enMany) =>
        (n == 1 ? T(heOne, enOne) : T(heMany, enMany)).Replace("{0:N0}", n.ToString("N0")).Replace("{0}", n.ToString());

    /// <summary>Latin text (drive letter, path, PC name) kept in its order inside a Hebrew sentence.</summary>
    public static string Ltr(string s) => "\u200E" + s + "\u200E";

    /// <summary>A path or file name kept in one left-to-right piece inside a Hebrew sentence, even when it
    /// mixes Latin and Hebrew ("E:\", "report (\u05DC\u05D0 \u05E9\u05DC\u05DD).docx"). Embedding marks, not isolates: WinForms
    /// (GDI) draws isolate marks as boxes.</summary>
    public static string LtrPath(string s) => "\u202A" + s + "\u202C";

    /// <summary>A file name inside a sentence: a Latin name stays in one piece ("report (\u05DC\u05D0 \u05E9\u05DC\u05DD).docx");
    /// a Hebrew one is left to the sentence's own right-to-left order.</summary>
    public static string Name(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var ch in s)
        {
            if (ch is >= '\u0590' and <= '\u05FF') return s;
            if (char.IsLetter(ch)) return LtrPath(s);
        }
        return LtrPath(s);
    }

    /// <summary>Hebrew text that must read right-to-left even where the reading direction is guessed
    /// from the first letter (Windows notifications): starts with a right-to-left mark.</summary>
    public static string RtlStart(string s) => Hebrew && s.Length > 0 ? "\u200F" + s : s;

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
