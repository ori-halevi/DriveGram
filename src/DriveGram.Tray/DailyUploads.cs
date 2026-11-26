using System.Text.Json;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>
/// How much this PC uploaded to Telegram today (all drives), and the user's optional limits: a notice
/// above an amount (also when what waits to upload is about to pass it), and a daily cap that pauses
/// uploads until midnight (files wait safely on this PC meanwhile). Kept across restarts.
/// </summary>
sealed class DailyUploads
{
    sealed class Saved
    {
        public double AlertGb { get; set; }
        public double CapGb { get; set; }
        public string Day { get; set; } = "";
        public long Bytes { get; set; }
        public string AlertedDay { get; set; } = "";
        public string AboutToDay { get; set; } = "";
        public string CapToldDay { get; set; } = "";
        public string CapLiftedDay { get; set; } = "";
        /// <summary>The default notice was set once (a user who turns it off later keeps it off).</summary>
        public bool AlertDefaultSet { get; set; }
    }

    /// <summary>The daily notice is on by default, at this much.</summary>
    public const double DefaultAlertGb = 20;

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
        NewDayIfNeeded();
    }

    static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public long TodayBytes { get { NewDayIfNeeded(); return _s.Bytes; } }
    public double AlertGb => _s.AlertGb;
    public double CapGb => _s.CapGb;
    static long Gb(double gb) => (long)(gb * (1L << 30));

    /// <summary>The daily cap was reached today (and not lifted by the user): uploads wait for tomorrow.</summary>
    public bool CapReached => _s.CapGb > 0 && _s.Bytes >= Gb(_s.CapGb) && _s.CapLiftedDay != Today;

    public void SetLimits(double alertGb, double capGb)
    {
        // a limit that changed is judged afresh today; one left as it was keeps today's state
        // (e.g. "upload anyway today" is not undone by saving the window again)
        if (Math.Max(0, alertGb) != _s.AlertGb) _s.AlertedDay = _s.AboutToDay = "";
        if (Math.Max(0, capGb) != _s.CapGb) _s.CapToldDay = _s.CapLiftedDay = "";
        _s.AlertGb = Math.Max(0, alertGb);
        _s.CapGb = Math.Max(0, capGb);
        Save();
    }

    /// <summary>"Upload anyway today": the cap is lifted until midnight.</summary>
    public void LiftCapForToday()
    {
        _s.CapLiftedDay = Today;
        Save();
    }

    void NewDayIfNeeded()
    {
        if (_s.Day == Today) return;
        _s.Day = Today;
        _s.Bytes = 0;
        _dirty = true;
    }

    /// <summary>
    /// Every tick: add what each running drive uploaded since the last tick, and say what to tell the user
    /// (null: nothing). waiting = bytes still to upload on every drive.
    /// </summary>
    public string? Tick(IEnumerable<(object Key, long Uploaded)> drives, long waiting)
    {
        NewDayIfNeeded();
        var alive = new HashSet<object>();
        foreach (var (key, up) in drives)
        {
            alive.Add(key);
            if (_seen.TryGetValue(key, out var before) && up > before) { _s.Bytes += up - before; _dirty = true; }
            _seen[key] = up;
        }
        foreach (var gone in _seen.Keys.Where(k => !alive.Contains(k)).ToList()) _seen.Remove(gone);
        if (_dirty && Environment.TickCount64 - _savedAt > 30_000) Save();

        string? say = null;
        if (_s.CapGb > 0 && _s.Bytes >= Gb(_s.CapGb) && _s.CapLiftedDay != Today && _s.CapToldDay != Today)
        {
            _s.CapToldDay = Today;
            if (_s.AlertGb > 0 && _s.AlertGb <= _s.CapGb) _s.AlertedDay = _s.AboutToDay = Today; // said by the cap notice
            Save();
            say = L.T($"היום עלו לטלגרם {Ui.Size(_s.Bytes)}, וזו התקרה היומית שבחרת. ההעלאות מושהות עד חצות, והקבצים מחכים בינתיים בבטחה במחשב הזה. אפשר להמשיך בכל זאת מלשונית \"העברות\" בחלון של DriveGram.",
                $"{Ui.Size(_s.Bytes)} went up to Telegram today: the daily cap you chose. Uploads are paused until midnight, and the files wait safely on this PC meanwhile. You can go on anyway from the \"Transfers\" tab of the DriveGram window.");
        }
        else if (_s.AlertGb > 0 && _s.Bytes >= Gb(_s.AlertGb) && _s.AlertedDay != Today)
        {
            _s.AlertedDay = _s.AboutToDay = Today;
            Save();
            say = L.T($"היום עלו לטלגרם כבר {Ui.Size(_s.Bytes)} (ההתראה שלך: {_s.AlertGb:0.#} GB).",
                $"{Ui.Size(_s.Bytes)} already went up to Telegram today (your notice: {_s.AlertGb:0.#} GB).");
        }
        else if (_s.AlertGb > 0 && _s.Bytes < Gb(_s.AlertGb) && _s.Bytes + waiting >= Gb(_s.AlertGb) && _s.AboutToDay != Today
                 && !CapReached) // paused by the cap: what waits does not go up today
        {
            _s.AboutToDay = Today;
            Save();
            say = L.T($"מה שממתין עכשיו להעלאה ({Ui.Size(waiting)}) יביא את ההעלאות של היום מעל {_s.AlertGb:0.#} GB (עד עכשיו היום: {Ui.Size(_s.Bytes)}).",
                $"What waits to upload now ({Ui.Size(waiting)}) takes today's uploads over {_s.AlertGb:0.#} GB (so far today: {Ui.Size(_s.Bytes)}).");
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
