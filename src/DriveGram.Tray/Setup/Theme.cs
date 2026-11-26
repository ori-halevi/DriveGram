using System.Runtime.CompilerServices;
using DriveGram.Host;

namespace DriveGram.Tray.Setup;

/// <summary>Which look the program wears. "Auto" follows Windows' own light/dark choice.</summary>
enum ThemeKind { Auto, Light, Dark, Rose }

/// <summary>
/// One look's colors. Every colour the program paints comes from here, by NAME (a slot), never as a
/// literal: that is what lets the look change while the program runs.
/// </summary>
sealed record Palette
{
    public required bool Dark { get; init; }
    /// <summary>A window's or page's background.</summary>
    public required Color Window { get; init; }
    /// <summary>A quieter strip inside a window (headers, footers, the help under a list).</summary>
    public required Color Surface { get; init; }
    /// <summary>A card standing on the window.</summary>
    public required Color Card { get; init; }
    /// <summary>Where the user types, and the lists (a little sunken).</summary>
    public required Color Input { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required Color Accent { get; init; }
    /// <summary>The accent under the pointer / for a pressed state.</summary>
    public required Color AccentDark { get; init; }
    /// <summary>A pale wash of the accent (a chosen item, a disabled primary button).</summary>
    public required Color AccentSoft { get; init; }
    /// <summary>Text ON the accent (white on a light look, near-black on a dark one).</summary>
    public required Color OnAccent { get; init; }
    public required Color Error { get; init; }
    public required Color Success { get; init; }
    public required Color WarnBack { get; init; }
    public required Color WarnBorder { get; init; }
    /// <summary>A quiet "this is fine" card (the signed-in account, "everything is saved").</summary>
    public required Color InfoBack { get; init; }
    public required Color InfoBorder { get; init; }
    /// <summary>"Worth knowing" — not an error (an upload paused, no recovery key yet, a new version).</summary>
    public required Color Attention { get; init; }
    /// <summary>A card about something that cannot be undone (deleting a drive, a drive that vanished).</summary>
    public required Color DangerBack { get; init; }
    public required Color DangerBorder { get; init; }
    /// <summary>A row or item under the pointer.</summary>
    public required Color Hover { get; init; }

    public Color Of(Slot s) => s switch
    {
        Slot.Window => Window,
        Slot.Surface => Surface,
        Slot.Card => Card,
        Slot.Input => Input,
        Slot.Border => Border,
        Slot.Text => Text,
        Slot.Muted => Muted,
        Slot.Accent => Accent,
        Slot.AccentDark => AccentDark,
        Slot.AccentSoft => AccentSoft,
        Slot.OnAccent => OnAccent,
        Slot.Error => Error,
        Slot.Success => Success,
        Slot.WarnBack => WarnBack,
        Slot.WarnBorder => WarnBorder,
        Slot.InfoBack => InfoBack,
        Slot.InfoBorder => InfoBorder,
        Slot.Attention => Attention,
        Slot.DangerBack => DangerBack,
        Slot.DangerBorder => DangerBorder,
        Slot.Hover => Hover,
        _ => Color.Empty,
    };
}

/// <summary>A colour by its job, not by its value. <see cref="Slot.None"/> = leave this colour alone.</summary>
enum Slot
{
    None, Window, Surface, Card, Input, Border, Text, Muted,
    Accent, AccentDark, AccentSoft, OnAccent, Error, Success, Attention,
    WarnBack, WarnBorder, InfoBack, InfoBorder, DangerBack, DangerBorder, Hover,
}

/// <summary>Colors, fonts and small control factories shared by every DriveGram window.</summary>
static class Theme
{
    // ── the three looks ──────────────────────────────────────────────────────
    public static readonly Palette LightPalette = new()
    {
        Dark = false,
        Window = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Surface = Color.FromArgb(0xF6, 0xF8, 0xFA),
        Card = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Input = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Border = Color.FromArgb(0xD8, 0xDE, 0xE4),
        Text = Color.FromArgb(0x1F, 0x23, 0x28),
        Muted = Color.FromArgb(0x65, 0x6D, 0x76),
        Accent = Color.FromArgb(0x22, 0x9E, 0xD9),
        AccentDark = Color.FromArgb(0x16, 0x6F, 0xC4),
        AccentSoft = Color.FromArgb(0xE3, 0xF2, 0xFB),
        OnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Error = Color.FromArgb(0xC6, 0x28, 0x28),
        Success = Color.FromArgb(0x1E, 0x8E, 0x3E),
        WarnBack = Color.FromArgb(0xFF, 0xF4, 0xD6),
        WarnBorder = Color.FromArgb(0xE8, 0xB3, 0x39),
        InfoBack = Color.FromArgb(0xEC, 0xF7, 0xEE),
        InfoBorder = Color.FromArgb(0xB7, 0xDF, 0xC0),
        Attention = Color.FromArgb(0xB4, 0x6A, 0x00),
        DangerBack = Color.FromArgb(0xFD, 0xEC, 0xEA),
        DangerBorder = Color.FromArgb(0xE5, 0x9A, 0x92),
        Hover = Color.FromArgb(0xF0, 0xF4, 0xF8),
    };

