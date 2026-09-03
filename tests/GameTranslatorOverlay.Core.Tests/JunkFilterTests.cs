using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class JunkFilterTests
{
    [Theory]
    [InlineData("Armour")]
    [InlineData("+25%")]
    [InlineData("10–15")]
    [InlineData("Level 20")]
    [InlineData("3/5")]
    [InlineData("x2")]
    [InlineData("-10%")]
    [InlineData("Adds 3 to 7 Fire Damage")]
    [InlineData("HP")]
    [InlineData("Lv.20")]
    [InlineData("T3")]
    [InlineData("Play")]
    public void IsMeaningful_przepuszcza_sensowny_tekst(string text)
    {
        Assert.True(JunkFilter.IsMeaningful(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("…")]
    [InlineData("|||")]
    [InlineData("###")]
    [InlineData("24")]
    [InlineData("-- ~~ ++")]
    [InlineData("sc.@ß")]
    [InlineData("D.7.4")]
    [InlineData("0.7.4")]
    public void IsMeaningful_odrzuca_smieci(string text)
    {
        Assert.False(JunkFilter.IsMeaningful(text));
    }
}
