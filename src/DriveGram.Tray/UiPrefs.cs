using System.Text.Json;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>Small view choices remembered between runs (how the lists are sorted, the files filter). Losing
/// them costs nothing: any error just means the defaults.</summary>
sealed class UiPrefs
{
    public int UpSortColumn { get; set; } = -1;
    public bool UpSortDescending { get; set; }
    public int LocalSortColumn { get; set; } = -1;
    public bool LocalSortDescending { get; set; }
    public int LocalFilter { get; set; }

    static string FilePath => Path.Combine(AppState.Root, "ui-prefs.json");

    public static UiPrefs Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { Log.Debug($"ui prefs: {ex.Message}"); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { Log.Debug($"ui prefs: {ex.Message}"); }
    }
}
