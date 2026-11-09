using GameTranslatorOverlay.Infrastructure.Diagnostics;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class StartupDiagnosticsTests
{
    [Fact]
    public void Wpis_startowy_zawiera_wersje_system_monitory_i_jezyki_ocr()
    {
        var text = StartupReport.Format(
            "0.9.0", new Version(10, 0, 26200), ".NET 10.0.1",
            [new MonitorSummary(2560, 1440, 1.25), new MonitorSummary(1920, 1080, 1.0)],
            ["en-US", "pl"]);

        Assert.Contains("wersja 0.9.0", text);
        Assert.Contains("kompilacja 26200", text);
        Assert.Contains(".NET 10.0.1", text);
        Assert.Contains("monitory: 2 (2560x1440 @ 125%; 1920x1080 @ 100%)", text);
        Assert.Contains("języki OCR: en-US, pl", text);
    }

    [Fact]
    public void Brak_danych_o_monitorach_i_jezykach_jest_opisany()
    {
        var text = StartupReport.Format("1.0", new Version(10, 0, 19045), ".NET", [], []);

        Assert.Contains("monitory: 0 (brak danych)", text);
        Assert.Contains("języki OCR: brak", text);
    }

    [Fact]
    public void Okno_bledu_najwyzej_raz_na_minute()
    {
        var throttle = new ErrorDialogThrottle();
        var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(throttle.TryAcquire(start, liveRunning: false));
        Assert.False(throttle.TryAcquire(start.AddSeconds(30), liveRunning: false));
        Assert.True(throttle.TryAcquire(start.AddSeconds(61), liveRunning: false));
    }

    [Fact]
    public void Okno_bledu_nie_otwiera_sie_w_trakcie_live_i_nie_zuzywa_limitu()
    {
        var throttle = new ErrorDialogThrottle();
        var start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(throttle.TryAcquire(start, liveRunning: true));
        Assert.True(throttle.TryAcquire(start.AddSeconds(1), liveRunning: false));
    }
}
