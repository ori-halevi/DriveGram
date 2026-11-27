using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// One drive's space on THIS PC: what it keeps here now, how much it may keep, and freeing it. A drive's
/// own matter, so it lives in the drive's own window and not in the program's settings (where it would be
/// one number for drives that may deserve very different ones — a drive of films and a drive of documents).
/// </summary>
sealed class DriveSpaceForm : Form
{
    readonly AppState _state;
    readonly DriveSession? _session;
    readonly NumericUpDown _limit = new()
    {
        Minimum = 1, Maximum = 10000, DecimalPlaces = 0, Width = 90, Font = Theme.Body,
        RightToLeft = RightToLeft.No, Margin = new Padding(0, 2, 10, 6),
    };
    readonly Label _now = Theme.Label("", Theme.Body, Theme.Text, 560);
    readonly Label _prepared = Theme.Label("", Theme.Small, Theme.Muted, 560);
    readonly Button _free;

    /// <summary>The limit the user left in the box (GB), once the window closed with "Save".</summary>
    public double LimitGb => (double)_limit.Value;

    public DriveSpaceForm(AppState state, DriveSession? session, Action freeNow)
    {
        _state = state;
        _session = session;
        Ui.Prepare(this);
        Text = L.T($"DriveGram – מקום במחשב הזה: {Ui.DriveTitle(state)}", $"DriveGram – space on this PC: {state.DisplayName}");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var col = Theme.Column();
        col.Controls.Add(Theme.Label(L.T(
            "קבצים שאתה פותח מהכונן נשמרים כאן כעותק זמני, כדי שבפעם הבאה הם ייפתחו מיד ובלי להוריד שוב. כשנגמר המקום שהקצית, הישנים ביותר נמחקים לבד — הקבצים עצמם תמיד שמורים בטלגרם.",
            "A file you open from the drive is kept here as a temporary copy, so next time it opens at once with no download. When the space you allow runs out, the oldest copies are deleted by themselves — the files themselves are always safe in Telegram."),
            Theme.Body, Theme.Text, 560));
        col.Controls.Add(_now);
        col.Controls.Add(_prepared);

        var row = Theme.Row();
        row.Margin = new Padding(0, 14, 0, 0);
        row.Controls.Add(Theme.Label(L.T("עד:", "Up to:"), Theme.Body, Theme.Text, 80).Wears(fore: Slot.Text));
        row.Controls.Add(_limit);
        row.Controls.Add(Theme.Label("GB", Theme.Body, Theme.Text, 60).Wears(fore: Slot.Text));
        col.Controls.Add(row);
        col.Controls.Add(Theme.Label(L.T(
            "מומלץ 10 GB. יותר מקום פירושו פחות הורדות חוזרות; פחות מקום משאיר יותר מקום פנוי בדיסק.",
            "10 GB is a good default. More space means fewer downloads again; less space leaves more room on your disk."),
            Theme.Small, Theme.Muted, 560));
        col.Controls.Add(Theme.Label(L.T(
            "קבצים שהכנת במיוחד (\"הכן במחשב הזה\") לא נספרים כאן ולא נמחקים לבד — רק אתה מפנה אותם. גם קבצים שעוד ממתינים להעלאה לטלגרם לא נספרים כאן: הם חייבים להישאר עד שיעלו.",
            "Files you prepared on purpose (\"Prepare on this PC\") are not counted here and are never deleted by themselves — only you free them. Files still waiting to upload to Telegram are not counted either: they must stay until they are up."),
            Theme.Small, Theme.Muted, 560));

        _free = Theme.Secondary(L.T("פנה מקום עכשיו…", "Free up space now…"));
        // the limit the user just typed is kept too: pressing "free up space now" is not "cancel"
        _free.Click += (_, _) => { DialogResult = DialogResult.OK; freeNow(); Close(); };
        var ok = Theme.Primary(L.T("שמור", "Save"));
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = Theme.Secondary(L.T("ביטול", "Cancel"));
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange([ok, cancel, _free]);
        col.Controls.Add(buttons);

        _limit.Value = (decimal)Math.Clamp(state.Config.CacheQuotaGb, 1, 10000);
        Theme.Fill(this, col, new Padding(20, 18, 20, 14));
        Shown += async (_, _) => await MeasureAsync();
    }

    /// <summary>What the drive keeps here now. A connected drive knows at once; a disconnected one is
    /// measured from its folder, in the background (thousands of small files).</summary>
    async Task MeasureAsync()
    {
        if (_session is { } s)
        {
            long prepared = s.Engine.PreparedBytes;
            Show(Math.Max(0, s.Status.CacheBytes - prepared), prepared);
            return;
        }
        _now.Text = L.T("בודק כמה מקום הכונן תופס כאן…", "Checking how much space the drive takes here…");
        long temporary = -1, kept = 0;
        try
        {
            var state = _state;
            (temporary, kept) = await Task.Run(() => (OfflineCache.TemporaryBytes(state),
                OfflineCache.Prepared(state).Sum(p => (long)(p.Size * p.Fraction))));
        }
        catch (Exception ex) { Log.Debug($"drive space: {ex.Message}"); }
        if (IsDisposed) return;
        if (temporary < 0)
        {
            _now.Text = L.T("לא הצלחתי לבדוק עכשיו כמה מקום הכונן תופס כאן.", "Could not check how much space the drive takes here right now.");
            _prepared.Text = "";
            return;
        }
        Show(temporary, kept);
    }

    void Show(long temporary, long prepared)
    {
        _now.Text = L.T($"עותקים זמניים כרגע: {Ui.Size(temporary)}", $"Temporary copies right now: {Ui.Size(temporary)}");
        _prepared.Text = prepared > 0
            ? L.T($"בנוסף, קבצים שהכנת תופסים {Ui.Size(prepared)} (לא נספרים במגבלה).",
                  $"Files you prepared take another {Ui.Size(prepared)} (not counted against the limit).")
            : "";
    }
}
