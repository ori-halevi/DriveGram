using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// "Move the drive to another Telegram account": the same drive, files and password; only the account
/// this PC uses to upload and download changes (e.g. one with Telegram Premium, which uploads faster).
/// Accounts already signed in here are one click; another one goes through the usual sign-in.
/// </summary>
sealed class SwitchAccountForm : Form
{
    readonly Label _result = Theme.Label("", Theme.Body, Theme.Muted, 640);
    readonly FlowLayoutPanel _accounts = Theme.Row();
    readonly List<Button> _buttons = [];

    /// <summary>The user picked an account signed in on this PC: check it and switch (the tray does that).</summary>
    public event Func<AccountInfo, Task<string?>>? AccountChosen;
    public event Action? SignInAnother;
    public event Action? OpenBackup;

    public SwitchAccountForm(AppState state)
    {
        Ui.Prepare(this);
        Text = L.T($"DriveGram – העברה לחשבון טלגרם אחר ({Ui.DriveTitle(state)})", $"DriveGram – move to another Telegram account ({state.DisplayName})");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 640);
        MinimumSize = new Size(620, 460);
        BackColor = Theme.Window;

        var col = Theme.Column();
        col.AutoScroll = true;
        col.AutoSize = false;
        col.Dock = DockStyle.Fill;

        var current = string.IsNullOrEmpty(state.Config.AccountName) ? "" : L.Name(state.Config.AccountName);
        Section(col, L.T("מה זה עושה", "What it does"));
        Para(col, L.T(
            $"הכונן נשאר אותו כונן, עם אותם קבצים ואותה סיסמה. מתחלף רק החשבון שהמחשב הזה משתמש בו כדי להעלות ולהוריד{(current.Length > 0 ? $" (עכשיו: {current})" : "")}. שום קובץ לא עולה מחדש.",
            $"The drive stays the same drive, with the same files and the same password. Only the account this PC uses to upload and download changes{(current.Length > 0 ? $" (now: {current})" : "")}. No file is uploaded again."));
        Para(col, L.T(
            "למה? למשל, חשבון עם Telegram Premium: בלי Premium טלגרם מאט את ההעלאות.",
            "Why? For example, an account with Telegram Premium: without Premium, Telegram slows uploads down."), Theme.Muted);

        Section(col, L.T("מה צריך", "What it needs"));
        Para(col, L.T(
            "החשבון האחר צריך לנהל את שני הערוצים של הכונן, עם הרשאה לפרסם, לערוך ולמחוק הודעות. אפשר להוסיף אותו כך בלחיצה אחת, ב\"גיבוי בחשבון נוסף\":",
            "The other account must administer both of the drive's channels, allowed to post, edit and delete messages. You can add it like that in one click, in \"Backup on another account\":"));
        var backup = Theme.Secondary(L.T("פתח את \"גיבוי בחשבון נוסף…\"", "Open \"Backup on another account…\""));
        backup.Click += (_, _) => OpenBackup?.Invoke();
        col.Controls.Add(backup);

        Section(col, L.T("בחר חשבון", "Choose the account"));
        var others = TelegramAccounts.Known().Where(a => a.Key != state.Config.AccountKey).ToList();
        if (others.Count > 0)
        {
            Para(col, L.T("חשבונות שכבר מחוברים במחשב הזה:", "Accounts already signed in on this PC:"));
            foreach (var a in others)
            {
                var b = Theme.Primary(L.Name(a.Name));
                b.Click += async (_, _) => await ChooseAsync(a);
                _buttons.Add(b);
                _accounts.Controls.Add(b);
            }
            col.Controls.Add(_accounts);
        }
        else Para(col, L.T("אין במחשב הזה חשבון טלגרם אחר שמחובר.", "No other Telegram account is signed in on this PC."), Theme.Muted);
        var signIn = Theme.Secondary(L.T("התחבר לחשבון אחר…", "Sign in to another account…"));
        signIn.Click += (_, _) => SignInAnother?.Invoke();
        col.Controls.Add(signIn);
        Para(col, L.T(
            "בהתחברות: סרוק את קוד ה־QR עם החשבון האחר, ואז בחר את הכונן הזה ברשימה (הוא יסומן \"אתה מנהל בו (גיבוי)\").",
            "Signing in: scan the QR code with the other account, then pick this drive in the list (it is marked \"you are an admin (backup)\")."), Theme.Muted);
        col.Controls.Add(_result);

        Section(col, L.T("טוב לדעת", "Good to know"));
        Para(col, "•  " + L.T(
            "במחשבים אחרים הכונן ממשיך עם החשבון שלהם. אפשר להעביר כל מחשב בנפרד.",
            "On other PCs the drive keeps their own account. Each PC can be moved separately."));
        Para(col, "•  " + L.T(
            "הבעלות על הערוצים לא משתנה. רוצה שהכונן גם יהיה שייך לחשבון החדש? ראה סעיף 3, \"להעביר את הבעלות לחשבון הגיבוי\", בחלון \"גיבוי בחשבון נוסף…\".",
            "Ownership of the channels does not change. Want the drive to belong to the new account too? See section 3, \"transfer the ownership to the backup account\", in the \"Backup on another account…\" window."));

        var close = Theme.Secondary(L.T("סגור", "Close"));
        close.Click += (_, _) => Close();
        close.Margin = new Padding(0, 16, 0, 0);
        CancelButton = close;
        col.Controls.Add(close);
        Theme.Fill(this, col, new Padding(24, 18, 24, 18));
    }

    async Task ChooseAsync(AccountInfo a)
    {
        if (AccountChosen is null) return;
        foreach (var b in _buttons) b.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = L.T($"בודק את ההרשאות של {L.Name(a.Name)} בערוצים של הכונן…", $"Checking the rights of {a.Name} in the drive's channels…");
        string? problem;
        try { problem = await AccountChosen(a); }
        catch (Exception ex) { problem = TelegramErrors.Text(ex); }
        if (IsDisposed) return;
        foreach (var b in _buttons) b.Enabled = true;
        if (problem is null) { Close(); return; }
        _result.ForeColor = problem.Length == 0 ? Theme.Muted : Theme.Error;
        _result.Text = problem;
    }

    static void Section(FlowLayoutPanel col, string title)
    {
        var l = Theme.Label(title, Theme.Heading, Theme.Text, 680);
        l.Margin = new Padding(0, 16, 0, 6);
        col.Controls.Add(l);
    }

    static void Para(FlowLayoutPanel col, string text, Color? color = null) =>
        col.Controls.Add(Theme.Label(text, Theme.Body, color ?? Theme.Text, 680));
}
