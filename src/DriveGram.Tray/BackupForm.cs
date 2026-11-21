using DriveGram.Host;
using DriveGram.Remote;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// "Backup on another account": explains why and how, makes another account a full admin of both of
/// the drive's channels in one click, shows who administers them now, and gives the manual steps for
/// what only the Telegram app can do (transferring ownership) and for restoring from the backup.
/// </summary>
sealed class BackupForm : Form
{
    readonly AppState _state;
    readonly TextBox _username = Theme.Input(260);
    readonly Button _add = Theme.Primary(L.T("הוסף כמנהל לשני הערוצים", "Add as admin of both channels"));
    readonly Label _addResult = Theme.Label("", Theme.Small, Theme.Muted, 640);
    readonly FlowLayoutPanel _admins = Theme.Column();
    readonly Label _adminsState = Theme.Label("", Theme.Body, Theme.Muted, 640);
    readonly Label _journalName = Theme.Label("", Theme.Small, Theme.Text, 640);
    readonly Label _dataName = Theme.Label("", Theme.Small, Theme.Text, 640);
    readonly Button _makeLinks = Theme.Secondary(L.T("צור קישורי הצטרפות", "Create invite links"));
    readonly FlowLayoutPanel _links = Theme.Column();
    readonly Button _checkJoined = Theme.Secondary(L.T("בדוק מי הצטרף", "Check who joined"));
    readonly FlowLayoutPanel _joined = Theme.Column();
    readonly Label _linksResult = Theme.Label("", Theme.Small, Theme.Muted, 640);

