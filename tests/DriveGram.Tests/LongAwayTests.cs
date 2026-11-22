using DriveGram.Index;
using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>
/// A PC away for a long time with an edit it could not send, while the writing PC deleted the file for
/// good and cleaned up its chunks on Telegram: the edit must not arrive as a broken file.
/// </summary>
public class LongAwayTests
{
    static SyncOptions Opts(string name, bool autoFlush) => new()
    {
        DeviceName = name,
        FlushInterval = autoFlush ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromHours(1),
        GarbageGrace = TimeSpan.Zero,
    };

    static async Task<(TestKit Kit, Pc A, string File, byte[] Expected)> HeldEditWhosePartsWereCollected()
    {
        var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Opts("PC-A", autoFlush: false));
        await a.Sync.StartAsync(false, default);
        var f = a.NewFile("photo.raw");
        var data = new byte[TestKit.ChunkSize * 5 / 2];
        new Random(7).NextBytes(data);
        a.WriteFile(f, data);
        await a.SettleAsync();                                    // sent: three chunks on Telegram

        // A edits only the end; the change waits on A (it is never sent before B takes the drive)
        var edit = new byte[100];
        new Random(8).NextBytes(edit);
        a.WriteFile(f, edit, offset: 2L * TestKit.ChunkSize + 10);
        await a.Engine.DrainAsync(null, default);
        var expected = (byte[])data.Clone();
        edit.CopyTo(expected, 2 * TestKit.ChunkSize + 10);

        var b = kit.Pc("b", so: Opts("PC-B", autoFlush: true));
        await b.Sync.StartAsync(false, default);
        await b.Sync.TakeDriveAsync(force: true, default);
        b.Db.ApplyLocal(new PurgeOp(f));                          // deleted for good on B
        await b.SettleAsync();
        await b.Sync.CollectGarbageAsync(default);                // nothing on B uses A's chunks: deleted
        await b.Sync.StopAsync(true, default);

        a.Db.SetMeta("verify_outbox", "1");                       // as after more than a day away
        return (kit, a, f, expected);
    }

    [Fact]
    public async Task AHeldEditWhosePartsWereDeletedIsCompletedFromThisPcsCopy()
    {
        var (kit, a, f, expected) = await HeldEditWhosePartsWereCollected();
        await using var _ = kit;
        await a.Sync.TakeDriveAsync(force: false, default);
        Assert.True(a.Sync.Writable);
        await a.SettleAsync();

        var c = kit.Pc("c");
        await c.Sync.StartAsync(false, default);
        Assert.Equal("photo.raw", c.Db.Get(f)!.Name);
        Assert.Equal(expected, c.ReadFile(f));                    // whole, on every PC
    }

    [Fact]
    public async Task WithoutACopyTheHeldEditIsKeptUnderAMarkedName()
    {
        var (kit, a, f, _) = await HeldEditWhosePartsWereCollected();
        await using var __ = kit;
        await a.Engine.DisposeAsync();                            // no local copies of the old parts
        Directory.Delete(Path.Combine(a.Dir, "cache"), recursive: true);
        string? told = null;
        a.Sync.FileIncomplete += (_, kept, _) => told = kept;
        await a.Sync.TakeDriveAsync(force: false, default);
        await a.Sync.FlushAsync(default);

        var c = kit.Pc("c");
        await c.Sync.StartAsync(false, default);
        var n = c.Db.Get(f)!;
        Assert.Equal("photo (incomplete).raw", n.Name);           // never a broken file under the real name
        Assert.Equal(TestKit.ChunkSize * 5 / 2, c.ReadFile(f).Length); // readable: zeros where parts were lost
        Assert.Equal("photo (incomplete).raw", told);
    }
}
