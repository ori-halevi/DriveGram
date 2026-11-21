using TL;

namespace DriveGram.Tray.Setup;

/// <summary>Telegram errors in plain Hebrew, with what to do about them.</summary>
static class TelegramErrors
{
    public static string Hebrew(Exception ex) => ex switch
    {
        RpcException { Code: 420 } r => $"יותר מדי ניסיונות. טלגרם מבקש להמתין {Wait(r.X)} לפני שמנסים שוב.",
        RpcException r => r.Message switch
        {
            "PHONE_NUMBER_INVALID" => "מספר הטלפון לא תקין. כתוב אותו בפורמט בינלאומי, למשל ‎+972501234567‎.",
            "PHONE_NUMBER_BANNED" => "המספר הזה חסום בטלגרם.",
            "PHONE_NUMBER_UNOCCUPIED" => "אין חשבון טלגרם למספר הזה. צור חשבון באפליקציית טלגרם ונסה שוב.",
            "PHONE_CODE_INVALID" => "הקוד שגוי. ודא שאתה מקליד את הקוד האחרון שקיבלת (כל שליחה חדשה מבטלת את הקודם).",
            "PHONE_CODE_EXPIRED" => "תוקף הקוד פג. לחץ שוב על 'שלח קוד'.",
            "PHONE_CODE_EMPTY" => "לא הוקלד קוד.",
            "PASSWORD_HASH_INVALID" => "סיסמת האימות הדו־שלבי שגויה.",
            "AUTH_RESTART" => "טלגרם ביקש להתחיל את ההתחברות מחדש. לחץ 'נסה שוב'.",
            "AUTH_TOKEN_EXPIRED" => "תוקף קוד ה־QR פג. נוצר קוד חדש.",
            "API_ID_INVALID" or "API_ID_PUBLISHED_FLOOD" => "פרטי ה־API (api_id / api_hash) לא תקינים. חזור לשלב הקודם ובדוק אותם.",
            "SESSION_REVOKED" or "AUTH_KEY_UNREGISTERED" => "החיבור בוטל ממכשיר אחר. התחבר שוב.",
            _ => $"טלגרם החזיר שגיאה: {r.Message}",
        },
        TimeoutException or System.Net.Sockets.SocketException or IOException =>
            "אין חיבור לשרתי טלגרם. בדוק את החיבור לאינטרנט ונסה שוב.",
        _ => ex.Message,
    };

    static string Wait(int seconds) => seconds switch
    {
        < 90 => $"{seconds} שניות",
        < 5400 => $"{(seconds + 59) / 60} דקות",
        _ => $"{seconds / 3600.0:0.#} שעות",
    };
}
