using GameTranslatorOverlay.Core.Usage;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveFailureBackoffTests
{
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Przerwy_miedzy_probami_rosna_az_do_dwoch_sekund()
    {
        var backoff = new LiveFailureBackoff();

        var delays = Enumerable.Range(0, 7).Select(i => backoff.RecordFailure(Ms(i * 100)).Delay).ToList();

        Assert.Equal([Ms(200), Ms(500), Ms(1000), Ms(2000), Ms(2000), Ms(2000), Ms(2000)], delays);
    }

    [Fact]
    public void Pierwszy_blad_nigdy_nie_zatrzymuje_sesji()
    {
        var backoff = new LiveFailureBackoff();

        Assert.False(backoff.RecordFailure(TimeSpan.FromHours(1)).Stop);
    }

    [Fact]
    public void Szesc_szybkich_bledow_pod_rzad_nie_zatrzymuje_sesji()
    {
        var backoff = new LiveFailureBackoff();

        for (var i = 0; i < 6; i++) Assert.False(backoff.RecordFailure(Ms(i * 170)).Stop);
    }

    [Fact]
    public void Zatrzymanie_dopiero_po_dziesieciu_sekundach_nieprzerwanych_bledow()
    {
        var backoff = new LiveFailureBackoff();
        var now = TimeSpan.Zero;
        var attempts = 0;

        while (true)
        {
            var step = backoff.RecordFailure(now);
            attempts++;
            if (step.Stop) break;
            now += step.Delay;
            Assert.InRange(attempts, 1, 30);
        }

        Assert.True(now >= LiveFailureBackoff.StopAfter);
        Assert.True(now < LiveFailureBackoff.StopAfter + Ms(2000));
    }

    [Fact]
    public void Blad_w_dziewiatej_sekundzie_jeszcze_nie_zatrzymuje()
    {
        var backoff = new LiveFailureBackoff();
        backoff.RecordFailure(Ms(0));

        Assert.False(backoff.RecordFailure(Ms(9999)).Stop);
        Assert.True(backoff.RecordFailure(Ms(10000)).Stop);
    }

    [Fact]
    public void Pierwszy_udany_cykl_zeruje_stan()
    {
        var backoff = new LiveFailureBackoff();
        backoff.RecordFailure(Ms(0));
        backoff.RecordFailure(Ms(5000));
        backoff.RecordSuccess();

        var step = backoff.RecordFailure(Ms(11000));

        Assert.False(step.Stop);
        Assert.Equal(Ms(200), step.Delay);
        Assert.Equal(1, backoff.Failures);
        Assert.Equal(TimeSpan.Zero, backoff.FailingFor(Ms(11000)));
    }

    [Fact]
    public void Liczenie_czasu_zaczyna_sie_od_pierwszego_bledu_serii()
    {
        var backoff = new LiveFailureBackoff();
        backoff.RecordFailure(Ms(3000));
        backoff.RecordFailure(Ms(6000));

        Assert.Equal(Ms(4000), backoff.FailingFor(Ms(7000)));
        Assert.False(backoff.RecordFailure(Ms(12999)).Stop);
        Assert.True(backoff.RecordFailure(Ms(13000)).Stop);
    }

    [Fact]
    public void Bez_bledow_czas_trwania_serii_to_zero()
    {
        Assert.Equal(TimeSpan.Zero, new LiveFailureBackoff().FailingFor(Ms(5000)));
    }
}

public class LiveErrorTallyTests
{
    [Fact]
    public void Pierwsze_wystapienie_kategorii_jest_zgloszone_raz()
    {
        var tally = new LiveErrorTally();

        Assert.True(tally.Record(LiveErrorCategory.BlockTracking, typeof(ArgumentException)));
        Assert.False(tally.Record(LiveErrorCategory.BlockTracking, typeof(InvalidOperationException)));
        Assert.True(tally.Record(LiveErrorCategory.Correction, typeof(InvalidOperationException)));
    }

    [Fact]
    public void Podsumowanie_ma_liczby_na_kategorie_i_typ_pierwszego_wyjatku()
    {
        var tally = new LiveErrorTally();
        tally.Record(LiveErrorCategory.LostTextRead, typeof(TimeoutException));
        tally.Record(LiveErrorCategory.LostTextRead, typeof(ArgumentException));
        tally.Record(LiveErrorCategory.BlockTracking, typeof(IndexOutOfRangeException));

        var summary = tally.Summary();

        Assert.Equal(3, tally.Total);
        Assert.Equal(
            [
                new LiveErrorSummary(LiveErrorCategory.BlockTracking, 1, nameof(IndexOutOfRangeException)),
                new LiveErrorSummary(LiveErrorCategory.LostTextRead, 2, nameof(TimeoutException)),
            ],
            summary);
    }

    [Fact]
    public void Opis_podsumowania_zawiera_tylko_kategorie_liczby_i_typy()
    {
        var tally = new LiveErrorTally();
        tally.Record(LiveErrorCategory.GlyphCover, typeof(OverflowException));
        tally.Record(LiveErrorCategory.GlyphCover, typeof(OverflowException));

        Assert.Equal("GlyphCover=2 (OverflowException)", tally.Describe());
    }

