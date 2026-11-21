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
    public override string Subtitle => L.T("כונן ב־This PC שהקבצים שלו נשמרים, מוצפנים, בחשבון הטלגרם שלך.",
        "A drive in This PC whose files are stored, encrypted, in your Telegram account.");
    public override string NextText => L.T("בוא נתחיל", "Let's start");

    public WelcomePage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        bool another = ctx.IsNew && AppState.All().Count > 0;
        if (another)
            col.Controls.Add(Theme.Label(L.T(
                "כל כונן מקבל אות משלו וערוצים משלו בטלגרם. אפשר לשים אותו באותו חשבון של כונן קיים, או בחשבון אחר.",
                "Each drive gets its own letter and its own Telegram channels. It can live on the same account as an existing drive, or on another one."), maxWidth: 640));
        col.Controls.Add(Theme.Label(L.T("ההגדרה לוקחת כ־2 דקות:", "Setup takes about 2 minutes:"), Theme.Heading));
        string[] steps = ctx.UsesTelegram
            ? [L.T("פרטי API של טלגרם (מזהה חינמי שכל תוכנה צריכה)", "Telegram API details (a free ID every app needs)"),
               L.T("התחברות לחשבון: סריקת קוד QR מהטלפון, או קוד בהודעה", "Sign in: scan a QR code with your phone, or use a code"),
               L.T("סיסמה לכונן: מצפינה הכל, ורק אתה יודע אותה", "A drive password: it encrypts everything, only you know it"),
               L.T("אות לכונן והפעלה אוטומטית עם וינדוס", "A drive letter, and starting with Windows")]
            : [L.T("סיסמה לכונן (מצב ניסיון: הקבצים נשמרים בתיקייה מקומית)", "A drive password (trial mode: files are kept in a local folder)"),
               L.T("אות לכונן והפעלה אוטומטית עם וינדוס", "A drive letter, and starting with Windows")];
        for (int i = 0; i < steps.Length; i++)
            col.Controls.Add(Theme.Label($"  {i + 1}.  {steps[i]}"));

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
        col.Controls.Add(Theme.Label(L.T("3. העתק לכאן את api_id ואת api_hash:", "3. Copy api_id and api_hash here:")));

        col.Controls.Add(Theme.Label("api_id", Theme.Small, Theme.Muted));
        col.Controls.Add(_id);
        col.Controls.Add(Theme.Label("api_hash", Theme.Small, Theme.Muted));
        col.Controls.Add(_hash);

        var import = Theme.Secondary(L.T("ייבוא מקובץ env…", "Import from an .env file…"));
        import.Click += (_, _) => { try { ImportEnv(); } catch (Exception ex) { Ui.Error(ex.Message); } };
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
                L.T("api_id הוא מספר, ו־api_hash הוא 32 תווים (0-9, a-f).", "api_id is a number; api_hash is 32 characters (0-9, a-f)."));
        Changed(ok);
    }

    void ImportEnv()
    {
        using var dlg = new OpenFileDialog { Title = L.T("בחר קובץ .env", "Choose an .env file"), Filter = "env|*.env;.env|*.*|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var line in File.ReadAllLines(dlg.FileName))
        {
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0] == "API_ID") _id.Text = kv[1].Trim('"');
            if (kv[0] == "API_HASH") _hash.Text = kv[1].Trim('"');
        }
        if (!Valid)
        {
            _status.ForeColor = Theme.Error;
            _status.Text = L.T("לא נמצאו API_ID ו־API_HASH תקינים בקובץ.", "No valid API_ID and API_HASH in that file.");
        }
    }

    public override Task EnterAsync()
    {
        UpdateState();
        _autoSkip = !_entered && Valid; // already configured: straight to sign-in (Back still reaches this page)
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
    readonly Panel _account = Theme.Card(Color.FromArgb(0xEC, 0xF7, 0xEE), Color.FromArgb(0xB7, 0xDF, 0xC0));
    readonly Label _accountText = Theme.Label("", Theme.Body, Theme.Text, 600);
    readonly FlowLayoutPanel _signIn = Theme.Column();
    readonly RadioButton _byQr = MethodButton(L.T("סריקת קוד QR (מומלץ)", "Scan a QR code (recommended)"), true);
    readonly RadioButton _byPhone = MethodButton(L.T("מספר טלפון וקוד", "Phone number and code"), false);

    readonly FlowLayoutPanel _qr = Theme.Row();
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
        var frame = Theme.Card(Color.White, Theme.Border);
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
        _twoFa.Controls.Add(Theme.Label(L.T("לחשבון יש אימות דו־שלבי. הקלד את הסיסמה שלו:", "This account has two-step verification. Enter its password:"), Theme.Heading));
        _twoFa.Controls.AddRange([_twoFaHint, twoFaRow]);
        _twoFa.Visible = false;
        _twoFaOk.Click += async (_, _) => await SubmitTwoFaAsync();

        _signIn.Controls.AddRange([methods, _qr, _phone]);
        _known.Visible = false;
        col.Controls.AddRange([_account, _known, _signIn, _twoFa, _error, _enterRetry]);
        _enterRetry.Visible = false;
        _enterRetry.Click += async (_, _) => { Ctx.ResetClient(signOut: false); await EnterAsync(); };
        SetContent(col);

        _tick.Tick += (_, _) =>
        {
            var left = (int)Math.Ceiling((_qrExpires - DateTime.UtcNow).TotalSeconds);
            if (_qrTask is { IsCompleted: false } && _qrImage.Image is not null && left > 0)
                _qrState.Text = L.T($"ממתין לסריקה… הקוד מתחדש בעוד {left} שניות.", $"Waiting for the scan… a new code in {left} seconds.");
        };
        Ctx.AskPassword = AskPasswordFromQr;
        Changed(false);
    }

    // ── flow ─────────────────────────────────────────────────────────────────
    public override async Task EnterAsync()
    {
        _error.Text = "";
        _enterRetry.Visible = false;
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
            Ctx.ResetClient(signOut: true);
            _account.Visible = false;
            _signIn.Visible = true;
            ShowKnownAccounts();
            await ModeChangedAsync();
        }
        catch (NotSignedInException)
        {
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
    void ShowKnownAccounts()
    {
        _known.Controls.Clear();
        var accounts = TelegramAccounts.Known().Where(a => a.Key != Ctx.State.Config.AccountKey).ToList();
        _known.Visible = accounts.Count > 0;
        if (accounts.Count == 0) return;
        _known.Controls.Add(Theme.Label(L.T("חשבון שכבר מחובר במחשב הזה (בלי התחברות נוספת):", "An account already signed in on this PC (no new sign-in):"), Theme.Heading));
        var row = Theme.Row();
        foreach (var a in accounts)
        {
            var b = Theme.Secondary(a.Name);
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
    }

    public override Task<bool> NextAsync() => Task.FromResult(Ctx.User is not null && CanGoNext);

    void ShowSignedIn(TL.User user)
    {
        Ctx.User = user;
        _qrCts?.Cancel();
        _tick.Stop();
        _signIn.Visible = false;
        _twoFa.Visible = false;
        _error.Text = "";
        var name = $"{user.first_name} {user.last_name}".Trim();
        var handle = string.IsNullOrEmpty(user.username) ? "" : $"  ·  @{user.username}";
        var phone = string.IsNullOrEmpty(user.phone) ? "" : $"  ·  ‎+{user.phone}‎";
        _accountText.Text = L.T($"✓  מחובר כ־{name}{handle}{phone}", $"✓  Signed in as {name}{handle}{phone}");
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
            var user = await Task.Run(() => client.LoginWithQRCode(url => BeginInvoke(() => ShowQr(url)), logoutFirst: false, ct: ct), ct);
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

    void ShowQr(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var ms = new MemoryStream(new PngByteQRCode(data).GetGraphic(8));
        using var img = Image.FromStream(ms);
        var old = _qrImage.Image;
        _qrImage.Image = new Bitmap(img);
        old?.Dispose();
        _qrExpires = DateTime.UtcNow.AddSeconds(30);
        _qrState.Text = L.T("ממתין לסריקה… הקוד מתחדש בעוד 30 שניות.", "Waiting for the scan… a new code in 30 seconds.");
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
            UseWaitCursor = false;
        }
    }

    async Task SwitchAccountAsync()
    {
        if (Ui.Ask(L.T("להתנתק מהחשבון הנוכחי ולהתחבר עם חשבון אחר?", "Sign out of this account and use a different one?"),
                def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => Ctx.ResetClient(signOut: true));
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
    readonly CheckBox _show = new() { Text = L.T("הצג סיסמה", "Show password"), AutoSize = true, Font = Theme.Small };
    readonly FlowLayoutPanel _strength = Theme.Row();
    readonly Panel _meter = new() { Size = new Size(290, 6), BackColor = Theme.Border, Margin = new Padding(0, 8, 0, 0) };
    readonly Panel _meterFill = new() { Dock = L.Rtl ? DockStyle.Right : DockStyle.Left, Width = 0 };
    readonly Label _meterText = Theme.Label("", Theme.Small, Theme.Muted);
    readonly Panel _warning = Theme.Card(Theme.WarnBack, Theme.WarnBorder);
    readonly CheckBox _saved = new() { Text = L.T("שמרתי את הסיסמה במקום בטוח", "I saved the password somewhere safe"), AutoSize = true };
    readonly CheckBox _remember = new()
    {
        Text = L.T("לזכור את הסיסמה במחשב הזה (נדרש כדי שהכונן יעלה לבד עם וינדוס)", "Remember the password on this PC (needed for the drive to start with Windows)"),
        AutoSize = true, Checked = true,
    };
    readonly Label _error = Theme.Label("", Theme.Small, Theme.Error);
    Mode _mode = Mode.Checking;
    bool _wasVanished;
    DriveCandidate? _selected;   // null while "a new drive" is chosen
    ControlState? _ctrl;
    IRemote? _remote;

    public override string Title => L.T("הכונן", "The drive");
    public override string Subtitle => _mode switch
    {
        Mode.Create when _wasVanished => L.T("הכונן הקודם נמחק מטלגרם.", "The previous drive was deleted from Telegram."),
        Mode.Create => L.T("כונן חדש, וסיסמה שמצפינה אותו.", "A new drive, and the password that encrypts it."),
        Mode.Connect => L.T("חבר את המחשב הזה לכונן קיים.", "Connect this PC to an existing drive."),
        Mode.Connected => L.T("הכל מוכן כאן.", "All set here."),
        _ => L.T("רגע אחד…", "One moment…"),
    };
    public override int DemoVariants => 4;

    public DrivePage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        _connected.Controls.Add(Theme.Label(L.T("✓  המחשב הזה כבר מחובר לכונן, והסיסמה שמורה בו.", "✓  This PC is already connected to the drive, and its password is remembered."), Theme.Heading, Theme.Success));
        _connected.Controls.Add(Theme.Label(L.T("אפשר להמשיך לשלב האחרון.", "Continue to the last step."), Theme.Body, Theme.Muted));
        _connected.Visible = false;

        var vcol = Theme.Column();
        vcol.Controls.Add(Theme.Label(L.T(
            "⚠  הערוצים של הכונן שהיה מחובר כאן נמחקו מטלגרם (או שהחשבון יצא מהם), ואיתם הקבצים. אפשר לבחור כונן אחר או ליצור כונן חדש וריק.",
            "⚠  The channels of the drive connected here were deleted from Telegram (or the account left them), with its files. You can choose another drive or create a new, empty one."),
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
            "⚠  אי אפשר לשחזר את הסיסמה. בלעדיה אף אחד, כולל אתה, לא יוכל לקרוא את הקבצים, גם עם גישה מלאה לחשבון הטלגרם. שמור אותה במנהל סיסמאות או רשום אותה.",
            "⚠  The password cannot be recovered. Without it nobody, including you, can read the files, even with full access to the Telegram account. Keep it in a password manager or write it down."),
            Theme.Small, Theme.Text, 640));
        _name.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        _name.Font = Theme.Body;
        _form.Controls.AddRange([_intro, _nameLabel, _name, grid, _strength, _show, _warning, _saved, _remember, _error]);
        _form.Visible = false;

        col.Controls.AddRange([_checking, _driveRetry, _connected, _vanished, _choices, _form]);
        _driveRetry.Visible = false;
        _driveRetry.Click += async (_, _) => { Ctx.ResetClient(signOut: false); await EnterAsync(); };
        SetContent(col);

        _show.CheckedChanged += (_, _) => _pw.UseSystemPasswordChar = _pw2.UseSystemPasswordChar = !_show.Checked;
        _pw.TextChanged += (_, _) => UpdateState();
        _pw2.TextChanged += (_, _) => UpdateState();
        _name.TextChanged += (_, _) => UpdateState();
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
                catch (DriveVanishedException) { _wasVanished = true; }
            }

            var candidates = await Task.Run(() => TelegramRemote.DiscoverAsync(client));
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

    /// <summary>One radio button per drive found, plus "a new drive".</summary>
    void ShowChoices(List<DriveCandidate> candidates)
    {
        _choices.Controls.Clear();
        var onThisPc = AppState.All().Where(a => a.Dir != Ctx.State.Dir && a.Config.Channels is not null)
            .ToDictionary(a => a.Config.Channels!.JournalId, a => a.Config.MountPoint);
        RadioButton? first = null;
        if (candidates.Count > 0)
            _choices.Controls.Add(Theme.Label(L.T("כוננים שהחשבון הזה יכול לפתוח:", "Drives this account can open:"), Theme.Heading));
        foreach (var c in candidates)
        {
            string? why = c.Channels is null ? L.T("ערוץ הקבצים שלו חסר", "its data channel is missing")
                : !c.HasVault ? L.T("היצירה שלו לא הושלמה", "it was never finished")
                : onThisPc.TryGetValue(c.Channels.JournalId, out var letter) ? L.T($"כבר מחובר במחשב הזה כ־{letter}", $"already on this PC as {letter}")
                : null;
            var owner = c.Owned ? L.T("שלך", "yours") : L.T("אתה מנהל בו (גיבוי)", "you are an admin (backup)");
            var rb = Choice($"{c.Name}  ·  {owner}{(why is null ? "" : $"  —  {why}")}", c, enabled: why is null);
            if (why is null) first ??= rb;
        }
        var create = Choice(L.T("כונן חדש בחשבון הזה", "A new drive on this account"), null, enabled: true);
        create.Font = new Font(Theme.Body, FontStyle.Bold);
        _choices.Visible = true;
        (first ?? create).Checked = true;
        if (_wasVanished) _vanished.Visible = true;
    }

    RadioButton Choice(string text, DriveCandidate? candidate, bool enabled)
    {
        var rb = new RadioButton { Text = text, AutoSize = true, Enabled = enabled, Tag = candidate, Margin = new Padding(0, 2, 0, 4) };
        rb.CheckedChanged += (_, _) => { if (rb.Checked) Select((DriveCandidate?)rb.Tag); };
        _choices.Controls.Add(rb);
        return rb;
    }

    void Select(DriveCandidate? c)
    {
        _selected = c;
        _ctrl = null;
        _error.Text = "";
        if (c is null)
        {
            var taken = AppState.All().Select(a => a.Config.VolumeLabel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _name.Text = Enumerable.Range(1, 99).Select(i => i == 1 ? "DriveGram" : $"DriveGram {i}").First(n => !taken.Contains(n));
            SetMode(Mode.Create);
        }
        else SetMode(Mode.Connect);
    }

    // ── form ─────────────────────────────────────────────────────────────────
    public override void Demo(int variant)
    {
        _wasVanished = variant == 3;
        if (variant == 0)
        {
            ShowChoices([
                new DriveCandidate("a", "DriveGram", new TelegramChannels(1, 1, 2, 2), true, true, true),
                new DriveCandidate("b", "Photos", new TelegramChannels(3, 3, 4, 4), true, true, true),
                new DriveCandidate("c", "DriveGram", new TelegramChannels(5, 5, 6, 6), false, true, false)]);
            _pw.Text = "secret";
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
        _confirmLabel.Visible = _pw2.Visible = _strength.Visible = _warning.Visible = _saved.Visible = create;
        _intro.Text = create
            ? L.T("בחר סיסמה לכונן, לפחות 10 תווים. היא מצפינה כל קובץ לפני שהוא עולה לטלגרם.",
                  "Choose a drive password, at least 10 characters. It encrypts every file before it goes to Telegram.")
            : L.T("הקלד את הסיסמה של הכונן (או את מפתח השחזור שלו) כדי לחבר אליו את המחשב הזה.", "Enter the drive's password (or its recovery key) to connect this PC to it.");
        if (_form.Visible) (create && _name.Visible ? _name : _pw).Focus();
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
        if (_mode == Mode.Connected) ok = true;
        else if (_mode == Mode.Connect) ok = _pw.Text.Length > 0 && vanishedOk;
        else if (_mode == Mode.Create)
        {
            var (score, text, color) = Strength(_pw.Text);
            _meterFill.Width = _pw.Text.Length == 0 ? 0 : _meter.Width * score / 4;
            _meterFill.BackColor = color;
            _meterText.Text = _pw.Text.Length == 0 ? "" : L.T($"חוזק: {text}", $"Strength: {text}");
            _meterText.ForeColor = color;
            bool match = _pw2.Text.Length == 0 || _pw2.Text == _pw.Text;
            _error.Text = match ? "" : L.T("הסיסמאות לא זהות.", "The passwords do not match.");
            ok = _pw.Text.Length >= 10 && _pw2.Text == _pw.Text && _saved.Checked && vanishedOk &&
                 (!_name.Visible || _name.Text.Trim().Length > 0);
        }
        else ok = false;
        Changed(ok);
    }

    // ── next ─────────────────────────────────────────────────────────────────
    public override async Task<bool> NextAsync()
    {
        if (_mode == Mode.Connected) return true;
        _error.Text = "";
        var pw = _pw.Text;
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
            if (pending > 0 || unsent > 0)
            {
                _error.Text = L.T(
                    $"בכונן הקודם יש {pending} קבצים שעוד לא עלו ו־{unsent} שינויים שעוד לא נשלחו, והם קיימים רק במחשב הזה. חבר אותו קודם (אם צריך, התחבר מחדש) וחכה שיעלו, או הסר אותו מהמחשב.",
                    $"The previous drive has {pending} files not yet uploaded and {unsent} changes not yet sent, existing only on this PC. Connect it first (sign in again if needed) and let them upload, or remove it from this PC.");
                return false;
            }
        }
        void ResetIfRebinding()
        {
            if (!rebinding) return;
            Ctx.State.ResetLocalData();
            rebinding = false;
            _wasVanished = false;
        }

        Keys keys;
        if (_mode == Mode.Create)
        {
            DriveIdentity? identity = null;
            if (Ctx.UsesTelegram)
            {
                var name = _name.Text.Trim();
                var driveId = Guid.NewGuid().ToString("N");
                var client = Ctx.GetClient();
                var channels = await Task.Run(() => TelegramRemote.CreateChannelsAsync(client, name, driveId));
                await UseRemoteAsync(await Task.Run(() => TelegramRemote.OpenAsync(client, channels, ownsClient: false)));
                identity = new DriveIdentity(driveId, name, channels.DataId);
                cfg.Channels = channels;
                cfg.DriveId = driveId;
                cfg.VolumeLabel = name;
            }
            var remote = _remote!;
            keys = await Task.Run(() => SyncService.CreateDriveAsync(remote, pw, default, identity: identity));
            ResetIfRebinding();
            Log.Info($"setup: created drive '{cfg.VolumeLabel}' ({Ctx.State.Id})");
        }
        else
        {
            if (_selected is { Channels: { } chosen } c)
            {
                await UseRemoteAsync(await Task.Run(() => TelegramRemote.OpenAsync(Ctx.GetClient(), chosen, ownsClient: false)));
                _ctrl = await SyncService.ReadControlAsync(_remote!, default);
                if (_ctrl is null) { _error.Text = L.T("לכונן הזה אין רשומת בקרה.", "This drive has no control record."); return false; }
                cfg.Channels = chosen;
                cfg.DriveId = c.DriveId ?? "";
                if (cfg.VolumeLabel is "" or "DriveGram") cfg.VolumeLabel = c.Name;
            }
            try { keys = await Task.Run(() => Keys.UnlockAny(_ctrl!.Vault, pw)); } // the password, or the recovery key
            catch (WrongPasswordException)
            {
                _error.Text = L.T("הסיסמה שגויה.", "Wrong password.");
                _pw.SelectAll();
                _pw.Focus();
                return false;
            }
            ResetIfRebinding();
        }
        Ctx.State.Save();
        if (_remember.Checked) Ctx.State.RememberMaster(keys.ExportMaster());
        else Ctx.State.ForgetMaster();
        return true;
    }
}

// ── 5. Finish ───────────────────────────────────────────────────────────────
sealed class FinishPage : WizardPage
{
    readonly ComboBox _letter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Font = Theme.Body, RightToLeft = RightToLeft.No };
    readonly TextBox _label = Theme.Input(240);
    readonly CheckBox _autostart = new() { Text = L.T("הפעל את DriveGram אוטומטית כשנכנסים לוינדוס", "Start DriveGram automatically when I sign in to Windows"), AutoSize = true, Checked = true };
    readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, 620);

    public override string Title => L.T("כמעט סיימנו", "Almost done");
    public override string Subtitle => L.T("איך הכונן יופיע במחשב.", "How the drive appears on this PC.");
    public override string NextText => L.T("סיום וחיבור הכונן", "Finish and connect");

    public FinishPage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        var summaryCard = Theme.Card(Theme.Surface, Theme.Border);
        summaryCard.Controls.Add(_summary);
        col.Controls.Add(summaryCard);
        col.Controls.Add(Theme.Label(L.T("אות הכונן:", "Drive letter:"), Theme.Small, Theme.Muted));
        col.Controls.Add(_letter);
        col.Controls.Add(Theme.Label(L.T("שם הכונן ב־This PC:", "Name in This PC:"), Theme.Small, Theme.Muted));
        col.Controls.Add(_label);
        col.Controls.Add(_autostart);
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
        // the first drive: on; otherwise keep the choice the user already made
        _autostart.Checked = (Ctx.IsNew && AppState.All().Count == 0) || ShellIntegration.AutostartEnabled;

        var account = Ctx.State.Folder ?? (string.IsNullOrEmpty(cfg.AccountName) ? null : cfg.AccountName);
        _summary.Text = (account is null ? "" : L.T($"חשבון: {account}\n", $"Account: {account}\n")) +
                        L.T("הכונן מוכן. הקבצים יעלו מוצפנים ברקע, ואייקון ליד השעון יראה מה המצב.",
                            "The drive is ready. Files upload encrypted in the background; an icon next to the clock shows what is happening.");
        return Task.CompletedTask;
    }

    public override Task<bool> NextAsync()
    {
        var cfg = Ctx.State.Config;
        cfg.MountPoint = (string)_letter.SelectedItem!;
        cfg.VolumeLabel = string.IsNullOrWhiteSpace(_label.Text) ? "DriveGram" : _label.Text.Trim();
        Ctx.State.Save();
        try { ShellIntegration.SetAutostart(_autostart.Checked); }
        catch (Exception ex) { Log.Warn($"autostart: {ex.Message}"); }
        return Task.FromResult(true);
    }
}
