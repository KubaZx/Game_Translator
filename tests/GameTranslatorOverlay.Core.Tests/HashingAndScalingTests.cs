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

    [Fact]
    public void ComputeUpscale_bez_automatyki_nie_powieksza_malych_regionow()
    {
        Assert.Equal(1.0, OcrScaling.ComputeUpscale(200, 60, 2600, preferredUpscale: 1.0, allowAutoUpscale: false));
        Assert.Equal(3.0, OcrScaling.ComputeUpscale(200, 60, 2600, preferredUpscale: 3.0, allowAutoUpscale: false));
    }

    [Theory]
    [InlineData(null, 0.0, 0.0, true)]
    [InlineData(null, 1.0, 1.0, true)]
    [InlineData(1.0, 0.0, 1.0, false)]
    [InlineData(2.5, 0.0, 2.5, true)]
    public void ResolvePreference_rozroznia_brak_ustawienia_od_jawnego_1(
        double? profileUpscale, double settingsUpscale, double expectedPreferred, bool expectedAuto)
    {
        var preference = OcrScaling.ResolvePreference(profileUpscale, settingsUpscale);

        Assert.Equal(expectedPreferred, preference.Preferred);
        Assert.Equal(expectedAuto, preference.AllowAuto);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0 / 1.5)]
    [InlineData(1.0 / 3.0)]
    [InlineData(2.0)]
    public void RectPx_Scale_zachowuje_krawedzie_bez_dryfu(double factor)
    {
        var rect = new RectPx(5, 7, 13, 9);

        var scaled = rect.Scale(factor);

        Assert.Equal((int)Math.Round(rect.X * factor), scaled.X);
        Assert.Equal((int)Math.Round(rect.Right * factor), scaled.Right);
        Assert.Equal((int)Math.Round(rect.Y * factor), scaled.Y);
        Assert.Equal((int)Math.Round(rect.Bottom * factor), scaled.Bottom);
    }

    [Fact]
    public void RectPx_Scale_z_powrotem_trafia_w_oryginal()
    {
        // Typowy przypadek OCR: 1.5× przed rozpoznaniem, wynik skalowany odwrotnie.
        var original = new RectPx(101, 57, 333, 41);

        var roundTrip = original.Scale(1.5).Scale(1.0 / 1.5);

        Assert.Equal(original, roundTrip);
    }
}
