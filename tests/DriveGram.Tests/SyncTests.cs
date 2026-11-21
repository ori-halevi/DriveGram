using DriveGram.Index;
using DriveGram.Sync;

namespace DriveGram.Tests;

public class SyncTests
{
    [Fact]
    public async Task SecondPcSeesTheTreeAfterTheFirstReleasesTheLease()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        Assert.True(a.Sync.Writable);

        var docs = a.NewDir("Documents");
        var f = a.NewFile("report.txt", docs);
        a.WriteFile(f, "hello from A"u8.ToArray());
        var gone = a.NewFile("temp.txt", docs);
        a.Db.ApplyLocal(new TrashOp(gone, DateTime.UtcNow.ToFileTimeUtc()));
        await a.SettleAsync();

        // B starts while A still holds the lease -> read-only, but it already sees A's files
        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        Assert.False(b.Sync.Writable);
        Assert.Equal("hello from A"u8.ToArray(), b.ReadFile(b.Db.Resolve(@"\Documents\report.txt")!.Id));
        Assert.Null(b.Db.Resolve(@"\Documents\temp.txt"));

        await a.Sync.StopAsync(releaseLease: true, default);

        // B takes turns: writes, and A sees it next time it starts
        var c = kit.Pc("b2");
        await c.Sync.StartAsync(false, default);
        Assert.True(c.Sync.Writable);
        var file = c.Db.Resolve(@"\Documents\report.txt")!.Id;
        c.WriteFile(file, " and from B"u8.ToArray(), offset: 12);
        await c.SettleAsync();
        await c.Sync.StopAsync(true, default);

