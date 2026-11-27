using System.Text.Json;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>
/// How much this PC uploaded to Telegram in the LAST 24 HOURS (all drives together), and the user's
/// optional limits: a notice above an amount, and a cap that holds the uploads back.
///
/// A rolling day, not a calendar one: what went up 24 hours ago leaves the count by itself, so 5 GB sent
/// at 9 in the evening frees itself at 9 the next evening — the user never waits for midnight, and the
/// limit means the same thing whatever hour it is. Counted in quarter-hour slots (96 of them), kept
/// across restarts.
/// </summary>
sealed class DailyUploads
{
    sealed class Saved
    {
        public double AlertGb { get; set; }
        public double CapGb { get; set; }
        /// <summary>Bytes per quarter-hour slot (the key is the slot's number); older than a day are dropped.</summary>
        public Dictionary<string, long> Slots { get; set; } = [];
        /// <summary>The notice / the cap were already told for THIS rise above the limit (they are armed
        /// again once the count falls back under it).</summary>
        public bool Alerted { get; set; }
        public bool AboutToTold { get; set; }
        public bool CapTold { get; set; }
        /// <summary>"Upload anyway": the cap is ignored until the count falls back below it.</summary>
        public bool CapLifted { get; set; }
        /// <summary>The default notice was set once (a user who turns it off later keeps it off).</summary>
        public bool AlertDefaultSet { get; set; }
    }

    /// <summary>The notice is on by default, at this much.</summary>
    public const double DefaultAlertGb = 20;

    const int SlotMinutes = 15;
    const int Slots = 24 * 60 / SlotMinutes; // a whole day

    static string FilePath => Path.Combine(AppState.Root, "upload-limits.json");
    Saved _s = new();
    readonly Dictionary<object, long> _seen = new(); // per running drive: its upload counter last time
    long _savedAt;
    bool _dirty;

