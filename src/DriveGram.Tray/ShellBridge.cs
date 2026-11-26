using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>
/// The line between Explorer and DriveGram: the small shell extension (overlay icons and the right-click
/// menu) asks here what state a file is in (saved in Telegram yet? how much of it is on this PC?), and asks
/// to prepare or free files. A named pipe of this Windows session, open to the current user only. One
/// request per connection, UTF-8 text:
///   S \t path                 → one letter: P paused upload, U waiting to upload / uploading (not in
///                               Telegram yet); K prepared, F all here, H partly here, 0 not here (saved
///                               in Telegram); - not a DriveGram file
///   L \t path                 → percent on this PC (0-100), or -1 (the 1.3.0 extension, still loaded by
///                               Explorer until the next sign-in)
///   M \t path \x1f path ...   → the menu lines: info \x1f prepare \x1f free (an empty line: no such item)
///   P \t paths  /  F \t paths → prepare / free those files; the answer is "ok"
/// </summary>
sealed class ShellBridge : IDisposable
{
    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint session);

    public static string PipeName
    {
        get
        {
            ProcessIdToSessionId((uint)Environment.ProcessId, out var s);
            return $"DriveGram.Shell.{s}";
        }
    }

    /// <summary>A DriveGram file at a Windows path: its session and id (null: not one).</summary>
    public required Func<string, (DriveSession Session, string Id)?> Resolve { get; init; }
    public required Action<IReadOnlyList<string>> Prepare { get; init; }
    public required Action<IReadOnlyList<string>> Free { get; init; }
    public required Func<DriveSession, string, bool> IsPreparing { get; init; }

    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, (int Percent, long At)> _recent = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (char State, long At)> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        for (int i = 0; i < 8; i++) _ = Task.Run(ServeAsync); // Explorer asks for many files at once
    }

    const int MaxRequest = 1 << 20, MaxPaths = 200; // Explorer sends at most 200 paths (the menu's own limit)
    volatile bool _toldTaken;

    async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                var buf = new byte[64 * 1024];
                using var ms = new MemoryStream();
                do
                {
                    int n = await pipe.ReadAsync(buf, _cts.Token);
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxRequest) throw new InvalidDataException("request too long");
                } while (!pipe.IsMessageComplete);
                var answer = Answer(Encoding.UTF8.GetString(ms.ToArray()));
                var bytes = Encoding.UTF8.GetBytes(answer);
                await pipe.WriteAsync(bytes, _cts.Token);
                await pipe.FlushAsync(_cts.Token);
                try { pipe.WaitForPipeDrain(); } catch (IOException) { }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { } // Explorer stopped waiting (it asks again): back to listening at once
            catch (UnauthorizedAccessException ex)
            {
                // the name is held by a pipe of another Windows user: the Explorer extension refuses to talk to it
                // (it checks the owner), so there are no icons until it goes away
                if (!_toldTaken) { _toldTaken = true; Log.Warn($"shell bridge: the pipe name is taken by another user ({ex.Message})"); }
                try { await Task.Delay(5000, _cts.Token); } catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                Log.Debug($"shell bridge: {ex.Message}");
                try { await Task.Delay(200, _cts.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    string Answer(string request)
    {
        int tab = request.IndexOf('\t');
        if (tab != 1) return "";
        var paths = request[2..].Split('\x1f', StringSplitOptions.RemoveEmptyEntries).Take(MaxPaths).ToArray();
        switch (request[0])
        {
            case 'S': return paths.Length == 1 ? State(paths[0]).ToString() : "-";
            // the 1.3.0 extension (loaded until the next sign-in) shows a green check for 100%: never for a
            // file that is not in Telegram yet (answered from the same short memory as 'S')
            case 'L':
                if (paths.Length != 1) return "-1";
                return State(paths[0]) switch { 'K' or 'F' => "100", 'H' => Percent(paths[0]).ToString(), '0' => "0", _ => "-1" };
            case 'M': return Menu(paths);
            case 'P': Prepare(paths); Forget(paths); return "ok";
            case 'F': Free(paths); Forget(paths); return "ok";
            default: return "";
        }
    }

    /// <summary>These files' answers are asked again (they changed state).</summary>
    public void Forget(IEnumerable<string> paths)
    {
        lock (_recent) foreach (var p in paths) { _recent.Remove(p); _states.Remove(p); }
    }

    // files Explorer was told are "not in Telegram yet": redrawn once they are (a rename is sent within a
    // second or two, often between two of the tray's looks, which would never notice it)
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DriveSession Session, string Id)> _shownWaiting = new(StringComparer.OrdinalIgnoreCase);
    // answers that took so long Explorer had given up (no mark shown): redrawn soon, when it is quiet again
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _late = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths shown as waiting that are in Telegram now (they need a redraw); off the UI thread.</summary>
    public List<string> TakeNoLongerWaiting(DriveSession s)
    {
        var done = new List<string>();
        foreach (var (path, f) in _shownWaiting)
        {
            if (f.Session != s) continue;
            bool waiting;
            try { waiting = s.Engine.IsWaitingToUpload(f.Id); } catch { waiting = false; }
            if (!waiting && _shownWaiting.TryRemove(path, out _)) done.Add(path);
        }
        return done;
    }

    /// <summary>Paths whose answer came too late for Explorer.</summary>
    public List<string> TakeLate()
    {
        var late = _late.Keys.ToList();
        foreach (var p in late) _late.TryRemove(p, out _);
        return late;
    }

    /// <summary>The file's state for Explorer's icons (one icon per state); asked for every file shown,
    /// so kept for a moment.</summary>
    char State(string path)
    {
        long now = Environment.TickCount64;
        lock (_recent)
        {
            if (_states.TryGetValue(path, out var r) && now - r.At < 1500) return r.State;
            if (_states.Count > 5000) _states.Clear();
        }
        char state = '-';
        try
        {
            if (Resolve(path) is { } f)
            {
                var e = f.Session.Engine;
                if (e.IsWaitingToUpload(f.Id))
                {
                    // orange only for pieces held by a pause (a rename or a journal entry is not paused)
                    state = e.IsUploadPending(f.Id) && e.IsPaused(f.Id) ? 'P' : 'U';
                    if (_shownWaiting.Count < 20_000) _shownWaiting[path] = (f.Session, f.Id);
                }
                else
                {
                    int p = Percent(path);
                    state = p >= 100 ? (e.IsPrepared(f.Id) ? 'K' : 'F') : p > 0 ? 'H' : '0';
                }
            }
        }
        catch (Exception ex) { Log.Debug($"shell bridge: {path}: {ex.Message}"); }
        lock (_recent) _states[path] = (state, now);
        if (Environment.TickCount64 - now > 140 && _late.Count < 5000) _late[path] = 0; // Explorer gave up waiting
        return state;
    }

    bool WaitingToUpload(string path)
    {
        try { return Resolve(path) is { } f && f.Session.Engine.IsWaitingToUpload(f.Id); }
        catch { return true; } // unknown: never "saved"
    }

    /// <summary>Percent of the file on this PC; asked for every file Explorer shows, so kept for a moment.</summary>
    int Percent(string path)
    {
        long now = Environment.TickCount64;
        lock (_recent)
        {
            if (_recent.TryGetValue(path, out var r) && now - r.At < 3000) return r.Percent;
            if (_recent.Count > 5000) _recent.Clear();
        }
        int percent = -1;
        try
        {
            if (Resolve(path) is { } f)
            {
                double x = f.Session.Engine.LocalFraction(f.Id);
                percent = x >= 0.999 ? 100 : x <= 0.001 ? 0 : Math.Clamp((int)Math.Round(x * 100), 1, 99);
            }
        }
        catch (Exception ex) { Log.Debug($"shell bridge: {path}: {ex.Message}"); }
        lock (_recent) _recent[path] = (percent, now);
        return percent;
    }

    string Menu(string[] paths)
    {
        var files = paths.Select(p => Resolve(p)).ToList();
        if (files.Count == 0 || files.Any(f => f is null)) return "";
        var all = files.Select(f => f!.Value).ToList();
        string info;
        if (all.Count == 1)
        {
            var (s, id) = all[0];
            int p = Percent(paths[0]);
            var e = s.Engine;
            bool contentSaved = e.IsContentInTelegram(id), pending = e.IsUploadPending(id);
            bool paused = pending && e.IsPaused(id);
            info = !contentSaved
                    ? e.HasSavedVersion(id)
                        ? paused ? L.T("DriveGram: השינוי מושהה ועוד לא עלה (הגרסה הקודמת שמורה בטלגרם)", "DriveGram: the change is paused, not uploaded yet (the earlier version is in Telegram)")
                            : L.T("DriveGram: השינוי עוד לא עלה לטלגרם (הגרסה הקודמת שמורה בו)", "DriveGram: the change is not in Telegram yet (the earlier version is)")
                        : paused ? L.T("DriveGram: ההעלאה מושהית, עוד לא שמור בטלגרם", "DriveGram: upload paused, not in Telegram yet")
                            : L.T("DriveGram: עוד לא שמור בטלגרם (מחכה להעלאה או עולה עכשיו)", "DriveGram: not in Telegram yet (waiting to upload, or uploading)")
                : e.IsWaitingToUpload(id) ? L.T("DriveGram: התוכן שמור בטלגרם; השם או המיקום החדש עוד לא נשלחו", "DriveGram: the content is in Telegram; the new name or place is not sent yet")
                : IsPreparing(s, id) ? L.T("DriveGram: מוריד למחשב הזה…", "DriveGram: downloading to this PC…")
                : e.IsPrepared(id) && p >= 100 ? L.T("DriveGram: שמור בטלגרם ✓ ומוכן במחשב הזה (נשאר עד שתפנה אותו)", "DriveGram: saved in Telegram ✓ and prepared on this PC (stays until you free it)")
                : e.IsPrepared(id) ? L.T($"DriveGram: ההכנה לא הושלמה ({p}%). בחר \"הכן במחשב הזה\" שוב", $"DriveGram: preparing did not finish ({p}%). Choose \"Prepare on this PC\" again")
                : p >= 100 ? L.T("DriveGram: שמור בטלגרם, וכולו גם במחשב הזה (זמני)", "DriveGram: saved in Telegram, all of it on this PC too (temporary)")
                : p > 0 ? L.T($"DriveGram: שמור בטלגרם, {p}% ממנו במחשב הזה (זמני)", $"DriveGram: saved in Telegram, {p}% of it on this PC (temporary)")
                : L.T("DriveGram: שמור בטלגרם בלבד (לא במחשב הזה)", "DriveGram: only in Telegram (not on this PC)");
        }
        else info = L.T($"DriveGram: {all.Count} קבצים", $"DriveGram: {all.Count} files");
        // a file whose content is not in Telegram yet is only here: nothing to prepare, nothing that may be freed
        // (a rename still on its way does not matter: the content is safe)
        var saved = all.Where(f => f.Session.Engine.IsContentInTelegram(f.Id)).ToList();
        bool anyToPrepare = saved.Any(f => !f.Session.Engine.IsPrepared(f.Id) || f.Session.Engine.LocalFraction(f.Id) < 0.999);
        bool anyToFree = saved.Any(f => f.Session.Engine.IsPrepared(f.Id) || f.Session.Engine.LocalFraction(f.Id) > 0.001);
        var prepare = anyToPrepare ? L.T("הכן במחשב הזה (הורד את כולו)", "Prepare on this PC (download all of it)") : "";
        var free = anyToFree ? L.T("פנה מהמחשב הזה", "Free from this PC") : "";
        return string.Join('\x1f', info, prepare, free);
    }

    public void Dispose() => _cts.Cancel();
}
