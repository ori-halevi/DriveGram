using DriveGram.Engine;

namespace DriveGram.Tests;

/// <summary>Files prepared on this PC ahead of time, how much of a file is here, and pausing uploads.</summary>
public class PrepareAndPauseTests
{
    const int CS = TestKit.ChunkSize;

    [Fact]
    public async Task APreparedFileStaysHereUntilFreed()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var data = TestKit.RandomBytes(CS * 3, 21);
        var id = a.NewFile("movie.mkv");
        a.WriteFile(id, data);
        await a.SettleAsync();

        // another PC with a cache far smaller than the file
        var b = kit.Pc("b", new EngineOptions
        {
            CommitDelay = TimeSpan.FromMilliseconds(50),
            SealIdle = TimeSpan.FromMilliseconds(100),
            CacheQuota = 1L << 20,
        });
        await b.Sync.StartAsync(false, default);
        Assert.Equal(0, b.Engine.LocalFraction(id));

        await b.Engine.PrepareAsync(id, null, default);
        Assert.Equal(1, b.Engine.LocalFraction(id), 3);
        Assert.True(b.Engine.IsPrepared(id));

        // other reads do not push it out of the (tiny) cache
        var other = a.NewFile("other.bin");
        a.WriteFile(other, TestKit.RandomBytes(CS * 2, 22));
        await a.SettleAsync();
        await b.Sync.StartAsync(false, default);
        Assert.Equal(CS * 2, b.ReadFile(other).Length);
        Assert.Equal(1, b.Engine.LocalFraction(id), 3);

        kit.Remote.Latency = 0;
        Assert.Equal(data, b.ReadFile(id));

