using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>What the settings page needs from the tray.</summary>
interface ISettingsHost
{
    /// <summary>"auto", "he" or "en" (every window is drawn again in it).</summary>
    void SetLanguage(string preference);
    void SetTheme(ThemeKind kind);
    void SetLimits(double alertGb, double capGb);
}

/// <summary>
/// The settings of the program itself (not of one drive): the language, the look, and the daily upload
/// cap. The only place each of these is changed — a setting that lives in two places is a setting the
/// user cannot trust. What belongs to ONE drive (its letter, its name, its type, how much space it may
/// use here) stays in that drive's own windows.
/// </summary>
sealed class SettingsPage : UserControl
{
    readonly ISettingsHost _host;
    readonly DailyUploads _daily;
    readonly List<LookCard> _looks = [];
    readonly List<(RadioButton Button, string Key)> _languages = [];
    readonly CheckBox _alertOn, _capOn;
    readonly NumericUpDown _alert = Number(), _cap = Number();
    readonly Label _today = Theme.Label("", Theme.Body, Theme.Text, 620);
    readonly System.Windows.Forms.Timer _save = new() { Interval = 700 };
    bool _loading = true;

    const int TextWidth = 620;

    public SettingsPage(ISettingsHost host, DailyUploads daily)
    {
        _host = host;
        _daily = daily;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(22, 18, 22, 18);
        this.Wears(Slot.Window, Slot.Text);

        var col = Theme.Column();
        col.Dock = DockStyle.Fill;
        col.AutoSize = false;
        col.AutoScroll = true;

        // ── language ──
        col.Controls.Add(Head(L.T("שפה", "Language")));
        var langs = Theme.Row();
        foreach (var (key, label) in new[] { ("auto", L.T("לפי וינדוס", "Same as Windows")), ("he", "עברית"), ("en", "English") })
        {
            var rb = new RadioButton { Text = label, AutoSize = true, Font = Theme.Body, Margin = new Padding(0, 0, 22, 0), Checked = L.Preference == key };
            rb.CheckedChanged += (_, _) => { if (rb.Checked && !_loading && L.Preference != key) _host.SetLanguage(key); };
            _languages.Add((rb, key));
            langs.Controls.Add(rb);
        }
        col.Controls.Add(langs);
        col.Controls.Add(Note(L.T("\"לפי וינדוס\": עברית כשווינדוס בעברית, אחרת אנגלית.",
            "\"Same as Windows\": Hebrew when Windows is in Hebrew, English otherwise.")));

        // ── the look ──
        col.Controls.Add(Head(L.T("מראה", "Look")));
        var looks = Theme.Row();
        looks.WrapContents = true;
        looks.MaximumSize = new Size(TextWidth + 40, 0);
        foreach (var (kind, name) in new[]
                 {
                     (ThemeKind.Auto, L.T("לפי וינדוס", "Same as Windows")),
                     (ThemeKind.Light, L.T("בהיר", "Light")),
                     (ThemeKind.Dark, L.T("כהה", "Dark")),
                     (ThemeKind.Rose, L.T("ורד", "Rose")),
                 })
        {
            var card = new LookCard(kind, name);
            card.Click += (_, _) => { if (!_loading) _host.SetTheme(kind); };
            _looks.Add(card);
            looks.Controls.Add(card);
        }
        col.Controls.Add(looks);
        col.Controls.Add(Note(L.T("פסי גלילה וכמה פרטים קטנים שווינדוס מצייר בעצמו יתאימו למראה החדש בהפעלה הבאה של DriveGram.",
            "Scroll bars and a few small parts that Windows draws itself match the new look the next time DriveGram starts.")));

        // ── the daily cap ──
        col.Controls.Add(Head(L.T("תקרת העלאה (24 שעות)", "Upload cap (24 hours)")));
        col.Controls.Add(Note(L.T(
            "טלגרם לא מפרסם מגבלה יומית. מה שידוע: חשבון בלי Premium מעלה לאט יותר, וטלגרם חוסם דפוסים שנראים כמו ספאם או שימוש לרעה. אם אתה מעדיף להיזהר, אפשר לקבל התראה או לעצור את ההעלאות בכמות שתבחר. הספירה היא על 24 השעות האחרונות (לא עד חצות), וכוללת את כל הכוננים במחשב הזה.",
            "Telegram publishes no daily limit. What is known: an account without Premium uploads more slowly, and Telegram blocks patterns that look like spam or abuse. If you prefer to be careful, you can get a notice, or pause uploads, at an amount you choose. The count covers the last 24 hours (not up to midnight), and every drive on this PC.")));
        col.Controls.Add(_today);

        _alertOn = Theme.WrapCheck(L.T("הודע לי כשעלו לטלגרם, ב־24 השעות האחרונות, יותר מ:", "Tell me when more than this went up to Telegram in the last 24 hours:"), Theme.Body, TextWidth);
        _alertOn.Margin = new Padding(0, 12, 0, 2);
        col.Controls.Add(_alertOn);
        col.Controls.Add(WithGb(_alert));
        col.Controls.Add(Note(L.T("ההתראה מופיעה גם מראש, כשמה שממתין להעלאה עומד לחרוג מהכמות הזו, ותופיע שוב רק אחרי שהספירה תרד מתחת לכמות.",
            "The notice also comes ahead of time, when what waits to upload is about to pass that amount, and comes again only once the count has fallen back under it.")));

        _capOn = Theme.WrapCheck(L.T("עצור את ההעלאות כשעלו, ב־24 השעות האחרונות, יותר מ:", "Pause uploads once more than this went up in the last 24 hours:"), Theme.Body, TextWidth);
        _capOn.Margin = new Padding(0, 12, 0, 2);
        col.Controls.Add(_capOn);
        col.Controls.Add(WithGb(_cap));
        col.Controls.Add(Note(L.T(
            "הספירה מתגלגלת: מה שעלה לפני 24 שעות יוצא ממנה מעצמו, וההעלאות ממשיכות לבד. בינתיים הקבצים מחכים בבטחה במחשב הזה, ואפשר גם להמשיך בכל זאת מלשונית \"העברות\". העתקה גדולה לכונן בזמן העצירה עלולה להיעצר כשנגמר המקום במחשב.",
            "The count rolls: what went up 24 hours ago leaves it by itself, and the uploads go on. Meanwhile the files wait safely on this PC, and you can also go on anyway from the \"Transfers\" tab. A big copy into the drive while paused may stop when this PC runs out of space.")));

        Controls.Add(col);

        _alertOn.CheckedChanged += (_, _) => { _alert.Enabled = _alertOn.Checked; Queue(); };
        _capOn.CheckedChanged += (_, _) => { _cap.Enabled = _capOn.Checked; Queue(); };
        _alert.ValueChanged += (_, _) => Queue();
        _cap.ValueChanged += (_, _) => Queue();
        _alert.TextChanged += (_, _) => Queue();
        _cap.TextChanged += (_, _) => Queue();
        _save.Tick += (_, _) => { _save.Stop(); Store(); };
        Disposed += (_, _) => { Commit(); _save.Dispose(); };
        Reload();
    }

