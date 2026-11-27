using DriveGram.Engine;
using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>What the transfers window needs from the tray.</summary>
interface ITransfersHost
{
    IReadOnlyList<DriveController> Drives { get; }
    Preparer Preparer { get; }
    DailyUploads Daily { get; }
    /// <summary>The user paused (or resumed) every upload of a drive.</summary>
    void SetPaused(DriveController d, bool paused);
    /// <summary>"Resume all": every pause of the user ends; past the daily cap only if the user agrees.</summary>
    void ResumeEverything();
    /// <summary>Resume just these files: everything else that is paused stays paused (asks before passing the daily cap).</summary>
    void ResumeOnly(IReadOnlyList<(DriveController D, string Id)> files);
    void LiftCap();
    /// <summary>Show the program's settings (where the daily cap is set).</summary>
    void OpenSettings();
    void PrepareFiles(IEnumerable<(DriveSession Session, string Id)> files);
    /// <param name="confirmed">The caller already asked and says the result itself: no question, no notice.</param>
    Task<long> FreeFiles(IEnumerable<(DriveSession Session, string Id)> files, bool confirmed = false);
    /// <summary>A short notice (a balloon by the clock).</summary>
    void Notify(string title, string text);
}

/// <summary>Icons from Windows' own symbol font (Segoe Fluent Icons on Windows 11, MDL2 before).</summary>
static class Glyphs
{
    public const char Pause = '', Play = '', Cancel = '', Upload = '', Download = '',
        Pin = '', Delete = '', Clock = '', Warning = '', Check = '', Settings = '',
        Refresh = '', Edit = '', Add = '', Gauge = '', Hourglass = '', Computer = '',
        OpenFile = '\uE8E5', Folder = '\uE838', Copy = '\uE8C8', SelectAll = '\uE8B3',
        People = '', Disk = '', Person = '', More = '';

    static readonly string Family = new System.Drawing.Text.InstalledFontCollection().Families
        .Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Bitmap Make(char glyph, Color color, int size = 16)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(Family, size * 0.72f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var s = glyph.ToString();
        var sz = g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic);
        g.DrawString(s, font, brush, (size - sz.Width) / 2, (size - sz.Height) / 2, StringFormat.GenericTypographic);
        return bmp;
    }
}

