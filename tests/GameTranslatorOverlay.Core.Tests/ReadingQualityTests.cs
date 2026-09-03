using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class ReadingQualityTests
{
    [Theory]
    [InlineData("Prologue")]
    [InlineData("Last Played: 06.08.2026")]
    [InlineData("Chapter One: Fall Term")]
    [InlineData("Adds 3 to 7 Fire Damage")]
    public void Czysty_tekst_ma_jakosc_1(string text)
    {
        Assert.Equal(1.0, ReadingQuality.Score(text));
    }

    [Theory]
    [InlineData("lRrgIé@ue")]
    [InlineData("Pr016gue")]
    [InlineData("•LastlPlaVed?OR08i2026")]
    [InlineData("Prologue) *e.n")]
    public void Smieciowy_odczyt_ma_wyraznie_nizsza_jakosc(string text)
    {
        var score = ReadingQuality.Score(text);

        // Sesja odrzuca odczyt brudniejszy o >0,1 od wyświetlanego (czysty = 1,0).
        Assert.True(score < 0.9, $"„{text}” powinno mieć jakość < 0,9, a ma {score:0.00}");
    }

    [Fact]
    public void Czysty_odczyt_jest_lepszy_od_smieciowego_tego_samego_napisu()
    {
        Assert.True(ReadingQuality.Score("Prologue") > ReadingQuality.Score("Prologue) *e.n"));
        Assert.True(ReadingQuality.Score("Last Played: 06.08.2026") > ReadingQuality.Score("played: 06.@8.2026"));
    }
}
