using DriveGram.Host;

namespace DriveGram.Tray.Setup;

/// <summary>
/// The tabs of a DriveGram window: one strip across the WHOLE width, each tab an equal share of it, and
/// the chosen one marked with the accent colour. Painted by us, for two reasons: Windows paints a
/// TabControl's tabs in its own grey (a dark look cannot change them), and it sizes each tab to its text.
///
/// Painting (rather than text in a control) also keeps Hebrew right: in a mirrored window GDI mirrors
/// where things land but never the letters themselves, so tab 1 appears on the right by itself.
/// </summary>
sealed class ThemedTabs : Panel
{
    sealed record Item(string Text, char Glyph, Control Page);

    readonly List<Item> _items = [];
    readonly Strip _strip;
    readonly Panel _host = new() { Dock = DockStyle.Fill };
    int _selected = -1;

    public event Action<int>? SelectedChanged;

    public ThemedTabs()
    {
        _strip = new Strip(this) { Dock = DockStyle.Top, Height = 46 };
        Controls.Add(_host);
        Controls.Add(_strip);
        this.Wears(Slot.Window);
        _host.Wears(Slot.Window);
    }

    public int Count => _items.Count;

    public void Add(string text, char glyph, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _items.Add(new Item(text, glyph, page));
        _host.Controls.Add(page);
        if (_selected < 0) Selected = 0;
        _strip.Invalidate();
    }

    public Control PageAt(int index) => _items[index].Page;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected) return;
            _selected = value;
            for (int i = 0; i < _items.Count; i++) _items[i].Page.Visible = i == value;
            _strip.Invalidate();
            SelectedChanged?.Invoke(value);
        }
    }

    /// <summary>Select the tab that holds this page (nothing happens when it holds none).</summary>
    public void SelectPage(Control page)
    {
        int at = _items.FindIndex(i => i.Page == page);
        if (at >= 0) Selected = at;
    }

    sealed class Strip : Panel
    {
        readonly ThemedTabs _owner;
        int _hot = -1;

        public Strip(ThemedTabs owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            this.Wears(Slot.Surface);
        }

        int TabAt(int x)
        {
            int n = _owner._items.Count;
            if (n == 0 || Width <= 0) return -1;
            int i = x * n / Math.Max(1, Width);
            return Math.Clamp(i, 0, n - 1);
        }

        /// <summary>Tab `i`'s place. The last one takes what rounding left over, so the strip is filled exactly.</summary>
        Rectangle Place(int i)
        {
            int n = Math.Max(1, _owner._items.Count);
            int from = Width * i / n, to = i == n - 1 ? Width : Width * (i + 1) / n;
            return new Rectangle(from, 0, to - from, Height);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int at = TabAt(e.X);
            if (at != _hot) { _hot = at; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != -1) { _hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                _owner.Selected = TabAt(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(System.Windows.Forms.Keys key) =>
            key is System.Windows.Forms.Keys.Left or System.Windows.Forms.Keys.Right or System.Windows.Forms.Keys.Home or System.Windows.Forms.Keys.End || base.IsInputKey(key);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_owner._items.Count == 0) { base.OnKeyDown(e); return; }
            // the arrows move between tabs; in Hebrew the strip is mirrored, so they are swapped
            int step = e.KeyCode == System.Windows.Forms.Keys.Right ? 1 : e.KeyCode == System.Windows.Forms.Keys.Left ? -1 : 0;
            if (L.Rtl) step = -step;
            if (step != 0) _owner.Selected = Math.Clamp(_owner.Selected + step, 0, _owner._items.Count - 1);
            else if (e.KeyCode == System.Windows.Forms.Keys.Home) _owner.Selected = 0;
            else if (e.KeyCode == System.Windows.Forms.Keys.End) _owner.Selected = _owner._items.Count - 1;
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        static readonly string IconFamily = new System.Drawing.Text.InstalledFontCollection().Families
            .Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, ClientRectangle);
            // sized from the text beside it, so the pair keeps its proportions when Windows scales text
            using var icons = new Font(IconFamily, Math.Max(12f, Theme.Body.Height * 0.78f), GraphicsUnit.Pixel);
            const int bar = 3;
            for (int i = 0; i < _owner._items.Count; i++)
            {
                var item = _owner._items[i];
                var r = Place(i);
                bool on = i == _owner._selected;
                if (on) using (var b = new SolidBrush(Theme.Window)) g.FillRectangle(b, r);
                else if (i == _hot) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, r);
                var color = on ? Theme.Accent : Theme.Muted;
                // icon and text measured together, so the pair sits in the middle of the tab
                var textSize = TextRenderer.MeasureText(g, item.Text, Theme.Body, new Size(r.Width, r.Height), TextFormatFlags.NoPrefix);
                var iconSize = TextRenderer.MeasureText(g, item.Glyph.ToString(), icons, new Size(40, r.Height), TextFormatFlags.NoPrefix);
                int gap = 8, total = iconSize.Width + gap + textSize.Width;
                int x = r.X + Math.Max(0, (r.Width - total) / 2);
                int top = (r.Height - bar - textSize.Height) / 2;
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, item.Glyph.ToString(), icons,
                    new Rectangle(x, top + (textSize.Height - iconSize.Height) / 2, iconSize.Width, iconSize.Height), color, flags);
                TextRenderer.DrawText(g, item.Text, Theme.Body,
                    new Rectangle(x + iconSize.Width + gap, top, textSize.Width, textSize.Height), color, flags);
                if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, new Rectangle(r.X, r.Bottom - bar, r.Width, bar));
                if (Focused && on)
                    using (var p = new Pen(Theme.Accent, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                        g.DrawRectangle(p, new Rectangle(r.X + 2, 2, r.Width - 5, r.Height - bar - 4));
            }
            using var line = new Pen(Theme.Border);
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }
}
