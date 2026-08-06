using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class VisionTests
{
    private static byte[] SolidBgra(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static byte[] HalfAndHalfBgra(int width, int height, byte left, byte right)
    {
        var pixels = SolidBgra(width, height, left);
        for (var y = 0; y < height; y++)
        {
            for (var x = width / 2; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = right;
                pixels[offset + 1] = right;
                pixels[offset + 2] = right;
            }
        }
        return pixels;
    }

    [Fact]
    public void LuminanceGrid_liczy_jasnosc_komorek()
    {
        var pixels = HalfAndHalfBgra(96, 54, left: 0, right: 255);
        var grid = LuminanceGrid.FromBgra32(pixels, 96, 54, 96 * 4, columns: 8, rows: 4);

        Assert.True(grid.Cells[0] < 10, $"Lewa strona powinna być ciemna, a jest {grid.Cells[0]}");
        Assert.True(grid.Cells[7] > 245, $"Prawa strona powinna być jasna, a jest {grid.Cells[7]}");
    }

    [Fact]
    public void ChangedFraction_identycznych_klatek_wynosi_zero()
    {
        var pixels = SolidBgra(64, 64, 120);
        var a = LuminanceGrid.FromBgra32(pixels, 64, 64, 64 * 4);
        var b = LuminanceGrid.FromBgra32(pixels, 64, 64, 64 * 4);

        Assert.Equal(0.0, FrameChangeDetector.ChangedFraction(a, b));
    }

    [Fact]
    public void ChangedFraction_odwroconych_klatek_wynosi_jeden()
    {
        var a = LuminanceGrid.FromBgra32(SolidBgra(64, 64, 0), 64, 64, 64 * 4);
        var b = LuminanceGrid.FromBgra32(SolidBgra(64, 64, 255), 64, 64, 64 * 4);

        Assert.Equal(1.0, FrameChangeDetector.ChangedFraction(a, b));
    }

    [Fact]
    public void ChangedFraction_ignoruje_drobny_szum()
    {
        var a = LuminanceGrid.FromBgra32(SolidBgra(64, 64, 100), 64, 64, 64 * 4);
        var b = LuminanceGrid.FromBgra32(SolidBgra(64, 64, 104), 64, 64, 64 * 4);

        Assert.Equal(0.0, FrameChangeDetector.ChangedFraction(a, b, cellDelta: 10.0));
    }

    [Fact]
    public void Rozne_wymiary_siatek_traktowane_sa_jak_pelna_zmiana()
    {
        var a = new LuminanceGrid(2, 2, new float[4]);
        var b = new LuminanceGrid(4, 4, new float[16]);

        Assert.Equal(1.0, FrameChangeDetector.ChangedFraction(a, b));
    }

    [Fact]
    public void Stabilizer_odpala_OCR_dopiero_po_ustaniu_zmian()
    {
        var stabilizer = new ChangeStabilizer(TimeSpan.FromMilliseconds(300));

        Assert.False(stabilizer.Update(frameChanged: true, TimeSpan.FromMilliseconds(0)));
        Assert.False(stabilizer.Update(frameChanged: false, TimeSpan.FromMilliseconds(100)));
        Assert.False(stabilizer.Update(frameChanged: true, TimeSpan.FromMilliseconds(200)));
        Assert.False(stabilizer.Update(frameChanged: false, TimeSpan.FromMilliseconds(400)));
        Assert.True(stabilizer.Update(frameChanged: false, TimeSpan.FromMilliseconds(550)));
        Assert.False(stabilizer.Update(frameChanged: false, TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public void Stabilizer_przetwarza_cyklicznie_przy_animowanym_tle()
    {
        var stabilizer = new ChangeStabilizer(
            TimeSpan.FromMilliseconds(300), maxDirtyDuration: TimeSpan.FromMilliseconds(1000));

        // Obraz zmienia się bez przerwy co 100 ms — stabilizacja nigdy nie nadchodzi,
        // ale po sekundzie ciągłych zmian klatka i tak musi zostać przetworzona.
        var fired = 0;
        for (var ms = 0; ms <= 2200; ms += 100)
        {
            if (stabilizer.Update(frameChanged: true, TimeSpan.FromMilliseconds(ms))) fired++;
        }

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Stabilizer_ForceDirty_wymusza_ponowne_przetworzenie()
    {
        var stabilizer = new ChangeStabilizer(TimeSpan.FromMilliseconds(100));
        stabilizer.ForceDirty(TimeSpan.Zero);

        Assert.True(stabilizer.Update(frameChanged: false, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void Analyze_wskazuje_prostokat_zmiany_w_pikselach_klatki()
    {
        var previous = new LuminanceGrid(4, 2, new float[8]);
        var cells = new float[8];
        cells[1] = 200;
        var current = new LuminanceGrid(4, 2, cells);

        var analysis = FrameChangeDetector.Analyze(previous, current, 400, 200);

        Assert.Equal(new RectPx(100, 0, 100, 100), analysis.ChangedRegion);
        Assert.Equal(1.0 / 8, analysis.ChangedFraction, 3);
    }

    [Fact]
    public void Analyze_bez_zmian_nie_zwraca_regionu()
    {
        var grid = new LuminanceGrid(4, 2, new float[8]);

        var analysis = FrameChangeDetector.Analyze(grid, grid, 400, 200);

        Assert.Null(analysis.ChangedRegion);
        Assert.Equal(0.0, analysis.ChangedFraction);
    }

    [Fact]
    public void Sampler_zwraca_kolor_jasnego_tekstu()
    {
        const int width = 20;
        const int height = 10;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 3; y < 7; y++)
        {
            for (var x = 4; x < 12; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = 40;
                pixels[offset + 1] = 40;
                pixels[offset + 2] = 230;
                pixels[offset + 3] = 255;
            }
        }

        var color = TextColorSampler.SampleTextColorRgb(pixels, width, height, stride, new RectPx(0, 0, width, height), minLuminance: 90);

        Assert.True(color >= 0, "Próbkowanie powinno znaleźć jasny tekst");
        Assert.True(((color >> 16) & 0xFF) > 200, "Kolor powinien być czerwonawy");
        Assert.True((color & 0xFF) < 80, "Niebieski kanał powinien być niski");
    }

    [Fact]
    public void Sampler_bez_jasnego_tekstu_zwraca_minus_jeden()
    {
        const int width = 16;
        const int height = 8;
        var pixels = new byte[width * 4 * height];

        Assert.Equal(-1, TextColorSampler.SampleTextColorRgb(pixels, width, height, width * 4, new RectPx(0, 0, width, height)));
    }

    [Fact]
    public void Keyer_rozroznia_powtorzenia_tego_samego_tekstu()
    {
        var blocks = new[]
        {
            new TextBlock("Health Potion", new RectPx(10, 10, 100, 20), []),
            new TextBlock("Health Potion", new RectPx(10, 200, 100, 20), []),
            new TextBlock("Mana Potion", new RectPx(10, 400, 100, 20), []),
        };

        var keyed = LiveBlockKeyer.AssignKeys(blocks);

        Assert.Equal(3, keyed.Count);
        Assert.EndsWith("#0", keyed[0].Key);
        Assert.EndsWith("#1", keyed[1].Key);
        Assert.NotEqual(keyed[0].Key[..16], keyed[2].Key[..16]);
    }

    [Fact]
    public void Keyer_daje_stabilne_klucze_niezaleznie_od_kolejnosci_wejscia()
    {
        var first = new TextBlock("Alpha", new RectPx(0, 0, 50, 20), []);
        var second = new TextBlock("Beta", new RectPx(0, 100, 50, 20), []);

        var forward = LiveBlockKeyer.AssignKeys([first, second]);
        var reversed = LiveBlockKeyer.AssignKeys([second, first]);

        Assert.Equal(forward.Select(static k => k.Key), reversed.Select(static k => k.Key));
    }
}