    public DailyUploads()
    {
        try { if (File.Exists(FilePath)) _s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { Log.Warn($"upload limits: {ex.Message}"); }
        if (!_s.AlertDefaultSet)
        {
            // on by default; a user who had already chosen a number keeps it
            if (_s.AlertGb <= 0) _s.AlertGb = DefaultAlertGb;
            _s.AlertDefaultSet = true;
            _dirty = true;
        }
        Trim();
    }

    static long SlotOf(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMinutes / SlotMinutes;
    static DateTime SlotEnds(long slot) => DateTime.UnixEpoch.AddMinutes((slot + 1) * SlotMinutes);

    /// <summary>Forget what is older than a day (that is what "rolling" means).</summary>
    void Trim()
    {
        long oldest = SlotOf(DateTime.UtcNow) - Slots + 1;
        var gone = _s.Slots.Keys.Where(k => !long.TryParse(k, out var slot) || slot < oldest).ToList();
        foreach (var k in gone) _s.Slots.Remove(k);
        if (gone.Count > 0) _dirty = true;
    }

    /// <summary>Bytes uploaded in the last 24 hours.</summary>
    public long RecentBytes { get { Trim(); return _s.Slots.Values.Sum(); } }

    public double AlertGb => _s.AlertGb;
    public double CapGb => _s.CapGb;
    static long Gb(double gb) => (long)(gb * (1L << 30));

    /// <summary>The cap holds the uploads back right now (reached, and not let through by the user).</summary>
    public bool CapReached => _s.CapGb > 0 && !_s.CapLifted && RecentBytes >= Gb(_s.CapGb);

    /// <summary>
    /// When the count will fall back under the cap by itself, if nothing more goes up: the moment the
    /// oldest uploads leave the 24-hour window. Null when the cap is not reached.
    /// </summary>
    public DateTime? CapFreesAt()
    {
        if (_s.CapGb <= 0) return null;
        long cap = Gb(_s.CapGb), left = RecentBytes;
        if (left < cap) return null;
        foreach (var slot in _s.Slots.Keys.Select(k => long.TryParse(k, out var v) ? v : 0).OrderBy(v => v))
        {
            left -= _s.Slots[slot.ToString()];
            if (left < cap) return SlotEnds(slot).AddHours(24).ToLocalTime();
        }
        return null;
    }

    /// <summary>"in about 3 hours (at 21:15)" — how long the cap still holds.</summary>
    public string FreesInWords()
    {
        if (CapFreesAt() is not { } when) return "";
        var left = when - DateTime.Now;
        var at = L.Ltr(when.ToString("HH:mm"));
        if (left <= TimeSpan.FromMinutes(20)) return L.T($"בעוד דקות ספורות (בערך ב־{at})", $"in a few minutes (around {at})");
        if (left < TimeSpan.FromHours(1)) return L.T($"בעוד פחות משעה (בערך ב־{at})", $"in under an hour (around {at})");
        int hours = (int)Math.Round(left.TotalHours);
        return L.N(hours, $"בעוד כשעה (בערך ב־{at})", $"בעוד כ־{{0}} שעות (בערך ב־{at})",
            $"in about an hour (around {at})", $"in about {{0}} hours (around {at})");
    }

    public void SetLimits(double alertGb, double capGb)
    {
        // a limit that changed is judged afresh; one left as it was keeps what was already told
        // (e.g. "upload anyway" is not undone by saving the window again)
        if (Math.Max(0, alertGb) != _s.AlertGb) _s.Alerted = _s.AboutToTold = false;
        if (Math.Max(0, capGb) != _s.CapGb) { _s.CapTold = false; _s.CapLifted = false; }
        _s.AlertGb = Math.Max(0, alertGb);
        _s.CapGb = Math.Max(0, capGb);
        Save();
    }

    /// <summary>"Upload anyway": the cap is ignored until the count falls back under it by itself.</summary>
    public void LiftCap()
    {
        _s.CapLifted = true;
        Save();
    }

    /// <summary>
    /// Every tick: add what each running drive uploaded since the last tick, and say what to tell the user
    /// (null: nothing). waiting = bytes still to upload on every drive.
    /// </summary>
    public string? Tick(IEnumerable<(object Key, long Uploaded)> drives, long waiting)
    {
        Trim();
        var alive = new HashSet<object>();
        var slot = SlotOf(DateTime.UtcNow).ToString();
        foreach (var (key, up) in drives)
        {
            alive.Add(key);
            if (_seen.TryGetValue(key, out var before) && up > before)
            {
                _s.Slots[slot] = _s.Slots.GetValueOrDefault(slot) + (up - before);
                _dirty = true;
            }
            _seen[key] = up;
        }
        foreach (var gone in _seen.Keys.Where(k => !alive.Contains(k)).ToList()) _seen.Remove(gone);
        if (_dirty && Environment.TickCount64 - _savedAt > 30_000) Save();

        long recent = _s.Slots.Values.Sum();
        // under the limit again: the notice and the cap are armed for the next rise, and "upload anyway" is over
        if (_s.CapGb > 0 && recent < Gb(_s.CapGb) && (_s.CapTold || _s.CapLifted)) { _s.CapTold = _s.CapLifted = false; Save(); }
        if (_s.AlertGb > 0 && recent < Gb(_s.AlertGb) && (_s.Alerted || _s.AboutToTold)) { _s.Alerted = _s.AboutToTold = false; Save(); }

        string? say = null;
        if (_s.CapGb > 0 && recent >= Gb(_s.CapGb) && !_s.CapLifted && !_s.CapTold)
        {
            _s.CapTold = true;
            if (_s.AlertGb > 0 && _s.AlertGb <= _s.CapGb) _s.Alerted = _s.AboutToTold = true; // said by the cap notice
            Save();
            say = L.T($"ב־24 השעות האחרונות עלו לטלגרם {Ui.Size(recent)}, וזו התקרה שבחרת. ההעלאות מושהות, והקבצים מחכים בינתיים בבטחה במחשב הזה. הן ימשיכו מעצמן {FreesInWords()}, ואפשר להמשיך כבר עכשיו מלשונית \"העברות\".",
                $"{Ui.Size(recent)} went up to Telegram in the last 24 hours: the cap you chose. Uploads are paused, and the files wait safely on this PC meanwhile. They go on by themselves {FreesInWords()}, and you can go on now from the \"Transfers\" tab.");
        }
        else if (_s.AlertGb > 0 && recent >= Gb(_s.AlertGb) && !_s.Alerted)
        {
            _s.Alerted = _s.AboutToTold = true;
            Save();
            say = L.T($"ב־24 השעות האחרונות עלו לטלגרם כבר {Ui.Size(recent)} (ההתראה שלך: {_s.AlertGb:0.#} GB).",
                $"{Ui.Size(recent)} went up to Telegram in the last 24 hours (your notice: {_s.AlertGb:0.#} GB).");
        }
        else if (_s.AlertGb > 0 && recent < Gb(_s.AlertGb) && recent + waiting >= Gb(_s.AlertGb) && !_s.AboutToTold
                 && !CapReached) // held by the cap: what waits does not go up now
        {
            _s.AboutToTold = true;
            Save();
            say = L.T($"מה שממתין עכשיו להעלאה ({Ui.Size(waiting)}) יביא את ההעלאות של 24 השעות האחרונות מעל {_s.AlertGb:0.#} GB (עד עכשיו: {Ui.Size(recent)}).",
                $"What waits to upload now ({Ui.Size(waiting)}) takes the last 24 hours over {_s.AlertGb:0.#} GB (so far: {Ui.Size(recent)}).");
        }
        return say;
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(AppState.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_s));
            _savedAt = Environment.TickCount64;
            _dirty = false;
        }
        catch (Exception ex) { Log.Warn($"upload limits: {ex.Message}"); }
    }

    public void Flush() { if (_dirty) Save(); }
}
