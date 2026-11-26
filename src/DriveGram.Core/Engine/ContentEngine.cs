using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using DriveGram.Crypto;
using DriveGram.Index;
using DriveGram.Remote;

namespace DriveGram.Engine;

public sealed class EngineOptions
{
    /// <summary>Max bytes of local, not-yet-evictable file data. Writers slow down above it.</summary>
    public long StagingQuota { get; init; } = 8L << 30;
    public long CacheQuota { get; init; } = 10L << 30;
    public int UploadConcurrency { get; init; } = 2;
    /// <summary>A full piece untouched this long, in a file still open, is uploaded early.</summary>
    public TimeSpan SealIdle { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Delay between the last close of a file and its commit (lets save-via-rename settle).</summary>
    public TimeSpan CommitDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>How long a writer waits for staging room WITHOUT any upload progress before the write fails
    /// with "disk full" (offline, signed out, stuck).</summary>
    public TimeSpan BackpressureTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>The longest one write may wait while uploads do move (the copy just slows to the upload
    /// speed). Windows cancels a file-system request after 5 minutes, so it stays below that.</summary>
    public TimeSpan MaxWriteWait { get; init; } = TimeSpan.FromSeconds(270);
    /// <summary>Space always left free on the local disk (staging and cache shrink to keep it).</summary>
    public long MinFreeDisk { get; init; } = 3L << 30;
}

/// <summary>The data is only in Telegram, and this reader may not trigger a download.</summary>
public sealed class DataNotLocalException() : IOException("the data is not on this PC");

/// <summary>Preparing a file needs more free space on this PC than there is.</summary>
public sealed class NotEnoughSpaceException(long needed, long free) : IOException($"not enough space on this PC: {needed:N0} bytes needed, {free:N0} free")
{
    public long Needed => needed;
    public long Free => free;
}

public sealed record EngineStatus(int PendingFiles, long StagedBytes, int UploadsInFlight, long BytesUploaded,
    long BytesDownloaded, long CacheBytes);

public enum UploadState { Waiting, Uploading, Paused, Writing }

/// <summary>One file waiting to upload or uploading: Done and Total count the parts that must go up.</summary>
/// <param name="Saved">An earlier version is already in Telegram: this upload is a change made here.</param>
public sealed record UploadItem(string Id, string Name, long Size, long Done, long Total, UploadState State, bool Saved = false);

/// <summary>
/// File content on top of immutable remote chunks.
///
/// A file = list of fixed-size chunks (the last one shorter). Writes never touch remote data:
/// the affected chunk becomes a local "piece" (staging/{node}/{idx}), materialized from the
/// remote chunk on first write (copy-on-write per chunk, so editing 1 byte of a 50 GB file
/// downloads and re-uploads one chunk, not the file). Pieces upload in the background — early,
/// while a big copy is still running, so local disk use stays bounded (backpressure) — and when
/// the last writer closes, a ContentOp swaps the file to the new chunk list. Only then is the
/// change journaled, so no other PC ever sees a file whose bytes are still only on this PC.
///
/// Everything needed to resume (pieces, dirty rows, pending files, uploaded-but-unreferenced
/// chunks) is on disk, so a crash or reboot mid-upload continues where it stopped.
/// </summary>
public sealed class ContentEngine : IAsyncDisposable
{
    sealed class NodeState
    {
        public readonly object Lock = new();
        public int OpenWriters;
        public long Gen;
        /// <summary>Last modification stamp per piece. Stamps come from one engine-wide counter that only
        /// grows and are never cleared or reused, so a slow upload that captured an old stamp can never
        /// match a later write (resetting per-piece counters after a commit made that possible).</summary>
        public readonly Dictionary<int, long> PieceGen = new();
        public readonly Dictionary<int, long> LastWrite = new();
        public long LastAnyWrite; // TickCount64 of the last write of any piece (kept after commit)
        public long LastReadEnd = -1;
        /// <summary>Pieces uploaded once and then changed again while the file stayed open.</summary>
        public readonly HashSet<int> Rewritten = new();
        public int Committing;
        /// <summary>A commit is already scheduled for a write that came with no writer handle open.</summary>
        public int OrphanCommit;
    }

    abstract record Seg(int Idx, int InOff, int DstOff, int Len);
    sealed record StagedSeg(int Idx, int InOff, int DstOff, int Len, string Path) : Seg(Idx, InOff, DstOff, Len);
    sealed record ZeroSeg(int Idx, int InOff, int DstOff, int Len) : Seg(Idx, InOff, DstOff, Len);
    sealed record RemoteSeg(int Idx, int InOff, int DstOff, int Len, ChunkLoc Loc) : Seg(Idx, InOff, DstOff, Len);

    readonly IndexDb _db;
    readonly IRemote _remote;
    readonly Keys _keys;
    readonly BlockCache _cache;
    readonly ChunkReader _reader;
    readonly string _staging;
    readonly EngineOptions _o;
    readonly int CS;

    readonly ConcurrentDictionary<string, NodeState> _nodes = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _chunkLocks = new();
    readonly SemaphoreSlim _uploadSlots;
    readonly SemaphoreSlim _pieceSlots; // pieces held in memory (read, hashed, encrypted, uploading)
    readonly ConcurrentDictionary<string, int> _commitFailures = new();
    readonly Channel<string> _commitQueue = Channel.CreateUnbounded<string>();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _room = new(0);
    readonly AutoResetEvent _roomSignal = new(false); // "staging space was freed": wakes a waiting writer
    readonly List<Task> _loops = [];
    long _stagingBytes;
    long _stamp;
    long _ticks;
    long _bytesUploaded;
    int _uploadsInFlight;

    public ContentEngine(IndexDb db, IRemote remote, Keys keys, string stateDir, EngineOptions? options = null)
    {
        _db = db;
        _remote = remote;
        _keys = keys;
        _o = options ?? new EngineOptions();
        CS = keys.ChunkSize;
        _staging = Path.Combine(stateDir, "staging");
        Directory.CreateDirectory(_staging);
        _cache = new BlockCache(Path.Combine(stateDir, "cache"), _o.CacheQuota, waitForKept: true) // Start → LoadKept
        {
            PreviewQuota = PreviewQuota,
        };
        _reader = new ChunkReader(remote, _cache, keys);
        _uploadSlots = new SemaphoreSlim(Math.Max(1, _o.UploadConcurrency));
        _pieceSlots = new SemaphoreSlim(Math.Max(1, _o.UploadConcurrency) + 2);
    }

    // ── pause ─────────────────────────────────────────────────────────────────
    readonly ConcurrentDictionary<string, byte> _pausedNodes = new();
    volatile bool _pausedAll;

    /// <summary>No new upload starts while true (what is already sending finishes). Files wait on this PC.</summary>
    public bool PausedAll
    {
        get => _pausedAll;
        set
        {
            if (!value) _allowed.Clear();
            bool changed = _pausedAll != value;
            _pausedAll = value;
            if (value && Interlocked.Read(ref _stagingBytes) >= _o.StagingQuota) _overQuota = true;
            Interlocked.Exchange(ref _pausedStagedAt, 0); // what paused files hold is measured again
            if (changed) PauseChanged();
        }
    }

    // replaced (and completed) on every pause change: the files waiting for a pause wake up together
    TaskCompletionSource _pauseSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    void PauseChanged() => Interlocked.Exchange(ref _pauseSignal, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    // files the user let through while everything is paused ("resume" on just them): the rest, and files
    // added later, stay paused
    readonly ConcurrentDictionary<string, byte> _allowed = new();
    public bool IsPaused(string id) => !_forceUploads && ((_pausedAll && !_allowed.ContainsKey(id)) || _pausedNodes.ContainsKey(id));
    /// <summary>This file was paused by itself (not the whole drive).</summary>
    public bool IsPausedByItself(string id) => _pausedNodes.ContainsKey(id);

    volatile bool _forceUploads;
    /// <summary>Handing the drive over: every file goes up now, paused or not. The pauses themselves are kept
    /// (and hold again if the handover does not happen).</summary>
    public bool ForceUploads
    {
        get => _forceUploads;
        set { _forceUploads = value; PauseChanged(); if (value) foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node); }
    }
    public void Pause(string id) => Pause([id]);
    /// <summary>Pause these files (one save and one wake-up for all of them).</summary>
    public void Pause(IEnumerable<string> ids)
    {
        foreach (var id in ids) { _allowed.TryRemove(id, out _); _pausedNodes[id] = 0; }
        SavePaused();
        if (Interlocked.Read(ref _stagingBytes) >= _o.StagingQuota) _overQuota = true; // its staged pieces stay
        Interlocked.Exchange(ref _pausedStagedAt, 0);
        PauseChanged();
    }
    /// <summary>Resume one file; while everything is paused, only this file goes on.</summary>
    public void Resume(string id) => Resume([id]);
    public void Resume(IEnumerable<string> ids)
    {
        bool changed = false;
        foreach (var id in ids)
        {
            changed |= _pausedNodes.TryRemove(id, out _);
            if (_pausedAll) _allowed[id] = 0;
        }
        if (changed) SavePaused();
        Interlocked.Exchange(ref _pausedStagedAt, 0);
        PauseChanged();
    }
    public void ResumeAll()
    {
        _pausedAll = false;
        bool had = !_pausedNodes.IsEmpty;
        _pausedNodes.Clear();
        _allowed.Clear();
        if (had) SavePaused();
        PauseChanged();
    }
    /// <summary>A file that is gone (its upload cancelled): no pause state left for it.</summary>
    public void Forget(string id)
    {
        ForgetQuietly(id);
        PauseChanged();
    }

    // a file gone or saved: its pause is over (nothing wakes: nothing waits for it any more)
    void ForgetQuietly(string id)
    {
        if (_pausedNodes.TryRemove(id, out _)) SavePaused();
        _allowed.TryRemove(id, out _);
    }

    // files paused one by one stay paused after a restart (paused.txt, next to prepared.txt)
    string PausedFile => Path.Combine(Path.GetDirectoryName(_staging)!, "paused.txt");
    readonly object _pausedFileGate = new();

    int _saveQueued;

    // many pauses in a row (a mass delete, a selection) are written once, not on the caller's thread
    void SavePaused()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        _ = Task.Run(SavePausedNow);
    }

    void SavePausedNow()
    {
        lock (_pausedFileGate)
        {
            Interlocked.Exchange(ref _saveQueued, 0); // a change from now on writes again
            try { File.WriteAllLines(PausedFile, _pausedNodes.Keys); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"paused files: {ex.Message}"); }
        }
    }