        Assert.True(b.Engine.FreeLocal(id) > 0);
        Assert.False(b.Engine.IsPrepared(id));
        Assert.Equal(0, b.Engine.LocalFraction(id));
        Assert.Equal(data, b.ReadFile(id)); // still readable: downloaded again
    }

    [Fact]
    public async Task HalfReadFileIsPartlyHere()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var data = TestKit.RandomBytes(CS * 4, 23);
        var id = a.NewFile("video.mp4");
        a.WriteFile(id, data);
        await a.SettleAsync();

        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        var buf = new byte[CS * 2];
        b.Engine.Read(id, 0, buf); // "watched" the first half
        double f = b.Engine.LocalFraction(id);
        Assert.InRange(f, 0.45, 0.8); // the first half, plus some read-ahead
    }

    [Fact]
    public async Task PausedUploadsWaitAndThenGoOn()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        int before = kit.Remote.StoredChunkCount;
        a.Engine.PausedAll = true;
        var data = TestKit.RandomBytes(CS * 2, 24);
        var id = a.NewFile("paused.bin");
        a.WriteFile(id, data);
        await Task.Delay(1500);
        Assert.Equal(before, kit.Remote.StoredChunkCount); // nothing went up
        var item = Assert.Single(a.Engine.Uploads());
        Assert.Equal(UploadState.Paused, item.State);
        Assert.Equal(0, item.Done);
        Assert.Equal(CS * 2, item.Total);

        a.Engine.ResumeAll();
        await a.SettleAsync();
        Assert.Empty(a.Engine.Uploads());
        Assert.True(kit.Remote.StoredChunkCount >= before + 2);
    }

    [Fact]
    public async Task OnePausedFileDoesNotHoldTheOthers()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        var held = a.NewFile("held.bin");
        a.Engine.Pause(held);
        a.WriteFile(held, TestKit.RandomBytes(CS, 25));
        var free = a.NewFile("free.bin");
        a.WriteFile(free, TestKit.RandomBytes(CS, 26));
        for (int i = 0; i < 300 && a.Engine.Uploads().Any(u => u.Id == free); i++) await Task.Delay(100);
        var left = Assert.Single(a.Engine.Uploads());
        Assert.Equal(held, left.Id);
        a.Engine.Resume(held);
        await a.SettleAsync();
        Assert.Empty(a.Engine.Uploads());
    }

    [Fact]
    public async Task ResumingOneFileWhileAllArePausedMovesOnlyThatFile()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        a.Engine.PausedAll = true; // e.g. the daily cap
        var chosen = a.NewFile("chosen.bin");
        a.WriteFile(chosen, TestKit.RandomBytes(CS, 27));
        var other = a.NewFile("other.bin");
        a.WriteFile(other, TestKit.RandomBytes(CS, 28));
        a.Engine.Resume(chosen);
        for (int i = 0; i < 300 && a.Engine.Uploads().Any(u => u.Id == chosen); i++) await Task.Delay(100);
        Assert.DoesNotContain(a.Engine.Uploads(), u => u.Id == chosen);
        // the rest stays paused, and so does a file copied in afterwards
        var later = a.NewFile("later.bin");
        a.WriteFile(later, TestKit.RandomBytes(CS, 29));
        await Task.Delay(1500);
        Assert.Equal(new[] { other, later }.Order(), a.Engine.Uploads().Select(u => u.Id).Order());
        Assert.All(a.Engine.Uploads(), u => Assert.Equal(UploadState.Paused, u.State));
        a.Engine.ResumeAll();
        await a.SettleAsync();
        Assert.Empty(a.Engine.Uploads());
    }

    [Fact]
    public async Task APausedOpenFileDoesNotHoldTheSealingOfOthers()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", new EngineOptions { SealIdle = TimeSpan.FromMilliseconds(50), CacheQuota = 64L << 20 });
        int before = kit.Remote.StoredChunkCount;
        var held = a.NewFile("held.mkv");
        a.Engine.Opened(held, true);
        a.Engine.Pause(held);
        var heldData = TestKit.RandomBytes(CS * 2, 30);
        a.Engine.Write(held, 0, heldData);
        var going = a.NewFile("going.mkv");
        a.Engine.Opened(going, true);
        a.Engine.Write(going, 0, TestKit.RandomBytes(CS * 2, 31));
        // both files stay open: only the seal loop uploads the full pieces of "going"
        for (int i = 0; i < 300 && kit.Remote.StoredChunkCount < before + 2; i++) await Task.Delay(100);
        Assert.True(kit.Remote.StoredChunkCount >= before + 2, "the open file that is not paused was not sealed");
        a.Engine.Resume(held);
    }

    [Fact]
    public async Task WhilePausedACopyBiggerThanStagingIsTaken()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", new EngineOptions
        {
            StagingQuota = CS * 2,
            SealIdle = TimeSpan.FromMilliseconds(50),
            CacheQuota = 64L << 20,
            BackpressureTimeout = TimeSpan.FromMilliseconds(300),
        });
        a.Engine.PausedAll = true; // nothing uploads: the files wait on this PC (the disk has room)
        var id = a.NewFile("big.mkv");
        a.Engine.Opened(id, true);
        var data = TestKit.RandomBytes(CS * 5, 32);
        for (int i = 0; i < data.Length; i += 512 * 1024) a.Engine.Write(id, i, data.AsSpan(i, 512 * 1024));
        a.Engine.ResumeAll();
    }

    [Fact]
    public async Task CancellingTheUploadOfAChangedFileBringsBackTheSavedVersion()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var saved = TestKit.RandomBytes(CS + 1000, 34);
        var id = a.NewFile("video.mkv");
        a.WriteFile(id, saved);
        await a.SettleAsync();

        a.Engine.Pause(id); // the change waits here, not uploaded
        var changed = TestKit.RandomBytes(CS * 2 + 5, 35);
        a.WriteFile(id, changed);
        await Task.Delay(500);
        var item = Assert.Single(a.Engine.Uploads());
        Assert.True(item.Saved);

        a.Engine.Forget(id);
        Assert.True(a.Engine.DropLocalChange(id));
        Assert.Empty(a.Engine.Uploads());
        Assert.Equal(saved, a.ReadFile(id)); // the file is still there, as saved in Telegram
        await a.SettleAsync();
        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        Assert.Equal(saved, b.ReadFile(id));

        // a new file was never saved: nothing to go back to
        var fresh = a.NewFile("new.bin");
        a.Engine.Pause(fresh);
        a.WriteFile(fresh, TestKit.RandomBytes(1000, 36));
        Assert.False(a.Engine.DropLocalChange(fresh));
        Assert.False(Assert.Single(a.Engine.Uploads()).Saved);
        a.Engine.Resume(fresh);
    }

    [Fact]
    public async Task AFilePausedByItselfStaysPausedAfterARestart()
    {
        await using var kit = await TestKit.CreateAsync();
        int before = kit.Remote.StoredChunkCount;
        var a = kit.Pc("a");
        var held = a.NewFile("held.bin");
        a.Engine.Pause(held);
        a.WriteFile(held, TestKit.RandomBytes(CS, 37));
        await Task.Delay(500);
        await a.DisposeAsync(); // the PC restarts

        var again = kit.Pc("a");
        await Task.Delay(1500);
        Assert.True(again.Engine.IsPaused(held));
        Assert.Equal(before, kit.Remote.StoredChunkCount); // still nothing went up
        again.Engine.Resume(held);
        await again.SettleAsync();
        Assert.Empty(again.Engine.Uploads());
    }

    [Fact]
    public async Task PauseHoldsFilesAlreadyWaitingInLine()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", new EngineOptions { UploadConcurrency = 1, CommitDelay = TimeSpan.FromMilliseconds(20) });
        kit.Remote.Latency = 1500; // slow uploads: the files queue up behind the first ones
        int before = kit.Remote.StoredChunkCount;
        for (int i = 0; i < 8; i++)
        {
            var id = a.NewFile($"f{i}.bin");
            a.WriteFile(id, TestKit.RandomBytes(CS / 4, 40 + i));
        }
        await Task.Delay(300); // all closed and queued for upload
        a.Engine.PausedAll = true;
        await Task.Delay(14000); // long enough for the whole line to go up if the pause did not hold it
        // only what was already being sent may finish (a few pieces), not the whole line
        Assert.InRange(kit.Remote.StoredChunkCount - before, 0, 4);
        kit.Remote.Latency = 0;
        a.Engine.ResumeAll();
        await a.SettleAsync();
        Assert.Empty(a.Engine.Uploads());
    }

    [Fact]
    public async Task ThousandsOfPausedNewFilesDoNotHoldTheChangesBehindThem()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        // a folder of thousands of new files, all waiting (paused): their changes fill the front of the queue
        a.Db.InTransaction(() =>
        {
            for (int i = 0; i < 5200; i++) a.Db.SetPending(a.NewFile($"photo{i}.jpg"));
        });
        var after = a.NewDir("made later"); // a change that can go out now
        var batch = a.Db.PeekSendable(500);
        Assert.Contains(batch, b => b.Op.Id == after);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WaitingForUploadsDoesNotWaitForPausedFiles()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        var held = a.NewFile("held.bin");
        a.Engine.Pause(held);
        a.WriteFile(held, TestKit.RandomBytes(CS, 50));
        var free = a.NewFile("free.bin");
        a.WriteFile(free, TestKit.RandomBytes(CS, 51));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await a.Engine.DrainAsync(null, cts.Token); // returns once the files that are not paused went up
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(held, Assert.Single(a.Engine.Uploads()).Id);
        Assert.True(a.Engine.IsPaused(held)); // and it is still paused

        a.Engine.ForceUploads = true; // a handover: everything goes up
        await a.Engine.DrainAsync(null, cts.Token);
        Assert.Empty(a.Engine.Uploads());
        Assert.False(a.Engine.IsPausedByItself(held)); // it went up: a later change of it is not held
    }

    [Fact]
    public async Task APrepareCutShortIsNotKept()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a");
        await a.Sync.StartAsync(false, default);
        var id = a.NewFile("movie.mkv");
        a.WriteFile(id, TestKit.RandomBytes(CS * 3, 33));
        await a.SettleAsync();
        var b = kit.Pc("b");
        await b.Sync.StartAsync(false, default);
        kit.Remote.Latency = 2000; // slow downloads: the prepare is still running when it is cancelled
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Engine.PrepareAsync(id, null, cts.Token));
        Assert.False(b.Engine.IsPrepared(id)); // what came down is ordinary temporary cache
        kit.Remote.Latency = 0;
    }
}
