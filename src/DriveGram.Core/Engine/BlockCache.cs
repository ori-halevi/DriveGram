namespace DriveGram.Engine;

/// <summary>
/// On-disk LRU cache with one byte quota, holding two kinds of entries:
///  - "w": 1 MiB windows of stored (still ENCRYPTED) chunk messages, from remote reads;
///  - "p": whole PLAINTEXT chunks that were written on this PC and then committed, so reading
///         back a file you just copied in costs no download.
/// Survives restarts (rebuilt from the directory, ordered by last access time).
/// </summary>
public sealed class BlockCache
{
    sealed record Entry(string Key, string Path, long Size);

    readonly string _wDir, _pDir;
    readonly object _gate = new();
    readonly LinkedList<Entry> _lru = new(); // First = most recently used
    readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    long _bytes;

    public long Quota { get; set; }
    public long Bytes { get { lock (_gate) return _bytes; } }

    public BlockCache(string dir, long quota)
    {
        Quota = quota;
        _wDir = Path.Combine(dir, "w");
        _pDir = Path.Combine(dir, "p");
        Directory.CreateDirectory(_wDir);
        Directory.CreateDirectory(_pDir);
        var files = new DirectoryInfo(_wDir).EnumerateFiles().Select(f => ("w/" + f.Name, f))
            .Concat(new DirectoryInfo(_pDir).EnumerateFiles().Select(f => ("p/" + f.Name, f)))
            .Where(x =>
            {
                if (!x.f.Name.EndsWith(".tmp")) return true;
                try { x.f.Delete(); } catch (IOException) { } // left by a crash mid-write
                return false;
            })
            .OrderByDescending(x => x.f.LastAccessTimeUtc);
        foreach (var (key, f) in files)
        {
            _map[key] = _lru.AddLast(new Entry(key, f.FullName, f.Length));
            _bytes += f.Length;
        }
        Evict();
    }

    static string WKey(int msg, int win) => $"w/{msg}_{win}";

    public void SetQuota(long quota)
    {
        lock (_gate) Quota = quota;
        Evict();
    }

    public bool HasWindow(int msg, int win)
    {
        lock (_gate) return _map.ContainsKey(WKey(msg, win));
    }

    public void RemovePlain(string chunkId)
    {
        var key = "p/" + chunkId;
        string? path;
        lock (_gate) path = _map.TryGetValue(key, out var n) ? n.Value.Path : null;
        Remove(key);
        if (path is not null) TryDelete(path);
    }

    public void RemoveWindow(int msg, int win)
    {
        var key = WKey(msg, win);
        string? path;
        lock (_gate) path = _map.TryGetValue(key, out var n) ? n.Value.Path : null;
        Remove(key);
        if (path is not null) TryDelete(path);
    }

    public byte[]? TryGetWindow(int msg, int win)
    {
        var path = Touch(WKey(msg, win));
        if (path is null) return null;
        try { return File.ReadAllBytes(path); }
        catch (IOException) { Remove(WKey(msg, win)); return null; }
    }

    public void PutWindow(int msg, int win, byte[] data)
    {
        var key = WKey(msg, win);
        var path = Path.Combine(_wDir, $"{msg}_{win}");
        try
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { return; } // cache is best-effort
        Add(key, path, data.Length);
    }

    public string? TryGetPlain(string chunkId) => Touch("p/" + chunkId);

    /// <summary>Drop every cached copy (all of it is stored in Telegram too). A read that was about to use
    /// one simply fetches it again. Returns the bytes freed.</summary>
    public long Clear()
    {
        List<string> victims;
        long freed;
        lock (_gate)
        {
            victims = _lru.Select(e => e.Path).ToList();
            freed = _bytes;
            _lru.Clear();
            _map.Clear();
            _bytes = 0;
        }
        foreach (var v in victims) TryDelete(v);
        return freed;
    }

    /// <summary>Take ownership of a staged plaintext piece as the cached copy of a committed chunk.</summary>
    public void AdoptPlain(string chunkId, string sourcePath)
    {
        var key = "p/" + chunkId;
        var path = Path.Combine(_pDir, chunkId);
        long size;
        try
        {
            size = new FileInfo(sourcePath).Length;
            File.Move(sourcePath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(sourcePath); return; }
        Add(key, path, size);
    }

    string? Touch(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Path;
        }
    }

    void Add(string key, string path, long size)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var old)) { _lru.Remove(old); _bytes -= old.Value.Size; }
            _map[key] = _lru.AddFirst(new Entry(key, path, size));
            _bytes += size;
        }
        Evict();
    }

    void Remove(string key)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var node)) { _lru.Remove(node); _bytes -= node.Value.Size; }
        }
    }

    void Evict()
    {
        var victims = new List<string>();
        lock (_gate)
        {
            while (_bytes > Quota && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Size;
                victims.Add(last.Value.Path);
            }
        }
        foreach (var v in victims) TryDelete(v);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
