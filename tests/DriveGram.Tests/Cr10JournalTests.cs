using System.Text;
using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;

namespace DriveGram.Tests;

sealed class FaultRemote(IRemote inner) : IRemote
{
    public volatile bool FailAppend;
    public volatile int TruncateJournalRead = -1;
    public Task<int> UploadChunkAsync(string name, byte[] payload, CancellationToken ct) => inner.UploadChunkAsync(name, payload, ct);
    public Task<IReadOnlySet<int>> MissingChunksAsync(IReadOnlyList<int> msgs, CancellationToken ct) => inner.MissingChunksAsync(msgs, ct);
    public Task<byte[]> ReadWindowAsync(int msg, int window, CancellationToken ct) => inner.ReadWindowAsync(msg, window, ct);
    public Task DeleteChunksAsync(IReadOnlyList<int> msgs, CancellationToken ct) => inner.DeleteChunksAsync(msgs, ct);
    public Task<int> AppendJournalAsync(string name, byte[] payload, CancellationToken ct)
    {
        if (FailAppend) throw new IOException("offline");
        return inner.AppendJournalAsync(name, payload, ct);
    }
    public Task<IReadOnlyList<JournalItem>> ListJournalAsync(int afterId, CancellationToken ct) => inner.ListJournalAsync(afterId, ct);
    public async Task<byte[]> ReadJournalAsync(int id, CancellationToken ct)
    {
        var b = await inner.ReadJournalAsync(id, ct);
        if (Interlocked.CompareExchange(ref TruncateJournalRead, -1, id) == id) return b[..(b.Length / 2)];
        return b;
    }
    public Task<string?> ReadControlAsync(CancellationToken ct) => inner.ReadControlAsync(ct);
    public Task WriteControlAsync(string text, CancellationToken ct) => inner.WriteControlAsync(text, ct);
    public DateTime? ControlEditedAtUtc => inner.ControlEditedAtUtc;
    public DateTime ServerNowUtc => inner.ServerNowUtc;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class Cr10JournalTests
{
    static SyncOptions Opts(string name, bool autoFlush = false) => new()
    {
        DeviceName = name,
        FlushInterval = autoFlush ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromHours(1),
        GarbageGrace = TimeSpan.Zero,
    };
    static string Text(Pc pc, string id) => Encoding.UTF8.GetString(pc.ReadFile(id));

    static string LastErr = "";
    static byte[]? TryRead(Pc pc, string id)
    {
        try { return pc.ReadFile(id); } catch (Exception ex) { LastErr = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    // F1: a batch whose append failed (offline) and was cancelled (shutdown) stays as "inflight". Months later it
    // is re-sent verbatim, bypassing CheckHeldBackContentAsync: its chunk refs point at messages GC deleted.
    [Theory]
    [InlineData(true)]
    [InlineData(false)] // control: same story without the stale inflight -> passes
    public async Task StaleInflightBypassesHeldBackCheck(bool inflight)
    {
        await using var kit = await TestKit.CreateAsync();
        var fa = new FaultRemote(kit.Remote);
        var a = new Pc(Path.Combine(kit.Root, "a"), fa, kit.Keys, so: Opts("PC-A"));
        try
        {
            await a.Sync.StartAsync(false, default);
            var f = a.NewFile("photo.raw");
            var data = TestKit.RandomBytes(TestKit.ChunkSize * 5 / 2, 7);
            a.WriteFile(f, data);
            await a.SettleAsync();
            var edit = TestKit.RandomBytes(100, 8);
            a.WriteFile(f, edit, offset: 2L * TestKit.ChunkSize + 10);
            await a.Engine.DrainAsync(null, default);
            var expected = (byte[])data.Clone();
            edit.CopyTo(expected, 2 * TestKit.ChunkSize + 10);

            if (inflight)
            {
                fa.FailAppend = true;                          // offline while sending; then shut down
                using var cts = new CancellationTokenSource(800);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.Sync.FlushAsync(cts.Token));
                Assert.NotNull(a.Db.GetMeta("inflight"));
                fa.FailAppend = false;
            }

            var b = kit.Pc("b", so: Opts("PC-B", autoFlush: true));
            await b.Sync.StartAsync(false, default);
            await b.Sync.TakeDriveAsync(force: true, default);
            b.WriteFile(f, TestKit.RandomBytes(data.Length, 99)); // B rewrites the whole file
            await b.SettleAsync();
            await b.Sync.CollectGarbageAsync(default);         // old chunks of f (A's held edit still uses 0,1) deleted
            await b.Sync.StopAsync(true, default);

            a.Db.SetMeta("verify_outbox", "1");
            await a.Sync.TakeDriveAsync(force: false, default);
            Assert.True(a.Sync.Writable);
            await a.SettleAsync();

            var c = kit.Pc("c", so: Opts("PC-C"));
            await c.Sync.StartAsync(false, default);
            var got = TryRead(c, f);
            Assert.True(got is not null && got.AsSpan().SequenceEqual(expected), "A's held edit arrived broken on C: " + LastErr);
        }
        finally { await a.DisposeAsync(); }
    }

    // F2: replay flag persisted, but a pass stopped on an OWN entry (short read) after ReapplyOutbox consumed
    // "rebase". A local edit made between passes is then overwritten by the replayed older own entry.
    [Fact]
    public async Task ReplayResumedOnOwnEntryRevertsNewerLocalEdit()
    {
        await using var kit = await TestKit.CreateAsync();
        var fa = new FaultRemote(kit.Remote);
        var a = new Pc(Path.Combine(kit.Root, "a"), fa, kit.Keys, so: new SyncOptions { DeviceName = "PC-A", FlushInterval = TimeSpan.FromHours(1) });
        try
        {
            await a.Sync.StartAsync(false, default);
            var f = a.NewFile("report.txt");
            a.WriteFile(f, "v1"u8.ToArray());
            await a.SettleAsync();

            var b = kit.Pc("b", so: new SyncOptions { DeviceName = "PC-B", FlushInterval = TimeSpan.FromMilliseconds(100) });
            await b.Sync.StartAsync(false, default);
            await b.Sync.TakeDriveAsync(force: true, default);
            b.WriteFile(f, "B's version"u8.ToArray());
            await b.SettleAsync();                             // F1 (B)

            a.WriteFile(f, "A's version"u8.ToArray());         // A still trusts its lease (<10 s)
            await a.SettleAsync();                             // O (A) after F1
            var ownId = (await kit.Remote.ListJournalAsync(0, default)).Last(j => j.Name.StartsWith("j-" + a.Db.DeviceId[..8])).Id;

            fa.TruncateJournalRead = ownId;
            await a.Sync.CatchUpAsync(default);                // F1 applied, O short read -> stopped
            Assert.Equal("1", a.Db.GetMeta("replay"));

            a.WriteFile(f, "A's NEWEST!"u8.ToArray());          // user keeps working on A (not sent yet)
            await a.Engine.DrainAsync(null, default);
            Assert.Equal("A's NEWEST!", Text(a, f));

            await a.Sync.CatchUpAsync(default);                // O replayed; no rebase
            if (Environment.GetEnvironmentVariable("CR10_PERM") == "1")
            {
                await b.Sync.StopAsync(true, default);
                await a.Sync.TakeDriveAsync(force: true, default);
                await a.SettleAsync();                         // A's newest edit is sent
                await a.Sync.CatchUpAsync(default);
                var c = kit.Pc("c", so: new SyncOptions { DeviceName = "PC-C", FlushInterval = TimeSpan.FromHours(1) });
                await c.Sync.StartAsync(false, default);
                Assert.Equal(Text(c, f), Text(a, f));          // A vs every other PC
            }
            Assert.Equal("A's NEWEST!", Text(a, f));            // the user's newest local edit must stay
        }
        finally { await a.DisposeAsync(); }
    }

    // F3: CheckHeldBackContentAsync interrupted (shutdown/cancel during a re-upload) after ReplaceOutboxOp of one
    // op with zeros but before the "(incomplete)" rename: the next pass sees no missing chunk for it -> sent under
    // the real name with zeros inside.
    [Fact]
    public async Task InterruptedCheckLosesIncompleteMark()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Opts("PC-A"));
        await a.Sync.StartAsync(false, default);
        var x = a.NewFile("x.raw");
        var y = a.NewFile("y.raw");
        a.WriteFile(x, TestKit.RandomBytes(TestKit.ChunkSize * 5 / 2, 1));
        a.WriteFile(y, TestKit.RandomBytes(TestKit.ChunkSize * 5 / 2, 2));
        await a.SettleAsync();
        a.WriteFile(x, TestKit.RandomBytes(100, 3), offset: 2L * TestKit.ChunkSize + 10);
        a.WriteFile(y, TestKit.RandomBytes(100, 4), offset: 2L * TestKit.ChunkSize + 10);
        await a.Engine.DrainAsync(null, default);

        var b = kit.Pc("b", so: Opts("PC-B", autoFlush: true));
        await b.Sync.StartAsync(false, default);
        await b.Sync.TakeDriveAsync(force: true, default);
        b.WriteFile(x, TestKit.RandomBytes(TestKit.ChunkSize * 5 / 2, 11));
        b.WriteFile(y, TestKit.RandomBytes(TestKit.ChunkSize * 5 / 2, 12));
        await b.SettleAsync();
        await b.Sync.CollectGarbageAsync(default);
        await b.Sync.StopAsync(true, default);

        await a.Engine.DisposeAsync();                        // no local copies
        Directory.Delete(Path.Combine(a.Dir, "cache"), recursive: true);
        a.Db.SetMeta("verify_outbox", "1");
        int calls = 0;
        var real = a.Sync.RecoverChunk!;
        a.Sync.RecoverChunk = async (c, ct) =>
        {
            if (++calls == 3) throw new OperationCanceledException("shutdown");
            return await real(c, ct);
        };
        await a.Sync.TakeDriveAsync(force: false, default);
        Assert.True(a.Sync.Writable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.Sync.FlushAsync(default));
        await a.Sync.FlushAsync(default);                     // next try (e.g. after restart)

        var c = kit.Pc("c", so: Opts("PC-C"));
        await c.Sync.StartAsync(false, default);
        var names = $"{c.Db.Get(x)!.Name} / {c.Db.Get(y)!.Name}";
        Assert.True(c.Db.Get(x)!.Name.Contains("(incomplete)") && c.Db.Get(y)!.Name.Contains("(incomplete)"), "names on C: " + names);
    }

