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
    public void Imie_w_innym_kolorze_nad_kwestia_staje_sie_osobnym_blokiem()
    {
        var block = TextBlockGrouper.Group([
            Line("Keeper", 768, 1298, 531, 119),
            Line("Hello there, traveler.", 793, 1495, 948, 97),
        ]);
        Assert.Single(block);

        var split = TextBlockSplitter.SplitLabels(block, static box => box.Y < 1400 ? 0xFBFA86 : 0xF5F5F5);

        Assert.Equal(["Keeper", "Hello there, traveler."], split.Select(static b => b.Text));
    }

    [Fact]
    public void Podobny_kolor_albo_dluga_pierwsza_linia_nie_dziela_bloku()
    {
        var block = TextBlockGrouper.Group([
            Line("Keeper", 768, 1298, 531, 119),
            Line("Hello there, traveler.", 793, 1495, 948, 97),
        ]);
        var words = new[] { "The", "old", "lighthouse", "keeper" }.Select((w, i) => new OcrWord(w, new RectPx(100 + i * 120, 100, 110, 40))).ToList();
        var paragraph = TextBlockGrouper.Group([
            new OcrLine("The old lighthouse keeper", new RectPx(100, 100, 470, 40), words),
            Line("left a note.", 100, 150, 300, 40),
        ]);

        Assert.Same(block, TextBlockSplitter.SplitLabels(block, static box => box.Y < 1400 ? 0xF0F0F0 : 0xF5F5F5));
        Assert.Same(paragraph, TextBlockSplitter.SplitLabels(paragraph, static box => box.Y < 120 ? 0xFBFA86 : 0xF5F5F5));
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
