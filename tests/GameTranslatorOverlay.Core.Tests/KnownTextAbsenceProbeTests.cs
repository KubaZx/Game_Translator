using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class KnownTextAbsenceProbeTests
{
    private const int White = 0xFAFAFA;
    private const int Outline = 0x0C0B0D;
    private static readonly RectPx Box = new(10, 10, 100, 30);

    [Fact]
    public void Biale_glify_w_polu_to_wciaz_obecny_tekst()
    {
        var frame = Texture(140, 60);
        Fill(frame, new RectPx(20, 15, 60, 20), White);

        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, White, Outline));
    }

    [Fact]
    public void Ciemna_tekstura_bez_jasnych_pikseli_dowodzi_znikniecia_bialego_napisu()
    {
        var frame = Texture(140, 60);

        Assert.False(TextPresenceProbe.IsClearlyEmpty(frame, Box, White, Outline));
        Assert.True(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, White, Outline));
    }

    [Fact]
    public void Zmiana_barwy_napisu_przy_najechaniu_nie_jest_zniknieciem()
    {
        var frame = Texture(140, 60);
        Fill(frame, new RectPx(20, 15, 60, 20), 0xE6C83C);

        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, White, Outline));
    }

    [Theory]
    [InlineData(0x5B5B5B, false)]
    [InlineData(0x4A4A4A, false)]
    [InlineData(0x3C3C3C, true)]
    public void Napis_przygaszony_zostaje_tekstem_dopoki_ma_cwierc_dawnego_kontrastu(int dimmedRgb, bool expectedAbsent)
    {
        var frame = Texture(140, 60);
        Fill(frame, new RectPx(20, 15, 60, 20), dimmedRgb);

        Assert.Equal(expectedAbsent, KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, White, Outline));
    }

    [Fact]
    public void Ciemny_napis_na_jasnym_tle_znika_gdy_zostaje_samo_tlo()
    {
        var frame = Solid(140, 60, 0xE8E0D0);

        Assert.True(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, 0x101010, 0xE8E0D0));

        Fill(frame, new RectPx(30, 20, 8, 8), 0x202020);

        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, 0x101010, 0xE8E0D0));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Pojedyncze_jasne_piksele_mieszcza_sie_w_tolerancji_promila(int brightPixels, bool expectedAbsent)
    {
        var frame = Texture(140, 60);
        for (var i = 0; i < brightPixels; i++) SetPixel(frame, 12 + i * 7, 12, White);

        Assert.Equal(expectedAbsent, KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, White, Outline));
    }

    [Theory]
    [InlineData(0xC03030, 0x30A030)]
    [InlineData(0x808080, 0x707070)]
    public void Bez_kontrastu_luminancji_sonda_nie_rozstrzyga(int textRgb, int backgroundRgb)
    {
        var frame = Solid(140, 60, 0x000000);

        Assert.False(KnownTextAbsenceProbe.CanCheck(textRgb, backgroundRgb));
        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, textRgb, backgroundRgb));
    }

    [Theory]
    [InlineData(-1, Outline)]
    [InlineData(White, -1)]
    public void Nieznane_kolory_nie_sa_dowodem(int textRgb, int backgroundRgb)
    {
        var frame = Texture(140, 60);

        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, textRgb, backgroundRgb));
    }

    [Fact]
    public void Pole_wychodzace_poza_klatke_jest_nierozstrzygniete()
    {
        var frame = Texture(140, 60);

        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, new RectPx(100, 10, 100, 30), White, Outline));
        Assert.False(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, new RectPx(-1, 10, 20, 20), White, Outline));
    }

    [Fact]
    public void Niekompletne_pole_nie_dowodzi_znikniecia()
    {
        var frame = Texture(10, 2);
        var probe = new KnownTextAbsenceProbe(White, Outline, expectedPixels: 20);

        probe.ObserveBgra32(frame.PixelsBgra32.AsSpan(0, 40));

        Assert.True(probe.CanDecide);
        Assert.False(probe.HasTextPixels);
        Assert.False(probe.IsTextAbsent);

        probe.ObserveBgra32(frame.PixelsBgra32.AsSpan(40, 40));

        Assert.True(probe.IsTextAbsent);
    }

    [Fact]
    public void Niepelny_piksel_albo_nadmiar_danych_uniewaznia_dowod()
    {
        var frame = Texture(10, 2);
        var partial = new KnownTextAbsenceProbe(White, Outline, expectedPixels: 20);
        partial.ObserveBgra32(frame.PixelsBgra32.AsSpan(0, 78));
        Assert.False(partial.IsTextAbsent);

        var overflow = new KnownTextAbsenceProbe(White, Outline, expectedPixels: 4);
        overflow.ObserveBgra32(frame.PixelsBgra32.AsSpan(0, 40));
        Assert.False(overflow.IsTextAbsent);
    }

    [Fact]
    public void Kanal_alfa_nie_wplywa_na_wynik()
    {
        var frame = Texture(140, 60);
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                frame.PixelsBgra32[y * frame.Stride + x * 4 + 3] = 255;

        Assert.True(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, Box, White, Outline));
    }

    [Fact]
    public void Wiersze_z_dopelnieniem_sa_czytane_tylko_w_obrebie_pola()
    {
        var frame = Texture(140, 60, paddingBytes: 12);
        for (var y = 0; y < frame.Height; y++)
            for (var p = 0; p < 12; p++)
                frame.PixelsBgra32[y * frame.Stride + 140 * 4 + p] = 255;

        Assert.True(KnownTextAbsenceProbe.IsKnownTextAbsent(frame, new RectPx(40, 10, 100, 30), White, Outline));
    }

    private static OcrBitmap Texture(int width, int height, int paddingBytes = 0)
    {
        var frame = Solid(width, height, 0, paddingBytes);
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

    private static OcrBitmap Solid(int width, int height, int rgb, int paddingBytes = 0)
    {
        var stride = width * 4 + paddingBytes;
        var frame = new OcrBitmap(new byte[stride * height], width, height, stride);
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
