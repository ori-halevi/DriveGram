using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// "Drive type": a network drive (recommended, the default for a new drive) or a regular disk, with what
/// each means in plain words. From the tray the change takes effect when the drive is connected again (the
/// tray does that right away); from the setup wizard it only sets the choice.
/// </summary>
sealed class DriveTypeForm : Form
{
    readonly RadioButton _disk = new() { AutoSize = true, Font = Theme.Heading };
    readonly RadioButton _network = new() { AutoSize = true, Font = Theme.Heading };

    /// <summary>The choice when the user pressed OK.</summary>
    public bool Network => _network.Checked;

    public DriveTypeForm(string driveName, bool network, bool inWizard = false)
    {
        Ui.Prepare(this);
        Text = L.T($"DriveGram – סוג הכונן ({driveName})", $"DriveGram – drive type ({driveName})");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 720);
        BackColor = Theme.Window;

        var col = Theme.Column();
        col.AutoSize = false;
        col.AutoScroll = true;
        col.Dock = DockStyle.Fill;

        col.Controls.Add(Theme.Label(L.T(
            "הקבצים, ההצפנה, המהירות והעדכון בין המחשבים זהים בשני המצבים. ההבדל הוא רק איך וינדוס ותוכנות אחרות מתייחסים לכונן. לא בטוח? השאר \"כונן רשת\".",
            "Files, encryption, speed and updates between PCs are the same either way. The only difference is how Windows and other programs treat the drive. Not sure? Keep \"Network drive\"."),
            Theme.Body, Theme.Text, 660));

        _network.Text = L.T("כונן רשת (מומלץ)", "Network drive (recommended)");
        col.Controls.Add(Spaced(_network));
        Bullets(col,
            L.T("✓  רוב תוכנות הגיבוי, האנטי־וירוס והאינדוקס מדלגות על כונני רשת מעצמן: אף תוכנה לא \"תוריד את הכל\" ברקע.",
                "✓  Most backup, antivirus and indexing programs skip network drives by themselves: nothing \"downloads everything\" in the background."),
            L.T("✓  מחיקה פשוטה: וינדוס לא מנסה להשתמש בסל המחזור שלו (הקובץ עובר לסל המחזור של DriveGram).", "✓  Simple deletes: Windows does not try to use its own recycle bin (the file goes to DriveGram's recycle bin)."),
            L.T("✗  מופיע ב\"מחשב זה\" בחלק \"מיקומי רשת\", ולא ליד הדיסקים.", "✗  Shown in This PC under \"Network locations\", not next to the disks."),
            L.T("✗  חלק מהתוכנות מסרבות לכונני רשת: קטלוג של Lightroom, קובצי דואר של Outlook (PST), התקנת משחקים ותוכנות, מכונות וירטואליות.",
                "✗  Some programs refuse network drives: a Lightroom catalog, Outlook mail files (PST), installing games and programs, virtual machines."),
            L.T("✗  אין חיפוש בתוך תוכן מסמכים (חיפוש לפי שם עובד, לאט יותר).", "✗  No searching inside documents (searching by name works, more slowly)."),
            L.T("✗  הפעלת תוכנה (‎.exe‎) מהכונן מציגה אזהרת אבטחה; מסמכי Office עם מאקרו נפתחים כשהמאקרו חסום.",
                "✗  Running a program (.exe) from the drive shows a security warning; Office documents with macros open with macros blocked."),
            L.T("✗  כל מחיקה שואלת \"למחוק לצמיתות?\" (בפועל הקובץ נשמר בסל המחזור של DriveGram).", "✗  Every delete asks \"delete permanently?\" (in fact the file is still kept in DriveGram's recycle bin)."),
            L.T("✗  גם תוכנת גיבוי שאתה כן רוצה שתגבה את הכונן תדלג עליו, עד שתגדיר אותה לגבות גם כונני רשת.", "✗  Even a backup program you DO want to back up the drive skips it, until you set it to back up network drives too."));

        _disk.Text = L.T("דיסק רגיל", "Regular disk");
        col.Controls.Add(Spaced(_disk));
        Bullets(col,
            L.T($"✓  מופיע ב\"מחשב זה\" ליד {L.Ltr("C:")} ו־{L.Ltr("D:")}, עם האייקון והשם של DriveGram.", "✓  Shown in This PC next to C: and D:, with DriveGram's icon and name."),
            L.T("✓  כל התוכנות עובדות איתו כמו עם כל דיסק (גם Lightroom, ‏Outlook, התקנת תוכנות).", "✓  Every program treats it like any disk (Lightroom, Outlook, installing programs too)."),
            L.T("✓  חיפוש מילה בתוך מסמכים עובד (למסמכים ששמורים במחשב הזה), אם מוסיפים את הכונן לאינדקס החיפוש של וינדוס.", "✓  Searching for words inside documents works (for documents stored on this PC), if the drive is added to Windows' search index."),
            L.T("✗  תוכנות גיבוי ואנטי־וירוס שסורקות \"את כל הדיסקים\" עלולות לנסות להוריד את כל הכונן מטלגרם. DriveGram כבר מונע את זה מ־Defender ומתוכנות מוכרות, אבל אי אפשר להכיר מראש כל תוכנה.",
                "✗  Backup and antivirus programs that scan \"all disks\" may try to download the whole drive from Telegram. DriveGram already stops Defender and well-known programs from doing so, but it cannot know every program in advance."));

        col.Controls.Add(Theme.Label(L.T(
            "המלצה: כונן רשת מתאים לרוב השימושים (מסמכים, תמונות, סרטונים, ארכיון), ומגן על הכונן מתוכנות שמנסות להוריד את כולו. דיסק רגיל עדיף לקטלוג Lightroom, ‏Outlook, התקנת תוכנות, או חיפוש בתוך מסמכים. אפשר לשנות את זה אחר כך בכל רגע, בתפריט של הכונן.",
            "Advice: a network drive suits most uses (documents, photos, videos, an archive) and protects the drive from programs that try to download all of it. A regular disk is better for a Lightroom catalog, Outlook, installing programs, or searching inside documents. You can change it later at any time, from the drive's menu."),
            Theme.Small, Theme.Muted, 660));
        if (!inWizard)
            col.Controls.Add(Theme.Label(L.T("השינוי מנתק את הכונן ומחבר אותו מחדש (לוקח כמה שניות).", "The change disconnects the drive and connects it again (takes a few seconds)."),
                Theme.Small, Theme.Muted, 660));

        (network ? _network : _disk).Checked = true;
        var ok = Theme.Primary(inWizard ? L.T("בחר", "Choose") : L.T("שמור", "Save"));
        var cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var row = Theme.Row();
        row.Margin = new Padding(0, 16, 0, 0);
        row.Controls.AddRange([ok, cancel]);
        col.Controls.Add(row);
        Theme.Fill(this, col, new Padding(24, 18, 24, 18));
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
