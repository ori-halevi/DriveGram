namespace DriveGram;

/// <summary>Minimal thread-safe file logger (plus optional console echo).</summary>
public static class Log
{
    static readonly object Gate = new();
    static StreamWriter? _file;
    public static bool Console { get; set; }
    public static bool Verbose { get; set; }

    public static void Open(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"drivegram-{DateTime.Now:yyyy-MM-dd}.log");
        lock (Gate)
        {
            _file?.Dispose();
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
        }
    }

    public static void Debug(string msg) { if (Verbose) Write("DBG", msg); }
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex is null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        lock (Gate)
        {
            _file?.WriteLine(line);
            if (Console && (level != "DBG" || Verbose)) System.Console.Error.WriteLine(line);
        }
    }
}
