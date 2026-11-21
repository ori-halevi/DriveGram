using DriveGram.Engine;

namespace DriveGram.Tests;

public class EngineTests
{
    const int CS = TestKit.ChunkSize;

    [Fact]
    public async Task CopyInReadBackCommitAndReadFromRemote()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var data = TestKit.RandomBytes(CS * 3 + 12345, 1);
        var id = a.NewFile("big.bin");

        a.WriteFile(id, data);
        Assert.Equal(data, a.ReadFile(id));           // readable immediately, from local pieces

        await a.SettleAsync();
        Assert.False(a.Db.IsPending(id));
        Assert.Equal(4, a.Db.CommittedChunks(id).Count);
        Assert.Equal(data, a.ReadFile(id));           // from the local plain cache

        var b = kit.Pc("b");                          // a PC with an empty cache reads from the remote
        await b.Sync.StartAsync(false, default);
        Assert.Equal(data, b.ReadFile(id));
    }

    [Fact]
    public async Task EditingTheMiddleOfABigFileReuploadsOneChunk()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        var data = TestKit.RandomBytes(CS * 4, 2);
        var id = a.NewFile("video.mkv");
        a.WriteFile(id, data);
        await a.SettleAsync();
        int before = kit.Remote.StoredChunkCount;

        var patch = new byte[] { 1, 2, 3, 4, 5 };
        a.WriteFile(id, patch, offset: CS * 2 + 100);
        patch.CopyTo(data, CS * 2 + 100);
        await a.SettleAsync();

        Assert.Equal(before + 1, kit.Remote.StoredChunkCount);
        Assert.Equal(data, a.ReadFile(id));
    }

    [Fact]
    public async Task TruncateAndExtend()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        var data = TestKit.RandomBytes(CS * 2 + 500, 3);
        var id = a.NewFile("f");
        a.WriteFile(id, data);
        await a.SettleAsync();

        a.Engine.SetSize(id, CS + 10);
        await a.SettleAsync();
        Assert.Equal(data.AsSpan(0, CS + 10).ToArray(), a.ReadFile(id));

        a.Engine.SetSize(id, CS * 3);                 // grows with zeros
        await a.SettleAsync();
        var expected = new byte[CS * 3];
        data.AsSpan(0, CS + 10).CopyTo(expected);
        Assert.Equal(expected, a.ReadFile(id));

        a.Engine.SetSize(id, 0);
        await a.SettleAsync();
        Assert.Empty(a.ReadFile(id));
        Assert.Empty(a.Db.CommittedChunks(id));
    }

    [Fact]
    public async Task SparseFilesStoreOneZeroChunk()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        var id = a.NewFile("disk.img");
        a.Engine.SetSize(id, (long)CS * 50);         // like a preallocated image
        await a.SettleAsync();
        Assert.Equal(1, kit.Remote.StoredChunkCount); // 50 identical zero chunks -> stored once
        Assert.Equal(50, a.Db.CommittedChunks(id).Count);
        var buf = new byte[1000];
        Assert.Equal(1000, a.Engine.Read(id, (long)CS * 49 + 5, buf));
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task IdenticalFilesAreStoredOnce()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        var data = TestKit.RandomBytes(CS * 2, 4);
        a.WriteFile(a.NewFile("one"), data);
        await a.SettleAsync();
        int n = kit.Remote.StoredChunkCount;
        a.WriteFile(a.NewFile("two"), data);
        await a.SettleAsync();
        Assert.Equal(n, kit.Remote.StoredChunkCount);
    }

    [Fact]
    public async Task CrashBeforeCommitResumesOnNextStart()
    {
        await using var kit = await TestKit.CreateAsync();
        var data = TestKit.RandomBytes(CS * 2 + 77, 5);
        string id;
        string dir;
        {
            // engine never started: nothing uploads, like a PC that lost power right after the copy
            var a = kit.Pc("a", startEngine: false);
            dir = a.Dir;
            id = a.NewFile("unsaved.docx");
            a.WriteFile(id, data);
            Assert.True(a.Db.IsPending(id));
            await a.DisposeAsync();
        }
        await using var again = new Pc(dir, kit.Remote, kit.Keys);
        await again.SettleAsync();
        Assert.False(again.Db.IsPending(id));
        Assert.Equal(data, again.ReadFile(id));
    }

    [Fact]
    public async Task HugeCopyStaysWithinTheStagingQuota()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", new EngineOptions
        {
            StagingQuota = CS * 3,                    // far smaller than the file
            SealIdle = TimeSpan.FromMilliseconds(50),
            CommitDelay = TimeSpan.FromMilliseconds(50),
            CacheQuota = 64L << 20,
            BackpressureTimeout = TimeSpan.FromSeconds(60),
        });
        var data = TestKit.RandomBytes(CS * 12, 6);
        var id = a.NewFile("backup.tar");
        long peak = 0;
        a.Engine.Opened(id, true);
        for (int i = 0; i < data.Length; i += 256 * 1024)
        {
            a.Engine.Write(id, i, data.AsSpan(i, 256 * 1024));
            peak = Math.Max(peak, a.Engine.Status.StagedBytes);
        }
        a.Engine.Closed(id, true);
        await a.SettleAsync();
        Assert.True(peak <= CS * 4, $"staging peaked at {peak:N0} bytes");
        Assert.Equal(data, a.ReadFile(id));
    }
}
