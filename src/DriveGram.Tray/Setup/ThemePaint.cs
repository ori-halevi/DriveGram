using System.Runtime.InteropServices;
using DriveGram.Host;

namespace DriveGram.Tray.Setup;

/// <summary>
/// The parts Windows would otherwise paint in its own colours: a window's title bar, the headers of a
/// list, and every menu and tool bar. Everything here is best-effort — a look that cannot be applied
/// never stops a window from opening.
/// </summary>
static class ThemePaint
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int UseImmersiveDarkMode = 20;

    /// <summary>A dark window gets a dark title bar (and a light one gets it back).</summary>
    public static void TitleBar(Form f)
    {
        void Set()
        {
            try
            {
                int on = Theme.IsDark ? 1 : 0;
                DwmSetWindowAttribute(f.Handle, UseImmersiveDarkMode, ref on, sizeof(int));
            }
            catch (Exception ex) { Log.Debug($"theme: title bar: {ex.Message}"); }
        }
        if (f.IsHandleCreated) Set();
        else f.HandleCreated += (_, _) => Set();
    }

    // ── lists ────────────────────────────────────────────────────────────────
    const int LvmGetHeader = 0x101F;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window, string? app, string? id);

    /// <summary>
    /// A list in the current look. The rows' colours are ours, but the column headers are a little window
    /// of Windows' own inside the list: it paints them, and only its "dark mode" styles make them dark.
    /// (Painting them ourselves was tried first and came out in pieces — in a Hebrew, mirrored list Windows
    /// repaints only the part it believes changed.)
    /// </summary>
    public static void List(ListView list)
    {
        list.BackColor = Theme.InputBack;
        list.ForeColor = Theme.Text;
        void Dress()
        {
            if (!list.IsHandleCreated) return;
            try
            {
                SetWindowTheme(list.Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null);
                var header = SendMessage(list.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, Theme.IsDark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
            catch (Exception ex) { Log.Debug($"theme: list: {ex.Message}"); }
            list.Invalidate(true);
        }
        if (list.IsHandleCreated) Dress(); else list.HandleCreated += (_, _) => Dress();
    }

    // ── menus and tool bars ──────────────────────────────────────────────────
    /// <summary>The renderer every DriveGram menu and tool bar uses (the tray's menu included).</summary>
    public static ToolStripRenderer Renderer => new MenuRenderer();

    /// <summary>Dress a menu or tool bar (and its sub-menus) in the current look.</summary>
    public static void Menu(ToolStrip strip)
    {
        strip.Renderer = Renderer;
        strip.BackColor = Theme.CardBack;
        strip.ForeColor = Theme.Text;
        foreach (ToolStripItem item in strip.Items) MenuItem(item);
    }

    static void MenuItem(ToolStripItem item)
    {
        // a colour the caller chose on purpose (the red "delete for ever", the orange "a new version")
        // stays as it is; everything else follows the look
        if (item.ForeColor.ToArgb() is var c && (c == 0 || c == Theme.LightPalette.Text.ToArgb()
            || c == Theme.DarkPalette.Text.ToArgb() || c == Theme.RosePalette.Text.ToArgb()))
            item.ForeColor = Theme.Text;
        if (item is ToolStripMenuItem m)
            foreach (ToolStripItem sub in m.DropDownItems) MenuItem(sub);
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.CardBack;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.AccentSoft;
        public override Color MenuItemSelected => Theme.AccentSoft;
        public override Color MenuItemSelectedGradientBegin => Theme.AccentSoft;
        public override Color MenuItemSelectedGradientEnd => Theme.AccentSoft;
        public override Color MenuItemPressedGradientBegin => Theme.CardBack;
        public override Color MenuItemPressedGradientMiddle => Theme.CardBack;
        public override Color MenuItemPressedGradientEnd => Theme.CardBack;
        public override Color ImageMarginGradientBegin => Theme.CardBack;
        public override Color ImageMarginGradientMiddle => Theme.CardBack;
        public override Color ImageMarginGradientEnd => Theme.CardBack;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color ToolStripBorder => Theme.Border;
        public override Color ToolStripGradientBegin => Theme.Surface;
        public override Color ToolStripGradientMiddle => Theme.Surface;
        public override Color ToolStripGradientEnd => Theme.Surface;
        public override Color ToolStripContentPanelGradientBegin => Theme.Surface;
        public override Color ToolStripContentPanelGradientEnd => Theme.Surface;
        public override Color ButtonSelectedHighlight => Theme.Hover;
        public override Color ButtonSelectedHighlightBorder => Theme.Border;
        public override Color ButtonSelectedGradientBegin => Theme.Hover;
        public override Color ButtonSelectedGradientMiddle => Theme.Hover;
        public override Color ButtonSelectedGradientEnd => Theme.Hover;
        public override Color ButtonSelectedBorder => Theme.Border;
        public override Color ButtonCheckedGradientBegin => Theme.AccentSoft;
        public override Color ButtonCheckedGradientMiddle => Theme.AccentSoft;
        public override Color ButtonCheckedGradientEnd => Theme.AccentSoft;
        public override Color ButtonPressedGradientBegin => Theme.AccentSoft;
        public override Color ButtonPressedGradientMiddle => Theme.AccentSoft;
        public override Color ButtonPressedGradientEnd => Theme.AccentSoft;
        public override Color ButtonPressedBorder => Theme.Accent;
        public override Color CheckBackground => Theme.AccentSoft;
        public override Color CheckSelectedBackground => Theme.AccentSoft;
        public override Color GripDark => Theme.Border;
        public override Color GripLight => Theme.Border;
    }

    sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // a disabled item is readable, and a greyed "title" item (the file's state in the right-click
            // menu) is not nearly invisible as Windows would draw it
            if (!e.Item!.Enabled) e.TextColor = Theme.Muted;
            else if (e.TextColor.ToArgb() == SystemColors.ControlText.ToArgb()) e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? Theme.Muted : Theme.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using var brush = new SolidBrush(Theme.Accent);
            var r = new Rectangle(e.ImageRectangle.X + 3, e.ImageRectangle.Y + 3, e.ImageRectangle.Width - 6, e.ImageRectangle.Height - 6);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(brush, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var b = e.Item!.Bounds;
            using var pen = new Pen(Theme.Border);
            if (e.Vertical) e.Graphics.DrawLine(pen, b.Width / 2, 4, b.Width / 2, b.Height - 4);
            else e.Graphics.DrawLine(pen, 2, b.Height / 2, b.Width - 2, b.Height / 2);
        }
    }
}
