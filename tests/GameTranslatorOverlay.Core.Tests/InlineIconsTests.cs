using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Vision;

namespace GameTranslatorOverlay.Core.Tests;

public class InlineIconsTests
{
    private const int Width = 900;
    private const int Height = 200;
    private const int LineTop = 80;
    private const int LineHeight = 40;

    [Fact]
    public void Ikona_w_odstepie_miedzy_slowami_dostaje_znacznik_w_tym_miejscu()
    {
        var frame = Paper();
        var words = new[] { Word("Running", 40, 180), Word("with", 200, 290), Word("is", 400, 440) };
        foreach (var word in words) Glyphs(frame, word.Box);
        Keycap(frame, 310, LineTop - 4, 64, 48);

        var line = Assert.Single(InlineIcons.Annotate(frame, [Line(words)], default, 1.0));

        Assert.Equal("Running with [X] is", line.Text);
        var icon = line.Words[2];
        Assert.True(InlineIcons.IsMarker(icon.Text));
        Assert.InRange(icon.Box.X, 306, 314);
        Assert.InRange(icon.Box.Width, 58, 70);
        Assert.Equal(LineTop, line.Box.Y);
    }

    [Fact]
    public void Ikona_tuz_za_ostatnim_slowem_dostaje_znacznik_na_koncu()
    {
        var frame = Paper();
        var words = new[] { Word("and", 40, 110), Word("with", 130, 220) };
        foreach (var word in words) Glyphs(frame, word.Box);
        Keycap(frame, 236, LineTop - 6, 52, 60);

        var line = Assert.Single(InlineIcons.Annotate(frame, [Line(words)], default, 1.0));

        Assert.Equal("and with [X]", line.Text);
        Assert.True(line.Words[^1].Box.Right >= 286);
        Assert.Equal(220, line.Box.Right);
    }

    [Fact]
    public void Waski_slupek_i_rama_przycisku_za_slowem_nie_sa_ikonami()
    {
        var frame = Paper();
        var first = new[] { Word("Escape!", 40, 200) };
        var second = new[] { new OcrWord("Accept", new RectPx(40, LineTop + 80, 160, LineHeight)) };
        foreach (var word in first.Concat(second)) Glyphs(frame, word.Box);
        Keycap(frame, 214, LineTop - 12, 16, 64);
        for (var x = 206; x < 260; x++)
        {
            Set(frame, x, LineTop + 72, 0x2A2830);
            Set(frame, x, LineTop + 73, 0x2A2830);
            Set(frame, x, LineTop + 128, 0x2A2830);
            Set(frame, x, LineTop + 129, 0x2A2830);
        }
        for (var y = LineTop + 72; y < LineTop + 130; y++)
        {
            Set(frame, 258, y, 0x2A2830);
            Set(frame, 259, y, 0x2A2830);
        }

        var lines = InlineIcons.Annotate(frame, [Line(first), Line(second)], default, 1.0);

        Assert.Equal("Escape!", lines[0].Text);
        Assert.Equal("Accept", lines[1].Text);
    }

    [Fact]
    public void Czysta_kartka_daleka_grafika_i_dlugi_pasek_nie_sa_ikonami()
    {
        var frame = Paper();
        var words = new[] { Word("Volume", 40, 180), Word("80", 520, 560) };
        foreach (var word in words) Glyphs(frame, word.Box);
        Keycap(frame, 200, LineTop + 10, 300, 16);
        Keycap(frame, 680, LineTop - 4, 60, 48);

        var lines = InlineIcons.Annotate(frame, [Line(words)], default, 1.0);

        Assert.Equal("Volume 80", Assert.Single(lines).Text);
    }

    [Fact]
    public void Grafika_zajeta_przez_inne_slowo_nie_jest_ikona()
    {
        var frame = Paper();
        var words = new[] { Word("Press", 40, 160), Word("now", 330, 420) };
        foreach (var word in words) Glyphs(frame, word.Box);
        Glyphs(frame, new RectPx(200, LineTop, 70, LineHeight));
        var other = new OcrLine("Key", new RectPx(195, LineTop - 2, 80, LineHeight + 4), [new OcrWord("Key", new RectPx(195, LineTop - 2, 80, LineHeight + 4))]);

        var lines = InlineIcons.Annotate(frame, [Line(words), other], default, 1.0);

        Assert.Equal("Press now", lines[0].Text);
    }

    [Fact]
    public void Uklad_zastepuje_znacznik_rezerwa_i_zapamietuje_odstep_przed_nim()
    {
        var (text, slots) = InlineIcons.Layout("Bieganie z [X] jest zabronione.", 1, static (_, spaced) => spaced ? 5 : 4);

        Assert.Equal("Bieganie z" + new string(InlineIcons.Reserve, 5) + " jest zabronione.", text);
        var slot = Assert.Single(slots);
        Assert.Equal(10, slot.Start);
        Assert.Equal(5, slot.Length);
        Assert.True(slot.Spaced);
    }

    [Fact]
    public void Znacznik_na_poczatku_nie_ma_odstepu_a_nadmiarowe_znaczniki_znikaja()
    {
        var (text, slots) = InlineIcons.Layout("[X] Hint [X] now", 1, static (_, spaced) => spaced ? 3 : 2);

        Assert.Equal(new string(InlineIcons.Reserve, 2) + " Hint now", text);
        var slot = Assert.Single(slots);
        Assert.False(slot.Spaced);
        Assert.Equal(0, slot.Start);
    }

    [Fact]
    public void Zamiana_znacznikow_na_pusty_tekst_sciaga_spacje()
    {
        Assert.Equal("z jest\nok", InlineIcons.Replace("z [X] jest\nok", string.Empty));
        Assert.Equal("z ▢ jest", InlineIcons.Replace("z [X] jest", InlineIcons.SymbolReplacement));
        Assert.Equal(2, InlineIcons.CountMarkers("a [X] b [X]"));
        Assert.Equal("bez znacznika", InlineIcons.Replace("bez znacznika", string.Empty));
    }

    private static OcrWord Word(string text, int left, int right) => new(text, new RectPx(left, LineTop, right - left, LineHeight));

    private static OcrLine Line(IReadOnlyList<OcrWord> words)
    {
        var box = words.Select(static w => w.Box).Aggregate(static (a, b) => a.Union(b));
        return new OcrLine(string.Join(' ', words.Select(static w => w.Text)), box, words);
    }

    private static OcrBitmap Paper()
    {
        var frame = new OcrBitmap(new byte[Width * Height * 4], Width, Height, Width * 4);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                Set(frame, x, y, 0xE6E1D8);
        return frame;
    }

    private static void Glyphs(OcrBitmap frame, RectPx box)
    {
        for (var x = box.X; x < box.Right - 4; x += 14)
            for (var y = box.Y + 8; y < box.Bottom - 6; y++)
                for (var dx = 0; dx < 5; dx++)
                    Set(frame, x + dx, y, 0x202020);
    }

    private static void Keycap(OcrBitmap frame, int left, int top, int width, int height)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                var edge = x < left + 3 || x >= left + width - 3 || y < top + 3 || y >= top + height - 3;
                Set(frame, x, y, edge ? 0x2A2830 : 0x8A86A0);
            }
        }
        for (var x = left + width / 3; x < left + width * 2 / 3; x++)
            for (var y = top + height / 3; y < top + height * 2 / 3; y++)
                Set(frame, x, y, 0xFFFFFF);
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
