using System.ComponentModel;
using DriveGram.Host;
using DriveGram.Remote;

namespace DriveGram.Tray.Setup;

/// <summary>State shared by the wizard pages: the profile, the Telegram client and what was decided.</summary>
sealed class SetupContext : IDisposable
{
    public AppState State { get; }
    /// <summary>Adding a new drive (vs. fixing / changing an existing one).</summary>
    public bool IsNew { get; }
    public bool UsesTelegram => State.Folder is null;
    public WTelegram.Client? Client { get; private set; }
    public TL.User? User { get; set; }
    public TelegramRemote? Remote { get; set; }
    /// <summary>The drive's keys, once unlocked in the wizard (used for the first mount, in memory only).</summary>
    public DriveGram.Crypto.Keys? UnlockedKeys { get; set; }
    /// <summary>Set when THIS wizard created a new drive in Telegram (only such a drive may be deleted on cancel).</summary>
    public string? CreatedDriveId { get; set; }
    /// <summary>Answers a 2FA prompt during QR sign-in (set by the login page).</summary>
    public Func<string?>? AskPassword { get; set; }

    /// <summary>The drive this profile was bound to when the wizard opened (null for a new profile).
    /// Binding the profile to any OTHER drive means its local index/cache belong to the old one.</summary>
    public Remote.TelegramChannels? OriginalChannels { get; }

    public SetupContext(AppState state, bool isNew)
    {
        State = state;
        IsNew = isNew;
        OriginalChannels = state.Config.Channels;
    }

    AccountLease? _lease;

    /// <summary>The account's shared connection (the same one running drives use — never a second
    /// client on the same sign-in file). Before sign-in it is a fresh, profile-local one.</summary>
    public WTelegram.Client GetClient()
    {
        if (Client is null)
        {
            _lease = TelegramAccounts.Acquire(State, () => AskPassword?.Invoke());
            Client = _lease.Client;
        }
        return Client;
    }

    /// <summary>A sign-in just succeeded: store it as the account's shared sign-in (or switch to the
    /// one this PC already had for that account, logging the duplicate out).</summary>
    public void AdoptSignIn(TL.User user)
    {
        if (_lease is null) return;
        _lease = TelegramAccounts.Adopt(State, _lease, user);
        Client = _lease.Client;
    }

    /// <summary>Use an account already signed in on this PC — no new sign-in needed.</summary>
    public void UseAccount(AccountInfo a)
    {
        ResetClient(signOut: false);
        State.Config.AccountKey = a.Key;
        State.Config.ApiId = a.ApiId;
        State.Config.ApiHash = a.ApiHash;
        State.Config.AccountId = a.Id;
        State.Config.AccountName = a.Name;
        State.Config.Channels = null; // another account: a new binding (never open the old drive's channels with it)
        State.Config.DriveId = "";
    }

    /// <summary>Drop the connection (new API credentials, switching account, or a broken sign-in).
    /// signOut: forget this profile's sign-in; a shared account is signed out only if nothing uses it.</summary>
    public void ResetClient(bool signOut)
    {
        if (Remote is not null) { Remote.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); Remote = null; }
        _lease?.Dispose();
        _lease = null;
        Client = null;
        User = null;
        if (!signOut) return;
        var key = State.Config.AccountKey;
        State.Config.AccountKey = "";
        if (string.IsNullOrEmpty(key)) { try { File.Delete(State.SessionPath); } catch { } }
        else _ = TelegramAccounts.SignOutIfUnusedAsync(key);
    }

    public void Dispose()
    {
        Remote?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); // the wizard's remotes do not own the connection
        _lease?.Dispose();
        _lease = null;
        Client = null;
        Remote = null;
    }
}

/// <summary>One step of the wizard.</summary>
abstract class WizardPage : UserControl
{
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public virtual string NextText => L.T("הבא", "Next");
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanGoNext { get; protected set; } = true;
    public event Action? StateChanged;
    protected SetupContext Ctx { get; }

    protected WizardPage(SetupContext ctx)
    {
        Ctx = ctx;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Font = Theme.Body;
    }

