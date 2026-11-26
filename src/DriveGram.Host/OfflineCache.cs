using DriveGram.Crypto;
using DriveGram.Index;

namespace DriveGram.Host;

/// <summary>
/// What is on this PC for a drive that is NOT connected: the files prepared on it (from the drive's local
/// index), and freeing copies. Nothing here talks to Telegram: it only reads the local index and deletes
/// local cache files, so it works offline and while the drive is disconnected.
///
/// Safety: everything here runs only while holding the drive's own lock (the one a mount takes), so no
/// engine can be using the cache meanwhile (the tray, the command line, another window); and when the list
/// of prepared files cannot be read, nothing is deleted (it would delete what the user chose to keep).
/// Staged pieces (not in Telegram yet) and the preview windows (cache\v) are never touched.
/// </summary>
public static class OfflineCache
{
    /// <param name="Fraction">How much of it is on this PC (a prepare cut short, or a file changed since).</param>
    public sealed record PreparedFile(string Id, string Name, string Path, long Size, double Fraction);

    /// <summary>The drive is open (mounted) right now, here or in another window.</summary>
    public sealed class DriveInUseException() : InvalidOperationException("the drive is open");

    static string CacheDir(string dir) => Path.Combine(dir, "cache");
    static string PreparedList(string dir) => Path.Combine(dir, "prepared.txt");
    static string IndexOf(string dir) => Path.Combine(dir, "index.db");

    /// <summary>Runs `work` holding the drive's lock; DriveInUseException when it is mounted.</summary>
    static T Locked<T>(string dir, Func<T> work)
    {
        using var instance = new Semaphore(1, 1, DriveSession.InstanceName(dir));
        if (!instance.WaitOne(0)) throw new DriveInUseException();
        try { return work(); }
        finally { try { instance.Release(); } catch (SemaphoreFullException) { } }
    }

    /// <summary>The ids in prepared.txt; throws when it exists but cannot be read (then nothing may be freed).
    /// Read without keeping the engine of a mount that starts meanwhile from writing it.</summary>
    static HashSet<string> PreparedIds(string dir)
    {
        if (!File.Exists(PreparedList(dir))) return [];
        using var f = new FileStream(PreparedList(dir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f);
        var ids = new HashSet<string>();
        while (r.ReadLine() is { } l) if (l.Length > 0) ids.Add(l);
        return ids;
    }

    /// <summary>The files prepared on this PC (kept until freed), with their place on the drive.</summary>
    public static List<PreparedFile> Prepared(AppState s) => Prepared(s.Dir, s.Config.MountPoint);

    /// <param name="dir">The drive's folder on this PC (its index, cache and lists).</param>
    /// (Reading only: no lock, so it never keeps the drive from connecting meanwhile.)
    public static List<PreparedFile> Prepared(string dir, string mountPoint)
    {
        var ids = PreparedIds(dir);
        var res = new List<PreparedFile>();
        if (ids.Count == 0 || !File.Exists(IndexOf(dir))) return res;
        using var db = new ReadOnlyIndex(IndexOf(dir));
        int frame = db.FrameSize();
        foreach (var id in ids)
            if (db.Get(id) is { Dir: false } n && PathOf(db, n) is { } path) // gone, or in the recycle bin: not listed
                res.Add(new PreparedFile(id, n.Name, mountPoint + path, n.Size, Fraction(dir, db.Chunks(id), n.Size, frame)));
        return res;
    }

    /// <summary>The drive's index opened for reading only: no schema step, no write, no file created (it may
    /// run while a mount opens the same index, or while the drive is being removed).</summary>
    sealed class ReadOnlyIndex : IDisposable
    {
        readonly Microsoft.Data.Sqlite.SqliteConnection _c;
        public ReadOnlyIndex(string path)
        {
            _c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            _c.Open();
        }
        public void Dispose() => _c.Dispose();

        Microsoft.Data.Sqlite.SqliteCommand Cmd(string sql, params (string, object)[] args)
        {
            var c = _c.CreateCommand();
            c.CommandText = sql;
            foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v);
            return c;
        }

        public Node? Get(string id)
        {
            using var r = Cmd("SELECT id,parent,name,dir,size,attrs,ctime,mtime,trashed FROM nodes WHERE id=@id", ("@id", id)).ExecuteReader();
            return r.Read() ? new Node(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
                r.GetInt64(4), (uint)r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)) : null;
        }

        public List<(string ChunkId, int Msg, int Size)> Chunks(string node)
        {
            var res = new List<(string, int, int)>();
            using var r = Cmd("SELECT c.id, c.msg, c.size FROM file_chunks f JOIN chunks c ON c.id=f.chunk WHERE f.node=@n ORDER BY f.idx", ("@n", node)).ExecuteReader();
            while (r.Read()) res.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
            return res;
        }

