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

    /// <summary>True: hand the drive over. False: refused (or withdrawn).</summary>
    public bool Approved { get; private set; }

    /// <summary>The request disappeared while the question was open (cancelled there, or the drive was taken).</summary>
    public bool Withdrawn { get; private set; }

    readonly Func<Task<bool>> _stillOpen;

    // shown on top of whatever the user is doing, but without taking the keyboard from it
    protected override bool ShowWithoutActivation => true;
    int _polls;
    bool _polling;

    public HandoverAskForm(DriveSession session, HandoverRequest r, Func<Task<bool>> stillOpen)
    {
        _stillOpen = stillOpen;
        Ui.Prepare(this);
        Text = L.T("DriveGram – בקשה לקבל את הכונן", "DriveGram – a request for the drive");
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Window;
        // the asking PC counts 60 s from the moment this question is on screen ("seen"); answer before that
        _deadline = DateTime.UtcNow + SyncService.HandoverAnswerTime - TimeSpan.FromSeconds(10);

        var col = Theme.Column();
        var asker = r.Name.Length > 40 ? r.Name[..40] + "…" : r.Name;
        col.Controls.Add(Theme.Label(L.T($"המחשב {L.Ltr(asker)} מבקש לקבל את הכונן {L.Ltr(session.MountPoint)}.", $"{asker} asks for the drive {session.MountPoint}."),
            Theme.Heading, Theme.Text, 560));
        col.Controls.Add(Theme.Label(L.T(
            "רק מחשב אחד יכול לשנות קבצים בכונן בכל רגע. אם תעביר, המחשב הזה יעבור לקריאה בלבד והמחשב השני יוכל לשנות קבצים. תמיד אפשר לבקש את הכונן בחזרה.",
            "Only one PC at a time can change files on the drive. If you hand it over, this PC becomes read-only and the other PC can change files. You can always ask for it back."),
            Theme.Body, Theme.Text, 560));
        int pending = session.Status.PendingFiles, open = session.Engine.FilesOpenForWriting;
        if (pending > 0)
            col.Controls.Add(Theme.Label(L.N(pending, "• קובץ אחד עוד עולה לטלגרם: הכונן יעבור אחרי שיסיים.", "• {0} קבצים עוד עולים לטלגרם: הכונן יעבור אחרי שיסיימו.",
                "• 1 file is still uploading to Telegram: the drive moves once it finishes.", "• {0} files are still uploading to Telegram: the drive moves once they finish."), Theme.Body, Theme.Text, 560));
        if (open > 0)
            col.Controls.Add(Theme.Label(L.N(open,
                "⚠  קובץ אחד פתוח כרגע לעריכה. אחרי ההעברה לא תוכל לשמור אותו כאן. שמור וסגור אותו קודם. שינויים שכבר נשמרו לא ילכו לאיבוד: הם יישלחו כשהכונן יחזור למחשב הזה.",
                "⚠  {0} קבצים פתוחים כרגע לעריכה. אחרי ההעברה לא תוכל לשמור אותם כאן. שמור וסגור אותם קודם. שינויים שכבר נשמרו לא ילכו לאיבוד: הם יישלחו כשהכונן יחזור למחשב הזה.",
                "⚠  1 file is open for editing right now. After the handover it cannot be saved here; save and close it first. Changes already saved are not lost: they are sent when the drive comes back to this PC.",
                "⚠  {0} files are open for editing right now. After the handover they cannot be saved here; save and close them first. Changes already saved are not lost: they are sent when the drive comes back to this PC."),
                Theme.Body, Theme.Error, 560));
        col.Controls.Add(_countdown);
        var give = Theme.Secondary(L.T("העבר את הכונן", "Hand it over"));
        var refuse = Theme.Primary(L.T("השאר את הכונן כאן", "Keep the drive here"));
        give.Click += (_, _) => { Approved = true; Close(); };
        refuse.Click += (_, _) => { Approved = false; Close(); };
        var row = Theme.Row();
        row.Margin = new Padding(0, 12, 0, 0);
        row.Controls.AddRange([refuse, give]);
        CancelButton = refuse; // Esc keeps it; Enter never gives it away (focus is not taken from the user's app)
        col.Controls.Add(row);
        Theme.Fill(this, col, new Padding(24, 18, 24, 18));

        Approved = false; // closing the window = refusing (only silence until the countdown ends = yes)
        _tick.Tick += (_, _) => UpdateCountdown();
        Shown += (_, _) => { UpdateCountdown(); _tick.Start(); };
        FormClosed += (_, _) => _tick.Dispose();
    }

    void UpdateCountdown()
    {
        int left = (int)Math.Ceiling((_deadline - DateTime.UtcNow).TotalSeconds);
        if (left <= 0) { Approved = true; Close(); return; }
        if (++_polls % 3 == 0 && !_polling) _ = CheckStillOpenAsync();
        _countdown.Text = L.T("אם לא תענה, הכונן יעבור ", "Without an answer, the drive moves ") + Seconds(left)
            + L.T(". סגירת החלון משאירה את הכונן כאן.", ". Closing this window keeps the drive here.");
    }

    /// <summary>"בעוד 5 שניות" / "בעוד שנייה" ("in 5 seconds" / "in 1 second").</summary>
    internal static string Seconds(int left) =>
        L.N(left, "בעוד שנייה", "בעוד {0} שניות", "in 1 second", "in {0} seconds");

    async Task CheckStillOpenAsync()
    {
        _polling = true;
        try
        {
            if (!await Task.Run(_stillOpen) && !IsDisposed)
            {
                Withdrawn = true;
                Approved = false;
                Close();
            }
        }
        catch { } // offline for a moment: keep asking
        finally { _polling = false; }
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
    readonly string _holder, _fromHolder, _atHolder; // "X" / "מ־X" / "ב־X" (Hebrew joins the preposition to the word)
    HandoverRequest? _mine;
    bool _finished, _taking, _refusedShown;

    public HandoverWaitForm(DriveSession session)
    {
        _session = session;
        var holder = session.Sync.LeaseHolder;
        _holder = holder is null ? L.T("המחשב השני", "the other PC") : L.T(L.Ltr(holder), holder);
        _fromHolder = holder is null ? "מהמחשב השני" : $"מ־{L.Ltr(holder)}";
        _atHolder = holder is null ? "במחשב השני" : $"ב־{L.Ltr(holder)}";
        Ui.Prepare(this);
        Text = L.T($"DriveGram – בקשת הכונן {L.Ltr(session.MountPoint)}", $"DriveGram – asking for {session.MountPoint}");
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Window;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T($"מבקש את הכונן {_fromHolder}", $"Asking {_holder} for the drive"), Theme.Heading, Theme.Text, 560));
        col.Controls.Add(_state);
        col.Controls.Add(Theme.Label(L.T(
            "לקיחת הכונן בלי לחכות: אם במחשב השני יש קבצים שעוד לא עלו, הם יגיעו כשהכונן יחזור אליו (ואם שניכם שיניתם אותו קובץ, יישמרו שתי הגרסאות).",
            "Taking the drive without waiting: files the other PC has not uploaded yet arrive when the drive goes back to it (if you both changed the same file, both versions are kept)."),
            Theme.Small, Theme.Muted, 560));
        var row = Theme.Row();
        row.Margin = new Padding(0, 12, 0, 0);
        row.Controls.AddRange([_takeNow, _cancel]);
        col.Controls.Add(row);
        Theme.Fill(this, col, new Padding(24, 18, 24, 18));

        _takeNow.Click += async (_, _) =>
        {
            if (_refusedShown && Ui.Ask(L.T($"{_holder} סירב. לקחת את הכונן בכל זאת? מי שעובד שם לא יוכל לשמור שינויים בכונן.",
                    $"{_holder} refused. Take the drive anyway? Whoever works there will not be able to save changes on the drive."),
                    icon: MessageBoxIcon.Warning, def: MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await TakeAsync(force: true);
        };
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
        }
        catch (OperationCanceledException) { return; }
        catch (HandoverBusyException ex)
        {
            _state.ForeColor = Theme.Error;
            _state.Text = L.T($"המחשב {L.Ltr(ex.Other)} כבר ביקש את הכונן. נסה שוב בעוד דקה.", $"{ex.Other} already asked for the drive. Try again in a minute.");
            return;
        }
        catch (Exception ex)
        {
            _state.ForeColor = Theme.Error;
            _state.Text = TelegramErrors.Text(ex);
            return;
        }

        var countFrom = DateTime.UtcNow;     // answer time: from the request, or from "seen" once the other PC shows it
        string? lastState = null;
        long lastUpdated = -1;
        var lastProgress = DateTime.UtcNow;  // judged by THIS PC's clock (the other PC's clock may differ)
        while (!ct.IsCancellationRequested && !_finished)
        {
            try
            {
                var mine = _mine!;
                var (req, lease) = await Task.Run(() => _session.Sync.HandoverStatusAsync(mine, ct));
                if (_taking) return;
                if (lease?.Dev == _session.Db.DeviceId) { await TakeAsync(force: false); return; }
                var state = req?.Id == mine.Id ? req.State : "asked";
                if (state != lastState)
                {
                    if (state == "seen") countFrom = DateTime.UtcNow;
                    lastState = state;
                    lastProgress = DateTime.UtcNow;
                }
                if (req?.Id == mine.Id && req.Updated != lastUpdated)
                {
                    if (state == "seen" && lastUpdated != -1) countFrom = DateTime.UtcNow; // the question was shown again
                    lastUpdated = req.Updated;
                    lastProgress = DateTime.UtcNow;
                }
                _state.ForeColor = Theme.Text;
                switch (state)
                {
                    case "refused" or "failed":
                        _state.ForeColor = Theme.Error;
                        _state.Text = state == "refused"
                            ? L.T($"{_holder} סירב להעביר את הכונן.", $"{_holder} refused to hand the drive over.")
                            : L.T($"{_holder} לא הצליח להעביר את הכונן (אולי אין לו חיבור).", $"{_holder} could not hand the drive over (perhaps it is offline).");
                        _takeNow.Text = L.T("קח בכל זאת", "Take it anyway");
                        _refusedShown = state == "refused";
                        _cancel.Text = L.T("סגור", "Close");
                        break; // keep watching: the drive may still arrive
                    case "finishing":
                        if (DateTime.UtcNow - lastProgress > SyncService.HandoverStall)
                        {
                            _state.Text = L.T($"{_holder} הפסיק לענות. לוקח את הכונן…", $"{_holder} stopped answering. Taking the drive…");
                            await TakeAsync(force: true);
                            return;
                        }
                        _state.Text = req!.Pending > 0
                            ? L.T($"{_holder} מסכים, ומסיים להעלות ", $"{_holder} agreed and is finishing ")
                              + L.N(req.Pending, "קובץ אחד", "{0} קבצים", "1 upload", "{0} uploads")
                              + L.T(". הכונן יעבור מיד אחר כך.", ". The drive moves right after.")
                            : L.T($"{_holder} מסכים ומעביר את הכונן…", $"{_holder} agreed and is handing the drive over…");
                        break;
                    default: // asked / seen
                        int left = (int)Math.Ceiling((SyncService.HandoverAnswerTime - (DateTime.UtcNow - countFrom)).TotalSeconds);
                        if (left <= 0)
                        {
                            _state.Text = L.T($"אין תשובה {_fromHolder}. לוקח את הכונן…", $"No answer from {_holder}. Taking the drive…");
                            await TakeAsync(force: true);
                            return;
                        }
                        _state.Text = state == "seen"
                            ? L.T($"השאלה מוצגת עכשיו {_atHolder}… בלי תשובה, הכונן יעבור לכאן ",
                                  $"The question is now on the screen of {_holder}… without an answer, the drive moves here ") + HandoverAskForm.Seconds(left) + "."
                            : L.T($"ממתין לתשובה {_fromHolder}… אם לא תגיע תשובה, הכונן יעבור לכאן ",
                                  $"Waiting for {_holder} to answer… without an answer, the drive moves here ") + HandoverAskForm.Seconds(left) + ".";
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // a short network problem: say so, and keep trying
                _state.ForeColor = Theme.Error;
                _state.Text = TelegramErrors.Text(ex);
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
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