        await a.Sync.CatchUpAsync(default);
        Assert.Equal("hello from A and from B"u8.ToArray(), a.ReadFile(file));
    }

    [Fact]
    public async Task NewPcLoadsFromSnapshot()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        for (int i = 0; i < 20; i++) a.WriteFile(a.NewFile($"file{i}.txt"), TestKit.RandomBytes(1000 + i, i));
        await a.SettleAsync();
        await a.Sync.SnapshotAsync(default);
        var late = a.NewFile("after-snapshot.txt");
        a.WriteFile(late, "late"u8.ToArray());
        await a.SettleAsync();
        await a.Sync.StopAsync(true, default);

        var ctrl = await SyncService.ReadControlAsync(kit.Remote, default);
        Assert.NotNull(ctrl!.Snap);

        var fresh = kit.Pc("fresh");
        await fresh.Sync.StartAsync(false, default);
        Assert.Equal(21, fresh.Db.Counts().Files);
        Assert.Equal("late"u8.ToArray(), fresh.ReadFile(late));
        Assert.Equal(TestKit.RandomBytes(1007, 7), fresh.ReadFile(fresh.Db.Resolve(@"\file7.txt")!.Id));
    }

    [Fact]
    public async Task StaleLeaseIsTakenOver()
    {
        await using var kit = await TestKit.CreateAsync();
        var so = new SyncOptions { LeaseDuration = TimeSpan.FromSeconds(1), FlushInterval = TimeSpan.FromMilliseconds(100) };
        var a = kit.Pc("a", so: so);
        await a.Sync.StartAsync(false, default);
        await a.Sync.DisposeAsync(); // "crashed": never releases the lease
        await Task.Delay(2500);

        var b = kit.Pc("b", so: so);
        await b.Sync.StartAsync(false, default);
        Assert.True(b.Sync.Writable);
    }

    [Fact]
    public async Task ContentWrittenAgainAfterItsChunksWereCollectedIsStoredFresh()
    {
        await using var kit = await TestKit.CreateAsync();
        var so = new SyncOptions { TrashRetention = TimeSpan.Zero, GarbageGrace = TimeSpan.Zero, FlushInterval = TimeSpan.FromMilliseconds(100) };
        var a = kit.Pc("a", so: so);
        await a.Sync.StartAsync(false, default);
        var content = TestKit.RandomBytes(TestKit.ChunkSize + 5, 12);
        var old = a.NewFile("old.bin");
        a.WriteFile(old, content);
        await a.SettleAsync();
        a.Db.ApplyLocal(new TrashOp(old, DateTime.UtcNow.AddDays(-1).ToFileTimeUtc()));
        await Task.Delay(50);

        // the sweep takes the chunks out of the index first ...
        a.Db.ApplyLocal(new PurgeOp(old));
        await Task.Delay(50);
        Assert.Equal(2, a.Db.ClaimGarbage(TimeSpan.Zero));
        // ... the same bytes are written again before the messages are actually deleted ...
        var again = a.NewFile("again.bin");
        a.WriteFile(again, content);
        await a.SettleAsync();
        // ... and deleting the old messages must not break the new file
        await a.Sync.CollectGarbageAsync(default);
        var fresh = kit.Pc("fresh");
        await fresh.Sync.StartAsync(false, default);
        Assert.Equal(content, fresh.ReadFile(again));
    }

    [Fact]
    public async Task AnotherPcNeverReusesAChunkTheWriterAlreadyDeleted()
    {
        await using var kit = await TestKit.CreateAsync();
        var so = new SyncOptions { TrashRetention = TimeSpan.Zero, GarbageGrace = TimeSpan.Zero, FlushInterval = TimeSpan.FromMilliseconds(100) };
        var content = TestKit.RandomBytes(5000, 14);

        var a = kit.Pc("a", so: so);
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("x.bin");
        a.WriteFile(f, content);
        await a.SettleAsync();
        a.Db.ApplyLocal(new PurgeOp(f));
        await Task.Delay(50);
        await a.Sync.CollectGarbageAsync(default);       // A deletes the chunk's message
        Assert.Equal(0, kit.Remote.StoredChunkCount);
        await a.Sync.StopAsync(releaseLease: true, default);

        var b = kit.Pc("b", so: so);                      // B learned about the chunk from the journal
        await b.Sync.StartAsync(false, default);
        var g = b.NewFile("same-bytes.bin");
        b.WriteFile(g, content);                          // must upload again, not point at the deleted message
        await b.SettleAsync();
        Assert.Equal(1, kit.Remote.StoredChunkCount);
        Assert.Equal(content, b.ReadFile(g));
    }

    [Fact]
    public async Task OldDrivesGetAnIdentityOnceAndKeepIt()
    {
        await using var kit = await TestKit.CreateAsync(); // created like a pre-0.7 drive: no identity
        var before = await SyncService.ReadControlAsync(kit.Remote, default);
        Assert.Null(before!.Drive);

        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var id = await a.Sync.EnsureIdentityAsync(() => new DriveIdentity("d1", "Photos", 42), default);
        Assert.Equal("d1", id!.Id);
        var again = await a.Sync.EnsureIdentityAsync(() => new DriveIdentity("other", "x", 1), default);
        Assert.Equal("d1", again!.Id);                      // never replaced

        var after = await SyncService.ReadControlAsync(kit.Remote, default);
        Assert.Equal(42, after!.Drive!.DataChannel);
        Assert.Equal(before.Vault, after.Vault);            // the rest of the record is untouched
    }

    [Fact]
    public void ControlRecordsWithoutIdentityStillParse()
    {
        var legacy = """{"v":1,"vault":{"v":1,"salt":"AA==","iter":1000,"chunkSize":2097152,"frameSize":65536,"verifier":"ab"},"lease":null,"snap":null}""";
        var ctrl = ControlState.FromJson(legacy)!;
        Assert.Null(ctrl.Drive);
        Assert.Equal(1000, ctrl.Vault.Iter);
    }

    [Fact]
    public async Task GarbageCollectionWaitsForUploads()
    {
        await using var kit = await TestKit.CreateAsync();
        var so = new SyncOptions { TrashRetention = TimeSpan.Zero, GarbageGrace = TimeSpan.Zero, FlushInterval = TimeSpan.FromMilliseconds(100) };
        var a = kit.Pc("a", so: so);
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("x.bin");
        a.WriteFile(f, TestKit.RandomBytes(1000, 13));
        await a.SettleAsync();
        a.Db.ApplyLocal(new PurgeOp(f));
        await Task.Delay(50);
        a.Sync.CanCollectGarbage = () => false;   // e.g. an upload is in flight
        await a.Sync.CollectGarbageAsync(default);
        Assert.Equal(1, kit.Remote.StoredChunkCount);
        a.Sync.CanCollectGarbage = () => true;
        await a.Sync.CollectGarbageAsync(default);
        Assert.Equal(0, kit.Remote.StoredChunkCount);
    }

    [Fact]
    public async Task GarbageIsCollectedOnlyAfterTrashIsPurged()
    {
        await using var kit = await TestKit.CreateAsync();
        var so = new SyncOptions { TrashRetention = TimeSpan.Zero, GarbageGrace = TimeSpan.Zero, FlushInterval = TimeSpan.FromMilliseconds(100) };
        var a = kit.Pc("a", so: so);
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("old.bin");
        a.WriteFile(f, TestKit.RandomBytes(TestKit.ChunkSize + 5, 9));
        await a.SettleAsync();
        Assert.Equal(2, kit.Remote.StoredChunkCount);

        a.Db.ApplyLocal(new TrashOp(f, DateTime.UtcNow.AddDays(-1).ToFileTimeUtc()));
        await Task.Delay(50);
        await a.Sync.CollectGarbageAsync(default);
        Assert.Equal(0, kit.Remote.StoredChunkCount);
        Assert.Null(a.Db.Get(f));
    }
}
