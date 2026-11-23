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
    /// <summary>The saved channels did not match: the next setup shows the list of drives.</summary>
    public bool MustChooseDrive;
    public ExportForm? ExportWindow;
    public HandoverWaitForm? HandoverWait;
    public HandoverAskForm? HandoverAsk;
    /// <summary>The writable state the user was last told about (no second balloon for the same news).</summary>
    public bool? ToldWritable;
    /// <summary>When the user was last told that Telegram cannot be reached for a file (Environment.TickCount64).</summary>
    public long ToldUnreachableAt;
    /// <summary>Keys just unlocked in the setup wizard: the first mount uses them instead of asking again.</summary>
    public Keys? PendingKeys;
    /// <summary>The last mount failed because Windows refused it (not offline, not a password).</summary>
    public bool LastMountRefused;
    /// <summary>Files kept as "(incomplete)" waiting for one shared balloon.</summary>
    public readonly List<(string Orig, string Kept, string Folder)> Incomplete = [];

    public DriveController(AppState state) => State = state;

    public string Letter => Session?.MountPoint ?? State.Config.MountPoint;
    public string Name => $"{L.Ltr(Letter)} {State.Config.VolumeLabel}";
    public bool Busy => !Stopping && Session is { } s && (s.Status.PendingFiles > 0 || s.Status.UploadsInFlight > 0);
}

