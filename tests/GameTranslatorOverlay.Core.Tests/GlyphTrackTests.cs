using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class GlyphTrackTests
{
    private const int Width = 520;
    private const int Height = 220;

    [Fact]
    public void Napis_w_miejscu_na_nowym_tle_jest_znaleziony_bez_przesuniecia()
    {
        var first = Scene(seed: 1, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        Assert.NotNull(cover.Track);

        var second = Scene(seed: 7, x: 60, y: 70);
        var match = GlyphTracker.Locate(cover.Track, second, 0, 0, 40);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident, $"koszt {match.Value.Cost:F1}");
        Assert.True(match.Value.IsStatic);
    }

    [Fact]
    public void Przesuniety_napis_jest_znaleziony_z_dokladnym_przesunieciem()
    {
        var first = Scene(seed: 2, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;

        var moved = Scene(seed: 3, x: 83, y: 63);
        var match = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 48);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident, $"koszt {match.Value.Cost:F1}, typowy {match.Value.Typical:F1}");
        Assert.Equal(23, match.Value.WorkDx);
        Assert.Equal(-7, match.Value.WorkDy);
    }

    [Fact]
    public void Brak_napisu_nie_daje_pewnego_dopasowania()
    {
        var first = Scene(seed: 4, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;

        var empty = Background(seed: 5);
        var match = GlyphTracker.Locate(cover.Track!, empty, 0, 0, 48);

        Assert.True(match is null || !match.Value.IsConfident);
    }

    [Fact]
    public void Odswiezona_latka_bierze_kolory_z_nowego_tla_i_przesuwa_kotwice()
    {
        var first = Scene(seed: 6, x: 60, y: 70, background: 0x203040);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])! with { Anchor = box };

        var moved = Scene(seed: 6, x: 70, y: 74, background: 0x804020);
        var match = GlyphTracker.Locate(cover.Track!, moved, 0, 0, 40)!.Value;
        var refreshed = GlyphCoverBuilder.Refill(cover, moved, 0, 0, match.WorkDx, match.WorkDy)!;

        Assert.Equal(box.Offset(10, 4), refreshed.Anchor);
        Assert.Equal(cover.PatchPbgra.Length, refreshed.PatchPbgra.Length);
        var opaque = 0;
        for (var o = 0; o < refreshed.PatchPbgra.Length; o += 4)
        {
            if (refreshed.PatchPbgra[o + 3] != 255) continue;
            opaque++;
            Assert.InRange(refreshed.PatchPbgra[o + 2], 0x80 - 20, 0x80 + 20);
            Assert.InRange(refreshed.PatchPbgra[o], 0x20 - 20, 0x20 + 20);
        }
        Assert.True(opaque > 200);
    }

    [Fact]
    public void Duzy_napis_budowany_w_zmniejszeniu_jest_sledzony_w_pikselach_okna()
    {
        const int width = 900;
        const int height = 360;
        var first = Solid(width, height, 0x101820);
        var glyphs = Word(80, 100, capHeight: 110, stroke: 16);
        Paint(first, glyphs, 0xE8E8E8);
        var box = new RectPx(70, 90, 560, 130);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"])!;
        Assert.True(cover.Track!.Step >= 2);

        var moved = Solid(width, height, 0x101820);
        Paint(moved, Word(80 + 30, 100 - 12, capHeight: 110, stroke: 16), 0xE8E8E8);
        var match = GlyphTracker.Locate(cover.Track, moved, 0, 0, 60);

        Assert.NotNull(match);
        Assert.True(match.Value.IsConfident);
        Assert.InRange(match.Value.WindowDx, 28, 32);
        Assert.InRange(match.Value.WindowDy, -14, -10);
    }

    [Fact]
    public void Mapa_okna_przesuwa_poczatek_sledzenia()
    {
        var first = Scene(seed: 8, x: 60, y: 70);
        var box = new RectPx(56, 66, 210, 50);
        var cover = GlyphCoverBuilder.Build(first, box, [box], ["Hill"], map: new GlyphTrackMap(1000, 500, 1))!;
        var region = Scene(seed: 9, x: 60, y: 70);

        var match = GlyphTracker.Locate(cover.Track!, region, 1000, 500, 30);

        Assert.True(match!.Value.IsConfident);
        Assert.True(match.Value.IsStatic);
        Assert.True(cover.Track!.OriginX >= 1000);
    }

    [Fact]
    public void Ikona_i_slowo_jako_osobne_linie_ocr_daja_jeden_wiersz_i_wykryta_ikone()
    {
        var frame = Solid(Width, Height, 0x202830);
        Paint(frame, Word(30, 70, capHeight: 40, stroke: 6, letters: 1), 0xF0F0F0);
        Paint(frame, Word(110, 70, capHeight: 40, stroke: 6, letters: 4), 0xF0F0F0);
        var icon = new RectPx(26, 66, 40, 48);
        var word = new RectPx(106, 66, 200, 48);
        var block = new GameTranslatorOverlay.Core.Text.TextBlock("X Hint", icon.Union(word),
        [
            new OcrLine("Hint", word.Offset(0, -2), [new OcrWord("Hint", word.Offset(0, -2))]),
            new OcrLine("X", icon, [new OcrWord("X", icon)]),
        ]);

        var cover = GlyphCoverBuilder.BuildForBlock(frame, block, new RectPx(0, 0, Width, Height), 1.0);

        Assert.NotNull(cover);
        Assert.Single(cover.Lines);
        Assert.Equal("X", cover.IconToken);
        Assert.True(cover.IconSkipPx > 0);
    }

    private static OcrBitmap Scene(int seed, int x, int y, int background = -1)
    {
        var frame = background >= 0 ? Solid(Width, Height, background) : Background(seed);
        var glyphs = Word(x, y, capHeight: 40, stroke: 6).ToList();
        Paint(frame, glyphs, 0x000000, grow: 3);
        Paint(frame, glyphs, 0xF0F0F0);
        return frame;
    }

    private static OcrBitmap Background(int seed)
    {
        var random = new Random(seed);
        var frame = new OcrBitmap(new byte[Width * Height * 4], Width, Height, Width * 4);
        var baseR = random.Next(40, 160);
        var baseG = random.Next(40, 160);
        var baseB = random.Next(40, 160);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var wave = (int)(30 * Math.Sin((x + seed * 37) / 23.0) + 20 * Math.Cos((y + seed * 11) / 17.0));
                Set(frame, x, y, Clamp(baseR + wave) << 16 | Clamp(baseG - wave / 2) << 8 | Clamp(baseB + wave / 3));
            }
        }
        return frame;
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

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
            var mid = top + capHeight / 2 + letter * 3;
            for (var bx = left; bx <= right; bx++)
                for (var dy = 0; dy < stroke; dy++) points.Add((bx, mid + dy));
        }
        return points;
    }

    private static void Paint(OcrBitmap frame, IEnumerable<(int X, int Y)> points, int rgb, int grow = 0)
    {
        foreach (var (x, y) in points)
        {
            for (var oy = -grow; oy <= grow; oy++)
                for (var ox = -grow; ox <= grow; ox++)
                    Set(frame, x + ox, y + oy, rgb);
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
}
