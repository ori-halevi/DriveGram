using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>What the main window needs from the tray (a sample one for the preview).</summary>
interface IMainHost : ITransfersHost, ISettingsHost
{
    List<AccountInfo> Accounts();
    string DriveStatus(DriveController d);
    void OpenDriveNow(DriveController d);
    Task ConnectOrDisconnectAsync(DriveController d);
    List<ToolStripItem> DriveItems(DriveController d);
    Task AddAccountAsync();
    Task AddDrivesAsync(AccountInfo account, bool createNew);
    /// <summary>"Add a drive" without naming the account: the wizard asks which one.</summary>
    Task AddDriveChoosingAccountAsync();
    Task SignOutAccountAsync(AccountInfo a);
}

/// <summary>
/// The main window, and the place the user comes back to: the Telegram accounts on this PC (add one, and
/// add drives to each), the drives (open, connect, and everything else in "⋯"), the transfers, and the
/// program's settings. With nothing set up yet it opens on the accounts, with one clear first step.
/// </summary>
sealed class MainForm : Form
{
    public enum Tab { Accounts, Drives, Transfers, Settings }

    readonly IMainHost _app;
    readonly ThemedTabs _tabs = new() { Dock = DockStyle.Fill };
    readonly Panel _accountsPage = Page(), _drivesPage = Page(), _transfersPage = Page(), _settingsPage = Page();
    readonly FlowLayoutPanel _accounts = Scroller();
    readonly FlowLayoutPanel _drives = Scroller();
    readonly TransfersForm _transfers;
    readonly SettingsPage _settings;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly Dictionary<DriveController, (Label Status, Button Open, Button Connect)> _driveRows = [];
    string? _drivesShape, _accountsShape; // null: build on the next tick

    const int CardWidth = 760;

    public MainForm(IMainHost app)
    {
        _app = app;
        Ui.Prepare(this);
        Text = "DriveGram";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 680);
        MinimumSize = new Size(720, 480);

        _accountsPage.Controls.Add(_accounts);
        _drivesPage.Controls.Add(_drives);
        _transfers = new TransfersForm(app) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
        _transfersPage.Controls.Add(_transfers);
        _settings = new SettingsPage(app, app.Daily);
        _settingsPage.Controls.Add(_settings);

        _tabs.Add(L.T("חשבונות", "Accounts"), Glyphs.People, _accountsPage);
        _tabs.Add(L.T("כוננים", "Drives"), Glyphs.Disk, _drivesPage);
        _tabs.Add(L.T("העברות", "Transfers"), Glyphs.Upload, _transfersPage);
        _tabs.Add(L.T("הגדרות", "Settings"), Glyphs.Settings, _settingsPage);
        _tabs.SelectedChanged += i =>
        {
            if (_tabs.PageAt(i) == _transfersPage) _transfers.Rescan();
            else if (_tabs.PageAt(i) == _settingsPage) _settings.Reload();
        };

        Controls.Add(_tabs);
        _updateBar.Dock = DockStyle.Top;
        _updateBar.Visible = false;
        Controls.Add(_updateBar);

