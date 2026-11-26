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
        await b.Engine.PrepareAsync(kept, null, default);
        Assert.Equal(CS * 2, b.ReadFile(temp).Length); // a temporary copy
        b.Engine.Read(seen, 0, new byte[4096], allowRemote: true, preview: true); // a thumbnail's read
        Assert.True(b.Engine.LocalFraction(temp) > 0.99);
        var dir = b.Dir;
        await b.DisposeAsync(); // the drive is disconnected

        var prepared = OfflineCache.Prepared(dir, "T:");
        Assert.Equal(["movie.mkv"], prepared.Select(p => p.Name));
        Assert.Equal(@"T:\movie.mkv", prepared[0].Path);
        Assert.True(OfflineCache.FreeTemporary(dir) > 0);

        // connected again: the prepared file is all here (no download), the other one is not, the preview is
        var again = kit.Pc("b");
        await again.Sync.StartAsync(false, default);
        Assert.True(again.Engine.IsFullyLocal(kept));
        Assert.True(again.Engine.LocalFraction(temp) < 0.01);
        long before = again.Engine.Status.BytesDownloaded;
        again.Engine.Read(seen, 0, new byte[4096], allowRemote: true, preview: true);
        Assert.Equal(before, again.Engine.Status.BytesDownloaded);
        kit.Remote.Latency = 0;
        Assert.Equal(keptData, again.ReadFile(kept));
    }
}