    [Fact]
    public void Pusty_licznik_nic_nie_opisuje()
    {
        var tally = new LiveErrorTally();

        Assert.Equal(0, tally.Total);
        Assert.Empty(tally.Summary());
        Assert.Equal(string.Empty, tally.Describe());
    }

    [Fact]
    public void Licznik_jest_bezpieczny_dla_wielu_watkow()
    {
        var tally = new LiveErrorTally();
        var firsts = 0;

        Parallel.For(0, 4000, i =>
        {
            if (tally.Record((LiveErrorCategory)(i % 5), typeof(InvalidOperationException))) Interlocked.Increment(ref firsts);
        });

        Assert.Equal(4000, tally.Total);
        Assert.Equal(5, firsts);
        Assert.All(tally.Summary(), entry => Assert.Equal(800, entry.Count));
    }
}

public class LiveStopNoticeTests
{
    public static TheoryData<LiveStopReason> ErrorReasons() => new(
        LiveStopReason.WindowClosed, LiveStopReason.CaptureError, LiveStopReason.MissingOcrPack,
        LiveStopReason.ErrorSeries, LiveStopReason.UnexpectedError);

    [Theory]
    [MemberData(nameof(ErrorReasons))]
    public void Zatrzymanie_z_powodu_bledu_ma_krotki_osobny_tekst_z_przyczyna(LiveStopReason reason)
    {
        var notice = OverlayNotices.LiveStoppedBecause(reason, "en-US");

        Assert.NotEqual(NoticeTexts.LiveStopped, notice.Text);
        Assert.StartsWith("■ Live zatrzymany: ", notice.Text);
        Assert.True(notice.Text.Length <= NoticeTexts.MaxLength, notice.Text);
        Assert.Equal(NoticeSeverity.Error, notice.Severity);
    }

    [Theory]
    [MemberData(nameof(ErrorReasons))]
    public void Zatrzymanie_z_powodu_bledu_trwa_osiem_do_dziesieciu_sekund_i_przechodzi_przez_wylaczenie_komunikatow(LiveStopReason reason)
    {
        var notice = OverlayNotices.LiveStoppedBecause(reason);

        Assert.InRange(notice.Duration, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10));
        Assert.True(notice.ShowsWhenNoticesDisabled);
        Assert.True(notice.ShowsWhenHiddenByUser);
        Assert.True(notice.IsStateChange);
    }

    [Fact]
    public void Powody_maja_rozne_teksty()
    {
        var texts = Enum.GetValues<LiveStopReason>()
            .Where(static r => r != LiveStopReason.UserStop)
            .Select(static r => OverlayNotices.LiveStoppedBecause(r, "en-US").Text)
            .ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void Brak_pakietu_OCR_mowi_czego_brakuje()
    {
        Assert.Contains("en-US", OverlayNotices.LiveStoppedBecause(LiveStopReason.MissingOcrPack, "en-US").Text);
        Assert.Contains("pakiet", OverlayNotices.LiveStoppedBecause(LiveStopReason.MissingOcrPack).Text);
    }

    [Fact]
    public void Dlugi_szczegol_nie_rozpycha_komunikatu()
    {
        var notice = OverlayNotices.LiveStoppedBecause(LiveStopReason.MissingOcrPack, new string('x', 200));

        Assert.True(notice.Text.Length <= NoticeTexts.MaxLength);
    }

    [Fact]
    public void Reczny_stop_zostaje_przy_zwyklym_komunikacie_i_nie_omija_ustawien()
    {
        var notice = OverlayNotices.LiveStoppedBecause(LiveStopReason.UserStop);

        Assert.Equal(NoticeTexts.LiveStopped, notice.Text);
        Assert.False(notice.ShowsWhenNoticesDisabled);
    }

    [Fact]
    public void Komunikat_o_zrzucie_ekranu_mowi_wprost_o_innych_oknach_i_mieści_sie_w_limicie()
    {
        var notice = OverlayNotices.ReadingScreen();

        Assert.True(notice.Text.Length <= NoticeTexts.MaxLength, notice.Text);
        Assert.Contains("zrzut ekranu", notice.Text);
        Assert.Contains("okna nad grą", notice.Text);
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.False(notice.ShowsWhenNoticesDisabled);
    }

    [Fact]
    public void Komunikat_o_pelnym_ekranie_wspomina_zrzut_i_mieści_sie_w_limicie()
    {
        Assert.True(NoticeTexts.ExclusiveFullscreen.Length <= NoticeTexts.MaxLength);
        Assert.Contains("zrzut", NoticeTexts.ExclusiveFullscreen);
        Assert.Contains("okno bez ramki", NoticeTexts.ExclusiveFullscreen);
    }

    [Fact]
    public void Zatrzymanie_z_powodu_bledu_przechodzi_przez_polityke_mimo_ostatniego_komunikatu()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(OverlayNotices.ReadingScreen(), TimeSpan.FromSeconds(1)));

        Assert.True(policy.Offer(OverlayNotices.LiveStoppedBecause(LiveStopReason.ErrorSeries), TimeSpan.FromSeconds(2)));
        Assert.True(policy.Offer(OverlayNotices.LiveStoppedBecause(LiveStopReason.ErrorSeries), TimeSpan.FromSeconds(3)));
    }
}
