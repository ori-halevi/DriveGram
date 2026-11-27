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
        // the country on the reading-start side (a mirrored window puts it on the right by itself)
        _country.Location = new Point(0, 1);
        _country.Height = Height - 2;
        _local.Location = new Point(_country.Right + 6, 1);
        _local.Width = Math.Max(80, Width - _country.Width - 8);
        _local.Height = Height - 2;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _local.ReadOnly;
        set { _local.ReadOnly = value; _country.Enabled = !value; }
    }

    /// <summary>The whole number in international form, e.g. "+972501234567" (empty when nothing is typed).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Number
    {
        get
        {
            var digits = new string(_local.Text.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
            return digits.Length == 0 ? "" : "+" + _chosen.Dial + digits;
        }
        set
        {
            var country = Countries.OfNumber(value);
            var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
            if (country is not null)
            {
                _chosen = country;
                digits = digits[country.Dial.Length..];
            }
            _filling = true;
            _local.Text = digits;
            _filling = false;
            ShowCountry();
        }
    }

    public new void Focus() => _local.Focus();

    /// <summary>A whole international number typed or pasted into the local box: take its country.</summary>
    void FollowTyping()
    {
        var text = _local.Text.TrimStart();
        if (!text.StartsWith('+') && !text.StartsWith("00")) return;
        if (Countries.OfNumber(text) is not { } country) return;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (text.StartsWith("00")) digits = digits[2..];
        if (!digits.StartsWith(country.Dial, StringComparison.Ordinal)) return;
        _chosen = country;
        _filling = true;
        _local.Text = digits[country.Dial.Length..];
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
            _search.PlaceholderText = L.T("חיפוש מדינה או קידומת…", "Search a country or a code…");
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
            body.Controls.Add(_list);
            body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
            body.Controls.Add(_search);
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
            var flag = Flags.Of(c.Iso, 14);
            int x = e.Bounds.X + 8;
            if (flag is not null)
            {
                e.Graphics.DrawImage(flag, new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - 14) / 2, flag.Width, 14));
                x += flag.Width + 10;
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            var code = L.Ltr(c.Plus);
            var codeWidth = TextRenderer.MeasureText(code, Theme.Body).Width + 6;
            TextRenderer.DrawText(e.Graphics, c.Name, Theme.Body,
                new Rectangle(x, e.Bounds.Y, e.Bounds.Width - x - codeWidth - 8, e.Bounds.Height), Theme.Text, flags);
            TextRenderer.DrawText(e.Graphics, code, Theme.Body,
                new Rectangle(e.Bounds.Right - codeWidth - 6, e.Bounds.Y, codeWidth, e.Bounds.Height), Theme.Muted,
                flags | TextFormatFlags.Right);
        }
    }
}