        RefreshAll();
        _transfers.Show();
        _transfers.Start();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Dispose(); _transfers.Close(); };
        Theme.Apply(this);
    }

    static Panel Page() => new Panel { Dock = DockStyle.Fill }.Wears(Slot.Window);

    static FlowLayoutPanel Scroller() => new FlowLayoutPanel
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(18, 16, 18, 16),
    }.Wears(Slot.Window);

    public void Select(Tab tab, bool localFiles = false)
    {
        _tabs.SelectPage(tab switch
        {
            Tab.Accounts => _accountsPage,
            Tab.Drives => _drivesPage,
            Tab.Settings => _settingsPage,
            _ => _transfersPage,
        });
        if (tab == Tab.Transfers && localFiles) _transfers.ShowLocalTab();
        else if (tab == Tab.Transfers) _transfers.ShowUploadsTab();
    }

    /// <summary>Developer aid (screenshots): sample rows in the transfers lists.</summary>
    internal void DemoTransfers(int tab) => _transfers.Demo(tab);

    /// <summary>The files list, showing only what was prepared on this PC.</summary>
    public void ShowPrepared()
    {
        _tabs.SelectPage(_transfersPage);
        _transfers.ShowPrepared();
    }

    // a thin bar at the top when a new version is out (the tray decides; clicking opens the update window)
    readonly LinkLabel _updateBar = new LinkLabel
    {
        AutoSize = false, Height = 30, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Body,
        LinkBehavior = LinkBehavior.HoverUnderline,
    }.Wears(Slot.WarnBack, Slot.AccentDark);
    Action? _onUpdate;

    /// <summary>Show (text) or hide (null) the new-version bar.</summary>
    public void ShowUpdate(string? text, Action? onClick)
    {
        _onUpdate = onClick;
        if (_updateBar.Tag is null) { _updateBar.Tag = true; _updateBar.LinkClicked += (_, _) => _onUpdate?.Invoke(); }
        _updateBar.Text = text ?? "";
        _updateBar.Visible = text is not null;
    }

    public void RefreshAll()
    {
        _accountsShape = _drivesShape = null;
        _settings.Reload();
        Tick();
    }

    /// <summary>Rebuild a list when what it shows changed; otherwise only the drives' status lines.</summary>
    void Tick()
    {
        if (IsDisposed) return;
        var known = _app.Accounts();
        var accountsShape = string.Join("|", known.Select(a => a.Key + ":" + string.Join(",", _app.Drives.Where(d => d.State.Config.AccountKey == a.Key).Select(d => d.Letter))));
        if (accountsShape != _accountsShape) { _accountsShape = accountsShape; BuildAccounts(known); }
        // the controller object too: after Settings or an account switch a fresh one replaces it (same values)
        var drivesShape = string.Join("|", _app.Drives.Select(d => $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d)}:{d.State.Id}:{d.Letter}:{(d.Session is { } s ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s) : 0)}:{d.State.Config.VolumeLabel}:{d.State.Config.AccountName}"));
        if (drivesShape != _drivesShape) { _drivesShape = drivesShape; BuildDrives(); _transfers.Rescan(); }
        if (_tabs.Selected >= 0 && _tabs.PageAt(_tabs.Selected) == _settingsPage) _settings.Tick();
        foreach (var (d, row) in _driveRows)
        {
            row.Status.Text = _app.DriveStatus(d);
            row.Open.Enabled = d.Session is not null && !d.Stopping;
            row.Connect.Text = d.Session is null ? L.T("חבר", "Connect") : L.T("נתק", "Disconnect");
            row.Connect.Enabled = !d.Starting && !d.Stopping;
        }
    }

    // ── accounts ─────────────────────────────────────────────────────────────
    void BuildAccounts(List<AccountInfo> known)
    {
        _accounts.SuspendLayout();
        _accounts.Controls.Clear();
        if (known.Count == 0)
        {
            // the very first step, and nothing else
            var hello = Theme.Column();
            hello.Margin = new Padding(0, 30, 0, 0);
            var logo = new PictureBox { Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 12) };
            var ico = Path.Combine(AppContext.BaseDirectory, "drivegram.ico");
            if (File.Exists(ico)) logo.Image = new Icon(ico, 72, 72).ToBitmap();
            hello.Controls.Add(logo);
            hello.Controls.Add(Theme.Label(L.T("ברוך הבא ל־DriveGram", "Welcome to DriveGram"), new Font(Theme.Heading.FontFamily, 18f, FontStyle.Bold), Theme.Text));
            hello.Controls.Add(Theme.Label(L.T(
                "DriveGram שומר את הקבצים שלך מוצפנים בחשבון הטלגרם שלך, ומציג אותם ככונן רגיל במחשב. כדי להתחיל, חבר את חשבון הטלגרם שלך. אחר כך תיצור בו כונן, או תחבר כוננים שכבר יש בו.",
                "DriveGram keeps your files encrypted in your Telegram account and shows them as an ordinary drive on this PC. To start, connect your Telegram account. Then you create a drive in it, or connect drives it already has."),
                Theme.Body, Theme.Muted, 620));
            var first = Theme.Primary(L.T("חבר את חשבון הטלגרם הראשון שלך", "Connect your first Telegram account"));
            first.Font = new Font(Theme.Body.FontFamily, 12f, FontStyle.Bold);
            first.Padding = new Padding(18, 8, 18, 8);
            first.Margin = new Padding(0, 18, 0, 0);
            first.Click += async (_, _) => await _app.AddAccountAsync();
            hello.Controls.Add(first);
            _accounts.Controls.Add(hello);
            _accounts.ResumeLayout();
            return;
        }

        var head = Theme.Row();
        head.Controls.Add(Theme.Label(L.T("חשבונות הטלגרם במחשב הזה", "Telegram accounts on this PC"), new Font(Theme.Heading.FontFamily, 14f, FontStyle.Bold), Theme.Text));
        _accounts.Controls.Add(head);
        _accounts.Controls.Add(Theme.Label(L.T(
            "כל כונן שמור בשני ערוצים פרטיים של אחד החשבונות. אפשר לחבר כמה חשבונות, ולכל אחד כמה כוננים.",
            "Each drive lives in two private channels of one account. You can connect several accounts, each with several drives."),
            Theme.Small, Theme.Muted, CardWidth));
        var add = Theme.Secondary(L.T("+  הוסף חשבון טלגרם…", "+  Add a Telegram account…"));
        add.Margin = new Padding(0, 10, 0, 12);
        add.Click += async (_, _) => await _app.AddAccountAsync();
        _accounts.Controls.Add(add);

        foreach (var a in known)
        {
            var drives = _app.Drives.Where(d => d.State.Config.AccountKey == a.Key).ToList();
            var card = Card();
            var col = Theme.Column();
            col.MinimumSize = new Size(CardWidth - 30, 0); // the whole card: in Hebrew everything starts on the right
            var title = Theme.Row();
            title.Controls.Add(new PictureBox { Image = Glyphs.Make(Glyphs.Person, Theme.Accent, 28), Size = new Size(28, 28), Margin = new Padding(0, 0, 10, 0) });
            title.Controls.Add(Theme.Label(L.Name(a.Name), new Font(Theme.Heading.FontFamily, 13f, FontStyle.Bold), Theme.Text));
            col.Controls.Add(title);
            if (drives.Count == 0)
            {
                // the account is in, but it holds nothing here yet: say so where the eye lands, not quietly
                var todo = Theme.Card(Slot.WarnBack, Slot.WarnBorder);
                var todoRow = Theme.Row();
                todoRow.Controls.Add(new PictureBox
                {
                    Image = Glyphs.Make(Glyphs.Warning, Theme.Attention, 20), Size = new Size(20, 20),
                    SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 1, 10, 0),
                });
                var todoText = Theme.Label(L.T(
                    "עוד אין כוננים מהחשבון הזה במחשב הזה. לחץ \"הוסף כונן…\" כדי ליצור כונן חדש, או לחבר כונן שכבר קיים בחשבון.",
                    "No drives of this account on this PC yet. Press \"Add a drive…\" to create a new one, or to connect one the account already has."),
                    Theme.Body, Theme.Text, CardWidth - 90);
                todoText.Margin = new Padding(0, 1, 0, 0);
                todoRow.Controls.Add(todoText);
                todo.Controls.Add(todoRow);
                todo.Margin = new Padding(0, 4, 0, 4);
                col.Controls.Add(todo);
            }
            else
            {
                col.Controls.Add(Theme.Label(L.N(drives.Count, "כונן אחד במחשב הזה:", "{0} כוננים במחשב הזה:",
                    "1 drive on this PC:", "{0} drives on this PC:"), Theme.Body, Theme.Muted, CardWidth - 40));
                // one left-to-right run: drive letters are Latin, and without this the names of several
                // drives are reordered among themselves in a Hebrew window
                col.Controls.Add(Theme.Label(L.LtrPath(string.Join("   ·   ", drives.Select(d => d.Name))),
                    Theme.Body, Theme.Text, CardWidth - 40));
            }
            var buttons = Theme.Row();
            buttons.Margin = new Padding(0, 10, 0, 0);
            // one way in: the window behind it both creates a new drive and connects ones the account has
            var create = Theme.Primary(L.T("+  הוסף כונן…", "+  Add a drive…"));
            create.Click += async (_, _) => await _app.AddDrivesAsync(a, createNew: false);
            buttons.Controls.Add(create);
            if (drives.Count == 0)
            {
                var signOut = new LinkLabel { Text = L.T("התנתק מהחשבון", "Sign out of this account"), AutoSize = true, Font = Theme.Body, LinkColor = Theme.Muted, Margin = new Padding(16, 8, 16, 0) };
                signOut.LinkClicked += async (_, _) => await _app.SignOutAccountAsync(a);
                buttons.Controls.Add(signOut);
            }
            col.Controls.Add(buttons);
            card.Controls.Add(col);
            _accounts.Controls.Add(card);
        }
        _accounts.ResumeLayout();
    }

    // ── drives ───────────────────────────────────────────────────────────────
    void BuildDrives()
    {
        _drives.SuspendLayout();
        _drives.Controls.Clear();
        _driveRows.Clear();
        var head = Theme.Row();
        head.Controls.Add(Theme.Label(L.T("הכוננים במחשב הזה", "Drives on this PC"), new Font(Theme.Heading.FontFamily, 14f, FontStyle.Bold), Theme.Text));
        _drives.Controls.Add(head);
        var add = Theme.Secondary(L.T("+  הוסף כונן…", "+  Add a drive…"));
        add.Margin = new Padding(0, 10, 0, 12);
        add.Click += (_, _) => AddDrive();
        _drives.Controls.Add(add);

        if (_app.Drives.Count == 0)
        {
            _drives.Controls.Add(Theme.Label(L.T(
                "עוד אין כוננים במחשב הזה. כונן נוצר (או מתחבר) מתוך חשבון טלגרם: לחץ \"הוסף כונן\", או עבור ללשונית \"חשבונות\".",
                "No drives on this PC yet. A drive is created (or connected) from a Telegram account: click \"Add a drive\", or go to the \"Accounts\" tab."),
                Theme.Body, Theme.Muted, CardWidth));
            _drives.ResumeLayout();
            return;
        }

        foreach (var d in _app.Drives)
        {
            var card = Card();
            var row = Theme.Row();
            row.MinimumSize = new Size(CardWidth - 30, 0); // the whole card: in Hebrew everything starts on the right
            row.Controls.Add(new PictureBox { Image = d.Session is null ? ToolStripRenderer.CreateDisabledImage(DriveIcon) : DriveIcon, Size = new Size(44, 44), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 2, 14, 0) });
            var col = Theme.Column();
            col.Controls.Add(Theme.Label(d.Name, new Font(Theme.Heading.FontFamily, 13f, FontStyle.Bold), Theme.Text));
            var account = d.State.Folder is not null ? L.T("תיקייה מקומית (ניסיון)", "local folder (trial)") : L.Name(d.State.Config.AccountName ?? "");
            col.Controls.Add(Theme.Label(L.T($"חשבון: {account}", $"Account: {account}"), Theme.Small, Theme.Muted));
            var status = Theme.Label(_app.DriveStatus(d), Theme.Body, Theme.Text, CardWidth - 120);
            col.Controls.Add(status);
            var buttons = Theme.Row();
            buttons.Margin = new Padding(0, 10, 0, 0);
            var open = Theme.Primary(L.T("פתח", "Open"));
            open.Click += (_, _) => _app.OpenDriveNow(d);
            var connect = Theme.Secondary(d.Session is null ? L.T("חבר", "Connect") : L.T("נתק", "Disconnect"));
            connect.Click += async (_, _) => { await _app.ConnectOrDisconnectAsync(d); RefreshAll(); };
            var more = Theme.Secondary(L.T("עוד…", "More…"));
            more.Image = Glyphs.Make(Glyphs.More, Theme.AccentDark, 16); // "⋯"
            more.TextImageRelation = TextImageRelation.ImageBeforeText;
            more.Click += (_, _) =>
            {
                var menu = new ContextMenuStrip { Font = Ui.Font, RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No };
                menu.Items.AddRange(_app.DriveItems(d).ToArray());
                ThemePaint.Menu(menu);
                menu.Closed += (_, _) => BeginInvoke(() => { menu.Dispose(); RefreshAll(); });
                menu.Show(more, new Point(0, more.Height));
            };
            buttons.Controls.AddRange([open, connect, more]);
            col.Controls.Add(buttons);
            row.Controls.Add(col);
            card.Controls.Add(row);
            _drives.Controls.Add(card);
            _driveRows[d] = (status, open, connect);
        }
        _drives.ResumeLayout();
    }

    /// <summary>"Add a drive": one window that asks which account (only when there is more than one) and
    /// then which drive. With no account at all there is nothing to add a drive to: the Accounts tab first.</summary>
    void AddDrive()
    {
        if (_app.Accounts().Count == 0) { Select(Tab.Accounts); return; }
        _ = _app.AddDriveChoosingAccountAsync();
    }

    static Bitmap? _driveIcon;
    static Bitmap DriveIcon => _driveIcon ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "drivegram.ico"))
        ? new Icon(Path.Combine(AppContext.BaseDirectory, "drivegram.ico"), 48, 48).ToBitmap() : Glyphs.Make(Glyphs.Disk, Theme.Accent, 44);

    static Panel Card()
    {
        var card = Theme.Card(Slot.Card, Slot.Border);
        card.Margin = new Padding(0, 0, 0, 12);
        card.MinimumSize = new Size(CardWidth, 0);
        return card;
    }
}
