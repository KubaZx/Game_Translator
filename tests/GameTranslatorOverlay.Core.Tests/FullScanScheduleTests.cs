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

    private static FullScanSchedule AfterFirstScan(TimeSpan settle)
    {
        var schedule = new FullScanSchedule(Interval, settle);
        Assert.False(schedule.IsDue(Ms(1000), 1, pipelineReady: true));
        schedule.OnScanStarted(Ms(1000), fullFrame: true);
        return schedule;
    }

    [Fact]
    public void Klatka_anulowana_przez_przebudowe_dostaje_pelny_odczyt_zaraz_po_ustaleniu_pipelineu()
    {
        var schedule = AfterFirstScan(Ms(120));

        Assert.True(schedule.RequestAfterPipelineChange(Ms(1400), 2));

        Assert.False(schedule.IsDue(Ms(1500), 2, pipelineReady: true));
        Assert.True(schedule.IsDue(Ms(1520), 2, pipelineReady: true));
    }

    [Fact]
    public void Odczyt_po_przebudowie_czeka_na_gotowy_pipeline()
    {
        var schedule = AfterFirstScan(Ms(120));
        schedule.RequestAfterPipelineChange(Ms(1400), 2);

        Assert.False(schedule.IsDue(Ms(1700), 2, pipelineReady: false));
        schedule.OnScanStarted(Ms(1700), fullFrame: true);
        Assert.True(schedule.RescanRequested);
        Assert.True(schedule.RequestAfterPipelineChange(Ms(1750), 2));

        Assert.True(schedule.IsDue(Ms(1900), 2, pipelineReady: true));
    }

    [Fact]
    public void Seria_zmian_ustawien_daje_jeden_odczyt_po_ostatniej_zmianie()
    {
        var schedule = AfterFirstScan(Ms(120));
        var dueChecks = 0;
        var version = 1L;

        for (var at = 1400; at <= 2000; at += 40)
        {
            version++;
            schedule.RequestAfterPipelineChange(Ms(at), version);
            if (schedule.IsDue(Ms(at + 20), version, pipelineReady: true)) dueChecks++;
        }

        Assert.Equal(0, dueChecks);
        Assert.False(schedule.IsDue(Ms(2100), version, pipelineReady: true));
        Assert.True(schedule.IsDue(Ms(2130), version, pipelineReady: true));
        schedule.OnScanStarted(Ms(2130), fullFrame: true);
        Assert.False(schedule.IsDue(Ms(2300), version, pipelineReady: true));
    }

    [Fact]
    public void Ta_sama_wersja_pipelineu_nie_prosi_o_odczyt_drugi_raz()
    {
        var schedule = AfterFirstScan(Ms(120));
        Assert.True(schedule.RequestAfterPipelineChange(Ms(1400), 2));
        Assert.True(schedule.IsDue(Ms(1600), 2, pipelineReady: true));
        schedule.OnScanStarted(Ms(1600), fullFrame: true);

        Assert.False(schedule.RequestAfterPipelineChange(Ms(1700), 2));
        Assert.False(schedule.RescanRequested);
        Assert.False(schedule.IsDue(Ms(2000), 2, pipelineReady: true));
        Assert.True(schedule.IsDue(Ms(5600), 2, pipelineReady: true));
    }

    [Fact]
    public void Wycinek_nie_zastepuje_pelnego_odczytu_po_przebudowie()
    {
        var schedule = AfterFirstScan(Ms(120));
        schedule.RequestAfterPipelineChange(Ms(1400), 2);

        Assert.True(schedule.IsDue(Ms(1600), 2, pipelineReady: true));
        schedule.OnScanStarted(Ms(1600), fullFrame: false);

        Assert.True(schedule.RescanRequested);
        Assert.True(schedule.IsDue(Ms(1700), 2, pipelineReady: true));
    }

    [Fact]
    public void Przebudowa_bez_anulowanej_klatki_nie_wymusza_odczytu()
    {
        var schedule = AfterFirstScan(Ms(120));

        Assert.False(schedule.IsDue(Ms(1400), 2, pipelineReady: true));
        Assert.False(schedule.IsDue(Ms(3000), 3, pipelineReady: true));
        Assert.True(schedule.IsDue(Ms(5000), 3, pipelineReady: true));
    }
}
