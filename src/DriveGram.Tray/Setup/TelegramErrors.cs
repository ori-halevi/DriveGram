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
            "PHONE_CODE_EXPIRED" => L.T("תוקף הקוד פג. לחץ שוב על 'שלח קוד'.", "The code expired. Click 'Send code' again."),
            "PHONE_CODE_EMPTY" => L.T("לא הוקלד קוד.", "No code was entered."),
            "PASSWORD_HASH_INVALID" => L.T("סיסמת האימות הדו־שלבי שגויה.", "Wrong two-step verification password."),
            "AUTH_RESTART" => L.T("טלגרם ביקש להתחיל את ההתחברות מחדש. לחץ 'נסה שוב'.", "Telegram asked to restart the sign-in. Click 'Try again'."),
            "AUTH_TOKEN_EXPIRED" => L.T("תוקף קוד ה־QR פג. נוצר קוד חדש.", "The QR code expired. A new one was created."),
            "API_ID_INVALID" or "API_ID_PUBLISHED_FLOOD" => L.T("פרטי ה־API (api_id / api_hash) לא תקינים. חזור לשלב הקודם ובדוק אותם.",
                                                                "The API details (api_id / api_hash) are not valid. Go back and check them."),
            "SESSION_REVOKED" or "AUTH_KEY_UNREGISTERED" => L.T("החיבור בוטל ממכשיר אחר. התחבר שוב.", "The sign-in was revoked from another device. Sign in again."),
            _ => L.T($"טלגרם החזיר שגיאה: {r.Message}", $"Telegram error: {r.Message}"),
        },
        TimeoutException or System.Net.Sockets.SocketException or IOException =>
            L.T("אין חיבור לשרתי טלגרם. בדוק את החיבור לאינטרנט ונסה שוב.", "Cannot reach Telegram. Check the internet connection and try again."),
        _ => ex.Message,
    };

    static string Wait(int seconds) => seconds switch
    {
        < 90 => L.T($"{seconds} שניות", $"{seconds} seconds"),
        < 5400 => L.T($"{(seconds + 59) / 60} דקות", $"{(seconds + 59) / 60} minutes"),
        _ => L.T($"{seconds / 3600.0:0.#} שעות", $"{seconds / 3600.0:0.#} hours"),
    };
}
