using DriveGram.Engine;
using DriveGram.Host;

namespace DriveGram.Tests;

/// <summary>A drive that is not connected: the files prepared on this PC are listed from the local index,
/// and freeing deletes only the temporary copies (never a prepared file's pieces, never the previews).</summary>
public class OfflineCacheTests
{
    const int CS = TestKit.ChunkSize;

    [Fact]
    public async Task FreeingWhileDisconnectedKeepsPreparedFilesAndPreviews()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var kept = a.NewFile("movie.mkv");
        var temp = a.NewFile("other.bin");
        var seen = a.NewFile("clip.mp4");
        var keptData = TestKit.RandomBytes(CS * 2, 51);
        a.WriteFile(kept, keptData);
        a.WriteFile(temp, TestKit.RandomBytes(CS * 2, 52));
        a.WriteFile(seen, TestKit.RandomBytes(CS, 53));
        await a.SettleAsync();

        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        // a file written on this PC and prepared: its pieces are plain copies (cache\p), not windows
        var mine = b.NewFile("mine.doc");
        var mineData = TestKit.RandomBytes(CS + 1000, 54);
        b.WriteFile(mine, mineData);
        await b.SettleAsync();
        await b.Engine.PrepareAsync(mine, null, default);
        await b.Engine.PrepareAsync(kept, null, default);
        Assert.Equal(CS * 2, b.ReadFile(temp).Length); // a temporary copy
        b.Engine.Read(seen, 0, new byte[4096], allowRemote: true, preview: true); // a thumbnail's read
        Assert.True(b.Engine.LocalFraction(temp) > 0.99);
        var dir = b.Dir;
        await b.DisposeAsync(); // the drive is disconnected

        var prepared = OfflineCache.Prepared(dir, "T:").OrderBy(p => p.Name).ToList();
        Assert.Equal(["mine.doc", "movie.mkv"], prepared.Select(p => p.Name));
        Assert.Equal(@"T:\movie.mkv", prepared[1].Path);
        Assert.All(prepared, p => Assert.Equal(1, p.Fraction, 3));
        long size = OfflineCache.TemporaryBytes(dir);
        Assert.True(size > 0);
        Assert.Equal(size, OfflineCache.FreeTemporary(dir)); // what the question says is what is freed

        // connected again: the prepared files are all here (no download), the other one is not, the preview is
        var again = kit.Pc("b");
        await again.Sync.StartAsync(false, default);
        Assert.True(again.Engine.IsFullyLocal(kept));
        Assert.True(again.Engine.IsFullyLocal(mine));
        Assert.True(again.Engine.LocalFraction(temp) < 0.01);
        long before = again.Engine.Status.BytesDownloaded;
        again.Engine.Read(seen, 0, new byte[4096], allowRemote: true, preview: true);
        kit.Remote.Latency = 0;
        Assert.Equal(keptData, again.ReadFile(kept));
        Assert.Equal(mineData, again.ReadFile(mine));
        Assert.Equal(before, again.Engine.Status.BytesDownloaded); // none of it came from Telegram
    }

    [Fact]
    public async Task APreparedFileCanBeFreedWhileDisconnected()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var one = a.NewFile("one.mkv");
        var two = a.NewFile("two.mkv");
        a.WriteFile(one, TestKit.RandomBytes(CS * 2, 61));
        a.WriteFile(two, TestKit.RandomBytes(CS * 2, 62));
        await a.SettleAsync();
        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        await b.Engine.PrepareAsync(one, null, default);
        await b.Engine.PrepareAsync(two, null, default);
        var dir = b.Dir;
        await b.DisposeAsync();

        Assert.True(OfflineCache.FreePrepared(dir, [one]) > 0);
        Assert.Equal(["two.mkv"], OfflineCache.Prepared(dir, "T:").Select(p => p.Name));

        var again = kit.Pc("b");
        await again.Sync.StartAsync(false, default);
        Assert.False(again.Engine.IsPrepared(one));
        Assert.True(again.Engine.LocalFraction(one) < 0.01);
        Assert.True(again.Engine.IsFullyLocal(two));
    }

    [Fact]
    public async Task NothingIsFreedWhileTheDriveIsOpen()
    {
        await using var kit = await TestKit.CreateAsync();
        var b = kit.Pc("b");
        using var mounted = new Semaphore(1, 1, DriveSession.InstanceName(b.Dir));
        Assert.True(mounted.WaitOne(0)); // as a mounted drive holds it
        Assert.Throws<OfflineCache.DriveInUseException>(() => OfflineCache.FreeTemporary(b.Dir));
        mounted.Release();
    }
}
