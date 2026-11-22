using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>A PC that asked for the drive and vanished right after it was handed over: the giving PC
/// notices, and can take the drive back.</summary>
public class HandoverClaimTests
{
    static SyncOptions Fast(string name) => new()
    {
        DeviceName = name,
        FlushInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = TimeSpan.FromMinutes(1),
    };

    static async Task<(TestKit Kit, Pc A, Pc B, HandoverRequest Req)> HandedToB()
    {
        var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);
        HandoverRequest? seen = null;
        a.Sync.HandoverRequested += r => seen = r;
        await b.Sync.RequestHandoverAsync(default);
        for (int i = 0; i < 400 && seen is null; i++) await Task.Delay(50);
        Assert.NotNull(seen);
        Assert.True(await a.Sync.BeginHandoverAsync(seen!, 0, default));
        Assert.True(await a.Sync.CompleteHandoverAsync(seen!, default));
        Assert.False(a.Sync.Writable);
        return (kit, a, b, seen!);
    }

    [Fact]
    public async Task ADriveNobodyTookIsNoticedAndCanBeTakenBack()
    {
        var (kit, a, b, req) = await HandedToB();
        await using var _ = kit;
        await b.Sync.StopAsync(false, default);                 // B vanished before taking it
        Assert.True(await a.Sync.HandoverUnclaimedAsync(req, default));
        await a.Sync.TakeDriveAsync(force: true, default);
        Assert.True(a.Sync.Writable);
        Assert.False(await a.Sync.HandoverUnclaimedAsync(req, default));
    }

    [Fact]
    public async Task ADriveThatArrivedIsNotReportedAsUnclaimed()
    {
        var (kit, a, b, req) = await HandedToB();
        await using var _ = kit;
        await b.Sync.TakeDriveAsync(force: false, default);
        Assert.True(b.Sync.Writable);
        Assert.False(await a.Sync.HandoverUnclaimedAsync(req, default));
    }
}