    public static readonly Palette DarkPalette = new()
    {
        Dark = true,
        Window = Color.FromArgb(0x17, 0x1A, 0x1F),
        Surface = Color.FromArgb(0x1E, 0x22, 0x27),
        Card = Color.FromArgb(0x1C, 0x20, 0x25),
        Input = Color.FromArgb(0x0F, 0x12, 0x16),
        Border = Color.FromArgb(0x2E, 0x34, 0x3B),
        Text = Color.FromArgb(0xE6, 0xEA, 0xF0),
        Muted = Color.FromArgb(0x9A, 0xA4, 0xB0),
        Accent = Color.FromArgb(0x3B, 0xAE, 0xE6),
        AccentDark = Color.FromArgb(0x6A, 0xC5, 0xF2),
        AccentSoft = Color.FromArgb(0x16, 0x2F, 0x3E),
        OnAccent = Color.FromArgb(0x0B, 0x0E, 0x12),
        Error = Color.FromArgb(0xF2, 0x66, 0x6C),
        Success = Color.FromArgb(0x4C, 0xC4, 0x70),
        WarnBack = Color.FromArgb(0x3A, 0x2E, 0x12),
        WarnBorder = Color.FromArgb(0x8A, 0x6A, 0x1E),
        InfoBack = Color.FromArgb(0x15, 0x29, 0x1C),
        InfoBorder = Color.FromArgb(0x2E, 0x5A, 0x3C),
        Attention = Color.FromArgb(0xE5, 0xA3, 0x3A),
        DangerBack = Color.FromArgb(0x3A, 0x1C, 0x1C),
        DangerBorder = Color.FromArgb(0x74, 0x31, 0x31),
        Hover = Color.FromArgb(0x25, 0x2B, 0x32),
    };

    /// <summary>Light, with a rose accent and a hint of rose in the quiet areas.</summary>
    public static readonly Palette RosePalette = new()
    {
        Dark = false,
        Window = Color.FromArgb(0xFF, 0xFB, 0xFC),
        Surface = Color.FromArgb(0xFD, 0xF1, 0xF5),
        Card = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Input = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Border = Color.FromArgb(0xEF, 0xD6, 0xDE),
        Text = Color.FromArgb(0x27, 0x19, 0x1E),
        Muted = Color.FromArgb(0x7A, 0x66, 0x70),
        Accent = Color.FromArgb(0xC9, 0x35, 0x6F),
        AccentDark = Color.FromArgb(0xA8, 0x27, 0x59),
        AccentSoft = Color.FromArgb(0xFC, 0xE7, 0xEF),
        OnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF),
        Error = Color.FromArgb(0xC6, 0x28, 0x28),
        Success = Color.FromArgb(0x1E, 0x8E, 0x3E),
        WarnBack = Color.FromArgb(0xFF, 0xF4, 0xD6),
        WarnBorder = Color.FromArgb(0xE8, 0xB3, 0x39),
        InfoBack = Color.FromArgb(0xF3, 0xF9, 0xF4),
        InfoBorder = Color.FromArgb(0xBF, 0xE0, 0xC8),
        Attention = Color.FromArgb(0xB4, 0x63, 0x0A),
        DangerBack = Color.FromArgb(0xFD, 0xEC, 0xEA),
        DangerBorder = Color.FromArgb(0xE5, 0x9A, 0x92),
        Hover = Color.FromArgb(0xFB, 0xE9, 0xEF),
    };