    public BackupForm(AppState state)
    {
        _state = state;
        Ui.Prepare(this);
        Text = L.T($"DriveGram – גיבוי בחשבון נוסף ({state.DisplayName})", $"DriveGram – backup on another account ({state.DisplayName})");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(780, 720);
        MinimumSize = new Size(640, 480);
        BackColor = Color.White;

        var col = Theme.Column();
        col.Padding = new Padding(24, 18, 24, 18);
        col.AutoScroll = true;
        col.AutoSize = false;
        col.Dock = DockStyle.Fill;

        Section(col, L.T("למה חשבון גיבוי?", "Why a backup account?"));
        Para(col, L.T(
            "אם החשבון שהכונן נמצא בו ייחסם או יימחק, הערוצים של הכונן נשארים בטלגרם, והמנהלים שלהם שומרים על ההרשאות. חשבון נוסף שמנהל את שני הערוצים יוכל לפתוח את הכונן גם אז.",
            "If the account the drive lives on is blocked or deleted, the drive's channels stay in Telegram and their admins keep their rights. Another account that administers both channels can still open the drive then."));
        Para(col, L.T(
            "הקבצים נשארים מוצפנים: חשבון הגיבוי רואה רק חתיכות מוצפנות, וכדי לפתוח את הכונן הוא צריך את סיסמת הכונן. שמור אותה במקום בטוח.",
            "Files stay encrypted: the backup account sees only encrypted chunks, and needs the drive password to open the drive. Keep it somewhere safe."), Theme.Muted);

        Section(col, L.T("1. הוספה בלחיצה", "1. Add it in one click"));
        Para(col, L.T("שם המשתמש של חשבון הגיבוי (מופיע בהגדרות של החשבון ההוא, למשל ‎@my_backup‎):",
            "The backup account's username (shown in that account's settings, e.g. @my_backup):"));
        _username.PlaceholderText = "@username";
        var row = Theme.Row();
        row.Controls.AddRange([_username, _add]);
        col.Controls.Add(row);
        col.Controls.Add(_addResult);
        _add.Click += async (_, _) => await AddAsync();
        AcceptButton = _add;

        var noName = Theme.Label(L.T("אין לחשבון הגיבוי שם משתמש, והוא לא באנשי הקשר?", "The backup account has no username and is not a contact?"), Theme.Body, Theme.Text, 680);
        noName.Margin = new Padding(0, 14, 0, 4);
        noName.Font = new Font(Theme.Body, FontStyle.Bold);
        col.Controls.Add(noName);
        Para(col, L.T(
            "צור שני קישורי הצטרפות, אחד לכל ערוץ, ושלח את שניהם לחשבון הגיבוי בכל דרך. פתח את שני הקישורים מהחשבון ההוא כדי להצטרף, ואז לחץ \"בדוק מי הצטרף\" והפוך אותו למנהל. כל קישור עובד פעם אחת בלבד ופג אחרי 24 שעות. אם קישור דלף, מי שפותח אותו ראשון מצטרף (בלי הרשאות ניהול): לכן ב\"בדוק מי הצטרף\" ודא שזה באמת החשבון שלך לפני שאתה הופך אותו למנהל.",
            "Create two invite links, one per channel, and send both to the backup account any way you like. Open both links from that account to join, then click \"Check who joined\" and make it an admin. Each link works once only and expires after 24 hours. If a link leaks, whoever opens it first joins (without admin rights): so in \"Check who joined\" make sure it really is your account before making it an admin."));
        col.Controls.Add(_makeLinks);
        col.Controls.Add(_links);
        col.Controls.Add(_checkJoined);
        col.Controls.Add(_joined);
        col.Controls.Add(_linksResult);
        _makeLinks.Click += async (_, _) => await MakeLinksAsync();
        _checkJoined.Click += async (_, _) => await CheckJoinedAsync();

        Section(col, L.T("2. מי מנהל את הערוצים עכשיו", "2. Who administers the channels now"));
        col.Controls.Add(_adminsState);
        col.Controls.Add(_admins);
        var refresh = Theme.Secondary(L.T("רענן", "Refresh"));
        refresh.Click += async (_, _) => await LoadAdminsAsync();
        col.Controls.Add(refresh);

        Section(col, L.T("3. מומלץ: להעביר את הבעלות לחשבון הגיבוי", "3. Recommended: transfer ownership to the backup account"));
        Para(col, L.T(
            "מנהל עלול להישאר עם הרשאות חלקיות אם חשבון הבעלים נמחק. בעלים לא. את זה טלגרם מאפשר רק מהאפליקציה שלהם, ובחשבון הנוכחי צריך אימות דו־שלבי:",
            "An admin may be left with partial rights if the owner account is deleted; an owner is not. Telegram allows this only from its own app, and the current account needs two-step verification:"));
        foreach (var step in new[]
        {
            L.T("פתח בטלגרם, עם החשבון של הכונן, את כל אחד משני הערוצים שלמטה.", "In Telegram, signed in as the drive's account, open each of the two channels below."),
            L.T("לחץ על שם הערוץ ← מנהלים ← חשבון הגיבוי.", "Tap the channel name → Administrators → the backup account."),
            L.T("בתחתית: \"העבר בעלות על הערוץ\", ואשר עם סיסמת האימות הדו־שלבי.", "At the bottom: \"Transfer channel ownership\", and confirm with the two-step password."),
            L.T("DriveGram ימשיך לעבוד כרגיל: החשבון הנוכחי נשאר מנהל עם כל ההרשאות.", "DriveGram keeps working as before: the current account stays an admin with all rights."),
            L.T("⚠  חשוב: הבעלים יכול להסיר מנהלים ולמחוק את הערוצים. העבר בעלות רק לחשבון שהוא שלך, ושמוגן באימות דו־שלבי לפחות כמו החשבון הנוכחי.",
                "⚠  Important: the owner can remove admins and delete the channels. Transfer ownership only to an account that is yours and protected (two-step verification) at least as well as the current one."),
        }) Para(col, "•  " + step);

        Section(col, L.T("4. הוספה ידנית (במקום שלב 1, אם מעדיפים)", "4. Adding it by hand (instead of step 1)"));
        Para(col, L.T("באפליקציית טלגרם, בכל אחד משני הערוצים: שם הערוץ ← מנהלים ← הוספת מנהל ← בחר את חשבון הגיבוי ← הפעל את כל ההרשאות ← שמור.",
            "In the Telegram app, in each of the two channels: channel name → Administrators → Add admin → choose the backup account → turn on every permission → save."));
        Para(col, L.T("הערוצים של הכונן הזה:", "This drive's channels:"));
        col.Controls.Add(_journalName);
        col.Controls.Add(_dataName);
        Para(col, L.T("חשוב: שני הערוצים, לא רק אחד. בלי ערוץ היומן אי אפשר לדעת מה יש בכונן, ובלי ערוץ הקבצים אין את הקבצים.",
            "Important: both channels, not just one. Without the journal nobody knows what the drive holds; without the data channel there are no files."), Theme.Muted);

        Section(col, L.T("5. איך משחזרים מחשבון הגיבוי", "5. How to restore from the backup account"));
        foreach (var step in new[]
        {
            L.T("ב־DriveGram (בכל מחשב): בתפריט של האייקון בחר \"הוסף כונן נוסף\".", "In DriveGram (on any PC): in the tray menu choose \"Add another drive\"."),
            L.T("התחבר עם חשבון הגיבוי (סריקת QR).", "Sign in with the backup account (scan the QR code)."),
            L.T("ברשימת הכוננים בחר את הכונן המסומן \"אתה מנהל בו (גיבוי)\".", "In the list of drives choose the one marked \"you are an admin (backup)\"."),
            L.T("הקלד את סיסמת הכונן. זהו, הכונן עולה עם כל הקבצים.", "Enter the drive password. That's it: the drive comes up with all its files."),
        }) Para(col, "•  " + step);

        var close = Theme.Secondary(L.T("סגור", "Close"));
        close.Click += (_, _) => Close();
        CancelButton = close;
        col.Controls.Add(close);
        Controls.Add(col);

        var ch = state.Config.Channels;
        _journalName.Text = L.T("•  ערוץ היומן: ", "•  Journal channel: ") + "…";
        _dataName.Text = L.T("•  ערוץ הקבצים: ", "•  Data channel: ") + "…";
        if (ch is null || state.Folder is not null)
        {
            _add.Enabled = false;
            _adminsState.Text = L.T("לכונן הזה אין ערוצי טלגרם (כונן ניסיון מקומי).", "This drive has no Telegram channels (a local trial drive).");
        }
        else Shown += async (_, _) => await LoadAdminsAsync();
    }

