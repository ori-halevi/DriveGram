using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DriveGram.Host;

/// <summary>Per-user Windows integration: start with Windows, drive icon and name in "This PC".
/// Everything lives under HKCU, so no admin rights are needed.</summary>
public static class ShellIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "DriveGram";
    const string DrivesKey = @"Software\Classes\Applications\Explorer.exe\Drives";

    public static string AppDir => AppContext.BaseDirectory;
    public static string TrayExe => Path.Combine(AppDir, "drivegram-tray.exe");
    public static string IconPath => Path.Combine(AppDir, "drivegram.ico");

    public static bool AutostartEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string s && s.Contains("drivegram-tray", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunValue, $"\"{TrayExe}\"");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        // the user's choice, kept with the drives: an uninstall/reinstall that lost the setting is noticed
        try
        {
            if (on) { Directory.CreateDirectory(AppState.Root); File.WriteAllText(AutostartWantedPath, "1"); }
            else File.Delete(AutostartWantedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string AutostartWantedPath => Path.Combine(AppState.Root, "autostart.wanted");

    /// <summary>The user chose to start DriveGram with Windows, but the setting is gone (removed by an
    /// uninstall whose reinstall could not bring it back).</summary>
    public static bool AutostartLost => !AutostartEnabled && File.Exists(AutostartWantedPath);

    /// <summary>The user said not to bring it back: stop asking.</summary>
    public static void ForgetAutostartChoice()
    {
        try { File.Delete(AutostartWantedPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void SetDriveIcon(string mountPoint, string label)
    {
        if (!File.Exists(IconPath)) return;
        var letter = mountPoint.TrimEnd(':', '\\').ToUpperInvariant();
        using (var k = Registry.CurrentUser.CreateSubKey($@"{DrivesKey}\{letter}\DefaultIcon")) k.SetValue("", IconPath);
        using (var k = Registry.CurrentUser.CreateSubKey($@"{DrivesKey}\{letter}\DefaultLabel")) k.SetValue("", label);
        Refresh();
    }

    /// <summary>Remove DriveGram's icon/label from letters no drive uses (a borrowed letter left after a
    /// crash would show any USB stick as "DriveGram").</summary>
    public static void ClearStaleDriveIcons(IEnumerable<string> ownLetters)
    {
        try
        {
            var keep = ownLetters.Select(m => m.TrimEnd(':', '\\').ToUpperInvariant()).ToHashSet();
            using var drives = Registry.CurrentUser.OpenSubKey(DrivesKey);
            if (drives is null) return;
            foreach (var letter in drives.GetSubKeyNames())
            {
                if (keep.Contains(letter.ToUpperInvariant())) continue;
                if (IsDriveGramVolume(letter)) continue; // a DriveGram drive of another profile set, mounted now
                using var icon = drives.OpenSubKey($@"{letter}\DefaultIcon");
                if (icon?.GetValue("") is string path && path.Contains("drivegram", StringComparison.OrdinalIgnoreCase))
                    Registry.CurrentUser.DeleteSubKeyTree($@"{DrivesKey}\{letter}", throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex) { Log.Warn($"drive icons: {ex.Message}"); }
    }

    /// <summary>Is a DriveGram drive mounted on this letter right now (any profile, any program copy)?</summary>
    public static bool IsDriveGramVolume(string mountPoint)
    {
        try
        {
            var d = new DriveInfo(mountPoint.TrimEnd(':', '\\') + ":\\");
            return d.IsReady && string.Equals(d.DriveFormat, "DriveGram", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static void ClearDriveIcon(string mountPoint)
    {
        var letter = mountPoint.TrimEnd(':', '\\').ToUpperInvariant();
        Registry.CurrentUser.DeleteSubKeyTree($@"{DrivesKey}\{letter}", throwOnMissingSubKey: false);
        Refresh();
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tell Explorer that icons/associations changed, so "This PC" redraws.</summary>
    static void Refresh() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