    /// <summary>Put the page's main column in place: full width, so the content hugs the reading edge.</summary>
    protected void SetContent(FlowLayoutPanel column)
    {
        column.AutoSize = false;
        column.Dock = DockStyle.Fill;
        column.AutoScroll = true;
        Controls.Add(column);
    }

    protected void Changed(bool canGoNext)
    {
        CanGoNext = canGoNext;
        StateChanged?.Invoke();
    }

    /// <summary>Called each time the page is shown.</summary>
    public virtual Task EnterAsync() => Task.CompletedTask;
    /// <summary>Called when leaving the page (forward or back): cancel background work.</summary>
    public virtual void LeavePage() { }
    /// <summary>Validate / do the page's work before moving on. False = stay.</summary>
    public virtual Task<bool> NextAsync() => Task.FromResult(true);
    /// <summary>Skip this page entirely right now (e.g. already configured).</summary>
    public virtual bool Skip => false;
    /// <summary>Snapshot aid: put the page into an alternative visual state (no network).</summary>
    public virtual void Demo(int variant) { }
    public virtual int DemoVariants => 0;
}

/// <summary>
/// The first-run / add-drive / settings wizard: Telegram API details → sign-in (QR or phone, with
/// 2FA) → create or connect the drive → drive letter and start with Windows. Hebrew (RTL) or English.
/// </summary>
sealed class SetupWizard : Form
{
    readonly SetupContext _ctx;
    public SetupContext Context => _ctx;
    readonly List<WizardPage> _pages;
    readonly Label _title = new() { Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true };
    readonly Label _subtitle = new() { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(540, 0) };
    readonly Label _step = new() { Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true };
    readonly Panel _progress = new() { Height = 3, Dock = DockStyle.Bottom, BackColor = Theme.Border };
    readonly Panel _progressFill = new() { Dock = L.Rtl ? DockStyle.Right : DockStyle.Left, BackColor = Theme.Accent };
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(32, 20, 32, 8), BackColor = Color.White };
    readonly Button _next = Theme.Primary(L.T("הבא", "Next"));
    readonly Button _back = Theme.Secondary(L.T("חזרה", "Back"));
    readonly Button _cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
    int _index = -1;
    bool _busy;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SnapshotDir { get; init; }

    public SetupWizard(AppState state, bool isNew)
    {
        _ctx = new SetupContext(state, isNew);
        Text = isNew ? L.T("DriveGram – כונן חדש", "DriveGram – new drive") : L.T($"DriveGram – הגדרות הכונן {state.DisplayName}", $"DriveGram – drive settings: {state.DisplayName}");
        RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = L.Rtl;
        Font = Theme.Body;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(760, 600);
        var icon = Path.Combine(AppContext.BaseDirectory, "drivegram.ico");
        if (File.Exists(icon)) Icon = new Icon(icon);

        List<WizardPage> pages = _ctx.UsesTelegram
            ? [new WelcomePage(_ctx), new ApiPage(_ctx), new LoginPage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)]
            : [new WelcomePage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)];
        if (!isNew) pages.RemoveAt(0); // changing a drive's settings: no welcome tour
        _pages = pages;
        foreach (var p in _pages) p.StateChanged += UpdateButtons;

        // header: logo on the reading-start side, step counter on the other
        var header = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Color.White, Padding = new Padding(32, 18, 32, 0) };
        var logo = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Dock = L.Rtl ? DockStyle.Right : DockStyle.Left };
        if (File.Exists(icon)) logo.Image = new Icon(icon, 48, 48).ToBitmap();
        var titles = Theme.Column();
        titles.Dock = DockStyle.Fill;
        titles.Padding = new Padding(14, 0, 14, 0);
        titles.Controls.AddRange([_title, _subtitle]);
        _step.Dock = L.Rtl ? DockStyle.Left : DockStyle.Right;
        _progress.Controls.Add(_progressFill);
        header.Controls.AddRange([titles, logo, _step, _progress]);

        // footer: Next/Back on the reading-end side, Cancel on the other
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = Theme.Surface, Padding = new Padding(28, 14, 28, 14) };
        // explicit left-to-right layout so the order never depends on automatic mirroring:
        // English  [Cancel] ............ [Back][Next]     Hebrew  [Next][Back] ............ [Cancel]
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, RightToLeft = RightToLeft.No, FlowDirection = FlowDirection.LeftToRight,
            Dock = L.Rtl ? DockStyle.Left : DockStyle.Right,
        };
        buttons.Controls.AddRange(L.Rtl ? [_next, _back] : [_back, _next]);
        _cancel.Dock = L.Rtl ? DockStyle.Right : DockStyle.Left;
        footer.Controls.AddRange([buttons, _cancel]);
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, 0, footer.Width, 0);

        Controls.AddRange([_host, header, footer]);
        AcceptButton = _next;
        _next.Click += async (_, _) => await GoNextAsync();
        _back.Click += async (_, _) => await GoAsync(_index - 1, -1);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        FormClosing += (_, e) =>
        {
            if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (_index >= 0) _pages[_index].LeavePage();
        };
        FormClosed += (_, _) => _ctx.Dispose();
        Shown += async (_, _) =>
        {
            // opened from the tray (or after a wait) it would otherwise appear behind the browser
            Activate();
            TopMost = true;
            TopMost = false;
            if (SnapshotDir is null) await GoAsync(0, +1);
            else { await SnapshotAsync(SnapshotDir); Close(); }
        };
    }

    async Task GoNextAsync()
    {
        if (_busy || _index < 0) return;
        var page = _pages[_index];
        SetBusy(true);
        bool ok;
        try { ok = await page.NextAsync(); }
        catch (Exception ex) { Ui.Error(TelegramErrors.Text(ex)); ok = false; }
        finally { SetBusy(false); }
        if (!ok) return;
        if (_index == _pages.Count - 1)
        {
            page.LeavePage();
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        await GoAsync(_index + 1, +1);
    }

    async Task GoAsync(int index, int direction)
    {
        if (index < 0 || index >= _pages.Count) return;
        if (_index >= 0) _pages[_index].LeavePage();
        var page = _pages[index];
        _index = index;
        _host.Controls.Clear();
        _host.Controls.Add(page);
        _step.Text = L.T($"שלב {index + 1} מתוך {_pages.Count}", $"Step {index + 1} of {_pages.Count}");
        _progressFill.Width = (int)(ClientSize.Width * (index + 1.0) / _pages.Count);
        UpdateButtons();
        // snapshots never touch Telegram (the real session may be in use by a running drive)
        bool offline = SnapshotDir is not null && page is (LoginPage or DrivePage);
        try { if (!offline) await page.EnterAsync(); }
        catch (Exception ex) { Ui.Error(TelegramErrors.Text(ex)); }
        UpdateButtons();
        if (direction != 0 && page.Skip && index + direction >= 0 && index + direction < _pages.Count)
            await GoAsync(index + direction, direction);
    }

    /// <summary>Developer aid: render every page (and its demo states) to PNG files.</summary>
    async Task SnapshotAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < _pages.Count; i++)
        {
            await GoAsync(i, 0);
            await Task.Delay(600);
            if (_pages[i].DemoVariants == 0) Save(Path.Combine(dir, $"step{i + 1}.png"));
            for (int v = 0; v < _pages[i].DemoVariants; v++)
            {
                _pages[i].LeavePage();
                _pages[i].Demo(v);
                UpdateButtons();
                await Task.Delay(300);
                Save(Path.Combine(dir, $"step{i + 1}-demo{v + 1}.png"));
            }
        }
        _ctx.State.Delete(); // the snapshot used a throw-away new profile
    }

    void Save(string path)
    {
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(path);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        if (_index < 0) return;
        var page = _pages[_index];
        _title.Text = page.Title;
        _subtitle.Text = page.Subtitle;
        _next.Text = page.NextText;
        _next.Enabled = page.CanGoNext && !_busy;
        _back.Enabled = _index > 0 && !_busy;
        _back.Visible = _index > 0;
    }
}