    static void Section(FlowLayoutPanel col, string title)
    {
        var l = Theme.Label(title, Theme.Heading, Theme.Text, 680);
        l.Margin = new Padding(0, 16, 0, 6);
        col.Controls.Add(l);
    }

    static void Para(FlowLayoutPanel col, string text, Color? color = null) =>
        col.Controls.Add(Theme.Label(text, Theme.Body, color ?? Theme.Text, 680));

    async Task LoadAdminsAsync()
    {
        if (_state.Config.Channels is not { } ch) return;
        _adminsState.ForeColor = Theme.Muted;
        _adminsState.Text = L.T("בודק…", "Checking…");
        _admins.Controls.Clear();
        try
        {
            List<ChannelAdmins> list;
            using (var lease = TelegramAccounts.Acquire(_state))
                list = await Task.Run(() => TelegramRemote.ListAdminsAsync(lease.Client, ch));
            foreach (var c in list)
            {
                var who = string.Join(",  ", c.Admins.Select(a => a.IsOwner ? $"{a.Name} ({L.T("בעלים", "owner")})" : a.Name));
                var kind = c.IsJournal ? L.T("ערוץ היומן", "Journal") : L.T("ערוץ הקבצים", "Data");
                _admins.Controls.Add(Theme.Label($"{kind}:  {who}", Theme.Body, Theme.Text, 680));
                (c.IsJournal ? _journalName : _dataName).Text = $"•  {kind}:  “{c.Title}”";
            }
            // a backup exists when some OTHER account administers both channels
            var me = _state.Config.AccountId;
            bool backed = list.Count == 2 && list.All(c => c.Admins.Any(a => a.UserId != me));
            _adminsState.ForeColor = backed ? Theme.Success : Color.FromArgb(0xB4, 0x6A, 0x00);
            _adminsState.Text = backed
                ? L.T("✓  יש חשבון נוסף שמנהל את שני הערוצים.", "✓  Another account administers both channels.")
                : L.T("עדיין אין חשבון נוסף שמנהל את שני הערוצים.", "No other account administers both channels yet.");
        }
        catch (Exception ex)
        {
            _adminsState.ForeColor = Theme.Error;
            _adminsState.Text = TelegramErrors.Text(ex);
        }
    }

    async Task MakeLinksAsync()
    {
        if (_state.Config.Channels is not { } ch) return;
        _makeLinks.Enabled = false;
        _links.Controls.Clear();
        _linksResult.ForeColor = Theme.Muted;
        _linksResult.Text = L.T("יוצר קישורים…", "Creating links…");
        try
        {
            (string Journal, string Data) links;
            using (var lease = TelegramAccounts.Acquire(_state))
                links = await Task.Run(() => TelegramRemote.CreateInviteLinksAsync(lease.Client, ch));
            AddLink(L.T("ערוץ היומן", "Journal channel"), links.Journal);
            AddLink(L.T("ערוץ הקבצים", "Data channel"), links.Data);
            var both = Theme.Secondary(L.T("העתק את שני הקישורים", "Copy both links"));
            both.Click += (_, _) => Clipboard.SetText($"{links.Journal}\n\n{links.Data}");
            _links.Controls.Add(both);
            _linksResult.Text = L.T("שלח את שני הקישורים לחשבון הגיבוי. אחרי שהוא הצטרף לשניהם, לחץ \"בדוק מי הצטרף\".",
                "Send both links to the backup account. Once it has joined both, click \"Check who joined\".");
        }
        catch (Exception ex)
        {
            _linksResult.ForeColor = Theme.Error;
            _linksResult.Text = TelegramErrors.Text(ex);
        }
        finally { _makeLinks.Enabled = true; }
    }

