using DriveGram.Host;
using DriveGram.Sync;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// On the PC that writes: another PC asks for the drive. Approve, refuse, or let the countdown approve
/// (the asking PC takes the drive anyway when its own wait runs out, so silence means "yes").
/// </summary>
sealed class HandoverAskForm : Form
{
    readonly Label _countdown = Theme.Label("", Theme.Body, Theme.Muted, 560);
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    readonly DateTime _deadline;

    /// <summary>True: hand the drive over. False: refused.</summary>
    public bool Approved { get; private set; }

    public HandoverAskForm(DriveSession session, HandoverRequest r)
    {
        Ui.Prepare(this);
        Text = L.T("DriveGram – בקשה לקבל את הכונן", "DriveGram – a request for the drive");
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        // the other PC takes the drive 60 s after it asked; answer a little before that
        _deadline = DateTime.UtcNow + SyncService.HandoverAnswerTime - TimeSpan.FromSeconds(12);

        var col = Theme.Column();
        col.Padding = new Padding(24, 18, 24, 18);
        col.Controls.Add(Theme.Label(L.T($"המחשב {r.Name} מבקש לקבל את הכונן {session.MountPoint}.", $"{r.Name} asks for the drive {session.MountPoint}."),
            Theme.Heading, Theme.Text, 560));
        col.Controls.Add(Theme.Label(L.T(
            "אם תעביר, המחשב הזה יעבור לקריאה בלבד והמחשב השני יוכל לשנות קבצים. תמיד אפשר לבקש את הכונן בחזרה.",
            "If you hand it over, this PC becomes read-only and the other PC can change files. You can always ask for it back."),
            Theme.Body, Theme.Text, 560));
        int pending = session.Status.PendingFiles, open = session.Engine.FilesOpenForWriting;
        if (pending > 0)
            col.Controls.Add(Theme.Label(L.T($"• {pending} קבצים עוד עולים לטלגרם: הכונן יעבור אחרי שיסיימו.",
                $"• {pending} files are still uploading to Telegram: the drive moves once they finish."), Theme.Body, Theme.Text, 560));
        if (open > 0)
            col.Controls.Add(Theme.Label(L.T(
                $"⚠  {open} קבצים פתוחים כרגע לעריכה. אחרי ההעברה לא תוכל לשמור אותם כאן. שמור וסגור אותם קודם. שינויים שכבר נשמרו לא ילכו לאיבוד: הם יישלחו כשהכונן יחזור למחשב הזה.",
                $"⚠  {open} files are open for editing right now. After the handover they cannot be saved here; save and close them first. Changes already saved are not lost: they are sent when the drive comes back to this PC."),
                Theme.Body, Theme.Error, 560));
        col.Controls.Add(_countdown);
        var give = Theme.Primary(L.T("העבר את הכונן", "Hand it over"));
        var refuse = Theme.Secondary(L.T("סרב", "Refuse"));
        give.Click += (_, _) => { Approved = true; Close(); };
        refuse.Click += (_, _) => { Approved = false; Close(); };
        var row = Theme.Row();
        row.Margin = new Padding(0, 12, 0, 0);
        row.Controls.AddRange([give, refuse]);
        col.Controls.Add(row);
        Controls.Add(col);

        Approved = true; // closing the window without choosing = not refused
        _tick.Tick += (_, _) => UpdateCountdown();
        Shown += (_, _) => { Activate(); UpdateCountdown(); _tick.Start(); };
        FormClosed += (_, _) => _tick.Dispose();
    }

    void UpdateCountdown()
    {
        int left = (int)Math.Ceiling((_deadline - DateTime.UtcNow).TotalSeconds);
        if (left <= 0) { Approved = true; Close(); return; }
        _countdown.Text = L.T($"אם לא תענה, הכונן יעבור בעוד {left} שניות.", $"Without an answer, the drive moves in {left} seconds.");
    }
}

/// <summary>
/// On the PC that wants the drive: ask, then wait — for the drive (handed over), a refusal, or the
/// countdown (no answer: the other PC is off / asleep / away, so take it). "Take now" never waits.
/// </summary>
sealed class HandoverWaitForm : Form
{
    readonly DriveSession _session;
    readonly Label _state = Theme.Label("", Theme.Body, Theme.Text, 560);
    readonly Button _takeNow = Theme.Primary(L.T("קח עכשיו", "Take it now"));
    readonly Button _cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
    readonly CancellationTokenSource _cts = new();
    readonly string _holder;
    HandoverRequest? _mine;
    bool _finished, _taking;

