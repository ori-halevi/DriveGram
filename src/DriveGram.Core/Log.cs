namespace DriveGram;

/// <summary>
/// Minimal thread-safe file logger (plus optional console echo). One file per day, at most 20 MB each
/// (then "-2", "-3"…), files older than 14 days are deleted, and a message repeated over and over (an
/// offline retry loop) is written once with a count instead of thousands of times.
/// </summary>
public static class Log
{
    const long MaxFileBytes = 20L << 20;
    const int KeepDays = 14;

    static readonly object Gate = new();
    static StreamWriter? _file;
    static string? _dir;
    static DateTime _day;
    static int _part;
    static string? _lastKey;
    static int _repeats;
    static DateTime _lastAt;

    public static bool Console { get; set; }
    public static bool Verbose { get; set; }

    public static void Open(string directory)
    {
        Directory.CreateDirectory(directory);
        lock (Gate)
        {
            _dir = directory;
            OpenFor(DateTime.Now.Date, 1);
        }
        try
        {
            foreach (var f in Directory.GetFiles(directory, "drivegram-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void OpenFor(DateTime day, int part)
    {
        _file?.Dispose();
        _day = day;
        _part = part;
        var name = part == 1 ? $"drivegram-{day:yyyy-MM-dd}.log" : $"drivegram-{day:yyyy-MM-dd}-{part}.log";
        _file = new StreamWriter(new FileStream(Path.Combine(_dir!, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public static void Debug(string msg) { if (Verbose) Write("DBG", msg); }
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex is null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var now = DateTime.Now;
        lock (Gate)
        {
            var key = level + msg;
            if (key == _lastKey && now - _lastAt < TimeSpan.FromMinutes(10))
            {
                _repeats++;
                return;
            }
            if (_repeats > 0) Emit(now, "INF", $"(previous message repeated {_repeats} more times)");
            _lastKey = key;
            _repeats = 0;
            _lastAt = now;
            Emit(now, level, msg);
        }
    }

    static void Emit(DateTime now, string level, string msg)
    {
        var line = $"{now:HH:mm:ss.fff} {level} {msg}";
        if (_file is not null)
        {
            try
            {
                if (now.Date != _day) OpenFor(now.Date, 1);
                else if (_file.BaseStream.Length > MaxFileBytes) OpenFor(_day, _part + 1);
                _file.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        if (Console && (level != "DBG" || Verbose)) System.Console.Error.WriteLine(line);
    }
}
