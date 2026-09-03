using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Tests;

public class TextBlockGrouperRowTests
{
    private static OcrLine Line(string text, int x, int y, int width, int height) =>
        new(text, new RectPx(x, y, width, height), [new OcrWord(text, new RectPx(x, y, width, height))]);

    [Fact]
    public void Fragmenty_w_jednym_wierszu_lacza_sie_spacja_nie_nowa_linia()
    {
        // OCR pociął „Chapter One: Fall Term” na dwie „linie” obok siebie na tej samej wysokości.
        var lines = new[]
        {
            Line("Chapter One:", 100, 200, 500, 100),
            Line("Fall Term", 640, 205, 420, 96),
        };

        var blocks = TextBlockGrouper.Group(lines);

        var block = Assert.Single(blocks);
        Assert.Equal("Chapter One: Fall Term", block.Text);
    }

    [Fact]
    public void Kolejne_wiersze_akapitu_zostaja_rozdzielone_nowa_linia()
    {
        var lines = new[]
        {
            Line("The Miller wants", 100, 100, 600, 40),
            Line("to talk to you", 100, 150, 500, 40),
        };

        var blocks = TextBlockGrouper.Group(lines);

        var block = Assert.Single(blocks);
        Assert.Equal("The Miller wants\nto talk to you", block.Text);
    }

    [Fact]
    public void Kolejnosc_w_wierszu_wyznacza_X_niezaleznie_od_kolejnosci_wejscia()
    {
        var lines = new[]
        {
            Line("Term", 900, 200, 200, 100),
            Line("Chapter One: Fall", 100, 202, 760, 98),
        };

        var blocks = TextBlockGrouper.Group(lines);

        Assert.Equal("Chapter One: Fall Term", Assert.Single(blocks).Text);
    }
}