        public int FrameSize() =>
            Cmd("SELECT v FROM meta WHERE k='layout'").ExecuteScalar() is string l && l.Split('/') is [_, var f] && int.TryParse(f, out var v) ? v : Keys.DefaultFrameSize;
    }

    /// <summary>Where the file is on the drive; null when it, or a folder above it, is in the recycle bin.</summary>
    static string? PathOf(ReadOnlyIndex db, Node n)
    {
        var parts = new List<string>();
        for (Node? x = n; x is not null && !x.IsRoot; x = x.Parent is null ? null : db.Get(x.Parent))
        {
            if (x.Trashed != 0 || parts.Count > 4096) return null;
            parts.Add(x.Name);
        }
        parts.Reverse();
        return @"\" + string.Join('\\', parts);
    }

    /// <summary>How much of a file's stored content is in the cache (as the engine's LocalFraction).</summary>
    static double Fraction(string dir, List<(string ChunkId, int Msg, int Size)> chunks, long size, int frame)
    {
        if (size == 0) return 1;
        if (chunks.Count == 0) return 0;
        long here = 0, all = 0;
        foreach (var c in chunks)
        {
            all += c.Size;
            if (File.Exists(Path.Combine(CacheDir(dir), "p", c.ChunkId))) { here += c.Size; continue; }
            int wins = (int)((ChunkCipher.EncryptedSize(c.Size, frame) + (1 << 20) - 1) >> 20), have = 0;
            for (int w = 0; w < wins; w++) if (File.Exists(Path.Combine(CacheDir(dir), "w", $"{c.Msg}_{w}"))) have++;
            here += (long)c.Size * have / Math.Max(1, wins);
        }
        return all == 0 ? 0 : Math.Clamp((double)here / all, 0, 1);
    }

    /// <summary>The copies to delete: every temporary copy, or (`only`) the copies of these files; never what
    /// a prepared file (other than those) still needs.</summary>
    static List<FileInfo> Victims(string dir, HashSet<string>? only, out HashSet<string> remaining)
    {
        var ids = PreparedIds(dir);
        remaining = only is null ? ids : ids.Where(i => !only.Contains(i)).ToHashSet();
        var keepMsgs = new HashSet<int>();
        var keepChunks = new HashSet<string>();
        var dropMsgs = new HashSet<int>();
        var dropChunks = new HashSet<string>();
        if ((remaining.Count > 0 || only is not null) && !File.Exists(IndexOf(dir)))
            throw new IOException("the drive's index is missing"); // cannot tell what is kept: delete nothing
        if (File.Exists(IndexOf(dir)))
        {
            using var db = IndexDb.Open(IndexOf(dir));
            void Collect(string id, HashSet<int> msgs, HashSet<string> chunkIds)
            {
                foreach (var c in db.CommittedChunks(id)) { msgs.Add(c.Msg); chunkIds.Add(c.ChunkId); }
                foreach (var d in db.DirtyRows(id).Where(r => r.Kind == 'U' && !r.Staged)) { msgs.Add(d.Msg); chunkIds.Add(d.ChunkId!); }
            }
            foreach (var id in remaining) Collect(id, keepMsgs, keepChunks);
            if (only is not null) foreach (var id in only) Collect(id, dropMsgs, dropChunks);
        }
        var files = new List<FileInfo>();
        var w = new DirectoryInfo(Path.Combine(CacheDir(dir), "w"));
        if (w.Exists)
            foreach (var f in w.EnumerateFiles())
            {
                int us = f.Name.IndexOf('_');
                if (us <= 0 || !int.TryParse(f.Name.AsSpan(0, us), out var msg) || keepMsgs.Contains(msg)) continue;
                if (only is null || dropMsgs.Contains(msg)) files.Add(f);
            }
        var p = new DirectoryInfo(Path.Combine(CacheDir(dir), "p"));
        if (p.Exists)
            foreach (var f in p.EnumerateFiles())
                if (!keepChunks.Contains(f.Name) && (only is null || dropChunks.Contains(f.Name))) files.Add(f);
        return files;
    }

    static long Delete(List<FileInfo> files)
    {
        long freed = 0;
        foreach (var f in files)
            try { long len = f.Length; f.Delete(); freed += len; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return freed;
    }

    /// <summary>Bytes of temporary copies that freeing would delete.</summary>
    public static long TemporaryBytes(AppState s) => TemporaryBytes(s.Dir);
    public static long TemporaryBytes(string dir) => Locked(dir, () => Victims(dir, null, out _).Sum(f => f.Length));

    /// <summary>Delete the temporary copies; returns the bytes freed.</summary>
    public static long FreeTemporary(AppState s) => FreeTemporary(s.Dir);
    public static long FreeTemporary(string dir) => Locked(dir, () => Delete(Victims(dir, null, out _)));

    /// <summary>"Free from this PC" for prepared files of a drive that is not connected: no longer kept, and
    /// their copies deleted (what another prepared file shares stays).</summary>
    public static long FreePrepared(string dir, IEnumerable<string> ids) => Locked(dir, () =>
    {
        var victims = Victims(dir, ids.ToHashSet(), out var remaining);
        var tmp = PreparedList(dir) + ".tmp";
        File.WriteAllLines(tmp, remaining);
        File.Move(tmp, PreparedList(dir), overwrite: true); // the list first: a copy deleted later is just cache
        return Delete(victims);
    });
}
