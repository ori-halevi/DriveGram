using DriveGram;
using DriveGram.Host;

namespace DriveGram.Tray;

static class Program
{
    /// <summary>Named mutex = "the tray app is running": one copy per data folder (so a copy run from the
    /// source, with its own DRIVEGRAM_ROOT, runs beside the installed one), and the installer waits on it.</summary>
    public static string RunningMutex => AppState.Scoped("DriveGramTrayRunning");
    /// <summary>A second launch signals this, so the running copy shows itself instead.</summary>
    public static string ActivateEvent => AppState.Scoped(@"Local\DriveGram-activate");

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessShutdownParameters(uint level, uint flags);

    /// <summary>Developer aid: a window painting itself into a bitmap, for the screenshots.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [STAThread]
    static void Main(string[] args)
    {
        Log.Open(Path.Combine(AppState.Root, "logs"));
        Log.Verbose = args.Contains("--verbose");
        // shut down after the apps (0x100 = the lowest normal level): Word & co. save to the drive first
        try { SetProcessShutdownParameters(0x100, 0); } catch { }
        WTelegram.Helpers.Log = (level, msg) => { if (level >= 3) Log.Warn("[tg] " + msg); else Log.Debug("[tg] " + msg); };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // The saved look, before any window exists: it also decides what Windows itself draws dark.
        // After SetCompatibleTextRenderingDefault, which must come first of all.
        Setup.Theme.Load();
        int themeArg = Array.IndexOf(args, "--theme");
        if (themeArg >= 0 && themeArg + 1 < args.Length) Setup.Theme.Override(args[themeArg + 1]); // developer aid
        if (args.Contains("--en")) L.Override("en");
        if (args.Contains("--he")) L.Override("he");
        Application.ThreadException += (_, e) => Log.Error("tray: unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("tray: fatal", e.ExceptionObject as Exception);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // developer aid: the main window, every tab, in a chosen look — no Telegram, no drive, and in a
        // throw-away data folder, so nothing of the user's is touched
        int snapMain = Array.IndexOf(args, "--snapshot-main");
        if (snapMain >= 0 && snapMain + 1 < args.Length)
        {
            var host = new SampleHost(SynchronizationContext.Current!);
            var main = new MainForm(host)
            {
                // off screen: the pictures are of this window alone, never of the desktop
                StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false,
            };
            main.Shown += async (_, _) =>
            {
                var dir = args[snapMain + 1];
                Directory.CreateDirectory(dir);
                // PrintWindow with "render full content": the window paints itself into the picture, and
                // nothing else on screen is involved. What Windows paints inside a LIST (its rows and
                // column headers) stays out of such a picture whatever we do, so a list looks empty here.
                void Save(string name)
                {
                    using var bmp = new Bitmap(main.Width, main.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var hdc = g.GetHdc();
                        try { PrintWindow(main.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    // a Hebrew window is mirrored by Windows itself, and PrintWindow copies it mirrored:
                    // flipping the picture back gives exactly what the window looks like on screen
                    if (L.Rtl) bmp.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    bmp.Save(Path.Combine(dir, $"main-{Setup.Theme.Name(Setup.Theme.Kind)}-{name}.png"));
                }
                main.DemoTransfers(0);
                foreach (var tab in Enum.GetValues<MainForm.Tab>())
                {
                    main.Select(tab);
                    await Task.Delay(500);
                    Application.DoEvents();
                    Save(tab.ToString().ToLowerInvariant());
                    if (tab != MainForm.Tab.Transfers) continue;
                    main.DemoTransfers(1);
                    await Task.Delay(500);
                    Application.DoEvents();
                    Save("transfers-files");
                }
                main.Close();
            };
            Application.Run(main);
            return;
        }

        int snapCountry = Array.IndexOf(args, "--snapshot-countries");
        if (snapCountry >= 0 && snapCountry + 1 < args.Length) // developer aid: the phone field's country list
        {
            var picker = new Setup.PhoneBox.CountryPicker(Setup.Countries.Here())
                { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
            picker.Shown += (_, _) =>
            {
                Directory.CreateDirectory(args[snapCountry + 1]);
                using var bmp = new Bitmap(picker.Width, picker.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { PrintWindow(picker.Handle, hdc, 2); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (L.Rtl) bmp.RotateFlip(RotateFlipType.RotateNoneFlipX);
                bmp.Save(Path.Combine(args[snapCountry + 1], $"countries-{Setup.Theme.Name(Setup.Theme.Kind)}.png"));
                picker.Close();
            };
            Application.Run(picker);
            return;
        }

        int snap = Array.IndexOf(args, "--snapshot-setup");
        if (snap >= 0 && snap + 1 < args.Length) // developer aid: render the wizard pages to images, offline
        {
            Application.Run(new Setup.SetupWizard(AppState.CreateNew(), isNew: true)
                { SnapshotDir = args[snap + 1], StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false });
            return;
        }

        int snapDel = Array.IndexOf(args, "--snapshot-delete");
        if (snapDel >= 0 && snapDel + 1 < args.Length) // developer aid: render the delete confirmation, offline
        {
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
