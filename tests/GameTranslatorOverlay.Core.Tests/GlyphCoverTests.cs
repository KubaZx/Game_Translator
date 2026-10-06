using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class GlyphCoverTests
{
    private const int Width = 420;
    private const int Height = 160;

    [Fact]
    public void Jasny_napis_z_konturem_i_cieniem_daje_kolor_kontur_i_cien()
    {
        var frame = Solid(Width, Height, 0x1A111B);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x000000, grow: 4, dx: 4, dy: 4);
        Paint(frame, glyphs, 0x000000, grow: 4);
        Paint(frame, glyphs, 0xA6A6A6);

        var cover = GlyphCoverBuilder.Build(frame, new RectPx(36, 46, 200, 50), [new RectPx(36, 46, 200, 50)], ["Hill"]);

        Assert.NotNull(cover);
        Assert.False(cover.Soft);
        AssertColor(0xA6A6A6, cover.TextRgb, 12);
        AssertColor(0x000000, cover.OutlineRgb, 12);
        Assert.InRange(cover.OutlinePx, 2, 6);
        Assert.True(cover.ShadowDy > 0);
        Assert.True(cover.ShadowDx > 0);
    }

    [Fact]
    public void Latka_wypelnia_litery_kolorem_tla_a_poza_maska_jest_przezroczysta()
    {
        var frame = Solid(Width, Height, 0x2B3A4C);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xF5F5F5);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var patchOrigin = (X: box.X + (int)cover.PatchX, Y: box.Y + (int)cover.PatchY);
        var covered = 0;
        foreach (var (x, y) in glyphs)
        {
            var px = x - patchOrigin.X;
            var py = y - patchOrigin.Y;
            var o = (py * cover.PatchPixelWidth + px) * 4;
            Assert.Equal(255, cover.PatchPbgra[o + 3]);
            Assert.InRange(cover.PatchPbgra[o + 2], 0x2B - 6, 0x2B + 6);
            Assert.InRange(cover.PatchPbgra[o + 1], 0x3A - 6, 0x3A + 6);
            Assert.InRange(cover.PatchPbgra[o], 0x4C - 6, 0x4C + 6);
            covered++;
        }
        Assert.True(covered > 200);
        Assert.Equal(0, cover.PatchPbgra[3]);
        Assert.Equal(-1, cover.OutlineRgb);
    }

    [Fact]
    public void Gradient_tla_jest_odtworzony_pod_literami()
    {
        var frame = new OcrBitmap(new byte[Width * Height * 4], Width, Height, Width * 4);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                Set(frame, x, y, (x / 3) << 16 | 0x30 << 8 | (y / 2));
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xFFFFFF);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var worst = 0;
        foreach (var (x, y) in glyphs)
        {
            var o = ((y - box.Y - (int)cover.PatchY) * cover.PatchPixelWidth + (x - box.X - (int)cover.PatchX)) * 4;
            worst = Math.Max(worst, Math.Abs(cover.PatchPbgra[o + 2] - x / 3));
            worst = Math.Max(worst, Math.Abs(cover.PatchPbgra[o] - y / 2));
        }
        Assert.InRange(worst, 0, 8);
    }

    [Fact]
    public void Ciemny_tekst_na_jasnym_oknie_ma_ciemny_kolor()
    {
        var frame = Solid(Width, Height, 0xEDE6D6);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0x202020);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"]);

        Assert.NotNull(cover);
        AssertColor(0x202020, cover.TextRgb, 12);
    }

    [Fact]
    public void Linia_bazowa_i_wysokosc_wersalikow_sa_mierzone_z_pikseli()
    {
        var frame = Solid(Width, Height, 0x101010);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xE0E0E0);

        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        var line = Assert.Single(cover.Lines);
        Assert.InRange(box.Y + line.InkTop, 49, 51);
        Assert.InRange(box.Y + line.Baseline, 89, 91);
        Assert.InRange(cover.Ascent, 38, 42);
        Assert.InRange(box.X + line.InkLeft, 39, 41);
        Assert.Equal("Hill", line.Text);
        Assert.InRange(line.Density, 0.05, 1);
    }

    [Fact]
    public void Wyrownanie_do_srodka_rozpoznane_z_dwoch_linii()
    {
        var frame = Solid(Width, 200, 0x101010);
        Paint(frame, Word(60, 30, capHeight: 30, stroke: 5, letters: 6), 0xE0E0E0);
        Paint(frame, Word(110, 90, capHeight: 30, stroke: 5, letters: 2), 0xE0E0E0);

        var lines = new[] { new RectPx(56, 26, 230, 40), new RectPx(106, 86, 130, 40) };
        var cover = GlyphCoverBuilder.Build(frame, new RectPx(56, 26, 230, 100), lines, ["Hello there", "Hi"]);

        Assert.NotNull(cover);
        Assert.Equal(TextAlignHint.Center, cover.Align);
        Assert.Equal(2, cover.Lines.Count);
        Assert.InRange(cover.LinePitch, 58, 62);
    }

    [Fact]
    public void Ikona_klawisza_przed_tekstem_nie_jest_zamazywana()
    {
        var frame = Solid(Width, Height, 0x0A0A0A);
        var key = Rect(40, 50, 40, 40);
        Paint(frame, key, 0x968CA6);
        var glyphs = Word(120, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xFAFAFA);

        var box = new RectPx(40, 50, 260, 42);
        var words = new[] { new OcrWord("X", new RectPx(40, 50, 40, 40)), new OcrWord("Hint", new RectPx(120, 50, 170, 40)) };
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["X Hint"], firstLineWords: words)!;

        Assert.Equal("X", cover.IconToken);
        Assert.True(cover.IconSkipPx > 40);
        foreach (var (x, y) in key)
        {
            var o = ((y - box.Y - (int)cover.PatchY) * cover.PatchPixelWidth + (x - box.X - (int)cover.PatchX)) * 4;
            Assert.Equal(0, cover.PatchPbgra[o + 3]);
        }
    }

    [Fact]
    public void Wysoka_ikona_za_tekstem_nie_jest_zamazywana()
    {
        var frame = Solid(Width, Height, 0x2A2A30);
        var glyphs = Word(40, 50, capHeight: 40, stroke: 6);
        Paint(frame, glyphs, 0xA5A5A5);
        var icon = Rect(260, 47, 40, 46);
        Paint(frame, icon, 0xFFF080);

        var box = new RectPx(40, 47, 262, 48);
        var words = new[] { new OcrWord("Hill", new RectPx(40, 50, 205, 40)), new OcrWord("to", new RectPx(260, 47, 40, 46)) };
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill to"], firstLineWords: words)!;

        Assert.Equal("to", cover.TailToken);
        Assert.True(cover.TailSkipPx >= 40);
        AssertColor(0xA5A5A5, cover.TextRgb, 12);
        foreach (var (x, y) in icon)
        {
            var o = ((y - box.Y - (int)cover.PatchY) * cover.PatchPixelWidth + (x - box.X - (int)cover.PatchX)) * 4;
            Assert.Equal(0, cover.PatchPbgra[o + 3]);
        }
    }

    [Fact]
    public void Zwykle_krotkie_slowo_na_koncu_nie_jest_ikona()
    {
        var frame = Solid(Width, Height, 0x2A2A30);
        Paint(frame, Word(40, 50, capHeight: 40, stroke: 6), 0xA5A5A5);
        Paint(frame, Word(258, 50, capHeight: 40, stroke: 6, letters: 1), 0xA5A5A5);

        var box = new RectPx(40, 50, 260, 40);
        var words = new[] { new OcrWord("Hill", new RectPx(40, 50, 205, 40)), new OcrWord("A", new RectPx(258, 50, 30, 40)) };
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill A"], firstLineWords: words)!;

        Assert.Equal(string.Empty, cover.TailToken);
        Assert.Equal(0, cover.TailSkipPx);
    }

    [Fact]
    public void Duzy_napis_liczony_w_polowie_rozdzielczosci_ma_wspolrzedne_natywne()
    {
        var frame = Solid(900, 300, 0x1A1A2A);
        var glyphs = Word(60, 80, capHeight: 100, stroke: 14);
        Paint(frame, glyphs, 0xF0F0F0);

        var box = new RectPx(52, 72, 520, 116);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])!;

        Assert.True(cover.PatchPixelWidth < cover.PatchWidth * 0.6);
        Assert.InRange(cover.Ascent, 96, 104);
        Assert.InRange(box.Y + cover.Lines[0].Baseline, 178, 182);
    }

    [Fact]
    public void Brak_tekstu_nie_daje_latki()
    {
        var frame = Solid(Width, Height, 0x334455);
        Assert.Null(GlyphCoverBuilder.Build(frame, new RectPx(36, 46, 200, 50)));
    }

    [Fact]
    public void Podpis_pola_odroznia_zmiane_tla_od_szumu()
    {
        var frame = Solid(Width, Height, 0x334455);
        var region = new RectPx(30, 40, 220, 70);
        var first = GlyphCoverBuilder.Signature(frame, region);
        Assert.Equal(0, GlyphCover.SignatureDifference(first, GlyphCoverBuilder.Signature(frame, region)));

        var changed = Solid(Width, Height, 0x8899AA);
        Assert.True(GlyphCover.SignatureDifference(first, GlyphCoverBuilder.Signature(changed, region)) > GlyphCoverBuilder.StaticSignatureTolerance);
        Assert.Equal(double.PositiveInfinity, GlyphCover.SignatureDifference(first, []));
    }

    [Fact]
    public void Kotwica_przesuwa_wspolrzedne_wzgledem_nowego_pola()
    {
        var frame = Solid(Width, Height, 0x101010);
        Paint(frame, Word(40, 50, capHeight: 40, stroke: 6), 0xE0E0E0);
        var box = new RectPx(36, 46, 200, 50);
        var cover = GlyphCoverBuilder.Build(frame, box, [box], ["Hill"])! with { Anchor = box };

        var moved = cover.AnchorTo(new RectPx(34, 47, 200, 50));

        Assert.Equal(cover.PatchX + 2, moved.PatchX);
        Assert.Equal(cover.PatchY - 1, moved.PatchY);
        Assert.Equal(cover.Lines[0].Baseline - 1, moved.Lines[0].Baseline);
        Assert.Equal(new RectPx(34, 47, 200, 50), moved.Anchor);
    }

    [Fact]
    public void Profil_tuszu_pomija_kropke_i_ogonek_przy_linii_bazowej()
    {
        const int w = 60;
        const int h = 60;
        var mask = new bool[w * h];
        for (var y = 10; y < 40; y++)
            for (var x = 5; x < 11; x++) mask[y * w + x] = true;
        for (var y = 20; y < 40; y++)
            for (var x = 15; x < 45; x++) mask[y * w + x] = true;
        for (var y = 40; y < 50; y++)
            for (var x = 40; x < 44; x++) mask[y * w + x] = true;
        for (var y = 2; y < 6; y++)
            for (var x = 50; x < 54; x++) mask[y * w + x] = true;

        var profile = InkProfile.Measure(mask, w, new RectPx(0, 0, w, h))!;

        Assert.Equal(10, profile.Top);
        Assert.Equal(40, profile.Baseline);
        Assert.Equal(50, profile.Bottom);
        Assert.Equal(5, profile.Left);
        Assert.Equal(54, profile.Right);
    }

    private static IEnumerable<(int X, int Y)> Word(int x, int top, int capHeight, int stroke, int letters = 4)
    {
        var points = new HashSet<(int, int)>();
        var advance = capHeight * 3 / 4 + stroke * 2;
        for (var letter = 0; letter < letters; letter++)
        {
            var left = x + letter * advance;
            var right = left + capHeight * 3 / 4;
            for (var y = top; y < top + capHeight; y++)
            {
                for (var dx = 0; dx < stroke; dx++)
                {
                    points.Add((left + dx, y));
                    if (letter % 2 == 0) points.Add((right - dx, y));
                }
            }
            if (letter % 2 == 0)
            {
                var middle = top + capHeight / 2 - stroke / 2;
                for (var y = middle; y < middle + stroke; y++)
                    for (var px = left; px <= right; px++) points.Add((px, y));
            }
        }
        return points.ToList();
    }

    private static IEnumerable<(int X, int Y)> Rect(int x, int y, int w, int h)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++) yield return (xx, yy);
    }

    private static void Paint(OcrBitmap frame, IEnumerable<(int X, int Y)> points, int rgb, int grow = 0, int dx = 0, int dy = 0)
    {
        foreach (var (x, y) in points)
        {
            for (var oy = -grow; oy <= grow; oy++)
                for (var ox = -grow; ox <= grow; ox++)
                    Set(frame, x + ox + dx, y + oy + dy, rgb);
        }
    }

    private static OcrBitmap Solid(int width, int height, int rgb)
    {
        var frame = new OcrBitmap(new byte[width * height * 4], width, height, width * 4);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) Set(frame, x, y, rgb);
        return frame;
    }

    private static void Set(OcrBitmap frame, int x, int y, int rgb)
    {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return;
        var o = y * frame.Stride + x * 4;
        frame.PixelsBgra32[o] = (byte)rgb;
        frame.PixelsBgra32[o + 1] = (byte)(rgb >> 8);
        frame.PixelsBgra32[o + 2] = (byte)(rgb >> 16);
        frame.PixelsBgra32[o + 3] = 255;
    }

    private static void AssertColor(int expected, int actual, int tolerance)
    {
        Assert.True(actual >= 0, "brak koloru");
        for (var shift = 0; shift <= 16; shift += 8)
        {
            Assert.InRange((actual >> shift) & 0xFF, ((expected >> shift) & 0xFF) - tolerance, ((expected >> shift) & 0xFF) + tolerance);
        }
    }
}
