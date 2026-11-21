using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>Copies a whole mounted drive to a folder, with progress, cancel, and resume on re-run.</summary>
sealed class ExportForm : Form
{
    readonly Label _status = Theme.Label("", Theme.Body, Theme.Text, 600);
    readonly Label _current = Theme.Label("", Theme.Small, Theme.Muted, 600);
    readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, Width = 600, Height = 14 };
    readonly Button _cancel = Theme.Secondary(L.T("עצור", "Stop"));
    readonly CancellationTokenSource _cts = new();

    public ExportForm(DriveSession session, string target)
    {
        Ui.Prepare(this);
        Text = L.T($"DriveGram – ייצוא {session.MountPoint}", $"DriveGram – exporting {session.MountPoint}");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        BackColor = Color.White;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T($"מעתיק את כל הקבצים אל: {target}", $"Copying every file to: {target}"), Theme.Body, Theme.Text, 600));
        col.Controls.Add(Theme.Label(L.T("קבצים שלא שמורים במחשב יורדים מטלגרם, וזה יכול לקחת זמן. אפשר לעצור ולהמשיך אחר כך: מה שכבר הועתק מדולג.",
            "Files not cached on this PC download from Telegram, which can take a while. You can stop and continue later: what was already copied is skipped."),
            Theme.Small, Theme.Muted, 600));
        col.Controls.AddRange([_bar, _status, _current, _cancel]);
        Controls.Add(col);
        _cancel.Click += (_, _) => { if (_cts.IsCancellationRequested) Close(); else _cts.Cancel(); };
        FormClosing += (_, _) => _cts.Cancel();

        Shown += async (_, _) =>
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                _status.Text = L.T($"הועתקו {p.Files:N0} קבצים ({Ui.Size(p.Bytes)}), {p.Skipped:N0} כבר היו שם",
                                   $"{p.Files:N0} files copied ({Ui.Size(p.Bytes)}), {p.Skipped:N0} already there");
                _current.Text = p.Current;
            });
            try
            {
                var errors = await Task.Run(() => Exporter.CopyAsync(session.MountPoint + "\\", target, progress, _cts.Token));
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 100;
                _current.Text = errors.Count == 0
                    ? L.T("✓  הייצוא הושלם.", "✓  Export complete.")
                    : L.T($"הושלם, אבל {errors.Count} קבצים לא הועתקו (פרטים ביומן). אפשר להריץ שוב כדי לנסות אותם.",
                          $"Done, but {errors.Count} files could not be copied (details in the log). Run it again to retry them.");
                foreach (var e in errors) Log.Warn($"export: {e}");
            }
            catch (OperationCanceledException) { _current.Text = L.T("נעצר. אפשר להמשיך אחר כך.", "Stopped. You can continue later."); }
            catch (Exception ex) { _current.Text = TelegramErrors.Text(ex); }
            _cancel.Text = L.T("סגור", "Close");
            _cts.Cancel();
        };
    }
}
