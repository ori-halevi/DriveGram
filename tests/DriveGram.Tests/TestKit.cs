using DriveGram.Crypto;
using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>One simulated PC: its own index + state dir, sharing a remote with other PCs.</summary>
public sealed class Pc : IAsyncDisposable
{
    public IndexDb Db { get; }
    public ContentEngine Engine { get; }
    public SyncService Sync { get; }
    public string Dir { get; }

    public Pc(string dir, IRemote remote, Keys keys, EngineOptions? eo = null, SyncOptions? so = null, bool startEngine = true)
    {
        Dir = dir;
        Db = IndexDb.Open(Path.Combine(dir, "index.db"));
        Engine = new ContentEngine(Db, remote, keys, dir, eo ?? TestKit.FastEngine);
        Sync = new SyncService(Db, remote, keys, so ?? new SyncOptions { FlushInterval = TimeSpan.FromMilliseconds(100) });
        if (startEngine) Engine.Start();
    }

    public string NewFile(string name, string parent = IndexDb.RootId)
    {
        var id = Guid.NewGuid().ToString("N");
        long now = DateTime.UtcNow.ToFileTimeUtc();
        Db.ApplyLocal(new UpsertOp(id, parent, name, false, 0x20, now, now));
        return id;
    }

    public string NewDir(string name, string parent = IndexDb.RootId)
    {
        var id = Guid.NewGuid().ToString("N");
        long now = DateTime.UtcNow.ToFileTimeUtc();
        Db.ApplyLocal(new UpsertOp(id, parent, name, true, 0x10, now, now));
        return id;
    }

    /// <summary>Write like an application does: open for write, write, close.</summary>
    public void WriteFile(string id, byte[] data, long offset = 0, int block = 64 * 1024)
    {
        Engine.Opened(id, true);
        for (int i = 0; i < data.Length; i += block)
            Engine.Write(id, offset + i, data.AsSpan(i, Math.Min(block, data.Length - i)));
        Engine.Closed(id, true);
    }

    public byte[] ReadFile(string id)
    {
        var n = Db.Get(id)!;
        var buf = new byte[n.Size];
        int done = 0;
        while (done < buf.Length)
        {
            int r = Engine.Read(id, done, buf.AsSpan(done, Math.Min(300_000, buf.Length - done)));
            if (r == 0) break;
            done += r;
        }
        return buf;
    }

    public async Task SettleAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Engine.DrainAsync(null, cts.Token);
        if (Sync.Writable) await Sync.FlushAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Sync.DisposeAsync();
        Db.Dispose();
    }
}

public sealed class TestKit : IAsyncDisposable
{
    public const int ChunkSize = 2 * 1024 * 1024;   // small chunks: many boundaries in small files
    public const int FrameSize = 64 * 1024;
    public static readonly EngineOptions FastEngine = new()
    {
        CommitDelay = TimeSpan.FromMilliseconds(50),
        SealIdle = TimeSpan.FromMilliseconds(100),
        CacheQuota = 64L << 20,
    };

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "drivegram-tests", Guid.NewGuid().ToString("N"));
    public FolderRemote Remote { get; }
    public Keys Keys { get; private set; } = null!;
    readonly List<Pc> _pcs = [];

    public TestKit() { Remote = new FolderRemote(Path.Combine(Root, "remote")); }

    public static async Task<TestKit> CreateAsync()
    {
        var kit = new TestKit();
        kit.Keys = await SyncService.CreateDriveAsync(kit.Remote, "correct horse", CancellationToken.None,
            ChunkSize, FrameSize, iterations: 1000);
        return kit;
    }

    public Pc Pc(string name, EngineOptions? eo = null, SyncOptions? so = null, bool startEngine = true)
    {
        var pc = new Pc(Path.Combine(Root, name), Remote, Keys, eo, so, startEngine);
        _pcs.Add(pc);
        return pc;
    }

    public static byte[] RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pc in _pcs) await pc.DisposeAsync();
        try { Directory.Delete(Root, true); } catch { }
    }
}
