using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class FullScanScheduleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Pierwszy_pelny_skan_jest_nalezny_po_interwale_od_startu()
    {
        var schedule = new FullScanSchedule(Interval);

        Assert.False(schedule.IsDue(Ms(3999)));
        Assert.True(schedule.IsDue(Ms(4000)));
    }

    [Fact]
    public void Ciagle_wycinki_nie_odsuwaja_pelnego_skanu()
    {
        var schedule = new FullScanSchedule(Interval);
        schedule.OnScanStarted(Ms(1000), fullFrame: true);

        for (var at = 1600; at < 5000; at += 600)
            schedule.OnScanStarted(Ms(at), fullFrame: false);

        Assert.False(schedule.IsDue(Ms(4999)));
        Assert.True(schedule.IsDue(Ms(5000)));
    }

    [Fact]
    public void Pelny_skan_zeruje_zegar()
    {
        var schedule = new FullScanSchedule(Interval);
        schedule.OnScanStarted(Ms(1000), fullFrame: true);
        schedule.OnScanStarted(Ms(3000), fullFrame: true);

        Assert.False(schedule.IsDue(Ms(5000)));
        Assert.True(schedule.IsDue(Ms(7000)));
    }

    [Fact]
    public void Zerowy_interwal_wylacza_bezpiecznik()
    {
        var schedule = new FullScanSchedule(TimeSpan.Zero);

        Assert.False(schedule.IsDue(TimeSpan.FromHours(1)));
    }
}