    public static Palette Current { get; private set; } = LightPalette;

    /// <summary>What the user chose (Auto follows Windows).</summary>
    public static ThemeKind Kind { get; private set; } = ThemeKind.Auto;

    /// <summary>The look changed: windows repaint, and the main window is built again.</summary>
    public static event Action? Changed;

    static bool _loaded;

    /// <summary>Read the saved look. Called once, before any window is created.</summary>
    public static void Load()
    {
        Kind = Parse(AppSettings.Load().Theme);
        Current = Resolve(Kind);
        _loaded = true;
        ApplyColorMode();
    }

    public static void Set(ThemeKind kind)
    {
        if (!_loaded) Load();
        if (kind == Kind) return;
        Kind = kind;
        AppSettings.Update(s => s.Theme = Name(kind)); // the language (and anything else there) is kept
        var open = Application.OpenForms.Cast<Form>().ToList();
        // First, while the OLD look is still in force, let every control on screen record which colour it
        // wears (Apply guesses that from the colour it has now). Only then change the look and dress them
        // again — the other way round the guess would be made against colours nothing on screen has yet.
        foreach (var f in open) try { Apply(f); } catch (Exception ex) { Log.Debug($"theme: {ex.Message}"); }
        Current = Resolve(kind);
        ApplyColorMode();
        foreach (var f in open) try { Apply(f); } catch (Exception ex) { Log.Debug($"theme: {ex.Message}"); }
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn($"theme: listener failed: {ex.Message}"); }
        Log.Info($"look changed to {Name(kind)}");
    }

    /// <summary>Use a look for this run only, without saving it (the screenshot aid).</summary>
    public static void Override(string name)
    {
        Kind = Parse(name);
        Current = Resolve(Kind);
        _loaded = true;
        ApplyColorMode();
    }

    public static string Name(ThemeKind k) => k switch
    {
        ThemeKind.Light => "light", ThemeKind.Dark => "dark", ThemeKind.Rose => "rose", _ => "auto",
    };

    static ThemeKind Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "light" => ThemeKind.Light, "dark" => ThemeKind.Dark, "rose" => ThemeKind.Rose, _ => ThemeKind.Auto,
    };

    static Palette Resolve(ThemeKind k) => k switch
    {
        ThemeKind.Light => LightPalette,
        ThemeKind.Dark => DarkPalette,
        ThemeKind.Rose => RosePalette,
        _ => WindowsPrefersDark ? DarkPalette : LightPalette,
    };

    /// <summary>Is Windows itself set to a dark look for apps?</summary>
    public static bool WindowsPrefersDark
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Windows draws some parts itself (scroll bars, a drop-down list, the window's title bar). Telling
    /// WinForms which mode we are in makes those match. Windows created from now on follow it; ones
    /// already open keep their scroll bars until they are opened again.
    /// </summary>
    static void ApplyColorMode()
    {
        try { Application.SetColorMode(Current.Dark ? SystemColorMode.Dark : SystemColorMode.Classic); }
        catch (Exception ex) { Log.Debug($"theme: color mode: {ex.Message}"); }
    }

    // ── the colours, by name ─────────────────────────────────────────────────
    public static Color Accent => Current.Accent;
    public static Color AccentDark => Current.AccentDark;
    public static Color AccentSoft => Current.AccentSoft;
    public static Color OnAccent => Current.OnAccent;
    public static Color Text => Current.Text;
    public static Color Muted => Current.Muted;
    public static Color Border => Current.Border;
    public static Color Surface => Current.Surface;
    public static Color Window => Current.Window;
    public static Color CardBack => Current.Card;
    public static Color InputBack => Current.Input;
    public static Color Error => Current.Error;
    public static Color Success => Current.Success;
    public static Color WarnBack => Current.WarnBack;
    public static Color WarnBorder => Current.WarnBorder;
    public static Color InfoBack => Current.InfoBack;
    public static Color InfoBorder => Current.InfoBorder;
    public static Color Attention => Current.Attention;
    public static Color DangerBack => Current.DangerBack;
    public static Color DangerBorder => Current.DangerBorder;
    public static Color Hover => Current.Hover;
    public static bool IsDark => Current.Dark;

    public static readonly Font Body = new("Segoe UI", 10.5f);
    public static readonly Font Small = new("Segoe UI", 9.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font Heading = new("Segoe UI Semibold", 12f);
    public static readonly Font Mono = new("Consolas", 12f);

    // ── which colour each control wears ──────────────────────────────────────
    sealed class Wear { public Slot Back = Slot.None, Fore = Slot.None, Line = Slot.None; public bool Primary, Secondary; }

    static readonly ConditionalWeakTable<Control, Wear> Worn = new();

    static Wear Of(Control c) => Worn.GetValue(c, _ => new Wear());

    /// <summary>Say which colours a control wears, so it follows the look when it changes.</summary>
    public static T Wears<T>(this T c, Slot back = Slot.None, Slot fore = Slot.None) where T : Control
    {
        var w = Of(c);
        if (back != Slot.None) w.Back = back;
        if (fore != Slot.None) w.Fore = fore;
        if (back != Slot.None) c.BackColor = Current.Of(back);
        if (fore != Slot.None) c.ForeColor = Current.Of(fore);
        return c;
    }

    /// <summary>The slot a colour stands for, guessed in an order that fits the control (Window and Card
    /// are the same white in the light look, so the order decides).</summary>
    static Slot SlotOf(Color c, params Slot[] order)
    {
        foreach (var s in order) if (Current.Of(s).ToArgb() == c.ToArgb()) return s;
        return Slot.None;
    }

    static readonly Slot[] BackOrder =
        [Slot.Window, Slot.Card, Slot.Surface, Slot.Input, Slot.WarnBack, Slot.InfoBack, Slot.DangerBack,
         Slot.AccentSoft, Slot.Accent, Slot.Hover, Slot.Border];
    static readonly Slot[] ForeOrder =
        [Slot.Text, Slot.Muted, Slot.Error, Slot.Success, Slot.Attention, Slot.Accent, Slot.AccentDark, Slot.OnAccent, Slot.Border];

    /// <summary>
    /// Dress a window (and everything in it) in the current look. Called when a window is built and again
    /// whenever the look changes. A control that was given its slots (<see cref="Wears"/>, or one of the
    /// factories below) wears those; for the rest the slot is guessed once, from the colour it has now,
    /// and remembered — so the guess happens while that colour still belongs to the look in force.
    /// </summary>
    public static void Apply(Control root)
    {
        if (!_loaded) Load();
        var w = Of(root);
        if (w.Primary || w.Secondary) Style((Button)root, w.Primary);
        else
        {
            if (w.Back == Slot.None && !(root is System.Windows.Forms.Label or LinkLabel or CheckBox or RadioButton))
                w.Back = SlotOf(root.BackColor, BackOrder);
            if (w.Fore == Slot.None) w.Fore = SlotOf(root.ForeColor, ForeOrder);
            // A control nobody dressed and whose colour belongs to no slot (a system colour, DimGray):
            // give it the window's own colours, so it is never dark text on a dark window.
            if (w.Back == Slot.None && root is ListView or TextBox or NumericUpDown or ComboBox or ListBox) w.Back = Slot.Input;
            if (w.Fore == Slot.None) w.Fore = Slot.Text;
            if (w.Back != Slot.None) root.BackColor = Current.Of(w.Back);
            root.ForeColor = Current.Of(w.Fore);
        }
        if (root is LinkLabel link)
        {
            link.LinkColor = Current.Of(w.Fore == Slot.Muted ? Slot.Muted : Slot.Accent);
            link.ActiveLinkColor = AccentDark;
            link.VisitedLinkColor = link.LinkColor;
            link.DisabledLinkColor = Muted;
        }
        if (root is ListView list) ThemePaint.List(list);
        foreach (Control child in root.Controls) Apply(child);
        root.Invalidate(true);
        if (root is Form form) ThemePaint.TitleBar(form);
    }

    // ── control factories ────────────────────────────────────────────────────
    public static Label Label(string text, Font? font = null, Color? color = null, int maxWidth = 520)
    {
        var l = new Label
        {
            Text = text,
            Font = font ?? Body,
            ForeColor = color ?? Text,
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            Margin = new Padding(0, 0, 0, 8),
        };
        Of(l).Fore = SlotOf(l.ForeColor, ForeOrder) is var s && s != Slot.None ? s : Slot.Text;
        return l;
    }

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
        return cb.Wears(fore: Slot.Text);
    }

    /// <summary>Puts a window's content in place, with space around it, and dresses it in the current look.
    /// The space is the window's Padding around a docked column, not the column's own Padding: in a mirrored
    /// (Hebrew) window a column's Padding landed twice on the right and not at all on the left, in an
    /// AutoScroll column its vertical Padding was counted twice against the scroll range (the last button
    /// could not be scrolled into view), and an AutoSize window's Padding does not move a child that is not
    /// docked (measured).</summary>
    public static void Fill(Form f, Control content, Padding space)
    {
        f.Padding = space;
        content.Dock = DockStyle.Fill;
        f.Controls.Add(content);
        Apply(f);
    }

    public static Button Primary(string text) => Style(new Button { Text = text }, primary: true);
    public static Button Secondary(string text) => Style(new Button { Text = text }, primary: false);

    static Button Style(Button b, bool primary)
    {
        var w = Of(b);
        bool first = !w.Primary && !w.Secondary;
        w.Primary = primary;
        w.Secondary = !primary;
        b.FlatStyle = FlatStyle.Flat;
        if (first)
        {
            b.Font = Body;
            b.AutoSize = true;
            b.MinimumSize = new Size(110, 38);
            b.Padding = new Padding(14, 2, 14, 2);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
        }
        if (primary)
        {
            b.BackColor = b.Enabled ? Accent : Blend(Accent, Window, 0.55);
            b.ForeColor = b.Enabled ? OnAccent : Blend(OnAccent, Accent, 0.4);
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
        }
        else
        {
            b.BackColor = CardBack;
            b.ForeColor = b.Enabled ? Text : Muted;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Hover;
        }
        if (first) b.EnabledChanged += (_, _) => Style(b, Of(b).Primary);
        return b;
    }

    /// <summary>`a` mixed with `b` (0 = all a, 1 = all b): a disabled button, a quiet line.</summary>
    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>A text box for left-to-right input (phone, codes, passwords, API keys) inside the RTL window.</summary>
    public static TextBox Input(int width = 300, bool password = false)
    {
        var t = new TextBox
        {
            Font = Mono,
            Width = width,
            RightToLeft = RightToLeft.No,
            UseSystemPasswordChar = password,
            Margin = new Padding(0, 0, 0, 10),
            BorderStyle = BorderStyle.FixedSingle,
        };
        return t.Wears(Slot.Input, Slot.Text);
    }

    public static Panel Card(Color back, Color border) =>
        Card(SlotOf(back, BackOrder), SlotOf(border, Slot.Border, Slot.WarnBorder, Slot.InfoBorder, Slot.DangerBorder, Slot.Accent, Slot.Error));

    public static Panel Card(Slot back, Slot border)
    {
        if (back == Slot.None) back = Slot.Card;
        if (border == Slot.None) border = Slot.Border;
        var p = new CardPanel { Padding = new Padding(14, 10, 14, 10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 10) };
        var w = Of(p);
        w.Back = back;
        w.Line = border;
        p.BackColor = Current.Of(back);
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Current.Of(Of(p).Line is var l && l != Slot.None ? l : Slot.Border));
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
