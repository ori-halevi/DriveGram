using System.Drawing.Drawing2D;

namespace DriveGram.Tray.Setup;

/// <summary>
/// An on/off switch (a check box drawn as a switch): for settings that apply to the whole page, unlike
/// the check boxes that choose items. In a right-to-left window it is mirrored with the window.
/// </summary>
sealed class ToggleSwitch : CheckBox
{
    const int TrackW = 40, TrackH = 20, Gap = 10;

    public ToggleSwitch(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 4, 0, 4);
        Text = text;
        Fit();
        TextChanged += (_, _) => Fit();
        FontChanged += (_, _) => Fit();
    }

    void Fit()
    {
        var t = TextRenderer.MeasureText(Text, Font);
        Size = new Size(TrackW + Gap + t.Width + 4, Math.Max(TrackH + 8, t.Height + 6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // logical left: the reading start (a mirrored window puts it on the right by itself)
        var track = new Rectangle(1, (Height - TrackH) / 2, TrackW, TrackH);
        using (var path = Pill(track))
        using (var fill = new SolidBrush(!Enabled ? Theme.Border : Checked ? Theme.Accent : Theme.CardBack))
        using (var pen = new Pen(Checked && Enabled ? Theme.Accent : Theme.Muted, 1.4f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        int d = TrackH - 8;
        var knob = new Rectangle(Checked ? track.Right - d - 4 : track.X + 4, track.Y + 4, d, d);
        using (var kb = new SolidBrush(Checked ? Theme.OnAccent : Theme.Muted)) g.FillEllipse(kb, knob);
        if (Focused && ShowFocusCues) using (var fp = new Pen(Theme.AccentDark, 1) { DashStyle = DashStyle.Dot }) g.DrawPath(fp, Pill(Rectangle.Inflate(track, 2, 2)));
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(track.Right + Gap, 0, Width - track.Right - Gap, Height), Enabled ? Theme.Text : Theme.Muted, flags);
    }

    static GraphicsPath Pill(Rectangle r)
    {
        var p = new GraphicsPath();
        int d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}