    static Label Head(string text)
    {
        var l = Theme.Label(text, Theme.Heading, Theme.Text, TextWidth);
        l.Margin = new Padding(0, 18, 0, 6);
        return l;
    }

    static Label Note(string text) => Theme.Label(text, Theme.Small, Theme.Muted, TextWidth);

    static NumericUpDown Number() => new()
    {
        Minimum = 1, Maximum = 10000, DecimalPlaces = 0, Width = 90, Font = Theme.Body, Value = 50,
        RightToLeft = RightToLeft.No, Margin = new Padding(26, 2, 26, 6),
    };

    static FlowLayoutPanel WithGb(NumericUpDown n)
    {
        var r = Theme.Row();
        r.Controls.Add(n);
        r.Controls.Add(Theme.Label("GB", Theme.Body, Theme.Text, 60).Wears(fore: Slot.Text));
        return r;
    }

    void Queue()
    {
        if (_loading) return;
        _save.Stop();
        _save.Start();
    }

    /// <summary>Save a change that is still waiting its moment (the page is going away, or about to be
    /// filled again): without this, ticking the cap and closing the window at once loses the tick.</summary>
    public void Commit()
    {
        if (!_save.Enabled) return;
        _save.Stop();
        Store();
    }

    void Store() => _host.SetLimits(_alertOn.Checked ? Typed(_alert) : 0, _capOn.Checked ? Typed(_cap) : 0);

    /// <summary>What the box shows, including a number typed and not yet left: NumericUpDown keeps that as
    /// text until the control is validated, so "200" typed and then closed used to save the old value.</summary>
    static double Typed(NumericUpDown box) =>
        double.TryParse(box.Text, out var v) && v >= (double)box.Minimum && v <= (double)box.Maximum ? v : (double)box.Value;

