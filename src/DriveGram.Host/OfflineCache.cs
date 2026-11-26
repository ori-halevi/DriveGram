using DriveGram.Index;

namespace DriveGram.Host;

/// <summary>
/// What is on this PC for a drive that is NOT connected: the files prepared on it (from the drive's local
/// index), and freeing its temporary copies. Nothing here talks to Telegram: it only reads the local index
/// and deletes local cache files, so it works offline and while the drive is disconnected.
/// The preview windows (cache\v) and the prepared files' pieces stay.
/// </summary>
public static class OfflineCache
{
    public sealed record PreparedFile(string Id, string Name, string Path, long Size);

    static string CacheDir(string dir) => Path.Combine(dir, "cache");
    static string PreparedList(string dir) => Path.Combine(dir, "prepared.txt");
    static string IndexOf(string dir) => Path.Combine(dir, "index.db");

    static HashSet<string> PreparedIds(string s)
    {
        try { return File.Exists(PreparedList(s)) ? File.ReadAllLines(PreparedList(s)).Where(l => l.Length > 0).ToHashSet() : []; }
        catch (IOException) { return []; }
    }

    /// <summary>The files prepared on this PC (kept until freed), with their place on the drive.</summary>
    public static List<PreparedFile> Prepared(AppState s) => Prepared(s.Dir, s.Config.MountPoint);

    /// <param name="dir">The drive's folder on this PC (its index, cache and lists).</param>
    public static List<PreparedFile> Prepared(string s, string mountPoint)
    {
        var ids = PreparedIds(s);
        var res = new List<PreparedFile>();
        if (ids.Count == 0 || !File.Exists(IndexOf(s))) return res;
        using var db = IndexDb.Open(IndexOf(s));
        foreach (var id in ids)
            if (db.Get(id) is { Dir: false, Trashed: 0 } n)
                res.Add(new PreparedFile(id, n.Name, mountPoint + PathOf(db, n), n.Size));
        return res;
    }

    static string PathOf(IndexDb db, Node n)
    {
        var parts = new List<string>();
        for (Node? x = n; x is not null && !x.IsRoot && parts.Count < 4096; x = x.Parent is null ? null : db.Get(x.Parent)) parts.Add(x.Name);
        parts.Reverse();
        return @"\" + string.Join('\\', parts);
    }

    /// <summary>The temporary copies (not prepared files, not previews): their files and total size.</summary>
    static (List<FileInfo> Files, long Bytes) Temporary(string s)
    {
        var keepMsgs = new HashSet<int>();
        var keepChunks = new HashSet<string>();
        var ids = PreparedIds(s);
        if (ids.Count > 0 && File.Exists(IndexOf(s)))
        {
            using var db = IndexDb.Open(IndexOf(s));
            foreach (var id in ids)
            {
                foreach (var c in db.CommittedChunks(id)) { keepMsgs.Add(c.Msg); keepChunks.Add(c.ChunkId); }
                foreach (var d in db.DirtyRows(id).Where(r => r.Kind == 'U' && !r.Staged)) { keepMsgs.Add(d.Msg); keepChunks.Add(d.ChunkId!); }
            }
        }
        var files = new List<FileInfo>();
        var w = new DirectoryInfo(Path.Combine(CacheDir(s), "w"));
        if (w.Exists)
            foreach (var f in w.EnumerateFiles())
            {
                int us = f.Name.IndexOf('_');
                if (us > 0 && int.TryParse(f.Name.AsSpan(0, us), out var msg) && keepMsgs.Contains(msg)) continue;
                files.Add(f);
            }
        var p = new DirectoryInfo(Path.Combine(CacheDir(s), "p"));
        if (p.Exists)
            foreach (var f in p.EnumerateFiles())
                if (!keepChunks.Contains(f.Name)) files.Add(f);
        return (files, files.Sum(f => f.Length));
    }

    /// <summary>Bytes of temporary copies that freeing would delete.</summary>
    public static long TemporaryBytes(AppState s) => Temporary(s.Dir).Bytes;

    /// <summary>Delete the temporary copies; returns the bytes freed. Only while the drive is not connected.</summary>
    public static long FreeTemporary(AppState s) => FreeTemporary(s.Dir);

    public static long FreeTemporary(string dir)
    {
        long freed = 0;
        foreach (var f in Temporary(dir).Files)
            try { long len = f.Length; f.Delete(); freed += len; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return freed;
    }
}
