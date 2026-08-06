using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveBlockSurvivalTests
{
    private static readonly RectPx DefaultBox = new(10, 10, 100, 20);

    private static LiveOverlayBlock Block(int misses = 0, RectPx? box = null) =>
        new(box ?? DefaultBox, "Tłumaczenie", "tłumaczenie", 20, -1, Misses: misses);

    private static Dictionary<string, LiveOverlayBlock> Displayed(params (string Key, int Misses)[] blocks) =>
        blocks.ToDictionary(static b => b.Key, static b => Block(b.Misses), StringComparer.Ordinal);

    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);

    [Fact]
    public void Calkowity_whiff_OCR_nie_zdejmuje_zadnego_bloku()
    {
        var displayed = Displayed(("a", 0), ("b", 0), ("c", 0));

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), [], sceneCut: false, missGrace: 2);

        Assert.Equal(3, survivors.Count);
        Assert.All(survivors, static s => Assert.Equal(1, s.Value.Misses));
    }

    [Fact]
    public void Blok_znika_po_wyczerpaniu_okresu_laski()
    {
        var displayed = Displayed(("a", 2), ("b", 1));

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), [], sceneCut: false, missGrace: 2);

        Assert.Single(survivors);
        Assert.Equal("b", survivors[0].Key);
        Assert.Equal(2, survivors[0].Value.Misses);
    }

    [Fact]
    public void Ciecie_sceny_wylacza_okres_laski()
    {
        var displayed = Displayed(("a", 0), ("b", 0));

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), [], sceneCut: true, missGrace: 2);

        Assert.Empty(survivors);
    }

    [Fact]
    public void Bloki_widoczne_w_biezacym_przebiegu_nie_sa_duplikowane()
    {
        var displayed = Displayed(("a", 1), ("b", 0));

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys("a"), [], sceneCut: false, missGrace: 2);

        Assert.Single(survivors);
        Assert.Equal("b", survivors[0].Key);
    }

    [Fact]
    public void Nowy_tekst_w_tym_samym_miejscu_eksmituje_zgubiony_blok()
    {
        // Kolejna linia dialogu zajmuje box poprzedniej — stara nie może wisieć obok nowej.
        var displayed = Displayed(("stara-linia", 0));
        var claimed = new[] { new RectPx(12, 11, 100, 20) };

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), claimed, sceneCut: false, missGrace: 2);

        Assert.Empty(survivors);
    }

    [Fact]
    public void Rozpoznanie_w_innym_miejscu_nie_eksmituje_zgubionego_bloku()
    {
        var displayed = Displayed(("a", 0));
        var claimed = new[] { new RectPx(500, 400, 100, 20) };

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), claimed, sceneCut: false, missGrace: 2);

        Assert.Single(survivors);
    }

    [Fact]
    public void Male_musniecie_boxa_nie_eksmituje_bloku()
    {
        // Nakładanie poniżej progu (róg boxa) to sąsiedztwo, nie przejęcie miejsca.
        var displayed = Displayed(("a", 0));
        var claimed = new[] { new RectPx(100, 25, 30, 20) };

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), claimed, sceneCut: false, missGrace: 2);

        Assert.Single(survivors);
    }

    [Fact]
    public void Zerowa_laska_przywraca_dawne_zachowanie()
    {
        var displayed = Displayed(("a", 0));

        var survivors = LiveBlockSurvival.Survivors(displayed, Keys(), [], sceneCut: false, missGrace: 0);

        Assert.Empty(survivors);
    }

    [Fact]
    public void Pusta_lista_wyswietlanych_nie_generuje_ocalalych()
    {
        var survivors = LiveBlockSurvival.Survivors(
            new Dictionary<string, LiveOverlayBlock>(), Keys(), [], sceneCut: false, missGrace: 2);

        Assert.Empty(survivors);
    }
}
