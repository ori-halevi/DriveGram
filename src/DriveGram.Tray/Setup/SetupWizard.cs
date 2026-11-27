using System.ComponentModel;
using DriveGram.Host;
using DriveGram.Remote;

namespace DriveGram.Tray.Setup;

/// <summary>
/// A drive to add after the current one, in the same sitting and on the same account: an existing drive
/// the user ticked too (Candidate), a new one (CreateNew), or "let me choose" (neither). PasswordHint is the
/// password that opened the previous drive, tried first (drives often share one); kept in memory only.
/// Verified: that password was already checked against this drive on the first page, so its run goes
/// straight to its letter and name (Remember: the first page's "remember the password" choice).
/// </summary>
/// Account null: "a drive from another Telegram account" — that run starts with choosing or signing in to it.
/// Letter / Label / Network: chosen on the first drive page already, so that run finishes by itself.
sealed record NextDrive(AccountInfo? Account, DriveCandidate? Candidate, bool CreateNew, string? PasswordHint,
    bool Verified = false, bool Remember = true, string? Letter = null, string? Label = null, bool? Network = null);

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
    /// <summary>This run continues a sitting: which drive it is for (null: an ordinary run).</summary>
    public NextDrive? Current { get; init; }
    /// <summary>Drives still to add after this one (filled by the drive page and the last page).</summary>
    public List<NextDrive> Queue { get; init; } = [];
    /// <summary>This run's drive letter and name, chosen on the drive page (null: the last page asks).</summary>
    public string? PresetLetter { get; set; }
    public string? PresetLabel { get; set; }
    /// <summary>The drive type and autostart, chosen on the drive page (null: the last page asks).</summary>
    public bool? PresetNetwork { get; set; }
    /// <summary>The drive this queued run was for is no longer there: the user picked another one, so the
    /// letter, name and type chosen for the queued drive do not apply.</summary>
    public bool QueuedDriveGone { get; set; }
    public bool? PresetAutostart { get; set; }
    /// <summary>A new Telegram drive: everything is chosen on the drive page, and the last page only
    /// saves it (not shown, not counted in "step N of M"). Changing an existing drive keeps the last page.</summary>
    public bool DrivePageFinishes => IsNew && UsesTelegram && !AccountOnly;
    /// <summary>The password that opened (or created) this run's drive, for the next drives in the queue.</summary>
    public string? LastPassword { get; set; }
    /// <summary>Only adding a Telegram account (the main window's "Add account"): no drive here.</summary>
    public bool AccountOnly { get; init; }
    /// <summary>The user chose another language on the first page: open the wizard again in it.</summary>
    public bool RestartForLanguage { get; set; }
    /// <summary>The API details were already known, so their page was not shown (the sign-in page offers
    /// a link to change them instead).</summary>
    public bool ApiSkipped { get; set; }
    /// <summary>Sign in with a different account (from "move to another Telegram account"): the sign-in page
    /// starts signed out instead of showing the current account.</summary>
    public bool FreshSignIn { get; init; }
    /// <summary>Someone signed in to Telegram in this run (it is shared at once: closing the window after it
    /// still counts as signed in).</summary>
    public bool SignedInHere { get; set; }
    /// <summary>The drive page found this run's drive missing on the signed-in account.</summary>
    public bool DriveVanished { get; set; }
    /// <summary>The account this run's drive is on, to add more drives on it without a new sign-in.</summary>
    public AccountInfo? Account => UsesTelegram && !string.IsNullOrEmpty(State.Config.AccountKey)
        ? new AccountInfo(State.Config.AccountKey, State.Config.AccountId, State.Config.AccountName, State.Config.ApiId, State.Config.ApiHash)
        : null;

    /// <summary>Answers a 2FA prompt during QR sign-in (set by the login page).</summary>
    public Func<string?>? AskPassword { get; set; }

    /// <summary>The drive this profile was bound to when the wizard opened (null for a new profile).
    /// Binding the profile to any OTHER drive means its local index/cache belong to the old one.</summary>
    public Remote.TelegramChannels? OriginalChannels { get; }

    /// <param name="originalChannels">The drive the profile on disk is bound to, when the caller has already
    /// cleared it from <paramref name="state"/> (to show the list of drives); otherwise state's own.</param>
    public SetupContext(AppState state, bool isNew, Remote.TelegramChannels? originalChannels = null)
    {
        State = state;
        IsNew = isNew;
        OriginalChannels = originalChannels ?? state.Config.Channels;
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
        BackColor = Theme.Window;
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
    /// <summary>Known before the page is reached: it will be skipped (not counted in "step N of M").</summary>
    public virtual bool SkipsAhead => false;
    /// <summary>False after this page did something that cannot be undone: no "Back" from here.</summary>
    public virtual bool CanGoBack => true;
    /// <summary>The page finished its work by itself (e.g. a sign-in): go on as if Next was pressed.</summary>
    public event Action? NextRequested;
    protected void RequestNext() => NextRequested?.Invoke();
    /// <summary>The page asks the wizard to show another page of this run (e.g. "change the API details").</summary>
    public event Action<Type>? PageRequested;
    protected void RequestPage(Type page) => PageRequested?.Invoke(page);
    /// <summary>Everything on this page is already decided: go on by itself once (Back still shows it).</summary>
    public virtual bool AutoNext => false;
    /// <summary>False for a page that only finishes what an earlier page chose (not in "step N of M").</summary>
    public virtual bool Counted => true;
    /// <summary>Set by the wizard: the page's own background work holds the buttons (true) or frees them.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? Busy { get; set; }
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
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(32, 20, 32, 8) };
    readonly Button _next = Theme.Primary(L.T("הבא", "Next"));
    readonly Button _back = Theme.Secondary(L.T("חזרה", "Back"));
    readonly Button _cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
    readonly Panel _header;
    readonly FlowLayoutPanel _titles;
    int _index = -1;
    bool _busy;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SnapshotDir { get; init; }

    /// <param name="title">The window's title for this way in (null: by the kind of run).</param>
    /// <param name="chooseAccount">"Add a drive" from the Drives tab: which account first (skipped when
    /// there is only one), then the drive. No account can be added here — that is the Accounts tab's job.</param>
    public SetupWizard(AppState state, bool isNew, TelegramChannels? originalChannels = null,
        NextDrive? next = null, List<NextDrive>? queue = null, bool accountOnly = false, bool firstRun = false,
        string? title = null, bool freshSignIn = false, bool chooseAccount = false)
    {
        _ctx = new SetupContext(state, isNew, originalChannels) { Current = next, Queue = queue ?? [], AccountOnly = accountOnly, FreshSignIn = freshSignIn };
        if (next?.Account is { } sameAccount) _ctx.UseAccount(sameAccount); // the same account: no API details, no sign-in
        int more = next is null ? 0 : _ctx.Queue.Count;
        Text = chooseAccount ? L.T("DriveGram – הוספת כונן", "DriveGram – add a drive")
            : accountOnly ? L.T("DriveGram – הוספת חשבון טלגרם", "DriveGram – add a Telegram account")
            : next is { CreateNew: true } ? L.T("DriveGram – כונן חדש", "DriveGram – new drive")
            : next is not null ? L.T("DriveGram – כוננים בחשבון ", "DriveGram – drives on ") + (next.Account is { } na ? L.Name(na.Name) : "") + (more > 0 ? L.N(more, " (ועוד אחד אחריו)", " (ועוד {0} אחריו)", " (1 more after it)", " ({0} more after it)") : "")
            : isNew ? L.T("DriveGram – כונן חדש", "DriveGram – new drive") : L.T($"DriveGram – הגדרות הכונן {Ui.DriveTitle(state)}", $"DriveGram – drive settings: {state.DisplayName}");
        if (title is not null) Text = title;
        RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = L.Rtl;
        Font = Theme.Body;
        BackColor = Theme.Window;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        // not taller than the screen (a 1366x768 laptop): the page scrolls, the buttons stay reachable
        int room = Screen.FromPoint(Cursor.Position).WorkingArea.Height - (SystemInformation.CaptionHeight + 2 * SystemInformation.FixedFrameBorderSize.Height + 8);
        ClientSize = new Size(900, Math.Max(560, Math.Min(740, room)));
        var icon = Path.Combine(AppContext.BaseDirectory, "drivegram.ico");
        if (File.Exists(icon)) Icon = new Icon(icon);

        List<WizardPage> pages = accountOnly
            ? (firstRun ? [new WelcomePage(_ctx), new ApiPage(_ctx), new LoginPage(_ctx)] : [new ApiPage(_ctx), new LoginPage(_ctx)])
            : chooseAccount ? [new AccountPage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)]
            : next is { Account: not null } ? [new DrivePage(_ctx), new FinishPage(_ctx)] // the account is known already
            : next is not null ? [new ApiPage(_ctx), new LoginPage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)] // another account: sign in first
            : _ctx.UsesTelegram
            ? [new WelcomePage(_ctx), new ApiPage(_ctx), new LoginPage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)]
            : [new WelcomePage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)];
        if (!isNew && next is null && !accountOnly)
        {
            pages.RemoveAt(0); // an existing drive: no welcome tour,
            pages.RemoveAll(p => p is FinishPage); // and no last page (its letter, name and type have their own windows)
        }
        _pages = pages;
        foreach (var p in _pages)
        {
            p.StateChanged += UpdateButtons;
            p.Busy = SetBusy;
            var page = p;
            p.NextRequested += async () => { if (_index >= 0 && _pages[_index] == page && !_busy) await GoNextAsync(); };
            p.PageRequested += async t => { if (!_busy && _pages.FindIndex(x => x.GetType() == t) is >= 0 and var i) await GoAsync(i, 0); };
        }

        // header: logo on the reading-start side, step counter on the other
        var header = _header = new Panel { Dock = DockStyle.Top, Height = 118, Padding = new Padding(32, 18, 32, 0) };
        var logo = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Dock = L.Rtl ? DockStyle.Right : DockStyle.Left };
        if (File.Exists(icon)) logo.Image = new Icon(icon, 48, 48).ToBitmap();
        var titles = _titles = Theme.Column();
        titles.Dock = DockStyle.Fill;
        titles.Padding = new Padding(14, 0, 14, 0);
        titles.Controls.AddRange([_title, _subtitle]);
        _step.Dock = L.Rtl ? DockStyle.Left : DockStyle.Right;
        _progress.Controls.Add(_progressFill);
        header.Controls.AddRange([titles, logo, _step, _progress]);

        // footer: Next/Back on the reading-end side, Cancel on the other
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = Theme.Surface, Padding = new Padding(28, 13, 28, 13) };
        // explicit left-to-right layout so the order never depends on automatic mirroring:
        // English  [Cancel] ............ [Back][Next]     Hebrew  [Next][Back] ............ [Cancel]
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, RightToLeft = RightToLeft.No, FlowDirection = FlowDirection.LeftToRight,
            Dock = L.Rtl ? DockStyle.Left : DockStyle.Right,
        };
        _next.Margin = _back.Margin = new Padding(3, 0, 3, 0);
        buttons.Controls.AddRange(L.Rtl ? [_next, _back] : [_back, _next]);
        _cancel.Dock = L.Rtl ? DockStyle.Right : DockStyle.Left;
        footer.Controls.AddRange([buttons, _cancel]);
        // tall enough for the buttons (a bordered one is 41 px at 125%, more with larger text): a fixed
        // inner height cut off their bottom edge
        footer.Height = Math.Max(68, footer.Padding.Vertical + new[] { _next, _back, _cancel }.Max(b => b.GetPreferredSize(Size.Empty).Height));
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Border), 0, 0, footer.Width, 0);

        Controls.AddRange([_host, header, footer]);
        AcceptButton = _next;
        _next.Click += async (_, _) => await GoNextAsync();
        _back.Click += async (_, _) => { if (_history.Count >= 2) await GoAsync(_history[^2], 0); };
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

    /// <returns>True when it moved on (or finished).</returns>
    async Task<bool> GoNextAsync()
    {
        if (_busy || _index < 0) return false;
        var page = _pages[_index];
        SetBusy(true);
        bool ok;
        try { ok = await page.NextAsync(); }
        catch (Exception ex) { Ui.Error(TelegramErrors.Text(ex)); ok = false; }
        finally { SetBusy(false); }
        if (!ok) return false;
        if (_index == _pages.Count - 1)
        {
            page.LeavePage();
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }
        await GoAsync(_index + 1, +1);
        return true;
    }

    async Task GoAsync(int index, int direction)
    {
        if (index < 0 || index >= _pages.Count) return;
        if (_index >= 0) _pages[_index].LeavePage();
        var page = _pages[index];
        _index = index;
        _host.Controls.Clear();
        _host.Controls.Add(page);
        UpdateStep();
        UpdateButtons();
        // snapshots never touch Telegram (the real session may be in use by a running drive)
        bool offline = SnapshotDir is not null && page is (LoginPage or DrivePage);
        try { if (!offline) await page.EnterAsync(); }
        catch (Exception ex) { Ui.Error(TelegramErrors.Text(ex)); }
        UpdateStep(); // a page may know only now whether it counts (a last page shown after all)
        UpdateButtons();
        if (direction != 0 && page.Skip && index + direction >= 0 && index + direction < _pages.Count)
        {
            _skipped.Add(page); // not counted in "step N of M"
            if (page is ApiPage) _ctx.ApiSkipped = true;
            await GoAsync(index + direction, direction);
        }
        else
        {
            // the pages shown in this run, in order: Back goes to the one before (a page shown again
            // drops what came after it, e.g. back on the sign-in page after changing the API details)
            bool auto = direction > 0 && page.AutoNext;
            if (auto)
            {
                // nothing to ask here: on by itself, and not a page to come back to (nor a step)
                _skipped.Add(page);
                if (await GoNextAsync()) return;
                _skipped.Remove(page); // it stayed (its work failed): it is a page the user sees after all
            }
            int at = _history.IndexOf(index);
            if (at >= 0) _history.RemoveRange(at + 1, _history.Count - at - 1); else _history.Add(index);
            _skipped.Remove(page);
            UpdateStep();
            UpdateButtons();
        }
    }

    readonly HashSet<WizardPage> _skipped = [];
    readonly List<int> _history = [];

    /// <summary>"Step N of M", without pages that were skipped (e.g. API details already known).</summary>
    void UpdateStep()
    {
        bool Shows(WizardPage p) => p.Counted && !_skipped.Contains(p) && !(p.SkipsAhead && !_history.Contains(_pages.IndexOf(p)))
            && !(p is ApiPage && _ctx.ApiSkipped); // opened from the sign-in page's link: a side trip, not a step
        int total = _pages.Count(Shows);
        int step = Math.Min(total, _pages.Take(_index + 1).Count(Shows));
        _step.Visible = _progress.Visible = total > 1; // one page: no "step 1 of 1"
        _step.Text = L.T($"שלב {step} מתוך {total}", $"Step {step} of {total}");
        _progressFill.Width = (int)(_progress.Width * (double)step / Math.Max(1, total)); // the line, not the window: it is inset
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

    /// <summary>Header tall enough for the title and a two-line subtitle (more if it wraps further, e.g. with
    /// large text scaling): a fixed height put the second line on top of the progress line.</summary>
    void FitHeader()
    {
        int twoLines = _title.PreferredHeight + _title.Margin.Vertical + 2 * _subtitle.Font.Height + _subtitle.Margin.Vertical;
        int actual = _titles.GetPreferredSize(new Size(_titles.Width, 0)).Height;
        _header.Height = Math.Max(118, _header.Padding.Top + Math.Max(twoLines, actual) + 14 + _progress.Height);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    /// <summary>The first page's language link: close and open again in the other language (nothing was
    /// entered yet on that page, so nothing is lost).</summary>
    public void SwitchLanguage()
    {
        if (_busy) return;
        L.Set(L.Hebrew ? "en" : "he");
        _ctx.RestartForLanguage = true;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    void UpdateButtons()
    {
        if (_index < 0) return;
        var page = _pages[_index];
        _title.Text = page.Title;
        _subtitle.Text = page.Subtitle;
        FitHeader();
        _next.Text = page.NextText;
        _next.Enabled = page.CanGoNext && !_busy;
        bool back = _history.Count >= 2 && page.CanGoBack;
        _back.Enabled = back && !_busy;
        _back.Visible = back;
    }
}
