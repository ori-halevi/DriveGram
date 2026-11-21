using System.IO.Compression;
using System.Text;
using DriveGram.Index;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>Regression tests for the second code review (held-back changes, lease paths, format gate).</summary>
public class ReviewFix2Tests
{
    static SyncOptions Fast(TimeSpan? heartbeat = null, string? name = null) => new()
    {
        DeviceName = name ?? Environment.MachineName,
        FlushInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = heartbeat ?? TimeSpan.FromMinutes(1),
        TrashRetention = TimeSpan.FromDays(30),
        GarbageGrace = TimeSpan.FromDays(7),
    };

    static async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task HeldBackChangesAreReplayedAfterTheOtherPcsAndEveryPcEndsTheSame()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast(TimeSpan.FromMilliseconds(200)));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("report.txt");
        a.WriteFile(f, "original"u8.ToArray());
        await a.SettleAsync();
        await a.Sync.DisposeAsync();                              // A "sleeps": its loop stops, lease stays A's

        a.WriteFile(f, "edited on A"u8.ToArray());                // committed locally, not journaled yet
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60))) await a.Engine.DrainAsync(null, cts.Token);
        Assert.True(a.Db.OutboxCount() > 0);

        var b = kit.Pc("b", so: Fast());
        await b.Sync.StartAsync(takeOver: true, default);         // B takes over, edits, and leaves cleanly
        b.WriteFile(f, "edited on B, longer"u8.ToArray());
        await b.SettleAsync();
        await b.Sync.StopAsync(true, default);
        await b.DisposeAsync();

        await Task.Delay(300);
        await a.Sync.FlushAsync(default);                         // A wakes: its lease is gone, nothing is sent
        Assert.False(a.Sync.Writable);
        await a.DisposeAsync();

        var a2 = new Pc(a.Dir, kit.Remote, kit.Keys, so: Fast());
        await a2.Sync.StartAsync(false, default);                 // catch up (B's edit -> conflict copy), rebase, lease
        Assert.True(a2.Sync.Writable);
        await a2.SettleAsync();
        Assert.Equal(0, a2.Db.OutboxCount());

        var c = kit.Pc("c", so: Fast());
        await c.Sync.StartAsync(false, default);
        foreach (var pc in new[] { a2, c })
        {
            Assert.Equal((pc == a2 ? "a2" : "c") + ": edited on A", (pc == a2 ? "a2" : "c") + ": " + Encoding.UTF8.GetString(pc.ReadFile(f)));
            var copy = pc.Db.Children(IndexDb.RootId).Single(n => n.Name.StartsWith("report (conflict"));
            Assert.Equal("edited on B, longer", Encoding.UTF8.GetString(pc.ReadFile(copy.Id)));
        }
        Assert.Equal(c.Db.Children(IndexDb.RootId).Select(n => n.Name).Order(), a2.Db.Children(IndexDb.RootId).Select(n => n.Name).Order());
        await a2.DisposeAsync();
        await c.DisposeAsync();
    }

    [Fact]
    public async Task AWriterThatFindsItsLeaseReleasedCatchesUpBeforeWritingAgain()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast(TimeSpan.FromMilliseconds(200)));
        await a.Sync.StartAsync(false, default);

        var b = kit.Pc("b", so: Fast());
        await b.Sync.StartAsync(takeOver: true, default);
        var fromB = b.NewFile("from B.txt");
        await b.SettleAsync();
        await b.Sync.StopAsync(true, default);                    // lease released (null)
        await b.DisposeAsync();

        await Eventually(() => a.Db.Get(fromB) is not null && a.Sync.Writable, "A took in B's change and writes again");
        await a.DisposeAsync();
    }

    [Fact]
    public async Task AnUnknownRecordKindStopsReplayButAStrayFileDoesNot()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast());
        await a.Sync.StartAsync(false, default);
        await a.Sync.StopAsync(true, default);
        await a.DisposeAsync();

        await kit.Remote.AppendJournalAsync("holiday photo.jpg", [1, 2, 3], default); // someone posted a file there
        var c = kit.Pc("c", so: Fast());
        await c.Sync.StartAsync(false, default);
        Assert.False(c.Sync.NeedsUpdate);
        await c.Sync.StopAsync(true, default);
        await c.DisposeAsync();

        int at = await kit.Remote.AppendJournalAsync("x-future.bin", [1, 2, 3], default); // a newer version's record
        var d = kit.Pc("d", so: Fast());
        await d.Sync.StartAsync(false, default);
        Assert.True(d.Sync.NeedsUpdate);
        Assert.False(d.Sync.Writable);
        Assert.True(d.Db.Applied < at);
        await d.DisposeAsync();
    }

    [Fact]
    public void SnapshotsKeepTheirOwnFormatVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-snap-" + Guid.NewGuid().ToString("N"));
        using (var db = IndexDb.Open(Path.Combine(dir, "index.db")))
        {
            using var gz = new GZipStream(new MemoryStream(db.ExportSnapshot()), CompressionMode.Decompress);
            var json = new StreamReader(gz).ReadToEnd();
            Assert.Contains("\"v\":1", json);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void FieldsFromANewerVersionSurviveARewrite()
    {
        var ctrl = new ControlState(1, new Crypto.VaultHeader(1, "c2FsdHNhbHRzYWx0", 600000, 1 << 25, 1 << 18, "ab"), null, null);
        var json = ctrl.ToJson();
        json = json.Replace("\"verifier\":\"ab\"", "\"verifier\":\"ab\",\"futureWrap\":{\"k\":1}");
        json = json.TrimEnd('}') + ",\"futureField\":[1,2]}";
        var back = ControlState.FromJson(json)!;
        var rewritten = (back with { Lease = new LeaseInfo("d", "PC", 1) }).ToJson();
        Assert.Contains("\"futureField\":[1,2]", rewritten);
        Assert.Contains("\"futureWrap\":{\"k\":1}", rewritten);
    }

    [Fact]
    public async Task ANewFileReachesOtherPcsTogetherWithItsContent()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = new Pc(Path.Combine(kit.Root, "a"), kit.Remote, kit.Keys, so: Fast(), startEngine: false); // uploads held
        await a.Sync.StartAsync(false, default);
        var folder = a.NewDir("Docs");
        var f = a.NewFile("video.bin", folder);
        a.WriteFile(f, TestKit.RandomBytes(5000, 3));
        await a.Sync.FlushAsync(default);                       // the folder goes out, the half-uploaded file waits

        var b = kit.Pc("b", so: Fast());
        await b.Sync.StartAsync(false, default);
        Assert.NotNull(b.Db.Get(folder));
        Assert.Null(b.Db.Get(f));

        a.Engine.Start();
        await a.SettleAsync();                                  // committed: name + content go out together
        await b.Sync.CatchUpAsync(default);
        Assert.Equal(5000, b.Db.Get(f)!.Size);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task TheDriveCanBeHandedToAnotherPcOnRequest()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast(name: "PC-A"));
        await a.Sync.StartAsync(false, default);
        var b = kit.Pc("b", so: Fast(name: "PC-B"));
        await b.Sync.StartAsync(false, default);
        Assert.True(a.Sync.Writable);
        Assert.False(b.Sync.Writable);

        // B asks; A refuses
        HandoverRequest? seen = null;
        a.Sync.HandoverRequested += r => seen = r;
        var ask = await b.Sync.RequestHandoverAsync(default);
        await Eventually(() => seen is not null, "A noticed the request");
        await a.Sync.RefuseHandoverAsync(seen!, default);
        Assert.Equal("refused", (await b.Sync.HandoverStatusAsync(ask, default)).Request!.State);
        Assert.True(a.Sync.Writable);

        // B asks again; A agrees, with a file written just before
        var f = a.NewFile("last.txt");
        a.WriteFile(f, "from A"u8.ToArray());
        seen = null;
        ask = await b.Sync.RequestHandoverAsync(default);
        await Eventually(() => seen is not null, "A noticed the second request");
        await a.Sync.BeginHandoverAsync(seen!, 1, default);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60))) await a.Engine.DrainAsync(null, cts.Token);
        Assert.True(await a.Sync.CompleteHandoverAsync(seen!, default));
        Assert.False(a.Sync.Writable);

        var (_, lease) = await b.Sync.HandoverStatusAsync(ask, default);
        Assert.Equal(b.Db.DeviceId, lease!.Dev);
        await b.Sync.TakeDriveAsync(force: false, default);
        Assert.True(b.Sync.Writable);
        Assert.Equal("from A", System.Text.Encoding.UTF8.GetString(b.ReadFile(f))); // nothing left behind on A
        Assert.Null((await SyncService.ReadControlAsync(kit.Remote, default))!.Handover);
    }

    [Fact]
    public async Task WithoutAnAnswerTheRequesterTakesTheDrive()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast(name: "PC-A"));
        await a.Sync.StartAsync(false, default);
        await a.Sync.DisposeAsync();                               // A is asleep: it never answers
        var b = kit.Pc("b", so: Fast(name: "PC-B"));
        await b.Sync.StartAsync(false, default);
        await b.Sync.RequestHandoverAsync(default);
        await b.Sync.TakeDriveAsync(force: true, default);         // the countdown ran out / "take now"
        Assert.True(b.Sync.Writable);
        Assert.Equal(b.Db.DeviceId, (await SyncService.ReadControlAsync(kit.Remote, default))!.Lease!.Dev);
    }
}
