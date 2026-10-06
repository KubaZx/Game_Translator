using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class OcrBandsTests
{
    [Theory]
    [InlineData(3840, 2160, 2)]
    [InlineData(3840, 2160, 4)]
    [InlineData(1920, 1080, 2)]
    [InlineData(2560, 1440, 4)]
    public void Pasy_pokrywaja_caly_obraz_a_strefy_wlasnosci_dziela_go_bez_luk(int width, int height, int count)
    {
        var bands = OcrBands.Plan(width, height, count);

        Assert.Equal(count, bands.Count);
        Assert.Equal(0, bands[0].Rect.Y);
        Assert.Equal(height, bands[^1].Rect.Bottom);
        Assert.Equal(0, bands[0].OwnTop);
        Assert.Equal(height, bands[^1].OwnBottom);
        for (var k = 1; k < bands.Count; k++)
        {
            Assert.Equal(bands[k - 1].OwnBottom, bands[k].OwnTop);
            Assert.True(bands[k - 1].Rect.Bottom - bands[k].Rect.Y >= 64);
            Assert.True(bands[k].OwnTop - bands[k].Rect.Y >= 32);
            Assert.True(bands[k - 1].Rect.Bottom - bands[k - 1].OwnBottom >= 32);
        }
        Assert.All(bands, b => Assert.Equal(width, b.Rect.Width));
    }

    [Fact]
    public void Linia_z_zakladki_pasow_trafia_do_wyniku_raz_w_ukladzie_calej_klatki()
    {
        var bands = OcrBands.Plan(3840, 2160, 2);
        var overlapTop = bands[1].Rect.Y;
        var y = bands[0].OwnBottom - 20;
        var inFirst = Line("Escape!", 1500, y, 400, 60);
        var inSecond = Line("Escape!", 1500, y - overlapTop, 400, 60);
        var onlySecond = Line("Tab Items", 120, 2000 - overlapTop, 300, 52);

        var merged = OcrBands.Merge([(bands[0], [inFirst]), (bands[1], [inSecond, onlySecond])]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new RectPx(1500, y, 400, 60), merged[0].Box);
        Assert.Equal(new RectPx(120, 2000, 300, 52), merged[1].Box);
        Assert.Equal(new RectPx(120, 2000, 300, 52), merged[1].Words[0].Box);
    }

    [Theory]
    [InlineData(3840, 2160, 0, true)]
    [InlineData(1920, 1080, 0, true)]
    [InlineData(3840, 2160, 1, false)]
    [InlineData(736, 368, 0, false)]
    public void Ponowna_proba_tylko_dla_pustego_odczytu_duzego_obrazu(int width, int height, int lines, bool expected)
    {
        Assert.Equal(expected, OcrBands.ShouldRetry(width, height, lines));
    }

    [Fact]
    public void Wycinek_kopiuje_wlasciwe_piksele()
    {
        var width = 6;
        var height = 5;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[(y * width + x) * 4] = (byte)(y * 10 + x);
        var bitmap = new OcrBitmap(pixels, width, height, width * 4);

        var crop = OcrBands.Crop(bitmap, new RectPx(1, 2, 3, 2));

        Assert.Equal(3, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(21, crop.PixelsBgra32[0]);
        Assert.Equal(33, crop.PixelsBgra32[(1 * 3 + 2) * 4]);
    }

    private static OcrLine Line(string text, int x, int y, int width, int height) =>
        new(text, new RectPx(x, y, width, height), [new OcrWord(text, new RectPx(x, y, width, height))]);
}
