using System.Text;
using DriveGram.Crypto;
using DriveGram.Index;
using DriveGram.Remote;
using DriveGram.Sync;

namespace DriveGram.Tests;

sealed class FlakyRemote(IRemote inner) : IRemote
{
    public volatile int FailReadOf = -1;
    public Task<int> UploadChunkAsync(string name, byte[] payload, CancellationToken ct) => inner.UploadChunkAsync(name, payload, ct);
    public Task<byte[]> ReadWindowAsync(int msg, int window, CancellationToken ct) => inner.ReadWindowAsync(msg, window, ct);
    public Task DeleteChunksAsync(IReadOnlyList<int> msgs, CancellationToken ct) => inner.DeleteChunksAsync(msgs, ct);
    public Task<int> AppendJournalAsync(string name, byte[] payload, CancellationToken ct) => inner.AppendJournalAsync(name, payload, ct);
    public Task<IReadOnlyList<JournalItem>> ListJournalAsync(int afterId, CancellationToken ct) => inner.ListJournalAsync(afterId, ct);
    public Task<byte[]> ReadJournalAsync(int id, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref FailReadOf, -1, id) == id) throw new IOException("network dropped");
        return inner.ReadJournalAsync(id, ct);
    }
    public Task<string?> ReadControlAsync(CancellationToken ct) => inner.ReadControlAsync(ct);
    public Task WriteControlAsync(string text, CancellationToken ct) => inner.WriteControlAsync(text, ct);
    public DateTime? ControlEditedAtUtc => inner.ControlEditedAtUtc;
    public DateTime ServerNowUtc => inner.ServerNowUtc;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class Cr9SyncTests
{
    static SyncOptions Fast(string name) => new() { DeviceName = name, FlushInterval = TimeSpan.FromMilliseconds(100) };
    static string Text(Pc pc, string id) => Encoding.UTF8.GetString(pc.ReadFile(id));

    // T3: a network error while re-reading an own entry that follows a foreign one loses the "replaying"
    // state: the next pass skips the own entry, and this PC keeps the foreign version for good.
    [Fact]
    public async Task ReplayStateSurvivesANetworkErrorMidPass()
    {
        await using var kit = await TestKit.CreateAsync();
        var flaky = new FlakyRemote(kit.Remote);
        var aDir = Path.Combine(kit.Root, "a");
        var a = new Pc(aDir, flaky, kit.Keys, so: Fast("PC-A"));
        try
        {
            await a.Sync.StartAsync(false, default);
            var f = a.NewFile("report.txt");
            a.WriteFile(f, "v1"u8.ToArray());
            await a.SettleAsync();

            var b = kit.Pc("b", so: Fast("PC-B"));
            await b.Sync.StartAsync(false, default);
            await b.Sync.TakeDriveAsync(force: true, default);
            b.WriteFile(f, "B's version"u8.ToArray());
            await b.SettleAsync();                               // F1 (B) first in the journal

            a.WriteFile(f, "A's version"u8.ToArray());           // A still trusts its lease (<10 s)
            await a.SettleAsync();                               // O (A) after F1
            var ownId = (await kit.Remote.ListJournalAsync(0, default)).Last(j => j.Name.StartsWith("j-" + a.Db.DeviceId[..8])).Id;

            flaky.FailReadOf = ownId;
            await Assert.ThrowsAnyAsync<IOException>(() => a.Sync.CatchUpAsync(default)); // drops mid-pass
            await a.Sync.CatchUpAsync(default);                  // next pass

            var c = kit.Pc("c", so: Fast("PC-C"));
            await c.Sync.StartAsync(false, default);
            Assert.Equal(Text(c, f), Text(a, f));                // same as every other PC (the replay state survives the error)
        }
        finally { await a.DisposeAsync(); }
    }
}

public class Cr9GcTests
{
    static SyncOptions Fast(string name) => new() { DeviceName = name, FlushInterval = TimeSpan.FromMilliseconds(100), GarbageGrace = TimeSpan.Zero };
    static string Text(Pc pc, string id) => Encoding.UTF8.GetString(pc.ReadFile(id));

    // T3b: the diverged PC later writes and collects garbage: it deletes the chunks every other PC uses.
    [Fact]
    public async Task DivergedPcGcDeletesChunksOthersUse()
    {
        await using var kit = await TestKit.CreateAsync();
        var flaky = new FlakyRemote(kit.Remote);
        var a = new Pc(Path.Combine(kit.Root, "a"), flaky, kit.Keys, so: Fast("PC-A"));
        try
        {
            await a.Sync.StartAsync(false, default);
            var f = a.NewFile("report.txt");
            a.WriteFile(f, "v1"u8.ToArray());
            await a.SettleAsync();
            var b = kit.Pc("b", so: Fast("PC-B"));
            await b.Sync.StartAsync(false, default);
            await b.Sync.TakeDriveAsync(force: true, default);
            b.WriteFile(f, "B's version"u8.ToArray());
            await b.SettleAsync();
            a.WriteFile(f, "A's version"u8.ToArray());
            await a.SettleAsync();
            var ownId = (await kit.Remote.ListJournalAsync(0, default)).Last(j => j.Name.StartsWith("j-" + a.Db.DeviceId[..8])).Id;
            flaky.FailReadOf = ownId;
            await Assert.ThrowsAnyAsync<IOException>(() => a.Sync.CatchUpAsync(default));
            await a.Sync.CatchUpAsync(default);
            await b.Sync.CatchUpAsync(default);
            await b.Sync.StopAsync(true, default);

            await a.Sync.TakeDriveAsync(force: false, default);
            Assert.True(a.Sync.Writable);
            await a.Sync.CollectGarbageAsync(default);

            var c = kit.Pc("c", so: Fast("PC-C"));
            await c.Sync.StartAsync(false, default);
            Assert.Equal("A's version", Text(c, f));             // A's GC kept the chunk C needs
        }
        finally { await a.DisposeAsync(); }
    }
}
