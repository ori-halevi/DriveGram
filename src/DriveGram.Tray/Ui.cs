using DriveGram.Host;

namespace DriveGram.Tray;

static class Ui
{
    public static readonly Font Font = new("Segoe UI", 10f);

    public static string Size(long bytes) => L.Ltr(bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    });

    /// <summary>"X: name · @account" for window titles, with the letter and a Latin account kept in order.</summary>
    public static string DriveTitle(AppState s) => string.IsNullOrEmpty(s.Config.AccountName)
        ? $"{L.Ltr(s.Config.MountPoint)} {s.Config.VolumeLabel}"
        : $"{L.Ltr(s.Config.MountPoint)} {s.Config.VolumeLabel} · {L.Name(s.Config.AccountName)}";

    /// <summary>Direction, font and icon for a window, by the UI language.</summary>
    public static void Prepare(Form f)
    {
        f.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        f.RightToLeftLayout = L.Rtl;
        f.Font = Font;
        if (AppIcon is not null) f.Icon = AppIcon;
    }

    static Icon? _appIcon;
    static Icon? AppIcon => _appIcon ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "drivegram.ico"))
        ? new Icon(Path.Combine(AppContext.BaseDirectory, "drivegram.ico")) : null;

    static MessageBoxOptions Options => L.Rtl ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0;

    public static DialogResult Ask(string text, MessageBoxButtons buttons = MessageBoxButtons.YesNo,
        MessageBoxIcon icon = MessageBoxIcon.Question, MessageBoxDefaultButton def = MessageBoxDefaultButton.Button1) =>
        OnTop(owner => MessageBox.Show(owner, text, "DriveGram", buttons, icon, def, Options));

    /// <summary>A message box owned by an invisible top-most window: never hidden behind the browser.</summary>
    static T OnTop<T>(Func<IWin32Window, T> show)
    {
        if (Form.ActiveForm is { Visible: true } active) return show(active);
        using var owner = new Form
        {
            TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0,
        };
        owner.Show();
        owner.Activate();
        return show(owner);
    }

    /// <summary>Copy a secret: kept out of Windows clipboard history and cloud clipboard, and cleared after a minute.</summary>
    public static void CopySecret(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        static MemoryStream Zero() => new(BitConverter.GetBytes(0));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", Zero());
        data.SetData("CanIncludeInClipboardHistory", Zero());
        data.SetData("CanUploadToCloudClipboard", Zero());
        try { Clipboard.SetDataObject(data, copy: true); }
        catch (Exception ex)
        {
            Log.Warn($"clipboard: {ex.Message}");
            Error(L.T("ההעתקה ללוח נכשלה (אולי תוכנה אחרת משתמשת בו כרגע). נסה שוב.", "Copying to the clipboard failed (another program may be using it right now). Try again."));
            return;
        }
        var timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); } catch { }
        };
        timer.Start();
    }

    /// <summary>A question whose buttons say what they do (Windows' own Yes/No follow the Windows
    /// language, not the app's). Returns the chosen button's index; closing the window = cancelIndex
    /// (without a cancel button the window cannot be closed without choosing).</summary>
    public static int Choose(string text, string[] buttons, int defaultIndex = 0, int cancelIndex = -1,
        MessageBoxIcon icon = MessageBoxIcon.Question) =>
        OnTop(owner =>
        {
            using var f = new ChoiceForm(text, buttons, defaultIndex, cancelIndex, icon);
            f.ShowDialog(owner);
            return f.Choice;
        });

    public static void Error(string text) =>
        OnTop(owner => MessageBox.Show(owner, text, "DriveGram", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Options));
}

/// <summary>The window behind Ui.Choose: an icon, the text, and one button per answer.</summary>
sealed class ChoiceForm : Form
{
    public int Choice { get; private set; }

