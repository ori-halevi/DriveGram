using System.Text;
using DriveGram.Crypto;
using DriveGram.Index;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>An old DriveGram meeting a drive written by a newer one must stop safely, never crash or write.</summary>
public class FormatTests
{
    [Fact]
    public async Task NewerWriterFormatMakesTheDriveReadOnly()
    {
        await using var kit = await TestKit.CreateAsync();
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        await kit.Remote.WriteControlAsync((ctrl with { MinWriter = StorageFormat.Current + 1 }).ToJson(), default);

        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        Assert.True(a.Sync.NeedsUpdate);
        Assert.False(a.Sync.Writable);
        Assert.Null((await SyncService.ReadControlAsync(kit.Remote, default))!.Lease); // took no lease either
    }

    [Fact]
    public async Task NewerReaderFormatRefusesToOpen()
    {
        await using var kit = await TestKit.CreateAsync();
        var ctrl = (await SyncService.ReadControlAsync(kit.Remote, default))!;
        await kit.Remote.WriteControlAsync((ctrl with { MinReader = StorageFormat.Current + 1 }).ToJson(), default);
        var a = kit.Pc("a");
        await Assert.ThrowsAsync<NeedsUpdateException>(() => a.Sync.StartAsync(false, default));
    }

    [Fact]
    public async Task AnUnknownJournalEntryStopsReplayWithoutSkippingIt()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        a.WriteFile(a.NewFile("before.txt"), "x"u8.ToArray());
        await a.SettleAsync();
        await a.Sync.StopAsync(true, default);

        // a newer DriveGram journals an op type this version does not know, then an ordinary one
        var future = """{"v":1,"dev":"future-pc","ts":0,"ops":[{"t":"zz-new-op","id":"n1"}]}""";
        int unknownId = await kit.Remote.AppendJournalAsync("j-future.bin", Envelope.Seal(kit.Keys.JournalKey, Encoding.UTF8.GetBytes(future)), default);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var later = OpJson.SerializeEntry(new JournalEntry(1, "future-pc", 0, [new UpsertOp("n2", IndexDb.RootId, "after.txt", false, 0x20, now, now)]));
        await kit.Remote.AppendJournalAsync("j-future.bin", Envelope.Seal(kit.Keys.JournalKey, later), default);

        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        Assert.True(b.Sync.NeedsUpdate);
        Assert.False(b.Sync.Writable);
        Assert.True(b.Db.Applied < unknownId);            // nothing past the unknown entry was taken as applied
        Assert.NotNull(b.Db.Resolve(@"\before.txt"));
        Assert.Null(b.Db.Resolve(@"\after.txt"));         // and nothing after it was applied out of order
    }

    [Fact]
    public async Task AJournalEntryFromANewerFormatStopsReplay()
    {
        await using var kit = await TestKit.CreateAsync();
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var entry = OpJson.SerializeEntry(new JournalEntry(StorageFormat.Current + 1, "future-pc", 0,
            [new UpsertOp("n1", IndexDb.RootId, "x.txt", false, 0x20, now, now)]));
        await kit.Remote.AppendJournalAsync("j-future.bin", Envelope.Seal(kit.Keys.JournalKey, entry), default);
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        Assert.True(a.Sync.NeedsUpdate);
        Assert.Null(a.Db.Resolve(@"\x.txt"));
    }
}
