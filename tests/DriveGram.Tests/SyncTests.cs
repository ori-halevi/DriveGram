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