    public ChoiceForm(string text, string[] buttons, int defaultIndex, int cancelIndex, MessageBoxIcon icon)
    {
        Choice = cancelIndex;
        Ui.Prepare(this);
        Text = "DriveGram";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ControlBox = cancelIndex >= 0;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Padding = new Padding(20, 18, 20, 14);

        var sys = icon switch
        {
            MessageBoxIcon.Warning => SystemIcons.Warning,
            MessageBoxIcon.Error => SystemIcons.Error,
            MessageBoxIcon.Information => SystemIcons.Information,
            _ => SystemIcons.Question,
        };
        var top = Setup.Theme.Row();
        top.Controls.Add(new PictureBox { Image = sys.ToBitmap(), Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 2, 0, 0) });
        var label = Setup.Theme.Label(text, Ui.Font, Setup.Theme.Text, 460);
        label.Margin = new Padding(12, 0, 12, 0);
        top.Controls.Add(label);

        // a plain left-to-right flow: the mirrored (Hebrew) window puts the first button on the right
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = i == defaultIndex ? Setup.Theme.Primary(buttons[i]) : Setup.Theme.Secondary(buttons[i]);
            b.Font = Ui.Font;
            b.MinimumSize = new Size(90, 32);
            b.Click += (_, _) => { Choice = index; DialogResult = DialogResult.OK; Close(); };
            row.Controls.Add(b);
            if (i == defaultIndex) AcceptButton = b;
            if (i == cancelIndex) CancelButton = b;
        }
        var col = Setup.Theme.Column();
        col.Controls.AddRange([top, row]);
        Controls.Add(col);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing && cancelIndex < 0 && DialogResult != DialogResult.OK) e.Cancel = true; };
        Shown += (_, _) => { Activate(); (AcceptButton as Control)?.Focus(); };
    }
}

