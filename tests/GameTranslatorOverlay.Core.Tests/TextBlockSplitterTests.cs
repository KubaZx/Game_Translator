using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class TextBlockSplitterTests
{
    private static OcrLine Line(string text, int x, int y, int width, int height) =>
        new(text, new RectPx(x, y, width, height), [new OcrWord(text, new RectPx(x, y, width, height))]);

    [Fact]
    public void Sklejony_odczyt_dwoch_potwierdzonych_napisow_HUD_wraca_do_dwoch_blokow()
    {
        var merged = TextBlockGrouper.Group([
            Line("X Hint", 122, 1853, 242, 72),
            Line("rab", 125, 2009, 44, 27),
            Line("Items", 221, 1984, 194, 80),
        ]);
        Assert.Contains(merged, static b => b.Text == "X Hint\nItems");

        var split = TextBlockSplitter.SplitAlong(merged, [new RectPx(123, 1862, 240, 62), new RectPx(120, 2001, 297, 52)]);

        Assert.DoesNotContain(split, static b => b.Lines.Count > 1 && b.Text.Contains("Hint") && b.Text.Contains("Items"));
        Assert.Contains(split, static b => b.Text == "X Hint");
        Assert.Contains(split, static b => b.Text == "Items");
        Assert.Contains(split, static b => b.Text == "rab");
    }

    [Fact]
    public void Linia_spoza_potwierdzonych_napisow_zostawia_blok_w_calosci()
    {
        var block = TextBlockGrouper.Group([
            Line("The clock tells", 100, 100, 400, 40),
            Line("your secret", 100, 150, 300, 40),
        ]);

        var split = TextBlockSplitter.SplitAlong(block, [new RectPx(90, 95, 420, 50), new RectPx(2000, 2000, 50, 50)]);

        Assert.Same(block, split);
    }

    [Fact]
    public void Jeden_potwierdzony_napis_nie_dzieli_akapitu()
    {
        var block = TextBlockGrouper.Group([
            Line("The clock tells", 100, 100, 400, 40),
            Line("your secret", 100, 150, 300, 40),
        ]);

        var split = TextBlockSplitter.SplitAlong(block, [new RectPx(90, 90, 430, 110)]);

        Assert.Same(block, split);
    }
}
