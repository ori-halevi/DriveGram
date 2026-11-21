using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DriveGram.Index;

public sealed record Node(string Id, string? Parent, string Name, bool Dir, long Size, uint Attrs,
    long Ctime, long Mtime, long Trashed)
{
    public bool IsRoot => Id == IndexDb.RootId;

    /// <summary>Stable 64-bit file index for Windows (FNV-1a of the id).</summary>
    public ulong Ino
    {
        get
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in Id) { h ^= ch; h *= 1099511628211UL; }
            return h;
        }
    }
}

/// <summary>A committed chunk of a file (its content as of the last ContentOp).</summary>
public sealed record ChunkRow(int Idx, string ChunkId, int Msg, int Size, string Hash);

public sealed record StoredChunk(string ChunkId, int Msg, int Size, string Hash);

/// <summary>Kind of a local overlay piece: S = staged & modified, Z = zeros (sparse, no file),
/// U = uploaded (ChunkId/Msg set; a staged copy may still exist on disk).</summary>
public sealed record DirtyRow(string Node, int Idx, char Kind, int Size, bool Staged,
    string? ChunkId = null, int Msg = 0, string? Hash = null);

/// <summary>
/// The local index (SQLite). Holds the folder tree, the committed chunk lists, the chunk store,
/// the local write overlay (dirty/pending) and the outbox of ops not yet sent to the journal.
/// One connection, serialized by a lock: metadata queries are microseconds, and the heavy work
/// (network, disk IO of file data) never happens while holding it.
/// </summary>
public sealed class IndexDb : IDisposable
{
    public const string RootId = "root";
    const uint FileAttributeDirectory = 0x10;

    readonly SqliteConnection _c;
    readonly object _gate = new();
    SqliteTransaction? _tx;
    long _usedCache = -1;
    long _usedAt;

    public string DeviceId { get; private set; }