/// <summary>Asks for a drive's password (when it is not remembered on this PC).</summary>
sealed class PasswordForm : Form
{
    readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 300, RightToLeft = RightToLeft.No };
    readonly CheckBox _remember = new()
    {
        Text = L.T("לזכור את הסיסמה במחשב הזה (כדי שהכונן ייפתח לבד, בלי לשאול)", "Remember the password on this PC (so the drive opens by itself, without asking)"),
        AutoSize = true, Checked = true,
    };
    readonly Label _error = new() { ForeColor = Color.Firebrick, AutoSize = true };

    public string Password => _pw.Text;
    public bool Remember => _remember.Checked;

    public PasswordForm(string driveName, string? error)
    {
        Ui.Prepare(this);
        Text = L.T($"DriveGram – הסיסמה של {driveName}", $"DriveGram – password for {driveName}");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var ok = new Button { Text = L.T("פתח את הכונן", "Open the drive"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = L.T("ביטול", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;
        _error.Text = error ?? "";

        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange([ok, cancel]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var rememberNote = new Label
        {
            Text = L.T("היא נשמרת מוצפנת, רק למשתמש שלך בווינדוס: מי שנכנס למשתמש הזה יוכל לפתוח את הכונן בלי סיסמה. אם אנשים אחרים משתמשים באותו משתמש, עדיף לא לסמן.",
                "It is kept encrypted, for your Windows user only: anyone who can use that Windows user can open the drive without the password. If others share that Windows user, better leave it off."),
            AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DimGray,
        };
        layout.Controls.AddRange([new Label { Text = L.T("הקלד את סיסמת הכונן, או את מפתח השחזור שלו:", "Enter the drive password, or its recovery key:"), AutoSize = true },
            _pw, _remember, rememberNote, _error, buttons]);
        Controls.Add(layout);
        Shown += (_, _) => { Activate(); _pw.Focus(); };
    }
}

/// <summary>The drive's own recycle bin: restore or delete forever.</summary>
sealed class TrashForm : Form
{
    readonly DriveSession _session;
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = true, HideSelection = false,
    };

    public TrashForm(DriveSession session)
    {
        _session = session;
        Ui.Prepare(this);
        _list.RightToLeftLayout = L.Rtl;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 520);

        _list.Columns.Add(L.T("שם", "Name"), 260);
        _list.Columns.Add(L.T("מיקום מקורי", "Original location"), 300);
        _list.Columns.Add(L.T("תאריך מחיקה", "Deleted on"), 150);
        _list.Columns.Add(L.T("גודל", "Size"), 100);

        var restore = new Button { Text = L.T("שחזר", "Restore"), AutoSize = true };
        var purge = new Button { Text = L.T("מחק לצמיתות", "Delete forever"), AutoSize = true };
        var refresh = new Button { Text = L.T("רענן", "Refresh"), AutoSize = true };
        restore.Click += (_, _) => Act(restore: true);
        purge.Click += (_, _) => Act(restore: false);
        refresh.Click += (_, _) => Reload();
        _list.DoubleClick += (_, _) => Act(restore: true);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bar.Controls.AddRange([restore, purge, refresh,
            new Label
            {
                Text = L.T("פריטים בסל המחזור נמחקים לצמיתות אחרי 30 יום. אלה לא מגיעים לסל ונמחקים מיד: קובץ שנמחק תוך 2 דקות מהרגע שנוצר, לפני שעלה לטלגרם; העתקה שבוטלה; קבצים זמניים.", "Items in the recycle bin are deleted forever after 30 days. These skip the bin and are deleted at once: a file deleted within 2 minutes of being created, before it was uploaded to Telegram; a cancelled copy; temporary files."),
                AutoSize = true, MaximumSize = new Size(520, 0), Padding = new Padding(12, 8, 0, 0), ForeColor = Color.DimGray,
            }]);
        Controls.Add(_list);
        Controls.Add(bar);
        Reload();
    }

    void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in _session.Trash())
        {
            _list.Items.Add(new ListViewItem([t.Dir ? $"📁 {t.Name}" : t.Name, L.LtrPath(t.OriginalFolder),
                t.DeletedAt.ToString(L.Hebrew ? "dd/MM/yyyy HH:mm" : "g"), t.Dir ? "" : Ui.Size(t.Size)]) { Tag = t });
        }
        _list.EndUpdate();
        Text = L.T($"DriveGram – סל המחזור של {L.Ltr(_session.MountPoint)} ({_list.Items.Count})",
                   $"DriveGram – {_session.MountPoint} recycle bin ({_list.Items.Count})");
    }

    async void Act(bool restore)
    {
        var items = _list.SelectedItems.Cast<ListViewItem>().Select(i => (TrashItem)i.Tag!).ToList();
        if (items.Count == 0) return;
        if (!restore && Ui.Ask(L.N(items.Count, "למחוק לצמיתות פריט אחד? אי אפשר לבטל את זה.", "למחוק לצמיתות {0} פריטים? אי אפשר לבטל את זה.",
                    "Delete 1 item forever? This cannot be undone.", "Delete {0} items forever? This cannot be undone."),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var elsewhere = new List<string>(); // restored, but not to the original folder (it is gone)
        string? error = null;
        _acting = true;
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                foreach (var t in items)
                {
                    if (restore)
                    {
                        var to = _session.Restore(t.Id);
                        var folder = Path.GetDirectoryName(to) ?? to;
                        if (!string.Equals(folder.TrimEnd('\\'), t.OriginalFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            elsewhere.Add(to);
                    }
                    else _session.DeleteForever(t.Id);
                }
            });
        }
        catch (Exception ex) { error = Setup.TelegramErrors.Text(ex); }
        finally { _acting = false; }
        if (IsDisposed) return; // closed meanwhile (the drive was disconnected)
        UseWaitCursor = false;
        Enabled = true;
        if (error is not null) Ui.Error(error);
        else if (elsewhere.Count > 0)
            Ui.Ask(L.N(elsewhere.Count, "התיקייה המקורית כבר לא קיימת, ולכן הפריט שוחזר אל:\n\n", "התיקיות המקוריות של {0} פריטים כבר לא קיימות, ולכן הם שוחזרו אל:\n\n",
                        "The original folder no longer exists, so the item was restored to:\n\n", "The original folders of {0} items no longer exist, so they were restored to:\n\n")
                    + string.Join("\n", elsewhere.Take(10).Select(L.LtrPath)) + (elsewhere.Count > 10 ? "\n…" : ""),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        try { Reload(); } catch (Exception ex) { Log.Warn($"trash: reload failed: {ex.Message}"); }
    }

    bool _acting;

    /// <summary>A restore or delete is still running (the tray waits for it before stopping the drive).</summary>
    public bool Acting => _acting;
}
