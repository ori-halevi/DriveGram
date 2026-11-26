using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>"A new version of DriveGram": what is new, and download-and-install / the download page /
/// skip this version / not now.</summary>
sealed class UpdateForm : Form
{
    /// <summary>The user chose to skip this version.</summary>
    public bool Skipped { get; private set; }

    readonly UpdateInfo _u;
    readonly Button _install;
    readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted, 560);
    CancellationTokenSource? _download;

    public UpdateForm(UpdateInfo u)
    {
        _u = u;
        Ui.Prepare(this);
        Text = L.T("DriveGram – גרסה חדשה", "DriveGram – a new version");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 520);
        MinimumSize = new Size(520, 400);
        BackColor = Color.White;
        TopMost = true; // opened from the tray: in front

        var head = Theme.Column();
        head.Dock = DockStyle.Top;
        head.AutoSize = true;
        head.Padding = new Padding(20, 16, 20, 8);
        head.Controls.Add(Theme.Label(L.T($"יש גרסה חדשה של DriveGram: {L.Ltr(u.Version.ToString())}", $"A new version of DriveGram: {u.Version}"), Theme.Heading, Theme.Text, 580));
        head.Controls.Add(Theme.Label(L.T($"מה חדש? הגרסה שלך: {L.Ltr(Updates.Current.ToString())}", $"What's new? Yours is {Updates.Current}"), Theme.Small, Theme.Muted, 580));

        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, Font = Ui.Font, Text = Clean(u.Notes),
            RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        var notesBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 8) };
        notesBox.Controls.Add(notes);

        var foot = Theme.Column();
        foot.Dock = DockStyle.Bottom;
        foot.AutoSize = true;
        foot.Padding = new Padding(20, 4, 20, 16);
        _status.Text = L.T("ההתקנה נפתחת כרגיל, ואפשר לבטל אותה. כוננים מחוברים יתנתקו לרגע, וקבצים שעוד לא עלו ימשיכו לעלות אחריה.",
            "The installer opens as usual, and can be cancelled. Connected drives disconnect for a moment; files not uploaded yet go on uploading after it.");
        foot.Controls.Add(_status);
        var buttons = Theme.Row();
        buttons.Margin = new Padding(0, 8, 0, 0);
        _install = Theme.Primary(L.T("הורד והתקן", "Download and install"));
        _install.Enabled = u.SetupUrl is not null;
        _install.Click += async (_, _) => await InstallAsync();
        var page = Theme.Secondary(L.T("פתח את דף ההורדה", "Open the download page"));
        page.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.PageUrl) { UseShellExecute = true }); } catch (Exception ex) { Ui.Error(ex.Message); } };
        var skip = Theme.Secondary(L.T("דלג על הגרסה הזו", "Skip this version"));
        skip.Click += (_, _) => { Skipped = true; Close(); };
        buttons.Controls.AddRange([_install, page, skip]); // "not now": the window's X, or Esc
        foot.Controls.Add(buttons);

        Controls.Add(notesBox);
        Controls.Add(foot);
        Controls.Add(head);
        FormClosing += (_, _) => _download?.Cancel();
        Shown += (_, _) => { notes.SelectionLength = 0; ActiveControl = _install.Enabled ? _install : page; };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>The release text without Markdown's marks (it is shown as plain text).</summary>
    static string Clean(string notes)
    {
        var lines = notes.Replace("\r\n", "\n").Split('\n')
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\b[0-9a-fA-F]{64}\b")) // the installer's check sum: for the program
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", ""))
            .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "•  " + l[2..] : l);
        var text = string.Join(Environment.NewLine, lines).Trim();
        return text.Length > 0 ? text : L.T("(אין תיאור לגרסה הזו)", "(no description for this version)");
    }

    async Task InstallAsync()
    {
        _install.Enabled = false;
        _download = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(f => _status.Text = L.T($"מוריד את ההתקנה… {f * 100:0}%", $"Downloading the installer… {f * 100:0}%"));
            var path = await Updates.DownloadAsync(_u, progress, _download.Token);
            _status.Text = L.T("פותח את ההתקנה…", "Opening the installer…");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"update: {ex.Message}");
            _status.Text = L.T("ההורדה לא הצליחה. אפשר לנסות שוב, או לפתוח את דף ההורדה ולהוריד משם.", "The download did not work. Try again, or open the download page and download it from there.");
            _status.ForeColor = Theme.Error;
            _install.Enabled = true;
        }
    }
}
