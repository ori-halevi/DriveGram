using System.Text.Json;
using DriveGram.Host;
using DriveGram.Tray;

namespace DriveGram.Tests;

/// <summary>
/// The upload cap counts the last 24 HOURS, not "since midnight": what went up 24 hours ago leaves the
/// count by itself. The count is seeded here by writing the file the program keeps it in, and the clock is
/// moved instead of waiting — the whole point of the rewrite is what happens as time passes.
/// Runs against an isolated DRIVEGRAM_ROOT.
/// </summary>
[Collection("isolated-root")]
public sealed class UploadCapTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "drivegram-tests", "cap-" + Guid.NewGuid().ToString("N"));
    readonly string? _previous = Environment.GetEnvironmentVariable("DRIVEGRAM_ROOT");
    DateTime _now = DateTime.UtcNow;

    public UploadCapTests()
    {
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _root);
        Directory.CreateDirectory(_root);
        DailyUploads.Clock = () => _now;
    }

    public void Dispose()
    {
        DailyUploads.Clock = () => DateTime.UtcNow;
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _previous);
        try { Directory.Delete(_root, true); } catch { }
    }

    const long Gb = 1L << 30;
    string Path_ => Path.Combine(_root, "upload-limits.json");

    /// <summary>The quarter-hour slot a moment in the past belongs to (how the program stores the count).</summary>
    string Slot(TimeSpan ago) => ((long)(_now - ago - DateTime.UnixEpoch).TotalMinutes / 15).ToString();

    void Seed(double capGb, params (TimeSpan Ago, long Bytes)[] uploads)
    {
        var slots = new Dictionary<string, long>();
        foreach (var (ago, bytes) in uploads) slots[Slot(ago)] = slots.GetValueOrDefault(Slot(ago)) + bytes;
        File.WriteAllText(Path_, JsonSerializer.Serialize(new
        {
            AlertGb = 0.0, CapGb = capGb, Slots = slots, AlertDefaultSet = true,
        }));
    }

    /// <summary>One turn of the tray's timer, with a drive that uploaded this much since the last one.</summary>
    static string? Tick(DailyUploads d, object drive, long uploaded = 0)
    {
        var first = d.Tick([(drive, 0L)], 0); // the first turn only learns where the drive's counter is
        return d.Tick([(drive, uploaded)], 0) ?? first;
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
        Assert.InRange((frees!.Value - _now.ToLocalTime()).TotalMinutes, 45, 75);
    }

    [Fact]
    public void TheCapReleasesItselfAsTimePasses()
    {
        Seed(5, (TimeSpan.FromHours(23), 4 * Gb), (TimeSpan.FromHours(1), 2 * Gb));
        var daily = new DailyUploads();
        var drive = new object();
        Tick(daily, drive);
        Assert.True(daily.CapReached);

        _now = _now.AddHours(2); // the 4 GB is now more than a day old
        Tick(daily, drive);
        Assert.False(daily.CapReached);
        Assert.Equal(2 * Gb, daily.RecentBytes);
    }

    [Fact]
    public void AClockThatWasAheadCannotHoldTheUploadsForEver()
    {
        // the PC's clock was days ahead when these bytes were counted; it has since been corrected.
        // They were really uploaded, so they still count — but from NOW, so they leave within a day
        // instead of waiting three days to stop holding every upload back.
        Seed(5, (TimeSpan.FromDays(-3), 10 * Gb), (TimeSpan.FromHours(1), 1 * Gb));
        var daily = new DailyUploads();
        Assert.Equal(11 * Gb, daily.RecentBytes);
        Assert.True(daily.CapReached);
        var frees = daily.CapFreesAt();
        Assert.NotNull(frees);
        Assert.InRange((frees!.Value - _now.ToLocalTime()).TotalHours, 23.5, 24.1);

        var later = new DailyUploads();
        _now = _now.AddHours(25);
        Tick(later, new object());
        Assert.False(later.CapReached); // and a day later nothing holds the uploads back
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
        Seed(5, (TimeSpan.FromHours(23), 6 * Gb));
        var daily = new DailyUploads();
        var drive = new object();
        Assert.True(daily.CapReached);

        daily.LiftCap();
        Assert.False(daily.CapReached); // the user chose to go on

        _now = _now.AddHours(2); // those 6 GB leave the window: the count is under the cap again
        Tick(daily, drive);
        Assert.False(daily.CapReached);

        Tick(daily, drive, 6 * Gb); // and the next rise above the cap holds the uploads once more
        Assert.True(daily.CapReached);
    }

    [Fact]
    public void UploadAnywayDoesNotOutliveTheWindowItWasGivenIn()
    {
        Seed(5, (TimeSpan.FromMinutes(10), 6 * Gb));
        var daily = new DailyUploads();
        var drive = new object();
        daily.LiftCap();
        Assert.False(daily.CapReached);

        // the uploads go on and keep the count above the cap the whole time, so the count never falls
        // back under it — only the 24 hours of "upload anyway" running out can bring the cap back
        string? said = null;
        for (int hour = 1; hour <= 25; hour++)
        {
            _now = _now.AddHours(1);
            said ??= Tick(daily, drive, 6 * Gb);
            if (hour < 24) Assert.False(daily.CapReached); // still let through
        }
        Assert.True(daily.CapReached);
        // and the user is told the uploads stopped again, instead of them stopping silently
        Assert.NotNull(said);
    }

    [Fact]
    public void TheCapIsToldOnceForEachRiseAboveIt()
    {
        Seed(5, (TimeSpan.FromMinutes(10), 1 * Gb));
        var daily = new DailyUploads();
        var drive = new object();
        Assert.Null(Tick(daily, drive));                 // under the cap: nothing to say
        Assert.NotNull(Tick(daily, drive, 5 * Gb));      // passed it: said once
        Assert.Null(Tick(daily, drive, 1 * Gb));         // still over it: not said again
    }

    [Fact]
    public void WhatWasCountedSurvivesARestart()
    {
        Seed(50, (TimeSpan.FromHours(1), 1 * Gb));
        var daily = new DailyUploads();
        var drive = new object();
        Tick(daily, drive, 2 * Gb);
        _now = _now.AddMinutes(40); // a later quarter-hour slot
        Tick(daily, drive, 3 * Gb);
        daily.Flush();

        Assert.Equal(6 * Gb, new DailyUploads().RecentBytes); // 1 seeded + 2 + 3, in three different slots
    }

    [Fact]
    public void SavingKeepsTheCopyThatWasThereBefore()
    {
        // what the fallback above relies on: every save leaves the file it replaced beside it
        Seed(0, (TimeSpan.FromHours(1), 1 * Gb));
        var daily = new DailyUploads();
        daily.SetLimits(0, 7);
        daily.SetLimits(0, 9);
        Assert.True(File.Exists(Path_ + ".bak"), "a save left no copy behind");
        Assert.Contains("\"CapGb\":7", File.ReadAllText(Path_ + ".bak"));
        Assert.Contains("\"CapGb\":9", File.ReadAllText(Path_));
    }

    [Fact]
    public void ADamagedFileDoesNotQuietlyCostTheUserTheirCap()
    {
        Seed(0, (TimeSpan.FromHours(1), 1 * Gb));
        var saved = new DailyUploads();
        saved.SetLimits(0, 7); // writes the file
        saved.SetLimits(0, 7); // and again, so the copy beside it holds a whole file too
        File.WriteAllText(Path_, "{ this is not json");

        var daily = new DailyUploads();
        Assert.Equal(7, daily.CapGb); // taken from the copy beside it
        Assert.Null(daily.LoadFailed);
    }

    [Fact]
    public void WithNoCopyToFallBackOnTheUserIsTold()
    {
        File.WriteAllText(Path_, "{ this is not json");
        var daily = new DailyUploads();
        Assert.Equal(0, daily.CapGb);
        Assert.NotNull(daily.LoadFailed); // the window says so; it does not pretend a cap is still in force
    }

    [Fact]
    public void TheLimitsOfVersion170AreKept()
    {
        // what 1.7.0 wrote: a calendar day's count, in fields this version no longer knows
        File.WriteAllText(Path_, """
            {"AlertGb":20,"CapGb":30,"Day":"2026-11-27","Bytes":5898577134,"AlertedDay":"2026-11-26",
             "CapLiftedDay":"","AlertDefaultSet":true}
            """);
        var daily = new DailyUploads();
        Assert.Equal(20, daily.AlertGb);
        Assert.Equal(30, daily.CapGb);
        Assert.Equal(0, daily.RecentBytes); // the day's count starts afresh, once, in the permissive direction
    }
}
