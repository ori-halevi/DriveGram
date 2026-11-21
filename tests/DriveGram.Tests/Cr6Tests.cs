using DriveGram.Sync;

namespace DriveGram.Tests;

/// <summary>Regression tests for the sixth review (handover edge cases, shutdown release).</summary>
public class Cr6Tests
{
    static SyncOptions Fast(string name, int heartbeatMs = 60_000) => new()
    {
        DeviceName = name,
        FlushInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatInterval = TimeSpan.FromMilliseconds(heartbeatMs),
    };

    static async Task<HandoverRequest> Seen(Pc holder, Func<HandoverRequest?> got)
    {
        for (int i = 0; i < 400 && got() is null; i++) await Task.Delay(50);
        return got() ?? throw new TimeoutException("the request was never noticed");
    }

    [Fact]
    public async Task HandoverCompletesWhileANewFileIsStillOpenInAnApp()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);

        var f = a.NewFile("~$report.docx");                 // Word's owner file: new, and kept open
        a.Engine.Opened(f, true);
        a.Engine.Write(f, 0, "lock"u8);

        HandoverRequest? seen = null;
        a.Sync.HandoverRequested += r => seen = r;
        await b.Sync.RequestHandoverAsync(default);
        var req = await Seen(a, () => seen);
        Assert.True(await a.Sync.BeginHandoverAsync(req, 0, default));
        Assert.True(await a.Sync.CompleteHandoverAsync(req, default)); // the open file's ops wait on A
        await b.Sync.TakeDriveAsync(force: false, default);
        Assert.True(b.Sync.Writable);
        a.Engine.Closed(f, true);
    }

    [Fact]
    public async Task ACancelledRequestIsNeverHandedOver()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A"));
        await a.Sync.StartAsync(false, default);
        var b = kit.Pc("b", so: Fast("PC-B"));
        await b.Sync.StartAsync(false, default);

        HandoverRequest? seen = null;
        a.Sync.HandoverRequested += r => seen = r;
        var mine = await b.Sync.RequestHandoverAsync(default);
        var req = await Seen(a, () => seen);
        await b.Sync.CancelHandoverAsync(mine, default);         // B changed its mind

        Assert.False(await a.Sync.RequestStillOpenAsync(req, default));
        Assert.False(await a.Sync.BeginHandoverAsync(req, 0, default));
        Assert.False(a.Sync.HandingOver);
        Assert.True(a.Sync.Writable);
        Assert.Equal(a.Db.DeviceId, (await SyncService.ReadControlAsync(kit.Remote, default))!.Lease!.Dev);
    }

    [Fact]
    public async Task ALeaseReleasedForShutdownIsNotTakenBackByTheHeartbeat()
    {
        await using var kit = await TestKit.CreateAsync();
        var a = kit.Pc("a", so: Fast("PC-A", heartbeatMs: 200));
        await a.Sync.StartAsync(false, default);
        await a.Sync.PrepareForShutdownAsync(default);
        await Task.Delay(1500);                                   // several heartbeats
        Assert.Null((await SyncService.ReadControlAsync(kit.Remote, default))!.Lease);
    }
}
