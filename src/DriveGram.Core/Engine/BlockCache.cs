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
    long _bytes, _keptBytes;
    Func<string, bool>? _kept;
    bool _waitForKept; // nothing is evicted until the prepared files are known (they would go first)

    /// <summary>
    /// Entries of files prepared on this PC on purpose: never evicted and not counted against the quota
    /// (the user chose to keep them), until the predicate says otherwise. Call again when it changes.
    /// </summary>
    public void SetKept(Func<string, bool>? kept)
    {
        lock (_gate)
        {
            _kept = kept;
            _waitForKept = false;
            _keptBytes = kept is null ? 0 : _lru.Where(e => kept(e.Key)).Sum(e => e.Size);
        }
        Evict();
    }

    bool IsKept(string key) => _kept is { } k && k(key);

    /// <summary>Bytes of prepared (kept) files.</summary>
    public long KeptBytes { get { lock (_gate) return _keptBytes; } }

    public long Quota { get; set; }
    public long Bytes { get { lock (_gate) return _bytes; } }

    /// <param name="waitForKept">Evict nothing until <see cref="SetKept"/> is called: on a restart the
    /// prepared files are not known yet, and as the oldest entries they would be evicted first.</param>
    public BlockCache(string dir, long quota, bool waitForKept = false)
    {
        Quota = quota;
        _waitForKept = waitForKept;
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

    /// <summary>Is this chunk's plaintext cached (without counting as a use)?</summary>
    public bool TryGetPlainQuiet(string chunkId)
    {
        lock (_gate) return _map.ContainsKey("p/" + chunkId);
    }

    /// <summary>Drop every cached copy (all of it is stored in Telegram too). A read that was about to use
    /// one simply fetches it again. Returns the bytes freed.</summary>
    public long Clear()
    {
        var victims = new List<string>();
        long freed = 0;
        lock (_gate)
        {
            // files prepared on purpose stay (they are freed one by one, by the user)
            for (var n = _lru.First; n is not null;)
            {
                var next = n.Next;
                if (!IsKept(n.Value.Key))
                {
                    victims.Add(n.Value.Path);
                    freed += n.Value.Size;
                    _map.Remove(n.Value.Key);
                    _lru.Remove(n);
                }
                n = next;
            }
            _bytes -= freed;
        }
        foreach (var v in victims) TryDelete(v);
        return freed;
    }

    /// <summary>Drop these entries now (a prepared file the user freed).</summary>
    public long Drop(Func<string, bool> which)
    {
        var victims = new List<string>();
        long freed = 0;
        lock (_gate)
        {
            for (var n = _lru.First; n is not null;)
            {
                var next = n.Next;
                if (which(n.Value.Key))
                {
                    victims.Add(n.Value.Path);
                    freed += n.Value.Size;
                    if (IsKept(n.Value.Key)) _keptBytes -= n.Value.Size;
                    _map.Remove(n.Value.Key);
                    _lru.Remove(n);
                }
                n = next;
            }
            _bytes -= freed;
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
            bool kept = IsKept(key);
            if (_map.Remove(key, out var old)) { _lru.Remove(old); _bytes -= old.Value.Size; if (kept) _keptBytes -= old.Value.Size; }
            _map[key] = _lru.AddFirst(new Entry(key, path, size));
            _bytes += size;
            if (kept) _keptBytes += size;
        }
        Evict();
    }

    void Remove(string key)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var node))
            {
                _lru.Remove(node);
                _bytes -= node.Value.Size;
                if (IsKept(key)) _keptBytes -= node.Value.Size;
            }
        }
    }

    void Evict()
    {
        var victims = new List<string>();
        lock (_gate)
        {
            if (_waitForKept) return;
            // oldest first; prepared files are skipped and do not count against the quota
            var passed = new List<LinkedListNode<Entry>>();
            for (var n = _lru.Last; n is not null && _bytes - _keptBytes > Quota;)
            {
                var prev = n.Previous;
                if (!IsKept(n.Value.Key))
                {
                    _lru.Remove(n);
                    _map.Remove(n.Value.Key);
                    _bytes -= n.Value.Size;
                    victims.Add(n.Value.Path);
                }
                else passed.Add(n);
                n = prev;
            }
            // kept for good: moved to the other end (after the walk), so the next eviction does not pass them again
            foreach (var k in passed) { _lru.Remove(k); _lru.AddFirst(k); }
        }
        foreach (var v in victims) TryDelete(v);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