    /// <summary>Show what is saved now (opening the page, and after the look or the language changed).</summary>
    public void Reload()
    {
        Commit(); // what the user just changed is saved before the page is filled from what is saved
        _loading = true;
        try
        {
            foreach (var (rb, key) in _languages) rb.Checked = L.Preference == key;
            foreach (var card in _looks) card.On = card.Kind == Theme.Kind;
            double alert = _daily.AlertGb, cap = _daily.CapGb;
            _alertOn.Checked = alert > 0;
            if (alert > 0) _alert.Value = (decimal)Math.Clamp(alert, 1, 10000);
            _capOn.Checked = cap > 0;
            if (cap > 0) _cap.Value = (decimal)Math.Clamp(cap, 1, 10000);
            _alert.Enabled = _alertOn.Checked;
            _cap.Enabled = _capOn.Checked;
        }
        finally { _loading = false; }
        Tick();
    }

    /// <summary>Every second from the window: only what changes by itself (today's uploads).</summary>
    public void Tick() => _today.Text = L.T($"ב־24 השעות האחרונות עלו: {Ui.Size(_daily.RecentBytes)}",
        $"Uploaded in the last 24 hours: {Ui.Size(_daily.RecentBytes)}");

    /// <summary>
    /// One look to choose from: its name, a small picture of a window in it, and a mark when it is the
    /// chosen one. Painted, so each card shows ITS colours while the window around it wears another look.
    /// </summary>
    sealed class LookCard : Panel
    {
        public ThemeKind Kind { get; }
        readonly string _name;
        bool _on, _hot;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool On { get => _on; set { if (_on == value) return; _on = value; Invalidate(); } }

        public LookCard(ThemeKind kind, string name)
        {
            Kind = kind;
            _name = name;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // wide enough for the longest name under it ("Same as Windows"), in any language
            Size = new Size(Math.Max(132, TextRenderer.MeasureText(name, Theme.Body).Width + 22), 118);
            Margin = new Padding(0, 4, 14, 10);
            Cursor = Cursors.Hand;
            MouseEnter += (_, _) => { _hot = true; Invalidate(); };
            MouseLeave += (_, _) => { _hot = false; Invalidate(); };
        }

        /// <summary>The colours this card shows: "same as Windows" shows what it would be right now.</summary>
        Palette Shown => Kind switch
        {
            ThemeKind.Light => Theme.LightPalette,
            ThemeKind.Dark => Theme.DarkPalette,
            ThemeKind.Rose => Theme.RosePalette,
            _ => Theme.WindowsPrefersDark ? Theme.DarkPalette : Theme.LightPalette,
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Parent?.BackColor ?? Theme.Window)) g.FillRectangle(back, ClientRectangle);
            var p = Shown;

            // the little window: a tab strip with an accent mark, and a card with two lines of text
            var box = new Rectangle(2, 2, Width - 5, 74);
            using (var b = new SolidBrush(p.Window)) g.FillRectangle(b, box);
            using (var b = new SolidBrush(p.Surface)) g.FillRectangle(b, new Rectangle(box.X, box.Y, box.Width, 16));
            using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, new Rectangle(box.X + 6, box.Y + 13, 30, 3));
            var card = new Rectangle(box.X + 8, box.Y + 26, box.Width - 16, 36);
            using (var b = new SolidBrush(p.Card)) g.FillRectangle(b, card);
            using (var pen = new Pen(p.Border)) g.DrawRectangle(pen, card);
            using (var b = new SolidBrush(p.Text)) g.FillRectangle(b, new Rectangle(card.X + 6, card.Y + 7, 48, 5));
            using (var b = new SolidBrush(p.Muted)) g.FillRectangle(b, new Rectangle(card.X + 6, card.Y + 17, 72, 4));
            using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, new Rectangle(card.Right - 26, card.Bottom - 14, 20, 8));
            using (var pen = new Pen(_on ? Theme.Accent : _hot ? Theme.Muted : Theme.Border, _on ? 2f : 1f))
                g.DrawRectangle(pen, new Rectangle(box.X, box.Y, box.Width, box.Height));

            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Top;
            if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(g, (_on ? "● " : "") + _name, Theme.Body,
                new Rectangle(0, box.Bottom + 7, Width, Height - box.Bottom - 8), _on ? Theme.Accent : Theme.Text, flags);
        }
    }
}
