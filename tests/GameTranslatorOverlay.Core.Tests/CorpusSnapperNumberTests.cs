using GameTranslatorOverlay.Core.Corpus;

namespace GameTranslatorOverlay.Core.Tests;

public class CorpusSnapperNumberTests
{
    private static readonly CorpusEntry[] Synthetic =
    [
        E("+10%"),
        E("+100%"),
        E("$25"),
        E("€ 40"),
        E("Inspect"),
        E("Increases your movement speed by +10% for a while."),
        E("All enemies in the arena take 10% more damage."),
        E("Collect 5kg of wheat for the old farmer."),
        E("Meet me at 10am near the old lighthouse."),
        E("You finished in 1st place this time!"),
        E("Combo x3 unlocked for this bonus round only."),
        E("Stay close to me. Bring 7kg of salt to the harbour. Watch out for the rocks above."),
        E("I'm sure you will find the old key soon."),
    ];

    private static CorpusEntry E(string text) => new()
    {
        Key = text,
        En = text,
        Kind = CorpusEntryKind.Ui,
        Source = "synthetic",
    };

    private static CorpusSnapper Snapper() => new(CorpusIndex.Build(Synthetic));

    [Theory]
    [InlineData("-10%")]
    [InlineData("10")]
    [InlineData("10%")]
    [InlineData("+10")]
    [InlineData("€25")]
    [InlineData("25")]
    [InlineData("$ 40")]
    public void Znak_waluta_i_procent_przy_liczbie_musza_sie_zgadzac_w_dopasowaniu_dokladnym(string reading)
    {
        Assert.Null(Snapper().Snap(reading));
    }

    [Theory]
    [InlineData("+10%", "+10%")]
    [InlineData("(+10%)", "+10%")]
    [InlineData("$25.", "$25")]
    [InlineData("€ 40", "€ 40")]
    public void Ta_sama_liczba_z_tym_samym_znakiem_przyciaga_sie_dokladnie(string reading, string expected)
    {
        var match = Snapper().Snap(reading);

        Assert.Equal(expected, match?.Entry.En);
        Assert.Equal(CorpusMatchKind.Exact, match?.Kind);
    }

    [Fact]
    public void Etykieta_obok_liczby_z_innym_znakiem_nie_przyciaga_liczby()
    {
        Assert.DoesNotContain(Snapper().SnapBlock("Collect 5kg of wheat for the old farmer. -100%").Segments, static s => s.Match.Entry.En == "+100%");
        Assert.Contains(Snapper().SnapBlock("Collect 5kg of wheat for the old farmer. +100%").Segments, static s => s.Match.Entry.En == "+100%");
    }

    [Theory]
    [InlineData("Increases your movement speed by -10% for a while.")]
    [InlineData("Increases your movement speed by +10 for a while.")]
    [InlineData("All enemies in the arena take 10 more damage.")]
    [InlineData("All enemies in the arena take $10% more damage.")]
    public void Inny_znak_przy_liczbie_blokuje_przyblizenie(string reading)
    {
        Assert.Null(Snapper().Snap(reading));
    }

    [Fact]
    public void Literowka_poza_liczba_nadal_daje_przyblizenie()
    {
        var match = Snapper().Snap("Increases your movement speed by +10% for a whi1e.");

        Assert.Equal(CorpusMatchKind.Fuzzy, match?.Kind);
        Assert.Equal("Increases your movement speed by +10% for a while.", match?.Entry.En);
    }

    [Theory]
    [InlineData("Collect 6kg of wheat for the old farmer.")]
    [InlineData("Collect 56kg of wheat for the old farmer.")]
    [InlineData("Collect kg of wheat for the old farmer.")]
    [InlineData("Meet me at 11am near the old lighthouse.")]
    [InlineData("Meet me at IOam near the old lighthouse.")]
    [InlineData("You finished in 2nd place this time!")]
    [InlineData("Combo x5 unlocked for this bonus round only.")]
    public void Liczby_sklejone_z_literami_musza_sie_zgadzac(string reading)
    {
        Assert.Null(Snapper().Snap(reading));
    }

    [Theory]
    [InlineData("Co11ect 5kg of wheat for the o1d farmer.", "Collect 5kg of wheat for the old farmer.")]
    [InlineData("Meet me at 10am near the 01d 1ighthouse.", "Meet me at 10am near the old lighthouse.")]
    [InlineData("Combo x3 un1ocked for thi5 8onus round on1y.", "Combo x3 unlocked for this bonus round only.")]
    [InlineData("You finished in 1st p1ace this time!", "You finished in 1st place this time!")]
    [InlineData("11m sure you will find the old key soon.", "I'm sure you will find the old key soon.")]
    public void Cyfra_odczytu_naprzeciw_mylonej_litery_korpusu_jest_dopuszczalna(string reading, string expected)
    {
        Assert.Equal(expected, Snapper().Snap(reading)?.Entry.En);
    }

    [Fact]
    public void Fragment_z_inna_liczba_sklejona_z_litera_nie_wskazuje_wpisu()
    {
        Assert.Null(Snapper().Snap("Bring 9kg of salt to the harbour."));
        Assert.Equal(CorpusMatchKind.Fragment, Snapper().Snap("Bring 7kg of salt to the harbour.")?.Kind);
    }

    [Fact]
    public void Opis_z_inna_liczba_obok_etykiety_nie_jest_przyciagany()
    {
        Assert.DoesNotContain(Snapper().SnapBlock("Collect 6kg of wheat for the old farmer. Inspect").Segments,
            static s => s.Match.Entry.En.StartsWith("Collect", StringComparison.Ordinal));
        Assert.Contains(Snapper().SnapBlock("Collect 5kg of wheat for the o1d farmer. Inspect").Segments,
            static s => s.Match.Entry.En.StartsWith("Collect", StringComparison.Ordinal));
    }
}
