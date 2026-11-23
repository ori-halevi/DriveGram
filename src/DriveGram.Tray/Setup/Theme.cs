using DriveGram.Host;

namespace DriveGram.Tray.Setup;

/// <summary>Colors, fonts and small control factories shared by the setup wizard.</summary>
static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0x22, 0x9E, 0xD9);
    public static readonly Color AccentDark = Color.FromArgb(0x16, 0x6F, 0xC4);
    public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
    public static readonly Color Muted = Color.FromArgb(0x65, 0x6D, 0x76);
    public static readonly Color Border = Color.FromArgb(0xD8, 0xDE, 0xE4);
    public static readonly Color Surface = Color.FromArgb(0xF6, 0xF8, 0xFA);
    public static readonly Color Error = Color.FromArgb(0xC6, 0x28, 0x28);
    public static readonly Color Success = Color.FromArgb(0x1E, 0x8E, 0x3E);
    public static readonly Color WarnBack = Color.FromArgb(0xFF, 0xF4, 0xD6);
    public static readonly Color WarnBorder = Color.FromArgb(0xE8, 0xB3, 0x39);

    public static readonly Font Body = new("Segoe UI", 10.5f);
    public static readonly Font Small = new("Segoe UI", 9.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font Heading = new("Segoe UI Semibold", 12f);
    public static readonly Font Mono = new("Consolas", 12f);

    public static Label Label(string text, Font? font = null, Color? color = null, int maxWidth = 520) => new()
    {
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 0, 0, 8),
    };

    /// <summary>A check box whose text wraps at maxWidth: an AutoSize check box never wraps (MaximumSize
    /// only cuts its text off), so a long one widened the window or added a horizontal scroll bar.</summary>
    public static CheckBox WrapCheck(string text, Font font, int maxWidth, bool on = false)
    {
        var cb = new CheckBox { Text = text, Font = font, AutoSize = true, Checked = on };
        void Fit()
        {
            cb.AutoSize = true;
            var one = cb.GetPreferredSize(Size.Empty);
            if (one.Width <= maxWidth) return;
            var line = TextRenderer.MeasureText(cb.Text, cb.Font);
            var wrapped = TextRenderer.MeasureText(cb.Text, cb.Font, new Size(maxWidth - (one.Width - line.Width), 0), TextFormatFlags.WordBreak);
            cb.AutoSize = false;
            cb.CheckAlign = ContentAlignment.TopLeft; // mirrored to the right in Hebrew
            cb.TextAlign = ContentAlignment.TopLeft;
            cb.Size = new Size(maxWidth, one.Height - line.Height + wrapped.Height);
        }
        Fit();
        cb.FontChanged += (_, _) => Fit(); // also after a DPI change
        return cb;
    }

    /// <summary>Puts a window's content in place, with space around it. The space is the window's Padding
    /// around a docked column, not the column's own Padding: in a mirrored (Hebrew) window a column's
    /// Padding landed twice on the right and not at all on the left, in an AutoScroll column its vertical
    /// Padding was counted twice against the scroll range (the last button could not be scrolled into
    /// view), and an AutoSize window's Padding does not move a child that is not docked (measured).</summary>
    public static void Fill(Form f, Control content, Padding space)
    {
        f.Padding = space;
        content.Dock = DockStyle.Fill;
        f.Controls.Add(content);
    }

    public static Button Primary(string text) => Style(new Button { Text = text }, primary: true);
    public static Button Secondary(string text) => Style(new Button { Text = text }, primary: false);

    static Button Style(Button b, bool primary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.Font = Body;
        b.AutoSize = true;
        b.MinimumSize = new Size(110, 38);
        b.Padding = new Padding(14, 2, 14, 2);
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
        }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Surface;
        }
        b.EnabledChanged += (_, _) =>
        {
            if (primary) b.BackColor = b.Enabled ? Accent : Color.FromArgb(0xA9, 0xD3, 0xEC);
        };
        return b;
    }

    /// <summary>A text box for left-to-right input (phone, codes, passwords, API keys) inside the RTL window.</summary>
    public static TextBox Input(int width = 300, bool password = false) => new()
    {
        Font = Mono,
        Width = width,
        RightToLeft = RightToLeft.No,
        UseSystemPasswordChar = password,
        Margin = new Padding(0, 0, 0, 10),
    };

    public static Panel Card(Color back, Color border)
    {
        var p = new CardPanel { BackColor = back, Padding = new Padding(14, 10, 14, 10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 10) };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(border);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        // a Panel does not place its children by its Padding: without this the content sat on the
        // border (covering its top and left lines) and the space on the far side was missing
        p.ControlAdded += (_, e) => e.Control!.Location = new Point(p.Padding.Left, p.Padding.Top);
        return p;
    }

    /// <summary>Redrawn whole when its size changes: the border is drawn around the full panel, and a
    /// partial repaint after growing left a gap in it.</summary>
    sealed class CardPanel : Panel
    {
        public CardPanel() { ResizeRedraw = true; DoubleBuffered = true; }
    }

    public static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0),
    };

    /// <summary>Items in reading order: right-to-left in Hebrew, left-to-right in English. Every Hebrew
    /// window is mirrored (RightToLeftLayout), which already turns a left-to-right flow around; a
    /// right-to-left flow here would turn it back (measured: the first item ended up on the left).</summary>
    public static FlowLayoutPanel Row() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 0, 0, 8),
    };
}
