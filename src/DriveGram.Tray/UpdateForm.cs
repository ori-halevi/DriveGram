using System.Text.RegularExpressions;
using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>"A new version of DriveGram": what is new, and download-and-install / the download page /
/// don't offer this version / not now.</summary>
sealed class UpdateForm : Form
{
    /// <summary>The user chose not to be offered this version again.</summary>
    public bool Skipped { get; private set; }

    readonly UpdateInfo _u;
    readonly Button _install, _skip, _later;
    readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted, 600);
    CancellationTokenSource? _download;

    bool CanInstall => _u.SetupUrl is not null && _u.Sha256 is not null;

    public UpdateForm(UpdateInfo u)
    {
        _u = u;
        Ui.Prepare(this);
        Text = L.T("DriveGram – גרסה חדשה", "DriveGram – a new version");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(720, 520);
        MinimumSize = new Size(600, 400);
        BackColor = Color.White;

        var head = Theme.Column();
        head.Dock = DockStyle.Top;
        head.AutoSize = true;
        head.Padding = new Padding(20, 16, 20, 8);
        head.Controls.Add(Theme.Label(L.T($"יש גרסה חדשה של DriveGram: {L.Ltr(u.Version.ToString())}", $"A new version of DriveGram: {u.Version}"), Theme.Heading, Theme.Text, 660));
        head.Controls.Add(Theme.Label(L.T($"הגרסה שלך: {L.Ltr(Updates.Current.ToString())}  ·  מה חדש בגרסה {L.Ltr(u.Version.ToString())}:", $"Yours: {Updates.Current}  ·  what's new in {u.Version}:"), Theme.Small, Theme.Muted, 660));

        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, Font = Ui.Font, Text = Clean(u.Notes, u.Version),
            RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        var notesBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 8) };
        notesBox.Controls.Add(notes);

        var foot = Theme.Column();
        foot.Dock = DockStyle.Bottom;
        foot.AutoSize = true;
        foot.Padding = new Padding(20, 4, 20, 16);
        _status.Text = CanInstall
            ? L.T("\"הורד והתקן\" מוריד את קובץ ההתקנה, בודק שהוא בדיוק הקובץ שפורסם, ופותח אותו. לפני ההתקנה DriveGram מסיים להעלות את מה שעוד לא עלה ומתנתק לרגע.",
                "\"Download and install\" downloads the installer, checks it is exactly the file published, and opens it. Before installing, DriveGram finishes uploading what is not up yet and disconnects for a moment.")
            : L.T("לגרסה הזו אין הורדה ישירה מכאן. אפשר להוריד אותה מדף ההורדה.", "This version has no direct download from here. It can be downloaded from the download page.");
        foot.Controls.Add(_status);
        var buttons = Theme.Row();
        buttons.Margin = new Padding(0, 8, 0, 0);
        _install = Theme.Primary(L.T("הורד והתקן", "Download and install"));
        _install.Enabled = CanInstall;
        _install.Click += async (_, _) => await InstallAsync();
        var page = CanInstall ? Theme.Secondary(L.T("פתח את דף ההורדה", "Open the download page")) : Theme.Primary(L.T("פתח את דף ההורדה", "Open the download page"));
        page.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.PageUrl) { UseShellExecute = true }); } catch (Exception ex) { Ui.Error(ex.Message); } };
        _skip = Theme.Secondary(L.T("אל תציע לי את הגרסה הזו", "Don't offer this version"));
        _skip.Click += (_, _) => { Skipped = true; Close(); };
        _later = Theme.Secondary(L.T("לא עכשיו", "Not now"));
        _later.Click += (_, _) => { if (_download is { IsCancellationRequested: false }) _download.Cancel(); else Close(); };
        if (CanInstall) buttons.Controls.Add(_install);
        buttons.Controls.AddRange([page, _skip, _later]);
        foot.Controls.Add(buttons);

        Controls.Add(notesBox);
        Controls.Add(foot);
        Controls.Add(head);
        FormClosing += (_, _) => _download?.Cancel();
        // in front once (opened from the tray), not on top of the browser it may open
        Shown += (_, _) => { TopMost = true; TopMost = false; Activate(); notes.SelectionLength = 0; ActiveControl = CanInstall ? _install : page; };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { _later.PerformClick(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>The release text as plain text: no Markdown marks, no heading repeating the title, links as
    /// "text (address)", and no check-sum line (it is for the program).</summary>
    static string Clean(string notes, Version version)
    {
        var lines = new List<string>();
        bool first = true;
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw;
            if (Regex.IsMatch(l, @"\b[0-9a-fA-F]{64}\b")) continue;
            l = Regex.Replace(l, @"^\s{0,3}#{1,6}\s+", "");                       // headings
            l = Regex.Replace(l, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");          // links
            l = l.Replace("**", "").Replace("__", "").Replace("`", "");
            l = Regex.Replace(l, @"^(\s*)[-*+]\s+", m => new string(' ', m.Groups[1].Length) + "•  "); // bullets, nested too
            if (first && l.Trim().Length > 0)
            {
                first = false;
                if (l.Contains(version.ToString()) && l.Contains("DriveGram")) continue; // "DriveGram 1.6.0": the title already says it
            }
            lines.Add(l);
        }
        var text = string.Join(Environment.NewLine, lines).Trim();
        return text.Length > 0 ? text : L.T("(אין תיאור לגרסה הזו)", "(no description for this version)");
    }

    async Task InstallAsync()
    {
        _install.Enabled = false;
        _skip.Enabled = false;
        _later.Text = L.T("בטל את ההורדה", "Cancel the download");
        _status.ForeColor = Theme.Muted;
        _download = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double?>(f => { if (!IsDisposed) _status.Text = f is { } x ? L.T($"מוריד את ההתקנה… {x * 100:0}%", $"Downloading the installer… {x * 100:0}%") : L.T("מוריד את ההתקנה…", "Downloading the installer…"); });
            using var file = await Updates.DownloadAsync(_u, progress, _download.Token);
            if (IsDisposed || _download.IsCancellationRequested) return; // closed meanwhile: nothing is opened
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true });
            Close();
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            if (IsDisposed) return;
            _status.Text = L.T("ההורדה בוטלה.", "The download was cancelled.");
            Reset();
        }
        catch (Updates.MismatchException)
        {
            Log.Warn("update: the downloaded installer did not match the release");
            if (IsDisposed) return;
            _status.Text = L.T("הקובץ שהורד לא תואם לגרסה שפורסמה, ולכן הוא נמחק ולא הותקן. נסה שוב מאוחר יותר.",
                "The downloaded file does not match the published version, so it was deleted and not installed. Try again later.");
            _status.ForeColor = Theme.Error;
            Reset();
        }
        catch (Exception ex)
        {
            Log.Warn($"update: {ex.Message}");
            if (IsDisposed) return;
            _status.Text = L.T("ההורדה לא הצליחה (אולי החיבור לאינטרנט נקטע). אפשר לנסות שוב, או לפתוח את דף ההורדה.",
                "The download did not work (perhaps the internet connection dropped). Try again, or open the download page.");
            _status.ForeColor = Theme.Error;
            Reset();
        }
    }

    void Reset()
    {
        _install.Enabled = true;
        _skip.Enabled = true;
        _later.Text = L.T("לא עכשיו", "Not now");
    }
}
