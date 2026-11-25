using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>The optional daily upload notice and cap (all drives on this PC together).</summary>
sealed class LimitsForm : Form
{
    readonly CheckBox _alertOn = Theme.WrapCheck(L.T("התרע כשביום אחד עולים לטלגרם יותר מ־:", "Tell me when more than this goes up to Telegram in one day:"), Theme.Body, 560);
    readonly NumericUpDown _alert = Number();
    readonly CheckBox _capOn = Theme.WrapCheck(L.T("עצור את ההעלאות עד חצות כשביום אחד עולים יותר מ־:", "Pause uploads until midnight once more than this went up in one day:"), Theme.Body, 560);
    readonly NumericUpDown _cap = Number();

    public double AlertGb => _alertOn.Checked ? (double)_alert.Value : 0;
    public double CapGb => _capOn.Checked ? (double)_cap.Value : 0;

    static NumericUpDown Number() => new()
    {
        Minimum = 1, Maximum = 10000, DecimalPlaces = 0, Width = 90, Font = Theme.Body, Value = 50,
        RightToLeft = RightToLeft.No, Margin = new Padding(26, 2, 26, 10),
    };

    public LimitsForm(double alertGb, double capGb, long today)
    {
        Ui.Prepare(this);
        Text = L.T("DriveGram – תקרה יומית להעלאות", "DriveGram – daily upload cap");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T(
            "טלגרם לא מפרסם מגבלה יומית של העלאות. מה שידוע: חשבון בלי Premium מקבל מהירות העלאה איטית יותר, וטלגרם חוסם דפוסים שנראים כמו ספאם או שימוש לרעה. אם אתה מעדיף להיזהר, אפשר לקבל התראה או לעצור את ההעלאות בכמות שתבחר. הספירה כוללת את כל הכוננים במחשב הזה.",
            "Telegram publishes no daily upload limit. What is known: an account without Premium uploads more slowly, and Telegram blocks patterns that look like spam or abuse. If you prefer to be careful, you can get a notice, or pause uploads, at an amount you choose. It counts every drive on this PC."),
            Theme.Small, Theme.Muted, 560));
        col.Controls.Add(Theme.Label(L.T($"היום הועלו עד עכשיו: {Ui.Size(today)}", $"Uploaded so far today: {Ui.Size(today)}"), Theme.Body, Theme.Text, 560));

        _alertOn.Margin = new Padding(0, 12, 0, 2);
        col.Controls.Add(_alertOn);
        col.Controls.Add(Row(_alert));
        col.Controls.Add(Theme.Label(L.T("ההתראה מופיעה גם מראש, כשמה שממתין להעלאה עומד לחרוג מהכמות הזו.", "The notice also comes ahead of time, when what waits to upload is about to pass that amount."),
            Theme.Small, Theme.Muted, 560));

        _capOn.Margin = new Padding(0, 12, 0, 2);
        col.Controls.Add(_capOn);
        col.Controls.Add(Row(_cap));
        col.Controls.Add(Theme.Label(L.T(
            "בזמן העצירה הקבצים מחכים בבטחה במחשב הזה, ועולים לבד אחרי חצות. אפשר גם להמשיך בכל זאת מלשונית \"העברות\" בחלון של DriveGram. העתקה גדולה לכונן בזמן העצירה עלולה להיעצר כשנגמר המקום במחשב.",
            "While paused, files wait safely on this PC and go up by themselves after midnight. You can also go on anyway from the \"Transfers\" tab of the DriveGram window. A big copy into the drive while paused may stop when this PC runs out of space."),
            Theme.Small, Theme.Muted, 560));

        _alertOn.Checked = alertGb > 0;
        if (alertGb > 0) _alert.Value = (decimal)Math.Clamp(alertGb, 1, 10000);
        _capOn.Checked = capGb > 0;
        if (capGb > 0) _cap.Value = (decimal)Math.Clamp(capGb, 1, 10000);
        _alert.Enabled = _alertOn.Checked;
        _cap.Enabled = _capOn.Checked;
        _alertOn.CheckedChanged += (_, _) => _alert.Enabled = _alertOn.Checked;
        _capOn.CheckedChanged += (_, _) => _cap.Enabled = _capOn.Checked;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
        var ok = Theme.Primary(L.T("שמור", "Save"));
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
        col.Controls.Add(buttons);
        Theme.Fill(this, col, new Padding(20, 18, 20, 14));
    }

    static FlowLayoutPanel Row(NumericUpDown n)
    {
        var r = Theme.Row();
        r.Controls.Add(n);
        r.Controls.Add(new Label { Text = "GB", AutoSize = true, Font = Theme.Body, Margin = new Padding(0, 5, 0, 0) });
        return r;
    }
}
