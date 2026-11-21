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
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How long a writer waits for staging room before the write fails with "disk full".</summary>
    public TimeSpan BackpressureTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed record EngineStatus(int PendingFiles, long StagedBytes, int UploadsInFlight, long BytesUploaded,
    long BytesDownloaded, long CacheBytes);

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
        public long LastReadEnd = -1;
        public int Committing;
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
    readonly Channel<string> _commitQueue = Channel.CreateUnbounded<string>();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _room = new(0);
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
        _cache = new BlockCache(Path.Combine(stateDir, "cache"), _o.CacheQuota);
        _reader = new ChunkReader(remote, _cache, keys);
        _uploadSlots = new SemaphoreSlim(Math.Max(1, _o.UploadConcurrency));
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

    public void Start()
    {
        ReconcileStaging(adjustCount: false); // before counting: the count starts from what is really staged
        long staged = 0;
        foreach (var row in _db.AllDirty())
        {
            var path = PiecePath(row.Node, row.Idx);
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
        foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node);
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

    /// <summary>Drop uncommitted local changes (the file was deleted).</summary>
    public void Discard(string id)
    {
        var st = State(id);
        lock (st.Lock)
        {
            foreach (var row in _db.DirtyRows(id).Where(r => r.Staged)) DeletePiece(id, row.Idx);
            _db.DeleteAllDirty(id);
            _db.ClearPending(id);
            foreach (var k in st.PieceGen.Keys.ToList()) st.PieceGen[k] = Interlocked.Increment(ref _stamp);
            st.LastWrite.Clear();
            st.Gen++;
        }
        TryDeleteDir(Path.Combine(_staging, id));
    }

    // ── read ─────────────────────────────────────────────────────────────────
    public int Read(string id, long offset, Span<byte> dst)
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
            bool sequential = Interlocked.Exchange(ref st.LastReadEnd, offset + n) == offset;
            try
            {
                Execute(plan, dst[..n], sequential);
                return n;
            }
            catch (Exception ex) when (attempt == 0 && ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // a piece moved to the cache / was evicted between planning and reading: plan again
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
                var plain = _cache.TryGetPlain(c.ChunkId);
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

    void Execute(List<Seg> plan, Span<byte> dst, bool sequential)
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
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    cts.CancelAfter(_o.ReadTimeout);
                    _reader.ReadAsync(r.Loc, r.InOff, buf, sequential, cts.Token).GetAwaiter().GetResult();
                    buf.CopyTo(target);
                    break;
                }
            }
        }
    }

    // ── write / resize ───────────────────────────────────────────────────────
    public void Write(string id, long offset, ReadOnlySpan<byte> src)
    {
        if (src.IsEmpty) return;
        WaitForRoom(src.Length);
        var st = State(id);
        lock (st.Lock)
        {
            var node = _db.Get(id) ?? throw new FileNotFoundException(id);
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
            _db.SetLocalSize(id, size, Now);
            _db.SetPending(id);
            st.Gen++;
        }
    }

    public void SetSize(string id, long newSize)
    {
        var st = State(id);
        lock (st.Lock)
        {
            var node = _db.Get(id) ?? throw new FileNotFoundException(id);
            if (node.Size == newSize) return;
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
            foreach (var row in _db.DirtyRows(id).Where(r => r.Idx >= newCount))
            {
                if (row.Staged) DeletePiece(id, row.Idx);
                st.PieceGen[row.Idx] = Interlocked.Increment(ref _stamp); // invalidate any upload in flight
                st.LastWrite.Remove(row.Idx);
            }
            _db.DeleteDirtyFrom(id, newCount);
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

    /// <summary>Make piece idx a local modifiable file of exactly `len` bytes; returns its path.</summary>
    string EnsureStagedLocked(NodeState st, string id, int idx, int len)
    {
        var path = PiecePath(id, idx);
        var row = _db.GetDirty(id, idx);
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
            try { return File.ReadAllBytes(plain); } catch (IOException) { }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(_o.ReadTimeout);
        return _reader.ReadWholeAsync(loc, cts.Token).GetAwaiter().GetResult();
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
    void WaitForRoom(int incoming)
    {
        if (Interlocked.Read(ref _stagingBytes) + incoming <= _o.StagingQuota) return;
        var deadline = DateTime.UtcNow + _o.BackpressureTimeout;
        while (Interlocked.Read(ref _stagingBytes) + incoming > _o.StagingQuota)
        {
            EvictUploadedPieces();
            if (Interlocked.Read(ref _stagingBytes) + incoming <= _o.StagingQuota) return;
            if (DateTime.UtcNow > deadline) throw new IOException("DriveGram: local staging area is full (uploads are not keeping up).");
            _room.Wait(500);
        }
    }

    /// <summary>Free disk by dropping local copies of pieces already safely uploaded.</summary>
    void EvictUploadedPieces()
    {
        foreach (var row in _db.AllDirty().Where(r => r is { Kind: 'U', Staged: true }))
        {
            var st = State(row.Node);
            lock (st.Lock)
            {
                var cur = _db.GetDirty(row.Node, row.Idx);
                if (cur is not { Kind: 'U', Staged: true }) continue;
                DeletePiece(row.Node, row.Idx);
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

    /// <summary>Upload one S/Z piece and mark it U, unless it was modified meanwhile.</summary>
    async Task<bool> UploadPieceAsync(string id, int idx, CancellationToken ct)
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
        byte[] data;
        if (row.Kind == 'Z') data = new byte[row.Size];
        else
        {
            data = new byte[row.Size];
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
        return true;
    }

    /// <summary>Upload everything a closed file still needs, then journal its new content.</summary>
    async Task CommitAsync(string id, CancellationToken ct)
    {
        var st = State(id);
        if (Interlocked.Exchange(ref st.Committing, 1) == 1)
        {
            ScheduleCommit(id); // one is running; look again shortly so this request is not lost
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
                    foreach (var d in dirty.Values.Where(d => d.Staged))
                    {
                        var path = PiecePath(id, d.Idx);
                        long len = File.Exists(path) ? new FileInfo(path).Length : 0;
                        _cache.AdoptPlain(d.ChunkId!, path);
                        Interlocked.Add(ref _stagingBytes, -len);
                    }
                    st.LastWrite.Clear(); // PieceGen is kept: stamps must never repeat
                    TryDeleteDir(Path.Combine(_staging, id));
                    Log.Info($"committed {node.Name} ({node.Size:N0} bytes, {count} chunks)");
                    _room.Release();
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
                    try { await CommitAsync(id, ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log.Error($"commit {id} failed, retrying in 30s", ex);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { });
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
                long now = Environment.TickCount64;
                var due = new List<(string, int)>();
                foreach (var row in _db.AllDirty().Where(r => r.Kind is 'S' or 'Z' && r.Size == CS))
                {
                    var st = State(row.Node);
                    lock (st.Lock)
                    {
                        if (st.OpenWriters == 0) continue; // closed files are handled by commit
                        long last = st.LastWrite.GetValueOrDefault(row.Idx);
                        if (now - last >= _o.SealIdle.TotalMilliseconds) due.Add((row.Node, row.Idx));
                    }
                }
                if (due.Count > 0)
                    await Task.WhenAll(due.Take(_o.UploadConcurrency * 2).Select(d => UploadPieceAsync(d.Item1, d.Item2, ct)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("seal loop", ex); }
        }
    }

    /// <summary>Wait until every pending file is committed (used before unmount / handing over).</summary>
    /// <param name="stall">give up when nothing moved for this long (offline / signed out): the files
    /// stay pending on disk and upload next time. Null waits as long as it takes.</param>
    public async Task DrainAsync(Action<EngineStatus>? progress, CancellationToken ct, TimeSpan? stall = null)
    {
        foreach (var node in _db.PendingNodes()) _commitQueue.Writer.TryWrite(node);
        var last = (Pending: -1, Up: -1L, Sent: -1L);
        var movedAt = DateTime.UtcNow;
        while (_db.PendingNodes().Count > 0)
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
                Log.Warn($"{what} failed (attempt {attempt}): {ex.Message}; retrying in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }
}