    IndexDb(SqliteConnection c)
    {
        _c = c;
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA journal_size_limit=67108864;
            CREATE TABLE IF NOT EXISTS nodes(
                id TEXT PRIMARY KEY, parent TEXT, name TEXT NOT NULL, nkey TEXT NOT NULL,
                dir INTEGER NOT NULL, size INTEGER NOT NULL DEFAULT 0, attrs INTEGER NOT NULL DEFAULT 0,
                ctime INTEGER NOT NULL, mtime INTEGER NOT NULL, trashed INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_nodes_name ON nodes(parent, nkey) WHERE trashed = 0;
            CREATE INDEX IF NOT EXISTS ix_nodes_parent ON nodes(parent);
            CREATE TABLE IF NOT EXISTS file_chunks(
                node TEXT NOT NULL, idx INTEGER NOT NULL, chunk TEXT NOT NULL, PRIMARY KEY(node, idx));
            CREATE INDEX IF NOT EXISTS ix_fc_chunk ON file_chunks(chunk);
            CREATE TABLE IF NOT EXISTS chunks(
                id TEXT PRIMARY KEY, msg INTEGER NOT NULL, size INTEGER NOT NULL, hash TEXT NOT NULL,
                created INTEGER NOT NULL, local INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS dirty(
                node TEXT NOT NULL, idx INTEGER NOT NULL, kind TEXT NOT NULL, size INTEGER NOT NULL,
                staged INTEGER NOT NULL, chunk TEXT, msg INTEGER, hash TEXT, PRIMARY KEY(node, idx));
            CREATE TABLE IF NOT EXISTS pending(node TEXT PRIMARY KEY, since INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(seq INTEGER PRIMARY KEY AUTOINCREMENT, op TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
            CREATE TABLE IF NOT EXISTS pending_gc(msg INTEGER PRIMARY KEY, since INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS own_appends(msg INTEGER PRIMARY KEY);
            CREATE INDEX IF NOT EXISTS ix_dirty_kind ON dirty(kind, staged);
            CREATE TABLE IF NOT EXISTS seen_entries(nonce TEXT PRIMARY KEY);
            """);
        // migration (0.5 → 0.6): which chunk rows this PC uploaded itself
        if (Scalar("SELECT 1 FROM pragma_table_info('chunks') WHERE name='local'") is null)
            Exec("ALTER TABLE chunks ADD COLUMN local INTEGER NOT NULL DEFAULT 0");
        var dev = GetMeta("device");
        if (dev is null)
        {
            dev = Guid.NewGuid().ToString("N");
            SetMeta("device", dev);
        }
        DeviceId = dev;
        if (Get(RootId) is null)
        {
            long now = DateTime.UtcNow.ToFileTimeUtc();
            Exec("INSERT INTO nodes(id,parent,name,nkey,dir,attrs,ctime,mtime) VALUES(@id,NULL,'','',1,@a,@t,@t)",
                ("@id", RootId), ("@a", FileAttributeDirectory), ("@t", now));
        }
    }

    /// <summary>
    /// The device id belongs to this PC, not to the file: an index copied to another PC (disk clone,
    /// restored profile) must not share an id with the original, or both would treat the same lease and
    /// the same journal entries as their own.
    /// </summary>
    public void BindToMachine(string machineId)
    {
        var known = GetMeta("machine");
        if (known is not null && known != machineId)
        {
            DeviceId = Guid.NewGuid().ToString("N");
            SetMeta("device", DeviceId);
            Exec("DELETE FROM own_appends");
            Log.Warn("this index came from another PC: it now has its own device id");
        }
        SetMeta("machine", machineId);
    }

    // ── journal bookkeeping (survives restarts and crashes) ──────────────────
    /// <summary>Remove sent ops and remember which journal message carried them, atomically.</summary>
    public void Sent(IEnumerable<long> seqs, int msg, string nonce) => InTransaction(() =>
    {
        foreach (var s in seqs) Exec("DELETE FROM outbox WHERE seq=@s", ("@s", s));
        Exec("INSERT OR IGNORE INTO own_appends(msg) VALUES(@m)", ("@m", msg));
        Exec("INSERT OR IGNORE INTO seen_entries(nonce) VALUES(@n)", ("@n", nonce));
    });

    public bool IsOwnAppend(int msg) => Scalar("SELECT 1 FROM own_appends WHERE msg=@m", ("@m", msg)) is not null;
    public bool SeenEntry(string nonce) => Scalar("SELECT 1 FROM seen_entries WHERE nonce=@n", ("@n", nonce)) is not null;
    public void MarkSeen(string nonce) => Exec("INSERT OR IGNORE INTO seen_entries(nonce) VALUES(@n)", ("@n", nonce));

    /// <summary>Apply ops of this PC's own journal entry again, in journal order (no conflict copies,
    /// nothing queued): used when another PC's entry turned out to sit BEFORE it in the journal.</summary>
    public void ApplyAsOwn(IReadOnlyList<Op> ops)
    {
        foreach (var op in ops)
        {
            if (op is ContentOp c && IsPending(c.Id))
            {
                // the file has newer local changes: take the committed chunks, keep the local size and time
                var n = Get(c.Id);
                Apply(op, remote: false);
                if (n is not null) SetLocalSize(c.Id, n.Size, n.Mtime);
                continue;
            }
            Apply(op, remote: false);
        }
    }

    public static IndexDb Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open();
        return new IndexDb(c);
    }

    public void Dispose()
    {
        lock (_gate) _c.Dispose();
    }

    // ── low-level helpers ────────────────────────────────────────────────────
    SqliteCommand Cmd(string sql, (string, object?)[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    int Exec(string sql, params (string, object?)[] args)
    {
        lock (_gate) { using var cmd = Cmd(sql, args); return cmd.ExecuteNonQuery(); }
    }

    object? Scalar(string sql, params (string, object?)[] args)
    {
        lock (_gate) { using var cmd = Cmd(sql, args); var r = cmd.ExecuteScalar(); return r is DBNull ? null : r; }
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>Run several statements atomically.</summary>
    public void InTransaction(Action body)
    {
        lock (_gate)
        {
            if (_tx is not null) { body(); return; } // nested: join the outer transaction
            _tx = _c.BeginTransaction();
            try { body(); _tx.Commit(); }
            catch { _tx.Rollback(); throw; }
            finally { _tx.Dispose(); _tx = null; }
        }
    }

    const string NodeCols = "id,parent,name,dir,size,attrs,ctime,mtime,trashed";

    static Node MapNode(SqliteDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
        r.GetInt64(4), (uint)r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8));

    public static string NameKey(string name) => name.ToUpperInvariant();

    // ── meta ─────────────────────────────────────────────────────────────────
    public string? GetMeta(string k) => Scalar("SELECT v FROM meta WHERE k=@k", ("@k", k)) as string;

    public void SetMeta(string k, string? v) =>
        Exec("INSERT INTO meta(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=excluded.v", ("@k", k), ("@v", v));

    /// <summary>Highest journal message id already applied to this index.</summary>
    public int Applied
    {
        get => int.TryParse(GetMeta("applied"), out var v) ? v : 0;
        set => SetMeta("applied", value.ToString());
    }

    /// <summary>True until the first journal entry or snapshot has been applied.</summary>
    public bool IsFresh => GetMeta("applied") is null;

    // ── tree queries ─────────────────────────────────────────────────────────
    public Node? Get(string id) =>
        Query($"SELECT {NodeCols} FROM nodes WHERE id=@id", MapNode, ("@id", id)).FirstOrDefault();

    public Node? Child(string parentId, string name) =>
        Query($"SELECT {NodeCols} FROM nodes WHERE parent=@p AND nkey=@k AND trashed=0", MapNode,
            ("@p", parentId), ("@k", NameKey(name))).FirstOrDefault();

    public List<Node> Children(string parentId) =>
        Query($"SELECT {NodeCols} FROM nodes WHERE parent=@p AND trashed=0", MapNode, ("@p", parentId));

    /// <summary>Children including trashed ones.</summary>
    public List<Node> ChildrenAll(string parentId) =>
        Query($"SELECT {NodeCols} FROM nodes WHERE parent=@p", MapNode, ("@p", parentId));

    public bool HasChildren(string id) =>
        Scalar("SELECT 1 FROM nodes WHERE parent=@p AND trashed=0 LIMIT 1", ("@p", id)) is not null;

    public static string[] SplitPath(string path) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Resolve a Windows path ("\a\b") to a live node.</summary>
    public Node? Resolve(string path)
    {
        var node = Get(RootId);
        foreach (var part in SplitPath(path))
        {
            if (node is null || !node.Dir) return null;
            node = Child(node.Id, part);
        }
        return node;
    }

    /// <summary>Resolve the parent directory of a path, and return the last component.</summary>
    public (Node? Parent, string Name) ResolveParent(string path)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0) return (null, "");
        var parent = Resolve(string.Join('\\', parts[..^1]));
        return (parent is { Dir: true } ? parent : null, parts[^1]);
    }

    /// <summary>Is `ancestorId` the node itself or one of its ancestors?</summary>
    public bool IsSelfOrAncestor(string ancestorId, string nodeId)
    {
        string? cur = nodeId;
        for (int guard = 0; cur is not null && guard < 4096; guard++)
        {
            if (cur == ancestorId) return true;
            cur = Scalar("SELECT parent FROM nodes WHERE id=@id", ("@id", cur)) as string;
        }
        return false;
    }

    public List<Node> Trashed() =>
        Query($"SELECT {NodeCols} FROM nodes WHERE trashed<>0 ORDER BY trashed DESC", MapNode);

    public long UsedBytes()
    {
        long now = Environment.TickCount64;
        if (_usedCache >= 0 && now - _usedAt < 5000) return _usedCache;
        _usedCache = Convert.ToInt64(Scalar("SELECT COALESCE(SUM(size),0) FROM nodes WHERE dir=0 AND trashed=0") ?? 0L);
        _usedAt = now;
        return _usedCache;
    }

    public (long Files, long Dirs) Counts() =>
        Query("SELECT COALESCE(SUM(dir=0),0), COALESCE(SUM(dir=1),0) FROM nodes WHERE trashed=0 AND id<>'root'",
            r => (r.GetInt64(0), r.GetInt64(1))).First();

    // ── applying ops ─────────────────────────────────────────────────────────
    /// <summary>Apply an op made on this PC and queue it for the journal, atomically.</summary>
    public void ApplyLocal(Op op) => ApplyLocal([op]);

    public void ApplyLocal(IReadOnlyList<Op> ops) => InTransaction(() =>
    {
        foreach (var op in ops)
        {
            Apply(op, remote: false);
            Exec("INSERT INTO outbox(op) VALUES(@o)", ("@o", OpJson.Serialize(op)));
        }
    });

    /// <summary>Apply an op that came from the journal (another PC, or a replay).</summary>
    public void ApplyRemote(Op op) => InTransaction(() => Apply(op, remote: true));

    void Apply(Op op, bool remote)
    {
        switch (op)
        {
            case UpsertOp u:
            {
                var name = UniqueName(u.Parent, u.Name, u.Id);
                uint attrs = u.Dir ? u.Attrs | FileAttributeDirectory : u.Attrs & ~FileAttributeDirectory;
                if (Get(u.Id) is null)
                    Exec("INSERT INTO nodes(id,parent,name,nkey,dir,attrs,ctime,mtime) VALUES(@id,@p,@n,@k,@d,@a,@c,@m)",
                        ("@id", u.Id), ("@p", u.Parent), ("@n", name), ("@k", NameKey(name)), ("@d", u.Dir ? 1 : 0),
                        ("@a", attrs), ("@c", u.Ctime), ("@m", u.Mtime));
                else
                    Exec("UPDATE nodes SET parent=@p,name=@n,nkey=@k,dir=@d,attrs=@a,ctime=@c,mtime=@m WHERE id=@id",
                        ("@id", u.Id), ("@p", u.Parent), ("@n", name), ("@k", NameKey(name)), ("@d", u.Dir ? 1 : 0),
                        ("@a", attrs), ("@c", u.Ctime), ("@m", u.Mtime));
                break;
            }
            case ContentOp c:
            {
                if (Get(c.Id) is null) { Log.Warn($"content op for unknown node {c.Id} ignored"); return; }
                if (remote && (IsPending(c.Id) || OutboxHasContent(c.Id)))
                {
                    // Both PCs changed this file. Never splice the two: the local version stays (it commits,
                    // or its held-back ContentOp is journaled after this one), and the other PC's version
                    // becomes a conflict copy next to it, journaled so every PC ends up with both.
                    var n = Get(c.Id)!;
                    var copyName = $"{Path.GetFileNameWithoutExtension(n.Name)} (conflict {DateTime.Now:yyyy-MM-dd HH-mm}){Path.GetExtension(n.Name)}";
                    var copyId = Guid.NewGuid().ToString("N");
                    ApplyLocal([new UpsertOp(copyId, n.Parent ?? RootId, copyName, false, n.Attrs, n.Ctime, c.Mtime),
                                new ContentOp(copyId, c.Size, c.Mtime, c.Chunks)]);
                    Log.Warn($"conflict on '{n.Name}': the other PC's version was kept as '{copyName}'");
                    return;
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                TouchChunksOf(c.Id, now);
                Exec("DELETE FROM file_chunks WHERE node=@n", ("@n", c.Id));
                for (int i = 0; i < c.Chunks.Length; i++)
                {
                    var k = c.Chunks[i];
                    // The latest record wins: identical bytes re-uploaded after an old copy was
                    // garbage-collected get a NEW message, and replay must follow it (keeping the
                    // first-seen mapping left other PCs pointing at the deleted message).
                    Exec("""
                        INSERT INTO chunks(id,msg,size,hash,created) VALUES(@id,@m,@z,@h,@t)
                        ON CONFLICT(id) DO UPDATE SET msg=excluded.msg, size=excluded.size, hash=excluded.hash,
                            local = 0
                        """, ("@id", k.C), ("@m", k.M), ("@z", k.Z), ("@h", k.H), ("@t", now));
                    Exec("INSERT INTO file_chunks(node,idx,chunk) VALUES(@n,@i,@c)", ("@n", c.Id), ("@i", i), ("@c", k.C));
                }
                Exec("UPDATE nodes SET size=@s, mtime=@m WHERE id=@id", ("@id", c.Id), ("@s", c.Size), ("@m", c.Mtime));
                break;
            }
            case TrashOp t:
                Exec("UPDATE nodes SET trashed=@t WHERE id=@id AND id<>@root", ("@id", t.Id), ("@t", t.At), ("@root", RootId));
                break;
            case RestoreOp r:
            {
                var name = UniqueName(r.Parent, r.Name, r.Id);
                Exec("UPDATE nodes SET trashed=0, parent=@p, name=@n, nkey=@k WHERE id=@id",
                    ("@id", r.Id), ("@p", r.Parent), ("@n", name), ("@k", NameKey(name)));
                break;
            }
            case PurgeOp p:
            {
                if (p.Id == RootId) return;
                // set-based: a folder of 500,000 files is a handful of statements, not millions
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Exec("DROP TABLE IF EXISTS temp.purge_ids");
                Exec("""
                    CREATE TEMP TABLE purge_ids AS
                    WITH RECURSIVE sub(id) AS (SELECT @id UNION ALL SELECT n.id FROM nodes n JOIN sub ON n.parent = sub.id)
                    SELECT id FROM sub
                    """, ("@id", p.Id));
                Exec("UPDATE chunks SET created=@t WHERE id IN (SELECT chunk FROM file_chunks WHERE node IN (SELECT id FROM temp.purge_ids))", ("@t", now));
                Exec("DELETE FROM file_chunks WHERE node IN (SELECT id FROM temp.purge_ids)");
                Exec("DELETE FROM dirty WHERE node IN (SELECT id FROM temp.purge_ids)");
                Exec("DELETE FROM pending WHERE node IN (SELECT id FROM temp.purge_ids)");
                Exec("DELETE FROM nodes WHERE id IN (SELECT id FROM temp.purge_ids)");
                Exec("DROP TABLE temp.purge_ids");
                break;
            }
            default:
                throw new NotSupportedException(op.GetType().Name);
        }
    }

    /// <summary>
    /// The grace period of garbage collection counts from when a chunk STOPPED being used here, not from
    /// when it was first stored: another PC may still hold a change based on the old version (a pending
    /// edit that keeps unchanged chunks, or a held-back ContentOp), and it gets the full grace to send it.
    /// </summary>
    void TouchChunksOf(string node, long now) =>
        Exec("UPDATE chunks SET created=@t WHERE id IN (SELECT chunk FROM file_chunks WHERE node=@n)", ("@n", node), ("@t", now));

    /// <summary>Does a change held back on this PC (not yet journaled) set this file's content?</summary>
    bool OutboxHasContent(string id) =>
        // serialized as {"t":"ct",...,"id":"<id>"} (the id comes last; chunk refs use "c", never "id")
        Scalar("SELECT 1 FROM outbox WHERE op LIKE '{\"t\":\"ct\",%' AND op LIKE @p LIMIT 1", ("@p", "%\"id\":\"" + id + "\"%")) is not null;

    /// <summary>
    /// Other PCs' entries were applied while changes made here were still held back. Those changes will be
    /// journaled AFTER them, so every other PC applies them last: replay them here on top as well, in order,
    /// so this PC ends in the same state (ops carry full state, replaying is harmless).
    /// </summary>
    public int ReapplyOutbox()
    {
        int n = 0;
        InTransaction(() =>
        {
            foreach (var (_, op) in PeekOutbox(int.MaxValue))
            {
                // a file being edited again keeps its local size; its committed chunks were never touched
                // (another PC's content for it became a conflict copy instead)
                if (op is ContentOp c && IsPending(c.Id)) continue;
                Apply(op, remote: false);
                n++;
            }
            SetMeta("rebase", null);
        });
        return n;
    }

    /// <summary>A name in `parent` that no other live node uses ("a.txt" -> "a (2).txt").</summary>
    string UniqueName(string parent, string name, string selfId)
    {
        var other = Child(parent, name);
        if (other is null || other.Id == selfId) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            var o = Child(parent, candidate);
            if (o is null || o.Id == selfId) return candidate;
        }
    }

    /// <summary>Local-only size/mtime while a file has uncommitted changes (the ContentOp carries them later).</summary>
    public void SetLocalSize(string id, long size, long mtime)
    {
        Exec("UPDATE nodes SET size=@s, mtime=@m WHERE id=@id", ("@id", id), ("@s", size), ("@m", mtime));
    }

    // ── committed content + chunk store ──────────────────────────────────────
    public List<ChunkRow> CommittedChunks(string node) => Query(
        "SELECT f.idx, c.id, c.msg, c.size, c.hash FROM file_chunks f JOIN chunks c ON c.id=f.chunk WHERE f.node=@n ORDER BY f.idx",
        r => new ChunkRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4)), ("@n", node));

    public ChunkRow? CommittedChunk(string node, int idx) => Query(
        "SELECT f.idx, c.id, c.msg, c.size, c.hash FROM file_chunks f JOIN chunks c ON c.id=f.chunk WHERE f.node=@n AND f.idx=@i",
        r => new ChunkRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4)),
        ("@n", node), ("@i", idx)).FirstOrDefault();

    /// <summary>
    /// A stored chunk that new content may safely reuse (dedup): one a file still references (garbage
    /// collection never deletes those), or one this PC uploaded and that was NEVER committed (an
    /// interrupted upload resuming — no other PC knows it, so no other PC can delete it). Once a chunk
    /// appears in any ContentOp its `local` flag is cleared: after the file is purged, any PC holding
    /// the lease may garbage-collect it, and this PC is never told.
    /// </summary>
    public StoredChunk? FindReusableChunk(string chunkId) => Query("""
        SELECT id,msg,size,hash FROM chunks c WHERE id=@id AND (local=1
            OR EXISTS (SELECT 1 FROM file_chunks f WHERE f.chunk=c.id)
            OR EXISTS (SELECT 1 FROM dirty d WHERE d.chunk=c.id))
        """, r => new StoredChunk(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)), ("@id", chunkId)).FirstOrDefault();

    /// <summary>A few committed chunks (for checking that the data channel is the right one).</summary>
    public List<StoredChunk> SampleChunks(int n) => Query(
        "SELECT c.id,c.msg,c.size,c.hash FROM chunks c WHERE EXISTS (SELECT 1 FROM file_chunks f WHERE f.chunk=c.id) ORDER BY c.created DESC LIMIT @n",
        r => new StoredChunk(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)), ("@n", n));

    public StoredChunk? FindChunk(string chunkId) => Query("SELECT id,msg,size,hash FROM chunks WHERE id=@id",
        r => new StoredChunk(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)), ("@id", chunkId)).FirstOrDefault();

    /// <summary>Register an uploaded chunk immediately (before any file references it), so a crash
    /// or a retry dedups against it instead of uploading again (lesson from InfiDrive 6.3).</summary>
    public void AddChunk(StoredChunk c) => Exec(
        "INSERT INTO chunks(id,msg,size,hash,created,local) VALUES(@id,@m,@z,@h,@t,1) ON CONFLICT(id) DO UPDATE SET msg=excluded.msg, local=1",
        ("@id", c.ChunkId), ("@m", c.Msg), ("@z", c.Size), ("@h", c.Hash), ("@t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    /// <summary>Chunks no file (live, trashed or pending) references, older than the grace period.</summary>
    public List<StoredChunk> GarbageChunks(TimeSpan grace, int limit = 500) => Query("""
        SELECT id,msg,size,hash FROM chunks c
        WHERE created <= @cut
          AND NOT EXISTS (SELECT 1 FROM file_chunks f WHERE f.chunk = c.id)
          AND NOT EXISTS (SELECT 1 FROM dirty d WHERE d.chunk = c.id)
        LIMIT @lim
        """, r => new StoredChunk(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)),
        ("@cut", DateTimeOffset.UtcNow.Add(-grace).ToUnixTimeMilliseconds()), ("@lim", limit));

    /// <summary>
    /// Atomically take unreferenced chunks out of the index and queue their messages for deletion.
    /// The reference check and the removal happen in ONE transaction, so a file that starts using a
    /// chunk (dedup) can never end up pointing at a message being deleted (InfiDrive lost 107 of 111
    /// thumbnails to a sweep that judged "unreferenced" by a rule that missed a user).
    /// The queue is on disk, so a crash between here and Telegram never forgets a deletion.
    /// </summary>
    public int ClaimGarbage(TimeSpan grace, int limit = 500)
    {
        int n = 0;
        InTransaction(() =>
        {
            foreach (var c in GarbageChunks(grace, limit))
            {
                Exec("INSERT INTO pending_gc(msg,since) VALUES(@m,@t) ON CONFLICT(msg) DO NOTHING",
                    ("@m", c.Msg), ("@t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Exec("DELETE FROM chunks WHERE id=@id", ("@id", c.ChunkId));
                n++;
            }
        });
        return n;
    }

    public List<int> PendingDeletions(int limit = 500) =>
        Query("SELECT msg FROM pending_gc ORDER BY since LIMIT @l", r => r.GetInt32(0), ("@l", limit));

    public void DeletionsDone(IEnumerable<int> msgs) => InTransaction(() =>
    {
        foreach (var m in msgs) Exec("DELETE FROM pending_gc WHERE msg=@m", ("@m", m));
    });

    /// <summary>Keep the index file small: flush the WAL, and compact when much of it is free space.</summary>
    public void Maintain()
    {
        lock (_gate)
        {
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            long pages = Convert.ToInt64(Scalar("PRAGMA page_count") ?? 0L);
            long free = Convert.ToInt64(Scalar("PRAGMA freelist_count") ?? 0L);
            // VACUUM rewrites the whole index under the lock (the drive stalls meanwhile): only small ones
            if (pages > 256 && pages < 16384 && free * 4 > pages) // over 25% unused, index under ~64 MB
            {
                Exec("VACUUM;");
                Log.Info($"index compacted ({free:N0} of {pages:N0} pages were free)");
            }
        }
    }

    // ── local overlay (dirty pieces) ─────────────────────────────────────────
    static DirtyRow MapDirty(SqliteDataReader r) => new(r.GetString(0), r.GetInt32(1), r.GetString(2)[0], r.GetInt32(3),
        r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? 0 : r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetString(7));

    const string DirtyCols = "node,idx,kind,size,staged,chunk,msg,hash";

    public DirtyRow? GetDirty(string node, int idx) =>
        Query($"SELECT {DirtyCols} FROM dirty WHERE node=@n AND idx=@i", MapDirty, ("@n", node), ("@i", idx)).FirstOrDefault();

    public List<DirtyRow> DirtyRows(string node) =>
        Query($"SELECT {DirtyCols} FROM dirty WHERE node=@n ORDER BY idx", MapDirty, ("@n", node));

    /// <summary>Dirty rows matching a fixed condition (internal callers only; no user input).</summary>
    public List<DirtyRow> DirtyWhere(string condition) => Query($"SELECT {DirtyCols} FROM dirty WHERE {condition}", MapDirty);

    public List<DirtyRow> AllDirty() => Query($"SELECT {DirtyCols} FROM dirty", MapDirty);

    public void PutDirty(DirtyRow d) => Exec($"""
        INSERT INTO dirty({DirtyCols}) VALUES(@n,@i,@k,@z,@s,@c,@m,@h)
        ON CONFLICT(node,idx) DO UPDATE SET kind=excluded.kind,size=excluded.size,staged=excluded.staged,
            chunk=excluded.chunk,msg=excluded.msg,hash=excluded.hash
        """, ("@n", d.Node), ("@i", d.Idx), ("@k", d.Kind.ToString()), ("@z", d.Size), ("@s", d.Staged ? 1 : 0),
        ("@c", d.ChunkId), ("@m", d.ChunkId is null ? null : d.Msg), ("@h", d.Hash));

    public void DeleteDirty(string node, int idx) =>
        Exec("DELETE FROM dirty WHERE node=@n AND idx=@i", ("@n", node), ("@i", idx));

    public void DeleteDirtyFrom(string node, int fromIdx) =>
        Exec("DELETE FROM dirty WHERE node=@n AND idx>=@i", ("@n", node), ("@i", fromIdx));

    public void DeleteAllDirty(string node) => Exec("DELETE FROM dirty WHERE node=@n", ("@n", node));

    public void SetPending(string node) => Exec(
        "INSERT INTO pending(node,since) VALUES(@n,@t) ON CONFLICT(node) DO NOTHING",
        ("@n", node), ("@t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public void ClearPending(string node) => Exec("DELETE FROM pending WHERE node=@n", ("@n", node));

    public bool IsPending(string node) => Scalar("SELECT 1 FROM pending WHERE node=@n", ("@n", node)) is not null;

    public List<string> PendingNodes() => Query("SELECT node FROM pending ORDER BY since", r => r.GetString(0));

    // ── outbox ───────────────────────────────────────────────────────────────
    public List<(long Seq, Op Op)> PeekOutbox(int max) => Query(
        "SELECT seq, op FROM outbox ORDER BY seq LIMIT @m", r => (r.GetInt64(0), OpJson.Deserialize(r.GetString(1))), ("@m", max));

    public void DeleteOutboxUpTo(long seq) => Exec("DELETE FROM outbox WHERE seq<=@s", ("@s", seq));

    public void DeleteOutbox(IEnumerable<long> seqs) => InTransaction(() =>
    {
        foreach (var s in seqs) Exec("DELETE FROM outbox WHERE seq=@s", ("@s", s));
    });

    /// <summary>Files with local changes that were never committed (new files still uploading).</summary>
    public HashSet<string> UncommittedNewFiles() => Query(
        "SELECT p.node FROM pending p WHERE NOT EXISTS (SELECT 1 FROM file_chunks f WHERE f.node = p.node)",
        r => r.GetString(0)).ToHashSet();

    /// <summary>Outbox ops that can be sent now (not those of a new file still being written).</summary>
    public int SendableOutboxCount()
    {
        var held = UncommittedNewFiles();
        return held.Count == 0 ? OutboxCount() : PeekOutbox(int.MaxValue).Count(o => !held.Contains(o.Op.Id));
    }

    public int OutboxCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM outbox") ?? 0);

    // ── snapshots (full index image, stored encrypted in the journal channel) ─
    /// <summary>The snapshot layout has its own version (unchanged since 0.1): tying it to StorageFormat made
    /// older readers refuse snapshots whose content they fully understand.</summary>
    public const int SnapshotFormat = 1;

    sealed record Snap(List<Node> Nodes, List<string[]> FileChunks, List<StoredChunk> Chunks, int V = SnapshotFormat, int Upto = 0);

    public byte[] ExportSnapshot(int upto = 0)
    {
        Snap snap;
        lock (_gate)
        {
            snap = new Snap(
                Upto: upto,
                Nodes: 
                Query($"SELECT {NodeCols} FROM nodes", MapNode),
                FileChunks: Query("SELECT node, idx, chunk FROM file_chunks", r => new[] { r.GetString(0), r.GetInt32(1).ToString(), r.GetString(2) }),
                Chunks: Query("SELECT id,msg,size,hash FROM chunks c WHERE EXISTS (SELECT 1 FROM file_chunks f WHERE f.chunk=c.id)",
                    r => new StoredChunk(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3))));
        }
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gz, snap, OpJson.Options);
        return ms.ToArray();
    }

    /// <summary>Load a snapshot; returns the journal position it covers if the snapshot itself says so.</summary>
    public int? ImportSnapshot(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        var snap = JsonSerializer.Deserialize<Snap>(gz, OpJson.Options)!;
        if (snap.V > SnapshotFormat) throw new NeedsUpdateException($"snapshot is format {snap.V}");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        InTransaction(() =>
        {
            Exec("DELETE FROM file_chunks; DELETE FROM nodes; DELETE FROM dirty; DELETE FROM pending;");
            foreach (var n in snap.Nodes)
                Exec("INSERT INTO nodes(id,parent,name,nkey,dir,size,attrs,ctime,mtime,trashed) VALUES(@id,@p,@n,@k,@d,@s,@a,@c,@m,@t)",
                    ("@id", n.Id), ("@p", n.Parent), ("@n", n.Name), ("@k", NameKey(n.Name)), ("@d", n.Dir ? 1 : 0),
                    ("@s", n.Size), ("@a", n.Attrs), ("@c", n.Ctime), ("@m", n.Mtime), ("@t", n.Trashed));
            foreach (var c in snap.Chunks)
                Exec("INSERT INTO chunks(id,msg,size,hash,created) VALUES(@id,@m,@z,@h,@t) ON CONFLICT(id) DO NOTHING",
                    ("@id", c.ChunkId), ("@m", c.Msg), ("@z", c.Size), ("@h", c.Hash), ("@t", now));
            foreach (var f in snap.FileChunks)
                Exec("INSERT INTO file_chunks(node,idx,chunk) VALUES(@n,@i,@c)", ("@n", f[0]), ("@i", int.Parse(f[1])), ("@c", f[2]));
        });
        return snap.Upto > 0 ? snap.Upto : null;
    }
}
