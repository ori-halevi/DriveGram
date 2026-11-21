using DriveGram.Crypto;
using DriveGram.Host;
using DriveGram.Tray.Setup;
using Keys = DriveGram.Crypto.Keys;

namespace DriveGram.Tray;

/// <summary>
/// The drive's password and recovery key. A recovery key opens the drive like the password does
/// (type it wherever the password is asked); it is shown ONCE. Changing the password re-wraps the same
/// master key, so no file is re-uploaded and PCs that remember the key keep working.
/// </summary>
sealed class SecurityForm : Form
{
    readonly DriveSession _session;
    readonly Label _recoveryState = Theme.Label("", Theme.Body, Theme.Text, 640);
    readonly Button _makeKey = Theme.Primary(L.T("צור מפתח שחזור", "Create a recovery key"));
    readonly FlowLayoutPanel _keyPanel = Theme.Column();
    readonly TextBox _keyBox = new() { ReadOnly = true, Font = new Font("Consolas", 14f), Width = 640, RightToLeft = RightToLeft.No, BorderStyle = BorderStyle.FixedSingle };
    readonly CheckBox _keySaved = new() { AutoSize = true, Text = L.T("שמרתי את מפתח השחזור במקום בטוח (לא רק במחשב הזה)", "I saved the recovery key somewhere safe (not only on this PC)") };
    readonly TextBox _current = Theme.Input(300, password: true);
    readonly TextBox _new1 = Theme.Input(300, password: true);
    readonly TextBox _new2 = Theme.Input(300, password: true);
    readonly Button _change = Theme.Primary(L.T("החלף סיסמה", "Change password"));
    readonly Label _changeResult = Theme.Label("", Theme.Small, Theme.Muted, 640);
    readonly Button _close = Theme.Secondary(L.T("סגור", "Close"));

    public SecurityForm(DriveSession session)
    {
        _session = session;
        Ui.Prepare(this);
        Text = L.T($"DriveGram – סיסמה ומפתח שחזור ({session.MountPoint})", $"DriveGram – password and recovery key ({session.MountPoint})");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 720);
        BackColor = Color.White;

        var col = Theme.Column();
        col.Padding = new Padding(24, 18, 24, 18);
        col.AutoSize = false;
        col.AutoScroll = true;
        col.Dock = DockStyle.Fill;

        Heading(col, L.T("מפתח שחזור", "Recovery key"));
        col.Controls.Add(Theme.Label(L.T(
            "אם תשכח את הסיסמה, מפתח השחזור פותח את הכונן במקומה: מקלידים אותו בכל מקום שמבקשים סיסמה. בלי סיסמה ובלי מפתח שחזור, אף אחד לא יוכל לפתוח את הכונן לעולם.",
            "If you forget the password, the recovery key opens the drive instead: type it wherever the password is asked. Without the password or a recovery key, nobody can ever open the drive."),
            Theme.Body, Theme.Text, 680));
        col.Controls.Add(_recoveryState);
        col.Controls.Add(_makeKey);
        _keyPanel.Visible = false;
        var keyButtons = Theme.Row();
        var copy = Theme.Secondary(L.T("העתק", "Copy"));
        copy.Click += (_, _) => Ui.CopySecret(_keyBox.Text);
        var save = Theme.Secondary(L.T("שמור לקובץ…", "Save to a file…"));
        save.Click += (_, _) => SaveKey();
        keyButtons.Controls.AddRange([copy, save]);
        _keyPanel.Controls.AddRange([
            Theme.Label(L.T("⚠  זו הפעם היחידה שהמפתח מוצג. שמור אותו עכשיו: הדפס, רשום, או שמור במנהל סיסמאות.",
                "⚠  This is the only time the key is shown. Save it now: print it, write it down, or keep it in a password manager."),
                Theme.Body, Theme.Error, 680),
            _keyBox, keyButtons, _keySaved]);
        col.Controls.Add(_keyPanel);
        _makeKey.Click += async (_, _) => await MakeKeyAsync();
        _keySaved.CheckedChanged += (_, _) => _close.Enabled = true;

        Heading(col, L.T("החלפת סיסמה", "Change the password"));
        col.Controls.Add(Theme.Label(L.T(
            "הקבצים לא מועלים מחדש, וזה לוקח שניות. מחשבים שזוכרים את הסיסמה ימשיכו לעבוד; מחשב חדש יצטרך את הסיסמה החדשה. חשוב לדעת: זה לא נועל בחוץ מישהו שכבר ידע את הסיסמה הקודמת וכבר פתח את הכונן. כל המחשבים צריכים DriveGram בגרסה 1.0 ומעלה (לפני השינוי, עדכן את כולם).",
            "No file is re-uploaded; it takes seconds. PCs that remember the password keep working; a new PC will need the new one. Note: this does not lock out someone who already knew the old password and opened the drive. Every PC needs DriveGram 1.0 or later (update them all first)."),
            Theme.Small, Theme.Muted, 680));
        col.Controls.Add(Theme.Label(L.T("הסיסמה הנוכחית (או מפתח השחזור):", "Current password (or the recovery key):"), Theme.Small, Theme.Muted));
        col.Controls.Add(_current);
        col.Controls.Add(Theme.Label(L.T("סיסמה חדשה (לפחות 10 תווים):", "New password (at least 10 characters):"), Theme.Small, Theme.Muted));
        col.Controls.Add(_new1);
        col.Controls.Add(Theme.Label(L.T("הקלד שוב:", "Repeat it:"), Theme.Small, Theme.Muted));
        col.Controls.Add(_new2);
        col.Controls.Add(_change);
        col.Controls.Add(_changeResult);
        _change.Click += async (_, _) => await ChangeAsync();

