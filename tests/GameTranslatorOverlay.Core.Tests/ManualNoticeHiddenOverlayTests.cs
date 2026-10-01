using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Ręczne tłumaczenie przy nakładce schowanej skrótem (Ctrl+Shift+H): gdy nic nie
/// przetłumaczono, nic nie odkrywa nakładki — komunikat musi przejść sam, inaczej gracz
/// nie dostaje żadnej odpowiedzi na swoje żądanie.
/// </summary>
public class ManualNoticeHiddenOverlayTests
{
    private static TranslationOutcome Failed(TranslationFailureKind kind) =>
        new("x", "x", null, TranslationOrigin.Unavailable, "błąd") { Issue = OutcomeIssue.Provider, FailureKind = kind };

    private static TranslationOutcome Miss(string text) =>
        new(text, text, null, TranslationOrigin.Unavailable, "Cache-only") { Issue = OutcomeIssue.CacheOnlyMiss };

    public static TheoryData<string> NieKrytyczneWyniki() => ["brak-tekstu", "siec", "timeout", "cache-only", "ogolna"];

    [Theory]
    [MemberData(nameof(NieKrytyczneWyniki))]
    public void Niekrytyczny_komunikat_recznego_tlumaczenia_przechodzi_przez_ukrycie_nakladki(string przypadek)
    {
        IReadOnlyList<TranslationOutcome> outcomes = przypadek switch
        {
            "brak-tekstu" => [],
            "siec" => [Failed(TranslationFailureKind.NetworkError)],
            "timeout" => [Failed(TranslationFailureKind.Timeout)],
            "cache-only" => [Miss("Hello")],
            _ => [new TranslationOutcome("x", "x", null, TranslationOrigin.Unavailable, "?")],
        };

        var notice = OverlayNotices.ForManualResult(outcomes, "DeepL");

        Assert.NotNull(notice);
        Assert.False(notice.IsCritical);
        Assert.True(notice.IsExplicitRequest);
        Assert.True(notice.ShowsWhenHiddenByUser);
    }

    [Fact]
    public void Ogolna_porazka_recznego_tlumaczenia_przechodzi_przez_ukrycie_nakladki()
    {
        var notice = OverlayNotices.ManualTranslationFailed();

        Assert.Equal(NoticeTexts.TranslationFailed, notice.Text);
        Assert.True(notice.ShowsWhenHiddenByUser);
    }

    [Fact]
    public void Niekrytyczne_komunikaty_live_nadal_sa_wyciszane_przy_ukryciu()
    {
        Assert.False(OverlayNotices.Failure(TranslationFailureKind.NetworkError, "DeepL").ShowsWhenHiddenByUser);
        Assert.False(OverlayNotices.CacheOnlyMisses(3).ShowsWhenHiddenByUser);
        Assert.False(OverlayNotices.TranslationFailed().ShowsWhenHiddenByUser);
        Assert.True(OverlayNotices.LiveStopped().ShowsWhenHiddenByUser);
    }
}
