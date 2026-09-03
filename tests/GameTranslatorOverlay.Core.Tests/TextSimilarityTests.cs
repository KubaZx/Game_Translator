using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class TextSimilarityTests
{
    [Fact]
    public void Identyczne_teksty_maja_podobienstwo_1()
    {
        Assert.Equal(1.0, TextSimilarity.Ratio("Last Played: 06.08.2026", "Last Played: 06.08.2026"));
    }

    [Fact]
    public void Drzenie_OCR_nad_ruchomym_tlem_to_wciaz_ten_sam_napis()
    {
        var ratio = TextSimilarity.Ratio("Last Played: 06.08.2026", "Lasi Played: 06.0840261");

        Assert.True(ratio >= 0.7, $"Podobieństwo {ratio:0.00} powinno być wysokie");
    }

    [Fact]
    public void Zupelnie_inny_tekst_ma_niskie_podobienstwo()
    {
        var ratio = TextSimilarity.Ratio("Chapter One: Fall Term", "Click to wishlist Escape Academy 2!");

        Assert.True(ratio < 0.4, $"Podobieństwo {ratio:0.00} powinno być niskie");
    }

    [Fact]
    public void Wielkosc_liter_nie_ma_znaczenia()
    {
        Assert.Equal(1.0, TextSimilarity.Ratio("PROLOGUE", "prologue"));
    }

    [Fact]
    public void Pusty_i_niepusty_tekst()
    {
        Assert.Equal(0.0, TextSimilarity.Ratio("", "abc"));
        Assert.Equal(1.0, TextSimilarity.Ratio("", ""));
    }
}
