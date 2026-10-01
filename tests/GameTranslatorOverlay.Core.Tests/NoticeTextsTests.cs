using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class NoticeTextsTests
{
    public static TheoryData<TranslationFailureKind> AllKinds()
    {
        var data = new TheoryData<TranslationFailureKind>();
        foreach (var kind in Enum.GetValues<TranslationFailureKind>()) data.Add(kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Kazdy_rodzaj_bledu_ma_krotki_polski_komunikat(TranslationFailureKind kind)
    {
        foreach (var provider in new[] { "DeepL", "Claude", "LLM", "", null, "Bardzo-dluga-nazwa-dostawcy-tlumaczen-z-serwera" })
        {
            var text = NoticeTexts.For(kind, provider);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.True(text.Length <= NoticeTexts.MaxLength, $"{kind}/{provider}: {text.Length} znaków");
        }
    }

    [Fact]
    public void Rodzaje_bledu_maja_rozne_komunikaty()
    {
        var texts = Enum.GetValues<TranslationFailureKind>().Select(kind => NoticeTexts.For(kind, "DeepL")).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Theory]
    [InlineData(TranslationFailureKind.MissingApiKey, "⚠ Brak klucza DeepL")]
    [InlineData(TranslationFailureKind.QuotaExceeded, "⚠ Limit znaków DeepL wyczerpany")]
    [InlineData(TranslationFailureKind.RateLimited, "⏳ DeepL ogranicza zapytania")]
    [InlineData(TranslationFailureKind.NetworkError, "⚠ Brak połączenia z dostawcą")]
    public void Przykladowe_komunikaty_dla_DeepL(TranslationFailureKind kind, string expected)
    {
        Assert.Equal(expected, NoticeTexts.For(kind, "DeepL"));
    }

    [Fact]
    public void Komunikat_o_niedzialajacym_cache()
    {
        Assert.Equal("⚠ Cache niedostępny — tłumaczenia nie są zapisywane", NoticeTexts.CacheDegraded);
    }

    [Fact]
    public void Stale_komunikaty_mieszcza_sie_w_limicie()
    {
        string[] texts =
        [
            NoticeTexts.CacheDegraded, NoticeTexts.SessionLimit, NoticeTexts.LiveStarted, NoticeTexts.LiveStopped,
            NoticeTexts.NoTextFound, NoticeTexts.TranslationFailed, NoticeTexts.EmptyResult("DeepL"),
            NoticeTexts.EmptyResult(new string('x', 200)), NoticeTexts.CacheOnlyMisses(int.MaxValue),
        ];

        Assert.All(texts, text => Assert.True(text.Length <= NoticeTexts.MaxLength, text));
    }

    [Theory]
    [InlineData(1, "Cache-only: 1 tekst bez tłumaczenia")]
    [InlineData(2, "Cache-only: 2 teksty bez tłumaczenia")]
    [InlineData(4, "Cache-only: 4 teksty bez tłumaczenia")]
    [InlineData(5, "Cache-only: 5 tekstów bez tłumaczenia")]
    [InlineData(12, "Cache-only: 12 tekstów bez tłumaczenia")]
    [InlineData(22, "Cache-only: 22 teksty bez tłumaczenia")]
    [InlineData(112, "Cache-only: 112 tekstów bez tłumaczenia")]
    public void Licznik_Cache_only_ma_polska_odmiane(int count, string expected)
    {
        Assert.Equal(expected, NoticeTexts.CacheOnlyMisses(count));
    }

    [Fact]
    public void Krytyczne_sa_klucz_i_limit_oraz_zatrzymanie_live()
    {
        var critical = Enum.GetValues<TranslationFailureKind>()
            .Where(kind => OverlayNotices.Failure(kind, "DeepL").IsCritical)
            .ToHashSet();

        Assert.Equal(
            new HashSet<TranslationFailureKind>
            {
                TranslationFailureKind.MissingApiKey, TranslationFailureKind.InvalidApiKey, TranslationFailureKind.QuotaExceeded,
            },
            critical);
        Assert.True(OverlayNotices.LiveStopped().IsCritical);
        Assert.True(OverlayNotices.SessionLimit().IsCritical);
        Assert.False(OverlayNotices.LiveStarted().IsCritical);
        Assert.False(OverlayNotices.CacheOnlyMisses(3).IsCritical);
    }

    [Fact]
    public void Komunikaty_wygasaja_po_3_do_5_sekundach()
    {
        var notices = Enum.GetValues<TranslationFailureKind>().Select(kind => OverlayNotices.Failure(kind, "DeepL"))
            .Concat([
                OverlayNotices.SessionLimit(), OverlayNotices.EmptyResult("DeepL"), OverlayNotices.CacheOnlyMisses(2),
                OverlayNotices.CacheDegraded(), OverlayNotices.LiveStarted(), OverlayNotices.LiveStopped(),
                OverlayNotices.NoTextFound(), OverlayNotices.TranslationFailed(),
            ]);

        Assert.All(notices, notice =>
            Assert.InRange(notice.Duration, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Wynik_z_pierwszym_problemem_daje_jego_komunikat()
    {
        TranslationOutcome[] outcomes =
        [
            new("a", "a", "PL:a", TranslationOrigin.Provider),
            new("b", "b", null, TranslationOrigin.Unavailable, "limit") { Issue = OutcomeIssue.SessionLimit },
            new("c", "c", null, TranslationOrigin.Unavailable, "klucz")
            {
                Issue = OutcomeIssue.Provider, FailureKind = TranslationFailureKind.MissingApiKey,
            },
        ];

        Assert.Equal(NoticeTexts.SessionLimit, OverlayNotices.FromOutcomes(outcomes, "DeepL")!.Text);
    }

    [Fact]
    public void Blad_dostawcy_bez_rodzaju_to_blad_ogolny()
    {
        TranslationOutcome[] outcomes =
            [new("c", "c", null, TranslationOrigin.Unavailable, "?") { Issue = OutcomeIssue.Provider }];

        Assert.Equal(NoticeTexts.For(TranslationFailureKind.Unknown, "DeepL"), OverlayNotices.FromOutcomes(outcomes, "DeepL")!.Text);
    }

    [Fact]
    public void Pusty_wynik_dostawcy_ma_swoj_komunikat()
    {
        TranslationOutcome[] outcomes =
            [new("c", "c", null, TranslationOrigin.Unavailable, "?") { Issue = OutcomeIssue.EmptyResult }];

        Assert.Equal("⚠ DeepL zwrócił pusty wynik", OverlayNotices.FromOutcomes(outcomes, "DeepL")!.Text);
    }

    [Fact]
    public void Reczne_tlumaczenie_bez_tekstu_mowi_ze_nic_nie_rozpoznano()
    {
        Assert.Equal(NoticeTexts.NoTextFound, OverlayNotices.ForManualResult([], "DeepL")!.Text);
    }

    [Fact]
    public void Reczne_tlumaczenie_z_brakiem_klucza_pokazuje_blad_zamiast_ciszy()
    {
        TranslationOutcome[] outcomes =
        [
            new("a", "a", null, TranslationOrigin.Unavailable, "klucz")
            {
                Issue = OutcomeIssue.Provider, FailureKind = TranslationFailureKind.MissingApiKey,
            },
        ];

        Assert.Equal("⚠ Brak klucza DeepL", OverlayNotices.ForManualResult(outcomes, "DeepL")!.Text);
    }

    [Fact]
    public void Reczne_tlumaczenie_w_Cache_only_liczy_rozne_pudla()
    {
        TranslationOutcome[] outcomes =
        [
            new("a", "a", null, TranslationOrigin.Unavailable, "cache") { Issue = OutcomeIssue.CacheOnlyMiss },
            new("a", "a", null, TranslationOrigin.Unavailable, "cache") { Issue = OutcomeIssue.CacheOnlyMiss },
            new("b", "b", null, TranslationOrigin.Unavailable, "cache") { Issue = OutcomeIssue.CacheOnlyMiss },
            new("c", "c", "PL:c", TranslationOrigin.Cache),
        ];

        Assert.Equal("Cache-only: 2 teksty bez tłumaczenia", OverlayNotices.ForManualResult(outcomes, "DeepL")!.Text);
    }

    [Fact]
    public void Reczne_tlumaczenie_z_nieznanym_problemem_daje_ogolny_komunikat()
    {
        TranslationOutcome[] outcomes = [new("a", "a", null, TranslationOrigin.Unavailable, "Brak wyniku.")];

        Assert.Equal(NoticeTexts.TranslationFailed, OverlayNotices.ForManualResult(outcomes, "DeepL")!.Text);
    }

    [Fact]
    public void Udane_reczne_tlumaczenie_nie_daje_komunikatu()
    {
        TranslationOutcome[] outcomes = [new("a", "a", "PL:a", TranslationOrigin.Provider)];

        Assert.Null(OverlayNotices.ForManualResult(outcomes, "DeepL"));
    }
}