    void AddLink(string label, string link)
    {
        var row = Theme.Row();
        var box = Theme.Input(380);
        box.Text = link;
        box.ReadOnly = true;
        box.Font = Theme.Small;
        var copy = Theme.Secondary(L.T("העתק", "Copy"));
        copy.Click += (_, _) => Clipboard.SetText(link);
        row.Controls.AddRange([Theme.Label(label + ":", Theme.Small, Theme.Muted), box, copy]);
        _links.Controls.Add(row);
    }

    async Task CheckJoinedAsync()
    {
        if (_state.Config.Channels is not { } ch) return;
        _checkJoined.Enabled = false;
        _joined.Controls.Clear();
        _linksResult.ForeColor = Theme.Muted;
        _linksResult.Text = L.T("בודק…", "Checking…");
        try
        {
            List<ChannelAdmin> joined;
            using (var lease = TelegramAccounts.Acquire(_state))
                joined = await Task.Run(() => TelegramRemote.JoinedNotAdminAsync(lease.Client, ch));
            if (joined.Count == 0)
            {
                _linksResult.Text = L.T("אף חשבון עוד לא הצטרף לשני הערוצים (או שהוא כבר מנהל).", "No account has joined both channels yet (or it is already an admin).");
                return;
            }
            _linksResult.Text = L.T("הצטרפו לשני הערוצים:", "Joined both channels:");
            foreach (var u in joined)
            {
                var row = Theme.Row();
                var make = Theme.Primary(L.T("הפוך למנהל", "Make admin"));
                make.Click += async (_, _) => await PromoteAsync(ch, u, make);
                row.Controls.AddRange([Theme.Label(u.Name, Theme.Body), make]);
                _joined.Controls.Add(row);
            }
        }
        catch (Exception ex)
        {
            _linksResult.ForeColor = Theme.Error;
            _linksResult.Text = TelegramErrors.Text(ex);
        }
        finally { _checkJoined.Enabled = true; }
    }

    async Task PromoteAsync(TelegramChannels ch, ChannelAdmin user, Button button)
    {
        button.Enabled = false;
        try
        {
            string name;
            using (var lease = TelegramAccounts.Acquire(_state))
                name = await Task.Run(() => TelegramRemote.PromoteJoinedAsync(lease.Client, ch, user.UserId));
            _joined.Controls.Clear();
            _linksResult.ForeColor = Theme.Success;
            _linksResult.Text = L.T($"✓  {name} מנהל עכשיו את שני הערוצים, עם כל ההרשאות.", $"✓  {name} now administers both channels, with every permission.");
            await LoadAdminsAsync();
        }
        catch (Exception ex)
        {
            button.Enabled = true;
            _linksResult.ForeColor = Theme.Error;
            _linksResult.Text = TelegramErrors.Text(ex);
        }
    }

    async Task AddAsync()
    {
        if (_state.Config.Channels is not { } ch || _username.Text.Trim().TrimStart('@').Length == 0) return;
        _add.Enabled = false;
        _addResult.ForeColor = Theme.Muted;
        _addResult.Text = L.T("מוסיף…", "Adding…");
        try
        {
            string name;
            var username = _username.Text; // read on the UI thread
            using (var lease = TelegramAccounts.Acquire(_state))
            {
                var user = await Task.Run(() => TelegramRemote.ResolveUserAsync(lease.Client, username));
                var who = $"{user.first_name} {user.last_name}".Trim();
                // a typo or a look-alike name would hand a stranger full control: show exactly who it is
                if (Ui.Ask(L.T(
                        $"להפוך את החשבון הזה למנהל מלא של שני הערוצים של הכונן?\n\n{who}\n@{user.username}   (מזהה {user.id})\n\nמנהל יכול לקרוא את רשומת הכונן, לשנות ואף למחוק את כל הערוצים. ודא שזה באמת החשבון שלך.",
                        $"Make this account a full admin of both of the drive's channels?\n\n{who}\n@{user.username}   (id {user.id})\n\nAn admin can read the drive's record, change, and even delete both channels. Make sure it really is your account."),
                        icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    _addResult.Text = "";
                    return;
                }
                name = await Task.Run(() => TelegramRemote.AddBackupAdminAsync(lease.Client, ch, user));
            }
            _addResult.ForeColor = Theme.Success;
            _addResult.Text = L.T($"✓  {name} מנהל עכשיו את שני הערוצים, עם כל ההרשאות.", $"✓  {name} now administers both channels, with every permission.");
            await LoadAdminsAsync();
        }
        catch (Exception ex)
        {
            _addResult.ForeColor = Theme.Error;
            _addResult.Text = TelegramErrors.Text(ex);
        }
        finally { _add.Enabled = true; }
    }
}