/// <summary>
/// The notification-area app. One running copy per Windows user; it mounts every drive configured
/// on this PC (each on its own Telegram account) and shows what each is doing.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly SynchronizationContext _ui;
    readonly NotifyIcon _icon;
    readonly Icon _idle, _busy;
    readonly ContextMenuStrip _menu = new() { Font = Ui.Font };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly List<DriveController> _drives;
    readonly EventWaitHandle _activate, _quit;
    Setup.SetupWizard? _wizard;
    bool _quitting;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current!;
        var dir = AppContext.BaseDirectory;
        _idle = new Icon(Path.Combine(dir, "drivegram.ico"), SystemInformation.SmallIconSize);
        _busy = new Icon(Path.Combine(dir, "drivegram-busy.ico"), SystemInformation.SmallIconSize);
        foreach (var s in AppState.All()) TelegramAccounts.MigrateLegacy(s); // 0.8: one shared sign-in per account
        _drives = AppState.All().Select(s => new DriveController(s)).ToList();

        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _icon = new NotifyIcon { Icon = _idle, Text = "DriveGram", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => Activate();
        _timer.Tick += (_, _) => UpdateIcon();
        _timer.Start();
        SystemEvents.SessionEnding += (_, _) => OnSessionEnding();
        SystemEvents.SessionEnded += (_, _) => OnSessionEnded();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) _ui.Post(async _ => { await Task.Delay(3000); RetryWaitingDrivesNow(); }, null);
        };
        ShellIntegration.ClearStaleDriveIcons(_drives.Select(x => x.State.Config.MountPoint)); // left by a crash on a borrowed letter
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _ui.Post(_ => OnResume(), null); };

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEvent);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, DriveSession.QuitEventName);
        Listen(_activate, () => Activate());
        Listen(_quit, async () => await QuitAsync(ask: false));

        if (_drives.Count == 0) _ui.Post(async _ => await AddDriveAsync(), null);
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
        d.RetryTimer?.Stop();
        d.Starting = true;
        d.LastMountRefused = false;
        d.Problem = null;
        d.NeedsSetup = false;
        UpdateIcon();
        try
        {
            var session = await Task.Run(() => DriveSession.StartAsync(d.State, ctrl => UnlockAsync(d, ctrl), null, takeOver: false));
            if (!_drives.Contains(d) || _quitting)
            {
                // the drive was replaced (settings) or removed while it was starting: never leave it orphaned
                await Task.Run(() => session.StopAsync(waitForUploads: false));
                await session.DisposeAsync();
                return;
            }
            d.Session = session;
            StopRetrying(d);
            d.RetryDelaySeconds = 15;
            d.RetryDelaySeconds = 15;
            session.StopRequested += () => _ui.Post(async _ => await StopAsync(d, ask: false), null);
            session.SignedOut += () => _ui.Post(async _ => await OnSignedOutAsync(d), null);
            session.Sync.HandoverRequested += r => _ui.Post(async _ => await OnHandoverRequestedAsync(d, session, r), null);
            session.Engine.RemoteUnreachable += () => _ui.Post(_ => OnRemoteUnreachable(d, session), null);
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
            Balloon(L.T("לא ניתן לחבר את הכונן", "The drive could not be connected"), ex.Message, ToolTipIcon.Error,
                () => _ = SetupAsync(d)); // the message suggests another letter in the settings
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
                L.T($"{d.Name} לא נמצא בחשבון הטלגרם (אולי הערוצים שלו נמחקו, או שהחשבון הוסר מהם). פתח את \"הגדרות והתחברות…\" כדי להחליט מה לעשות.",
                    $"{d.Name} was not found in the Telegram account (its channels may have been deleted, or the account removed from them). Open \"Settings and sign-in…\" to decide what to do."),
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
                L.T($"{d.Name}: בעיה במחשב הזה, לא באינטרנט ({Short(Setup.TelegramErrors.Text(ex))}). פרטים ביומן הפעילות: בתפריט ← \"פתח את תיקיית יומן הפעילות\".",
                    $"{d.Name}: a problem on this PC, not the internet ({Short(Setup.TelegramErrors.Text(ex))}). Details in the log: menu → \"Open the log folder\"."),
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
        int pending = session.Status.PendingFiles;
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
            int pending = _drives.Sum(d => d.Session?.Status.PendingFiles ?? 0);
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
        foreach (var d in _drives.ToList()) await StopAsync(d, ask: false);
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
    async Task SetupAsync(DriveController? d, Setup.NextDrive? next = null, List<Setup.NextDrive>? queue = null)
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
        if (d?.MustChooseDrive == true)
        {
            state.Config.Channels = null; // its data channel did not match: show the drives to choose from
            state.Config.DriveId = "";
        }
        bool isNew = d is null;
        DialogResult result;
        Keys? wizardKeys = null;
        string? createdDriveId = null;
        bool restartForLanguage;
        List<Setup.NextDrive> after;
        using (_wizard = new Setup.SetupWizard(state, isNew, originalChannels, next, queue))
        {
            result = _wizard.ShowDialog();
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
                    $"הכונן \"{saved.Config.VolumeLabel}\" כבר נוצר בטלגרם, אבל ההגדרה לא הסתיימה.\n\nאם תשמור אותו, הוא יתחבר עם הגדרות ברירת המחדל (אפשר לשנות אותן ב\"הגדרות והתחברות…\").",
                    $"The drive \"{saved.Config.VolumeLabel}\" was already created in Telegram, but setup did not finish.\n\nIf you keep it, it connects with default settings (they can be changed in \"Settings and sign-in…\")."),
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
        await MountAsync(d);
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
        return true;
    }

    Task AddDriveAsync() => SetupAsync(null);

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
        if (_drives.Count == 0) { _ = AddDriveAsync(); return; }
        if (_pendingSetup is { } p) { _ = SetupAsync(p); return; }
        var mounted = _drives.FirstOrDefault(d => d.Session is not null);
        if (mounted is not null) OpenDrive(mounted);
        else Balloon("DriveGram", Summary(), ToolTipIcon.Info);
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
        if (st.PendingFiles > 0 || st.UploadsInFlight > 0)
            return L.T("מעלה: ", "uploading: ") + L.N(st.PendingFiles, "קובץ אחד", "{0} קבצים", "1 file", "{0} files") + $", {Ui.Size(st.StagedBytes)}" + wait;
        if (s.Sync.UnsentChanges is > 0 and var unsent)
            return L.N(unsent, "שינוי אחד ממתין לשליחה לטלגרם", "{0} שינויים ממתינים לשליחה לטלגרם", "1 change waiting to be sent to Telegram", "{0} changes waiting to be sent to Telegram") + wait;
        return L.T("הכל שמור בטלגרם ✓", "everything is saved in Telegram ✓");
    }

    static string ReadOnlyWhy(DriveSession s) => !s.Sync.ControlTrusted
        ? L.T("רשומת הכונן שונתה מחוץ ל־DriveGram, ולכן הוא לקריאה בלבד. בתפריט: \"אשר את רשומת הכונן…\".",
              "The drive's record was changed outside DriveGram, so it is read-only. In the menu: \"Confirm the drive's record…\".")
        : s.Sync.LeaseHolder is { } h
        ? L.T($"הכונן פתוח לכתיבה במחשב {L.Ltr(h)}. כאן אפשר רק לקרוא. כדי לכתוב כאן: בתפריט, \"בקש את הכונן מ־{L.Ltr(h)}…\".",
              $"The drive is open for writing on {h}. Here it can only be read. To write here: \"Ask {h} for the drive…\" in the menu.")
        : L.T("הכונן זמין כרגע לקריאה בלבד; DriveGram בודק אם אפשר לכתוב.", "The drive is read-only for now; DriveGram is checking whether it can write.");

    string Summary() => _drives.Count == 0
        ? L.T("אין עדיין כוננים", "no drives yet")
        : string.Join("\n", _drives.Select(d => _drives.Count > 1 ? $"{L.Ltr(d.Letter)} {StatusOf(d)}" : $"{d.Name}: {StatusOf(d)}"));

    void BuildMenu()
    {
        _menu.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _menu.Items.Clear();
        var title = new ToolStripMenuItem("DriveGram") { Enabled = false, Font = BoldFont };
        _menu.Items.Add(title);
        foreach (var d in _drives)
        {
            var account = d.State.Folder is not null ? L.T("תיקייה מקומית", "local folder") : L.Name(d.State.Config.AccountName ?? "");
            var item = new ToolStripMenuItem($"{d.Name}{(string.IsNullOrEmpty(account) ? "" : $" · {account}")} — {StatusOf(d)}")
            {
                Image = d.Busy || d.Starting ? _busyBitmap : _idleBitmap,
            };
            bool on = d.Session is not null && !d.Stopping;
            item.DropDownItems.Add(Item(L.T("פתח את הכונן", "Open the drive"), () => OpenDrive(d), on, bold: true));
            item.DropDownItems.Add(Item(L.T("סל המחזור…", "Recycle bin…"), () => ShowTrash(d), on));
            item.DropDownItems.Add(Item(L.T("ייצוא כל הקבצים לתיקייה…", "Export every file to a folder…"), () => Export(d), on));
            item.DropDownItems.Add(new ToolStripSeparator());
            if (d.Session is null)
                item.DropDownItems.Add(Item(L.T("חבר", "Connect"), async () => { if (d.NeedsSetup) await SetupAsync(d); else await MountAsync(d); },
                    !d.Starting));
            else item.DropDownItems.Add(Item(L.T("נתק", "Disconnect"), async () => await StopAsync(d), !d.Stopping));
            if (d.Session is { Sync.ControlTrusted: false } tampered)
                item.DropDownItems.Add(Item(L.T("אשר את רשומת הכונן…", "Confirm the drive's record…"), async () => await ConfirmRecordAsync(d, tampered), !d.Stopping, bold: true));
            if (d.Session is { Writable: false, Sync: { LeaseHolder: { } holder, NeedsUpdate: false } })
                item.DropDownItems.Add(Item(L.T($"בקש את הכונן מ־{L.Ltr(holder)}…", $"Ask {holder} for the drive…"), () => AskForDrive(d), !d.Stopping, bold: true));
            item.DropDownItems.Add(Item(L.T("הגדרות והתחברות…", "Settings and sign-in…"), async () => await SetupAsync(d), !d.Starting && !d.Stopping));
            item.DropDownItems.Add(Item(L.T("סיסמה ומפתח שחזור…", "Password and recovery key…"), () => ShowSecurity(d), on));
            item.DropDownItems.Add(Item(d.State.Config.NetworkDrive ? L.T("סוג הכונן: כונן רשת…", "Drive type: network drive…") : L.T("סוג הכונן: דיסק רגיל…", "Drive type: regular disk…"),
                async () => await ChangeDriveTypeAsync(d), !d.Starting && !d.Stopping));
            if (d.State.Folder is null && d.State.Config.Channels is not null)
                item.DropDownItems.Add(Item(L.T("גיבוי בחשבון נוסף…", "Backup on another account…"), () => ShowBackup(d)));
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(Item(L.T("הסר מהמחשב הזה…", "Remove from this PC…"), async () => await RemoveAsync(d), !d.Starting && !d.Stopping));
            // never offered while the drive's pairing is in doubt (it could name another drive's data)
            var delete = Item(L.T("מחק את הכונן לצמיתות…", "Delete the drive permanently…"), async () => await DeleteForeverAsync(d),
                !d.Starting && !d.Stopping && !d.MustChooseDrive);
            delete.ForeColor = Color.FromArgb(0xC6, 0x28, 0x28);
            item.DropDownItems.Add(delete);
            item.Click += (_, _) => { if (on) OpenDrive(d); };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(L.T("הוסף כונן נוסף…", "Add another drive…"), async () => await AddDriveAsync()));

        var autostart = new ToolStripMenuItem(L.T("הפעל עם וינדוס", "Start with Windows")) { Checked = ShellIntegration.AutostartEnabled };
        autostart.Click += (_, _) =>
        {
            try { ShellIntegration.SetAutostart(!ShellIntegration.AutostartEnabled); }
            catch (Exception ex) { Ui.Error(Setup.TelegramErrors.Text(ex)); }
        };
        _menu.Items.Add(autostart);

        var lang = new ToolStripMenuItem(L.T("שפה / Language", "Language / שפה"));
        foreach (var (key, label) in new[] { ("auto", L.T("לפי וינדוס", "Same as Windows")), ("he", "עברית"), ("en", "English") })
        {
            var li = new ToolStripMenuItem(label) { Checked = L.Preference == key };
            li.Click += (_, _) => { L.Set(key); UpdateIcon(); };
            lang.DropDownItems.Add(li);
        }
        _menu.Items.Add(lang);
        _menu.Items.Add(Item(L.T("פתח את תיקיית יומן הפעילות", "Open the log folder"),
            () => Process.Start(new ProcessStartInfo(Path.Combine(AppState.Root, "logs")) { UseShellExecute = true })));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(L.T("יציאה", "Exit"), async () => await QuitAsync(ask: true)));
    }

    static readonly Font BoldFont = new(Ui.Font, FontStyle.Bold);
    Bitmap? _idleBmp, _busyBmp;
    Bitmap _idleBitmap => _idleBmp ??= _idle.ToBitmap();
    Bitmap _busyBitmap => _busyBmp ??= _busy.ToBitmap();

    static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true, bool bold = false)
    {
        var i = new ToolStripMenuItem(text) { Enabled = enabled };
        if (bold) i.Font = BoldFont;
        i.Click += (_, _) => onClick();
        return i;
    }

    void UpdateIcon()
    {
        if (_quitting) return;
        bool busy = _drives.Any(d => d.Busy || d.Starting || d.Stopping);
        _icon.Icon = busy ? _busy : _idle;
        if (_drives.Any(d => d.Stopping)) return; // the stop progress owns the tooltip
        SetTooltip(_drives.Count == 1 ? $"{_drives[0].Letter} {StatusOf(_drives[0])}" : Summary().Replace("\n", " | "));
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
