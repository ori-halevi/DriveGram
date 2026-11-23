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
        Text = L.T($"DriveGram – ייצוא {L.Ltr(session.MountPoint)}", $"DriveGram – exporting {session.MountPoint}");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T($"מעתיק את כל הקבצים אל: {L.LtrPath(target)}", $"Copying every file to: {target}"), Theme.Body, Theme.Text, 600));
        col.Controls.Add(Theme.Label(L.T("קבצים שלא שמורים במחשב הזה יורדים מטלגרם, וזה יכול לקחת זמן. אפשר לעצור ולהמשיך אחר כך: מה שכבר הועתק לא יועתק שוב. הקבצים בכונן לא משתנים.",
            "Files not stored on this PC download from Telegram, which can take a while. You can stop and continue later: what was already copied is not copied again. The files on the drive are not changed."),
            Theme.Small, Theme.Muted, 600));
        col.Controls.AddRange([_bar, _status, _current, _cancel]);
        Theme.Fill(this, col, new Padding(20));
        _cancel.Click += (_, _) => { if (_cts.IsCancellationRequested) Close(); else _cts.Cancel(); };
        FormClosing += (_, _) => _cts.Cancel();

        Shown += async (_, _) =>
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                _status.Text = L.N(p.Files, "הועתק קובץ אחד", "הועתקו {0:N0} קבצים", "1 file copied", "{0:N0} files copied")
                               + $" ({Ui.Size(p.Bytes)})" + L.T(",\u200F ", ", ") // RLM: the count after the size stays after it in Hebrew
                               + L.N(p.Skipped, "אחד כבר היה שם", "{0:N0} כבר היו שם", "1 already there", "{0:N0} already there");
                _current.Text = L.LtrPath(p.Current);
            });
            try
            {
                ExportResult result;
                try { result = await Task.Run(() => Exporter.CopyAsync(session.MountPoint + "\\", target, progress, _cts.Token)); }
                catch (Exporter.TargetNotEmptyException)
                {
                    if (Ui.Ask(L.T("בתיקייה כבר יש קבצים. קובץ עם אותו שם כמו בכונן יוחלף בגרסה שבכונן, אלא אם הקובץ שבתיקייה חדש יותר: אז הוא יישאר כמו שהוא. להמשיך?",
                            "The folder already holds files. A file with the same name as on the drive is replaced by the drive's version, unless the folder's file is newer: then it is kept as it is. Continue?"),
                            icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        Close();
                        return;
                    }
                    result = await Task.Run(() => Exporter.CopyAsync(session.MountPoint + "\\", target, progress, _cts.Token, allowNonEmpty: true));
                }
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 100;
                var errors = result.Errors;
                var lines = new List<string>();
                lines.Add(errors.Count == 0
                    ? L.T("✓  הייצוא הושלם.", "✓  Export complete.")
                    : L.N(errors.Count,
                        "הושלם, אבל קובץ אחד לא הועתק בגלל שגיאה (פרטים ביומן הפעילות). אפשר להריץ שוב כדי לנסות אותו.",
                        "הושלם, אבל {0:N0} קבצים לא הועתקו בגלל שגיאה (פרטים ביומן הפעילות). אפשר להריץ שוב כדי לנסות אותם.",
                        "Done, but 1 file could not be copied (details in the log). Run it again to retry it.",
                        "Done, but {0:N0} files could not be copied (details in the log). Run it again to retry them."));
                if (result.KeptNewer.Count > 0)
                    lines.Add(L.N(result.KeptNewer.Count,
                        "קובץ אחד בתיקייה חדש יותר מהגרסה שבכונן, ולכן לא הוחלף.",
                        "{0:N0} קבצים בתיקייה חדשים יותר מהגרסה שבכונן, ולכן לא הוחלפו.",
                        "1 file in the folder is newer than the drive's copy, so it was not replaced.",
                        "{0:N0} files in the folder are newer than the drive's copy, so they were not replaced."));
                _current.Text = string.Join("\n", lines);
                foreach (var e in errors) Log.Warn($"export: {e}");
                foreach (var k in result.KeptNewer) Log.Info($"export: {k}: newer than the drive's copy; kept");
            }
            catch (OperationCanceledException) { _current.Text = L.T("נעצר. אפשר להמשיך אחר כך.", "Stopped. You can continue later."); }
            catch (Exception ex) { _current.Text = TelegramErrors.Text(ex); }
            _cancel.Text = L.T("סגור", "Close");
            _cts.Cancel();
        };
    }
}
