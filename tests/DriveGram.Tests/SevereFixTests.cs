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
        await kit.Remote.WriteControlAsync((ctrl with { Lease = ctrl.Lease! with { Until = old } }).Signed(kit.Keys.ControlKey).ToJson(), default); // as a PC with the password writes it

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        Assert.False(b.Sync.Writable);                            // first sight: never taken on the timestamp alone
    }

    [Fact]
    public async Task ARecordChangedWithoutThePasswordIsNotBelieved()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        Assert.True(a.Sync.Writable);
        await a.Sync.StopAsync(true, default);
        await a.DisposeAsync();

        // someone with access to the channel (no password) hands the lease to a fake PC and asks a minReader
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        var forged = ctrl with { Lease = new LeaseInfo("evil", "EVIL-PC", DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()),
                                 MinReader = 99, Mac = null };
        await kit.Remote.WriteControlAsync(forged.ToJson(), default);
        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);                 // not locked out by the forged minReader
        Assert.False(b.Sync.ControlTrusted);
        await b.DisposeAsync();
        // a PC that knew the drive before (A): the repair restores what the password protects from ITS copy
        b = new Pc(a.Dir, kit.Remote, kit.Keys, so: Fast("PC-A"));
        await b.Sync.StartAsync(false, default);
        Assert.False(b.Sync.ControlTrusted);
        Assert.False(b.Sync.Writable);                           // and does not act on anything in it

        await b.Sync.RepairControlAsync(default);                // the user confirms: signed again, misleading parts dropped
        Assert.True(b.Sync.ControlTrusted);
        await b.Sync.TakeDriveAsync(force: false, default);
        Assert.True(b.Sync.Writable);
    }

    [Fact]
    public async Task APurgeOnAnotherPcNeverDestroysAFileStillWaitingToUploadHere()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var drafts = a.NewDir("Drafts");
        await a.SettleAsync();

        var doc = a.NewFile("doc.txt", drafts);                  // new, still open in an app: held back on A
        a.Engine.Opened(doc, true);
        a.Engine.Write(doc, 0, "unsent work"u8);

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        await b.Sync.TakeDriveAsync(force: true, default);       // B takes the drive...
        b.Db.ApplyLocal(new PurgeOp(drafts));                    // ...and deletes the (to it: empty) folder for good
        await b.SettleAsync();

        await a.Sync.CatchUpAsync(default);
        var kept = a.Db.Get(doc);
        Assert.NotNull(kept);                                    // not destroyed
        Assert.Equal(IndexDb.RootId, kept!.Parent);              // moved to the root
        Assert.True(a.Db.IsPending(doc));                        // and still waiting to upload
        a.Engine.Closed(doc, true);
    }

    [Fact]
    public async Task AskingAnOlderVersionForTheDriveLeavesItsRecordUnsigned()
    {
        await using var kit = await TestKit.CreateAsync();
        // a record as 1.0.2 writes it (no signature), held by a PC still running 1.0.2
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        var legacy = ctrl with { Mac = null, MinWriter = 2,
            Lease = new LeaseInfo("old-pc", "OLD-PC", DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()) };
        await kit.Remote.WriteControlAsync(legacy.ToJson(), default);

        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        Assert.True(b.Sync.ControlTrusted);                       // an unsigned record from before signing: accepted
        await b.Sync.RequestHandoverAsync(default);              // B (not the holder) asks for the drive
        var after = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        Assert.Null(after.Mac);                                  // still unsigned: the old holder's rewrites stay valid
        Assert.Equal(2, after.MinWriter);
        Assert.NotNull(after.Handover);
    }
}
