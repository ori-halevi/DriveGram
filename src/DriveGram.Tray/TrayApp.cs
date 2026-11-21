using System.Diagnostics;
using DriveGram.Crypto;
using DriveGram.Host;
using DriveGram.Sync;
using Microsoft.Win32;
using Keys = DriveGram.Crypto.Keys;

namespace DriveGram.Tray;

/// <summary>The notification-area app: mounts the drive at sign-in and shows what it is doing.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly AppState _state;
    readonly SynchronizationContext _ui;
    readonly NotifyIcon _icon;
    readonly Icon _idle, _busy;
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _open = new("פתח את הכונן");
    readonly ToolStripMenuItem _trash = new("סל המחזור...");
    readonly ToolStripMenuItem _toggle = new("חבר");
    readonly ToolStripMenuItem _autostart = new("הפעל עם וינדוס") { CheckOnClick = true };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    DriveSession? _session;
    TrashForm? _trashForm;
    bool _starting, _stopping;
    string? _lastError;

    public TrayApp(AppState state)
    {
        _state = state;
        _ui = SynchronizationContext.Current!;
        var dir = AppContext.BaseDirectory;
        _idle = new Icon(Path.Combine(dir, "drivegram.ico"), SystemInformation.SmallIconSize);
        _busy = new Icon(Path.Combine(dir, "drivegram-busy.ico"), SystemInformation.SmallIconSize);

        var logs = new ToolStripMenuItem("פתח את תיקיית היומן");
        var exit = new ToolStripMenuItem("יציאה");
        _open.Font = new Font(_open.Font, FontStyle.Bold);
        _open.Click += (_, _) => OpenDrive();
        _trash.Click += (_, _) => ShowTrash();
        _toggle.Click += async (_, _) => { if (_session is null) await MountAsync(); else await StopAsync(exitApp: false); };
        _autostart.Checked = ShellIntegration.AutostartEnabled;
        _autostart.Click += (_, _) => ToggleAutostart();
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo(_state.LogDir) { UseShellExecute = true });
        exit.Click += async (_, _) => await StopAsync(exitApp: true);

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, Font = Ui.Font };
        menu.Items.AddRange([_status, new ToolStripSeparator(), _open, _trash, new ToolStripSeparator(),
            _toggle, _autostart, logs, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) => UpdateUi();

        _icon = new NotifyIcon { Icon = _idle, Text = "DriveGram", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => OpenDrive();
        _timer.Tick += (_, _) => UpdateUi();
        _timer.Start();
        SystemEvents.SessionEnding += (_, _) => _session?.StopForShutdown();

        _ = MountAsync();
    }

    // ── mount / unmount ──────────────────────────────────────────────────────
    async Task MountAsync()
    {
        if (_session is not null || _starting) return;
        _starting = true;
        _lastError = null;
        UpdateUi();
        try
        {
            _session = await Task.Run(() => DriveSession.StartAsync(_state, ask: null, UnlockAsync, null, takeOver: false));
            _session.StopRequested += () => _ui.Post(async _ => await StopAsync(exitApp: true, ask: false), null);
            _session.Sync.WritableChanged += () => _ui.Post(_ => Balloon(
                _session?.Writable == true ? "הכונן ניתן לכתיבה" : "הכונן במצב קריאה בלבד",
                _session?.Writable == true ? "אפשר לשמור קבצים שוב." : $"הכונן פתוח לכתיבה במחשב {_session?.Sync.LeaseHolder}.",
                ToolTipIcon.Warning), null);
            Balloon("DriveGram מחובר", _session.Writable
                ? $"הכונן זמין ב־{_session.MountPoint}"
                : $"הכונן זמין ב־{_session.MountPoint} לקריאה בלבד: הוא פתוח לכתיבה במחשב {_session.Sync.LeaseHolder}.");
        }
        catch (OperationCanceledException) { _lastError = "לא הוזנה סיסמה"; }
        catch (NeedsLoginException) { Fail("צריך להתחבר לטלגרם", "הרץ פעם אחת: drivegram login"); }
        catch (AlreadyMountedException)
        {
            Balloon("DriveGram כבר פועל", "הכונן כבר מחובר במחשב הזה (בחלון אחר).", ToolTipIcon.Info);
            await Task.Delay(4000);
            Quit();
        }
        catch (Exception ex)
        {
            Log.Error("tray: mount failed", ex);
            Fail("החיבור נכשל", ex.Message);
        }
        finally
        {
            _starting = false;
            UpdateUi();
        }
    }

    void Fail(string title, string text)
    {
        _lastError = title;
        Balloon(title, text, ToolTipIcon.Error);
    }

    /// <summary>Unmount (and optionally exit). With pending uploads, ask whether to wait for them.</summary>
    async Task StopAsync(bool exitApp, bool ask = true)
    {
        if (_stopping) return;
        if (_session is null) { if (exitApp) Quit(); return; }
        bool wait = true;
        int pending = _session.Status.PendingFiles;
        if (pending > 0 && ask)
        {
            var answer = MessageBox.Show(
                $"{pending} קבצים עדיין עולים לטלגרם.\n\nכן – לחכות שיסיימו (מומלץ)\nלא – לנתק עכשיו; ההעלאה תמשיך בפעם הבאה שהכונן יעלה במחשב הזה",
                "DriveGram", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            if (answer == DialogResult.Cancel) return;
            wait = answer == DialogResult.Yes;
        }
        _stopping = true;
        _trashForm?.Close();
        UpdateUi();
        var session = _session;
        try
        {
            await Task.Run(() => session.StopAsync(wait, st => _ui.Post(_ => SetTooltip($"מסיים העלאות: נותרו {st.PendingFiles} קבצים"), null)));
            await session.DisposeAsync();
        }
        catch (Exception ex) { Log.Error("tray: stop failed", ex); }
        _session = null;
        _stopping = false;
        UpdateUi();
        if (exitApp) Quit();
    }

    void Quit()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    // ── password ─────────────────────────────────────────────────────────────
    /// <summary>Runs on a worker thread; shows the password dialog on the UI thread when needed.</summary>
    Task<Keys> UnlockAsync(ControlState ctrl)
    {
        if (_state.RememberedMaster() is { } master)
        {
            try { return Task.FromResult(Keys.FromMaster(ctrl.Vault, master)); }
            catch (WrongPasswordException) { _state.ForgetMaster(); }
        }
        string? error = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (string pw, bool remember)? input = null;
            _ui.Send(_ =>
            {
                using var f = new PasswordForm(error);
                if (f.ShowDialog() == DialogResult.OK) input = (f.Password, f.Remember);
            }, null);
            if (input is null) throw new OperationCanceledException();
            try
            {
                var keys = Keys.Unlock(ctrl.Vault, input.Value.pw);
                if (input.Value.remember) _state.RememberMaster(keys.ExportMaster());
                return Task.FromResult(keys);
            }
            catch (WrongPasswordException) { error = "סיסמה שגויה, נסה שוב."; }
        }
        throw new WrongPasswordException();
    }

    // ── actions ──────────────────────────────────────────────────────────────
    void OpenDrive()
    {
        if (_session is null) return;
        Process.Start(new ProcessStartInfo(_session.MountPoint + "\\") { UseShellExecute = true });
    }

    void ShowTrash()
    {
        if (_session is null) return;
        if (_trashForm is { IsDisposed: false }) { _trashForm.Activate(); return; }
        _trashForm = new TrashForm(_session);
        _trashForm.Show();
    }

    void ToggleAutostart()
    {
        try
        {
            ShellIntegration.SetAutostart(_autostart.Checked);
            if (_autostart.Checked && _state.RememberedMaster() is null)
                Balloon("הפעלה עם וינדוס", "הסיסמה לא שמורה במחשב, אז היא תתבקש בכל כניסה לוינדוס.", ToolTipIcon.Info);
        }
        catch (Exception ex) { Ui.Error(ex.Message); }
        _autostart.Checked = ShellIntegration.AutostartEnabled;
    }

    // ── status ───────────────────────────────────────────────────────────────
    void UpdateUi()
    {
        string status;
        bool busy = false;
        if (_starting) status = "מתחבר...";
        else if (_stopping) status = "מתנתק...";
        else if (_session is null) status = _lastError is null ? "לא מחובר" : $"לא מחובר: {_lastError}";
        else
        {
            var s = _session.Status;
            busy = s.PendingFiles > 0 || s.UploadsInFlight > 0;
            if (!_session.Writable) status = $"קריאה בלבד (פתוח לכתיבה ב־{_session.Sync.LeaseHolder})";
            else if (busy) status = $"מעלה לטלגרם: {s.PendingFiles} קבצים, {Ui.Size(s.StagedBytes)} ממתינים";
            else status = "הכל שמור בטלגרם ✓";
        }
        _status.Text = status;
        _open.Text = _session is null ? "פתח את הכונן" : $"פתח את הכונן ({_session.MountPoint})";
        _open.Enabled = _trash.Enabled = _session is not null && !_stopping;
        _toggle.Text = _session is null ? "חבר את הכונן" : "נתק את הכונן";
        _toggle.Enabled = !_starting && !_stopping;
        _icon.Icon = busy || _starting || _stopping ? _busy : _idle;
        if (!_stopping) SetTooltip(status);
    }

    void SetTooltip(string text)
    {
        var t = "DriveGram · " + text;
        _icon.Text = t.Length > 127 ? t[..127] : t;
    }

    void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.None)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }
}
