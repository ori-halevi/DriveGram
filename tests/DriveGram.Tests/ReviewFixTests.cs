using DriveGram.Engine;
using DriveGram.Index;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>Regression tests for the findings of the core code review.</summary>
public class ReviewFixTests
{
    static SyncOptions Fast(TimeSpan? heartbeat = null) => new()
    {
        FlushInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = heartbeat ?? TimeSpan.FromMinutes(1),
        TrashRetention = TimeSpan.Zero,
        GarbageGrace = TimeSpan.Zero,
    };

    [Fact]
    public async Task ACommittedChunkIsNotReusedAfterAnotherPcMayHaveCollectedIt()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast());
        await a.Sync.StartAsync(false, default);
        var content = TestKit.RandomBytes(4000, 21);
        var f = a.NewFile("photo.jpg");
        a.WriteFile(f, content);
        await a.SettleAsync();
        a.Db.ApplyLocal(new PurgeOp(f));                       // file gone for good
        await a.SettleAsync();
        // another PC holding the lease collected the chunk; this PC was never told
        await kit.Remote.DeleteChunksAsync([a.Db.SampleChunks(10).Concat(a.Db.GarbageChunks(TimeSpan.Zero)).First().Msg], default);
        int before = kit.Remote.StoredChunkCount;

        var g = a.NewFile("photo again.jpg");
        a.WriteFile(g, content);                               // same bytes: must be stored again, not deduped
        await a.SettleAsync();
        Assert.Equal(before + 1, kit.Remote.StoredChunkCount);

        var fresh = kit.Pc("fresh", so: Fast());
        await fresh.Sync.StartAsync(false, default);
        Assert.Equal(content, fresh.ReadFile(g));
    }

    [Fact]
    public async Task AFormerWriterHoldsItsChangesBackInsteadOfAppending()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast(TimeSpan.FromMilliseconds(200)));
        await a.Sync.StartAsync(false, default);
        await a.Sync.DisposeAsync();                            // stop A's background loop (it was "asleep")

        // meanwhile another PC took the lease
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        await kit.Remote.WriteControlAsync((ctrl with { Lease = new LeaseInfo("other-pc", "LAPTOP-2", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 900) }).ToJson(), default);
        int journalBefore = (await kit.Remote.ListJournalAsync(0, default)).Count;

        await Task.Delay(300);                                  // A's last lease confirmation is now stale
        a.Db.ApplyLocal(new UpsertOp("n1", IndexDb.RootId, "late.txt", false, 0x20, 1, 1));
        await a.Sync.FlushAsync(default);

        Assert.Equal(journalBefore, (await kit.Remote.ListJournalAsync(0, default)).Count); // nothing appended
        Assert.False(a.Sync.Writable);
        Assert.Equal(1, a.Sync.HeldBackChanges);
    }

    [Fact]
    public async Task AnotherPcsVersionOfAFileWithLocalChangesBecomesAConflictCopy()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast());
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("notes.txt");
        a.WriteFile(f, "original"u8.ToArray());
        await a.SettleAsync();
        await a.Sync.StopAsync(true, default);

        var b = kit.Pc("b", so: Fast());
        await b.Sync.StartAsync(false, default);
        b.WriteFile(f, "edited on B, longer"u8.ToArray());
        await b.SettleAsync();
        await b.Sync.StopAsync(true, default);

        // A comes back with an uncommitted local edit of the same file (crashed before uploading it)
        var a2 = new Pc(a.Dir, kit.Remote, kit.Keys, so: Fast(), startEngine: false);
        a2.WriteFile(f, "edited on A"u8.ToArray());
        await a2.Sync.StartAsync(false, default);               // B's version arrives while A's is pending
        Assert.Equal("edited on A".Length, a2.Db.Get(f)!.Size);  // local version untouched
        var copy = a2.Db.Children(IndexDb.RootId).Single(n => n.Name.StartsWith("notes (conflict"));
        Assert.Equal("edited on B, longer".Length, copy.Size);
        await a2.DisposeAsync();
    }

    [Fact]
    public async Task NoSnapshotWhileFilesAreStillUploading()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast(), startEngine: false);   // engine off: the file stays pending
        await a.Sync.StartAsync(false, default);
        a.WriteFile(a.NewFile("big.bin"), TestKit.RandomBytes(5000, 22));
        await a.Sync.SnapshotAsync(default);
        Assert.Null((await SyncService.ReadControlAsync(kit.Remote, default))!.Snap);
    }

    [Fact]
    public async Task OrphanedPieceFilesAreCleanedUpOnStart()
    {
        await using var kit = await TestKit.CreateAsync();
        var dir = Path.Combine(kit.Root, "a");
        var orphan = Path.Combine(dir, "staging", "deadbeef", "0.piece");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllBytes(orphan, new byte[1234]);
        var a = kit.Pc("a");
        Assert.False(File.Exists(orphan));
        Assert.Equal(0, a.Engine.Status.StagedBytes);
    }
}
