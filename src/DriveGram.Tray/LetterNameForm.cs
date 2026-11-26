using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// "Drive letter and name": the two things people change most, without the whole setup wizard (no Telegram
/// sign-in, nothing disconnected while the window is open). A connected drive reconnects for a few seconds
/// only when something really changed.
/// </summary>
sealed class LetterNameForm : Form
{
    readonly ComboBox _letter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Font = Theme.Body, RightToLeft = RightToLeft.No };
    readonly TextBox _label = Theme.Input(260);

    public string Letter => (string)_letter.SelectedItem!;
    public string Label => string.IsNullOrWhiteSpace(_label.Text) ? "DriveGram" : _label.Text.Trim();

    /// <param name="mountedOn">The letter it is on right now (another one than usual when its own was busy).</param>
    /// <param name="preparing">Files of this drive are being prepared (they start again after the reconnect).</param>
    /// <param name="reconnectsNow">Not connected, but it connects right after the change (its letter was refused).</param>
    public LetterNameForm(AppState state, bool connected, string? mountedOn = null, bool preparing = false, bool reconnectsNow = false)
    {
        Ui.Prepare(this);
        Text = L.T($"DriveGram – אות ושם הכונן ({state.Config.VolumeLabel})", $"DriveGram – drive letter and name ({state.Config.VolumeLabel})");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var cfg = state.Config;
        var inUse = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var otherDrives = AppState.All().Where(a => a.Dir != state.Dir).Select(a => char.ToUpperInvariant(a.Config.MountPoint[0])).ToHashSet();
        var current = char.ToUpperInvariant(cfg.MountPoint[0]);
        var now = char.ToUpperInvariant((mountedOn ?? cfg.MountPoint)[0]); // its own letters, even though "in use"
        foreach (var c in "DEFGHIJKLMNOPQRSTUVWXYZ".Where(c => !otherDrives.Contains(c) && (!inUse.Contains(c) || c == current || c == now)))
            _letter.Items.Add($"{c}:");
        if (!_letter.Items.Contains($"{current}:")) _letter.Items.Insert(0, $"{current}:");
        _letter.SelectedItem = $"{current}:";
        _label.Text = cfg.VolumeLabel;
        _label.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _label.Font = Theme.Body;
        _label.MaxLength = 32;

        var col = Theme.Column();
        col.Padding = new Padding(24, 20, 24, 16);
        col.Controls.Add(Theme.Label(L.T("אות הכונן:", "Drive letter:"), Theme.Small, Theme.Text));
        col.Controls.Add(_letter);
        var free = Theme.Label(mountedOn is not null && char.ToUpperInvariant(mountedOn[0]) != current
                ? L.T($"מוצגות רק אותיות פנויות. כרגע הכונן ב־{L.Ltr(mountedOn)}, כי {L.Ltr(cfg.MountPoint)} הייתה תפוסה.", $"Only free letters are shown. The drive is on {mountedOn} right now, because {cfg.MountPoint} was busy.")
                : L.T("מוצגות רק אותיות פנויות.", "Only free letters are shown."), Theme.Small, Theme.Muted, 380);
        free.Margin = new Padding(0, 2, 0, 8);
        col.Controls.Add(free);
        col.Controls.Add(Theme.Label(L.T("שם הכונן ב\"מחשב זה\":", "Name in This PC:"), Theme.Small, Theme.Text));
        col.Controls.Add(_label);
        var note = Theme.Label(connected
                ? L.T("אם תשנה משהו, הכונן יתנתק ויתחבר מחדש לכמה שניות כשתלחץ \"שמור\". קבצים שעוד עולים ימשיכו לעלות אחר כך.",
                    "If you change something, the drive disconnects and reconnects for a few seconds when you press \"Save\". Files still uploading go on afterwards.")
                  + (preparing ? L.T(" גם הכנת הקבצים שרצה עכשיו תמשיך אחרי החיבור מחדש.", " Files being prepared now go on after the reconnect too.") : "")
                : reconnectsNow ? L.T("אחרי השמירה הכונן ינסה להתחבר מיד, באות ובשם החדשים.", "After saving, the drive tries to connect right away, with the new letter and name.")
                : L.T("השינוי ייכנס לתוקף בפעם הבאה שהכונן יתחבר.", "The change takes effect the next time the drive connects."),
            Theme.Small, Theme.Muted, 380);
        note.Margin = new Padding(0, 8, 0, 8);
        col.Controls.Add(note);
        var buttons = Theme.Row();
        buttons.Margin = new Padding(0, 10, 0, 0);
        var save = Theme.Primary(L.T("שמור", "Save"));
        var cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.AddRange([save, cancel]);
        col.Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(col);
    }
}
