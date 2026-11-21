using DriveGram;
using DriveGram.Host;

namespace DriveGram.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string? folder = null;
        int i = Array.IndexOf(args, "--folder");
        if (i >= 0 && i + 1 < args.Length) folder = args[i + 1];

        var state = new AppState(folder);
        Log.Open(state.LogDir);
        Log.Verbose = args.Contains("--verbose");
        WTelegram.Helpers.Log = (level, msg) => { if (level >= 3) Log.Warn("[tg] " + msg); else Log.Debug("[tg] " + msg); };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("tray: unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("tray: fatal", e.ExceptionObject as Exception);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        int snap = Array.IndexOf(args, "--snapshot-setup");
        if (snap >= 0 && snap + 1 < args.Length)
        {
            Application.Run(new Setup.SetupWizard(state)
                { SnapshotDir = args[snap + 1], StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false });
            return;
        }
        // Lets the installer see that the app is running (Inno Setup AppMutex) before it replaces files.
        using var running = new Mutex(false, "DriveGramTrayRunning");
        Application.Run(new TrayApp(state));
        GC.KeepAlive(running);
    }
}
