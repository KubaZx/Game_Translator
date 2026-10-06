using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Tests;

public class OcrGeometryTests
{
    [Theory]
    [InlineData(-3.6, 77, 1752, 125, 1866)]
    [InlineData(1.9, 152, 1924, 125, 1866)]
    public void Ramka_z_wyprostowanego_obrazu_wraca_na_miejsce_w_klatce(double angle, int reportedX, int reportedY, int expectedX, int expectedY)
    {
        var box = OcrGeometry.Unrotate(reportedX, reportedY, 41, 43, angle, 3840, 2160);

        Assert.InRange(box.X, expectedX - 6, expectedX + 4);
        Assert.InRange(box.Y, expectedY - 6, expectedY + 4);
        Assert.Equal(41, box.Width);
        Assert.Equal(43, box.Height);
    }

    [Fact]
    public void Bez_kata_ramka_zostaje_bez_zmian()
    {
        Assert.Equal(new RectPx(10, 20, 31, 41), OcrGeometry.Unrotate(10.4, 20.2, 30.1, 40.6, null, 100, 100));
        Assert.Equal(new RectPx(10, 20, 30, 40), OcrGeometry.Unrotate(10, 20, 30, 40, 0.0, 100, 100));
    }

    [Fact]
    public void Srodek_obrazu_nie_przesuwa_sie_przy_obrocie()
    {
        var box = OcrGeometry.Unrotate(1900, 1070, 40, 20, 5, 3840, 2160);

        Assert.InRange(box.X + box.Width / 2, 1918, 1922);
        Assert.InRange(box.Y + box.Height / 2, 1078, 1082);
    }
}