    // F4: an edit still open/pending while away is committed AFTER the one-time check: its unchanged chunks come from
    // the stale committed base (deleted by the writer's GC) and the ContentOp is fresh (at=now) -> never checked.
    [Fact]
    public async Task PendingEditCommittedAfterCheckIsNotVerified()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Opts("PC-A"));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("photo.raw");
        var data = TestKit.RandomBytes(TestKit.ChunkSize * 5 / 2, 7);
        a.WriteFile(f, data);
        await a.SettleAsync();
        var edit = TestKit.RandomBytes(100, 8);
        a.Engine.Opened(f, true);                             // still being edited / cannot upload (offline)
        a.Engine.Write(f, 2L * TestKit.ChunkSize + 10, edit);
        var expected = (byte[])data.Clone();
        edit.CopyTo(expected, 2 * TestKit.ChunkSize + 10);

        var b = kit.Pc("b", so: Opts("PC-B", autoFlush: true));
        await b.Sync.StartAsync(false, default);
        await b.Sync.TakeDriveAsync(force: true, default);
        b.WriteFile(f, TestKit.RandomBytes(data.Length, 99));
        await b.SettleAsync();
        await b.Sync.CollectGarbageAsync(default);
        await b.Sync.StopAsync(true, default);

        a.Db.SetMeta("caught_up_at", "1");                     // as after a long time away
        await a.Sync.TakeDriveAsync(force: false, default);
        Assert.True(a.Sync.Writable);
        await a.Sync.FlushAsync(default);                     // the check runs (nothing to check yet)
        a.Engine.Closed(f, true);                             // upload done, commit
        await a.SettleAsync();

        var c = kit.Pc("c", so: Opts("PC-C"));
        await c.Sync.StartAsync(false, default);
        var got = TryRead(c, f);
        Assert.True(got is not null && got.AsSpan().SequenceEqual(expected), "A's edit arrived broken on C: " + LastErr);
    }

    // F5: a message claimed for deletion (pending_gc) stays queued even after a journal entry makes its chunk used
    // again; the next CollectGarbage deletes a chunk that a live file references.
    [Fact]
    public void ClaimedDeletionIsNotCancelledWhenChunkIsReferencedAgain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-cr10-" + Guid.NewGuid().ToString("N"));
        using var db = IndexDb.Open(Path.Combine(dir, "index.db"));
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var chunk = new ChunkRef(new string('a', 64), 777, 10, new string('b', 64));
        db.ApplyRemote(new UpsertOp("f", IndexDb.RootId, "f", false, 0x20, now, now));
        db.ApplyRemote(new ContentOp("f", 10, now, [chunk]));
        db.ApplyRemote(new PurgeOp("f", ["f"]));
        Assert.Equal(1, db.ClaimGarbage(TimeSpan.Zero));     // claimed; Telegram delete fails / PC loses the lease
        // another PC's (held-back) entry still uses that very chunk message
        db.ApplyRemote(new UpsertOp("g", IndexDb.RootId, "g", false, 0x20, now, now));
        db.ApplyRemote(new ContentOp("g", 10, now, [chunk]));
        Assert.Equal(777, db.CommittedChunks("g").Single().Msg);
        Assert.DoesNotContain(777, db.PendingDeletions());   // would be deleted on the next GC run
    }

    // F6: a held-back edit lands on a file another PC trashed long ago: the rebased edit stays in the trash with the
    // OLD trash time, and the next trash expiry purges it at once — the newest version is gone for good.
    [Fact]
    public async Task HeldEditOnOldTrashedFileIsPurgedImmediately()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Opts("PC-A"));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("thesis.docx");
        a.WriteFile(f, "v1"u8.ToArray());
        await a.SettleAsync();
        a.WriteFile(f, "two weeks of offline work"u8.ToArray());   // held back on A
        await a.Engine.DrainAsync(null, default);

        var b = kit.Pc("b", so: Opts("PC-B", autoFlush: true));
        await b.Sync.StartAsync(false, default);
        await b.Sync.TakeDriveAsync(force: true, default);
        b.Db.ApplyLocal(new TrashOp(f, DateTime.UtcNow.AddDays(-31).ToFileTimeUtc())); // trashed 31 days ago
        await b.SettleAsync();
        await b.Sync.StopAsync(true, default);                // B switched off before its trash expiry ran

        await a.Sync.TakeDriveAsync(force: false, default);
        Assert.True(a.Sync.Writable);
        await a.SettleAsync();
        await a.Sync.CollectGarbageAsync(default);           // trash expiry
        Assert.NotNull(a.Db.Get(f));                          // the edit from minutes ago must not be destroyed
    }
}