        _close.Click += (_, _) => Close();
        _close.Margin = new Padding(0, 18, 0, 0);
        col.Controls.Add(_close);
        Controls.Add(col);
        FormClosing += (_, e) =>
        {
            if (_keyPanel.Visible && !_keySaved.Checked && e.CloseReason != CloseReason.WindowsShutDown &&
                Ui.Ask(L.T("לא סימנת ששמרת את מפתח השחזור. הוא לא יוצג שוב. לסגור בכל זאת?",
                    "You did not confirm you saved the recovery key. It will not be shown again. Close anyway?"),
                    icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                e.Cancel = true;
        };
        Shown += async (_, _) => await RefreshAsync();
    }

    static void Heading(FlowLayoutPanel col, string text)
    {
        var l = Theme.Label(text, Theme.Heading, Theme.Text, 680);
        l.Margin = new Padding(0, 14, 0, 6);
        col.Controls.Add(l);
    }

    async Task RefreshAsync()
    {
        try
        {
            var ctrl = await Task.Run(() => _session.Sync.ReadControlAsync(default));
            bool has = ctrl?.Vault.Recovery is not null;
            _recoveryState.ForeColor = has ? Theme.Success : Color.FromArgb(0xB4, 0x6A, 0x00);
            _recoveryState.Text = has
                ? L.T("✓  לכונן יש מפתח שחזור. יצירת מפתח חדש מחליפה אותו (מי שראה בעבר את רשומת הכונן ושמר גם את המפתח הישן עדיין יכול לפתוח איתו).", "✓  The drive has a recovery key. Creating a new one replaces it (someone who once saw the drive's record and also kept the old key can still open it).")
                : L.T("לכונן עדיין אין מפתח שחזור. מומלץ מאוד ליצור אחד.", "The drive has no recovery key yet. Creating one is strongly recommended.");
            _makeKey.Text = has ? L.T("צור מפתח שחזור חדש", "Create a new recovery key") : L.T("צור מפתח שחזור", "Create a recovery key");
        }
        catch (Exception ex) { _recoveryState.Text = TelegramErrors.Text(ex); }
        bool writable = _session.Writable;
        _makeKey.Enabled = _change.Enabled = writable;
        if (!writable)
            _changeResult.Text = L.T("הכונן במצב קריאה בלבד במחשב הזה: את השינויים האלה עושים מהמחשב שבו הוא פתוח לכתיבה.",
                "The drive is read-only on this PC: make these changes on the PC where it is open for writing.");
    }

    async Task MakeKeyAsync()
    {
        if (_keyPanel.Visible && !_keySaved.Checked) return;
        _makeKey.Enabled = false;
        try
        {
            var text = await Task.Run(() => _session.Sync.CreateRecoveryKeyAsync(_session.Keys, default));
            _keyBox.Text = text;
            _keyPanel.Visible = true;
            _keySaved.Checked = false;
            _close.Enabled = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Ui.Error(TelegramErrors.Text(ex));
            await RefreshAsync(); // show what the drive really has now
        }
        finally { _makeKey.Enabled = _session.Writable; }
    }

    void SaveKey()
    {
        using var dlg = new SaveFileDialog
        {
            FileName = $"DriveGram recovery key - {_session.State.Config.VolumeLabel}.txt",
            Filter = "*.txt|*.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.FileName.StartsWith(_session.MountPoint, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error(L.T("אל תשמור את מפתח השחזור על הכונן עצמו: בדיוק כשתצטרך אותו, הכונן לא ייפתח.",
                "Do not save the recovery key on the drive itself: exactly when you need it, the drive will not open."));
            return;
        }
        try
        {
            File.WriteAllText(dlg.FileName,
            $"DriveGram recovery key\r\nDrive: {_session.State.Config.VolumeLabel} ({_session.State.Config.AccountName})\r\n" +
            $"Created: {DateTime.Now:yyyy-MM-dd HH:mm}\r\n\r\n{_keyBox.Text}\r\n\r\n" +
            "Type this key wherever DriveGram asks for the drive password. Keep it away from this PC.\r\n");
        }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    async Task ChangeAsync()
    {
        _changeResult.ForeColor = Theme.Error;
        if (_new1.Text.Length < 10) { _changeResult.Text = L.T("הסיסמה החדשה קצרה מדי.", "The new password is too short."); return; }
        if (_new1.Text != _new2.Text) { _changeResult.Text = L.T("הסיסמאות החדשות לא זהות.", "The new passwords do not match."); return; }
        _change.Enabled = false;
        try
        {
            // proves the user knows the current secret (the drive is open, but this PC may just remember the key)
            var ctrl = await Task.Run(() => _session.Sync.ReadControlAsync(default));
            var current = _current.Text;
            await Task.Run(() => Keys.UnlockAny(ctrl!.Vault, current));
            var fresh = _new1.Text;
            await Task.Run(() => _session.Sync.ChangePasswordAsync(_session.Keys, fresh, default));
            _changeResult.ForeColor = Theme.Success;
            _changeResult.Text = L.T("✓  הסיסמה הוחלפה.", "✓  The password was changed.");
            _current.Text = _new1.Text = _new2.Text = "";
        }
        catch (WrongPasswordException) { _changeResult.Text = L.T("הסיסמה הנוכחית (או מפתח השחזור) שגויה.", "The current password (or recovery key) is wrong."); }
        catch (Exception ex) { _changeResult.Text = TelegramErrors.Text(ex); }
        finally { _change.Enabled = _session.Writable; }
    }
}
