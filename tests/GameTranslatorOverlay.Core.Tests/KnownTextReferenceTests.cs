using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class KnownTextReferenceTests
{
    private const int White = 0xFAFAFA;
    private const int Outline = 0x0C0B0D;
    private static readonly RectPx Box = new(10, 10, 100, 30);
    private static readonly RectPx Glyphs = new(20, 15, 60, 20);

    [Fact]
    public void Liczy_piksele_rdzenia_bialych_glifow_w_calym_polu()
    {
        var frame = Texture(140, 60);
        Fill(frame, Glyphs, White);

        Assert.Equal(1200, KnownTextReference.CountCorePixels(frame, Box, White, Outline));
    }

    [Fact]
    public void Napis_nadal_w_polu_potwierdza_obecnosc_mimo_innego_tla()
    {
        var confirmed = Texture(140, 60);
        Fill(confirmed, Glyphs, White);
        var reference = KnownTextReference.FromSample(confirmed, Box, White, Outline, 1.0);
        var animated = Solid(140, 60, 0x303030);
        Fill(animated, Glyphs, White);

        Assert.Equal(1200, reference.TextPixels);
        Assert.True(reference.IsPresent(KnownTextReference.CountCorePixels(animated, Box, White, Outline)));
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(0, false)]
    public void Jasna_plamka_po_zniknieciu_napisu_nie_potwierdza_obecnosci(int spot, bool expected)
    {
        var confirmed = Texture(140, 60);
        Fill(confirmed, Glyphs, White);
        var reference = KnownTextReference.FromSample(confirmed, Box, White, Outline, 1.0);
        var after = Texture(140, 60);
        if (spot > 0) Fill(after, new RectPx(40, 20, spot, spot), White);

        Assert.Equal(expected, reference.IsPresent(KnownTextReference.CountCorePixels(after, Box, White, Outline)));
    }

    [Fact]
    public void Jasna_tekstura_wypelniajaca_pole_nie_jest_napisem()
    {
        var confirmed = Texture(140, 60);
        Fill(confirmed, Glyphs, White);
        var reference = KnownTextReference.FromSample(confirmed, Box, White, Outline, 1.0);
        var bright = Solid(140, 60, 0xE0E0E0);

        Assert.False(reference.IsPresent(KnownTextReference.CountCorePixels(bright, Box, White, Outline)));
    }

    [Fact]
    public void Wycinek_powiekszony_dwukrotnie_liczy_piksele_w_skali_okna()
    {
        var frame = Texture(140, 60);
        Fill(frame, Glyphs, White);

        Assert.Equal(300, KnownTextReference.FromSample(frame, Box, White, Outline, 0.5).TextPixels);
    }

    [Theory]
    [InlineData(0x808080, 0x707070)]
    [InlineData(-1, Outline)]
    public void Bez_znanego_kontrastu_nie_ma_dowodu_obecnosci(int textRgb, int backgroundRgb)
    {
        var frame = Texture(140, 60);
        Fill(frame, Glyphs, White);
        var reference = KnownTextReference.FromSample(frame, Box, textRgb, backgroundRgb, 1.0);

        Assert.Equal(-1, KnownTextReference.CountCorePixels(frame, Box, textRgb, backgroundRgb));
        Assert.False(reference.CanConfirmPresence);
        Assert.False(reference.IsPresent(1200));
    }

    [Fact]
    public void Pole_poza_klatka_nie_jest_liczone()
    {
        var frame = Texture(140, 60);

        Assert.Equal(-1, KnownTextReference.CountCorePixels(frame, new RectPx(100, 10, 100, 30), White, Outline));
    }

    [Fact]
    public void Wzorzec_z_ostatniego_odczytu_nie_uznaje_przygaszonego_napisu_za_zniknety()
    {
        var dimmed = Solid(140, 60, Outline);
        Fill(dimmed, Glyphs, 0x3C3C3C);
        var refreshed = KnownTextReference.FromSample(dimmed, Box, 0x3C3C3C, Outline, 1.0);

        Assert.True(KnownTextAbsenceProbe.IsKnownTextAbsent(dimmed, Box, White, Outline));
        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(dimmed, Box, refreshed.TextRgb, refreshed.BackgroundRgb));
        Assert.True(refreshed.IsPresent(KnownTextReference.CountCorePixels(dimmed, Box, refreshed.TextRgb, refreshed.BackgroundRgb)));
    }

    [Fact]
    public void Blok_bez_wzorca_sondy_uzywa_kolorow_rysowania()
    {
        var plain = new LiveOverlayBlock(Box, "Zbadaj", "zbadaj", 20, White, Outline);
        var probed = plain with { Probe = new KnownTextReference(0x3C3C3C, 0x050505, 1200) };

        Assert.Equal(White, plain.ProbeTextRgb);
        Assert.Equal(Outline, plain.ProbeBackgroundRgb);
        Assert.Equal(0x3C3C3C, probed.ProbeTextRgb);
        Assert.Equal(0x050505, probed.ProbeBackgroundRgb);
        Assert.Equal(White, probed.ColorRgb);
    }

    [Fact]
    public void Licznik_odrzuca_niepelny_piksel()
    {
        var counter = new KnownTextReference.Counter(White, Outline);

        counter.ObserveBgra32(new byte[6]);

        Assert.True(counter.Invalid);
    }

    private static OcrBitmap Texture(int width, int height)
    {
        var frame = Solid(width, height, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = (x * 7 + y * 13) % 50;
                SetPixel(frame, x, y, ((40 + t) << 16) | ((30 + t / 2) << 8) | (20 + t / 3));
            }
        }
        return frame;
    }

    private static OcrBitmap Solid(int width, int height, int rgb)
    {
        var frame = new OcrBitmap(new byte[width * 4 * height], width, height, width * 4);
        Fill(frame, new RectPx(0, 0, width, height), rgb);
        return frame;
    }

    private static void Fill(OcrBitmap frame, RectPx box, int rgb)
    {
        for (var y = box.Y; y < box.Bottom; y++)
            for (var x = box.X; x < box.Right; x++)
                SetPixel(frame, x, y, rgb);
    }

    private static void SetPixel(OcrBitmap frame, int x, int y, int rgb)
    {
        var offset = y * frame.Stride + x * 4;
        frame.PixelsBgra32[offset] = (byte)(rgb & 255);
        frame.PixelsBgra32[offset + 1] = (byte)((rgb >> 8) & 255);
        frame.PixelsBgra32[offset + 2] = (byte)((rgb >> 16) & 255);
    }
}
