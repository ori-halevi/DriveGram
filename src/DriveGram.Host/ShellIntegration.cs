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
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{TrayExe}\"");
        else k.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static void SetDriveIcon(string mountPoint, string label)
    {
        if (!File.Exists(IconPath)) return;
        var letter = mountPoint.TrimEnd(':', '\\').ToUpperInvariant();
        using (var k = Registry.CurrentUser.CreateSubKey($@"{DrivesKey}\{letter}\DefaultIcon")) k.SetValue("", IconPath);
        using (var k = Registry.CurrentUser.CreateSubKey($@"{DrivesKey}\{letter}\DefaultLabel")) k.SetValue("", label);
        Refresh();
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
