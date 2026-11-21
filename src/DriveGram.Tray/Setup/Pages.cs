using System.Diagnostics;
using System.Text.RegularExpressions;
using DriveGram.Crypto;
using DriveGram.Host;
using DriveGram.Remote;
using DriveGram.Sync;
using QRCoder;
using Keys = DriveGram.Crypto.Keys;

namespace DriveGram.Tray.Setup;

// ── 1. Welcome ──────────────────────────────────────────────────────────────
sealed class WelcomePage : WizardPage
{
    public override string Title => "ברוך הבא ל־DriveGram";
    public override string Subtitle => "כונן ב־This PC שהקבצים שלו נשמרים, מוצפנים, בחשבון הטלגרם שלך.";
    public override string NextText => "בוא נתחיל";

    public WelcomePage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        col.Controls.Add(Theme.Label("ההגדרה לוקחת כ־2 דקות ונעשית פעם אחת במחשב הזה:", Theme.Heading));
        string[] steps = ctx.UsesTelegram
            ? ["פרטי API של טלגרם (מזהה חינמי שכל תוכנה צריכה)",
               "התחברות לחשבון: סריקת קוד QR מהטלפון, או קוד בהודעה",
               "סיסמה לכונן: מצפינה הכל, ורק אתה יודע אותה",
               "אות לכונן והפעלה אוטומטית עם וינדוס"]
            : ["סיסמה לכונן (מצב ניסיון: הקבצים נשמרים בתיקייה מקומית)", "אות לכונן והפעלה אוטומטית עם וינדוס"];
        for (int i = 0; i < steps.Length; i++)
            col.Controls.Add(Theme.Label($"  {i + 1}.  {steps[i]}"));

        if (ctx.UsesTelegram)
        {
            var tip = Theme.Card(Theme.Surface, Theme.Border);
            tip.Controls.Add(Theme.Label(
                "המלצה: השתמש בחשבון טלגרם ייעודי ל־DriveGram ולא בחשבון האישי. טלגרם יכול להגביל או לחסום חשבונות שמשמשים כאחסון כבד.",
                Theme.Small, Theme.Muted, 600));
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

    public override string Title => "פרטי API של טלגרם";
    public override string Subtitle => "טלגרם דורש מכל תוכנה מזהה משלה. מקבלים אותו בחינם, פעם אחת.";
    public override bool Skip => _autoSkip;

    public ApiPage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        var link = new LinkLabel { Text = "my.telegram.org", AutoSize = true, Font = Theme.Body, LinkColor = Theme.Accent };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://my.telegram.org/apps") { UseShellExecute = true });
        var row = Theme.Row();
        row.Controls.AddRange([Theme.Label("1. היכנס עם מספר הטלפון של החשבון ל־"), link]);
        col.Controls.Add(row);
        col.Controls.Add(Theme.Label("2. בחר API development tools, ומלא טופס קצר. השם לא משנה, למשל DriveGram."));
        col.Controls.Add(Theme.Label("3. העתק לכאן את api_id ואת api_hash:"));

        col.Controls.Add(Theme.Label("api_id", Theme.Small, Theme.Muted));
        col.Controls.Add(_id);
        col.Controls.Add(Theme.Label("api_hash", Theme.Small, Theme.Muted));
        col.Controls.Add(_hash);

        var import = Theme.Secondary("ייבוא מקובץ env…");
        import.Click += (_, _) => ImportEnv();
        col.Controls.Add(import);
        col.Controls.Add(_status);
        SetContent(col);

        _id.Text = ctx.State.Config.ApiId == 0 ? "" : ctx.State.Config.ApiId.ToString();
        _hash.Text = ctx.State.Config.ApiHash;
        _id.TextChanged += (_, _) => UpdateState();
        _hash.TextChanged += (_, _) => UpdateState();
    }

    bool Valid => Regex.IsMatch(_id.Text.Trim(), @"^\d{3,12}$") && Regex.IsMatch(_hash.Text.Trim(), "^[0-9a-fA-F]{32}$");

