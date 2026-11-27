using System.Text.Json;
using DriveGram.Host;
using DriveGram.Tray;

namespace DriveGram.Tests;

/// <summary>
/// The upload cap counts the last 24 HOURS, not "since midnight": what went up 24 hours ago leaves the
/// count by itself. The count is seeded here by writing the file the program keeps it in, which is the
/// only way to test "a day ago" without moving the clock. Runs against an isolated DRIVEGRAM_ROOT.
/// </summary>
[Collection("isolated-root")]
public sealed class UploadCapTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "drivegram-tests", "cap-" + Guid.NewGuid().ToString("N"));
    readonly string? _previous = Environment.GetEnvironmentVariable("DRIVEGRAM_ROOT");

    public UploadCapTests()
    {
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _previous);
        try { Directory.Delete(_root, true); } catch { }
    }

    const long Gb = 1L << 30;

    /// <summary>The quarter-hour slot a moment in the past belongs to (how the program stores the count).</summary>
    static string Slot(TimeSpan ago) =>
        ((long)(DateTime.UtcNow - ago - DateTime.UnixEpoch).TotalMinutes / 15).ToString();

    void Seed(double capGb, params (TimeSpan Ago, long Bytes)[] uploads)
    {
        var slots = new Dictionary<string, long>();
        foreach (var (ago, bytes) in uploads) slots[Slot(ago)] = slots.GetValueOrDefault(Slot(ago)) + bytes;
        File.WriteAllText(Path.Combine(_root, "upload-limits.json"), JsonSerializer.Serialize(new
        {
            AlertGb = 0.0, CapGb = capGb, Slots = slots, AlertDefaultSet = true,
        }));
    }

    static string? Tick(DailyUploads d, long uploadedSinceLastTick = 0)
    {
        var drive = new object();
        d.Tick([(drive, 0L)], 0);                        // the drive's counter as it was
        return d.Tick([(drive, uploadedSinceLastTick)], 0); // and what it uploaded since
    }

    [Fact]
    public void WhatWentUpOverADayAgoIsOutOfTheCount()
    {
        Seed(0, (TimeSpan.FromHours(25), 10 * Gb), (TimeSpan.FromHours(1), 2 * Gb));
        Assert.Equal(2 * Gb, new DailyUploads().RecentBytes);
    }

    [Fact]
    public void TheCapHoldsUntilTheOldestUploadsLeaveTheWindow()
    {
        // 4 GB 23 hours ago and 2 GB an hour ago: 6 GB in the window, over a cap of 5 GB
        Seed(5, (TimeSpan.FromHours(23), 4 * Gb), (TimeSpan.FromHours(1), 2 * Gb));
        var daily = new DailyUploads();
        Assert.True(daily.CapReached);

        // it frees itself when the 23-hour-old 4 GB leaves the window: in about an hour, not at midnight
        var frees = daily.CapFreesAt();
        Assert.NotNull(frees);
        var inHowLong = frees!.Value - DateTime.Now;
        Assert.InRange(inHowLong.TotalMinutes, 40, 80);
    }

    [Fact]
    public void UnderTheCapNothingIsHeldBack()
    {
        Seed(5, (TimeSpan.FromHours(23), 4 * Gb));
        var daily = new DailyUploads();
        Assert.False(daily.CapReached);
        Assert.Null(daily.CapFreesAt());
    }

    [Fact]
    public void UploadAnywayLastsOnlyUntilTheCountFallsBackUnderTheCap()
    {
        Seed(5, (TimeSpan.FromMinutes(10), 6 * Gb));
        var daily = new DailyUploads();
        Assert.True(daily.CapReached);

        daily.LiftCap();
        Assert.False(daily.CapReached); // the user chose to go on

        // the count is under the cap again (here: a fresh start on a count that is low), so the cap is armed
        // again — and the next rise above it holds the uploads once more
        Seed(5, (TimeSpan.FromMinutes(10), 1 * Gb));
        var later = new DailyUploads();
        Tick(later);
        Assert.False(later.CapReached);
        Tick(later, 5 * Gb);
        Assert.True(later.CapReached);
    }

    [Fact]
    public void TheCapIsToldOnceForEachRiseAboveIt()
    {
        Seed(5, (TimeSpan.FromMinutes(10), 1 * Gb));
        var daily = new DailyUploads();
        Assert.Null(Tick(daily));                       // under the cap: nothing to say
        Assert.NotNull(Tick(daily, 5 * Gb));            // passed it: said once
        Assert.Null(Tick(daily, 1 * Gb));               // still over it: not said again
    }
}
