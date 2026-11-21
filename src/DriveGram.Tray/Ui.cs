using DriveGram.Host;

namespace DriveGram.Tray;

static class Ui
{
    public static readonly Font Font = new("Segoe UI", 10f);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

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
        MessageBox.Show(text, "DriveGram", buttons, icon, def, Options);

    /// <summary>Copy a secret: kept out of Windows clipboard history and cloud clipboard, and cleared after a minute.</summary>
    public static void CopySecret(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        static MemoryStream Zero() => new(BitConverter.GetBytes(0));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", Zero());
        data.SetData("CanIncludeInClipboardHistory", Zero());
        data.SetData("CanUploadToCloudClipboard", Zero());
        try { Clipboard.SetDataObject(data, copy: true); } catch (Exception ex) { Error(ex.Message); return; }
        var timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); } catch { }
        };
        timer.Start();
    }

    public static void Error(string text) =>
        MessageBox.Show(text, "DriveGram", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Options);
}

/// <summary>Asks for a drive's password (when it is not remembered on this PC).</summary>
sealed class PasswordForm : Form
{
    readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 300, RightToLeft = RightToLeft.No };
    readonly CheckBox _remember = new()
    {
        Text = L.T("לזכור את הסיסמה במחשב הזה (נדרש כדי לעלות עם וינדוס)", "Remember the password on this PC (needed to start with Windows)"),
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
        layout.Controls.AddRange([new Label { Text = L.T("הקלד את סיסמת הכונן, או את מפתח השחזור שלו:", "Enter the drive password, or its recovery key:"), AutoSize = true },
            _pw, _remember, _error, buttons]);
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
        _list.Columns.Add(L.T("נמחק ב־", "Deleted"), 150);
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
                Text = L.T("פריטים נמחקים לצמיתות אוטומטית אחרי 30 יום. קובץ שנוסף לפני פחות מחצי שעה ועוד לא עלה נמחק מיד, בלי לעבור בסל.", "Items are deleted forever automatically after 30 days. A file added less than half an hour ago that has not uploaded yet is deleted at once, without the bin."),
                AutoSize = true, Padding = new Padding(12, 8, 0, 0), ForeColor = Color.DimGray,
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
            _list.Items.Add(new ListViewItem([t.Dir ? $"📁 {t.Name}" : t.Name, t.OriginalFolder,
                t.DeletedAt.ToString(L.Hebrew ? "dd/MM/yyyy HH:mm" : "g"), t.Dir ? "" : Ui.Size(t.Size)]) { Tag = t });
        }
        _list.EndUpdate();
        Text = L.T($"DriveGram – סל המחזור של {_session.MountPoint} ({_list.Items.Count})",
                   $"DriveGram – {_session.MountPoint} recycle bin ({_list.Items.Count})");
    }

    void Act(bool restore)
    {
        var items = _list.SelectedItems.Cast<ListViewItem>().Select(i => (TrashItem)i.Tag!).ToList();
        if (items.Count == 0) return;
        if (!restore && Ui.Ask(L.T($"למחוק לצמיתות {items.Count} פריטים? אי אפשר לבטל את זה.",
                    $"Delete {items.Count} items forever? This cannot be undone."),
                icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            foreach (var t in items)
            {
                if (restore) _session.Restore(t.Id);
                else _session.DeleteForever(t.Id);
            }
        }
        catch (Exception ex) { Ui.Error(ex.Message); }
        Reload();
    }
}