/// <summary>
/// Everything moving between this PC and Telegram: the files waiting to upload or uploading (pause,
/// resume, cancel, the daily limit), and the files on this PC (how much of each, prepare a whole file
/// ahead of time, free it again).
/// </summary>
sealed class TransfersForm : Form
{
    readonly ITransfersHost _host;
    readonly ThemedTabs _tabs = new() { Dock = DockStyle.Fill };
    readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, 740);
    readonly Panel _capBar = Theme.Card(Theme.WarnBack, Theme.WarnBorder);
    readonly Label _capText = Theme.Label("", Theme.Small, Theme.Text, 600);

    // uploads
    readonly ListView _up = NewList();
    readonly Label _upEmpty = Theme.Label(L.T("✓  אין העלאות כרגע. הכל שמור בטלגרם.", "✓  No uploads right now. Everything is saved in Telegram."), Theme.Body, Theme.Success);
    readonly ToolStripButton _pauseAll, _resumeAll, _pauseSel, _resumeSel, _cancelSel;

    // on this PC
    readonly ListView _local = NewList();
    readonly Label _localState = Theme.Label("", Theme.Small, Theme.Muted, 740);
    readonly ToolStripButton _prepareSel, _freeSel;
    readonly Label _localEmpty = Theme.Label("", Theme.Body, Theme.Muted);
    readonly ToolStripComboBox _localFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 140 };
    readonly ToolStripTextBox _localSearch = SearchBox(), _upSearch = SearchBox();
    bool _scanning;
    List<LocalRow> _localRows = [];
    int _localTotal; // files found (the list shows at most 2,000)
    readonly ListSort _upSort, _localSort;
    readonly Dictionary<string, long> _upRank = [], _localRank = [];
    readonly UiPrefs _prefs = UiPrefs.Load();
    bool _settingFilter;
    readonly System.Windows.Forms.Timer _searchWait = new() { Interval = 250 };
    bool _scannedOnce;

    readonly ImageList _icons = new() { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    readonly ToolTip _legendTip = new() { AutoPopDelay = 20000, InitialDelay = 300 };

    /// <summary>What the marks on files in Explorer mean (the real icons), one short line each: the uploads
    /// tab shows the uploading marks, the files tab the "on this PC" ones; the full meaning when the mouse
    /// rests on one.</summary>
    Control Legend(bool uploading)
    {
        var box = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 4, 0, 0), Margin = new Padding(0),
        };
        FlowLayoutPanel row = null!;
        void Group(string title)
        {
            row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(0), MaximumSize = new Size(900, 0) };
            row.Controls.Add(new Label { Text = title, Font = Theme.Small, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 3, 10, 4) });
            box.Controls.Add(row);
        }
        void Item(string? icon, string text, string tip)
        {
            var item = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 14, 4) };
            if (icon is not null && OverlayMark(icon) is { } img)
            {
                var pic = new PictureBox { Image = img, Size = img.Size, SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 0, 3, 0) };
                _legendTip.SetToolTip(pic, tip);
                item.Controls.Add(pic);
            }
            var label = new Label { Text = text, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            _legendTip.SetToolTip(label, tip);
            item.Controls.Add(label);
            _legendTip.SetToolTip(item, tip);
            row.Controls.Add(item);
        }
        if (!uploading) goto OnThisPc;
        Group(L.T("סימנים בסייר הקבצים – העלאה:", "Marks in Explorer – uploading:"));
        Item("upload", L.T("עוד לא בטלגרם", "Not in Telegram yet"),
            L.T("הקובץ (או שינוי בו) מחכה להעלאה או עולה עכשיו. בינתיים הוא שמור רק במחשב הזה, גם אחרי כיבוי, ויעלה לטלגרם כשהכונן יתחבר.",
                "The file (or a change to it) is waiting to upload or uploading now. Meanwhile it is kept only on this PC, also after a shutdown, and goes up to Telegram while the drive is connected."));
        Item("paused", L.T("ההעלאה מושהית, עוד לא בטלגרם", "Upload paused, not in Telegram yet"),
            L.T("ההעלאה של הקובץ מושהית. בינתיים הוא שמור רק במחשב הזה, ויעלה כשתמשיך את ההעלאה.",
                "The file's upload is paused. Meanwhile it is kept only on this PC, and goes up when you resume the upload."));
        return box;
    OnThisPc:
        Group(L.T("סימנים בסייר הקבצים – במחשב הזה:", "Marks in Explorer – on this PC:"));
        Item("kept", L.T("בטלגרם, ומוכן כאן", "In Telegram, prepared here"),
            L.T("שמור בטלגרם, והכנת אותו במחשב הזה (\"הכן\"): הוא נשאר כאן עד שתפנה אותו, ונפתח מיד, גם בלי אינטרנט.",
                "Saved in Telegram, and you prepared it on this PC (\"Prepare\"): it stays here until you free it, and opens at once, even offline."));
        Item("local", L.T("בטלגרם, וכולו כאן (זמני)", "In Telegram, all here (temporary)"),
            L.T("שמור בטלגרם, וכל הקובץ נמצא גם במחשב הזה, אז הוא נפתח מיד. DriveGram עשוי למחוק את העותק הזה לבד כשצריך מקום. כדי שיישאר, בחר \"הכן\".",
                "Saved in Telegram, and all of it is on this PC too, so it opens at once. DriveGram may delete this copy by itself when room is needed. To keep it, choose \"Prepare\"."));
        Item("partial", L.T("בטלגרם, וחלק ממנו כאן", "In Telegram, part of it here"),
            L.T("שמור בטלגרם. רק החלקים שכבר נפתחו נמצאים במחשב הזה, והשאר יורד כשצריך אותו.",
                "Saved in Telegram. Only the parts already opened are on this PC; the rest downloads when needed."));
        Group(L.T("בלי סימן: הקובץ רק בטלגרם (התמונה הממוזערת נשארת)", "No mark: the file is only in Telegram (its thumbnail stays)"));
        _legendTip.SetToolTip(row.Controls[0], L.T("שמור בטלגרם, והקובץ עצמו לא במחשב הזה: הוא יורד כשפותחים אותו (צריך אינטרנט). התמונה הממוזערת והפרטים שלו נשארים, כדי שהתיקייה תיפתח בלי הורדות.",
            "Saved in Telegram, and the file itself is not on this PC: it downloads when opened (needs internet). Its thumbnail and details stay, so the folder opens without downloads."));
        return box;
    }

    /// <summary>Are all of Explorer's mark names in place (machine-wide, written at install with Windows'
    /// permission)? Declined: some marks never show, and the legend says so.</summary>
    static bool OverlayNamesInstalled()
    {
        try
        {
            using var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers");
            if (k is null) return true; // cannot tell: say nothing
            var names = k.GetSubKeyNames();
            return new[] { "   DriveGramFull", "   DriveGramPartial", "   DriveGramKept", "   DriveGram1Upload", "   DriveGram2Paused" }
                .All(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
        }
        catch { return true; }
    }

    /// <summary>The mark itself, cut out of the overlay icon the Explorer extension uses (its corner of a
    /// 32 px icon); null when the icon file is not there.</summary>
    static Image? OverlayMark(string name)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir is not null; i++) // next to the program; while developing, the repo's assets
            {
                foreach (var p in new[] { Path.Combine(dir, $"overlay-{name}.ico"), Path.Combine(dir, "assets", $"overlay-{name}.ico") })
                    if (File.Exists(p))
                    {
                        using var icon = new Icon(p, 32, 32);
                        using var full = icon.ToBitmap();
                        return full.Clone(new Rectangle(0, 8, 24, 24), full.PixelFormat); // the mark's corner, whole
                    }
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
        }
        catch (Exception ex) { Log.Debug($"legend icon {name}: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// The strip under a list: what the marks in Explorer mean, and one short line about this list. Always
    /// there — help nobody opens is help nobody reads — and short enough to leave the list its room; the
    /// full explanation of every item comes when the mouse rests on it.
    /// </summary>
    Control Help(Control note, Control legend)
    {
        var box = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 7, 12, 8),
        };
        box.Wears(Slot.Surface);
        box.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, box.Width, 0); // a line of its own, so it is not read as a row
        };
        foreach (var c in new[] { legend, note }) { c.Dock = DockStyle.None; c.Margin = new Padding(0, 2, 0, 2); box.Controls.Add(c); }
        // the strip is as wide as the window, which the user can make narrow: everything in it wraps to
        // what there is instead of being cut off (it can no longer be closed to get the room back)
        box.Resize += (_, _) => FitWidth(box, box.ClientSize.Width - box.Padding.Horizontal);
        return box;
    }

    /// <summary>Let every label and every wrapping row inside know how much width it really has.</summary>
    static void FitWidth(Control parent, int width)
    {
        if (width < 80) return;
        foreach (Control c in parent.Controls)
        {
            if (c is Label label) label.MaximumSize = new Size(width, 0);
            else if (c is FlowLayoutPanel { WrapContents: true } row) row.MaximumSize = new Size(width, 0);
            FitWidth(c, width);
        }
    }

    static ToolStripTextBox SearchBox()
    {
        var box = new ToolStripTextBox { AutoSize = false, Width = 140, Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.Never };
        box.TextBox.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
        box.TextBox.HandleCreated += (_, _) => SetCue(box.TextBox, L.T("חיפוש לפי שם…", "Search by name…"));
        return box;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    static void SetCue(TextBox box, string text) => SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, 1, text);

    static bool Matches(string name, string search) =>
        search.Length == 0 || name.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The file column takes the room the others leave (no sideways scrolling in a narrow window).</summary>
    static void FitColumns(ListView list)
    {
        if (list.Columns.Count == 0 || !list.IsHandleCreated) return;
        int others = 0;
        for (int i = 1; i < list.Columns.Count; i++) others += list.Columns[i].Width;
        int w = Math.Max(150, list.ClientSize.Width - others - 2);
        if (Math.Abs(list.Columns[0].Width - w) > 2) list.Columns[0].Width = w;
    }

    /// <summary>A mark of Explorer, list size (the rows look like the files in Explorer).</summary>
    static Image Mark(string name, Image fallback)
    {
        if (OverlayMark(name) is not { } m) return fallback;
        var b = new Bitmap(18, 18);
        using (var g = Graphics.FromImage(b))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(m, new Rectangle(0, 0, 18, 18));
        }
        m.Dispose();
        return b;
    }

    /// <summary>A file on this PC. Session is null for a drive that is not connected (Offline: only what was
    /// prepared, read from the drive's local index).</summary>
    sealed record LocalRow(DriveController Drive, DriveSession? Session, string Id, string Name, string Path, long Size, double Fraction, bool Prepared, bool Offline = false);

    public TransfersForm(ITransfersHost host)
    {
        _host = host;
        Ui.Prepare(this);
        Text = L.T("DriveGram – העברות וקבצים במחשב", "DriveGram – transfers and files on this PC");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(940, 620);
        MinimumSize = new Size(700, 420);

        _icons.Images.Add("wait", Glyphs.Make(Glyphs.Clock, Theme.Muted, 18));
        // the same marks as on the files in Explorer (the clock and the pencil are only here)
        _icons.Images.Add("up", Mark("upload", Glyphs.Make(Glyphs.Upload, Theme.Accent, 18)));
        _icons.Images.Add("paused", Mark("paused", Glyphs.Make(Glyphs.Pause, Theme.Attention, 18)));
        _icons.Images.Add("writing", Glyphs.Make(Glyphs.Edit, Theme.Muted, 18));
        _icons.Images.Add("ready", Mark("kept", Glyphs.Make(Glyphs.Pin, Theme.Success, 18)));
        _icons.Images.Add("down", Glyphs.Make(Glyphs.Download, Theme.Accent, 18));
        _icons.Images.Add("part", Mark("partial", Glyphs.Make(Glyphs.Computer, Theme.Muted, 18)));
        _icons.Images.Add("full", Mark("local", Glyphs.Make(Glyphs.Computer, Theme.Accent, 18))); // all here (temporary)
        _icons.Images.Add("offline", Glyphs.Make(Glyphs.Pin, Theme.Muted, 18));
        _up.SmallImageList = _icons;
        _local.SmallImageList = _icons;

        // ── header: speed, time left, today's total; the daily cap when it holds uploads back ──
        var head = Theme.Column();
        head.Dock = DockStyle.Top;
        head.AutoSize = true;
        head.Padding = new Padding(16, 12, 16, 6);
        head.Controls.Add(_summary);
        var capRow = Theme.Column(); // the text, then its button (side by side, a larger font pushed the button out)
        var lift = Theme.Secondary(L.T("המשך להעלות בכל זאת", "Upload anyway"));
        lift.Click += (_, _) => { _host.LiftCap(); RefreshNow(); };
        _capText.Margin = new Padding(0, 6, 0, 0);
        capRow.Controls.AddRange([_capText, lift]);
        _capBar.Controls.Add(capRow);
        _capBar.Visible = false;
        head.Controls.Add(_capBar);
        if (!OverlayNamesInstalled())
        {
            var missing = Theme.Label(L.T("חלק מהסימנים בסייר הקבצים לא הותקנו – פרטים", "Some of the marks in Explorer are not installed – details"), Theme.Small, Theme.Error, 740);
            missing.Margin = new Padding(0, 4, 0, 0);
            _legendTip.SetToolTip(missing, L.T("לא אושרה בקשת ווינדוס בהתקנה, ולכן חלק מהסימנים על הקבצים לא יופיעו. כדי להוסיף אותם, התקן את DriveGram שוב ואשר את הבקשה.",
                "Windows' request at install was not approved, so some of the marks on files do not show. To add them, install DriveGram again and approve the request."));
            head.Controls.Add(missing);
        }

        // ── tab 1: uploads ──
        var upTab = new Panel { Dock = DockStyle.Fill }.Wears(Slot.Window);
        var upBar = NewBar();
        _pauseAll = Button(upBar, Glyphs.Pause, L.T("השהה הכל", "Pause all"), () => SetAll(true));
        _resumeAll = Button(upBar, Glyphs.Play, L.T("המשך הכל", "Resume all"), () => SetAll(false));
        upBar.Items.Add(new ToolStripSeparator());
        _pauseSel = Button(upBar, Glyphs.Pause, L.T("השהה", "Pause"), () => ForSelected(true));
        _resumeSel = Button(upBar, Glyphs.Play, L.T("המשך", "Resume"), () => ForSelected(false));
        _cancelSel = Button(upBar, Glyphs.Cancel, L.T("בטל…", "Cancel…"), CancelSelected, Theme.Error);
        _pauseSel.ToolTipText = L.T("השהה את הקבצים המסומנים ברשימה", "Pause the files selected in the list");
        _resumeSel.ToolTipText = L.T("המשך את הקבצים המסומנים ברשימה", "Resume the files selected in the list");
        _cancelSel.ToolTipText = L.T("בטל את ההעלאה של הקבצים המסומנים ברשימה", "Cancel the upload of the files selected in the list");
        upBar.Items.Add(new ToolStripSeparator());
        var cap = Button(upBar, Glyphs.Gauge, L.T("תקרת העלאה (בהגדרות)…", "Upload cap (in Settings)…"), () => _host.OpenSettings());
        cap.Overflow = ToolStripItemOverflow.Never; // never hidden behind the toolbar's "»"
        cap.ToolTipText = L.T("נקבעת בהגדרות: כמה מותר להעלות ב־24 שעות, והתראה לפני שמגיעים לזה",
            "Set in Settings: how much may be uploaded in 24 hours, and a notice before reaching it");
        upBar.Items.Add(_upSearch);
        _upSearch.TextChanged += (_, _) => RefreshNow();
        _up.Columns.Add(L.T("קובץ", "File"), 280);
        _up.Columns.Add(L.T("כונן", "Drive"), 60);
        _up.Columns.Add(L.T("גודל", "Size"), 90);
        _up.Columns.Add(L.T("התקדמות", "Progress"), BarWidth);
        _up.Columns.Add(L.T("מצב", "State"), 220);
        _up.SelectedIndexChanged += (_, _) => UpdateButtons();
        _up.ContextMenuStrip = RowMenu(UploadMenu);
        _up.KeyDown += (_, e) => Keys(e, _up, cancel: CancelSelected, open: () => ShowInFolder(Selected(_up).Select(x => x.D.Session!.FullPathOf(x.U.Id))));
        _up.ItemActivate += (_, _) => { if (!DemoSays()) ShowInFolder(Selected(_up).Select(x => x.D.Session!.FullPathOf(x.U.Id))); };
        _up.Resize += (_, _) => FitColumns(_up);
        _upSort = ListSort.Attach(_up, (item, col) => item.Tag is not UploadItem u ? null : col switch
        {
            0 => u.Name, 2 => u.Size, 3 => u.Total <= 0 ? 0.0 : (double)u.Done / u.Total,
            _ => item.SubItems[col].Text,
        }, item => _upRank.TryGetValue(item.Name, out var r) ? r : long.MaxValue); // a third click: the queue's order again
        var upNote = Theme.Label(L.T(
            "השהיה שומרת את הקובץ במחשב הזה עד שתמשיך.  ·  \"בטל\" מוחק קובץ חדש, או מחזיר קובץ ששונה לגרסה ששמורה בטלגרם.",
            "Pausing keeps the file on this PC until you resume.  ·  \"Cancel\" deletes a new file, or brings a changed one back to the version saved in Telegram."),
            Theme.Small, Theme.Muted, 860);
        _legendTip.SetToolTip(upNote, L.T(
            "השהיה: קבצים שעוד לא עלו מחכים במחשב הזה. אם מעתיקים עוד לכונן בזמן ההשהיה, ההעתקה תיעצר אם ייגמר המקום במחשב. \"בטל\" על קובץ חדש מוחק אותו מהכונן לצמיתות (הוא עוד לא שמור בטלגרם); על קובץ ששונה כאן, מבטל את השינוי ומחזיר אותו לגרסה השמורה.",
            "Paused: files not uploaded yet wait on this PC. Copying more into the drive while paused stops if this PC runs out of space. \"Cancel\" on a new file deletes it from the drive for good (it is not saved in Telegram yet); on a file changed here, it drops the change and brings back the saved version."));
        _upEmpty.Dock = DockStyle.Top;
        _upEmpty.Padding = new Padding(10, 14, 10, 0);
        _upEmpty.Visible = false;
        upTab.Controls.AddRange([_up, _upEmpty, Help(upNote, Legend(uploading: true)), upBar]);

        // ── tab 2: on this PC ──
        var localTab = new Panel { Dock = DockStyle.Fill }.Wears(Slot.Window);
        var localBar = NewBar();
        Button(localBar, Glyphs.Add, L.T("בחר קובץ מהכונן להכנה…", "Choose a file from the drive to prepare…"), PickAndPrepare);
        _prepareSel = Button(localBar, Glyphs.Download, L.T("הכן את המסומנים", "Prepare the selected"), PrepareSelected);
        _freeSel = Button(localBar, Glyphs.Delete, L.T("פנה מהמחשב הזה", "Free from this PC"), FreeSelected);
        _prepareSel.ToolTipText = L.T("הורד את כל הקבצים שבחרת ברשימה ושמור אותם במחשב הזה", "Download all of the files chosen in the list and keep them on this PC");
        _freeSel.ToolTipText = L.T("מחק מהמחשב הזה את העותקים של הקבצים שבחרת ברשימה (הם נשארים בטלגרם)", "Delete this PC's copies of the files chosen in the list (they stay in Telegram)");
        localBar.Items.Add(new ToolStripSeparator());
        var refresh = Button(localBar, Glyphs.Refresh, L.T("רענן", "Refresh"), () => _ = ScanAsync());
        refresh.ToolTipText = "F5";
        _localFilter.Items.AddRange([L.T("כל הקבצים", "All files"), L.T("רק קבצים שהכנתי", "Only files I prepared"), L.T("רק עותקים זמניים", "Only temporary copies")]);
        _localFilter.SelectedIndex = 0;
        _localFilter.Alignment = ToolStripItemAlignment.Right;
        _localFilter.Overflow = ToolStripItemOverflow.Never;
        _localFilter.ToolTipText = L.T("אילו קבצים להציג", "Which files to show");
        _localFilter.SelectedIndexChanged += (_, _) =>
        {
            if (!_settingFilter) { _prefs.LocalFilter = _localFilter.SelectedIndex; _prefs.Save(); }
            FillLocal();
        };
        _searchWait.Tick += (_, _) => { _searchWait.Stop(); if (!IsDisposed) FillLocal(); };
        _localSearch.TextChanged += (_, _) => { _searchWait.Stop(); _searchWait.Start(); };
        localBar.Items.Add(_localSearch);
        localBar.Items.Add(_localFilter);
        localBar.Items.Add(new ToolStripLabel(L.T("הצג:", "Show:")) { Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.Never });
        _local.Columns.Add(L.T("קובץ", "File"), 280);
        _local.Columns.Add(L.T("כונן", "Drive"), 60);
        _local.Columns.Add(L.T("גודל", "Size"), 90);
        _local.Columns.Add(L.T("במחשב הזה", "On this PC"), BarWidth);
        _local.Columns.Add(L.T("מצב", "State"), 220);
        _local.SelectedIndexChanged += (_, _) => UpdateButtons();
        _local.ContextMenuStrip = RowMenu(LocalMenu);
        _local.KeyDown += (_, e) => Keys(e, _local, cancel: FreeSelected, open: () => OpenFiles(SelectedLocal().Select(r => r.Path)));
        _local.ItemActivate += (_, _) =>
        {
            if (DemoSays()) return;
            var here = SelectedLocal().ToList();
            if (here.Count == 0 && SelectedOffline().Count > 0) { Ui.Info(L.T("הכונן לא מחובר – אפשר לפתוח את הקובץ אחרי שהכונן יתחבר.", "The drive is not connected – the file opens once the drive connects.")); return; }
            OpenFiles(here.Select(r => r.Path));
        };
        _local.Resize += (_, _) => FitColumns(_local);
        _localSort = ListSort.Attach(_local, (item, col) => item.Tag is not LocalRow r ? null : col switch
        {
            0 => r.Name, 2 => r.Size, 3 => r.Fraction,
            _ => item.SubItems[col].Text,
        }, item => _localRank.TryGetValue(item.Name, out var k) ? k : long.MaxValue);
        var localNote = Theme.Label(L.T(
            "\"הכן\" מוריד קובץ שלם ושומר אותו כאן עד שתפנה אותו — הוא ייפתח מיד, גם בלי אינטרנט.  ·  אפשר גם בלחיצה ימנית על הקובץ בסייר הקבצים.",
            "\"Prepare\" downloads a whole file and keeps it here until you free it — it opens at once, even offline.  ·  Also by right-clicking the file in Explorer."),
            Theme.Small, Theme.Muted, 860);
        _legendTip.SetToolTip(localNote, L.T(
            "\"הכן\" מוריד את כל הקובץ עכשיו ושומר אותו במחשב עד שתפנה אותו, למשל סרט לערב: הוא ייפתח מיד, גם בלי אינטרנט. גם בסייר הקבצים: לחיצה ימנית על קובץ בכונן ← \"הצג אפשרויות נוספות\" ← \"הכן במחשב הזה (הורד את כולו)\". אחרי \"פנה\" נשארות רק התמונה הממוזערת והפרטים (כמו אורך סרטון), כדי שהתיקייה תיפתח בלי הורדות.",
            "\"Prepare\" downloads the whole file now and keeps it on this PC until you free it, e.g. a movie for tonight: it opens at once, even offline. In Explorer too: right-click a file in the drive → \"Show more options\" → \"Prepare on this PC (download all of it)\". After \"Free\", only the thumbnail and the details (like a video's length) stay, so the folder opens without downloads."));
        _localState.Dock = DockStyle.Bottom;
        _localState.Padding = new Padding(8, 4, 8, 2);
        _localEmpty.Dock = DockStyle.Top;
        _localEmpty.Padding = new Padding(10, 14, 10, 0);
        _localEmpty.Visible = false;
        localTab.Controls.AddRange([_local, _localEmpty, _localState, Help(localNote, Legend(uploading: false)), localBar]);

        // as the user left them last time
        _upSort.Set(_prefs.UpSortColumn, _prefs.UpSortDescending);
        _localSort.Set(_prefs.LocalSortColumn, _prefs.LocalSortDescending);
        _upSort.Changed += () => { (_prefs.UpSortColumn, _prefs.UpSortDescending) = (_upSort.Column, _upSort.Descending); _prefs.Save(); };
        _localSort.Changed += () => { (_prefs.LocalSortColumn, _prefs.LocalSortDescending) = (_localSort.Column, _localSort.Descending); _prefs.Save(); };
        _settingFilter = true;
        if (_prefs.LocalFilter is >= 0 and <= 2) _localFilter.SelectedIndex = _prefs.LocalFilter;
        _settingFilter = false;

        _tabs.Add(L.T("העלאות לטלגרם", "Uploads to Telegram"), Glyphs.Upload, upTab);
        _tabs.Add(L.T("קבצים במחשב הזה", "Files on this PC"), Glyphs.Computer, localTab);
        _tabs.SelectedChanged += index => { if (index == 1 && !_scanning && !_demo) _ = ScanAsync(); UpdateButtons(); };
        Controls.Add(_tabs);
        Controls.Add(head);

        _timer.Tick += (_, _) => RefreshNow();
        // a prepare that finished: its row shows the result (not the last progress it reported)
        Action<Preparer.Job, bool, string?> prepared = (_, _, _) => { if (!_demo && !IsDisposed && _tabs.Selected == 1) _ = ScanAsync(); };
        _host.Preparer.Finished += prepared;
        Disposed += (_, _) => _host.Preparer.Finished -= prepared;
        Shown += (_, _) => Start();
        FormClosed += (_, _) => _timer.Dispose();
        Disposed += (_, _) => { _timer.Dispose(); _searchWait.Dispose(); };
    }

    /// <summary>Back to the uploads list (a notice about uploads opens it there).</summary>
    public void ShowUploadsTab() => _tabs.Selected = 0;

    /// <summary>The window is shown again, or the drives changed: the files list shows what is there now.</summary>
    public void Rescan() { if (_tabs.Selected == 1 && !_demo && !IsDisposed) _ = ScanAsync(); }

    public void ShowLocalTab()
    {
        if (_tabs.Selected == 1 && !_demo) _ = ScanAsync(); // already on it: show what is there now
        _tabs.Selected = 1;
    }

    /// <summary>The files I prepared (the tray's "Files I prepared…", and the notice when C: runs low).</summary>
    public void ShowPrepared()
    {
        _localSearch.Text = "";
        _settingFilter = true; // shown this way now; the user's own choice stays for next time
        _localFilter.SelectedIndex = 1;
        _settingFilter = false;
        ShowLocalTab();
    }

    /// <summary>Start refreshing (on its own it starts when shown; inside the main window, as a tab, it is called).</summary>
    public void Start()
    {
        if (_demo || _timer.Enabled) return;
        RefreshNow();
        _timer.Start();
    }

    bool _demo;

    /// <summary>Developer aid (screenshots): sample rows, no drive needed.</summary>
    internal void Demo(int tab)
    {
        _demo = true;
        _timer.Stop(); // the sample rows stay put (a refresh would replace them with the real ones: none)
        _summary.Text = L.T("נותרו להעלות 11.6 GB   ·   מהירות 4.2 MB לשנייה   ·   עוד כ־47 דקות   ·   ב־24 שעות עלו 23.4 GB   ·   תקרה ל־24 שעות 50 GB",
            "11.6 GB left to upload   ·   4.2 MB/s   ·   about 47 minutes left   ·   23.4 GB uploaded in 24 hours   ·   cap 50 GB");
        long G = 1L << 30, M = 1L << 20;
        var ups = new[]
        {
            (new UploadItem("1", "Loki.S02E04.1080p.mkv", (long)(2.5 * G), (long)(1.2 * G), (long)(2.5 * G), UploadState.Uploading), "up", L.T("עולה", "Uploading")),
            (new UploadItem("2", "עונה 1 פרק 3.mp4", 400 * M, 0, 400 * M, UploadState.Paused), "paused", L.T("מושהה", "Paused")),
            (new UploadItem("3", "Photos 2024.zip", 8 * G, 64 * M, 8 * G, UploadState.Writing), "writing", L.T("עדיין מועתק לכונן", "Still being copied in")),
            (new UploadItem("4", "דוח שנתי.pdf", 3 * M, 0, 3 * M, UploadState.Waiting), "wait", L.T("ממתין בתור", "Waiting in line")),
        };
        foreach (var (u, icon, state) in ups)
        {
            var item = new ListViewItem(FileName(u.Name)) { Tag = u, ImageKey = icon, Name = "x/" + u.Id };
            item.SubItems.AddRange([L.Ltr("T:"), Ui.Size(u.Size), Bar((double)u.Done / u.Total), state]);
            item.ToolTipText = @"T:\" + u.Name + "\n" + UploadTip(u.State, state);
            _up.Items.Add(item);
        }
        _up.Items[1].Selected = true;
        var locals = new[]
        {
            (new LocalRow(null!, null!, "a", "הסרט של הערב.mkv", @"T:\Movies\x.mkv", 3 * G, 1, true), "ready", L.T("מוכן – יישאר עד שתפנה", "Ready – stays until you free it")),
            (new LocalRow(null!, null!, "b", "Loki.S02E05.1080p.mkv", @"T:\x.mkv", (long)(2.3 * G), 0.42, true), "down", L.T("מוריד למחשב הזה…", "Downloading to this PC…")),
            (new LocalRow(null!, null!, "c", "עונה 1 פרק 3.mp4", @"T:\x.mp4", 400 * M, 0.55, false), "part", L.T("חלקו כאן (זמני)", "Partly here (temporary)")),
            (new LocalRow(null!, null!, "d", "מצגת.pptx", @"T:\x.pptx", 12 * M, 1, false), "full", L.T("כולו כאן (זמני)", "All here (temporary)")),
        };
        foreach (var (r, icon, state) in locals)
        {
            var item = new ListViewItem(FileName(r.Name)) { Tag = r, ImageKey = icon };
            item.SubItems.AddRange([L.Ltr("T:"), Ui.Size(r.Size), Bar(r.Fraction), state]);
            item.ToolTipText = r.Path + "\n" + LocalTip(state, r.Fraction, r.Prepared, icon == "down" && r.Fraction < 1);
            _local.Items.Add(item);
        }
        _localState.Text = L.T("קבצים שהוכנו תופסים 5.3 GB. עותקים זמניים של קבצים שנפתחו: 1.1 GB (נמחקים לבד כשצריך מקום).",
            "Prepared files take 5.3 GB. Temporary copies of files you opened: 1.1 GB (removed by themselves when space is needed).");
        _tabs.Selected = tab;
        _capBar.Visible = tab == 0;
        _capText.Text = L.T("הגעת לתקרה (50GB ב־24 שעות): ההעלאות מושהות ויימשכו מעצמן בעוד כ־3 שעות (בערך ב־21:15). הקבצים מחכים בבטחה במחשב הזה.",
            "The cap (50 GB in 24 hours) was reached: uploads are paused and go on by themselves in about 3 hours (around 21:15). The files wait safely on this PC.");
    }

    static readonly int BarWidth = TextRenderer.MeasureText("100%  " + new string('\u25B0', 8), Ui.Font).Width + 36;

    /// <summary>A file name as shown in the lists: a Hebrew name keeps its extension at its end (a right-to-
    /// left mark before the last dot), e.g. "עונה 1 פרק 3.mp4" and "מצגת.pptx", not "מצגתpptx.".</summary>
    static string FileName(string name)
    {
        int dot = name.LastIndexOf('.');
        bool hebrew = name.Any(c => c >= '\u0590' && c <= '\u05FF');
        if (!hebrew || dot <= 0) return L.Name(name);
        return name.Insert(dot, char.IsDigit(name[dot - 1]) ? "\u200E" : "\u200F"); // "פרק 3.mp4" / "מצגת.pptx"
    }

    static ListView NewList() => new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, ShowItemToolTips = true,
        BorderStyle = BorderStyle.None, Font = Ui.Font, RightToLeftLayout = L.Rtl,
        RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No,
    };

    static ToolStrip NewBar()
    {
        var bar = new ToolStrip
        {
            Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Font = Ui.Font,
            Padding = new Padding(4, 4, 4, 4), ImageScalingSize = new Size(16, 16),
        };
        bar.Renderer = ThemePaint.Renderer;
        return bar.Wears(Slot.Surface, Slot.Text);
    }

    static ToolStripButton Button(ToolStrip bar, char glyph, string text, Action click, Color? color = null)
    {
        var b = new ToolStripButton(text, Glyphs.Make(glyph, color ?? Theme.AccentDark, 16)) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Margin = new Padding(2, 0, 6, 0) };
        b.Click += (_, _) => click();
        bar.Items.Add(b);
        return b;
    }

    /// <summary>"42%  ▰▰▰▱▱▱▱▱": a progress bar made of text (right to left in Hebrew, by itself).</summary>
    static string Bar(double f)
    {
        f = Math.Clamp(f, 0, 1);
        int n = (int)Math.Round(f * 8);
        // the percent first: a narrow column cuts the bar, never the number
        return $"{Math.Floor(f * 100):0}%  " + new string('\u25B0', n) + new string('\u25B1', 8 - n);
    }

    // ── refresh ──────────────────────────────────────────────────────────────
    void RefreshNow()
    {
        if (IsDisposed || _demo) return;
        var drives = _host.Drives.Where(d => d.Session is not null && !d.Stopping).ToList();
        double speed = drives.Sum(d => d.Uploading ? d.Speed : 0);
        long left = 0;
        foreach (var d in drives) try { left += d.Session!.Engine.BytesToUpload(); } catch { }
        var parts = new List<string>();
        if (left > 0)
        {
            parts.Add(L.T($"נותרו להעלות {Ui.Size(left)}", $"{Ui.Size(left)} left to upload"));
            if (speed > 1024) parts.Add(L.T($"מהירות {Ui.Size((long)speed)} לשנייה", $"{Ui.Size((long)speed)}/s"));
            if (speed > 1024) parts.Add(TimeLeft(left / speed));
        }
        parts.Add(L.T($"ב־24 שעות עלו {Ui.Size(_host.Daily.RecentBytes)}", $"{Ui.Size(_host.Daily.RecentBytes)} uploaded in 24 hours"));
        if (_host.Daily.CapGb > 0) parts.Add(L.T($"תקרה ל־24 שעות {_host.Daily.CapGb:0.#} GB", $"24-hour cap {_host.Daily.CapGb:0.#} GB"));
        var waits = drives.Select(d => d.Session!.WaitingUntil).Where(w => w is not null).Max();
        if (waits is { } until && until > DateTime.Now)
            parts.Add(L.T($"טלגרם ביקש להמתין עד {until:HH:mm:ss}", $"Telegram asked to wait until {until:HH:mm:ss}"));
        _summary.Text = string.Join("\u00A0\u00A0·   ", parts.Select(p => p.Replace(' ', '\u00A0')));
        _capBar.Visible = _host.Daily.CapReached;
        _capText.Text = L.T($"הגעת לתקרה ({_host.Daily.CapGb:0.#} GB ב־24 שעות): ההעלאות מושהות ויימשכו מעצמן {_host.Daily.FreesInWords()}. הקבצים מחכים בבטחה במחשב הזה.",
            $"The cap ({_host.Daily.CapGb:0.#} GB in 24 hours) was reached: uploads are paused and go on by themselves {_host.Daily.FreesInWords()}. The files wait safely on this PC.");

        RefreshUploads(drives);
        if (_tabs.Selected == 1) RefreshLocalProgress();
        UpdateButtons();
    }

    static string TimeLeft(double sec) =>
        sec < 60 ? L.T("עוד פחות מדקה", "under a minute left")
        : sec < 3600 ? L.N((int)Math.Ceiling(sec / 60), "עוד כדקה", "עוד כ־{0} דקות", "about a minute left", "about {0} minutes left")
        : sec < 5400 ? L.T("עוד כשעה", "about an hour left")
        : L.N((int)Math.Round(sec / 3600), "עוד כשעה", "עוד כ־{0} שעות", "about an hour left", "about {0} hours left");

    void RefreshUploads(List<DriveController> drives)
    {
        var rows = new List<(DriveController D, UploadItem U)>();
        foreach (var d in drives)
            try { rows.AddRange(d.Session!.Engine.Uploads().Select(u => (d, u))); } catch { }
        var waitUntil = drives.Select(d => d.Session!.WaitingUntil).Where(w => w is not null).Max();
        int all = rows.Count;
        var search = _upSearch.Text;
        // the drive's speed shared by the files uploading at once: each one's time left, roughly
        var sending = rows.Where(x => x.U.State == UploadState.Uploading).GroupBy(x => x.D).ToDictionary(g => g.Key, g => g.Count());
        _up.BeginUpdate();
        var byKey = _up.Items.Cast<ListViewItem>().ToDictionary(i => i.Name);
        var keep = new HashSet<string>();
        _upRank.Clear();
        long rank = 0;
        var added = new List<ListViewItem>();
        foreach (var (d, u) in rows)
        {
            var key = d.State.Id + "/" + u.Id;
            _upRank[key] = rank++; // the queue's order (oldest first)
            if (!Matches(u.Name, search)) continue;
            keep.Add(key);
            if (!byKey.TryGetValue(key, out var item))
            {
                item = new ListViewItem(FileName(u.Name)) { Name = key };
                item.SubItems.AddRange(["", "", "", ""]);
                added.Add(item);
            }
            item.Tag = u;
            item.SubItems[1].Text = L.Ltr(d.Letter);
            item.SubItems[2].Text = Ui.Size(u.Size);
            item.SubItems[3].Text = Bar(u.Total <= 0 ? 0 : (double)u.Done / u.Total);
            var (icon, state) = u.State switch
            {
                UploadState.Paused => ("paused", d.Session!.Engine.PausedAll && _host.Daily.CapReached ? L.T("מושהה (תקרה)", "Paused (cap)") : L.T("מושהה", "Paused")),
                UploadState.Uploading when waitUntil is { } w && w > DateTime.Now => ("up", L.T("טלגרם ביקש להמתין", "Telegram asked to wait")),
                UploadState.Uploading when d.Uploading && d.Speed / Math.Max(1, sending.GetValueOrDefault(d)) is var each && each > 16 * 1024 && u.Total > u.Done
                    => ("up", L.T("עולה", "Uploading") + "  ·  " + TimeLeft((u.Total - u.Done) / each)),
                UploadState.Uploading => ("up", L.T("עולה", "Uploading")),
                UploadState.Writing => ("writing", L.T("עדיין מועתק לכונן", "Still being copied in")),
                _ => ("wait", L.T("ממתין בתור", "Waiting in line")),
            };
            item.ImageKey = icon;
            item.SubItems[4].Text = state;
            item.ToolTipText = d.Session!.FullPathOf(u.Id) + "\n" + UploadTip(u.State, state);
        }
        foreach (var gone in byKey.Keys.Where(k => !keep.Contains(k))) _up.Items.RemoveByKey(gone);
        if (added.Count > 0) _up.Items.AddRange(added.ToArray());
        _upSort.Apply();
        _up.EndUpdate();
        _upSort.Arrows();
        bool allConnected = _host.Drives.Count > 0 && _host.Drives.All(d => d.Session is not null && !d.Stopping);
        _upEmpty.Text = allConnected ? L.T("✓  אין העלאות כרגע. הכל שמור בטלגרם.", "✓  No uploads right now. Everything is saved in Telegram.")
            : drives.Count == 0 ? L.T("אין כונן מחובר כרגע. קבצים שממתינים להעלאה יעלו כשהכונן יתחבר.", "No drive is connected right now. Files waiting to upload go up when the drive connects.")
            : L.T("אין העלאות כרגע בכוננים המחוברים. קבצים של כונן שאינו מחובר יעלו כשהוא יתחבר.", "No uploads right now on the connected drives. Files of a drive that is not connected go up when it connects.");
        // green means "saved in Telegram": not for "no drive is connected", which says nothing of the sort
        _upEmpty.Says(allConnected && drives.Count > 0 ? Slot.Success : Slot.Muted);
        if (all > 0 && keep.Count == 0)
        {
            _upEmpty.Text = L.T($"אין העלאות ששמן מכיל \"{search.Trim()}\".", $"No uploads whose name contains \"{search.Trim()}\".");
            _upEmpty.Says(Slot.Muted);
        }
        _upEmpty.Visible = keep.Count == 0;
        _up.Visible = keep.Count > 0;
    }

    static string UploadTip(UploadState st, string state) => state + ": " + (st switch
    {
        UploadState.Paused => L.T("ההעלאה של הקובץ הזה מושהית. הוא שמור במחשב הזה עד שתמשיך אותה.", "This file's upload is paused. It is kept on this PC until you resume it."),
        UploadState.Uploading => L.T("הקובץ עולה עכשיו לטלגרם, מוצפן. אפשר להמשיך לעבוד כרגיל.", "The file is uploading to Telegram now, encrypted. You can keep working as usual."),
        UploadState.Writing => L.T("תוכנה עדיין כותבת את הקובץ לכונן. הוא יעלה כשהכתיבה תסתיים.", "A program is still writing the file into the drive. It uploads when the writing ends."),
        _ => L.T("הקובץ מחכה לתורו. הוא כבר שמור במחשב הזה ויעלה מיד אחרי הקבצים שלפניו.", "The file waits its turn. It is already kept on this PC and uploads right after the files before it."),
    });

    static string LocalTip(string state, double f, bool prepared, bool running) => state + ": " + (
        running ? L.T($"הקובץ יורד עכשיו למחשב הזה ({Math.Floor(f * 100):0}%). כשיסתיים, הוא ייפתח מיד, גם בלי אינטרנט.", $"The file is downloading to this PC now ({Math.Floor(f * 100):0}%). When done, it opens at once, even offline.")
        : prepared && f >= 0.999 ? L.T("כל הקובץ נמצא במחשב הזה ונשמר עד שתפנה אותו. הוא נפתח מיד, גם בלי אינטרנט.", "The whole file is on this PC and stays until you free it. It opens at once, even offline.")
        : prepared ? L.T("ההכנה לא הושלמה (אולי נקטע החיבור). בחר \"הכן\" שוב כדי להשלים.", "Preparing did not finish (perhaps the connection dropped). Choose \"Prepare\" again to finish.")
        : f >= 0.999 ? L.T("כל הקובץ נמצא כאן, אבל כעותק זמני: DriveGram מוחק עותקים כאלה לבד כשצריך מקום. כדי לשמור אותו, בחר \"הכן\".", "The whole file is here, but as a temporary copy: DriveGram removes such copies by itself when space is needed. To keep it, choose \"Prepare\".")
        : L.T($"רק {Math.Floor(f * 100):0}% מהקובץ נמצאים כאן (החלקים שנפתחו), כעותק זמני. השאר יורד מטלגרם כשצריך. כדי שכולו יהיה כאן, בחר \"הכן\".", $"Only {Math.Floor(f * 100):0}% of the file is here (the parts that were opened), as a temporary copy. The rest downloads from Telegram when needed. To have all of it here, choose \"Prepare\"."));

    // ── selection: right-click menus, keys ───────────────────────────────────
    /// <summary>F5 and Ctrl+F wherever the focus is in the window (a list, the toolbar, the tabs).</summary>
    protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
    {
        bool local = _tabs.Selected == 1;
        if (keyData == System.Windows.Forms.Keys.F5) { if (local) _ = ScanAsync(); else RefreshNow(); return true; }
        if (keyData == (System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F)) { (local ? _localSearch : _upSearch).Focus(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    ContextMenuStrip RowMenu(Action<ContextMenuStrip> fill)
    {
        var m = new ContextMenuStrip { RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No, Font = Ui.Font, ShowImageMargin = true };
        m.Renderer = ThemePaint.Renderer;
        m.Opening += (_, e) => { m.Items.Clear(); fill(m); ThemePaint.Menu(m); e.Cancel = m.Items.Count == 0; };
        return m;
    }

    ToolStripMenuItem MenuItem(ContextMenuStrip m, char glyph, string text, Action click, bool enabled = true, Color? color = null, string? keys = null)
    {
        var i = new ToolStripMenuItem(text, Glyphs.Make(glyph, color ?? Theme.AccentDark, 16)) { Enabled = enabled, ShortcutKeyDisplayString = keys };
        i.Click += (_, _) => { if (!DemoSays()) click(); };
        m.Items.Add(i);
        return i;
    }

    /// <summary>"…" with the number of rows when several are selected: it is clear what the action covers.</summary>
    static string Many(string text, int n) => n > 1 ? $"{text} ({n})" : text;

    void UploadMenu(ContextMenuStrip m)
    {
        var sel = Selected(_up).ToList();
        if (sel.Count == 0) return;
        MenuItem(m, Glyphs.Pause, Many(L.T("השהה", "Pause"), sel.Count), () => ForSelected(true), sel.Any(CanPause));
        MenuItem(m, Glyphs.Play, Many(L.T("המשך", "Resume"), sel.Count), () => ForSelected(false), sel.Any(x => x.U.State == UploadState.Paused));
        m.Items.Add(new ToolStripSeparator());
        MenuItem(m, Glyphs.Folder, L.T("הצג בתיקייה", "Show in folder"), () => ShowInFolder(sel.Select(x => x.D.Session!.FullPathOf(x.U.Id))), keys: "Enter");
        MenuItem(m, Glyphs.Copy, L.T("העתק נתיב", "Copy path"), () => CopyPaths(sel.Select(x => x.D.Session!.FullPathOf(x.U.Id))), keys: "Ctrl+C");
        m.Items.Add(new ToolStripSeparator());
        MenuItem(m, Glyphs.SelectAll, L.T("בחר הכל", "Select all"), () => SelectAll(_up), keys: "Ctrl+A");
        m.Items.Add(new ToolStripSeparator());
        MenuItem(m, Glyphs.Cancel, Many(sel.All(x => x.U.Saved) ? L.T("בטל את השינוי…", "Drop the change…") : L.T("בטל את ההעלאה…", "Cancel the upload…"), sel.Count), CancelSelected, color: Theme.Error, keys: "Del");
    }

    /// <summary>Chosen rows of drives that are not connected.</summary>
    List<LocalRow> SelectedOffline() => _local.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<LocalRow>()
        .Where(r => !_demo && (r.Offline || _host.Drives.FirstOrDefault(x => x.State.Id == r.Drive.State.Id) is { Session: null })).ToList();

    void LocalMenu(ContextMenuStrip m)
    {
        var sel = SelectedLocal().ToList();
        var offline = SelectedOffline();
        if (sel.Count == 0 && offline.Count > 0)
        {
            m.Items.Add(new ToolStripMenuItem(L.T("הכונן לא מחובר – אפשר לפתוח אחרי שהכונן יתחבר", "The drive is not connected – it opens once the drive connects")) { Enabled = false });
            MenuItem(m, Glyphs.Delete, Many(L.T("פנה מהמחשב הזה", "Free from this PC"), offline.Count), FreeSelected, keys: "Del");
            MenuItem(m, Glyphs.Copy, L.T("העתק נתיב", "Copy path"), () => CopyPaths(offline.Select(r => r.Path)), keys: "Ctrl+C");
            return;
        }
        if (sel.Count == 0) return;
        MenuItem(m, Glyphs.OpenFile, Many(L.T("פתח", "Open"), sel.Count), () => OpenFiles(sel.Select(r => r.Path)), keys: "Enter").Font = new Font(Ui.Font, FontStyle.Bold);
        MenuItem(m, Glyphs.Folder, L.T("הצג בתיקייה", "Show in folder"), () => ShowInFolder(sel.Select(r => r.Path)));
        m.Items.Add(new ToolStripSeparator());
        MenuItem(m, Glyphs.Download, Many(L.T("הכן: שמור את כולו במחשב הזה", "Prepare: keep all of it on this PC"), sel.Count), PrepareSelected, sel.Any(r => r.Fraction < 0.999 || !r.Prepared));
        var running = sel.Where(r => r.Session is not null && _host.Preparer.ProgressOf(r.Session, r.Id) is not null).ToList();
        if (running.Count > 0)
            MenuItem(m, Glyphs.Cancel, Many(L.T("עצור את ההכנה", "Stop preparing"), running.Count), () =>
            {
                foreach (var r in running) _host.Preparer.Cancel(r.Session!, r.Id);
                _ = ScanAsync();
            });
        MenuItem(m, Glyphs.Delete, Many(L.T("פנה מהמחשב הזה", "Free from this PC"), sel.Count + offline.Count), FreeSelected, keys: "Del");
        m.Items.Add(new ToolStripSeparator());
        MenuItem(m, Glyphs.Copy, L.T("העתק נתיב", "Copy path"), () => CopyPaths(sel.Select(r => r.Path).Concat(offline.Select(r => r.Path))), keys: "Ctrl+C");
        if (offline.Count > 0)
            m.Items.Add(new ToolStripMenuItem(L.N(offline.Count, "קובץ אחד מהנבחרים שייך לכונן לא מחובר: הוא רק יפונה", "{0} מהנבחרים שייכים לכונן לא מחובר: הם רק יפונו",
                "One of the chosen files is of a drive not connected: it is only freed", "{0} of the chosen files are of a drive not connected: they are only freed")) { Enabled = false });
        MenuItem(m, Glyphs.SelectAll, L.T("בחר הכל", "Select all"), () => SelectAll(_local), keys: "Ctrl+A");
    }

    void Keys(KeyEventArgs e, ListView list, Action cancel, Action open)
    {
        if (_demo && !(e.Control && e.KeyCode == System.Windows.Forms.Keys.A)) return;
        if (e.Control && e.KeyCode == System.Windows.Forms.Keys.A) { SelectAll(list); e.Handled = true; }
        else if (e.Control && e.KeyCode == System.Windows.Forms.Keys.C)
        {
            CopyPaths(list == _up ? Selected(_up).Select(x => x.D.Session!.FullPathOf(x.U.Id)) : SelectedLocal().Select(r => r.Path).Concat(SelectedOffline().Select(r => r.Path)));
            e.Handled = true;
        }
        else if (e.KeyCode == System.Windows.Forms.Keys.Delete && list.SelectedItems.Count > 0) { cancel(); e.Handled = true; }

    }

    static void SelectAll(ListView list)
    {
        list.BeginUpdate();
        foreach (ListViewItem i in list.Items) i.Selected = true;
        list.EndUpdate();
    }

    static void CopyPaths(IEnumerable<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths);
        if (text.Length > 0) try { Clipboard.SetText(text); } catch { }
    }

    static void OpenFiles(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count > 10 && Ui.Ask(L.T($"לפתוח {list.Count} קבצים?", $"Open {list.Count} files?")) != DialogResult.Yes) return;
        foreach (var path in list)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error(ex.Message); break; }
    }

    /// <summary>Explorer at the files' folder with them selected (the first folder, when they are in several).</summary>
    static void ShowInFolder(IEnumerable<string> paths)
    {
        var first = paths.FirstOrDefault();
        if (first is null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{first}\""); }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    /// <summary>In the preview (sample rows) an action only says that it would run here.</summary>
    bool DemoSays()
    {
        if (!_demo) return false;
        Ui.Info(L.T("(הדגמה) כאן הפעולה מתבצעת על השורות שנבחרו.", "(preview) Here the action runs on the selected rows."));
        return true;
    }

    void UpdateButtons()
    {
        if (!_demo) // sample rows: "all" stays as it is
        {
            var drives = _host.Drives.Where(d => d.Session is not null).ToList();
            _pauseAll.Enabled = _host.Drives.Any(d => !d.ManualPaused); // also under the daily cap: holds past midnight
            _resumeAll.Enabled = drives.Any(d => d.Session!.Engine.PausedAll) || _host.Drives.Any(d => d.ManualPaused)
                || _up.Items.Cast<ListViewItem>().Any(i => i.Tag is UploadItem { State: UploadState.Paused });
        }
        var sel = Selected(_up).ToList();
        _pauseSel.Text = Many(L.T("השהה", "Pause"), sel.Count);
        _resumeSel.Text = Many(L.T("המשך", "Resume"), sel.Count);
        _cancelSel.Text = Many(L.T("בטל…", "Cancel…"), sel.Count);
        _pauseSel.Enabled = !_cancelling && sel.Any(CanPause);
        _resumeSel.Enabled = sel.Any(x => x.U.State == UploadState.Paused);
        _cancelSel.Enabled = sel.Count > 0 && !_cancelling;
        var lsel = SelectedLocal().ToList();
        _prepareSel.Text = Many(L.T("הכן", "Prepare"), lsel.Count);
        _freeSel.Text = Many(L.T("פנה מהמחשב הזה", "Free from this PC"), lsel.Count + SelectedOffline().Count);
        _prepareSel.Enabled = lsel.Any(r => r.Fraction < 0.999 || !r.Prepared);
        _freeSel.Enabled = lsel.Count > 0 || SelectedOffline().Count > 0;
    }

    // a file can be paused by itself unless it already is (paused only by the daily cap or "pause all", it
    // can still be: then it stays paused after those end)
    bool CanPause((DriveController D, UploadItem U) x) =>
        _demo ? x.U.State != UploadState.Paused : !x.D.Session!.Engine.IsPausedByItself(x.U.Id);

    IEnumerable<(DriveController D, UploadItem U)> Selected(ListView list)
    {
        foreach (ListViewItem i in list.SelectedItems)
        {
            if (i.Tag is not UploadItem u) continue;
            if (_demo) { yield return (null!, u); continue; }
            var driveId = i.Name.Split('/')[0];
            if (_host.Drives.FirstOrDefault(d => d.State.Id == driveId && d.Session is not null) is { } d) yield return (d, u);
        }
    }

    IEnumerable<LocalRow> SelectedLocal()
    {
        foreach (var r in _local.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<LocalRow>())
        {
            if (_demo) { yield return r; continue; }
            // the drive may have reconnected since the list was made: act on its connection now (none: skip)
            var d = _host.Drives.FirstOrDefault(x => x.State.Id == r.Drive.State.Id && x.Session is not null && !x.Stopping);
            if (d is not null) yield return r with { Drive = d, Session = d.Session! };
        }
    }

    // ── actions: uploads ─────────────────────────────────────────────────────
    void SetAll(bool paused)
    {
        if (paused) foreach (var d in _host.Drives) _host.SetPaused(d, true);
        else _host.ResumeEverything();
        RefreshNow();
    }

    void ForSelected(bool pause)
    {
        if (DemoSays()) return;
        var sel = Selected(_up).ToList();
        if (pause) foreach (var g in sel.GroupBy(x => x.D)) g.Key.Session!.Engine.Pause(g.Select(x => x.U.Id).ToList());
        else _host.ResumeOnly(sel.Select(x => (x.D, x.U.Id)).ToList());
        RefreshNow();
    }

    bool _cancelling;

    async void CancelSelected()
    {
        if (DemoSays() || _cancelling) return;
        var sel = Selected(_up).ToList();
        if (sel.Count == 0) return;
        // two kinds: a new file (never in Telegram) is deleted; a change to a saved file is dropped
        static string List(IEnumerable<string> names, int count) =>
            string.Join("\n", names.Take(6).Select(n => "•  " + FileName(n))) + (count > 6 ? "\n…" : "");
        var fresh = sel.Where(x => !x.U.Saved).Select(x => x.U.Name).ToList();
        var changed = sel.Where(x => x.U.Saved).Select(x => x.U.Name).ToList();
        var text = fresh.Count == 0
            ? L.N(sel.Count, "לבטל את השינוי?", "לבטל את השינויים?", "Drop the change?", "Drop the changes?")
            : L.N(sel.Count, "לבטל את ההעלאה?", "לבטל את ההעלאות?", "Cancel the upload?", "Cancel the uploads?");
        if (fresh.Count > 0)
            text += "\n\n" + L.N(fresh.Count,
                "הקובץ הזה עוד לא שמור בטלגרם, ולכן הוא יימחק מהכונן לצמיתות (לא לסל המחזור):",
                "{0} הקבצים האלה עוד לא שמורים בטלגרם, ולכן הם יימחקו מהכונן לצמיתות (לא לסל המחזור):",
                "This file is not saved in Telegram yet, so it is deleted from the drive for good (not to the recycle bin):",
                "These {0} files are not saved in Telegram yet, so they are deleted from the drive for good (not to the recycle bin):")
                + "\n" + List(fresh, fresh.Count);
        if (changed.Count > 0)
            text += "\n\n" + L.N(changed.Count,
                "הקובץ הזה כבר שמור בטלגרם ושונה במחשב הזה: השינוי יבוטל, והוא יחזור לגרסה השמורה:",
                "{0} הקבצים האלה כבר שמורים בטלגרם ושונו במחשב הזה: השינויים יבוטלו, והם יחזרו לגרסה השמורה:",
                "This file is already saved in Telegram and was changed on this PC: the change is dropped, and it goes back to the saved version:",
                "These {0} files are already saved in Telegram and were changed on this PC: the changes are dropped, and they go back to the saved versions:")
                + "\n" + List(changed, changed.Count);
        if (fresh.Count > 0) text += "\n\n" + L.T("המקור שממנו העתקת, אם יש, לא נפגע.", "The original you copied from, if any, is not affected.");
        if (Ui.Ask(text, icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        // the others go on when one is refused; what was refused is said once, at the end
        var refused = new List<string>();
        _cancelling = true;
        _cancelSel.Enabled = false;
        try
        {
            await Task.Run(() => // a prepared file's local copies can be thousands of files: not on the window's thread
            {
                foreach (var (d, u) in sel)
                {
                    try { d.Session!.CancelUpload(u.Id); }
                    catch (Exception ex) { refused.Add(ex.Message); }
                }
            });
        }
        finally { _cancelling = false; }
        if (IsDisposed) return;
        RefreshNow();
        if (refused.Count > 0) Ui.Info(string.Join("\n\n", refused.Take(5)) + (refused.Count > 5 ? "\n\n…" : ""));
    }

    // ── actions: on this PC ──────────────────────────────────────────────────
    void PickAndPrepare()
    {
        var drives = _host.Drives.Where(d => d.Session is not null).ToList();
        if (drives.Count == 0) { Ui.Info(L.T("אין כונן מחובר.", "No drive is connected.")); return; }
        using var dlg = new OpenFileDialog
        {
            Title = L.T("בחר קבצים מהכונן להכנה במחשב הזה", "Choose files from the drive to prepare on this PC"),
            InitialDirectory = drives[0].Letter + "\\", Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var files = new List<(DriveSession, string)>();
        foreach (var path in dlg.FileNames)
        {
            var d = drives.FirstOrDefault(x => path.StartsWith(x.Letter, StringComparison.OrdinalIgnoreCase));
            if (d?.Session?.FileIdOf(path) is { } id) files.Add((d.Session, id));
        }
        if (files.Count == 0) { Ui.Info(L.T("אפשר להכין רק קבצים מכונני DriveGram.", "Only files on DriveGram drives can be prepared.")); return; }
        _host.PrepareFiles(files);
        _ = ScanAsync();
    }

    void PrepareSelected()
    {
        if (DemoSays()) return;
        _host.PrepareFiles(SelectedLocal().Select(r => (r.Session!, r.Id)));
        RefreshLocalProgress();
    }

    async void FreeSelected()
    {
        if (DemoSays()) return;
        var offline = SelectedOffline();
        var sel = SelectedLocal().ToList();
        if (offline.Count == 0 && sel.Count == 0) return;
        if (offline.Count == 0)
        {
            await _host.FreeFiles(sel.Select(r => (r.Session!, r.Id))); // asks itself (for prepared files) and tells the result
            if (!IsDisposed) await ScanAsync();
            return;
        }
        int all = offline.Count + sel.Count;
        var notConnected = sel.Count == 0 ? "" : L.N(offline.Count, " אחד מהם שייך לכונן לא מחובר.", " {0} מהם שייכים לכונן לא מחובר.",
            " One of them is of a drive not connected.", " {0} of them are of a drive not connected.");
        if (Ui.Ask(L.N(all,
                "לפנות מהמחשב הזה את הקובץ? הוא שמור בטלגרם, ויורד שוב כשתפתח אותו (צריך אינטרנט).",
                "לפנות מהמחשב הזה {0} קבצים? הם שמורים בטלגרם, ויורדו שוב כשתפתח אותם (צריך אינטרנט).",
                "Free the file from this PC? It is stored in Telegram, and downloads again when you open it (needs internet).",
                "Free {0} files from this PC? They are stored in Telegram, and download again when you open them (needs internet).") + notConnected) != DialogResult.Yes) return;
        long freed = await FreeOffline(offline);
        if (sel.Count > 0) freed += await _host.FreeFiles(sel.Select(r => (r.Session!, r.Id)), confirmed: true);
        if (freed > 0) _host.Notify(L.T("התפנה מקום ✓", "Space freed ✓"), L.T($"פונו {Ui.Size(freed)} במחשב הזה. הקבצים עצמם שמורים בטלגרם.", $"{Ui.Size(freed)} freed on this PC. The files themselves are stored in Telegram."));
        if (!IsDisposed) await ScanAsync();
    }

    /// <summary>Prepared files of a drive that is not connected: freed from its local folder (they stay in Telegram).</summary>
    /// <returns>The bytes freed (the question was asked by the caller).</returns>
    async Task<long> FreeOffline(List<LocalRow> rows)
    {
        long freed = 0;
        var failed = new List<string>();
        foreach (var g in rows.GroupBy(r => r.Drive))
        {
            var d = g.Key;
            if (d.Session is not null || d.Starting || d.FreeingOffline) { failed.Add(d.Name); continue; } // connected meanwhile
            d.FreeingOffline = true; // no connecting while its files are deleted
            try { freed += await Task.Run(() => OfflineCache.FreePrepared(d.State.Dir, g.Select(r => r.Id))); }
            catch (Exception ex) { Log.Warn($"free (disconnected) {d.Name}: {ex.Message}"); failed.Add(d.Name); }
            finally { d.FreeingOffline = false; }
        }
        if (failed.Count > 0)
            Ui.Info(L.T($"לא פונו הקבצים של: {string.Join(", ", failed)} – בינתיים הכונן התחבר או נפתח בחלון אחר. מהקבצים האלה לא נמחק כלום. נסה שוב בעוד רגע.",
                $"The files of {string.Join(", ", failed)} were not freed – meanwhile the drive connected or was opened in another window. Nothing of them was deleted. Try again in a moment."));
        return freed;
    }

    /// <summary>Find the files that are (partly) on this PC, in the background: it reads the whole index.</summary>
    bool _scanAgain;

    async Task ScanAsync()
    {
        if (_demo) return;
        if (_scanning) { _scanAgain = true; return; }
        _scanning = true;
        _localState.Text = L.T("בודק מה נמצא במחשב הזה…", "Checking what is on this PC…");
        var drives = _host.Drives.Where(d => d.Session is not null && !d.Stopping).ToList();
        var offline = _host.Drives.Where(d => d.Session is null && !d.Starting && !d.Stopping).ToList();
        int total = 0;
        var preparing = _host.Preparer.Running.Select(j => (j.Session, j.Id)).ToHashSet();
        List<LocalRow> rows = [];
        long kept = 0, cache = 0, previews = 0;
        try
        {
            rows = await Task.Run(() =>
            {
                var list = new List<LocalRow>();
                foreach (var d in drives)
                {
                    var s = d.Session!;
                    kept += s.Engine.PreparedBytes;
                    cache += s.Status.CacheBytes;
                    previews += s.Engine.PreviewBytes;
                    var stack = new Stack<string>();
                    stack.Push(DriveGram.Index.IndexDb.RootId);
                    int seen = 0;
                    while (stack.Count > 0 && seen < 200_000)
                    {
                        foreach (var n in s.Db.Children(stack.Pop()))
                        {
                            seen++;
                            if (n.Dir) { stack.Push(n.Id); continue; }
                            bool prepared = s.Engine.IsPrepared(n.Id);
                            double f = s.Engine.LocalFraction(n.Id);
                            if (f > 0.001 || prepared || preparing.Contains((s, n.Id)))
                                list.Add(new LocalRow(d, s, n.Id, n.Name, s.FullPathOf(n.Id), n.Size, f, prepared));
                        }
                    }
                }
                // a drive that is not connected: what was prepared on this PC, from its local index
                foreach (var d in offline)
                    try
                    {
                        foreach (var p in OfflineCache.Prepared(d.State))
                        {
                            list.Add(new LocalRow(d, null, p.Id, p.Name, p.Path, p.Size, p.Fraction, true, Offline: true));
                            kept += (long)(p.Size * p.Fraction);
                            cache += (long)(p.Size * p.Fraction);
                        }
                    }
                    catch (Exception ex) { Log.Debug($"transfers: {d.Name} (not connected): {ex.Message}"); }
                total = list.Count;
                return list.OrderByDescending(r => r.Prepared).ThenByDescending(r => r.Fraction).ThenBy(r => r.Name).ToList();
            });
        }
        catch (Exception ex) { Log.Warn($"transfers: scanning: {ex.Message}"); }
        _scanning = false;
        if (_scanAgain && !IsDisposed) { _scanAgain = false; _ = ScanAsync(); return; }
        if (IsDisposed) return;
        _localRows = rows;
        _localTotal = total;
        _scannedOnce = true;
        _localSizes = rows.Count == 0 && drives.Count == 0 ? ""
            : L.T($"קבצים שהכנת: {Ui.Size(kept)}  ·  עותקים זמניים: {Ui.Size(Math.Max(0, cache - kept))} (נמחקים לבד כשחסר מקום)  ·  תמונות ממוזערות: {Ui.Size(previews)}",
                  $"Files you prepared: {Ui.Size(kept)}  ·  temporary copies: {Ui.Size(Math.Max(0, cache - kept))} (removed by themselves when space runs short)  ·  thumbnails: {Ui.Size(previews)}");
        _anyConnected = drives.Count > 0;
        FillLocal();
    }

    string _localSizes = "";
    bool _anyConnected;

    /// <summary>The files list from the last scan, through the filter and the search (no new scan); what was
    /// chosen stays chosen, the sort stays.</summary>
    void FillLocal()
    {
        if (_demo) return;
        if (!_scannedOnce) // before the first scan: not "nothing here"
        {
            _localEmpty.Text = L.T("בודק מה נמצא במחשב הזה…", "Checking what is on this PC…");
            _localEmpty.Visible = true;
            _local.Visible = false;
            return;
        }
        var chosen = _local.SelectedItems.Cast<ListViewItem>().Select(i => i.Name).ToHashSet();
        var top = _local.TopItem?.Name;
        var search = _localSearch.Text;
        var matching = _localRows.Where(r => _localFilter.SelectedIndex switch { 1 => r.Prepared, 2 => !r.Prepared, _ => true } && Matches(r.Name, search)).ToList();
        var shown = matching.Take(2000).ToList(); // the list shows 2,000 at most; the search covers them all
        _local.BeginUpdate();
        _local.Items.Clear();
        _localRank.Clear();
        long rank = 0;
        var items = new ListViewItem[shown.Count];
        for (int i = 0; i < shown.Count; i++)
        {
            var r = shown[i];
            var key = r.Drive.State.Id + "/" + r.Id;
            _localRank[key] = rank++;
            items[i] = new ListViewItem(FileName(r.Name)) { Tag = r, Name = key, ToolTipText = r.Path };
            items[i].SubItems.AddRange([L.Ltr(r.Drive.Letter), Ui.Size(r.Size), "", ""]);
        }
        _local.ListViewItemSorter = null; // added together, sorted once (one by one, a sorted list re-sorts each time)
        _local.Items.AddRange(items);
        RefreshLocalProgress(); // the state column first: it is sorted by its text
        _local.ListViewItemSorter = _localSort; // (setting it sorts)
        foreach (var item in items) if (chosen.Contains(item.Name)) item.Selected = true;
        _local.EndUpdate();
        _localSort.Arrows();
        if (top is not null && _local.Items[top] is { } back) _local.TopItem = back; // where it was, not back to the top
        bool filtered = _localFilter.SelectedIndex != 0 || search.Trim().Length > 0;
        _localEmpty.Text = _localRows.Count == 0
            ? (_anyConnected ? L.T("אין כרגע קבצים מהכוננים במחשב הזה. קבצים שפותחים נשמרים כאן לזמן מה, וקובץ ש\"מכינים\" נשמר עד שמפנים אותו.",
                    "No files of the drives are on this PC right now. Files you open are kept here for a while, and a file you \"prepare\" stays until you free it.")
                : L.T("אין כונן מחובר, ולא הוכנו קבצים במחשב הזה.", "No drive is connected, and no files were prepared on this PC."))
            : L.T("אין קבצים שמתאימים לחיפוש או לסינון.", "No files match the search or the filter.");
        _localEmpty.Visible = shown.Count == 0;
        _local.Visible = shown.Count > 0;
        var count = filtered || matching.Count > shown.Count
            ? L.T($"מוצגים {shown.Count:N0} מתוך {_localTotal:N0} קבצים ({Ui.Size(matching.Sum(r => r.Size))})", $"Showing {shown.Count:N0} of {_localTotal:N0} files ({Ui.Size(matching.Sum(r => r.Size))})")
            : L.T($"{shown.Count:N0} קבצים ({Ui.Size(shown.Sum(r => r.Size))})", $"{shown.Count:N0} files ({Ui.Size(shown.Sum(r => r.Size))})");
        _localState.Text = _localRows.Count == 0 ? _localSizes : count + "  ·  " + _localSizes;
        UpdateButtons();
    }

    /// <summary>The state column of each local row (and the percent of files being prepared right now).</summary>
    void RefreshLocalProgress()
    {
        foreach (ListViewItem item in _local.Items)
        {
            if (item.Tag is not LocalRow r) continue;
            if (r.Offline)
            {
                item.SubItems[3].Text = Bar(r.Fraction);
                (item.ImageKey, item.SubItems[4].Text) = r.Fraction >= 0.999
                    ? ("offline", L.T("מוכן (הכונן לא מחובר)", "Prepared (drive not connected)"))
                    : ("offline", L.T("מוכן בחלקו (הכונן לא מחובר)", "Partly prepared (drive not connected)"));
                item.ToolTipText = r.Path + "\n" + (r.Fraction >= 0.999
                    ? L.T("הקובץ הוכן במחשב הזה. הכונן לא מחובר כרגע, ולכן אפשר לפתוח אותו רק אחרי שהכונן יתחבר. אפשר לפנות אותו גם עכשיו.",
                        "The file was prepared on this PC. The drive is disconnected right now, so it opens only once the drive connects. It can be freed now too.")
                    : L.T("רק חלק מהקובץ הוכן במחשב הזה. אחרי שהכונן יתחבר, בחר \"הכן\" שוב כדי להשלים.",
                        "Only part of the file was prepared on this PC. Once the drive connects, choose \"Prepare\" again to finish."));
                continue;
            }
            if (!_demo && _host.Drives.FirstOrDefault(x => x.State.Id == r.Drive.State.Id && x.Session is not null) is { } now
                && now.Session != r.Session)
                item.Tag = r = r with { Drive = now, Session = now.Session! }; // reconnected since the list was made
            if (r.Session is null) continue;
            var running = _host.Preparer.ProgressOf(r.Session, r.Id);
            if (running is { } p && p > r.Fraction) item.Tag = r = r with { Fraction = p };
            bool prepared = r.Session.Engine.IsPrepared(r.Id);
            item.SubItems[3].Text = Bar(r.Fraction);
            (item.ImageKey, item.SubItems[4].Text) = running is not null ? ("down", L.T("מוריד למחשב הזה…", "Downloading to this PC…"))
                : prepared && r.Fraction >= 0.999 ? ("ready", L.T("מוכן – יישאר עד שתפנה", "Ready – stays until you free it"))
                : prepared ? ("down", L.T("מוכן בחלקו (נסה שוב)", "Partly prepared (try again)"))
                : r.Fraction >= 0.999 ? ("full", L.T("כולו כאן (זמני)", "All here (temporary)"))
                : ("part", L.T("חלקו כאן (זמני)", "Partly here (temporary)"));
            item.ToolTipText = r.Path + "\n" + LocalTip(item.SubItems[4].Text, r.Fraction, prepared, running is not null);
        }
    }
}
