using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>What the main window needs from the tray (a sample one for the preview).</summary>
interface IMainHost : ITransfersHost
{
    List<AccountInfo> Accounts();
    string DriveStatus(DriveController d);
    void OpenDriveNow(DriveController d);
    Task ConnectOrDisconnectAsync(DriveController d);
    List<ToolStripItem> DriveItems(DriveController d);
    Task AddAccountAsync();
    Task AddDrivesAsync(AccountInfo account, bool createNew);
    Task SignOutAccountAsync(AccountInfo a);
}

/// <summary>
/// The main window: the Telegram accounts on this PC (add one, and add drives to each), the drives (open,
/// connect, and everything else in "⋯"), and the transfers. With nothing set up yet it opens on the
/// accounts, with one clear first step.
/// </summary>
sealed class MainForm : Form
{
    public enum Tab { Accounts, Drives, Transfers }

    readonly IMainHost _app;
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No, RightToLeftLayout = L.Rtl }; // Hebrew: the first tab on the right
    readonly TabPage _accountsPage, _drivesPage, _transfersPage;
    readonly FlowLayoutPanel _accounts = Scroller();
    readonly FlowLayoutPanel _drives = Scroller();
    readonly TransfersForm _transfers;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly Dictionary<DriveController, (Label Status, Button Open, Button Connect)> _driveRows = [];
    string? _drivesShape, _accountsShape; // null: build on the next tick
    readonly ImageList _tabIcons = new() { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };

    const int CardWidth = 760;

    public MainForm(IMainHost app)
    {
        _app = app;
        Ui.Prepare(this);
        Text = "DriveGram";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 680);
        MinimumSize = new Size(720, 480);
        BackColor = Color.White;

        _tabIcons.Images.Add("accounts", Glyphs.Make('', Theme.AccentDark, 18)); // people
        _tabIcons.Images.Add("drives", Glyphs.Make('', Theme.AccentDark, 18));   // hard drive
        _tabIcons.Images.Add("transfers", Glyphs.Make(Glyphs.Upload, Theme.AccentDark, 18));
        _tabs.ImageList = _tabIcons;
        _tabs.Font = new Font(Theme.Body.FontFamily, 11f);
        _tabs.Padding = new Point(16, 6);

        _accountsPage = new TabPage(L.T("חשבונות", "Accounts")) { BackColor = Color.White, ImageIndex = 0 };
        _accountsPage.Controls.Add(_accounts);
        _drivesPage = new TabPage(L.T("כוננים", "Drives")) { BackColor = Color.White, ImageIndex = 1 };
        _drivesPage.Controls.Add(_drives);
        _transfersPage = new TabPage(L.T("העברות", "Transfers")) { BackColor = Color.White, ImageIndex = 2 };
        _transfers = new TransfersForm(app) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
        _transfersPage.Controls.Add(_transfers);
        _tabs.TabPages.AddRange([_accountsPage, _drivesPage, _transfersPage]);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == _transfersPage) _transfers.Rescan(); };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 12) };
        body.Controls.Add(_tabs);
        Controls.Add(body);
        _updateBar.Dock = DockStyle.Top;
        _updateBar.Visible = false;
        Controls.Add(_updateBar);

        RefreshAll();
        _transfers.Show();
        _transfers.Start();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Dispose(); _transfers.Close(); };
    }

    static FlowLayoutPanel Scroller() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(18, 16, 18, 16), BackColor = Color.White,
    };

    public void Select(Tab tab, bool localFiles = false)
    {
        _tabs.SelectedTab = tab switch { Tab.Accounts => _accountsPage, Tab.Drives => _drivesPage, _ => _transfersPage };
        if (tab == Tab.Transfers && localFiles) _transfers.ShowLocalTab();
        else if (tab == Tab.Transfers) _transfers.ShowUploadsTab();
    }

    /// <summary>The files list, showing only what was prepared on this PC.</summary>
    public void ShowPrepared()
    {
        _tabs.SelectedTab = _transfersPage;
        _transfers.ShowPrepared();
    }

    // a thin bar at the top when a new version is out (the tray decides; clicking opens the update window)
    readonly LinkLabel _updateBar = new()
    {
        AutoSize = false, Height = 30, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Body,
        BackColor = Color.FromArgb(0xFF, 0xF4, 0xE5), LinkColor = Color.FromArgb(0xB4, 0x5A, 0x00), LinkBehavior = LinkBehavior.HoverUnderline,
    };
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
            title.Controls.Add(new PictureBox { Image = Glyphs.Make('', Theme.Accent, 28), Size = new Size(28, 28), Margin = new Padding(0, 0, 10, 0) });
            title.Controls.Add(Theme.Label(L.Name(a.Name), new Font(Theme.Heading.FontFamily, 13f, FontStyle.Bold), Theme.Text));
            col.Controls.Add(title);
            col.Controls.Add(Theme.Label(drives.Count == 0
                    ? L.T("עוד אין במחשב הזה כוננים מהחשבון הזה.", "No drives of this account on this PC yet.")
                    : L.N(drives.Count, $"כונן אחד במחשב הזה: {string.Join(", ", drives.Select(d => d.Name))}",
                        $"{{0}} כוננים במחשב הזה: {string.Join(", ", drives.Select(d => d.Name))}",
                        $"1 drive on this PC: {string.Join(", ", drives.Select(d => d.Name))}",
                        $"{{0}} drives on this PC: {string.Join(", ", drives.Select(d => d.Name))}"),
                Theme.Body, Theme.Muted, CardWidth - 40));
            var buttons = Theme.Row();
            buttons.Margin = new Padding(0, 10, 0, 0);
            var create = Theme.Primary(L.T("+  כונן חדש…", "+  New drive…"));
            create.Click += async (_, _) => await _app.AddDrivesAsync(a, createNew: true);
            var connect = Theme.Secondary(L.T("חבר כוננים קיימים…", "Connect existing drives…"));
            connect.Click += async (_, _) => await _app.AddDrivesAsync(a, createNew: false);
            buttons.Controls.AddRange([create, connect]);
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
        add.Click += (_, _) => AddDrive(add);
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
            more.Image = Glyphs.Make('', Theme.AccentDark, 16); // "⋯"
            more.TextImageRelation = TextImageRelation.ImageBeforeText;
            more.Click += (_, _) =>
            {
                var menu = new ContextMenuStrip { Font = Ui.Font, RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No };
                menu.Items.AddRange(_app.DriveItems(d).ToArray());
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

    /// <summary>"Add a drive": one account → its drives; several → choose; none → the accounts tab.</summary>
    void AddDrive(Control anchor)
    {
        var known = _app.Accounts();
        if (known.Count == 0) { Select(Tab.Accounts); return; }
        if (known.Count == 1) { _ = _app.AddDrivesAsync(known[0], createNew: false); return; }
        var menu = new ContextMenuStrip { Font = Ui.Font, RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No };
        menu.Items.Add(new ToolStripMenuItem(L.T("באיזה חשבון?", "On which account?")) { Enabled = false });
        foreach (var a in known)
        {
            var item = new ToolStripMenuItem(L.Name(a.Name));
            item.Click += async (_, _) => await _app.AddDrivesAsync(a, createNew: false);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem(L.T("חשבון טלגרם אחר…", "Another Telegram account…"));
        other.Click += async (_, _) => await _app.AddAccountAsync();
        menu.Items.Add(other);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    static Bitmap? _driveIcon;
    static Bitmap DriveIcon => _driveIcon ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "drivegram.ico"))
        ? new Icon(Path.Combine(AppContext.BaseDirectory, "drivegram.ico"), 48, 48).ToBitmap() : Glyphs.Make('', Theme.Accent, 44);

    static Panel Card()
    {
        var card = Theme.Card(Color.White, Theme.Border);
        card.Margin = new Padding(0, 0, 0, 12);
        card.MinimumSize = new Size(CardWidth, 0);
        return card;
    }
}
