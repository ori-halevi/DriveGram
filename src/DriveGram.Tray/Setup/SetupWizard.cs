using DriveGram.Host;
using DriveGram.Remote;

namespace DriveGram.Tray.Setup;

/// <summary>State shared by the wizard pages: the profile, the Telegram client and what was decided.</summary>
sealed class SetupContext : IDisposable
{
    public AppState State { get; }
    public bool UsesTelegram => State.Folder is null;
    public WTelegram.Client? Client { get; private set; }
    public TL.User? User { get; set; }
    public TelegramRemote? Remote { get; set; }
    /// <summary>Answers a 2FA prompt during QR sign-in (set by the login page).</summary>
    public Func<string?>? AskPassword { get; set; }

    public SetupContext(AppState state) => State = state;

    public WTelegram.Client GetClient() => Client ??= DriveSession.NewTelegramClient(State, () => AskPassword?.Invoke());

    /// <summary>Drop the client (new API credentials, or switching account).</summary>
    public void ResetClient(bool signOut)
    {
        var c = Client;
        Client = null;
        User = null;
        Remote = null;
        if (c is null) return;
        if (signOut)
        {
            try { TL.SchemaExtensions.Auth_LogOut(c).Wait(TimeSpan.FromSeconds(10)); } catch { }
        }
        c.Dispose();
        if (signOut) try { File.Delete(State.SessionPath); } catch { }
    }

    public void Dispose()
    {
        if (Remote is not null) Remote.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); // disposes its client
        else Client?.Dispose();
        Client = null;
        Remote = null;
    }
}

/// <summary>One step of the wizard.</summary>
abstract class WizardPage : UserControl
{
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public virtual string NextText => "הבא";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CanGoNext { get; protected set; } = true;
    public event Action? StateChanged;
    protected SetupContext Ctx { get; }

    protected WizardPage(SetupContext ctx)
    {
        Ctx = ctx;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Font = Theme.Body;
        AutoScroll = true;
    }

    /// <summary>Put the page's main column in place: full width, so right-to-left content hugs the right edge.</summary>
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
    /// <summary>Skip this page entirely right now (e.g. already signed in and nothing to show).</summary>
    public virtual bool Skip => false;
    /// <summary>Snapshot aid: put the page into an alternative visual state (no network).</summary>
    public virtual void Demo(int variant) { }
    public virtual int DemoVariants => this is LoginPage or DrivePage ? 2 : 0;
}

/// <summary>
/// The first-run / settings wizard: Telegram API details → sign-in (QR or phone, with 2FA) →
/// create or connect the drive → drive letter and start with Windows. Hebrew, right-to-left.
/// </summary>
sealed class SetupWizard : Form
{
    readonly SetupContext _ctx;
    readonly List<WizardPage> _pages;
    readonly Label _title = new() { Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true };
    readonly Label _subtitle = new() { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(540, 0) };
    readonly Label _step = new() { Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true };
    readonly Panel _progress = new() { Height = 3, Dock = DockStyle.Bottom, BackColor = Theme.Border };
    readonly Panel _progressFill = new() { Dock = DockStyle.Right, BackColor = Theme.Accent };
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(32, 20, 32, 8), BackColor = Color.White };
    readonly Button _next = Theme.Primary("הבא");
    readonly Button _back = Theme.Secondary("חזרה");
    readonly Button _cancel = Theme.Secondary("ביטול");
    int _index = -1;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? SnapshotDir { get; init; }
    bool _busy;

    public SetupWizard(AppState state)
    {
        _ctx = new SetupContext(state);
        Text = "DriveGram – הגדרה";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = Theme.Body;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(760, 600);
        var icon = Path.Combine(AppContext.BaseDirectory, "drivegram.ico");
        if (File.Exists(icon)) Icon = new Icon(icon);

        _pages = _ctx.UsesTelegram
            ? [new WelcomePage(_ctx), new ApiPage(_ctx), new LoginPage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)]
            : [new WelcomePage(_ctx), new DrivePage(_ctx), new FinishPage(_ctx)];
        foreach (var p in _pages) p.StateChanged += UpdateButtons;

        // header
        var header = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Color.White, Padding = new Padding(32, 18, 32, 0) };
        var logo = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Right };
        var png = Path.Combine(AppContext.BaseDirectory, "drivegram.ico");
        if (File.Exists(png)) logo.Image = new Icon(png, 48, 48).ToBitmap();
        var titles = Theme.Column();
        titles.Dock = DockStyle.Fill;
        titles.Padding = new Padding(14, 0, 14, 0);
        titles.Controls.AddRange([_title, _subtitle]);
        _step.Dock = DockStyle.Left;
        _progress.Controls.Add(_progressFill);
        header.Controls.AddRange([titles, logo, _step, _progress]);

        // footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, BackColor = Theme.Surface, Padding = new Padding(28, 14, 28, 14) };
        var right = Theme.Row();
        right.Dock = DockStyle.Right;
        right.Controls.AddRange([_next, _back]);
        _cancel.Dock = DockStyle.Left;
        footer.Controls.AddRange([right, _cancel]);
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
        Shown += async (_, _) => { if (SnapshotDir is null) await GoAsync(0, +1); else { await SnapshotAsync(SnapshotDir); Close(); } };
    }

    async Task GoNextAsync()
    {
        if (_busy || _index < 0) return;
        var page = _pages[_index];
        SetBusy(true);
        bool ok;
        try { ok = await page.NextAsync(); }
        catch (Exception ex) { Ui.Error(TelegramErrors.Hebrew(ex)); ok = false; }
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
        _title.Text = page.Title;
        _subtitle.Text = page.Subtitle;
        _step.Text = $"שלב {index + 1} מתוך {_pages.Count}";
        _progressFill.Width = (int)(ClientSize.Width * (index + 1.0) / _pages.Count);
        UpdateButtons();
        // snapshots never touch Telegram (the real session may be in use by the running drive)
        bool offline = SnapshotDir is not null && page is (LoginPage or DrivePage);
        try { if (!offline) await page.EnterAsync(); }
        catch (Exception ex) { Ui.Error(TelegramErrors.Hebrew(ex)); }
        if (direction != 0 && page.Skip && index + direction >= 0 && index + direction < _pages.Count)
            await GoAsync(index + direction, direction);
    }

    /// <summary>Developer aid: render every page to PNG files (window kept off-screen).</summary>
    public async Task SnapshotAsync(string dir)
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
