using System.Diagnostics;
using System.Text.RegularExpressions;
using DriveGram.Host;
using DriveGram.Remote;
using DriveGram.Sync;
using QRCoder;
using Keys = DriveGram.Crypto.Keys;
using WrongPasswordException = DriveGram.Crypto.WrongPasswordException;

namespace DriveGram.Tray.Setup;

// ── 1. Welcome ──────────────────────────────────────────────────────────────
sealed class WelcomePage : WizardPage
{
    public override string Title => Ctx.IsNew && AppState.All().Count > 0
        ? L.T("כונן נוסף", "Another drive")
        : L.T("ברוך הבא ל־DriveGram", "Welcome to DriveGram");
    public override string Subtitle => L.T("כונן ב\"מחשב זה\" שהקבצים שלו נשמרים, מוצפנים, בחשבון הטלגרם שלך.",
        "A drive in This PC whose files are stored, encrypted, in your Telegram account.");
    public override string NextText => L.T("בוא נתחיל", "Let's start");

    readonly LinkLabel _language;

    /// <summary>Only while nothing was set up yet (coming back here after a drive was created or chosen,
    /// a restart would drop it).</summary>
    public override Task EnterAsync()
    {
        _language.Visible = Ctx.State.Config.Channels is null && Ctx.CreatedDriveId is null;
        return Task.CompletedTask;
    }

    public WelcomePage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        // the other language, before anything is typed: the wizard opens again in it (and the app follows)
        var language = _language = new LinkLabel
        {
            Text = L.Hebrew ? "English" : "עברית", AutoSize = true, Font = Theme.Small, LinkColor = Theme.Accent,
            RightToLeft = L.Hebrew ? RightToLeft.No : RightToLeft.Yes, Margin = new Padding(0, 0, 0, 10),
        };
        language.LinkClicked += (_, _) => (FindForm() as SetupWizard)?.SwitchLanguage();
        col.Controls.Add(language);
        bool another = ctx.IsNew && AppState.All().Count > 0;
        if (another)
            col.Controls.Add(Theme.Label(L.T(
                "כל כונן מקבל אות משלו וערוצים משלו בטלגרם. אפשר לשים אותו באותו חשבון של כונן קיים, או בחשבון אחר.",
                "Each drive gets its own letter and its own Telegram channels. It can live on the same account as an existing drive, or on another one."), maxWidth: 640));
        col.Controls.Add(Theme.Label(L.T("ההגדרה לוקחת כ־2 דקות:", "Setup takes about 2 minutes:"), Theme.Heading));
        string[] steps = ctx.UsesTelegram
            ? [L.T("פרטי API של טלגרם (מזהה חינמי שכל תוכנה צריכה)", "Telegram API details (a free ID every app needs)"),
               L.T("התחברות לחשבון: סריקת קוד QR מהטלפון, או קוד שטלגרם שולח", "Sign in: scan a QR code with your phone, or type a code Telegram sends you"),
               L.T("סיסמה לכונן: מצפינה הכל, ורק אתה יודע אותה", "A drive password: it encrypts everything, only you know it"),
               L.T("אות לכונן וסוג הכונן (DriveGram יופעל עם וינדוס; אפשר לכבות בתפריט של הסמל)", "A drive letter and the drive type (DriveGram starts with Windows; it can be turned off in the icon's menu)")]
            : [L.T("סיסמה לכונן (מצב ניסיון: הקבצים נשמרים בתיקייה מקומית)", "A drive password (trial mode: files are kept in a local folder)"),
               L.T("אות לכונן, סוג הכונן, והפעלה אוטומטית עם וינדוס", "A drive letter, the drive type, and starting with Windows")];
        for (int i = 0; i < steps.Length; i++)
            col.Controls.Add(Theme.Label($"  {i + 1}.  {steps[i]}", maxWidth: 640)); // one line each (a wrapped line lost its indent)

        if (ctx.UsesTelegram)
        {
            var tip = Theme.Card(Theme.Surface, Theme.Border);
            tip.Controls.Add(Theme.Label(L.T(
                "המלצה: השתמש בחשבון טלגרם ייעודי ל־DriveGram ולא בחשבון האישי. טלגרם יכול להגביל או לחסום חשבונות שמשמשים כאחסון כבד.",
                "Tip: use a dedicated Telegram account for DriveGram, not your personal one. Telegram may limit or ban accounts used as heavy storage."),
                Theme.Small, Theme.Muted, 620));
            col.Controls.Add(tip);
        }
        SetContent(col);
    }
}

// ── 2. API credentials ──────────────────────────────────────────────────────
sealed class ApiPage : WizardPage
{
    readonly TextBox _id = Theme.Input(180);
    readonly TextBox _hash = Theme.Input(380);
    readonly Label _status = Theme.Label("", Theme.Small);
    bool _entered, _autoSkip;

    public override string Title => L.T("פרטי API של טלגרם", "Telegram API details");
    public override string Subtitle => L.T("טלגרם דורש מכל תוכנה מזהה משלה. מקבלים אותו בחינם, פעם אחת.",
        "Telegram requires every app to have its own ID. It is free, and needed once.");
    public override bool Skip => _autoSkip;

    public ApiPage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        var link = new LinkLabel { Text = "my.telegram.org", AutoSize = true, Font = Theme.Body, LinkColor = Theme.Accent };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://my.telegram.org/apps") { UseShellExecute = true });
        var row = Theme.Row();
        row.Controls.AddRange([Theme.Label(L.T("1. היכנס עם מספר הטלפון של החשבון ל־", "1. Sign in with the account's phone number at ")), link]);
        col.Controls.Add(row);
        col.Controls.Add(Theme.Label(L.T("2. בחר API development tools, ומלא טופס קצר. השם לא משנה, למשל DriveGram.",
            "2. Choose API development tools and fill in the short form (any name, e.g. DriveGram).")));
        col.Controls.Add(Theme.Label(L.T("3. העתק לכאן את api_id ואת api_hash (הם נשמרים רק במחשב הזה):", "3. Copy api_id and api_hash here (they are kept only on this PC):")));

        col.Controls.Add(Theme.Label("api_id", Theme.Small, Theme.Muted));
        col.Controls.Add(_id);
        col.Controls.Add(Theme.Label("api_hash", Theme.Small, Theme.Muted));
        col.Controls.Add(_hash);

        var import = Theme.Secondary(L.T($"ייבוא מקובץ {L.Ltr(".env")}…", "Import from an .env file…"));
        import.Click += (_, _) => { try { ImportEnv(); } catch (Exception ex) { Ui.Error(TelegramErrors.Text(ex)); } };
        col.Controls.Add(import);
        col.Controls.Add(_status);
        SetContent(col);

        _id.Text = ctx.State.Config.ApiId == 0 ? "" : ctx.State.Config.ApiId.ToString();
        _hash.Text = ctx.State.Config.ApiHash;
        _id.TextChanged += (_, _) => UpdateState();
        _hash.TextChanged += (_, _) => UpdateState();
    }

    bool Valid => Regex.IsMatch(_id.Text.Trim(), @"^\d{3,10}$") && int.TryParse(_id.Text.Trim(), out _) && Regex.IsMatch(_hash.Text.Trim(), "^[0-9a-fA-F]{32}$");

    void UpdateState()
    {
        bool ok = Valid;
        _status.ForeColor = ok ? Theme.Success : Theme.Muted;
        _status.Text = ok ? L.T("✓ הפרטים נראים תקינים", "✓ These look valid") :
            (_id.Text.Length + _hash.Text.Length == 0 ? "" :
                L.T($"api_id הוא מספר, ו־api_hash הוא 32 תווים ({L.Ltr("0-9, a-f")}).", "api_id is a number; api_hash is 32 characters (0-9, a-f)."));
        Changed(ok);
    }

    void ImportEnv()
    {
        using var dlg = new OpenFileDialog { Title = L.T($"בחר קובץ {L.Ltr(".env")}", "Choose an .env file"), Filter = "env|*.env;.env|*.*|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var (key, value) in DriveGram.Host.EnvFile.Read(dlg.FileName))
        {
            if (key is "API_ID" or "TELEGRAM_API_ID") _id.Text = value;
            if (key is "API_HASH" or "TELEGRAM_API_HASH") _hash.Text = value;
        }
        if (!Valid)
        {
            _status.ForeColor = Theme.Error;
            _status.Text = L.T("לא נמצאו API_ID ו־API_HASH תקינים בקובץ.", "No valid API_ID and API_HASH in that file.");
        }
    }

    public override bool SkipsAhead => !_entered && Valid;
    public override string NextText => Ctx.ApiSkipped ? L.T("שמירה וחזרה להתחברות", "Save and back to sign-in") : base.NextText;

    public override Task EnterAsync()
    {
        UpdateState();
        _autoSkip = !_entered && Valid; // already configured: straight to sign-in (a link there leads back here)
        _entered = true;
        return Task.CompletedTask;
    }

    public override Task<bool> NextAsync()
    {
        int id = int.Parse(_id.Text.Trim());
        var hash = _hash.Text.Trim().ToLowerInvariant();
        var cfg = Ctx.State.Config;
        if (cfg.ApiId != id || cfg.ApiHash != hash)
        {
            cfg.ApiId = id;
            cfg.ApiHash = hash;
            Ctx.ResetClient(signOut: false);
        }
        return Task.FromResult(true);
    }
}

// ── 3. Telegram sign-in ─────────────────────────────────────────────────────
sealed class LoginPage : WizardPage
{
    public override string NextText => Ctx.AccountOnly ? L.T("המשך לכוננים בחשבון", "Continue to the account's drives") : base.NextText;
    // an existing drive that is signed in already (e.g. "choose another drive"): straight on, once
    bool _autoUsed, _wasSignedOut, _freshDone;
    public override bool AutoNext { get { bool a = !Ctx.IsNew && !Ctx.FreshSignIn && Ctx.User is not null && !_autoUsed; if (a) _autoUsed = true; return a; } }
    readonly LinkLabel _apiLink = new()
    {
        Text = L.T("משתמשים בפרטי ההתחברות לטלגרם (API) ששמרת קודם  ·  לשנות אותם", "Using the Telegram sign-in details (API) you saved before  ·  change them"),
        AutoSize = true, LinkColor = Theme.Accent, Font = Theme.Small, Margin = new Padding(0, 0, 0, 10), Visible = false,
    };
    readonly Panel _account = Theme.Card(Color.FromArgb(0xEC, 0xF7, 0xEE), Color.FromArgb(0xB7, 0xDF, 0xC0));
    readonly Label _accountText = Theme.Label("", Theme.Body, Theme.Text, 600);
    readonly FlowLayoutPanel _signIn = Theme.Column();
    readonly RadioButton _byQr = MethodButton(L.T("סריקת קוד QR (מומלץ)", "Scan a QR code (recommended)"), true);
    readonly RadioButton _byPhone = MethodButton(L.T("מספר טלפון וקוד", "Phone number and code"), false);

    readonly FlowLayoutPanel _qr = Theme.Row();
    // white whatever the look is: a QR code the phone's camera can read
    readonly PictureBox _qrImage = new() { Size = new Size(230, 230), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    readonly Label _qrState = Theme.Label(L.T("מכין קוד…", "Preparing a code…"), Theme.Small, Theme.Muted, 360);
    readonly Button _qrRetry = Theme.Secondary(L.T("נסה שוב", "Try again"));

    readonly FlowLayoutPanel _phone = Theme.Column();
    readonly TextBox _phoneBox = Theme.Input(240);
    readonly Button _sendCode = Theme.Primary(L.T("שלח קוד", "Send code"));
    readonly FlowLayoutPanel _codeRow = Theme.Row();
    readonly LinkLabel _changeNumber = new()
    {
        Text = L.T("מספר אחר / קוד חדש", "Another number / a new code"), AutoSize = true,
        Font = Theme.Small, LinkColor = Theme.Accent, Margin = new Padding(12, 10, 12, 0),
    };
    readonly TextBox _codeBox = Theme.Input(140);
    readonly Button _verify = Theme.Primary(L.T("אמת", "Verify"));

    readonly FlowLayoutPanel _twoFa = Theme.Column();
    readonly Label _twoFaHint = Theme.Label("", Theme.Small, Theme.Muted);
    readonly TextBox _twoFaBox = Theme.Input(260, password: true);
    readonly Button _twoFaOk = Theme.Primary(L.T("אישור", "OK"));

    readonly Label _error = Theme.Label("", Theme.Small, Theme.Error, 640);
    readonly Button _enterRetry = Theme.Secondary(L.T("נסה שוב", "Try again"));
    readonly FlowLayoutPanel _known = Theme.Column();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    CancellationTokenSource? _qrCts;
    Task? _qrTask;
    DateTime _qrExpires;
    TaskCompletionSource<string?>? _pwWait;
    int _pwAsks;

    public override string Title => L.T("התחברות לטלגרם", "Sign in to Telegram");
    public override string Subtitle => L.T("החשבון שבו יישמרו הקבצים. החיבור נשמר מוצפן, רק למשתמש שלך במחשב הזה.",
        "The account that will hold the files. The sign-in is stored encrypted, for your Windows user only.");
    public override int DemoVariants => 2;

    static RadioButton MethodButton(string text, bool on)
    {
        var rb = new RadioButton { Text = text, AutoSize = true, Checked = on, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4) };
        rb.FlatAppearance.CheckedBackColor = Color.FromArgb(0xE3, 0xF2, 0xFB);
        rb.FlatAppearance.BorderColor = Theme.Border;
        rb.Margin = new Padding(0);
        return rb;
    }

