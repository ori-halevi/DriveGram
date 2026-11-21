# DriveGram

כונן ב־"This PC" (למשל `T:`) שכל הקבצים שלו נשמרים מוצפנים בחשבון הטלגרם שלך.
עובדים איתו בסייר הקבצים כמו עם כל כונן אחר.

## דרישות

- Windows 10/11
- [WinFsp 2.1](https://winfsp.dev/rel/): `winget install WinFsp.WinFsp --version 2.1.25156`
- ‎.NET 10 SDK, לצורך בנייה
- `api_id` ו־`api_hash` מ־https://my.telegram.org (אפשר לקחת את אלה של Infinoom)

## הפעלה

```powershell
dotnet build -c Release
$dg = "src\DriveGram\bin\Release\net10.0-windows\drivegram.exe"

.drivegram login --env ..\..\FullStack\Infinoom\backend\.env   # פעם אחת לכל מחשב: מספר טלפון + קוד
.drivegram init                                                # יצירת הכונן וסיסמה (או חיבור מחשב נוסף לכונן קיים)
.drivegram mount T:                                            # הכונן מופיע ב-This PC. ‏Ctrl+C לניתוק
.drivegram status
.drivegram unmount                                             # ניתוק נקי מחלון אחר
```

**ניסיון בלי טלגרם:** מוסיפים `--folder <תיקייה>` ל־init, mount, status ו־unmount. הכונן נשמר אז בתיקייה מקומית.

## איך זה בנוי

| שכבה | קובץ | תפקיד |
|---|---|---|
| Windows | `src/DriveGram/DriveFileSystem.cs` | WinFsp: כל פעולת קובץ של Windows |
| תוכן | `Core/Engine/ContentEngine.cs` | חתיכות של 32MiB, שינוי מקומי לפי חתיכה, העלאה ברקע, בלם דיסק, התאוששות מקריסה |
| קריאה | `Core/Engine/ChunkReader.cs`, `BlockCache.cs` | קריאת טווחים בחלונות של 1MiB, קריאה מוקדמת, מטמון LRU |
| אינדקס | `Core/Index/IndexDb.cs`, `Ops.cs` | עץ התיקיות ב־SQLite. כל שינוי הוא פעולה (op) ביומן |
| סנכרון | `Core/Sync/SyncService.cs` | יומן שינויים מוצפן בטלגרם, "מפתח" כתיבה (lease), תמונות מצב, ניקוי |
| הצפנה | `Core/Crypto/*` | PBKDF2 → מפתח ראשי. AES-256-GCM במסגרות של 256KiB |
| טלגרם | `Core/Remote/TelegramRemote.cs`, `Governor.cs` | שני ערוצים פרטיים, העלאה מקבילית, ויסות AIMD כשטלגרם מגביל קצב, timeout לכל בקשה |

### כללים שאסור לשבור

- את `ChunkSize` ו־`FrameSize` של כונן לא משנים אף פעם. הם קבועים ב־vault, וכל חתיכה שנשמרה תלויה בהם.
- פעולת `ContentOp` נכתבת ליומן רק אחרי שכל החתיכות שלה כבר בטלגרם. כך מחשב אחר לעולם לא רואה קובץ שהתוכן שלו קיים רק במחשב אחד.
- חתיכה שאף קובץ לא מפנה אליה נמחקת רק אחרי 7 ימים. כך העלאה שנקטעה ממשיכה בלי לשלוח שוב את מה שכבר עלה (לקח מ־InfiDrive 6.3).

### מצב של כל מחשב

נשמר ב־`%LOCALAPPDATA%\DriveGram\telegram\`:
- `index.db`: האינדקס
- `staging\`: שינויים שעוד לא עלו
- `cache\`: מטמון
- `telegram.session`: החיבור לטלגרם, מוצפן ב־DPAPI
- `unlock.key`: רק אם בחרת "לזכור סיסמה". גם הוא מוצפן ב־DPAPI
- `logs\`

### שני מחשבים

- **לסירוגין:** נתמך. המחשב שמחזיק את ה־lease כותב, וכל מחשב אחר עולה לקריאה בלבד. בניתוק נקי ה־lease משתחרר. מחשב שקרס מאבד את ה־lease אחרי 15 דקות, או מיד עם `--take-over`.
- **בו זמנית:** פורמט היומן כבר מתאים לזה. מה שחסר הוא הטיפול בהתנגשויות (עותק "conflict" לקובץ ששונה בשני מחשבים).

## בדיקות

```
dotnet test
```

19 בדיקות:
- הצפנה וטווחי קריאה
- העתקה, קריאה וקומיט
- עריכה באמצע קובץ גדול (רק חתיכה אחת עולה מחדש)
- קיצוץ והגדלה של קובץ
- קבצים דלילים
- מניעת כפילויות
- קריסה לפני קומיט
- בלם דיסק בהעתקה ענקית
- שני מחשבים לסירוגין
- תמונת מצב
- lease ישן
- ניקוי
