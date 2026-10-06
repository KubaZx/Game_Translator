using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveReadingEvidenceTests
{
    [Fact]
    public void Smieciowy_odczyt_w_miejscu_etykiety_nie_potwierdza_jej_obecnosci()
    {
        const string displayed = "Inspect";
        const string junk = "lRrgIé@ue";
        var stabilizer = new LiveReadingStabilizer();

        Assert.True(JunkFilter.IsMeaningful(junk));
        Assert.Equal(LiveReadingDecision.Keep, stabilizer.Observe("label", displayed, junk));
        Assert.True(LiveReadingStabilizer.IsUnrelatedDirtierReading(displayed, junk));
    }

    [Theory]
    [InlineData("Last Played: 06.08.2026", "Lasi Played: 06.0840261")]
    [InlineData("Last Played: 06.08.2026", "•LastlPlaVed?OR08i2026")]
    [InlineData("Prologue", "Pr016gue")]
    public void Podobny_brudniejszy_odczyt_dalej_potwierdza_napis(string displayed, string candidate)
    {
        Assert.True(TextSimilarity.Ratio(TextNormalizer.Normalize(candidate), TextNormalizer.Normalize(displayed)) >= 0.5);
        Assert.False(LiveReadingStabilizer.IsUnrelatedDirtierReading(displayed, candidate));
    }

    [Theory]
    [InlineData("Inspect", "Inspect")]
    [InlineData("Inspect", "INSPECT")]
    [InlineData("Inspect", "")]
    [InlineData("", "lRrgIé@ue")]
    public void Ten_sam_tekst_ani_pusty_odczyt_nie_sa_niepowiazane(string displayed, string candidate)
    {
        Assert.False(LiveReadingStabilizer.IsUnrelatedDirtierReading(displayed, candidate));
    }

    [Theory]
    [InlineData("The door is locked", "The door is open")]
    [InlineData("Inspect", "Open drawer")]
    [InlineData("Inspect", "The drawer is open")]
    public void Czysta_nowa_tresc_nie_jest_uznana_za_smiec(string displayed, string candidate)
    {
        Assert.False(LiveReadingStabilizer.IsUnrelatedDirtierReading(displayed, candidate));
    }

    [Fact]
    public void Ucieta_koncowka_calych_slow_nie_jest_niepowiazana()
    {
        Assert.False(LiveReadingStabilizer.IsUnrelatedDirtierReading("Inspect the old drawer", "Inspect the"));
    }
}