    public LoginPage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();

        _apiLink.LinkClicked += (_, _) => RequestPage(typeof(ApiPage));
        var switchAcc = new LinkLabel { Text = L.T("התחבר עם חשבון אחר", "Use a different account"), AutoSize = true, LinkColor = Theme.Accent, Font = Theme.Small };
        switchAcc.LinkClicked += async (_, _) => await SwitchAccountAsync();
        var accCol = Theme.Column();
        accCol.Controls.AddRange([_accountText, switchAcc]);
        _account.Controls.Add(accCol);
        _account.Visible = false;

        var methods = Theme.Row();
        foreach (var rb in new[] { _byQr, _byPhone })
            rb.CheckedChanged += async (_, _) => { if (rb.Checked) await ModeChangedAsync(); };
        methods.Controls.AddRange([_byQr, _byPhone]);
        methods.Margin = new Padding(0, 0, 0, 14);

        var qrSide = Theme.Column();
        qrSide.Padding = new Padding(18, 0, 18, 0);
        qrSide.Controls.Add(Theme.Label(L.T("בטלפון:", "On your phone:"), Theme.Heading));
        qrSide.Controls.Add(Theme.Label(L.T("1. פתח את טלגרם", "1. Open Telegram"), maxWidth: 360));
        qrSide.Controls.Add(Theme.Label(L.T("2. הגדרות ← מכשירים ← קישור מכשיר שולחני", "2. Settings → Devices → Link Desktop Device"), maxWidth: 360));
        qrSide.Controls.Add(Theme.Label(L.T("3. כוון את המצלמה לקוד", "3. Point the camera at this code"), maxWidth: 360));
        qrSide.Controls.Add(_qrState);
        _qrRetry.Visible = false;
        _qrRetry.Click += (_, _) => StartQr();
        qrSide.Controls.Add(_qrRetry);
        var frame = Theme.Card(Slot.Card, Slot.Border);
        frame.Padding = new Padding(6);
        frame.Controls.Add(_qrImage);
        _qr.Controls.AddRange([frame, qrSide]);

        _phoneBox.PlaceholderText = "+972501234567";
        var phoneRow = Theme.Row();
        phoneRow.Controls.AddRange([_phoneBox, _sendCode]);
        _codeBox.PlaceholderText = "12345";
        _codeRow.Controls.AddRange([_codeBox, _verify, _changeNumber]);
        _changeNumber.LinkClicked += (_, _) =>
        {
            Ctx.ResetClient(signOut: true); // a clean sign-in attempt (the old code is void anyway)
            _phoneBox.ReadOnly = false;
            _sendCode.Visible = true;
            _sendCode.Enabled = true;
            _codeRow.Visible = false;
            _error.Text = "";
            _phoneBox.Focus();
        };
        _codeRow.Visible = false;
        _phone.Controls.Add(Theme.Label(L.T("מספר הטלפון של החשבון, בפורמט בינלאומי:", "The account's phone number, in international format:"), Theme.Small, Theme.Muted));
        _phone.Controls.Add(phoneRow);
        _phone.Controls.Add(Theme.Label(L.T(
            "הקוד נשלח לאפליקציית טלגרם עצמה, לצ'אט בשם \"Telegram\" עם ✓ כחול (בטלפון או במחשב). בדרך כלל הוא לא מגיע ב־SMS. כל שליחה חדשה מבטלת את הקוד הקודם.",
            "The code arrives inside the Telegram app, in the chat named \"Telegram\" with a blue ✓ (on your phone or computer), usually not by SMS. Each new code cancels the previous one."),
            Theme.Small, Theme.Muted, 620));
        _phone.Controls.Add(_codeRow);
        _phone.Visible = false;
        _sendCode.Click += async (_, _) => await SendCodeAsync();
        _verify.Click += async (_, _) => await VerifyCodeAsync();

        var twoFaRow = Theme.Row();
        twoFaRow.Controls.AddRange([_twoFaBox, _twoFaOk]);
        _twoFa.Controls.Add(Theme.Label(L.T("לחשבון יש אימות דו־שלבי. הקלד את הסיסמה של האימות בטלגרם (לא את סיסמת הכונן):", "This account has two-step verification. Enter its Telegram password (not the drive password):"), Theme.Heading, Theme.Text, 640));
        _twoFa.Controls.AddRange([_twoFaHint, twoFaRow]);
        _twoFa.Visible = false;
        _twoFaOk.Click += async (_, _) => await SubmitTwoFaAsync();
        // Enter in these boxes does their step (the wizard's "Next" stays off until the sign-in is done)
        EnterClicks(_phoneBox, _sendCode);
        EnterClicks(_codeBox, _verify);
        EnterClicks(_twoFaBox, _twoFaOk);

        _signIn.Controls.AddRange([_apiLink, methods, _qr, _phone]);
        _known.Visible = false;
        col.Controls.AddRange([_account, _known, _signIn, _twoFa, _error, _enterRetry]);
        _enterRetry.Visible = false;
        _enterRetry.Click += async (_, _) => { Ctx.ResetClient(signOut: false); await EnterAsync(); };
        SetContent(col);