    public HandoverWaitForm(DriveSession session)
    {
        _session = session;
        _holder = session.Sync.LeaseHolder ?? L.T("המחשב השני", "the other PC");
        Ui.Prepare(this);
        Text = L.T($"DriveGram – בקשת הכונן {session.MountPoint}", $"DriveGram – asking for {session.MountPoint}");
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var col = Theme.Column();
        col.Padding = new Padding(24, 18, 24, 18);
        col.Controls.Add(Theme.Label(L.T($"מבקש את הכונן מ־{_holder}", $"Asking {_holder} for the drive"), Theme.Heading, Theme.Text, 560));
        col.Controls.Add(_state);
        col.Controls.Add(Theme.Label(L.T(
            "\"קח עכשיו\" לא מחכה: אם במחשב השני יש קבצים שעוד לא עלו, הם יגיעו כשהכונן יחזור אליו (ואם שניכם שיניתם אותו קובץ, יישמרו שתי הגרסאות).",
            "\"Take it now\" does not wait: files the other PC has not uploaded yet arrive when the drive goes back to it (if you both changed the same file, both versions are kept)."),
            Theme.Small, Theme.Muted, 560));
        var row = Theme.Row();
        row.Margin = new Padding(0, 12, 0, 0);
        row.Controls.AddRange([_takeNow, _cancel]);
        col.Controls.Add(row);
        Controls.Add(col);

        _takeNow.Click += async (_, _) => await TakeAsync(force: true);
        _cancel.Click += (_, _) => Close();
        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, _) =>
        {
            _cts.Cancel();
            if (!_finished && _mine is { } m) _ = Task.Run(() => _session.Sync.CancelHandoverAsync(m, default));
        };
    }

    /// <summary>The drive is now writable on this PC.</summary>
    public bool Received { get; private set; }

    async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            _state.Text = L.T("שולח בקשה…", "Sending the request…");
            _mine = await Task.Run(() => _session.Sync.RequestHandoverAsync(ct));
            var asked = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && !_finished)
            {
                var mine = _mine;
                var (req, lease) = await Task.Run(() => _session.Sync.HandoverStatusAsync(mine, ct));
                if (_taking) return;
                if (lease?.Dev == _session.Db.DeviceId) { await TakeAsync(force: false); return; }
                long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                switch (req?.State)
                {
                    case "refused" when req.Id == mine.Id:
                        _state.ForeColor = Theme.Error;
                        _state.Text = L.T($"{_holder} סירב להעביר את הכונן.", $"{_holder} refused to hand the drive over.");
                        _takeNow.Text = L.T("קח בכל זאת", "Take it anyway");
                        _cancel.Text = L.T("סגור", "Close");
                        return;
                    case "finishing" when req.Id == mine.Id:
                        if (nowUnix - req.Updated > SyncService.HandoverStall.TotalSeconds)
                        {
                            _state.Text = L.T($"{_holder} הפסיק לענות. לוקח את הכונן…", $"{_holder} stopped answering. Taking the drive…");
                            await TakeAsync(force: true);
                            return;
                        }
                        _state.Text = req.Pending > 0
                            ? L.T($"{_holder} מסכים, ומסיים להעלות {req.Pending} קבצים. הכונן יעבור מיד אחר כך.",
                                  $"{_holder} agreed and is finishing {req.Pending} uploads. The drive moves right after.")
                            : L.T($"{_holder} מסכים ומעביר את הכונן…", $"{_holder} agreed and is handing the drive over…");
                        break;
                    default:
                        int left = (int)Math.Ceiling((SyncService.HandoverAnswerTime - (DateTime.UtcNow - asked)).TotalSeconds);
                        if (left <= 0)
                        {
                            _state.Text = L.T($"אין תשובה מ־{_holder}. לוקח את הכונן…", $"No answer from {_holder}. Taking the drive…");
                            await TakeAsync(force: true);
                            return;
                        }
                        _state.Text = L.T($"ממתין לתשובה מ־{_holder}… אם לא תגיע תשובה, הכונן יעבור לכאן בעוד {left} שניות.",
                                          $"Waiting for {_holder} to answer… without an answer, the drive moves here in {left} seconds.");
                        break;
                }
                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (HandoverBusyException ex)
        {
            _state.ForeColor = Theme.Error;
            _state.Text = L.T($"המחשב {ex.Other} כבר ביקש את הכונן. נסה שוב בעוד דקה.", $"{ex.Other} already asked for the drive. Try again in a minute.");
        }
        catch (Exception ex)
        {
            _state.ForeColor = Theme.Error;
            _state.Text = TelegramErrors.Text(ex);
        }
    }

    async Task TakeAsync(bool force)
    {
        if (_taking) return;
        _taking = true;
        _takeNow.Enabled = false;
        try
        {
            await Task.Run(() => _session.Sync.TakeDriveAsync(force, _cts.Token));
            if (_session.Sync.Writable)
            {
                _finished = true;
                Received = true;
                Close();
                return;
            }
            _state.ForeColor = Theme.Error;
            _state.Text = L.T("לא הצלחתי לקחת את הכונן. נסה שוב.", "Could not take the drive. Try again.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _state.ForeColor = Theme.Error;
            _state.Text = TelegramErrors.Text(ex);
        }
        finally
        {
            _taking = false;
            if (!IsDisposed) _takeNow.Enabled = true;
        }
    }
}
