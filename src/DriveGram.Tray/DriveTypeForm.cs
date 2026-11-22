using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// "Drive type": a regular disk (the default) or a network drive, with what each means in plain words.
/// The change takes effect when the drive is connected again (the tray does that right away).
/// </summary>
sealed class DriveTypeForm : Form
{
    readonly RadioButton _disk = new() { AutoSize = true, Font = Theme.Heading };
    readonly RadioButton _network = new() { AutoSize = true, Font = Theme.Heading };

    /// <summary>The choice when the user pressed OK.</summary>
    public bool Network => _network.Checked;

    public DriveTypeForm(string driveName, bool network)
    {
        Ui.Prepare(this);
        Text = L.T($"DriveGram – סוג הכונן ({driveName})", $"DriveGram – drive type ({driveName})");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 720);
        BackColor = Color.White;

        var col = Theme.Column();
        col.Padding = new Padding(24, 18, 24, 18);
        col.AutoSize = false;
        col.AutoScroll = true;
        col.Dock = DockStyle.Fill;

        col.Controls.Add(Theme.Label(L.T(
            "הקבצים, ההצפנה, המהירות והסנכרון זהים בשני המצבים. ההבדל הוא איך וינדוס ותוכנות אחרות מתייחסים לכונן.",
            "Files, encryption, speed and syncing are the same either way. The difference is how Windows and other programs treat the drive."),
            Theme.Body, Theme.Text, 680));

        _disk.Text = L.T("דיסק רגיל (ברירת המחדל)", "Regular disk (the default)");
        col.Controls.Add(Spaced(_disk));
        Bullets(col,
            L.T($"✓  מופיע ב\"מחשב זה\" ליד {L.Ltr("C:")} ו־{L.Ltr("D:")}, עם האייקון והשם של DriveGram.", "✓  Shown in This PC next to C: and D:, with DriveGram's icon and name."),
            L.T("✓  כל התוכנות עובדות איתו כמו עם כל דיסק (גם Lightroom, ‏Outlook, התקנת תוכנות).", "✓  Every program treats it like any disk (Lightroom, Outlook, installing programs too)."),
            L.T("✓  חיפוש מילה בתוך מסמכים עובד, אם מוסיפים את הכונן לאינדקס של וינדוס.", "✓  Searching for words inside documents works, if the drive is added to Windows' index."),
            L.T("✗  תוכנות גיבוי ואנטי־וירוס שסורקות \"את כל הדיסקים\" עלולות לנסות להוריד את כל הכונן מטלגרם. Defender ותוכנות מוכרות נחסמות כבר, אבל לא כל תוכנה מוכרת.",
                "✗  Backup and antivirus programs that scan \"all disks\" may try to download the whole drive from Telegram. Defender and well-known programs are already blocked, but not every program is known."));

        _network.Text = L.T("כונן רשת", "Network drive");
        col.Controls.Add(Spaced(_network));
        Bullets(col,
            L.T("✓  רוב תוכנות הגיבוי, האנטי־וירוס והאינדוקס מדלגות על כונני רשת מעצמן: אף תוכנה לא \"תוריד את הכל\" ברקע.",
                "✓  Most backup, antivirus and indexing programs skip network drives by themselves: nothing \"downloads everything\" in the background."),
            L.T("✓  מחיקה פשוטה: וינדוס לא מנסה להשתמש בסל המחזור שלו (הקובץ עובר לסל של DriveGram).", "✓  Simple deletes: Windows does not try its own recycle bin (the file goes to DriveGram's bin)."),
            L.T("✗  מופיע ב\"מחשב זה\" בחלק \"מיקומי רשת\", ולא ליד הדיסקים.", "✗  Shown in This PC under \"Network locations\", not next to the disks."),
            L.T("✗  חלק מהתוכנות מסרבות לכונני רשת: קטלוג של Lightroom, קובצי דואר של Outlook (PST), התקנת משחקים ותוכנות, מכונות וירטואליות.",
                "✗  Some programs refuse network drives: a Lightroom catalog, Outlook mail files (PST), installing games and programs, virtual machines."),
            L.T("✗  אין חיפוש בתוך תוכן מסמכים (חיפוש לפי שם עובד, לאט יותר).", "✗  No searching inside documents (searching by name works, more slowly)."),
            L.T("✗  הפעלת תוכנה (‎.exe‎) מהכונן מציגה אזהרת אבטחה; מסמכי Office עם מאקרו נפתחים כשהמאקרו חסום.",
                "✗  Running a program (.exe) from the drive shows a security warning; Office documents with macros open with macros blocked."),
            L.T("✗  כל מחיקה שואלת \"למחוק לצמיתות?\" (בפועל הקובץ נשמר בסל של DriveGram).", "✗  Every delete asks \"delete permanently?\" (the file is still kept in DriveGram's bin)."),
            L.T("✗  תוכנת גיבוי שאתה כן רוצה שתגבה את הכונן תדלג עליו, עד שתגדיר אותה במפורש.", "✗  A backup program you DO want to back up the drive skips it until you set that up explicitly."));

        col.Controls.Add(Theme.Label(L.T(
            "המלצה: לקבצים אישיים (מסמכים, תמונות, סרטים) שני המצבים מתאימים. כונן רשת עדיף אם מותקנת תוכנת גיבוי כמו Backblaze, או אנטי־וירוס שאינו Defender. דיסק רגיל עדיף לקטלוג Lightroom, ‏Outlook, תוכנות, או חיפוש בתוך מסמכים.",
            "Advice: for personal files (documents, photos, videos) both work. A network drive is better with a backup program like Backblaze, or an antivirus other than Defender. A regular disk is better for a Lightroom catalog, Outlook, programs, or searching inside documents."),
            Theme.Small, Theme.Muted, 680));
        col.Controls.Add(Theme.Label(L.T("השינוי מנתק ומחבר את הכונן מחדש (לוקח כמה שניות).", "The change disconnects and reconnects the drive (a few seconds)."),
            Theme.Small, Theme.Muted, 680));

        (network ? _network : _disk).Checked = true;
        var ok = Theme.Primary(L.T("שמור", "Save"));
        var cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var row = Theme.Row();
        row.Margin = new Padding(0, 16, 0, 0);
        row.Controls.AddRange([ok, cancel]);
        col.Controls.Add(row);
        Controls.Add(col);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static Control Spaced(Control c)
    {
        c.Margin = new Padding(0, 16, 0, 4);
        return c;
    }

    static void Bullets(FlowLayoutPanel col, params string[] lines)
    {
        foreach (var line in lines)
        {
            var l = Theme.Label(line, Theme.Body, Theme.Text, 610);
            l.Margin = new Padding(16, 2, 16, 2);
            col.Controls.Add(l);
        }
    }
}
