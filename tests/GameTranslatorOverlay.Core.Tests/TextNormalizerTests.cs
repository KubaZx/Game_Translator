using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("+25%")]
    [InlineData("10–15")]
    [InlineData("1.5 seconds")]
    [InlineData("Level 20")]
    [InlineData("3/5")]
    [InlineData("x2")]
    [InlineData("-10%")]
    [InlineData("24% increased Lightning Damage")]
    public void Normalize_zachowuje_tokeny_statystyk(string text)
    {
        Assert.Equal(text, TextNormalizer.Normalize(text));
    }

    [Fact]
    public void Normalize_skleja_nadmiarowe_spacje_i_tabulatory()
    {
        Assert.Equal("Adds 3 to 7 Fire Damage", TextNormalizer.Normalize("  Adds  3  to \t 7   Fire Damage  "));
    }

    [Fact]
    public void Normalize_usuwa_znaki_zerowej_szerokosci()
    {
        Assert.Equal("Armour", TextNormalizer.Normalize("Ar​mo‌ur﻿"));
    }

    [Fact]
    public void Normalize_zamienia_twarda_spacje_na_zwykla()
    {
        Assert.Equal("Level 20", TextNormalizer.Normalize("Level 20"));
    }

    [Fact]
    public void Normalize_usuwa_puste_linie_i_normalizuje_konce_linii()
    {
        Assert.Equal("Pierwsza\nDruga", TextNormalizer.Normalize("Pierwsza\r\n\r\n   \r\nDruga\r\n"));
    }

    [Fact]
    public void Normalize_pustego_tekstu_zwraca_pusty_string()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(""));
        Assert.Equal(string.Empty, TextNormalizer.Normalize("   \n \t "));
    }
}