        _tick.Tick += (_, _) =>
        {
            var left = (int)Math.Ceiling((_qrExpires - DateTime.UtcNow).TotalSeconds);
            if (_qrTask is { IsCompleted: false } && _qrImage.Image is not null && left > 0)
                _qrState.Text = L.N(left, "ממתין לסריקה… הקוד מתחדש בעוד שנייה.", "ממתין לסריקה… הקוד מתחדש בעוד {0} שניות.",
                    "Waiting for the scan… a new code in 1 second.", "Waiting for the scan… a new code in {0} seconds.");
        };
        Ctx.AskPassword = AskPasswordFromQr;
        Changed(false);
    }

    static void EnterClicks(TextBox box, Button button)
    {
        box.PreviewKeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Enter) e.IsInputKey = true; };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != System.Windows.Forms.Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            if (button.Visible && button.Enabled) button.PerformClick();
        };
    }

    // ── flow ─────────────────────────────────────────────────────────────────
    public override async Task EnterAsync()
    {
        _error.Text = "";
        _enterRetry.Visible = false;
        _apiLink.Visible = Ctx.ApiSkipped;
        if (Ctx.FreshSignIn && !_freshDone)
        {
            // "sign in with another account": no current account shown as signed in
            _freshDone = true;
            await SwitchAccountAsync(ask: false);
            return;
        }
        if (Ctx.User is not null) { ShowSignedIn(Ctx.User); return; }
        try
        {
            var user = await Task.Run(() => TelegramRemote.ResumeAsync(Ctx.GetClient()));
            ShowSignedIn(user);
        }
        catch (NotSignedInException) when (Ctx.GetClient().UserId != 0)
        {
            // a saved sign-in that Telegram revoked (e.g. "terminate all sessions"): start from a clean one,
            // or every new attempt would run on the dead session again
            _revoked = Ctx.State.Config.AccountKey;
            Ctx.ResetClient(signOut: true);
            _wasSignedOut = true;
            _account.Visible = false;
            _signIn.Visible = true;
            ShowKnownAccounts();
            await ModeChangedAsync();
        }
        catch (NotSignedInException)
        {
            _wasSignedOut = true;
            _account.Visible = false;
            _signIn.Visible = true;
            ShowKnownAccounts();
            await ModeChangedAsync();
        }
        catch (Exception ex)
        {
            _error.Text = TelegramErrors.Text(ex);
            _enterRetry.Visible = true;
        }
    }

    /// <summary>Accounts already signed in on this PC: one click instead of a new sign-in.</summary>
    string? _revoked;

    void ShowKnownAccounts()
    {
        _known.Controls.Clear();
        var accounts = Ctx.AccountOnly ? new List<AccountInfo>() : TelegramAccounts.Known().Where(a => a.Key != Ctx.State.Config.AccountKey && a.Key != _revoked).ToList();
        _known.Visible = accounts.Count > 0;
        if (accounts.Count == 0) return;
        _known.Controls.Add(Theme.Label(L.T("חשבון שכבר מחובר במחשב הזה (בלי התחברות נוספת):", "An account already signed in on this PC (no new sign-in):"), Theme.Heading));
        var row = Theme.Row();
        foreach (var a in accounts)
        {
            var b = Theme.Secondary(L.Name(a.Name));
            b.Click += async (_, _) =>
            {
                LeavePage();
                if (_qrTask is not null) { try { await _qrTask; } catch { } }
                Ctx.UseAccount(a);
                await EnterAsync();
            };
            row.Controls.Add(b);
        }
        _known.Controls.Add(row);
        _known.Controls.Add(Theme.Label(L.T("או התחבר לחשבון אחר:", "Or sign in to another account:"), Theme.Small, Theme.Muted));
    }

    public override void LeavePage()
    {
        _qrCts?.Cancel();
        _tick.Stop();
        _pwWait?.TrySetResult(null);
        _pwWait = null; // answered: a later phone sign-in's 2FA must not be taken for the QR flow's
    }

    public override Task<bool> NextAsync() => Task.FromResult(Ctx.User is not null && CanGoNext);

    void ShowSignedIn(TL.User user)
    {
        if (_wasSignedOut)
        {
            // the user just signed in here: it is shared at once, and the page has nothing more to ask
            _wasSignedOut = false;
            Ctx.SignedInHere = true;
            if (!Ctx.IsNew || Ctx.AccountOnly) BeginInvoke(RequestNext);
        }
        Ctx.User = user;
        _qrCts?.Cancel();
        _tick.Stop();
        _signIn.Visible = false;
        _twoFa.Visible = false;
        _error.Text = "";
        var name = $"{user.first_name} {user.last_name}".Trim();
        var handle = string.IsNullOrEmpty(user.username) ? "" : $"  ·  {L.Ltr("@" + user.username)}";
        var phone = string.IsNullOrEmpty(user.phone) ? "" : $"  ·  ‎+{user.phone}‎";
        _accountText.Text = L.T($"✓  מחובר כ־{L.Name(name)}{handle}{phone}", $"✓  Signed in as {name}{handle}{phone}");
        _account.Visible = true;
        _known.Visible = false;

        Ctx.State.Config.AccountId = user.id;
        Ctx.State.Config.AccountName = string.IsNullOrEmpty(user.username) ? name : "@" + user.username;
        Changed(true);
    }

    void ShowError(Exception ex) => _error.Text = TelegramErrors.Text(ex);

    public override void Demo(int variant)
    {
        _account.Visible = false;
        _signIn.Visible = true;
        if (variant == 0)
        {
            _qr.Visible = true;
            _phone.Visible = false;
            ShowQr("tg://login?token=AQTllwFrRdZaXmvADMPwhnSW5XRrCpa0PEE_rdEpvHIrMA-DEMO");
            _qrState.Text = L.T("ממתין לסריקה… הקוד מתחדש בעוד 23 שניות.", "Waiting for the scan… a new code in 23 seconds.");
        }
        else
        {
            _byPhone.Checked = true;
            _qrCts?.Cancel();
            _qr.Visible = false;
            _phone.Visible = true;
            _phoneBox.Text = "+972501234567";
            _codeRow.Visible = true;
            _codeBox.Text = "12345";
            ShowError(new TL.RpcException(400, "PHONE_CODE_INVALID"));
        }
    }

    async Task ModeChangedAsync()
    {
        if (Ctx.User is not null) return;
        _error.Text = "";
        _twoFa.Visible = false;
        if (_byQr.Checked)
        {
            _phone.Visible = false;
            _qr.Visible = true;
            StartQr();
        }
        else
        {
            _qrCts?.Cancel();
            _tick.Stop();
            _pwWait?.TrySetResult(null); // a QR sign-in waiting for its 2FA password must not block the switch
            _pwWait = null;
            if (_qrTask is not null) { try { await _qrTask; } catch { } }
            _qr.Visible = false;
            _phone.Visible = true;
            _phoneBox.Focus();
        }
    }

    void StartQr()
    {
        if (_qrTask is { IsCompleted: false }) return;
        _qrRetry.Visible = false;
        _error.Text = "";
        _qrState.Text = L.T("מכין קוד…", "Preparing a code…");
        _qrCts = new CancellationTokenSource();
        _pwAsks = 0;
        _tick.Start();
        _qrTask = RunQrAsync(_qrCts.Token, retryAfterRestart: true);
    }

    async Task RunQrAsync(CancellationToken ct, bool retryAfterRestart)
    {
        try
        {
            var client = Ctx.GetClient();
            var user = await Task.Run(() => client.LoginWithQRCode((url, expires) => BeginInvoke(() => ShowQr(url, expires)), logoutFirst: false, ct: ct), ct);
            await Task.Run(() => Ctx.AdoptSignIn(user));
            ShowSignedIn(user);
        }
        catch (OperationCanceledException) { }
        catch (TL.RpcException e) when (e.Message == "AUTH_RESTART" && retryAfterRestart)
        {
            // the saved session is in a half-finished sign-in state: start from a clean one
            Ctx.ResetClient(signOut: true);
            await RunQrAsync(ct, retryAfterRestart: false);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Log.Warn($"QR sign-in: {ex.Message}");
            ShowError(ex);
            _qrState.Text = L.T("הקוד לא פעיל.", "The code is not active.");
            _qrImage.Image = null;
            _qrRetry.Visible = true;
        }
    }

    /// <param name="expires">when Telegram replaces this code (the library waits until then by this PC's
    /// clock, so the countdown matches the real refresh)</param>
    void ShowQr(string url, DateTime? expires = null)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var ms = new MemoryStream(new PngByteQRCode(data).GetGraphic(8));
        using var img = Image.FromStream(ms);
        var old = _qrImage.Image;
        _qrImage.Image = new Bitmap(img);
        old?.Dispose();
        var until = expires is { } e ? (e.Kind == DateTimeKind.Local ? e.ToUniversalTime() : e) : DateTime.UtcNow.AddSeconds(30);
        var left = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
        if (left is < 1 or > 300) { until = DateTime.UtcNow.AddSeconds(30); left = 30; } // never a strange countdown
        _qrExpires = until;
        _qrState.Text = L.N(left, "ממתין לסריקה… הקוד מתחדש בעוד שנייה.", "ממתין לסריקה… הקוד מתחדש בעוד {0} שניות.",
            "Waiting for the scan… a new code in 1 second.", "Waiting for the scan… a new code in {0} seconds.");
    }

    /// <summary>The library asks for the 2FA password on a worker thread during QR sign-in.</summary>
    string? AskPasswordFromQr()
    {
        var wait = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () =>
        {
            _pwWait = wait;
            _qr.Visible = false;
            await ShowTwoFaAsync(_pwAsks++ > 0 ? L.T("הסיסמה שגויה, נסה שוב.", "Wrong password, try again.") : null);
        });
        var pw = wait.Task.GetAwaiter().GetResult();
        return pw ?? throw new OperationCanceledException();
    }

    async Task ShowTwoFaAsync(string? error)
    {
        _known.Visible = false; // mid sign-in: not the place to pick another account
        _twoFa.Visible = true;
        _twoFaBox.Text = "";
        _error.Text = error ?? "";
        try
        {
            var info = await TL.SchemaExtensions.Account_GetPassword(Ctx.GetClient());
            _twoFaHint.Text = string.IsNullOrEmpty(info.hint) ? "" : L.T($"רמז: {info.hint}", $"Hint: {info.hint}");
        }
        catch { _twoFaHint.Text = ""; }
        _twoFaBox.Focus();
    }

    async Task SubmitTwoFaAsync()
    {
        var pw = _twoFaBox.Text;
        if (pw.Length == 0) return;
        if (_pwWait is { } wait) // the QR flow is waiting on us
        {
            _pwWait = null;
            _twoFa.Visible = false;
            _qr.Visible = true;
            _qrState.Text = L.T("מאמת…", "Verifying…");
            wait.TrySetResult(pw);
            return;
        }
        await RunLoginStepAsync(_twoFaOk, pw); // phone flow
    }

    async Task SendCodeAsync()
    {
        var phone = new string(_phoneBox.Text.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (phone.Length < 8)
        {
            _error.Text = L.T("הקלד מספר טלפון מלא, למשל ‎+972501234567‎.", "Enter the full phone number, e.g. +972501234567.");
            return;
        }
        if (!phone.StartsWith('+') && phone.StartsWith('0'))
        {
            if (!L.Hebrew)
            {
                _error.Text = L.T("הקלד את המספר עם קידומת המדינה, למשל ‎+972501234567‎.", "Enter the number with its country code, e.g. +44 7700 900123.");
                return;
            }
            phone = "+972" + phone[1..]; // local Israeli format
        }
        await RunLoginStepAsync(_sendCode, phone);
    }

    Task VerifyCodeAsync() => RunLoginStepAsync(_verify, new string(_codeBox.Text.Where(char.IsDigit).ToArray()));

    /// <summary>One step of client.Login(): phone → code → (2FA password) → done.</summary>
    async Task RunLoginStepAsync(Button button, string value)
    {
        _error.Text = "";
        button.Enabled = false;
        _changeNumber.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var client = Ctx.GetClient();
            var next = await Task.Run(() => client.Login(value));
            switch (next)
            {
                case null:
                    await Task.Run(() => Ctx.AdoptSignIn(client.User!));
                    ShowSignedIn(client.User!);
                    break;
                case "verification_code":
                    if (button == _verify) _error.Text = L.T("הקוד שגוי, נסה שוב.", "Wrong code, try again.");
                    _phoneBox.ReadOnly = true;   // the code belongs to this number; changing it needs a new start
                    _sendCode.Visible = false;
                    _codeRow.Visible = true;
                    _codeBox.Text = "";
                    _codeBox.Focus();
                    break;
                case "password":
                    _codeRow.Visible = false;
                    await ShowTwoFaAsync(_twoFa.Visible ? L.T("הסיסמה שגויה, נסה שוב.", "Wrong password, try again.") : null);
                    break;
                case "name":
                    _error.Text = L.T("אין חשבון טלגרם למספר הזה. צור חשבון באפליקציית טלגרם ונסה שוב.",
                                      "There is no Telegram account for this number. Create one in the Telegram app and try again.");
                    break;
                default:
                    _error.Text = L.T($"טלגרם ביקש '{next}', שעדיין לא נתמך כאן.", $"Telegram asked for '{next}', which is not supported here yet.");
                    break;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            button.Enabled = true;
            _changeNumber.Enabled = true;
            UseWaitCursor = false;
        }
    }

    async Task SwitchAccountAsync(bool ask = true)
    {
        if (ask && Ui.Ask(L.T("להתנתק מהחשבון הנוכחי ולהתחבר עם חשבון אחר?", "Sign out of this account and use a different one?"),
                def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _revoked = Ctx.State.Config.AccountKey; // signing out now: not offered again on this page
        await Task.Run(() => Ctx.ResetClient(signOut: true));
        _wasSignedOut = true;
        Ctx.State.Config.Channels = null; // the drive lives on the other account
        Ctx.State.Config.AccountId = 0;
        Ctx.State.Config.AccountName = "";
        Changed(false);
        await EnterAsync();
    }
}

// ── 4. The drive ──────────────────────────────────────────────────────────────
/// <summary>
/// Which drive this profile is: one of the drives the account can open (its own, or ones it is a
/// backup admin of), or a new one. Several drives may live on one account; they are told apart by the
/// identity in each drive's control record, not by channel names.
/// </summary>
sealed class DrivePage : WizardPage
{
    enum Mode { Checking, Connected, Connect, Create }

    readonly Button _driveRetry = Theme.Secondary(L.T("נסה שוב", "Try again"));
    readonly Label _checking = Theme.Label(L.T("מחפש כונני DriveGram בחשבון…", "Looking for DriveGram drives on the account…"), Theme.Body, Theme.Muted);
    readonly FlowLayoutPanel _connected = Theme.Column();
    readonly FlowLayoutPanel _choices = Theme.Column();
    readonly FlowLayoutPanel _form = Theme.Column();
    readonly Panel _vanished = Theme.Card(Color.FromArgb(0xFD, 0xEC, 0xEA), Color.FromArgb(0xE5, 0x9A, 0x92));
    readonly CheckBox _vanishedOk = new() { AutoSize = true, Text = L.T("הבנתי. אפשר להמשיך.", "I understand. Continue.") };
    readonly Label _intro = Theme.Label("", Theme.Body, Theme.Text, 640);
    readonly Label _nameLabel = Theme.Label(L.T("שם הכונן:", "Drive name:"), Theme.Small, Theme.Muted);
    readonly TextBox _name = Theme.Input(290);
    readonly TextBox _pw = Theme.Input(290, password: true);
    readonly Label _pwLabel = Theme.Label(L.T("סיסמה:", "Password:"), Theme.Small, Theme.Muted);
    readonly Label _confirmLabel = Theme.Label(L.T("הקלד שוב:", "Repeat it:"), Theme.Small, Theme.Muted);
    readonly TextBox _pw2 = Theme.Input(290, password: true);
    readonly CheckBox _show = new ToggleSwitch(L.T("הצג סיסמה", "Show password"));
    readonly FlowLayoutPanel _strength = Theme.Row();
    readonly Panel _meter = new() { Size = new Size(290, 6), BackColor = Theme.Border, Margin = new Padding(0, 8, 0, 0) };
    readonly Panel _meterFill = new() { Dock = L.Rtl ? DockStyle.Right : DockStyle.Left, Width = 0 };
    readonly Label _meterText = Theme.Label("", Theme.Small, Theme.Muted);
    readonly Panel _warning = Theme.Card(Theme.WarnBack, Theme.WarnBorder);
    readonly CheckBox _saved = new() { Text = L.T("שמרתי את הסיסמה במקום בטוח", "I saved the password somewhere safe"), AutoSize = true };
    readonly CheckBox _remember = new ToggleSwitch(L.T("לזכור את הסיסמה במחשב הזה", "Remember the password on this PC")) { Checked = true };
    // the details of "remember" sit behind an (i): one glance at the page stays simple
    readonly Label _rememberInfo = new() { Text = "ⓘ", AutoSize = true, Font = new Font(Theme.Body.FontFamily, 13f), ForeColor = Theme.Accent, Cursor = Cursors.Help, Margin = new Padding(4, 3, 4, 0) };
    readonly ToolTip _tips = new() { AutoPopDelay = 30000, InitialDelay = 200 };
    static readonly string RememberText = L.T(
        "הסיסמה נשמרת מוצפנת, רק למשתמש שלך בווינדוס, כדי שהכונן ייפתח לבד בלי לשאול. מי שנכנס למשתמש הזה יוכל לפתוח את הכונן בלי סיסמה: אם אנשים אחרים משתמשים באותו משתמש, עדיף לכבות (ואז הסיסמה תתבקש בכל חיבור של הכונן).",
        "The password is kept encrypted, for your Windows user only, so the drive opens by itself without asking. Anyone who can use that Windows user can open the drive without the password: if others share that Windows user, better turn it off (the password is then asked each time the drive connects).");
    readonly FlowLayoutPanel _settingsRow = Theme.Row();
    // what the last page used to ask (a new Telegram drive): the drive type, and starting with Windows
    // each drive's own type and "remember" (a new drive's here; an existing drive's in its card)
    readonly ComboBox _newType = TypeBox();
    readonly FlowLayoutPanel _newRememberRow = Theme.Row();
    readonly Dictionary<long, ComboBox> _types = [];
    readonly Dictionary<long, CheckBox> _remembers = [];
    readonly CheckBox _autostart = new ToggleSwitch(L.T("הפעל את DriveGram אוטומטית כשנכנסים לווינדוס", "Start DriveGram automatically when I sign in to Windows")) { Checked = true };
    bool _askAutostart;
    readonly Label _error = Theme.Label("", Theme.Small, Theme.Error);
    Mode _mode = Mode.Checking;
    bool _wasVanished;
    DriveCandidate? _selected;   // null while "a new drive" is chosen
    bool _noneChosen;            // several-drives list with nothing ticked
    readonly List<CheckBox> _ticks = [];
    CheckBox? _newTick;
    List<NextDrive> _enqueued = [];
    ControlState? _ctrl;

    /// <summary>Several drives can be ticked (a new drive in the usual sitting, or "another drive" without
    /// a drive chosen yet); changing an existing drive's settings, or a drive already chosen, is one drive.</summary>
    bool Multi => Ctx.IsNew && Ctx.UsesTelegram && Ctx.Current is not { Candidate: not null } and not { CreateNew: true };

    /// <summary>This page also chooses the letter, name, drive type and autostart, and is the last step
    /// (a queued drive got them from the page that queued it).</summary>
    bool ChoosesAll => Ctx.DrivePageFinishes && Ctx.Current is not { Candidate: not null };

    public override string NextText => !Ctx.IsNew
        ? _mode == Mode.Create ? L.T("יצירת הכונן וחיבורו", "Create and connect the drive")
          : Ctx.OriginalChannels is { } o && (_selected?.Channels ?? Ctx.State.Config.Channels) is { } t && t.JournalId != o.JournalId ? L.T("מעבר לכונן הזה", "Switch to this drive")
          : L.T("חיבור הכונן", "Connect the drive")
        : !ChoosesAll || _mode is not (Mode.Connect or Mode.Create) ? base.NextText
        : Ticked.Count + (_newTick?.Checked == true ? 1 : 0) > 1 ? L.T("סיום וחיבור הכוננים", "Finish and connect the drives")
        : L.T("סיום וחיבור הכונן", "Finish and connect");

    // with check boxes: each ticked drive's password right under it (empty = the same as the one before),
    // and the new drive's fields right under "a new drive", shown only while it is ticked
    readonly Dictionary<long, TextBox> _otherBoxes = [];
    readonly Dictionary<long, Label> _sameHints = [];
    readonly Dictionary<CheckBox, Control> _tickBoxes = [];  // tick → its card (password, letter, name)
    readonly Dictionary<long, ComboBox> _letters = [];
    readonly Dictionary<long, TextBox> _labels = [];
    readonly ComboBox _newLetter = LetterBox();
    readonly FlowLayoutPanel _newLetterRow = Theme.Row();
    readonly Dictionary<long, string> _otherVerified = [];
    readonly FlowLayoutPanel _createGroup = Theme.Column();
    readonly Label _pickHint = Theme.Label(L.T("סמן כונן אחד או יותר.", "Tick one drive or more."), Theme.Small, Theme.Muted);
    bool _autoTried, _autoDone;
    string? _typedNewName; // the new drive's name as typed, kept while another drive is shown
    TelegramChannels? _createdChannels; // this run already created the new drive's channels (a retry reuses them)
    string? _createdDriveId, _createdFor; // ...on this account
    public override bool Skip => _autoDone;
    bool _connectedAutoUsed, _committed;
    // an existing drive already set (password remembered): nothing to ask, so on by itself, once
    public override bool AutoNext { get { bool a = !Ctx.IsNew && _mode == Mode.Connected && !_connectedAutoUsed; if (a) _connectedAutoUsed = true; return a; } }
    // after this PC switched to another drive (its old local data is reset), there is no going back
    public override bool CanGoBack => !_committed;

    // a new drive's piece size (fixed for the drive's life)
    readonly RadioButton _chunk32 = new() { Text = L.T("32 MB (מומלץ)", "32 MB (recommended)"), AutoSize = true, Checked = true, Margin = new Padding(0, 2, 0, 2) };
    readonly RadioButton _chunk64 = new() { Text = "64 MB", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    readonly FlowLayoutPanel _chunkBox = Theme.Column();
    IRemote? _remote;

    public override string Title => L.T("הכונן", "The drive");
    public override string Subtitle => _mode switch
    {
        Mode.Create or Mode.Connect when _wasVanished => L.T("הכונן הקודם לא נמצא בחשבון הזה.", "The previous drive was not found on this account."),
        Mode.Create => L.T("כונן חדש, וסיסמה שמצפינה אותו.", "A new drive, and the password that encrypts it."),
        Mode.Connect => L.T("חבר את המחשב הזה לכונן קיים.", "Connect this PC to an existing drive."),
        Mode.Connected => L.T("הכל מוכן כאן.", "All set here."),
        _ => L.T("רגע אחד…", "One moment…"),
    };
    public override int DemoVariants => 5;

    public DrivePage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        _connected.Controls.Add(Theme.Label(L.T("✓  המחשב הזה כבר משויך לכונן, והסיסמה שמורה בו.", "✓  This PC is already paired with the drive, and its password is remembered."), Theme.Heading, Theme.Success));
        _connected.Controls.Add(Theme.Label(L.T("הכונן מתחבר עכשיו. החלון ייסגר, ותופיע הודעה ליד השעון.", "The drive connects now. This window closes, and a notice appears by the clock."), Theme.Body, Theme.Muted));
        _connected.Visible = false;

        var vcol = Theme.Column();
        vcol.Controls.Add(Theme.Label(L.T(
            "⚠  הכונן שהיה מחובר כאן לא נמצא בחשבון הטלגרם הזה. אם התחברת בטעות לחשבון אחר, חזור אחורה והתחבר לחשבון של הכונן: הקבצים שלו לא נפגעו. אחרת, ייתכן שהערוצים שלו נמחקו או שהחשבון הוסר מהם. אפשר לבחור כונן אחר או ליצור כונן חדש וריק.",
            "⚠  The drive connected here was not found on this Telegram account. If you signed in to another account by mistake, go back and sign in to the drive's account: its files are not affected. Otherwise its channels may have been deleted, or the account removed from them. You can choose another drive or create a new, empty one."),
            Theme.Small, Theme.Text, 620));
        vcol.Controls.Add(_vanishedOk);
        _vanished.Controls.Add(vcol);
        _vanished.Visible = false;
        _choices.Visible = false;

        _meter.Controls.Add(_meterFill);
        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Margin = new Padding(0) };
        grid.Controls.Add(_pwLabel, 0, 0);
        grid.Controls.Add(_confirmLabel, 1, 0);
        grid.Controls.Add(_pw, 0, 1);
        grid.Controls.Add(_pw2, 1, 1);
        _pw.Margin = new Padding(0, 0, 0, 6);
        _pw2.Margin = new Padding(16, 0, 16, 6);
        _confirmLabel.Margin = new Padding(16, 0, 16, 4);
        _meterText.Margin = new Padding(10, 0, 10, 0);
        _strength.Controls.AddRange([_meter, _meterText]);
        _warning.Controls.Add(Theme.Label(L.T(
            "⚠  אי אפשר לשחזר את הסיסמה, גם לא DriveGram. בלעדיה (ובלי מפתח שחזור) אף אחד, גם לא אתה, לא יוכל לקרוא את הקבצים, אפילו עם גישה מלאה לחשבון הטלגרם. שמור אותה במנהל סיסמאות או רשום אותה. אחרי ההגדרה מומלץ ליצור מפתח שחזור: בתפריט של הכונן ← \"סיסמה ומפתח שחזור…\".",
            "⚠  The password cannot be recovered, not even by DriveGram. Without it (or a recovery key) nobody, including you, can read the files, even with full access to the Telegram account. Keep it in a password manager or write it down. After setup, creating a recovery key is recommended: the drive's menu → \"Password and recovery key…\"."),
            Theme.Small, Theme.Text, 640));
        _name.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _name.Font = Theme.Body;
        var chunkTitle = Theme.Label(L.T("גודל החתיכות בטלגרם:", "Piece size in Telegram:"), Theme.Small, Theme.Muted);
        chunkTitle.Margin = new Padding(0, 8, 0, 2);
        var chunkRow = Theme.Row();
        var chunkWhy = new LinkLabel { Text = L.T("מה ההבדל?", "What is the difference?"), AutoSize = true, Font = Theme.Body, LinkColor = Theme.Accent, Margin = new Padding(12, 4, 12, 2) };
        chunkWhy.LinkClicked += (_, _) => Ui.Info(L.T(
            "כל קובץ נשמר בטלגרם בחתיכות בגודל קבוע. המהירות כמעט זהה בשני הגדלים: כל חתיכה עולה בחלקים קטנים, כמה במקביל.\n\n" +
            "32 MB (מומלץ): מתאים לכל שימוש, ובמיוחד כשעורכים קבצים גדולים בתוך הכונן (וידאו, ‏Outlook, מכונות וירטואליות): שינוי קטן מוריד ומעלה מחדש עד 32MB. פחות זיכרון ופחות מקום זמני במחשב.\n\n" +
            "64 MB: מתאים לכונן שרק מעתיקים אליו קבצים גדולים מאוד ולא עורכים אותם (ארכיון, גיבויים): חצי מכמות הפניות לטלגרם. אבל כל שינוי בקובץ קיים מוריד ומעלה מחדש עד 64MB, ודורש יותר זיכרון ומקום זמני.\n\n" +
            "אי אפשר לשנות את זה אחרי שהכונן נוצר.",
            "Every file is stored in Telegram in pieces of a fixed size. Speed is about the same with both: each piece is uploaded in small parts, several at once.\n\n" +
            "32 MB (recommended): suits every use, especially editing large files inside the drive (video, Outlook, virtual machines): a small change downloads and uploads again at most 32 MB. Less memory and less temporary space on this PC.\n\n" +
            "64 MB: suits a drive you only copy very large files into and do not edit (an archive, backups): half as many requests to Telegram. But any change to an existing file downloads and uploads again up to 64 MB, and needs more memory and temporary space.\n\n" +
            "It cannot be changed after the drive is created."));
        chunkRow.Controls.AddRange([_chunk32, _chunk64, chunkWhy]);
        _chunkBox.Controls.AddRange([chunkTitle, chunkRow]);

        var newLetterLabel = Theme.Label(L.T("אות הכונן:", "Drive letter:"), Theme.Small, Theme.Muted);
        newLetterLabel.Margin = new Padding(0, 6, 8, 0);
        _newLetterRow.Controls.AddRange([newLetterLabel, _newLetter, TypeCaption(), _newType, TypeWhy(_newType, () => _name.Text)]);
        _newLetterRow.Visible = false;
        _newLetter.SelectedIndexChanged += (_, _) => UpdateState();
        _createGroup.Controls.AddRange([_nameLabel, _name, grid, _strength, _show, _newLetterRow, _chunkBox, _warning, _saved]);
        _tips.SetToolTip(_rememberInfo, RememberText);
        _createGroup.Paint += (_, e) =>
        {
            if (_createGroup.Padding.All == 0 && _createGroup.Padding.Left == 0) return; // inside the one-drive form: no frame
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, _createGroup.Width - 1, _createGroup.Height - 1);
        };
        _rememberInfo.Click += (_, _) => Ui.Info(RememberText);
        _settingsRow.Controls.AddRange([_remember, _rememberInfo]);
        _autostart.Margin = new Padding(0, 6, 0, 2);
        // the first drive on this PC: on (later drives keep what the user chose; the tray menu has it too)
        _askAutostart = false; // starting with Windows is in the icon's menu; the first drive turns it on by itself
        _autostart.Visible = _askAutostart;
        _newLetterRow.Visible = ChoosesAll;
        _form.Controls.AddRange([_intro, _createGroup, _settingsRow, _autostart, _error]);
        _form.Visible = false;

        col.Controls.AddRange([_checking, _driveRetry, _connected, _vanished, _choices, _form]);
        _driveRetry.Visible = false;
        _driveRetry.Click += async (_, _) => { Ctx.ResetClient(signOut: false); await EnterAsync(); };
        SetContent(col);

        _show.CheckedChanged += (_, _) =>
        {
            _pw.UseSystemPasswordChar = _pw2.UseSystemPasswordChar = !_show.Checked;
            foreach (var b in _otherBoxes.Values) b.UseSystemPasswordChar = !_show.Checked;
        };
        _pw.TextChanged += (_, _) => UpdateState();
        _pw2.TextChanged += (_, _) => UpdateState();
        _name.TextChanged += (_, _) => { UpdateState(); RefreshPlaceholders(); };
        _saved.CheckedChanged += (_, _) => UpdateState();
        _vanishedOk.CheckedChanged += (_, _) => UpdateState();
        Changed(false);
    }

    // ── discovery ────────────────────────────────────────────────────────────
    public override async Task EnterAsync()
    {
        _error.Text = "";
        _driveRetry.Visible = false;
        _wasVanished = false;
        _selected = null;
        _noneChosen = false;
        _autoDone = false;
        _choices.Visible = false; // shown again by the list; stale after Back otherwise
        SetMode(Mode.Checking);
        try
        {
            if (!Ctx.UsesTelegram) { await EnterFolderAsync(); return; }
            var client = Ctx.GetClient();

            // a profile that already knows its drive: reopen exactly that one
            if (Ctx.State.Config.Channels is { } known)
            {
                try
                {
                    await UseRemoteAsync(await Task.Run(() => TelegramRemote.OpenAsync(client, known, ownsClient: false)));
                    _ctrl = await SyncService.ReadControlAsync(_remote!, default);
                    if (_ctrl is not null && RememberedKeyWorks()) { SetMode(Mode.Connected); return; }
                    if (_ctrl is not null) { SetMode(Mode.Connect); return; }
                }
                catch (DriveVanishedException) { _wasVanished = true; Ctx.DriveVanished = true; }
            }

            var candidates = await Task.Run(() => TelegramRemote.DiscoverAsync(client));
            if (Ctx.Current is { CreateNew: true } cn)
            {
                Select(null);
                if (cn.Label is { Length: > 0 } l) _name.Text = l;
                if (cn.Letter is { } lt && _newLetter.Items.Contains(lt)) _newLetter.SelectedItem = lt;
                if (cn.Network is { } nw) _newType.SelectedIndex = nw ? 0 : 1;
                _remember.Checked = cn.Remember;
                return;
            }
            if (Ctx.Current is { Candidate: { Channels: { } wanted } want })
            {
                // a drive ticked earlier in this sitting: straight to its password (the previous one is tried first)
                var found = candidates.FirstOrDefault(c => c.Channels?.JournalId == wanted.JournalId && c.HasVault);
                if (found is not null && !OnThisPc().ContainsKey(wanted.JournalId))
                {
                    Select(found);
                    _pw.Text = Ctx.Current.PasswordHint ?? "";
                    _pw.SelectAll();
                    _remember.Checked = Ctx.Current.Remember;
                    if (Ctx.Current.Verified && !_autoTried)
                    {
                        // its password was typed and checked with the first drive: no second password window.
                        // Only once: coming Back here shows the page as usual.
                        _autoTried = true;
                        Busy?.Invoke(true);
                        try { _autoDone = await NextAsync(); }
                        catch (Exception ex) { _error.Text = TelegramErrors.Text(ex); }
                        finally { Busy?.Invoke(false); }
                    }
                    return;
                }
                Ctx.QueuedDriveGone = true;
                ShowChoices(candidates); // selects a default, which clears the error line: the note comes after
                _error.Text = L.T($"הכונן \"{want.Name}\" כבר לא זמין לחיבור (אולי הוא כבר מחובר, או שהוסר). אפשר לבחור כונן אחר, או ללחוץ \"ביטול\" כדי לדלג עליו.",
                    $"The drive \"{want.Name}\" can no longer be connected (perhaps it is connected already, or was removed). You can choose another one, or press \"Cancel\" to skip it.");
                return;
            }
            ShowChoices(candidates);
        }
        catch (Exception ex)
        {
            _checking.ForeColor = Theme.Error;
            _checking.Text = TelegramErrors.Text(ex);
            _driveRetry.Visible = true;
        }
    }

    async Task EnterFolderAsync()
    {
        _remote = new FolderRemote(Ctx.State.Folder!);
        _ctrl = await SyncService.ReadControlAsync(_remote, default);
        if (_ctrl is null) { SetMode(Mode.Create); return; }
        SetMode(RememberedKeyWorks() ? Mode.Connected : Mode.Connect);
    }

    bool RememberedKeyWorks()
    {
        if (Ctx.State.RememberedMaster() is not { } master || _ctrl is null) return false;
        try { Keys.FromMaster(_ctrl.Vault, master); return true; }
        catch (WrongPasswordException) { return false; } // kept: only a correct new password replaces it
    }

    async Task UseRemoteAsync(TelegramRemote remote)
    {
        if (Ctx.Remote is { } old && !ReferenceEquals(old, remote)) await old.DisposeAsync();
        Ctx.Remote = remote;
        _remote = remote;
    }

    Dictionary<long, string> OnThisPc() => AppState.All().Where(a => a.Dir != Ctx.State.Dir && a.Config.Channels is not null)
        .GroupBy(a => a.Config.Channels!.JournalId).ToDictionary(g => g.Key, g => g.First().Config.MountPoint);

    /// <summary>One radio button per drive found, plus "a new drive" (check boxes when several may be chosen).</summary>
    void ShowChoices(List<DriveCandidate> candidates)
    {
        _choices.Controls.Clear();
        _ticks.Clear();
        _tickBoxes.Clear();
        _otherBoxes.Clear();
        _sameHints.Clear();
        _newTick = null;
        var onThisPc = OnThisPc();
        _letters.Clear();
        _labels.Clear();
        _types.Clear();
        _remembers.Clear();
        if (Multi) { ShowTicks(candidates, onThisPc); return; }
        if (_createGroup.Parent != _form) { _form.Controls.Add(_createGroup); _form.Controls.SetChildIndex(_createGroup, 1); }
        if (_show.Parent != _createGroup) { _createGroup.Controls.Add(_show); _createGroup.Controls.SetChildIndex(_show, 4); }
        if (_settingsRow.Parent != _form) { _form.Controls.Add(_settingsRow); _form.Controls.SetChildIndex(_settingsRow, 2); }
        _createGroup.Margin = Padding.Empty;
        _createGroup.Padding = Padding.Empty;
        RadioButton? first = null;
        if (candidates.Count > 0)
            _choices.Controls.Add(Theme.Label(L.T("כוננים שהחשבון הזה יכול לפתוח:", "Drives this account can open:"), Theme.Heading));
        foreach (var c in candidates)
        {
            string? why = c.Channels is null ? L.T("ערוץ הקבצים שלו חסר", "its data channel is missing")
                : !c.HasVault ? L.T("היצירה שלו לא הושלמה", "it was never finished")
                : onThisPc.TryGetValue(c.Channels.JournalId, out var letter) ? L.T($"כבר מחובר במחשב הזה כ־{L.Ltr(letter)}", $"already on this PC as {letter}")
                : null;
            var owner = c.Owned ? L.T("שלך", "yours") : L.T("אתה מנהל בו (גיבוי)", "you are an admin (backup)");
            var rb = Choice($"{c.Name}  ·  {owner}{(why is null ? "" : $"  —  {why}")}", c, enabled: why is null);
            if (why is null) first ??= rb;
        }
        var create = Choice(L.T("כונן חדש וריק בחשבון הזה", "A new, empty drive on this account"), null, enabled: true);
        create.Font = new Font(Theme.Body, FontStyle.Bold);
        _choices.Visible = true;
        (first ?? create).Checked = true;
        if (_wasVanished) _vanished.Visible = true;
    }

    /// <summary>The same list with check boxes: any number of existing drives, and/or a new one. This run
    /// handles the first; the others follow right after, each with its own letter and name.</summary>
    void ShowTicks(List<DriveCandidate> candidates, Dictionary<long, string> onThisPc)
    {
        // settings for every drive on the page, apart from the list: switches, not check boxes
        // the settings of this page (not of one drive): plain switches under a title
        _settingsRow.Controls.Clear();
        _show.Text = L.T("הצג את הסיסמאות שאני מקליד", "Show the passwords I type");
        _autostart.Text = L.T("הפעל את DriveGram אוטומטית כשנכנסים לווינדוס", "Start DriveGram automatically when I sign in to Windows");
        _show.Margin = new Padding(0, 4, 0, 2);
        _autostart.Margin = new Padding(0, 2, 0, 2);
        var switches = Theme.Column();
        switches.Controls.Add(_show);
        if (_askAutostart) switches.Controls.Add(_autostart);
        // the new drive's own "remember", in its group under the letter and type
        _remember.Text = L.T("לזכור את הסיסמה במחשב הזה", "Remember the password on this PC");
        _remember.Margin = new Padding(0, 6, 0, 2);
        _rememberInfo.Margin = new Padding(4, 8, 4, 0);
        if (_newRememberRow.Parent != _createGroup)
        {
            _newRememberRow.Controls.AddRange([_remember, _rememberInfo]);
            _createGroup.Controls.Add(_newRememberRow);
            _createGroup.Controls.SetChildIndex(_newRememberRow, _createGroup.Controls.GetChildIndex(_newLetterRow) + 1);
        }
        var title = Theme.Label(L.T("הגדרות לעמוד הזה", "Settings for this page"), Theme.Body, Theme.Text);
        title.Font = new Font(Theme.Body, FontStyle.Bold);
        title.Margin = new Padding(0, 0, 0, 2);
        var bar = Theme.Card(Theme.Surface, Theme.Border);
        var barCol = Theme.Column();
        barCol.Controls.AddRange([title, switches]);
        bar.Controls.Add(barCol);
        bar.Margin = new Padding(0, 0, 0, 12);
        _choices.Controls.Add(bar);
        _choices.Controls.Add(Theme.Label(candidates.Count > 0 ? L.T("סמן את הכוננים שיתחברו למחשב הזה:", "Tick the drives to connect to this PC:")
            : L.T("בחשבון הזה עוד אין כוננים:", "This account has no drives yet:"), Theme.Heading));
        foreach (var c in candidates)
        {
            string? why = c.Channels is null ? L.T("ערוץ הקבצים שלו חסר", "its data channel is missing")
                : !c.HasVault ? L.T("היצירה שלו לא הושלמה", "it was never finished")
                : onThisPc.TryGetValue(c.Channels.JournalId, out var letter) ? L.T($"כבר מחובר במחשב הזה כ־{L.Ltr(letter)}", $"already on this PC as {letter}")
                : null;
            var owner = c.Owned ? L.T("שלך", "yours") : L.T("אתה מנהל בו (גיבוי)", "you are an admin (backup)");
            var cb = new CheckBox { Text = $"{c.Name}  ·  {owner}{(why is null ? "" : $"  —  {why}")}", AutoSize = true, Enabled = why is null, Tag = c, Margin = new Padding(0, 2, 0, 4) };
            cb.CheckedChanged += (_, _) => TicksChanged();
            _choices.Controls.Add(cb);
            if (why is null)
            {
                _ticks.Add(cb);
                var card = DriveCard(c);
                _tickBoxes[cb] = card;
                _choices.Controls.Add(card);
            }
        }
        _newTick = new CheckBox
        {
            Text = L.T("כונן חדש וריק בחשבון הזה", "A new, empty drive on this account"), AutoSize = true,
            Font = new Font(Theme.Body, FontStyle.Bold), Margin = new Padding(0, 2, 0, 4),
        };
        _newTick.CheckedChanged += (_, _) => TicksChanged();
        _choices.Controls.Add(_newTick);
        // the new drive's fields right under it, in a card like the others
        _createGroup.Margin = new Padding(26, 0, 0, 10);
        _createGroup.Padding = new Padding(12, 10, 12, 10); // drawn as a card (see the constructor)
        _choices.Controls.Add(_createGroup);
        _newLetterRow.Visible = true;
        _choices.Controls.Add(_pickHint);
        _choices.Visible = true;
        if (_ticks.Count > 0) _ticks[0].Checked = true; else _newTick.Checked = true;
        if (_wasVanished) _vanished.Visible = true;
    }

    List<DriveCandidate> Ticked => _ticks.Where(t => t.Checked).Select(t => (DriveCandidate)t.Tag!).ToList();

    static ComboBox TypeBox()
    {
        var b = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Font = Theme.Body, RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No };
        b.Items.AddRange([L.T("כונן רשת (מומלץ)", "Network drive (recommended)"), L.T("דיסק רגיל", "Regular disk")]);
        b.SelectedIndex = 0;
        return b;
    }

    static Label TypeCaption()
    {
        var l = Theme.Label(L.T("סוג:", "Type:"), Theme.Small, Theme.Muted);
        l.Margin = new Padding(22, 6, 8, 0);
        return l;
    }

    /// <summary>"What is the difference?" next to a drive's type: the comparison, and its choice back in the box.</summary>
    LinkLabel TypeWhy(ComboBox box, Func<string> name)
    {
        var link = new LinkLabel { Text = L.T("מה ההבדל?", "What is the difference?"), AutoSize = true, Font = Theme.Small, LinkColor = Theme.Accent, Margin = new Padding(8, 6, 8, 0) };
        link.LinkClicked += (_, _) =>
        {
            using var f = new DriveTypeForm(name().Trim() is { Length: > 0 } n ? n : "DriveGram", box.SelectedIndex == 0, inWizard: true);
            if (f.ShowDialog(FindForm()) == DialogResult.OK) box.SelectedIndex = f.Network ? 0 : 1;
        };
        return link;
    }

    /// <summary>The drive this run handles: its "remember" and its type (a new drive's, or the first ticked one's).</summary>
    bool FirstRemember => Multi && _mode == Mode.Connect && Ticked.FirstOrDefault()?.Channels is { } ch && _remembers.TryGetValue(ch.JournalId, out var r) ? r.Checked : _remember.Checked;
    bool FirstNetwork => Multi && _mode == Mode.Connect && Ticked.FirstOrDefault()?.Channels is { } ch && _types.TryGetValue(ch.JournalId, out var t) ? t.SelectedIndex == 0 : _newType.SelectedIndex == 0;

    static ComboBox LetterBox() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Font = Theme.Body, RightToLeft = RightToLeft.No };

    /// <summary>Letters free for a new drive here (not in use, not another DriveGram drive's).</summary>
    List<string> FreeLetters()
    {
        var inUse = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var others = AppState.All().Where(a => a.Dir != Ctx.State.Dir).Select(a => char.ToUpperInvariant(a.Config.MountPoint[0])).ToHashSet();
        return "TUVWXYZSRQPONMLKJIHGFED".Where(ch => !inUse.Contains(ch) && !others.Contains(ch)).OrderBy(ch => ch).Select(ch => $"{ch}:").ToList();
    }

    /// <summary>A ticked drive's card: its password, its letter and its name in This PC (all labelled).</summary>
    Panel DriveCard(DriveCandidate c)
    {
        var id = c.Channels!.JournalId;
        var card = Theme.Card(Slot.Card, Slot.Border);
        card.Margin = new Padding(26, 0, 0, 10);
        card.Visible = false;
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        Label Caption(string t) { var l = Theme.Label(t, Theme.Small, Theme.Muted); l.Margin = new Padding(0, 7, 12, 0); return l; }
        var box = Theme.Input(300, password: true);
        box.UseSystemPasswordChar = !_show.Checked;
        box.Margin = new Padding(0, 0, 0, 2);
        var sameHint = Theme.Label("", Theme.Small, Theme.Muted, 300);
        sameHint.Margin = new Padding(0, 0, 0, 8);
        sameHint.Visible = false;
        var pwCell = Theme.Column();
        pwCell.Margin = new Padding(0);
        pwCell.Controls.AddRange([box, sameHint]);
        _sameHints[id] = sameHint;
        box.TextChanged += (_, _) => UpdateState();
        var letter = LetterBox();
        letter.Margin = new Padding(0, 0, 0, 8);
        letter.SelectedIndexChanged += (_, _) => UpdateState();
        var label = Theme.Input(300);
        label.Text = c.Name;
        label.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        label.Font = Theme.Body;
        label.Margin = new Padding(0, 0, 0, 8);
        label.TextChanged += (_, _) => { UpdateState(); RefreshPlaceholders(); };
        letter.Margin = new Padding(0);
        var type = TypeBox();
        var letterAndType = Theme.Row();
        letterAndType.Margin = new Padding(0, 0, 0, 4);
        letterAndType.Controls.AddRange([letter, TypeCaption(), type, TypeWhy(type, () => label.Text)]);
        var remember = new ToggleSwitch(L.T("לזכור את הסיסמה במחשב הזה", "Remember the password on this PC")) { Checked = true };
        var info = new Label { Text = "ⓘ", AutoSize = true, Font = _rememberInfo.Font, ForeColor = Theme.Accent, Cursor = Cursors.Help, Margin = new Padding(4, 8, 4, 0) };
        _tips.SetToolTip(info, RememberText);
        info.Click += (_, _) => Ui.Info(RememberText);
        var rememberRow = Theme.Row();
        rememberRow.Margin = new Padding(0);
        rememberRow.Controls.AddRange([remember, info]);
        grid.Controls.Add(Caption(L.T("שם הכונן:", "Drive name:")), 0, 0);
        grid.Controls.Add(label, 1, 0);
        grid.Controls.Add(Caption(L.T("סיסמת הכונן:", "Drive password:")), 0, 1);
        grid.Controls.Add(pwCell, 1, 1);
        grid.Controls.Add(Caption(L.T("אות הכונן:", "Drive letter:")), 0, 2);
        grid.Controls.Add(letterAndType, 1, 2);
        grid.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0) }, 0, 3);
        grid.Controls.Add(rememberRow, 1, 3);
        card.Controls.Add(grid);
        _types[id] = type;
        _remembers[id] = remember;
        _otherBoxes[id] = box;
        _letters[id] = letter;
        _labels[id] = label;
        return card;
    }

    /// <summary>Each ticked drive (and a ticked new one) gets a free letter no other ticked drive has.</summary>
    void AssignLetters()
    {
        var free = FreeLetters();
        var boxes = new List<ComboBox>();
        foreach (var c in Ticked) if (c.Channels is { } ch && _letters.TryGetValue(ch.JournalId, out var b)) boxes.Add(b);
        if (_newTick?.Checked == true || (!Multi && _mode == Mode.Create)) boxes.Insert(0, _newLetter);
        // in order (the new drive first): a box keeping a letter an earlier ticked box already has picks again
        var taken = new HashSet<string>();
        foreach (var b in boxes)
            if (b.SelectedItem is string l && !taken.Add(l)) b.SelectedIndex = -1;
        foreach (var b in boxes.Append(_newLetter).Concat(_letters.Values).Distinct())
        {
            if (b.Items.Count == free.Count) continue;
            var keep = b.SelectedItem as string;
            b.Items.Clear();
            foreach (var l in free) b.Items.Add(l);
            if (keep is not null && free.Contains(keep)) b.SelectedItem = keep;
        }
        foreach (var b in boxes)
        {
            if (b.SelectedItem is string) continue;
            // T: first, as before (then the letters after it, then back towards D:)
            var pick = "TUVWXYZSRQPONMLKJIHGFED".Select(ch => $"{ch}:").FirstOrDefault(l => free.Contains(l) && !taken.Contains(l));
            if (pick is null) continue;
            b.SelectedItem = pick;
            taken.Add(pick);
        }
    }

    /// <summary>Two ticked drives with the same letter (null: all different).</summary>
    string? LetterClash()
    {
        var chosen = new List<(string Name, string Letter)>();
        if (_newTick?.Checked == true && _newLetter.SelectedItem is string nl) chosen.Add((_name.Text.Trim(), nl));
        foreach (var c in Ticked)
            if (c.Channels is { } ch && _letters.TryGetValue(ch.JournalId, out var b) && b.SelectedItem is string l) chosen.Add((c.Name, l));
        var dup = chosen.GroupBy(x => x.Letter).FirstOrDefault(g => g.Count() > 1);
        return dup is null ? null : L.T($"שני כוננים קיבלו את האות {L.Ltr(dup.Key)}. בחר לאחד מהם אות אחרת.", $"Two drives got the letter {dup.Key}. Choose another letter for one of them.");
    }

    /// <summary>A new drive, if ticked, is made first (its fields are on this page); the existing drives
    /// ticked with it follow, their passwords checked here.</summary>
    void TicksChanged()
    {
        var ticked = Ticked;
        bool wantNew = _newTick?.Checked == true;
        _noneChosen = ticked.Count == 0 && !wantNew;
        _pickHint.Visible = _noneChosen;
        foreach (var (cb, box) in _tickBoxes) box.Visible = cb.Checked;
        AssignLetters();
        KeepOrder();
        RefreshPlaceholders();
        if (wantNew && _mode == Mode.Create) SetMode(Mode.Create); // keeps the name typed (only the texts follow)
        else if (wantNew) Select(null);
        else if (ticked.Count > 0) Select(ticked[0]);
        else { _form.Visible = false; _createGroup.Visible = false; UpdateState(); }
    }

    /// <summary>
    /// Each box right under its tick, the new drive's fields under "a new drive". Windows moves a control
    /// shown for the first time to the end of the list (its window is created only then), so put them back.
    /// </summary>
    void KeepOrder()
    {
        var all = _choices.Controls;
        foreach (var (cb, box) in _tickBoxes)
            if (all.Contains(box)) all.SetChildIndex(box, all.GetChildIndex(cb) + 1);
        if (_newTick is not null && all.Contains(_createGroup)) all.SetChildIndex(_createGroup, all.GetChildIndex(_newTick) + 1);
        if (all.Contains(_pickHint)) all.SetChildIndex(_pickHint, all.Count - 1);
    }

    /// <summary>The first box to type in is the one that must be filled; the others may stay empty (the same password).</summary>
    void RefreshPlaceholders()
    {
        // the drive whose password an empty box borrows: the one before it (the new drive comes first)
        string? before = _newTick?.Checked == true ? _name.Text.Trim() : null;
        foreach (var c in Ticked)
        {
            if (c.Channels is not { } ch || !_otherBoxes.TryGetValue(ch.JournalId, out var box)) continue;
            if (_sameHints.TryGetValue(ch.JournalId, out var hint))
            {
                hint.Text = before is null ? "" : L.T($"אפשר להשאיר ריק אם הסיסמה זהה לזו של \"{L.Name(before)}\".", $"Leave it empty if the password is the same as \"{before}\"'s.");
                hint.Visible = before is not null;
            }
            before = _labels.TryGetValue(ch.JournalId, out var n) && n.Text.Trim().Length > 0 ? n.Text.Trim() : c.Name;
        }
    }

    /// <summary>Where the password of the drive handled first is typed.</summary>
    TextBox PwBox => Multi && _mode == Mode.Connect && Ticked.FirstOrDefault()?.Channels is { } ch && _otherBoxes.TryGetValue(ch.JournalId, out var b) ? b : _pw;

    RadioButton Choice(string text, DriveCandidate? candidate, bool enabled)
    {
        var rb = new RadioButton { Text = text, AutoSize = true, Enabled = enabled, Tag = candidate, Margin = new Padding(0, 2, 0, 4) };
        rb.CheckedChanged += (_, _) => { if (rb.Checked) Select((DriveCandidate?)rb.Tag); };
        _choices.Controls.Add(rb);
        return rb;
    }

    void Select(DriveCandidate? c)
    {
        if (c is not null && _mode == Mode.Create && !string.IsNullOrWhiteSpace(_name.Text)) _typedNewName = _name.Text;
        _selected = c;
        _ctrl = null;
        _error.Text = "";
        if (c is null)
        {
            var taken = AppState.All().Select(a => a.Config.VolumeLabel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _name.Text = _typedNewName
                ?? Enumerable.Range(1, 99).Select(i => i == 1 ? "DriveGram" : $"DriveGram {i}").First(n => !taken.Contains(n));
            SetMode(Mode.Create);
        }
        else SetMode(Mode.Connect);
    }

    /// <summary>The other ticked drives' passwords, checked now: a wrong one is said here, not in a later window.</summary>
    async Task<bool> CheckOtherPasswordsAsync(string first)
    {
        _otherVerified.Clear();
        if (!Multi) return true;
        var prev = first;
        foreach (var c in Ticked.Skip(_mode == Mode.Connect ? 1 : 0))
        {
            if (c.Channels is not { } ch || !_otherBoxes.TryGetValue(ch.JournalId, out var box)) continue;
            var pw = box.Text.Length > 0 ? box.Text : prev;
            prev = pw;
            var remote = await Task.Run(() => TelegramRemote.OpenAsync(Ctx.GetClient(), ch, ownsClient: false));
            try
            {
                var ctrl = await SyncService.ReadControlAsync(remote, default);
                if (ctrl is null) continue; // its own run will say what is wrong
                try { await Task.Run(() => Keys.UnlockAny(ctrl.Vault, pw)); }
                catch (WrongPasswordException)
                {
                    _error.Text = box.Text.Length > 0
                        ? L.T($"הסיסמה של \"{c.Name}\" שגויה.", $"The password of \"{c.Name}\" is wrong.")
                        : L.T($"הסיסמה שלמעלה לא פותחת את \"{c.Name}\". הקלד את הסיסמה שלו בשדה שמתחתיו.", $"The password above does not open \"{c.Name}\". Type its own password in the box under it.");
                    box.SelectAll();
                    box.Focus();
                    return false;
                }
                _otherVerified[ch.JournalId] = pw;
            }
            finally { await remote.DisposeAsync(); }
        }
        return true;
    }

    /// <summary>The other ticked drives (and a new one, if ticked next to existing ones) come right after
    /// this one, before anything queued earlier. Going Back and Next again replaces what this page queued.</summary>
    void EnqueueTheRest(string pw)
    {
        // Back to this page after its drive was created or connected: it reopened that drive (no list was
        // shown), so what was queued the first time stays as it is
        if (_mode == Mode.Connect && _selected is null) return;
        foreach (var e in _enqueued) Ctx.Queue.Remove(e);
        _enqueued = [];
        if (!Multi || Ctx.Account is not { } account) return;
        foreach (var c in Ticked.Skip(_mode == Mode.Connect ? 1 : 0))
        {
            var id = c.Channels!.JournalId;
            bool known = _otherVerified.ContainsKey(id);
            _enqueued.Add(new NextDrive(account, c, false, known ? _otherVerified[id] : pw, known, _remembers.GetValueOrDefault(id)?.Checked ?? true,
                _letters.GetValueOrDefault(id)?.SelectedItem as string, _labels.GetValueOrDefault(id)?.Text.Trim() is { Length: > 0 } n ? n : c.Name,
                _types.GetValueOrDefault(id)?.SelectedIndex is { } t ? t == 0 : null));
        }
        if (_newTick?.Checked == true && _mode == Mode.Connect) _enqueued.Add(new NextDrive(account, null, true, pw, Remember: _remember.Checked,
            Letter: _newLetter.SelectedItem as string, Label: _name.Text.Trim(), Network: _newType.SelectedIndex == 0));
        Ctx.Queue.InsertRange(0, _enqueued);
    }

    // ── form ─────────────────────────────────────────────────────────────────
    public override void Demo(int variant)
    {
        _wasVanished = variant == 3;
        if (variant is 0 or 4)
        {
            ShowChoices([
                new DriveCandidate("a", "DriveGram", new TelegramChannels(1, 1, 2, 2), true, true, true),
                new DriveCandidate("b", "Photos", new TelegramChannels(3, 3, 4, 4), true, true, true),
                new DriveCandidate("c", "DriveGram", new TelegramChannels(5, 5, 6, 6), false, true, false)]);
            if (_ticks.Count > 1) _ticks[1].Checked = true; // two drives: each with its password box
            PwBox.Text = "secret";
            if (variant == 4 && _newTick is not null) { _newTick.Checked = true; _pw.Text = _pw2.Text = "Correct-Horse-7"; }
            return;
        }
        _choices.Visible = false;
        SetMode(variant == 2 ? Mode.Connect : Mode.Create);
        _name.Text = "DriveGram";
        _pw.Text = variant == 2 ? "secret" : "Correct-Horse-7";
        _pw2.Text = variant == 2 ? "" : "Correct-Horse-7";
        if (variant == 2) _error.Text = L.T("הסיסמה שגויה.", "Wrong password.");
        _vanished.Visible = _wasVanished;
    }

    void SetMode(Mode mode)
    {
        _mode = mode;
        _checking.Visible = mode == Mode.Checking;
        _checking.ForeColor = Theme.Muted;
        if (mode == Mode.Checking) _checking.Text = L.T("מחפש כונני DriveGram בחשבון…", "Looking for DriveGram drives on the account…");
        _connected.Visible = mode == Mode.Connected;
        _form.Visible = mode is Mode.Connect or Mode.Create;
        bool create = mode == Mode.Create;
        _nameLabel.Visible = _name.Visible = create && Ctx.UsesTelegram;
        _confirmLabel.Visible = _pw2.Visible = _strength.Visible = _warning.Visible = _saved.Visible = _chunkBox.Visible = create;
        if (Multi) _show.Visible = true; // in the settings bar, for every box
        // with check boxes the new drive's fields are under its tick: only while it is ticked
        _createGroup.Visible = !Multi || create;
        // with check boxes, a new drive's explanation goes above its own fields (under its tick)
        var introHome = Multi && create ? _createGroup : _form;
        if (_intro.Parent != introHome) { introHome.Controls.Add(_intro); introHome.Controls.SetChildIndex(_intro, 0); }
        if (Multi) KeepOrder();
        else if (create && ChoosesAll) AssignLetters(); // one new drive (from the main window): its letter here too
        _intro.Text = create
            ? L.T("בחר סיסמה לכונן, לפחות 10 תווים. היא מצפינה כל קובץ לפני שהוא עולה לטלגרם.",
                  "Choose a drive password, at least 10 characters. It encrypts every file before it goes to Telegram.")
              + (Ctx.UsesTelegram ? L.T(
                  " בחשבון ייווצרו שני ערוצים פרטיים: \"DriveGram Data · <שם הכונן>\" (הקבצים) ו־\"DriveGram Journal · <שם הכונן>\" (היומן: רשימת הקבצים והשינויים). הם הכונן עצמו: אל תמחק אותם ואל תשלח בהם הודעות.",
                  " Two private channels are created on the account: \"DriveGram Data · <drive name>\" (the files) and \"DriveGram Journal · <drive name>\" (the journal: the list of files and changes). They are the drive itself: do not delete them or post in them.") : "")
            : _selected is not null && Ctx.Current is { Candidate: not null }
            ? L.T($"הקלד את הסיסמה של \"{_selected.Name}\" (או את מפתח השחזור שלו). אם יש לו אותה סיסמה כמו לכונן הקודם, היא כבר כתובה כאן.",
                  $"Enter the password of \"{_selected.Name}\" (or its recovery key). If it is the same as the previous drive's, it is already filled in.")
            : Multi ? L.T("הקלד מתחת לכל כונן שסימנת את הסיסמה שלו (או את מפתח השחזור שלו).", "Type each ticked drive's password (or its recovery key) under it.")
            : L.T("הקלד את הסיסמה של הכונן (או את מפתח השחזור שלו) כדי לחבר אליו את המחשב הזה.", "Enter the drive's password (or its recovery key) to connect this PC to it.");
        int later = Multi && mode is Mode.Connect or Mode.Create ? Ticked.Count + (_newTick?.Checked == true ? 1 : 0) - 1 : 0;
        if (Multi && mode == Mode.Connect) _intro.Text = ""; // every ticked drive has its own labelled card
        _intro.Visible = _intro.Text.Length > 0;
        if (later > 0 && Multi && mode == Mode.Create)
        {
            // the name typed for the new drive, if any (not generic words in quotes, as if they were a name)
            var typed = _name.Text.Trim();
            string firstHe = typed.Length > 0 ? $"ב־\"{L.Name(typed)}\"" : "בכונן החדש";
            string firstEn = typed.Length > 0 ? $"\"{typed}\"" : "the new drive";
            _intro.Text += L.N(later,
                $" מתחילים {firstHe}, ומיד אחריו ממשיכים לכונן הנוסף שסימנת.",
                $" מתחילים {firstHe}, ומיד אחריו ממשיכים ל־{{0}} הכוננים הנוספים שסימנת.",
                $" Starting with {firstEn}; right after it we continue to the other drive you ticked.",
                $" Starting with {firstEn}; right after it we continue to the {{0}} other drives you ticked.");
        }
        if (_form.Visible) (create && _name.Visible ? _name : PwBox).Focus();
        UpdateState();
    }

    static (int Score, string Text, Color Color) Strength(string pw)
    {
        int classes = (pw.Any(char.IsLower) ? 1 : 0) + (pw.Any(char.IsUpper) ? 1 : 0) +
                      (pw.Any(char.IsDigit) ? 1 : 0) + (pw.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0) +
                      (pw.Any(c => c > 127) ? 1 : 0);
        int score = pw.Length < 10 ? 0 : Math.Min(4, (pw.Length - 6) / 4 + classes - 1);
        return score switch
        {
            <= 0 => (1, pw.Length < 10 ? L.T("קצרה מדי", "too short") : L.T("חלשה", "weak"), Theme.Error),
            1 => (2, L.T("בינונית", "fair"), Color.FromArgb(0xE8, 0x8F, 0x1A)),
            2 or 3 => (3, L.T("טובה", "good"), Theme.Accent),
            _ => (4, L.T("חזקה מאוד", "very strong"), Theme.Success),
        };
    }

    void UpdateState()
    {
        bool ok;
        bool vanishedOk = !_wasVanished || _vanishedOk.Checked;
        if (_noneChosen && Multi) ok = false;
        else if (_mode == Mode.Connected) ok = true;
        else if (_mode == Mode.Connect) ok = PwBox.Text.Length > 0 && vanishedOk && (!Multi || LetterClash() is null);
        else if (_mode == Mode.Create)
        {
            var (score, text, color) = Strength(_pw.Text);
            _meterFill.Width = _pw.Text.Length == 0 ? 0 : _meter.Width * score / 4;
            _meterFill.BackColor = color;
            _meterText.Text = _pw.Text.Length == 0 ? "" : L.T($"הסיסמה {text}", $"Strength: {text}");
            _meterText.ForeColor = color;
            bool match = _pw2.Text.Length == 0 || _pw2.Text == _pw.Text;
            _error.Text = match ? "" : L.T("הסיסמאות לא זהות.", "The passwords do not match.");
            ok = _pw.Text.Length >= 10 && _pw2.Text == _pw.Text && _saved.Checked && vanishedOk &&
                 (!_name.Visible || _name.Text.Trim().Length > 0) && (!Multi || LetterClash() is null);
        }
        else ok = false;
        if (Multi && _mode is Mode.Connect or Mode.Create && LetterClash() is { } clash) _error.Text = clash;
        else if (Multi && _error.Text.StartsWith(L.T("שני כוננים", "Two drives"))) _error.Text = "";
        Changed(ok);
    }

    // ── next ─────────────────────────────────────────────────────────────────
    public override async Task<bool> NextAsync()
    {
        if (_mode == Mode.Connected)
        {
            if (!Ctx.IsNew)
            {
                Ctx.State.Save(); // an existing drive has no last page: its sign-in is kept here
                await Task.Delay(1200); // a moment to read "all set" before the window closes by itself
            }
            return true;
        }
        _error.Text = "";
        var pw = PwBox.Text;
        var cfg = Ctx.State.Config;
        // Binding this profile to a different drive (another account, another drive, a new one, or the
        // old one vanished): its local index and cache belong to the old drive and must be reset — but
        // never while files exist only here, unless the old drive is gone anyway.
        var target = _mode == Mode.Create ? null : _selected?.Channels ?? cfg.Channels;
        bool rebinding = Ctx.UsesTelegram && Ctx.OriginalChannels is { } original
                         && (target is null || target.JournalId != original.JournalId);
        if (rebinding && !_wasVanished)
        {
            var (pending, unsent) = DriveDeletion.LocalOnly(Ctx.State);
            if (pending < 0)
            {
                // the index could not be read: say so, and let the user decide (default: stay)
                if (Ui.Ask(L.T(
                        "⚠  לא הצלחתי לבדוק אם בכונן הקודם יש קבצים או שינויים שקיימים רק במחשב הזה. אם יש, הם יימחקו מהמחשב לצמיתות כשהמחשב יעבור לכונן האחר.\n\nלהמשיך בכל זאת?",
                        "⚠  Could not check whether the previous drive has files or changes that exist only on this PC. If it does, they are deleted from this PC for good when it switches to the other drive.\n\nContinue anyway?"),
                        icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return false;
            }
            else if (pending > 0 || unsent > 0)
            {
                _error.Text = L.T("בכונן הקודם יש ", "The previous drive has ") + TrayApp.LocalOnlyText(pending, unsent) + L.T(
                    " (כרגע רק במחשב הזה). חבר אותו קודם (אם צריך, התחבר מחדש) וחכה שההעלאה תסתיים, או הסר אותו מהמחשב.",
                    " (only on this PC for now). Connect it first (sign in again if needed) and wait for the upload to finish, or remove it from this PC.");
                return false;
            }
            else if (!Ctx.IsNew && Ui.Ask(L.T(
                    $"מעכשיו {L.Ltr(Ctx.State.Config.MountPoint)} יציג את הכונן {(_selected is { } chosen ? $"\"{L.Name(chosen.Name)}\"" : "שבחרת")} במקום הכונן הקודם.\n\nהעותק המקומי של הכונן הקודם יימחק מהמחשב הזה. הקבצים שלו נשארים בטלגרם, ואפשר לחזור אליו אחר כך דרך \"החלפה לכונן אחר באותו חשבון…\".\n\nלהמשיך?",
                    $"From now on {Ctx.State.Config.MountPoint} shows the drive {(_selected is { } chosenEn ? $"\"{chosenEn.Name}\"" : "you chose")} instead of the previous one.\n\nThe previous drive's local copy is deleted from this PC. Its files stay in Telegram, and you can switch back later with \"Switch to another drive of the account…\".\n\nContinue?"),
                    icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return false;
        }
        else if (rebinding)
        {
            // the old drive was not found: often just the wrong Telegram account (or this account was removed
            // from the drive's channels) — the drive itself still exists, and files only on this PC are lost
            // for good if this profile is reset now
            var (pending, unsent) = DriveDeletion.LocalOnly(Ctx.State);
            if (pending != 0 || unsent > 0)
            {
                var what = pending < 0
                    ? L.T("לא הצלחתי לבדוק אם יש בו קבצים שקיימים רק במחשב הזה.", "Could not check whether it has files that exist only on this PC.")
                    : L.T("יש בו ", "It has ") + TrayApp.LocalOnlyText(pending, unsent) + L.T(" (כרגע רק במחשב הזה).", " (only on this PC for now).");
                var account = string.IsNullOrEmpty(Ctx.State.Config.AccountName) ? ""
                    : L.T($"\n\nהחשבון המחובר עכשיו: {L.Name(Ctx.State.Config.AccountName)}", $"\n\nSigned in now as: {Ctx.State.Config.AccountName}");
                if (Ui.Ask(L.T(
                        $"⚠  הכונן הקודם לא נמצא בחשבון הטלגרם הזה. {what}{account}\n\n" +
                        "אם התחברת בטעות לחשבון אחר, חזור אחורה והתחבר לחשבון של הכונן: הכל יישמר.\n\n" +
                        "אם תמשיך, הקבצים והשינויים האלה יימחקו מהמחשב לצמיתות. להמשיך בכל זאת?",
                        $"⚠  The previous drive was not found in this Telegram account. {what}{account}\n\n" +
                        "If you signed in to another account by mistake, go back and sign in to the drive's account: everything is kept.\n\n" +
                        "If you continue, these files and changes are deleted from this PC for good. Continue anyway?"),
                        icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return false;
            }
        }
        void ResetIfRebinding()
        {
            if (!rebinding) return;
            _committed = true;
            Ctx.State.ResetLocalData();
            rebinding = false;
            _wasVanished = false;
        }

        if (!await CheckOtherPasswordsAsync(pw)) return false;

        Keys keys;
        if (_mode == Mode.Create)
        {
            DriveIdentity? identity = null;
            if (Ctx.UsesTelegram)
            {
                var name = _name.Text.Trim();
                var client = Ctx.GetClient();
                // tried before in this run and failed after the channels were made: the same pair again,
                // not a second one left behind in Telegram
                if (_createdFor != Ctx.State.Config.AccountKey) (_createdChannels, _createdDriveId) = (null, null);
                var driveId = _createdDriveId ?? Guid.NewGuid().ToString("N");
                var channels = _createdChannels ?? await Task.Run(() => TelegramRemote.CreateChannelsAsync(client, name, driveId));
                (_createdChannels, _createdDriveId, _createdFor) = (channels, driveId, Ctx.State.Config.AccountKey);
                await UseRemoteAsync(await Task.Run(() => TelegramRemote.OpenAsync(client, channels, ownsClient: false)));
                identity = new DriveIdentity(driveId, name, channels.DataId);
                cfg.Channels = channels;
                cfg.DriveId = driveId;
                Ctx.CreatedDriveId = driveId;
                cfg.VolumeLabel = name;
            }
            var remote = _remote!;
            int chunkSize = _chunk64.Checked ? 64 << 20 : Keys.DefaultChunkSize;
            keys = await Task.Run(() => SyncService.CreateDriveAsync(remote, pw, default, chunkSize: chunkSize, identity: identity));
            ResetIfRebinding();
            DriveSession.MarkNewDrive(Ctx.State); // its first start adds the note about the risks
            Log.Info($"setup: created drive '{cfg.VolumeLabel}' ({Ctx.State.Id})");
        }
        else
        {
            if (_selected is { Channels: { } chosen } c)
            {
                await UseRemoteAsync(await Task.Run(() => TelegramRemote.OpenAsync(Ctx.GetClient(), chosen, ownsClient: false)));
                _ctrl = await SyncService.ReadControlAsync(_remote!, default);
                if (_ctrl is null) { _error.Text = L.T("ההגדרה של הכונן הזה לא הושלמה. בחר כונן אחר או צור חדש.", "This drive was never finished. Choose another one or create a new one."); return false; }
            }
            try { keys = await Task.Run(() => Keys.UnlockAny(_ctrl!.Vault, pw)); } // the password, or the recovery key
            catch (WrongPasswordException)
            {
                _error.Text = Multi && Ticked.FirstOrDefault() is { } wrongOne
                    ? L.T($"הסיסמה של \"{L.Name(wrongOne.Name)}\" שגויה.", $"The password of \"{wrongOne.Name}\" is wrong.")
                    : L.T("הסיסמה שגויה.", "Wrong password.");
                PwBox.SelectAll();
                PwBox.Focus();
                return false;
            }
            // bound to the chosen drive only now: after a wrong password, Back and Next show the list again
            // (bound earlier, the page came back as "this drive" with its password box hidden in the list)
            if (_selected is { Channels: { } bound } sel)
            {
                cfg.Channels = bound;
                cfg.DriveId = sel.DriveId ?? "";
                // a new drive's default name, or an existing drive switched to another one: the chosen drive's name
                if (cfg.VolumeLabel is "" or "DriveGram" || (!Ctx.IsNew && Ctx.OriginalChannels is { } was && was.JournalId != bound.JournalId))
                    cfg.VolumeLabel = sel.Name;
            }
            ResetIfRebinding();
        }
        Ctx.State.Save();
        Ctx.UnlockedKeys = keys;
        Ctx.LastPassword = pw;
        if (FirstRemember) Ctx.State.RememberMaster(keys.ExportMaster());
        else Ctx.State.ForgetMaster();
        if (Multi)
        {
            if (_mode == Mode.Create) { Ctx.PresetLetter = _newLetter.SelectedItem as string; Ctx.PresetLabel = _name.Text.Trim(); }
            else if (Ticked.FirstOrDefault() is { Channels: { } first } fc)
            {
                Ctx.PresetLetter = _letters.GetValueOrDefault(first.JournalId)?.SelectedItem as string;
                Ctx.PresetLabel = _labels.GetValueOrDefault(first.JournalId)?.Text.Trim() is { Length: > 0 } n ? n : fc.Name;
            }
        }
        if (ChoosesAll)
        {
            if (!Multi && _mode == Mode.Create) { Ctx.PresetLetter = _newLetter.SelectedItem as string; Ctx.PresetLabel = _name.Text.Trim(); }
            Ctx.PresetNetwork = FirstNetwork;
            Ctx.PresetAutostart = _askAutostart ? _autostart.Checked : null;
        }
        EnqueueTheRest(pw);
        return true;
    }
}

// ── 5. Finish ───────────────────────────────────────────────────────────────
sealed class FinishPage : WizardPage
{
    readonly ComboBox _letter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Font = Theme.Body, RightToLeft = RightToLeft.No };
    readonly TextBox _label = Theme.Input(240);
    readonly CheckBox _autostart = new() { Text = L.T("הפעל את DriveGram אוטומטית כשנכנסים לווינדוס", "Start DriveGram automatically when I sign in to Windows"), AutoSize = true, Checked = true };
    readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, 620);
    readonly RadioButton _network = new() { Text = L.T("כונן רשת (מומלץ)", "Network drive (recommended)"), AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    readonly RadioButton _disk = new() { Text = L.T("דיסק רגיל", "Regular disk"), AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    readonly Label _queued = Theme.Label("", Theme.Small, Theme.Muted, 640);
    readonly List<Control> _letterAndName = [];
    bool _typeSet, _autoUsed, _auto, _presetUsed;
    public override bool AutoNext { get { bool a = _auto && !_autoUsed; if (a) _autoUsed = true; return a; } }
    public override bool Counted => !Ctx.DrivePageFinishes || (_entered && !_auto);
    bool _entered;

    // a new drive normally never shows this page (the drive page chose everything); changing an existing
    // drive's settings does: its letter, name and type
    public override string Title => Ctx.IsNew ? L.T("כמעט סיימנו", "Almost done") : L.T("איך הכונן יופיע במחשב", "How the drive appears on this PC");
    public override string Subtitle => Ctx.IsNew ? L.T("איך הכונן יופיע במחשב.", "How the drive appears on this PC.") : L.T("האות, השם וסוג הכונן.", "The letter, the name and the drive type.");
    public override string NextText => Ctx.IsNew ? L.T("סיום וחיבור הכונן", "Finish and connect") : L.T("שמירה וחיבור הכונן", "Save and connect");

    public FinishPage(SetupContext ctx) : base(ctx)
    {
        _label.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No; // a Hebrew name, typed right to left
        _label.Font = Theme.Body;
        var col = Theme.Column();
        var summaryCard = Theme.Card(Theme.Surface, Theme.Border);
        summaryCard.Controls.Add(_summary);
        col.Controls.Add(summaryCard);
        var letterCaption = Theme.Label(L.T("אות הכונן:", "Drive letter:"), Theme.Small, Theme.Muted);
        var labelCaption = Theme.Label(L.T("שם הכונן ב\"מחשב זה\":", "Name in This PC:"), Theme.Small, Theme.Muted);
        col.Controls.AddRange([letterCaption, _letter, labelCaption, _label]);
        _letterAndName.AddRange([letterCaption, _letter, labelCaption, _label]);

        // the drive type: a network drive by default, the full comparison one click away
        var typeTitle = Theme.Label(L.T("סוג הכונן:", "Drive type:"), Theme.Small, Theme.Muted);
        typeTitle.Margin = new Padding(0, 6, 0, 2);
        col.Controls.Add(typeTitle);
        var types = Theme.Row();
        types.Controls.AddRange([_network, _disk]);
        var diff = new LinkLabel { Text = L.T("מה ההבדל?", "What is the difference?"), AutoSize = true, Font = Theme.Body, LinkColor = Theme.Accent, Margin = new Padding(12, 4, 12, 2) };
        diff.LinkClicked += (_, _) =>
        {
            using var f = new DriveTypeForm(_label.Text.Trim() is { Length: > 0 } n ? n : "DriveGram", _network.Checked, inWizard: true);
            if (f.ShowDialog(FindForm()) == DialogResult.OK) (f.Network ? _network : _disk).Checked = true;
        };
        types.Controls.Add(diff);
        col.Controls.Add(types);
        col.Controls.Add(Theme.Label(L.T(
            "כונן רשת מגן על הכונן מתוכנות גיבוי ואנטי־וירוס שמנסות להוריד את כולו. דיסק רגיל מתאים יותר ל־Lightroom, ‏Outlook ולהתקנת תוכנות. אפשר לשנות את זה בכל רגע אחר כך, בתפריט של הכונן ← \"סוג הכונן\".",
            "A network drive protects the drive from backup and antivirus programs that try to download all of it. A regular disk suits Lightroom, Outlook and installing programs better. You can change it at any time later, from the drive's menu → \"Drive type\"."),
            Theme.Small, Theme.Muted, 640));

        _autostart.Margin = new Padding(0, 8, 0, 4);
        col.Controls.Add(_autostart);
        col.Controls.Add(_queued);
        SetContent(col);
    }

    public override Task EnterAsync()
    {
        var cfg = Ctx.State.Config;
        var inUse = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var otherDrives = AppState.All().Where(a => a.Dir != Ctx.State.Dir).Select(a => char.ToUpperInvariant(a.Config.MountPoint[0])).ToHashSet();
        var current = char.ToUpperInvariant(cfg.MountPoint[0]);
        var free = "DEFGHIJKLMNOPQRSTUVWXYZ".Where(c => !otherDrives.Contains(c) && (!inUse.Contains(c) || c == current)).ToList();
        _letter.Items.Clear();
        foreach (var c in free) _letter.Items.Add($"{c}:");
        char pick = free.Contains(current) ? current : "TUVWXYZSRQPONMLKJIHGFED".FirstOrDefault(free.Contains, 'T');
        _letter.SelectedItem = $"{pick}:";
        _label.Text = cfg.VolumeLabel;
        // chosen on the drive page: this run's drive, or a drive queued from it (that run goes on by itself)
        var forThis = Ctx.QueuedDriveGone ? null : Ctx.Current;
        var presetLetter = Ctx.PresetLetter ?? forThis?.Letter;
        var presetLabel = Ctx.PresetLabel ?? forThis?.Label;
        bool preset = presetLetter is not null && _letter.Items.Contains(presetLetter);
        if (preset) { _letter.SelectedItem = presetLetter; if (!string.IsNullOrWhiteSpace(presetLabel)) _label.Text = presetLabel; }
        foreach (var c in _letterAndName) c.Visible = !preset;
        _presetUsed = preset;
        if ((Ctx.PresetNetwork ?? forThis?.Network) is { } net) { (net ? _network : _disk).Checked = true; _typeSet = true; }
        // a new Telegram drive: everything was chosen on the drive page, this page only saves it
        _auto = preset && (forThis?.Letter is not null || Ctx.DrivePageFinishes);
        _entered = true;
        // a new drive: a network drive (the user's choice is kept when coming back to this page);
        // an existing one: as it is now
        if (!Ctx.IsNew) (cfg.NetworkDrive ? _network : _disk).Checked = true;
        else if (!_typeSet) { _network.Checked = true; _typeSet = true; }
        // more drives ticked on the drive page (they follow this one)
        int queued = Ctx.Queue.Count;
        _queued.Visible = queued > 0;
        _queued.Text = L.N(queued, "אחרי שהכונן הזה יתחבר, נמשיך לכונן הנוסף שסימנת.", "אחרי שהכונן הזה יתחבר, נמשיך ל־{0} הכוננים הנוספים שסימנת.",
            "After this drive connects, we continue to the other drive you ticked.", "After this drive connects, we continue to the {0} other drives you ticked.");
        // the first drive: on; otherwise keep the choice the user already made
        _autostart.Checked = (Ctx.IsNew && !AppState.All().Any(a => a.Dir != Ctx.State.Dir)) || ShellIntegration.AutostartEnabled;

        var account = Ctx.State.Folder is { } folder ? L.LtrPath(folder)
            : string.IsNullOrEmpty(cfg.AccountName) ? null : L.Name(cfg.AccountName);
        var where = !_presetUsed ? "" : L.T($"הכונן יופיע כ־{L.Ltr((string)_letter.SelectedItem!)} {L.Name(_label.Text)}\n", $"The drive appears as {(string)_letter.SelectedItem!} {_label.Text}\n");
        var ready = Ctx.IsNew ? L.T("הכונן מוכן. הקבצים יעלו מוצפנים ברקע, ואייקון ליד השעון יראה מה המצב.",
            "The drive is ready. Files upload encrypted in the background; an icon next to the clock shows what is happening.") : "";
        _summary.Text = ((account is null ? "" : L.T($"חשבון: {account}\n", $"Account: {account}\n")) + where + ready).TrimEnd('\n');
        _summary.Parent!.Visible = _summary.Text.Length > 0;
        // starting with Windows belongs to the whole app (the tray menu), not to one drive's settings
        _autostart.Visible = Ctx.IsNew && !AppState.All().Any(a => a.Dir != Ctx.State.Dir);
        return Task.CompletedTask;
    }

    public override Task<bool> NextAsync()
    {
        var cfg = Ctx.State.Config;
        cfg.MountPoint = (string)_letter.SelectedItem!;
        cfg.VolumeLabel = string.IsNullOrWhiteSpace(_label.Text) ? "DriveGram" : _label.Text.Trim();
        cfg.NetworkDrive = _network.Checked;
        Ctx.State.Save();
        // the drives queued from the drive page get the same drive type
        for (int i = 0; i < Ctx.Queue.Count; i++)
            if (Ctx.Queue[i] is { Letter: not null, Network: null } q) Ctx.Queue[i] = q with { Network = _network.Checked };
        // the first drive on this PC starts DriveGram with Windows; later ones leave the user's choice (tray menu) alone
        if (Ctx.IsNew && !AppState.All().Any(a => a.Dir != Ctx.State.Dir))
            try { ShellIntegration.SetAutostart(Ctx.PresetAutostart ?? _autostart.Checked); }
        catch (Exception ex) { Log.Warn($"autostart: {ex.Message}"); }
        return Task.FromResult(true);
    }
}
