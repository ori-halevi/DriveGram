using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// The last stop before a drive is destroyed: says exactly what will be lost, and only enables the
/// delete button after the user ticks "I understand" AND types the drive's name. Cancel is the default.
/// </summary>
sealed class DeleteDriveForm : Form
{
    static Color Danger => Theme.Error;
    readonly CheckBox _understand = new() { AutoSize = true };
    readonly TextBox _typed = Theme.Input(300);
    // red in every look, with white on it: the one button that cannot be undone. Slot.Fixed keeps the
    // look's hands off it — in a dark look "white" belongs to no slot and would be read as ordinary text.
    readonly Button _delete = new Button
    {
        AutoSize = true, MinimumSize = new Size(170, 38), FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
        Enabled = false, Font = Theme.Body, DialogResult = DialogResult.OK,
    }.Wears(Slot.Fixed, Slot.Fixed);
    readonly string _name;

    public DeleteDriveForm(string letter, string name, DeletionPreview p)
    {
        _name = name;
        Ui.Prepare(this);
        Text = L.T("DriveGram – מחיקת כונן לצמיתות", "DriveGram – delete a drive permanently");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Window;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T("⚠  מחיקת כונן לצמיתות", "⚠  Delete a drive permanently"), Theme.Title, Danger, 700));
        var nameLabel = Theme.Label($"{name}  ({letter})", Theme.Heading, Theme.Text, 700);
        nameLabel.RightToLeft = RightToLeft.No; // the drive's own name, left-to-right, on its own line
        col.Controls.Add(nameLabel);
        col.Controls.Add(Theme.Label(L.T("את הפעולה הזו אי אפשר לבטל.", "This cannot be undone."), Theme.Heading, Danger));

        var what = Theme.Card(Slot.DangerBack, Slot.DangerBorder);
        var list = Theme.Column();
        list.MinimumSize = new Size(700 - 28, 0); // the card as wide as the texts around it (700, with its padding)
        list.Controls.Add(Theme.Label(L.T("יימחקו לצמיתות, מטלגרם ומהמחשב הזה:", "Deleted for good, from Telegram and from this PC:"), Theme.Body, Theme.Text, 660));
        var folders = L.N(p.Folders, "תיקייה אחת", "{0:N0} תיקיות", "1 folder", "{0:N0} folders");
        list.Controls.Add(Theme.Label("•  " + L.N(p.Files, "קובץ אחד", "{0:N0} קבצים", "1 file", "{0:N0} files")
            + L.T(p.Folders == 1 ? " ו" : " ו־", " and ") + folders + $" ({Ui.Size(p.Bytes)})", maxWidth: 660));
        if (p.Trashed > 0)
            list.Controls.Add(Theme.Label(L.N(p.Trashed, "•  פריט אחד בסל המחזור של הכונן", "•  {0:N0} פריטים בסל המחזור של הכונן",
                "•  1 item in the drive's recycle bin", "•  {0:N0} items in the drive's recycle bin"), maxWidth: 660));
        if (p.PendingUploads > 0)
            list.Controls.Add(Theme.Label(L.N(p.PendingUploads, "•  קובץ אחד שעוד לא סיים לעלות", "•  {0:N0} קבצים שעוד לא סיימו לעלות",
                "•  1 file that has not finished uploading", "•  {0:N0} files that have not finished uploading"), maxWidth: 660));
        list.Controls.Add(Theme.Label(L.T("•  שני הערוצים של הכונן בטלגרם, כולל היסטוריית השינויים והגיבויים שלו", "•  the drive's two Telegram channels, with its change history and backups"), maxWidth: 660));
        what.Controls.Add(list);
        col.Controls.Add(what);

        if (p.OpenElsewhere is { } pc)
            col.Controls.Add(Theme.Label(L.T($"הכונן פתוח כרגע לכתיבה במחשב \"{L.Ltr(pc)}\". שינויים שנעשו שם ועוד לא עלו יאבדו.",
                $"The drive is open for writing on \"{pc}\" right now. Changes made there that have not uploaded yet will be lost."), Theme.Body, Danger, 700));
        col.Controls.Add(Theme.Label(L.T(
            "מחשבים אחרים שמחוברים לכונן הזה (וחשבונות גיבוי שמנהלים אותו) יאבדו אליו גישה. אם יש משהו שתרצה לשמור, העתק אותו מהכונן לפני המחיקה. רוצה רק שהכונן לא יופיע במחשב הזה? בחר במקום זה \"הסר מהמחשב הזה…\": הקבצים יישארו בטלגרם.",
            "Other PCs connected to this drive (and backup accounts that administer it) lose access too. If there is anything to keep, copy it out of the drive first. Only want the drive gone from this PC? Choose \"Remove from this PC…\" instead: the files stay in Telegram."),
            Theme.Body, Theme.Muted, 700));

        _understand.Text = L.T("אני מבין שהקבצים יימחקו לצמיתות ואי אפשר יהיה לשחזר אותם", "I understand the files will be deleted forever and cannot be recovered");
        col.Controls.Add(_understand);
        // the name on a line of its own, in bold: a Hebrew name would otherwise read as part of the sentence
        col.Controls.Add(Theme.Label(L.T("כדי לאשר, הקלד את שם הכונן:", "To confirm, type the drive's name:"), Theme.Body, Theme.Text, 700));
        var typeThis = Theme.Label(name, new Font(Theme.Body, FontStyle.Bold), Theme.Text, 700);
        typeThis.Margin = new Padding(16, 0, 16, 8);
        col.Controls.Add(typeThis);
        _typed.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _typed.Font = Theme.Body;
        col.Controls.Add(_typed);

        _delete.Text = L.T("מחק לצמיתות", "Delete permanently");
        _delete.FlatAppearance.BorderSize = 0;
        var cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0), WrapContents = false };
        buttons.Controls.AddRange([cancel, _delete]);
        col.Controls.Add(buttons);
        Theme.Fill(this, col, new Padding(24));

        CancelButton = cancel;
        AcceptButton = null; // Enter never deletes
        _understand.CheckedChanged += (_, _) => UpdateState();
        _typed.TextChanged += (_, _) => UpdateState();
        Shown += (_, _) => cancel.Focus();
    }

    /// <summary>For the screenshots: the state where the button is really armed (red, white on it) — the
    /// one that cannot be checked by eye otherwise, because it needs the box ticked and the name typed.</summary>
    internal void Demo() { _understand.Checked = true; _typed.Text = _name; }

    void UpdateState()
    {
        bool ok = _understand.Checked && string.Equals(_typed.Text.Trim(), _name, StringComparison.Ordinal);
        _delete.Enabled = ok;
        _delete.BackColor = ok ? Danger : Theme.Blend(Danger, Theme.Window, 0.6);
    }
}
