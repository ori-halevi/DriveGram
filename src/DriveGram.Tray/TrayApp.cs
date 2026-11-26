using System.Diagnostics;
using DriveGram.Crypto;
using DriveGram.Host;
using DriveGram.Remote;
using DriveGram.Sync;
using Microsoft.Win32;
using Keys = DriveGram.Crypto.Keys;

namespace DriveGram.Tray;

/// <summary>One drive (profile) as the tray sees it.</summary>
sealed class DriveController
{
    public AppState State { get; }
    public DriveSession? Session;
    public bool Starting, Stopping;
    /// <summary>Short reason it is not mounted (shown in the menu), or null.</summary>
    public string? Problem;
    /// <summary>Not mountable until the user goes through setup (signed out, no drive, channels gone).</summary>
    public bool NeedsSetup;
    public int RetryDelaySeconds = 15;
    public System.Windows.Forms.Timer? RetryTimer;
    public TrashForm? Trash;
    public BackupForm? Backup;
    public SecurityForm? Security;
    public LetterNameForm? LetterName;
    public volatile bool FreeingOffline; // temporary copies being deleted while disconnected: no connecting meanwhile
    public bool ChangingLetter;           // the letter / name window, through to the reconnect
    /// <summary>The saved channels did not match: the next setup shows the list of drives.</summary>
    public bool MustChooseDrive;
    public ExportForm? ExportWindow;
    public HandoverWaitForm? HandoverWait;
    public HandoverAskForm? HandoverAsk;
    /// <summary>The writable state the user was last told about (no second balloon for the same news).</summary>
    public bool? ToldWritable;
    /// <summary>When the user was last told that Telegram cannot be reached for a file (Environment.TickCount64).</summary>
    public long ToldUnreachableAt;
    /// <summary>The failing-uploads balloon already shown for this spell (its starting time), or null.</summary>
    public DateTime? ToldUploadsFailing;
    // ── one "upload session": from the first file waiting to upload until everything is in Telegram ──
    public bool Uploading;            // a session is running
    public DateTime UploadSince;      // when it started
    public long UploadedAtStart;      // the engine's byte counter then
    public int MostFiles;             // the most files waiting at once (for "N files uploaded")
    public Dictionary<string, bool>? ShellSeen; // files not in Telegram yet (paused?) as Explorer's icons last showed them
    public DriveSession? ShellSeenFor;
    public volatile bool ShellBusy;      // a look is running (in the background)
    public bool ToldUploadStart;      // the "uploading, wait for the message" balloon was shown
    public DateTime DiskShortQuietUntil;
    public SwitchAccountForm? SwitchAccount;
    /// <summary>The user paused every upload of this drive: saved with the drive, so it holds after Settings,
    /// an account switch or a restart.</summary>
    public bool ManualPaused
    {
        get => State.Config.UploadsPaused;
        set
        {
            if (State.Config.UploadsPaused == value) return;
            State.Config.UploadsPaused = value;
            try { State.Save(); } catch (Exception ex) { Log.Warn($"saving the pause: {ex.Message}"); }
        }
    }
    public Form? DiskShortNotice;
    public long SampleBytes;          // last sample of the byte counter, for the speed
    public DateTime SampleAt;
    public double Speed;              // bytes/s, smoothed
    /// <summary>Keys just unlocked in the setup wizard: the first mount uses them instead of asking again.</summary>
    public Keys? PendingKeys;
    /// <summary>The last mount failed because Windows refused it (not offline, not a password).</summary>
    public bool LastMountRefused;
    /// <summary>A refused mount is explained by the caller (a new network drive: the user is asked what to
    /// do), so no "could not connect, is the letter taken?" balloon first.</summary>
    public bool QuietRefusal;
    /// <summary>Files kept as "(incomplete)" waiting for one shared balloon.</summary>
    public readonly List<(string Orig, string Kept, string Folder)> Incomplete = [];

    public DriveController(AppState state) => State = state;

    public string Letter => Session?.MountPoint ?? State.Config.MountPoint;
    public string Name => $"{L.Ltr(Letter)} {State.Config.VolumeLabel}";
    public bool Busy => !Stopping && Session is { } s && (s.Status.UploadsInFlight > 0 || s.Engine.PendingNotPaused() > 0);
}

