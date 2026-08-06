using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class HashingAndScalingTests
{
    [Fact]
    public void Sha256Hex_jest_stabilny_dla_tego_samego_tekstu()
    {
        Assert.Equal(TextHasher.Sha256Hex("Energy Shield"), TextHasher.Sha256Hex("Energy Shield"));
    }

    [Fact]
    public void Sha256Hex_rozroznia_teksty_i_wielkosc_liter()
    {
        Assert.NotEqual(TextHasher.Sha256Hex("Armour"), TextHasher.Sha256Hex("armour"));
        Assert.NotEqual(TextHasher.Sha256Hex("Armour"), TextHasher.Sha256Hex("Evasion"));
    }

    [Theory]
    [InlineData(200, 60, 2600, 2.0)]
    [InlineData(300, 100, 2600, 2.0)]
    [InlineData(1920, 1080, 2600, 1.0)]
    public void ComputeUpscale_powieksza_male_regiony(int width, int height, int max, double expected)
    {
        Assert.Equal(expected, OcrScaling.ComputeUpscale(width, height, max));
    }

    [Fact]
    public void ComputeUpscale_nie_przekracza_limitu_silnika()
    {
        var factor = OcrScaling.ComputeUpscale(2000, 90, 2600, preferredUpscale: 2.0);
        Assert.True(2000 * factor <= 2600);
        Assert.True(factor >= 1.0);
    }

    [Fact]
    public void ComputeUpscale_respektuje_preferencje_profilu()
    {
        Assert.Equal(3.0, OcrScaling.ComputeUpscale(400, 200, 2600, preferredUpscale: 3.0));
    }

    [Theory]
    [InlineData(5200, 100, 2600, 0.5)]
    [InlineData(1000, 1000, 2600, 1.0)]
    public void ComputeDownscale_zmniejsza_zbyt_duze_obrazy(int width, int height, int max, double expected)
    {
        Assert.Equal(expected, OcrScaling.ComputeDownscale(width, height, max));
    }

    [Fact]
    public void RectPx_Union_obejmuje_oba_prostokaty()
    {
        var union = new RectPx(10, 10, 20, 20).Union(new RectPx(50, 40, 10, 10));
        Assert.Equal(new RectPx(10, 10, 50, 40), union);
    }
}
