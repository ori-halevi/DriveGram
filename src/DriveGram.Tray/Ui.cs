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

    public static void Rtl(Form f)
    {
        f.RightToLeft = RightToLeft.Yes;
        f.RightToLeftLayout = true;
        f.Font = Font;
        var icon = Path.Combine(AppContext.BaseDirectory, "drivegram.ico");
        if (File.Exists(icon)) f.Icon = new Icon(icon);
    }

    public static void Error(string text) =>
        MessageBox.Show(text, "DriveGram", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
}

/// <summary>Asks for the drive password (when it is not remembered on this PC).</summary>
sealed class PasswordForm : Form
{
    readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 300 };
    readonly CheckBox _remember = new() { Text = "לזכור את הסיסמה במחשב הזה (נדרש כדי לעלות עם וינדוס)", AutoSize = true, Checked = true };
    readonly Label _error = new() { ForeColor = Color.Firebrick, AutoSize = true };

    public string Password => _pw.Text;
    public bool Remember => _remember.Checked;

    public PasswordForm(string? error)
    {
        Ui.Rtl(this);
        Text = "DriveGram – סיסמת הכונן";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var ok = new Button { Text = "פתח את הכונן", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;
        _error.Text = error ?? "";

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([ok, cancel]);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.AddRange([new Label { Text = "הקלד את סיסמת הכונן:", AutoSize = true }, _pw, _remember, _error, buttons]);
        Controls.Add(layout);
        Shown += (_, _) => { Activate(); _pw.Focus(); };
    }
}

/// <summary>The drive's own recycle bin: restore or delete forever.</summary>
sealed class TrashForm : Form
{
    readonly DriveGram.Host.DriveSession _session;
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = true,
        RightToLeftLayout = true, HideSelection = false,
    };

    public TrashForm(DriveGram.Host.DriveSession session)
    {
        _session = session;
        Ui.Rtl(this);
        Text = "DriveGram – סל המחזור";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 520);

        _list.Columns.Add("שם", 260);
        _list.Columns.Add("מיקום מקורי", 300);
        _list.Columns.Add("נמחק ב־", 150);
        _list.Columns.Add("גודל", 100);

        var restore = new Button { Text = "שחזר", AutoSize = true };
        var purge = new Button { Text = "מחק לצמיתות", AutoSize = true };
        var refresh = new Button { Text = "רענן", AutoSize = true };
        restore.Click += (_, _) => Act(restore: true);
        purge.Click += (_, _) => Act(restore: false);
        refresh.Click += (_, _) => Reload();
        _list.DoubleClick += (_, _) => Act(restore: true);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bar.Controls.AddRange([restore, purge, refresh,
            new Label { Text = "פריטים נמחקים לצמיתות אוטומטית אחרי 30 יום.", AutoSize = true, Padding = new Padding(12, 8, 0, 0), ForeColor = Color.DimGray }]);
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
            var item = new ListViewItem([t.Dir ? $"📁 {t.Name}" : t.Name, t.OriginalFolder, t.DeletedAt.ToString("dd/MM/yyyy HH:mm"),
                t.Dir ? "" : Ui.Size(t.Size)]) { Tag = t };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        Text = $"DriveGram – סל המחזור ({_list.Items.Count})";
    }

    void Act(bool restore)
    {
        var items = _list.SelectedItems.Cast<ListViewItem>().Select(i => (DriveGram.Host.TrashItem)i.Tag!).ToList();
        if (items.Count == 0) return;
        if (!restore && MessageBox.Show($"למחוק לצמיתות {items.Count} פריטים? אי אפשר לבטל את זה.", "DriveGram",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading) != DialogResult.Yes)
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
