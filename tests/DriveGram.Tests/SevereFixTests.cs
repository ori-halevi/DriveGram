using System.Text;
using DriveGram.Index;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>Regression tests for the severe-issues review (convergence, bad records, lost snapshots).</summary>
public class SevereFixTests
{
    static SyncOptions Fast(string name) => new()
    {
        DeviceName = name,
        FlushInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = TimeSpan.FromMinutes(1),
        TrashRetention = TimeSpan.FromDays(30),
        GarbageGrace = TimeSpan.FromDays(7),
    };

    static string Text(Pc pc, string id) => Encoding.UTF8.GetString(pc.ReadFile(id));

    [Fact]
    public async Task TwoWritersAtOnceStillEndIdenticalOnEveryPc()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("report.txt");
        a.WriteFile(f, "v1"u8.ToArray());
        await a.SettleAsync();

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        await b.Sync.TakeDriveAsync(force: true, default);       // "take now"
        Assert.True(b.Sync.Writable);

        a.WriteFile(f, "A's late version"u8.ToArray());          // A still trusts its lease for up to a minute
        await a.SettleAsync();
        b.WriteFile(f, "B's version"u8.ToArray());               // B never saw A's late entry
        await b.SettleAsync();

        await b.Sync.CatchUpAsync(default);                      // B finds A's entry BEFORE its own: replays in order
        await a.Sync.CatchUpAsync(default);
        var c = kit.Pc("c", so: Fast("PC-C"));
        await c.Sync.StartAsync(false, default);
        foreach (var pc in new[] { a, b, c }) Assert.Equal("B's version", Text(pc, f));
    }

    [Fact]
    public async Task AJunkFileInTheJournalDoesNotStopTheDrive()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("keep.txt");
        a.WriteFile(f, "still here"u8.ToArray());
        await a.SettleAsync();
        await kit.Remote.AppendJournalAsync("j-00000000.bin", TestKit.RandomBytes(5000, 7), default); // garbage
        await kit.Remote.AppendJournalAsync("j-11111111.bin", "DGE1 not really"u8.ToArray(), default);  // bad envelope
        a.NewFile("after.txt");
        await a.SettleAsync();

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        Assert.False(b.Sync.NeedsUpdate);
        Assert.Equal("still here", Text(b, f));
        Assert.Contains(b.Db.Children(IndexDb.RootId), n => n.Name == "after.txt");
    }

    [Fact]
    public async Task ADeletedSnapshotFallsBackToTheWholeJournal()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("doc.txt");
        a.WriteFile(f, "content"u8.ToArray());
        await a.SettleAsync();
        await a.Sync.StopAsync(true, default);                   // writes a snapshot
        var snaps = Directory.GetFiles(Path.Combine(kit.Root, "remote", "journal"), "*_s-*");
        Assert.NotEmpty(snaps);
        foreach (var s in snaps) File.Delete(s);

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        Assert.Equal("content", Text(b, f));
    }

    [Fact]
    public async Task ALiveHolderWithADifferentClockIsNeverTakenOver()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        // A's clock is 3 hours behind: its lease looks long expired to B, but A keeps renewing it
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        long old = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        await kit.Remote.WriteControlAsync((ctrl with { Lease = ctrl.Lease! with { Until = old } }).ToJson(), default);

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        Assert.False(b.Sync.Writable);                            // first sight: never taken on the timestamp alone
    }
}