/// <summary>
/// The notification-area app. One running copy per Windows user; it mounts every drive configured
/// on this PC (each on its own Telegram account) and shows what each is doing.
/// </summary>
sealed class TrayApp : ApplicationContext, IMainHost
{
    readonly SynchronizationContext _ui;
    readonly DailyUploads _daily = new();
    readonly Preparer _preparer;
    ShellBridge? _bridge;
    readonly NotifyIcon _icon;
    readonly Icon _idle, _busy;
    readonly ContextMenuStrip _menu = new() { Font = Ui.Font };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly List<DriveController> _drives;
    readonly EventWaitHandle _activate, _quit;
    readonly System.Windows.Forms.Timer _clickWait = new();
    Setup.SetupWizard? _wizard;
    bool _quitting;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current!;
        _preparer = new Preparer(_ui);
        _preparer.Finished += OnPrepared;
        var dir = AppContext.BaseDirectory;
        _idle = new Icon(Path.Combine(dir, "drivegram.ico"), SystemInformation.SmallIconSize);
        _busy = new Icon(Path.Combine(dir, "drivegram-busy.ico"), SystemInformation.SmallIconSize);
        foreach (var s in AppState.All()) TelegramAccounts.MigrateLegacy(s); // 0.8: one shared sign-in per account
        _drives = AppState.All().Select(s => new DriveController(s)).ToList();

        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _updates.Changed += () => _ui.Post(_ => OnUpdatesChanged(), null);
        _menu.Renderer = Setup.ThemePaint.Renderer;
        _icon = new NotifyIcon { Icon = _idle, Text = "DriveGram", ContextMenuStrip = _menu, Visible = true };
        // One click opens the menu, two open the window. A second click can only be told from the first by
        // waiting out Windows' own double-click time, so the menu appears a moment after the click.
        _clickWait.Interval = Math.Max(200, SystemInformation.DoubleClickTime);
        _clickWait.Tick += (_, _) => { _clickWait.Stop(); ShowTrayMenu(); };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) { _clickWait.Stop(); _clickWait.Start(); } };
        _icon.DoubleClick += (_, _) => { _clickWait.Stop(); ShowMain(DriveGram.Tray.MainForm.Tab.Transfers); };
        Setup.Theme.Changed += () => _ui.Post(_ => OnLookChanged(), null);
        _timer.Tick += (_, _) => UpdateIcon();
        _timer.Start();
        SystemEvents.SessionEnding += (_, _) => OnSessionEnding();
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _updates.Nudge(); };
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) _updates.Nudge(); };
        SystemEvents.SessionEnded += (_, _) => OnSessionEnded();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) _ui.Post(async _ => { await Task.Delay(3000); RetryWaitingDrivesNow(); }, null);
        };
        ShellIntegration.ClearStaleDriveIcons(_drives.Select(x => x.State.Config.MountPoint)); // left by a crash on a borrowed letter
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _ui.Post(_ => OnResume(), null); };

        // Explorer's overlay icons and right-click menu ask here (the shell extension)
        try
        {
            _bridge = new ShellBridge
            {
                Resolve = ResolveShellPath,
                Prepare = paths => _ui.Post(_ => PrepareFiles(paths.Select(ResolveShellPath).Where(f => f is not null).Select(f => f!.Value)), null),
                Free = paths => _ui.Post(_ => _ = FreeFiles(paths.Select(ResolveShellPath).Where(f => f is not null).Select(f => f!.Value)), null),
                IsPreparing = _preparer.IsPreparing,
            };
            _bridge.Start();
        }
        catch (Exception ex) { Log.Warn($"shell bridge: {ex.Message}"); }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEvent);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, DriveSession.QuitEventName);
        Listen(_activate, () => Activate());
        Listen(_quit, async () => await QuitAsync(ask: false));

        if (_drives.Count == 0) _ui.Post(_ => ShowMain(), null); // the first run: the accounts tab, "add your first account"
        else
        {
            foreach (var d in _drives) _ = MountAsync(d);
            _ui.Post(async _ =>
            {
                // after the drives started (a password may be asked meanwhile): one question at a time
                await Task.Delay(TimeSpan.FromSeconds(5));
                for (int i = 0; i < 120 && _drives.Any(x => x.Starting); i++) await Task.Delay(1000);
                AskToRestoreAutostart();
            }, null);
        }
    }

    /// <summary>"Start with Windows" was on, and an uninstall/reinstall removed it: ask once to bring it back.</summary>
    void AskToRestoreAutostart()
    {
        bool lost;
        try { lost = ShellIntegration.AutostartLost; } catch { return; }
        if (!lost || _quitting || _drives.Count == 0) return;
        if (Ui.Ask(L.T("DriveGram הופעל קודם אוטומטית עם וינדוס, אבל ההגדרה הזו נמחקה (כנראה בהסרה והתקנה מחדש).\n\nלהפעיל אותו שוב אוטומטית עם וינדוס?",
                       "DriveGram used to start with Windows, but that setting was removed (probably by an uninstall and reinstall).\n\nStart it with Windows again?"),
                icon: MessageBoxIcon.Question) == DialogResult.Yes)
        {
            try { ShellIntegration.SetAutostart(true); } catch (Exception ex) { Log.Warn($"autostart: {ex.Message}"); }
        }
        else ShellIntegration.ForgetAutostartChoice();
    }

    /// <summary>Windows asks to end the session (DriveGram is asked after the other apps): send what is
    /// queued, but keep every drive mounted — the shutdown can still be cancelled.</summary>
    void OnSessionEnding()
    {
        var sessions = _drives.Select(x => x.Session).OfType<DriveSession>().ToList();
        Parallel.ForEach(sessions, s => s.PrepareForShutdown());
    }

    /// <summary>The session really ends: unmount everything within Windows' few seconds.</summary>
    void OnSessionEnded()
    {
        _quitting = true;
        var sessions = _drives.Select(x => x.Session).OfType<DriveSession>().ToList();
        Parallel.ForEach(sessions, s => s.EndForShutdown());
        _icon.Visible = false;
        Environment.Exit(0); // nothing is left running half-stopped (Restart Manager waits for this)
    }

    void RetryWaitingDrivesNow()
    {
        foreach (var d in _drives)
            if (d.Session is null && d.RetryTimer is not null && !d.Starting && !_quitting)
            {
                d.RetryDelaySeconds = 15;
                _ = MountAsync(d);
            }
    }

    void OnResume()
    {
        Log.Info("tray: the PC woke up");
        TelegramAccounts.WakeUp();
        foreach (var d in _drives)
        {
            d.Session?.Sync.Wake();
            if (d.Session is null && d.RetryTimer is not null && !d.Starting) { d.RetryDelaySeconds = 15; _ = MountAsync(d); }
        }
    }

    void Listen(EventWaitHandle ev, Action onSignal) => new Thread(() =>
    {
        try { while (ev.WaitOne()) _ui.Post(_ => onSignal(), null); }
        catch (ObjectDisposedException) { }
    }) { IsBackground = true }.Start();

    // ── mount / unmount ──────────────────────────────────────────────────────
    async Task MountAsync(DriveController d)
    {
        if (d.Session is not null || d.Starting || _quitting || !_drives.Contains(d)) return;
        while (d.FreeingOffline) await Task.Delay(200); // a few seconds at most
        if (d.Session is not null || d.Starting || _quitting || !_drives.Contains(d)) return;
        d.RetryTimer?.Stop();
        d.Starting = true;
        d.LastMountRefused = false;
        d.Problem = null;
        d.NeedsSetup = false;
        UpdateIcon();
        try
        {
            bool capHolds = _daily.CapReached;
            var session = await Task.Run(() => DriveSession.StartAsync(d.State, ctrl => UnlockAsync(d, ctrl), null, takeOver: false, startPaused: capHolds));
            if (!_drives.Contains(d) || _quitting)
            {
                // the drive was replaced (settings) or removed while it was starting: never leave it orphaned
                await Task.Run(() => session.StopAsync(waitForUploads: false));
                await session.DisposeAsync();
                return;
            }
            d.Session = session;
            ApplyPause(d); // the daily cap (or the user's pause) holds from the first moment
            StopRetrying(d);
            d.RetryDelaySeconds = 15;
            d.RetryDelaySeconds = 15;
            session.StopRequested += () => _ui.Post(async _ => await StopAsync(d, ask: false), null);
            session.SignedOut += () => _ui.Post(async _ => await OnSignedOutAsync(d), null);
            session.Sync.HandoverRequested += r => _ui.Post(async _ => await OnHandoverRequestedAsync(d, session, r), null);
            session.Engine.RemoteUnreachable += () => _ui.Post(_ => OnRemoteUnreachable(d, session), null);
            session.Engine.LocalDiskShort += (root, free) => _ui.Post(_ => OnLocalDiskShort(d, session, root, free), null);
            session.Sync.FileIncomplete += (orig, kept, folderId) => _ui.Post(async _ => await OnFileIncompleteAsync(d, session, orig, kept, folderId), null);
            if (session.Sync.NeedsUpdate)
                Balloon(L.T("צריך לעדכן את DriveGram", "Update DriveGram"),
                    L.T($"הכונן {L.Ltr(session.MountPoint)} נכתב בגרסה חדשה יותר של DriveGram. עד שתתקין אותה, הוא פתוח לקריאה בלבד.",
                        $"{session.MountPoint} was written by a newer version of DriveGram. It is read-only until you install that version."), ToolTipIcon.Warning);
            d.ToldWritable = session.Writable;
            session.Sync.WritableChanged += () => _ui.Post(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (d.Session is not { } s || s.Sync.HandingOver || s.Writable == d.ToldWritable) return; // flipped back / already told
                d.ToldWritable = s.Writable;
                Balloon(s.Writable ? L.T($"{L.Ltr(d.Letter)} ניתן לכתיבה", $"{d.Letter} is writable") : L.T($"{L.Ltr(d.Letter)} לקריאה בלבד", $"{d.Letter} is read-only"),
                    s.Writable ? L.T("אפשר לשמור קבצים בכונן.", "Files can be saved on the drive.") : ReadOnlyWhy(s),
                    ToolTipIcon.Warning, () => OpenDrive(d));
            }, null);

            string text = session.Writable
                ? L.T($"הכונן זמין ב־{L.Ltr(session.MountPoint)}", $"The drive is available as {session.MountPoint}")
                : $"{L.Ltr(session.MountPoint)} – {ReadOnlyWhy(session)}";
            if (session.LetterChanged is { } moved)
                text += L.T($"\nהאות הרגילה תפוסה כרגע ({L.Ltr(moved)}); בפעם הבאה הכונן ינסה אותה שוב.", $"\nThe usual letter is taken right now ({moved}); next time the drive tries it again.");
            if (!session.Sync.NeedsUpdate) Balloon(L.T("DriveGram מחובר", "DriveGram connected"), text, onClick: () => OpenDrive(d));
        }
        catch (PasswordNotGivenException) { StopRetrying(d); d.Problem = L.T("לא הוזנה סיסמה", "no password entered"); }
        catch (MountFailedException ex)
        {
            d.LastMountRefused = true;
            d.Problem = Short(ex.Message);
            if (!d.QuietRefusal) Balloon(L.T("לא ניתן לחבר את הכונן", "The drive could not be connected"), ex.Message, ToolTipIcon.Error,
                () => _ = ChangeLetterAndNameAsync(d)); // the message suggests another letter
        }
        catch (NotSignedInException)
        {
            d.NeedsSetup = true;
            d.Problem = L.T("צריך להתחבר לטלגרם", "needs Telegram sign-in");
            OfferSetup(d, L.T($"{d.Name}: צריך להתחבר לטלגרם.", $"{d.Name}: Telegram sign-in needed."));
        }
        catch (NoDriveException)
        {
            d.NeedsSetup = true;
            d.Problem = L.T("ההגדרה לא הושלמה", "setup not finished");
            OfferSetup(d, L.T($"{d.Name}: ההגדרה לא הושלמה.", $"{d.Name}: setup is not finished."));
        }
        catch (DriveVanishedException)
        {
            d.NeedsSetup = true;
            d.Problem = L.T("הכונן לא נמצא בחשבון הטלגרם", "the drive was not found in the Telegram account");
            _pendingSetup = d; // clicking the balloon opens its settings
            Balloon(L.T("הכונן לא נמצא בטלגרם", "Drive not found in Telegram"),
                L.T($"{d.Name} לא נמצא בחשבון הטלגרם (אולי הערוצים שלו נמחקו, או שהחשבון הוסר מהם). לחץ כאן כדי להחליט מה לעשות.",
                    $"{d.Name} was not found in the Telegram account (its channels may have been deleted, or the account removed from them). Click here to decide what to do."),
                ToolTipIcon.Error, () => _ = SetupAsync(d));
        }
        catch (NeedsUpdateException)
        {
            d.Problem = L.T("צריך לעדכן את DriveGram", "DriveGram needs an update");
            Balloon(L.T("צריך לעדכן את DriveGram", "Update DriveGram"),
                L.T($"{d.Name} נכתב בגרסה חדשה יותר של DriveGram. התקן את הגרסה החדשה כדי לפתוח אותו.",
                    $"{d.Name} was written by a newer DriveGram. Install the new version to open it."), ToolTipIcon.Warning);
        }
        catch (AlreadyMountedException) { d.Problem = L.T("פתוח כבר בחלון אחר", "already open in another window"); }
        catch (WinFspMissingException ex)
        {
            d.Problem = L.T("WinFsp לא מותקן", "WinFsp is not installed");
            Balloon(L.T("חסר רכיב מערכת", "A system component is missing"), ex.Message, ToolTipIcon.Error);
        }
        catch (DrivePairingException ex)
        {
            d.MustChooseDrive = true;
            d.NeedsSetup = true;
            d.Problem = L.T("צריך לבחור את הכונן מחדש", "the drive must be chosen again");
            OfferSetup(d, $"{d.Name}: {ex.Message}");
        }
        catch (WrongPasswordException) { d.Problem = L.T("סיסמה שגויה", "wrong password"); }
        catch (Exception ex) when (IsLocalProblem(ex))
        {
            Log.Error($"tray: mounting {d.Name} failed (local problem)", ex);
            d.Problem = L.T("לא ניתן לפתוח את הכונן במחשב הזה (בעיה מקומית)", "the drive could not be opened on this PC (a local problem)");
            Balloon(L.T("לא ניתן לפתוח את הכונן", "The drive could not be opened"),
                L.T($"{d.Name}: בעיה במחשב הזה, לא באינטרנט ({Short(Setup.TelegramErrors.Text(ex))}). פרטים ביומן הפעילות: בתפריט של האייקון שליד השעון ← \"פתח את תיקיית יומן הפעילות\".",
                    $"{d.Name}: a problem on this PC, not the internet ({Short(Setup.TelegramErrors.Text(ex))}). Details in the log: the menu of the icon next to the clock → \"Open the log folder\"."),
                ToolTipIcon.Error, () => OpenFolder(Path.Combine(AppState.Root, "logs")));
        }
        catch (Exception ex)
        {
            // most likely no internet yet (e.g. right after boot): retry with backoff
            Log.Warn($"tray: mounting {d.Name} failed: {ex.Message}");
            d.Problem = L.T($"ממתין לחיבור… ({Short(Setup.TelegramErrors.Text(ex))})", $"waiting to connect… ({Short(Setup.TelegramErrors.Text(ex))})");
            ScheduleRetry(d);
        }
        finally
        {
            d.Starting = false;
            UpdateIcon();
        }
    }

    /// <summary>Files kept as "(incomplete)" arrive in bursts: one balloon per burst, naming the file and
    /// its folder when there is only one.</summary>
    async Task OnFileIncompleteAsync(DriveController d, DriveSession s, string orig, string kept, string folderId)
    {
        string folder;
        try { folder = s.MountPoint + FolderPath(s, folderId); }
        catch { folder = s.MountPoint + "\\"; }
        d.Incomplete.Add((orig, kept, folder));
        if (d.Incomplete.Count > 1) return; // a balloon for this burst is already on its way
        await Task.Delay(1500);
        var list = d.Incomplete.ToList();
        d.Incomplete.Clear();
        if (d.Session != s) return;
        var open = list[0].Folder;
        if (list.Count == 1)
        {
            var (o, k, f) = list[0];
            Balloon(L.T("קובץ נשמר חלקית", "A file was saved only in part"),
                L.T($"חלק מהגרסה של \"{L.Name(o)}\" ששמרת במחשב הזה אבד בזמן שחיכתה לשליחה. מה שנשאר נשמר בשם \"{L.Name(k)}\" (בתיקייה {L.LtrPath(f)}), וחסרים בו חלקים.",
                    $"Part of the version of \"{o}\" you saved on this PC was lost while it waited to be sent. What is left was kept as \"{k}\" (in {f}); parts of it are missing."),
                ToolTipIcon.Warning, () => OpenFolder(open));
        }
        else
            Balloon(L.N(list.Count, "קובץ נשמר חלקית", "{0} קבצים נשמרו חלקית", "A file was saved only in part", "{0} files were saved only in part"),
                L.T($"חלק מהגרסאות ששמרת במחשב הזה אבדו בזמן שחיכו לשליחה. מה שנשאר נשמר בשמות שמסתיימים ב־\"{s.Sync.IncompleteSuffix.Trim()}\", וחסרים בהם חלקים.",
                    $"Parts of versions you saved on this PC were lost while they waited to be sent. What is left was kept under names ending in \"{s.Sync.IncompleteSuffix.Trim()}\"; parts of them are missing."),
                ToolTipIcon.Warning, () => OpenFolder(open));
    }

    /// <summary>A folder's path inside the drive ("\a\b"), from the index.</summary>
    static string FolderPath(DriveSession s, string id)
    {
        var parts = new List<string>();
        var n = s.Db.Get(id);
        for (int guard = 0; n is not null && !n.IsRoot && guard < 4096; guard++)
        {
            parts.Add(n.Name);
            n = n.Parent is null ? null : s.Db.Get(n.Parent);
        }
        parts.Reverse();
        return "\\" + string.Join('\\', parts);
    }

    static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo(path.EndsWith('\\') ? path : path + "\\") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"tray: opening {path}: {ex.Message}"); }
    }

    /// <summary>Failures a retry cannot fix (files on this PC unreadable, no permission, damaged data).</summary>
    static bool IsLocalProblem(Exception ex) => ex is UnauthorizedAccessException or InvalidDataException
        or NotSupportedException or Microsoft.Data.Sqlite.SqliteException or DirectoryNotFoundException or PathTooLongException
        || (ex is IOException io && (io.HResult & 0xFFFF) is 112 or 39); // disk full

    static string Short(string text) => text.Length > 70 ? text[..70] + "…" : text;

    void ScheduleRetry(DriveController d)
    {
        d.RetryTimer?.Dispose();
        d.RetryTimer = new System.Windows.Forms.Timer { Interval = d.RetryDelaySeconds * 1000 };
        d.RetryTimer.Tick += async (_, _) => { d.RetryTimer?.Stop(); await MountAsync(d); };
        d.RetryTimer.Start();
        d.RetryDelaySeconds = Math.Min(300, d.RetryDelaySeconds * 2);
    }

    void OfferSetup(DriveController d, string text)
    {
        Balloon(L.T("נדרשת הגדרה", "Setup needed"), text + L.T(" לחץ כאן כדי להמשיך.", " Click here to continue."), ToolTipIcon.Info,
            () => _ = SetupAsync(d));
        _pendingSetup = d;
        if (!_autoSetupShown) // first time: open the wizard right away
        {
            _autoSetupShown = true;
            _ui.Post(async _ => await SetupAsync(d), null);
        }
    }
    DriveController? _pendingSetup;
    bool _autoSetupShown;

    async Task OnSignedOutAsync(DriveController d)
    {
        await StopAsync(d, ask: false, waitForUploads: false);
        d.NeedsSetup = true;
        d.Problem = L.T("החיבור לטלגרם בוטל", "Telegram sign-in was revoked");
        Balloon(L.T("החיבור לטלגרם בוטל", "Telegram sign-in revoked"),
            L.T($"{d.Name} נותק כי החיבור שלו לטלגרם בוטל (למשל ב\"סיום כל ההפעלות האחרות\" בטלפון). קבצים שעוד לא עלו שמורים במחשב ויעלו אחרי שתתחבר מחדש. לחץ כאן כדי להתחבר.",
                $"{d.Name} was disconnected because its Telegram sign-in was revoked (e.g. by \"Terminate all other sessions\" on the phone). Files not yet uploaded are kept on this PC and upload after you sign in again. Click here to sign in."),
            ToolTipIcon.Warning, () => _ = SetupAsync(d));
        _pendingSetup = d;
    }

    /// <summary>Unmount one drive. With pending uploads (and ask), let the user choose to wait.</summary>
    async Task<bool> StopAsync(DriveController d, bool ask = true, bool waitForUploads = true)
    {
        if (d.Stopping)
        {
            while (d.Stopping) await Task.Delay(200); // let the stop already running finish
            return d.Session is null;
        }
        StopRetrying(d); // the user disconnects: it must not come back by itself (e.g. after sleep)
        if (d.Session is not { } session) return true;
        int open = session.Engine.FilesOpenForWriting;
        if (open > 0 && ask && Ui.Ask(
                L.N(open, $"ב־{d.Name} יש קובץ אחד פתוח לעריכה באפליקציה. אחרי הניתוק לא יהיה אפשר לשמור אותו (מה שכבר נשמר לא ילך לאיבוד).\n\nלנתק בכל זאת?",
                          $"ב־{d.Name} יש {{0}} קבצים פתוחים לעריכה באפליקציות. אחרי הניתוק לא יהיה אפשר לשמור אותם (מה שכבר נשמר לא ילך לאיבוד).\n\nלנתק בכל זאת?",
                          $"{d.Name} has 1 file open for editing in an app. After disconnecting it cannot be saved (what was already saved is kept).\n\nDisconnect anyway?",
                          $"{d.Name} has {{0}} files open for editing in apps. After disconnecting they cannot be saved (what was already saved is kept).\n\nDisconnect anyway?"),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return false;
        int pending = session.Engine.PendingNotPaused(); // paused files stay on this PC: not waited for
        if (pending > 0 && ask && waitForUploads)
        {
            var answer = Ui.Choose(
                L.N(pending, $"קובץ אחד ב־{d.Name} עדיין עולה לטלגרם.", $"{{0}} קבצים ב־{d.Name} עדיין עולים לטלגרם.",
                             $"1 file on {d.Name} is still uploading to Telegram.", $"{{0}} files on {d.Name} are still uploading to Telegram.")
                + L.T("\n\nאם תנתק עכשיו, ההעלאה תמשיך בפעם הבאה שהכונן יתחבר במחשב הזה.",
                      "\n\nIf you disconnect now, uploading continues next time the drive starts on this PC."),
                [L.T("לחכות שיסיימו (מומלץ)", "Wait for them (recommended)"), L.T("לנתק עכשיו", "Disconnect now"), L.T("ביטול", "Cancel")],
                cancelIndex: 2);
            if (answer is 2 or -1) return false;
            waitForUploads = answer == 0;
        }
        if (d.Session != session) return d.Session is null; // it changed while the questions were open
        _preparer.CancelAll(session); // only now: answering "cancel" above keeps the drive, and its downloads
        d.Stopping = true;
        while (d.Trash is { IsDisposed: false, Acting: true }) await Task.Delay(200); // a restore / delete still running
        d.Trash?.Close();
        d.Security?.Close();
        d.ExportWindow?.Close();
        d.HandoverWait?.Close();
        d.HandoverAsk?.Close();
        UpdateIcon();
        try
        {
            await Task.Run(() => session.StopAsync(waitForUploads, st => _ui.Post(_ =>
                SetTooltip(L.T($"מסיים העלאות ב־{L.Ltr(d.Letter)}: ", $"Finishing uploads on {d.Letter}: ")
                    + L.N(st.PendingFiles, "נותר קובץ אחד", "נותרו {0} קבצים", "1 file left", "{0} files left")), null)));
            await session.DisposeAsync();
        }
        catch (Exception ex) { Log.Error($"tray: stopping {d.Name} failed", ex); }
        d.Session = null;
        d.Stopping = false;
        UpdateIcon();
        return true;
    }

    bool _askingToQuit;

    async Task QuitAsync(bool ask)
    {
        if (_quitting || _askingToQuit) return;
        _askingToQuit = true;
        try { await QuitCoreAsync(ask); }
        finally { _askingToQuit = false; }
    }

    async Task QuitCoreAsync(bool ask)
    {
        if (ask)
        {
            int open = _drives.Sum(d => d.Session?.Engine.FilesOpenForWriting ?? 0);
            if (open > 0 && Ui.Ask(
                    L.N(open, "קובץ אחד פתוח לעריכה באפליקציה. אחרי היציאה לא יהיה אפשר לשמור אותו בכונן (מה שכבר נשמר לא ילך לאיבוד).\n\nלצאת בכל זאת?",
                              "{0} קבצים פתוחים לעריכה באפליקציות. אחרי היציאה לא יהיה אפשר לשמור אותם בכונן (מה שכבר נשמר לא ילך לאיבוד).\n\nלצאת בכל זאת?",
                              "1 file is open for editing in an app. After exiting it cannot be saved on the drive (what was already saved is kept).\n\nExit anyway?",
                              "{0} files are open for editing in apps. After exiting they cannot be saved on the drive (what was already saved is kept).\n\nExit anyway?"),
                    icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            int pending = _drives.Sum(d => d.Session?.Engine.PendingNotPaused() ?? 0); // paused ones stay here
            if (pending > 0)
            {
                var answer = Ui.Choose(
                    L.N(pending, "קובץ אחד עדיין עולה לטלגרם.", "{0} קבצים עדיין עולים לטלגרם.",
                                 "1 file is still uploading to Telegram.", "{0} files are still uploading to Telegram.")
                    + L.T("\n\nאם תצא עכשיו, ההעלאה תמשיך בפעם הבאה ש־DriveGram יופעל.", "\n\nIf you exit now, uploading continues the next time DriveGram starts."),
                    [L.T("לחכות ואז לצאת (מומלץ)", "Wait, then exit (recommended)"), L.T("לצאת עכשיו", "Exit now"), L.T("ביטול", "Cancel")],
                    cancelIndex: 2);
                if (answer is 2 or -1) return;
                if (answer == 1)
                    foreach (var d in _drives) await StopAsync(d, ask: false, waitForUploads: false);
            }
        }
        _quitting = true;
        _wizard?.Close();
        // a paused drive (by the user or the daily cap) is not waited for: its files stay safely on this PC and
        // go up the next time, within the limits the user chose
        foreach (var d in _drives.ToList()) await StopAsync(d, ask: false); // waits only for files that are not paused
        TrackDaily();
        _daily.Flush();
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _activate.Dispose();
        _quit.Dispose();
        ExitThread();
    }

    // ── password ─────────────────────────────────────────────────────────────
    /// <summary>Runs on a worker thread; shows the password dialog on the UI thread when needed.</summary>
    Task<Keys> UnlockAsync(DriveController d, ControlState ctrl)
    {
        if (d.PendingKeys is { } fresh)
        {
            d.PendingKeys = null;
            try { return Task.FromResult(Keys.FromMaster(ctrl.Vault, fresh.ExportMaster())); }
            catch (WrongPasswordException) { }
        }
        if (d.State.RememberedMaster() is { } master)
        {
            try { return Task.FromResult(Keys.FromMaster(ctrl.Vault, master)); }
            catch (WrongPasswordException) { Log.Warn($"{d.Name}: the remembered key does not match the drive; asking for the password (the key is kept)"); }
        }
        string? error = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (string pw, bool remember)? input = null;
            PasswordGate.Wait();
            try
            {
                _ui.Send(_ =>
                {
                    using var f = new PasswordForm(d.Name, error);
                    if (f.ShowDialog() == DialogResult.OK) input = (f.Password, f.Remember);
                }, null);
            }
            finally { PasswordGate.Release(); }
            if (input is null) throw new PasswordNotGivenException();
            try
            {
                var keys = Keys.UnlockAny(ctrl.Vault, input.Value.pw); // the password, or the recovery key
                if (input.Value.remember) d.State.RememberMaster(keys.ExportMaster());
                return Task.FromResult(keys);
            }
            catch (WrongPasswordException) { error = L.T("סיסמה שגויה, נסה שוב.", "Wrong password, try again."); }
        }
        throw new WrongPasswordException();
    }

    // ── setup / add / remove ─────────────────────────────────────────────────
    /// <summary>Open the wizard for a drive (or a new one); mount when it finishes. next/queue: more drives
    /// added in the same sitting, on the same account (each run adds one; the queue continues after it).</summary>
    /// <param name="chooseOther">"Choose another drive of the account": the list of drives, no sign-in.</param>
    /// <param name="freshSignIn">"Sign in with another account" (moving a drive): the sign-in starts signed out.</param>
    async Task SetupAsync(DriveController? d, Setup.NextDrive? next = null, List<Setup.NextDrive>? queue = null,
        bool chooseOther = false, bool freshSignIn = false)
    {
        if (_wizard is { IsDisposed: false })
        {
            _wizard.Activate();
            if (next is null) return;
            // the next drive of a sitting, while another wizard is open: it waits for that one, not lost
            while (_wizard is { IsDisposed: false } && !_quitting) await Task.Delay(500);
            if (_quitting) return;
        }
        bool wasMountedBefore = d?.Session is not null;
        if (d?.Session is not null)
        {
            if (Ui.Ask(L.T($"כדי לשנות את ההגדרות של {d.Name} צריך לנתק אותו לרגע. לנתק?",
                    $"To change the settings of {d.Name} it must be disconnected for a moment. Disconnect?")) != DialogResult.Yes) return;
            if (!await StopAsync(d)) return;
        }
        bool wasRetrying = d?.RetryTimer is not null;
        bool wasMounted = wasMountedBefore;
        if (d is not null) StopRetrying(d);
        // the wizard edits a COPY: cancelling it leaves the live drive's settings untouched
        var state = d is null ? AppState.CreateNew() : AppState.Open(d.State.Id);
        // the drive this profile's local data belongs to: choosing any other one resets that data (and
        // first checks for files that exist only here) — also when the list is shown because of a mismatch
        var originalChannels = state.Config.Channels;
        if (d?.MustChooseDrive == true || chooseOther)
        {
            state.Config.Channels = null; // show the drives to choose from (its data channel did not match, or asked)
            state.Config.DriveId = "";
        }
        bool isNew = d is null;
        DialogResult result;
        Keys? wizardKeys = null;
        string? createdDriveId = null;
        bool restartForLanguage;
        List<Setup.NextDrive> after;
        // the window says what this run is for (the menu item that opened it)
        string? title = d is null ? null
            : chooseOther ? L.T($"DriveGram – החלפת הכונן ב־{L.Ltr(d.Letter)}", $"DriveGram – switching the drive on {d.Letter}")
            : freshSignIn ? L.T($"DriveGram – התחברות לחשבון טלגרם אחר ({Ui.DriveTitle(d.State)})", $"DriveGram – sign in with another Telegram account ({d.State.DisplayName})")
            : d.MustChooseDrive ? L.T($"DriveGram – בחירת הכונן מחדש ({Ui.DriveTitle(d.State)})", $"DriveGram – choose the drive again ({d.State.DisplayName})")
            : L.T($"DriveGram – חיבור הכונן מחדש ({Ui.DriveTitle(d.State)})", $"DriveGram – connecting the drive again ({d.State.DisplayName})");
        bool signedInHere, driveVanished;
        using (_wizard = new Setup.SetupWizard(state, isNew, originalChannels, next, queue, title: title, freshSignIn: freshSignIn))
        {
            result = _wizard.ShowDialog();
            signedInHere = _wizard.Context.SignedInHere;
            driveVanished = _wizard.Context.DriveVanished;
            wizardKeys = _wizard.Context.UnlockedKeys;
            createdDriveId = _wizard.Context.CreatedDriveId;
            restartForLanguage = _wizard.Context.RestartForLanguage;
            after = _wizard.Context.Queue;
            _wizard = null;
        }
        if (restartForLanguage && isNew && createdDriveId is null)
        {
            // the language link on the first page: nothing was entered yet; open the wizard again in it
            state.Delete();
            UpdateIcon();
            await SetupAsync(d);
            return;
        }
        // The drive page saves a new binding (other channels / account) as soon as it is chosen, so a
        // cancel AFTER that point cannot restore the old one: the profile on disk is the truth.
        bool rebound = !isNew && Binding(AppState.Open(state.Id)) != Binding(d!.State);
        var oldAccount = d?.State.Config.AccountKey;
        bool createdHere = result != DialogResult.OK && isNew && createdDriveId is not null
            && AppState.Open(state.Id).Config.DriveId == createdDriveId;
        bool keepForNextRun = createdHere && _quitting; // no one to ask now: kept, it mounts next time
        if (keepForNextRun && _drives.Count == 0) try { ShellIntegration.SetAutostart(true); } catch { }
        if (createdHere && !_quitting)
        {
            var saved = AppState.Open(state.Id);
            var keep = Ui.Choose(L.T(
                    $"הכונן \"{saved.Config.VolumeLabel}\" כבר נוצר בטלגרם, אבל ההגדרה לא הסתיימה.\n\nאם תשמור אותו, הוא יתחבר עם הגדרות ברירת המחדל (אפשר לשנות את האות והשם ב\"אות ושם הכונן…\" בתפריט של הכונן).",
                    $"The drive \"{saved.Config.VolumeLabel}\" was already created in Telegram, but setup did not finish.\n\nIf you keep it, it connects with default settings (the letter and name can be changed in \"Drive letter and name…\" in the drive's menu)."),
                [L.T("שמור את הכונן", "Keep the drive"), L.T("מחק אותו מטלגרם", "Delete it from Telegram")]);
            if (keep != 1)
            {
                result = DialogResult.OK;
                saved.Config.NetworkDrive = true; // as the last page would have set it
                saved.Config.MountPoint = FreeLetter(saved);
                saved.Save();
                if (_drives.Count == 0) try { ShellIntegration.SetAutostart(true); } catch { } // as the last page would have
            }
            else
            {
                try
                {
                    SetTooltip(L.T("מוחק את הכונן מטלגרם…", "Deleting the drive from Telegram…"));
                    await Task.Run(() => DriveDeletion.DeleteStorageAsync(saved));
                }
                catch (Exception ex)
                {
                    Log.Warn($"tray: deleting a cancelled new drive: {ex.Message}");
                    // kept in the list, so it can still be deleted (or used) later
                    result = DialogResult.OK;
                    if (_drives.Count == 0) try { ShellIntegration.SetAutostart(true); } catch { }
                    Ui.Error(L.T($"המחיקה מטלגרם נכשלה: {Setup.TelegramErrors.Text(ex)}\n\nהכונן נשאר ברשימה. אפשר למחוק אותו אחר כך מהתפריט שלו ← \"מחק את הכונן לצמיתות…\".",
                                 $"Deleting from Telegram failed: {Setup.TelegramErrors.Text(ex)}\n\nThe drive stays in the list. You can delete it later from its menu → \"Delete the drive permanently…\"."));
                }
                finally { UpdateIcon(); }
            }
        }
        // signed in, then the window was closed: the sign-in is shared already, and this drive's own record did
        // not change, so it counts as done (the drive connects; others on the account too)
        if (result != DialogResult.OK && !isNew && signedInHere && !rebound && !driveVanished && state.Config.Channels is not null
            && state.Config.Channels.JournalId == originalChannels?.JournalId
            && state.Config.AccountId == d!.State.Config.AccountId) // the drive's own account (another one: nothing saved)
        {
            // only the sign-in is taken from the wizard's copy: what other windows changed meanwhile stays
            var live = AppState.Open(state.Id);
            live.Config.AccountKey = state.Config.AccountKey;
            live.Config.AccountId = state.Config.AccountId;
            live.Config.AccountName = state.Config.AccountName;
            live.Config.ApiId = state.Config.ApiId;
            live.Config.ApiHash = state.Config.ApiHash;
            live.Save();
            result = DialogResult.OK;
        }
        if (result != DialogResult.OK && !rebound)
        {
            if (isNew && !keepForNextRun) state.Delete();
            else if (wasMounted && d is { Session: null }) await MountAsync(d);
            else if (wasRetrying && d is { Session: null }) ScheduleRetry(d);
            UpdateIcon();
            // one drive of a sitting was cancelled: the others are not dropped without asking
            if (isNew && after.Count > 0 && !_quitting
                && Ui.Ask(L.N(after.Count, "להמשיך לכונן הבא שסימנת?", "להמשיך ל־{0} הכוננים הנוספים שסימנת?",
                              "Continue to the next drive you ticked?", "Continue to the {0} other drives you ticked?")) == DialogResult.Yes)
                await SetupAsync(null, after[0], after.Skip(1).ToList());
            return;
        }
        if (_pendingSetup == d) _pendingSetup = null;
        var account = state.Config.AccountKey;
        if (isNew)
        {
            d = new DriveController(AppState.Open(state.Id));
            _drives.Add(d);
        }
        else
        {
            var fresh = new DriveController(AppState.Open(state.Id)); // reload: letter, label, account may have changed
            Replace(d!, fresh);
            d = fresh;
        }
        d.PendingKeys = wizardKeys;
        d.QuietRefusal = isNew && d.State.Config.NetworkDrive; // a refusal is followed by a question, not a balloon
        try { await MountAsync(d); }
        finally { d.QuietRefusal = false; }
        if (isNew && d.Session is null && !d.Starting && d.LastMountRefused && d.State.Config.NetworkDrive)
        {
            // Windows refused a network drive on this PC (the wizard's default). Never changed behind the
            // user's back: they decide, knowing what a regular disk means (it can be changed later)
            Log.Warn($"tray: {d.Name} could not connect as a network drive; asking whether to use a regular disk");
            int choice = Ui.Choose(L.T(
                    $"וינדוס לא איפשר לחבר את \"{d.Name}\" ככונן רשת במחשב הזה.\n\n" +
                    "אפשר לחבר אותו כדיסק רגיל: הקבצים, ההצפנה והמהירות זהים. ההבדל: תוכנות גיבוי ואנטי־וירוס שסורקות את כל הדיסקים עלולות לנסות להוריד את כל הכונן מטלגרם. אפשר לנסות שוב כונן רשת בכל רגע, בתפריט של הכונן ← \"סוג הכונן\".\n\n" +
                    "או להשאיר אותו לא מחובר כרגע (הוא נשאר ברשימה, ואפשר לחבר אותו או לשנות את הסוג מהתפריט).",
                    $"Windows did not allow \"{d.Name}\" as a network drive on this PC.\n\n" +
                    "It can be connected as a regular disk instead: files, encryption and speed are the same. The difference: backup and antivirus programs that scan all disks may try to download the whole drive from Telegram. You can try a network drive again at any time, from the drive's menu → \"Drive type\".\n\n" +
                    "Or leave it disconnected for now (it stays in the list; connect it or change its type from the menu)."),
                [L.T("חבר כדיסק רגיל", "Connect as a regular disk"), L.T("השאר לא מחובר", "Leave it disconnected")],
                defaultIndex: 0, cancelIndex: 1, icon: MessageBoxIcon.Warning);
            if (choice == 0 && _drives.Contains(d) && d.Session is null && !d.Starting)
            {
                d.PendingKeys = wizardKeys; // used up by the first attempt: never ask again for a password just typed
                d.State.Config.NetworkDrive = false;
                d.State.Save();
                await MountAsync(d);
                if (d.Session is null && !d.Starting && d.LastMountRefused)
                {
                    d.State.Config.NetworkDrive = true; // refused as a disk too: the type was not the reason
                    d.State.Save();
                }
            }
            else StopRetrying(d); // left disconnected, as chosen: no retry as a network drive behind the user's back
        }
        if (!string.IsNullOrEmpty(oldAccount) && oldAccount != d.State.Config.AccountKey)
            await TelegramAccounts.SignOutIfUnusedAsync(oldAccount);
        if (!string.IsNullOrEmpty(account))
            foreach (var other in _drives.Where(o => o != d && o.NeedsSetup && o.Session is null && o.State.Config.AccountKey == account).ToList())
            {
                other.NeedsSetup = false; // same account, signed in again just now
                if (_pendingSetup == other) _pendingSetup = null;
                _ = MountAsync(other);
            }
        // the next drive of this sitting (ticked together, or "add another drive"), once this one is up
        if (isNew && after.Count > 0 && !_quitting)
            await SetupAsync(null, after[0], after.Skip(1).ToList());
    }

    /// <summary>A letter no other drive uses and nothing occupies now (as the wizard's last page picks it).</summary>
    static string FreeLetter(AppState s)
    {
        var inUse = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).ToHashSet();
        var others = AppState.All().Where(a => a.Dir != s.Dir).Select(a => char.ToUpperInvariant(a.Config.MountPoint[0])).ToHashSet();
        var current = char.ToUpperInvariant(s.Config.MountPoint[0]);
        if (!others.Contains(current) && !inUse.Contains(current)) return $"{current}:";
        var free = "TUVWXYZSRQPONMLKJIHGFED".FirstOrDefault(c => !others.Contains(c) && !inUse.Contains(c));
        return free == default ? s.Config.MountPoint : $"{free}:";
    }

    /// <summary>Which drive a profile points at (account + channels + drive id).</summary>
    static string Binding(AppState s) =>
        System.Text.Json.JsonSerializer.Serialize(new { s.Config.AccountKey, s.Config.DriveId, s.Config.Channels });

    static void StopRetrying(DriveController d)
    {
        d.RetryTimer?.Stop();
        d.RetryTimer?.Dispose();
        d.RetryTimer = null;
    }

    bool Replace(DriveController old, DriveController fresh)
    {
        StopRetrying(old);
        int i = _drives.IndexOf(old);
        if (i < 0) return false;
        _drives[i] = fresh;
        old.DiskShortNotice?.Close();
        old.SwitchAccount?.Close();
        old.Backup?.Close(); // it works on the old record (and maybe an account signed out now)
        return true;
    }


    bool _removing;

    async Task RemoveAsync(DriveController d)
    {
        if (_removing) return;
        _removing = true;
        try { await RemoveCoreAsync(d); }
        finally { _removing = false; }
    }

    async Task RemoveCoreAsync(DriveController d)
    {
        bool wasMounted = d.Session is not null;
        if (Ui.Ask(L.T(
                $"להסיר את {d.Name} מהמחשב הזה?\n\nהקבצים נשארים בטלגרם, ואפשר לחבר את הכונן שוב בכל עת (עם הסיסמה שלו). מה שהמחשב הזה שומר בשביל הכונן (עותקים מקומיים של קבצים, הסיסמה הזכורה) יימחק ממנו.",
                $"Remove {d.Name} from this PC?\n\nThe files stay in Telegram, and you can connect the drive again at any time (with its password). What this PC keeps for the drive (local copies of files, the remembered password) is deleted from it."),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!await StopAsync(d, ask: false, waitForUploads: false)) return;
        if (!ConfirmLosingLocalChanges(d))
        {
            if (wasMounted) await MountAsync(d); // kept: put it back the way it was
            return;
        }
        await ForgetLocallyAsync(d);
    }

    /// <summary>Files not yet uploaded / changes not yet sent live only in this PC's profile: removing it
    /// deletes them for good. Say so plainly and default to keeping them.</summary>
    static bool ConfirmLosingLocalChanges(DriveController d)
    {
        var (pending, unsent) = DriveDeletion.LocalOnly(d.State);
        if (pending == 0 && unsent == 0) return true;
        if (pending < 0)
            return Ui.Ask(L.T(
                    $"⚠  לא הצלחתי לבדוק אם ב־{d.Name} יש קבצים או שינויים שקיימים רק במחשב הזה. אם יש, הסרת הכונן תמחק אותם לצמיתות.\n\nלהסיר בכל זאת?",
                    $"⚠  Could not check whether {d.Name} has files or changes that exist only on this PC. If it does, removing the drive deletes them for good.\n\nRemove anyway?"),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        return Ui.Ask(L.T($"⚠  ב־{d.Name} יש ", $"⚠  {d.Name} has ") + LocalOnlyText(pending, unsent) + L.T(" (כרגע רק במחשב הזה).\n\n" +
                "אם תסיר את הכונן עכשיו, כל מה שעוד לא הגיע לטלגרם יימחק לצמיתות. כדי לשמור אותו: חבר את הכונן (אם צריך, התחבר מחדש לטלגרם) וחכה שההעלאה תסתיים.\n\nלהסיר בכל זאת?",
                " (only on this PC for now).\n\n" +
                "Removing the drive now deletes everything that has not reached Telegram yet, for good. To keep it, connect the drive (sign in again if needed) and wait for the upload to finish.\n\nRemove anyway?"),
            icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>"3 files not yet uploaded and one change not yet sent" — only the parts that are not zero.</summary>
    public static string LocalOnlyText(int pending, int unsent)
    {
        var parts = new List<string>();
        if (pending > 0)
            parts.Add(L.N(pending, "קובץ אחד שעוד לא עלה לטלגרם", "{0} קבצים שעוד לא עלו לטלגרם",
                "1 file not yet uploaded to Telegram", "{0} files not yet uploaded to Telegram"));
        if (unsent > 0)
            parts.Add(L.N(unsent, "שינוי אחד שעוד לא נשלח", "{0} שינויים שעוד לא נשלחו",
                "1 change not yet sent", "{0} changes not yet sent"));
        // Hebrew "and" joins the next word: "ושינוי אחד", but "ו־3 שינויים"
        return parts.Count == 2 ? parts[0] + L.T(char.IsDigit(parts[1][0]) ? " ו־" : " ו", " and ") + parts[1] : parts.FirstOrDefault() ?? "";
    }

    /// <summary>Everything this PC keeps for the drive: profile, icon, and the account's sign-in if unused now.</summary>
    async Task ForgetLocallyAsync(DriveController d)
    {
        if (d.Session is not null) return;
        try { ShellIntegration.ClearDriveIcon(d.Letter); } catch { }
        var accountKey = d.State.Config.AccountKey;
        FormerDevices.Remember(d.State);
        d.State.Delete();
        _drives.Remove(d);
        if (_drives.Count == 0)
        {
            try { ShellIntegration.SetAutostart(false); } catch (Exception ex) { Log.Warn($"autostart: {ex.Message}"); }
        }
        if (_pendingSetup == d) _pendingSetup = null;
        // the account's last drive on this PC: sign it out too, so no stray "device" stays behind
        if (!string.IsNullOrEmpty(accountKey)) await TelegramAccounts.SignOutIfUnusedAsync(accountKey);
        UpdateIcon();
    }

    // ── handing the drive between PCs ────────────────────────────────────────
    void AskForDrive(DriveController d)
    {
        if (d.Session is not { } s) return;
        if (d.HandoverWait is { IsDisposed: false }) { d.HandoverWait.Activate(); return; }
        var form = new HandoverWaitForm(s);
        d.HandoverWait = form;
        form.FormClosed += (_, _) =>
        {
            if (form.Received) d.ToldWritable = true;
            if (form.Received)
                Balloon(L.T($"{L.Ltr(d.Letter)} ניתן לכתיבה", $"{d.Letter} is writable"),
                    L.T("הכונן עבר למחשב הזה. אפשר לשמור קבצים.", "The drive moved to this PC. You can save files."),
                    onClick: () => OpenDrive(d));
            UpdateIcon();
        };
        form.Show();
    }

    async Task OnHandoverRequestedAsync(DriveController d, DriveSession s, HandoverRequest r)
    {
        if (d.Session != s) return;
        if (d.HandoverAsk is { IsDisposed: false })
        {
            s.Sync.ForgetSurfacedRequest(); // offered again once the open question is gone
            return;
        }
        bool approved, withdrawn;
        using (var ask = new HandoverAskForm(s, r, () => s.Sync.RequestStillOpenAsync(r, default)))
        {
            // not modal: the user's other DriveGram windows (setup, recycle bin, a password being typed)
            // stay usable while the question waits for an answer
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ask.FormClosed += (_, _) => closed.TrySetResult();
            d.HandoverAsk = ask;
            ask.Show();
            await closed.Task;
            approved = ask.Approved;
            withdrawn = ask.Withdrawn;
        }
        d.HandoverAsk = null;
        if (d.Session != s || d.Stopping || withdrawn) return; // closed because the drive is disconnecting
        try
        {
            if (!approved)
            {
                // the other PC takes the drive when no answer arrives: a refusal is retried while it still counts
                for (int attempt = 0; ; attempt++)
                {
                    try { await Task.Run(() => s.Sync.RefuseHandoverAsync(r, default)); return; }
                    catch (Exception ex) when (d.Session != s)
                    {
                        // the drive was disconnected meanwhile: nothing left to refuse from here
                        Log.Warn($"tray: refusing the handover of {d.Name}: {ex.Message}; the drive is no longer connected");
                        return;
                    }
                    catch (Exception ex) when (attempt < 5)
                    {
                        Log.Warn($"tray: refusing the handover of {d.Name}: {ex.Message}; trying again");
                        await Task.Delay(5000);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"tray: refusing the handover of {d.Name}: {ex.Message}");
                        if (d.Session != s) return;
                        if (!s.Writable)
                            Balloon(L.T("הכונן עבר למחשב אחר", "The drive moved to another PC"),
                                L.T($"הסירוב לא הגיע ל־{L.Ltr(r.Name)} בזמן, ולכן הכונן עבר אליו. אפשר לבקש אותו בחזרה מהתפריט.",
                                    $"The refusal did not reach {r.Name} in time, so the drive moved there. You can ask for it back from the menu."),
                                ToolTipIcon.Warning);
                        else
                            Balloon(L.T("הסירוב לא נשלח", "The refusal was not sent"),
                                L.T($"לא הצלחתי לשלוח את הסירוב (בדוק את החיבור לאינטרנט). אם {L.Ltr(r.Name)} ייקח את הכונן, המחשב הזה יעבור לקריאה בלבד, ואפשר לבקש אותו בחזרה מהתפריט.",
                                    $"The refusal could not be sent (check the internet connection). If {r.Name} takes the drive, this PC becomes read-only, and you can ask for it back from the menu."),
                                ToolTipIcon.Warning);
                        return;
                    }
                }
            }
            UpdateIcon();
            bool done = await Task.Run(() => s.HandOverAsync(r, null, default));
            if (done) d.ToldWritable = false;
            if (done)
            {
                Balloon(L.T($"{L.Ltr(d.Letter)} עבר ל־{L.Ltr(r.Name)}", $"{d.Letter} moved to {r.Name}"),
                    L.T("המחשב הזה עכשיו לקריאה בלבד. אפשר לבקש את הכונן בחזרה מהתפריט.",
                        "This PC is now read-only. You can ask for the drive back from the menu."));
                _ = WatchHandoverClaimedAsync(d, s, r);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"tray: handover of {d.Name}: {ex.Message}");
            Balloon(L.T("ההעברה לא הושלמה", "The handover did not complete"),
                L.T($"{d.Name} נשאר במחשב הזה ({Short(Setup.TelegramErrors.Text(ex))}).", $"{d.Name} stays on this PC ({Short(Setup.TelegramErrors.Text(ex))})."),
                ToolTipIcon.Warning);
        }
        UpdateIcon();
    }

    /// <summary>
    /// A file whose data is only in Telegram was opened or changed while Telegram could not be reached: the
    /// app shows only a vague error (or hangs for a while first), so say why — at most every 10 minutes.
    /// </summary>
    /// <summary>
    /// A copy into the drive waits for room on the local disk (staging lives there): explain how it works,
    /// that it goes on by itself, and how to speed it up. Not a blocking dialog: the copy keeps going.
    /// </summary>
    void OnLocalDiskShort(DriveController d, DriveSession s, string root, long free)
    {
        if (d.Session != s || d.Stopping || _quitting || d.DiskShortNotice is not null
            || DateTime.UtcNow < d.DiskShortQuietUntil) return;
        var disk = root.TrimEnd('\\');
        // paused (by the user or the daily cap): nothing goes up, so the copy cannot go on by itself
        bool paused = d.ManualPaused || _daily.CapReached;
        string text;
        if (paused)
            text = L.T(
                $"העתקה גדולה ל־{L.Ltr(d.Letter)}: אין מספיק מקום פנוי ב־{L.Ltr(disk)}.\n\n" +
                $"ההעלאות לטלגרם מושהות ({(d.ManualPaused ? "השהית אותן" : "הגעת לתקרה היומית")}), ולכן כל מה שמעתיקים לכונן נשאר בינתיים ב־{L.Ltr(disk)}. " +
                $"נשארו שם {Ui.Size(free)} שאפשר להשתמש בהם (DriveGram תמיד משאיר עוד 3 GB פנויים), וההעתקה נעצרה.\n\n" +
                "כדי להמשיך להעתיק: המשך את ההעלאות, או פנה מקום במחשב ונסה שוב.",
                $"A big copy into {d.Letter}: there is not enough free space on {disk}.\n\n" +
                $"Uploads to Telegram are paused ({(d.ManualPaused ? "you paused them" : "the daily cap was reached")}), so everything copied into the drive stays on {disk} for now. " +
                $"It has {Ui.Size(free)} left to use (DriveGram always keeps another 3 GB free), and the copy stopped.\n\n" +
                "To go on copying: resume the uploads, or free up space on this PC and try again.");
        else
            text = L.T(
                $"העתקה גדולה ל־{L.Ltr(d.Letter)}: אין מספיק מקום פנוי ב־{L.Ltr(disk)}.\n\n" +
                $"איך זה עובד: מה שמעתיקים לכונן נשמר קודם במחשב הזה, בכונן {L.Ltr(disk)}, ומשם עולה לטלגרם. כל חלק שעלה נמחק מ־{L.Ltr(disk)} ומפנה מקום לחלק הבא. " +
                $"כרגע ההעלאה לטלגרם איטית יותר מההעתקה, וב־{L.Ltr(disk)} נשארו {Ui.Size(free)} שאפשר להשתמש בהם (DriveGram תמיד משאיר עוד 3 GB פנויים).\n\n" +
                $"לא צריך לעצור: ההעתקה תאט ותמשיך לבד, בקצב ההעלאה. כדי להאיץ אותה, פנה מקום ב־{L.Ltr(disk)}.\n" +
                "אם וינדוס יציג בכל זאת \"אין מספיק מקום\", לחץ שם \"נסה שוב\" בעוד כמה דקות.",
                $"A big copy into {d.Letter}: there is not enough free space on {disk}.\n\n" +
                $"How it works: what you copy into the drive is first kept on this PC, on {disk}, and uploaded to Telegram from there. Each part that was uploaded is deleted from {disk}, making room for the next one. " +
                $"Right now the upload to Telegram is slower than the copy, and {disk} has {Ui.Size(free)} left to use (DriveGram always keeps another 3 GB free).\n\n" +
                $"No need to stop: the copy slows down and goes on by itself, at the upload speed. To speed it up, free up space on {disk}.\n" +
                "If Windows still says \"not enough space\", press \"Try Again\" there in a few minutes.");
        var storage = L.T("פתח את הגדרות האחסון של וינדוס", "Open Windows storage settings");
        var ok = L.T("הבנתי", "OK");
        string[] buttons = paused ? [L.T("המשך את ההעלאות", "Resume uploads"), storage, ok] : [storage, ok];
        var f = new ChoiceForm(text, buttons, buttons.Length - 1, buttons.Length - 1, MessageBoxIcon.Warning);
        d.DiskShortNotice = f;
        f.FormClosed += (_, _) =>
        {
            d.DiskShortNotice = null;
            var chosen = f.Choice >= 0 && f.Choice < buttons.Length ? buttons[f.Choice] : ok;
            if (chosen == storage)
            {
                // Windows' own storage page (what takes the space, temporary files, the recycle bin)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warn($"tray: opening storage settings: {ex.Message}"); }
            }
            else if (paused && f.Choice == 0 && _drives.Contains(d)) SetPaused(d, false); // asks first when the daily cap holds
            d.DiskShortQuietUntil = DateTime.UtcNow.AddMinutes(chosen == ok ? 30 : 10); // still short later: said again
            f.Dispose();
        };
        f.Show();
    }

    void OnRemoteUnreachable(DriveController d, DriveSession s)
    {
        if (d.Session != s || d.Stopping || _quitting) return;
        long now = Environment.TickCount64;
        if (d.ToldUnreachableAt != 0 && now - d.ToldUnreachableAt < 10 * 60_000) return;
        d.ToldUnreachableAt = now;
        Balloon(L.T("אין חיבור לטלגרם", "Telegram cannot be reached"),
            L.T($"קובץ ב־{L.Ltr(d.Letter)} שלא שמור במחשב הזה לא נפתח או לא נשמר, כי אין חיבור לאינטרנט. הוא יהיה זמין כשהחיבור יחזור. קבצים שנפתחו לאחרונה במחשב הזה נפתחים גם בלי חיבור.",
                $"A file on {d.Letter} that is not stored on this PC could not be opened or saved, because there is no internet connection. It will be available when the connection is back. Files opened recently on this PC open without a connection too."),
            ToolTipIcon.Warning);
    }

    /// <summary>
    /// The PC the drive was given to may have gone away right after asking (switched off, lost its
    /// connection): then no PC can save files until its hold on the drive runs out. If it still has not
    /// taken the drive after about two minutes, offer to take it back.
    /// </summary>
    async Task WatchHandoverClaimedAsync(DriveController d, DriveSession s, HandoverRequest r)
    {
        // two answers "not taken" a minute apart; a check that fails (a short network problem) is retried
        int notTaken = 0;
        for (int attempt = 0; attempt < 6 && notTaken < 2; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (d.Session != s || d.Stopping || _quitting || s.Writable) return;
            try
            {
                if (!await Task.Run(() => s.Sync.HandoverUnclaimedAsync(r, default))) return; // it took the drive
                notTaken++;
            }
            catch (Exception ex) { Log.Warn($"tray: checking the handover of {d.Name}: {ex.Message}"); }
        }
        if (notTaken < 2) return;
        Log.Warn($"tray: '{r.Name}' was given {d.Name} but has not taken it");
        Balloon(L.T($"{L.Ltr(r.Name)} לא לקח את הכונן", $"{r.Name} did not take the drive"),
            L.T($"הכונן הועבר ל־{L.Ltr(r.Name)}, אבל נראה שהוא כבר לא מחובר, ולכן כרגע אי אפשר לשמור קבצים באף מחשב. לחץ כאן כדי לקחת את הכונן בחזרה.",
                $"The drive was handed to {r.Name}, but it seems to be gone, so right now no PC can save files. Click here to take the drive back."),
            ToolTipIcon.Warning, () => _ = TakeBackAsync(d, s, r));
    }

    async Task TakeBackAsync(DriveController d, DriveSession s, HandoverRequest r)
    {
        if (d.Session != s || s.Writable) return;
        try
        {
            if (!await Task.Run(() => s.Sync.HandoverUnclaimedAsync(r, default)))
            {
                Balloon(L.T("הכונן כבר נלקח", "The drive was already taken"),
                    L.T($"{L.Ltr(r.Name)} התחבר בינתיים ולקח את הכונן. אפשר לבקש אותו בחזרה מהתפריט.",
                        $"{r.Name} came back meanwhile and took the drive. You can ask for it back from the menu."));
                return;
            }
            if (Ui.Ask(L.T($"לקחת את הכונן בחזרה מ־{L.Ltr(r.Name)}?\n\nאם {L.Ltr(r.Name)} בכל זאת עובד, הוא יעבור לקריאה בלבד, ושינויים שלא הספיק לשלוח יישמרו אצלו עד שיקבל את הכונן שוב.",
                           $"Take the drive back from {r.Name}?\n\nIf {r.Name} is working after all, it becomes read-only; changes it has not sent yet are kept there until it gets the drive again."),
                    icon: MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (d.Session != s) return;
            await Task.Run(() => s.Sync.TakeDriveAsync(force: true, default));
            if (s.Writable)
            {
                d.ToldWritable = true;
                Balloon(L.T($"{L.Ltr(d.Letter)} ניתן לכתיבה", $"{d.Letter} is writable"),
                    L.T("הכונן חזר למחשב הזה. אפשר לשמור קבצים.", "The drive is back on this PC. You can save files."),
                    onClick: () => OpenDrive(d));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"tray: taking {d.Name} back: {ex.Message}");
            Balloon(L.T("לא הצלחתי לקחת את הכונן", "Could not take the drive"),
                L.T($"{Short(Setup.TelegramErrors.Text(ex))}. אפשר לבקש את הכונן מהתפריט.", $"{Short(Setup.TelegramErrors.Text(ex))}. You can ask for the drive from the menu."),
                ToolTipIcon.Warning);
        }
        UpdateIcon();
    }

    /// <summary>Delete the local copies of files that are safely in Telegram, only when nothing waits here.</summary>
    async void FreeUpSpace(DriveController d)
    {
        if (d.Session is null) { await FreeUpSpaceOfflineAsync(d); return; }
        if (d.Session is not { } s) return;
        if (s.CacheClearBlocker() is { } why) { Ui.Info(why); return; }
        long size = Math.Max(0, s.Status.CacheBytes - s.Engine.PreparedBytes); // prepared files stay
        if (size < 1 << 20) { Ui.Info(L.T("אין כרגע עותקים מקומיים שאפשר לפנות.", "There are no local copies to free right now.")); return; }
        if (Ui.Ask(L.T(
                $"יימחקו מהמחשב הזה עותקים מקומיים של קבצים מ־{d.Name}, בגודל {Ui.Size(size)}.\n\n" +
                "הקבצים עצמם שמורים בטלגרם ולא נפגעים: כשתפתח אותם הם יורדו שוב. עד אז הם ייפתחו רק כשיש חיבור לאינטרנט. קבצים שהכנת נשארים במחשב.\n\nלפנות?",
                $"Local copies of files from {d.Name} will be deleted from this PC ({Ui.Size(size)}).\n\n" +
                "The files themselves are stored in Telegram and are not affected: they are downloaded again when you open them. Until then they open only with an internet connection. Files you prepared stay on this PC.\n\nFree up the space?"),
                icon: MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (d.Session != s) return; // disconnected while the question was open
        try
        {
            long freed = await Task.Run(() => s.ClearCache()); // thousands of small files: not on the UI thread
            Balloon(L.T("התפנה מקום ✓", "Space freed ✓"), L.T($"פונו {Ui.Size(freed)} במחשב הזה.", $"{Ui.Size(freed)} freed on this PC."), onClick: () => OpenDrive(d));
        }
        catch (InvalidOperationException ex) { Ui.Info(ex.Message); } // something started meanwhile
        catch (Exception ex)
        {
            Log.Warn($"free up space: {ex.Message}");
            Ui.Error(L.T("לא הצלחתי לפנות את כל המקום (ייתכן שקובץ מהכונן פתוח כרגע). נסה שוב בעוד רגע.",
                "Could not free all of the space (a file from the drive may be open right now). Try again in a moment."));
        }
        UpdateIcon();
    }

    /// <summary>"Free up space" for a drive that is not connected: its temporary copies, from the local
    /// cache folder (prepared files and thumbnails stay).</summary>
    async Task FreeUpSpaceOfflineAsync(DriveController d)
    {
        if (d.Starting || d.Stopping || d.FreeingOffline) return;
        long size;
        d.FreeingOffline = true; // (also while measuring: it holds the drive's lock)
        try { size = await Task.Run(() => OfflineCache.TemporaryBytes(d.State)); }
        catch (OfflineCache.DriveInUseException) { Ui.Info(OpenElsewhere); return; }
        catch (Exception ex) { Log.Warn($"free up space (offline): {ex.Message}"); Ui.Error(L.T("לא הצלחתי לבדוק כמה מקום אפשר לפנות. לא נמחק כלום.", "Could not check how much space can be freed. Nothing was deleted.")); return; }
        finally { d.FreeingOffline = false; }
        if (size < 1 << 20) { Ui.Info(L.T("אין כרגע עותקים זמניים שאפשר לפנות.", "There are no temporary copies to free right now.")); return; }
        if (Ui.Ask(L.T(
                $"יימחקו מהמחשב הזה עותקים זמניים של קבצים מ־{d.Name}, בגודל {Ui.Size(size)}.\n\n" +
                "הקבצים עצמם שמורים בטלגרם ולא נפגעים: כשתפתח אותם הם יורדו שוב. קבצים שהכנת ותמונות ממוזערות לא נמחקים.\n\nלפנות?",
                $"Temporary copies of files from {d.Name} will be deleted from this PC ({Ui.Size(size)}).\n\n" +
                "The files themselves are stored in Telegram and are not affected: they download again when you open them. Files you prepared and the thumbnails stay on this PC.\n\nFree up the space?"),
                icon: MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (d.Session is not null || d.Starting) { Ui.Info(L.T("הכונן התחבר בינתיים. נסה שוב.", "The drive connected meanwhile. Try again.")); return; }
        d.FreeingOffline = true;
        try
        {
            long freed = await Task.Run(() => OfflineCache.FreeTemporary(d.State));
            Balloon(L.T("התפנה מקום ✓", "Space freed ✓"), L.T($"פונו {Ui.Size(freed)} במחשב הזה.", $"{Ui.Size(freed)} freed on this PC."));
        }
        catch (OfflineCache.DriveInUseException) { Ui.Info(OpenElsewhere); }
        catch (Exception ex)
        {
            Log.Warn($"free up space (offline): {ex.Message}");
            Ui.Error(L.T("לא הצלחתי לפנות את המקום (לא נמחק כלום שהכנת). נסה שוב בעוד רגע.", "Could not free the space (nothing you prepared was deleted). Try again in a moment."));
        }
        finally { d.FreeingOffline = false; UpdateIcon(); }
        UpdateIcon();
    }

    static string OpenElsewhere => L.T("הכונן פתוח כרגע בחלון אחר של DriveGram, ולכן אי אפשר לפנות עכשיו. סגור את החלון ההוא ונסה שוב.",
        "The drive is open in another DriveGram window right now, so nothing can be freed now. Close that window and try again.");

    async Task ConfirmRecordAsync(DriveController d, DriveSession s)
    {
        if (Ui.Ask(L.T(
                "רשומת הכונן בטלגרם שונתה מחוץ ל־DriveGram, בידי מישהו בלי סיסמת הכונן (או ידנית). לכן DriveGram לא סומך על מה שכתוב בה, והכונן פתוח לקריאה בלבד.\n\n" +
                "אם אתה יודע שזה בסדר (למשל, אתה שינית אותה), אפשר לאשר: DriveGram יחזיר את הפרטים החשובים מהעותק שהמחשב הזה כבר בדק, ויאפס פרטים שעלולים להטעות (איזה מחשב מחזיק בכונן, בקשות להעברת הכונן, ועותק מקוצר של רשימת הקבצים). הקבצים לא ייפגעו.\n\n" +
                "אם לא, בדוק מי מנהל את ערוצי הכונן (בתפריט של הכונן ← \"גיבוי בחשבון נוסף…\").\n\nלאשר?",
                "The drive's record in Telegram was changed outside DriveGram, by someone without the drive's password (or by hand). So DriveGram does not trust what it says, and the drive is read-only.\n\n" +
                "If you know this is fine (e.g. you changed it), you can confirm: DriveGram brings back the important details from the copy this PC already checked, and resets details that could mislead (which PC holds the drive, requests to hand it over, and a shortcut copy of the file list). Files are not affected.\n\n" +
                "If not, check who administers the drive's channels (the drive's menu → \"Backup on another account…\").\n\nConfirm?"),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        string? secret = null, error = null;
        bool useThisPcsCopy = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await Task.Run(() => s.Sync.RepairControlAsync(default, secret, useThisPcsCopy));
                break;
            }
            catch (NewerVaultException ex)
            {
                if (ex.WrongSecret && attempt >= 3)
                {
                    // the record may be a fake, or the newer password is not known here
                    useThisPcsCopy = Ui.Ask(L.T(
                            "הסיסמה לא פותחת את רשומת הכונן שבטלגרם.\n\nאפשר להחזיר את הרשומה שהמחשב הזה מכיר. אבל אם הסיסמה או מפתח השחזור שונו במחשב אחר, השינוי הזה יבוטל: הסיסמה הישנה תחזור לעבוד, ומפתח שחזור חדש יפסיק לעבוד.\n\nעדיף לאשר במחשב שבו הסיסמה שונתה. להחזיר בכל זאת?",
                            "The password does not open the drive's record in Telegram.\n\nThe record this PC knows can be brought back. But if the password or recovery key was changed on another PC, that change is undone: the old password works again, and a new recovery key stops working.\n\nBetter confirm on the PC where the password was changed. Bring it back anyway?"),
                        icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    if (!useThisPcsCopy) break;
                    continue;
                }
                error = ex.WrongSecret ? L.T("הסיסמה לא נכונה.", "Wrong password.")
                    : L.T("ייתכן שהסיסמה שונתה במחשב אחר. הקלד את הסיסמה הנוכחית של הכונן (או את מפתח השחזור).",
                          "The password may have been changed on another PC. Type the drive's current password (or the recovery key).");
                string? typed = null;
                await PasswordGate.WaitAsync();
                try
                {
                    using var f = new PasswordForm(d.Name, error);
                    if (f.ShowDialog() == DialogResult.OK) typed = f.Password;
                }
                finally { PasswordGate.Release(); }
                if (typed is null) break;
                secret = typed;
            }
            catch (Exception ex) { Ui.Error(Setup.TelegramErrors.Text(ex)); break; }
        }
        UpdateIcon();
    }

    /// <summary>The drive's letter and name, in a small window; a connected drive reconnects only if
    /// something changed (Windows takes both only when it mounts the drive).</summary>
    async Task ChangeLetterAndNameAsync(DriveController d)
    {
        if (d.LetterName is { IsDisposed: false }) { d.LetterName.Activate(); return; }
        if (d.ChangingLetter) return;
        d.ChangingLetter = true;
        try { await ChangeLetterAndNameCoreAsync(d); }
        finally { d.ChangingLetter = false; }
    }

    async Task ChangeLetterAndNameCoreAsync(DriveController d)
    {
        string letter, label;
        using (var form = new LetterNameForm(d.State, d.Session is not null, d.Session?.MountPoint, _preparer.Running.Any(j => j.Session == d.Session),
                   reconnectsNow: d.Session is null && (d.LastMountRefused || d.RetryTimer is not null)))
        {
            d.LetterName = form;
            var ok = form.ShowDialog();
            d.LetterName = null;
            if (ok != DialogResult.OK) return;
            letter = form.Letter;
            label = form.Label;
        }
        var cfg = d.State.Config;
        var nowOn = d.Session?.MountPoint ?? cfg.MountPoint; // a busy usual letter: this run is on another one
        if (string.Equals(letter, cfg.MountPoint, StringComparison.OrdinalIgnoreCase) && string.Equals(letter, nowOn, StringComparison.OrdinalIgnoreCase)
            && label == cfg.VolumeLabel) return;
        if (!_drives.Contains(d)) return; // removed while the window was open
        while (d.Starting || d.Stopping) await Task.Delay(200); // an automatic reconnect: decide after it
        if (!_drives.Contains(d)) return;
        bool wasMounted = d.Session is not null;
        if (!wasMounted && await DriveOpenElsewhereAsync(d))
        {
            Ui.Info(L.T("הכונן פתוח כרגע בחלון אחר של DriveGram. סגור אותו ונסה שוב.", "The drive is open in another DriveGram window right now. Close it and try again."));
            return;
        }
        // it should be connected after the change: it was, or it was trying to (e.g. its letter was refused)
        bool connect = wasMounted || d.LastMountRefused || d.RetryTimer is not null;
        // files being prepared stop with the drive: started again after it reconnects
        var preparing = d.Session is { } before ? _preparer.Running.Where(j => j.Session == before).Select(j => j.Id).ToList() : [];
        StopRetrying(d);
        if (wasMounted && !await StopAsync(d, waitForUploads: false)) return;
        var (oldLetter, oldLabel) = (cfg.MountPoint, cfg.VolumeLabel);
        cfg.MountPoint = letter;
        cfg.VolumeLabel = label;
        d.State.Save();
        Log.Info($"tray: {oldLetter} {oldLabel} is now {letter} {label}");
        UpdateIcon();
        if (!connect) return;
        bool letterChanged = !string.Equals(oldLetter, letter, StringComparison.OrdinalIgnoreCase);
        d.QuietRefusal = letterChanged; // a refusal of a NEW letter is said once, below; otherwise the usual way
        try { await MountAsync(d); }
        finally { d.QuietRefusal = false; }
        if (d.Session is null && !d.Starting && d.LastMountRefused && letterChanged)
        {
            // Windows refused the new letter: back to how it was, and say so
            cfg.MountPoint = oldLetter;
            cfg.VolumeLabel = oldLabel;
            d.State.Save();
            Ui.Error(L.T($"הכונן לא הצליח להתחבר באות {L.Ltr(letter)}, ולכן הוחזר ל־{L.Ltr(oldLetter)}.", $"The drive could not connect as {letter}, so it was set back to {oldLetter}."));
            await MountAsync(d);
        }
        if (d.Session is { } s && preparing.Count > 0) PrepareFiles(preparing.Select(id => (s, id)));
    }

    /// <summary>The drive is mounted by another DriveGram window (the command line): its files are in use.</summary>
    static async Task<bool> DriveOpenElsewhereAsync(DriveController d)
    {
        try
        {
            if (d.FreeingOffline) return false; // our own short hold, not another window
            using var instance = new Semaphore(1, 1, DriveSession.InstanceName(d.State.Dir));
            for (int i = 0; i < 10; i++) // a short hold (a list being read) passes: 2 s, without freezing the tray
            {
                if (instance.WaitOne(0)) { instance.Release(); return false; }
                await Task.Delay(200);
            }
            return true;
        }
        catch { return true; } // cannot tell (e.g. a command line run as administrator): do not change it under it
    }

    async Task ChangeDriveTypeAsync(DriveController d)
    {
        using var form = new DriveTypeForm(d.Name, d.State.Config.NetworkDrive);
        if (form.ShowDialog() != DialogResult.OK || form.Network == d.State.Config.NetworkDrive) return;
        if (!_drives.Contains(d)) return; // removed while the window was open
        bool wasMounted = d.Session is not null;
        // uploads need not finish first: what is staged uploads again right after the remount
        if (wasMounted && !await StopAsync(d, waitForUploads: false)) return;
        bool before = d.State.Config.NetworkDrive;
        d.State.Config.NetworkDrive = form.Network;
        d.State.Save();
        Log.Info($"tray: {d.Name} is now a {(form.Network ? "network drive" : "regular disk")}");
        if (!wasMounted) return;
        await MountAsync(d);
        if (d.Session is null && !d.Starting && d.LastMountRefused)
        {
            // Windows refused it this way: back to how it was, and say so
            d.State.Config.NetworkDrive = before;
            d.State.Save();
            Ui.Error(L.T("הכונן לא הצליח להתחבר בסוג החדש, ולכן הוחזר לסוג הקודם.", "The drive could not connect as the new type, so it was set back to the previous one."));
            await MountAsync(d);
        }
    }

    static readonly SemaphoreSlim PasswordGate = new(1, 1);

    bool _deleting;

    sealed class PasswordNotGivenException() : Exception("no password entered");

    /// <summary>
    /// Permanently delete a drive — its Telegram channels and everything on this PC. Shows what will be
    /// lost and requires ticking "I understand" plus typing the drive's name. Drives this account does
    /// not own (a backup admin) can only be removed from this PC.
    /// </summary>
    async Task DeleteForeverAsync(DriveController d)
    {
        if (_deleting) return;
        _deleting = true;
        try { await DeleteForeverCoreAsync(d); }
        finally { _deleting = false; }
    }

    async Task DeleteForeverCoreAsync(DriveController d)
    {
        DeletionPreview p;
        try
        {
            SetTooltip(L.T($"בודק את {d.Name}…", $"Checking {d.Name}…"));
            p = await Task.Run(() => DriveDeletion.PreviewAsync(d.State, d.Session?.Db));
        }
        catch (Exception ex)
        {
            Ui.Error(L.T($"לא ניתן לבדוק את הכונן כרגע: {Setup.TelegramErrors.Text(ex)}", $"Cannot check the drive right now: {Setup.TelegramErrors.Text(ex)}"));
            return;
        }
        finally { UpdateIcon(); }

        if (!p.RemoteExists)
        {
            if (Ui.Ask(L.T($"{d.Name} לא נמצא בחשבון הטלגרם שלו (אולי הערוצים שלו נמחקו, או שהחשבון הוסר מהם), ולכן אין מה למחוק בטלגרם. להסיר את הכונן מהמחשב הזה?",
                    $"{d.Name} was not found on its Telegram account (its channels may have been deleted, or the account removed from them), so there is nothing to delete in Telegram. Remove the drive from this PC?"),
                    icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            // the drive may still exist (only this account lost it): files that exist only here are asked about
            if (await StopAsync(d, ask: false, waitForUploads: false) && ConfirmLosingLocalChanges(d)) await ForgetLocallyAsync(d);
            return;
        }
        if (!p.CanDeleteRemote)
        {
            if (Ui.Ask(L.T($"החשבון שמחובר כאן לא הבעלים של {d.Name} (למשל חשבון גיבוי), ולכן הוא לא יכול למחוק אותו מטלגרם. רק הבעלים יכול.\n\nלהסיר את הכונן רק מהמחשב הזה? הקבצים יישארו בטלגרם.",
                    $"The account connected here does not own {d.Name} (e.g. a backup account), so it cannot delete it from Telegram — only the owner can.\n\nRemove the drive from this PC only? The files stay in Telegram."),
                    icon: MessageBoxIcon.Information, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (await StopAsync(d, ask: false) && ConfirmLosingLocalChanges(d)) await ForgetLocallyAsync(d);
            return;
        }

        using (var form = new DeleteDriveForm(d.Letter, d.State.Config.VolumeLabel, p))
            if (form.ShowDialog() != DialogResult.OK) return;

        // uploads are pointless now: disconnect without waiting for them
        if (!await StopAsync(d, ask: false, waitForUploads: false) || d.Session is not null) return;
        try { await Task.Run(() => DriveDeletion.DeleteStorageAsync(d.State)); }
        catch (Exception ex)
        {
            Log.Error($"deleting {d.Name} failed", ex);
            var why = ex is UnauthorizedAccessException
                ? L.T("רק החשבון שבבעלותו הכונן יכול למחוק את הערוצים שלו.", "Only the account that owns the drive can delete its channels.")
                : Setup.TelegramErrors.Text(ex);
            Ui.Error(L.T($"המחיקה מטלגרם נכשלה: {why}\nהכונן נשאר ברשימה, אפשר לנסות שוב.",
                         $"Deleting from Telegram failed: {why}\nThe drive stays in the list; you can try again."));
            return;
        }
        var name = d.Name;
        await ForgetLocallyAsync(d);
        Balloon(L.T("הכונן נמחק", "Drive deleted"), L.T($"{name} נמחק לצמיתות.", $"{name} was deleted permanently."), ToolTipIcon.Info);
    }

    // ── actions ──────────────────────────────────────────────────────────────
    /// <summary>Double-click / second launch: open the drive, or what needs attention.</summary>
    void Activate()
    {
        if (_wizard is { IsDisposed: false }) { _wizard.Activate(); return; }
        if (_pendingSetup is { } p) { _ = SetupAsync(p); return; }
        ShowMain();
    }

    static void OpenDrive(DriveController d)
    {
        if (d.Session is null) return;
        Process.Start(new ProcessStartInfo(d.Session.MountPoint + "\\") { UseShellExecute = true });
    }

    static void ShowSecurity(DriveController d)
    {
        if (d.Session is null) return;
        if (d.Security is { IsDisposed: false }) { d.Security.Activate(); return; }
        d.Security = new SecurityForm(d.Session);
        d.Security.Show();
    }

    static void Export(DriveController d)
    {
        if (d.Session is null) return;
        if (d.ExportWindow is { IsDisposed: false }) { d.ExportWindow.Activate(); return; }
        using var dlg = new FolderBrowserDialog
        {
            Description = L.T($"לאן להעתיק את כל הקבצים של {d.Name}?", $"Where should every file of {d.Name} be copied?"),
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (dlg.SelectedPath.StartsWith(d.Session.MountPoint, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error(L.T("בחר תיקייה מחוץ לכונן עצמו.", "Choose a folder outside the drive itself."));
            return;
        }
        d.ExportWindow = new ExportForm(d.Session, dlg.SelectedPath);
        d.ExportWindow.Show();
    }

    static void ShowBackup(DriveController d)
    {
        if (d.Backup is { IsDisposed: false }) { d.Backup.Activate(); return; }
        d.Backup = new BackupForm(d.State);
        d.Backup.Show();
    }

    // ── transfers: uploads, files on this PC, the daily limit ────────────────
    public IReadOnlyList<DriveController> Drives => _drives;
    public Preparer Preparer => _preparer;
    public DailyUploads Daily => _daily;

    void ShowTransfers(bool local = false) => ShowMain(DriveGram.Tray.MainForm.Tab.Transfers, local);

    /// <summary>The files prepared on this PC (all drives, connected or not).</summary>
    void ShowPrepared()
    {
        ShowMain(DriveGram.Tray.MainForm.Tab.Transfers, localFiles: true);
        _main?.ShowPrepared();
    }

    long _diskCheckedAt;
    DateTime _toldLowDiskOn;

    /// <summary>The disk DriveGram keeps its files on runs low while files prepared on purpose take a lot
    /// of it: a notice, at most once a day, that leads to them (they are never deleted by themselves).</summary>
    void TrackLowDisk()
    {
        long now = Environment.TickCount64;
        if (now - _diskCheckedAt < 5 * 60_000) return;
        _diskCheckedAt = now;
        if (_toldLowDiskOn == DateTime.Today) return;
        var drives = _drives.ToList();
        _ = Task.Run(() => CheckLowDisk(drives)); // reading disconnected drives' indexes: not on the UI thread
    }

    void CheckLowDisk(List<DriveController> _drives)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppState.Root))!;
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= 5L << 30) return;
            long prepared = 0;
            foreach (var d in _drives)
                try
                {
                    if (d.Session is { } s) prepared += s.Engine.PreparedBytes;
                    else if (!d.Starting && !d.Stopping) prepared += OfflineCache.Prepared(d.State).Sum(p => (long)(p.Size * p.Fraction));
                }
                catch { }
            if (prepared < 1L << 30) return;
            _ui.Post(_ => LowDiskNotice(root, free, prepared), null);
        }
        catch (Exception ex) { Log.Debug($"low disk: {ex.Message}"); }
    }

    void LowDiskNotice(string root, long free, long prepared)
    {
        if (_toldLowDiskOn == DateTime.Today) return;
        {
            _toldLowDiskOn = DateTime.Today;
            Balloon(L.T($"נשאר מעט מקום ב־{L.Ltr(root.TrimEnd('\\'))}", $"Little space left on {root.TrimEnd('\\')}"),
                L.T($"נשארו {Ui.Size(free)} פנויים. קבצים שהכנת תופסים {Ui.Size(prepared)} ולא נמחקים לבד. לחץ כאן כדי לבחור מה לפנות.",
                    $"{Ui.Size(free)} free. Files you prepared take {Ui.Size(prepared)}, and DriveGram never deletes them by itself. Click here to see them and free what you no longer need."),
                ToolTipIcon.Warning, ShowPrepared);
        }
    }

    DriveGram.Tray.MainForm? _main;

    /// <summary>The main window (accounts, drives, transfers). Without a tab: accounts when there is no drive yet.</summary>
    internal void ShowMain(DriveGram.Tray.MainForm.Tab? tab = null, bool localFiles = false)
    {
        if (_main is not { IsDisposed: false })
        {
            _main = new DriveGram.Tray.MainForm(this);
            if (_updates.Offered is { } u) _main.ShowUpdate(UpdateBar(u), ShowUpdate);
            _main.Show();
        }
        else
        {
            if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
            _main.Activate();
        }
        _main.Select(tab ?? (_drives.Count == 0 ? DriveGram.Tray.MainForm.Tab.Accounts : DriveGram.Tray.MainForm.Tab.Drives), localFiles);
    }

    /// <summary>"Add account": a short wizard for the sign-in only; then the new account can add drives.</summary>
    public async Task AddAccountAsync()
    {
        if (_wizard is { IsDisposed: false }) { _wizard.Activate(); return; }
        var before = TelegramAccounts.Known().Select(a => a.Key).ToHashSet();
        var temp = AppState.CreateNew(); // only carries the sign-in; the account itself is kept by TelegramAccounts
        bool restart, finished;
        try
        {
            using (_wizard = new Setup.SetupWizard(temp, isNew: true, accountOnly: true, firstRun: TelegramAccounts.Known().Count == 0))
            {
                finished = _wizard.ShowDialog() == DialogResult.OK;
                restart = _wizard.Context.RestartForLanguage;
                _wizard = null;
            }
        }
        finally { temp.Delete(); }
        var signedIn = temp.Config.AccountKey; // the account the wizard signed in to (maybe one already here)
        if (restart) { await AddAccountAsync(); return; }
        _main?.RefreshAll();
        if (!finished)
        {
            // closed after signing in: the account is added (its drives can be added from the main window)
            if (TelegramAccounts.Known().FirstOrDefault(a => !before.Contains(a.Key)) is { } justAdded)
                Balloon(L.T("החשבון נוסף", "The account was added"),
                    L.T($"החשבון {L.Name(justAdded.Name)} נוסף. אפשר להוסיף לו כוננים מהחלון הראשי ← חשבונות.", $"The account {justAdded.Name} was added. Its drives can be added from the main window → Accounts."));
            return;
        }
        var added = TelegramAccounts.Known().FirstOrDefault(a => !before.Contains(a.Key))
                    ?? (string.IsNullOrEmpty(signedIn) ? null : TelegramAccounts.Known().FirstOrDefault(a => a.Key == signedIn));
        if (added is null) return;
        Log.Info("tray: a Telegram account was added");
        // straight on to its drives: the ones it already has, or a new one
        await AddDrivesAsync(added, createNew: false);
    }

    /// <summary>Drives for one account: a new one, or the account's existing drives to connect (several at once).</summary>
    public async Task AddDrivesAsync(AccountInfo account, bool createNew)
    {
        await SetupAsync(null, new Setup.NextDrive(account, null, createNew, null));
        _main?.RefreshAll();
    }

    /// <summary>Sign out of an account no drive here uses.</summary>
    public async Task SignOutAccountAsync(AccountInfo a)
    {
        if (_drives.Any(d => d.State.Config.AccountKey == a.Key)) return;
        if (Ui.Ask(L.T($"להתנתק מהחשבון {L.Name(a.Name)} במחשב הזה?\n\nשום דבר לא נמחק בטלגרם. אפשר להתחבר אליו שוב בכל רגע.",
                $"Sign out of {a.Name} on this PC?\n\nNothing is deleted in Telegram. You can sign in to it again at any time.")) != DialogResult.Yes) return;
        bool done = await TelegramAccounts.SignOutIfUnusedAsync(a.Key);
        _main?.RefreshAll();
        if (!done)
            Ui.Info(L.T($"לא הצלחתי להתנתק מ־{L.Name(a.Name)} כרגע (אין חיבור לטלגרם, או שהחשבון בשימוש עכשיו). נסה שוב בעוד רגע.",
                $"Could not sign out of {a.Name} right now (no connection to Telegram, or the account is in use). Try again in a moment."));
    }

    public List<AccountInfo> Accounts() => TelegramAccounts.Known();
    public string DriveStatus(DriveController d) => StatusOf(d);
    public void OpenDriveNow(DriveController d) => OpenDrive(d);
    public async Task ConnectOrDisconnectAsync(DriveController d)
    {
        if (d.Session is not null) await StopAsync(d);
        else if (d.NeedsSetup) await SetupAsync(d);
        else await MountAsync(d);
    }

    /// <summary>A drive's uploads run unless the user paused them or the daily cap holds them back.</summary>
    void ApplyPause(DriveController d)
    {
        // disconnecting with "wait for the uploads", or handing the drive to another PC: everything must go up
        if (d.Stopping || d.Session is not { } s || s.Sync.HandingOver) return;
        s.Engine.PausedAll = d.ManualPaused || _daily.CapReached;
    }

    public void SetPaused(DriveController d, bool paused)
    {
        d.ManualPaused = paused;
        // resuming while the daily cap holds: the user's pause ends; past the cap only if the user agrees
        if (!paused && (!_daily.CapReached || AskPastCap())) d.Session?.Engine.ResumeAll();
        foreach (var x in _drives) ApplyPause(x);
    }

    /// <summary>"Resume all": the user's pauses end; past the daily cap only if the user agrees.</summary>
    public void ResumeEverything()
    {
        if (_daily.CapReached && !AskPastCap())
        {
            // only the user's own pauses end: the cap still holds until midnight
            foreach (var d in _drives) { d.ManualPaused = false; ApplyPause(d); }
            return;
        }
        foreach (var d in _drives) { d.ManualPaused = false; d.Session?.Engine.ResumeAll(); }
        foreach (var d in _drives) ApplyPause(d);
    }

    bool AskPastCap()
    {
        if (Ui.Ask(L.T($"הגעת היום לתקרה היומית ({_daily.CapGb:0.#} GB). להמשיך להעלות היום בכל זאת?",
                $"Today's daily cap ({_daily.CapGb:0.#} GB) was reached. Keep uploading today anyway?")) != DialogResult.Yes) return false;
        _daily.LiftCapForToday();
        return true;
    }

    public void ResumeOnly(IReadOnlyList<(DriveController D, string Id)> files)
    {
        if (files.Count == 0) return;
        bool capHolds = _daily.CapReached;
        if (capHolds && Ui.Ask(L.N(files.Count,
                $"הגעת היום לתקרה היומית ({_daily.CapGb:0.#} GB). להעלות את הקובץ שבחרת בכל זאת?\n\nשאר הקבצים יישארו מושהים עד מחר, או עד שתמשיך אותם.",
                $"הגעת היום לתקרה היומית ({_daily.CapGb:0.#} GB). להעלות את {{0}} הקבצים שבחרת בכל זאת?\n\nשאר הקבצים יישארו מושהים עד מחר, או עד שתמשיך אותם.",
                $"Today's daily cap ({_daily.CapGb:0.#} GB) was reached. Upload the file you chose anyway?\n\nThe other files stay paused until tomorrow, or until you resume them.",
                $"Today's daily cap ({_daily.CapGb:0.#} GB) was reached. Upload the {{0}} files you chose anyway?\n\nThe other files stay paused until tomorrow, or until you resume them.")) != DialogResult.Yes) return;
        // the drive (or the cap) keeps everything else paused, also files copied in later: only these go on
        foreach (var g in files.GroupBy(f => f.D)) g.Key.Session?.Engine.Resume(g.Select(f => f.Id).ToList());
    }

    public void LiftCap()
    {
        _daily.LiftCapForToday();
        foreach (var d in _drives) ApplyPause(d);
    }

    /// <summary>The settings of the program itself: one window, one tab, one of each setting.</summary>
    public void OpenSettings() => ShowMain(DriveGram.Tray.MainForm.Tab.Settings);

    public void SetLimits(double alertGb, double capGb)
    {
        _daily.SetLimits(alertGb, capGb);
        foreach (var d in _drives) ApplyPause(d);
    }

    public void SetTheme(Setup.ThemeKind kind) => Setup.Theme.Set(kind);

    /// <summary>The look changed: the icons drawn in the accent colour are made again, and every window
    /// already wears the new colours (Theme.Set saw to that).</summary>
    void OnLookChanged()
    {
        _idleBmp = _busyBmp = null;
        _main?.RefreshAll();
        UpdateIcon();
    }

    /// <summary>
    /// The language changed: every text on screen was built in the old one, so the main window is built
    /// again (the tray's menu is built each time it opens, and the other windows when they are opened).
    /// </summary>
    public void SetLanguage(string preference)
    {
        if (L.Preference == preference) return;
        L.Set(preference);
        Log.Info($"tray: the language is now '{preference}'");
        if (_main is { IsDisposed: false } old)
        {
            var place = old.Bounds;
            var state = old.WindowState;
            old.Close();
            old.Dispose();
            _main = null;
            ShowMain(DriveGram.Tray.MainForm.Tab.Settings);
            if (_main is { } fresh && state == FormWindowState.Normal) { fresh.StartPosition = FormStartPosition.Manual; fresh.Bounds = place; }
            else if (_main is { } max) max.WindowState = state;
        }
        UpdateIcon();
    }

    /// <summary>The icon's menu, as a left click opens it (the same menu a right click shows).</summary>
    void ShowTrayMenu()
    {
        try
        {
            // NotifyIcon knows how to place its own menu against the taskbar; nothing public asks for it
            var show = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (show is not null) { show.Invoke(_icon, null); return; }
        }
        catch (Exception ex) { Log.Debug($"tray: menu: {ex.Message}"); }
        _menu.Show(Control.MousePosition);
    }

    void TrackDaily()
    {
        var running = _drives.Where(d => d.Session is not null).ToList(); // also a drive finishing its uploads before it stops
        long waiting = 0;
        foreach (var d in running) try { waiting += d.Session!.Engine.BytesToUpload(); } catch { }
        string? say = null;
        try { say = _daily.Tick(running.Select(d => ((object)d.Session!, d.Session!.Status.BytesUploaded)), waiting); }
        catch (Exception ex) { Log.Warn($"daily uploads: {ex.Message}"); }
        foreach (var d in _drives) ApplyPause(d);
        if (say is not null && !_quitting)
            Balloon(L.T("העלאות היום", "Today's uploads"), say, ToolTipIcon.Info, () => ShowTransfers());
    }

    public void PrepareFiles(IEnumerable<(DriveSession Session, string Id)> files)
    {
        var list = files.ToList();
        foreach (var (s, id) in list) _preparer.Start(s, id);
        Balloon(L.T("מכין במחשב הזה", "Preparing on this PC"),
            L.N(list.Count,
                "מוריד את הקובץ כולו. תקבל הודעה כשהוא מוכן, ואפשר לעקוב בלשונית \"העברות\" בחלון של DriveGram.",
                "מוריד {0} קבצים במלואם. תקבל הודעה כשכל אחד מוכן, ואפשר לעקוב בלשונית \"העברות\" בחלון של DriveGram.",
                "Downloading the whole file. You will be told when it is ready; follow it on the \"Transfers\" tab of the DriveGram window.",
                "Downloading {0} whole files. You will be told when each is ready; follow them on the \"Transfers\" tab of the DriveGram window."),
            ToolTipIcon.Info, () => ShowTransfers(local: true));
    }

    public async Task<long> FreeFiles(IEnumerable<(DriveSession Session, string Id)> files, bool confirmed = false)
    {
        var list = files.ToList();
        if (list.Count == 0) return 0;
        int prepared = list.Count(f => f.Session.Engine.IsPrepared(f.Id));
        if (!confirmed && prepared > 0 && Ui.Ask(L.N(prepared,
                "לפנות מהמחשב הזה קובץ שהכנת? הוא שמור בטלגרם, אבל ייפתח שוב רק עם אינטרנט, ויורד מחדש.",
                "לפנות מהמחשב הזה {0} קבצים שהכנת? הם שמורים בטלגרם, אבל ייפתחו שוב רק עם אינטרנט, ויורדו מחדש.",
                "Free a file you prepared from this PC? It is stored in Telegram, but opens again only online, downloaded again.",
                "Free {0} files you prepared from this PC? They are stored in Telegram, but open again only online, downloaded again.")) != DialogResult.Yes)
            return 0;
        foreach (var (s, id) in list) _preparer.Cancel(s, id);
        // a big prepared file is thousands of small cache files: deleted off the UI thread
        long freed = await Task.Run(() =>
        {
            long n = 0;
            foreach (var (s, id) in list)
                try { n += s.Engine.FreeLocal(id); }
                catch (Exception ex) { Log.Warn($"free {id}: {ex.Message}"); }
            return n;
        });
        foreach (var (s, id) in list) try { ShellChanged(s.FullPathOf(id)); } catch { }
        if (confirmed) return freed;
        if (freed == 0)
            Ui.Info(L.N(list.Count,
                "לא היה מה לפנות: הקובץ עוד מחכה להעלאה לטלגרם, או שאין ממנו עותק במחשב הזה.",
                "לא היה מה לפנות: הקבצים עוד מחכים להעלאה לטלגרם, או שאין מהם עותק במחשב הזה.",
                "Nothing to free: the file is still waiting to upload to Telegram, or there is no copy of it on this PC.",
                "Nothing to free: the files are still waiting to upload to Telegram, or there is no copy of them on this PC."));
        else
            Balloon(L.T("התפנה מקום ✓", "Space freed ✓"), L.N(list.Count, $"פונו {Ui.Size(freed)} במחשב הזה. הקובץ עצמו שמור בטלגרם.", $"פונו {Ui.Size(freed)} במחשב הזה. הקבצים עצמם שמורים בטלגרם.",
                $"{Ui.Size(freed)} freed on this PC. The file itself is stored in Telegram.", $"{Ui.Size(freed)} freed on this PC. The files themselves are stored in Telegram."));
        return freed;
    }

    /// <summary>A short notice from a window (e.g. the space freed).</summary>
    public void Notify(string title, string text) => Balloon(title, text);

    void OnPrepared(Preparer.Job job, bool ok, string? error)
    {
        try { ShellChanged(job.Session.FullPathOf(job.Id)); } catch { }
        if (ok)
            Balloon(L.T("הקובץ מוכן במחשב הזה ✓", "The file is ready on this PC ✓"),
                L.T($"\"{L.Name(job.Name)}\" ייפתח מיד, גם בלי אינטרנט, ונשמר עד שתפנה אותו (בלשונית \"העברות\" בחלון של DriveGram, או בלחיצה ימנית עליו).",
                    $"\"{job.Name}\" opens at once, even offline, and stays until you free it (on the \"Transfers\" tab of the DriveGram window, or by right-clicking it)."),
                ToolTipIcon.Info, () => ShowTransfers(local: true));
        else if (!string.IsNullOrEmpty(error))
            Balloon(L.T("ההכנה לא הושלמה", "Preparing did not finish"), $"\"{L.Name(job.Name)}\": {error}", ToolTipIcon.Warning, () => ShowTransfers(local: true));
    }

    /// <summary>A Windows path on one of the running drives: its file (null: not a DriveGram file).</summary>
    (DriveSession Session, string Id)? ResolveShellPath(string path)
    {
        DriveController[] drives;
        try { drives = _drives.ToArray(); } catch { return null; }
        foreach (var d in drives)
        {
            if (d.Session is not { } s || d.Stopping) continue;
            if (!path.StartsWith(s.MountPoint, StringComparison.OrdinalIgnoreCase)) continue;
            try { return s.FileIdOf(path) is { } id ? (s, id) : null; }
            catch { return null; }
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    /// <summary>A file started or finished its upload, or was paused or resumed: Explorer is told to redraw
    /// its icon (it does not ask by itself). Told a moment later, once the extension's own short memory of
    /// the old state has passed.</summary>
    void TrackShellIcons(DriveController d)
    {
        if (_bridge is not { } bridge) return;
        if (d.Session is not { } s || d.Stopping) { d.ShellSeen = null; d.ShellSeenFor = null; return; }
        if (d.ShellBusy) return;
        d.ShellBusy = true;
        var before = d.ShellSeenFor == s ? d.ShellSeen : null;
        // the database work in the background: during a big copy it waits behind the writes, and the tray's
        // menu and windows must not
        _ = Task.Run(() =>
        {
            try
            {
                var now = s.Engine.PendingStates();
                var seen = now;
                var paths = new List<string>();
                if (before is not null) // the first look: Explorer asks by itself as it shows the files
                {
                    // finished first (they may now say "saved"), then new ones and paused / resumed ones
                    var changed = before.Keys.Where(id => !now.ContainsKey(id))
                        .Concat(now.Where(x => !before.TryGetValue(x.Key, out var p) || p != x.Value).Select(x => x.Key))
                        .ToList();
                    var told = new List<string>();
                    var heldBack = new List<string>();
                    var byPath = new List<(string Id, string Path)>();
                    foreach (var id in changed.Take(2000))
                        if (s.LivePathOf(id) is { } path) byPath.Add((id, path)); // gone or in the trash: nothing to redraw
                    heldBack.AddRange(changed.Skip(2000));
                    if (byPath.Count <= 200) { told.AddRange(byPath.Select(x => x.Id)); paths.AddRange(byPath.Select(x => x.Path)); }
                    else
                    {
                        // many at once (pause all, a big copy): whole folders, each at most every 10 s, 100 a look;
                        // the files of the others are told on a later look
                        long t = Environment.TickCount64;
                        int dirs = 0;
                        foreach (var g in byPath.GroupBy(x => Path.GetDirectoryName(x.Path) ?? "", StringComparer.OrdinalIgnoreCase))
                        {
                            bool recent = _dirToldAt.TryGetValue(g.Key, out var at) && t - at < 10_000;
                            if (g.Key.Length > 0 && !recent && dirs < 100)
                            {
                                dirs++;
                                _dirToldAt[g.Key] = t;
                                _dirsToTell.Enqueue(g.Key);
                                told.AddRange(g.Select(x => x.Id));
                            }
                            else heldBack.AddRange(g.Select(x => x.Id));
                        }
                        if (_dirToldAt.Count > 10_000) _dirToldAt.Clear();
                    }
                    if (heldBack.Count > 0)
                    {
                        // kept as they were: the next look sees them as changed again
                        seen = new Dictionary<string, bool>(now);
                        foreach (var id in heldBack)
                            if (before.TryGetValue(id, out var p)) seen[id] = p; else seen.Remove(id);
                    }
                }
                // files shown as "not in Telegram yet" that are in it now (a rename sent between two looks)
                paths.AddRange(bridge.TakeNoLongerWaiting(s));
                _ui.Post(_ => { if (d.Session == s && !d.Stopping) { d.ShellSeen = seen; d.ShellSeenFor = s; } }, null);
                var dirsNow = new List<string>();
                while (_dirsToTell.TryDequeue(out var dir)) dirsNow.Add(dir);
                if (paths.Count == 0 && dirsNow.Count == 0) return;
                bridge.Forget(paths);
                _ = Task.Delay(2500).ContinueWith(_ =>
                {
                    bridge.Forget(paths);
                    foreach (var p in paths) ShellChanged(p);
                    foreach (var dir in dirsNow) ShellFolderChanged(dir);
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { Log.Debug($"shell icons: {ex.Message}"); }
            finally { d.ShellBusy = false; }
        });
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _dirToldAt = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _dirsToTell = new();
    readonly Dictionary<string, long> _lateToldAt = new(StringComparer.OrdinalIgnoreCase); // UI thread only

    /// <summary>Files whose answer reached Explorer too late (no mark shown): redrawn, a moment later.</summary>
    void RedrawLateAnswers()
    {
        if (_bridge is not { } bridge) return;
        // each file at most every 20 s, 200 a look: redrawing makes Explorer ask again, and a tray still busy
        // must not be asked ever more
        long t = Environment.TickCount64;
        var late = bridge.TakeLate().Where(p => !(_lateToldAt.TryGetValue(p, out var at) && t - at < 20_000)).Take(200).ToList();
        if (late.Count == 0) return;
        foreach (var p in late) _lateToldAt[p] = t;
        if (_lateToldAt.Count > 10_000) _lateToldAt.Clear();
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            bridge.Forget(late);
            foreach (var p in late) ShellChanged(p);
        }, TaskScheduler.Default);
    }

    /// <summary>Explorer redraws a whole folder (its files' overlay icons included).</summary>
    static void ShellFolderChanged(string dir)
    {
        try { SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, dir, IntPtr.Zero); } catch { }
    }

    /// <summary>Explorer redraws the file's overlay icon (how much of it is on this PC).</summary>
    static void ShellChanged(string path)
    {
        try { SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, path, IntPtr.Zero); } catch { }
    }

    void ShowSwitchAccount(DriveController d)
    {
        if (d.SwitchAccount is { IsDisposed: false }) { d.SwitchAccount.Activate(); return; }
        var f = new SwitchAccountForm(d.State);
        f.OpenBackup += () => ShowBackup(d);
        f.SignInAnother += () => { f.Close(); _ = SetupAsync(d, freshSignIn: true); };
        f.AccountChosen += a => SwitchAccountAsync(d, a);
        d.SwitchAccount = f;
        f.Show();
    }

    /// <summary>
    /// Move the drive to another account signed in on this PC: check that it administers both channels with
    /// the rights DriveGram needs, then reconnect the drive with it. Nothing is uploaded again; what waits
    /// to upload goes on with the new account. Returns null when done, or what is wrong.
    /// </summary>
    async Task<string?> SwitchAccountAsync(DriveController d, AccountInfo a)
    {
        if (d.State.Config.Channels is not { } ch) return "";
        DriveAccess access;
        {
            // a throw-away copy of the profile, pointing at that account (never saved)
            var probe = AppState.Open(d.State.Id);
            probe.Config.AccountKey = a.Key;
            probe.Config.ApiId = a.ApiId;
            probe.Config.ApiHash = a.ApiHash;
            using var lease = TelegramAccounts.Acquire(probe);
            access = await Task.Run(() => TelegramRemote.DriveAccessAsync(lease.Client, ch));
        }
        var who = L.Name(a.Name);
        if (!access.JournalFound || !access.DataFound)
            return L.T($"החשבון {who} עוד לא מנהל את {(!access.JournalFound && !access.DataFound ? "שני הערוצים" : !access.JournalFound ? "ערוץ היומן" : "ערוץ הקבצים")} של הכונן. הוסף אותו ב\"גיבוי בחשבון נוסף…\" ונסה שוב.",
                $"The account {a.Name} does not administer {(!access.JournalFound && !access.DataFound ? "both channels" : !access.JournalFound ? "the journal channel" : "the data channel")} of the drive. Add it in \"Backup on another account…\" and try again.");
        if (!access.Ok)
            return L.T($"ל־{who} אין את כל ההרשאות שהכונן צריך (לפרסם, לערוך ולמחוק הודעות) ב{(!access.JournalRights && !access.DataRights ? "שני הערוצים" : !access.JournalRights ? "ערוץ היומן" : "ערוץ הקבצים")}. הוסף אותו שוב ב\"גיבוי בחשבון נוסף…\": זה נותן את כל ההרשאות.",
                $"{a.Name} lacks some rights the drive needs (post, edit and delete messages) in {(!access.JournalRights && !access.DataRights ? "both channels" : !access.JournalRights ? "the journal channel" : "the data channel")}. Add it again in \"Backup on another account…\": that gives every right.");
        if (Ui.Ask(L.T($"להעביר את {d.Name} לחשבון {who}?\n\nהכונן יתנתק לרגע ויתחבר מחדש עם החשבון הזה. שום קובץ לא עולה מחדש, ומה שממתין להעלאה ימשיך עם החשבון החדש.",
                $"Move {d.Name} to the account {a.Name}?\n\nThe drive disconnects for a moment and connects again with that account. No file is uploaded again, and what waits to upload goes on with the new account.")) != DialogResult.Yes)
            return "";
        if (d.Session is not null && !await StopAsync(d, ask: false, waitForUploads: false))
            return L.T("לא הצלחתי לנתק את הכונן כרגע. נסה שוב בעוד רגע.", "Could not disconnect the drive right now. Try again in a moment.");
        var state = AppState.Open(d.State.Id);
        state.Config.AccountKey = a.Key;
        state.Config.ApiId = a.ApiId;
        state.Config.ApiHash = a.ApiHash;
        state.Config.AccountId = a.Id;
        state.Config.AccountName = a.Name;
        state.Save();
        Log.Info($"tray: {d.Name} moved to another Telegram account");
        var fresh = new DriveController(AppState.Open(state.Id));
        Replace(d, fresh);
        await MountAsync(fresh);
        UpdateIcon();
        if (fresh.Session is not null)
            Balloon(L.T($"{L.Ltr(fresh.Letter)} עבר לחשבון {who}", $"{fresh.Letter} moved to {a.Name}"),
                L.T("הכונן מחובר עם החשבון החדש. הקבצים לא השתנו.", "The drive is connected with the new account. The files did not change."), onClick: () => OpenDrive(fresh));
        return null;
    }

    static void ShowTrash(DriveController d)
    {
        if (d.Session is null) return;
        if (d.Trash is { IsDisposed: false }) { d.Trash.Activate(); return; }
        d.Trash = new TrashForm(d.Session);
        d.Trash.Show();
    }

    // ── menu & status ────────────────────────────────────────────────────────
    static string StatusOf(DriveController d)
    {
        if (d.Starting) return L.T("מתחבר…", "connecting…");
        if (d.Stopping) return L.T("מתנתק…", "disconnecting…");
        if (d.Session is not { } s) return d.Problem is null ? L.T("לא מחובר", "not connected") : d.Problem;
        var st = s.Status;
        if (!s.Sync.ControlTrusted) return L.T("⚠ רשומת הכונן שונתה מחוץ ל־DriveGram: קריאה בלבד", "⚠ the drive's record was changed outside DriveGram: read-only");
        if (s.Sync.NeedsUpdate) return L.T("קריאה בלבד: צריך לעדכן את DriveGram", "read-only: DriveGram needs an update");
        if (s.Sync.HandingOver)
            return L.T("מעביר את הכונן למחשב אחר", "handing the drive to another PC") + " (" + L.N(st.PendingFiles, "נשאר קובץ אחד להעלות", "נשארו {0} קבצים להעלות", "1 upload left", "{0} uploads left") + ")";
        if (s.Sync.HeldBackChanges is > 0 and var held)
            return "⚠ " + L.N(held,
                $"שינוי אחד ממתין כאן: הכונן פתוח לכתיבה ב־{L.Ltr(s.Sync.LeaseHolder ?? "")}, והשינוי יישלח כשהכונן יחזור לכאן",
                $"{{0}} שינויים ממתינים כאן: הכונן פתוח לכתיבה ב־{L.Ltr(s.Sync.LeaseHolder ?? "")}, והם יישלחו כשהכונן יחזור לכאן",
                $"1 change is waiting here: the drive is open for writing on {s.Sync.LeaseHolder}; it is sent when the drive comes back here",
                $"{{0}} changes are waiting here: the drive is open for writing on {s.Sync.LeaseHolder}; they are sent when the drive comes back here");
        if (!s.Writable) return s.Sync.LeaseHolder is { } h
            ? L.T($"קריאה בלבד (פתוח לכתיבה ב־{L.Ltr(h)})", $"read-only (open for writing on {h})")
            : L.T("קריאה בלבד כרגע (בודק…)", "read-only for now (checking…)");
        var wait = s.WaitingUntil is { } until ? L.T($" · טלגרם ביקש להמתין עד {until:HH:mm}", $" · Telegram asked to wait until {until:HH:mm}") : "";
        if (st.PendingFiles > 0 && st.UploadsInFlight == 0 && s.Engine.PendingNotPaused() == 0)
            return (s.Engine.PausedAll && !d.ManualPaused
                    ? L.T("ההעלאות מושהות עד חצות (תקרה יומית): ", "uploads paused until midnight (daily cap): ")
                    : L.T("ההעלאות מושהות: ", "uploads paused: "))
                   + L.N(st.PendingFiles, "קובץ אחד מחכה", "{0} קבצים מחכים", "1 file waits", "{0} files wait")
                   + L.T($" במחשב הזה ({Ui.Size(st.StagedBytes)})", $" on this PC ({Ui.Size(st.StagedBytes)})");
        if (s.Engine.UploadTrouble is { } bad && (st.PendingFiles > 0 || st.UploadsInFlight > 0))
            return L.T($"⚠ ההעלאה לא מצליחה מאז {bad.Since:HH:mm} · ", $"⚠ uploads have been failing since {bad.Since:HH:mm} · ")
                   + L.N(st.PendingFiles, "קובץ אחד מחכה", "{0} קבצים מחכים", "1 file waits", "{0} files wait")
                   + L.T($" במחשב הזה ({Short(bad.Error)})", $" on this PC ({Short(bad.Error)})");
        if (st.PendingFiles > 0 || st.UploadsInFlight > 0)
            return L.T("מעלה: ", "uploading: ") + L.N(st.PendingFiles, "קובץ אחד", "{0} קבצים", "1 file", "{0} files") + $", {Ui.Size(st.StagedBytes)}"
                   + UploadProgress(d, st) + wait;
        if (s.Sync.UnsentChanges is > 0 and var unsent)
            return L.N(unsent, "שינוי אחד ממתין לשליחה לטלגרם", "{0} שינויים ממתינים לשליחה לטלגרם", "1 change waiting to be sent to Telegram", "{0} changes waiting to be sent to Telegram") + wait;
        return L.T("הכל שמור בטלגרם ✓", "everything is saved in Telegram ✓");
    }

    static string ReadOnlyWhy(DriveSession s) => !s.Sync.ControlTrusted
        ? L.T("רשומת הכונן שונתה מחוץ ל־DriveGram, ולכן הוא לקריאה בלבד. בתפריט של הכונן: \"אשר את רשומת הכונן…\".",
              "The drive's record was changed outside DriveGram, so it is read-only. In the drive's menu: \"Confirm the drive's record…\".")
        : s.Sync.LeaseHolder is { } h
        ? L.T($"הכונן פתוח לכתיבה במחשב {L.Ltr(h)}. כאן אפשר רק לקרוא. כדי לכתוב כאן: בתפריט של הכונן, \"בקש את הכונן מ־{L.Ltr(h)}…\".",
              $"The drive is open for writing on {h}. Here it can only be read. To write here: \"Ask {h} for the drive…\" in the drive's menu.")
        : L.T("הכונן זמין כרגע לקריאה בלבד; DriveGram בודק אם אפשר לכתוב.", "The drive is read-only for now; DriveGram is checking whether it can write.");

    string Summary() => _drives.Count == 0
        ? L.T("אין עדיין כוננים", "no drives yet")
        : string.Join("\n", _drives.Select(d => _drives.Count > 1 ? $"{L.Ltr(d.Letter)} {StatusOf(d)}" : $"{d.Name}: {StatusOf(d)}"));

    // ── a new version on GitHub: a line in the menu, a bar in the main window, one notice per version ──
    readonly Updates _updates = new();
    UpdateForm? _updateForm;

    string UpdateText(UpdateInfo u) => L.T($"גרסה חדשה זמינה ({L.Ltr(u.Version.ToString())})…", $"A new version is available ({u.Version})…");
    static string UpdateBar(UpdateInfo u) => L.T($"יש גרסה חדשה של DriveGram: {L.Ltr(u.Version.ToString())} – לחץ כאן לפרטים", $"A new version of DriveGram: {u.Version} – click here for details");

    bool _checkingByHand;

    async Task CheckForUpdateNowAsync()
    {
        if (_checkingByHand) return;
        _checkingByHand = true; // the window opens by itself: no notice besides it
        try
        {
            bool ok = await _updates.CheckNowAsync();
            if (_updates.Offered is not null) { ShowUpdate(); return; }
            Ui.Info(ok ? L.T($"יש לך את הגרסה האחרונה של DriveGram ({L.Ltr(Updates.Current.ToString())}).", $"You have the latest version of DriveGram ({Updates.Current}).")
                       : L.T("לא הצלחתי לבדוק עכשיו – אין חיבור לאינטרנט, או שהאתר לא ענה. DriveGram ינסה שוב לבד.", "Could not check right now – no internet connection, or the site did not answer. DriveGram tries again by itself."));
        }
        finally { _checkingByHand = false; }
    }

    void OnUpdatesChanged()
    {
        var u = _updates.Offered;
        if (_main is { IsDisposed: false } m) m.ShowUpdate(u is null ? null : UpdateBar(u), ShowUpdate);
        // a window still showing an older release (a newer one came out): closed, it shows the new one when opened
        if (_updateForm is { IsDisposed: false, Downloading: false } open && open.Version != u?.Version) open.Close();
        if (u is not null && _updates.FirstTimeFor(u) && !_checkingByHand)
            Balloon(L.T("יש גרסה חדשה של DriveGram", "A new version of DriveGram"),
                L.T($"גרסה {L.Ltr(u.Version.ToString())} זמינה. לחץ כאן כדי לראות מה חדש, או מאוחר יותר מהתפריט של האייקון.",
                    $"Version {u.Version} is available. Click here to see what's new, or later from the icon's menu."),
                onClick: ShowUpdate);
    }

    void ShowUpdate()
    {
        if (_updates.Offered is not { } u) return;
        if (_updateForm is { IsDisposed: false }) { _updateForm.Activate(); return; }
        _updateForm = new UpdateForm(u, () => _drives.Sum(d => d.Session is { } s && !d.Stopping ? s.Status.PendingFiles : 0));
        _updateForm.FormClosed += (_, _) => { if (_updateForm?.Skipped == true) _updates.Skip(u); _updateForm = null; };
        _updateForm.Show();
    }

    void BuildMenu()
    {
        _menu.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _menu.Items.Clear();
        if (_updates.Offered is { } update)
        {
            var item = Item(UpdateText(update), ShowUpdate);
            item.Image = Glyphs.Make('\uE896' /* download arrow */, Setup.Theme.Attention, 16);
            item.ForeColor = Setup.Theme.Attention;
            _menu.Items.Add(item);
            _menu.Items.Add(new ToolStripSeparator());
        }
        var open = Item(L.T("פתח את DriveGram", "Open DriveGram"), () => ShowMain(), bold: true);
        open.Image = _idleBitmap;
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());
        foreach (var d in _drives)
        {
            var account = d.State.Folder is not null ? L.T("תיקייה מקומית", "local folder") : L.Name(d.State.Config.AccountName ?? "");
            var item = new ToolStripMenuItem($"{d.Name}{(string.IsNullOrEmpty(account) ? "" : $" · {account}")} — {StatusOf(d)}")
            {
                Image = d.Busy || d.Starting ? _busyBitmap : _idleBitmap,
            };
            bool on = d.Session is not null && !d.Stopping;
            item.DropDownItems.AddRange(DriveItems(d).ToArray());
            item.Click += (_, _) => { if (on) OpenDrive(d); };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        var transfers = Item(L.T("העברות וקבצים במחשב…", "Transfers and files on this PC…"), () => ShowTransfers());
        transfers.Image = Glyphs.Make(Glyphs.Upload, Setup.Theme.AccentDark, 16);
        _menu.Items.Add(transfers);
        _menu.Items.Add(Item(L.T("קבצים שהכנתי…", "Files I prepared…"), ShowPrepared));
        _menu.Items.Add(Item(L.T("חשבונות טלגרם והוספת כונן…", "Telegram accounts and adding a drive…"), () => ShowMain(DriveGram.Tray.MainForm.Tab.Accounts)));
        var settings = Item(L.T("הגדרות (שפה, מראה, תקרה יומית)…", "Settings (language, look, daily cap)…"), OpenSettings);
        settings.Image = Glyphs.Make(Glyphs.Settings, Setup.Theme.AccentDark, 16);
        _menu.Items.Add(settings);

        var autostart = new ToolStripMenuItem(L.T("הפעל עם וינדוס", "Start with Windows")) { Checked = ShellIntegration.AutostartEnabled };
        autostart.Click += (_, _) =>
        {
            try { ShellIntegration.SetAutostart(!ShellIntegration.AutostartEnabled); }
            catch (Exception ex) { Ui.Error(Setup.TelegramErrors.Text(ex)); }
        };
        _menu.Items.Add(autostart);

        _menu.Items.Add(Item(L.T("בדוק אם יש גרסה חדשה", "Check for a new version"), () => _ = CheckForUpdateNowAsync()));
        _menu.Items.Add(Item(L.T("פתח את תיקיית יומן הפעילות", "Open the log folder"),
            () => Process.Start(new ProcessStartInfo(Path.Combine(AppState.Root, "logs")) { UseShellExecute = true })));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(L.T("יציאה", "Exit"), async () => await QuitAsync(ask: true)));
        Setup.ThemePaint.Menu(_menu); // the menu wears the look the user chose
    }

    /// <summary>Everything that can be done with one drive (the tray menu, and "⋯" in the main window).</summary>
    public List<ToolStripItem> DriveItems(DriveController d)
    {
        var list = new List<ToolStripItem>();
        bool on = d.Session is not null && !d.Stopping;
        list.Add(Item(L.T("פתח את הכונן", "Open the drive"), () => OpenDrive(d), on, bold: true));
        list.Add(Item(L.T("סל המחזור…", "Recycle bin…"), () => ShowTrash(d), on));
        list.Add(Item(L.T("ייצוא כל הקבצים לתיקייה…", "Export every file to a folder…"), () => Export(d), on));
        // works disconnected too: only local copies are deleted
        list.Add(Item(L.T("פנה מקום במחשב הזה…", "Free up space on this PC…"), () => FreeUpSpace(d), !d.Starting && !d.Stopping && !d.FreeingOffline));
        list.Add(new ToolStripSeparator());
        if (d.Session is null)
            list.Add(Item(L.T("חבר", "Connect"), async () => { if (d.NeedsSetup) await SetupAsync(d); else await MountAsync(d); },
                !d.Starting && !d.FreeingOffline && !d.ChangingLetter));
        else list.Add(Item(L.T("נתק", "Disconnect"), async () => await StopAsync(d), !d.Stopping && !d.ChangingLetter));
        if (d.Session is { Sync.ControlTrusted: false } tampered)
            list.Add(Item(L.T("אשר את רשומת הכונן…", "Confirm the drive's record…"), async () => await ConfirmRecordAsync(d, tampered), !d.Stopping, bold: true));
        if (d.Session is { Writable: false, Sync: { LeaseHolder: { } holder, NeedsUpdate: false } })
            list.Add(Item(L.T($"בקש את הכונן מ־{L.Ltr(holder)}…", $"Ask {holder} for the drive…"), () => AskForDrive(d), !d.Stopping, bold: true));
        // "Settings and sign-in" was one wizard for three different things: split by what is wanted
        bool idle = !d.Starting && !d.Stopping && !d.FreeingOffline && !d.ChangingLetter;
        if (d.NeedsSetup)
            list.Add(Item(L.T("התחברות לטלגרם…", "Telegram sign-in…"), async () => await SetupAsync(d), idle, bold: true));
        list.Add(Item(L.T("סיסמה ומפתח שחזור…", "Password and recovery key…"), () => ShowSecurity(d), on));
        // on: forget it (the password is asked each time the drive connects); off: remember it again,
        // from the key of the connected drive (disconnected, it is offered when the password is asked)
        bool remembered = d.State.HasRememberedMaster;
        var remember = Item(remembered || on ? L.T("לזכור את הסיסמה במחשב הזה", "Remember the password on this PC")
            : L.T("לזכור את הסיסמה במחשב הזה (אחרי שהכונן מתחבר)", "Remember the password on this PC (once the drive is connected)"),
            () => ToggleRemember(d), remembered || on);
        remember.Checked = remembered;
        list.Add(remember);
        list.Add(Item(L.T("אות ושם הכונן…", "Drive letter and name…"), async () => await ChangeLetterAndNameAsync(d), idle));
        list.Add(Item(d.State.Config.NetworkDrive ? L.T("סוג הכונן: כונן רשת…", "Drive type: network drive…") : L.T("סוג הכונן: דיסק רגיל…", "Drive type: regular disk…"),
            async () => await ChangeDriveTypeAsync(d), idle));
        if (d.State.Folder is null && d.State.Config.Channels is not null)
        {
            list.Add(Item(L.T("גיבוי בחשבון נוסף…", "Backup on another account…"), () => ShowBackup(d)));
            list.Add(Item(L.T("העבר לחשבון טלגרם אחר…", "Move to another Telegram account…"), () => ShowSwitchAccount(d), !d.Starting && !d.Stopping));
        }
        if (!d.NeedsSetup) // the full wizard: another drive of the account, or signing in again (rare)
            list.Add(Item(L.T("החלפה לכונן אחר באותו חשבון…", "Switch to another drive of the account…"), async () => await SetupAsync(d, chooseOther: true), idle));
        list.Add(new ToolStripSeparator());
        list.Add(Item(L.T("הסר מהמחשב הזה…", "Remove from this PC…"), async () => await RemoveAsync(d), idle));
        // never offered while the drive's pairing is in doubt (it could name another drive's data)
        var delete = Item(L.T("מחק את הכונן לצמיתות…", "Delete the drive permanently…"), async () => await DeleteForeverAsync(d),
            !d.Starting && !d.Stopping && !d.MustChooseDrive);
        delete.ForeColor = Setup.Theme.Error;
        list.Add(delete);
        return list;
    }

    static readonly Font BoldFont = new(Ui.Font, FontStyle.Bold);
    Bitmap? _idleBmp, _busyBmp;
    Bitmap _idleBitmap => _idleBmp ??= _idle.ToBitmap();
    Bitmap _busyBitmap => _busyBmp ??= _busy.ToBitmap();

    /// <summary>Remember the drive's password on this PC, or forget it (asked each time the drive connects).</summary>
    void ToggleRemember(DriveController d)
    {
        var name = L.Name(d.State.Config.VolumeLabel);
        if (d.State.HasRememberedMaster)
        {
            if (Ui.Ask(L.T($"לשכוח את הסיסמה של \"{name}\" במחשב הזה?\n\nהכונן יישאר מחובר עכשיו. מהחיבור הבא, הסיסמה תתבקש בכל פעם שהכונן מתחבר (גם כשנכנסים לווינדוס).",
                    $"Forget the password of \"{name}\" on this PC?\n\nThe drive stays connected now. From the next time, the password is asked each time the drive connects (also when you sign in to Windows).")) != DialogResult.Yes) return;
            d.State.ForgetMaster();
            Log.Info($"tray: the password of {d.State.Id} is no longer remembered");
        }
        else if (d.Session is { } s)
        {
            d.State.RememberMaster(s.Keys.ExportMaster());
            Log.Info($"tray: the password of {d.State.Id} is remembered");
            Balloon(L.T("הסיסמה נשמרה ✓", "Password remembered ✓"),
                L.T($"הסיסמה של \"{name}\" תיזכר במחשב הזה, והכונן יתחבר בלי לשאול.", $"The password of \"{name}\" is remembered on this PC: the drive connects without asking."));
        }
    }

    static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true, bool bold = false)
    {
        var i = new ToolStripMenuItem(text) { Enabled = enabled };
        if (bold) i.Font = BoldFont;
        i.Click += (_, _) => onClick();
        return i;
    }

    void UpdateIcon()
    {
        if (_quitting) { TrackDaily(); return; } // "wait, then exit": what still goes up counts for today
        foreach (var d in _drives) TrackUploads(d);
        foreach (var d in _drives) TrackUploadTrouble(d);
        foreach (var d in _drives) TrackShellIcons(d);
        RedrawLateAnswers();
        TrackLowDisk();
        _updates.Tick();
        TrackDaily();
        bool busy = _drives.Any(d => d.Busy || d.Starting || d.Stopping || d.FreeingOffline);
        _icon.Icon = busy ? _busy : _idle;
        if (_drives.Any(d => d.Stopping)) return; // the stop progress owns the tooltip
        SetTooltip(_drives.Count == 1 ? $"{_drives[0].Letter} {StatusOf(_drives[0])}" : Summary().Replace("\n", " | "));
    }

    /// <summary>
    /// Uploads that keep failing used to show as "uploading, 0%" for as long as it took. Tell the user
    /// once per spell: the files are safe here, and the drive goes on by itself when the line is back.
    /// </summary>
    void TrackUploadTrouble(DriveController d)
    {
        if (d.Session is not { } s || d.Stopping || _quitting) return;
        if (s.Engine.UploadTrouble is not { } bad) { d.ToldUploadsFailing = null; return; }
        if (d.ToldUploadsFailing == bad.Since) return;
        d.ToldUploadsFailing = bad.Since;
        Log.Warn($"{d.State.Id}: uploads failing since {bad.Since:HH:mm:ss}: {bad.Error}");
        Balloon(L.T("ההעלאה לטלגרם לא מצליחה", "Uploads to Telegram are failing"),
            L.T($"{d.Name} לא מצליח להעלות מאז {bad.Since:HH:mm} ({Short(bad.Error)}). הקבצים שמורים במחשב הזה וההעלאה תימשך מעצמה כשהחיבור יחזור.",
                $"{d.Name} has not been able to upload since {bad.Since:HH:mm} ({Short(bad.Error)}). The files are kept on this PC and uploading continues by itself when the connection is back."),
            ToolTipIcon.Warning, () => ShowTransfers());
    }

    DateTime _lastStartBalloon;

    /// <summary>
    /// Tells the user when a real upload starts ("wait for the message before disconnecting") and when
    /// everything is in Telegram ("you can disconnect / shut down"), and keeps the speed for the time left.
    /// </summary>
    void TrackUploads(DriveController d)
    {
        if (d.Session is not { } s || d.Stopping) { d.Uploading = false; d.SampleAt = default; return; }
        var st = s.Status;
        var now = DateTime.UtcNow;
        if (d.SampleAt != default && (now - d.SampleAt).TotalSeconds >= 1)
        {
            double inst = Math.Max(0, st.BytesUploaded - d.SampleBytes) / (now - d.SampleAt).TotalSeconds;
            d.Speed = d.Speed <= 0 ? inst : d.Speed * 0.75 + inst * 0.25;
        }
        d.SampleBytes = st.BytesUploaded;
        d.SampleAt = now;

        bool busy = st.UploadsInFlight > 0 || s.Engine.PendingNotPaused() > 0; // paused files are not "uploading"
        if (busy && !d.Uploading)
        {
            d.Uploading = true;
            d.UploadSince = now;
            d.UploadedAtStart = st.BytesUploaded;
            d.MostFiles = 0;
            d.ToldUploadStart = false;
            d.Speed = 0;
        }
        if (busy)
        {
            d.MostFiles = Math.Max(d.MostFiles, st.PendingFiles);
            // a real upload (not one small file saved in passing), and not a new balloon for every batch
            bool real = st.StagedBytes >= 20L << 20 || now - d.UploadSince > TimeSpan.FromSeconds(15);
            if (!d.ToldUploadStart && real && s.Writable && now - _lastStartBalloon > TimeSpan.FromMinutes(3))
            {
                d.ToldUploadStart = true;
                _lastStartBalloon = now;
                Balloon(L.T($"מעלה לטלגרם ({L.Ltr(d.Letter)})", $"Uploading to Telegram ({d.Letter})"),
                    L.T("אפשר להמשיך לעבוד כרגיל. מה שעוד לא עלה שמור במחשב הזה, גם אם תכבה אותו, ויעלה כשהכונן יתחבר שוב. כדי שהקבצים יהיו בטלגרם (ובמחשבים אחרים), חכה להודעה \"הכל שמור בטלגרם\". לחץ כאן כדי לראות את ההעלאות.",
                        "You can keep working as usual. What is not uploaded yet stays on this PC, even if you shut it down, and goes up when the drive connects again. For the files to be in Telegram (and on other PCs), wait for the \"everything is saved in Telegram\" message. Click here to see the uploads."),
                    onClick: () => ShowTransfers());
            }
            return;
        }
        if (!d.Uploading || s.Sync.UnsentChanges > 0) return; // the last changes are still on their way
        d.Uploading = false;
        if (st.PendingFiles > 0) return; // the rest is paused (by the user, or the daily cap): not "everything is saved"
        if (!d.ToldUploadStart && now - d.UploadSince < TimeSpan.FromSeconds(20)) return; // a short one: no news
        long sent = Math.Max(0, st.BytesUploaded - d.UploadedAtStart);
        Balloon(L.T("הכל שמור בטלגרם ✓", "Everything is saved in Telegram ✓"),
            L.N(Math.Max(1, d.MostFiles), $"הקובץ עלה ({Ui.Size(sent)}). ", $"{{0}} קבצים עלו ({Ui.Size(sent)}). ",
                    $"The file was uploaded ({Ui.Size(sent)}). ", $"{{0}} files were uploaded ({Ui.Size(sent)}). ")
            + L.T("עכשיו הקבצים זמינים גם ממחשבים אחרים.", "The files are now available from other PCs too."),
            onClick: () => OpenDrive(d));
    }

    /// <summary>", 45%, about 2 minutes left" for the running upload (empty while there is no estimate yet).</summary>
    static string UploadProgress(DriveController d, DriveGram.Engine.EngineStatus st)
    {
        if (!d.Uploading) return "";
        long done = Math.Max(0, st.BytesUploaded - d.UploadedAtStart), left = st.StagedBytes;
        if (done + left <= 0) return "";
        int pct = (int)Math.Clamp(100 * done / (done + left), 0, 99);
        var text = $", {pct}%";
        if (d.Speed > 16 * 1024 && DateTime.UtcNow - d.UploadSince > TimeSpan.FromSeconds(6))
        {
            double sec = left / d.Speed;
            text += sec < 60 ? L.T(", עוד פחות מדקה", ", under a minute left")
                : sec < 3600 ? L.N((int)Math.Ceiling(sec / 60), ", עוד כדקה", ", עוד כ־{0} דקות", ", about a minute left", ", about {0} minutes left")
                : L.N((int)Math.Round(sec / 3600), ", עוד כשעה", ", עוד כ־{0} שעות", ", about an hour left", ", about {0} hours left");
        }
        return text;
    }

    void SetTooltip(string text)
    {
        var t = "DriveGram · " + text;
        _icon.Text = t.Length > 127 ? t[..126] + "…" : t;
    }

    /// <summary>A notification. onClick: what clicking it does (nothing when null) — always about the
    /// balloon on screen, never another drive's pending setup.</summary>
    void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.None, Action? onClick = null)
    {
        // Hebrew starts with a right-to-left mark: where the direction is guessed from the first letter,
        // a text opening with a drive letter would otherwise be laid out left-to-right
        _icon.BalloonTipTitle = L.RtlStart(title);
        _icon.BalloonTipText = L.RtlStart(text);
        _icon.BalloonTipIcon = icon;
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000);
        _icon.BalloonTipClicked -= OnBalloonClicked;
        _icon.BalloonTipClicked += OnBalloonClicked;
    }

    Action? _balloonClick;

    void OnBalloonClicked(object? sender, EventArgs e) => _balloonClick?.Invoke();
}
