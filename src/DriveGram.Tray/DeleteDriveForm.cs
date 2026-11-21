using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// The last stop before a drive is destroyed: says exactly what will be lost, and only enables the
/// delete button after the user ticks "I understand" AND types the drive's name. Cancel is the default.
/// </summary>
sealed class DeleteDriveForm : Form
{
    static readonly Color Danger = Color.FromArgb(0xC6, 0x28, 0x28);
    readonly CheckBox _understand = new() { AutoSize = true };
    readonly TextBox _typed = Theme.Input(300);
    readonly Button _delete = new()
    {
        AutoSize = true, MinimumSize = new Size(170, 38), FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
        BackColor = Color.FromArgb(0xE8, 0xA9, 0xA9), Enabled = false, Font = Theme.Body, DialogResult = DialogResult.OK,
    };
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
        Padding = new Padding(24);
        BackColor = Color.White;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T("⚠  מחיקת כונן לצמיתות", "⚠  Delete a drive permanently"), Theme.Title, Danger, 600));
        var nameLabel = Theme.Label($"{name}  ({letter})", Theme.Heading, Theme.Text, 600);
        nameLabel.RightToLeft = RightToLeft.No; // the drive's own name, left-to-right, on its own line
        col.Controls.Add(nameLabel);
        col.Controls.Add(Theme.Label(L.T("את הפעולה הזו אי אפשר לבטל.", "This cannot be undone."), Theme.Heading, Danger));

        var what = Theme.Card(Color.FromArgb(0xFD, 0xEC, 0xEA), Color.FromArgb(0xE5, 0x9A, 0x92));
        var list = Theme.Column();
        list.Controls.Add(Theme.Label(L.T("יימחקו לצמיתות, מטלגרם ומהמחשב הזה:", "Deleted for good, from Telegram and from this PC:"), Theme.Body, Theme.Text, 560));
        list.Controls.Add(Theme.Label(L.T($"•  {p.Files:N0} קבצים ו־{p.Folders:N0} תיקיות ({Ui.Size(p.Bytes)})", $"•  {p.Files:N0} files and {p.Folders:N0} folders ({Ui.Size(p.Bytes)})"), maxWidth: 560));
        if (p.Trashed > 0)
            list.Controls.Add(Theme.Label(L.T($"•  {p.Trashed:N0} פריטים בסל המחזור של הכונן", $"•  {p.Trashed:N0} items in the drive's recycle bin"), maxWidth: 560));
        if (p.PendingUploads > 0)
            list.Controls.Add(Theme.Label(L.T($"•  {p.PendingUploads:N0} קבצים שעוד לא סיימו לעלות", $"•  {p.PendingUploads:N0} files that have not finished uploading"), maxWidth: 560));
        list.Controls.Add(Theme.Label(L.T("•  שני הערוצים של הכונן בטלגרם, כולל היסטוריית השינויים והגיבויים שלו", "•  the drive's two Telegram channels, with its change history and backups"), maxWidth: 560));
        what.Controls.Add(list);
        col.Controls.Add(what);

        if (p.OpenElsewhere is { } pc)
            col.Controls.Add(Theme.Label(L.T($"הכונן פתוח כרגע לכתיבה במחשב \"{pc}\". שינויים שנעשים שם ועוד לא עלו — יאבדו.",
                $"The drive is open for writing on \"{pc}\" right now. Changes made there that have not uploaded yet will be lost."), Theme.Body, Danger, 600));
        col.Controls.Add(Theme.Label(L.T(
            "מחשבים אחרים שמחוברים לכונן הזה (וחשבונות גיבוי שמנהלים בו) יאבדו אליו גישה. אם יש משהו שתרצה לשמור — העתק אותו מהכונן לפני המחיקה.",
            "Other PCs connected to this drive (and backup accounts that administer it) lose access too. If there is anything to keep, copy it out of the drive first."),
            Theme.Body, Theme.Muted, 600));

        _understand.Text = L.T("אני מבין שהקבצים יימחקו לצמיתות ואי אפשר יהיה לשחזר אותם", "I understand the files will be deleted forever and cannot be recovered");
        col.Controls.Add(_understand);
        col.Controls.Add(Theme.Label(L.T($"כדי לאשר, הקלד את שם הכונן:  {name}", $"To confirm, type the drive's name:  {name}"), Theme.Body, Theme.Text, 600));
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
        Controls.Add(col);

        CancelButton = cancel;
        AcceptButton = null; // Enter never deletes
        _understand.CheckedChanged += (_, _) => UpdateState();
        _typed.TextChanged += (_, _) => UpdateState();
        Shown += (_, _) => cancel.Focus();
    }

    void UpdateState()
    {
        bool ok = _understand.Checked && string.Equals(_typed.Text.Trim(), _name, StringComparison.Ordinal);
        _delete.Enabled = ok;
        _delete.BackColor = ok ? Danger : Color.FromArgb(0xE8, 0xA9, 0xA9);
    }
}
