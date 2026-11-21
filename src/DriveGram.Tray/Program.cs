using DriveGram;
using DriveGram.Host;

namespace DriveGram.Tray;

static class Program
{
    /// <summary>Named mutex = "the tray app is running": single instance, and the installer waits on it.</summary>
    public const string RunningMutex = "DriveGramTrayRunning";
    /// <summary>A second launch signals this, so the running copy shows itself instead.</summary>
    public const string ActivateEvent = @"Local\DriveGram-activate";

    [STAThread]
    static void Main(string[] args)
    {
        Log.Open(Path.Combine(AppState.Root, "logs"));
        Log.Verbose = args.Contains("--verbose");
        WTelegram.Helpers.Log = (level, msg) => { if (level >= 3) Log.Warn("[tg] " + msg); else Log.Debug("[tg] " + msg); };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("tray: unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("tray: fatal", e.ExceptionObject as Exception);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        int snap = Array.IndexOf(args, "--snapshot-setup");
        if (snap >= 0 && snap + 1 < args.Length) // developer aid: render the wizard pages to images, offline
        {
            if (args.Contains("--en")) L.Override("en");
            if (args.Contains("--he")) L.Override("he");
            Application.Run(new Setup.SetupWizard(AppState.CreateNew(), isNew: true)
                { SnapshotDir = args[snap + 1], StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false });
            return;
        }

        int snapDel = Array.IndexOf(args, "--snapshot-delete");
        if (snapDel >= 0 && snapDel + 1 < args.Length) // developer aid: render the delete confirmation, offline
        {
            if (args.Contains("--en")) L.Override("en");
            if (args.Contains("--he")) L.Override("he");
            var form = new DeleteDriveForm("U:", "Photos", new DeletionPreview(1234, 56, 7_340_032_000, 12, 3, true, true, "LAPTOP-2"))
                { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
            form.Shown += (_, _) =>
            {
                Application.DoEvents();
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                Directory.CreateDirectory(args[snapDel + 1]);
                bmp.Save(Path.Combine(args[snapDel + 1], "delete.png"));
                form.Close();
            };
            Application.Run(form);
            return;
        }

        int snapBak = Array.IndexOf(args, "--snapshot-backup");
        if (snapBak >= 0 && snapBak + 1 < args.Length) // developer aid: render the backup window (fake drive, offline)
        {
            if (args.Contains("--en")) L.Override("en");
            if (args.Contains("--he")) L.Override("he");
            var fake = AppState.CreateNew();
            fake.Config.Channels = new DriveGram.Remote.TelegramChannels(1, 1, 2, 2);
            fake.Config.VolumeLabel = "Photos";
            var form = new BackupForm(fake) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false, Size = new Size(780, 1500) };
            form.Shown += async (_, _) =>
            {
                await Task.Delay(1500);
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                Directory.CreateDirectory(args[snapBak + 1]);
                bmp.Save(Path.Combine(args[snapBak + 1], "backup.png"));
                form.Close();
                fake.Delete();
            };
            Application.Run(form);
            return;
        }

        using var running = new Mutex(true, RunningMutex, out bool first);
        if (!first)
        {
            // already running: bring that copy forward (it opens the drive, or setup if there is none)
            if (EventWaitHandle.TryOpenExisting(ActivateEvent, out var ev)) ev.Set();
            return;
        }

        // `--folder <path>`: developer/trial drive in a local folder, added to the list of drives
        int fi = Array.IndexOf(args, "--folder");
        if (fi >= 0 && fi + 1 < args.Length)
        {
            var trial = AppState.ForFolder(args[fi + 1]);
            if (!trial.Exists) trial.Save();
        }

        Application.Run(new TrayApp());
        GC.KeepAlive(running);
    }
}
