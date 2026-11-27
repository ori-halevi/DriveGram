using DriveGram.Host;

namespace DriveGram.Tray.Setup;

/// <summary>
/// A phone number as modern apps ask for it: the country beside the field, with its flag and its calling
/// code, and only the local number to type. The country starts as the one Windows is set to, and follows
/// what is typed — someone who pastes a whole "+44…" number sees the country change to the United Kingdom.
/// The number itself (what Telegram is given) is always the full international form.
/// </summary>
sealed class PhoneBox : UserControl
{
    readonly Button _country = new() { FlatStyle = FlatStyle.Flat, AutoSize = false, Height = 34, Width = 128, Cursor = Cursors.Hand };
    readonly TextBox _local = new() { BorderStyle = BorderStyle.FixedSingle, Font = Theme.Mono, RightToLeft = RightToLeft.No };
    Country _chosen = Countries.Here();
    bool _filling;

    public event Action? Changed;
    /// <summary>Enter in the number field (the page's own button takes it from here).</summary>
    public event Action? Entered;

    public PhoneBox(int width = 300)
    {
        Size = new Size(width, 36);
        Margin = new Padding(0, 0, 0, 10);
        this.Wears(Slot.Window);
        _country.Click += (_, _) => Choose();
        _country.FlatAppearance.BorderSize = 1;
        _country.TextImageRelation = TextImageRelation.ImageBeforeText;
        _country.ImageAlign = ContentAlignment.MiddleLeft;
        _country.TextAlign = ContentAlignment.MiddleLeft;
        _country.Padding = new Padding(6, 0, 0, 0);
        _country.Font = Theme.Body;
        // left to right whatever the window's language: a flag, then "+972", like a phone's own keypad
        _country.RightToLeft = RightToLeft.No;
        _local.PlaceholderText = L.T("50-123-4567", "50-123-4567");
        _local.TextChanged += (_, _) => { if (!_filling) { FollowTyping(); Changed?.Invoke(); } };
        _local.PreviewKeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Enter) e.IsInputKey = true; };
        _local.KeyDown += (_, e) =>
        {
            if (e.KeyCode != System.Windows.Forms.Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            Entered?.Invoke();
        };
        Controls.AddRange([_country, _local]);
        Layout += (_, _) => Place();
        Place();
        ShowCountry();
    }

    void Place()
    {
        // The country comes FIRST in reading order — on the right in Hebrew — because that is the order a
        // phone number is read and dictated. A control places its own children itself, and Windows does not
        // mirror those positions for us, so the sides are chosen here.
        int gap = 6, local = Math.Max(80, Width - _country.Width - gap - 2);
        _country.Height = _local.Height = Height - 2;
        _local.Width = local;
        if (L.Rtl)
        {
            _country.Location = new Point(Width - _country.Width - 1, 1);
            _local.Location = new Point(1, 1);
        }
        else
        {
            _country.Location = new Point(0, 1);
            _local.Location = new Point(_country.Width + gap, 1);
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _local.ReadOnly;
        set { _local.ReadOnly = value; _country.Enabled = !value; }
    }

    static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// The local part as the user meant it. People write their number in every shape there is, and all of
    /// these must end up the same: "50-123-4567", "050 123 4567" (the 0 is how it is dialled at home),
    /// "+972 50 123 4567", "00972…" (the old way of writing +), and "972501234567" (the whole number with
    /// no +, which used to come out as +972972501234567 and was sent to Telegram like that).
    /// </summary>
    string LocalDigits() => LocalDigits(_local.Text, _chosen);

    /// <summary>
    /// The same, for any text and country (and so checkable without a window).
    ///
    /// The hard case is a number that BEGINS with its own country's code and is not meant to: every
    /// Kazakh number starts with 7 and the code is +7, Italian mobiles start with 39 and the code is +39.
    /// The code is therefore taken off only when what was typed is too long to be a national number at
    /// all (someone wrote the whole international number without a +), never when it could be one.
    /// </summary>
    internal static string LocalDigits(string text, Country country)
    {
        var dial = country.Dial;
        var digits = Digits(text);
        // "+972…", or "00972…" (the old way of writing it): the user SAID the front is the country's code
        bool international = text.TrimStart().StartsWith('+');
        if (digits.StartsWith("00", StringComparison.Ordinal)) { digits = digits[2..]; international = true; }
        // with no "+" it is a guess, and it is made only when what was typed is longer than any national
        // number can be — otherwise a Kazakh 701… or an Italian 391… would lose its own first digits
        const int LongestNational = 11;
        if ((international || digits.Length > LongestNational)
            && digits.Length >= dial.Length + 4 && digits.StartsWith(dial, StringComparison.Ordinal))
            digits = digits[dial.Length..];
        // Russia and Kazakhstan dial a long-distance call with 8 where the rest of the world writes +7
        if (dial == "7" && digits.Length == 11 && digits.StartsWith('8')) digits = digits[1..];
        // the 0 of dialling at home, dropped once (not every zero). Italy is the country that keeps it:
        // its numbers are dialled from abroad WITH the zero, and dropping it would break them.
        else if (digits.StartsWith('0') && country.Iso != "IT") digits = digits[1..];
        int room = 15 - dial.Length; // E.164: 15 digits in all
        return digits.Length > room ? digits[..room] : digits;
    }

    /// <summary>How many digits the local part has (what makes a number too short, not the text's length).</summary>
    public int LocalLength => LocalDigits().Length;

    /// <summary>The whole number in international form, e.g. "+972501234567" (empty when nothing is typed).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Number
    {
        get
        {
            var digits = LocalDigits();
            return digits.Length == 0 ? "" : "+" + _chosen.Dial + digits;
        }
        set
        {
            var digits = Digits(value);
            // "00972…" is the old way of writing "+972…"; no calling code begins with a zero
            if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
            // a whole number is given here, so its front IS the country's code
            var country = Countries.OfNumber(digits);
            if (country is not null)
            {
                _chosen = country;
                digits = digits[country.Dial.Length..];
            }
            _filling = true;
            _local.Text = digits;
            _filling = false;
            ShowCountry();
            Changed?.Invoke(); // a number put here from outside is a change like any other
        }
    }

    public new void Focus() => _local.Focus();

    /// <summary>A whole international number typed or pasted into the local box: take its country.</summary>
    void FollowTyping()
    {
        var text = _local.Text.TrimStart();
        var digits = Digits(text);
        // "+972…" or the old "00972…"; the digits of a "00" number start one step in
        if (text.StartsWith("00", StringComparison.Ordinal) && digits.Length > 2) digits = digits[2..];
        else if (!text.StartsWith('+')) return;
        if (Countries.OfNumber(digits) is not { } country) return;
        if (!digits.StartsWith(country.Dial, StringComparison.Ordinal)) return;
        var local = digits[country.Dial.Length..];
        if (local.Length is 0 or > 14) return; // not a number: nothing but a code, or two numbers merged
        _chosen = country;
        _filling = true;
        _local.Text = local;
        _local.SelectionStart = _local.Text.Length;
        _filling = false;
        ShowCountry();
    }

    void ShowCountry()
    {
        _country.Image = Flags.Of(_chosen.Iso, 16);
        _country.Text = "  " + _chosen.Plus + "  ⌄";
        _tip.SetToolTip(_country, L.T($"{_chosen.Name} ({_chosen.Plus}) – לחץ כדי לבחור מדינה אחרת",
            $"{_chosen.Name} ({_chosen.Plus}) – click to choose another country"));
    }

    readonly ToolTip _tip = new();

    void Choose()
    {
        using var picker = new CountryPicker(_chosen);
        if (picker.ShowDialog(FindForm()) != DialogResult.OK || picker.Chosen is not { } country) return;
        _chosen = country;
        ShowCountry();
        Changed?.Invoke();
        _local.Focus();
    }

    /// <summary>The list of countries: type to narrow it, Enter or a double click to choose.</summary>
    internal sealed class CountryPicker : Form
    {
        readonly TextBox _search = new() { BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body, Dock = DockStyle.Top, Height = 30 };
        readonly ListBox _list = new()
        {
            Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26, BorderStyle = BorderStyle.None,
            IntegralHeight = false,
        };

        public Country? Chosen { get; private set; }

        public CountryPicker(Country current)
        {
            Ui.Prepare(this);
            Text = L.T("בחר מדינה", "Choose a country");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 430);
            _search.PlaceholderText = ""; // the line above the box already says what to type
            _list.DrawItem += DrawRow;
            // one click chooses: this list exists to pick one country, and waiting for a second click (or
            // for Enter after a click) is the kind of thing nobody guesses
            _list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left && _list.IndexFromPoint(e.Location) >= 0) Take(); };
            _list.KeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Enter) { e.Handled = true; Take(); } };
            _search.TextChanged += (_, _) => Fill(_search.Text);
            _search.PreviewKeyDown += (_, e) => { if (e.KeyCode is System.Windows.Forms.Keys.Down or System.Windows.Forms.Keys.Enter) e.IsInputKey = true; };
            _search.KeyDown += (_, e) =>
            {
                if (e.KeyCode == System.Windows.Forms.Keys.Down) { e.Handled = true; _list.Focus(); }
                else if (e.KeyCode == System.Windows.Forms.Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Take(); }
            };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10) };
            var what = Theme.Label(L.T("חפש מדינה או קידומת:", "Search a country or a code:"), Theme.Small, Theme.Muted, 330);
            what.Dock = DockStyle.Top;
            body.Controls.Add(_list);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
            body.Controls.Add(_search);
            body.Controls.Add(what);
            Controls.Add(body);
            Fill("");
            _list.SelectedItem = Countries.All.FirstOrDefault(c => c.Iso == current.Iso);
            if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
            Shown += (_, _) => _search.Focus();
            Theme.Apply(this);
        }

        void Fill(string search)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in Countries.Search(search)) _list.Items.Add(c);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.EndUpdate();
        }

        void Take()
        {
            if (_list.SelectedItem is not Country c) return;
            Chosen = c;
            DialogResult = DialogResult.OK;
            Close();
        }

        void DrawRow(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || _list.Items[e.Index] is not Country c) return;
            bool on = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(on ? Theme.AccentSoft : Theme.InputBack)) e.Graphics.FillRectangle(back, e.Bounds);
            // the flag and the name start where reading starts, the code ends the row: a row is painted
            // by us, and Windows does not turn it round for a Hebrew window
            var flag = Flags.Of(c.Iso, 14);
            var text = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            var code = L.Ltr(c.Plus);
            int codeWidth = TextRenderer.MeasureText(code, Theme.Body).Width + 6;
            int flagWidth = flag is null ? 0 : flag.Width + 10;
            int top = e.Bounds.Y + (e.Bounds.Height - 14) / 2;
            Rectangle name;
            if (L.Rtl)
            {
                text |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
                if (flag is not null) e.Graphics.DrawImage(flag, new Rectangle(e.Bounds.Right - 8 - flag.Width, top, flag.Width, 14));
                name = new Rectangle(e.Bounds.X + codeWidth + 8, e.Bounds.Y, e.Bounds.Width - flagWidth - codeWidth - 16, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, code, Theme.Body,
                    new Rectangle(e.Bounds.X + 6, e.Bounds.Y, codeWidth, e.Bounds.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
            }
            else
            {
                if (flag is not null) e.Graphics.DrawImage(flag, new Rectangle(e.Bounds.X + 8, top, flag.Width, 14));
                name = new Rectangle(e.Bounds.X + 8 + flagWidth, e.Bounds.Y, e.Bounds.Width - flagWidth - codeWidth - 16, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, code, Theme.Body,
                    new Rectangle(e.Bounds.Right - codeWidth - 6, e.Bounds.Y, codeWidth, e.Bounds.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
            }
            TextRenderer.DrawText(e.Graphics, c.Name, Theme.Body, name, Theme.Text, text);
        }
    }
}
