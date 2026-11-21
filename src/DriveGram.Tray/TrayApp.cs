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
        SystemEvents.SessionEnding += (_, _) => { foreach (var d in _drives) d.Session?.StopForShutdown(); };

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEvent);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, DriveSession.QuitEventName);
        Listen(_activate, () => Activate());
        Listen(_quit, async () => await QuitAsync(ask: false));

        if (_drives.Count == 0) _ui.Post(async _ => await AddDriveAsync(), null);
        else foreach (var d in _drives) _ = MountAsync(d);
    }

    void Listen(EventWaitHandle ev, Action onSignal) => new Thread(() =>
    {
        try { while (ev.WaitOne()) _ui.Post(_ => onSignal(), null); }
        catch (ObjectDisposedException) { }
    }) { IsBackground = true }.Start();

    // ── mount / unmount ──────────────────────────────────────────────────────
    async Task MountAsync(DriveController d)
    {
        if (d.Session is not null || d.Starting || _quitting) return;
        d.RetryTimer?.Stop();
        d.Starting = true;
        d.Problem = null;
        d.NeedsSetup = false;
        UpdateIcon();
        try
        {
            var session = await Task.Run(() => DriveSession.StartAsync(d.State, ctrl => UnlockAsync(d, ctrl), null, takeOver: false));
            d.Session = session;
            d.RetryDelaySeconds = 15;
            session.StopRequested += () => _ui.Post(async _ => await StopAsync(d, ask: false), null);
            session.SignedOut += () => _ui.Post(async _ => await OnSignedOutAsync(d), null);
            session.Sync.WritableChanged += () => _ui.Post(_ => Balloon(
                d.Session?.Writable == true ? L.T($"{d.Letter} ניתן לכתיבה", $"{d.Letter} is writable") : L.T($"{d.Letter} לקריאה בלבד", $"{d.Letter} is read-only"),
                d.Session?.Writable == true
                    ? L.T("אפשר לשמור קבצים שוב.", "You can save files again.")
                    : L.T($"הכונן פתוח לכתיבה במחשב {d.Session?.Sync.LeaseHolder}.", $"The drive is open for writing on {d.Session?.Sync.LeaseHolder}."),
                ToolTipIcon.Warning), null);

            string text = session.Writable
                ? L.T($"הכונן זמין ב־{session.MountPoint}", $"The drive is available as {session.MountPoint}")
                : L.T($"{session.MountPoint} זמין לקריאה בלבד: הוא פתוח לכתיבה במחשב {session.Sync.LeaseHolder}.",
                      $"{session.MountPoint} is read-only: it is open for writing on {session.Sync.LeaseHolder}.");
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
        catch (AlreadyMountedException) { d.Problem = L.T("פתוח כבר בחלון אחר", "already open in another window"); }
        catch (WrongPasswordException) { d.Problem = L.T("סיסמה שגויה", "wrong password"); }
        catch (Exception ex)
        {
            // most likely no internet yet (e.g. right after boot): retry with backoff
            Log.Warn($"tray: mounting {d.Name} failed: {ex.Message}");
            d.Problem = L.T($"ממתין לחיבור… ({Short(ex)})", $"waiting to connect… ({Short(ex)})");
            ScheduleRetry(d);
        }
        finally
        {
            d.Starting = false;
            UpdateIcon();
        }
    }

    static string Short(Exception ex) => ex.Message.Length > 60 ? ex.Message[..60] + "…" : ex.Message;

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
        if (d.Stopping) return false;
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
            catch (WrongPasswordException) { d.State.ForgetMaster(); }
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
                var keys = Keys.Unlock(ctrl.Vault, input.Value.pw);
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
        if (d?.Session is not null)
        {
            if (Ui.Ask(L.T($"כדי לשנות את ההגדרות של {d.Name} צריך לנתק אותו לרגע. לנתק?",
                    $"To change the settings of {d.Name} it must be disconnected for a moment. Disconnect?")) != DialogResult.Yes) return;
            if (!await StopAsync(d)) return;
        }
        var state = d?.State ?? AppState.CreateNew();
        bool isNew = d is null;
        DialogResult result;
        using (_wizard = new Setup.SetupWizard(state, isNew))
        {
            result = _wizard.ShowDialog();
            _wizard = null;
        }
        if (result != DialogResult.OK)
        {
            if (isNew) state.Delete();
            UpdateIcon();
            return;
        }
        if (_pendingSetup == d) _pendingSetup = null;
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
    }

    bool Replace(DriveController old, DriveController fresh)
    {
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
        await ForgetLocallyAsync(d);
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

    /// <summary>
    /// Permanently delete a drive — its Telegram channels and everything on this PC. Shows what will be
    /// lost and requires ticking "I understand" plus typing the drive's name. Drives this account does
    /// not own (a backup admin) can only be removed from this PC.
    /// </summary>
    async Task DeleteForeverAsync(DriveController d)
    {
        DeletionPreview p;
        try
        {
            SetTooltip(L.T($"בודק את {d.Name}…", $"Checking {d.Name}…"));
            p = await Task.Run(() => DriveDeletion.PreviewAsync(d.State, d.Session?.Db));
        }
        catch (Exception ex)
        {
            Ui.Error(L.T($"לא ניתן לבדוק את הכונן כרגע: {ex.Message}", $"Cannot check the drive right now: {ex.Message}"));
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
            if (await StopAsync(d, ask: false)) await ForgetLocallyAsync(d);
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
            Ui.Error(L.T($"המחיקה מטלגרם נכשלה: {ex.Message}\nהכונן נשאר ברשימה, אפשר לנסות שוב.",
                         $"Deleting from Telegram failed: {ex.Message}\nThe drive stays in the list; you can try again."));
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
        if (!s.Writable) return L.T($"קריאה בלבד (פתוח לכתיבה ב־{s.Sync.LeaseHolder})", $"read-only (open for writing on {s.Sync.LeaseHolder})");
        if (st.PendingFiles > 0 || st.UploadsInFlight > 0)
            return L.T($"מעלה: {st.PendingFiles} קבצים, {Ui.Size(st.StagedBytes)}", $"uploading: {st.PendingFiles} files, {Ui.Size(st.StagedBytes)}");
        return L.T("הכל שמור בטלגרם ✓", "everything is saved in Telegram ✓");
    }

    string Summary() => _drives.Count == 0
        ? L.T("אין עדיין כוננים", "no drives yet")
        : string.Join("\n", _drives.Select(d => $"{d.Name}: {StatusOf(d)}"));

    void BuildMenu()
    {
        _menu.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _menu.Items.Clear();
        var title = new ToolStripMenuItem("DriveGram") { Enabled = false, Font = new Font(Ui.Font, FontStyle.Bold) };
        _menu.Items.Add(title);
        foreach (var d in _drives)
        {
            var account = d.State.Folder is not null ? L.T("תיקייה מקומית", "local folder") : d.State.Config.AccountName;
            var item = new ToolStripMenuItem($"{d.Name}{(string.IsNullOrEmpty(account) ? "" : $" · {account}")} — {StatusOf(d)}")
            {
                Image = (d.Busy || d.Starting ? _busy : _idle).ToBitmap(),
            };
            bool on = d.Session is not null && !d.Stopping;
            item.DropDownItems.Add(Item(L.T("פתח את הכונן", "Open the drive"), () => OpenDrive(d), on, bold: true));
            item.DropDownItems.Add(Item(L.T("סל המחזור…", "Recycle bin…"), () => ShowTrash(d), on));
            item.DropDownItems.Add(new ToolStripSeparator());
            if (d.Session is null)
                item.DropDownItems.Add(Item(L.T("חבר", "Connect"), async () => { if (d.NeedsSetup) await SetupAsync(d); else await MountAsync(d); },
                    !d.Starting));
            else item.DropDownItems.Add(Item(L.T("נתק", "Disconnect"), async () => await StopAsync(d), !d.Stopping));
            item.DropDownItems.Add(Item(L.T("הגדרות והתחברות…", "Settings and sign-in…"), async () => await SetupAsync(d), !d.Starting && !d.Stopping));
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
        _menu.Items.Add(Item(L.T("הוסף כונן (חשבון טלגרם נוסף)…", "Add a drive (another Telegram account)…"), async () => await AddDriveAsync()));

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

    static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true, bool bold = false)
    {
        var i = new ToolStripMenuItem(text) { Enabled = enabled };
        if (bold) i.Font = new Font(Ui.Font, FontStyle.Bold);
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
