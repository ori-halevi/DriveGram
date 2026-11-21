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
    public ExportForm? ExportWindow;

    public DriveController(AppState state) => State = state;

    public string Letter => Session?.MountPoint ?? State.Config.MountPoint;
    public string Name => $"{Letter} {State.Config.VolumeLabel}";
    public bool Busy => Session is { } s && (s.Status.PendingFiles > 0 || s.Status.UploadsInFlight > 0);
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
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _ui.Post(_ => OnResume(), null); };

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEvent);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, DriveSession.QuitEventName);
        Listen(_activate, () => Activate());
        Listen(_quit, async () => await QuitAsync(ask: false));

        if (_drives.Count == 0) _ui.Post(async _ => await AddDriveAsync(), null);
        else foreach (var d in _drives) _ = MountAsync(d);
    }

    /// <summary>Windows is ending the session: unmount fast. If the user then cancels the shutdown (an app
    /// asked "save changes?"), the process lives on: bring the drives back a minute later.</summary>
    void OnSessionEnding()
    {
        foreach (var d in _drives)
        {
            if (d.Session is not { } s) continue;
            s.StopForShutdown();
            d.Session = null;
            d.Problem = L.T("נותק (כיבוי של וינדוס)", "disconnected (Windows shutdown)");
            _ui.Post(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                try { await s.DisposeAsync(); } catch (Exception ex) { Log.Warn($"after shutdown stop: {ex.Message}"); }
                if (!_quitting && d.Session is null) await MountAsync(d);
            }, null);
        }
        UpdateIcon();
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
            d.RetryDelaySeconds = 15;
            session.StopRequested += () => _ui.Post(async _ => await StopAsync(d, ask: false), null);
            session.SignedOut += () => _ui.Post(async _ => await OnSignedOutAsync(d), null);
            if (session.Sync.NeedsUpdate)
                Balloon(L.T("צריך לעדכן את DriveGram", "Update DriveGram"),
                    L.T($"{session.MountPoint} נכתב בגרסה חדשה יותר. הוא פתוח לקריאה בלבד עד שתתקין את הגרסה החדשה.",
                        $"{session.MountPoint} was written by a newer version. It is read-only until you install it."), ToolTipIcon.Warning);
            bool? shown = session.Writable;
            session.Sync.WritableChanged += () => _ui.Post(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (d.Session is not { } s || s.Writable == shown) return; // flipped back meanwhile
                shown = s.Writable;
                Balloon(s.Writable ? L.T($"{d.Letter} ניתן לכתיבה", $"{d.Letter} is writable") : L.T($"{d.Letter} לקריאה בלבד", $"{d.Letter} is read-only"),
                    s.Writable ? L.T("אפשר לשמור קבצים שוב.", "You can save files again.") : ReadOnlyWhy(s),
                    ToolTipIcon.Warning);
            }, null);

            string text = session.Writable
                ? L.T($"הכונן זמין ב־{session.MountPoint}", $"The drive is available as {session.MountPoint}")
                : $"{session.MountPoint}: {ReadOnlyWhy(session)}";
            if (session.LetterChanged is { } moved)
                text += L.T($"\nהאות הקודמת הייתה תפוסה ({moved}).", $"\nThe usual letter was taken ({moved}).");
            Balloon(L.T("DriveGram מחובר", "DriveGram connected"), text);
        }
        catch (OperationCanceledException) { d.Problem = L.T("לא הוזנה סיסמה", "no password entered"); }
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
            d.Problem = L.T("הערוצים של הכונן נמחקו מטלגרם", "the drive's channels were deleted from Telegram");
            Balloon(L.T("הכונן לא נמצא בטלגרם", "Drive not found in Telegram"),
                L.T($"הערוצים של {d.Name} נמחקו מהחשבון. פתח את 'הגדרות והתחברות' כדי להחליט מה לעשות.",
                    $"The channels of {d.Name} were deleted from the account. Open 'Settings and sign-in' to decide what to do."),
                ToolTipIcon.Error);
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
            d.NeedsSetup = true;
            d.Problem = L.T("צריך לבחור את הכונן מחדש", "the drive must be chosen again");
            OfferSetup(d, $"{d.Name}: {ex.Message}");
        }
        catch (WrongPasswordException) { d.Problem = L.T("סיסמה שגויה", "wrong password"); }
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
        Balloon(L.T("נדרשת הגדרה", "Setup needed"), text + L.T(" לחץ כאן כדי להמשיך.", " Click here to continue."), ToolTipIcon.Info);
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
            L.T($"{d.Name} נותק כי החיבור בוטל (למשל מ'סיום כל ההפעלות' בטלפון). קבצים שעוד לא עלו שמורים במחשב ויעלו אחרי התחברות מחדש.",
                $"{d.Name} was disconnected because its sign-in was revoked (e.g. 'terminate all sessions' on the phone). Files not yet uploaded are kept on this PC and upload after you sign in again."),
            ToolTipIcon.Warning);
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
        d.RetryTimer?.Stop();
        if (d.Session is not { } session) return true;
        int pending = session.Status.PendingFiles;
        if (pending > 0 && ask && waitForUploads)
        {
            var answer = Ui.Ask(L.T(
                    $"{pending} קבצים ב־{d.Name} עדיין עולים לטלגרם.\n\nכן – לחכות שיסיימו (מומלץ)\nלא – לנתק עכשיו; ההעלאה תמשיך בפעם הבאה שהכונן יעלה במחשב הזה",
                    $"{pending} files on {d.Name} are still uploading to Telegram.\n\nYes – wait for them to finish (recommended)\nNo – disconnect now; uploading continues next time the drive starts on this PC"),
                MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel) return false;
            waitForUploads = answer == DialogResult.Yes;
        }
        d.Stopping = true;
        d.Trash?.Close();
        d.Security?.Close();
        d.ExportWindow?.Close();
        UpdateIcon();
        try
        {
            await Task.Run(() => session.StopAsync(waitForUploads, st => _ui.Post(_ =>
                SetTooltip(L.T($"מסיים העלאות ב־{d.Letter}: נותרו {st.PendingFiles} קבצים", $"Finishing uploads on {d.Letter}: {st.PendingFiles} files left")), null)));
            await session.DisposeAsync();
        }
        catch (Exception ex) { Log.Error($"tray: stopping {d.Name} failed", ex); }
        d.Session = null;
        d.Stopping = false;
        UpdateIcon();
        return true;
    }

    async Task QuitAsync(bool ask)
    {
        if (_quitting) return;
        if (ask)
        {
            int pending = _drives.Sum(d => d.Session?.Status.PendingFiles ?? 0);
            if (pending > 0)
            {
                var answer = Ui.Ask(L.T(
                        $"{pending} קבצים עדיין עולים לטלגרם.\n\nכן – לחכות שיסיימו ואז לצאת (מומלץ)\nלא – לצאת עכשיו; ההעלאה תמשיך בהפעלה הבאה",
                        $"{pending} files are still uploading to Telegram.\n\nYes – wait for them, then exit (recommended)\nNo – exit now; uploading continues next time"),
                    MessageBoxButtons.YesNoCancel);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.No)
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
        if (d.State.RememberedMaster() is { } master)
        {
            try { return Task.FromResult(Keys.FromMaster(ctrl.Vault, master)); }
            catch (WrongPasswordException) { Log.Warn($"{d.Name}: the remembered key does not match the drive; asking for the password (the key is kept)"); }
        }
        string? error = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (string pw, bool remember)? input = null;
            _ui.Send(_ =>
            {
                using var f = new PasswordForm(d.Name, error);
                if (f.ShowDialog() == DialogResult.OK) input = (f.Password, f.Remember);
            }, null);
            if (input is null) throw new OperationCanceledException();
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
    /// <summary>Open the wizard for a drive (or a new one); mount when it finishes.</summary>
    async Task SetupAsync(DriveController? d)
    {
        if (_wizard is { IsDisposed: false }) { _wizard.Activate(); return; }
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
        bool isNew = d is null;
        DialogResult result;
        using (_wizard = new Setup.SetupWizard(state, isNew))
        {
            result = _wizard.ShowDialog();
            _wizard = null;
        }
        // The drive page saves a new binding (other channels / account) as soon as it is chosen, so a
        // cancel AFTER that point cannot restore the old one: the profile on disk is the truth.
        bool rebound = !isNew && Binding(AppState.Open(state.Id)) != Binding(d!.State);
        if (result != DialogResult.OK && !rebound)
        {
            if (isNew) state.Delete();
            else if (wasMounted && d is { Session: null }) await MountAsync(d);
            else if (wasRetrying && d is { Session: null }) ScheduleRetry(d);
            UpdateIcon();
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
        await MountAsync(d);
        if (!string.IsNullOrEmpty(account))
            foreach (var other in _drives.Where(o => o != d && o.NeedsSetup && o.Session is null && o.State.Config.AccountKey == account).ToList())
            {
                other.NeedsSetup = false; // same account, signed in again just now
                if (_pendingSetup == other) _pendingSetup = null;
                _ = MountAsync(other);
            }
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

    async Task RemoveAsync(DriveController d)
    {
        if (Ui.Ask(L.T(
                $"להסיר את {d.Name} מהמחשב הזה?\n\nהקבצים נשארים בטלגרם, ואפשר לחבר את הכונן שוב בכל עת (עם הסיסמה שלו).",
                $"Remove {d.Name} from this PC?\n\nThe files stay in Telegram, and you can connect the drive again at any time (with its password)."),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!await StopAsync(d)) return;
        if (!ConfirmLosingLocalChanges(d)) return;
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
        return Ui.Ask(L.T(
                $"⚠  ב־{d.Name} יש {pending} קבצים שעוד לא עלו לטלגרם ו־{unsent} שינויים שעוד לא נשלחו. הם קיימים רק במחשב הזה.\n\n" +
                "אם תסיר את הכונן עכשיו, הם יימחקו לצמיתות. כדי לשמור אותם: חבר את הכונן (אם צריך, התחבר מחדש לטלגרם) וחכה שיעלו.\n\nלהסיר בכל זאת?",
                $"⚠  {d.Name} has {pending} files not yet uploaded to Telegram and {unsent} changes not yet sent. They exist only on this PC.\n\n" +
                "Removing the drive now deletes them for good. To keep them, connect the drive (sign in again if needed) and let them upload.\n\nRemove anyway?"),
            icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>Everything this PC keeps for the drive: profile, icon, and the account's sign-in if unused now.</summary>
    async Task ForgetLocallyAsync(DriveController d)
    {
        if (d.Session is not null) return;
        try { ShellIntegration.ClearDriveIcon(d.Letter); } catch { }
        var accountKey = d.State.Config.AccountKey;
        d.State.Delete();
        _drives.Remove(d);
        if (_pendingSetup == d) _pendingSetup = null;
        // the account's last drive on this PC: sign it out too, so no stray "device" stays behind
        if (!string.IsNullOrEmpty(accountKey)) await TelegramAccounts.SignOutIfUnusedAsync(accountKey);
        UpdateIcon();
    }

    bool _deleting;

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
            if (Ui.Ask(L.T($"האחסון של {d.Name} כבר לא קיים (הערוצים נמחקו מטלגרם). להסיר את הכונן מהמחשב הזה?",
                    $"The storage of {d.Name} no longer exists (its channels were deleted from Telegram). Remove the drive from this PC?"),
                    icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (await StopAsync(d, ask: false, waitForUploads: false)) await ForgetLocallyAsync(d);
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
        if (s.Sync.NeedsUpdate) return L.T("קריאה בלבד: צריך לעדכן את DriveGram", "read-only: DriveGram needs an update");
        if (s.Sync.HeldBackChanges is > 0 and var held)
            return L.T($"⚠ {held} שינויים מוחזקים: הכונן נפתח לכתיבה ב־{s.Sync.LeaseHolder}", $"⚠ {held} changes held back: the drive was opened for writing on {s.Sync.LeaseHolder}");
        if (!s.Writable) return s.Sync.LeaseHolder is { } h
            ? L.T($"קריאה בלבד (פתוח לכתיבה ב־{h})", $"read-only (open for writing on {h})")
            : L.T("קריאה בלבד כרגע (בודק…)", "read-only for now (checking…)");
        var wait = s.WaitingUntil is { } until ? L.T($" · טלגרם ביקש להמתין עד {until:HH:mm}", $" · Telegram asked to wait until {until:HH:mm}") : "";
        if (st.PendingFiles > 0 || st.UploadsInFlight > 0)
            return L.T($"מעלה: {st.PendingFiles} קבצים, {Ui.Size(st.StagedBytes)}", $"uploading: {st.PendingFiles} files, {Ui.Size(st.StagedBytes)}") + wait;
        if (s.Sync.UnsentChanges is > 0 and var unsent)
            return L.T($"{unsent} שינויים ממתינים לשליחה לטלגרם", $"{unsent} changes waiting to be sent to Telegram") + wait;
        return L.T("הכל שמור בטלגרם ✓", "everything is saved in Telegram ✓");
    }

    static string ReadOnlyWhy(DriveSession s) => s.Sync.LeaseHolder is { } h
        ? L.T($"הכונן פתוח לכתיבה במחשב {h}. כאן אפשר רק לקרוא.", $"The drive is open for writing on {h}. Here it can only be read.")
        : L.T("הכונן זמין כרגע לקריאה בלבד; DriveGram בודק אם אפשר לכתוב.", "The drive is read-only for now; DriveGram is checking whether it can write.");

    string Summary() => _drives.Count == 0
        ? L.T("אין עדיין כוננים", "no drives yet")
        : string.Join("\n", _drives.Select(d => $"{d.Name}: {StatusOf(d)}"));

    void BuildMenu()
    {
        _menu.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _menu.Items.Clear();
        var title = new ToolStripMenuItem("DriveGram") { Enabled = false, Font = BoldFont };
        _menu.Items.Add(title);
        foreach (var d in _drives)
        {
            var account = d.State.Folder is not null ? L.T("תיקייה מקומית", "local folder") : d.State.Config.AccountName;
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
            item.DropDownItems.Add(Item(L.T("הגדרות והתחברות…", "Settings and sign-in…"), async () => await SetupAsync(d), !d.Starting && !d.Stopping));
            item.DropDownItems.Add(Item(L.T("סיסמה ומפתח שחזור…", "Password and recovery key…"), () => ShowSecurity(d), on));
            if (d.State.Folder is null && d.State.Config.Channels is not null)
                item.DropDownItems.Add(Item(L.T("גיבוי בחשבון נוסף…", "Backup on another account…"), () => ShowBackup(d)));
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(Item(L.T("הסר מהמחשב הזה…", "Remove from this PC…"), async () => await RemoveAsync(d), !d.Starting && !d.Stopping));
            var delete = Item(L.T("מחק את הכונן לצמיתות…", "Delete the drive permanently…"), async () => await DeleteForeverAsync(d), !d.Starting && !d.Stopping);
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
            catch (Exception ex) { Ui.Error(ex.Message); }
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
        _menu.Items.Add(Item(L.T("פתח את תיקיית היומן", "Open the log folder"),
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

    void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.None)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
        _icon.BalloonTipClicked -= OnBalloonClicked;
        _icon.BalloonTipClicked += OnBalloonClicked;
    }

    void OnBalloonClicked(object? sender, EventArgs e) => Activate();
}
