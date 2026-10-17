using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveCorrectionsTests
{
    private static LiveOverlayBlock Block(string source, string translated, string? cacheKey = null) =>
        new(new RectPx(120, 340, 410, 52), translated, translated.ToLowerInvariant(), 38, 0xF0E0C0, 0x101418, 0x000000,
            Misses: 1, SourceText: source, PendingBackgroundRgb: 0x202020,
            Cover: new GlyphCover
            {
                PatchPbgra = new byte[16], PatchPixelWidth = 2, PatchPixelHeight = 2,
                PatchX = -4, PatchY = -3, PatchWidth = 2, PatchHeight = 2, TextRgb = 0xF0E0C0,
            },
            QualityIssues: TranslationQualityFlags.NumbersChanged)
        {
            CacheKey = cacheKey,
        };

    private static LiveCorrection Correction(string normalized, string? cacheKey = null) =>
        new(normalized, cacheKey, "mroczna-latarnia", "en", "pl");

    [Fact]
    public void Poprawka_obejmuje_blok_z_tym_samym_tekstem_albo_kluczem_cache()
    {
        var sameText = Block("Unlock level 20 door", "Otwórz drzwi poziomu");
        var sameKey = Block("Unl0ck level 20 door", "Otwórz drzwi poziomu", cacheKey: "Unlock level 20 door");
        var other = Block("Open the gate", "Otwórz bramę", cacheKey: "Open the gate");

        var correction = Correction("Unlock level 20 door");

        Assert.True(correction.Covers(sameText));
        Assert.True(correction.Covers(sameKey));
        Assert.False(correction.Covers(other));
        Assert.True(Correction("Unl0ck lvl 20 door", "Unlock level 20 door").Covers(sameKey));
    }

    [Fact]
    public void Poprawka_pomija_bloki_tymczasowe_i_bez_tekstu_zrodlowego()
    {
        var provisional = Block("Unlock level 20 door", "Otwórz drzwi poziomu") with { Provisional = true };
        var empty = Block(string.Empty, "Otwórz drzwi poziomu");

        Assert.False(Correction("Unlock level 20 door").Covers(provisional));
        Assert.False(Correction(string.Empty).Covers(empty));
    }

    [Fact]
    public void Poprawka_dotyczy_tylko_swojego_profilu_i_jezykow()
    {
        var correction = Correction("Unlock level 20 door");

        Assert.True(correction.AppliesTo("Mroczna-Latarnia", "EN", "pl"));
        Assert.False(correction.AppliesTo(string.Empty, "en", "pl"));
        Assert.False(correction.AppliesTo("mroczna-latarnia", "en", "de"));
    }

    [Fact]
    public void Poprawiony_blok_zachowuje_pozycje_styl_latke_i_sledzenie()
    {
        var block = Block("Unlock level 20 door", "Otwórz drzwi poziomu");
        var outcome = new TranslationOutcome("Unlock level 20 door", "Unlock level 20 door", "Otwórz drzwi poziomu 20", TranslationOrigin.Cache);

        var corrected = LiveCorrections.Corrected(block, outcome);

        Assert.NotNull(corrected);
        Assert.Equal("Otwórz drzwi poziomu 20", corrected.TranslatedText);
        Assert.Equal("Otwórz drzwi poziomu 20", corrected.NormalizedTranslation);
        Assert.Equal(TranslationQualityFlags.None, corrected.QualityIssues);
        Assert.Equal(block.WindowRelativeBox, corrected.WindowRelativeBox);
        Assert.Equal(block.LineHeight, corrected.LineHeight);
        Assert.Equal((block.ColorRgb, block.BackgroundRgb, block.OutlineRgb), (corrected.ColorRgb, corrected.BackgroundRgb, corrected.OutlineRgb));
        Assert.Equal(block.PendingBackgroundRgb, corrected.PendingBackgroundRgb);
        Assert.Equal(block.Misses, corrected.Misses);
        Assert.Same(block.Cover, corrected.Cover);
        Assert.Equal(block.SourceText, corrected.SourceText);
    }

    [Fact]
    public void Ten_sam_wynik_albo_brak_wyniku_nie_zmienia_bloku()
    {
        var block = Block("Unlock level 20 door", "Otwórz drzwi poziomu");
        var same = new TranslationOutcome("Unlock level 20 door", "Unlock level 20 door", "Otwórz drzwi poziomu", TranslationOrigin.Cache)
        {
            QualityIssues = TranslationQualityFlags.NumbersChanged,
        };

        Assert.Null(LiveCorrections.Corrected(block, same));
        Assert.Null(LiveCorrections.Corrected(block, null));
        Assert.Null(LiveCorrections.Corrected(block, same with { TranslatedText = null }));
    }

    [Fact]
    public void Wybiera_klucze_wyswietlanych_blokow_objetych_poprawkami()
    {
        var displayed = new Dictionary<string, LiveOverlayBlock>
        {
            ["a#0"] = Block("Unlock level 20 door", "Otwórz drzwi poziomu"),
            ["b#0"] = Block("Open the gate", "Otwórz bramę"),
            ["c#0"] = Block("Unl0ck level 20 door", "Otwórz drzwi poziomu", cacheKey: "Unlock level 20 door"),
        };

        Assert.Equal(["a#0", "c#0"], LiveCorrections.Affected(displayed, [Correction("Unlock level 20 door")]));
        Assert.Empty(LiveCorrections.Affected(displayed, []));
    }

    [Fact]
    public async Task Skrzynka_poprawek_budzi_petle_i_oddaje_wszystko_raz()
    {
        var inbox = new LiveCorrectionInbox();
        var first = inbox.Arrived;
        Assert.False(first.IsCompleted);
        Assert.Empty(inbox.TakeAll());

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => inbox.Post(Correction($"Line {i}")))));

        Assert.True(first.IsCompleted);
        var taken = inbox.TakeAll();
        Assert.Equal(32, taken.Select(static c => c.NormalizedText).Distinct().Count());
        Assert.False(inbox.Arrived.IsCompleted);
        Assert.Empty(inbox.TakeAll());

        inbox.Post(Correction("Later"));
        Assert.True(inbox.Arrived.IsCompleted);
        Assert.Single(inbox.TakeAll());
    }
}
