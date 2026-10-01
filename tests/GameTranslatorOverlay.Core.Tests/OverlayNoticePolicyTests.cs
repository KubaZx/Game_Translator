using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class OverlayNoticePolicyTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static OverlayNotice Info(string key = "info") =>
        new(key, NoticeSeverity.Info, "ℹ informacja", S(3));

    private static OverlayNotice Error(string key = "error") =>
        new(key, NoticeSeverity.Error, "⚠ błąd", S(5));

    private static TranslationOutcome Miss(string text) =>
        new(text, text, null, TranslationOrigin.Unavailable, "Cache-only") { Issue = OutcomeIssue.CacheOnlyMiss };

    private static TranslationOutcome Failed(TranslationFailureKind kind) =>
        new("x", "x", null, TranslationOrigin.Unavailable, "błąd") { Issue = OutcomeIssue.Provider, FailureKind = kind };

    private static TranslationOutcome Ok(string text) => new(text, text, "PL:" + text, TranslationOrigin.Provider);

    [Fact]
    public void Ten_sam_klucz_najwyzej_raz_na_30_sekund()
    {
        var policy = new OverlayNoticePolicy();

        Assert.True(policy.Offer(Error("failure:QuotaExceeded"), S(0)));
        Assert.False(policy.Offer(Error("failure:QuotaExceeded"), S(10)));
        Assert.False(policy.Offer(Error("failure:QuotaExceeded"), S(29.9)));
        Assert.True(policy.Offer(Error("failure:QuotaExceeded"), S(30)));
    }

    [Fact]
    public void Rozne_klucze_nie_blokuja_sie_nawzajem()
    {
        var policy = new OverlayNoticePolicy();

        Assert.True(policy.Offer(Error("a"), S(0)));
        Assert.True(policy.Offer(Error("b"), S(1)));
        Assert.Equal("b", policy.Current(S(1.5))!.DedupeKey);
    }

    [Fact]
    public void Blad_wypiera_informacje()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(Info(), S(0)));

        Assert.True(policy.Offer(Error(), S(1)));

        Assert.Equal(NoticeSeverity.Error, policy.Current(S(1.1))!.Severity);
    }

    [Fact]
    public void Informacja_nie_wypiera_widocznego_bledu_ale_moze_pojawic_sie_po_nim()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(Error(), S(0)));

        Assert.False(policy.Offer(Info(), S(1)));
        Assert.Equal("error", policy.Current(S(1))!.DedupeKey);

        // Odrzucenie przez ważniejszy komunikat nie zużyło okna powtórzeń informacji.
        Assert.True(policy.Offer(Info(), S(5)));
        Assert.Equal("info", policy.Current(S(5))!.DedupeKey);
    }

    [Fact]
    public void Komunikat_wygasa_po_swoim_czasie()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(Info(), S(10)));

        Assert.NotNull(policy.Current(S(12.9)));
        Assert.Null(policy.Current(S(13)));
        Assert.Null(policy.Current(S(20)));
    }

    [Fact]
    public void Brak_komunikatu_na_starcie()
    {
        Assert.Null(new OverlayNoticePolicy().Current(S(0)));
    }

    [Fact]
    public void Zmiana_stanu_przechodzi_zawsze_nawet_nad_bledem_i_w_oknie_powtorzen()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(OverlayNotices.LiveStarted(), S(0)));
        Assert.True(policy.Offer(Error(), S(0.5)));

        Assert.True(policy.Offer(OverlayNotices.LiveStopped(), S(1)));
        Assert.True(policy.Offer(OverlayNotices.LiveStarted(), S(2)));

        Assert.Equal(NoticeTexts.LiveStarted, policy.Current(S(2))!.Text);
    }

    [Fact]
    public void Pudla_Cache_only_sa_zbierane_w_jeden_komunikat()
    {
        var policy = new OverlayNoticePolicy();

        var first = policy.OfferCacheOnlyMisses(["a", "b", "c"], S(0));
        Assert.Equal("Cache-only: 3 teksty bez tłumaczenia", first!.Text);

        // W oknie 30 s nowe pudła tylko się liczą — bez kolejnych komunikatów.
        Assert.Null(policy.OfferCacheOnlyMisses(["d", "e"], S(5)));
        Assert.Null(policy.OfferCacheOnlyMisses(["a", "b"], S(10)));
        Assert.Equal(5, policy.CacheOnlyMissCount);

        var second = policy.OfferCacheOnlyMisses([], S(31));
        Assert.Equal("Cache-only: 5 tekstów bez tłumaczenia", second!.Text);
        Assert.Equal(OverlayNoticePolicy.CacheOnlyMissKey, second.DedupeKey);
    }

    [Fact]
    public void Te_same_pudla_po_30_sekundach_nie_wracaja_bez_nowych_tekstow()
    {
        var policy = new OverlayNoticePolicy();
        Assert.NotNull(policy.OfferCacheOnlyMisses(["a"], S(0)));

        // Pętla live widzi ten sam brak co klatkę — to nie jest nowa informacja.
        Assert.Null(policy.OfferCacheOnlyMisses(["a"], S(45)));
        Assert.Equal("Cache-only: 2 teksty bez tłumaczenia", policy.OfferCacheOnlyMisses(["a", "b"], S(46))!.Text);
    }

    [Fact]
    public void Puste_teksty_nie_licza_sie_jako_pudla()
    {
        var policy = new OverlayNoticePolicy();

        Assert.Null(policy.OfferCacheOnlyMisses(["", ""], S(0)));
        Assert.Equal(0, policy.CacheOnlyMissCount);
    }

    [Fact]
    public void Klatka_z_bledem_dostawcy_pokazuje_blad_przed_pudlami_i_liczy_pudla()
    {
        var policy = new OverlayNoticePolicy();

        var notice = policy.OfferFrame([Miss("a"), Failed(TranslationFailureKind.MissingApiKey)], "DeepL", false, S(0));

        Assert.Equal("⚠ Brak klucza DeepL", notice!.Text);
        Assert.True(notice.IsCritical);
        Assert.Equal(1, policy.CacheOnlyMissCount);
        // Ten sam błąd w kolejnej klatce jest powtórką; pudło czeka na swoją kolej (info < błąd).
        Assert.Null(policy.OfferFrame([Failed(TranslationFailureKind.MissingApiKey)], "DeepL", false, S(1)));
        Assert.Equal("Cache-only: 1 tekst bez tłumaczenia", policy.OfferFrame([Miss("a")], "DeepL", false, S(6))!.Text);
    }

    [Fact]
    public void Niedzialajacy_cache_ostrzega_tylko_raz_na_sesje()
    {
        var policy = new OverlayNoticePolicy();

        Assert.Equal(NoticeTexts.CacheDegraded, policy.OfferFrame([Ok("a")], "DeepL", true, S(0))!.Text);
        Assert.Null(policy.OfferFrame([Ok("a")], "DeepL", true, S(40)));
        Assert.Null(policy.OfferFrame([Ok("a")], "DeepL", true, S(400)));
    }

    [Fact]
    public void Ostrzezenie_o_cache_odrzucone_przez_blad_wraca_pozniej()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(Error(), S(0)));

        Assert.Null(policy.OfferFrame([Ok("a")], "DeepL", true, S(1)));
        Assert.Equal(NoticeTexts.CacheDegraded, policy.OfferFrame([Ok("a")], "DeepL", true, S(6))!.Text);
    }

    [Fact]
    public void Klatka_bez_problemow_nie_daje_komunikatu()
    {
        Assert.Null(new OverlayNoticePolicy().OfferFrame([Ok("a"), Ok("b")], "DeepL", false, S(0)));
    }

    [Fact]
    public void Reset_zapomina_historie()
    {
        var policy = new OverlayNoticePolicy();
        Assert.True(policy.Offer(Error(), S(0)));
        Assert.NotNull(policy.OfferCacheOnlyMisses(["a"], S(0)) ?? policy.OfferCacheOnlyMisses(["a"], S(6)));

        policy.Reset();

        Assert.Null(policy.Current(S(1)));
        Assert.Equal(0, policy.CacheOnlyMissCount);
        Assert.True(policy.Offer(Error(), S(1)));
    }
}
