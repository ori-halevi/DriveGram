using DriveGram.Host;
using TL;

namespace DriveGram.Tray.Setup;

/// <summary>Telegram errors in plain words (Hebrew or English), with what to do about them.</summary>
static class TelegramErrors
{
    public static string Text(Exception ex) => ex switch
    {
        RpcException { Code: 420 } r => L.T($"יותר מדי ניסיונות. טלגרם מבקש להמתין {Wait(r.X)} לפני שמנסים שוב.",
                                             $"Too many attempts. Telegram asks to wait {Wait(r.X)} before trying again."),
        RpcException r => r.Message switch
        {
            "PHONE_NUMBER_INVALID" => L.T("מספר הטלפון לא תקין. כתוב אותו בפורמט בינלאומי, למשל ‎+972501234567‎.",
                                          "The phone number is not valid. Use the international format, e.g. +972501234567."),
            "PHONE_NUMBER_BANNED" => L.T("המספר הזה חסום בטלגרם.", "This number is banned by Telegram."),
            "PHONE_NUMBER_UNOCCUPIED" => L.T("אין חשבון טלגרם למספר הזה. צור חשבון באפליקציית טלגרם ונסה שוב.",
                                             "There is no Telegram account for this number. Create one in the Telegram app and try again."),
            "PHONE_CODE_INVALID" => L.T("הקוד שגוי. ודא שאתה מקליד את הקוד האחרון שקיבלת (כל שליחה חדשה מבטלת את הקודם).",
                                        "Wrong code. Make sure you type the latest code (each new one cancels the previous)."),
            "PHONE_CODE_EXPIRED" => L.T("תוקף הקוד פג. לחץ על \"מספר אחר / קוד חדש\" ואז על \"שלח קוד\".", "The code expired. Click \"Another number / a new code\", then \"Send code\"."),
            "PHONE_CODE_EMPTY" => L.T("לא הוקלד קוד.", "No code was entered."),
            "PASSWORD_HASH_INVALID" => L.T("סיסמת האימות הדו־שלבי שגויה.", "Wrong two-step verification password."),
            "AUTH_RESTART" => L.T("טלגרם ביקש להתחיל את ההתחברות מחדש. לחץ \"נסה שוב\" (או, בהתחברות בטלפון, \"מספר אחר / קוד חדש\").",
                                  "Telegram asked to restart the sign-in. Click \"Try again\" (or, with a phone number, \"Another number / a new code\")."),
            "AUTH_TOKEN_EXPIRED" => L.T("תוקף קוד ה־QR פג. לחץ \"נסה שוב\" לקוד חדש.", "The QR code expired. Click \"Try again\" for a new one."),
            "PHONE_NUMBER_FLOOD" or "PHONE_PASSWORD_FLOOD" or "SEND_CODE_UNAVAILABLE" =>
                L.T("ביקשת יותר מדי קודים. טלגרם חוסם זמנית את המספר הזה: נסה שוב בעוד כמה שעות, או התחבר בסריקת QR.",
                    "Too many codes were requested. Telegram has blocked this number for a while: try again in a few hours, or sign in with the QR code."),
            "CHANNELS_TOO_MUCH" => L.T("החשבון הזה חבר ביותר מדי ערוצים, ולכן אי אפשר ליצור עוד. צא מכמה ערוצים, או השתמש בחשבון ייעודי.",
                                       "This account is in too many channels to create more. Leave some, or use a dedicated account."),
            "AUTH_KEY_DUPLICATED" => L.T("טלגרם ביטל את החיבור כי הוא שימש בשני מקומות בבת אחת. התחבר מחדש: בתפריט של הכונן, \"התחברות לטלגרם…\".",
                                         "Telegram cancelled the sign-in because it was used in two places at once. Sign in again: in the drive's menu, \"Telegram sign-in…\"."),
            "API_ID_INVALID" or "API_ID_PUBLISHED_FLOOD" => L.T("פרטי ה־API (api_id / api_hash) לא תקינים. חזור לשלב הקודם ובדוק אותם.",
                                                                "The API details (api_id / api_hash) are not valid. Go back and check them."),
            "SESSION_REVOKED" or "AUTH_KEY_UNREGISTERED" => L.T("החיבור בוטל ממכשיר אחר. התחבר שוב.", "The sign-in was revoked from another device. Sign in again."),
            "USERNAME_NOT_OCCUPIED" or "USERNAME_INVALID" => L.T("לא נמצא משתמש טלגרם עם השם הזה. בדוק את שם המשתמש (בלי רווחים, למשל ‎@my_backup‎).",
                "No Telegram user has that username. Check it (no spaces, e.g. @my_backup)."),
            "BACKUP_IS_SELF" => L.T("זה החשבון שהכונן כבר נמצא בו. חשבון הגיבוי צריך להיות חשבון אחר.",
                "That is the account the drive already lives on. The backup must be a different account."),
            "BACKUP_IS_BOT" => L.T("זה בוט, לא חשבון. חשבון הגיבוי צריך להיות חשבון טלגרם רגיל.", "That is a bot. The backup must be a regular Telegram account."),
            "USER_PRIVACY_RESTRICTED" or "USER_NOT_MUTUAL_CONTACT" => L.T(
                "הגדרות הפרטיות של חשבון הגיבוי לא מאפשרות להוסיף אותו לערוצים. פתרונות: בחשבון הגיבוי שנה את \"פרטיות ← קבוצות וערוצים\" ל'כולם' (זמנית), או שתוסיף את שני החשבונות זה לזה כאנשי קשר, ונסה שוב.",
                "The backup account's privacy settings do not allow adding it to channels. Either set 'Privacy → Groups & Channels' to Everybody on it (temporarily), or add each other as contacts, then try again."),
            "CHAT_ADMIN_REQUIRED" or "RIGHT_FORBIDDEN" => L.T("לחשבון הזה אין הרשאה למנות מנהלים בערוצים של הכונן. רק הבעלים (או מנהל עם הרשאת \"הוספת מנהלים\") יכול.",
                "This account may not appoint admins in the drive's channels. Only the owner (or an admin allowed to add admins) can."),
            "USER_NOT_PARTICIPANT" => L.T("החשבון הזה עדיין לא הצטרף לשני הערוצים. ודא שהוא פתח את שני הקישורים.", "That account has not joined both channels yet. Make sure it opened both links."),
            "ADMINS_TOO_MUCH" => L.T("בערוץ כבר יש מקסימום מנהלים.", "The channel already has the maximum number of admins."),
            "USER_CHANNELS_TOO_MUCH" => L.T("חשבון הגיבוי כבר חבר בכמות המקסימלית של ערוצים.", "The backup account is already in the maximum number of channels."),
            _ => L.T($"טלגרם החזיר שגיאה: {r.Message}", $"Telegram error: {r.Message}"),
        },
        DriveGram.Remote.NotSignedInException => L.T("החשבון לא מחובר לטלגרם במחשב הזה. בתפריט של הכונן בחר \"התחברות לטלגרם…\".",
            "The account is not signed in to Telegram on this PC. In the drive's menu choose \"Telegram sign-in…\"."),
        DriveGram.Remote.DriveVanishedException => L.T("הכונן לא נמצא בחשבון הטלגרם הזה (אולי הערוצים שלו נמחקו, או שהחשבון הוסר מהם).",
            "The drive was not found on this Telegram account (its channels may have been deleted, or the account removed from them)."),
        DriveGram.ControlNotTrustedException => L.T("רשומת הכונן שונתה מחוץ ל־DriveGram. אשר אותה קודם: בתפריט של הכונן, \"אשר את רשומת הכונן…\".",
            "The drive's record was changed outside DriveGram. Confirm it first: \"Confirm the drive's record…\" in the drive's menu."),
        DriveGram.LeaseLostException => L.T("מחשב אחר לקח את הכונן לכתיבה. אפשר לבקש אותו בחזרה: בתפריט של הכונן, \"בקש את הכונן מ־…\".",
            "Another PC took the drive for writing. You can ask for it back from the menu."),
        DriveGram.Crypto.WrongPasswordException => L.T("הסיסמה (או מפתח השחזור) שגויה.", "Wrong password (or recovery key)."),
        DriveGram.NewerVaultException { WrongSecret: true } => L.T("הסיסמה לא פותחת את רשומת הכונן שבטלגרם.", "The password does not open the drive's record in Telegram."),
        DriveGram.NewerVaultException => L.T("ייתכן שהסיסמה של הכונן שונתה במחשב אחר.", "The drive's password may have been changed on another PC."),
        DriveGram.Sync.HandoverBusyException b => L.T($"המחשב {L.Ltr(b.Other)} כבר ביקש את הכונן. נסה שוב בעוד דקה.", $"{b.Other} already asked for the drive. Try again in a minute."),
        InvalidOperationException { Message: var m } when m.StartsWith("The drive is read-only on this PC") =>
            L.T("הכונן במצב קריאה בלבד במחשב הזה: את השינוי הזה עושים במחשב שבו הוא פתוח לכתיבה (או בקש את הכונן מהתפריט).",
                "The drive is read-only on this PC: make this change on the PC where it is open for writing (or ask for the drive from the menu)."),
        IOException io when io.Message.StartsWith("The change could not be saved") =>
            L.T("השינוי לא נשמר בכונן (אולי החיבור נקטע). לא שונה דבר; נסה שוב.", "The change could not be saved to the drive (perhaps the connection dropped). Nothing was changed; try again."),
        UnauthorizedAccessException { Message: var m } when m.StartsWith("Only the drive's owner account") =>
            L.T("רק החשבון שבבעלותו הכונן יכול למחוק את הערוצים שלו.", "Only the account that owns the drive can delete its channels."),
        UnauthorizedAccessException => L.T("אין הרשאה לגשת לקובץ או לתיקייה הזו במחשב.", "No permission to access this file or folder on this PC."),
        NeedsUpdateException => L.T("הכונן הזה נכתב בגרסה חדשה יותר של DriveGram. התקן את הגרסה החדשה כדי לפתוח אותו.",
            "This drive was written by a newer version of DriveGram. Install it to open the drive."),
        DriveGram.Remote.NoDriveException => L.T("ההגדרה של הכונן הזה לא הושלמה.", "This drive's setup is not finished."),
        InvalidOperationException { Message: var m } when m.StartsWith("This PC never saw a verified copy") =>
            L.T("המחשב הזה אף פעם לא ראה עותק בדוק של רשומת הכונן, ולכן הוא לא יכול לאשר אותה. אשר אותה במחשב שכבר עבד עם הכונן.",
                "This PC never saw a verified copy of the drive's record, so it cannot confirm it. Confirm it on a PC that used the drive before."),
        InvalidOperationException { Message: "The drive's record is missing in Telegram." } =>
            L.T("רשומת הכונן חסרה בטלגרם.", "The drive's record is missing in Telegram."),
        InvalidOperationException { Message: var m } when m.StartsWith("The drive's journal names a different data channel") =>
            L.T("ערוץ היומן של הכונן מצביע על ערוץ קבצים אחר, ולכן לא נמחק דבר.", "The drive's journal points to a different data channel, so nothing was deleted."),
        InvalidOperationException { Message: var m } when m.StartsWith("This journal already holds") || m.StartsWith("This remote already holds") =>
            L.T("בערוצים האלה כבר יש כונן, ולכן לא נוצר דבר.", "These channels already hold a drive, so nothing was created."),
        IOException { Message: "Telegram did not return an invite link." } =>
            L.T("טלגרם לא החזיר קישור הצטרפות. נסה שוב.", "Telegram did not return an invite link. Try again."),
        TimeoutException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException
            or IOException { InnerException: System.Net.Sockets.SocketException } =>
            L.T("אין חיבור לשרתי טלגרם. בדוק את החיבור לאינטרנט ונסה שוב.", "Cannot reach Telegram. Check the internet connection and try again."),
        _ when ex.Message.Any(ch => ch is >= '\u0590' and <= '\u05FF') || ex.GetType().Namespace?.StartsWith("DriveGram") == true
            => ex.Message, // ours: already written for people
        _ when !L.Hebrew && OursInEnglish.Any(p => ex.Message.StartsWith(p, StringComparison.Ordinal)) => ex.Message, // ours (L.T), in English
        _ => L.T($"משהו השתבש ({ex.Message}). נסה שוב בעוד רגע; אם זה חוזר, הפרטים ביומן הפעילות (בתפריט של האייקון שליד השעון ← \"פתח את תיקיית יומן הפעילות\").",
                 $"Something went wrong ({ex.Message}). Try again in a moment; if it keeps happening, details are in the log (the menu of the icon next to the clock → \"Open the log folder\")."),
    };

    /// <summary>Messages DriveGram.Host builds with L.T on standard exception types: in English they have
    /// no Hebrew letters to recognize them by, but they are already written for people.</summary>
    static readonly string[] OursInEnglish =
    [
        "The item was already deleted for good",
        "This account is already in use by another DriveGram program",
        "The drive's record in Telegram shows a different layout",
    ];

    static string Wait(int seconds) => seconds switch
    {
        < 90 => L.N(seconds, "שנייה", "{0} שניות", "1 second", "{0} seconds"),
        < 5400 => L.N((seconds + 59) / 60, "דקה", "{0} דקות", "1 minute", "{0} minutes"),
        _ => L.T($"{seconds / 3600.0:0.#} שעות", $"{seconds / 3600.0:0.#} hours"),
    };
}