    void UpdateState()
    {
        bool ok = Valid;
        _status.ForeColor = ok ? Theme.Success : Theme.Muted;
        _status.Text = ok ? "✓ הפרטים נראים תקינים" :
            (_id.Text.Length + _hash.Text.Length == 0 ? "" : "api_id הוא מספר, ו־api_hash הוא 32 תווים (0-9, a-f).");
        Changed(ok);
    }

    void ImportEnv()
    {
        using var dlg = new OpenFileDialog { Title = "בחר קובץ .env", Filter = "env|*.env;.env|כל הקבצים|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var line in File.ReadAllLines(dlg.FileName))
        {
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0] == "API_ID") _id.Text = kv[1].Trim('"');
            if (kv[0] == "API_HASH") _hash.Text = kv[1].Trim('"');
        }
        if (!Valid) { _status.ForeColor = Theme.Error; _status.Text = "לא נמצאו API_ID ו־API_HASH תקינים בקובץ."; }
    }

    public override Task EnterAsync()
    {
        UpdateState();
        _autoSkip = !_entered && Valid; // already configured: go straight to sign-in (Back still reaches this page)
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
            Ctx.State.Save();
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
    readonly RadioButton _byQr = new() { Text = "סריקת קוד QR (מומלץ)", AutoSize = true, Checked = true, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4) };
    readonly RadioButton _byPhone = new() { Text = "מספר טלפון וקוד", AutoSize = true, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4) };

    readonly FlowLayoutPanel _qr = Theme.Row();
    readonly PictureBox _qrImage = new() { Size = new Size(230, 230), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Margin = new Padding(0, 0, 0, 0) };
    readonly Label _qrState = Theme.Label("מכין קוד…", Theme.Small, Theme.Muted, 360);
    readonly Button _qrRetry = Theme.Secondary("נסה שוב");

    readonly FlowLayoutPanel _phone = Theme.Column();
    readonly TextBox _phoneBox = Theme.Input(240);
    readonly Button _sendCode = Theme.Primary("שלח קוד");
    readonly FlowLayoutPanel _codeRow = Theme.Row();
    readonly TextBox _codeBox = Theme.Input(140);
    readonly Button _verify = Theme.Primary("אמת");

    readonly FlowLayoutPanel _twoFa = Theme.Column();
    readonly Label _twoFaHint = Theme.Label("", Theme.Small, Theme.Muted);
    readonly TextBox _twoFaBox = Theme.Input(260, password: true);
    readonly Button _twoFaOk = Theme.Primary("אישור");

    readonly Label _error = Theme.Label("", Theme.Small, Theme.Error, 640);
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    CancellationTokenSource? _qrCts;
    Task? _qrTask;
    DateTime _qrExpires;
    TaskCompletionSource<string?>? _pwWait;
    int _pwAsks;

    public override string Title => "התחברות לטלגרם";
    public override string Subtitle => "החשבון שבו יישמרו הקבצים. החיבור נשמר מוצפן, רק למשתמש שלך במחשב הזה.";

    public LoginPage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();

        // signed-in card
        var switchAcc = new LinkLabel { Text = "התחבר עם חשבון אחר", AutoSize = true, LinkColor = Theme.Accent, Font = Theme.Small };
        switchAcc.LinkClicked += async (_, _) => await SwitchAccountAsync();
        var accCol = Theme.Column();
        accCol.Controls.AddRange([_accountText, switchAcc]);
        _account.Controls.Add(accCol);
        _account.Visible = false;

        // method switch
        var methods = Theme.Row();
        foreach (var rb in new[] { _byQr, _byPhone })
        {
            rb.FlatAppearance.CheckedBackColor = Color.FromArgb(0xE3, 0xF2, 0xFB);
            rb.FlatAppearance.BorderColor = Theme.Border;
            rb.Margin = new Padding(0, 0, 0, 0);
            rb.CheckedChanged += async (_, _) => { if (rb.Checked) await ModeChangedAsync(); };
        }
        methods.Controls.AddRange([_byQr, _byPhone]);
        methods.Margin = new Padding(0, 0, 0, 14);

        // QR
        var qrSide = Theme.Column();
        qrSide.Padding = new Padding(18, 0, 0, 0);
        qrSide.Controls.Add(Theme.Label("בטלפון:", Theme.Heading));
        qrSide.Controls.Add(Theme.Label("1. פתח את טלגרם", maxWidth: 360));
        qrSide.Controls.Add(Theme.Label("2. הגדרות ← מכשירים ← קישור מכשיר שולחני", maxWidth: 360));
        qrSide.Controls.Add(Theme.Label("3. כוון את המצלמה לקוד", maxWidth: 360));
        qrSide.Controls.Add(_qrState);
        _qrRetry.Visible = false;
        _qrRetry.Click += (_, _) => StartQr();
        qrSide.Controls.Add(_qrRetry);
        var frame = Theme.Card(Color.White, Theme.Border);
        frame.Padding = new Padding(6);
        frame.Controls.Add(_qrImage);
        _qr.Controls.AddRange([frame, qrSide]);

        // phone
        _phoneBox.PlaceholderText = "+972501234567";
        var phoneRow = Theme.Row();
        phoneRow.Controls.AddRange([_phoneBox, _sendCode]);
        _codeBox.PlaceholderText = "12345";
        _codeRow.Controls.AddRange([_codeBox, _verify]);
        _codeRow.Visible = false;
        _phone.Controls.Add(Theme.Label("מספר הטלפון של החשבון, בפורמט בינלאומי:", Theme.Small, Theme.Muted));
        _phone.Controls.Add(phoneRow);
        _phone.Controls.Add(Theme.Label(
            "הקוד נשלח לאפליקציית טלגרם עצמה, לצ'אט בשם \"Telegram\" עם ✓ כחול (בטלפון או במחשב). בדרך כלל הוא לא מגיע ב־SMS. כל שליחה חדשה מבטלת את הקוד הקודם.",
            Theme.Small, Theme.Muted, 620));
        _phone.Controls.Add(_codeRow);
        _phone.Visible = false;
        _sendCode.Click += async (_, _) => await SendCodeAsync();
        _verify.Click += async (_, _) => await VerifyCodeAsync();

        // two-step verification (used by both methods)
        var twoFaRow = Theme.Row();
        twoFaRow.Controls.AddRange([_twoFaBox, _twoFaOk]);
        _twoFa.Controls.Add(Theme.Label("לחשבון יש אימות דו־שלבי. הקלד את הסיסמה שלו:", Theme.Heading));
        _twoFa.Controls.AddRange([_twoFaHint, twoFaRow]);
        _twoFa.Visible = false;
        _twoFaOk.Click += async (_, _) => await SubmitTwoFaAsync();

        _signIn.Controls.AddRange([methods, _qr, _phone]);
        col.Controls.AddRange([_account, _signIn, _twoFa, _error]);
        SetContent(col);

        _tick.Tick += (_, _) =>
        {
            var left = (int)Math.Ceiling((_qrExpires - DateTime.UtcNow).TotalSeconds);
            if (_qrTask is { IsCompleted: false } && _qrImage.Image is not null && left > 0)
                _qrState.Text = $"ממתין לסריקה… הקוד מתחדש בעוד {left} שניות.";
        };
        Ctx.AskPassword = AskPasswordFromQr;
        Changed(false);
    }

    // ── flow ─────────────────────────────────────────────────────────────────
    public override async Task EnterAsync()
    {
        _error.Text = "";
        if (Ctx.User is not null) { ShowSignedIn(Ctx.User); return; }
        try
        {
            var user = await Task.Run(() => TelegramRemote.ResumeAsync(Ctx.GetClient()));
            ShowSignedIn(user);
        }
        catch (NotSignedInException)
        {
            _account.Visible = false;
            _signIn.Visible = true;
            await ModeChangedAsync();
        }
    }

    public override void LeavePage()
    {
        _qrCts?.Cancel();
        _tick.Stop();
        _pwWait?.TrySetResult(null);
    }

    public override Task<bool> NextAsync() => Task.FromResult(Ctx.User is not null);

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
        _accountText.Text = $"✓  מחובר כ־{name}{handle}{phone}";
        _account.Visible = true;
        Changed(true);
    }

    void ShowError(Exception ex) => _error.Text = TelegramErrors.Hebrew(ex);

    /// <summary>Snapshot aid: show the sign-in screens without touching Telegram.</summary>
    public override void Demo(int variant)
    {
        _account.Visible = false;
        _signIn.Visible = true;
        if (variant == 0)
        {
            _qr.Visible = true;
            _phone.Visible = false;
            ShowQr("tg://login?token=AQTllwFrRdZaXmvADMPwhnSW5XRrCpa0PEE_rdEpvHIrMA-DEMO");
            _qrState.Text = "ממתין לסריקה… הקוד מתחדש בעוד 23 שניות.";
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
            _error.Text = "הקוד שגוי. ודא שאתה מקליד את הקוד האחרון שקיבלת (כל שליחה חדשה מבטלת את הקודם).";
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
        _qrState.Text = "מכין קוד…";
        _qrCts = new CancellationTokenSource();
        var ct = _qrCts.Token;
        _pwAsks = 0;
        _tick.Start();
        _qrTask = RunQrAsync(ct, retryAfterRestart: true);
    }

    async Task RunQrAsync(CancellationToken ct, bool retryAfterRestart)
    {
        try
        {
            var client = Ctx.GetClient();
            var user = await Task.Run(() => client.LoginWithQRCode(url => BeginInvoke(() => ShowQr(url)), logoutFirst: false, ct: ct), ct);
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
            _qrState.Text = "הקוד לא פעיל.";
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
        _qrState.Text = "ממתין לסריקה… הקוד מתחדש בעוד 30 שניות.";
    }

    /// <summary>The library asks for the 2FA password on a worker thread during QR sign-in.</summary>
    string? AskPasswordFromQr()
    {
        var wait = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () =>
        {
            _pwWait = wait;
            _qr.Visible = false;
            await ShowTwoFaAsync(_pwAsks++ > 0 ? "הסיסמה שגויה, נסה שוב." : null);
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
            _twoFaHint.Text = string.IsNullOrEmpty(info.hint) ? "" : $"רמז: {info.hint}";
        }
        catch { _twoFaHint.Text = ""; }
        _twoFaBox.Focus();
    }

    async Task SubmitTwoFaAsync()
    {
        var pw = _twoFaBox.Text;
        if (pw.Length == 0) return;
        if (_pwWait is { } wait) // QR flow is waiting on us
        {
            _pwWait = null;
            _twoFa.Visible = false;
            _qr.Visible = true;
            _qrState.Text = "מאמת…";
            wait.TrySetResult(pw);
            return;
        }
        await RunLoginStepAsync(_twoFaOk, pw); // phone flow
    }

    async Task SendCodeAsync()
    {
        var phone = new string(_phoneBox.Text.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (phone.Length < 8) { _error.Text = "הקלד מספר טלפון מלא, למשל ‎+972501234567‎."; return; }
        if (!phone.StartsWith('+') && phone.StartsWith('0')) phone = "+972" + phone[1..]; // local Israeli format
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
                    ShowSignedIn(client.User!);
                    break;
                case "verification_code":
                    _codeRow.Visible = true;
                    _codeBox.Text = "";
                    _codeBox.Focus();
                    break;
                case "password":
                    _codeRow.Visible = false;
                    await ShowTwoFaAsync(_twoFa.Visible ? "הסיסמה שגויה, נסה שוב." : null);
                    break;
                case "name":
                    _error.Text = "אין חשבון טלגרם למספר הזה. צור חשבון באפליקציית טלגרם ונסה שוב.";
                    break;
                default:
                    _error.Text = $"טלגרם ביקש '{next}', שעדיין לא נתמך כאן.";
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
        if (MessageBox.Show("להתנתק מהחשבון הנוכחי ולהתחבר עם חשבון אחר?", "DriveGram", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading)
            != DialogResult.Yes) return;
        await Task.Run(() => Ctx.ResetClient(signOut: true));
        Ctx.State.Config.Channels = null; // the drive lives on the other account
        Ctx.State.Save();
        Changed(false);
        await EnterAsync();
    }
}

// ── 4. The drive ────────────────────────────────────────────────────────────
sealed class DrivePage : WizardPage
{
    enum Mode { Checking, Connected, Connect, Create }

    readonly Label _checking = Theme.Label("בודק אם כבר יש כונן DriveGram בחשבון…", Theme.Body, Theme.Muted);
    readonly FlowLayoutPanel _connected = Theme.Column();
    readonly FlowLayoutPanel _form = Theme.Column();
    readonly Label _intro = Theme.Label("", Theme.Body, Theme.Text, 640);
    readonly TextBox _pw = Theme.Input(320, password: true);
    readonly Label _confirmLabel = Theme.Label("הקלד שוב:", Theme.Small, Theme.Muted);
    readonly Label _pwLabel;
    readonly FlowLayoutPanel _strength;
    readonly TextBox _pw2 = Theme.Input(320, password: true);
    readonly CheckBox _show = new() { Text = "הצג סיסמה", AutoSize = true, Font = Theme.Small };
    readonly Panel _meter = new() { Size = new Size(290, 6), BackColor = Theme.Border, Margin = new Padding(0, 0, 0, 4) };
    readonly Panel _meterFill = new() { Dock = DockStyle.Left, Width = 0 };
    readonly Label _meterText = Theme.Label("", Theme.Small, Theme.Muted);
    readonly Panel _warning = Theme.Card(Theme.WarnBack, Theme.WarnBorder);
    readonly CheckBox _saved = new() { Text = "שמרתי את הסיסמה במקום בטוח", AutoSize = true };
    readonly CheckBox _remember = new() { Text = "לזכור את הסיסמה במחשב הזה (נדרש כדי שהכונן יעלה לבד עם וינדוס)", AutoSize = true, Checked = true };
    readonly Label _error = Theme.Label("", Theme.Small, Theme.Error);
    Mode _mode = Mode.Checking;
    ControlState? _ctrl;
    IRemote? _remote;

    public override string Title => "הכונן";
    public override string Subtitle => _mode switch
    {
        Mode.Create => "יצירת כונן חדש וסיסמה שמצפינה אותו.",
        Mode.Connect => "בחשבון כבר יש כונן. חבר אליו את המחשב הזה.",
        Mode.Connected => "הכל מוכן כאן.",
        _ => "רגע אחד…",
    };

    public DrivePage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        _connected.Controls.Add(Theme.Label("✓  המחשב הזה כבר מחובר לכונן, והסיסמה שמורה בו.", Theme.Heading, Theme.Success));
        _connected.Controls.Add(Theme.Label("אפשר להמשיך לשלב האחרון.", Theme.Body, Theme.Muted));
        _connected.Visible = false;

        _meter.Controls.Add(_meterFill);
        _pw.Width = _pw2.Width = 290;
        _pwLabel = Theme.Label("סיסמה:", Theme.Small, Theme.Muted);
        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Margin = new Padding(0) };
        grid.Controls.Add(_pwLabel, 0, 0);
        grid.Controls.Add(_confirmLabel, 1, 0);
        grid.Controls.Add(_pw, 0, 1);
        grid.Controls.Add(_pw2, 1, 1);
        _pw.Margin = new Padding(0, 0, 0, 6);
        _pw2.Margin = new Padding(16, 0, 0, 6);
        _confirmLabel.Margin = new Padding(16, 0, 0, 4);
        _strength = Theme.Row();
        _meter.Margin = new Padding(0, 8, 0, 0);
        _strength.Controls.AddRange([_meter, _meterText]);
        _meterText.Margin = new Padding(10, 0, 0, 0);
        _warning.Controls.Add(Theme.Label(
            "⚠  אי אפשר לשחזר את הסיסמה. בלעדיה אף אחד, כולל אתה, לא יוכל לקרוא את הקבצים, גם עם גישה מלאה לחשבון הטלגרם. שמור אותה במנהל סיסמאות או רשום אותה.",
            Theme.Small, Theme.Text, 640));
        _form.Controls.AddRange([_intro, grid, _strength, _show, _warning, _saved, _remember, _error]);
        _form.Visible = false;

        col.Controls.AddRange([_checking, _connected, _form]);
        SetContent(col);

        _show.CheckedChanged += (_, _) => _pw.UseSystemPasswordChar = _pw2.UseSystemPasswordChar = !_show.Checked;
        _pw.TextChanged += (_, _) => UpdateState();
        _pw2.TextChanged += (_, _) => UpdateState();
        _saved.CheckedChanged += (_, _) => UpdateState();
        Changed(false);
    }

    public override async Task EnterAsync()
    {
        _error.Text = "";
        SetMode(Mode.Checking);
        try
        {
            if (Ctx.UsesTelegram)
            {
                var client = Ctx.GetClient();
                bool exists = Ctx.Remote is not null ||
                              await Task.Run(() => TelegramRemote.DriveExistsAsync(client, Ctx.State.Config.Channels));
                if (exists && Ctx.Remote is null) Ctx.Remote = await Task.Run(() => OpenRemoteAsync(create: false));
                _remote = Ctx.Remote;
            }
            else _remote = new FolderRemote(Ctx.State.Folder!);

            _ctrl = _remote is null ? null : await SyncService.ReadControlAsync(_remote, default);
            if (_ctrl is null) { SetMode(Mode.Create); return; }
            if (Ctx.State.RememberedMaster() is { } master)
            {
                try { Keys.FromMaster(_ctrl.Vault, master); SetMode(Mode.Connected); return; }
                catch (WrongPasswordException) { Ctx.State.ForgetMaster(); }
            }
            SetMode(Mode.Connect);
        }
        catch (Exception ex)
        {
            _checking.ForeColor = Theme.Error;
            _checking.Text = TelegramErrors.Hebrew(ex);
        }
    }

    public override void Demo(int variant)
    {
        SetMode(variant == 0 ? Mode.Create : Mode.Connect);
        _pw.Text = variant == 0 ? "Correct-Horse-7" : "secret";
        _pw2.Text = variant == 0 ? "Correct-Horse-7" : "";
        if (variant == 1) _error.Text = "הסיסמה שגויה.";
    }

    async Task<TelegramRemote> OpenRemoteAsync(bool create)
    {
        var remote = await TelegramRemote.OpenAsync(Ctx.GetClient(), Ctx.State.Config.Channels, create);
        Ctx.State.Config.Channels = remote.Channels;
        Ctx.State.Save();
        return remote;
    }

    void SetMode(Mode mode)
    {
        _mode = mode;
        _checking.Visible = mode == Mode.Checking;
        _checking.ForeColor = Theme.Muted;
        _connected.Visible = mode == Mode.Connected;
        _form.Visible = mode is Mode.Connect or Mode.Create;
        bool create = mode == Mode.Create;
        _confirmLabel.Visible = _pw2.Visible = _strength.Visible = _warning.Visible = _saved.Visible = create;
        _intro.Text = create
            ? "בחר סיסמה לכונן, לפחות 10 תווים. היא מצפינה כל קובץ לפני שהוא עולה לטלגרם."
            : "נמצא כונן DriveGram בחשבון. הקלד את הסיסמה שלו כדי לחבר את המחשב הזה.";
        if (_form.Visible) _pw.Focus();
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
            <= 0 => (1, pw.Length < 10 ? "קצרה מדי" : "חלשה", Theme.Error),
            1 => (2, "בינונית", Color.FromArgb(0xE8, 0x8F, 0x1A)),
            2 or 3 => (3, "טובה", Theme.Accent),
            _ => (4, "חזקה מאוד", Theme.Success),
        };
    }

    void UpdateState()
    {
        bool ok;
        if (_mode == Mode.Connected) ok = true;
        else if (_mode == Mode.Connect) ok = _pw.Text.Length > 0;
        else if (_mode == Mode.Create)
        {
            var (score, text, color) = Strength(_pw.Text);
            _meterFill.Width = _pw.Text.Length == 0 ? 0 : _meter.Width * score / 4;
            _meterFill.BackColor = color;
            _meterText.Text = _pw.Text.Length == 0 ? "" : $"חוזק: {text}";
            _meterText.ForeColor = color;
            bool match = _pw2.Text.Length == 0 || _pw2.Text == _pw.Text;
            _error.Text = match ? "" : "הסיסמאות לא זהות.";
            ok = _pw.Text.Length >= 10 && _pw2.Text == _pw.Text && _saved.Checked;
        }
        else ok = false;
        Changed(ok);
    }

    public override async Task<bool> NextAsync()
    {
        if (_mode == Mode.Connected) return true;
        _error.Text = "";
        var pw = _pw.Text;
        Keys keys;
        if (_mode == Mode.Create)
        {
            if (Ctx.UsesTelegram && Ctx.Remote is null)
            {
                Ctx.Remote = await Task.Run(() => OpenRemoteAsync(create: true));
                _remote = Ctx.Remote;
            }
            keys = await Task.Run(() => SyncService.CreateDriveAsync(_remote!, pw, default));
            Log.Info("setup: created a new drive");
        }
        else
        {
            try { keys = await Task.Run(() => Keys.Unlock(_ctrl!.Vault, pw)); }
            catch (WrongPasswordException)
            {
                _error.Text = "הסיסמה שגויה.";
                _pw.SelectAll();
                _pw.Focus();
                return false;
            }
        }
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
    readonly CheckBox _autostart = new() { Text = "הפעל את DriveGram אוטומטית כשנכנסים לוינדוס", AutoSize = true, Checked = true };
    readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, 620);

    public override string Title => "כמעט סיימנו";
    public override string Subtitle => "איך הכונן יופיע במחשב.";
    public override string NextText => "סיום וחיבור הכונן";

    public FinishPage(SetupContext ctx) : base(ctx)
    {
        var col = Theme.Column();
        var summaryCard = Theme.Card(Theme.Surface, Theme.Border);
        summaryCard.Controls.Add(_summary);
        col.Controls.Add(summaryCard);
        col.Controls.Add(Theme.Label("אות הכונן:", Theme.Small, Theme.Muted));
        col.Controls.Add(_letter);
        col.Controls.Add(Theme.Label("שם הכונן ב־This PC:", Theme.Small, Theme.Muted));
        col.Controls.Add(_label);
        col.Controls.Add(_autostart);
        SetContent(col);
    }

    public override Task EnterAsync()
    {
        var cfg = Ctx.State.Config;
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        var current = char.ToUpperInvariant(cfg.MountPoint[0]);
        var free = "TUVWXYZSRQPONMLKJIHGFED".Where(c => !used.Contains(c) || c == current).ToList();
        _letter.Items.Clear();
        foreach (var c in free.OrderBy(c => c)) _letter.Items.Add($"{c}:");
        _letter.SelectedItem = free.Contains(current) ? $"{current}:" : $"{free.FirstOrDefault('T')}:";
        _label.Text = cfg.VolumeLabel;
        _autostart.Checked = true; // the usual choice; unticking it turns autostart off

        var account = Ctx.User is { } u ? $"{u.first_name} {u.last_name}".Trim() + (string.IsNullOrEmpty(u.username) ? "" : $" (@{u.username})") : null;
        _summary.Text = (account is null ? "" : $"חשבון טלגרם: {account}\n") +
                        "הכונן מוכן. הקבצים יעלו מוצפנים ברקע, ואייקון ליד השעון יראה מה המצב.";
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
