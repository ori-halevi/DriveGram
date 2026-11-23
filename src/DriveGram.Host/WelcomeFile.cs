using System.Text;

namespace DriveGram.Host;

/// <summary>
/// A short note put once into every brand-new drive, in the language of the PC that created it: what can go
/// wrong when files live in a Telegram account, and what to do about it. It is an ordinary file (stored in
/// Telegram, seen on every PC): the user may edit or delete it, and it never comes back.
/// </summary>
public static class WelcomeFile
{
    public static string Name => L.T("חשוב לקרוא - סיכונים בשמירת קבצים בטלגרם.md",
                                     "Important - risks of keeping files in Telegram.md");

    public static string Text => L.T(He, En);

    /// <summary>Write the note into the drive's root (through the drive itself, like any app would).
    /// False if it could not be written now (the caller tries again at the next start).</summary>
    public static bool TryWrite(string mountPoint)
    {
        try
        {
            var path = mountPoint.TrimEnd('\\') + "\\" + Name;
            if (!File.Exists(path)) File.WriteAllText(path, Text.Replace("\n", "\r\n"), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"welcome file: {ex.Message}");
            return false;
        }
    }

    const string He = """
        # חשוב לקרוא: סיכונים בשמירת קבצים בטלגרם

        הקבצים בכונן הזה נשמרים מוצפנים בחשבון הטלגרם שלך. זה נוח, אבל טלגרם לא נבנה לשמירת קבצים, ולכן כדאי להכיר כמה סיכונים:

        - **החשבון הוא המקום היחיד של הקבצים.** אם טלגרם יחסום או ימחק אותו, הקבצים יאבדו. טלגרם גם מוחק חשבון שלא נכנסו אליו זמן רב: כדאי לבחור את התקופה הארוכה ביותר בטלגרם, בהגדרות ← פרטיות ואבטחה ← מחיקת החשבון שלי.
        - **זה לא גיבוי.** שמור את הקבצים החשובים גם במקום נוסף. בתפריט של DriveGram יש "ייצוא כל הקבצים לתיקייה…" ו"גיבוי בחשבון נוסף…".
        - **בלי הסיסמה אי אפשר לפתוח את הקבצים**, ואין דרך לשחזר אותה. צור מפתח שחזור ("סיסמה ומפתח שחזור…" בתפריט) ושמור אותו במקום בטוח.
        - **אל תמחק את הערוצים של הכונן בטלגרם ואל תשנה בהם דבר.** הם הכונן עצמו. מותר להשתיק אותם.
        - **הפעל אימות דו־שלבי בטלגרם.** מי שייכנס לחשבון לא יוכל לקרוא את הקבצים, אבל יוכל למחוק אותם.

        אפשר למחוק את הקובץ הזה. הוא לא ייווצר שוב.
        """;

    const string En = """
        # Important: risks of keeping files in Telegram

        The files on this drive are kept encrypted in your Telegram account. That is convenient, but Telegram was not built for storing files, so it is worth knowing a few risks:

        - **Your account is the only place the files are.** If Telegram blocks or deletes it, the files are lost. Telegram also deletes accounts nobody signed in to for a long time: choose the longest period in Telegram, under Settings → Privacy and Security → Delete my account.
        - **This is not a backup.** Keep the files that matter somewhere else too. DriveGram's menu has "Export every file to a folder…" and "Backup on another account…".
        - **Without the password the files cannot be opened**, and it cannot be recovered. Create a recovery key ("Password and recovery key…" in the menu) and keep it somewhere safe.
        - **Do not delete the drive's channels in Telegram or change anything in them.** They are the drive itself. Muting them is fine.
        - **Turn on two-step verification in Telegram.** Someone who gets into the account cannot read the files, but can delete them.

        You may delete this file. It will not be created again.
        """;
}
