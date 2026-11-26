using DriveGram.Engine;

namespace DriveGram.Tests;

/// <summary>What Explorer reads for thumbnails and details is kept apart: freeing a file leaves it, browsing
/// again downloads nothing, and it does not count as the file being here.</summary>
public class PreviewTests
{
    const int CS = TestKit.ChunkSize;

    static async Task<(TestKit Kit, Pc B, string Id, byte[] Data)> TwoPcsAsync()
    {
        var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var data = TestKit.RandomBytes(CS * 3, 41);
        var id = a.NewFile("clip.mp4");
        a.WriteFile(id, data);
        await a.SettleAsync();
        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        return (kit, b, id, data);
    }

    static byte[] PreviewRead(Pc pc, string id, long offset, int len)
    {
        var buf = new byte[len];
        pc.Engine.Read(id, offset, buf, allowRemote: true, preview: true);
        return buf;
    }

    [Fact]
    public async Task FreeingKeepsThePreviewSoBrowsingAgainDownloadsNothing()
    {
        var (kit, b, id, data) = await TwoPcsAsync();
        await using var _ = kit;

        // Explorer reads the start and the end (a video's header and index)
        Assert.Equal(data[..4096], PreviewRead(b, id, 0, 4096));
        Assert.Equal(data[^4096..], PreviewRead(b, id, data.Length - 4096, 4096));
        long after = b.Engine.Status.BytesDownloaded;
        Assert.True(after > 0);

        // a preview is not "the file is here" (no mark in Explorer), and freeing leaves it
        Assert.Equal(0, b.Engine.LocalFraction(id));
        b.Engine.FreeLocal(id);
        b.Engine.ClearCache();

        // browsing again: nothing is downloaded
        Assert.Equal(data[..4096], PreviewRead(b, id, 0, 4096));
        Assert.Equal(data[^4096..], PreviewRead(b, id, data.Length - 4096, 4096));
        Assert.Equal(after, b.Engine.Status.BytesDownloaded);
    }

    [Fact]
    public async Task AProgramReadingTheFileUsesThePreviewAndCachesItAsUsual()
    {
        var (kit, b, id, data) = await TwoPcsAsync();
        await using var _ = kit;
        PreviewRead(b, id, 0, 4096);
        long after = b.Engine.Status.BytesDownloaded;

        // a program reads the same start: no new download, and now it counts as here
        var buf = new byte[4096];
        b.Engine.Read(id, 0, buf);
        Assert.Equal(data[..4096], buf);
        Assert.Equal(after, b.Engine.Status.BytesDownloaded);
        Assert.True(b.Engine.LocalFraction(id) > 0);

        // freed: the file's copy goes, the preview stays
        b.Engine.FreeLocal(id);
        Assert.Equal(0, b.Engine.LocalFraction(id));
        Assert.Equal(data[..4096], PreviewRead(b, id, 0, 4096));
        Assert.Equal(after, b.Engine.Status.BytesDownloaded);
    }

    [Fact]
    public async Task ThePreviewHasItsOwnQuota()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new BlockCache(dir, 100L << 20) { PreviewQuota = 3L << 20 };
            var mb = new byte[1 << 20];
            for (int w = 0; w < 5; w++) cache.PutWindow(7, w, mb, preview: true);
            Assert.True(cache.PreviewBytes <= 3L << 20);
            Assert.Equal(0, cache.Bytes); // not counted with the cache
            Assert.False(cache.HasAnyWindow(7, 0)); // the oldest went
            Assert.True(cache.HasAnyWindow(7, 4));
            Assert.False(cache.HasWindow(7, 4)); // a preview window is not "cached as part of the file"
            Assert.Equal(0, cache.Clear()); // "free up space" leaves the preview

            // a restart keeps them apart
            var again = new BlockCache(dir, 100L << 20) { PreviewQuota = 3L << 20 };
            Assert.Equal(0, again.Bytes);
            Assert.True(again.HasAnyWindow(7, 4));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