    void LoadPaused()
    {
        try
        {
            if (!File.Exists(PausedFile)) return;
            // only files still waiting to upload (the others went up, or are gone)
            var waiting = _db.PendingNodes().ToHashSet();
            foreach (var id in File.ReadAllLines(PausedFile))
                if (id.Length > 0 && waiting.Contains(id)) _pausedNodes[id] = 0;
            SavePausedNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"paused files: {ex.Message}"); }
    }

    /// <summary>Waits while the file is paused. False: the file is gone (deleted meanwhile): nothing to upload.</summary>
    async Task<bool> WaitWhilePausedAsync(string id, CancellationToken ct)
    {
        // wakes on a pause change; a file deleted while paused stops waiting too (looked at once a minute:
        // tens of thousands of paused files must not keep the index busy)
        while (IsPaused(id))
        {
            var signal = Volatile.Read(ref _pauseSignal).Task;
            if (!IsPaused(id)) break;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = Task.Delay(60_000, stop.Token);
            var woke = await Task.WhenAny(signal, timeout);
            stop.Cancel(); // no timer left behind per wake-up
            ct.ThrowIfCancellationRequested();
            if (woke == timeout && _db.Get(id) is null) { ForgetQuietly(id); return false; }
        }
        return true;
    }

    /// <summary>A program has the file open for writing right now.</summary>
    public bool IsWriting(string id) => _nodes.TryGetValue(id, out var st) && Volatile.Read(ref st.OpenWriters) > 0;

    /// <summary>Every file waiting to upload or uploading, oldest first.</summary>
    public List<UploadItem> Uploads()
    {
        var list = new List<UploadItem>();
        var sending = _sendingNow.Keys.ToHashSet();
        foreach (var (id, name, size, total, done, saved) in _db.PendingProgress(1000))
        {
            bool writing = _nodes.TryGetValue(id, out var st) && Volatile.Read(ref st.OpenWriters) > 0;
            var state = IsPaused(id) ? UploadState.Paused : sending.Contains(id) ? UploadState.Uploading
                : writing ? UploadState.Writing : UploadState.Waiting;
            list.Add(new UploadItem(id, name, size, done, Math.Max(total, done), state, saved));
        }
        return list;
    }

    /// <summary>Bytes still to upload (all pending files).</summary>
    public long BytesToUpload() => _db.PendingBytesLeft();

    // ── what is on this PC ────────────────────────────────────────────────────
    /// <summary>How much of a file is on this PC, 0 to 1 (waiting to upload, or in the cache).</summary>
    public double LocalFraction(string id)
    {
        var node = _db.Get(id);
        if (node is null || node.Dir) return 0;
        if (node.Size == 0) return 1;
        var dirty = _db.DirtyRows(id).ToDictionary(r => r.Idx);
        var committed = _db.CommittedChunks(id).ToDictionary(c => c.Idx);
        double here = 0;
        for (int idx = 0; idx < Count(node.Size); idx++)
        {
            int len = PieceLen(idx, node.Size);
            ChunkLoc? loc = null;
            if (dirty.TryGetValue(idx, out var d))
            {
                if (d.Staged || d.Kind is 'Z' or 'S') { here += len; continue; }
                loc = new ChunkLoc(d.ChunkId!, d.Msg, d.Size, d.Hash!);
            }
            else if (committed.TryGetValue(idx, out var c)) loc = new ChunkLoc(c.ChunkId, c.Msg, c.Size, c.Hash);
            if (loc is not { } l) continue;
            if (_cache.TryGetPlainQuiet(l.ChunkId)) { here += len; continue; }
            int wins = _reader.WindowCount(l), have = 0;
            for (int w = 0; w < wins; w++) if (_cache.HasWindow(l.Msg, w)) have++;
            here += (double)len * have / Math.Max(1, wins);
        }
        return Math.Clamp(here / node.Size, 0, 1);
    }

    // ── files prepared on this PC (kept) ─────────────────────────────────────
    readonly object _keptGate = new();
    HashSet<string> _keptIds = [];
    HashSet<int> _keptMsgs = [];
    HashSet<string> _keptChunks = [];
    string KeptFile => Path.Combine(Path.GetDirectoryName(_staging)!, "prepared.txt");

    public IReadOnlyCollection<string> PreparedFiles { get { lock (_keptGate) return _keptIds.ToList(); } }
    public bool IsPrepared(string id) { lock (_keptGate) return _keptIds.Contains(id); }
    public long PreparedBytes => _cache.KeptBytes;

    void LoadKept()
    {
        try { if (File.Exists(KeptFile)) lock (_keptGate) _keptIds = File.ReadAllLines(KeptFile).Where(l => l.Length > 0).ToHashSet(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"prepared files: {ex.Message}"); }
        if (!PruneKept()) RefreshKept();
    }

    /// <summary>Prepared files deleted for good (here, or on another PC) are no longer kept: their copies
    /// become ordinary cache again. True when something was dropped (the kept entries were refreshed).</summary>
    bool PruneKept()
    {
        List<string> ids;
        lock (_keptGate) ids = _keptIds.ToList();
        var gone = ids.Where(id => _db.Get(id) is not { Dir: false, Trashed: 0 }).ToList(); // deleted, or in the recycle bin
        if (gone.Count == 0) return false;
        lock (_keptGate) foreach (var id in gone) _keptIds.Remove(id);
        SaveKept();
        RefreshKept();
        return true;
    }

    /// <summary>The cache entries of the prepared files (again after a change to one of them).</summary>
    readonly object _refreshKeptGate = new();

    // one at a time: a refresh that started earlier must not store its older set last
    void RefreshKept()
    {
        lock (_refreshKeptGate) RefreshKeptOnce();
    }

    void RefreshKeptOnce()
    {
        var msgs = new HashSet<int>();
        var chunks = new HashSet<string>();
        List<string> ids;
        lock (_keptGate) ids = _keptIds.ToList();
        foreach (var id in ids)
        {
            foreach (var c in _db.CommittedChunks(id)) { msgs.Add(c.Msg); chunks.Add(c.ChunkId); }
            foreach (var d in _db.DirtyRows(id).Where(r => r.Kind == 'U')) { msgs.Add(d.Msg); chunks.Add(d.ChunkId!); }
        }
        lock (_keptGate) { _keptMsgs = msgs; _keptChunks = chunks; }
        _cache.SetKept(ids.Count == 0 ? null : KeptKey);
    }

    bool KeptKey(string key)
    {
        HashSet<int> msgs;
        HashSet<string> chunks;
        lock (_keptGate) { msgs = _keptMsgs; chunks = _keptChunks; }
        if (key.StartsWith("p/")) return chunks.Contains(key[2..]);
        int us = key.IndexOf('_');
        return key.StartsWith("w/") && us > 2 && int.TryParse(key.AsSpan(2, us - 2), out var m) && msgs.Contains(m);
    }

    // prepares still downloading: kept in memory only, written to prepared.txt when complete (after a crash
    // or a power cut mid-way, the part that came down is ordinary cache, not pinned for good)
    readonly HashSet<string> _preparing = [];

    void SaveKept()
    {
        List<string> ids;
        lock (_keptGate) ids = _keptIds.Where(i => !_preparing.Contains(i)).ToList();
        try
        {
            // a new file put in place at once: a reader (the tray, for a list) never blocks the write
            var tmp = KeptFile + ".tmp";
            File.WriteAllLines(tmp, ids);
            File.Move(tmp, KeptFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"prepared files: {ex.Message}"); }
    }

    /// <summary>
    /// Download a whole file to this PC now and keep it (not evicted) until it is freed, e.g. a video to
    /// watch tonight. Progress: the part of the file on this PC (0 to 1).
    /// </summary>
    // bytes that prepares running now still have to download (several at once share the same free space)
    long _prepareReserved;
    readonly object _prepareGate = new();

    public async Task PrepareAsync(string id, IProgress<double>? progress, CancellationToken ct)
    {
        var node = _db.Get(id) ?? throw new FileNotFoundException("the file is gone");
        long missing = (long)(node.Size * (1 - LocalFraction(id)));
        bool makeRoom;
        lock (_prepareGate)
        {
            // temporary copies in the cache give their place up when the disk runs low: they count as room
            long free = FreeDisk() - _o.MinFreeDisk - _prepareReserved;
            long room = Math.Max(0, free + Math.Max(0, _cache.Bytes - _cache.KeptBytes));
            if (missing > room) throw new NotEnoughSpaceException(missing, room);
            makeRoom = missing > free;
            _prepareReserved += missing;
        }
        long reserved = missing;
        if (makeRoom) FitCacheToDisk(Interlocked.Read(ref _prepareReserved)); // shrink the cache to make room
        bool kept;
        lock (_keptGate)
        {
            kept = !_keptIds.Add(id); // prepared before (e.g. "prepare" again after the file changed)
            if (!kept) _preparing.Add(id);
        }
        RefreshKept();
        bool finished = false;
        try
        {
            var locs = new List<ChunkLoc>();
            var dirty = _db.DirtyRows(id).ToDictionary(r => r.Idx);
            foreach (var c in _db.CommittedChunks(id))
                if (!dirty.ContainsKey(c.Idx)) locs.Add(new ChunkLoc(c.ChunkId, c.Msg, c.Size, c.Hash));
            foreach (var d in dirty.Values.Where(r => r.Kind == 'U' && !r.Staged)) locs.Add(new ChunkLoc(d.ChunkId!, d.Msg, d.Size, d.Hash!));
            var todo = locs.Where(l => !_cache.TryGetPlainQuiet(l.ChunkId))
                .SelectMany(l => Enumerable.Range(0, _reader.WindowCount(l)).Select(w => (l, w)))
                .Where(x => !_cache.HasWindow(x.l.Msg, x.w)).ToList();
            long perWindow = todo.Count == 0 ? 0 : Math.Max(1, missing / todo.Count);
            // progress from a count (measuring the file again every few windows costs more the bigger it is)
            double start = Math.Clamp(1 - (double)missing / Math.Max(1, node.Size), 0, 1);
            int done = 0;
            using var slots = new SemaphoreSlim(4);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = todo.Select(async x =>
            {
                await slots.WaitAsync(stop.Token);
                try
                {
                    for (int attempt = 1; ; attempt++)
                    {
                        try { await _reader.FetchWindowAsync(x.l, x.w, stop.Token); break; }
                        catch (Exception ex) when (attempt < 6 && !stop.IsCancellationRequested
                            && ex is IOException or TimeoutException or HttpRequestException or System.Net.Sockets.SocketException)
                        {
                            // a blip (Wi-Fi, a moment offline): try that window again, waiting longer each time
                            await Task.Delay(TimeSpan.FromSeconds(5 << (attempt - 1)), stop.Token);
                        }
                    }
                }
                catch (Exception) when (!stop.IsCancellationRequested) { stop.Cancel(); throw; } // it keeps failing: stop the rest
                finally { slots.Release(); }
                lock (_prepareGate) { long give = Math.Min(perWindow, reserved); reserved -= give; _prepareReserved -= give; }
                int n = Interlocked.Increment(ref done);
                if (n % 8 == 0)
                {
                    progress?.Report(start + (1 - start) * n / Math.Max(1, todo.Count));
                    // the disk filled up meanwhile (something else wrote to it): stop before it is full
                    if (FreeDisk() < _o.MinFreeDisk) FitCacheToDisk(Interlocked.Read(ref _prepareReserved)); // temporary copies first
                    if (FreeDisk() < _o.MinFreeDisk) { stop.Cancel(); throw new NotEnoughSpaceException(reserved, 0); }
                }
            }).ToList();
            try { await Task.WhenAll(tasks); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // the others were stopped (cancelled): report the failure that stopped them
                var cause = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!)
                    .FirstOrDefault(e => e is not OperationCanceledException);
                if (cause is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);
                throw;
            }
            finished = true;
            lock (_keptGate) _preparing.Remove(id);
            SaveKept();
            progress?.Report(LocalFraction(id));
        }
        finally
        {
            lock (_prepareGate) _prepareReserved -= reserved;
            // cut short (cancelled, offline, no room): what came down stays as an ordinary temporary copy,
            // not kept for good (unless it was prepared before)
            if (!finished && !kept)
            {
                lock (_keptGate) { _keptIds.Remove(id); _preparing.Remove(id); }
                RefreshKept();
            }
        }
    }

    /// <summary>Stop keeping a prepared file, and drop its local copies (it is downloaded again when opened).</summary>
    public long FreeLocal(string id)
    {
        bool was;
        lock (_keptGate) was = _keptIds.Remove(id);
        if (was) SaveKept();
        var msgs = new HashSet<int>();
        var chunks = new HashSet<string>();
        foreach (var c in _db.CommittedChunks(id)) { msgs.Add(c.Msg); chunks.Add(c.ChunkId); }
        foreach (var d in _db.DirtyRows(id).Where(r => r.Kind == 'U' && !r.Staged)) { msgs.Add(d.Msg); chunks.Add(d.ChunkId!); }
        // never what another prepared file shares (the same content saved twice)
        RefreshKept();
        long freed = _cache.Drop(k => !KeptKey(k) && (k.StartsWith("p/") ? chunks.Contains(k[2..])
            : k.IndexOf('_') is int us and > 2 && int.TryParse(k.AsSpan(2, us - 2), out var m) && msgs.Contains(m)));
        foreach (var c in chunks) _verifiedPlain.TryRemove(c, out _);
        return freed;
    }

    /// <summary>Nothing is waiting to upload or uploading (garbage collection may run).</summary>
    public bool Idle => _uploadsInFlight == 0 && _db.PendingNodes().Count == 0;

    public EngineStatus Status => new(_db.PendingNodes().Count, Interlocked.Read(ref _stagingBytes), _uploadsInFlight,
        Interlocked.Read(ref _bytesUploaded), Interlocked.Read(ref _reader.BytesDownloaded), _cache.Bytes);

    /// <summary>Recover state left by a previous run and start the background workers.</summary>
    /// <summary>
    /// Delete piece files no dirty row refers to (left by a purge, a crash between commit and caching,
    /// or a discard): they are invisible otherwise and would leak disk and inflate the staging count.
    /// </summary>
    void ReconcileStaging(bool adjustCount)
    {
        _db.Durable(); // pieces are judged against the index as it is on disk
        foreach (var dir in Directory.EnumerateDirectories(_staging))
        {
            var node = Path.GetFileName(dir);
            var st = State(node);
            lock (st.Lock)
            {
                var keep = _db.DirtyRows(node).Where(r => r.Staged).Select(r => $"{r.Idx}.piece").ToHashSet();
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (keep.Contains(Path.GetFileName(f))) continue;
                    try
                    {
                        long len = new FileInfo(f).Length;
                        File.Delete(f);
                        if (adjustCount) Interlocked.Add(ref _stagingBytes, -len);
                        Log.Debug($"removed orphaned piece {node}/{Path.GetFileName(f)}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                TryDeleteDir(dir);
            }
        }
    }

    /// <summary>Local changes a crash left without the "pending" mark are marked again (they upload, and
    /// a new file's ops wait for its content). Called before the sync starts; Start repeats it.</summary>
    public void RepairPending()
    {
        foreach (var node in _db.AllDirty().Where(r => r.Kind != 'U' || r.Staged).Select(r => r.Node).Distinct())
            if (!_db.IsPending(node) && _db.Get(node) is not null)
            {
                Log.Warn($"local changes of {node} were not marked for upload (interrupted write); uploading them");
                _db.SetPending(node);
            }
    }

    static bool PieceMatches(string path, string hash)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(fs)) == hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Start()
    {
        ReconcileStaging(adjustCount: false); // before counting: the count starts from what is really staged
        RepairPending();
        long staged = 0;
        foreach (var row in _db.AllDirty())
        {
            var path = PiecePath(row.Node, row.Idx);
            if (row.Staged && row.Kind == 'U' && row.Hash is { } want && File.Exists(path) && !PieceMatches(path, want))
            {
                // its bytes did not reach the disk before a power cut (zeros): Telegram has the real ones
                Log.Warn($"staged copy of uploaded piece {row.Node}#{row.Idx} is damaged; using Telegram's copy");
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (row.Staged && !File.Exists(path))
            {
                if (row.Kind == 'U') _db.PutDirty(row with { Staged = false });
                else
                {
                    Log.Error($"staged piece lost: {row.Node}#{row.Idx}; reverting that chunk");
                    _db.DeleteDirty(row.Node, row.Idx);
                    if (_db.CommittedChunk(row.Node, row.Idx) is null)
                        _db.PutDirty(new DirtyRow(row.Node, row.Idx, 'Z', row.Size, false));
                }
                continue;
            }
            if (row.Staged) staged += new FileInfo(path).Length;
        }
        _stagingBytes = staged;
        _overQuota = staged > _o.StagingQuota; // paused files kept staging over the quota: the disk is the limit until it drains
        foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node);
        LoadPaused(); // before the first upload starts
        LoadKept();
        _loops.Add(Task.Run(CommitLoop));
        _loops.Add(Task.Run(SealLoop));
    }

    NodeState State(string id) => _nodes.GetOrAdd(id, _ => new NodeState());
    string PiecePath(string node, int idx) => Path.Combine(_staging, node, $"{idx}.piece");
    int Count(long size) => (int)((size + CS - 1) / CS);
    int PieceLen(int idx, long size) => (int)Math.Min(CS, size - (long)idx * CS);
    static long Now => DateTime.UtcNow.ToFileTimeUtc();

    // ── open / close ─────────────────────────────────────────────────────────
    public void Opened(string id, bool writer)
    {
        if (!writer) return;
        var st = State(id);
        lock (st.Lock) st.OpenWriters++;
    }

    public void Closed(string id, bool writer)
    {
        if (!writer) return;
        var st = State(id);
        bool last;
        lock (st.Lock) last = --st.OpenWriters == 0;
        if (last && _db.IsPending(id)) ScheduleCommit(id);
    }

    void ScheduleCommit(string id) => _ = Task.Run(async () =>
    {
        try { await Task.Delay(_o.CommitDelay, _cts.Token); } catch (OperationCanceledException) { return; }
        _commitQueue.Writer.TryWrite(id);
    });

    /// <summary>
    /// After unmount no handle can be open, but Windows' cache manager closes files lazily, so some
    /// Close callbacks never arrive once the volume is gone. Forget all open-writer counts and
    /// queue every pending file, or those files would wait for a close that will never come.
    /// </summary>
    public void ReleaseAllHandles()
    {
        foreach (var (id, st) in _nodes)
        {
            lock (st.Lock)
            {
                if (st.OpenWriters == 0) continue;
                st.OpenWriters = 0;
            }
            Log.Debug($"released lingering writer handles of {id}");
        }
        foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node);
    }

    /// <summary>
    /// A file already saved in Telegram and changed here: drop the change, back to the saved version (the
    /// user cancelled its upload). False when no version is saved yet (nothing is changed then).
    /// </summary>
    public bool DropLocalChange(string id)
    {
        var st = State(id);
        Action? dropFiles = null;
        lock (st.Lock)
        {
            var node = _db.Get(id);
            var saved = _db.CommittedChunks(id);
            if (node is null || saved.Count == 0) return false;
            // one transaction: a power cut leaves either the change or the saved version, never a mix of both
            _db.InTransaction(() =>
            {
                dropFiles = DiscardLater(id); // forgets the change; its piece files go once the index is on disk
                _db.ApplyLocal(new ContentOp(id, saved.Sum(c => (long)c.Size), node.Mtime,
                    saved.Select(c => new ChunkRef(c.ChunkId, c.Msg, c.Size, c.Hash)).ToArray()));
            });
        }
        if (dropFiles is not null) { _db.Durable(); dropFiles(); }
        return true;
    }

    /// <summary>A file never saved in Telegram, removed from the drive for good ("cancel" in the uploads list).</summary>
    public void DropNewFile(string id)
    {
        var st = State(id);
        Action? dropFiles = null;
        lock (st.Lock)
            _db.InTransaction(() =>
            {
                dropFiles = DiscardLater(id); // first: the purge removes the rows it uses
                _db.ApplyLocal(new PurgeOp(id));
            });
        FreeLocal(id); // if it was prepared: no longer kept
        if (dropFiles is not null) { _db.Durable(); dropFiles(); }
    }

    /// <summary>Written to within the last `ms` (Windows may still deliver data it holds for the file).</summary>
    public bool WrittenWithin(string id, int ms) =>
        _nodes.TryGetValue(id, out var st) && Environment.TickCount64 - Volatile.Read(ref st.LastAnyWrite) < ms;

    /// <summary>Drop uncommitted local changes (the file was deleted).</summary>
    public void Discard(string id) => DiscardLater(id)?.Invoke();

    /// <summary>
    /// Forget a file's local changes now; the returned action deletes its piece files. The caller runs it
    /// only after the index change that drops the file is on disk (<see cref="IndexDb.Durable"/>): a power
    /// cut in between must not leave a file whose pieces are gone (it would come back with zeros inside).
    /// </summary>
    public Action? DiscardLater(string id)
    {
        var st = State(id);
        List<int> pieces;
        lock (st.Lock)
        {
            pieces = _db.DirtyRows(id).Where(r => r.Staged).Select(r => r.Idx).ToList();
            _db.DeleteAllDirty(id);
            _db.ClearPending(id);
            foreach (var k in st.PieceGen.Keys.ToList()) st.PieceGen[k] = Interlocked.Increment(ref _stamp);
            st.LastWrite.Clear();
            st.Gen++;
        }
        ForgetQuietly(id); // its waiting is over: deleted, replaced, or its change dropped
        if (pieces.Count == 0) { TryDeleteDir(Path.Combine(_staging, id)); return null; }
        return () =>
        {
            lock (st.Lock) foreach (var idx in pieces) if (_db.GetDirty(id, idx) is null) DeletePiece(id, idx);
            TryDeleteDir(Path.Combine(_staging, id));
        };
    }

    // ── read ─────────────────────────────────────────────────────────────────
    public int Read(string id, long offset, Span<byte> dst) => Read(id, offset, dst, allowRemote: true);

    /// <param name="allowRemote">false: only data already on this PC (staged or cached in full).</param>
    /// <param name="preview">Explorer reading for a thumbnail or details (see <see cref="BlockCache"/>).</param>
    public int Read(string id, long offset, Span<byte> dst, bool allowRemote, bool preview = false)
    {
        var st = State(id);
        for (int attempt = 0; ; attempt++)
        {
            List<Seg> plan;
            int n;
            lock (st.Lock)
            {
                var node = _db.Get(id) ?? throw new FileNotFoundException(id);
                if (offset >= node.Size) return 0;
                n = (int)Math.Min(dst.Length, node.Size - offset);
                plan = Plan(id, offset, n);
            }
            if (!allowRemote && plan.OfType<RemoteSeg>().Any(r => !ChunkHere(r.Loc)))
                throw new DataNotLocalException();
            bool sequential = Interlocked.Exchange(ref st.LastReadEnd, offset + n) == offset;
            try
            {
                Execute(plan, dst[..n], sequential, preview);
                return n;
            }
            catch (Exception ex) when (attempt == 0 && ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // a piece moved to the cache / was evicted between planning and reading: plan again
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new IOException($"could not read the file's data: {ex.Message}", ex); // not "no such file"
            }
        }
    }

    List<Seg> Plan(string id, long offset, int n)
    {
        var plan = new List<Seg>();
        int done = 0;
        while (done < n)
        {
            long pos = offset + done;
            int idx = (int)(pos / CS), inOff = (int)(pos % CS);
            int len = Math.Min(CS - inOff, n - done);
            var row = _db.GetDirty(id, idx);
            Seg seg;
            if (row is not null)
            {
                var path = PiecePath(id, idx);
                seg = row.Kind switch
                {
                    'Z' => new ZeroSeg(idx, inOff, done, len),
                    'U' when !row.Staged => new RemoteSeg(idx, inOff, done, len, new ChunkLoc(row.ChunkId!, row.Msg, row.Size, row.Hash!)),
                    _ => new StagedSeg(idx, inOff, done, len, path),
                };
            }
            else if (_db.CommittedChunk(id, idx) is { } c)
            {
                var plain = TrustedPlain(c.ChunkId, c.Hash);
                seg = plain is not null
                    ? new StagedSeg(idx, inOff, done, len, plain)
                    : new RemoteSeg(idx, inOff, done, len, new ChunkLoc(c.ChunkId, c.Msg, c.Size, c.Hash));
            }
            else seg = new ZeroSeg(idx, inOff, done, len);
            plan.Add(seg);
            done += len;
        }
        return plan;
    }

    void Execute(List<Seg> plan, Span<byte> dst, bool sequential, bool preview)
    {
        foreach (var seg in plan)
        {
            var target = dst.Slice(seg.DstOff, seg.Len);
            switch (seg)
            {
                case ZeroSeg:
                    target.Clear();
                    break;
                case StagedSeg s:
                {
                    using var h = File.OpenHandle(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    int got = 0;
                    while (got < target.Length)
                    {
                        int r = RandomAccess.Read(h, target[got..], s.InOff + got);
                        if (r == 0) break;
                        got += r;
                    }
                    target[got..].Clear();
                    break;
                }
                case RemoteSeg r:
                {
                    var buf = new byte[r.Len];
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _waits.Token);
                    cts.CancelAfter(ReadBudget);
                    Remote(() => _reader.ReadAsync(r.Loc, r.InOff, buf, sequential, cts.Token, preview).GetAwaiter().GetResult());
                    buf.CopyTo(target);
                    break;
                }
            }
        }
    }

    // ── write / resize ───────────────────────────────────────────────────────
    public void Write(string id, long offset, ReadOnlySpan<byte> src) => Write(id, offset, src, mayExceedQuota: false);

    /// <param name="mayExceedQuota">Windows flushing data an app already wrote (the app was told it is saved):
    /// never refused or delayed for staging room — losing it would be worse than a full staging area.</param>
    public void Write(string id, long offset, ReadOnlySpan<byte> src, bool mayExceedQuota)
    {
        if (src.IsEmpty) return;
        if (!mayExceedQuota) WaitForRoom(id, NewRoomFor(id, offset, src.Length));
        else WaitForRoomPaging(id, NewRoomFor(id, offset, src.Length));
        var st = State(id);
        lock (st.Lock)
        {
            var node = _db.Get(id) ?? throw new FileNotFoundException(id);
            _db.SetPending(id);
            long end = offset + src.Length;
            long size = Math.Max(node.Size, end);
            if (end > node.Size) ResizeLocked(st, id, node.Size, end);
            int done = 0;
            while (done < src.Length)
            {
                long pos = offset + done;
                int idx = (int)(pos / CS), inOff = (int)(pos % CS);
                int len = Math.Min(CS - inOff, src.Length - done);
                var path = EnsureStagedLocked(st, id, idx, PieceLen(idx, size));
                using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    RandomAccess.Write(h, src.Slice(done, len), inOff);
                Touched(st, idx);
                done += len;
            }
            _db.SetLocalSize(id, size, node.Mtime); // "modified" is set when the handle closes
            _db.SetPending(id);
            st.Gen++;
            // Windows may deliver cached data through a handle that never counted as a writer (e.g. the
            // one that first read the file): no close of a writer will come, so make sure it commits.
            if (st.OpenWriters == 0 && Interlocked.Exchange(ref st.OrphanCommit, 1) == 0) ScheduleCommit(id);
        }
    }

    public void SetSize(string id, long newSize)
    {
        var st = State(id);
        lock (st.Lock)
        {
            var node = _db.Get(id) ?? throw new FileNotFoundException(id);
            if (node.Size == newSize) return;
            if (newSize > node.Size && FreeDisk() < HardFloor)
                throw new IOException("DriveGram: local staging area is full (the local disk is full).");
            ResizeLocked(st, id, node.Size, newSize);
            _db.SetLocalSize(id, newSize, Now);
            _db.SetPending(id);
            st.Gen++;
        }
        if (State(id).OpenWriters == 0) ScheduleCommit(id);
    }

    void Touched(NodeState st, int idx)
    {
        st.PieceGen[idx] = Interlocked.Increment(ref _stamp);
        st.LastWrite[idx] = Environment.TickCount64;
        st.LastAnyWrite = Environment.TickCount64;
    }

    void ResizeLocked(NodeState st, string id, long oldSize, long newSize)
    {
        int oldCount = Count(oldSize), newCount = Count(newSize);
        if (newSize > oldSize)
        {
            if (oldCount > 0 && oldSize % CS != 0)
                ResizePiece(st, id, oldCount - 1, PieceLen(oldCount - 1, newSize));
            for (int idx = oldCount; idx < newCount; idx++)
            {
                _db.PutDirty(new DirtyRow(id, idx, 'Z', PieceLen(idx, newSize), false));
                Touched(st, idx);
            }
        }
        else
        {
            var gone = new List<int>();
            foreach (var row in _db.DirtyRows(id).Where(r => r.Idx >= newCount))
            {
                if (row.Staged) gone.Add(row.Idx);
                st.PieceGen[row.Idx] = Interlocked.Increment(ref _stamp); // invalidate any upload in flight
                st.LastWrite.Remove(row.Idx);
            }
            _db.DeleteDirtyFrom(id, newCount);
            if (gone.Count > 0)
            {
                _db.Durable(); // a power cut must not leave rows whose pieces are already deleted (zeros)
                foreach (var idx in gone) DeletePiece(id, idx);
            }
            if (newCount > 0 && PieceLen(newCount - 1, newSize) != PieceLen(newCount - 1, oldSize))
                ResizePiece(st, id, newCount - 1, PieceLen(newCount - 1, newSize));
        }
    }

    /// <summary>Change the length of one piece (zero pieces stay sparse).</summary>
    void ResizePiece(NodeState st, string id, int idx, int newLen)
    {
        var row = _db.GetDirty(id, idx);
        if (row is { Kind: 'Z' }) _db.PutDirty(row with { Size = newLen });
        else EnsureStagedLocked(st, id, idx, newLen);
        Touched(st, idx);
    }

    static readonly TimeSpan RewriteSealIdle = TimeSpan.FromMinutes(5);

    /// <summary>Make piece idx a local modifiable file of exactly `len` bytes; returns its path.</summary>
    string EnsureStagedLocked(NodeState st, string id, int idx, int len)
    {
        var path = PiecePath(id, idx);
        var row = _db.GetDirty(id, idx);
        if (row is { Kind: 'U' }) st.Rewritten.Add(idx);
        if (row is { Staged: true } && File.Exists(path))
        {
            SetPieceLength(path, len);
            if (row.Kind != 'S' || row.Size != len)
                _db.PutDirty(new DirtyRow(id, idx, 'S', len, true));
            return path;
        }

        byte[]? initial = null;
        if (row is { Kind: 'U' })
            initial = ReadWhole(new ChunkLoc(row.ChunkId!, row.Msg, row.Size, row.Hash!));
        else if (row is null && _db.CommittedChunk(id, idx) is { } c)
            initial = ReadWhole(new ChunkLoc(c.ChunkId, c.Msg, c.Size, c.Hash));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            if (initial is not null) fs.Write(initial, 0, Math.Min(initial.Length, len));
            fs.SetLength(len);
            if (initial is not null) fs.Flush(flushToDisk: true);
        }
        Interlocked.Add(ref _stagingBytes, len);
        _db.PutDirty(new DirtyRow(id, idx, 'S', len, true));
        return path;
    }

    byte[] ReadWhole(ChunkLoc loc)
    {
        var plain = _cache.TryGetPlain(loc.ChunkId);
        if (plain is not null)
        {
            try
            {
                var bytes = File.ReadAllBytes(plain);
                // its content becomes part of an upload: verified every time, never trusted blindly
                if (bytes.Length == loc.Size && Convert.ToHexStringLower(SHA256.HashData(bytes)) == loc.Hash) return bytes;
                Log.Warn($"cached copy of chunk {loc.ChunkId[..12]} is damaged; fetching it again");
                _cache.RemovePlain(loc.ChunkId);
            }
            catch (IOException) { }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _waits.Token);
        cts.CancelAfter(ReadBudget);
        byte[] whole = [];
        Remote(() => whole = _reader.ReadWholeAsync(loc, cts.Token).GetAwaiter().GetResult());
        return whole;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _verifiedPlain = new();

    /// <summary>Free the disk space of the local copies (cache only: never a file waiting to upload).</summary>
    public long ClearCache()
    {
        _verifiedPlain.Clear();
        return _cache.Clear();
    }

    /// <summary>A cached plaintext chunk, checked against its hash the first time this session uses it
    /// (a power cut can leave a cache file with zeros where data should be).</summary>
    string? TrustedPlain(string chunkId, string hash)
    {
        var path = _cache.TryGetPlain(chunkId);
        if (path is null || _verifiedPlain.ContainsKey(chunkId)) return path;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Convert.ToHexStringLower(SHA256.HashData(fs)) == hash) { _verifiedPlain[chunkId] = true; return path; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        Log.Warn($"cached copy of chunk {chunkId[..12]} is damaged; it will be fetched again");
        _cache.RemovePlain(chunkId);
        return null;
    }

    long _lastReadFailure; // Environment.TickCount64; 0 = reads are working

    /// <summary>After a recent failed read (offline), give up within seconds: every read holds one of
    /// the file system's few threads, and a folder of thumbnails would otherwise freeze Explorer.</summary>
    TimeSpan ReadBudget =>
        _lastReadFailure != 0 && Environment.TickCount64 - Interlocked.Read(ref _lastReadFailure) < 30_000
            ? TimeSpan.FromSeconds(5) : _o.ReadTimeout;

    /// <summary>Data that is only in Telegram was needed (to open or to change a file) and Telegram could
    /// not be reached: the app that asked gets an error, so the user should hear why.</summary>
    public event Action? RemoteUnreachable;

    void Remote(Action read)
    {
        try
        {
            read();
            Interlocked.Exchange(ref _lastReadFailure, 0);
        }
        catch (FileNotFoundException ex)
        {
            throw new IOException($"a piece of this file is missing in Telegram ({ex.Message})", ex);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException
                                   or System.Net.Sockets.SocketException)
        {
            // only "cannot reach Telegram" makes the next reads give up early, not one bad message
            Interlocked.Exchange(ref _lastReadFailure, Environment.TickCount64);
            // the user is told only when Telegram did not answer at all (not for a bad reply from it), and
            // never because the drive is being stopped
            bool unreachable = ex is TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException
                               || ex.InnerException is System.Net.Sockets.SocketException;
            if (unreachable && !_cts.IsCancellationRequested && !_waits.IsCancellationRequested)
                try { RemoteUnreachable?.Invoke(); } catch (Exception e) { Log.Warn($"engine: listener failed: {e.Message}"); }
            throw;
        }
    }

    void SetPieceLength(string path, int len)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        long delta = len - fs.Length;
        if (delta == 0) return;
        fs.SetLength(len);
        Interlocked.Add(ref _stagingBytes, delta);
    }

    void DeletePiece(string id, int idx)
    {
        var path = PiecePath(id, idx);
        try
        {
            var len = new FileInfo(path).Length;
            File.Delete(path);
            Interlocked.Add(ref _stagingBytes, -len);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { }
    }

    // ── backpressure ─────────────────────────────────────────────────────────
    /// <summary>New local space a write needs: nothing inside pieces already staged at that length.</summary>
    int NewRoomFor(string id, long offset, int len)
    {
        int first = (int)(offset / CS), last = (int)((offset + len - 1) / CS);
        long need = 0;
        for (int idx = first; idx <= last; idx++)
        {
            var row = _db.GetDirty(id, idx);
            int end = (int)Math.Min(CS, offset + len - (long)idx * CS);
            need += row is { Staged: true } ? Math.Max(0, end - row.Size) : end;
        }
        return (int)Math.Min(need, int.MaxValue);
    }

    /// <summary>
    /// The local disk (not the staging quota) holds a copy back: raised once per wait, with the disk's root
    /// and its free space. The copy goes on at the upload speed; freeing space on that disk speeds it up.
    /// </summary>
    public event Action<string, long>? LocalDiskShort;

    /// <summary>
    /// A write waits until staging has room. While uploads move, it waits (the copy slows to the upload
    /// speed, also when Telegram slows uploads down) up to MaxWriteWait; with no upload progress at all for
    /// BackpressureTimeout it gives up ("disk full", Windows offers to try again).
    /// </summary>
    volatile bool _overQuota;

    void WaitForRoom(string id, int incoming)
    {
        if (incoming <= 0) return;
        if (Interlocked.Read(ref _stagingBytes) + incoming <= StagingLimit()) return;
        if (IsPaused(id))
        {
            // this file's upload is paused (by the user, or the daily cap): waiting cannot help, and it is meant
            // to wait on this PC. The staging quota is then only a soft limit: take it while the disk has room.
            EvictUploadedPieces();
            if (FreeDisk() - incoming <= _o.MinFreeDisk) FitCacheToDisk(incoming);
            if (FreeDisk() - incoming > _o.MinFreeDisk) { _overQuota = true; return; }
            try { LocalDiskShort?.Invoke(Path.GetPathRoot(Path.GetFullPath(_staging)) ?? "", Math.Max(0, FreeDisk())); }
            catch (Exception ex) { Log.Warn($"disk-short notice: {ex.Message}"); }
            throw new IOException("DriveGram: uploads are paused and the local disk is full.");
        }
        // staging went over the quota with files that were paused (they may still be, or just resumed and now
        // go up slowly): other writes must not wait for all of it, only the disk is the limit until it drains
        if (_overQuota)
        {
            if (Interlocked.Read(ref _stagingBytes) <= _o.StagingQuota) _overQuota = false;
            else if (FreeDisk() - incoming > _o.MinFreeDisk) return;
        }
        long start = Environment.TickCount64, lastMove = start;
        bool told = false;
        while (Interlocked.Read(ref _stagingBytes) + incoming > StagingLimit())
        {
            EvictUploadedPieces();
            if (StagingLimit() < Quota())
            {
                FitCacheToDisk(incoming); // the disk is the limit: free cache
                if (Interlocked.Read(ref _stagingBytes) + incoming > StagingLimit() && Volatile.Read(ref _uploadsInFlight) == 0
                    && _db.PendingNodes().Count == 0)
                    throw new IOException("DriveGram: local staging area is full (the local disk is full)."); // nothing will free it
                if (!told && Interlocked.Read(ref _stagingBytes) + incoming > StagingLimit())
                {
                    told = true;
                    try { LocalDiskShort?.Invoke(Path.GetPathRoot(Path.GetFullPath(_staging)) ?? "", Math.Max(0, FreeDisk())); }
                    catch (Exception ex) { Log.Warn($"disk-short notice: {ex.Message}"); }
                }
            }
            if (Interlocked.Read(ref _stagingBytes) + incoming <= StagingLimit()) return;
            long now = Environment.TickCount64;
            if (UploadsMoving()) lastMove = now;
            if (now - lastMove > _o.BackpressureTimeout.TotalMilliseconds || now - start > _o.MaxWriteWait.TotalMilliseconds
                || _cts.IsCancellationRequested || _waits.IsCancellationRequested)
                throw new IOException("DriveGram: local staging area is full (uploads are not keeping up).");
            WaitHandle.WaitAny([_roomSignal, _cts.Token.WaitHandle, _waits.Token.WaitHandle], 500);
        }
    }

    long _freeAt, _freeBytes = long.MaxValue, _stagedAtMeasure;

    /// <summary>Free space on the disk that holds staging and cache (re-measured every 2 s).</summary>
    long FreeDisk()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _freeAt) > 2000)
        {
            try { Interlocked.Exchange(ref _freeBytes, new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_staging))!).AvailableFreeSpace); }
            catch (Exception) { }
            Interlocked.Exchange(ref _stagedAtMeasure, Interlocked.Read(ref _stagingBytes));
            Interlocked.Exchange(ref _freeAt, now);
        }
        // a fast copy can stage gigabytes between two measurements: count what was staged since
        long grown = Interlocked.Read(ref _stagingBytes) - Interlocked.Read(ref _stagedAtMeasure);
        return Interlocked.Read(ref _freeBytes) - Math.Max(0, grown);
    }

    const long HardFloor = 1L << 30; // below this much free disk even already-saved data cannot be taken

    /// <summary>
    /// Data an app already "saved" (Windows delivers it later): it may not be refused while the disk has
    /// room, but it must not fill the disk either. Make room (uploaded pieces, cache), wait while uploads
    /// progress (this slows the copy to the upload speed, which is the point), then accept it over the
    /// staging quota — only a disk about to be full refuses it.
    /// </summary>
    void WaitForRoomPaging(string id, int incoming)
    {
        if (incoming <= 0 || Interlocked.Read(ref _stagingBytes) + incoming <= StagingLimit()) return;
        // paused (or staging held by paused files): waiting for uploads cannot help, the disk is the limit
        if ((IsPaused(id) || _overQuota) && FreeDisk() - incoming >= PagingFloor) { _overQuota = true; return; }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5); // per write: Windows' writer threads are few
        while (Interlocked.Read(ref _stagingBytes) + incoming > StagingLimit())
        {
            EvictUploadedPieces();
            FitCacheToDisk(incoming);
            if (Interlocked.Read(ref _stagingBytes) + incoming <= StagingLimit()) return;
            if (FreeDisk() - incoming < PagingFloor)
                throw new IOException("DriveGram: local staging area is full (the local disk is full).");
            // waiting helps only when the staging quota is the limit AND uploads are moving; when the disk
            // is the limit, or nothing uploads (offline, signed out), take it now — the disk still has room
            bool quotaBound = StagingLimit() >= Quota();
            if (!quotaBound || !UploadsMoving() || DateTime.UtcNow > deadline
                || _cts.IsCancellationRequested || _waits.IsCancellationRequested) return;
            WaitHandle.WaitAny([_roomSignal, _cts.Token.WaitHandle, _waits.Token.WaitHandle], 500);
        }
    }

    /// <summary>Data Windows already took from an app is refused only this close to a full disk.</summary>
    const long PagingFloor = 256L << 20;

    long _progressSent = -1, _progressAt;

    /// <summary>Did uploads make progress in the last 20 s (engine-wide, so no write waits to find out)?</summary>
    bool UploadsMoving()
    {
        long sent = _remote.BytesSent, now = Environment.TickCount64;
        long before = Interlocked.Exchange(ref _progressSent, sent);
        if (before != -1 && sent != before) Interlocked.Exchange(ref _progressAt, now); // first look: not "moving" yet
        return now - Interlocked.Read(ref _progressAt) < 20_000;
    }

    /// <summary>The cache is only a copy: when the local disk runs low it shrinks (below its current size
    /// if needed) to keep MinFreeDisk free, plus `needed` bytes for a write that is waiting.</summary>
    void FitCacheToDisk(long needed)
    {
        // the quota covers the temporary entries only (prepared files are never evicted)
        // (preview windows count too: they shrink with the rest when the disk is short)
        long room = _cache.Bytes - _cache.KeptBytes + _cache.PreviewBytes + FreeDisk() - _o.MinFreeDisk - needed;
        long preview = Math.Clamp(room / 8, 0, PreviewQuota);
        _cache.PreviewQuota = preview;
        _cache.SetQuota(Math.Clamp(room - preview, Math.Min(128L << 20, _o.CacheQuota), _o.CacheQuota));
        Interlocked.Exchange(ref _freeAt, 0); // re-measure right away
    }

    /// <summary>The staging quota, but never so much that the local disk would drop below MinFreeDisk.</summary>
    long StagingLimit() =>
        Math.Min(Quota(), Interlocked.Read(ref _stagingBytes) + Math.Max(0, FreeDisk() - _o.MinFreeDisk));

    long _pausedStaged, _pausedStagedAt;

    /// <summary>The staging quota plus what paused files keep staged (measured every few seconds): data meant
    /// to wait on this PC must not make other files' writes wait, or fail, while the disk has room.</summary>
    long Quota()
    {
        if (!_pausedAll && _pausedNodes.IsEmpty) return _o.StagingQuota;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _pausedStagedAt) > 3000)
        {
            Interlocked.Exchange(ref _pausedStagedAt, now);
            long sum = 0;
            try
            {
                foreach (var id in _db.PendingNodes())
                    if (IsPaused(id) && Directory.Exists(Path.Combine(_staging, id)))
                        foreach (var f in new DirectoryInfo(Path.Combine(_staging, id)).EnumerateFiles()) sum += f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Interlocked.Exchange(ref _pausedStaged, sum);
        }
        return _o.StagingQuota + Interlocked.Read(ref _pausedStaged);
    }

    /// <summary>Windows session ends: release every thread waiting in here, right now.</summary>
    public void Abort() => _cts.Cancel();

    readonly CancellationTokenSource _waits = new();

    /// <summary>Unmounting: file-system requests waiting for room or for a download give up now (uploads
    /// and commits go on), so the unmount does not hang behind them.</summary>
    public void CancelWaits() => _waits.Cancel();

    /// <summary>Is every byte of this file on this PC (staged or in the cache)?</summary>
    public bool IsFullyLocal(string id)
    {
        var node = _db.Get(id);
        if (node is null || node.Size == 0) return true;
        var dirty = _db.DirtyRows(id).ToDictionary(r => r.Idx);
        var committed = _db.CommittedChunks(id).ToDictionary(c => c.Idx);
        for (int idx = 0; idx < Count(node.Size); idx++)
        {
            if (dirty.TryGetValue(idx, out var d))
            {
                if (d.Staged || d.Kind == 'Z') continue;
                if (d.ChunkId is { } dc && ChunkHere(new ChunkLoc(dc, d.Msg, d.Size, d.Hash!))) continue;
                return false;
            }
            if (committed.TryGetValue(idx, out var c) && !ChunkHere(new ChunkLoc(c.ChunkId, c.Msg, c.Size, c.Hash))) return false;
        }
        return true;
    }

    /// <summary>A stored piece is on this PC: decrypted in full, or all its downloaded windows (a prepared file).</summary>
    bool ChunkHere(ChunkLoc l)
    {
        if (_cache.TryGetPlainQuiet(l.ChunkId)) return true;
        int wins = _reader.WindowCount(l);
        for (int w = 0; w < wins; w++) if (!_cache.HasWindow(l.Msg, w)) return false;
        return true;
    }

    /// <summary>Free disk by dropping local copies of pieces already safely uploaded.</summary>
    void EvictUploadedPieces()
    {
        var rows = _db.DirtyWhere("kind='U' AND staged=1").ToList();
        if (rows.Count == 0) return;
        _db.Durable(); // the 'U' rows: after a power cut a missing piece of an uploaded chunk is harmless
        foreach (var row in rows)
        {
            var st = State(row.Node);
            lock (st.Lock)
            {
                var cur = _db.GetDirty(row.Node, row.Idx);
                if (cur is not { Kind: 'U', Staged: true }) continue;
                if (Volatile.Read(ref st.OpenWriters) > 0 && cur.ChunkId is { } cid)
                {
                    var path = PiecePath(row.Node, row.Idx);
                    long len = 0;
                    try { len = new FileInfo(path).Length; } catch (IOException) { }
                    _cache.AdoptPlain(cid, path);
                    if (!File.Exists(path)) Interlocked.Add(ref _stagingBytes, -len);
                }
                else DeletePiece(row.Node, row.Idx);
                _db.PutDirty(cur with { Staged = false });
            }
            if (Interlocked.Read(ref _stagingBytes) <= _o.StagingQuota / 2) return;
        }
    }

    // ── upload ───────────────────────────────────────────────────────────────
    /// <summary>Store one plaintext chunk remotely (deduplicated); returns its location.</summary>
    async Task<ChunkLoc> StoreChunkAsync(byte[] data, CancellationToken ct)
    {
        var hash = SHA256.HashData(data);
        var hashHex = Convert.ToHexStringLower(hash);
        var chunkId = _keys.ChunkId(hash);
        if (_db.FindReusableChunk(chunkId) is { } known) return new ChunkLoc(known.ChunkId, known.Msg, known.Size, known.Hash);

        var gate = _chunkLocks.GetOrAdd(chunkId, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            if (_db.FindReusableChunk(chunkId) is { } again) return new ChunkLoc(again.ChunkId, again.Msg, again.Size, again.Hash);
            var payload = ChunkCipher.Encrypt(data, _keys.ChunkKey(hash), Convert.FromHexString(chunkId), _keys.FrameSize);
            await _uploadSlots.WaitAsync(ct);
            Interlocked.Increment(ref _uploadsInFlight);
            try
            {
                int msg = await Retry.ForeverAsync(() => _remote.UploadChunkAsync($"{chunkId[..32]}.bin", payload, ct),
                    $"upload chunk {chunkId[..12]}", ct);
                _db.AddChunk(new StoredChunk(chunkId, msg, data.Length, hashHex));
                Interlocked.Add(ref _bytesUploaded, data.Length);
                return new ChunkLoc(chunkId, msg, data.Length, hashHex);
            }
            finally
            {
                Interlocked.Decrement(ref _uploadsInFlight);
                _uploadSlots.Release();
            }
        }
        finally
        {
            gate.Release();
            _chunkLocks.TryRemove(chunkId, out _);
        }
    }

    /// <summary>
    /// A chunk a held-back change points at was deleted from Telegram meanwhile: upload it again from
    /// this PC's copy (as a NEW message, no dedup). No copy here: zeros of the same size, Lost = true.
    /// </summary>
    public async Task<(ChunkRef Ref, bool Lost)> RecoverChunkAsync(ChunkRef c, CancellationToken ct)
    {
        byte[]? data = null;
        if (TrustedPlain(c.C, c.H) is { } path)
        {
            try { data = await File.ReadAllBytesAsync(path, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { data = null; }
            if (data is not null && data.Length != c.Z) data = null;
        }
        bool lost = data is null;
        data ??= new byte[c.Z];
        var hash = SHA256.HashData(data);
        var hashHex = Convert.ToHexStringLower(hash);
        var chunkId = _keys.ChunkId(hash);
        var payload = ChunkCipher.Encrypt(data, _keys.ChunkKey(hash), Convert.FromHexString(chunkId), _keys.FrameSize);
        int msg = await Retry.ForeverAsync(() => _remote.UploadChunkAsync($"{chunkId[..32]}.bin", payload, ct),
            $"upload chunk {chunkId[..12]} again", ct);
        _db.AddChunk(new StoredChunk(chunkId, msg, data.Length, hashHex));
        Interlocked.Add(ref _bytesUploaded, data.Length);
        return (new ChunkRef(chunkId, msg, data.Length, hashHex), lost);
    }

    /// <summary>Upload one S/Z piece and mark it U, unless it was modified meanwhile.</summary>
    readonly ConcurrentDictionary<(string, int), Task<bool>> _pieceUploads = new();

    /// <summary>
    /// Upload one piece. The same piece is never sent twice at once: the seal loop and a commit can both ask
    /// for it (while Telegram slows uploads down, a request can wait minutes for a slot), and the second
    /// simply waits for the first one's result instead of uploading the same 32 MB again.
    /// </summary>
    /// <param name="waitIfPaused">False for the seal loop: a paused file (or a piece a commit already sends,
    /// which may itself wait for a pause) is skipped, never awaited, so it cannot hold every other file.</param>
    async Task<bool> UploadPieceAsync(string id, int idx, CancellationToken ct, bool waitIfPaused = true)
    {
        if (!waitIfPaused && IsPaused(id)) return false;
        var key = (id, idx);
        var mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _pieceUploads.GetOrAdd(key, mine.Task);
        if (running != mine.Task) return waitIfPaused && await running.WaitAsync(ct);
        try
        {
            bool ok = await UploadPieceOnceAsync(id, idx, ct, waitIfPaused);
            mine.TrySetResult(ok);
            return ok;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) mine.TrySetCanceled(); else mine.TrySetException(ex);
            _ = mine.Task.Exception; // observed here: a waiter (if any) gets it too
            throw;
        }
        finally { _pieceUploads.TryRemove(KeyValuePair.Create(key, mine.Task)); }
    }

    async Task<bool> UploadPieceOnceAsync(string id, int idx, CancellationToken ct, bool waitIfPaused = true)
    {
        var st = State(id);
        long gen;
        DirtyRow? row;
        lock (st.Lock)
        {
            row = _db.GetDirty(id, idx);
            if (row is null || row.Kind == 'U') return true;
            gen = st.PieceGen.GetValueOrDefault(idx);
        }
        // paused (by the user, or the daily limit): the file waits on this PC. Checked again once a slot is
        // free: pieces queued before "pause" was pressed must not go up after it
        while (true)
        {
            if (!waitIfPaused && IsPaused(id)) return false;
            if (waitIfPaused && !await WaitWhilePausedAsync(id, ct)) return true; // deleted meanwhile
            // At most a few pieces in memory at once (read + encrypted copy are 2 x 32 MiB each): without this
            // a folder of hundreds of closed files read them ALL in before waiting for an upload slot.
            await _pieceSlots.WaitAsync(ct);
            if (!IsPaused(id)) break;
            _pieceSlots.Release();
            if (!waitIfPaused) return false;
        }
        _sendingNow.AddOrUpdate(id, 1, (_, n) => n + 1);
        try
        {
            // the wait for a slot can be long: the piece may have been uploaded meanwhile (by a run that
            // started before this one), and its local copy then moved to the cache or deleted
            lock (st.Lock)
            {
                var cur = _db.GetDirty(id, idx);
                if (cur is null || cur.Kind == 'U') return true;
            }
            try { return await UploadPieceLockedAsync(id, idx, st, gen, row, ct); }
            catch (FileNotFoundException)
            {
                lock (st.Lock) if (_db.GetDirty(id, idx) is null or { Kind: 'U' }) return true; // uploaded meanwhile
                throw;
            }
        }
        finally
        {
            if (_sendingNow.AddOrUpdate(id, 0, (_, n) => n - 1) <= 0) _sendingNow.TryRemove(KeyValuePair.Create(id, 0));
            _pieceSlots.Release();
        }
    }

    /// <summary>Files with a piece being read, encrypted or sent right now (the rest are waiting in line).</summary>
    readonly ConcurrentDictionary<string, int> _sendingNow = new();

    async Task<bool> UploadPieceLockedAsync(string id, int idx, NodeState st, long gen, DirtyRow row, CancellationToken ct)
    {
        byte[] data;
        if (row.Kind == 'Z') data = new byte[row.Size];
        else
        {
            data = new byte[row.Size];
            using (var f = new FileStream(PiecePath(id, idx), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                f.Flush(flushToDisk: true);
            using var h = File.OpenHandle(PiecePath(id, idx), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int got = 0;
            while (got < data.Length)
            {
                int r = RandomAccess.Read(h, data.AsSpan(got), got);
                if (r == 0) break;
                got += r;
            }
        }
        var loc = await StoreChunkAsync(data, ct);
        lock (st.Lock)
        {
            if (st.PieceGen.GetValueOrDefault(idx) != gen) return false;
            var cur = _db.GetDirty(id, idx);
            if (cur is null || cur.Kind == 'U' || cur.Size != data.Length) return false;
            // a deduplicated chunk may have just been taken by garbage collection: re-upload instead
            if (_db.FindChunk(loc.ChunkId) is null) return false;
            _db.PutDirty(new DirtyRow(id, idx, 'U', data.Length, cur.Staged, loc.ChunkId, loc.Msg, loc.Hash));
        }
        _room.Release();
        _roomSignal.Set();
        return true;
    }

    /// <summary>Upload everything a closed file still needs, then journal its new content.</summary>
    async Task CommitAsync(string id, CancellationToken ct)
    {
        var st = State(id);
        Interlocked.Exchange(ref st.OrphanCommit, 0);
        if (Interlocked.Exchange(ref st.Committing, 1) == 1)
        {
            // one is running; look again shortly so this request is not lost — unless the file is paused:
            // the running one waits for the pause and reads the file again then (no retry every second)
            if (!IsPaused(id)) ScheduleCommit(id);
            return;
        }
        try
        {
            for (int round = 0; ; round++)
            {
                long gen;
                List<DirtyRow> rows;
                lock (st.Lock)
                {
                    if (!_db.IsPending(id) || st.OpenWriters > 0) return;
                    if (_db.Get(id) is null) { _db.ClearPending(id); return; }
                    rows = _db.DirtyRows(id);
                    gen = st.Gen;
                }
                // paused: one wait for the whole file (not one per piece), then everything is read again
                if (IsPaused(id)) { await WaitWhilePausedAsync(id, ct); continue; } // gone: the next round ends it
                await Task.WhenAll(rows.Where(r => r.Kind != 'U').Select(r => UploadPieceAsync(id, r.Idx, ct)));

                lock (st.Lock)
                {
                    if (st.OpenWriters > 0) return;
                    if (st.Gen != gen) continue;
                    var node = _db.Get(id);
                    if (node is null) { _db.ClearPending(id); return; }
                    var dirty = _db.DirtyRows(id).ToDictionary(r => r.Idx);
                    if (dirty.Values.Any(r => r.Kind != 'U')) continue;

                    int count = Count(node.Size);
                    var refs = new ChunkRef[count];
                    bool retry = false;
                    for (int i = 0; i < count && !retry; i++)
                    {
                        int want = PieceLen(i, node.Size);
                        if (dirty.TryGetValue(i, out var d)) refs[i] = new ChunkRef(d.ChunkId!, d.Msg, d.Size, d.Hash!);
                        else if (_db.CommittedChunk(id, i) is { } c) refs[i] = new ChunkRef(c.ChunkId, c.Msg, c.Size, c.Hash);
                        else { _db.PutDirty(new DirtyRow(id, i, 'Z', want, false)); retry = true; break; }
                        if (refs[i].Z != want)
                        {
                            // committed chunk no longer has the right length (file was resized)
                            EnsureStagedLocked(st, id, i, want);
                            Touched(st, i);
                            retry = true;
                        }
                    }
                    if (retry) { st.Gen++; continue; }

                    _db.InTransaction(() =>
                    {
                        _db.ApplyLocal(new ContentOp(id, node.Size, node.Mtime, refs));
                        _db.DeleteAllDirty(id);
                        _db.ClearPending(id);
                    });
                    _db.Durable(); // the commit is on disk BEFORE its pieces move into the cache
                    foreach (var d in dirty.Values.Where(d => d.Staged))
                    {
                        var path = PiecePath(id, d.Idx);
                        long len = File.Exists(path) ? new FileInfo(path).Length : 0;
                        try { using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); f.Flush(true); }
                        catch (IOException) { }
                        _cache.AdoptPlain(d.ChunkId!, path);
                        _verifiedPlain[d.ChunkId!] = true; // it is exactly what was hashed and uploaded
                        Interlocked.Add(ref _stagingBytes, -len);
                    }
                    st.LastWrite.Clear(); // PieceGen is kept: stamps must never repeat
                    TryDeleteDir(Path.Combine(_staging, id));
                    ForgetQuietly(id); // saved: a later change is not held by a pause set for this one
                    Log.Info($"committed {node.Name} ({node.Size:N0} bytes, {count} chunks)");
                    if (IsPrepared(id)) RefreshKept(); // its new content is kept too
                    _room.Release();
        _roomSignal.Set();
                    return;
                }
            }
        }
        finally { Interlocked.Exchange(ref st.Committing, 0); }
    }

    async Task CommitLoop()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var id in _commitQueue.Reader.ReadAllAsync(ct))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CommitAsync(id, ct);
                        _commitFailures.TryRemove(id, out _);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        int n = _commitFailures.AddOrUpdate(id, 1, (_, v) => v + 1);
                        var wait = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(n - 1, 5))));
                        if (n == 1) Log.Error($"commit {id} failed, retrying in {wait.TotalSeconds:0}s", ex);
                        else if (n % 10 == 0) Log.Warn($"commit {id} still failing ({n} tries): {ex.Message}");
                        await Task.Delay(wait, ct).ContinueWith(_ => { });
                        _commitQueue.Writer.TryWrite(id);
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Upload full pieces of files that are still being written (big copies), so the
    /// local staging area drains while the copy runs instead of after it.</summary>
    async Task SealLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                if (++_ticks % 60 == 0) ReconcileStaging(adjustCount: true);
                if (_ticks % 10 == 0) FitCacheToDisk(0);
                if (_ticks % 60 == 0 && !PruneKept()) RefreshKept(); // also: a prepared file changed on another PC
                if (Interlocked.Read(ref _stagingBytes) > StagingLimit()) EvictUploadedPieces();
                long now = Environment.TickCount64;
                var due = new List<(string, int)>();
                foreach (var row in _db.DirtyWhere($"kind='S' AND size={CS}"))
                {
                    // a paused file waits (its pieces stay staged): awaiting it here would hold every other file
                    if (IsPaused(row.Node)) continue;
                    var st = State(row.Node);
                    lock (st.Lock)
                    {
                        if (st.OpenWriters == 0) continue; // closed files are handled by commit
                        long last = st.LastWrite.GetValueOrDefault(row.Idx);
                        var idle = st.Rewritten.Contains(row.Idx) ? RewriteSealIdle : _o.SealIdle;
                        if (now - last >= idle.TotalMilliseconds) due.Add((row.Node, row.Idx));
                    }
                }
                if (due.Count > 0)
                    await Task.WhenAll(due.Take(_o.UploadConcurrency * 2).Select(d => UploadPieceAsync(d.Item1, d.Item2, ct, waitIfPaused: false)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("seal loop", ex); }
        }
    }

    /// <summary>An app asked for its data to be on disk (FlushFileBuffers): this file's staged pieces and the
    /// index are made durable.</summary>
    public void FlushAllToDisk()
    {
        foreach (var id in _db.AllDirty().Where(r => r.Staged).Select(r => r.Node).Distinct().ToList()) FlushToDisk(id);
        _db.Durable();
    }

    public void FlushToDisk(string id)
    {
        var st = State(id);
        lock (st.Lock)
        {
            foreach (var row in _db.DirtyRows(id).Where(r => r.Staged))
            {
                var path = PiecePath(id, row.Idx);
                try { using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); f.Flush(true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        _db.Durable();
    }

    /// <summary>Files with local changes that are still open for writing (an app is editing them).</summary>
    public int FilesOpenForWriting => _nodes.Values.Count(n => Volatile.Read(ref n.OpenWriters) > 0);

    /// <summary>Pending files that can commit now (no app still writing them).</summary>
    public int PendingClosedFiles() => _db.PendingNodes().Count(id => !_nodes.TryGetValue(id, out var n) || Volatile.Read(ref n.OpenWriters) == 0);

    /// <summary>Queue every pending file for commit at once (handing the drive over).</summary>
    public void CommitAllNow()
    {
        ForceUploads = true; // handing the drive over: everything must go up now (the pauses are kept)
        foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node);
    }

    /// <summary>Pending files that are not paused (what "wait for the uploads" waits for).</summary>
    /// <summary>What Explorer read for thumbnails and details, kept apart (see BlockCache): 1 GB at most,
    /// less when the disk is short (FitCacheToDisk).</summary>
    long PreviewQuota => Math.Clamp(_o.CacheQuota / 8, 64L << 20, 1L << 30);

    public int PendingNotPaused() => _db.PendingNodes().Count(id => !IsPaused(id));
    /// <summary>Everything not (all) in Telegram yet, and whether its upload is paused (Explorer's icons follow
    /// this): files waiting to upload, and items whose change is not in the journal yet.</summary>
    public Dictionary<string, bool> PendingStates()
    {
        var res = new Dictionary<string, bool>();
        foreach (var id in _db.PendingNodes()) res[id] = IsPaused(id);
        foreach (var id in _db.OutboxNodes()) res.TryAdd(id, false);
        return res;
    }

    /// <summary>The file (or its last change) is not in Telegram yet: waiting to upload, uploading, or its
    /// journal entry not sent yet (offline, or another PC holds the drive). Until then, losing this PC loses it.</summary>
    public bool IsWaitingToUpload(string id) => _db.IsPending(id) || _db.HasOutboxOp(id);

    /// <summary>The file's pieces are still waiting to upload (not just its journal entry).</summary>
    public bool IsUploadPending(string id) => _db.IsPending(id);

    /// <summary>The file's content is in Telegram (a rename or move of it may still wait): freeing or
    /// preparing it is safe.</summary>
    public bool IsContentInTelegram(string id) => !_db.IsPending(id) && !_db.HasOutboxContent(id);

    /// <summary>What Explorer's previews hold on this PC (thumbnails and details).</summary>
    public long PreviewBytes => _cache.PreviewBytes;

    /// <summary>An earlier version of the file is in Telegram (a change of it is what waits).</summary>
    public bool HasSavedVersion(string id) => _db.CommittedChunks(id).Count > 0;

    /// <summary>Bytes downloaded from Telegram so far (this session).</summary>
    public long BytesDownloaded => Interlocked.Read(ref _reader.BytesDownloaded);

    /// <summary>Wait until every pending file is committed (used before unmount / handing over).</summary>
    /// <param name="stall">give up when nothing moved for this long (offline / signed out): the files
    /// stay pending on disk and upload next time. Null waits as long as it takes.</param>
    public async Task DrainAsync(Action<EngineStatus>? progress, CancellationToken ct, TimeSpan? stall = null)
    {
        foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node);
        var last = (Pending: -1, Up: -1L, Sent: -1L);
        var movedAt = DateTime.UtcNow;
        while (_db.PendingNodes().Any(id => !IsPaused(id)))
        {
            var s = Status;
            progress?.Invoke(s);
            // progress = a file finished, a chunk finished, or even one 512 KiB part reached Telegram
            var now = (s.PendingFiles, s.BytesUploaded, _remote.BytesSent);
            if (now != last) { last = now; movedAt = DateTime.UtcNow; }
            else if (stall is { } limit && DateTime.UtcNow - movedAt > limit)
            {
                Log.Warn($"uploads made no progress for {limit.TotalMinutes:0} min; {s.PendingFiles} files stay pending on this PC");
                return;
            }
            await Task.Delay(500, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _commitQueue.Writer.TryComplete();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { }
        SavePausedNow(); // a save still queued is written before the drive goes
    }
}

public static class Retry
{
    /// <summary>Retry transient failures with exponential backoff (max 60s), until cancelled.</summary>
    public static async Task<T> ForeverAsync<T>(Func<Task<T>> action, string what, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (int attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not StopRetryingException)
            {
                // offline for hours means thousands of attempts: the first few, then one in 20
                if (attempt <= 3 || attempt % 20 == 0)
                    Log.Warn($"{what} failed (attempt {attempt}): {ex.Message}; retrying in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }
}
