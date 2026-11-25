using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DriveGram.Host;

namespace DriveGram.Tray;

/// <summary>
/// The line between Explorer and DriveGram: the small shell extension (overlay icons and the right-click
/// menu) asks here how much of a file is on this PC, and asks to prepare or free files. A named pipe of
/// this Windows session, open to the current user only. One request per connection, UTF-8 text:
///   L \t path                 → percent on this PC (0-100), or -1 (not a DriveGram file)
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

    public void Start()
    {
        for (int i = 0; i < 4; i++) _ = Task.Run(ServeAsync);
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
            case 'L': return paths.Length == 1 ? Percent(paths[0]).ToString() : "-1";
            case 'M': return Menu(paths);
            case 'P': Prepare(paths); Forget(paths); return "ok";
            case 'F': Free(paths); Forget(paths); return "ok";
            default: return "";
        }
    }

    void Forget(IEnumerable<string> paths)
    {
        lock (_recent) foreach (var p in paths) _recent.Remove(p);
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
            info = IsPreparing(s, id) ? L.T("DriveGram: מוריד למחשב הזה…", "DriveGram: downloading to this PC…")
                : s.Engine.IsPrepared(id) && p >= 100 ? L.T("DriveGram: מוכן במחשב הזה ✓", "DriveGram: ready on this PC ✓")
                : L.T($"DriveGram: {p}% במחשב הזה", $"DriveGram: {p}% on this PC");
        }
        else info = L.T($"DriveGram: {all.Count} קבצים", $"DriveGram: {all.Count} files");
        bool anyToPrepare = all.Any(f => !f.Session.Engine.IsPrepared(f.Id) || f.Session.Engine.LocalFraction(f.Id) < 0.999);
        bool anyToFree = all.Any(f => f.Session.Engine.IsPrepared(f.Id) || f.Session.Engine.LocalFraction(f.Id) > 0.001);
        var prepare = anyToPrepare ? L.T("הכן במחשב הזה (הורד את כולו)", "Prepare on this PC (download all of it)") : "";
        var free = anyToFree ? L.T("פנה מהמחשב הזה", "Free from this PC") : "";
        return string.Join('\x1f', info, prepare, free);
    }

    public void Dispose() => _cts.Cancel();
}
